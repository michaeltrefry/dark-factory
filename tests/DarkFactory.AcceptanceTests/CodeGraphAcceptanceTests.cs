using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// The CodeGraph prerequisites of the Phase 3 live tests (epic sc-25704, docs/acceptance.md "Phase 3"): a CodeGraph token, and the
/// sandbox repository indexed in the hosted CodeGraph (the project whose repository URL is exactly the sandbox's). Each skips naming
/// what is missing.
/// </summary>
internal static class CodeGraphLive
{
    /// <summary>The owner's CodeGraph token (skips without one).</summary>
    public static string RequireToken(FactoryOptions options) => Harness.RequireSecret(_ => options.CodeGraphToken);

    /// <summary>The CodeGraph project indexing <paramref name="repo"/>, through the production client (skips when there is none).</summary>
    public static async Task<string> RequireProjectAsync(CodeGraphMcpClient codeGraph, RepoRef repo, CancellationToken ct)
    {
        string? project;
        try
        {
            project = await codeGraph.FindProjectAsync(repo, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Assert.Skip($"CodeGraph could not be asked about {repo} ({ex.Message}): check CodeGraph:BaseUrl and the token.");
            throw;
        }
        if (project is null)
        {
            Assert.Skip($"{repo} is not indexed in CodeGraph: index it there (its repository URL must be exactly https://github.com/{repo}).");
        }
        return project!;
    }

    /// <summary>The verdict's reviews and second opinions as (who, its tool calls, its turns).</summary>
    public static IEnumerable<(string Who, IReadOnlyList<ToolCall> Tools, IReadOnlyList<ServedTurn>? Turns)> Sessions(ReviewVerdict verdict) =>
        verdict.Reviews.Where(r => r.CarriedFrom is null).SelectMany(r =>
            new[] { (r.Role, r.Tools ?? [], r.Turns) }.Concat(r.Findings.Where(f => f.Confirmation is not null)
                .Select(f => ($"second opinion on {r.Role}: {f.Title}", f.Confirmation!.Tools ?? (IReadOnlyList<ToolCall>)[], f.Confirmation.Turns))));

    /// <summary>The reviewers' CodeGraph calls in <paramref name="verdict"/> (reviews and second opinions).</summary>
    public static List<ToolCall> CodeGraphCalls(ReviewVerdict verdict) =>
        [.. Sessions(verdict).SelectMany(s => s.Tools).Where(t => ReviewTools.CodeGraphTools.Contains(t.Tool))];
}

/// <summary>
/// Phase 3 epic AT1 and AT2 (sc-25704, sc-25710): a factory PR on the sandbox reviewed by the real panel with CodeGraph configured.
/// Live: skipped unless FACTORY_E2E=1, its story variable, a CodeGraph token and the sandbox indexed in CodeGraph (C4 and C6 deployed
/// for the head-SHA overlay; C7 for the gate's risk-threshold, or the run escalates at the gate after the review, which these tests
/// do not depend on). Each runs <c>factory run</c> (production wiring, a throwaway ledger), so a passing PR is merged by the gate like
/// P2-AT1's.
/// </summary>
public class CodeGraphReviewTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(60);

