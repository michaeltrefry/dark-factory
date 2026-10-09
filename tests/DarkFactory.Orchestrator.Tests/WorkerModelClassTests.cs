using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25659 (E8): every worker session names a router model class and pins no model — the implementer and its fix rounds the
/// item's coding class (<c>low</c> for a simple story, <c>mid</c> otherwise), the CI fixer <c>mid</c>, triage <c>low</c>; the class
/// rides in <c>ANTHROPIC_CUSTOM_HEADERS</c> next to the router key and is checkpointed per session.
/// </summary>
public class WorkerModelClassTests
{
    private static WorkStory StoryWith(IReadOnlyList<string>? labels = null, int? estimate = null) =>
        new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77",
            Labels: labels, Estimate: estimate);

    [Theory]
    [InlineData(null, null, "mid")] // no estimate, no label: complex
    [InlineData(null, 1, "low")]
    [InlineData(null, 2, "low")]
    [InlineData(null, 3, "mid")]
    [InlineData(null, 0, "mid")]
    [InlineData(null, 8, "mid")]
    [InlineData("simple", null, "low")]
    [InlineData("Simple", null, "low")]
    [InlineData("simple", 8, "low")] // the label wins over a large estimate
    [InlineData("simpler", 5, "mid")]
    [InlineData("bug,backend", 3, "mid")]
    [InlineData("backend,simple", null, "low")]
    public void A_story_is_simple_with_the_simple_label_or_an_estimate_of_one_or_two_points(string? labels, int? estimate, string expected)
    {
        var story = StoryWith(labels?.Split(','), estimate);

        Assert.Equal(expected, WorkerModelClass.Coding(story));
        Assert.Equal(expected == WorkerModelClass.Low, WorkerModelClass.IsSimple(story));
    }

    [Fact]
    public void The_fixed_roles_have_fixed_classes()
    {
        Assert.Equal("mid", WorkerModelClass.CiFix);
        Assert.Equal("low", WorkerModelClass.Triage);
        Assert.Equal("x-weave-model-class", WorkerModelClass.Header);
    }

    [Theory]
    [InlineData(WorkerAuth.RouterKey, new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS" })]
    [InlineData(WorkerAuth.ClaudeLogin, new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS" })]
    public void The_class_rides_in_the_custom_headers_next_to_the_router_key_and_adds_no_variable(WorkerAuth auth, string[] keys)
    {
        foreach (var modelClass in new[] { "high", "mid", "low" })
        {
            var env = ClaudeWorker.BuildRouterVariables(new Uri("http://localhost:8080/"), "rk_worker", auth, modelClass);

            Assert.Equal(keys, env.Keys.Order());
            Assert.Equal($"X-Weave-Router-Key: rk_worker\nx-weave-model-class: {modelClass}", env["ANTHROPIC_CUSTOM_HEADERS"]);
            // No model pin: no model variable, no force-model header.
            Assert.DoesNotContain(env.Keys, k => k.Contains("MODEL", StringComparison.Ordinal));
            Assert.DoesNotContain(env.Values, v => v.Contains("force-model", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("MID")] // the router reads it case-insensitively, but the factory sends exactly its own values
    [InlineData("ultra")]
    [InlineData("mid\nx-weave-force-model: claude-opus-5-5")] // a second header smuggled in
    [InlineData("mid ")]
    public async Task Anything_but_high_mid_or_low_is_refused_before_a_worker_starts(string modelClass)
    {
        Assert.Throws<ArgumentException>(() => ClaudeWorker.BuildRouterVariables(new Uri("http://localhost:8080/"), "rk", WorkerAuth.RouterKey, modelClass));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ClaudeWorker("/nonexistent/claude", new Uri("http://localhost:8080/"), "rk", WorkerAuth.RouterKey, TimeSpan.FromMinutes(1))
                .RunAsync(Path.GetTempPath(), "p", null, modelClass, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(WorkerAuth.RouterKey, "rk_a\nx-weave-force-model: claude-opus-5-5")]
    [InlineData(WorkerAuth.ClaudeLogin, "rk_a\nx-weave-force-model: claude-opus-5-5")]
    [InlineData(WorkerAuth.ClaudeLogin, "rk_a\rx")]
    public void A_router_key_with_a_line_break_is_refused_in_every_auth_mode(WorkerAuth auth, string key) =>
        Assert.Throws<ArgumentException>(() => ClaudeWorker.BuildRouterVariables(new Uri("http://localhost:8080/"), key, auth, WorkerModelClass.Mid));

    [Fact]
    public void No_session_argument_pins_a_model()
    {
        foreach (var tools in new[] { WorkerTools.Implementer, WorkerTools.ReadOnly })
        {
            var args = ClaudeWorker.BuildArguments("do it", "sess-1", ClaudeWorker.BuildPauseSettings("/tmp/flag"), tools, "/tmp/work");

            Assert.DoesNotContain(args, a => a is "--model" or "--fallback-model" || a.StartsWith("--model=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_helper_block_carries_each_header_on_its_own_line_and_still_refuses_any_other_line_break()
    {
        var variables = ClaudeWorker.BuildRouterVariables(new Uri("http://localhost:8080/"), "rk_worker", WorkerAuth.RouterKey, WorkerModelClass.Low);

        Assert.Equal(
            "ANTHROPIC_BASE_URL=http://localhost:8080\nANTHROPIC_CUSTOM_HEADERS=X-Weave-Router-Key: rk_worker\n"
            + "ANTHROPIC_CUSTOM_HEADERS=x-weave-model-class: low\nANTHROPIC_AUTH_TOKEN=rk_worker\n\n",
            WorkerSandbox.BuildVariableBlock(variables));
        // An empty header line would end the block early; a carriage return or a line break elsewhere is refused as before.
        Assert.Throws<ArgumentException>(() => WorkerSandbox.BuildVariableBlock(new Dictionary<string, string> { ["ANTHROPIC_CUSTOM_HEADERS"] = "a: 1\n\nPATH=/evil" }));
        Assert.Throws<ArgumentException>(() => WorkerSandbox.BuildVariableBlock(new Dictionary<string, string> { ["ANTHROPIC_CUSTOM_HEADERS"] = "a: 1\r\nb: 2" }));
        Assert.Throws<ArgumentException>(() => WorkerSandbox.BuildVariableBlock(new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "http://x\nPATH=/evil" }));
    }

    [Theory]
    [InlineData(true, "API Error: 503 {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"model_class_unavailable: no model of class mid can serve this request\"}}", "", true)]
    [InlineData(true, "failed", "Error: model_class_unavailable: no model of class low can serve this request", true)]
    [InlineData(false, "The router answers model_class_unavailable when no model of the class can serve; I documented it.", "", false)] // prose
    [InlineData(true, "error_max_turns", "", false)]
    public void Model_class_unavailable_is_recognised_only_in_an_error_result_or_stderr(bool isError, string resultText, string stderr, bool expected)
    {
        var result = new WorkerResult("s1", 1, isError, "success", resultText, stderr);

        Assert.Equal(expected, result.ModelClassUnavailable);
        Assert.Equal(expected ? Controls.UsagePause.WorkerModelClassUnavailable : null, RunPipeline.UsagePauseReason(result));
        // A succeeded session never counts, whatever it says.
        Assert.False(new WorkerResult("s1", 0, false, "success", resultText, stderr).ModelClassUnavailable);
    }

    [Theory]
    [InlineData(new[] { "simple" }, null, "low")]
    [InlineData(new string[0], 2, "low")]
    [InlineData(new string[0], 3, "mid")]
    [InlineData(new string[0], null, "mid")]
    public async Task The_implementer_and_its_review_fix_round_run_on_the_items_coding_class_and_each_session_is_checkpointed(
        string[] labels, int? estimate, string expected)
    {
        var h = new Harness
        {
            Stories = new FakeWorkSource(StoryWith(labels, estimate)),
            Reviewer = new FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness && r.Pull.HeadSha == Sha1
                    ? [new Finding(Finding.Blocking, "empty input crashes", "src/x.cs", 3, "it is wrong")]
                    : [],
            },
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains(WorkState.Fixing, await h.Transitions());
        Assert.Equal([expected, expected], h.WorkerCalls.Select(c => c.ModelClass)); // the implementer, then the fixer
        var rows = await h.Rows();
        Assert.Equal([expected, expected], rows.Where(r => r.Step == RunPipeline.Steps.ModelClass).Select(r => r.Detail));
        // Each class row names its session and follows that session's start.
        Assert.All(rows.Where(r => r.Step == RunPipeline.Steps.ModelClass), r => Assert.NotNull(r.ClaudeSessionId));
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.Session) < rows.FindIndex(r => r.Step == RunPipeline.Steps.ModelClass));
    }

    [Fact]
    public async Task The_ci_fixer_runs_on_mid_even_for_a_simple_story()
    {
        var h = new Harness { Stories = new FakeWorkSource(StoryWith(["simple"])) };
        h.GitHub.Ci[Sha1] = new CiFacts(Sha1, [new CheckFact("build-test", true, "failure", 101)]);
        h.GitHub.Logs["build-test"] = "##[error]Process completed with exit code 1.";

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains(WorkState.CIHealing, await h.Transitions());
        Assert.Equal([WorkerModelClass.Low, WorkerModelClass.Mid], h.WorkerCalls.Select(c => c.ModelClass));
        Assert.Equal(["low", "mid"], (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.ModelClass).Select(r => r.Detail));
    }
}
