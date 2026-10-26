using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Prepare;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// S6 remote execution acceptance. Inputs are immutable source commits, not operator-written result receipts. Discovery,
/// placement, base overlay, remote dispatch/provenance, per-test parsing and judging all use production implementations.
/// No local Xcode/simulator execution is possible, including when a remote executor is missing or refuses the request.
/// The fixture manifest is supplied with FACTORY_E2E_S6_REMOTE_FIXTURES (JSON array of Fixture records).
/// </summary>
public sealed class HeadlessRemoteAcceptanceTests(ITestOutputHelper output)
{
    private static readonly RepoRef FakeRepo = new("acceptance", "remote-fixture");
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Head = "2222222222222222222222222222222222222222";
    private const string Workflow = "3333333333333333333333333333333333333333";
    private const string EnvironmentSource = ".factory-e2e/EnvironmentProcessTests.swift";

    /// <summary>
    /// Kind: hosted-ios, gui-ios-ui, gui-macos-apphost or gui-test-env. Tests must be the complete new-test set of this pair.
    /// The env fixture adds the shipped EnvironmentProcessTests.swift to its existing macOS app-host test target at Head;
    /// its project points directly to that file. Base and Head both contain probe-env and commit-owned prepare recipes.
    /// </summary>
    public sealed record Fixture(string Kind, string Repository, string Base, string Head, string[] Tests);

