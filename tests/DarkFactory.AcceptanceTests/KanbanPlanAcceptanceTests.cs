using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4b's P4b-AT2 (sc-25984; docs/acceptance.md): a <c>plan</c>-labelled KanbanBoard item becomes at least 3 items with
/// dependencies on the real board — each created once in the plan item's project and epic, in Backlog — released in dependency
/// order, each really worked and merged by the gate into the plan's changeset branch, and the changeset then merged into the
/// default branch by one gated pull request of the plan item's own. Driven run by run (<c>factory run kb-N</c>), like P4-AT1.
/// </summary>
public class KanbanPlanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(120);

    private static async Task<List<LedgerEntry>> HistoryAsync(E2e e2e, string name, CancellationToken ct)
    {
        await using var db = e2e.Db();
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.Source == ItemNaming.Kanban.Source && i.ExternalId == name, ct);
        return item is null ? [] : await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private static ItemRef Item(string name) =>
        ItemNaming.ParseAny(name) is { } item && item.Naming == ItemNaming.Kanban ? item : throw new FormatException($"'{name}' is no Kanban item id.");

    /// <summary>The bodies of the item's comments on the real board (the typed caller reads no comments: the factory never does).</summary>
    internal static async Task<List<string>> CommentsAsync(Orchestrator.Mcp.McpHttpClient kanban, Guid item, CancellationToken ct)
    {
        var answer = await kanban.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = item.ToString() }, ct);
        Assert.False(answer.IsError, answer.Text);
        return JsonDocument.Parse(answer.Text).RootElement.GetProperty("comments").EnumerateArray()
            .Select(c => PlanLive.Lines(c.GetProperty("body").GetString())).ToList();
    }

    /// <summary>The plan item's closeout exactly as the pipeline rendered it: from its rows before its posted <c>closeout</c> row and each planned item's own rows.</summary>
    private static async Task<string> ExpectedPlanCloseoutAsync(E2e e2e, string plan, IReadOnlyList<string> stories, RepoRef primary, CancellationToken ct)
    {
        var history = await HistoryAsync(e2e, plan, ct);
        var closeout = history.FindIndex(e => e.Step == RunPipeline.Steps.Closeout && e.Detail == RunPipeline.Posted);
        Assert.True(closeout >= 0, $"{plan}'s ledger has no posted closeout row");
        var before = history.Take(closeout).ToList();
        var reports = new List<PlanStoryReport>();
        await using var db = e2e.Db();
        foreach (var story in stories)
        {
            var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Source == ItemNaming.Kanban.Source && i.ExternalId == story, ct);
            reports.Add(new PlanStoryReport(item.ExternalId, item.Title, await HistoryAsync(e2e, story, ct)));
        }
        var planItem = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Source == ItemNaming.Kanban.Source && i.ExternalId == plan, ct);
        return WorkSourceComments.Attributed(LedgerReport.PlanCloseout(plan, reports, null,
            (Changeset.CurrentPass(before), await new WorkLedger(db, TimeProvider.System).SessionCostsAsync(planItem, ct)),
            Changeset.States(before, primary, WorkState.Merge)));
    }

    [Fact]
    public async Task P4b_AT2_a_plan_labelled_kanban_item_becomes_items_with_dependencies_worked_in_order_onto_a_changeset_branch_and_merged_by_one_gated_changeset_pull_request()
    {
        Harness.RequireOptIn();
        var itemId = Harness.RequireEnv("FACTORY_E2E_P4B_PLAN_KANBAN_ITEM",
            "the id (GUID) of a Ready, unclaimed, unblocked KanbanBoard item labelled `plan`, in an epic, whose work lands in the sandbox repo, "
            + "describing three or more steps that build on one another (a fresh item each run)");
        Assert.True(Guid.TryParse(itemId, out var guid), $"FACTORY_E2E_P4B_PLAN_KANBAN_ITEM='{itemId}' is not a GUID");
        var project = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_PROJECT", "the key of that item's project (the test's Kanban:Watch:Projects)");
        var writeToken = Harness.RequireSecret(o => o.KanbanWriteToken);
        await using var e2e = await E2e.StartAsync("df_p4b_at2", ItemNaming.Kanban);
        var options = e2e.Options(new Dictionary<string, string?> { [KanbanScope.Setting] = project });
        await ReviewGateTests.RequireGateReadyAsync(options, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;

        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var mcp = KanbanWrites.Client(kanbanHttp, writeToken);
        var board = new KanbanBoard(mcp);
        var parent = await board.GetItemAsync(guid, ct);
        Assert.True(parent is { Status: KanbanStatus.Ready, ClaimMarker: null } && parent.OpenBlockers.Count == 0,
            $"The plan item must be Ready, unclaimed and unblocked; it is {parent.Status}, claimed by {parent.Claimant ?? "nobody"}, "
            + $"with {parent.OpenBlockers.Count} open blocker(s).");
        Assert.True((parent.LabelList ?? []).Contains(PlanLabel.Name, StringComparer.OrdinalIgnoreCase), $"the item does not carry the `{PlanLabel.Name}` label");
        Assert.True(parent.EpicId is not null, "the plan item is in no epic: put it in one (the epic's scenario creates the planned items in it)");

        var name = await FactoryRunner.KanbanKeyAsync(options, guid, ct);
        var created = new List<(string Name, Guid Id)>();
        try
        {
            // The plan item's first run: one planner session, the plan's items on the board, the changeset branch, Planned.
            var outcome = await FactoryRunner.RunAsync(options, Item(name), ignoreScope: false, Console.Out, ct);
            Assert.True(outcome.Succeeded, outcome.Error);
            Assert.Equal(WorkState.Planned, outcome.State);
            var history = await HistoryAsync(e2e, name, ct);
            Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned], history.Where(e => e.Step is null).Select(e => e.State));
            Assert.Single(history, e => e.Step == RunPipeline.Steps.Session);
            var plan = Plan.FromDetail(Assert.Single(history, e => e.Step == RunPipeline.Steps.Plan).Detail);
            // The epic's scenario: at least 3 items with a dependency chain. A plan without one proves nothing about release order.
            if (PlanLive.ChainProblem(plan) is { } inconclusive)
            {
                Assert.Fail(inconclusive);
            }

            // The ledger names the plan's items kb-<n>; each is one item of the real board, created in the plan item's project and
            // epic, in Backlog, marked by code as planned from the plan item's GUID, and blocked by exactly the items the plan says.
            var names = PlanRelease.StoriesOf(Assert.Single(history, e => e.Step == RunPipeline.Steps.PlanStories).Detail);
            Assert.Equal(plan.Stories.Count, names.Count);
            await using (var db = e2e.Db())
            {
                foreach (var story in names)
                {
                    var key = Item(story).Id;
                    created.Add((story, (await db.KanbanItems.AsNoTracking().SingleAsync(k => k.Id == key, ct)).ItemId));
                }
            }
            Assert.Equal(created.Count, created.Select(c => c.Id).Distinct().Count());
            Assert.DoesNotContain(guid, created.Select(c => c.Id));
            for (var i = 0; i < created.Count; i++)
            {
                var item = await board.GetItemAsync(created[i].Id, ct);
                Assert.Equal((plan.Stories[i].Title, KanbanStatus.Backlog, parent.ProjectId, parent.EpicId), (item.Title, item.Status, item.ProjectId, item.EpicId));
                Assert.Equal(KanbanWorkSource.ItemType(plan.Stories[i].Type), item.Type);
                Assert.Equal(guid.ToString("D"), PlannedStory.ParentOf(item.Description));
                Assert.Null(item.ClaimMarker);
                var expected = plan.Stories[i].BlockedBy.Select(k => created[plan.Stories.ToList().FindIndex(s => s.Key == k)].Id).Order().ToList();
                Assert.Equal(expected, (item.Blockers ?? []).Select(b => b.Id).Order());
            }

            // The changeset branch, named after the plan item's ledger name, with nothing opened for it yet.
            await using var ledger = e2e.Db();
            var repo = RepoRef.Parse((await ledger.WorkItems.AsNoTracking().SingleAsync(w => w.ExternalId == name, ct)).Repo);
            var changeset = Changeset.Of(history, repo)!;
            Assert.Equal((repo.FullName, ItemNaming.Kanban.ChangesetBranchName(Item(name).Id)), (changeset.Repo, changeset.Branch));
            Assert.Empty(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));

            // Every later run of the plan item plans nothing and creates nothing: it releases exactly the items whose blockers the
            // ledger shows merged — Ready on the real board, one plan-released row each — and leaves the others in Backlog. Each
            // released item is then really worked and merged by the gate into the changeset branch, which makes it Done on the
            // board and is what releases its dependents.
            var merged = new HashSet<string>(StringComparer.Ordinal);
            while (merged.Count < created.Count)
            {
                var run = await FactoryRunner.RunAsync(options, Item(name), ignoreScope: false, Console.Out, ct);
                Assert.True(run.Succeeded, run.Error);
                Assert.Equal(WorkState.Planned, run.State);
                var now = await HistoryAsync(e2e, name, ct);
                Assert.Single(now, e => e.Step == RunPipeline.Steps.Session);
                Assert.Equal(names, PlanRelease.StoriesOf(now.Last(e => e.Step == RunPipeline.Steps.PlanStories).Detail));

                var released = now.Where(e => e.Step == RunPipeline.Steps.PlanReleased).ToList();
                var unblocked = Enumerable.Range(0, created.Count).Where(i => plan.Stories[i].BlockedBy.All(merged.Contains)).ToList();
                Assert.Equal(unblocked.Select(i => created[i].Name).Order(), released.Select(e => PlanRelease.StoriesOf(e.Detail)[0]).Order());
                // Released inside the watched project: nothing says a released item would not be picked up.
                Assert.All(released, e => Assert.DoesNotContain("outside the watch scope", e.Detail));
                for (var i = 0; i < created.Count; i++)
                {
                    if (!merged.Contains(plan.Stories[i].Key))
                    {
                        Assert.Equal(unblocked.Contains(i) ? KanbanStatus.Ready : KanbanStatus.Backlog, (await board.GetItemAsync(created[i].Id, ct)).Status);
                    }
                }
                // Another run with nothing newly merged adds no row and moves no item.
                Assert.True((await FactoryRunner.RunAsync(options, Item(name), ignoreScope: false, Console.Out, ct)).Succeeded);
                Assert.Equal(now.Count, (await HistoryAsync(e2e, name, ct)).Count);
                Assert.DoesNotContain(now, e => e.Step is RunPipeline.Steps.Closeout or RunPipeline.Steps.MergedReported);

                var tier = unblocked.Where(i => !merged.Contains(plan.Stories[i].Key)).ToList();
                Assert.NotEmpty(tier);
                foreach (var i in tier)
                {
                    var worked = await FactoryRunner.RunAsync(options, Item(created[i].Name), ignoreScope: false, Console.Out, ct);
                    Assert.True(worked.Succeeded, worked.Error);
                    Assert.Equal(WorkState.Watch, worked.State);
                    // Its pull request targets the changeset branch and the gate merged it there; the board shows it Done.
                    var pull = Assert.Single(await E2e.PullRequestsAsync(repo, ItemNaming.Kanban.BranchName(Item(created[i].Name).Id), ct));
                    Assert.Equal(changeset.Branch, pull.GetProperty("base").GetProperty("ref").GetString());
                    Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
                    var rows = await HistoryAsync(e2e, created[i].Name, ct);
                    Assert.Equal(changeset.Branch, Orchestrator.Controls.MergeFiles.FromDetail(rows.Last(e => e.Step == RunPipeline.Steps.MergeFiles).Detail)!.Base);
                    Assert.Contains(rows, e => e.Step == RunPipeline.Steps.Verdict);
                    Assert.Contains(rows, e => e.Step == RunPipeline.Steps.GatePassed);
                    ReviewGateTests.AssertTypedOutcomes(rows);
                    // Claimed only after its release, and as the plan's item.
                    var releasedAt = released.Single(e => PlanRelease.StoriesOf(e.Detail)[0] == created[i].Name).RecordedAt;
                    Assert.True(rows.First(e => e.Step == RunPipeline.Steps.Claimed).RecordedAt >= releasedAt, $"{created[i].Name} was claimed before its release");
                    Assert.Equal(KanbanStatus.Done, (await board.GetItemAsync(created[i].Id, ct)).Status);
                    merged.Add(plan.Stories[i].Key);
                }
            }

            // Every item merged into the changeset branch: the plan item's next runs open exactly one pull request from it to the
            // default branch and take it through Review → CI → MergeGate → Merge. Only then is the plan item Done on the board.
            RunOutcome last;
            var runs = 0;
            do
            {
                last = await FactoryRunner.RunAsync(options, Item(name), ignoreScope: false, Console.Out, ct);
                Assert.True(last.Succeeded, last.Error);
            }
            while (last.State != WorkState.Watch && ++runs < 4);
            Assert.Equal(WorkState.Watch, last.State);
            var final = await HistoryAsync(e2e, name, ct);
            Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
                final.Where(e => e.Step is null && e.State is not (WorkState.Fixing or WorkState.CIHealing) && e.Detail != "unpaused").Select(e => e.State).Distinct());
            var changesetPull = Assert.Single(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));
            Assert.Equal(changeset.Base, changesetPull.GetProperty("base").GetProperty("ref").GetString());
            Assert.NotEqual(JsonValueKind.Null, changesetPull.GetProperty("merged_at").ValueKind);
            Assert.Equal(last.PullRequestUrl, changesetPull.GetProperty("html_url").GetString());
            Assert.Contains(final, e => e.Step == RunPipeline.Steps.GatePassed);
            Assert.Equal(changeset.Base, Orchestrator.Controls.MergeFiles.FromDetail(final.Last(e => e.Step == RunPipeline.Steps.MergeFiles).Detail)!.Base);
            ReviewGateTests.AssertTypedOutcomes(final);
            // Each planned item was created once: the ledger still names the same items, and no key was given twice.
            Assert.Equal(names, PlanRelease.StoriesOf(Assert.Single(final, e => e.Step == RunPipeline.Steps.PlanStories).Detail));
            Assert.Single(final, e => e.Step == RunPipeline.Steps.PlanCreating);
            // The board: the plan item Done, its changeset pull request linked on it, and one closeout equal to the ledger's own
            // rendering of the throwaway ledger's rows (E5).
            var done = await board.GetItemAsync(guid, ct);
            Assert.Equal(KanbanStatus.Done, done.Status);
            Assert.True(done.HasLink("pr", last.PullRequestUrl!), $"{name} does not carry its changeset pull request as a link");
            Assert.Equal(RunPipeline.Posted, Assert.Single(final, e => e.Step == RunPipeline.Steps.Closeout).Detail);
            var closeout = await ExpectedPlanCloseoutAsync(e2e, name, names, repo, ct);
            Assert.Contains($"{name}'s plan is complete: {created.Count} of its {created.Count} stories merged", closeout);
            Assert.Contains(PlanLive.Lines(closeout), await CommentsAsync(mcp, guid, ct));
        }
        finally
        {
            await CloseUnfinishedAsync(board, created.Select(c => c.Id));
        }
    }

    /// <summary>
    /// The planned items are the test's own: one a failed run left unfinished is closed, so nothing picks it up afterwards (best
    /// effort; a finished one stays Done). A rerun needs a fresh plan item: this one's idempotency keys answer the same items again.
    /// </summary>
    private static async Task CloseUnfinishedAsync(KanbanBoard board, IEnumerable<Guid> created)
    {
        foreach (var id in created)
        {
            try
            {
                var item = await board.GetItemAsync(id, CancellationToken.None);
                if (item.Status is KanbanStatus.Backlog or KanbanStatus.Ready)
                {
                    await board.SetStatusAsync(id, KanbanStatus.Closed, item.Version, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cleanup] could not close Kanban item {id}: {ex.Message}");
            }
        }
    }
}
