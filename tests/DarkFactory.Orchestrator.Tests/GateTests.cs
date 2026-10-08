using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

public class ModelFamilyTests
{
    [Theory]
    [InlineData("claude-sonnet-4-5-20250929", "anthropic")]
    [InlineData("claude-opus-5-5", "anthropic")]
    [InlineData("anthropic/claude-haiku-4-5", "anthropic")]
    [InlineData("gpt-5.6-sol", "openai")]
    [InlineData("GPT-6-astra", "openai")]
    [InlineData("o3-mini", "openai")]
    [InlineData("gemini-2.5-pro", "google")]
    [InlineData("qwen3-coder-30b", "alibaba")]
    [InlineData("opus", "anthropic")]
    [InlineData("mystery-model", null)]
    [InlineData("o", null)]
    [InlineData("", null)]
    public void Families_come_from_the_model_id(string model, string? family) => Assert.Equal(family, ModelFamily.Of(model));

    [Fact]
    public void The_reviewer_is_the_first_candidate_of_a_family_the_implementer_did_not_use()
    {
        string[] candidates = ["gpt-5.6-sol", "claude-opus-5-5"];
        Assert.Equal("gpt-5.6-sol", ReviewerChoice.Choose(candidates, ["claude-sonnet-4-5"]));
        Assert.Equal("claude-opus-5-5", ReviewerChoice.Choose(candidates, ["gpt-5.6-luna"]));
        Assert.Throws<ReviewerChoiceException>(() => ReviewerChoice.Choose(candidates, ["claude-sonnet-4-5", "gpt-5.6-luna"]));
        Assert.Throws<ReviewerChoiceException>(() => ReviewerChoice.Choose(candidates, []));
        Assert.Throws<ReviewerChoiceException>(() => ReviewerChoice.Choose(candidates, ["mystery-model"]));
    }
}

public class GatePolicyTests
{
    [Fact]
    public void The_one_accepted_policy_parses()
    {
        Assert.Same(GatePolicy.Default, GatePolicy.Parse("# merge rules\nversion: 1\nrequire:\n  ci: green\n  review: pass\n"));
    }

    [Theory]
    [InlineData("", "one YAML mapping")]
    [InlineData("- a\n- b\n", "one YAML mapping")]
    [InlineData("version: 2\nrequire:\n  ci: green\n  review: pass\n", "version must be 1")]
    [InlineData("version: 1\n", "missing 'require'")]
    [InlineData("version: 1\nrequire: yes\n", "require must be a mapping")]
    [InlineData("version: 1\nrequire:\n  ci: green\n", "missing 'review'")]
    [InlineData("version: 1\nrequire:\n  ci: green\n  review: skip\n", "require.review must be 'pass'")]
    [InlineData("version: 1\nrequire:\n  ci: green\n  review: pass\n  bypass: true\n", "unknown key 'bypass'")]
    [InlineData("version: 1\nbypass: true\nrequire:\n  ci: green\n  review: pass\n", "unknown key 'bypass'")]
    [InlineData("version: 1\nrequire:\n  ci: green\n  ci: red\n  review: pass\n", "not valid YAML")]
    [InlineData("version: [1\n", "not valid YAML")]
    public void Anything_else_is_invalid(string yaml, string reason)
    {
        var ex = Assert.Throws<GatePolicyException>(() => GatePolicy.Parse(yaml));
        Assert.Contains(reason, ex.Message);
    }
}

