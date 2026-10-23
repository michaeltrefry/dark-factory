using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class RemoteIosGateTests
{
    internal const string Base = "1111111111111111111111111111111111111111";
    internal const string Head = "2222222222222222222222222222222222222222";
    internal const string Workflow = "3333333333333333333333333333333333333333";
    private static readonly RepoRef Repo = new("acme", "not-the-skeleton");

    internal static FakeTestRunner Sources(bool ios = true)
    {
        var runner = CargoIdentifyTests.Runner(ios ? XcodeIosFixture.Files() : XcodeFixture.Files());
        runner.BaseCommit = Base;
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? "passed" : "failed");
        return CargoIdentifyTests.With(runner, ios ? XcodeIosFixture.Head : [("WordsTests/New.swift", "import Testing\n@Test func newTest() { #expect(false) }\n")]);
    }

    internal sealed class Dispatch : IIosWorkflowGitHub
    {
        public readonly List<IosNewTestsRequest> Requests = [];
        public string Correlation = "";
        public string? Corrupt;
        public bool Never;
        public bool BasePass;
        public bool Wait;
        public bool FirstBaseBuildFails;
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) => Task.FromResult<IosSealedWorkflow?>(new("main", Workflow, RemoteIosWorkflow.Text));
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            Requests.Add(request); Correlation = correlation; return Task.CompletedTask;
        }
        public async Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
        {
            if (Wait) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Never ? [] : [new(42, "factory-ios-" + Correlation, Workflow, "workflow_dispatch", RemoteIosWorkflow.Path, 1, "completed", "success")];
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => Task.CompletedTask;
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
        {
            if (Corrupt == "json") return Task.FromResult("not json");
            var request = Requests[^1];
            using var execution = JsonDocument.Parse(request.Execution!);
            var plan = execution.RootElement;
            var side = plan.GetProperty("side").GetString()!;
            var result = side == "base" && !BasePass ? "Failed" : "Passed";
            var files = request.Tests.SelectMany(test => new[] {
                new ResultFile(XcodeNewTests.OutputName(test), "") { ExitCode = result == "Failed" ? 65 : 0 },
                new ResultFile(XcodeNewTests.JsonName(test), JsonSerializer.Serialize(new { testNodes = new[] {
                    new { nodeType = "Unit test bundle", name = test.Split('/')[0], children = new[] {
                        new { nodeType = "Test Case", nodeIdentifier = string.Join('/', test.Split('/').Skip(1)), result }
                    } }
                } })) { ExitCode = 0 }
            }).ToArray();
            var buildFailed = FirstBaseBuildFails && Requests.Count == 1;
            if (Corrupt == "missing" || buildFailed) files = [];
            return Task.FromResult(JsonSerializer.Serialize(new {
                version = 1, nonce = correlation, @base = request.Base, head = request.Head, side,
                environment = new { runtime = RemoteIosNewTests.SimulatorRuntime, deviceType = RemoteIosNewTests.SimulatorDeviceType, xcode = "16.4", build = "16F6" },
                executionHash = Corrupt == "hash" ? "wrong" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Execution!))),
                execution = new { commit = plan.GetProperty("commit").GetString(), root = "/private/tmp/factory-ios-test/" + side,
                    status = buildFailed ? "build-failed" : "ran", log = buildFailed ? "/private/tmp/factory-ios-test/base/TallyTests/IncrementTests.swift:6:9: error: cannot find 'newSymbol' in scope\n" : "", files }
            }));
        }
    }

    private static Task<NewTestsResult> Run(FakeTestRunner source, Dispatch dispatch, TimeSpan? timeout = null) =>
        NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), Repo, Base, Head, "gate-sc-1", null, CancellationToken.None,
            remoteIos: new RemoteIosNewTests(dispatch) { PollInterval = TimeSpan.Zero, CleanupTimeout = TimeSpan.FromMilliseconds(20), Timeout = timeout ?? (dispatch.Never ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromMinutes(1)) });

    [Fact]
    public async Task All_discovered_ios_unit_and_ui_tests_run_remotely_with_the_exact_test_overlay()
    {
        var source = Sources();
        var dispatch = new Dispatch();
        var result = await Run(source, dispatch);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(2, result.Tests.Count);
        Assert.Empty(source.Runs);
        Assert.Equal(2, dispatch.Requests.Count);
        using var plan = JsonDocument.Parse(dispatch.Requests[0].Execution!);
        Assert.Equal("base", plan.RootElement.GetProperty("side").GetString());
        Assert.Equal(Base, plan.RootElement.GetProperty("commit").GetString());
        Assert.Equal(Head, plan.RootElement.GetProperty("overlayFrom").GetString());
        Assert.Equal(new[] { XcodeIosFixture.UnitTestFile, XcodeIosFixture.UiTestFile }.Order(),
            plan.RootElement.GetProperty("overlayPaths").EnumerateArray().Select(x => x.GetString()).Order());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("missing")]
    [InlineData("hash")]
    public async Task Unreadable_incomplete_or_unbound_remote_evidence_is_error(string corrupt)
    {
        var source = Sources();
        var result = await Run(source, new Dispatch { Corrupt = corrupt });
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Empty(source.Runs);
    }

    [Fact]
    public async Task A_never_reported_dispatch_and_a_remote_timeout_are_errors()
    {
        Assert.Equal(NewTestsOutcome.Error, (await Run(Sources(), new Dispatch { Never = true })).Outcome);
        Assert.Equal(NewTestsOutcome.Error, (await Run(Sources(), new Dispatch { Wait = true }, timeout: TimeSpan.FromMilliseconds(20))).Outcome);
    }

    [Fact]
    public async Task A_differently_named_nested_project_and_nested_swift_testing_suite_are_not_limited_to_the_skeleton()
    {
        var original = Sources();
        var source = new FakeTestRunner { BaseCommit = Base };
        source.BaseFiles.Clear();
        foreach (var (path, text) in original.BaseFiles) source.BaseFiles.Add("Apps/" + path.Replace("Tally", "LedgerApp"), text.Replace("Tally", "LedgerApp"));
        var head = original.HeadFiles(Head).ToDictionary(p => "Apps/" + p.Key.Replace("Tally", "LedgerApp"), p => p.Value.Replace("Tally", "LedgerApp"));
        head["Apps/LedgerAppTests/Nested.swift"] = "import Testing\nstruct Outer { struct Inner { @Test(arguments: [1, 2]) func nested(_ n: Int) { #expect(n > 0) } } }\n";
        source.HeadFiles = _ => head;
        var dispatch = new Dispatch();
        var result = await Run(source, dispatch);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(3, result.Tests.Count);
        Assert.Contains(result.Tests, t => t.Test.StartsWith("LedgerAppTests/Outer/Inner/nested", StringComparison.Ordinal));
        Assert.Empty(source.Runs);
    }

    [Theory]
    [InlineData("xunit")]
    [InlineData("cargo")]
    [InlineData("swiftpm")]
    public async Task Other_test_stacks_are_unchanged_with_remote_routing_present(string stack)
    {
        var source = stack switch {
            "xunit" => new FakeTestRunner(),
            "cargo" => CargoIdentifyTests.With(CargoIdentifyTests.Runner(("Cargo.toml", "[package]\nname = \"words\"\nversion = \"0.1.0\"\n"), ("src/lib.rs", "pub fn answer() -> i32 { 0 }\n")),
                ("src/lib.rs", "pub fn answer() -> i32 { 1 }\n#[cfg(test)] mod tests { #[test] fn correct() { assert_eq!(super::answer(), 1); } }\n")),
            _ => SwiftIdentifyTests.Runner(("Tests/WordsTests/New.swift", "import Testing\n@Test func newTest() { #expect(false) }\n"))
        };
        source.BaseCommit = Base;
        source.Answer = spec => stack == "cargo" ? CargoIdentifyTests.Report(spec, spec.OverlayFrom is null ? "passed" : "failed")
            : FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? "passed" : "failed");
        var dispatch = new Dispatch();
        var result = await Run(source, dispatch);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(2, source.Runs.Count);
        Assert.Empty(dispatch.Requests);
    }

    [Fact]
    public async Task A_test_that_already_passes_on_the_base_is_rejected()
    {
        var result = await Run(Sources(), new Dispatch { BasePass = true });
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.All(result.Tests, test => Assert.Equal("passed", test.Base));
    }

    [Fact]
    public async Task Remote_compiler_attribution_retries_the_other_tests_without_the_test_that_does_not_build()
    {
        var source = Sources();
        var dispatch = new Dispatch { FirstBaseBuildFails = true };
        var result = await Run(source, dispatch);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(3, dispatch.Requests.Count);
        Assert.Equal("not-built", result.Tests.Single(t => t.Test == XcodeIosFixture.UnitTestId).Base);
        Assert.Equal("failed", result.Tests.Single(t => t.Test == XcodeIosFixture.UiTestId).Base);
        Assert.Equal("ran", result.BaseRetry);
        using var retry = JsonDocument.Parse(dispatch.Requests[2].Execution!);
        var replacement = retry.RootElement.GetProperty("replacements").GetProperty(XcodeIosFixture.UnitTestFile).GetString()!;
        Assert.DoesNotContain("testNextAddsOne", replacement);
        Assert.Empty(source.Runs);
    }

    [Fact]
    public async Task The_real_pipeline_records_remote_per_test_evidence_and_reuses_the_exact_base_head_after_interruption()
    {
        var dispatch = new Dispatch();
        var h = new GatePipelineTests.Harness { Swift = true,
            RemoteIos = new RemoteIosNewTests(dispatch) { PollInterval = TimeSpan.Zero } };
        var source = Sources();
        h.TestRunner.BaseFiles.Clear();
        foreach (var pair in source.BaseFiles) h.TestRunner.BaseFiles.Add(pair.Key, pair.Value);
        h.TestRunner.BaseCommit = Base;
        h.TestRunner.HeadFiles = source.HeadFiles;
        h.GitHub.Head = Head;
        h.GitHub.CurrentBaseSha = Base;
        h.GitHub.ExpectedPolicySha = Base;
        using var interruption = new CancellationTokenSource();
        h.GitHub.MergeThrows = new OperationCanceledException(interruption.Token);
        h.GitHub.OnMergeCall = interruption.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(ct: interruption.Token));
        Assert.Equal(2, dispatch.Requests.Count);
        var row = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.NewTests);
        var evidence = NewTestsResult.FromDetail(row.Detail)!;
        Assert.Equal(NewTestsOutcome.Pass, evidence.Outcome);
        Assert.Equal(2, evidence.Tests.Count);
        (h.GitHub.MergeThrows, h.GitHub.OnMergeCall) = (null, null);
        var resumed = await h.Run();
        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal(2, dispatch.Requests.Count);
        Assert.Empty(h.TestRunner.Runs);
        Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.NewTests);
    }

    [Fact]
    public async Task Macos_xcode_tests_stay_local_with_remote_routing_present()
    {
        var source = Sources(false);
        var dispatch = new Dispatch();
        var result = await Run(source, dispatch);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(2, source.Runs.Count);
        Assert.Empty(dispatch.Requests);
    }
}
