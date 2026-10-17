using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Plans;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Real git against a local bare "remote"; no network.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class GitWorkspaceTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _root = Directory.CreateTempSubdirectory("df-git-").FullName;
    private readonly string _remote;

    public GitWorkspaceTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", _remote);
        Git(_root, "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", _remote, "main");
    }

    [Fact]
    public async Task Recovery_first_read_closes_clone_locks_owner_side_without_a_worker_or_slot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "recovery-first")).FullName;
        if (OperatingSystem.IsMacOS())
        {
            var chmod = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false };
            foreach (var arg in new[] { "+a", "user:nobody allow list,search,read,file_inherit,directory_inherit", root }) chmod.ArgumentList.Add(arg);
            using var process = Process.Start(chmod)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var head = Git(Path.Combine(_root, "seed"), "rev-parse", "HEAD").Trim();
        var workspace = new GitWorkspace(root, _ => _remote, (_, _) => Task.FromResult<string?>(null),
            git: async (cwd, env, args, ct) =>
            {
                if (OperatingSystem.IsMacOS()) Assert.True(DarkFactory.Orchestrator.Worker.WorkerSandbox.IsClosed(
                    await DarkFactory.Orchestrator.Worker.WorkerSandbox.ListAclAsync(Path.Combine(root, CloneLock.DirectoryName), ct)));
                return await GitWorkspace.RunGitAsync(cwd, env, args, ct);
            }, protectCloneLocks: true);
        Assert.Equal("hello\n", await ((Gate.IReviewFiles)workspace).ReadAsync(Repo, head, "README.md", CancellationToken.None));
        var locks = Path.Combine(root, CloneLock.DirectoryName);
        if (OperatingSystem.IsMacOS())
            Assert.True(DarkFactory.Orchestrator.Worker.WorkerSandbox.IsClosed(
                await DarkFactory.Orchestrator.Worker.WorkerSandbox.ListAclAsync(locks, CancellationToken.None)));
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(locks));
    }

    private const string Token = "ghs_FakeInstallationToken123";
    private static readonly string BasicAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"x-access-token:{Token}"));

    private readonly List<(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env)> _gitCalls = [];
    private int _tokenRequests;

    private GitWorkspace Workspace() => new(Path.Combine(_root, "work"), _ => _remote,
        (_, _) =>
        {
            _tokenRequests++;
            return Task.FromResult<string?>(Token);
        },
        (cwd, env, args, ct) =>
        {
            _gitCalls.Add((args, env));
            return GitWorkspace.RunGitAsync(cwd, env, args, ct);
        },
        gitStream: (cwd, env, args, read, ct) =>
        {
            _gitCalls.Add((args, env));
            return GitWorkspace.RunGitStreamAsync(cwd, env, args, read, ct);
        });

    /// <summary>Git subcommands that talk to the remote and so must carry the App token.</summary>
    private static bool IsNetworkCall(IReadOnlyList<string> args) =>
        Subcommand(args) is "clone" or "fetch" or "push" or "pull" or "ls-remote"
        || (Subcommand(args) == "remote" && args.Contains("--auto"));

    /// <summary>The git subcommand, skipping global options such as <c>-c key=value</c>.</summary>
    private static string Subcommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-c")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }
        return "";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_restart_replaces_old_work_and_includes_the_delivered_commit_without_losing_plan_blockers(bool planChild)
    {
        var seed = Path.Combine(_root, "seed");
        Git(seed, "checkout", "-b", "factory/changeset-sc-1");
        File.WriteAllText(Path.Combine(seed, "blocker.txt"), "merged plan blocker");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "blocker");
        Git(seed, "push", _remote, "factory/changeset-sc-1");
        Git(seed, "checkout", "main");
        File.WriteAllText(Path.Combine(seed, "fixed.txt"), "delivered repair");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "repair");
        Git(seed, "push", _remote, "main");
        var corrected = Git(seed, "rev-parse", "HEAD").Trim();
        var workspace = Workspace();
        var old = await workspace.PrepareAsync(Repo, "factory/sc-77", default);
        File.WriteAllText(Path.Combine(old.Path, "stale.txt"), "old assumptions");
        var fresh = await workspace.PrepareCorrectedAsync(Repo, "factory/sc-77", corrected, default,
            planChild ? "factory/changeset-sc-1" : null);
        Assert.Equal("delivered repair", File.ReadAllText(Path.Combine(fresh.Path, "fixed.txt")));
        Assert.False(File.Exists(Path.Combine(fresh.Path, "stale.txt")));
        Assert.Equal(planChild, File.Exists(Path.Combine(fresh.Path, "blocker.txt")));
        Assert.Equal(planChild ? "factory/changeset-sc-1" : "main", fresh.BaseBranch);
        Assert.Equal("0", Git(fresh.Path, "rev-list", "--count", $"HEAD..{corrected}").Trim());
        await workspace.RemoveAsync(Repo, fresh, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conflicting_correction_and_plan_blocker_reach_a_fresh_worker_and_keep_both_merge_parents(bool resolve)
    {
        var seed = Path.Combine(_root, "seed");
        const string changeset = "factory/changeset-sc-10";
        Git(seed, "checkout", "-b", changeset);
        File.WriteAllText(Path.Combine(seed, "README.md"), "plan blocker behavior\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "plan blocker");
        Git(seed, "push", _remote, changeset);
        var blocker = Git(seed, "rev-parse", "HEAD").Trim();
        Git(seed, "checkout", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "corrected behavior\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "correction");
        Git(seed, "push", _remote, "main");
        var corrected = Git(seed, "rev-parse", "HEAD").Trim();
        var f = await RecoveryContinuationBoundaryTests.SeedSafe();
        await using (var db = f.Contexts.CreateDbContext())
        {
            var parent = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10");
            var change = new Changeset("owner/alpha", changeset, "main", blocker);
            db.Add(new LedgerEntry { WorkItemId = parent.Id, State = parent.State, Step = RunPipeline.Steps.Changeset,
                Detail = change.ToDetail(), Outcome = StepOutcome.Passed });
            db.Add(new LedgerEntry { WorkItemId = parent.Id, State = parent.State, Step = RunPipeline.Steps.ChangesetPushed,
                Detail = DarkFactory.Orchestrator.Plans.Changeset.PushedDetail(changeset, blocker, false), Outcome = StepOutcome.Passed });
            foreach (var row in await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Step).ToListAsync())
                row.Detail = JsonSerializer.Serialize(RecoveryRedirect.Parse(row.Detail)! with { Commit = corrected });
            await db.SaveChangesAsync();
        }
        var material = await f.Evidence.CaptureAsync(f.Trigger, default);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.Answer = JsonSerializer.Serialize(HotFileAssessment.FromDetail(incident.Answer)! with { Material = material });
            await db.SaveChangesAsync();
        }
        string? conflictPath = null;
        var worker = new FakeWorker(async call =>
        {
            Assert.Null(call.Resume);
            Assert.Contains("original spec", call.Prompt);
            Assert.Contains("Retain BOTH parents", call.Prompt);
            Assert.Contains("README.md", call.Prompt);
            Assert.Contains(corrected, call.Prompt);
            var path = conflictPath!;
            Assert.Contains("<<<<<<<", File.ReadAllText(path));
            if (resolve) File.WriteAllText(path, "plan blocker behavior AND corrected behavior\n");
            await call.Callbacks.OnSession!("fresh-conflict", default);
            return new Worker.WorkerResult("fresh-conflict", 0, false, "success", "done", "");
        });
        var real = Workspace();
        // The path is deterministic under the real workspace, as for every production worktree.
        conflictPath = Path.Combine(_root, "work", "worktrees", "owner", "alpha", "factory-sc-2", "README.md");
        await using var recovery = RecoveryContinuationBoundaryTests.Recovery(f);
        var result = await RecoveryContinuationBoundaryTests.Run(f, recovery, 2, real, worker);
        Assert.Single(worker.Calls);
        Assert.Equal(resolve ? WorkState.Review : WorkState.Escalated, result.State);
        var parents = Git(_root, "--git-dir=" + _remote, "rev-list", "--parents", "-n", "1", "factory/sc-2").Trim().Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal(blocker, parents[1]); Assert.Equal(corrected, parents[2]);
        if (!resolve) Assert.Contains("conflict markers", result.Error);
    }

    [Fact]
    public async Task Correction_paths_include_committed_staged_unstaged_untracked_and_both_rename_sides()
    {
        var git = Workspace();
        var ws = await git.PrepareAsync(Repo, "factory/rc-1", default);
        var pinned = (await git.HeadAsync(ws, default)).Trim();
        File.WriteAllText(Path.Combine(ws.Path, "committed.txt"), "first");
        await git.CommitAndPushAsync(Repo, ws, "first", TestGrants.Untainted, default);
        File.Move(Path.Combine(ws.Path, "README.md"), Path.Combine(ws.Path, "renamed.txt"));
        File.WriteAllText(Path.Combine(ws.Path, "staged.txt"), "stage");
        Git(ws.Path, "add", "staged.txt");
        File.WriteAllText(Path.Combine(ws.Path, "committed.txt"), "modified");
        File.WriteAllText(Path.Combine(ws.Path, "untracked.txt"), "new");
        File.WriteAllText(Path.Combine(ws.Path, "line\nbreak.txt"), "new");
        var paths = await git.CorrectionPathsAsync(ws, pinned, default);
        Assert.Equal(new[] { "README.md", "committed.txt", "line\nbreak.txt", "renamed.txt", "staged.txt", "untracked.txt" }, paths.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Correction_authority_is_checked_before_staging_token_mint_and_final_push(int refusal)
    {
        var git = Workspace();
        var ws = await git.PrepareAsync(Repo, "factory/rc-2", default);
        File.WriteAllText(Path.Combine(ws.Path, "repair.txt"), "repair");
        _gitCalls.Clear();
        var tokens = _tokenRequests;
        var reads = 0;
        var grant = TestGrants.Untainted.WithValidation(_ => ++reads == refusal
            ? Task.FromException(new InvalidOperationException("stale incident grant")) : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.CommitAndPushAsync(Repo, ws, "repair", grant, default));
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "push");
        if (refusal == 1) Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "add");
        if (refusal <= 2) Assert.Equal(tokens, _tokenRequests);
        Assert.Equal(refusal, reads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Correction_base_update_push_rechecks_authority_after_waits(int refusal)
    {
        var git = Workspace();
        var ws = await git.PrepareAsync(Repo, "factory/rc-3", default);
        _gitCalls.Clear();
        var reads = 0;
        Task Check(CancellationToken _) => ++reads == refusal
            ? Task.FromException(new InvalidOperationException("stale incident grant")) : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.PushAsync(Repo, ws, default, Check, Check));
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "push");
        Assert.Equal(refusal, reads);
    }

    [Fact]
    public async Task Every_network_git_call_carries_the_app_token_and_no_call_puts_it_in_argv_or_config()
    {
        var workspace = Workspace();
        await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None); // clone
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None); // fetch + set-head
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-9: Fix", TestGrants.Untainted, CancellationToken.None)); // push

        var network = _gitCalls.Where(c => IsNetworkCall(c.Args)).ToList();
        Assert.Equal(["clone", "fetch", "remote", "push"], network.Select(c => Subcommand(c.Args)));
        Assert.All(network, c =>
        {
            Assert.NotNull(c.Env);
            Assert.Equal("http.https://github.com/.extraheader", c.Env["GIT_CONFIG_KEY_0"]);
            Assert.Equal($"AUTHORIZATION: basic {BasicAuth}", c.Env["GIT_CONFIG_VALUE_0"]);
            Assert.Equal(("credential.helper", ""), (c.Env["GIT_CONFIG_KEY_1"], c.Env["GIT_CONFIG_VALUE_1"]));
        });
        Assert.Equal(3, _tokenRequests); // one per PrepareAsync, one for the push

        Assert.All(_gitCalls, c => Assert.DoesNotContain(c.Args, a => a.Contains(Token) || a.Contains(BasicAuth)));
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        foreach (var dir in new[] { clone, ws.Path })
        {
            var config = Git(dir, "config", "--list", "--show-origin");
            Assert.DoesNotContain(Token, config);
            Assert.DoesNotContain(BasicAuth, config);
            Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(_remote, Git(dir, "remote", "get-url", "origin").Trim());
        }
    }

    [Fact]
    public async Task Remove_deletes_the_worktree()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-4", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.False(Directory.Exists(ws.Path));
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        Assert.Single(Git(clone, "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries)); // main clone only
    }

    [Fact]
    public async Task Prepare_creates_worktree_on_factory_branch_from_default_branch()
    {
        var ws = await Workspace().PrepareAsync(Repo, "factory/sc-1", CancellationToken.None);

        Assert.Equal("main", ws.BaseBranch);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
        Assert.Equal("factory/sc-1", Git(ws.Path, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Commit_and_push_publishes_worker_changes_to_the_factory_branch()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-2", CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-2: Fix", TestGrants.Untainted, CancellationToken.None));

        Assert.Equal("sc-2: Fix", Git(_remote, "log", "-1", "--format=%s", "factory/sc-2").Trim());
        Assert.Equal("dark-factory[bot]", Git(_remote, "log", "-1", "--format=%an", "factory/sc-2").Trim());
        // The pushed commit is the one a fix round waits for on the PR (sc-25380).
        Assert.Equal(Git(_remote, "rev-parse", "factory/sc-2").Trim(), await workspace.HeadAsync(ws, CancellationToken.None));
    }

    [Fact]
    public async Task Rerun_prepares_a_fresh_worktree_and_push_without_changes_returns_false()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-3", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "scratch.txt"), "x");

        var second = await workspace.PrepareAsync(Repo, "factory/sc-3", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(second.Path, "scratch.txt")));
        Assert.False(await workspace.CommitAndPushAsync(Repo, second, "nothing", TestGrants.Untainted, CancellationToken.None));
    }

    private sealed class FakeSandbox(bool deleteRemovesNothing = false) : DarkFactory.Orchestrator.Worker.IWorkerSandbox
    {
        public List<string> Calls { get; } = [];
        public Task CloseToWorkerAsync(string path, CancellationToken ct) => Task.CompletedTask;

        /// <summary>The worktree's <c>ls -led</c> as the push check reads it (null: it cannot be read); the check is the production one, for the user <c>_dftest2</c>.</summary>
        public Func<string, string?> Listing { get; init; } = path => $"drwxr-xr-x+ 3 owner staff 96 Oct 13 12:00 {path}\n 0: user:_dftest2 allow list,add_file,search,delete\n";

        /// <summary>Several worker slots: what is inside the worktree is judged too, each entry's ACL read through <see cref="EntryAcl"/> (null: one slot, the root alone).</summary>
        public Func<string, string?>? EntryAcl { get; init; }

        public Task EnsureOnlyWorkerWritesAsync(string path, CancellationToken ct)
        {
            Calls.Add($"writers {path}");
            return DarkFactory.Orchestrator.Worker.WorkerSlots.EnsureOnlyWriterAsync(path, "_dftest2",
                (p, _) => Task.FromResult(Listing(p) ?? throw new InvalidOperationException("/bin/ls failed (1): no such file")), TimeSpan.Zero, ct,
                inside: EntryAcl);
        }
        public Task ShareAsync(string path, CancellationToken ct)
        {
            Calls.Add($"share {path} {(Directory.EnumerateFileSystemEntries(path).Any() ? "non-empty" : "empty")}");
            return Task.CompletedTask;
        }
        public Task DeleteAsWorkerAsync(string path, CancellationToken ct)
        {
            Calls.Add($"delete {path}");
            if (deleteRemovesNothing)
            {
                return Task.CompletedTask;
            }
            foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(path, recursive: true);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// sc-25990 (E4/E7): the push of worker work is refused — nothing staged, committed or pushed, no token minted — when the
    /// worktree's write grants name anyone but its own slot's user and the owner, or cannot be read.
    /// </summary>
    [Theory]
    [InlineData(" 0: user:_dftest2 allow list,add_file,search,delete\n 1: user:_dftest allow list,add_file,search", "user:_dftest")]
    [InlineData(" 0: user:_dftest allow list,add_file,search,delete,add_subdirectory", "user:_dftest")]
    [InlineData(" 0: user:_dftest2 allow list,add_file,search\n 1: group:staff allow add_file", "group:staff")]
    [InlineData(" 0: user:_dftest2 allow list,add_file,search\n 1: ABCDEF01-2345-6789-ABCD-EF0123456789 allow delete_child", "ABCDEF01-2345-6789-ABCD-EF0123456789")]
    [InlineData(null, "could not be read")]
    public async Task The_push_of_worker_work_is_refused_when_another_user_may_write_the_worktree(string? acl, string names)
    {
        var listing = acl;
        var sandbox = new FakeSandbox { Listing = path => listing is null ? null : $"drwxr-xr-x+ 3 owner staff 96 Oct 13 12:00 {path}\n{listing}\n" };
        var tokens = 0;
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => { tokens++; return Task.FromResult<string?>(Token); },
            sandbox: sandbox);
        var story = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);
        File.WriteAllText(Path.Combine(story.Path, "work.txt"), "the worker's\n");
        (var before, tokens) = (await workspace.HeadAsync(story, CancellationToken.None), 0);

        var refused = await Assert.ThrowsAsync<DarkFactory.Orchestrator.Worker.WorktreeWritersException>(
            () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));

        Assert.Contains(names, refused.Message);
        Assert.Contains("Refusing to push", refused.Message);
        Assert.Contains("_dftest2", refused.Message); // whose worktree it is
        Assert.Equal(story.Path, refused.Path);
        Assert.Equal(0, tokens); // no push token was minted
        Assert.Equal(before, await workspace.HeadAsync(story, CancellationToken.None)); // nothing committed
        Assert.Equal("", Git(_root, "--git-dir", _remote, "branch", "--list", "factory/sc-9").Trim()); // nothing pushed
        Assert.Contains("?? work.txt", Git(story.Path, "status", "--porcelain")); // and nothing staged

        // With only its own slot's user (and the owner) writing it, the same work is pushed.
        listing = " 0: user:_dftest2 allow list,add_file,search,delete\n 1: user:_dftest inherited allow list,search,readattr\n";
        Assert.True(await workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));
        Assert.NotEqual("", Git(_root, "--git-dir", _remote, "branch", "--list", "factory/sc-9").Trim());
    }

    /// <summary>
    /// sc-25990 (E4/E7), several worker slots: what a worker made inside its worktree is that sandbox user's own, so code it ran
    /// can have opened it to another slot's user without touching the worktree's root. The push is refused — nothing staged,
    /// committed or pushed, no token — for the first entry inside whose mode lets its group or everyone write it, whose ACL
    /// lets another principal write it, or that cannot be read; once it is closed again the same work is pushed.
    /// </summary>
    [Theory]
    [InlineData("other-file", "src/deep/work.txt in it has a mode (0646) that lets every user write it")]
    [InlineData("group-file", "src/deep/work.txt in it has a mode (0664) that lets its group write it")]
    [InlineData("other-directory", "src/deep in it has a mode (0757) that lets every user write it")]
    [InlineData("acl", "the ACL of src/deep/work.txt in it lets user:_dftest write it (write)")]
    [InlineData("acl-unreadable", "src/deep/work.txt in it could not be read (its ACL could not be read (errno 13)), so who may write it is not known")]
    [InlineData("unreadable-directory", "src/deep in it could not be read (")]
    public async Task With_several_slots_the_push_is_refused_when_something_inside_the_worktree_is_open_to_another_user(string how, string names)
    {
        const UnixFileMode File644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode Directory755 = File644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var open = true;
        var acls = new List<string>();
        var sandbox = new FakeSandbox
        {
            EntryAcl = path =>
            {
                acls.Add(path);
                return !open || !path.EndsWith("work.txt", StringComparison.Ordinal) ? null : how switch
                {
                    "acl" => "!#acl 1\nuser:FFFFEEEE-DDDD-CCCC-BBBB-AAAA000001C2:_dftest:450:allow:read,write\n",
                    "acl-unreadable" => throw new IOException("its ACL could not be read (errno 13)"),
                    _ => null,
                };
            },
        };
        var tokens = 0;
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => { tokens++; return Task.FromResult<string?>(Token); },
            sandbox: sandbox);
        var story = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);
        var deep = Directory.CreateDirectory(Path.Combine(story.Path, "src", "deep")).FullName;
        var file = Path.Combine(deep, "work.txt");
        File.WriteAllText(file, "the worker's\n");
        File.SetUnixFileMode(file, File644);
        File.SetUnixFileMode(deep, Directory755);
        switch (how)
        {
            case "other-file": File.SetUnixFileMode(file, File644 | UnixFileMode.OtherWrite); break;
            case "group-file": File.SetUnixFileMode(file, File644 | UnixFileMode.GroupWrite); break;
            case "other-directory": File.SetUnixFileMode(deep, Directory755 | UnixFileMode.OtherWrite); break;
            case "unreadable-directory": File.SetUnixFileMode(deep, UnixFileMode.None); break;
        }
        (var before, tokens) = (await workspace.HeadAsync(story, CancellationToken.None), 0);

        var refused = await Assert.ThrowsAsync<DarkFactory.Orchestrator.Worker.WorktreeWritersException>(
            () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));

        Assert.Contains($"Refusing to push the work in {story.Path}: {names}", refused.Message);
        Assert.Contains("the session's work is discarded with the worktree", refused.Message);
        Assert.Equal(0, tokens); // no push token was minted
        Assert.Equal(before, await workspace.HeadAsync(story, CancellationToken.None)); // nothing committed
        Assert.Equal("", Git(_root, "--git-dir", _remote, "branch", "--list", "factory/sc-9").Trim()); // nothing pushed
        File.SetUnixFileMode(deep, Directory755);
        Assert.Contains("?? src/", Git(story.Path, "status", "--porcelain")); // and nothing staged

        // Closed again (the modes a worker's files have under the usual umask; no such ACL entry): the same work is pushed.
        File.SetUnixFileMode(file, File644);
        (open, _) = (false, acls.Remove(file));
        Assert.True(await workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));
        Assert.NotEqual("", Git(_root, "--git-dir", _remote, "branch", "--list", "factory/sc-9").Trim());
        // Every entry inside was asked about, the worktree's `.git` file and the nested ones included.
        Assert.Contains(file, acls);
        Assert.Contains(deep, acls);
        Assert.Contains(Path.Combine(story.Path, ".git"), acls);
    }

    [Fact]
    public async Task With_several_slots_ordinary_modes_inside_the_worktree_push_and_a_symbolic_link_is_neither_followed_nor_judged()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-world-writable")).FullName;
        File.WriteAllText(Path.Combine(outside, "theirs.txt"), "not the worktree's\n");
        File.SetUnixFileMode(outside, (UnixFileMode)0b111_111_111);
        File.SetUnixFileMode(Path.Combine(outside, "theirs.txt"), (UnixFileMode)0b110_110_110);
        var asked = new List<string>();
        var sandbox = new FakeSandbox { EntryAcl = path => { asked.Add(path); return null; } };
        var workspace = Workspace(sandbox);
        var story = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);
        var deep = Directory.CreateDirectory(Path.Combine(story.Path, "src", "deep")).FullName;
        File.WriteAllText(Path.Combine(deep, "work.txt"), "the worker's\n");
        File.SetUnixFileMode(Path.Combine(deep, "work.txt"), (UnixFileMode)0b110_100_100); // 0644
        File.SetUnixFileMode(deep, (UnixFileMode)0b111_101_101); // 0755
        // Links out of the worktree, to a world-writable directory and to a world-writable file in it (a link's own mode says
        // nothing, and changing a link takes write on its directory, which is judged).
        Directory.CreateSymbolicLink(Path.Combine(deep, "to-directory"), outside);
        File.CreateSymbolicLink(Path.Combine(deep, "to-file"), Path.Combine(outside, "theirs.txt"));

        Assert.True(await workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));

        Assert.Contains(Path.Combine(deep, "work.txt"), asked);
        Assert.DoesNotContain(asked, path => path.Contains("to-directory", StringComparison.Ordinal) || path.Contains("to-file", StringComparison.Ordinal)
            || path.StartsWith(outside, StringComparison.Ordinal));
        Assert.Equal("120000", Git(_remote, "ls-tree", "factory/sc-9", "src/deep/to-directory").Split(' ')[0]); // pushed as the link it is
    }

    [Fact]
    public async Task With_one_slot_only_the_worktrees_root_is_judged_so_a_group_writable_file_inside_still_pushes()
    {
        // E9: with one sandbox user there is no other slot's user to open a file to; nothing inside is read (FakeSandbox's
        // EntryAcl is null, as WorkerSlots passes none with one slot) and the root's group bit is not judged.
        var sandbox = new FakeSandbox { Listing = path => $"drwxrwxr-x+ 3 owner staff 96 Oct 13 12:00 {path}\n 0: user:_dftest2 allow list,add_file,search,delete\n" };
        var workspace = Workspace(sandbox);
        var story = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);
        var file = Path.Combine(story.Path, "work.txt");
        File.WriteAllText(file, "the worker's\n");
        File.SetUnixFileMode(file, (UnixFileMode)0b110_110_110); // 0666

        Assert.True(await workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None));
    }

    [Fact]
    public async Task A_worktree_shared_with_no_worker_for_writing_is_not_asked_who_writes_it()
    {
        // A read-only seat (triage, planner) is never pushed with a grant in production; the check belongs to shared worktrees.
        var sandbox = new FakeSandbox { Listing = _ => null };
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token), sandbox: sandbox,
            shareWithWorker: false);
        var story = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);

        Assert.False(await workspace.CommitAndPushAsync(Repo, story, "nothing", TestGrants.Untainted, CancellationToken.None));
        Assert.DoesNotContain(sandbox.Calls, c => c.StartsWith("writers ", StringComparison.Ordinal));
    }

    private GitWorkspace Workspace(DarkFactory.Orchestrator.Worker.IWorkerSandbox sandbox, GitCommand? git = null) =>
        new(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token), git, sandbox);

    /// <summary>A sandbox whose worker user another run's worker phase may hold (<see cref="Busy"/>): a deletion that does not wait then deletes nothing.</summary>
    private sealed class BusySandbox : DarkFactory.Orchestrator.Worker.IWorkerSandbox
    {
        public bool Busy { get; set; } = true;
        public List<string> Calls { get; } = [];
        public Task CloseToWorkerAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureOnlyWorkerWritesAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task ShareAsync(string path, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsWorkerAsync(string path, CancellationToken ct)
        {
            Calls.Add($"delete {path}");
            Assert.False(Busy, "waited for a busy sandbox user"); // the real one would wait here, on no cancellation token
            Directory.Delete(path, recursive: true);
            return Task.CompletedTask;
        }

        public Task<bool> TryDeleteAsWorkerAsync(string path, CancellationToken ct)
        {
            Calls.Add($"try-delete {path}");
            if (!Busy)
            {
                Directory.Delete(path, recursive: true);
            }
            return Task.FromResult(!Busy);
        }
    }

    [Fact]
    public async Task The_clean_up_of_a_worktree_that_failed_never_waits_for_a_busy_sandbox_user_and_leaves_the_directory_as_a_leftover()
    {
        var sandbox = new BusySandbox();
        var workspace = Workspace(sandbox);
        var path = Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name, "factory-sc-404");
        Assert.False(await workspace.HasLeftoverAsync(Repo, "factory/sc-404", CancellationToken.None));

        // origin has no such branch: `git worktree add` fails under the clone's lock, after the directory was made. Outside a
        // worker phase (the merge queue's base update) the sandbox user is another run's: the clean-up asks once and leaves it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.RestoreAsync(Repo, "factory/sc-404", CancellationToken.None));

        Assert.Equal([$"try-delete {path}"], sandbox.Calls);
        Assert.True(Directory.Exists(path));
        // Not a worktree any run could reopen — and still a leftover: replacing it is a deletion as the worker user.
        Assert.Null(await workspace.ReopenAsync(Repo, "factory/sc-404", CancellationToken.None));
        Assert.True(await workspace.HasLeftoverAsync(Repo, "factory/sc-404", CancellationToken.None));

        // With the sandbox user free (inside a worker phase) the same failure cleans up after itself.
        sandbox.Busy = false;
        sandbox.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.RestoreAsync(Repo, "factory/sc-404", CancellationToken.None));
        Assert.Equal([$"delete {path}", $"try-delete {path}"], sandbox.Calls);
        Assert.False(Directory.Exists(path));
        Assert.False(await workspace.HasLeftoverAsync(Repo, "factory/sc-404", CancellationToken.None));
    }

    private string ClonePath => Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);

    private int WorktreeCount() => Git(ClonePath, "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public async Task Prepare_shares_the_fresh_worktree_while_empty_and_records_its_git_dir()
    {
        var sandbox = new FakeSandbox();
        var ws = await Workspace(sandbox).PrepareAsync(Repo, "factory/sc-5", CancellationToken.None);

        // Shared before the checkout, so the checkout inherits the ACL instead of a recursive chmod.
        Assert.Equal([$"share {ws.Path} empty"], sandbox.Calls);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
        Assert.Equal(Path.Combine(ClonePath, ".git", "worktrees", "factory-sc-5"), ws.GitDir.Replace("/private/var/", "/var/"));
    }

    [Fact]
    public async Task Owner_git_ignores_a_worker_rewritten_dot_git_file()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-6", CancellationToken.None);
        // The worker points the worktree's .git file at a repo whose hooks it controls.
        var evil = Path.Combine(_root, "evil");
        Git(_root, "init", evil);
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(evil, ".git", "hooks", "pre-commit");
        SandboxSupport.ExecutableAt(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.WriteAllText(Path.Combine(ws.Path, ".git"), $"gitdir: {Path.Combine(evil, ".git")}\n");
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-6: Fix", TestGrants.Untainted, CancellationToken.None));

        Assert.False(File.Exists(marker));
        Assert.Equal("sc-6: Fix", Git(_remote, "log", "-1", "--format=%s", "factory/sc-6").Trim());
    }

    [Fact]
    public async Task Reopen_finds_the_git_dir_from_the_clone_not_a_worker_rewritten_dot_git_file()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-8", CancellationToken.None);
        // An interrupted worker points .git at its own repo, on the same branch name.
        var evil = Path.Combine(_root, "evil");
        Git(_root, "init", "-b", "factory/sc-8", evil);
        File.WriteAllText(Path.Combine(ws.Path, ".git"), $"gitdir: {Path.Combine(evil, ".git")}\n");

        var reopened = await workspace.ReopenAsync(Repo, "factory/sc-8", CancellationToken.None);

        Assert.Equal(ws.GitDir, reopened?.GitDir);
    }

    [Fact]
    public async Task Remove_deletes_as_the_worker_first_then_the_owner_removes_what_is_left()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-7", CancellationToken.None);
        var locked = Directory.CreateDirectory(Path.Combine(ws.Path, "obj", "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "f"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.Equal([$"share {ws.Path} empty", $"delete {ws.Path}"], sandbox.Calls);
        Assert.False(Directory.Exists(ws.Path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Remove_deletes_as_the_worker_even_when_the_owner_could_delete_everything()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        // The owner never walks the worker-controlled tree before the worker has emptied it.
        Assert.Equal([$"share {ws.Path} empty", $"delete {ws.Path}"], sandbox.Calls);
    }

    [Fact]
    public async Task Remove_has_the_owner_delete_whatever_the_worker_left()
    {
        var sandbox = new FakeSandbox(deleteRemovesNothing: true);
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-10", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.False(Directory.Exists(ws.Path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Prepare_removes_the_worktree_when_a_step_after_worktree_add_fails()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox, (cwd, env, args, ct) => args.Contains("--absolute-git-dir")
            ? throw new InvalidOperationException("rev-parse exploded")
            : GitWorkspace.RunGitAsync(cwd, env, args, ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PrepareAsync(Repo, "factory/sc-11", CancellationToken.None));

        var path = Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name, "factory-sc-11");
        Assert.False(Directory.Exists(path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Sweep_deletes_worktrees_no_run_will_resume_and_keeps_the_rest()
    {
        var workspace = Workspace();
        var orphan = await workspace.PrepareAsync(Repo, "factory/sc-12", CancellationToken.None);
        var resumable = await workspace.PrepareAsync(Repo, "factory/sc-13", CancellationToken.None);
        var asked = new List<string>();

        await Workspace().SweepOrphansAsync((name, _) =>
        {
            asked.Add(name);
            return Task.FromResult(name == "factory-sc-13");
        }, LiveRuns.None, CancellationToken.None);

        Assert.Equal(["factory-sc-12", "factory-sc-13"], asked.Order());
        Assert.False(Directory.Exists(orphan.Path));
        Assert.True(File.Exists(Path.Combine(resumable.Path, "README.md")));
        Assert.Equal(2, WorktreeCount()); // the clone and the kept worktree
    }

    [Fact]
    public async Task A_triage_worktree_lives_apart_from_the_items_worktrees_and_is_not_shared_for_writing()
    {
        var sandbox = new FakeSandbox();
        var triage = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token), null, sandbox,
            worktreesDirectory: SandboxTriageRunner.TriageWorktrees, shareWithWorker: false);
        var item = await Workspace(sandbox).PrepareAsync(Repo, "factory/sc-15", CancellationToken.None);
        sandbox.Calls.Clear();

        var ws = await triage.PrepareAsync(Repo, "factory/triage-gh-1", CancellationToken.None);

        // Its own root, never the items' worktrees; read through the work root's inherited entry, not granted write.
        Assert.Equal(Path.Combine(_root, "work", SandboxTriageRunner.TriageWorktrees, Repo.Owner, Repo.Name, "factory-triage-gh-1"), ws.Path);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
        Assert.DoesNotContain(sandbox.Calls, c => c.StartsWith("share", StringComparison.Ordinal));
        // Each sweep sees only its own root: the items' sweep never asks about the triage worktree, and the triage sweep keeps no item's.
        var asked = new List<string>();
        await Workspace(sandbox).SweepOrphansAsync((name, _) => { asked.Add(name); return Task.FromResult(true); }, LiveRuns.None, CancellationToken.None);
        Assert.Equal(["factory-sc-15"], asked);
        await triage.SweepOrphansAsync((_, _) => Task.FromResult(false), LiveRuns.None, CancellationToken.None);
        Assert.False(Directory.Exists(ws.Path));
        Assert.True(File.Exists(Path.Combine(item.Path, "README.md")));
    }

    [Fact]
    public async Task A_seat_holds_a_read_only_checkout_of_each_repository_fetched_with_its_own_token_and_nothing_is_pushed()
    {
        // sc-25874: a second repository with its own remote; the planner's seat holds a worktree of each under one directory.
        var gears = new RepoRef("acme", "gears");
        var gearsRemote = Path.Combine(_root, "gears.git");
        var seed = Path.Combine(_root, "gears-seed");
        Git(_root, "init", "--bare", "-b", "trunk", gearsRemote);
        Git(_root, "init", "-b", "trunk", seed);
        File.WriteAllText(Path.Combine(seed, "Cargo.toml"), "[package]\nname = \"gears\"\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", gearsRemote, "trunk");
        var tokens = new List<string>();
        var sandbox = new FakeSandbox();
        GitWorkspace Planner() => new(Path.Combine(_root, "work"), r => r == gears ? gearsRemote : _remote,
            (r, _) =>
            {
                tokens.Add(r.FullName);
                return Task.FromResult<string?>(Token);
            },
            (cwd, env, args, ct) =>
            {
                _gitCalls.Add((args, env));
                return GitWorkspace.RunGitAsync(cwd, env, args, ct);
            }, sandbox: sandbox, worktreesDirectory: SandboxPlanRunner.PlanWorktrees, shareWithWorker: false);

        var seat = await Planner().PrepareSeatAsync([Repo, gears], "factory/plan-sc-9", CancellationToken.None);

        // One directory, under the planner's own root where the first repository's worktree would be, a checkout of each inside it.
        Assert.Equal(Path.Combine(_root, "work", SandboxPlanRunner.PlanWorktrees, Repo.Owner, Repo.Name, "factory-plan-sc-9"), seat.Path);
        Assert.Equal([(Repo, Path.Combine(seat.Path, "acme", "widgets")), (gears, Path.Combine(seat.Path, "acme", "gears"))],
            seat.Checkouts.Select(c => (c.Repo, c.Workspace.Path)));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(seat.Path, "acme", "widgets", "README.md")));
        Assert.StartsWith("[package]", File.ReadAllText(Path.Combine(seat.Path, "acme", "gears", "Cargo.toml")));
        Assert.Equal(["main", "trunk"], seat.Checkouts.Select(c => c.Workspace.BaseBranch)); // each repository's own default branch
        // Each clone was fetched with a token minted for that repository; nothing was pushed, and nothing shared for writing.
        Assert.Equal(["acme/widgets", "acme/gears"], tokens);
        Assert.Equal(["clone", "clone"], _gitCalls.Where(c => IsNetworkCall(c.Args)).Select(c => Subcommand(c.Args)));
        Assert.DoesNotContain(sandbox.Calls, c => c.StartsWith("share", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Planner().PrepareSeatAsync([Repo, gears], "plan-sc-9", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Planner().PrepareSeatAsync([Repo, new RepoRef("ACME", "Widgets")], "factory/plan-sc-9", CancellationToken.None));

        // A leftover seat (a crashed run's) is replaced by the next one, and the sweep removes one nothing resumes.
        File.WriteAllText(Path.Combine(seat.Path, "acme", "widgets", "stale.txt"), "left behind");
        var again = await Planner().PrepareSeatAsync([Repo, gears], "factory/plan-sc-9", CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(again.Path, "acme", "widgets", "stale.txt")));
        Assert.True(File.Exists(Path.Combine(again.Path, "acme", "gears", "Cargo.toml")));
        await Planner().SweepOrphansAsync((_, _) => Task.FromResult(false), LiveRuns.None, CancellationToken.None);
        Assert.False(Directory.Exists(again.Path));

        // Removing a seat deletes every checkout and the directory, and leaves neither clone a registered worktree.
        var last = await Planner().PrepareSeatAsync([Repo, gears], "factory/plan-sc-9", CancellationToken.None);
        await Planner().RemoveSeatAsync(last, CancellationToken.None);
        Assert.False(Directory.Exists(last.Path));
        Assert.Equal(1, WorktreeCount());
        Assert.Single(Git(Path.Combine(_root, "work", "repos", "acme", "gears"), "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "push");
    }

    [Fact]
    public async Task Sweep_without_any_worktrees_does_nothing()
    {
        await Workspace().SweepOrphansAsync((_, _) => throw new InvalidOperationException("no worktree to ask about"), LiveRuns.None, CancellationToken.None);
    }

    [Fact]
    public async Task Sharing_a_checkout_never_grants_acl_entries_on_targets_of_committed_symlinks()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // A target repo commits symlinks pointing out of the worktree (e.g. at the clone's hooks).
        var outsideDir = Directory.CreateDirectory(Path.Combine(_root, "outside-dir")).FullName;
        var outsideFile = Path.Combine(_root, "outside-file");
        File.WriteAllText(outsideFile, "secret\n");
        var seed = Path.Combine(_root, "seed2");
        Git(_root, "clone", _remote, seed);
        File.CreateSymbolicLink(Path.Combine(seed, "dir-link"), outsideDir);
        File.CreateSymbolicLink(Path.Combine(seed, "file-link"), outsideFile);
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "symlinks");
        Git(seed, "push", "origin", "main");
        // A real sandbox sharing with the current user (chmod needs an existing user); deletes run the helper unsudoed.
        var sandbox = new DarkFactory.Orchestrator.Worker.WorkerSandbox(Environment.UserName, SafeHelper.Create(_root).Path, SandboxSupport.FakeSudo(_root));

        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-14", CancellationToken.None);

        Assert.True(new FileInfo(Path.Combine(ws.Path, "dir-link")).LinkTarget is not null);
        Assert.DoesNotContain("allow", AclListing(outsideDir));
        Assert.DoesNotContain("allow", AclListing(outsideFile));
        Assert.DoesNotContain("allow", AclListing(Path.Combine(ClonePath, ".git", "hooks")));
        // The checkout itself got the entries, by inheritance.
        Assert.Contains($"user:{Environment.UserName} inherited allow", AclListing(Path.Combine(ws.Path, "README.md")));

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);
        Assert.False(Directory.Exists(ws.Path));
        Assert.True(File.Exists(outsideFile) && Directory.Exists(outsideDir));
    }

    private static string AclListing(string path)
    {
        var psi = new ProcessStartInfo("/bin/ls", ["-led", path]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    [Fact]
    public async Task Reopen_returns_the_interrupted_worktree_untouched()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-5", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "half-done.txt"), "wip");
        _gitCalls.Clear();

        var reopened = await workspace.ReopenAsync(Repo, "factory/sc-5", CancellationToken.None);

        Assert.Equal(first, reopened);
        Assert.True(File.Exists(Path.Combine(reopened!.Path, "half-done.txt")));
        Assert.DoesNotContain(_gitCalls, c => IsNetworkCall(c.Args) || Subcommand(c.Args) is "worktree" or "checkout" or "reset");
        Assert.True(await workspace.CommitAndPushAsync(Repo, reopened, "sc-5: Fix", TestGrants.Untainted, CancellationToken.None));
    }

    [Fact]
    public async Task Reopen_returns_null_when_the_worktree_is_gone()
    {
        var workspace = Workspace();
        Assert.Null(await workspace.ReopenAsync(Repo, "factory/sc-6", CancellationToken.None)); // no clone yet
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-6", CancellationToken.None);
        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.Null(await workspace.ReopenAsync(Repo, "factory/sc-6", CancellationToken.None));
    }

    [Fact]
    public async Task Restore_recreates_a_lost_worktree_from_the_pushed_branch()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-7", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "pushed.txt"), "done");
        Assert.True(await workspace.CommitAndPushAsync(Repo, first, "sc-7: Fix", TestGrants.Untainted, CancellationToken.None));
        var pushed = Git(_remote, "rev-parse", "factory/sc-7").Trim();
        await workspace.RemoveAsync(Repo, first, CancellationToken.None);

        var restored = await workspace.RestoreAsync(Repo, "factory/sc-7", CancellationToken.None);

        Assert.Equal(first, restored);
        Assert.Equal(pushed, Git(restored.Path, "rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(restored.Path, "pushed.txt")));
        // Pushing again changes nothing on the remote.
        await workspace.CommitAndPushAsync(Repo, restored, "sc-7: Fix", TestGrants.Untainted, CancellationToken.None);
        Assert.Equal(pushed, Git(_remote, "rev-parse", "factory/sc-7").Trim());
    }

    // ---- sc-25384: the merge queue's update of a PR branch with its base ----

    /// <summary>Pushes a commit writing <paramref name="file"/> to the remote's main (another PR merging meanwhile).</summary>
    private string AdvanceMain(string file, string content)
    {
        var other = Path.Combine(_root, $"main-{Guid.NewGuid():N}");
        Git(_root, "clone", "-q", _remote, other);
        File.WriteAllText(Path.Combine(other, file), content);
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", $"main: {file}");
        Git(other, "push", "-q", "origin", "main");
        return Git(other, "rev-parse", "HEAD").Trim();
    }

    /// <summary>A pushed PR branch writing <paramref name="file"/>, its worktree removed (as when it reaches the merge queue).</summary>
    private async Task<string> PushBranch(GitWorkspace workspace, string branch, string file, string content)
    {
        var ws = await workspace.PrepareAsync(Repo, branch, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, file), content);
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, $"{branch}: change", TestGrants.Untainted, CancellationToken.None));
        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);
        return Git(_remote, "rev-parse", branch).Trim();
    }

    [Fact]
    public async Task Merge_base_merges_the_moved_base_into_the_branch_and_pushes_it_as_a_fast_forward()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-20", "fix.txt", "fixed\n");
        var main = AdvanceMain("other.txt", "merged first\n");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-20", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);

        Assert.False(merge.Conflicted);
        Assert.False(merge.UpToDate);
        Assert.Equal(main, merge.BaseSha);
        Assert.Equal([head, main], Git(ws.Path, "rev-list", "--parents", "-n", "1", merge.Head).Trim().Split(' ')[1..]);
        _gitCalls.Clear();
        await workspace.PushAsync(Repo, ws, CancellationToken.None);
        Assert.Equal(merge.Head, Git(_remote, "rev-parse", "factory/sc-20").Trim());
        // A plain push (never forced), carrying the App token like every network call.
        var push = Assert.Single(_gitCalls, c => Subcommand(c.Args) == "push");
        Assert.DoesNotContain("--force", push.Args);
        Assert.NotNull(push.Env);

        // Already up to date: nothing to merge.
        var again = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.True(again.UpToDate);
        Assert.Equal(merge.Head, again.Head);
    }

    [Fact]
    public async Task Merge_base_reports_a_conflict_and_leaves_the_merge_for_the_fixer_to_resolve()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-21", "README.md", "hello from the PR\n");
        AdvanceMain("README.md", "hello from main\n");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-21", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);

        Assert.Equal(["README.md"], merge.Conflicts);
        Assert.Equal(head, merge.Head);
        Assert.True(GitWorkspace.HasConflictMarker(File.ReadAllText(Path.Combine(ws.Path, "README.md"))));

        // Pushed with the markers left in: they are found in the pushed commit.
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-21: merge", TestGrants.Untainted, CancellationToken.None));
        var marked = Git(_remote, "rev-parse", "factory/sc-21").Trim();
        Assert.Equal(["README.md"], await workspace.ConflictMarkersAsync(Repo, marked, ["README.md"], CancellationToken.None));

        // The fixer resolves it: the commit completes the merge (two parents) and has no marker left.
        File.WriteAllText(Path.Combine(ws.Path, "README.md"), "hello from both\n");
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-21: resolve", TestGrants.Untainted, CancellationToken.None));
        var resolved = Git(_remote, "rev-parse", "factory/sc-21").Trim();
        Assert.Empty(await workspace.ConflictMarkersAsync(Repo, resolved, ["README.md"], CancellationToken.None));
    }

    [Fact]
    public async Task A_conflicted_merge_committed_as_is_keeps_both_parents()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-23", "README.md", "pr\n");
        var main = AdvanceMain("README.md", "main\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-23", CancellationToken.None);
        await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, "README.md"), "both\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-23: resolve", TestGrants.Untainted, CancellationToken.None));

        var pushed = Git(_remote, "rev-parse", "factory/sc-23").Trim();
        Assert.Equal([head, main], Git(ws.Path, "rev-list", "--parents", "-n", "1", pushed).Trim().Split(' ')[1..]);
    }

    [Fact]
    public async Task The_fast_forward_push_refuses_a_branch_that_moved_on_the_remote()
    {
        var workspace = Workspace();
        await PushBranch(workspace, "factory/sc-22", "fix.txt", "fixed\n");
        AdvanceMain("other.txt", "x\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-22", CancellationToken.None);
        await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        // Someone pushes to the PR branch after the queue fetched it.
        var outside = Path.Combine(_root, "outside");
        Git(_root, "clone", "-q", "-b", "factory/sc-22", _remote, outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "y\n");
        Git(outside, "add", ".");
        Git(outside, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "outside");
        Git(outside, "push", "-q", "origin", "factory/sc-22");
        var moved = Git(_remote, "rev-parse", "factory/sc-22").Trim();

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PushAsync(Repo, ws, CancellationToken.None));
        Assert.Equal(moved, Git(_remote, "rev-parse", "factory/sc-22").Trim());
    }

    // ---- a plan's changeset branch (sc-25872): created once, a base for the plan's stories, only ever fast-forwarded ----

    private const string Changeset = "factory/changeset-sc-5";

    /// <summary>A commit on the remote's <paramref name="branch"/> from outside the factory's clone; returns its sha.</summary>
    private string AdvanceBranch(string branch, string file, string content)
    {
        var other = Path.Combine(_root, $"other-{Guid.NewGuid():N}");
        Git(_root, "clone", "-q", "-b", branch, _remote, other);
        File.WriteAllText(Path.Combine(other, file), content);
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", $"{branch}: {file}");
        Git(other, "push", "-q", "origin", branch);
        return Git(other, "rev-parse", "HEAD").Trim();
    }

    [Fact]
    public async Task A_changeset_branch_is_created_at_the_default_branchs_head_with_the_app_token_and_one_origin_has_is_adopted_as_it_is()
    {
        var workspace = Workspace();
        var main = Git(_remote, "rev-parse", "main").Trim();

        var start = await workspace.BranchStartAsync(Repo, Changeset, CancellationToken.None);

        Assert.Equal(new BranchStart("main", main, null), start);
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "push"); // reading where it would start pushes nothing
        _gitCalls.Clear();
        Assert.Equal(main, await workspace.CreateBranchAsync(Repo, Changeset, start.BaseSha, CancellationToken.None));
        Assert.Equal(main, Git(_remote, "rev-parse", Changeset).Trim());
        // One plain push of that commit to the new ref: never forced, the App token in git's environment, no worktree involved.
        var push = Assert.Single(_gitCalls, c => Subcommand(c.Args) == "push");
        Assert.Equal(["push", "origin", $"{main}:refs/heads/{Changeset}"], push.Args.SkipWhile(a => a != "push"));
        Assert.DoesNotContain("--force", push.Args);
        Assert.Equal($"AUTHORIZATION: basic {BasicAuth}", push.Env!["GIT_CONFIG_VALUE_0"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "work", "worktrees")));

        // Stories merge into it and the default branch moves on: asked again, it is adopted where it is, not reset to either commit.
        var merged = AdvanceBranch(Changeset, "story.txt", "a story\n");
        var moved = AdvanceMain("other.txt", "x\n");
        _gitCalls.Clear();
        Assert.Equal(new BranchStart("main", moved, merged), await workspace.BranchStartAsync(Repo, Changeset, CancellationToken.None));
        Assert.Equal(merged, await workspace.CreateBranchAsync(Repo, Changeset, moved, CancellationToken.None));
        Assert.Equal(merged, await workspace.CreateBranchAsync(Repo, Changeset, main, CancellationToken.None));
        Assert.Equal(merged, Git(_remote, "rev-parse", Changeset).Trim());
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) == "push");

        // Only under factory/: the rulesets let the factory's App write nowhere else.
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.CreateBranchAsync(Repo, "release/1", main, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.BranchStartAsync(Repo, "main", CancellationToken.None));
    }

    [Fact]
    public async Task A_plan_storys_worktree_starts_from_its_changeset_branch_which_is_the_base_it_is_pushed_and_updated_against()
    {
        var workspace = Workspace();
        var main = Git(_remote, "rev-parse", "main").Trim();
        await workspace.CreateBranchAsync(Repo, Changeset, main, CancellationToken.None);
        AdvanceBranch(Changeset, "first.txt", "the story that blocked this one\n");

        // Nothing of its own yet: not ahead of its base, though the changeset branch is ahead of the default branch.
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None, Changeset);
        Assert.Equal(Changeset, ws.BaseBranch);
        Assert.True(File.Exists(Path.Combine(ws.Path, "first.txt"))); // it sees its blocker's merged code
        Assert.False(await workspace.CommitAndPushAsync(Repo, ws, "sc-9: nothing", TestGrants.Untainted, CancellationToken.None));

        File.WriteAllText(Path.Combine(ws.Path, "second.txt"), "this story\n");
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-9: second", TestGrants.Untainted, CancellationToken.None));
        Assert.Equal(Changeset, (await workspace.ReopenAsync(Repo, "factory/sc-9", CancellationToken.None, Changeset))!.BaseBranch);
        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        // Both branches move; the story's branch is updated from its own base, the changeset branch, and gets nothing of main's.
        var changeset = AdvanceBranch(Changeset, "third.txt", "another story\n");
        AdvanceMain("main-only.txt", "not this plan's\n");
        var restored = await workspace.RestoreAsync(Repo, "factory/sc-9", CancellationToken.None, Changeset);
        var merge = await workspace.MergeBaseAsync(Repo, restored, CancellationToken.None);
        Assert.Equal((changeset, false, false), (merge.BaseSha, merge.Conflicted, merge.UpToDate));
        Assert.True(File.Exists(Path.Combine(restored.Path, "third.txt")));
        Assert.False(File.Exists(Path.Combine(restored.Path, "main-only.txt")));

        // A base is the default branch or a changeset branch that exists on origin: nothing else.
        var notChangeset = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.PrepareAsync(Repo, "factory/sc-10", CancellationToken.None, "factory/sc-9"));
        Assert.Contains("Refusing to base 'factory/sc-10' on 'factory/sc-9'", notChangeset.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PrepareAsync(Repo, Changeset, CancellationToken.None, Changeset));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.PrepareAsync(Repo, "factory/sc-10", CancellationToken.None, "factory/changeset-sc-404"));
        Assert.Contains("has no branch 'factory/changeset-sc-404' on origin", missing.Message);
    }

    [Fact]
    public async Task A_fix_rounds_push_to_a_changeset_branch_is_never_forced_so_one_that_would_lose_a_commit_is_refused()
    {
        var workspace = Workspace();
        await workspace.CreateBranchAsync(Repo, Changeset, Git(_remote, "rev-parse", "main").Trim(), CancellationToken.None);
        AdvanceBranch(Changeset, "story.txt", "a merged story\n");

        // The changeset pull request's fixer works on the changeset branch itself, against the default branch.
        var ws = await workspace.RestoreAsync(Repo, Changeset, CancellationToken.None);
        Assert.Equal("main", ws.BaseBranch);
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");
        _gitCalls.Clear();
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-5: fix review findings", TestGrants.Untainted, CancellationToken.None));
        Assert.DoesNotContain("--force", Assert.Single(_gitCalls, c => Subcommand(c.Args) == "push").Args);
        var pushed = Git(_remote, "rev-parse", Changeset).Trim();
        Assert.Equal(pushed, await workspace.HeadAsync(ws, CancellationToken.None));

        // Origin's changeset branch moved after the worktree was made: the push fails and origin keeps what it has.
        var moved = AdvanceBranch(Changeset, "late.txt", "merged meanwhile\n");
        File.WriteAllText(Path.Combine(ws.Path, "fix2.txt"), "fixed again\n");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workspace.CommitAndPushAsync(Repo, ws, "sc-5: fix again", TestGrants.Untainted, CancellationToken.None));
        Assert.Equal(moved, Git(_remote, "rev-parse", Changeset).Trim());

        // An item's own branch is still overwritten (a re-run after an escalation starts over from the base).
        var own = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);
        File.WriteAllText(Path.Combine(own.Path, "own.txt"), "x\n");
        _gitCalls.Clear();
        Assert.True(await workspace.CommitAndPushAsync(Repo, own, "sc-9: own", TestGrants.Untainted, CancellationToken.None));
        Assert.Contains("--force", Assert.Single(_gitCalls, c => Subcommand(c.Args) == "push").Args);
    }

    [Fact]
    public async Task A_changesets_changed_files_are_its_stories_files_even_after_the_default_branch_was_merged_into_it()
    {
        // What the new-tests check reads for the changeset pull request (git diff base...head): everything the stories added,
        // and nothing the default branch brought in when the merge queue updated the changeset branch from it.
        var workspace = Workspace();
        await workspace.CreateBranchAsync(Repo, Changeset, Git(_remote, "rev-parse", "main").Trim(), CancellationToken.None);
        AdvanceBranch(Changeset, "a_tests.rs", "#[test] fn a() {}\n");
        AdvanceBranch(Changeset, "b_tests.cs", "[Fact] void B() {}\n");
        var main = AdvanceMain("main-only.txt", "not this plan's\n");
        var ws = await workspace.RestoreAsync(Repo, Changeset, CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        await workspace.PushAsync(Repo, ws, CancellationToken.None);
        Assert.Equal(merge.Head, Git(_remote, "rev-parse", Changeset).Trim());

        var changes = await workspace.ChangedFilesAsync(Repo, main, merge.Head, CancellationToken.None);

        Assert.Equal([new Gate.ChangedFile('A', null, "a_tests.rs"), new Gate.ChangedFile('A', null, "b_tests.cs")],
            changes.OrderBy(c => c.NewPath, StringComparer.Ordinal));
    }

    // ---- owner-side git isolated from the owner's own git config (OwnerGit) ----

    [Fact]
    public void Owner_git_isolation_adds_its_environment_and_config_ahead_of_every_call()
    {
        Assert.Equal(["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "rerere.enabled=false", "-c", "filter.lfs.smudge=",
            "-c", "filter.lfs.clean=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
            // No git call leaves a maintenance process behind to write the shared clone outside its lock (sc-25987).
            "-c", "gc.autoDetach=false", "-c", "maintenance.autoDetach=false"], OwnerGit.ConfigArgs);
        var (env, args) = OwnerGit.Isolate(new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" }, ["merge", "x"]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_LFS_SKIP_SMUDGE"] = "1",
            ["GIT_CONFIG_COUNT"] = "1",
        }, env);
        Assert.Equal([.. OwnerGit.ConfigArgs, "merge", "x"], args);
        Assert.Equal(OwnerGit.Environment, OwnerGit.Isolate(null, []).Env);
        Assert.Throws<ArgumentException>(() => OwnerGit.Isolate(new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = "/home/x/.gitconfig" }, []));
    }

    [Fact]
    public async Task Every_git_call_of_every_workspace_operation_is_isolated_from_the_owners_git_config()
    {
        var workspace = Workspace();
        await PushBranch(workspace, "factory/sc-31", "README.md", "pr\n");
        var main = AdvanceMain("other.txt", "main\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-31", CancellationToken.None);
        Assert.NotNull(await workspace.ReopenAsync(Repo, "factory/sc-31", CancellationToken.None));
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        await workspace.PushAsync(Repo, ws, CancellationToken.None);
        await workspace.ConflictMarkersAsync(Repo, merge.Head, ["README.md"], CancellationToken.None);
        await workspace.ChangedFilesAsync(Repo, main, merge.Head, CancellationToken.None);
        await workspace.FilesAsync(Repo, merge.Head, CancellationToken.None);
        var run = await workspace.PrepareCommitAsync(Repo, "gate-sc-31-base", main, CancellationToken.None);
        await workspace.OverlayAsync(run, merge.Head, ["README.md"], ["other.txt"], CancellationToken.None,
            new Dictionary<string, string> { ["README.md"] = "replaced\n" });
        await workspace.RemoveAsync(Repo, run, CancellationToken.None);
        await workspace.SweepOrphansAsync((_, _) => Task.FromResult(false), LiveRuns.None, CancellationToken.None);

        Assert.Superset(new HashSet<string> { "clone", "fetch", "remote", "worktree", "rev-parse", "symbolic-ref", "add", "status", "commit", "rev-list",
            "push", "merge", "diff", "ls-tree", "cat-file", "checkout", "rm", "hash-object", "update-index", "checkout-index" },
            _gitCalls.Select(c => Subcommand(c.Args)).ToHashSet());
        Assert.All(_gitCalls, c =>
        {
            Assert.Equal(OwnerGit.ConfigArgs, c.Args.Take(OwnerGit.ConfigArgs.Count));
            Assert.NotNull(c.Env);
            Assert.All(OwnerGit.Environment, e => Assert.Equal(e.Value, c.Env[e.Key]));
        });
        // The token-carrying push keeps its auth alongside the isolation.
        Assert.All(_gitCalls.Where(c => Subcommand(c.Args) == "push"), c => Assert.Equal("credential.helper", c.Env!["GIT_CONFIG_KEY_1"]));
    }

    /// <summary>A script under the test root that records it ran (a file named <paramref name="marker"/> in <c>markers/</c>), then runs <paramref name="then"/>.</summary>
    private string Script(string marker, string then)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "scripts")).FullName;
        var markers = Directory.CreateDirectory(Path.Combine(_root, "markers")).FullName;
        var path = Path.Combine(dir, marker);
        SandboxSupport.ExecutableAt(path, $"#!/bin/sh\ntouch '{Path.Combine(markers, marker)}'\n{then}\n");
        return path;
    }

    private void Hook(string hooksDir, string prefix, params string[] names)
    {
        Directory.CreateDirectory(hooksDir);
        foreach (var name in names)
        {
            SandboxSupport.ExecutableAt(Path.Combine(hooksDir, name), File.ReadAllText(Script($"{prefix}-{name}", "exit 0")));
        }
    }

    private static readonly string[] HookNames = ["post-checkout", "post-merge", "pre-commit", "commit-msg", "post-commit", "pre-push", "pre-merge-commit"];

    [Fact]
    public async Task Owner_side_git_runs_no_hook_filter_merge_driver_or_fsmonitor_that_owner_config_or_a_pr_could_select()
    {
        // The owner's system and global git config (as git would find them through HOME and GIT_CONFIG_SYSTEM), each defining
        // a hooks path, filter and merge drivers that a PR's .gitattributes can select.
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        Hook(Path.Combine(_root, "global-hooks"), "global-hook", HookNames);
        File.WriteAllText(Path.Combine(home, ".gitconfig"), $"""
            [core]
                hooksPath = {Path.Combine(_root, "global-hooks")}
            [filter "evil"]
                smudge = {Script("global-smudge", "cat")}
                clean = {Script("global-clean", "cat")}
            [merge "evil"]
                driver = {Script("global-merge", "exit 1")}
            """);
        var system = Path.Combine(_root, "system.gitconfig");
        File.WriteAllText(system, $"""
            [filter "sysevil"]
                smudge = {Script("system-smudge", "cat")}
                clean = {Script("system-clean", "cat")}
            [merge "sysevil"]
                driver = {Script("system-merge", "exit 1")}
            """);
        IReadOnlyDictionary<string, string> OwnerEnvironment(IReadOnlyDictionary<string, string>? env) =>
            new Dictionary<string, string>(env ?? new Dictionary<string, string>())
            {
                ["HOME"] = home,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
                ["GIT_CONFIG_SYSTEM"] = system,
            };
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token),
            (cwd, env, args, ct) => GitWorkspace.RunGitAsync(cwd, OwnerEnvironment(env), args, ct));

        var first = await workspace.PrepareAsync(Repo, "factory/sc-30", CancellationToken.None);
        // The clone's own config and hooks dir: an LFS filter and an fsmonitor configured there, and hooks in .git/hooks.
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        Git(clone, "config", "filter.lfs.smudge", Script("clone-lfs-smudge", "cat"));
        Git(clone, "config", "filter.lfs.clean", Script("clone-lfs-clean", "cat"));
        Git(clone, "config", "core.fsmonitor", Script("clone-fsmonitor", "exit 1"));
        Hook(Path.Combine(clone, ".git", "hooks"), "clone-hook", HookNames);
        // The PR selects every driver for its files.
        File.WriteAllText(Path.Combine(first.Path, ".gitattributes"), "*.evil filter=evil merge=evil\n*.sys filter=sysevil merge=sysevil\n*.lfs filter=lfs\n");
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(first.Path, file), "pr\n");
        }
        Assert.True(await workspace.CommitAndPushAsync(Repo, first, "sc-30: change", TestGrants.Untainted, CancellationToken.None));
        await workspace.RemoveAsync(Repo, first, CancellationToken.None);
        // Main changes the same files (the base update conflicts in them), and moves on.
        var other = Path.Combine(_root, "main-30");
        Git(_root, "clone", "-q", _remote, other);
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(other, file), "main\n");
        }
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "main: same files");
        Git(other, "push", "-q", "origin", "main");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-30", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.Equal(["a.evil", "b.sys", "c.lfs"], merge.Conflicts.Order());
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(ws.Path, file), "both\n");
        }
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-30: resolve", TestGrants.Untainted, CancellationToken.None));
        var update = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.True(update.UpToDate);
        await workspace.PushAsync(Repo, ws, CancellationToken.None);

        var markers = Path.Combine(_root, "markers");
        Assert.Empty(Directory.GetFiles(markers).Select(Path.GetFileName));
        // The setup is live: the same git without the isolation runs the owner's filter on the PR's file.
        File.Delete(Path.Combine(ws.Path, "a.evil"));
        await GitWorkspace.RunGitAsync(ws.Path, OwnerEnvironment(null), ["checkout", "--", "a.evil"], CancellationToken.None);
        Assert.Contains("global-smudge", Directory.GetFiles(markers).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> main\n", true)]
    [InlineData("<<<<<<<\n", true)]
    [InlineData("x <<<<<<< not at the start\n===\n", false)]
    [InlineData("plain\n=======\n", false)]
    public void Conflict_markers_are_lines_git_writes(string text, bool marked) => Assert.Equal(marked, GitWorkspace.HasConflictMarker(text));

    [Fact]
    public async Task Refuses_branches_outside_factory_prefix()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace().PrepareAsync(Repo, "main", CancellationToken.None));
        Assert.Contains("factory/", ex.Message);
    }

    // ---- a reviewer's read_file, list_files and grep (sc-25706): the clone's objects, never a checkout ----

    /// <summary>A commit on the remote's main that <paramref name="write"/> makes in a fresh clone; returns its sha.</summary>
    private string CommitToMain(string message, Action<string> write)
    {
        var other = Path.Combine(_root, $"main-{Guid.NewGuid():N}");
        Git(_root, "clone", "-q", _remote, other);
        write(other);
        Git(other, "add", "-A");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        Git(other, "push", "-q", "origin", "main");
        return Git(other, "rev-parse", "HEAD").Trim();
    }

    [Fact]
    public async Task A_reviewers_reads_come_from_the_commits_objects_in_the_clone_and_a_missing_commit_is_fetched()
    {
        var ct = CancellationToken.None;
        Gate.IReviewFiles files = Workspace();
        var baseSha = CommitToMain("base", dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "src", "X.cs"), "class X {}\n");
        });
        var head = CommitToMain("head", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "src", "X.cs"), "class X { int Count; }\n");
            Directory.CreateDirectory(Path.Combine(dir, "a:b"));
            File.WriteAllText(Path.Combine(dir, "a:b", "c d.txt"), "Count: 1\nno\nCount: 2\n");
            File.WriteAllText(Path.Combine(dir, "-dash.txt"), "Count\n");
            File.WriteAllBytes(Path.Combine(dir, "nul.dat"), [0x43, 0x6F, 0x75, 0x6E, 0x74, 0x00, 0x62]); // "Count\0b"
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            File.WriteAllText(Path.Combine(dir, "big.txt"), new string('x', Gate.ReviewTools.MaxFileBytes + 1));
            File.CreateSymbolicLink(Path.Combine(dir, "link.cs"), "src/X.cs");
            Directory.CreateDirectory(Path.Combine(dir, "text"));
            File.WriteAllBytes(Path.Combine(dir, "text", "replacement.txt"), [0x61, 0xEF, 0xBF, 0xBD, 0x62, 0x0A]); // "a\uFFFDb\n", valid UTF-8
            File.WriteAllBytes(Path.Combine(dir, "text", "invalid.txt"), [0x61, 0xC3, 0x28, 0x0A]); // not UTF-8
            File.WriteAllText(Path.Combine(dir, "text", "bom16.txt"), "Count\n", new System.Text.UnicodeEncoding(false, true)); // UTF-16LE + BOM
            File.WriteAllText(Path.Combine(dir, "text", "dash.txt"), "x -dash y\nrun --open-files-in-pager=sh here\n");
        });

        // The first read clones; the base's version is read too.
        Assert.Equal("class X { int Count; }\n", await files.ReadAsync(Repo, head, "src/X.cs", ct));
        Assert.Equal("class X {}\n", await files.ReadAsync(Repo, baseSha, "src/X.cs", ct));
        Assert.Equal("Count: 1\nno\nCount: 2\n", await files.ReadAsync(Repo, head, "a:b/c d.txt", ct));
        // A directory, a symlink (never followed) and a missing path are no file.
        Assert.Null(await files.ReadAsync(Repo, head, "src", ct));
        Assert.Null(await files.ReadAsync(Repo, head, "link.cs", ct));
        Assert.Null(await files.ReadAsync(Repo, head, "nope.cs", ct));
        await Assert.ThrowsAsync<Gate.BinaryFileException>(() => files.ReadAsync(Repo, head, "nul.dat", ct));
        await Assert.ThrowsAsync<Gate.BinaryFileException>(() => files.ReadAsync(Repo, head, "logo.png", ct));
        // Read as bytes: a literal U+FFFD is text, invalid UTF-8 and UTF-16 (BOM and all: it has NUL bytes) are binary.
        Assert.Equal("a\uFFFDb\n", await files.ReadAsync(Repo, head, "text/replacement.txt", ct));
        await Assert.ThrowsAsync<Gate.BinaryFileException>(() => files.ReadAsync(Repo, head, "text/invalid.txt", ct));
        await Assert.ThrowsAsync<Gate.BinaryFileException>(() => files.ReadAsync(Repo, head, "text/bom16.txt", ct));
        Assert.Contains($"more than the {Gate.ReviewTools.MaxFileBytes} a reviewer reads",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => files.ReadAsync(Repo, head, "big.txt", ct))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(Repo, "main", "src/X.cs", ct)); // a full commit id only

        // A commit made after the clone is fetched before it is read.
        var later = CommitToMain("later", dir => File.WriteAllText(Path.Combine(dir, "later.txt"), "later\n"));
        Assert.Equal("later\n", await files.ReadAsync(Repo, later, "later.txt", ct));

        Assert.Equal(["src/X.cs"], (await files.ListAsync(Repo, head, "src", ct)).Items);
        var all = await files.ListAsync(Repo, head, null, ct);
        Assert.Equal(["-dash.txt", "README.md", "a:b/c d.txt", "big.txt", "link.cs", "logo.png", "nul.dat", "src/X.cs", "text/bom16.txt",
            "text/dash.txt", "text/invalid.txt", "text/replacement.txt"], all.Items);
        Assert.False(all.More);
        Assert.Empty((await files.ListAsync(Repo, head, "*", ct)).Items); // literal pathspecs: no glob or magic

        // grep: text files only (nul.dat is skipped), at most perFile lines a file, paths with colons and dashes intact.
        Assert.Equal([new Gate.GrepMatch("-dash.txt", 1, "Count"), new Gate.GrepMatch("a:b/c d.txt", 1, "Count: 1")],
            (await files.GrepAsync(Repo, head, "^Count", null, 1, ct)).Items);
        Assert.Equal([new Gate.GrepMatch("a:b/c d.txt", 1, "Count: 1"), new Gate.GrepMatch("a:b/c d.txt", 3, "Count: 2")],
            (await files.GrepAsync(Repo, head, "^Count: [0-9]$", "a:b", 5, ct)).Items);
        Assert.Empty((await files.GrepAsync(Repo, baseSha, "Count", null, 5, ct)).Items); // git's "no match" (exit 1) is an empty answer
        // A pattern starting with a dash is a pattern, never an option of git grep's.
        Assert.Equal([new Gate.GrepMatch("text/dash.txt", 2, "run --open-files-in-pager=sh here")], (await files.GrepAsync(Repo, head, "--open-files-in-pager=sh", null, 5, ct)).Items);
        Assert.Equal([new Gate.GrepMatch("text/dash.txt", 1, "x -dash y")], (await files.GrepAsync(Repo, head, "-dash", null, 5, ct)).Items);
        Assert.Equal(128, GitWorkspace.ExitCode(await Assert.ThrowsAsync<InvalidOperationException>(() => files.GrepAsync(Repo, head, "(", null, 5, ct)))); // a pattern git refuses

        // Nothing was checked out: no worktree, and every call was isolated from the owner's git config.
        Assert.False(Directory.Exists(Path.Combine(_root, "work", GitWorkspace.ItemWorktrees)));
        Assert.All(_gitCalls, c => Assert.Equal(OwnerGit.ConfigArgs, c.Args.Take(OwnerGit.ConfigArgs.Count)));
        Assert.DoesNotContain(_gitCalls, c => Subcommand(c.Args) is "checkout" or "worktree");
    }

    [Fact]
    public async Task A_reviewers_grep_and_listing_stop_reading_git_at_their_caps()
    {
        var ct = CancellationToken.None;
        Gate.IReviewFiles files = Workspace();
        const int many = Gate.ReviewTools.MaxGrepMatches + 100;
        var longLine = "hit " + new string('y', GitWorkspace.MaxGrepRecordBytes * 2);
        var head = CommitToMain("many", dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "many"));
            for (var i = 0; i < many; i++)
            {
                File.WriteAllText(Path.Combine(dir, "many", $"{i:D4}.txt"), "hit\nhit\n");
            }
            Directory.CreateDirectory(Path.Combine(dir, "long"));
            for (var i = 0; i < 100; i++)
            {
                File.WriteAllText(Path.Combine(dir, "long", $"{i:D3}.txt"), longLine + "\n");
            }
        });

        // More matches than are shown: exactly the cap is kept, and the answer says there were more.
        var grep = await files.GrepAsync(Repo, head, "^hit$", "many", Gate.ReviewTools.MaxGrepPerFile, ct);
        Assert.Equal(Gate.ReviewTools.MaxGrepMatches, grep.Items.Count);
        Assert.True(grep.More);
        // Long lines: each keeps at most MaxGrepRecordBytes, and the kept bytes stop the read before the match cap.
        var lines = await files.GrepAsync(Repo, head, "^hit", "long", Gate.ReviewTools.MaxGrepPerFile, ct);
        Assert.All(lines.Items, m => Assert.True(System.Text.Encoding.UTF8.GetByteCount(m.Text) < GitWorkspace.MaxGrepRecordBytes));
        Assert.InRange(lines.Items.Sum(m => System.Text.Encoding.UTF8.GetByteCount(m.Text)), 1, GitWorkspace.MaxReviewReadBytes);
        Assert.InRange(lines.Items.Count, 1, 99);
        Assert.True(lines.More);
        // A complete search says so.
        Assert.False((await files.GrepAsync(Repo, head, "^hit$", "many/0001.txt", Gate.ReviewTools.MaxGrepPerFile, ct)).More);

        var listing = await files.ListAsync(Repo, head, "many", ct);
        Assert.Equal(many, listing.Items.Count); // under the listing cap
        Assert.False(listing.More);
        var big = CommitToMain("more files", dir =>
        {
            for (var i = many; i < Gate.ReviewTools.MaxListedFiles + 10; i++)
            {
                File.WriteAllText(Path.Combine(dir, "many", $"{i:D4}.txt"), "x\n");
            }
        });
        var capped = await files.ListAsync(Repo, big, "many", ct);
        Assert.Equal(Gate.ReviewTools.MaxListedFiles, capped.Items.Count);
        Assert.True(capped.More);
    }

    [Fact]
    public async Task A_reviewers_git_read_that_outlasts_its_own_timeout_answers_an_error()
    {
        Gate.IReviewFiles files = Workspace();
        var head = CommitToMain("x", dir => File.WriteAllText(Path.Combine(dir, "x.txt"), "x\n"));
        Assert.Equal("x\n", await files.ReadAsync(Repo, head, "x.txt", CancellationToken.None)); // cloned

        Gate.IReviewFiles slow = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token))
        {
            ReviewReadLimit = TimeSpan.FromTicks(1),
        };
        Assert.Contains("did not finish within",
            (await Assert.ThrowsAsync<TimeoutException>(() => slow.GrepAsync(Repo, head, "x", null, 5, CancellationToken.None))).Message);
    }

    /// <summary>
    /// A git whose alias records its shell's pid in <paramref name="pidFile"/> and then sleeps (the shell becomes the sleep): a
    /// child of the git this test starts.
    /// </summary>
    private static string[] SleepingGit(string pidFile) => ["-c", $"alias.nap=!echo $$ > '{pidFile}'; exec sleep 30", "nap"];

    /// <summary>True once no process with <paramref name="pid"/> started at <paramref name="started"/> is running (a reused pid is another).</summary>
    private static async Task<bool> GoneAsync(int pid, DateTime started)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (p.HasExited || p.StartTime != started)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // not running
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true; // exited (or a zombie) while being read, or another user's process now has the pid
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<(int Pid, DateTime Started)> ChildAsync(string pidFile)
    {
        for (var i = 0; i < 200 && !(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0); i++)
        {
            await Task.Delay(50);
        }
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        using var p = System.Diagnostics.Process.GetProcessById(pid);
        return (pid, p.StartTime);
    }

    [Fact]
    public async Task A_cancelled_or_stopped_git_read_leaves_no_git_running()
    {
        // Cancelled: RunGitAsync stops the git it started, and what that git started, before it throws.
        var pidFile = Path.Combine(_root, "cancelled.pid");
        (int Pid, DateTime Started) git = default;
        using var cts = new CancellationTokenSource();
        var run = GitWorkspace.RunGitAsync(_root, null, SleepingGit(pidFile), p => git = (p.Id, p.StartTime), cts.Token);
        var child = await ChildAsync(pidFile);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(await GoneAsync(git.Pid, git.Started), "the cancelled git is still running");
        Assert.True(await GoneAsync(child.Pid, child.Started), "the cancelled git's child is still running");

        // Stopped early by its reader: the streamed git is stopped too.
        pidFile = Path.Combine(_root, "stopped.pid");
        await GitWorkspace.RunGitStreamAsync(_root, null, SleepingGit(pidFile), async (_, _) =>
        {
            child = await ChildAsync(pidFile);
            return false;
        }, CancellationToken.None);
        Assert.True(await GoneAsync(child.Pid, child.Started), "the stopped git's child is still running");
    }

    [Fact]
    public void Grep_output_is_parsed_by_its_nul_separators()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        Assert.Equal([new Gate.GrepMatch("a:b.txt", 12, "x: y\0z"), new Gate.GrepMatch("c.txt", 1, "")],
            GitWorkspace.ParseGrep($"{sha}:a:b.txt\012\0x: y\0z\r\n{sha}:c.txt\01\0\nnot a match line\n", sha));
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