    private static async Task<(E2e E2e, FactoryOptions Options, int StoryId, List<LedgerEntry> History, RunOutcome Outcome)> ReviewAsync(
        string variable, string prefix, CancellationToken ct)
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story(variable, "a To Do bug story whose fix lands in normal code of the sandbox repo (the gate may merge it)");
        var e2e = await E2e.StartAsync(prefix);
        try
        {
            var options = e2e.Options();
            var token = CodeGraphLive.RequireToken(options);
            using (var http = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl))
            {
                await CodeGraphLive.RequireProjectAsync(new CodeGraphMcpClient(http, token), options.DefaultRepo, ct);
            }
            await ReviewGateTests.RequireGateReadyAsync(options, ct);
            using var shortcutHttp = OutboundHttp.ShortcutApi();
            var outcome = await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true,
                Console.Out, ct);
            var history = await e2e.HistoryAsync(storyId, ct);
            Assert.True(RunPipeline.Verdicts(history).Any(), $"sc-{storyId} was never reviewed: {outcome.State} {outcome.Error}");
            return (e2e, options, storyId, history, outcome);
        }
        catch
        {
            await e2e.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// P3-AT1: the review calls read_file and a CodeGraph tool; the verdict lists every call with its result's SHA-256 and size and the
    /// served class of every turn (all <c>high</c>), and the PR body shows the tool use (rendered from the ledger, E8).
    /// </summary>
    [Fact]
    public async Task A_review_reads_the_code_and_asks_codegraph_and_the_verdict_and_pr_body_record_it()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var (e2e, options, storyId, history, _) = await ReviewAsync("FACTORY_E2E_P3_REVIEW_STORY", "df_e2e_p3at1", ct);
        await using var _e2e = e2e;

        var verdict = RunPipeline.Verdicts(history).First();
        var calls = CodeGraphLive.Sessions(verdict).SelectMany(s => s.Tools).ToList();
        if (!calls.Any(t => t.Tool == ReviewTools.ReadFile) || CodeGraphLive.CodeGraphCalls(verdict).Count == 0)
        {
            Assert.Fail($"INCONCLUSIVE: the panel made no read_file or no CodeGraph call on {verdict.HeadSha} (its tools: "
                + $"{string.Join(", ", calls.Select(t => t.Tool).Distinct())}); the prompts ask for both, but the model chooses: rerun on a fresh story.");
        }
        Assert.All(calls, t =>
        {
            Assert.Matches("^[0-9a-f]{64}$", t.ResultSha256);
            Assert.True(t.Bytes is >= 0, $"{t.Tool} records no result size");
        });
        foreach (var (who, _, turns) in CodeGraphLive.Sessions(verdict))
        {
            Assert.True(turns is { Count: > 0 }, $"{who} records no turns");
            Assert.All(turns!, t => Assert.True(t.ServedClass == ReviewModels.Class, $"{who} turn {t.Turn} served on {t.ServedClass ?? "(no class)"}"));
            Assert.Equal(Enumerable.Range(1, turns!.Count), turns.Select(t => t.Turn));
        }

        // The PR body carries the review's tool use only once it is rewritten from the ledger at merge (MergeAsync's pr-report row):
        // the body written when the PR opened predates the review.
        var latest = await e2e.HistoryAsync(storyId, ct);
        var report = latest.FindLastIndex(e => e.Step == RunPipeline.Steps.PrReport);
        if (report < 0)
        {
            Assert.Fail($"INCONCLUSIVE/needs merge: sc-{storyId} has no pr-report row (it did not merge; last state "
                + $"{latest.LastOrDefault()?.State}), so its PR body is still the one written at open, before the review; it needs a "
                + "merge (the gate must pass, CodeGraph C7 deployed) before the body can show the tool use: rerun on a fresh story.");
        }
        var pr = (await E2e.PullRequestsAsync(options.DefaultRepo, storyId, ct)).Single(p => p.GetProperty("html_url").GetString() == RunPipeline.LinkedPullRequestUrl(history));
        var body = pr.GetProperty("body").GetString() ?? "";
        Assert.Contains(" tool call", body);
        // The body is the ledger's report (E8), tool use included.
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        var story = (await FactoryRunner.CreateWorkSource(options, shortcutHttp).ReadSpecAsync(storyId, ct)).Story;
        Assert.Equal(LedgerReport.PullRequestBody(story, latest.Take(report).ToList(), await e2e.SessionCostsAsync(storyId, ct)).ReplaceLineEndings("\n"),
            body.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// P3-AT2: the reviewers' CodeGraph answers are for the head-SHA overlay: the review's <c>codegraph-overlay</c> checkpoint is
    /// <c>ready</c> for the reviewed head, and every CodeGraph call the panel made names exactly that head, with no fallback to the
    /// default branch.
    /// </summary>
    [Fact]
    public async Task The_reviewers_codegraph_answers_are_for_the_head_overlay()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var (e2e, _, _, history, _) = await ReviewAsync("FACTORY_E2E_P3_OVERLAY_STORY", "df_e2e_p3at2", ct);
        await using var _e2e = e2e;

        var verdict = RunPipeline.Verdicts(history).First();
        var overlay = history.Where(e => e.Step == RunPipeline.Steps.CodeGraphOverlay).Select(e => CodeGraphOverlay.FromDetail(e.Detail))
            .LastOrDefault(o => o is not null && string.Equals(o.HeadSha, verdict.HeadSha, StringComparison.OrdinalIgnoreCase));
        Assert.True(overlay is { IsReady: true }, $"no ready codegraph-overlay checkpoint for {verdict.HeadSha}: {overlay?.Describe ?? "none"}");
        Assert.True(verdict.Overlay is { IsReady: true }, "the verdict does not record the ready overlay");
        var calls = CodeGraphLive.CodeGraphCalls(verdict);
        if (calls.Count == 0)
        {
            Assert.Fail($"INCONCLUSIVE: the panel made no CodeGraph call on {verdict.HeadSha}: rerun on a fresh story.");
        }
        Assert.All(calls, t =>
        {
            Assert.Null(t.Fallback);
            if (!t.Error)
            {
                Assert.Equal(verdict.HeadSha.ToLowerInvariant(), t.Commit);
            }
        });
        Assert.Contains(calls, t => !t.Error);
    }
}

