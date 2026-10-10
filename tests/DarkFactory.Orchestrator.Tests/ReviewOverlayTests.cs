using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25708: a review first asks CodeGraph for its overlay of the PR head (owner-side) and waits for it, bounded; with it ready the
/// reviewers' CodeGraph calls carry <c>sha</c> = head and the answers name the head; otherwise they read the default-branch index,
/// labelled as not the PR head. The CodeGraph shapes are its contract's: overlays C6 (sc-25726, not built yet) and commit-pinned reads
/// C4 (michaeltrefry/CodeGraph PR #71), see <see cref="FakeMcpServer.CommitAnswer"/> and <see cref="FakeMcpServer.NotIndexed"/>.
/// </summary>
public class ReviewOverlayTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string IndexCommit = "c0ffee0123456789abcdef0123456789abcdef01";
    private const string CleanFindings = "{\"findings\": [], \"summary\": \"fine\"}";
    private const string Project = "R Service";
    private const string Listing = "- **r-tools** [csharp]\n  Repo: https://github.com/o/r-tools\n- **R Service** [csharp]\n  Repo: https://github.com/O/R.git\n";
    private static readonly RepoRef Repo = new("o", "r");

    private static readonly WorkStory Story = new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");
    private static readonly PullFacts Pull = new(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", Base, null);
    private static readonly RepoFiles Files = new(["src/X.cs"], false);

    private static CodeGraphMcpClient Client(FakeMcpServer server) => new(server.Client("https://codegraph.test/"), "cg_test_token");

    private static Task NoControls(CancellationToken _) => Task.CompletedTask;

    private static ReviewOverlays Overlays(FakeMcpServer server, TimeSpan? timeout = null) =>
        new(Client(server), timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));

    /// <summary>A CodeGraph whose overlay of the head answers <paramref name="statuses"/> in turn (the last one again once they run out).</summary>
    private static FakeMcpServer OverlayServer(params string[] statuses)
    {
        var n = 0;
        return FakeMcpServer.CodeGraph(Listing, (tool, _) => tool switch
        {
            CodeGraphMcpClient.RequestOverlayTool or CodeGraphMcpClient.OverlayStatusTool =>
                FakeMcpServer.Overlay(41, statuses[Math.Min(n++, statuses.Length - 1)], Head, IndexCommit),
            _ => FakeMcpServer.Text("unexpected"),
        });
    }

    // ---- the overlay wait ----

    [Fact]
    public async Task The_overlay_is_requested_for_the_head_in_the_pr_repositorys_project_and_polled_until_ready()
    {
        var server = OverlayServer("queued", "indexing", "ready");

        var overlay = await Overlays(server).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None);

        Assert.Equal(new CodeGraphOverlay(CodeGraphOverlay.Ready, Head, 41, IndexCommit), overlay);
        Assert.Equal(["search_projects", "request_overlay", "get_overlay_status", "get_overlay_status"], server.Calls.Select(c => c.Tool));
        // repo: the CodeGraph repository name (C3's BranchOverlayRequest.Repo, PR #67: `graphStore.GetRepositoryByName(repo)`), the
        // project the reviewer tools already pin (exact repo URL match); ref: the full head SHA.
        Assert.Equal($$"""{"repo":"{{Project}}","ref":"{{Head}}"}""", server.Calls[1].Arguments.ToJsonString());
        Assert.Equal("""{"overlayId":41}""", server.Calls[2].Arguments.ToJsonString());
        Assert.All(server.Api.Requests, r => Assert.Equal("Bearer cg_test_token", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task An_overlay_still_indexing_at_the_timeout_ends_the_wait_there_as_timed_out()
    {
        var server = OverlayServer("indexing");
        var clock = Stopwatch.StartNew();

        var overlay = await Overlays(server, TimeSpan.FromMilliseconds(300)).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None);

        Assert.Equal((CodeGraphOverlay.TimedOut, 41L), (overlay.Outcome, overlay.OverlayId));
        Assert.Contains("not ready within", overlay.Reason);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the wait took {clock.Elapsed}");
        Assert.True(server.Calls.Count(c => c.Tool == "get_overlay_status") > 1);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task A_failed_or_expired_overlay_ends_the_wait_at_once(string status)
    {
        var server = OverlayServer("indexing", status);

        var overlay = await Overlays(server).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None);

        Assert.Equal(CodeGraphOverlay.Failed, overlay.Outcome);
        Assert.Contains($"the overlay is {status}", overlay.Reason);
        Assert.Equal(2, server.Calls.Count(c => c.Tool.Contains("overlay")));
    }

    [Theory]
    [InlineData("This PAT user is not entitled to request overlays.")] // no entitlement
    [InlineData("Repository 'R Service' was not found.")] // unknown repository
    [InlineData("Unknown tool: 'request_overlay'")] // a CodeGraph without the tool (C6 not deployed)
    public async Task A_refused_request_falls_back_at_once_and_says_why(string refusal)
    {
        var server = FakeMcpServer.CodeGraph(Listing, (_, _) => FakeMcpServer.Structured(refusal, new JsonObject { ["error"] = "refused" }, isError: true));

        var overlay = await Overlays(server).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None);

        Assert.Equal(CodeGraphOverlay.Refused, overlay.Outcome);
        Assert.Contains(refusal, overlay.Reason);
        Assert.Equal(["search_projects", "request_overlay"], server.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task No_codegraph_an_unindexed_repository_or_a_ready_overlay_of_another_commit_is_never_the_heads()
    {
        Assert.Equal(CodeGraphOverlay.Unavailable,
            (await new ReviewOverlays(null, TimeSpan.FromMinutes(1)).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None)).Outcome);

        var unindexed = await Overlays(OverlayServer("ready")).WaitAsync(new RepoRef("o", "elsewhere"), Head, NoControls, TimeProvider.System,
            CancellationToken.None);
        Assert.Equal((CodeGraphOverlay.Unavailable, $"o/elsewhere: {ReviewTools.NotIndexed}"), (unindexed.Outcome, unindexed.Reason));

        var other = FakeMcpServer.CodeGraph(Listing, (_, _) => FakeMcpServer.Overlay(5, "ready", Base, IndexCommit));
        var wrong = await Overlays(other).WaitAsync(Repo, Head, NoControls, TimeProvider.System, CancellationToken.None);
        Assert.Equal(CodeGraphOverlay.Failed, wrong.Outcome);
        Assert.Contains($"the ready overlay is of {Base}, not the PR head", wrong.Reason);
    }

    // ---- the reviewer's CodeGraph calls ----

    private static FakeApi Router()
    {
        // Each session asks for analyze_impact and get_code_snippet once, then answers clean.
        return new FakeApi().On("POST /v1/messages", r =>
        {
            var messages = JsonDocument.Parse(r.Body!).RootElement.GetProperty("messages");
            var last = messages[messages.GetArrayLength() - 1].GetProperty("content");
            var answered = last.ValueKind == JsonValueKind.Array && last.EnumerateArray().Any(b => b.GetProperty("type").GetString() == "tool_result");
            return SseAnswers.Response(answered ? SseAnswers.Answer(CleanFindings)
                : SseAnswers.ToolTurn(("t1", "analyze_impact", "{\"name\": \"X.Count\"}"),
                    ("t2", "get_code_snippet", "{\"filePath\": \"src/X.cs\", \"startLine\": 1, \"endLine\": 2}")));
        });
    }

    /// <summary>
    /// A CodeGraph whose reads answer for the head when asked with <c>sha</c> (or, with <paramref name="headIndexed"/> false, say the
    /// head is not indexed, C4's typed answer), and from the default-branch index without it.
    /// </summary>
    private static FakeMcpServer ReadServer(bool headIndexed = true) => FakeMcpServer.CodeGraph(Listing, (tool, args) =>
        args["sha"] is { } sha
            ? headIndexed
                ? FakeMcpServer.CommitAnswer(sha.GetValue<string>(), $"# {tool} at the head", overlay: true)
                : FakeMcpServer.NotIndexed(Project, sha.GetValue<string>(), "expired", overlayId: 41, baseSha: IndexCommit)
            : FakeMcpServer.CommitAnswer(IndexCommit, $"# {tool} on the default branch"));

    private static List<string> Results(FakeApi router)
    {
        var messages = JsonDocument.Parse(router.Requests[1].Body!).RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "tool_result").Select(b => b.GetProperty("content").GetString()!).ToList();
    }

    private static ReviewRequest Request(CodeGraphOverlay? overlay) =>
        new(Story, "o/r", Pull, "+fix\n", Files, ReviewRoles.Correctness, ReviewPrompts.For(ReviewRoles.Correctness), "0b7c4d2e-0000-4000-8000-0000000000aa",
            overlay);

    private static readonly CodeGraphOverlay ReadyOverlay = new(CodeGraphOverlay.Ready, Head, 41, IndexCommit);

    [Fact]
    public async Task With_the_overlay_ready_impact_and_snippet_calls_carry_the_head_sha_and_the_answers_name_the_head()
    {
        var router = Router();
        var codeGraph = ReadServer();

        var review = await new RouterReviewer(router.Client("http://router.test/"), "rk", tools: new ReviewTools(null, Client(codeGraph)))
            .ReviewAsync(Request(ReadyOverlay), CancellationToken.None);

        Assert.True(review.Clean, review.Error);
        var sent = codeGraph.Calls.Where(c => c.Tool != "search_projects").ToList();
        Assert.Equal(["analyze_impact", "get_code_snippet"], sent.Select(c => c.Tool));
        Assert.All(sent, c => Assert.Equal((Head, Project), (c.Arguments["sha"]!.GetValue<string>(), c.Arguments["project"]!.GetValue<string>())));
        Assert.All(Results(router), r => Assert.Contains($"CodeGraph's index of {Project} at the PR head {Head}:", r));
        Assert.All(Results(router), r => Assert.DoesNotContain("not the PR head", r));
        Assert.All(review.Tools!, t => Assert.Equal((Head, (string?)null), (t.Commit, t.Fallback)));
        // The prompt says what the answers describe.
        Assert.Contains($"CodeGraph has indexed this pull request's head ({Head}) for this review", RouterReviewer.BuildPrompt(Request(ReadyOverlay)));
    }

    [Fact]
    public async Task With_the_overlay_not_ready_the_calls_read_the_default_branch_labelled_not_the_pr_head()
    {
        var router = Router();
        var codeGraph = ReadServer();
        var timedOut = new CodeGraphOverlay(CodeGraphOverlay.TimedOut, Head, 41, null, "not ready within 10 min");

        var review = await new RouterReviewer(router.Client("http://router.test/"), "rk", tools: new ReviewTools(null, Client(codeGraph)))
            .ReviewAsync(Request(timedOut), CancellationToken.None);

        Assert.True(review.Clean, review.Error);
        Assert.All(codeGraph.Calls, c => Assert.Null(c.Arguments["sha"]));
        Assert.All(Results(router), r => Assert.Contains($"as of {IndexCommit}, not the PR head {Head}", r));
        Assert.All(review.Tools!, t => Assert.Equal(IndexCommit, t.Commit));
        Assert.Contains("CodeGraph's index of the head is timed-out", RouterReviewer.BuildPrompt(Request(timedOut)));
    }

    [Fact]
    public async Task A_not_indexed_answer_for_the_head_falls_back_to_the_default_branch_labelled_and_recorded()
    {
        var router = Router();
        var codeGraph = ReadServer(headIndexed: false);

        var review = await new RouterReviewer(router.Client("http://router.test/"), "rk", tools: new ReviewTools(null, Client(codeGraph)))
            .ReviewAsync(Request(ReadyOverlay), CancellationToken.None);

        Assert.True(review.Clean, review.Error);
        // Each tool asked about the head first, then (not indexed) about the default branch.
        Assert.Equal([("analyze_impact", true), ("analyze_impact", false), ("get_code_snippet", true), ("get_code_snippet", false)],
            codeGraph.Calls.Where(c => c.Tool != "search_projects").Select(c => (c.Tool, c.Arguments["sha"] is not null)));
        Assert.All(Results(router), r =>
        {
            Assert.Contains($"as of {IndexCommit}, not the PR head {Head} (the PR head is not indexed for this call (commit_not_indexed, status expired)", r);
            Assert.Contains("on the default branch", r);
            Assert.DoesNotContain("Not indexed (", r); // the not-indexed answer itself carries no data and is not shown
        });
        Assert.All(review.Tools!, t =>
        {
            Assert.Equal(IndexCommit, t.Commit);
            Assert.StartsWith("the PR head is not indexed for this call (commit_not_indexed, status expired)", t.Fallback);
        });
    }

    [Fact]
    public async Task A_second_opinion_uses_the_same_overlay_state()
    {
        var router = new FakeApi().On("POST /v1/messages", r =>
        {
            var answered = r.Body!.Contains("\"tool_result\"", StringComparison.Ordinal);
            return SseAnswers.Response(answered ? SseAnswers.Answer("{\"confirmed\": true, \"reason\": \"x\"}")
                : SseAnswers.ToolTurn(("t1", "analyze_impact", "{\"name\": \"X.Count\"}")));
        });
        var codeGraph = ReadServer();
        var confirm = new ConfirmRequest(Story, "o/r", Pull, "+fix\n", Files, ReviewRoles.Correctness,
            new Finding(Finding.Blocking, "Count counts whitespace", "src/X.cs", 2, "x"), ReviewPrompts.Confirm, "0b7c4d2e-0000-4000-8000-0000000000bb",
            ReadyOverlay);

        var confirmation = await new RouterReviewer(router.Client("http://router.test/"), "rk", tools: new ReviewTools(null, Client(codeGraph)))
            .ConfirmAsync(confirm, CancellationToken.None);

        Assert.Equal(Confirmation.Confirmed, confirmation.Outcome);
        Assert.Equal(Head, codeGraph.Calls.Single(c => c.Tool == "analyze_impact").Arguments["sha"]!.GetValue<string>());
        Assert.Equal(Head, Assert.Single(confirmation.Tools!).Commit);
    }

    // ---- the overlay tools are the orchestrator's alone ----

    [Fact]
    public async Task The_overlay_tools_are_on_no_model_facing_tool_list_and_are_never_forwarded()
    {
        var overlayTools = CodeGraphMcpClient.OverlayTools;
        Assert.Equal(["request_overlay", "get_overlay_status"], overlayTools);
        foreach (var tool in overlayTools)
        {
            Assert.DoesNotContain(tool, ReviewTools.Names);
            Assert.DoesNotContain(tool, ReviewTools.CodeGraphTools);
            Assert.DoesNotContain(ReviewTools.Definitions, d => JsonSerializer.SerializeToElement(d).GetProperty("name").GetString() == tool);
            Assert.DoesNotContain(McpServers.KanbanTools, t => t == tool);
            Assert.DoesNotContain(McpServers.AllToolRules, r => r.EndsWith("__" + tool, StringComparison.Ordinal));
            foreach (var profile in new[] { McpProfile.Triage, McpProfile.Planner })
            {
                Assert.DoesNotContain(tool, profile.ToolsOf(McpServers.CodeGraph));
                Assert.DoesNotContain(tool, profile.ToolsOf(McpServers.Kanban));
            }
        }

        // A CodeGraph that lists them: the upstream (the proxy's and the reviewers' registry) neither lists nor calls them, and
        // the reviewers' client refuses them before sending anything.
        var server = new FakeMcpServer("/mcp", [.. overlayTools, ReviewTools.AnalyzeImpact], (t, _) => FakeMcpServer.Overlay(1, "ready", Head, Base));
        var client = Client(server);
        Assert.Equal([ReviewTools.AnalyzeImpact], (await client.Upstream.ListToolsAsync(CancellationToken.None)).Select(t => t.Name));
        var before = server.Api.Requests.Count;
        foreach (var tool in overlayTools)
        {
            await Assert.ThrowsAsync<ToolNotAllowedException>(() => client.Upstream.CallAsync(tool, new JsonObject(), CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => client.CallAsync(tool, new JsonObject(), CancellationToken.None));
        }
        Assert.Equal(before, server.Api.Requests.Count);

        // A reviewer asking for one gets "no such tool", and nothing reaches CodeGraph.
        var router = new FakeApi().On("POST /v1/messages", r => SseAnswers.Response(r.Body!.Contains("\"tool_result\"", StringComparison.Ordinal)
            ? SseAnswers.Answer(CleanFindings)
            : SseAnswers.ToolTurn(("t1", "request_overlay", $"{{\"repo\": \"{Project}\", \"ref\": \"main\"}}"), ("t2", "get_overlay_status", "{\"overlayId\": 1}"))));
        var codeGraph = ReadServer();
        var review = await new RouterReviewer(router.Client("http://router.test/"), "rk", tools: new ReviewTools(null, Client(codeGraph)))
            .ReviewAsync(Request(ReadyOverlay), CancellationToken.None);
        Assert.True(review.Clean, review.Error);
        Assert.All(review.Tools!, t => Assert.True(t.Error));
        Assert.All(Results(router), r => Assert.Contains("There is no tool named", r));
        Assert.Empty(codeGraph.Api.Requests);
    }

    // ---- through the pipeline ----

    private const string SandboxListing = "- **Sandbox** [csharp]\n  Repo: https://github.com/michaeltrefry/dark-factory-sandbox\n";

    private static FakeMcpServer PipelineServer(string status, Action<string>? onCall = null) => FakeMcpServer.CodeGraph(SandboxListing, (tool, _) =>
    {
        onCall?.Invoke(tool);
        return FakeMcpServer.Overlay(41, status, Sha1, IndexCommit);
    });

    [Fact]
    public async Task A_review_records_the_ready_overlay_in_its_verdict_and_every_panel_call_and_second_opinion_gets_it()
    {
        var server = PipelineServer("ready");
        // One blocking finding, which the second opinion does not confirm: the head passes on its first review.
        var h = new Harness
        {
            Overlays = Overlays(server),
            Reviewer = new FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness ? [new Finding(Finding.Blocking, "maybe", "src/x.cs", 1, "x")] : [],
                Confirm = _ => Confirmation.NotConfirmed,
            },
        };

        await h.Run();

        var overlay = new CodeGraphOverlay(CodeGraphOverlay.Ready, Sha1, 41, IndexCommit);
        Assert.NotEmpty(h.Reviewer.Requests);
        Assert.All(h.Reviewer.Requests, r => Assert.Equal(overlay, r.Overlay));
        Assert.NotEmpty(h.Reviewer.Confirms);
        Assert.All(h.Reviewer.Confirms, c => Assert.Equal(overlay, c.Overlay));
        Assert.Equal(overlay, (await h.Verdicts()).First().Overlay);
        // Requested once for the head, before the first panel call.
        Assert.Equal(1, server.Calls.Count(c => c.Tool == "request_overlay"));
        Assert.Contains("CodeGraph answered about the head (overlay `41`)", LedgerReport.Facts(await h.Rows(), []));
    }

    [Fact]
    public async Task An_overlay_not_ready_by_the_timeout_is_recorded_in_the_verdict_and_the_review_goes_on()
    {
        var server = PipelineServer("indexing");
        var h = new Harness { Overlays = Overlays(server, TimeSpan.FromMilliseconds(200)) };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal((CodeGraphOverlay.TimedOut, Sha1), (verdict.Overlay!.Outcome, verdict.Overlay.HeadSha));
        Assert.All(h.Reviewer.Requests, r => Assert.Equal(CodeGraphOverlay.TimedOut, r.Overlay!.Outcome));
        Assert.Contains("CodeGraph overlay of the head `timed-out`", LedgerReport.Facts(await h.Rows(), []));
        Assert.Contains("answers from the default branch", LedgerReport.Facts(await h.Rows(), []));
    }

    [Fact]
    public async Task A_pause_during_the_overlay_wait_pauses_the_item_before_any_panel_call()
    {
        Harness? harness = null;
        var server = PipelineServer("indexing", tool =>
        {
            if (tool == CodeGraphMcpClient.OverlayStatusTool)
            {
                harness!.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Paused, "tester", CancellationToken.None).GetAwaiter().GetResult();
            }
        });
        var h = harness = new Harness { Overlays = Overlays(server, TimeSpan.FromMinutes(5)) };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Equal((WorkState.Paused, RunPipeline.UserPaused), (await h.Rows()).Where(r => r.Step is null).Select(r => (r.State, r.Detail)).Last());
        Assert.Empty(h.Reviewer.Requests);
        Assert.Empty(await h.Verdicts());
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.ReviewSession);
        Assert.Equal(1, server.Calls.Count(c => c.Tool == CodeGraphMcpClient.OverlayStatusTool));
    }
}
