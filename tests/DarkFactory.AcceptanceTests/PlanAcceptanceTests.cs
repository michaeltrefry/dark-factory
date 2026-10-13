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

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4a's epic acceptance test P4-AT1 (sc-25870, extended by sc-25871 and sc-25872): a real planner session plans a
/// <c>plan</c>-labelled story into stories on the board, the waiting plan item releases them in dependency order, each released story
/// is really worked and merged by the gate into the plan's changeset branch, and the changeset then goes to the default branch by
/// one pull request of the plan item's own, through the same review, CI and gate. Runbook: <c>docs/acceptance.md</c>.
/// </summary>
public class PlanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(45);

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
            "a To Do story labelled `plan` (sc-<id>) on the sandbox repo, describing work worth two or more stories");
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
            var outcome = await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct);
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
                var run = await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct);
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
                Assert.True((await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct)).Succeeded);
                Assert.Equal(now.Count, (await e2e.HistoryAsync(storyId, ct)).Count);

                // The plan item is not reported merged while its changeset has not reached the default branch.
                Assert.DoesNotContain(now, e => e.Step is RunPipeline.Steps.Closeout or RunPipeline.Steps.MergedReported);
                var tier = unblocked.Where(i => !merged.Contains(plan.Stories[i].Key)).ToList();
                foreach (var i in tier)
                {
                    // The story's own run: its pull request targets the changeset branch and the gate merges it there.
                    var worked = await FactoryRunner.RunAsync(options, created[i], ignoreScope: true, Console.Out, ct);
                    Assert.True(worked.Succeeded, worked.Error);
                    Assert.Equal(WorkState.Watch, worked.State);
                    var pull = Assert.Single(await E2e.PullRequestsAsync(repo, created[i], ct));
                    Assert.Equal(changeset.Branch, pull.GetProperty("base").GetProperty("ref").GetString());
                    Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
                    var rows = await e2e.HistoryAsync(created[i], ct);
                    Assert.Equal(changeset.Branch, DarkFactory.Orchestrator.Controls.MergeFiles.FromDetail(rows.Last(e => e.Step == RunPipeline.Steps.MergeFiles).Detail)!.Base);
                    Assert.Contains(rows, e => e.Step == RunPipeline.Steps.Verdict);
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
                last = await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct);
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
            Assert.Contains(await E2e.StoryCommentsAsync(storyId, ct), c => c.Contains($"sc-{storyId}'s plan is complete: {created.Count} of its {created.Count} stories merged")
                && c.Contains($"Its changeset merged into `{changeset.Base}` as `{mergeCommit}`"));
        }
        finally
        {
            // The created stories are this test's own: archived so a rerun on the same plan story starts clean (best effort).
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
