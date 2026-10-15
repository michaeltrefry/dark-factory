using System.Diagnostics;
using System.Net;
using System.Text;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Prepare;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26232: the gate prepares each commit by its own recipe — the base runs (and their retries) by the base's, the head run by the
/// head's — before every new-tests strategy's build; a recipe that cannot be read or fetched is an error, never a pass; a commit
/// without one runs as before (E7).
/// </summary>
public sealed class PrepareGateTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-prepare-gate-").FullName;
    private readonly FakeArtifactServer _server = new();
    private readonly FakeResolver _resolver = new() { ["files.example"] = ["93.184.216.34"] };

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

    private PrepareStage Stage() => new(new ArtifactFetcher(new HttpClient(_server), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root))));

    private static string Recipe(string marker) => $"version: 1\nsteps:\n  - name: mark\n    argv: [/bin/echo, {marker}]\n";

    /// <summary>A fake runner per stack, each PR adding one new test (the strategies' own fixtures).</summary>
    public static TheoryData<string> Stacks => ["xunit", "cargo", "swiftpm", "xcode-macos", "xcode-ios"];

    private static FakeTestRunner Runner(string stack) => stack switch
    {
        "xunit" => new FakeTestRunner(),
        "cargo" => Cargo(),
        "swiftpm" => SwiftIdentifyTests.Runner((SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + "@Test func blank() { #expect(count(\"\") == 0) }\n")),
        "xcode-macos" => XcodeIdentifyTests.Runner(head: [(XcodeFixture.TestFile, XcodeFixture.BaseTests.Replace("struct WordsTests {", "struct WordsTests {\n    @Test func spaces() { #expect(isBlank(\" \")) }"))]),
        "xcode-ios" => Ios(),
        _ => throw new ArgumentOutOfRangeException(nameof(stack)),
    };

    private static FakeTestRunner Cargo()
    {
        const string lib = "pub fn words(s: &str) -> usize { s.split(' ').count() }\n#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn two() { assert_eq!(words(\"a b\"), 2); }\n}\n";
        var runner = CargoIdentifyTests.Runner(("Cargo.toml", "[package]\nname = \"words\"\nversion = \"0.1.0\"\nedition = \"2021\"\n"), ("src/lib.rs", lib));
        return CargoIdentifyTests.With(runner, ("src/lib.rs", lib.Replace("    fn two()", "    fn blank() { assert_eq!(words(\"\"), 0); }\n    #[test]\n    fn two()")));
    }

    private static FakeTestRunner Ios()
    {
        var runner = CargoIdentifyTests.Runner(XcodeIosFixture.Files());
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        return CargoIdentifyTests.With(runner, XcodeIosFixture.Head);
    }

    /// <summary>Adds <paramref name="baseRecipe"/> to the base and <paramref name="headRecipe"/> to every head (null: none).</summary>
    private static FakeTestRunner WithRecipes(FakeTestRunner runner, string? baseRecipe, string? headRecipe)
    {
        if (baseRecipe is not null)
        {
            runner.BaseFiles[PrepareRecipe.Path] = baseRecipe;
        }
        var heads = runner.HeadFiles;
        runner.HeadFiles = sha =>
        {
            var files = new Dictionary<string, string>(heads(sha), StringComparer.Ordinal);
            files.Remove(PrepareRecipe.Path);
            if (headRecipe is not null)
            {
                files[PrepareRecipe.Path] = headRecipe;
            }
            return files;
        };
        return runner;
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner, PreparedCommits? prepared) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(swift: true), Repo, FakeTestRunner.BaseSha, "head1", "gate-sc-1", null, CancellationToken.None, prepared);

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Every_strategy_runs_each_commits_own_recipe_after_its_toolchain_probes_and_before_its_build(string stack)
    {
        var runner = WithRecipes(Runner(stack), Recipe("base-recipe"), Recipe("head-recipe"));
        var prepared = await Stage().ForCommitsAsync(runner, Repo, FakeTestRunner.BaseSha, "head1", null, CancellationToken.None);

        var result = await Check(runner, prepared);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{stack}: {result.Outcome}: {result.Reason}");
        Assert.NotEmpty(runner.Runs);
        foreach (var run in runner.Runs)
        {
            var marker = run.Commit == FakeTestRunner.BaseSha ? "base-recipe" : "head-recipe";
            var index = run.Steps.ToList().FindIndex(s => s.Label == "prepare step mark");
            Assert.True(index >= 0, $"{stack}: {run.Name} has no recipe step");
            var step = run.Steps[index];
            Assert.Equal(("/bin/echo", marker, TestPhase.Restore, true), (step.Program, Assert.Single(step.Args), step.Phase, step.ExpandsWorktree));
            Assert.Single(run.Steps, s => s.Label is not null);
            // Only the strategy's toolchain probes come before it; its restore and build come after.
            Assert.All(run.Steps.Take(index), s => Assert.Equal(TestPhase.Toolchain, s.Phase));
            Assert.Contains(run.Steps.Skip(index + 1), s => s.Phase is TestPhase.Restore or TestPhase.Build);
        }
        // The base runs (with the PR's test files overlaid) use the base's recipe: the overlay applies test files only.
        Assert.Contains(runner.Runs, r => r.OverlayFrom is not null && r.Steps.Any(s => s.Args.Contains("base-recipe")));
        Assert.Contains(runner.Runs, r => r.OverlayFrom is null && r.Steps.Any(s => s.Args.Contains("head-recipe")));
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public async Task Without_a_recipe_every_strategys_runs_are_exactly_as_before(string stack)
    {
        var plain = Runner(stack);
        var prepared = Runner(stack);
        var commits = await Stage().ForCommitsAsync(prepared, Repo, FakeTestRunner.BaseSha, "head1", null, CancellationToken.None);

        await Check(plain, null);
        await Check(prepared, commits);

        Assert.False(commits.Any);
        // Each run's results directory is a fresh random name; everything else is the same, step for step.
        static string Steps(TestRunSpec run) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(
            string.Join("\n", run.Steps.Select(s => $"{s.Phase} {s.Label} {s.WorkingDirectory} {SandboxTestRunner.Describe(s)}")),
            $"{System.Text.RegularExpressions.Regex.Escape(NewTestsCheck.ResultsDirectoryPrefix)}[0-9a-f]{{32}}", "<results>"),
            "dark-factory-gate-[0-9a-f]{16}", "<simulator>"); // the iOS run's own simulator, named per run
        Assert.Equal(plain.Runs.Select(Steps), prepared.Runs.Select(Steps));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_recipe_that_does_not_parse_or_an_artifact_that_cannot_be_fetched_fails_naming_the_commit()
    {
        var bad = WithRecipes(new FakeTestRunner(), null, "version: 1\nsteps: [{name: a, argv: [curl, x]}]\n");
        var badParse = await Assert.ThrowsAsync<PrepareFailedException>(() =>
            Stage().ForCommitsAsync(bad, Repo, FakeTestRunner.BaseSha, "head1", null, CancellationToken.None));
        Assert.StartsWith("head1's prepare recipe: .factory/prepare.yaml", badParse.Message);
        Assert.Contains("may not run curl", badParse.Message);

        var missing = WithRecipes(new FakeTestRunner(),
            $"version: 1\nartifacts: [{{name: sdk, url: 'https://files.example/gone.zip', sha256: {PrepareRecipeTests.Sha}, max_bytes: 10}}]\nsteps: [{{name: a, argv: [make]}}]\n", null);
        var badFetch = await Assert.ThrowsAsync<PrepareFailedException>(() =>
            Stage().ForCommitsAsync(missing, Repo, FakeTestRunner.BaseSha, "head1", null, CancellationToken.None));
        Assert.StartsWith("base0's prepare recipe: artifact sdk", badFetch.Message);
        Assert.Contains("answered 404", badFetch.Message);
    }

    [Fact]
    public async Task Artifacts_are_fetched_once_per_commit_set_and_their_cached_paths_are_in_the_steps()
    {
        var zip = ArtifactFetchTests.Zip(("Generated.swift", "x", 0x81A4));
        _server.Serve("https://files.example/gen.zip", zip);
        var recipe = $"version: 1\nartifacts: [{{name: gen, url: 'https://files.example/gen.zip', sha256: {ArtifactFetchTests.Sha256(zip)}, max_bytes: 100000, unpack: zip}}]\n"
            + "steps: [{name: copy, argv: [/bin/cp, '${artifact:gen}/Generated.swift', '${worktree}/x.swift'], cwd: src}]\n";
        var runner = WithRecipes(new FakeTestRunner(), recipe, recipe);
        var downloads = new List<ArtifactDownloaded>();

        var prepared = await Stage().ForCommitsAsync(runner, Repo, FakeTestRunner.BaseSha, "head1", (d, _) => { downloads.Add(d); return Task.CompletedTask; },
            CancellationToken.None);

        var content = Path.Combine(ArtifactCache.RootOf(_root), $"{ArtifactFetchTests.Sha256(zip)}-zip", "content");
        var step = Assert.Single(prepared.StepsFor("head1"));
        Assert.Equal(["/bin/cp", content + "/Generated.swift", "${worktree}/x.swift"], step.Args.Prepend(step.Program));
        Assert.Equal("src", step.WorkingDirectory);
        Assert.Single(downloads);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_steps_directory_must_be_a_directory_inside_the_worktree_once_every_link_is_resolved()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        Directory.CreateDirectory(Path.Combine(worktree, "tools", "gen"));
        Directory.CreateSymbolicLink(Path.Combine(worktree, "out"), _root);
        await Task.CompletedTask;

        TestStep At(string? dir) => new(TestPhase.Restore, "/bin/true", []) { WorkingDirectory = dir };

        Assert.Equal(worktree, SandboxTestRunner.StepDirectory(worktree, At(null)));
        Assert.Equal(worktree, SandboxTestRunner.StepDirectory(worktree, At(".")));
        Assert.Equal(Path.Combine(worktree, "tools/gen"), SandboxTestRunner.StepDirectory(worktree, At("tools/gen")));
        Assert.Null(SandboxTestRunner.StepDirectory(worktree, At("out")));       // a link out of the worktree
        Assert.Null(SandboxTestRunner.StepDirectory(worktree, At("missing")));
        Assert.Null(SandboxTestRunner.StepDirectory(worktree, At("../x")));
    }

    [Fact]
    public async Task Through_the_pipeline_a_recipe_that_cannot_be_prepared_is_an_error_and_a_download_is_on_the_ledger()
    {
        var failing = new GatePipelineTests.Harness { Prepare = Stage() };
        WithRecipes(failing.TestRunner, null, "version: 1\nsteps: [{name: a, argv: [wget]}]\n");

        var refused = await failing.Run();

        Assert.Equal(WorkState.Escalated, refused.State);
        Assert.Empty(failing.Merges);
        var error = NewTestsResult.FromDetail((await failing.Rows()).Last(r => r.Step == RunPipeline.Steps.NewTests).Detail)!;
        Assert.Equal(NewTestsOutcome.Error, error.Outcome);
        Assert.Contains("may not run wget", error.Reason);
        Assert.Empty(failing.TestRunner.Runs); // nothing ran

        var body = "sdk"u8.ToArray();
        _server.Serve("https://files.example/sdk.bin", body);
        var merging = new GatePipelineTests.Harness { Prepare = Stage() };
        var recipe = $"version: 1\nartifacts: [{{name: sdk, url: 'https://files.example/sdk.bin', sha256: {ArtifactFetchTests.Sha256(body)}, max_bytes: 100}}]\n"
            + "steps: [{name: a, argv: [/bin/test, -f, '${artifact:sdk}']}]\n";
        WithRecipes(merging.TestRunner, recipe, recipe);

        var merged = await merging.Run();

        Assert.True(merged.Succeeded, merged.Error);
        var fetched = Assert.Single(await merging.Rows(), r => r.Step == RunPipeline.Steps.ArtifactFetch);
        Assert.Equal($"fetched sdk https://files.example/sdk.bin sha256={ArtifactFetchTests.Sha256(body)} bytes=3", fetched.Detail);
        Assert.Equal(StepOutcome.Passed, fetched.Outcome);
        Assert.All(merging.TestRunner.Runs, r => Assert.Contains(r.Steps, s => s.Label == "prepare step a"));
    }
}