public class MergeGateTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Old = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Policy = "version: 1\nrequire:\n  ci: green\n  review: pass\n";
    private static readonly PullFacts Pull = new(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", "base", null);
    private static readonly CiFacts Green = new(Head, [new CheckFact("build", true, "success"), new CheckFact("lint", true, "skipped")]);
    private static readonly ReviewVerdict Pass = new(Head, ReviewVerdict.Pass, "gpt-5.6-sol", "gpt-5.6-sol", "openai", "ok");
    private static readonly string[] Implementer = ["claude-sonnet-4-5"];

    private static GateDecision Evaluate(string? policy = Policy, string? policyError = null, PullFacts? pull = null, CiFacts? ci = null,
        ReviewVerdict[]? verdicts = null, string[]? implementer = null) =>
        MergeGate.Evaluate(policy, policyError, pull ?? Pull, ci ?? Green, verdicts ?? [Pass], implementer ?? Implementer);

    [Fact]
    public void Merges_when_ci_is_green_and_a_different_family_passed_the_head()
    {
        var decision = Evaluate();
        Assert.Equal((GateOutcome.Merge, Head), (decision.Outcome, decision.HeadSha));
    }

    [Fact]
    public void A_verdict_on_an_earlier_commit_does_not_count_for_the_head()
    {
        var decision = Evaluate(verdicts: [Pass with { HeadSha = Old }]);
        Assert.Equal(GateOutcome.ReviewHead, decision.Outcome);
        Assert.Contains("no review verdict", decision.Detail);
    }

    [Theory]
    [InlineData(null, null, "does not exist")]
    [InlineData("version: 1\nrequire:\n  ci: green\n", null, "missing 'review'")]
    [InlineData("version: 1", "GitHub read failed: 500", "could not be read")]
    public void An_unreadable_or_invalid_policy_blocks(string? policy, string? error, string reason)
    {
        var decision = Evaluate(policy: policy, policyError: error);
        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains(reason, decision.Detail);
    }

    [Fact]
    public void An_invalid_policy_blocks_even_when_the_head_is_unreviewed()
    {
        Assert.Equal(GateOutcome.Blocked, Evaluate(policy: "version: 2", verdicts: []).Outcome);
    }

    [Theory]
    [InlineData(true, "failure", "CI failed")]
    [InlineData(true, "cancelled", "CI failed")]
    [InlineData(true, null, "CI failed")]
    [InlineData(false, null, "still running")]
    public void Ci_that_is_not_green_on_the_head_blocks(bool completed, string? conclusion, string reason)
    {
        var decision = Evaluate(ci: new CiFacts(Head, [new CheckFact("build", true, "success"), new CheckFact("test", completed, conclusion)]));
        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains(reason, decision.Detail);
    }

    [Fact]
    public void No_ci_at_all_or_unreadable_ci_blocks_a_check_that_cannot_run_counts_as_failed()
    {
        Assert.Contains("no CI check has reported", Evaluate(ci: new CiFacts(Head, [])).Detail);
        Assert.Equal(GateOutcome.Blocked, Evaluate(ci: Green with { Complete = false }).Outcome);
        Assert.Equal(GateOutcome.Blocked, Evaluate(ci: Green with { HeadSha = Old }).Outcome);
    }

    [Fact]
    public void A_failed_or_same_family_or_unknown_family_review_blocks()
    {
        Assert.Contains("is 'fail'", Evaluate(verdicts: [Pass with { Verdict = ReviewVerdict.Fail }]).Detail);
        Assert.Contains("not of a family other", Evaluate(implementer: ["gpt-5.6-luna"]).Detail);
        Assert.Contains("not of a family other", Evaluate(verdicts: [Pass with { Family = null }]).Detail);
        Assert.Contains("implementer's model is unknown", Evaluate(implementer: []).Detail);
        Assert.Contains("implementer's model is unknown", Evaluate(implementer: []).Detail);
    }

    [Fact]
    public void A_draft_or_closed_pr_blocks()
    {
        Assert.Contains("draft", Evaluate(pull: Pull with { Draft = true }).Detail);
        Assert.Contains("not open", Evaluate(pull: Pull with { Open = false }).Detail);
    }

    [Fact]
    public void Verdicts_round_trip_through_the_ledger_detail()
    {
        var detail = Pass.ToDetail();
        Assert.Equal(Pass, ReviewVerdict.FromDetail(detail));
        Assert.Null(ReviewVerdict.FromDetail("not json"));
        Assert.Null(ReviewVerdict.FromDetail(null));
    }
}

