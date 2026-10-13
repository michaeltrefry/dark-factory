using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25987: the start-of-run sweeps and the shared clone under overlapping runs. Real git against local bare "remotes", the ledger
/// in memory, and a second live run simulated by holding its item's run lock. No worker, no sandbox helper and no process but git.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SafeCleanupTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private static readonly RepoRef Gears = new("acme", "gears");
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly string _root = Directory.CreateTempSubdirectory("df-cleanup-").FullName;
    private readonly Dictionary<RepoRef, string> _remotes = [];
    private readonly LedgerDbContext _db = TestDb.Create();
    private readonly WorkLedger _ledger;
    private readonly InProcessRunLocks _locks = new();

    public SafeCleanupTests()
    {
        _ledger = new WorkLedger(_db, TimeProvider.System);
        foreach (var repo in new[] { Repo, Gears })
        {
            var remote = _remotes[repo] = Path.Combine(_root, $"{repo.Name}.git");
            var seed = Path.Combine(_root, $"{repo.Name}-seed");
            Git(_root, "init", "--bare", "-b", "main", remote);
            Git(_root, "init", "-b", "main", seed);
            File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
            Git(seed, "add", ".");
            Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
            Git(seed, "push", remote, "main");
        }
    }

    private string WorkRoot => Path.Combine(_root, "work");
    private string Sessions => Path.Combine(WorkRoot, McpProxySessions.DirectoryName);

    private GitWorkspace Workspaces(string directory = GitWorkspace.ItemWorktrees, GitCommand? git = null) =>
        new(WorkRoot, r => _remotes[r], (_, _) => Task.FromResult<string?>(null), git, worktreesDirectory: directory, shareWithWorker: false);

    private GitWorkspace Triage => Workspaces(SandboxTriageRunner.TriageWorktrees);
    private GitWorkspace Planner => Workspaces(SandboxPlanRunner.PlanWorktrees);

    /// <summary>An item the ledger knows, taken through <paramref name="states"/>.</summary>
    private async Task<WorkItem> Item(string id, params WorkState[] states)
    {
        var naming = ItemNaming.ParseAny(id)!.Naming;
        var item = await _ledger.GetOrCreateAsync(naming.Source, id, id, Repo.FullName, null, None);
        foreach (var state in states)
        {
            await _ledger.RecordAsync(item, state, null, null, None);
        }
        return item;
    }

    /// <summary>A run of <paramref name="item"/> is live: its run lock is held until the result is disposed.</summary>
    private async Task<IAsyncDisposable> Live(WorkItem item) =>
        await _locks.TryAcquireAsync(item.Id, None) ?? throw new InvalidOperationException($"{item.ExternalId} is already running");

    /// <summary>The sweep at the start of an item's run (<see cref="FactoryRunner"/>): the ledger's resume rule, no item exempt.</summary>
    private Task SweepItems() =>
        Workspaces().SweepOrphansAsync((name, c) => RunPipeline.WorktreeIsResumableAsync(_ledger, name, c), new LiveRuns(_ledger, _locks), None);

    /// <summary>A seat's sweep (<see cref="SandboxTriageRunner"/>, <see cref="SandboxPlanRunner"/>) by a session of <paramref name="self"/>: nothing is resumed.</summary>
    private Task SweepSeats(GitWorkspace seats, string self) =>
        seats.SweepOrphansAsync((_, _) => Task.FromResult(false), new LiveRuns(_ledger, _locks, ItemNaming.ParseAny(self)), None);

    private Task SweepSessions(string self) =>
        McpProxySessions.SweepAsync(Sessions, new LiveRuns(_ledger, _locks, ItemNaming.ParseAny(self)), None);

    private string SessionFile(string name)
    {
        Directory.CreateDirectory(Sessions);
        var path = Path.Combine(Sessions, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    private async Task<string> Head() => (await Workspaces().BranchStartAsync(Repo, "factory/x", None)).BaseSha;

    private async Task<List<string>> GateWorktrees(string item, string head)
    {
        var paths = new List<string>();
        foreach (var suffix in new[] { "base", "head", "base-retry", "base-retry-2", "base-retry-3" })
        {
            paths.Add((await Workspaces().PrepareCommitAsync(Repo, $"gate-{item}-{suffix}", head, None)).Path);
        }
        return paths;
    }

    [Fact]
    public async Task A_second_runs_sweeps_delete_nothing_a_live_run_owns_and_still_delete_what_a_gone_run_left()
    {
        var head = await Head();
        // Live: sc-1 at the merge gate (its gate test worktrees and the merge queue's base-update worktree, which no re-run resumes in),
        // the plan item sc-3 (its changeset worktree, its planner's seat of two repositories, its MCP session file) and the triage of gh-7.
        var sc1 = await Item("sc-1", WorkState.Implement, WorkState.Review);
        var sc3 = await Item("sc-3", WorkState.Plan);
        var gh7 = await Item("gh-7");
        var live = new List<string>(await GateWorktrees("sc-1", head))
        {
            (await Workspaces().PrepareAsync(Repo, "factory/sc-1", None)).Path,
            (await Workspaces().PrepareAsync(Repo, "factory/changeset-sc-3", None)).Path,
            (await Planner.PrepareSeatAsync([Repo, Gears], "factory/plan-sc-3", None)).Path,
            (await Triage.PrepareAsync(Repo, "factory/triage-gh-7", None)).Path,
        };
        var liveFiles = new[] { SessionFile($"sc-3.{Guid.NewGuid():N}.json"), SessionFile($"gh-7.{Guid.NewGuid():N}.json") };
        // Gone: the same kinds of leftovers of sc-2, the plan items sc-4 and sc-5 and the triage of gh-8, whose runs died.
        await Item("sc-2", WorkState.Implement, WorkState.Review);
        await Item("sc-4", WorkState.Plan);
        await Item("sc-5", WorkState.Plan);
        await Item("gh-8");
        var gone = new List<string>(await GateWorktrees("sc-2", head))
        {
            (await Workspaces().PrepareAsync(Repo, "factory/sc-2", None)).Path,
            (await Workspaces().PrepareAsync(Repo, "factory/changeset-sc-4", None)).Path,
            (await Planner.PrepareSeatAsync([Repo, Gears], "factory/plan-sc-4", None)).Path,
            (await Planner.PrepareAsync(Repo, "factory/plan-sc-5", None)).Path,
            (await Triage.PrepareAsync(Repo, "factory/triage-gh-8", None)).Path,
        };
        var goneFiles = new[]
        {
            SessionFile($"sc-4.{Guid.NewGuid():N}.json"), SessionFile($"gh-8.{Guid.NewGuid():N}.json"),
            SessionFile($"{Guid.NewGuid():N}.json"), // written before files were named for their item
            SessionFile($"sc-999.{Guid.NewGuid():N}.json"), // an item the ledger never saw: nothing can run it
        };
        // A paused item's worktree is resumable by the ledger alone, running or not.
        await Item("sc-10", WorkState.Implement, WorkState.Paused);
        var resumable = (await Workspaces().PrepareAsync(Repo, "factory/sc-10", None)).Path;

        // The second run: of sc-20 (its start-of-run sweep), planning sc-21 and triaging gh-22 under their items' run locks.
        var sc21 = await Item("sc-21", WorkState.Plan);
        var gh22 = await Item("gh-22");
        var locks = new List<IAsyncDisposable>();
        foreach (var item in new[] { sc1, sc3, gh7, sc21, gh22 })
        {
            locks.Add(await Live(item));
        }
        await SweepItems();
        await SweepSeats(Planner, "sc-21");
        await SweepSeats(Triage, "gh-22");
        await SweepSessions("sc-21");
        await SweepSessions("gh-22");

        Assert.All(live, path => Assert.True(File.Exists(Path.Combine(path, path.Contains("plan-sc-3") ? "acme/gears/README.md" : "README.md")), path));
        Assert.All(liveFiles, file => Assert.True(File.Exists(file), file));
        Assert.All(gone, path => Assert.False(Directory.Exists(path), path));
        Assert.All(goneFiles, file => Assert.False(File.Exists(file), file));
        Assert.True(Directory.Exists(resumable));
        // The live runs' worktrees are still registered in the clone (nothing pruned them), and a swept item's lock was given back.
        var registered = Git(Path.Combine(WorkRoot, "repos", "acme", "widgets"), "worktree", "list");
        Assert.All(live.Where(p => !p.Contains("plan-sc-3")), path => Assert.Contains(Path.GetFileName(path), registered));
        Assert.DoesNotContain("gate-sc-2-base", registered);
        Assert.DoesNotContain("factory-sc-2", registered);
        await using (var free = await _locks.TryAcquireAsync((await _ledger.IdOfAsync(ItemNaming.ParseAny("sc-2")!, None))!.Value, None))
        {
            Assert.NotNull(free);
        }

        // Those runs are gone too (their locks died with them): the next run's sweeps remove what they left.
        foreach (var held in locks)
        {
            await held.DisposeAsync();
        }
        await SweepItems();
        await SweepSeats(Planner, "sc-21");
        await SweepSeats(Triage, "gh-22");
        await SweepSessions("gh-22");
        Assert.All(live, path => Assert.False(Directory.Exists(path), path));
        Assert.All(liveFiles, file => Assert.False(File.Exists(file), file));
        Assert.True(Directory.Exists(resumable));
    }

    [Fact]
    public async Task A_single_run_sweeps_the_same_leftovers_in_the_same_places_as_before()
    {
        var head = await Head();
        await Item("sc-1", WorkState.Implement); // resumable
        await Item("sc-2", WorkState.Implement, WorkState.Review);
        await Item("sc-3", WorkState.Implement, WorkState.Escalated);
        var kept = (await Workspaces().PrepareAsync(Repo, "factory/sc-1", None)).Path;
        var swept = new List<string>(await GateWorktrees("sc-1", head)) // a gate run's worktree is never resumed, whatever its item's state
        {
            (await Workspaces().PrepareAsync(Repo, "factory/sc-2", None)).Path,
            (await Workspaces().PrepareAsync(Repo, "factory/sc-3", None)).Path,
            (await Workspaces().PrepareAsync(Repo, "factory/sc-404", None)).Path, // no such item
        };
        var stray = Path.Combine(Path.GetDirectoryName(kept)!, "not-ours");
        Directory.CreateDirectory(stray);
        var plan = (await Planner.PrepareAsync(Repo, "factory/plan-sc-1", None)).Path;
        var triage = (await Triage.PrepareAsync(Repo, "factory/triage-gh-5", None)).Path;

        await SweepItems();

        // Each sweep sees only its own root.
        Assert.True(Directory.Exists(kept));
        Assert.All(swept.Append(stray), path => Assert.False(Directory.Exists(path), path));
        Assert.True(Directory.Exists(plan));
        Assert.True(Directory.Exists(triage));

        // A planner's and a triage's sweep keep nothing, the asking item's own leftover included (its run holds its lock).
        var sc1 = await Item("sc-1");
        await using var running = await Live(sc1);
        SessionFile($"sc-1.{Guid.NewGuid():N}.json");
        SessionFile("left.json");
        await SweepSeats(Planner, "sc-1");
        await SweepSeats(Triage, "sc-1");
        await SweepSessions("sc-1");
        Assert.False(Directory.Exists(plan));
        Assert.False(Directory.Exists(triage));
        Assert.Empty(Directory.EnumerateFiles(Sessions));
        Assert.True(Directory.Exists(kept));
    }

    [Fact]
    public async Task A_leftover_is_judged_again_under_its_items_run_lock_and_deleted_while_that_lock_is_held()
    {
        var sc1 = await Item("sc-1", WorkState.Implement, WorkState.Review);
        var path = (await Workspaces().PrepareAsync(Repo, "factory/sc-1", None)).Path;
        var asked = 0;

        // Its run ended as the sweep took the lock, leaving the item to be resumed in that worktree: the second answer decides.
        await Workspaces().SweepOrphansAsync(async (_, _) =>
        {
            if (++asked == 2)
            {
                Assert.Null(await _locks.TryAcquireAsync(sc1.Id, None)); // asked under the item's run lock
                return true;
            }
            return false;
        }, new LiveRuns(_ledger, _locks), None);
        Assert.Equal(2, asked);
        Assert.True(Directory.Exists(path));

        // Deleted: no run of the item can start while its worktree is being removed (the prune runs under its run lock).
        var pruned = false;
        await Workspaces(git: async (cwd, env, args, ct) =>
        {
            if (args.Contains("prune"))
            {
                pruned = true;
                Assert.False(Directory.Exists(path));
                Assert.Null(await _locks.TryAcquireAsync(sc1.Id, None));
            }
            return await GitWorkspace.RunGitAsync(cwd, env, args, ct);
        }).SweepOrphansAsync((_, _) => Task.FromResult(false), new LiveRuns(_ledger, _locks), None);
        Assert.True(pruned);
        await using var free = await _locks.TryAcquireAsync(sc1.Id, None);
        Assert.NotNull(free);
    }

    [Theory]
    [InlineData("factory-sc-7", "sc-7")]
    [InlineData("factory-gh-3", "gh-3")]
    [InlineData("factory-changeset-sc-7", "sc-7")]
    [InlineData("factory-triage-gh-3", "gh-3")]
    [InlineData("factory-plan-sc-7", "sc-7")]
    [InlineData("gate-sc-7-base", "sc-7")]
    [InlineData("gate-sc-7-head", "sc-7")]
    [InlineData("gate-gh-3-base-retry", "gh-3")]
    [InlineData("gate-sc-7-base-retry-2", "sc-7")]
    [InlineData("gate-sc-7", "sc-7")]
    [InlineData("gate-sc-7x-base", null)]
    [InlineData("gate-xx-7-base", null)]
    [InlineData("gate-7-base", null)]
    [InlineData("factory-changeset-triage-gh-7", null)]
    [InlineData("factory-plan-7", null)]
    [InlineData("factory-plan-SC-7", null)]
    [InlineData("factory-triage-", null)]
    [InlineData("not-ours", null)]
    public void A_leftover_directory_names_the_item_whose_run_made_it(string name, string? item) =>
        Assert.Equal(item, ItemNaming.OwnerOfLeftover(name)?.ToString());

    [Theory]
    [InlineData("sc-7.0123456789abcdef0123456789abcdef.json", "sc-7")]
    [InlineData("gh-3.x.json", "gh-3")]
    [InlineData("0123456789abcdef0123456789abcdef.json", null)]
    [InlineData("7.x.json", null)]
    [InlineData("sc-7.json", null)]
    [InlineData("sc-7.x.y.json", null)]
    [InlineData("sc-7.x.txt", null)]
    public void A_session_file_names_the_item_whose_run_opened_it(string name, string? item) =>
        Assert.Equal(item, ItemNaming.OwnerOfSessionFile(name)?.ToString());

    [Fact]
    public async Task Leftovers_of_no_item_or_of_one_the_ledger_never_saw_are_claimed_without_a_lock_and_the_askers_own_too()
    {
        var sc1 = await Item("sc-1");
        var sc2 = await Item("sc-2");
        await using var mine = await Live(sc1);
        var live = new LiveRuns(_ledger, _locks, ItemNaming.ParseAny("sc-1"));

        await using (var nobody = await live.TryClaimAsync(null, None))
        await using (var unknown = await live.TryClaimAsync(ItemNaming.ParseAny("sc-404"), None))
        await using (var own = await live.TryClaimAsync(ItemNaming.ParseAny("sc-1"), None))
        {
            Assert.All(new[] { nobody, unknown, own }, claim => Assert.False(claim!.UnderRunLock));
        }
        // Without the exemption its own held lock makes its leftovers a live run's, like anyone's.
        Assert.Null(await new LiveRuns(_ledger, _locks).TryClaimAsync(ItemNaming.ParseAny("sc-1"), None));
        await using (var other = await live.TryClaimAsync(ItemNaming.ParseAny("sc-2"), None))
        {
            Assert.True(other!.UnderRunLock);
            Assert.Null(await _locks.TryAcquireAsync(sc2.Id, None));
        }
        await using var released = await _locks.TryAcquireAsync(sc2.Id, None);
        Assert.NotNull(released);
        await using var all = await LiveRuns.None.TryClaimAsync(ItemNaming.ParseAny("sc-1"), None);
        Assert.False(all!.UnderRunLock);
    }

    // ---- the shared clone ----

    /// <summary>The git subcommands that change a clone's refs or its worktree list.</summary>
    private static bool Writes(IReadOnlyList<string> args) =>
        args.Any(a => a is "clone" or "fetch" or "remote" or "worktree" or "commit" or "merge" or "push");

    [Fact]
    public async Task Overlapping_runs_fetch_add_worktrees_commit_and_push_on_one_clone_and_all_succeed_one_writer_at_a_time()
    {
        var (writers, most, calls) = (0, 0, 0);
        GitCommand counting = async (cwd, env, args, ct) =>
        {
            if (!Writes(args))
            {
                return await GitWorkspace.RunGitAsync(cwd, env, args, ct);
            }
            var now = Interlocked.Increment(ref writers);
            InterlockedMax(ref most, now);
            Interlocked.Increment(ref calls);
            try
            {
                return await GitWorkspace.RunGitAsync(cwd, env, args, ct);
            }
            finally
            {
                Interlocked.Decrement(ref writers);
            }
        };

        // Each "run" has its own GitWorkspace, as each process and each run does. The first round races on a clone that does not
        // exist yet; the second on fetches of one that does.
        async Task<string> Run(int n)
        {
            var workspaces = Workspaces(git: counting);
            var first = await workspaces.PrepareAsync(Repo, $"factory/sc-{n}", None);
            Assert.Equal("hello\n", File.ReadAllText(Path.Combine(first.Path, "README.md")));
            await workspaces.RemoveAsync(Repo, first, None);
            var ws = await workspaces.PrepareAsync(Repo, $"factory/sc-{n}", None);
            File.WriteAllText(Path.Combine(ws.Path, $"run-{n}.txt"), $"{n}\n");
            Assert.True(await workspaces.CommitAndPushAsync(Repo, ws, $"sc-{n}", TestGrants.Untainted, None));
            var gate = await workspaces.PrepareCommitAsync(Repo, $"gate-sc-{n}-head", await workspaces.HeadAsync(ws, None), None);
            Assert.True(File.Exists(Path.Combine(gate.Path, $"run-{n}.txt")));
            await workspaces.RemoveAsync(Repo, gate, None);
            return ws.Path;
        }
        var paths = await Task.WhenAll(Enumerable.Range(1, 6).Select(n => Task.Run(() => Run(n))));

        Assert.Equal(1, most);
        Assert.True(calls >= 6 * 6, $"{calls} writes");
        // Every run's worktree is there with its own work, and every branch reached origin.
        var registered = Git(Path.Combine(WorkRoot, "repos", "acme", "widgets"), "worktree", "list");
        var pushed = Git(_remotes[Repo], "for-each-ref", "--format=%(refname:short)", "refs/heads/factory");
        for (var n = 1; n <= 6; n++)
        {
            Assert.True(File.Exists(Path.Combine(paths[n - 1], $"run-{n}.txt")));
            Assert.DoesNotContain(Directory.EnumerateFiles(paths[n - 1], "run-*.txt"), f => Path.GetFileName(f) != $"run-{n}.txt");
            Assert.Contains($"factory-sc-{n} ", registered.Replace("\n", " \n"));
            Assert.Contains($"factory/sc-{n}", pushed.Split('\n'));
        }
    }

    [Fact]
    public async Task A_run_never_sees_another_runs_half_finished_fetch_and_reads_by_object_do_not_wait()
    {
        var head = await Head(); // the clone exists
        var halfway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Run A stops between its fetch and the refresh of origin/HEAD that follows it: its refs are half updated.
        var a = Workspaces(git: async (cwd, env, args, ct) =>
        {
            var output = await GitWorkspace.RunGitAsync(cwd, env, args, ct);
            if (args.Contains("fetch"))
            {
                halfway.SetResult();
                await resume.Task;
            }
            return output;
        });
        var seen = new List<string>();
        var b = Workspaces(git: (cwd, env, args, ct) =>
        {
            lock (seen)
            {
                seen.Add(string.Join(' ', args.Where(x => !x.StartsWith('-') && !x.Contains('='))));
            }
            return GitWorkspace.RunGitAsync(cwd, env, args, ct);
        });

        var first = a.PrepareAsync(Repo, "factory/sc-1", None);
        await halfway.Task;
        var second = b.PrepareAsync(Repo, "factory/sc-2", None);
        // B's sweep deletes a leftover directory at once, and waits with the prune of the clone's worktree list.
        var leftover = Path.Combine(WorkRoot, GitWorkspace.ItemWorktrees, "acme", "widgets", "factory-gone");
        Directory.CreateDirectory(leftover);
        var sweep = b.SweepOrphansAsync((name, _) => Task.FromResult(name != "factory-gone"), LiveRuns.None, None);
        // Reads by object take no lock: they answer while A still holds the clone.
        Assert.Contains("README.md", await b.FilesAsync(Repo, head, None));
        Assert.Equal("hello\n", await b.ReadFileAsync(Repo, head, "README.md", None));
        await Task.Delay(300, None);
        Assert.False(second.IsCompleted);
        Assert.False(sweep.IsCompleted);
        Assert.False(Directory.Exists(leftover));
        lock (seen)
        {
            Assert.DoesNotContain(seen, call => Writes(call.Split(' ')) || call.Contains("symbolic-ref"));
        }

        resume.SetResult();
        var (one, two) = (await first, await second);
        await sweep;
        Assert.True(File.Exists(Path.Combine(one.Path, "README.md")));
        Assert.True(File.Exists(Path.Combine(two.Path, "README.md")));
        Assert.Equal("main", two.BaseBranch);
    }

    [Fact]
    public async Task The_clone_lock_is_one_file_per_clone_beside_the_clones_waited_for_and_given_up_on_cancellation()
    {
        await Head();
        var path = Path.Combine(WorkRoot, CloneLock.DirectoryName, "acme", "widgets.lock");
        Assert.True(File.Exists(path)); // GitWorkspace's own lock is this file
        var held = await CloneLock.AcquireAsync(path, None);

        // Another repository's clone is not held up; this one's is, until the holder lets go or the waiter is cancelled.
        Assert.True(Directory.Exists((await Workspaces().PrepareAsync(Gears, "factory/sc-1", None)).Path));
        using var cancel = new CancellationTokenSource();
        var waiting = Workspaces().PrepareAsync(Repo, "factory/sc-1", cancel.Token);
        var next = CloneLock.AcquireAsync(path, None);
        await Task.Delay(200, None);
        Assert.False(waiting.IsCompleted);
        Assert.False(next.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(Directory.Exists(Path.Combine(WorkRoot, GitWorkspace.ItemWorktrees, "acme", "widgets", "factory-sc-1")));

        held.Dispose();
        using (await next)
        {
        }
        Assert.True(Directory.Exists((await Workspaces().PrepareAsync(Repo, "factory/sc-1", None)).Path));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
