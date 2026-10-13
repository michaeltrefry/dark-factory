using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4a's epic acceptance test P4-AT5 (sc-25876): in a plan A → B with an independent C (and a merged D), A escalating leaves B
/// unreleased while C goes on, the plan item says "held by A" once on the real board, the dashboard's data groups the stories under
/// the plan, and a Stop on the plan item stops the unfinished stories and cancels the plan, the merged one untouched. No model is
/// called: the plan is a fixed one whose stories this test creates on the real board through the work source, and the stories'
/// own progress (in flight, escalated, merged) is seeded in the throwaway ledger. Runbook: <c>docs/acceptance.md</c>.
/// </summary>
public class PlanFailureAcceptanceTests
{
    private const string Fixed = """
        {"stories":[
          {"key":"a","title":"[dark-factory P4-AT5] A","description":"Seeded by P4-AT5; never worked.","acceptance":["a is observable"],"type":"chore","blocked_by":[]},
          {"key":"b","title":"[dark-factory P4-AT5] B needs A","description":"Seeded by P4-AT5; never worked.","acceptance":["b is observable"],"type":"chore","blocked_by":["a"]},
          {"key":"c","title":"[dark-factory P4-AT5] C independent","description":"Seeded by P4-AT5; never worked.","acceptance":["c is observable"],"type":"chore","blocked_by":[]},
          {"key":"d","title":"[dark-factory P4-AT5] D merged","description":"Seeded by P4-AT5; never worked.","acceptance":["d is observable"],"type":"chore","blocked_by":[]}
        ]}
        """;

    /// <summary>Not live: the fixed plan passes every rule a planner's answer must (so the live test never fails on its own fixture).</summary>
    [Fact]
    public void The_fixed_plan_of_P4_AT5_is_a_valid_plan_of_a_then_b_with_an_independent_c_and_d()
    {
        var plan = Plan.FromDetail(Fixed);

        Assert.Equal(["a", "b", "c", "d"], plan.Stories.Select(s => s.Key));
        Assert.Equal(["", "a", "", ""], plan.Stories.Select(s => string.Join(",", s.BlockedBy)));
        Assert.Equal(plan.Stories.Select(s => s.Title), Plan.FromDetail(plan.ToDetail()).Stories.Select(s => s.Title));
    }