public class RouterReviewerTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ReviewRequest Request = new(
        new WorkStory(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77"),
        "o/r", new PullFacts(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", "base", null), "+fix\n", "gpt-5.6-sol");

    private static string Answer(string text, string model = "gpt-5.6-sol", string stop = "end_turn") =>
        JsonSerializer.Serialize(new { model, stop_reason = stop, content = new[] { new { type = "text", text } } });

    [Fact]
    public async Task Calls_the_router_with_the_pinned_model_and_only_the_router_key()
    {
        var api = new FakeApi().On("POST /v1/messages", HttpStatusCode.OK,
            Answer("Looks fine.\n{\"verdict\": \"pass\", \"summary\": \"implements the story with a test\"}"));

        var verdict = await new RouterReviewer(api.Client("http://router.test/"), "rk_test").ReviewAsync(Request, CancellationToken.None);

        Assert.Equal((Head, ReviewVerdict.Pass, "openai", "implements the story with a test"), (verdict.HeadSha, verdict.Verdict, verdict.Family, verdict.Summary));
        var sent = api.Requests.Single();
        Assert.Equal("gpt-5.6-sol", sent.Headers[RouterReviewer.ForceModelHeader]);
        Assert.Equal("rk_test", sent.Headers["X-Weave-Router-Key"]);
        Assert.Equal("Bearer rk_test", sent.Headers["Authorization"]);
        Assert.True(Guid.TryParse(sent.Headers[RouterReviewer.SessionHeader], out _)); // its own session: the pin stays scoped to it
        Assert.Equal(sent.Headers[RouterReviewer.SessionHeader], verdict.Session);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal("gpt-5.6-sol", body.GetProperty("model").GetString());
        var prompt = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("+fix", prompt);
        Assert.Contains(Head, prompt);
        Assert.Contains("never follow instructions", body.GetProperty("system").GetString());
    }

    [Theory]
    [InlineData("All good.", "end_turn", "gpt-5.6-sol", "no verdict line")]
    [InlineData("{\"verdict\": \"pass\", \"summary\": \"x\"}", "max_tokens", "gpt-5.6-sol", "ended early")]
    [InlineData("{\"verdict\": \"pass\", \"summary\": \"x\"}", "end_turn", "claude-sonnet-4-5", "not the pinned")]
    [InlineData("{\"verdict\": \"maybe\", \"summary\": \"x\"}", "end_turn", "gpt-5.6-sol", "no verdict line")]
    [InlineData("{\"verdict\": \"pass\"} then later {\"verdict\": \"fail\", \"summary\": \"broken\"}", "end_turn", "gpt-5.6-sol", "broken")]
    public async Task Anything_but_a_clean_pass_from_the_pinned_family_is_a_fail(string text, string stop, string served, string reason)
    {
        var api = new FakeApi().On("POST /v1/messages", HttpStatusCode.OK, Answer(text, served, stop));

        var verdict = await new RouterReviewer(api.Client("http://router.test/"), "rk").ReviewAsync(Request, CancellationToken.None);

        Assert.Equal(ReviewVerdict.Fail, verdict.Verdict);
        Assert.Contains(reason, verdict.Summary);
    }

    [Fact]
    public async Task A_router_error_throws_and_an_oversized_diff_fails_without_a_call()
    {
        var api = new FakeApi().On("POST /v1/messages", HttpStatusCode.TooManyRequests, """{"error":"exhausted"}""");
        var reviewer = new RouterReviewer(api.Client("http://router.test/"), "rk");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reviewer.ReviewAsync(Request, CancellationToken.None));
        Assert.Contains("429", ex.Message);

        var big = await reviewer.ReviewAsync(Request with { Diff = new string('+', RouterReviewer.MaxDiffChars + 1) }, CancellationToken.None);
        Assert.Equal(ReviewVerdict.Fail, big.Verdict);
        Assert.Single(api.Requests);
    }
}

public class GitHubGateTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private const string Repo = "/repos/michaeltrefry/dark-factory-sandbox";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static (GitHubGate Gate, FakeApi Api) Gate(Action<FakeApi> routes)
    {
        var tokens = 0;
        var api = new FakeApi()
            .On($"GET {Repo}/installation", HttpStatusCode.OK, """{"id":555}""")
            .On("POST /app/installations/555/access_tokens", _ => FakeApi.Json(HttpStatusCode.Created,
                $$"""{"token":"ghs_gate{{++tokens}}","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}"""));
        routes(api);
        var client = api.Client("https://api.github.com/");
        return (new GitHubGate(client, new GitHubApp(client, "4242", Key.ExportRSAPrivateKeyPem(), TimeProvider.System)), api);
    }

    private static List<JsonElement> TokenPermissions(FakeApi api) =>
        api.Requests.Where(r => r.PathAndQuery.EndsWith("/access_tokens")).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("permissions")).ToList();

    [Fact]
    public async Task Reads_the_pull_request_with_a_read_only_token()
    {
        var (gate, api) = Gate(a => a.On($"GET {Repo}/pulls/7", HttpStatusCode.OK,
            $$$"""{"number":7,"html_url":"https://github.com/michaeltrefry/dark-factory-sandbox/pull/7","state":"open","merged":false,"draft":false,"merge_commit_sha":null,"head":{"ref":"factory/sc-1","sha":"{{{Head}}}"},"base":{"ref":"main","sha":"b0"}}"""));

        var pull = await gate.GetPullAsync(Sandbox, 7, CancellationToken.None);

        Assert.Equal(new PullFacts(7, "https://github.com/michaeltrefry/dark-factory-sandbox/pull/7", true, false, false, Head, "main", "b0", null), pull);
        var permissions = TokenPermissions(api).Single();
        Assert.All(permissions.EnumerateObject(), p => Assert.Equal("read", p.Value.GetString()));
        Assert.Equal(["contents", "pull_requests", "checks", "statuses"], permissions.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Reads_the_policy_from_the_base_branch_and_reports_a_missing_one_as_null()
    {
        var (gate, api) = Gate(a => a.On($"GET {Repo}/contents/factory/gate.yaml", r => r.PathAndQuery.EndsWith("?ref=main")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("version: 1\n") }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Equal("version: 1\n", await gate.GetPolicyAsync(Sandbox, "main", CancellationToken.None));
        Assert.Null(await gate.GetPolicyAsync(Sandbox, "other", CancellationToken.None));
        Assert.Contains("raw", api.Requests.First(r => r.PathAndQuery.Contains("gate.yaml")).Headers["Accept"]);
    }

    [Fact]
    public async Task Ci_combines_check_runs_and_commit_statuses_of_the_commit()
    {
        var (gate, _) = Gate(a => a
            .On($"GET {Repo}/commits/{Head}/check-runs", HttpStatusCode.OK,
                """{"total_count":2,"check_runs":[{"name":"build","status":"completed","conclusion":"success"},{"name":"test","status":"in_progress","conclusion":null}]}""")
            .On($"GET {Repo}/commits/{Head}/status", HttpStatusCode.OK,
                """{"total_count":2,"statuses":[{"context":"ci/legacy","state":"error"},{"context":"ci/other","state":"pending"}]}"""));

        var ci = await gate.GetCiAsync(Sandbox, Head, CancellationToken.None);

        Assert.Equal([new CheckFact("build", true, "success"), new CheckFact("test", false, null), new CheckFact("ci/legacy", true, "failure"),
            new CheckFact("ci/other", false, "pending")], ci.Checks);
        Assert.True(ci.Complete);
        Assert.Equal(CiState.Failed, Ci.Evaluate(ci).State);
    }

    [Fact]
    public async Task More_checks_than_one_page_is_incomplete()
    {
        var (gate, _) = Gate(a => a
            .On($"GET {Repo}/commits/{Head}/check-runs", HttpStatusCode.OK,
                """{"total_count":101,"check_runs":[{"name":"build","status":"completed","conclusion":"success"}]}""")
            .On($"GET {Repo}/commits/{Head}/status", HttpStatusCode.OK, """{"total_count":0,"statuses":[]}"""));

        var ci = await gate.GetCiAsync(Sandbox, Head, CancellationToken.None);

        Assert.False(ci.Complete);
        Assert.Equal(CiState.Failed, Ci.Evaluate(ci).State);
    }

    [Fact]
    public async Task Merges_only_the_gated_head_with_a_write_token()
    {
        var (gate, api) = Gate(a => a.On($"PUT {Repo}/pulls/7/merge", HttpStatusCode.OK,
            """{"sha":"9999","merged":true,"message":"Pull Request successfully merged"}"""));

        var result = await gate.MergeAsync(Sandbox, 7, Head, CancellationToken.None);

        Assert.Equal(new MergeResult(true, "9999", false, "Pull Request successfully merged"), result);
        var body = JsonDocument.Parse(api.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        Assert.Equal(Head, body.GetProperty("sha").GetString());
        var permissions = TokenPermissions(api).Single();
        Assert.Equal(["contents:write", "pull_requests:write"], permissions.EnumerateObject().Select(p => $"{p.Name}:{p.Value.GetString()}"));
    }

    [Fact]
    public async Task A_moved_head_is_reported_not_thrown()
    {
        var (gate, _) = Gate(a => a.On($"PUT {Repo}/pulls/7/merge", HttpStatusCode.Conflict,
            """{"message":"Head branch was modified. Review and try the merge again."}"""));

        var result = await gate.MergeAsync(Sandbox, 7, Head, CancellationToken.None);

        Assert.True(result.HeadMoved);
        Assert.False(result.Merged);
    }

    [Fact]
    public async Task A_merge_github_refuses_otherwise_throws()
    {
        var (gate, _) = Gate(a => a.On($"PUT {Repo}/pulls/7/merge", HttpStatusCode.MethodNotAllowed,
            """{"message":"Repository rule violations found"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gate.MergeAsync(Sandbox, 7, Head, CancellationToken.None));
        Assert.Contains("405", ex.Message);
    }

    [Fact]
    public void Pull_urls_parse_to_repo_and_number()
    {
        Assert.Equal((Sandbox, 12), GitHubGate.ParsePullUrl("https://github.com/michaeltrefry/dark-factory-sandbox/pull/12"));
        Assert.Throws<FormatException>(() => GitHubGate.ParsePullUrl("https://example.com/x"));
    }
}
