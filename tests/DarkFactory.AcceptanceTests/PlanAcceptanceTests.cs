using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4a's epic acceptance test P4-AT1 (sc-25870, extended by sc-25871): a real planner session plans a <c>plan</c>-labelled story
/// into stories on the board, and the waiting plan item then releases them in dependency order. It goes as far as "stories released in
/// order" — the stories are not worked here (their Merge rows are seeded in the throwaway ledger) and the plan is not closed out;
/// later stories of the epic extend it. Runbook: <c>docs/acceptance.md</c>.
/// </summary>
public class PlanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(45);

    [Fact]
    public async Task A_plan_item_is_planned_by_a_real_planner_into_backlog_stories_created_once_and_released_in_dependency_order()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P4_PLAN_STORY",
            "a To Do story labelled `plan` (sc-<id>) on the sandbox repo, describing work worth two or more stories");
        await using var e2e = await E2e.StartAsync("df_e2e_plan");
        var options = e2e.Options();
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
            // other story in the Backlog. The stories are not worked here: each released tier's Merge rows are seeded in this test's
            // throwaway ledger, which is what the next run reads. The last tier is left unmerged, so the plan is never closed out
            // (a closeout on the real board would report seeded merges).
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
                    Assert.Equal(unblocked.Contains(i) ? toDo : backlog, story.GetProperty("workflow_state_id").GetInt64());
                }

                // Another run with nothing newly merged adds no row and moves no story.
                Assert.True((await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct)).Succeeded);
                Assert.Equal(now.Count, (await e2e.HistoryAsync(storyId, ct)).Count);

                if (released.Count == created.Count)
                {
                    break;
                }
                foreach (var i in unblocked.Where(i => merged.Add(plan.Stories[i].Key)))
                {
                    await SeedMergedAsync(e2e, $"sc-{created[i]}", options.DefaultRepo.FullName, ct);
                }
            }
            Assert.DoesNotContain(await e2e.HistoryAsync(storyId, ct), e => e.Step == RunPipeline.Steps.Closeout);
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

    /// <summary>
    /// The story's ledger as a run through the merge gate would leave it, in the throwaway ledger only: the Merge row is what
    /// satisfies its dependents (<see cref="PlanRelease.Satisfied"/>). Nothing is merged anywhere.
    /// </summary>
    private static async Task SeedMergedAsync(E2e e2e, string externalId, string repo, CancellationToken ct)
    {
        await using var db = e2e.Db();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, externalId, externalId, repo, "seeded by P4-AT1", ct);
        foreach (var state in new[] { WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch })
        {
            await ledger.RecordAsync(item, state, null, "seeded by P4-AT1 (not a real merge)", ct);
        }
    }
}
