using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4a's epic acceptance test P4-AT1 (sc-25870, extended by sc-25871 and sc-25872): a real planner session plans a
/// <c>plan</c>-labelled story into stories on the board, the waiting plan item releases them in dependency order, each released story
/// is really worked and merged by the gate into the plan's changeset branch, and the changeset then goes to the default branch by
/// one pull request of the plan item's own, through the same review, CI and gate — once driven run by run
/// (<c>factory run</c>), and once by the real intake loop of <c>factory work</c> (sc-25877). Also P4-AT3 (sc-25874, sc-25877):
/// the planner's cross-repository proxy grant, and the three plans the code refuses with nothing created — a cycle, an oversized
/// plan (both a stub Claude CLI answering a fixed plan) and a repository the factory is not configured for. Runbook:
/// <c>docs/acceptance.md</c>.
/// </summary>
public class PlanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(45);

    /// <summary>A whole plan — planner, every story, the changeset pull request — through the intake loop's polls.</summary>
    private static readonly TimeSpan WorkTimeout = TimeSpan.FromMinutes(120);

    private static HttpClient Shortcut()
    {
        var shortcut = OutboundHttp.ShortcutApi();
        shortcut.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        return shortcut;
    }

    private static async Task<JsonElement> PlanStoryAsync(HttpClient shortcut, int storyId, CancellationToken ct)
    {
        var parent = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        Assert.True(parent.GetProperty("labels").EnumerateArray().Any(l => string.Equals(l.GetProperty("name").GetString(), PlanLabel.Name,
            StringComparison.OrdinalIgnoreCase)), $"sc-{storyId} does not carry the `{PlanLabel.Name}` label");
        return parent;
    }

    /// <summary>
    /// P4-AT3 (sc-25877): the item escalated on a plan the code refused, and nothing of a plan exists — no <c>plan</c>,
    /// <c>plan-creating</c>, <c>plan-stories</c> or <c>changeset</c> row, and not one story more in the parent's epic and team on
    /// the real board than before the run.
    /// </summary>
    private static async Task<List<LedgerEntry>> AssertRefusedWithNothingCreatedAsync(E2e e2e, HttpClient shortcut, JsonElement parent, int storyId,
        RunOutcome outcome, HashSet<int> before, CancellationToken ct)
    {
        Assert.Equal(WorkState.Escalated, outcome.State);
        var history = await e2e.HistoryAsync(storyId, ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Escalated], history.Where(e => e.Step is null).Select(e => e.State));
        Assert.DoesNotContain(history, e => e.Step is RunPipeline.Steps.Plan or RunPipeline.Steps.PlanCreating or RunPipeline.Steps.PlanStories
            or RunPipeline.Steps.Changeset or RunPipeline.Steps.ChangesetPushed or RunPipeline.Steps.PlanReleased);
        var after = await PlanLive.SiblingsAsync(shortcut, parent, ct);
        Assert.True(after.SetEquals(before), $"stories appeared in or left the plan story's epic/team during the run: "
            + $"new {string.Join(", ", after.Except(before).Order())}; gone {string.Join(", ", before.Except(after).Order())}");
        Assert.Empty(await E2e.PullRequestsAsync(Harness.Options.DefaultRepo, $"factory/changeset-sc-{storyId}", ct));
        return history;
    }

    /// <summary>
    /// A plan-labelled story run through <c>factory run</c> with a stub Claude CLI as the planner, answering <paramref name="answer"/>
    /// (a plan <see cref="PlanParser"/> refuses): the session really runs — through the production planner seat, as the owner
    /// (<c>Worker:RunAs=none</c>: the stub reads and writes nothing but its own files) — and the code refuses its plan.
    /// </summary>
    private static async Task AssertPlannerAnswerRefusedAsync(string variable, string prefix, string answer, string reason)
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story(variable, "a To Do story labelled `plan` (sc-<id>) on the sandbox repo; it escalates and nothing is created from it");
        await using var e2e = await E2e.StartAsync(prefix);
        using var stub = StubClaude.Answering(answer);
        var options = e2e.Options(stub.Settings());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        using var shortcut = Shortcut();
        var parent = await PlanStoryAsync(shortcut, storyId, ct);
        var before = await PlanLive.SiblingsAsync(shortcut, parent, ct);

        var outcome = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct);

        var history = await AssertRefusedWithNothingCreatedAsync(e2e, shortcut, parent, storyId, outcome, before, ct);
        // One planner session ran (the stub, to its end), and its answer is what was refused: plan-rejected, a gate rejection.
        var session = Assert.Single(history, e => e.Step == RunPipeline.Steps.Session).ClaudeSessionId;
        var run = Assert.Single(stub.Runs());
        Assert.Equal((session, StubClaude.Completed), (run.Session, run.Ending));
        var rejected = Assert.Single(history, e => e.Step == RunPipeline.Steps.PlanRejected);
        Assert.Contains(reason, rejected.Detail);
        Assert.Equal(StepOutcome.GateRejected, rejected.Outcome);
        Assert.Contains("The planner's answer is not a valid plan, so no story was created", outcome.Error);
        Assert.Contains(reason, outcome.Error);
    }

    /// <summary>P4-AT3: a plan whose stories block each other in a cycle is refused by the code; the item escalates, nothing is created.</summary>
    [Fact]
    public Task P4_AT3_a_plan_with_a_dependency_cycle_is_rejected_and_nothing_is_created() =>
        AssertPlannerAnswerRefusedAsync("FACTORY_E2E_P4_CYCLE_STORY", "df_e2e_p4at3_cycle", PlanLive.CyclicAnswer, "the stories block each other in a cycle");

    /// <summary>P4-AT3: a plan of more stories than <see cref="PlanParser.MaxStories"/> is refused whole — never cut; nothing is created.</summary>
    [Fact]
    public Task P4_AT3_an_oversized_plan_is_rejected_and_nothing_is_created() =>
        AssertPlannerAnswerRefusedAsync("FACTORY_E2E_P4_OVERSIZED_STORY", "df_e2e_p4at3_oversized", PlanLive.OversizedAnswer,
            $"the plan has {PlanParser.MaxStories + 1} stories; at most {PlanParser.MaxStories} are allowed");

    /// <summary>
    /// P4-AT3 (epic E1): a plan item whose <c>Repo:</c> line names a repository the factory is not configured for
    /// (<c>Factory:DefaultRepo</c>, <c>Factory:Repos</c>, <c>GitHub:Watch:Repos</c>) escalates before any planner session — also when it names only that
    /// one repository (sc-25877) — and nothing is created. The story's own description decides: one <c>Repo:</c> line (the form
    /// this covers; several lines with an unconfigured one among them pass too).
    /// </summary>
    [Fact]
    public async Task P4_AT3_a_plan_naming_an_unconfigured_repository_escalates_before_any_planner_session_and_nothing_is_created()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P4_UNCONFIGURED_STORY",
            "a To Do story labelled `plan` (sc-<id>) whose description has one line `Repo: owner/name` naming a repository the factory is NOT configured for");
        await using var e2e = await E2e.StartAsync("df_e2e_p4at3_unconfigured");
        var options = e2e.Options();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        using var shortcut = Shortcut();
        var parent = await PlanStoryAsync(shortcut, storyId, ct);
        var named = RepoResolver.ResolveAll(parent.GetProperty("description").GetString(), options.DefaultRepo);
        var unconfigured = named.Where(r => !options.ConfiguredRepos.Contains(r.FullName, StringComparer.OrdinalIgnoreCase)).Select(r => r.FullName).ToList();
        if (unconfigured.Count == 0)
        {
            Assert.Fail($"{PlanLive.Inconclusive}: sc-{storyId} names only configured repositories ({string.Join(", ", named)}; configured: "
                + $"{string.Join(", ", options.ConfiguredRepos)}). Give it a `Repo:` line naming one the factory is not configured for.");
        }
        var before = await PlanLive.SiblingsAsync(shortcut, parent, ct);

        var outcome = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct);

        var history = await AssertRefusedWithNothingCreatedAsync(e2e, shortcut, parent, storyId, outcome, before, ct);
        Assert.Contains($"{string.Join(", ", unconfigured)} {(unconfigured.Count == 1 ? "is" : "are")} not configured for this factory", outcome.Error);
        Assert.Contains("no planner session was started and no story was created", outcome.Error);
        // No planner: no session, no worker process, no rejected plan (there was no plan to reject).
        Assert.DoesNotContain(history, e => e.Step is RunPipeline.Steps.Session or RunPipeline.Steps.WorkerStarted or RunPipeline.Steps.ModelClass
            or RunPipeline.Steps.PlanRejected);
        await using var db = e2e.Db();
        Assert.Empty(await db.WorkerSessions.AsNoTracking().ToListAsync(ct));
        Assert.Contains(await E2e.StoryCommentsAsync(storyId, ct), c => c.Contains("not configured for this factory", StringComparison.Ordinal));
    }

    /// <summary>
    /// P4-AT1 through the real intake loop (sc-25877): <c>factory work</c> (<see cref="FactoryHost.BuildWork"/>, in process, a
    /// throwaway ledger and work root) watching the plan story's scope picks the plan item up itself, plans it, releases its stories
    /// into that watch scope and picks each of them up from <c>ListReadyAsync</c> — no run of the plan item or of a story is started
    /// by the test — until the plan's changeset is on the default branch. The scope (FACTORY_E2E_WATCH_TEAM / FACTORY_E2E_WATCH_EPIC)
    /// must hold no other To Do story, and no other <c>factory work</c> may watch it: the host claims every ready story it sees.
    /// </summary>
    [Fact]
    public async Task P4_AT1_factory_work_picks_up_a_plan_item_in_its_watch_scope_releases_its_stories_into_it_and_picks_them_up()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P4_WORK_PLAN_STORY",
            "a To Do story labelled `plan` (sc-<id>) INSIDE the watch scope, on the sandbox repo, describing three steps that build on one another");
        var scope = E2e.WatchScope();
        await using var e2e = await E2e.StartAsync("df_e2e_plan_work");
        var options = e2e.Options(scope);
        await ReviewGateTests.RequireGateReadyAsync(options, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(WorkTimeout);
        var ct = timeout.Token;
        using var shortcut = Shortcut();
        await PlanStoryAsync(shortcut, storyId, ct);
        using var sourceHttp = OutboundHttp.ShortcutApi();
        var source = WorkSourceRegistry.Shortcut(options, sourceHttp);
        Assert.Equal("To Do", (await E2e.StoryAsync(storyId, ct)).State);
        Assert.True(await source.InScopeAsync(storyId, ct), $"sc-{storyId} is not inside the watch scope: the intake loop would never see it");
        // The loop takes every ready story of the scope: this run must own all of them.
        var ready = await source.ListReadyAsync(ct);
        Assert.True(ready.SequenceEqual([storyId]), $"the watch scope's ready stories must be exactly sc-{storyId} (the host claims every one it sees), "
            + $"but they are: {string.Join(", ", ready.Select(StoryId.Format))}");
        List<int> created = [];

        await using var app = FactoryHost.BuildWork(options);
        await app.StartAsync(ct);
        try
        {
            // Picked up in the watch scope and planned, by the loop.
            await FactoryWorkTests.WaitForStateAsync(e2e, storyId, WorkState.Planned, RunTimeout, ct);
            created = StoryIds(await e2e.HistoryAsync(storyId, ct));
            var plan = Plan.FromDetail(Assert.Single(await e2e.HistoryAsync(storyId, ct), e => e.Step == RunPipeline.Steps.Plan).Detail);
            if (PlanLive.ChainProblem(plan) is { } inconclusive)
            {
                Assert.Fail(inconclusive);
            }
            // Its stories are created inside the same watch scope (the parent's team and epic), so a release makes them the loop's.
            foreach (var id in created)
            {
                Assert.True(await source.InScopeAsync(id, ct), $"sc-{id} was created outside the watch scope");
            }

            // The loop alone takes the plan to its end: every story released, picked up, merged; then the changeset pull request.
            await E2e.WaitForAsync($"sc-{storyId}'s plan to be delivered by the intake loop", WorkTimeout, async () =>
            {
                foreach (var id in created)
                {
                    var last = (await e2e.TransitionsAsync(id, ct)).LastOrDefault();
                    Assert.False(last?.State == WorkState.Escalated, $"sc-{id} escalated: {last?.Detail}");
                }
                var transitions = await e2e.TransitionsAsync(storyId, ct);
                Assert.False(transitions[^1].State == WorkState.Escalated, $"sc-{storyId} escalated: {transitions[^1].Detail}");
                return transitions.Any(t => t.State == WorkState.Watch);
            }, ct);

            var history = await e2e.HistoryAsync(storyId, ct);
            Assert.Single(history, e => e.Step == RunPipeline.Steps.Session); // planned once
            var released = history.Where(e => e.Step == RunPipeline.Steps.PlanReleased).ToList();
            Assert.Equal(created.Select(StoryId.Format).Order(), released.Select(e => PlanRelease.StoriesOf(e.Detail)[0]).Order());
            // Released into the watch scope: no release says nothing would pick the story up.
            Assert.All(released, e => Assert.DoesNotContain("outside the watch scope", e.Detail));
            await using var db = e2e.Db();
            var planItem = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(storyId), ct);
            for (var i = 0; i < created.Count; i++)
            {
                var rows = await e2e.HistoryAsync(created[i], ct);
                var releasedAt = released.Single(e => PlanRelease.StoriesOf(e.Detail)[0] == StoryId.Format(created[i])).RecordedAt;
                // Picked up by the loop from the ready listing (nothing else ran it): claimed after its release, as the plan's story.
                var claimed = rows.FirstOrDefault(e => e.Step == RunPipeline.Steps.Claimed);
                Assert.True(claimed is not null, $"sc-{created[i]} was never claimed by the intake loop");
                Assert.True(claimed.RecordedAt >= releasedAt, $"sc-{created[i]} was claimed at {claimed.RecordedAt:O}, before its release at {releasedAt:O}");
                Assert.Equal(planItem.Id, (await db.WorkItems.AsNoTracking().SingleAsync(w => w.ExternalId == StoryId.Format(created[i]), ct)).PlanItemId);
                Assert.Contains(rows, e => e.Step is null && e.State == WorkState.Merge);
                // In dependency order: released only after each of its blockers' Merge row.
                foreach (var blocker in plan.Stories[i].BlockedBy.Select(key => created[plan.Stories.ToList().FindIndex(s => s.Key == key)]))
                {
                    var merged = (await e2e.HistoryAsync(blocker, ct)).First(e => e.Step is null && e.State == WorkState.Merge).RecordedAt;
                    Assert.True(releasedAt >= merged, $"sc-{created[i]} was released at {releasedAt:O}, before its blocker sc-{blocker} merged at {merged:O}");
                }
            }
            // And the changeset reached the default branch through the plan item's own gated pull request.
            var changeset = Changeset.Of(history, options.DefaultRepo)!;
            var pull = Assert.Single(await E2e.PullRequestsAsync(options.DefaultRepo, changeset.Branch, ct));
            Assert.Equal(changeset.Base, pull.GetProperty("base").GetProperty("ref").GetString());
            Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
            Assert.Contains(history, e => e.Step == RunPipeline.Steps.GatePassed);
            Assert.Equal(RunPipeline.Posted, Assert.Single(history, e => e.Step == RunPipeline.Steps.Closeout).Detail);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await ArchiveAsync(shortcut, created);
        }
    }

    /// <summary>The created stories are a test's own: archived so a rerun on the same plan story starts clean (best effort).</summary>
    private static async Task ArchiveAsync(HttpClient shortcut, IEnumerable<int> created)
    {
        foreach (var id in created)
        {
            try
            {
                using var response = await shortcut.PutAsJsonAsync($"stories/{id}", new { archived = true }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cleanup] could not archive sc-{id}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// P4-AT3's proxy part (sc-25874): the production loopback proxy with a planner grant over two repositories
    /// (<c>FACTORY_E2E_P4_PLAN_REPOS=owner/a,owner/b</c>, both indexed in the hosted CodeGraph) answers for each one, lists the
    /// planner's cross-repository tools, and refuses a third project, a call naming no repository, and tools off the planner's
    /// allowlist (<c>get_architecture</c>), nothing forwarded. No model is called: the test plays the planner's MCP client itself.
    /// </summary>
    [Fact]
    public async Task P4_AT3_a_planner_grant_over_two_repositories_answers_for_each_and_refuses_a_third_project_and_off_list_tools()
    {
        Harness.RequireOptIn();
        var options = Harness.Options;
        CodeGraphLive.RequireToken(options);
        var repos = Harness.RequireEnv("FACTORY_E2E_P4_PLAN_REPOS", "two repositories indexed in CodeGraph, comma-separated owner/name")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(RepoRef.Parse).ToList();
        Assert.True(repos.Count == 2, "FACTORY_E2E_P4_PLAN_REPOS names exactly two repositories");
        var ct = TestContext.Current.CancellationToken;
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        var upstreams = FactoryRunner.CreateUpstreams(options, codeGraphHttp, kanbanHttp: null, Console.Out);
        var projects = new List<string>();
        foreach (var repo in repos)
        {
            projects.Add(await CodeGraphLive.RequireProjectAsync(upstreams.CodeGraph!, repo, ct));
        }
        await using var proxy = await McpProxy.StartAsync(upstreams, ct);
        using var grant = proxy.Grant(repos, McpProfile.Planner);
        using var proxyHttp = OutboundHttp.LoopbackMcp(proxy.Endpoint(McpServers.CodeGraph));
        var client = new McpHttpClient(proxyHttp, grant.Credential, "proxy", "");
        var refusedCode = McpProxy.InvalidParams.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // tools/list: only the planner's tools, the cross-repository ones among them, each tool about one project taking `repo`.
        var listed = await client.ListToolsAsync(ct);
        Assert.All(listed, t => Assert.Contains(t.Name, McpProfile.Planner.ToolsOf(McpServers.CodeGraph)));
        Assert.All(PlannerCodeGraph.Tools, t => Assert.Contains(listed, l => l.Name == t));
        Assert.DoesNotContain(listed, t => t.Name is "get_architecture" or "get_service_summary" or "get_project_health" or "rag_search");
        Assert.Equal(repos.Select(r => r.FullName), listed.Single(t => t.Name == ReviewTools.SearchGraph).InputSchema["properties"]!["repo"]!["enum"]!
            .AsArray().Select(e => e!.GetValue<string>()));

        // An answer for each repository's project, labelled with it.
        foreach (var (repo, project) in repos.Zip(projects))
        {
            var answer = await client.CallToolAsync(ReviewTools.SearchGraph, new JsonObject { ["namePattern"] = "%", ["limit"] = 1, ["repo"] = repo.FullName }, ct);
            Assert.StartsWith($"CodeGraph's index of {project}'s default branch", answer.Text);
        }

        // A third project (any other one CodeGraph indexes), by name or as a repository; no repository at all; a tool off the list.
        var all = await client.CallToolAsync(PlannerCodeGraph.SearchProjects, new JsonObject { ["pageSize"] = 100 }, ct);
        Assert.StartsWith("CodeGraph's search_projects, across every project it indexes", all.Text);
        var third = System.Text.RegularExpressions.Regex.Matches(all.Text, @"^\s*-\s+\*\*(?<name>[^*\r\n]+)\*\*", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value.Trim()).FirstOrDefault(name => !projects.Contains(name));
        if (third is null)
        {
            Assert.Fail("INCONCLUSIVE: CodeGraph indexes no project outside FACTORY_E2E_P4_PLAN_REPOS to name as the third.");
        }
        foreach (var (tool, arguments) in new (string, JsonObject)[]
        {
            (ReviewTools.SearchGraph, new JsonObject { ["namePattern"] = "%", ["project"] = third }),
            (ReviewTools.SearchGraph, new JsonObject { ["namePattern"] = "%", ["repo"] = "someone/else" }),
            (ReviewTools.SearchGraph, new JsonObject { ["namePattern"] = "%" }),
            (PlannerCodeGraph.FileHotspot, new JsonObject { ["path"] = "README.md", ["repo"] = repos[0].FullName, ["project"] = third }),
            ("get_architecture", new JsonObject { ["project"] = projects[0] }),
            ("get_service_summary", new JsonObject { ["project"] = projects[0] }),
            ("rag_search", new JsonObject { ["query"] = "anything" }),
        })
        {
            var refused = await client.CallToolAsync(tool, arguments, ct);
            Assert.True(refused.IsError, $"{tool} {arguments.ToJsonString()} was answered: {refused.Text}");
            Assert.Contains(refusedCode, refused.Text);
        }
    }

    [Fact]
    public async Task A_plan_item_is_planned_by_a_real_planner_into_stories_released_in_dependency_order_merged_into_its_changeset_branch_and_then_into_the_default_branch()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P4_PLAN_STORY",
            "a To Do story labelled `plan` (sc-<id>) on the sandbox repo, describing three or more steps that build on one another");
        await using var e2e = await E2e.StartAsync("df_e2e_plan");
        var options = e2e.Options();
        await ReviewGateTests.RequireGateReadyAsync(options, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        using var shortcut = OutboundHttp.ShortcutApi();
        shortcut.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        var parent = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        Assert.True(parent.GetProperty("labels").EnumerateArray().Any(l => string.Equals(l.GetProperty("name").GetString(), PlanLabel.Name,
            StringComparison.OrdinalIgnoreCase)), $"sc-{storyId} does not carry the `{PlanLabel.Name}` label");
        List<int> created = [];
        try
        {
            // The story is named explicitly by FACTORY_E2E_P4_PLAN_STORY, like a manual `factory run --ignore-scope`.
            var outcome = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct);
            var history = await e2e.HistoryAsync(storyId, ct);
            created = StoryIds(history);

            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal(WorkState.Planned, outcome.State);
            Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned], history.Where(e => e.Step is null).Select(e => e.State));
            Assert.Null(outcome.PullRequestUrl);

            // The plan's changeset branch (sc-25872): recorded, then created from the default branch's head, before the stories
            // were recorded as the plan's. It is on GitHub at that commit, and nothing has been opened for it yet.
            var repo = options.DefaultRepo;
            var changeset = Changeset.Of(history, repo)!;
            Assert.Equal((repo.FullName, $"factory/changeset-sc-{storyId}"), (changeset.Repo, changeset.Branch));
            var steps = history.Where(e => e.Step is not null).Select(e => e.Step).ToList();
            Assert.True(steps.IndexOf(RunPipeline.Steps.PlanCreating) < steps.IndexOf(RunPipeline.Steps.Changeset)
                && steps.IndexOf(RunPipeline.Steps.Changeset) < steps.IndexOf(RunPipeline.Steps.ChangesetPushed)
                && steps.IndexOf(RunPipeline.Steps.ChangesetPushed) < steps.IndexOf(RunPipeline.Steps.PlanStories), string.Join(' ', steps));
            using var github = OutboundHttp.GitHubApi();
            var gate = new GitHubGate(github, new GitHubApp(github, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System));
            var defaultAtStart = (await gate.CompareAsync(repo, changeset.Base, changeset.BaseSha, ct)).BaseSha;
            Assert.Equal(0, (await gate.CompareAsync(repo, changeset.Branch, changeset.BaseSha, ct)).BehindBy);
            Assert.Empty(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));

            // One planner session, on the high class, tainted from its start.
            var session = Assert.Single(history, e => e.Step == RunPipeline.Steps.Session).ClaudeSessionId!;
            Assert.Equal(WorkerModelClass.High, Assert.Single(history, e => e.Step == RunPipeline.Steps.ModelClass).Detail);
            await using (var db = e2e.Db())
            {
                Assert.Contains(await db.SessionTaints.AsNoTracking().Where(t => t.ClaudeSessionId == session).Select(t => t.Reason).ToListAsync(ct),
                    reason => reason == Taint.Planner);
            }

            // The ledger's plan passes every rule, and the board holds exactly its stories: Backlog, marked, blocked as planned.
            var plan = Plan.FromDetail(Assert.Single(history, e => e.Step == RunPipeline.Steps.Plan).Detail);
            // The epic's scenario: at least 3 stories with a dependency chain. A plan without one proves nothing about release order.
            if (PlanLive.ChainProblem(plan) is { } inconclusive)
            {
                Assert.Fail(inconclusive);
            }
            Assert.Equal(plan.Stories.Count, created.Count);
            Assert.Equal(created.Count, created.Distinct().Count());
            var backlog = await BacklogStateAsync(shortcut, parent, ct);
            for (var i = 0; i < created.Count; i++)
            {
                var story = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{created[i]}", ct);
                Assert.Equal(plan.Stories[i].Title, story.GetProperty("name").GetString());
                Assert.Equal(backlog, story.GetProperty("workflow_state_id").GetInt64());
                Assert.True(PlannedStory.IsMarked(story.GetProperty("description").GetString()), $"sc-{created[i]} carries no plan marker");
                var blockers = story.GetProperty("story_links").EnumerateArray()
                    .Where(l => l.GetProperty("verb").GetString() == "blocks" && l.GetProperty("object_id").GetInt32() == created[i])
                    .Select(l => l.GetProperty("subject_id").GetInt32()).Order().ToList();
                var expected = plan.Stories[i].BlockedBy.Select(key => created[plan.Stories.ToList().FindIndex(s => s.Key == key)]).Order().ToList();
                Assert.Equal(expected, blockers);
            }

            // Every later run plans nothing and creates nothing: it evaluates the waiting plan (sc-25871) and releases exactly the
            // stories whose blockers the ledger shows merged — To Do on the real board, one plan-released row each — and leaves every
            // other story in the Backlog. Each released story is then really worked (sc-25872): implemented from the changeset
            // branch, reviewed, and merged by the gate into the changeset branch, which is what satisfies its dependents.
            var toDo = await StateAsync(shortcut, parent, "To Do", ct);
            var merged = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var run = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct);
                Assert.True(run.Succeeded, run.Error);
                Assert.Equal(WorkState.Planned, run.State);
                var now = await e2e.HistoryAsync(storyId, ct);
                Assert.Single(now, e => e.Step == RunPipeline.Steps.Session);
                Assert.Single(now, e => e.Step == RunPipeline.Steps.Plan);
                Assert.Equal(created, StoryIds(now));

                var released = now.Where(e => e.Step == RunPipeline.Steps.PlanReleased).Select(e => PlanRelease.StoriesOf(e.Detail)[0]).ToList();
                var unblocked = Enumerable.Range(0, created.Count).Where(i => plan.Stories[i].BlockedBy.All(merged.Contains)).ToList();
                Assert.Equal(unblocked.Select(i => $"sc-{created[i]}").Order(), released.Order());
                Assert.Equal(released.Count, released.Distinct().Count());
                for (var i = 0; i < created.Count; i++)
                {
                    var story = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{created[i]}", ct);
                    if (!merged.Contains(plan.Stories[i].Key))
                    {
                        Assert.Equal(unblocked.Contains(i) ? toDo : backlog, story.GetProperty("workflow_state_id").GetInt64());
                    }
                }

                // Another run with nothing newly merged adds no row and moves no story.
                Assert.True((await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct)).Succeeded);
                Assert.Equal(now.Count, (await e2e.HistoryAsync(storyId, ct)).Count);

                // The plan item is not reported merged while its changeset has not reached the default branch.
                Assert.DoesNotContain(now, e => e.Step is RunPipeline.Steps.Closeout or RunPipeline.Steps.MergedReported);
                var tier = unblocked.Where(i => !merged.Contains(plan.Stories[i].Key)).ToList();
                foreach (var i in tier)
                {
                    // The story's own run: its pull request targets the changeset branch and the gate merges it there.
                    var worked = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, created[i]), ignoreScope: true, Console.Out, ct);
                    Assert.True(worked.Succeeded, worked.Error);
                    Assert.Equal(WorkState.Watch, worked.State);
                    var pull = Assert.Single(await E2e.PullRequestsAsync(repo, created[i], ct));
                    Assert.Equal(changeset.Branch, pull.GetProperty("base").GetProperty("ref").GetString());
                    Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
                    var rows = await e2e.HistoryAsync(created[i], ct);
                    Assert.Equal(changeset.Branch, DarkFactory.Orchestrator.Controls.MergeFiles.FromDetail(rows.Last(e => e.Step == RunPipeline.Steps.MergeFiles).Detail)!.Base);
                    Assert.Contains(rows, e => e.Step == RunPipeline.Steps.Verdict);
                    // Its closeout on the real board is exactly the one rendered from its own ledger rows, every row typed (E7).
                    ReviewGateTests.AssertTypedOutcomes(rows);
                    Assert.Contains(PlanLive.Lines(await PlanLive.ExpectedStoryCloseoutAsync(e2e, created[i], ct)),
                        (await E2e.StoryCommentsAsync(created[i], ct)).Select(PlanLive.Lines));
                    merged.Add(plan.Stories[i].Key);
                }
                // Nothing of the plan is on the default branch yet: it has not moved because of these merges.
                Assert.Equal(defaultAtStart, (await gate.CompareAsync(repo, changeset.Base, changeset.BaseSha, ct)).BaseSha);
                if (merged.Count == created.Count)
                {
                    break;
                }
            }

            // Every story merged into the changeset branch: the plan item's next runs open exactly one pull request from it to the
            // default branch and take it through Review → CI → MergeGate → Merge. Only then is the plan reported merged.
            RunOutcome last;
            var runs = 0;
            do
            {
                last = await FactoryRunner.RunAsync(options, new ItemRef(ItemNaming.Shortcut, storyId), ignoreScope: true, Console.Out, ct);
                Assert.True(last.Succeeded, last.Error);
            }
            while (last.State != WorkState.Watch && ++runs < 4);
            Assert.Equal(WorkState.Watch, last.State);
            var final = await e2e.HistoryAsync(storyId, ct);
            Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
                final.Where(e => e.Step is null && e.State is not (WorkState.Fixing or WorkState.CIHealing) && e.Detail != "unpaused").Select(e => e.State).Distinct());
            var changesetPull = Assert.Single(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));
            Assert.Equal(changeset.Base, changesetPull.GetProperty("base").GetProperty("ref").GetString());
            Assert.NotEqual(JsonValueKind.Null, changesetPull.GetProperty("merged_at").ValueKind);
            Assert.Equal(last.PullRequestUrl, changesetPull.GetProperty("html_url").GetString());
            Assert.Contains($"Changeset of plan", changesetPull.GetProperty("body").GetString());
            // The whole changeset had the full panel on its own head, and the gate's own decision.
            var verdict = ReviewVerdict.FromDetail(final.Last(e => e.Step == RunPipeline.Steps.Verdict).Detail)!;
            Assert.True(verdict.Passed);
            ReviewGateTests.AssertHighClassPanel(verdict);
            Assert.Contains(final, e => e.Step == RunPipeline.Steps.GatePassed);
            Assert.Equal(changeset.Base, DarkFactory.Orchestrator.Controls.MergeFiles.FromDetail(final.Last(e => e.Step == RunPipeline.Steps.MergeFiles).Detail)!.Base);
            // The default branch now holds the merge commit the ledger names, and the board shows the plan done with one closeout.
            var mergeCommit = final.Last(e => e.Step is null && e.State == WorkState.Merge).Detail!;
            Assert.Equal(0, (await gate.CompareAsync(repo, changeset.Base, mergeCommit, ct)).BehindBy);
            Assert.Equal(RunPipeline.Posted, Assert.Single(final, e => e.Step == RunPipeline.Steps.Closeout).Detail);
            ReviewGateTests.AssertTypedOutcomes(final);
            // The closeout on the real board equals the ledger's (E5): exactly the comment rendered from the throwaway ledger's
            // rows — the plan item's before it was posted, and each story's own — not merely a comment that mentions the merge.
            var closeout = await PlanLive.ExpectedPlanCloseoutAsync(e2e, storyId, created, repo, ct);
            Assert.Contains($"sc-{storyId}'s plan is complete: {created.Count} of its {created.Count} stories merged", closeout);
            Assert.Contains($"Its changeset merged into `{changeset.Base}` as `{mergeCommit}`", closeout);
            Assert.Contains(PlanLive.Lines(closeout), (await E2e.StoryCommentsAsync(storyId, ct)).Select(PlanLive.Lines));
        }
        finally
        {
            await ArchiveAsync(shortcut, created);
        }
    }

    private static List<int> StoryIds(List<LedgerEntry> history) =>
        (history.LastOrDefault(e => e.Step == RunPipeline.Steps.PlanStories)?.Detail ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => StoryId.TryParse(r, out var id) ? id : throw new FormatException(r)).ToList();

    private static Task<long> BacklogStateAsync(HttpClient shortcut, JsonElement parent, CancellationToken ct) => StateAsync(shortcut, parent, "Backlog", ct);

    private static async Task<long> StateAsync(HttpClient shortcut, JsonElement parent, string name, CancellationToken ct)
    {
        var workflow = await shortcut.GetFromJsonAsync<JsonElement>($"workflows/{parent.GetProperty("workflow_id").GetInt64()}", ct);
        return workflow.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name).GetProperty("id").GetInt64();
    }
}