/// <summary>
/// Phase 3 epic AT3 (sc-25704, sc-25710) and the live Kanban check of epic AT5: a triage session reaches CodeGraph only through the
/// loopback MCP proxy, holds no CodeGraph token in its env, argv or worktree, and a tool off its profile is refused; a reviewer session
/// can call a Kanban read tool, and a write tool is never offered.
/// </summary>
public class CodeGraphTriageTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    private const string SearchGraphRule = "mcp__codegraph__search_graph";

    private const string Issue = """
        [dark-factory fixture] `WordCounter.Count("  ")` returns 1; text that is only whitespace has no words, so it must return 0.
        Before you answer, locate the word counter with the CodeGraph search_graph tool (namePattern "%Count%").
        """;

    /// <summary>
    /// P3-AT3: the owner opens a fixture issue on the sandbox; the production intake triages it in a real sandboxed session whose Claude
    /// Code path is a scanning wrapper (<see cref="ScanWrapper"/>): before it hands over to the real <c>claude</c> it hashes every
    /// window of its env values, its argv, the <c>--mcp-config</c> file and every file of the triage worktree against the CodeGraph
    /// token's SHA-256 and prints the counts as the session's first (raw) line. Expected: the session called
    /// <c>mcp__codegraph__search_graph</c> (its transcript has the tool_use, the answer carries the commit label) and is tainted
    /// <c>mcp:</c>; the token was in none of those places, nor in any stored event; and, through the production proxy (the test as its
    /// MCP client, holding a triage grant), an off-profile CodeGraph tool and any Kanban tool are refused.
    /// </summary>
    [Fact]
    public async Task A_triage_session_uses_codegraph_through_the_proxy_and_holds_no_codegraph_token()
    {
        Harness.RequireOptIn();
        Harness.RequireEnv("FACTORY_E2E_ISSUES", "set to 1 to open and triage a real issue on the sandbox repo");
        var ownerToken = Harness.RequireEnv("GH_TOKEN", "the owner's GitHub token: it opens the fixture issue as a collaborator");
        await using var e2e = await E2e.StartAsync("df_e2e_p3at3");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var repo = Harness.Options.DefaultRepo;
        var probe = e2e.Options();
        var codeGraphToken = CodeGraphLive.RequireToken(probe);
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(probe.CodeGraphBaseUrl);
        await CodeGraphLive.RequireProjectAsync(new CodeGraphMcpClient(codeGraphHttp, codeGraphToken), repo, ct);
        if (FactoryOptions.LoadConfiguration()["Worker:RunAs"] == "none")
        {
            Assert.Skip("Issue triage refuses Worker__RunAs=none: run with the worker sandbox (sudo scripts/setup-worker-user.sh).");
        }
        var wrapper = ScanWrapper.Write(e2e.WorkRoot, [codeGraphToken]);
        var options = e2e.Options(new Dictionary<string, string?> { ["GitHub:Watch:Repos"] = repo.FullName, ["Worker:ClaudePath"] = wrapper });

        var status = new IntakeStatus(TimeProvider.System);
        await FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct); // starts watching from now
        using var owner = IssueTriageTests.UserClient(ownerToken);
        var number = await IssueTriageTests.OpenIssueAsync(owner, repo, "[dark-factory fixture] P3-AT3: whitespace-only text counts as one word", Issue, ct);
        try
        {
            var key = await IssueTriageTests.PollUntilTriagedAsync(e2e, options, status, repo, number, ct);
            await using var db = e2e.Db();
            var externalId = ItemNaming.GitHubIssue.Format(key);
            var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Source == "github" && i.ExternalId == externalId, ct);
            var events = await db.SessionEvents.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
            var taints = await db.SessionTaints.AsNoTracking().Where(t => t.WorkItemId == item.Id).Select(t => t.Reason).ToListAsync(ct);

            var used = events.Where(e => e.Type == "assistant").SelectMany(e => ToolUses(e.Payload)).Where(u => u.Name == SearchGraphRule).ToList();
            if (used.Count == 0)
            {
                Assert.Fail($"INCONCLUSIVE: the triage session made no {SearchGraphRule} call (the issue asks for one; the model chooses): rerun.");
            }
            Assert.Contains($"mcp:{SearchGraphRule}", taints);
            // The proxy relayed CodeGraph's answer with the factory's commit label (E3).
            var results = events.Where(e => e.Type == "user").SelectMany(e => ToolResults(e.Payload)).ToList();
            Assert.Contains(results, r => used.Any(u => u.Id == r.Id) && r.Text.Contains("CodeGraph's index of", StringComparison.Ordinal));

            // E5: the scan of the worker's env, argv, --mcp-config file and triage worktree found no CodeGraph token.
            var scan = ScanWrapper.Result(events.Select(e => e.Payload));
            Assert.True(scan is not null, "the scan wrapper's line is not in the session's events (did the session start through it?)");
            Assert.True(scan!.Vars > 0 && scan.Args > 0 && scan.Files > 0, $"the scan saw nothing: {scan}");
            Assert.True(scan.McpConfig, $"the session had no --mcp-config file to scan: {scan}");
            Assert.Equal((0, 0, 0, 0), (scan.Env, scan.Argv, scan.Worktree, scan.McpConfigHits));
            // Nor did anything the session wrote (its whole stored transcript).
            Assert.DoesNotContain(events, e => e.Payload.Contains(codeGraphToken, StringComparison.Ordinal));

            // E2/E6 through the production proxy with a triage grant: the off-profile tool and Kanban are refused, nothing forwarded.
            var upstreams = FactoryRunner.CreateUpstreams(options, codeGraphHttp, kanbanHttp: null, Console.Out);
            await using var proxy = await McpProxy.StartAsync(upstreams, ct);
            using var grant = proxy.Grant(repo, McpProfile.Triage);
            using var proxyHttp = OutboundHttp.LoopbackMcp(proxy.Endpoint(McpServers.CodeGraph));
            var client = new McpHttpClient(proxyHttp, grant.Credential, "proxy", "");
            var listed = (await client.ListToolsAsync(ct)).Select(t => t.Name).Order().ToList();
            Assert.Equal(McpProfile.Triage.ToolsOf(McpServers.CodeGraph).Order(), listed);
            var refused = await client.CallToolAsync(ReviewTools.AnalyzeImpact, new JsonObject { ["name"] = "WordCounter" }, ct);
            Assert.True(refused.IsError);
            Assert.Contains(McpProxy.InvalidParams.ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Text);
            var answered = await client.CallToolAsync(ReviewTools.SearchGraph, new JsonObject { ["namePattern"] = "%Count%" }, ct);
            Assert.StartsWith("CodeGraph's index of", answered.Text);
            using var kanbanHttp = OutboundHttp.LoopbackMcp(proxy.Endpoint(McpServers.Kanban));
            var kanban = await Assert.ThrowsAsync<McpHttpException>(() => new McpHttpClient(kanbanHttp, grant.Credential, "proxy", "")
                .CallToolAsync("ListProjects", new JsonObject(), ct));
            Assert.Equal(404, kanban.StatusCode);
        }
        finally
        {
            await IssueTriageTests.CloseAsync(owner, repo, number);
        }
    }

    /// <summary>
    /// Epic AT5's live Kanban check: with a Kanban token (keychain <c>kanban-token</c>), the reviewers' tool set — the production
    /// upstream registry and <see cref="ReviewTools"/> — offers Kanban's read tools and no write tool, a read tool
    /// (<c>ListProjects</c>) answers through it and is recorded with its hash and size, and a write tool asked for anyway is refused
    /// without being forwarded. No model is called: the test plays the reviewer's tool_use blocks itself.
    /// </summary>
    [Fact]
    public async Task A_reviewer_session_can_call_a_kanban_read_tool_and_is_never_offered_a_write_tool()
    {
        Harness.RequireOptIn();
        var options = Harness.Options;
        Harness.RequireSecret(o => o.KanbanToken);
        var ct = TestContext.Current.CancellationToken;
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var upstreams = FactoryRunner.CreateUpstreams(options, codeGraphHttp, kanbanHttp, Console.Out);
        Assert.NotNull(upstreams.Kanban);
        var tools = new ReviewTools(null, upstreams.CodeGraph, upstreams.Kanban, Console.Out);
        var session = ReviewTools.Session(options.DefaultRepo, new string('0', 40), new string('0', 40), 0);

        var definitions = await tools.DefinitionsAsync(session, ct);

        var offered = definitions.Select(d => JsonSerializer.SerializeToElement(d).GetProperty("name").GetString()!).ToList();
        Assert.Contains("ListProjects", session.KanbanTools);
        Assert.All(session.KanbanTools, t => Assert.Contains(t, McpServers.KanbanTools));
        string[] writes = ["CreateWorkItem", "UpdateWorkItem", "DeleteWorkItem", "MoveWorkItem", "AddWorkItemComment", "CreateEpic"];
        Assert.DoesNotContain(offered, writes.Contains);

        var read = await tools.RunAsync(new AnswerBlock(AnswerBlock.ToolUse, null, "toolu_k1", "ListProjects", "{}"), session, ct);
        Assert.False(read.IsError, read.Content);
        Assert.StartsWith("Kanban's ListProjects:", read.Content);
        Assert.Matches("^[0-9a-f]{64}$", read.Record.ResultSha256);
        Assert.True(read.Record.Bytes > 0);
        var write = await tools.RunAsync(new AnswerBlock(AnswerBlock.ToolUse, null, "toolu_k2", "CreateWorkItem", """{"title":"x"}"""), session, ct);
        Assert.True(write.IsError);
        Assert.StartsWith("There is no tool named 'CreateWorkItem'", write.Content);
    }

    /// <summary>The tool_use blocks (id, name) of one stream-json assistant event.</summary>
    internal static IEnumerable<(string Id, string Name)> ToolUses(string payload) =>
        Blocks(payload).Where(b => b["type"]?.GetValue<string>() == "tool_use")
            .Select(b => (b["id"]?.GetValue<string>() ?? "", b["name"]?.GetValue<string>() ?? ""));

    /// <summary>The tool_result blocks (tool_use id, text) of one stream-json user event.</summary>
    internal static IEnumerable<(string Id, string Text)> ToolResults(string payload) =>
        Blocks(payload).Where(b => b["type"]?.GetValue<string>() == "tool_result")
            .Select(b => (b["tool_use_id"]?.GetValue<string>() ?? "", b["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray a => string.Join("\n", a.OfType<JsonObject>().Select(c => c["text"]?.GetValue<string>() ?? "")),
                _ => "",
            }));

    private static IEnumerable<JsonObject> Blocks(string payload)
    {
        try
        {
            return (JsonNode.Parse(payload)?["message"]?["content"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Phase 3 epic AT4 (sc-25704, sc-25710): the merge gate's risk-threshold on CodeGraph's inputs. Live: skipped unless FACTORY_E2E=1,
/// its story variable, a CodeGraph token and the sandbox indexed in CodeGraph with C7 deployed. The review panel is the seeded stand-in
/// (<see cref="SeededReviewer"/>) so the gate's own rule decides. Neither merges: each closes its PR and deletes its branch at the end.
/// </summary>
public class CodeGraphGateTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(90);

    /// <summary>The probe line appended to the critical file on the PR branch (a C# comment: it changes no behaviour).</summary>
    internal const string ProbeLine = "// [dark-factory e2e] P3-AT4 blast-radius probe";

    /// <summary>
    /// P3-AT4 (a): a PR touching a file CodeGraph rates <c>critical</c> escalates on risk-threshold. The test finds such a file on the
    /// sandbox's main itself — each normal-tier <c>.cs</c> file under <c>src/</c> asked of <c>analyze_impact_file</c> at CodeGraph's
    /// default-branch index commit (named by <c>file_hotspot</c>'s <c>indexCommitSha</c>) until one answers <c>critical</c>; none
    /// skips with how to seed one (docs/acceptance.md) — then, before the first review, appends a comment line to it on the PR branch
    /// (as the workers' App). Expected: the last <c>gate</c> row is Blocked naming the file's blast radius (<c>critical</c>) over the
    /// limit (worded by the sandbox policy's version), gate_rejected; MergeGate → Escalated; nothing merged.
    /// </summary>
    [Fact]
    public async Task A_pr_touching_a_critical_file_escalates_on_risk_threshold()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P3_CRITICAL_STORY", "a To Do bug story whose fix lands in normal code of the sandbox repo");
        await using var e2e = await E2e.StartAsync("df_e2e_p3at4_critical");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var options = e2e.Options();
        await ReviewGateTests.RequireGateReadyAsync(options, ct);
        using var sandbox = new SandboxRepo(options, options.DefaultRepo);
        try
        {
            var critical = await CriticalFileAsync(options, sandbox, ct);
            var reviewer = new SeededReviewer(beforeFirst: async (_, c) =>
            {
                var token = await sandbox.AppTokenAsync(c);
                var branch = StoryId.BranchName(storyId);
                var text = await sandbox.FileTextAsync(token, branch, critical, c);
                await sandbox.PutFileAsync(token, branch, critical, $"{text.TrimEnd('\n')}\n{ProbeLine}\n", "acceptance: touch a critical file", c);
            });

            using var shortcutHttp = OutboundHttp.ShortcutApi();
            var outcome = await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true,
                Console.Out, ct, gate => gate with { Reviewer = reviewer });

            // The limit's wording depends on the sandbox's policy version (the code floor, or a v3 max_blast_radius): not asserted.
            var gateRow = await AssertEscalatedAtTheGateAsync(e2e, options, storyId, outcome, "risk threshold: blast radius at", ct);
            Assert.Contains($"{critical} critical", outcome.Error);
            Assert.Contains($"{critical} critical", gateRow.Detail);
            Assert.True(reviewer.Reviews > 0);
        }
        finally
        {
            await sandbox.CloseItemAsync(storyId);
        }
    }

    /// <summary>
    /// P3-AT4 (b): with CodeGraph unreachable (<c>CodeGraph:BaseUrl</c> = a closed loopback port, <see cref="Unreachable"/>) the gate
    /// waits on risk-threshold like pending CI — the overlay of the head cannot be read, recorded as a not-ready
    /// <c>codegraph-overlay</c> checkpoint — and at <c>Gate:CiTimeoutMinutes</c> (10 here: it also bounds the CI wait before MergeGate, so the sandbox CI must finish within it) records Blocked and escalates; nothing merges.
    /// The pending evaluations themselves are not ledger rows (by design: only decisions are), so "pending" shows as the not-ready
    /// overlay row and the Blocked reason naming the timeout.
    /// </summary>
    [Fact]
    public async Task With_codegraph_unreachable_the_gate_waits_then_blocks_and_does_not_merge()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P3_UNREACHABLE_STORY", "a To Do bug story whose fix lands in normal code of the sandbox repo");
        await using var e2e = await E2e.StartAsync("df_e2e_p3at4_unreachable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var options = e2e.Options(new Dictionary<string, string?>
        {
            ["CodeGraph:BaseUrl"] = Unreachable,
            ["CodeGraph:OverlayTimeoutMinutes"] = "1",
            ["Gate:CiTimeoutMinutes"] = "10", // also bounds the CI wait before MergeGate: the sandbox CI must finish within it
        });
        CodeGraphLive.RequireToken(options); // without a token risk-threshold fails at once instead of waiting
        await ReviewGateTests.RequireGateReadyAsync(options, ct);
        using var sandbox = new SandboxRepo(options, options.DefaultRepo);
        try
        {
            using var shortcutHttp = OutboundHttp.ShortcutApi();
            var outcome = await FactoryRunner.RunAsync(options, FactoryRunner.CreateWorkSource(options, shortcutHttp), storyId, ignoreScope: true,
                Console.Out, ct, gate => gate with { Reviewer = new SeededReviewer() });

            await AssertEscalatedAtTheGateAsync(e2e, options, storyId, outcome, "CodeGraph could still not be read after 00:10:00 (Gate:CiTimeoutMinutes)", ct);
            var history = await e2e.HistoryAsync(storyId, ct);
            var overlays = history.Where(e => e.Step == RunPipeline.Steps.CodeGraphOverlay).Select(e => CodeGraphOverlay.FromDetail(e.Detail)).ToList();
            Assert.NotEmpty(overlays);
            Assert.All(overlays, o => Assert.False(o?.IsReady ?? false, $"an overlay was ready with CodeGraph unreachable: {o?.Describe}"));
        }
        finally
        {
            await sandbox.CloseItemAsync(storyId);
        }
    }

    /// <summary>A closed loopback port (9, discard: nothing listens on it on the owner's Mac), so every CodeGraph call is refused at once.</summary>
    internal const string Unreachable = "http://127.0.0.1:9/";

    /// <summary>The item escalated at the gate with <paramref name="reason"/>, nothing merged; the last <c>gate</c> row.</summary>
    private static async Task<LedgerEntry> AssertEscalatedAtTheGateAsync(E2e e2e, FactoryOptions options, int storyId, RunOutcome outcome, string reason,
        CancellationToken ct)
    {
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        var history = await e2e.HistoryAsync(storyId, ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], history.Where(e => e.Step is null).Select(e => e.State).TakeLast(2));
        Assert.DoesNotContain(history, e => e.Step is null && e.State == WorkState.Merge);
        Assert.DoesNotContain(history, e => e.Step == RunPipeline.Steps.GatePassed);
        var gate = history.Last(e => e.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith($"{GateOutcome.Blocked} ", gate.Detail);
        Assert.Contains(reason, gate.Detail);
        Assert.Equal(StepOutcome.GateRejected, gate.Outcome);
        var url = RunPipeline.LinkedPullRequestUrl(history);
        var pr = (await E2e.PullRequestsAsync(options.DefaultRepo, storyId, ct)).Single(p => p.GetProperty("html_url").GetString() == url);
        Assert.Equal(JsonValueKind.Null, pr.GetProperty("merged_at").ValueKind);
        return gate;
    }

    /// <summary>
    /// A normal-tier <c>.cs</c> file under <c>src/</c> on the sandbox's main that CodeGraph rates critical at its default-branch index
    /// commit (skips when CodeGraph is not ready or rates none so).
    /// </summary>
    private static async Task<string> CriticalFileAsync(FactoryOptions options, SandboxRepo sandbox, CancellationToken ct)
    {
        var token = CodeGraphLive.RequireToken(options);
        using var http = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        var codeGraph = new CodeGraphMcpClient(http, token);
        var project = await CodeGraphLive.RequireProjectAsync(codeGraph, options.DefaultRepo, ct);
        var policy = GatePolicy.Parse((await sandbox.GatePolicyAsync("main", ct))!);
        var files = (await sandbox.TreeFilesAsync(await sandbox.AppTokenAsync(ct), "main", ct))
            .Where(p => p.StartsWith("src/", StringComparison.Ordinal) && p.EndsWith(".cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 0, $"{options.DefaultRepo} has no .cs file under src/ on main");
        var index = (await codeGraph.FileHotspotAsync(project, files[0], ct)).IndexCommitSha;
        if (index is null)
        {
            Assert.Skip($"CodeGraph's file_hotspot names no indexCommitSha for {options.DefaultRepo}: deploy CodeGraph C7 (michaeltrefry/CodeGraph PR #76).");
        }
        foreach (var path in files.Take(MaxProbed))
        {
            if (policy.TierOf(path).Tier != Tier.Normal)
            {
                continue;
            }
            var impact = await codeGraph.AnalyzeImpactFileAsync(project, path, index!, CodeGraphRiskReader.ImpactDepth, ct);
            if (impact.NotIndexed is not null || impact.IsError)
            {
                Assert.Skip($"CodeGraph cannot answer analyze_impact_file for {path} at its index commit {index}: {impact.Text}");
            }
            if (impact.Found == true && impact.BlastRadius == BlastRadii.Name(BlastRadius.Critical))
            {
                Console.WriteLine($"[p3-at4] {path} is critical at {index}");
                return path;
            }
        }
        Assert.Skip($"CodeGraph rates no normal-tier src/**/*.cs file of {options.DefaultRepo} critical at {index} (probed {Math.Min(files.Count, MaxProbed)}): "
            + "seed one as docs/acceptance.md (Phase 3, P3-AT4) describes.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The most files asked of <c>analyze_impact_file</c> while looking for a critical one.</summary>
    private const int MaxProbed = 60;
}

/// <summary>
/// P3-AT3's scanning Claude Code path (sc-25710): a Perl script the sandboxed worker runs in place of <c>claude</c> (through the
/// launch helper, as <c>_factory</c>, with the exact env and argv the factory gives Claude Code). Unless it is asked for
/// <c>--version</c> (the start-up check), it slides a window of each secret's length over every env value, every argument, the
/// <c>--mcp-config</c> file and every regular file of its working directory (the triage worktree; symlinks not followed), compares
/// each window's SHA-256 with the secrets' (only hashes are written into it: no secret is ever on disk), prints one raw line
/// <c>dark-factory-e2e-scan {json}</c> — a non-JSON stdout line the session recorder stores as a <c>raw</c> event — and then
/// <c>exec</c>s the real <c>claude</c> from the worker's PATH with the same arguments.
/// </summary>
internal static class ScanWrapper
{
    public const string Marker = "dark-factory-e2e-scan ";

    /// <summary>The scan's counts: hits per place, and how much it looked at.</summary>
    public sealed record Scan(int Env, int Argv, int Worktree, int McpConfigHits, bool McpConfig, int Vars, int Args, int Files);

    /// <summary>Writes the wrapper (mode 755) under <paramref name="workRoot"/> (which the worker user can read) and returns its path.</summary>
    public static string Write(string workRoot, IReadOnlyList<string> secrets)
    {
        var dir = Directory.CreateDirectory(Path.Combine(workRoot, "e2e-scan")).FullName;
        var path = Path.Combine(dir, "claude-scan.pl");
        File.WriteAllText(path, Script(secrets));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>The script, with <paramref name="secrets"/> only as (length, SHA-256) pairs.</summary>
    public static string Script(IReadOnlyList<string> secrets)
    {
        var targets = string.Join(", ", secrets.Select(s =>
            $"[{Encoding.UTF8.GetByteCount(s)}, '{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))}']"));
        return ScriptTemplate.Replace("TARGETS", targets, StringComparison.Ordinal);
    }

    private const string ScriptTemplate = """
        #!/usr/bin/perl
        # [dark-factory e2e] P3-AT3 scanning wrapper: counts the secrets (by hash) in env, argv, --mcp-config and the worktree.
        use strict;
        use warnings;
        use Digest::SHA qw(sha256_hex);
        use File::Find;

        if (grep { $_ eq '--version' } @ARGV) { exec { 'claude' } 'claude', @ARGV or die "exec claude: $!\n"; }

        my @targets = (TARGETS);
        sub hits {
            my ($text) = @_;
            my $n = 0;
            for my $t (@targets) {
                my ($len, $hash) = @$t;
                for (my $i = 0; $i + $len <= length($text); $i++) { $n++ if sha256_hex(substr($text, $i, $len)) eq $hash; }
            }
            return $n;
        }
        sub slurp {
            my ($file) = @_;
            open(my $fh, '<:raw', $file) or return undef;
            local $/;
            my $text = <$fh>;
            close($fh);
            return $text;
        }

        my ($env, $argv, $tree, $cfg, $vars, $args, $files, $has_cfg) = (0, 0, 0, 0, 0, 0, 0, 0);
        for my $k (keys %ENV) { $vars++; $env += hits($ENV{$k}) + hits($k); }
        for my $i (0 .. $#ARGV) {
            $args++;
            $argv += hits($ARGV[$i]);
            if ($ARGV[$i] eq '--mcp-config' && $i < $#ARGV) {
                my $text = slurp($ARGV[$i + 1]);
                if (defined $text) { $has_cfg = 1; $cfg += hits($text); }
            }
        }
        find({ no_chdir => 1, follow => 0, wanted => sub {
            return if -l $_ || !-f _ || -s _ > 2_000_000;
            my $text = slurp($_);
            return unless defined $text;
            $files++;
            $tree += hits($text);
        } }, '.');
        $| = 1;
        print "dark-factory-e2e-scan {\"env\":$env,\"argv\":$argv,\"worktree\":$tree,\"mcpConfigHits\":$cfg,\"mcpConfig\":"
            . ($has_cfg ? 'true' : 'false') . ",\"vars\":$vars,\"args\":$args,\"files\":$files}\n";
        exec { 'claude' } 'claude', @ARGV or die "exec claude: $!\n";

        """;

    /// <summary>The scan line among a session's stored lines, or null when there is none.</summary>
    public static Scan? Result(IEnumerable<string> lines)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }
        var json = JsonNode.Parse(line[Marker.Length..])!;
        int N(string name) => json[name]!.GetValue<int>();
        return new Scan(N("env"), N("argv"), N("worktree"), N("mcpConfigHits"), json["mcpConfig"]!.GetValue<bool>(), N("vars"), N("args"), N("files"));
    }
}
