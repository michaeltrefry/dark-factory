using System.Diagnostics;
using System.Text;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Prepare;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26232, the worker side (<c>Worker:Prepare</c>): implementers never download — the harness prefetches a worktree's recipe
/// artifacts before each session, and a session's <c>factory-prepare</c> asks for them through a request spool the owner process
/// polls, then runs the recipe's steps. The script runs here over a real spool answered by the real owner-side code (unsandboxed:
/// the worker is the owner; no sandbox user, no sudo), with downloads from a fake server.
/// </summary>
public sealed class WorkerPrepareTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("df-worker-prepare-").FullName;
    private readonly FakeArtifactServer _server = new();
    private readonly FakeResolver _resolver = new() { ["files.example"] = ["93.184.216.34"] };
    private static readonly string Script = Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-prepare");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WorkerPrepare Prepare() =>
        new(new ArtifactFetcher(new HttpClient(_server), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root))), WorkerPrepare.SpoolRootOf(_root), TextWriter.Null)
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
        };

    /// <summary>A session's spool the test polls by hand (<see cref="PrepareSpoolSession.PollOnceAsync"/>): nothing polls it in the background.</summary>
    private WorkerPrepare PolledByHand() =>
        new(new ArtifactFetcher(new HttpClient(_server), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root))), WorkerPrepare.SpoolRootOf(_root), TextWriter.Null)
        {
            PollsInBackground = false,
        };

    /// <summary>Waits, as factory-prepare does, for the owner's answer to <paramref name="id"/>: its text, or null after a minute (a bound, not a sync).</summary>
    private static async Task<string?> AwaitAnswerAsync(string spool, string id)
    {
        var path = Path.Combine(spool, $"response-{id}");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            await Task.Delay(25);
        }
        return null;
    }

    private string Worktree(string? recipe)
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(worktree, "sub"));
        if (recipe is not null)
        {
            Directory.CreateDirectory(Path.Combine(worktree, PrepareRecipe.Directory));
            File.WriteAllText(Path.Combine(worktree, PrepareRecipe.Path), recipe);
        }
        return worktree;
    }

    /// <summary>Runs the repo's factory-prepare in <paramref name="cwd"/>; it ends by itself (every case here is bounded by --wait).</summary>
    private static async Task<(int Exit, string Stderr)> RunScriptAsync(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(Script);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(bounded.Token);
        await stdout;
        return (process.ExitCode, await stderr);
    }

    private (string Recipe, byte[] Body) RecipeWithArtifact(string steps)
    {
        var body = "artifact text\n"u8.ToArray();
        _server.Serve("https://files.example/a.txt", body);
        return ($"version: 1\nartifacts: [{{name: data, url: 'https://files.example/a.txt', sha256: {ArtifactFetchTests.Sha256(body)}, max_bytes: 1000}}]\nsteps:\n{steps}", body);
    }

    [Fact]
    public async Task Factory_prepare_asks_through_the_spool_the_owner_fetches_and_answers_and_the_steps_run_as_the_worker()
    {
        var (recipe, _) = RecipeWithArtifact("""
              - name: copy
                argv: [/bin/sh, -c, 'cp "$SRC" out.txt']
                cwd: sub
                env: {SRC: '${artifact:data}'}
              - name: check
                argv: [/bin/test, -f, '${worktree}/sub/out.txt']
            """);
        var worktree = Worktree(recipe);
        var downloads = new List<ArtifactDownloaded>();
        var prepare = Prepare();
        await using var session = await prepare.OpenAsync(worktree, sandbox: null, (d, _) => { downloads.Add(d); return Task.CompletedTask; }, CancellationToken.None);

        var (exit, stderr) = await RunScriptAsync(worktree, "--spool", session.Directory, "--wait", "30");

        Assert.True(exit == 0, stderr);
        Assert.Equal("artifact text\n", File.ReadAllText(Path.Combine(worktree, "sub", "out.txt")));
        Assert.Contains("factory-prepare: step copy (in sub)", stderr);
        Assert.Contains("2 step(s) done", stderr);
        Assert.Equal(1, session.Answered);
        Assert.Single(downloads);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_recipe_the_owner_refuses_a_failing_step_and_a_missing_spool_or_recipe_each_end_factory_prepare_with_its_status()
    {
        var prepare = Prepare();
        var refused = Worktree("version: 1\nsteps: [{name: a, argv: [make], shell: true}]\n");
        await using (var session = await prepare.OpenAsync(refused, null, null, CancellationToken.None))
        {
            var (exit, stderr) = await RunScriptAsync(refused, "--spool", session.Directory, "--wait", "30");
            Assert.Equal(1, exit);
            Assert.Contains("the factory refused: .factory/prepare.yaml", stderr);
            Assert.Contains("unknown key 'shell'", stderr);
        }

        var failing = Worktree("version: 1\nsteps: [{name: boom, argv: [/bin/sh, -c, 'exit 7']}, {name: never, argv: [/usr/bin/touch, never]}]\n");
        await using (var session = await prepare.OpenAsync(failing, null, null, CancellationToken.None))
        {
            var (exit, stderr) = await RunScriptAsync(failing, "--spool", session.Directory, "--wait", "30");
            Assert.Equal(7, exit);
            Assert.Contains("step boom failed (exit 7)", stderr);
            Assert.False(File.Exists(Path.Combine(failing, "never")));
        }

        var noRecipe = Worktree(null);
        Assert.Equal(66, (await RunScriptAsync(noRecipe, "--spool", _root)).Exit);
        Assert.Equal(69, (await RunScriptAsync(failing, "--spool", Path.Combine(_root, "gone"))).Exit);
        Assert.Equal(64, (await RunScriptAsync(failing)).Exit);
        Assert.Equal(64, (await RunScriptAsync(failing, "--spool", "relative/dir")).Exit);
        // Nobody answers a spool no session polls: it gives up after --wait.
        var silent = Directory.CreateDirectory(Path.Combine(_root, "silent")).FullName;
        var (late, why) = await RunScriptAsync(failing, "--spool", silent, "--wait", "1");
        Assert.Equal(75, late);
        Assert.Contains("no answer from the factory within 1s", why);
    }

    [Fact]
    public async Task The_owner_answers_only_complete_requests_never_writes_through_a_planted_link_and_removes_the_spool()
    {
        var worktree = Worktree("version: 1\nsteps: [{name: a, argv: [/usr/bin/true]}]\n");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "untouched");
        var prepare = PolledByHand();
        var session = await prepare.OpenAsync(worktree, null, null, CancellationToken.None);

        File.WriteAllText(Path.Combine(session.Directory, "request-half"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n"); // not ended yet
        File.WriteAllText(Path.Combine(session.Directory, "request-bad!id"), $"{WorkerPrepare.RequestHeader}\nx\n{WorkerPrepare.RequestEnd}\n");
        File.WriteAllText(Path.Combine(session.Directory, "request-ok"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
        File.CreateSymbolicLink(Path.Combine(session.Directory, "response-ok"), outside); // as a worker could, before the answer
        await session.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, session.Answered);
        Assert.Equal("untouched", File.ReadAllText(outside));
        var answer = new FileInfo(Path.Combine(session.Directory, "response-ok"));
        Assert.Null(answer.LinkTarget); // the link was replaced, not followed
        Assert.StartsWith($"ok\0{PrepareSteps.PlanHeader}\0" + "1\0a\0.\00\01\0/usr/bin/true\0", File.ReadAllText(answer.FullName));
        Assert.False(File.Exists(Path.Combine(session.Directory, "response-half")));
        Assert.False(File.Exists(Path.Combine(session.Directory, "response-bad!id")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(answer.FullName));
        }

        await session.DisposeAsync();
        Assert.False(Directory.Exists(session.Directory));
    }

    [Fact]
    public async Task Two_polls_at_once_answer_a_request_exactly_once_and_neither_returns_before_it_is_answered()
    {
        var (recipe, _) = RecipeWithArtifact("  - name: a\n    argv: [/usr/bin/true]\n");
        var worktree = Worktree(recipe);
        var downloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await PolledByHand().OpenAsync(worktree, null, async (_, _) =>
        {
            downloaded.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        File.WriteAllText(Path.Combine(session.Directory, "request-1"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");

        var first = session.PollOnceAsync(CancellationToken.None);
        await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(60)); // the first pass is inside its fetch, request-1 taken
        var second = session.PollOnceAsync(CancellationToken.None);

        // The second pass waits for the first: it neither answers request-1 again nor returns before its answer is written.
        Assert.False(second.IsCompleted);
        Assert.False(File.Exists(Path.Combine(session.Directory, "response-1")));
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal((1, 1), (session.Answered, session.Fetched));
        Assert.StartsWith("ok\0", File.ReadAllText(Path.Combine(session.Directory, "response-1")));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public void A_recipe_behind_a_link_or_refused_by_the_safe_read_is_never_read()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(target, "prepare.yaml"), "version: 1\nsteps: [{name: a, argv: [make]}]\n");
        var linked = Worktree(null);
        Directory.CreateSymbolicLink(Path.Combine(linked, PrepareRecipe.Directory), target);
        var fileLink = Worktree(null);
        Directory.CreateDirectory(Path.Combine(fileLink, PrepareRecipe.Directory));
        File.CreateSymbolicLink(Path.Combine(fileLink, PrepareRecipe.Path), Path.Combine(target, "prepare.yaml"));

        var (viaDirectory, directoryWhy) = WorkerPrepare.ReadRecipe(linked);
        var (viaFile, fileWhy) = WorkerPrepare.ReadRecipe(fileLink);
        var (none, noneWhy) = WorkerPrepare.ReadRecipe(Worktree(null));
        Assert.Equal((false, ".factory is a symbolic link, so its recipe is not read"), (viaDirectory is not null, directoryWhy));
        Assert.Equal((false, ".factory/prepare.yaml is a symlink"), (viaFile is not null, fileWhy));
        Assert.Equal((false, (string?)null), (none is not null, noneWhy));
        Assert.NotNull(WorkerPrepare.ReadRecipe(Worktree("version: 1\nsteps: [{name: a, argv: [make]}]\n")).Recipe);
    }

    [Fact]
    public async Task The_spool_is_this_sessions_user_alone_to_add_to_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // The role account nobody stands in for the slot's user (an ACL entry must name an existing user; nothing runs as it).
        var sandbox = new WorkerSandbox("nobody", TestSlots.NoHelper);
        await using var session = await Prepare().OpenAsync(Worktree("version: 1\nsteps: [{name: a, argv: [make]}]\n"), sandbox, null, CancellationToken.None);

        var (listing, reads, resolved) = await SettledAclAsync(ct => WorkerSandbox.ListAsync(session.Directory, ct));
        Assert.True(resolved, $"the ACL listing never named its principals ({reads} reads): {listing}");
        // Every permission assertion stands: the session's user alone may add a file; nobody else may open it.
        Assert.True(WorkerSandbox.Grants(listing, "nobody", "add_file"), listing);
        Assert.False(WorkerSandbox.Grants(listing, "nobody", "delete_child"), listing);
        Assert.Null(WorkerSandbox.OpenToOthers(listing, "nobody"));
        // The root lets a user reach a session's directory by name and list nothing.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(WorkerPrepare.SpoolRootOf(_root)));
        Assert.InRange(reads, 1, AclObservationReads);
    }

    /// <summary>A listing with the long directory line <c>ls -led</c> prints first, then the given ACL entries.</summary>
    private static string Long(string entries) => "drwx------+ 1 owner staff 64 Jan 1 00:00 dir\n" + entries;

    /// <summary>One ACL entry of <c>ls -led</c>: its principal, and whether it names a user or a group at all.</summary>
    private static readonly System.Text.RegularExpressions.Regex AclEntry =
        new(@"^\s*[0-9]+:\s+(?<who>\S+)\s+(?:inherited\s+)?(?:allow|deny)\s+(?<rights>\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether the listing still names an entry's principal by something other than a user or a group: <c>ls</c> prints a bare
    /// UUID while the directory service does not answer. Such a listing is not a verdict yet, and is read again (bounded).
    /// </summary>
    internal static bool UnresolvedPrincipal(string listing) => listing.Split('\n')
        .Select(line => AclEntry.Match(line))
        .Where(m => m.Success)
        .Any(m => !m.Groups["who"].Value.StartsWith("user:", StringComparison.Ordinal)
            && !m.Groups["who"].Value.StartsWith("group:", StringComparison.Ordinal));

    // Test observation only: macOS may keep printing an unresolved role-account UUID for several seconds under load.
    // Never widen the production ACL decision or keep reading a resolved security failure until it passes.
    internal const int AclObservationReads = 20;
    private static readonly TimeSpan AclObservationRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The listing, read again only while a principal is unresolved (<see cref="UnresolvedPrincipal"/>): at most
    /// <see cref="AclObservationReads"/> reads, <see cref="AclObservationRetry"/> apart. A listing whose principals are
    /// all named is a verdict at once: a missing right or an unexpected writer fails the check there and is never read again
    /// until it passes.
    /// </summary>
    internal static async Task<(string Listing, int Reads)> ReadSettledAclAsync(Func<CancellationToken, Task<string>> read,
        Func<TimeSpan, Task>? delay = null)
    {
        delay ??= static interval => Task.Delay(interval);
        var listing = "";
        for (var attempt = 1; attempt <= AclObservationReads; attempt++)
        {
            listing = await read(CancellationToken.None);
            if (!UnresolvedPrincipal(listing)) return (listing, attempt);
            if (attempt < AclObservationReads) await delay(AclObservationRetry);
        }
        return (listing, AclObservationReads);
    }

    private static async Task<(string Listing, int Reads, bool Resolved)> SettledAclAsync(Func<CancellationToken, Task<string>> read)
    {
        var (listing, reads) = await ReadSettledAclAsync(read);
        return (listing, reads, !UnresolvedPrincipal(listing));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public async Task An_acl_listing_whose_principal_is_a_uuid_is_read_again_until_it_names_its_user(int resolvesAfter)
    {
        var uuid = Long(" 0: 1E9A2C4B-6D3F-4A5E-9B7C-2D4E6F8A0B1C allow add_file\n");
        var named = Long(" 0: user:nobody allow add_file\n");
        var reads = 0;
        var delays = new List<TimeSpan>();
        var (listing, count) = await ReadSettledAclAsync(_ =>
        {
            reads++;
            return Task.FromResult(reads < resolvesAfter ? uuid : named);
        }, interval => { delays.Add(interval); return Task.CompletedTask; });
        Assert.Equal(resolvesAfter - 1, delays.Count);
        Assert.All(delays, interval => Assert.Equal(TimeSpan.FromMilliseconds(500), interval));
        Assert.Equal(resolvesAfter, count);
        Assert.Equal(resolvesAfter, reads);
        Assert.False(UnresolvedPrincipal(listing));
        Assert.True(WorkerSandbox.Grants(listing, "nobody", "add_file"), listing);
    }

    [Fact]
    public async Task An_acl_listing_whose_principal_never_resolves_fails_after_the_bounded_reads()
    {
        var uuid = Long(" 0: 1E9A2C4B-6D3F-4A5E-9B7C-2D4E6F8A0B1C allow add_file\n");
        var reads = 0;
        var delays = 0;
        var (listing, count) = await ReadSettledAclAsync(_ =>
        {
            reads++;
            return Task.FromResult(uuid);
        }, _ => { delays++; return Task.CompletedTask; });
        Assert.Equal(AclObservationReads - 1, delays);
        Assert.Equal(AclObservationReads, count);
        Assert.Equal(AclObservationReads, reads);
        Assert.True(UnresolvedPrincipal(listing));
        // A UUID is never the sandbox user: it grants nothing and opens the directory to somebody.
        Assert.False(WorkerSandbox.Grants(listing, "nobody", "add_file"), listing);
        Assert.NotNull(WorkerSandbox.OpenToOthers(listing, "nobody"));
    }

    [Theory]
    [InlineData("missing-right")]
    [InlineData("unexpected-writer")]
    public async Task A_resolved_acl_listing_with_a_missing_right_or_an_unexpected_writer_fails_at_once(string problem)
    {
        var listing = problem == "missing-right"
            ? Long(" 0: user:nobody allow read\n")
            : Long(" 0: user:nobody allow add_file\n 0: user:someone allow add_file\n");
        Assert.False(UnresolvedPrincipal(listing));
        var reads = 0;
        var delays = 0;
        var (read, count) = await ReadSettledAclAsync(_ =>
        {
            reads++;
            return Task.FromResult(listing);
        }, _ => { delays++; return Task.CompletedTask; });
        Assert.Equal(0, delays);
        Assert.Equal(1, count);
        Assert.Equal(1, reads);
        // Never read again until it passes: the verdict is the listing's own.
        // A missing right is no grant; an unexpected writer is an extra allow entry (the grant itself may stand).
        Assert.Equal(problem == "unexpected-writer", WorkerSandbox.Grants(read, "nobody", "add_file"));
        Assert.Equal(problem == "unexpected-writer", WorkerSandbox.OpenToOthers(read, "nobody") is not null);
    }

    [Fact]
    public async Task Through_the_pipeline_a_session_of_a_worktree_with_a_recipe_is_prefetched_told_its_spool_and_answered_while_it_runs()
    {
        var (recipe, _) = RecipeWithArtifact("  - name: a\n    argv: [/usr/bin/true]\n");
        var h = new GatePipelineTests.Harness { WorkerPrepare = Prepare() };
        h.Workspaces.Root = Path.Combine(_root, "worktrees");
        var worktree = Path.Combine(h.Workspaces.Root, "factory", "sc-77");
        Directory.CreateDirectory(Path.Combine(worktree, PrepareRecipe.Directory));
        File.WriteAllText(Path.Combine(worktree, PrepareRecipe.Path), recipe);
        string? answer = null;
        h.WorkerOverrides[0] = async call =>
        {
            // As factory-prepare does, from inside the session: a request, then the owner's answer.
            var spool = System.Text.RegularExpressions.Regex.Match(call.Prompt, "--spool (\\S+)`").Groups[1].Value;
            File.WriteAllText(Path.Combine(spool, "request-1"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
            answer = await AwaitAnswerAsync(spool, "1");
            return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var prompt = h.WorkerCalls[0].Prompt;
        Assert.Contains($"{PrepareTool.InstalledPath} --spool {WorkerPrepare.SpoolRootOf(_root)}/", prompt);
        Assert.StartsWith($"ok\0{PrepareSteps.PlanHeader}\0", answer);
        // The prefetch downloaded it before the session; the session's own request came from the cache.
        var fetched = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
        Assert.StartsWith("fetched data https://files.example/a.txt", fetched.Detail);
        Assert.Single(_server.Requests);
        Assert.Empty(Directory.EnumerateDirectories(WorkerPrepare.SpoolRootOf(_root))); // removed with the session
    }

    [Fact]
    public async Task With_worker_prepare_a_session_of_a_worktree_without_a_recipe_still_gets_its_spool_and_can_add_one()
    {
        var (recipe, _) = RecipeWithArtifact("  - name: a\n    argv: [/usr/bin/true]\n");
        var h = new GatePipelineTests.Harness { WorkerPrepare = Prepare() };
        h.Workspaces.Root = Path.Combine(_root, "worktrees");
        var worktree = Path.Combine(h.Workspaces.Root, "factory", "sc-77");
        Directory.CreateDirectory(worktree);
        string? answer = null;
        h.WorkerOverrides[0] = async call =>
        {
            // The session adds the recipe itself, then asks: the spool was there from the start.
            Directory.CreateDirectory(Path.Combine(worktree, PrepareRecipe.Directory));
            File.WriteAllText(Path.Combine(worktree, PrepareRecipe.Path), recipe);
            var spool = System.Text.RegularExpressions.Regex.Match(call.Prompt, "--spool (\\S+)`").Groups[1].Value;
            File.WriteAllText(Path.Combine(spool, "request-1"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
            answer = await AwaitAnswerAsync(spool, "1");
            return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains("or once you add one", h.WorkerCalls[0].Prompt);
        Assert.StartsWith($"ok\0{PrepareSteps.PlanHeader}\0", answer);
        // Nothing to prefetch before the session; the session's own request downloaded it, recorded once the session ended.
        var fetched = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
        Assert.StartsWith("fetched data https://files.example/a.txt", fetched.Detail);
    }

    [Fact]
    public async Task A_workers_request_is_never_told_the_served_hash_but_the_ledger_gets_the_full_reason()
    {
        _server.Serve("https://files.example/a.txt", "tampered"u8.ToArray());
        var pinned = ArtifactFetchTests.Sha256("artifact text\n"u8.ToArray());
        var served = ArtifactFetchTests.Sha256("tampered"u8.ToArray());
        var worktree = Worktree($"version: 1\nartifacts: [{{name: data, url: 'https://files.example/a.txt', sha256: {pinned}, max_bytes: 1000}}]\nsteps: [{{name: a, argv: [/usr/bin/true]}}]\n");
        var failures = new List<string>();
        var session = await PolledByHand().OpenAsync(worktree, null, null, CancellationToken.None, failures.Add);
        await using (session)
        {
            File.WriteAllText(Path.Combine(session.Directory, "request-1"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
            await session.PollOnceAsync(CancellationToken.None);

            var answer = File.ReadAllText(Path.Combine(session.Directory, "response-1"));
            Assert.Equal($"error\0artifact data: https://files.example/a.txt does not match the pinned sha256 {pinned}; nothing was cached\0", answer);
            Assert.DoesNotContain(served, answer);
            Assert.Contains(served, Assert.Single(failures));
        }

        // Through the pipeline the full reason is on the ledger, as a failed artifact-fetch row.
        var h = new GatePipelineTests.Harness { WorkerPrepare = Prepare() };
        h.Workspaces.Root = Path.Combine(_root, "worktrees");
        var tree = Path.Combine(h.Workspaces.Root, "factory", "sc-77");
        Directory.CreateDirectory(tree);
        h.WorkerOverrides[0] = async call =>
        {
            Directory.CreateDirectory(Path.Combine(tree, PrepareRecipe.Directory));
            File.WriteAllText(Path.Combine(tree, PrepareRecipe.Path), File.ReadAllText(Path.Combine(worktree, PrepareRecipe.Path)));
            var spool = System.Text.RegularExpressions.Regex.Match(call.Prompt, "--spool (\\S+)`").Groups[1].Value;
            File.WriteAllText(Path.Combine(spool, "request-1"), $"{WorkerPrepare.RequestHeader}\n{tree}\n{WorkerPrepare.RequestEnd}\n");
            Assert.NotNull(await AwaitAnswerAsync(spool, "1"));
            return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
        };
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded, outcome.Error);
        var row = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
        Assert.StartsWith("failed: artifact data", row.Detail);
        Assert.Contains($"has SHA-256 {served}", row.Detail);
    }

    [Fact]
    public async Task A_session_is_answered_with_a_fetch_at_most_twenty_times_and_with_an_error_after()
    {
        var worktree = Worktree("version: 1\nsteps: [{name: a, argv: [/usr/bin/true]}]\n");
        await using var session = await PolledByHand().OpenAsync(worktree, null, null, CancellationToken.None);
        for (var i = 1; i <= WorkerPrepare.MaxRequestsPerSession + 2; i++)
        {
            File.WriteAllText(Path.Combine(session.Directory, $"request-{i:00}"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
        }

        await session.PollOnceAsync(CancellationToken.None);

        Assert.Equal(20, WorkerPrepare.MaxRequestsPerSession);
        Assert.Equal((22, 20), (session.Answered, session.Fetched));
        Assert.StartsWith("ok\0", File.ReadAllText(Path.Combine(session.Directory, "response-20")));
        foreach (var late in new[] { "response-21", "response-22" })
        {
            Assert.Equal("error\0this session has already asked 20 times, the most a session may: nothing more is fetched for it\0",
                File.ReadAllText(Path.Combine(session.Directory, late)));
        }
    }

    [Fact]
    public async Task Opening_a_spool_removes_what_a_dead_process_left_and_never_a_live_sessions()
    {
        // sc-26134: a crashed process leaves prepare-spool/<id> behind; sessions overlap, so only one whose lock no one holds is stale.
        var worktree = Worktree("version: 1\nsteps: [{name: a, argv: [/usr/bin/true]}]\n");
        var prepare = PolledByHand();
        var root = WorkerPrepare.SpoolRootOf(_root);
        await using var live = await prepare.OpenAsync(worktree, null, null, CancellationToken.None);
        Assert.True(File.Exists(WorkerPrepare.LockOf(root, Path.GetFileName(live.Directory))));
        var dead = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName; // no lock file: died before or after it
        File.WriteAllText(Path.Combine(dead, "request-1"), "left");
        var deadLocked = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(WorkerPrepare.LockOf(root, Path.GetFileName(deadLocked)), ""); // its lock file, held by no one
        var orphanLock = WorkerPrepare.LockOf(root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(orphanLock, "");
        var other = Path.Combine(root, "not-a-spool");
        Directory.CreateDirectory(other);

        var second = await prepare.OpenAsync(worktree, null, null, CancellationToken.None);

        Assert.False(Directory.Exists(dead));
        Assert.False(Directory.Exists(deadLocked));
        Assert.False(File.Exists(WorkerPrepare.LockOf(root, Path.GetFileName(deadLocked))));
        Assert.False(File.Exists(orphanLock));
        Assert.True(Directory.Exists(other)); // not named like a spool: not the sweep's
        // The live session's directory and lock are untouched, and it still answers.
        Assert.True(Directory.Exists(live.Directory));
        Assert.True(File.Exists(WorkerPrepare.LockOf(root, Path.GetFileName(live.Directory))));
        File.WriteAllText(Path.Combine(live.Directory, "request-ok"), $"{WorkerPrepare.RequestHeader}\n{worktree}\n{WorkerPrepare.RequestEnd}\n");
        await live.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, live.Answered);

        // A session's end removes its directory and its lock file.
        await second.DisposeAsync();
        Assert.False(Directory.Exists(second.Directory));
        Assert.False(File.Exists(WorkerPrepare.LockOf(root, Path.GetFileName(second.Directory))));
    }

    [Fact]
    public async Task A_sessions_requests_may_add_at_most_the_session_budget_to_the_cache_counted_at_declared_limits()
    {
        // sc-26134: the cache is never evicted, so what one session's own recipe may make it fetch is bounded, before any download.
        const long Max = PrepareRecipe.MaxArtifactBytes;
        Assert.Equal(40L * 1024 * 1024 * 1024, WorkerPrepare.MaxSessionBytes);
        string Recipe(int from, int count) => "version: 1\nartifacts: [" + string.Join(", ", Enumerable.Range(from, count).Select(i =>
        {
            var body = System.Text.Encoding.UTF8.GetBytes($"artifact {i}\n");
            _server.Serve($"https://files.example/a{i}.txt", body);
            return $"{{name: a{i}, url: 'https://files.example/a{i}.txt', sha256: {ArtifactFetchTests.Sha256(body)}, max_bytes: {Max}}}";
        })) + "]\nsteps: [{name: s, argv: [/usr/bin/true]}]\n";
        var refusedRecipe = Worktree(Recipe(0, 11)); // 11 × 4 GiB declared: more than a session may add
        var failures = new List<string>();
        await using (var session = await PolledByHand().OpenAsync(refusedRecipe, null, null, CancellationToken.None, failures.Add))
        {
            File.WriteAllText(Path.Combine(session.Directory, "request-1"), $"{WorkerPrepare.RequestHeader}\n{refusedRecipe}\n{WorkerPrepare.RequestEnd}\n");
            await session.PollOnceAsync(CancellationToken.None);
            var answer = File.ReadAllText(Path.Combine(session.Directory, "response-1"));
            Assert.StartsWith($"error\0the artifacts this recipe names that are not cached yet may take {11 * Max} bytes", answer);
            Assert.Contains($"more than the {WorkerPrepare.MaxSessionBytes} bytes a session may add to the artifact cache, so nothing was fetched", answer);
            Assert.Empty(_server.Requests);
            Assert.Single(failures);
        }

        // 10 × 4 GiB is exactly the budget: fetched. The next request naming one more uncached artifact is refused; one naming only
        // cached artifacts costs nothing and is answered.
        var full = Worktree(Recipe(0, 10));
        await using (var session = await PolledByHand().OpenAsync(full, null, null, CancellationToken.None))
        {
            File.WriteAllText(Path.Combine(session.Directory, "request-1"), $"{WorkerPrepare.RequestHeader}\n{full}\n{WorkerPrepare.RequestEnd}\n");
            await session.PollOnceAsync(CancellationToken.None);
            Assert.StartsWith("ok\0", File.ReadAllText(Path.Combine(session.Directory, "response-1")));
            File.WriteAllText(Path.Combine(full, PrepareRecipe.Path), Recipe(10, 1));
            File.WriteAllText(Path.Combine(session.Directory, "request-2"), $"{WorkerPrepare.RequestHeader}\n{full}\n{WorkerPrepare.RequestEnd}\n");
            await session.PollOnceAsync(CancellationToken.None);
            Assert.Contains($"may take {Max} bytes", File.ReadAllText(Path.Combine(session.Directory, "response-2")));
            Assert.Contains($"have already counted {10 * Max}", File.ReadAllText(Path.Combine(session.Directory, "response-2")));
            File.WriteAllText(Path.Combine(full, PrepareRecipe.Path), Recipe(0, 10));
            File.WriteAllText(Path.Combine(session.Directory, "request-3"), $"{WorkerPrepare.RequestHeader}\n{full}\n{WorkerPrepare.RequestEnd}\n");
            await session.PollOnceAsync(CancellationToken.None);
            Assert.StartsWith("ok\0", File.ReadAllText(Path.Combine(session.Directory, "response-3")));
        }
        Assert.DoesNotContain(_server.Requests, r => r.AbsolutePath == "/a10.txt");
        Assert.Equal(10, _server.Requests.Count); // the refused requests fetched nothing, the cached ones were not fetched again
    }

    [Fact]
    public void A_dot_factory_swapped_for_a_link_between_the_check_and_the_read_is_never_followed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "prepare.yaml"), "version: 1\nsteps: [{name: planted, argv: [make]}]\n");
        var worktree = Worktree("version: 1\nsteps: [{name: own, argv: [make]}]\n");
        var real = Path.Combine(worktree, PrepareRecipe.Directory);

        // After the check found a real directory: the worker moves it aside and puts a link to the outside in its place.
        var (recipe, why) = WorkerPrepare.ReadRecipe(worktree, () =>
        {
            Directory.Move(real, real + "-moved");
            Directory.CreateSymbolicLink(real, outside);
        });

        Assert.Null(recipe);
        Assert.StartsWith($"{PrepareRecipe.Path} is in a directory that cannot be opened without following a link", why);
        // Unswapped, the same read finds the worktree's own recipe.
        Directory.Delete(real);
        Directory.Move(real + "-moved", real);
        Assert.Equal("own", WorkerPrepare.ReadRecipe(worktree).Recipe!.Steps[0].Name);
    }

    [Fact]
    public async Task A_prefetch_that_fails_is_recorded_and_the_session_still_starts()
    {
        var h = new GatePipelineTests.Harness { WorkerPrepare = Prepare() };
        h.Workspaces.Root = Path.Combine(_root, "worktrees");
        var worktree = Path.Combine(h.Workspaces.Root, "factory", "sc-77");
        Directory.CreateDirectory(Path.Combine(worktree, PrepareRecipe.Directory));
        File.WriteAllText(Path.Combine(worktree, PrepareRecipe.Path),
            $"version: 1\nartifacts: [{{name: gone, url: 'https://files.example/gone', sha256: {PrepareRecipeTests.Sha}, max_bytes: 10}}]\nsteps: [{{name: a, argv: [make]}}]\n");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var failed = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
        Assert.StartsWith("failed: artifact gone", failed.Detail);
        Assert.Equal(StepOutcome.Failed, failed.Outcome);
        Assert.Contains("factory-prepare --spool", h.WorkerCalls[0].Prompt);
    }

    [Fact]
    public async Task Without_worker_prepare_a_session_of_a_worktree_with_a_recipe_is_exactly_as_before()
    {
        var h = new GatePipelineTests.Harness();
        h.Workspaces.Root = Path.Combine(_root, "worktrees");
        Directory.CreateDirectory(Path.Combine(h.Workspaces.Root, "factory", "sc-77", PrepareRecipe.Directory));
        File.WriteAllText(Path.Combine(h.Workspaces.Root, "factory", "sc-77", PrepareRecipe.Path), "version: 1\nsteps: [{name: a, argv: [make]}]\n");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.DoesNotContain("factory-prepare", h.WorkerCalls[0].Prompt);
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
    }

    [Fact]
    public void The_worker_tools_add_only_the_installed_factory_prepare_with_worker_prepare_and_a_repo_allowing_it_otherwise_taints()
    {
        foreach (var swift in new[] { false, true })
        {
            Assert.Same(WorkerTools.ImplementerFor(swift), WorkerTools.ImplementerFor(swift, prepare: false));
            Assert.Equal(ClaudeWorker.AllowedToolsFor(swift), ClaudeWorker.AllowedToolsFor(swift, prepare: false));
            Assert.Equal([.. ClaudeWorker.AllowedToolsFor(swift), "Bash(/usr/local/libexec/dark-factory/factory-prepare:*)"],
                WorkerTools.ImplementerFor(swift, prepare: true).Allowed);
        }
        var worktree = Worktree(null);
        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        File.WriteAllText(Path.Combine(worktree, ".claude", "settings.json"), $$"""{ "permissions": { "allow": ["{{PrepareTool.AllowRule}}"] } }""");
        Assert.NotNull(Taint.OfRepoSettings(worktree, ClaudeWorker.AllowedToolsFor(false, prepare: false)));
        Assert.Null(Taint.OfRepoSettings(worktree, ClaudeWorker.AllowedToolsFor(false, prepare: true)));
    }

    [Fact]
    public async Task The_installed_factory_prepare_must_be_the_repos_and_start_up_says_how_to_install_it()
    {
        var installed = Path.Combine(_root, "factory-prepare");

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareTool.EnsureCurrentAsync("sudo scripts/setup-worker-user.sh", CancellationToken.None, installed));
        Assert.Contains("run `sudo scripts/setup-worker-user.sh` to install it, or set Worker:Prepare=false", missing.Message);

        File.WriteAllText(installed, "#!/bin/bash\necho old\n");
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareTool.EnsureCurrentAsync("sudo scripts/setup-worker-user.sh --slots 2", CancellationToken.None, installed));
        Assert.Contains("is stale", stale.Message);
        Assert.Contains("--slots 2", stale.Message);

        File.Copy(Script, installed, overwrite: true);
        await PrepareTool.EnsureCurrentAsync("x", CancellationToken.None, installed);
    }

    [Fact]
    public void A_worker_plan_expands_every_placeholder_and_lists_each_steps_fields()
    {
        var recipe = PrepareRecipe.Parse("""
            version: 1
            artifacts: [{name: sdk, url: 'https://files.example/s.zip', sha256: 0f343b0931126a20f133d67c2b018a3b5f6b4a9e3b8ab6a4e5c1b2a3d4e5f607, max_bytes: 10, unpack: zip}]
            steps:
              - {name: b, argv: [make, 'SDK=${artifact:sdk}'], cwd: lib, env: {OUT: '${worktree}/o', A: '1'}}
            """);

        var plan = PrepareSteps.WorkerPlan(recipe, "/w", new Dictionary<string, string> { ["sdk"] = "/c/sdk" });

        Assert.Equal(string.Join('\0', PrepareSteps.PlanHeader, "1", "b", "lib", "2", "A=1", "OUT=/w/o", "2", "make", "SDK=/c/sdk") + "\0", plan);
        var step = Assert.Single(PrepareSteps.TestSteps(recipe, new Dictionary<string, string> { ["sdk"] = "/c/sdk" }));
        Assert.Equal(["/usr/bin/env", "A=1", "OUT=${worktree}/o", "make", "SDK=/c/sdk"], step.Args.Prepend(step.Program));
    }
}
