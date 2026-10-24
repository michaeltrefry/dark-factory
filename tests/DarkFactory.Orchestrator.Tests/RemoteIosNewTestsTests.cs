using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosNewTestsTests
{
    private static readonly RepoRef Repo = new("michaeltrefry", "dark-factory-sandbox");
    private const string Base = "1111111111111111111111111111111111111111";
    private const string Head = "2222222222222222222222222222222222222222";
    private const string WorkflowSha = "3333333333333333333333333333333333333333";
    private static readonly IosNewTestsRequest Request = new(Base, Head, ["TallyTests/TallyTests/testCounterStartsAtZero"]);

    [Theory]
    [InlineData("failed")]
    [InlineData("not-built")]
    public async Task Dispatch_reads_each_new_tests_base_and_head_from_the_recorded_run(string baseOutcome)
    {
        var github = new FakeWorkflow { BaseOutcome = baseOutcome };
        var result = await new RemoteIosNewTests(github) { PollInterval = TimeSpan.Zero }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("pass", result.Outcome);
        var test = Assert.Single(result.Tests);
        Assert.Equal(baseOutcome, test.Base);
        Assert.Equal("passed", test.Head);
        Assert.Equal(1, github.Dispatches);
        Assert.Equal(WorkflowSha, github.DispatchedWorkflow!.Commit);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("modified")]
    [InlineData("bad-sha")]
    public async Task An_untrusted_default_workflow_refuses_before_dispatch(string kind)
    {
        var github = new FakeWorkflow { Invalid = kind };
        var result = await new RemoteIosNewTests(github).RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("error", result.Outcome);
        Assert.Equal(0, github.Dispatches);
        Assert.Equal(0, github.ResultReads);
    }

    [Fact]
    public async Task No_gate_write_authorization_is_error_and_writes_nothing()
    {
        var github = new FakeWorkflow { Authorized = false };
        var result = await new RemoteIosNewTests(github) { CleanupTimeout = TimeSpan.FromMilliseconds(20) }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("error", result.Outcome);
        Assert.Equal(0, github.Dispatches);
        Assert.Equal(0, github.ResultReads);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("event")]
    [InlineData("attempt")]
    [InlineData("path")]
    [InlineData("nonce")]
    [InlineData("tests")]
    [InlineData("base")]
    [InlineData("outcome")]
    public async Task Wrong_run_or_result_provenance_never_passes(string corrupt)
    {
        var github = new FakeWorkflow { Corrupt = corrupt };
        var result = await new RemoteIosNewTests(github) { PollInterval = TimeSpan.Zero, CleanupTimeout = TimeSpan.FromMilliseconds(20) }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("error", result.Outcome);
    }

    [Theory]
    [InlineData("passed", "passed")]
    [InlineData("skipped", "passed")]
    [InlineData("failed", "failed")]
    public async Task Inadequate_executed_evidence_is_rejected(string baseOutcome, string headOutcome)
    {
        var github = new FakeWorkflow { BaseOutcome = baseOutcome, HeadOutcome = headOutcome };
        var result = await new RemoteIosNewTests(github) { PollInterval = TimeSpan.Zero }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("rejected", result.Outcome);
    }

    [Fact]
    public async Task A_never_reported_run_is_a_bounded_error_not_a_success()
    {
        var github = new FakeWorkflow { Never = true };
        var result = await new RemoteIosNewTests(github) { Timeout = TimeSpan.FromMilliseconds(20), CleanupTimeout = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.Zero }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("error", result.Outcome);
        Assert.Equal(1, github.Dispatches);
        Assert.Equal(0, github.ResultReads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("runtime")]
    [InlineData("deviceType")]
    [InlineData("xcode")]
    [InlineData("build")]
    [InlineData("missing-build")]
    [InlineData("numeric-xcode")]
    public async Task An_unvalidated_environment_is_not_executed_evidence(string corrupt)
    {
        var github = new FakeWorkflow { EnvironmentCorrupt = corrupt };
        var result = await new RemoteIosNewTests(github) { PollInterval = TimeSpan.Zero }.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("error", result.Outcome);
    }

    internal const string EnvironmentJson = """{"runtime":"com.apple.CoreSimulator.SimRuntime.iOS-18-5","deviceType":"com.apple.CoreSimulator.SimDeviceType.iPhone-16","xcode":"16.4","build":"16F6"}""";

    internal static string WithEnvironment(string json, string? corrupt = null)
    {
        var result = JsonNode.Parse(json)!.AsObject();
        var environment = JsonNode.Parse(EnvironmentJson)!.AsObject();
        if (corrupt is "runtime" or "deviceType") environment[corrupt] = "available-fallback";
        if (corrupt is "xcode" or "build") environment[corrupt] = " ";
        if (corrupt == "different-xcode") environment["xcode"] = "16.3";
        if (corrupt == "different-build") environment["build"] = "16F7";
        if (corrupt == "missing-build") environment.Remove("build");
        if (corrupt == "numeric-xcode") environment["xcode"] = 16;
        if (corrupt != "missing") result["environment"] = corrupt switch {
            "null" => null, "array" => new JsonArray(), _ => environment
        };
        return result.ToJsonString();
    }

    internal sealed class FakeWorkflow : IIosWorkflowGitHub
    {
        public string? Invalid, Corrupt, EnvironmentCorrupt;
        public bool Authorized = true, Never;
        public string BaseOutcome = "failed", HeadOutcome = "passed";
        public int Dispatches, ResultReads;
        public IosSealedWorkflow? DispatchedWorkflow;
        private string nonce = "";
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) =>
            Task.FromResult(Invalid == "missing" ? null : new IosSealedWorkflow("main", Invalid == "bad-sha" ? "main" : WorkflowSha,
                RemoteIosWorkflow.Text + (Invalid == "modified" ? "\n# changed" : "")));
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            if (!Authorized) throw new InvalidOperationException("Gate App has no Actions write authorization for this repository.");
            Dispatches++;
            DispatchedWorkflow = workflow;
            nonce = correlation;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
        {
            if (Never) return Task.FromResult<IReadOnlyList<IosWorkflowRun>>([]);
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ios-remote", "workflow-run.json"));
            // Hosted sandbox CI run recorded read-only on 2026-10-23. The fake dispatch substitutes its own
            // identity (title, SHA, event, path); attempt/status/conclusion remain the real GitHub shape.
            // This is NOT evidence of a live factory iOS run: that campaign belongs to S6.
            var run = JsonSerializer.Deserialize<IosWorkflowRun>(json)!;
            return Task.FromResult<IReadOnlyList<IosWorkflowRun>>([run with {
                Title = "factory-ios-" + nonce,
                HeadSha = Corrupt == "sha" ? Head : WorkflowSha,
                Event = Corrupt == "event" ? "pull_request" : "workflow_dispatch",
                Attempt = Corrupt == "attempt" ? 2 : run.Attempt,
                Path = Corrupt == "path" ? "other.yml" : RemoteIosWorkflow.Path,
            }]);
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => Task.CompletedTask;
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct)
        {
            ResultReads++;
            return Task.FromResult(WithEnvironment(JsonSerializer.Serialize(new {
                version = 1, nonce = Corrupt == "nonce" ? "other" : nonce,
                @base = Corrupt == "base" ? Head : Base, head = Head,
                tests = new[] { new { test = Corrupt == "tests" ? "Other/test" : Request.Tests[0],
                    @base = Corrupt == "outcome" ? "invented" : BaseOutcome, head = HeadOutcome } }
            }), EnvironmentCorrupt));
        }
    }
}