    [Theory]
    [InlineData("hosted-ios")]
    [InlineData("gui-ios-ui")]
    [InlineData("gui-macos-apphost")]
    [InlineData("gui-test-env")]
    public async Task S6_AT3_real_remote_new_tests_fail_on_base_and_pass_on_head(string kind)
    {
        Harness.RequireOptIn();
        var manifest = await S6FixtureProvisioning.ManifestAsync(failure: false, TestContext.Current.CancellationToken);
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        ValidateFixtures(fixtures);
        var fixture = Assert.Single(fixtures, f => f.Kind == kind);
        var repo = RepoRef.Parse(fixture.Repository);
        var appId = Harness.RequireSecret(o => o.GitHubGateAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        var gui = kind != "hosted-ios";
        var settings = Harness.Options.GuiWorkflow;
        if (gui && settings is null)
            Assert.Skip("GUI acceptance needs Gate:Gui:CollectorRepo, TrustedRepos and ConfigurationSha256, and the protected verified lwrunner deployment. No owner/hosted fallback is permitted.");
        if (gui) Assert.True(settings!.Allows(repo), "The fixture must be explicitly authorized in Gate:Gui:TrustedRepos.");

        using var http = OutboundHttp.GitHubApi();
        var app = new GitHubApp(http, appId, appKey, TimeProvider.System);
        var root = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromHours(2)); // several independently bounded base/head invocations
            var ct = deadline.Token;
            var git = new GitWorkspace(root, GitRemoteReads.GitHubRemote,
                async (repository, token) => (await app.CreateInstallationTokenAsync(repository, token,
                    new Dictionary<string, string> { ["contents"] = "read" })).Token);
            var source = new ReadOnlySource(new SandboxTestRunner(git, null, TimeSpan.FromMinutes(1)));
            var changes = await source.ChangesAsync(repo, fixture.Base, fixture.Head, ct);
            var strategy = new XcodeNewTests();
            var set = await strategy.IdentifyAsync(new TestSource(source, repo, fixture.Base, fixture.Head), changes, ct);
            Assert.Empty(set.Unisolable);
            Assert.Equal(fixture.Tests.Order(StringComparer.Ordinal), set.Tests.Order(StringComparer.Ordinal));
            Assert.NotEmpty(set.Tests);
            Assert.All(set.Tests, test => Assert.Equal(gui ? XcodeTestPlacement.Gui : XcodeTestPlacement.Hosted,
                set.XcodePlacements[test]));
            if (kind == "gui-ios-ui") Assert.Contains(set.Tests, test => test.Contains("UITests/", StringComparison.Ordinal));
            // The projects map is the strategy's serialized build identity, not external expected receipts.
            Assert.All(set.Projects.Values, build => Assert.Equal(kind is "hosted-ios" or "gui-ios-ui",
                build.Split('\n').Last() == XcodeProject.IOS));

            var hostedTransport = new ObservedWorkflow(new IosWorkflowGitHub(http, app));
            var guiTransport = new ObservedWorkflow(new IosWorkflowGitHub(http, app, gui: true));
            // Stable across test/process restarts: a cancelled run with an invisible correlation must not lose its
            // durable no-overlap barrier merely because the next acceptance run uses a fresh source clone.
            var dispatchRoot = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-remote-dispatches");
            var hosted = new RemoteIosNewTests(hostedTransport, Path.Combine(dispatchRoot, "hosted")) { Timeout = TimeSpan.FromMinutes(45) };
            var remoteGui = gui ? new RemoteIosNewTests(guiTransport, Path.Combine(dispatchRoot, "gui"), settings) { Timeout = TimeSpan.FromMinutes(45) } : null;
            var selected = gui ? guiTransport : hostedTransport;

            if (kind == "gui-test-env") await VerifyEnvironmentFixture(source, repo, fixture, ct);
            var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), repo,
                fixture.Base, fixture.Head, "gate-s6-remote", output.WriteLine, ct, remoteIos: hosted, remoteGui: remoteGui);
            output.WriteLine(result.ToDetail());
            Assert.True(result.Passed, result.Reason);
            Assert.Equal(fixture.Base, result.BaseSha);
            Assert.Equal(fixture.Head, result.HeadSha);
            Assert.Equal(fixture.Tests.Order(StringComparer.Ordinal), result.Tests.Select(t => t.Test).Order(StringComparer.Ordinal));
            Assert.All(result.Tests, test =>
            {
                // Runtime failures required: a compile failure or absent test is not this fixture's evidence.
                Assert.NotEmpty(test.BaseCases);
                Assert.Contains(test.BaseCases, c => c.Outcome == TestCaseResult.Failed);
                Assert.NotEmpty(test.HeadCases);
                Assert.All(test.HeadCases, c => Assert.Equal(TestCaseResult.Passed, c.Outcome));
            });
            Assert.Equal(0, source.LocalRuns);
            Assert.Empty((gui ? hostedTransport : guiTransport).Dispatches);
            Assert.Equal(2, selected.Dispatches.Count);
            Assert.All(selected.Dispatches, dispatch => Assert.Equal(gui ? settings!.Collector : repo, dispatch.Repo));
            Assert.Equal(new[] { "base", "head" }, selected.Dispatches.Select(d => Side(d.Request)));
            Assert.Equal(2, selected.ResultsRead); // actual artifacts validated by RemoteIosNewTests, never job conclusion alone
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dispatchRoot, gui ? "gui" : "hosted"), "*.json"));

            if (kind == "gui-test-env")
            {
                // Same immutable head and same real subprocess test, but no test_env. A transported declaration alone
                // cannot pass: its omission must make the native executed test fail, not produce a missing report/error.
                var spec = new TestRunSpec("gate-s6-without-env", fixture.Head, null, [], [], [], strategy,
                    ".factory-test-results-" + Guid.NewGuid().ToString("N"));
                var without = await remoteGui!.RunExecutionAsync(repo, fixture.Base, fixture.Head, spec,
                    set.Tests, set.Projects, prepare: null, ct);
                Assert.Equal(TestRunStatus.Ran, without.Status);
                Assert.Empty(without.UnreadableReports);
                Assert.All(set.Tests, test => Assert.Contains(without.Results[test], c => c.Outcome == TestCaseResult.Failed));
                Assert.Equal(3, selected.ResultsRead);
                Assert.Equal(3, selected.Dispatches.Count);
                // This native XCTest failure message occurs only AFTER Process.run, pipe read and waitUntilExit.
                // A missing executable, blocked child or wrong selection cannot satisfy the negative arm.
                Assert.Contains("S6_ENV_CHILD_OUTPUT_CONTRACT", selected.Artifacts[^1].Text);
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(dispatchRoot, "gui"), "*.json"));
            }
            if (gui) AssertSerializedGuiLeases(selected.Artifacts.Select(a => ParseGuiLease(a.Text)).ToArray());
        }
        finally
        {
            // Only owner-written clones exist here; ReadOnlySource cannot create a source worktree locally.
            // Dispatch journals live outside this directory and are never removed by acceptance cleanup. A rerun uses
            // the same journal and production reconciliation; operator repair requires proving the old run terminal.
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Real negative fixtures, never synthetic result receipts: the host-error pair has a macOS test host that cannot
    /// launch; the credential pair declares an anonymously inaccessible pinned package whose endpoint requires auth.
    /// Both still add discoverable test methods to targets already present at Base. Exact current sealed executors required.
    /// </summary>
    [Theory]
    [InlineData("gui-host-launch-error")]
    [InlineData("hosted-package-auth-required")]
    public async Task S6_AT4_real_remote_failure_artifacts_never_authorize_test_results(string kind)
    {
        Harness.RequireOptIn();
        var manifest = await S6FixtureProvisioning.ManifestAsync(failure: true, TestContext.Current.CancellationToken);
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Single(fixtures, f => f.Kind == "gui-host-launch-error");
        Assert.All(fixtures, f => Assert.Contains(f.Kind, new[] { "gui-host-launch-error", "hosted-package-auth-required" }));
        if (kind == "hosted-package-auth-required" && !fixtures.Any(f => f.Kind == kind))
            Assert.Skip("Optional VCS-auth live fixture not provisioned: requires a real anonymously requested pinned package endpoint answering 401/403; 404 is not fabricated as authentication refusal. This is not a mandatory S6 row.");
        var fixture = Assert.Single(fixtures, f => f.Kind == kind);
        Assert.True(RemoteIosWorkflow.Sha(fixture.Base));
        Assert.True(RemoteIosWorkflow.Sha(fixture.Head));
        Assert.NotEqual(fixture.Base, fixture.Head);
        Assert.NotEmpty(fixture.Tests);
        var repo = RepoRef.Parse(fixture.Repository);
        var gui = kind == "gui-host-launch-error";
        var settings = Harness.Options.GuiWorkflow;
        if (gui && settings is null)
            Assert.Skip("Host-launch acceptance requires the protected verified lwrunner GUI collector and Gate:Gui authority; no local fallback is allowed.");
        if (gui) Assert.True(settings!.Allows(repo));
        using var http = OutboundHttp.GitHubApi();
        var app = new GitHubApp(http, Harness.RequireSecret(o => o.GitHubGateAppId),
            Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem), TimeProvider.System);
        var root = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromHours(2));
            var ct = deadline.Token;
            var git = new GitWorkspace(root, GitRemoteReads.GitHubRemote,
                async (repository, token) => (await app.CreateInstallationTokenAsync(repository, token,
                    new Dictionary<string, string> { ["contents"] = "read" })).Token);
            var source = new ReadOnlySource(new SandboxTestRunner(git, null, TimeSpan.FromMinutes(1)));
            var changes = await source.ChangesAsync(repo, fixture.Base, fixture.Head, ct);
            var strategy = new XcodeNewTests();
            var set = await strategy.IdentifyAsync(new TestSource(source, repo, fixture.Base, fixture.Head), changes, ct);
            Assert.Empty(set.Unisolable);
            Assert.Equal(fixture.Tests.Order(StringComparer.Ordinal), set.Tests.Order(StringComparer.Ordinal));
            Assert.All(set.Tests, test => Assert.Equal(gui ? XcodeTestPlacement.Gui : XcodeTestPlacement.Hosted, set.XcodePlacements[test]));
            Assert.All(set.Projects.Values, build => Assert.Equal(!gui, build.Split('\n').Last() == XcodeProject.IOS));
            if (gui)
            {
                var seeds = Path.Combine(AppContext.BaseDirectory, "Seeds", "headless-remote");
                foreach (var commit in new[] { fixture.Base, fixture.Head })
                    Assert.Equal(await File.ReadAllTextAsync(Path.Combine(seeds, "HostLaunchFailure.swift"), ct),
                        await source.ReadAsync(repo, commit, ".factory-e2e/HostLaunchFailure.swift", ct));
                Assert.Null(await source.ReadAsync(repo, fixture.Base, ".factory-e2e/HostLaunchProbeTests.swift", ct));
                Assert.Equal(await File.ReadAllTextAsync(Path.Combine(seeds, "HostLaunchProbeTests.swift"), ct),
                    await source.ReadAsync(repo, fixture.Head, ".factory-e2e/HostLaunchProbeTests.swift", ct));
                Assert.EndsWith("/HostLaunchProbeTests/testHostMustActuallyLaunchBeforeThisCanCount()", Assert.Single(fixture.Tests));
            }
            var transport = new ObservedWorkflow(new IosWorkflowGitHub(http, app, gui));
            var journal = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-remote-dispatches", gui ? "gui" : "hosted");
            var remote = new RemoteIosNewTests(transport, journal, gui ? settings : null) { Timeout = TimeSpan.FromMinutes(45) };
            var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), repo,
                fixture.Base, fixture.Head, "gate-s6-failure", output.WriteLine, ct,
                remoteIos: gui ? null : remote, remoteGui: gui ? remote : null);
            output.WriteLine(result.ToDetail());
            Assert.Equal(NewTestsOutcome.Error, result.Outcome);
            Assert.False(result.Passed);
            Assert.Equal(0, source.LocalRuns);
            Assert.Equal(2, transport.Dispatches.Count);
            Assert.Equal(2, transport.Artifacts.Count);
            Assert.Equal(new[] { "base", "head" }, transport.Dispatches.Select(d => Side(d.Request)));
            Assert.All(transport.Dispatches, dispatch => Assert.Equal(gui ? settings!.Collector : repo, dispatch.Repo));
            const string invalid = "remote-invalid-evidence: The sealed iOS workflow or its executed evidence could not be validated. Verify the installed workflow and result artifacts.";
            const string auth = "remote-package-auth-required: A package requires credentials unavailable to the hosted runner. Use an anonymously accessible pinned dependency or a declared prepared artifact; owner credentials are never forwarded.";
            Assert.Equal($"the head {fixture.Head[..12]} could not be tested (no-results): {(gui ? invalid : auth)}", result.Reason);
            Assert.Equal(fixture.Tests.Order(StringComparer.Ordinal), result.Tests.Select(t => t.Test).Order(StringComparer.Ordinal));
            Assert.All(result.Tests, test => { Assert.Empty(test.HeadCases); Assert.Empty(test.BaseCases); });
            foreach (var artifact in transport.Artifacts)
            {
                using var document = JsonDocument.Parse(artifact.Text);
                var body = document.RootElement;
                Assert.Equal(artifact.Nonce, body.GetProperty("nonce").GetString());
                Assert.Equal(fixture.Base, body.GetProperty("base").GetString());
                Assert.Equal(fixture.Head, body.GetProperty("head").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.Request.Execution!))),
                    body.GetProperty("executionHash").GetString());
                var execution = body.GetProperty("execution");
                var side = Side(artifact.Request);
                Assert.Equal(side, body.GetProperty("side").GetString());
                Assert.Equal(side == "base" ? fixture.Base : fixture.Head, execution.GetProperty("commit").GetString());
                if (gui)
                {
                    Assert.Equal(settings!.ConfigurationSha256, body.GetProperty("guiConfigurationHash").GetString());
                    Assert.Equal("ran", execution.GetProperty("status").GetString());
                    var files = execution.GetProperty("files").Deserialize<ResultFile[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    var parsed = strategy.ParseResults(files);
                    Assert.Empty(parsed.Cases);
                    // This must be an actual app-host failure, not a missing report, unknown result or invalid selection.
                    Assert.NotEmpty(parsed.Unreadable);
                    Assert.All(parsed.Unreadable, error => Assert.Contains("the test host of its step's test failed before the test ran", error));
                }
                else
                {
                    Assert.Equal("prepare-failed", execution.GetProperty("status").GetString());
                    Assert.Equal("package-auth-required", execution.GetProperty("failure").GetProperty("code").GetString());
                    Assert.Empty(execution.GetProperty("files").EnumerateArray());
                }
            }
            // Terminal artifacts release the production dispatch barrier even though they prove no tests. Retrying the
            // same dedicated fixture can reconcile any earlier interrupted run through this stable repository journal.
            Assert.Empty(Directory.EnumerateFiles(journal, "*.json"));
            if (gui) AssertSerializedGuiLeases(transport.Artifacts.Select(a => ParseGuiLease(a.Text)).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task S6_AT4_two_concurrent_real_gui_checks_have_fresh_nonoverlapping_cleaned_source_leases()
    {
        Harness.RequireOptIn();
        var manifest = await S6FixtureProvisioning.ManifestAsync(failure: false, TestContext.Current.CancellationToken);
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        ValidateFixtures(fixtures);
        var fixture = Assert.Single(fixtures, f => f.Kind == "gui-macos-apphost");
        var repo = RepoRef.Parse(fixture.Repository);
        var settings = Harness.Options.GuiWorkflow;
        if (settings is null) Assert.Skip("Concurrent native GUI acceptance requires the deployed protected collector and Gate:Gui authority; no owner simulator is allowed.");
        Assert.True(settings!.Allows(repo));
        var appId = Harness.RequireSecret(o => o.GitHubGateAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        var root = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromHours(3));
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        async Task BeforeFirstDispatch(CancellationToken ct)
        {
            // Both production checks must have finished discovery and reached their own dispatch boundary before
            // either submits. Separate instances/journals deliberately cannot serialize the test on the owner side.
            if (Interlocked.Increment(ref ready) == 2) bothReady.TrySetResult();
            await bothReady.Task.WaitAsync(ct);
        }
        async Task<GuiLeaseEvidence[]> Arm(string name)
        {
            try
            {
                using var http = OutboundHttp.GitHubApi();
                var app = new GitHubApp(http, appId, appKey, TimeProvider.System);
                var git = new GitWorkspace(Path.Combine(root, name), GitRemoteReads.GitHubRemote,
                    async (repository, ct) => (await app.CreateInstallationTokenAsync(repository, ct,
                        new Dictionary<string, string> { ["contents"] = "read" })).Token);
                var source = new ReadOnlySource(new SandboxTestRunner(git, null, TimeSpan.FromMinutes(1)));
                var observed = new ObservedWorkflow(new IosWorkflowGitHub(http, app, gui: true), BeforeFirstDispatch);
                var journal = Path.Combine(Harness.Options.WorkRoot, "e2e", "s6-concurrent-dispatches", name);
                var remote = new RemoteIosNewTests(observed, journal, settings) { Timeout = TimeSpan.FromMinutes(60) };
                var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), repo, fixture.Base,
                    fixture.Head, "gate-s6-concurrent-" + name, null, deadline.Token, remoteGui: remote);
                Assert.True(result.Passed, name + ": " + result.Reason);
                Assert.Equal(fixture.Base, result.BaseSha);
                Assert.Equal(fixture.Head, result.HeadSha);
                Assert.Equal(fixture.Tests.Order(StringComparer.Ordinal), result.Tests.Select(t => t.Test).Order(StringComparer.Ordinal));
                Assert.All(result.Tests, test =>
                {
                    Assert.NotEmpty(test.BaseCases);
                    Assert.Contains(test.BaseCases, c => c.Outcome == TestCaseResult.Failed);
                    Assert.NotEmpty(test.HeadCases);
                    Assert.All(test.HeadCases, c => Assert.Equal(TestCaseResult.Passed, c.Outcome));
                });
                Assert.Equal(0, source.LocalRuns);
                Assert.Equal(2, observed.Dispatches.Count);
                Assert.Equal(2, observed.Artifacts.Count);
                Assert.All(observed.Dispatches, dispatch => Assert.Equal(settings.Collector, dispatch.Repo));
                Assert.Equal(new[] { "base", "head" }, observed.Dispatches.Select(d => Side(d.Request)));
                Assert.Empty(Directory.EnumerateFiles(journal, "*.json"));
                return observed.Artifacts.Select(a => ParseGuiLease(a.Text)).ToArray();
            }
            catch { deadline.Cancel(); throw; } // a failed arm cannot leave its sibling waiting on the first-dispatch barrier
        }
        try
        {
            var arms = await Task.WhenAll(Arm("a"), Arm("b"));
            var leases = arms.SelectMany(a => a).ToArray();
            Assert.Equal(2, ready);
            Assert.Equal(4, leases.Length);
            AssertSerializedGuiLeases(leases);
            output.WriteLine("Four actual base/head executions had distinct cleaned workspaces and nonoverlapping protected source-lease intervals.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal sealed record GuiLeaseEvidence(string WorkspaceSha256, double StartedUtcSeconds, double CompletedUtcSeconds, bool Cleaned, bool Reconciled);

    internal static GuiLeaseEvidence ParseGuiLease(string artifact)
    {
        try
        {
            using var document = JsonDocument.Parse(artifact, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.EnumerateObject().Count(p => p.Name == "guiLease") != 1) throw new InvalidDataException("Missing or duplicate trusted GUI lease evidence.");
            var lease = root.GetProperty("guiLease");
            var keys = lease.EnumerateObject().Select(p => p.Name).ToArray();
            if (!keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "cleaned", "completedUtcSeconds", "reconciled", "startedUtcSeconds", "workspaceSha256" }))
                throw new InvalidDataException("Incomplete or ambiguous GUI lease evidence.");
            var hash = lease.GetProperty("workspaceSha256").GetString()!;
            var started = lease.GetProperty("startedUtcSeconds").GetDouble();
            var completed = lease.GetProperty("completedUtcSeconds").GetDouble();
            var cleaned = lease.GetProperty("cleaned").GetBoolean();
            var reconciled = lease.GetProperty("reconciled").GetBoolean();
            if (!cleaned || !double.IsFinite(started) || !double.IsFinite(completed) || started <= 0 || completed <= started
                || !Regex.IsMatch(hash, "^[0-9a-f]{64}$")) throw new InvalidDataException("GUI source cleanup and a finite positive execution interval must be proven.");
            var executionRoot = root.GetProperty("execution").GetProperty("root").GetString()!;
            if (!Regex.IsMatch(executionRoot, "^/(?:private/)?tmp/factory-ios-[A-Za-z0-9_-]+/(?:base|head)$"))
                throw new InvalidDataException("The GUI lease has no execution workspace binding.");
            var workspace = executionRoot[..executionRoot.LastIndexOf('/')];
            if (hash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspace))))
                throw new InvalidDataException("The GUI lease describes another workspace.");
            return new(hash, started, completed, cleaned, reconciled);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new InvalidDataException("The trusted GUI lease evidence could not be read.", ex); }
    }

    internal static void AssertSerializedGuiLeases(IReadOnlyList<GuiLeaseEvidence> leases)
    {
        if (leases.Count == 0) throw new InvalidDataException("No actual GUI executions supplied source-lease evidence.");
        var ordered = leases.OrderBy(l => l.StartedUtcSeconds).ToArray();
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lease in ordered)
        {
            if (!lease.Cleaned || !double.IsFinite(lease.StartedUtcSeconds) || !double.IsFinite(lease.CompletedUtcSeconds)
                || lease.StartedUtcSeconds <= 0 || lease.CompletedUtcSeconds <= lease.StartedUtcSeconds)
                throw new InvalidDataException("A GUI source lease did not prove cleanup and a positive interval.");
            if (!workspaces.Add(lease.WorkspaceSha256)) throw new InvalidDataException("GUI base/head execution reused a source workspace.");
        }
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index].StartedUtcSeconds < ordered[index - 1].CompletedUtcSeconds)
                throw new InvalidDataException("Actual protected GUI source-lease intervals overlapped.");
    }

    private static async Task VerifyEnvironmentFixture(IGateTestRunner source, RepoRef repo, Fixture fixture, CancellationToken ct)
    {
        var seedRoot = Path.Combine(AppContext.BaseDirectory, "Seeds", "headless-remote");
        Assert.Null(await source.ReadAsync(repo, fixture.Base, EnvironmentSource, ct));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(seedRoot, "EnvironmentProcessTests.swift"), ct),
            await source.ReadAsync(repo, fixture.Head, EnvironmentSource, ct));
        foreach (var (commit, side) in new[] { (fixture.Base, "base"), (fixture.Head, "head") })
        {
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(seedRoot, "probe-env"), ct),
                await source.ReadAsync(repo, commit, ".factory-e2e/probe-env", ct));
            var yaml = await source.ReadAsync(repo, commit, PrepareRecipe.Path, ct);
            Assert.NotNull(yaml);
            var recipe = PrepareRecipe.Parse(yaml);
            Assert.Equal(side, recipe.TestEnvironment["REPO_MODE"]);
            Assert.Equal("${worktree}/.factory-e2e/probe-env", recipe.TestEnvironment["REPO_HELPER"]);
            Assert.Empty(recipe.Artifacts); // this fixture needs no mutable download or owner binary
        }
        Assert.Single(fixture.Tests);
        Assert.EndsWith("/EnvironmentProcessTests/testRepositoryEnvironmentReachesChild()", fixture.Tests[0]);
    }

    internal static void ValidateFixtures(IReadOnlyList<Fixture> fixtures)
    {
        Assert.Equal(new[] { "gui-ios-ui", "gui-macos-apphost", "gui-test-env", "hosted-ios" }, fixtures.Select(f => f.Kind).Order(StringComparer.Ordinal));
        foreach (var fixture in fixtures)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", fixture.Repository);
            Assert.True(RemoteIosWorkflow.Sha(fixture.Base));
            Assert.True(RemoteIosWorkflow.Sha(fixture.Head));
            Assert.NotEqual(fixture.Base, fixture.Head);
            Assert.NotEmpty(fixture.Tests);
            Assert.Equal(fixture.Tests.Length, fixture.Tests.Distinct(StringComparer.Ordinal).Count());
            Assert.All(fixture.Tests, test => Assert.Matches("^[A-Za-z0-9_]+/[A-Za-z0-9_/]+\\(\\)$", test));
        }
    }

    private static string? Side(IosNewTestsRequest request)
    {
        using var document = JsonDocument.Parse(request.Execution!);
        return document.RootElement.GetProperty("side").GetString();
    }

    /// <summary>All source reads/fetches remain production code; any accidentally local execution fails before a process starts.</summary>
    private sealed class ReadOnlySource(IGateTestRunner reads) : IGateTestRunner
    {
        public int LocalRuns;
        public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) => reads.ChangesAsync(repo, baseSha, headSha, ct);
        public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) => reads.FilesAsync(repo, sha, ct);
        public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) => reads.ReadAsync(repo, sha, path, ct);
        public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct)
        {
            LocalRuns++;
            throw new InvalidOperationException("Acceptance forbids all local source execution, including an owner simulator fallback.");
        }
    }

    private sealed class ObservedWorkflow(IIosWorkflowGitHub inner, Func<CancellationToken, Task>? beforeFirstDispatch = null) : IIosWorkflowGitHub
    {
        public readonly List<(RepoRef Repo, IosNewTestsRequest Request)> Dispatches = [];
        public readonly List<(string Nonce, IosNewTestsRequest Request, string Text)> Artifacts = [];
        private readonly Dictionary<string, IosNewTestsRequest> requests = new(StringComparer.Ordinal);
        public int ResultsRead;
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) => inner.ReadDefaultWorkflowAsync(repo, ct);
        public async Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            if (Dispatches.Count == 0 && beforeFirstDispatch is not null) await beforeFirstDispatch(ct);
            Dispatches.Add((repo, request));
            requests.Add(correlation, request);
            await inner.DispatchAsync(repo, workflow, request, correlation, ct);
        }
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct) => inner.RunsAsync(repo, ct);
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => inner.CancelAsync(repo, run, ct);
        public async Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
        {
            ResultsRead++;
            var text = await inner.ResultAsync(repo, run, correlation, ct);
            Artifacts.Add((correlation, requests[correlation], text));
            return text;
        }
    }

    [Fact]
    public void Safe_fixture_metadata_refuses_missing_matrix_rows_and_mutable_or_equal_commits()
    {
        Fixture[] Valid() => [.. new[] { "hosted-ios", "gui-ios-ui", "gui-macos-apphost", "gui-test-env" }
            .Select(kind => new Fixture(kind, FakeRepo.FullName, Base, Head, ["ProbeTests/Probe/testNew()"]))];
        ValidateFixtures(Valid());
        Assert.ThrowsAny<Exception>(() => ValidateFixtures(Valid()[1..]));
        var invalid = Valid(); invalid[0] = invalid[0] with { Head = "main" };
        Assert.ThrowsAny<Exception>(() => ValidateFixtures(invalid));
        invalid = Valid(); invalid[0] = invalid[0] with { Head = Base };
        Assert.ThrowsAny<Exception>(() => ValidateFixtures(invalid));
    }

    [Fact]
    public async Task Safe_green_workflow_without_executed_artifact_is_not_acceptance()
    {
        var transport = new FakeWorkflow();
        var result = await Remote(transport).RunAsync(FakeRepo, Request, CancellationToken.None);
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Empty(result.Tests);
        Assert.Equal(1, transport.ResultReads);
        Assert.Contains("remote-invalid-evidence", result.Reason);
    }

    [Fact]
    public async Task Safe_uncertain_dispatch_survives_restart_then_reconciles_before_replacement()
    {
        var directory = Directory.CreateTempSubdirectory("df-s6-journal-").FullName;
        var transport = new FakeWorkflow { Invisible = true };
        try
        {
            var first = await Remote(transport, directory).RunAsync(FakeRepo, Request, CancellationToken.None);
            Assert.Contains("remote-cancel-uncertain", first.Reason);
            Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
            var restarted = await Remote(transport, directory).RunAsync(FakeRepo, Request, CancellationToken.None);
            Assert.Contains("remote-cancel-uncertain", restarted.Reason);
            Assert.Equal(1, transport.Dispatches);
            Assert.Equal(0, transport.Cancels); // invisible is not permission to cancel some other run
            transport.Invisible = false;
            transport.Completed = false;
            var reconciled = await Remote(transport, directory).RunAsync(FakeRepo, Request, CancellationToken.None);
            Assert.Equal(2, transport.Dispatches);
            Assert.Equal(1, transport.Cancels);
            Assert.True(transport.TerminalBeforeReplacement);
            Assert.Equal(NewTestsOutcome.Error, reconciled.Outcome); // terminal cleanup never manufactures test evidence
            Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Safe_gui_source_not_explicitly_trusted_never_reads_or_dispatches_the_collector()
    {
        var transport = new FakeWorkflow { Gui = true };
        var settings = new GuiWorkflowSettings(new("acceptance", "collector"), [new("acceptance", "other")], new string('a', 64));
        var strategy = new XcodeNewTests();
        var spec = new TestRunSpec("gate-s6-head", Head, null, [], [], [], strategy, ".results");
        var result = await new RemoteIosNewTests(transport, gui: settings).RunExecutionAsync(FakeRepo, Base, Head,
            spec, ["ProbeTests/Probe/testNew()"], new Dictionary<string, string> { ["ProbeTests/Probe/testNew()"] = "-project\nProbe.xcodeproj\nProbe" }, null, CancellationToken.None);
        Assert.Equal(0, transport.Dispatches);
        Assert.Equal(0, transport.WorkflowReads);
        Assert.Equal(TestRunStatus.NoResults, result.Status);
        Assert.Contains("gui-source-refused", result.Log);
    }

    [Theory]
    [InlineData("package-auth-required", "remote-package-auth-required")]
    [InlineData("host-launch", "remote-invalid-evidence")]
    public async Task Safe_bound_credential_or_host_launch_failure_is_not_a_base_test_failure(string failure, string reason)
    {
        var transport = new FakeWorkflow { ExecutionFailure = failure };
        var strategy = new XcodeNewTests();
        const string test = "ProbeTests/Probe/testNew()";
        var spec = new TestRunSpec("gate-s6-base", Base, Head, [], [], [], strategy, ".results");
        var report = await Remote(transport).RunExecutionAsync(FakeRepo, Base, Head, spec, [test],
            new Dictionary<string, string> { [test] = "-project\nProbe.xcodeproj\nProbe\niOS" }, null, CancellationToken.None);
        Assert.Equal(TestRunStatus.NoResults, report.Status);
        Assert.Empty(report.Results);
        Assert.Contains(reason, report.Log);
        Assert.Equal(1, transport.Dispatches);
        Assert.Equal(1, transport.ResultReads);
        if (failure == "host-launch")
        {
            var parsed = strategy.ParseResults(FakeWorkflow.HostFailureFiles(test));
            Assert.NotEmpty(parsed.Unreadable);
            Assert.Empty(parsed.Cases);
            Assert.Contains(parsed.Unreadable, error => error.Contains("host", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Safe_environment_seed_requires_a_child_exit_and_commit_owned_inputs()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Seeds", "headless-remote");
        var swift = await File.ReadAllTextAsync(Path.Combine(root, "EnvironmentProcessTests.swift"), TestContext.Current.CancellationToken);
        Assert.Contains("let process = Process()", swift);
        Assert.Contains("try process.run()", swift);
        Assert.Contains("process.waitUntilExit()", swift);
        Assert.Contains("process.terminationStatus", swift);
        foreach (var side in new[] { "base", "head" })
        {
            var recipe = PrepareRecipe.Parse(await File.ReadAllTextAsync(Path.Combine(root, side + "-prepare.yaml"), TestContext.Current.CancellationToken));
            Assert.Equal(side, recipe.TestEnvironment["REPO_MODE"]);
            Assert.Equal("${worktree}/.factory-e2e/probe-env", recipe.TestEnvironment["REPO_HELPER"]);
            Assert.Single(recipe.Steps);
            Assert.Empty(recipe.Artifacts);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unclean")]
    [InlineData("zero")]
    [InlineData("reverse")]
    [InlineData("nonfinite")]
    [InlineData("hash")]
    [InlineData("root")]
    [InlineData("boolean")]
    [InlineData("reconciled-missing")]
    [InlineData("unknown")]
    public void Safe_fake_gui_lease_parser_refuses_unproven_cleanup_intervals_or_workspace_binding(string corruption)
    {
        var text = LeaseArtifact("parser", "base", 1, 2);
        var node = JsonNode.Parse(text)!;
        var lease = node["guiLease"]!;
        switch (corruption)
        {
            case "missing": node.AsObject().Remove("guiLease"); break;
            case "duplicate": text = text.Replace("\"cleaned\":true", "\"cleaned\":true,\"cleaned\":true", StringComparison.Ordinal); break;
            case "unclean": lease["cleaned"] = false; break;
            case "zero": lease["startedUtcSeconds"] = 0; break;
            case "reverse": lease["completedUtcSeconds"] = 0.5; break;
            case "nonfinite": text = text.Replace("\"completedUtcSeconds\":2", "\"completedUtcSeconds\":1e999", StringComparison.Ordinal); break;
            case "hash": lease["workspaceSha256"] = "invalid"; break;
            case "root": node["execution"]!["root"] = "/private/tmp/factory-ios-another/base"; break;
            case "boolean": lease["cleaned"] = "true"; break;
            case "reconciled-missing": lease.AsObject().Remove("reconciled"); break;
            case "unknown": lease["untrusted-extra"] = true; break;
            default: throw new ArgumentException(nameof(corruption));
        }
        if (corruption is not ("duplicate" or "nonfinite")) text = node.ToJsonString();
        Assert.Throws<InvalidDataException>(() => ParseGuiLease(text));
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("reuse")]
    public void Safe_fake_gui_lease_sequence_rejects_overlap_or_reused_base_head_workspace(string corruption)
    {
        var first = ParseGuiLease(LeaseArtifact("first", "base", 1, 3));
        var second = ParseGuiLease(LeaseArtifact(corruption == "reuse" ? "first" : "second", "head", corruption == "overlap" ? 2 : 3, 4));
        Assert.Throws<InvalidDataException>(() => AssertSerializedGuiLeases([first, second]));
    }

    [Fact]
    public void Safe_fake_gui_lease_sequence_accepts_distinct_cleaned_adjacent_execution_intervals()
    {
        var first = ParseGuiLease(LeaseArtifact("first", "base", 1, 2));
        var second = ParseGuiLease(LeaseArtifact("second", "head", 2, 3, reconciled: true));
        Assert.True(second.Reconciled);
        AssertSerializedGuiLeases([second, first]);
    }

    private static string LeaseArtifact(string workspace, string side, double started, double completed, bool reconciled = false)
    {
        var root = "/private/tmp/factory-ios-" + workspace;
        return JsonSerializer.Serialize(new
        {
            execution = new { root = root + "/" + side },
            guiLease = new { workspaceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root))),
                startedUtcSeconds = started, completedUtcSeconds = completed, cleaned = true, reconciled }
        });
    }

    private static readonly IosNewTestsRequest Request = new(Base, Head, ["TallyTests/TallyTests/testNew"]);
    private static RemoteIosNewTests Remote(IIosWorkflowGitHub transport, string? directory = null) => new(transport, directory)
    {
        Timeout = TimeSpan.FromSeconds(2), CleanupTimeout = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(1)
    };

    /// <summary>Safe transport-only fake: starts no process, calls no network, and intentionally supplies no test evidence.</summary>
    private sealed class FakeWorkflow : IIosWorkflowGitHub
    {
        public bool Invisible;
        public bool Gui;
        public bool Completed = true;
        public bool TerminalBeforeReplacement;
        public int Dispatches, Cancels, ResultReads, WorkflowReads;
        public string? ExecutionFailure;
        private IosNewTestsRequest? request;
        private string nonce = "";
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct)
        {
            WorkflowReads++;
            return Task.FromResult<IosSealedWorkflow?>(new("main", Workflow, Gui ? RemoteGuiWorkflow.Text : RemoteIosWorkflow.Text));
        }
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            if (Dispatches > 0) TerminalBeforeReplacement = Completed;
            Dispatches++; nonce = correlation; this.request = request;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IosWorkflowRun>>(Invisible ? [] :
                [new(42, "factory-ios-" + nonce, Workflow, "workflow_dispatch", Gui ? RemoteGuiWorkflow.Path : RemoteIosWorkflow.Path, 1,
                    Completed ? "completed" : "in_progress", Completed ? "success" : null)]);
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested);
            Assert.Equal("factory-ios-" + nonce, run.Title);
            Cancels++; Completed = true;
            return Task.CompletedTask;
        }
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
        {
            ResultReads++;
            if (ExecutionFailure is null) return Task.FromResult("{}");
            using var plan = JsonDocument.Parse(request!.Execution!);
            var side = plan.RootElement.GetProperty("side").GetString();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                version = 1, nonce = correlation, @base = request.Base, head = request.Head, side,
                executionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Execution!))),
                environment = new { runtime = RemoteIosNewTests.SimulatorRuntime, deviceType = RemoteIosNewTests.SimulatorDeviceType, xcode = "16.4", build = "16F6" },
                execution = new
                {
                    root = "/private/tmp/factory-ios-acceptance/" + side, commit = plan.RootElement.GetProperty("commit").GetString(),
                    status = ExecutionFailure == "host-launch" ? "ran" : "prepare-failed", log = "",
                    failure = new { code = ExecutionFailure, step = (int?)null },
                    files = ExecutionFailure == "host-launch" ? HostFailureFiles(request.Tests.Single()) : []
                }
            }));
        }
        public static ResultFile[] HostFailureFiles(string test) =>
        [
            new(XcodeNewTests.OutputName(test), "") { ExitCode = 65 },
            new(XcodeNewTests.JsonName(test), JsonSerializer.Serialize(new
            {
                testNodes = new[] { new { nodeType = "Unit test bundle", name = "ProbeTests", children = new[]
                {
                    new { nodeType = "Test Case", name = "Probe host encountered an error", nodeIdentifier = "Probe host encountered an error", result = "Failed",
                        children = new[] { new { nodeType = "Failure Message", name = "Host application failed to launch" } } }
                } } }
            })) { ExitCode = 0 }
        ];
    }
}
