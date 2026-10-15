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

    private WorkerPrepare Prepare(TimeSpan? poll = null) =>
        new(new ArtifactFetcher(new HttpClient(_server), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root))), WorkerPrepare.SpoolRootOf(_root), TextWriter.Null)
        {
            PollInterval = poll ?? TimeSpan.FromMilliseconds(50),
        };

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
        var refused = Worktree("version: 1\nsteps: [{name: a, argv: [curl, x]}]\n");
        await using (var session = await prepare.OpenAsync(refused, null, null, CancellationToken.None))
        {
            var (exit, stderr) = await RunScriptAsync(refused, "--spool", session.Directory, "--wait", "30");
            Assert.Equal(1, exit);
            Assert.Contains("the factory refused: .factory/prepare.yaml: steps[0].argv[0]: a step may not run curl", stderr);
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
        var prepare = Prepare(TimeSpan.FromHours(1)); // polled by hand below
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

        var listing = await WorkerSandbox.ListAsync(session.Directory, CancellationToken.None);
        Assert.True(WorkerSandbox.Grants(listing, "nobody", "add_file"), listing);
        Assert.False(WorkerSandbox.Grants(listing, "nobody", "delete_child"), listing);
        Assert.Null(WorkerSandbox.OpenToOthers(listing, "nobody"));
        // The root lets a user reach a session's directory by name and list nothing.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(WorkerPrepare.SpoolRootOf(_root)));
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
            for (var i = 0; i < 400 && answer is null; i++)
            {
                await Task.Delay(25);
                answer = File.Exists(Path.Combine(spool, "response-1")) ? File.ReadAllText(Path.Combine(spool, "response-1")) : null;
            }
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
        // A fix round's session of a worktree with no recipe is told nothing (E7): only sessions that have one are.
        Assert.All(h.WorkerCalls.Skip(1), c => Assert.DoesNotContain("factory-prepare", c.Prompt));
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