    [Fact]
    public async Task P4_AT5_a_failed_story_holds_its_dependent_the_plan_item_says_so_once_and_stop_on_the_plan_stops_its_stories()
    {
        Harness.RequireOptIn();
        var planId = E2e.Story("FACTORY_E2E_P4_HOLD_STORY",
            "a story (sc-<id>) on the sandbox repo's board that this test may comment on and move to the Backlog; it is not planned or worked");
        await using var e2e = await E2e.StartAsync("df_e2e_hold");
        var options = e2e.Options();
        var ct = TestContext.Current.CancellationToken;
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        var source = FactoryRunner.CreateWorkSource(options, shortcutHttp);
        var plan = Plan.FromDetail(Fixed);
        var planRef = $"sc-{planId}";
        var repo = options.DefaultRepo;
        var commentsBefore = (await E2e.StoryCommentsAsync(planId, ct)).Count;
        List<int> created = [];
        try
        {
            // The plan item as its Plan step leaves it (sc-25870), without a planner: the fixed plan's stories on the real board, the
            // throwaway ledger at Planned.
            await LedgerMigrations.MigrateAsync(e2e.Ledger, ct);
            created = [.. await source.CreateChildrenAsync(planId, plan.Children(planRef, repo), ct)];
            var (a, b, c, d) = (created[0], created[1], created[2], created[3]);
            var changeset = new Changeset(repo.FullName, ItemNaming.Shortcut.ChangesetBranchName(planId), "main", SeededBaseSha);
            Assert.Empty(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));
            await using (var db = e2e.Db())
            {
                var ledger = new WorkLedger(db, TimeProvider.System);
                var item = await ledger.GetOrCreateAsync(RunPipeline.Source, planRef, "P4-AT5 plan", repo.FullName, "seeded by P4-AT5", ct);
                await ledger.RecordAsync(item, WorkState.Plan, null, "seeded by P4-AT5", ct);
                await ledger.CheckpointAsync(item, RunPipeline.Steps.Plan, null, plan.ToDetail(), ct);
                await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanCreating, null, null, ct);
                // The production shape (sc-25872, sc-25877): the plan's changeset is recorded, then pushed, before its stories are
                // recorded as its own. Only the rows are seeded — nothing is pushed to the sandbox, so the branch has no pull
                // request — which is what the stop of a plan item reads: it drafts the changeset branch's pull requests.
                await ledger.CheckpointAsync(item, RunPipeline.Steps.Changeset, null, changeset.ToDetail(), ct);
                await ledger.CheckpointAsync(item, RunPipeline.Steps.ChangesetPushed, null, Changeset.PushedDetail(changeset.Branch, changeset.BaseSha, adopted: false), ct);
                var stories = string.Join(' ', created.Select(i => $"sc-{i}"));
                await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, null, stories, ct);
                await ledger.RecordAsync(item, WorkState.Planned, null, $"{created.Count} stories: {stories}", ct);
            }

            // First evaluation: A, C and D are released (To Do on the real board), B waits on A.
            var first = await FactoryRunner.RunAsync(options, source, planId, ignoreScope: true, Console.Out, ct);
            Assert.True(first.Succeeded, first.Error);
            Assert.Equal(["To Do", "Backlog", "To Do", "To Do"], await StatesAsync(created, ct));

            // A escalates, C is in flight, D merged — seeded in the throwaway ledger, which is all the plan reads.
            await SeedAsync(e2e, planRef, $"sc-{a}", repo.FullName, ct, WorkState.Implement, WorkState.Review, WorkState.Escalated);
            await SeedAsync(e2e, planRef, $"sc-{c}", repo.FullName, ct, WorkState.Implement, WorkState.Review);
            await SeedAsync(e2e, planRef, $"sc-{d}", repo.FullName, ct, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch);

            // The plan item is held by A: B stays unreleased, one plan-held row names A and its reason, one comment on the board.
            for (var poll = 0; poll < 3; poll++)
            {
                var held = await FactoryRunner.RunAsync(options, source, planId, ignoreScope: true, Console.Out, ct);
                Assert.True(held.Succeeded, held.Error);
                Assert.Equal(WorkState.Planned, held.State);
            }
            var history = await e2e.HistoryAsync(planId, ct);
            var row = Assert.Single(history, e => e.Step == RunPipeline.Steps.PlanHeld);
            Assert.StartsWith($"{PlanRelease.FailedPrefix}sc-{a} escalated at ", row.Detail);
            Assert.Contains("CI is red", row.Detail);
            Assert.Contains($"sc-{b} (behind sc-{a})", row.Detail);
            Assert.Equal(StepOutcome.Deferred, row.Outcome);
            Assert.Equal(RunPipeline.Posted, Assert.Single(history, e => e.Step == RunPipeline.Steps.PlanNotice).Detail);
            Assert.DoesNotContain(history, e => e.State == WorkState.Escalated);
            Assert.Equal("Backlog", (await StatesAsync([b], ct))[0]);
            var comments = (await E2e.StoryCommentsAsync(planId, ct)).Skip(commentsBefore).ToList();
            var comment = Assert.Single(comments);
            Assert.Contains($"{planRef} (plan) is held by a failed story", comment);
            Assert.Contains($"sc-{a} escalated", comment);
            Assert.Contains("CI is red", comment);

            // The dashboard's data, from the ledger: the plan row with its stories beneath it, each with its state and its blocker.
            var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(e2e.Ledger));
            var rows = await new DashboardData(contexts, TimeProvider.System).ActiveItemsAsync(ct);
            var view = rows.Single(r => r.ExternalId == planRef).Plan!;
            Assert.Equal(created.Select(i => $"sc-{i}"), view.Stories.Select(s => s.ExternalId));
            Assert.Equal(["Escalated", "not released", "Review", "Watch"], view.Stories.Select(s => s.Shown));
            Assert.Equal(["failed: escalated", $"held by sc-{a} (escalated)", "", ""], view.Stories.Select(s => s.Waits));
            Assert.Equal((1, 1, 1, 1, true, false), (view.Merged, view.InFlight, view.Waiting, view.Failed, view.Held, view.Stuck));
            Assert.All(rows.Where(r => r.ExternalId != planRef), r => Assert.Equal(rows.Single(p => p.ExternalId == planRef).Id, r.PlanItemId));

            // Stop on the plan item (as `factory stop --item sc-<plan>`): A and C stopped (Backlog on the real board, each with a
            // comment; their pull requests would be drafts again — these seeded stories have none), D untouched, B left in the
            // Backlog with nothing written, the plan Cancelled and told once.
            var stop = await FactoryRunner.ControlAsync(options, "stop", ControlScope.Item(planRef), "P4-AT5", Console.Out, ct);
            Assert.True(stop.Ok, stop.Message);
            Assert.Equal(0, stop.ExitCode);
            await using (var db = e2e.Db())
            {
                var states = await db.WorkItems.AsNoTracking().ToDictionaryAsync(i => i.ExternalId, i => i.State, ct);
                Assert.Equal((WorkState.Cancelled, WorkState.Cancelled, WorkState.Watch, WorkState.Cancelled),
                    (states[$"sc-{a}"], states[$"sc-{c}"], states[$"sc-{d}"], states[planRef]));
                Assert.False(states.ContainsKey($"sc-{b}"));
                Assert.Null(await db.Controls.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == ControlScope.Item(planRef), ct));
                // The plan item itself was stopped as a plan with a changeset is: its changeset branch's pull requests asked for in
                // the changeset's repository (there are none: nothing was drafted), then the board told.
                var planRows = await e2e.HistoryAsync(planId, ct);
                Assert.Equal(changeset, Changeset.Of(planRows, repo));
                var drafted = Assert.Single(planRows, e => e.Step == RunPipeline.Steps.PrsDrafted);
                Assert.Equal("none", drafted.Detail);
                Assert.Single(planRows, e => e.Step == RunPipeline.Steps.StopReported);
                foreach (var stopped in new[] { a, c })
                {
                    var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == $"sc-{stopped}", ct);
                    var steps = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).Select(e => e.Step).ToListAsync(ct);
                    Assert.Contains(RunPipeline.Steps.PrsDrafted, steps);
                    Assert.Contains(RunPipeline.Steps.StopReported, steps);
                }
            }
            Assert.Equal(["Backlog", "Backlog", "Backlog", "To Do"], await StatesAsync(created, ct));
            Assert.Contains(await E2e.StoryCommentsAsync(a, ct), text => text.Contains($"was stopped by P4-AT5 (with its plan {planRef})", StringComparison.Ordinal));
            Assert.Contains(await E2e.StoryCommentsAsync(c, ct), text => text.Contains($"was stopped by P4-AT5 (with its plan {planRef})", StringComparison.Ordinal));
            Assert.Empty(await E2e.StoryCommentsAsync(b, ct));
            Assert.Empty(await E2e.StoryCommentsAsync(d, ct));
            Assert.Equal("Backlog", (await E2e.StoryAsync(planId, ct)).State);
            var after = (await E2e.StoryCommentsAsync(planId, ct)).Skip(commentsBefore).ToList();
            Assert.Equal(2, after.Count);
            // As a plan with a changeset is stopped: its changeset branch is named as kept, with no pull request of it open.
            Assert.Contains($"It had no open pull request. Branch {changeset.Branch} is kept; nothing was merged or deleted.", after[1]);
            Assert.Contains($"Stories stopped with it: sc-{a}, sc-{c}. Already merged (left as they are): sc-{d}. "
                + $"Never started (left in the backlog; the factory will not release or start them): sc-{b}.", after[1]);

            // A stopped plan is not polled any more, and stopping it again does nothing.
            await using (var db = e2e.Db())
            {
                Assert.Empty(await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, FactoryRunner.Controls(options)));
            }
            Assert.True((await FactoryRunner.ControlAsync(options, "stop", ControlScope.Item(planRef), "P4-AT5", Console.Out, ct)).Ok);
            Assert.Equal(2, (await E2e.StoryCommentsAsync(planId, ct)).Count - commentsBefore);
        }
        finally
        {
            // The created stories are this test's own: archived so a rerun starts clean (best effort).
            shortcutHttp.DefaultRequestHeaders.Remove("Shortcut-Token");
            shortcutHttp.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
            foreach (var id in created)
            {
                try
                {
                    using var response = await shortcutHttp.PutAsJsonAsync($"stories/{id}", new { archived = true }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[cleanup] could not archive sc-{id}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>The commit the seeded <c>changeset</c> row names as the default branch's head: no commit of the sandbox (nothing reads it there).</summary>
    private const string SeededBaseSha = "0000000000000000000000000000000000000000";

    /// <summary>The workflow state name of each story, read straight from the Shortcut API.</summary>
    private static async Task<List<string>> StatesAsync(IReadOnlyList<int> stories, CancellationToken ct)
    {
        var states = new List<string>();
        foreach (var story in stories)
        {
            states.Add((await E2e.StoryAsync(story, ct)).State);
        }
        return states;
    }

    /// <summary>
    /// A story's ledger as its run would leave it, in the throwaway ledger only (nothing is implemented, opened or merged anywhere),
    /// linked to its plan as its first run links it. The last state's row carries the seeded reason.
    /// </summary>
    private static async Task SeedAsync(E2e e2e, string plan, string externalId, string repo, CancellationToken ct, params WorkState[] states)
    {
        await using var db = e2e.Db();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, externalId, externalId, repo, "seeded by P4-AT5", ct);
        await ledger.LinkPlanAsync(item, (await ledger.FindAsync(RunPipeline.Source, plan, ct))!.Id, ct);
        foreach (var state in states)
        {
            await ledger.RecordAsync(item, state, null, state == WorkState.Escalated ? "seeded by P4-AT5: CI is red (not a real failure)" : "seeded by P4-AT5", ct);
        }
    }
}