/// <summary>
/// sc-26232, the real toolchain as the owner (no sandbox user): a SwiftPM package whose build needs a file only the recipe's
/// artifact holds, served by a local HTTP server. The production download rules refuse loopback and http, so the test injects the
/// seams — a resolver answering a public address and a transport that carries the request to the local server — and leaves the
/// rules as they are. Skipped where swift is not installed.
/// </summary>
public sealed class PrepareRealRunTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-prepare-real-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public PrepareRealRunTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

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

    private static readonly Lazy<bool> HasSwift = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(SwiftNewTests.Swift, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_seed, path))!);
        File.WriteAllText(Path.Combine(_seed, path), content);
    }

    private string CommitAndPush(string message, string branch)
    {
        TestGit.Run(_seed, "add", "-A");
        TestGit.Run(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        TestGit.Run(_seed, "push", "-q", _remote, branch);
        return TestGit.Run(_seed, "rev-parse", "HEAD").Trim();
    }

    /// <summary>A local HTTP server answering one path with <paramref name="body"/>, counting requests.</summary>
    private sealed class LocalServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        public int Requests;

        public LocalServer(byte[] body)
        {
            // A free port, taken again if another test took it between the probe and the listener's start.
            for (var attempt = 1; ; attempt++)
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                try
                {
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    _listener.Close();
                }
            }
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Interlocked.Increment(ref Requests);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    /// <summary>The injected transport: the request for the artifact's https URL is carried to the local server (as plain http).</summary>
    private sealed class ToLocal(int port) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task With_swift_a_package_whose_build_needs_the_recipes_artifact_is_prepared_by_each_commits_recipe_and_passes()
    {
        Assert.SkipUnless(HasSwift.Value, "swift is not installed");
        var zip = ArtifactFetchTests.Zip(("gen/Generated.swift", "func generatedMagic() -> Int { 42 }\n", 0x81A4));
        using var server = new LocalServer(zip);
        var recipe = $$"""
            version: 1
            artifacts:
              - name: gen
                url: https://artifacts.example/gen.zip
                sha256: {{ArtifactFetchTests.Sha256(zip)}}
                max_bytes: 100000
                unpack: zip
            steps:
              - name: copy-generated
                argv: [/bin/sh, -c, 'cp "$GENERATED" Generated.swift']
                cwd: Sources/Words
                env:
                  GENERATED: ${artifact:gen}/gen/Generated.swift
              - name: check
                argv: [/bin/test, -f, '${worktree}/Sources/Words/Generated.swift']
            """;
        Write("Package.swift", SwiftIdentifyTests.Manifest);
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.BaseLibrary + "public let magic = generatedMagic()\n");
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests);
        Write(".gitignore", ".build/\nSources/Words/Generated.swift\n");
        Write(PrepareRecipe.Path, recipe);
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-6");
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary + "public let magic = generatedMagic()\n");
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + "@Test func blank() { #expect(count(\"\") == 0) }\n");
        var head = CommitAndPush("fix", "factory/sc-6");

        var runner = new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null,
            TimeSpan.FromMinutes(10));
        var fetcher = new ArtifactFetcher(new HttpClient(new ToLocal(server.Port)), new FakeResolver { ["artifacts.example"] = ["93.184.216.34"] },
            new ArtifactCache(ArtifactCache.RootOf(Path.Combine(_root, "work"))));
        var downloads = new List<ArtifactDownloaded>();
        var prepared = await new PrepareStage(fetcher).ForCommitsAsync(runner, Repo, baseSha, head, (d, _) => { downloads.Add(d); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        var pass = await NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, baseSha, head, "gate-sc-6", null, TestContext.Current.CancellationToken, prepared);
        // Without the recipe's steps the base does not build (Generated.swift is the artifact's): no evidence, an error.
        var unprepared = await NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, baseSha, head, "gate-sc-6", null, TestContext.Current.CancellationToken);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal([("WordsTests.blank()", "failed", "passed")], pass.Tests.Select(t => (t.Test, t.Base, t.Head)));
        Assert.Equal(1, server.Requests); // one download for both commits' recipes (the same artifact), then the cache
        Assert.Single(downloads);
        Assert.Equal(NewTestsOutcome.Error, unprepared.Outcome);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}
