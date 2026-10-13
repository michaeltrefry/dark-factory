using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25876: every story of a plan classified from ledger rows — merged, in flight, waiting, failed, blocked behind a failed one.</summary>
public class PlanClassificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static LedgerEntry Row(WorkState state, string? detail = null, string? step = null) =>
        new() { State = state, Step = step, Detail = detail, RecordedAt = T0, Outcome = StepOutcome.Passed };

    private static readonly LedgerEntry[] Merged = [Row(WorkState.Intake), Row(WorkState.MergeGate), Row(WorkState.Merge), Row(WorkState.Watch)];
    private static readonly LedgerEntry[] Working = [Row(WorkState.Intake), Row(WorkState.Implement), Row(WorkState.Review)];
    private static LedgerEntry[] Escalated(string reason = "CI is red") => [Row(WorkState.Intake), Row(WorkState.Implement), Row(WorkState.Escalated, reason)];

    private static PlanStatus Classify(string[] stories, Dictionary<string, string[]> blockers, Dictionary<string, LedgerEntry[]> rows, params string[] released) =>
        PlanRelease.Classify(stories, released.ToHashSet(), s => blockers.GetValueOrDefault(s) ?? [], s => rows.GetValueOrDefault(s));

    private static string States(PlanStatus status) => string.Join(' ', status.Stories.Select(s => $"{s.Id}:{s.State}"));

    [Fact]
    public void A_story_failed_when_its_last_transition_is_escalated_or_cancelled_and_it_never_merged()
    {
        Assert.Equal(PlanRelease.AsEscalated, PlanRelease.FailureOf(Escalated())!.As);
        Assert.Equal(("CI is red", T0), (PlanRelease.FailureOf(Escalated())!.Reason, PlanRelease.FailureOf(Escalated())!.At));
        Assert.Equal(PlanRelease.AsCancelled, PlanRelease.FailureOf([Row(WorkState.Intake), Row(WorkState.Cancelled, "stopped by tester")])!.As);
        // Its pull request was merged by someone else: the review, or the merge queue, found it merged and escalated.
        Assert.Equal(PlanRelease.AsMergedOutside, PlanRelease.FailureOf(
            Escalated("InvalidOperationException: https://github.com/acme/widgets/pull/9 is merged outside the factory."))!.As);
        Assert.Equal(PlanRelease.AsMergedOutside, PlanRelease.FailureOf(
            Escalated("GateBlockedException: https://github.com/acme/widgets/pull/9 was merged outside the gate."))!.As);
        // A checkpoint after the escalation (its comment) changes nothing.
        Assert.NotNull(PlanRelease.FailureOf([.. Escalated(), Row(WorkState.Escalated, "posted", RunPipeline.Steps.EscalationComment)]));

        // Not failed: unknown to the ledger, in flight, re-queued by a person after the escalation, merged (whatever came after).
        Assert.Null(PlanRelease.FailureOf(null));
        Assert.Null(PlanRelease.FailureOf(Working));
        Assert.Null(PlanRelease.FailureOf([.. Escalated(), Row(WorkState.Intake, "re-run after escalation")]));
        Assert.Null(PlanRelease.FailureOf(Merged));
        Assert.Null(PlanRelease.FailureOf([.. Merged, Row(WorkState.Escalated, "the closeout failed")]));
    }

    [Fact]
    public void A_failed_story_blocks_what_depends_on_it_and_leaves_an_independent_story_alone()
    {
        // A -> B, and C independent: A escalated.
        var status = Classify(["a", "b", "c"], new() { ["b"] = ["a"] }, new() { ["a"] = Escalated(), ["c"] = Working }, "a", "c");

        Assert.Equal("a:Failed b:Blocked c:InFlight", States(status));
        Assert.Equal(["a"], status.Stories[1].HeldBy);
        Assert.Equal(["a"], status.Stories[1].WaitsOn);
        Assert.True(status.Held);
        Assert.False(status.Stuck); // C is still worked
        Assert.Equal(WorkState.Escalated, status.Stories[0].ItemState);
    }

    [Fact]
    public void A_hold_is_transitive_and_names_the_failed_story_at_its_root()
    {
        // a -> b -> c -> d; e waits on a merged story and on nothing failed.
        var status = Classify(["a", "b", "c", "d", "m", "e"], new() { ["b"] = ["a"], ["c"] = ["b"], ["d"] = ["c", "m"], ["e"] = ["m"] },
            new() { ["a"] = Escalated(), ["m"] = Merged }, "a", "m");

        Assert.Equal("a:Failed b:Blocked c:Blocked d:Blocked m:Merged e:Waiting", States(status));
        Assert.All(status.Blocked, b => Assert.Equal(["a"], b.HeldBy));
        Assert.Equal(["c"], status.Stories[3].WaitsOn); // its merged blocker is not waited on
        Assert.Empty(status.Stories[5].WaitsOn);
        Assert.False(status.Stuck); // e is about to be released
    }

    [Fact]
    public void A_plan_is_stuck_when_every_story_that_has_not_merged_failed_or_is_blocked_behind_a_failed_one()
    {
        var blockers = new Dictionary<string, string[]> { ["b"] = ["a"], ["d"] = ["c"] };

        var stuck = Classify(["a", "b", "c", "d"], blockers, new() { ["a"] = Escalated(), ["c"] = Merged, ["d"] = Escalated("no") }, "a", "c", "d");
        Assert.Equal("a:Failed b:Blocked c:Merged d:Failed", States(stuck));
        Assert.True(stuck.Stuck);

        // One story still worked, or still to be released: held, not stuck.
        Assert.False(Classify(["a", "b", "c", "d"], blockers, new() { ["a"] = Escalated(), ["c"] = Merged, ["d"] = Working }, "a", "c", "d").Stuck);
        Assert.False(Classify(["a", "b", "c", "d"], blockers, new() { ["a"] = Escalated(), ["c"] = Merged }, "a", "c").Stuck);
        // Nothing failed: neither.
        var fine = Classify(["a", "b"], new() { ["b"] = ["a"] }, new() { ["a"] = Working }, "a");
        Assert.Equal((false, false, "a:InFlight b:Waiting"), (fine.Held, fine.Stuck, States(fine)));
    }

    [Fact]
    public void A_failed_story_that_is_re_run_holds_nothing_and_once_merged_releases_its_dependents()
    {
        var blockers = new Dictionary<string, string[]> { ["b"] = ["a"] };

        var requeued = Classify(["a", "b"], blockers, new() { ["a"] = [.. Escalated(), Row(WorkState.Intake)] }, "a");
        Assert.Equal((false, "a:InFlight b:Waiting"), (requeued.Held, States(requeued)));
        Assert.Equal(["a"], requeued.Stories[1].WaitsOn);

        var merged = Classify(["a", "b"], blockers, new() { ["a"] = [.. Escalated(), Row(WorkState.Intake), .. Merged] }, "a");
        Assert.Equal("a:Merged b:Waiting", States(merged));
        Assert.Empty(merged.Stories[1].WaitsOn);
    }

    [Fact]
    public void A_story_parked_on_a_refused_claim_still_only_waits_and_a_blocker_outside_the_plan_is_waited_on_until_satisfied()
    {
        LedgerEntry[] parked = [Row(WorkState.Intake), Row(WorkState.Paused, $"{RunPipeline.ClaimRefused}: {PlanRelease.Refusal(["a"])}")];
        var status = PlanRelease.Classify(["a", "b", "c"], new HashSet<string> { "a" }, s => s == "b" ? ["a"] : s == "c" ? ["sc-777"] : [],
            s => s == "a" ? Escalated() : s == "b" ? parked : null, id => false);

        Assert.Equal("a:Failed b:Blocked c:Waiting", States(status));
        Assert.Equal(["sc-777"], status.Stories[2].WaitsOn);
        Assert.Empty(PlanRelease.Classify(["c"], new HashSet<string>(), _ => ["sc-777"], _ => null, id => true).Stories[0].WaitsOn);
    }

    [Fact]
    public void The_hold_is_recorded_with_each_failed_storys_state_time_and_reason_clipped_and_inert_and_what_it_blocks()
    {
        var reason = "@owner see [this](http://evil.example) <b>now</b> `x`\nsecond line " + new string('y', 500);
        var status = Classify(["a", "b", "c"], new() { ["b"] = ["a"], ["c"] = ["b"] }, new() { ["a"] = Escalated(reason) }, "a");

        var held = PlanRelease.HeldByFailed(status);

        Assert.StartsWith("a escalated at 2026-10-12 12:00:00Z: `", held);
        Assert.EndsWith("; not released because of them: b (behind a), c (behind a)", held);
        // One line, no mention, link, markup or backtick of the reason's own, and no more of it than the bound.
        Assert.DoesNotContain('\n', held);
        Assert.DoesNotContain('@', held);
        Assert.DoesNotContain("](", held);
        Assert.DoesNotContain("<b>", held);
        Assert.DoesNotContain("://", held);
        Assert.Equal(2, held.Count(c => c == '`'));
        Assert.Contains("more characters; the ledger has them", held);
        Assert.True(held.Length < PlanRelease.MaxFailureReason + 250, held);
        // Nothing failed: nothing to record.
        Assert.Equal("", PlanRelease.HeldByFailed(Classify(["a"], new(), new() { ["a"] = Working }, "a")));
        Assert.Contains("`none recorded`", PlanRelease.HeldByFailed(Classify(["a"], new(), new() { ["a"] = [Row(WorkState.Intake), Row(WorkState.Escalated)] }, "a")));
    }

    [Fact]
    public void The_comment_names_the_failed_and_blocked_stories_and_both_remedies_and_a_cancelled_story_cannot_be_re_run()
    {
        var status = Classify(["a", "b", "c"], new() { ["b"] = ["a"] },
            new() { ["a"] = Escalated(), ["c"] = [Row(WorkState.Intake), Row(WorkState.Cancelled, "stopped by tester")] }, "a", "c");

        var comment = PlanRelease.HeldComment("sc-500", status);

        Assert.StartsWith("[author: dark-factory] sc-500 (plan) is stuck: every story of it that has not merged failed or is blocked behind a failed one", comment);
        Assert.Contains("- a escalated at 2026-10-12 12:00:00Z: `CI is red`", comment);
        Assert.Contains("- c cancelled at 2026-10-12 12:00:00Z: `stopped by tester`", comment);
        Assert.Contains("- b (behind a)", comment);
        Assert.Contains("fix what failed and re-run the story (`factory run a`)", comment);
        Assert.Contains("stop the plan (`factory stop --item sc-500`", comment);
        Assert.Contains("A cancelled story (c) cannot be re-run", comment);
        Assert.Contains("0 of 3 merged, 0 in flight, 0 waiting, 2 failed, 1 blocked behind a failed story", comment);

        var held = PlanRelease.HeldComment("sc-500", Classify(["a", "b", "c"], new() { ["b"] = ["a"] }, new() { ["a"] = Escalated(), ["c"] = Working }, "a", "c"));
        Assert.StartsWith("[author: dark-factory] sc-500 (plan) is held by a failed story", held);
        Assert.DoesNotContain("stuck", held);
    }

    [Fact]
    public void The_plans_titles_are_read_from_its_plan_checkpoint_by_the_stories_ids()
    {
        var plan = PlanParser.Parse(PlanAnswers.Valid);

        var titles = PlanRelease.RecordedTitles(plan.ToDetail(), ["sc-1", "sc-2", "sc-3"])!;

        Assert.Equal(plan.Stories.Select(s => s.Title), [titles["sc-1"], titles["sc-2"], titles["sc-3"]]);
        Assert.Null(PlanRelease.RecordedTitles(plan.ToDetail(), ["sc-1"]));
        Assert.Null(PlanRelease.RecordedTitles("not a plan", ["sc-1", "sc-2", "sc-3"]));
    }

    [Theory]
    [InlineData(RunPipeline.Steps.PlanHeld, "held by a failed story: sc-1 escalated", StepOutcome.Deferred)]
    [InlineData(RunPipeline.Steps.PlanStuck, "sc-1 escalated", StepOutcome.Deferred)]
    [InlineData(RunPipeline.Steps.PlanResumed, "no story of the plan is failed any more", StepOutcome.Passed)]
    [InlineData(RunPipeline.Steps.PlanNotice, "posted", StepOutcome.Passed)]
    [InlineData(RunPipeline.Steps.PlanNotice, "failed: 500", StepOutcome.Failed)]
    public void Every_step_of_a_held_plan_has_a_typed_outcome_and_none_is_an_escalation(string step, string detail, StepOutcome expected) =>
        Assert.Equal(expected, StepOutcomes.Of(WorkState.Planned, WorkState.Planned, step, detail));

    [Fact]
    public void A_waiting_plan_may_be_cancelled_and_paused_and_returns_from_a_pause_only_to_planned()
    {
        var none = new TransitionContext();
        Assert.Null(Lifecycle.Check(WorkState.Planned, WorkState.Cancelled, none));
        Assert.Null(Lifecycle.Check(WorkState.Planned, WorkState.Paused, none));
        Assert.Null(Lifecycle.Check(WorkState.Paused, WorkState.Planned, new TransitionContext(PausedFrom: WorkState.Planned)));
        Assert.NotNull(Lifecycle.Check(WorkState.Paused, WorkState.Implement, new TransitionContext(PausedFrom: WorkState.Planned)));
        Assert.NotNull(Lifecycle.Check(WorkState.Cancelled, WorkState.Planned, none)); // a stopped plan stays stopped
        Assert.Equal(StepOutcome.Deferred, StepOutcomes.Of(WorkState.Planned, WorkState.Cancelled, null, "stopped by tester"));
    }
}

/// <summary>
/// sc-25876: a failed story holds its dependents and the plan item says so once; Pause, Continue and Stop on a plan item reach its
/// stories; the dashboard's data groups them. Through the real pipeline and the Shortcut adapter over a fake board and an in-memory
/// ledger.
/// </summary>
public class PlanFailurePipelineTests
{
    private const int PlanId = 500;
    private const string Session = "sess-plan-story";
    private static readonly string Plan = $"sc-{PlanId}";

    /// <summary>A, B that needs A, and C that needs nothing.</summary>
    private static string Fork => PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
        PlanAnswers.Story("c", "Third story"));

    private static string Chain => PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
        PlanAnswers.Story("c", "Third story", "b"));

    /// <summary>Works in tool calls until cancelled or, honouring a pause, until the call after the request; a resumed session finishes at once.</summary>
    private sealed class ToolWorker(TaskCompletionSource? holdCancel = null) : IWorker
    {
        private int _pause;
        public List<string?> Resumes { get; } = [];
        public TaskCompletionSource Working { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, string modelClass, WorkerCallbacks? callbacks, CancellationToken ct)
        {
            Resumes.Add(resumeSessionId);
            await callbacks!.OnStarted!(WorkerPid, ct);
            await callbacks.OnSession!(Session, ct);
            if (resumeSessionId is not null)
            {
                return new WorkerResult(Session, 0, false, "success", "done", "");
            }
            Working.TrySetResult();
            try
            {
                while (true)
                {
                    if (Volatile.Read(ref _pause) == 1)
                    {
                        return new WorkerResult(Session, 0, false, "success", "", "", WorkerResult.HookStoppedReason);
                    }
                    await Task.Delay(5, ct);
                }
            }
            catch (OperationCanceledException)
            {
                CancelSeen.TrySetResult();
                if (holdCancel is not null)
                {
                    await holdCancel.Task;
                }
                throw;
            }
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
        public void RequestPause(string workingDirectory) => Volatile.Write(ref _pause, 1);
        public void CancelPause(string workingDirectory) => Volatile.Write(ref _pause, 0);
    }

    private sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness()
        {
            Db = new LedgerDbContext(_options);
            Board.Add(PlanId, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.InProgress, epic: 42, name: "Add CSV export");
            Board.Story(PlanId)["labels"] = new JsonArray(new JsonObject { ["name"] = "plan" });
            Source = new ShortcutWorkSource(Board.Client(), "tok", new WatchScope(["darkfactory"], []));
        }

        public LedgerDbContext Db { get; }
        public FakeShortcutBoard Board { get; } = new();
        public ShortcutWorkSource Source { get; }
        public LedgerDbContextFactory Contexts => new(_options);
        public IControls Controls => new LedgerControls(Contexts, TimeProvider.System);
        public WorkLedger Ledger => new(Db, TimeProvider.System);
        public InProcessRunLocks Locks { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public StringWriter Log { get; } = new();

        public Task<RunOutcome> Run(int id = PlanId, IWorker? worker = null, bool workerless = false) =>
            new RunPipeline(Source, Ledger, Locks, Workspaces, worker ?? new FakeWorker(Reports(Ok)), Prs, Sandbox, Log, controls: Controls,
                controlPollInterval: TimeSpan.FromMilliseconds(10), workerless: workerless).RunAsync(id, CancellationToken.None);

        public ControlActions Actions => new(Controls, Contexts, new Stops(this), Source, Locks);

        private sealed class Stops(Harness h) : IItemStops
        {
            public Task<ControlResult> StopAsync(int storyId, CancellationToken ct) =>
                new ItemStopper(h.Source, new WorkLedger(new LedgerDbContext(h._options), TimeProvider.System), h.Locks, h.Prs, h.Controls, TextWriter.Null)
                    .StopAsync(storyId, ct);
        }

        /// <summary>The plan item as its Plan step leaves it: its stories in the Backlog with their blockers, the ledger at Planned.</summary>
        public async Task<int[]> Planned(string answer)
        {
            var plan = PlanParser.Parse(answer);
            var ids = await Source.CreateChildrenAsync(PlanId, plan.Children(Plan, Sandbox), CancellationToken.None);
            var ledger = Ledger;
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, Plan, "Add CSV export", Sandbox.FullName, null, CancellationToken.None, 42);
            await ledger.RecordAsync(item, WorkState.Plan, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Plan, "plan-sess", plan.ToDetail(), CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanCreating, "plan-sess", null, CancellationToken.None);
            var stories = string.Join(' ', ids.Select(i => $"sc-{i}"));
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, "plan-sess", stories, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Planned, "plan-sess", $"{ids.Count} stories: {stories}", CancellationToken.None);
            Board.Requests.Clear();
            return [.. ids];
        }

        /// <summary>The story's own ledger as its run leaves it at <paramref name="last"/>, linked to the plan as its first run links it.</summary>
        public async Task<WorkItem> Worked(int id, WorkState last = WorkState.Watch)
        {
            var ledger = Ledger;
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, $"sc-{id}", $"Story {id}", Sandbox.FullName, null, CancellationToken.None, 42);
            var plan = await ledger.FindAsync(RunPipeline.Source, Plan, CancellationToken.None);
            await ledger.LinkPlanAsync(item, plan!.Id, CancellationToken.None);
            WorkState[] path = [WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch];
            foreach (var state in path.Skip(Array.IndexOf(path, item.State) + 1))
            {
                if (item.State == last)
                {
                    break;
                }
                await ledger.RecordAsync(item, state, null, state == WorkState.Merge ? $"c0ffee{id}" : null, CancellationToken.None);
            }
            return item;
        }

        public async Task Ends(int id, WorkState ended, string reason, WorkState from = WorkState.Review) =>
            await Ledger.RecordAsync(await Worked(id, from), ended, null, reason, CancellationToken.None);

        public async Task<List<LedgerEntry>> Rows(int id = PlanId)
        {
            var item = await Db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == $"sc-{id}");
            return item is null ? [] : await Db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync();
        }

        public async Task<List<(string? Step, string? Detail)>> Steps(params string[] steps) =>
            (await Rows()).Where(r => steps.Contains(r.Step)).Select(r => (r.Step, r.Detail)).ToList();

        public async Task<WorkState?> ItemState(int id) =>
            (await Db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == $"sc-{id}"))?.State;

        public List<string> Comments(int story) => Board.Requests
            .Where(r => r.Method == HttpMethod.Post && r.PathAndQuery == $"/api/v3/stories/{story}/comments")
            .Select(r => (string)JsonNode.Parse(r.Body!)!["text"]!).ToList();

        public long State(int id) => (long)Board.Story(id)["workflow_state_id"]!;

        public List<RecordedRequest> Writes(int story) => Board.Requests
            .Where(r => r.Method != HttpMethod.Get && r.PathAndQuery.StartsWith($"/api/v3/stories/{story}", StringComparison.Ordinal)).ToList();

        public async Task<IReadOnlyList<int>> InFlight()
        {
            await using var db = new LedgerDbContext(_options);
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), CancellationToken.None, Controls);
        }

        public async Task<IReadOnlyList<PipelineRow>> Dashboard() =>
            await new DashboardData(Contexts, TimeProvider.System).ActiveItemsAsync(CancellationToken.None);
    }

    private static readonly string[] HoldSteps =
        [RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanStuck, RunPipeline.Steps.PlanResumed, RunPipeline.Steps.PlanNotice];

    private static Func<HttpRequestMessage, HttpResponseMessage?> Failing(HttpMethod method, string pathEnd) => r =>
        r.Method == method && r.RequestUri!.AbsolutePath.EndsWith(pathEnd, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null;

    [Fact]
    public async Task In_a_plan_a_then_b_with_an_independent_c_a_escalating_leaves_b_unreleased_and_c_worked_and_the_plan_item_says_so_once()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        var (a, b, c) = (ids[0], ids[1], ids[2]);
        Assert.Null((await h.Run()).Error); // A and C are released
        Assert.Equal((FakeShortcutBoard.ToDo, FakeShortcutBoard.Backlog, FakeShortcutBoard.ToDo), (h.State(a), h.State(b), h.State(c)));
        await h.Ends(a, WorkState.Escalated, "CI is red: build failed in `Export.cs`");

        var held = await h.Run();

        // Not an error and not an escalation of the plan item: it stays Planned and says which story holds it.
        Assert.Equal((WorkState.Planned, (string?)null), (held.State, held.Error));
        var row = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanHeld);
        Assert.StartsWith($"{PlanRelease.FailedPrefix}sc-{a} escalated at ", row.Detail);
        Assert.Contains("`CI is red: build failed in 'Export.cs'`", row.Detail);
        Assert.EndsWith($"; not released because of them: sc-{b} (behind sc-{a})", row.Detail);
        Assert.Equal(StepOutcome.Deferred, row.Outcome);
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.Contains($"{Plan} (plan) is held by a failed story", comment);
        Assert.Contains($"- sc-{a} escalated at ", comment);
        Assert.Contains("CI is red: build failed", comment);
        Assert.Contains($"- sc-{b} (behind sc-{a})", comment);
        Assert.Contains($"`factory run sc-{a}`", comment);
        Assert.Contains($"`factory stop --item {Plan}`", comment);
        // The row came first, then the comment's result.
        Assert.Equal([RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanNotice], (await h.Steps(HoldSteps)).Select(s => s.Step));

        // C does not depend on A: it is worked like any ready story.
        var worker = new FakeWorker(Reports(Ok));
        Assert.Null((await h.Run(c, worker)).Error);
        Assert.Single(worker.Calls);
        Assert.Equal(WorkState.Review, await h.ItemState(c));

        // Every later poll: B stays unreleased, and nothing more is said or recorded.
        var rows = (await h.Rows()).Count;
        for (var poll = 0; poll < 3; poll++)
        {
            Assert.Equal((WorkState.Planned, (string?)null), ((await h.Run()).State, (await h.Run()).Error));
        }
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(b));
        Assert.Single(h.Comments(PlanId));
        Assert.Equal(rows, (await h.Rows()).Count);
        Assert.DoesNotContain(await h.Rows(), r => r.Outcome == StepOutcome.Escalated || r.State == WorkState.Escalated);
        Assert.Contains(PlanId, await h.InFlight());

        // The dashboard's data says the same, from the ledger: the plan row with its stories, each with what it waits on.
        var plan = Assert.Single(await h.Dashboard(), r => r.ExternalId == Plan).Plan!;
        Assert.Equal([$"sc-{a}", $"sc-{b}", $"sc-{c}"], plan.Stories.Select(s => s.ExternalId));
        Assert.Equal(["failed: escalated", $"held by sc-{a} (escalated)", ""], plan.Stories.Select(s => s.Waits));
        Assert.Equal((true, false, 0, 1, 1, 1), (plan.Held, plan.Stuck, plan.Merged, plan.InFlight, plan.Waiting, plan.Failed));
        Assert.Contains("CI is red", plan.Hold);
    }

    [Fact]
    public async Task A_chain_whose_first_story_failed_is_stuck_and_says_so_once_with_what_is_blocked_and_the_remedies()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Run();
        await h.Ends(ids[0], WorkState.Escalated, "review failed three times");

        for (var poll = 0; poll < 3; poll++)
        {
            Assert.Equal((WorkState.Planned, (string?)null), ((await h.Run()).State, (await h.Run()).Error));
        }

        var steps = await h.Steps(HoldSteps);
        Assert.Equal([RunPipeline.Steps.PlanStuck, RunPipeline.Steps.PlanNotice], steps.Select(s => s.Step));
        Assert.EndsWith($"; not released because of them: sc-{ids[1]} (behind sc-{ids[0]}), sc-{ids[2]} (behind sc-{ids[0]})", steps[0].Detail);
        Assert.Equal(StepOutcome.Deferred, (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.PlanStuck).Outcome);
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.Contains($"{Plan} (plan) is stuck", comment);
        Assert.Contains($"- sc-{ids[1]} (behind sc-{ids[0]})", comment);
        Assert.Contains($"- sc-{ids[2]} (behind sc-{ids[0]})", comment);
        Assert.Contains("fix what failed and re-run the story", comment);
        Assert.Contains("stop the plan", comment);
        // It stays Planned and polled: it recovers when a person acts. The plan item never escalates for it.
        Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned], (await h.Rows()).Where(r => r.Step is null).Select(r => r.State));
        Assert.Equal([PlanId], (await h.InFlight()).Where(i => i == PlanId));
        Assert.True(Assert.Single(await h.Dashboard(), r => r.ExternalId == Plan).Plan!.Stuck);
    }

    [Fact]
    public async Task A_held_plan_whose_other_stories_all_merge_becomes_stuck_and_says_that_once_too()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        await h.Run();
        await h.Ends(ids[0], WorkState.Escalated, "CI is red");
        await h.Worked(ids[2], WorkState.Review);
        await h.Run();
        Assert.Equal([RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanNotice], (await h.Steps(HoldSteps)).Select(s => s.Step));

        await h.Worked(ids[2]); // C merges: only the failed A and B behind it are left
        await h.Run();
        await h.Run();

        Assert.Equal([RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanNotice, RunPipeline.Steps.PlanStuck, RunPipeline.Steps.PlanNotice],
            (await h.Steps(HoldSteps)).Select(s => s.Step));
        Assert.Equal(2, h.Comments(PlanId).Count);
        Assert.Contains("is stuck", h.Comments(PlanId)[1]);
        Assert.Equal(WorkState.Planned, await h.ItemState(PlanId)); // not complete: A never merged
    }

    [Fact]
    public async Task A_failed_story_that_a_person_re_runs_lifts_the_hold_and_its_dependents_are_released_once_it_has_merged()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        var (a, b) = (ids[0], ids[1]);
        await h.Run();
        await h.Ends(a, WorkState.Escalated, "CI is red");
        await h.Worked(ids[2], WorkState.Review);
        await h.Run();
        Assert.Single(h.Comments(PlanId));

        // The person fixed it and re-ran the story: Escalated -> Intake. It is in flight again, so nothing is failed.
        var item = (await h.Ledger.FindAsync(RunPipeline.Source, $"sc-{a}", CancellationToken.None))!;
        await h.Ledger.RecordAsync(item, WorkState.Intake, null, "re-run after escalation", CancellationToken.None);
        await h.Run();
        await h.Run();

        var resumed = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanResumed);
        Assert.StartsWith("no story of the plan is failed any more (0 of 3 merged, 2 in flight, 1 waiting", resumed.Detail);
        Assert.Equal(StepOutcome.Passed, resumed.Outcome);
        Assert.Equal(2, h.Comments(PlanId).Count);
        Assert.Contains($"{Plan} (plan) is no longer held", h.Comments(PlanId)[1]);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(b)); // A has not merged yet: B waits on it as on any blocker
        Assert.False(Assert.Single(await h.Dashboard(), r => r.ExternalId == Plan).Plan!.Held);

        // A merges: B is released by the next evaluation, and nothing more is said about the hold.
        await h.Worked(a);
        await h.Run();
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(b));
        Assert.Contains(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanReleased && r.Detail == $"sc-{b}");
        Assert.Equal(2, h.Comments(PlanId).Count);

        // Failing again is a new hold: a new row and a new comment.
        await h.Ends(b, WorkState.Escalated, "review failed", WorkState.Implement);
        await h.Run();
        Assert.Equal(3, h.Comments(PlanId).Count);
        Assert.Equal([RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanResumed, RunPipeline.Steps.PlanHeld],
            (await h.Steps(RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanResumed)).Select(s => s.Step));
    }

    [Fact]
    public async Task A_story_merged_outside_the_factory_or_stopped_holds_its_dependents_like_an_escalated_one_and_is_named_as_such()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
            PlanAnswers.Story("c", "Third story"), PlanAnswers.Story("d", "Fourth story", "c"), PlanAnswers.Story("e", "Fifth story")));
        await h.Run();
        await h.Worked(ids[4], WorkState.Review);
        await h.Ends(ids[0], WorkState.Escalated, "InvalidOperationException: https://github.com/michaeltrefry/dark-factory-sandbox/pull/7 is merged outside the factory.");
        // A person's Stop on one story of the plan works as on any item; the stopped story then holds its dependents.
        await h.Worked(ids[2], WorkState.Review);
        Assert.True((await h.Actions.StopAsync(ControlScope.Item($"sc-{ids[2]}"), "tester", CancellationToken.None)).Ok);
        Assert.Equal(WorkState.Cancelled, await h.ItemState(ids[2]));

        await h.Run();
        await h.Run();

        var held = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanHeld).Detail!;
        Assert.Contains($"sc-{ids[0]} merged outside the factory at ", held);
        Assert.Contains($"sc-{ids[2]} cancelled at ", held);
        Assert.Contains("`stopped by tester`", held);
        Assert.EndsWith($"sc-{ids[1]} (behind sc-{ids[0]}), sc-{ids[3]} (behind sc-{ids[2]})", held);
        Assert.Equal((FakeShortcutBoard.Backlog, FakeShortcutBoard.Backlog), (h.State(ids[1]), h.State(ids[3])));
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.Contains($"A cancelled story (sc-{ids[2]}) cannot be re-run", comment);
        var plan = Assert.Single(await h.Dashboard(), r => r.ExternalId == Plan).Plan!;
        Assert.Equal($"held by sc-{ids[0]} (merged outside the factory)", plan.Stories[1].Waits);
        Assert.Equal(("Cancelled", "failed: cancelled"), (plan.Stories[2].Shown, plan.Stories[2].Waits));
    }

    [Fact]
    public async Task A_comment_the_board_refuses_is_recorded_tried_again_a_bounded_number_of_times_and_never_posted_twice()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        await h.Run();
        await h.Ends(ids[0], WorkState.Escalated, "CI is red");
        await h.Worked(ids[2], WorkState.Review);
        h.Board.Intercept = Failing(HttpMethod.Post, $"/stories/{PlanId}/comments");

        var first = await h.Run();

        // The hold is on the ledger before the comment is tried; the failed comment is a row of its own and no error of the run.
        Assert.Null(first.Error);
        var steps = await h.Steps(HoldSteps);
        Assert.Equal([RunPipeline.Steps.PlanHeld, RunPipeline.Steps.PlanNotice], steps.Select(s => s.Step));
        Assert.StartsWith("failed: ", steps[1].Detail);
        Assert.Equal(StepOutcome.Failed, (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.PlanNotice).Outcome);

        // The next evaluation posts it, once; later ones do nothing.
        h.Board.Intercept = null;
        h.Board.Requests.Clear();
        await h.Run();
        await h.Run();
        await h.Run();
        Assert.Single(h.Comments(PlanId));
        Assert.Equal(["failed", RunPipeline.Posted], (await h.Steps(RunPipeline.Steps.PlanNotice)).Select(s => s.Detail!.Split(':')[0]));
        Assert.Single(await h.Steps(RunPipeline.Steps.PlanHeld));

        // A board that keeps refusing: tried a bounded number of times, then left to the ledger and the dashboard.
        var stuck = new Harness();
        var other = await stuck.Planned(Chain);
        await stuck.Run();
        await stuck.Ends(other[0], WorkState.Escalated, "CI is red");
        stuck.Board.Intercept = Failing(HttpMethod.Post, $"/stories/{PlanId}/comments");
        for (var poll = 0; poll < RunPipeline.MaxPlanNoticeAttempts + 3; poll++)
        {
            Assert.Null((await stuck.Run()).Error);
        }
        Assert.Equal(RunPipeline.MaxPlanNoticeAttempts, (await stuck.Steps(RunPipeline.Steps.PlanNotice)).Count);
        Assert.Single(await stuck.Steps(RunPipeline.Steps.PlanStuck));
        Assert.Contains("not tried again", stuck.Log.ToString());
    }

    [Fact]
    public async Task A_run_that_died_between_the_hold_row_and_its_comment_posts_the_comment_once_on_the_next_evaluation()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Run();
        await h.Ends(ids[0], WorkState.Escalated, "CI is red");
        // As the dead run left it: the row, exactly as the evaluation writes it, and no comment.
        var known = await h.Ledger.ItemsByIdAsync(RunPipeline.Source, [.. ids.Select(i => $"sc-{i}")], CancellationToken.None);
        var status = PlanRelease.Classify([.. ids.Select(i => $"sc-{i}")], new HashSet<string> { $"sc-{ids[0]}" },
            s => s == $"sc-{ids[1]}" ? [$"sc-{ids[0]}"] : s == $"sc-{ids[2]}" ? [$"sc-{ids[1]}"] : [], s => known.TryGetValue(s, out var k) ? k.History : null);
        var plan = (await h.Ledger.FindAsync(RunPipeline.Source, Plan, CancellationToken.None))!;
        await h.Ledger.CheckpointAsync(plan, RunPipeline.Steps.PlanStuck, null, PlanRelease.HeldByFailed(status), CancellationToken.None);

        await h.Run();
        await h.Run();

        Assert.Single(h.Comments(PlanId));
        Assert.Equal([RunPipeline.Steps.PlanStuck, RunPipeline.Steps.PlanNotice], (await h.Steps(HoldSteps)).Select(s => s.Step));
    }

    [Fact]
    public async Task Pause_on_a_plan_item_pauses_its_in_flight_story_claims_and_releases_nothing_and_continue_resumes_all_of_it()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        var (a, b, c) = (ids[0], ids[1], ids[2]);
        await h.Run(); // A and C released
        var worker = new ToolWorker();
        var run = h.Run(a, worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var paused = await h.Actions.PauseAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60));

        // The plan's own item scope, no new scope kind: its story stops at its next tool call, as under an item or epic Pause.
        Assert.True(paused.Ok);
        Assert.Contains($"{Plan} is a plan", paused.Message);
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Equal((WorkState.Paused, RunPipeline.UserPaused), (await h.Rows(a)).Where(r => r.Step is null).Select(r => (r.State, r.Detail)).Last());
        Assert.Equal(ControlState.Paused, await h.Controls.EffectiveAsync($"sc-{a}", 42, CancellationToken.None));
        Assert.Empty(await h.InFlight());

        // Nothing new is claimed for the plan (C is ready on its board and the ledger does not know it yet), nothing released.
        h.Board.Requests.Clear();
        var unclaimed = await h.Run(c);
        Assert.Contains($"sc-{c} is Paused by a control; not claimed", unclaimed.Error);
        Assert.Empty(await h.Rows(c));
        await h.Worked(a); // even with A merged meanwhile, the paused plan releases nothing
        Assert.Contains("paused by a control", (await h.Run()).Error);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(b));
        Assert.DoesNotContain(h.Board.Requests, r => r.Method != HttpMethod.Get);
        Assert.Equal(ControlState.Paused, Assert.Single(await h.Dashboard(), r => r.ExternalId == $"sc-{a}").Control);

        // Continue lifts it: the plan is evaluated again and the story is claimed.
        Assert.True((await h.Actions.ContinueAsync(ControlScope.Item(Plan), "tester", CancellationToken.None)).Ok);
        Assert.Equal(ControlState.Running, await h.Controls.EffectiveAsync($"sc-{a}", 42, CancellationToken.None));
        Assert.Contains(PlanId, await h.InFlight());
        Assert.Null((await h.Run()).Error);
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(b));
        Assert.Null((await h.Run(c)).Error);
        Assert.Equal(WorkState.Review, await h.ItemState(c));
    }

    [Fact]
    public async Task A_paused_story_of_a_paused_plan_resumes_its_own_session_after_continue()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        await h.Run();
        var worker = new ToolWorker();
        var run = h.Run(ids[0], worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await h.Actions.PauseAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);
        Assert.Equal(WorkState.Paused, (await run.WaitAsync(TimeSpan.FromSeconds(60))).State);

        // Still paused: the listing leaves it out and a run of it does nothing.
        Assert.Contains("paused by a control", (await h.Run(ids[0], worker)).Error);
        await h.Actions.ContinueAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);
        Assert.Contains(ids[0], await h.InFlight());
        var resumed = await h.Run(ids[0], worker);

        Assert.Null(resumed.Error);
        Assert.Equal([null, Session], worker.Resumes);
        Assert.Equal(WorkState.Review, resumed.State);
    }

    [Fact]
    public async Task Stop_on_a_plan_item_stops_its_unfinished_stories_leaves_the_merged_and_the_unreleased_and_cancels_the_plan()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
            PlanAnswers.Story("c", "Third story"), PlanAnswers.Story("d", "Fourth story")));
        var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
        await h.Run(); // A, C and D released; B waits on A
        await h.Worked(a); // A merged
        Assert.Null((await h.Run(c)).Error); // C worked to Review with its pull request open
        Assert.Equal(WorkState.Review, await h.ItemState(c));
        h.Board.Requests.Clear();

        var stopped = await h.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);

        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal(0, stopped.ExitCode);
        // C, in flight: its pull request back to draft, its story to the Backlog with a comment, Cancelled — each on its own rows.
        var cRows = await h.Rows(c);
        Assert.Equal(PrUrl, cRows.Single(r => r.Step == RunPipeline.Steps.PrsDrafted).Detail);
        Assert.Equal((WorkState.Cancelled, $"stopped by tester (with its plan {Plan})"), (cRows[^1].State, cRows[^1].Detail));
        Assert.Contains($"factory/sc-{c}", h.Prs.Drafted);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(c));
        Assert.Contains($"sc-{c} was stopped by tester (with its plan {Plan})", Assert.Single(h.Comments(c)));
        // D, released and never started: stopped the same way, on a ledger item of its own.
        Assert.Equal([WorkState.Intake, WorkState.Cancelled], (await h.Rows(d)).Where(r => r.Step is null).Select(r => r.State));
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(d));
        Assert.Single(h.Comments(d));
        // A, merged, is untouched; B, never released, is left in the Backlog with nothing written anywhere.
        Assert.Equal(WorkState.Watch, await h.ItemState(a));
        Assert.Empty(h.Writes(a));
        Assert.Empty(h.Writes(b));
        Assert.Empty(await h.Rows(b));
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(b));
        // The plan item: Cancelled, its board told once with what was stopped and what had merged, its control cleared.
        var rows = await h.Rows();
        Assert.Equal((WorkState.Cancelled, "stopped by tester", StepOutcome.Deferred), (rows[^1].State, rows[^1].Detail, rows[^1].Outcome));
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(PlanId));
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.StartsWith($"[author: dark-factory] {Plan} was stopped by tester: the factory cancelled the plan (was Planned)", comment);
        Assert.Contains($"Stories stopped with it: sc-{c}, sc-{d}. Already merged (left as they are): sc-{a}. "
            + $"Never started (left in the backlog; the factory will not release or start them): sc-{b}.", comment);
        Assert.Null(await h.Controls.GetAsync(ControlScope.Item(Plan), CancellationToken.None));
        Assert.Empty(await h.InFlight());
        Assert.Contains($"Stories stopped with it: sc-{c}, sc-{d}", stopped.Message);

        // Stopping it again changes nothing.
        h.Board.Requests.Clear();
        Assert.True((await h.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None)).Ok);
        Assert.DoesNotContain(h.Board.Requests, r => r.Method != HttpMethod.Get);

        // B is never started afterwards, whoever moves it to ready: its claim is refused, once, with no board write.
        h.Board.Story(b)["workflow_state_id"] = FakeShortcutBoard.ToDo;
        var worker = new FakeWorker(Reports(Ok));
        var refused = await h.Run(b, worker);
        Assert.Equal(WorkState.Paused, refused.State);
        Assert.Contains($"sc-{b} parked: blocked by its plan {Plan}, which was stopped", refused.Error);
        var parked = (await h.Rows(b)).Count;
        Assert.Contains("which was stopped", (await h.Run(b, worker)).Error);
        Assert.Equal(parked, (await h.Rows(b)).Count);
        Assert.Empty(worker.Calls);
        Assert.DoesNotContain(h.Board.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Stop_on_a_plan_whose_story_is_running_is_unfinished_until_that_storys_own_run_has_stopped_it()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        var (a, b) = (ids[0], ids[1]);
        await h.Run();
        // A's run holds its run lock with a worker working; the worker's exit is held until the Stop call has returned.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ToolWorker(release);
        var run = h.Run(a, worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var result = await h.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);

        // One run per item: the plan's stop does not take a story from its run. Unfinished, so the CLI exits non-zero.
        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"sc-{a} is running (its run stops it at once)", result.Message);
        Assert.Contains("it stays Stopping", result.Message);
        Assert.Equal(WorkState.Planned, await h.ItemState(PlanId));
        Assert.Equal(ControlState.Stopping, (await h.Controls.GetAsync(ControlScope.Item(Plan), CancellationToken.None))!.State);
        Assert.Empty(h.Comments(PlanId));

        // The story's run sees its plan's Stop and stops the story itself: worker killed, Cancelled.
        await worker.CancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        release.SetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Equal($"stopped by tester (with its plan {Plan})", (await h.Rows(a))[^1].Detail);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(a));

        // The plan is still being stopped: the next poll lists it first and its run finishes the stop.
        Assert.Equal([PlanId], await h.InFlight());
        var finished = await h.Run();
        Assert.Equal((WorkState.Cancelled, (string?)null), (finished.State, finished.Error));
        Assert.Contains($"Stories stopped with it: sc-{a}, sc-{ids[2]}.", Assert.Single(h.Comments(PlanId)));
        Assert.Contains($"Never started (left in the backlog; the factory will not release or start them): sc-{b}.", h.Comments(PlanId)[0]);
        Assert.Null(await h.Controls.GetAsync(ControlScope.Item(Plan), CancellationToken.None));
        Assert.Empty(await h.InFlight());
    }

    [Fact]
    public async Task A_plan_stop_that_fails_half_way_stays_stopping_and_the_next_attempt_finishes_it_without_repeating_what_was_done()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story")));
        var (a, b) = (ids[0], ids[1]);
        await h.Run();
        Assert.Null((await h.Run(a)).Error); // A at Review with its pull request
        h.Board.Requests.Clear();
        // The board refuses A's move: its pull request is drafted (recorded), its stop is not finished.
        h.Board.Intercept = Failing(HttpMethod.Put, $"/stories/{a}");

        var first = await h.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);

        Assert.False(first.Ok);
        Assert.Contains($"1 of its stories not stopped yet: sc-{a}: ", first.Message);
        Assert.Equal((WorkState.Review, WorkState.Cancelled, WorkState.Planned), (await h.ItemState(a), await h.ItemState(b), await h.ItemState(PlanId)));
        Assert.Contains(await h.Rows(a), r => r.Step == RunPipeline.Steps.PrsDrafted);
        Assert.Empty(h.Comments(PlanId));
        // Both are listed as being stopped, the plan first; a workerless run of the plan defers the stop, touching nothing.
        Assert.Equal([PlanId, a], await h.InFlight());
        h.Board.Intercept = null;
        h.Board.Requests.Clear();
        var deferred = await h.Run(workerless: true);
        Assert.Contains("stop deferred to the next run", deferred.Error);
        Assert.Empty(h.Board.Requests);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("reopen ", StringComparison.Ordinal));

        // The next run of the plan (built with the worker lock) finishes it: A's draft is not repeated, B is not stopped twice.
        var finished = await h.Run();

        Assert.Equal((WorkState.Cancelled, (string?)null), (finished.State, finished.Error));
        Assert.Equal(WorkState.Cancelled, await h.ItemState(a));
        Assert.Single(h.Prs.Drafted, head => head == $"factory/sc-{a}");
        Assert.Single(await h.Rows(a), r => r.Step == RunPipeline.Steps.PrsDrafted);
        Assert.Empty(h.Writes(b));
        Assert.Single(await h.Rows(b), r => r.Step == RunPipeline.Steps.StopReported);
        Assert.Single(h.Comments(PlanId));
        Assert.Empty(await h.InFlight());

        // The plan item's own board write failing leaves it Stopping too, its stories already stopped; a retry tells the board once.
        var again = new Harness();
        var other = await again.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "Only story")));
        await again.Run();
        again.Board.Intercept = Failing(HttpMethod.Put, $"/stories/{PlanId}");
        var failed = await again.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);
        Assert.Equal((false, 1), (failed.Ok, failed.ExitCode));
        Assert.Equal((WorkState.Cancelled, WorkState.Planned), (await again.ItemState(other[0]), await again.ItemState(PlanId)));
        again.Board.Intercept = null;
        Assert.True((await again.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None)).Ok);
        Assert.Equal(WorkState.Cancelled, await again.ItemState(PlanId));
        Assert.Single(again.Comments(other[0]));
        Assert.Single(again.Comments(PlanId));
    }

    [Fact]
    public async Task A_story_whose_worker_a_crashed_run_left_is_stopped_by_its_own_next_run_before_the_plan_is_cancelled()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "Only story")));
        await h.Run();
        var item = await h.Worked(ids[0], WorkState.Implement);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, null, "999", CancellationToken.None); // then its run crashed

        var result = await h.Actions.StopAsync(ControlScope.Item(Plan), "tester", CancellationToken.None);

        // The plan's stop holds no worker run lock and cannot stop that worker: it is left to the story's own run.
        Assert.False(result.Ok);
        Assert.Contains($"sc-{ids[0]}'s worker may still be running", result.Message);
        Assert.Equal(WorkState.Implement, await h.ItemState(ids[0]));
        Assert.Equal(ControlState.Stopping, await h.Controls.EffectiveAsync($"sc-{ids[0]}", 42, CancellationToken.None));
        Assert.Equal([PlanId, ids[0]], await h.InFlight());

        var worker = new FakeWorker { OrphanAlive = true };
        var stopped = await h.Run(ids[0], worker);
        Assert.Equal([999], worker.OrphanStops);
        Assert.Equal(WorkState.Cancelled, stopped.State);
        Assert.Empty(worker.Calls);

        Assert.Equal(WorkState.Cancelled, (await h.Run()).State);
        Assert.Empty(await h.InFlight());
    }

    [Fact]
    public async Task A_plans_stop_and_pause_reach_a_story_through_the_plans_own_item_scope_and_a_stop_spares_a_merged_story()
    {
        var h = new Harness();
        var ids = await h.Planned(Fork);
        await h.Run();
        await h.Worked(ids[0]); // merged, in Watch
        await h.Worked(ids[2], WorkState.Review);
        var (merged, working) = ($"sc-{ids[0]}", $"sc-{ids[2]}");

        await h.Controls.SetAsync(ControlScope.Item(Plan), ControlState.Paused, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Paused, await h.Controls.EffectiveAsync(working, null, CancellationToken.None));
        Assert.Equal(ControlState.Paused, await h.Controls.EffectiveAsync(merged, null, CancellationToken.None));

        await h.Controls.SetAsync(ControlScope.Item(Plan), ControlState.Stopping, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Stopping, await h.Controls.EffectiveAsync(working, null, CancellationToken.None));
        Assert.Equal(ControlState.Running, await h.Controls.EffectiveAsync(merged, null, CancellationToken.None));
        Assert.Equal([PlanId, ids[2]], await h.InFlight());
        var rows = await h.Dashboard();
        Assert.Equal((ControlState.Stopping, ControlState.Stopping, ControlState.Running),
            (rows.Single(r => r.ExternalId == Plan).Control, rows.Single(r => r.ExternalId == working).Control, rows.Single(r => r.ExternalId == merged).Control));

        // An item that is no story of the plan is not touched by it, and a story's own Stop still outranks its plan's Pause.
        await h.Controls.SetAsync(ControlScope.Item(Plan), ControlState.Paused, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Running, await h.Controls.EffectiveAsync("sc-9999", null, CancellationToken.None));
        await h.Controls.SetAsync(ControlScope.Item(working), ControlState.Stopping, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Stopping, await h.Controls.EffectiveAsync(working, null, CancellationToken.None));
        // No scope kind was added: a plan is controlled at its item scope, and only an item scope can be stopped.
        Assert.False(ControlScope.IsValid($"plan:{Plan}"));
    }

    [Fact]
    public async Task The_dashboards_data_lists_a_plans_stories_under_it_with_state_blockers_and_inert_titles_from_the_ledger_alone()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
            PlanAnswers.Story("c", "Third @owner [x](http://evil.example) <b>story</b>", "b"), PlanAnswers.Story("d", "Fourth story")));
        await h.Run(); // A and D released
        await h.Worked(ids[0], WorkState.Review);
        h.Board.Requests.Clear();

        var rows = await h.Dashboard();

        Assert.Empty(h.Board.Requests); // the dashboard reads the ledger, never the board
        var plan = rows.Single(r => r.ExternalId == Plan);
        Assert.Null(plan.PlanItemId);
        Assert.Equal(plan.Id, rows.Single(r => r.ExternalId == $"sc-{ids[0]}").PlanItemId);
        var view = plan.Plan!;
        Assert.Equal(ids.Select(i => $"sc-{i}"), view.Stories.Select(s => s.ExternalId));
        Assert.Equal(["Review", "not released", "not released", "released"], view.Stories.Select(s => s.Shown));
        Assert.Equal(["", $"waits on sc-{ids[0]}", $"waits on sc-{ids[1]}", "to be picked up"], view.Stories.Select(s => s.Waits));
        Assert.Equal((0, 2, 2, 0, false, false, (string?)null), (view.Merged, view.InFlight, view.Waiting, view.Failed, view.Held, view.Stuck, view.Hold));
        // A story with a work item shows that item's title; one without, the plan's, made inert.
        Assert.Equal($"Story {ids[0]}", view.Stories[0].Title);
        Assert.Equal("Second story", view.Stories[1].Title);
        Assert.DoesNotContain('@', view.Stories[2].Title);
        Assert.DoesNotContain('<', view.Stories[2].Title);
        Assert.DoesNotContain("](", view.Stories[2].Title);
        Assert.DoesNotContain("://", view.Stories[2].Title);
        Assert.Contains("story", view.Stories[2].Title);
        // An item that is no plan has no stories, and no plan.
        Assert.Null(rows.Single(r => r.ExternalId == $"sc-{ids[0]}").Plan);

        // A hold by a blocker outside the plan is shown as the last one recorded.
        h.Board.Add(777, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.InProgress, name: "Someone else's story");
        using (var response = await h.Board.Client().PostAsJsonAsync("story-links", new { subject_id = 777, verb = "blocks", object_id = ids[1] }))
        {
            response.EnsureSuccessStatusCode();
        }
        await h.Worked(ids[0]);
        await h.Run();
        var held = Assert.Single(await h.Dashboard(), r => r.ExternalId == Plan).Plan!;
        Assert.StartsWith($"sc-{ids[1]} held by sc-777", held.LastHeld!.Value.Detail);
        Assert.Equal(1, held.Merged);
    }
}

/// <summary>
/// sc-25876 against the compose Postgres (skips locally without it): the plan-scope control reads, the control actions and the
/// dashboard's plan query run as real SQL, not only on the in-memory provider.
/// </summary>
public class PlanControlsPostgresTests
{
    [Fact]
    public async Task A_plans_control_reaches_its_stories_and_the_dashboard_groups_them_on_the_real_ledger()
    {
        await using var temp = await TempPostgresDatabase.CreateAsync("df_planctl");
        await LedgerMigrations.MigrateAsync(temp.ConnectionString, CancellationToken.None);
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(temp.ConnectionString));
        var plan = PlanParser.Parse(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a")));
        long planId;
        await using (var db = contexts.CreateDbContext())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-500", "Add CSV export", Sandbox.FullName, null, CancellationToken.None);
            planId = item.Id;
            await ledger.RecordAsync(item, WorkState.Plan, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Plan, null, plan.ToDetail(), CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, null, "sc-9001 sc-9002", CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Planned, null, "2 stories: sc-9001 sc-9002", CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanReleased, null, "sc-9001", CancellationToken.None);
            var story = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-9001", "First story", Sandbox.FullName, null, CancellationToken.None);
            await ledger.LinkPlanAsync(story, item.Id, CancellationToken.None);
            await ledger.RecordAsync(story, WorkState.Implement, null, null, CancellationToken.None);
            await ledger.RecordAsync(story, WorkState.Escalated, null, "CI is red", CancellationToken.None);
        }
        var controls = new LedgerControls(contexts, TimeProvider.System);
        var actions = new ControlActions(controls, contexts);

        var paused = await actions.PauseAsync(ControlScope.Item("sc-500"), "tester", CancellationToken.None);

        Assert.Contains("sc-500 is a plan", paused.Message);
        Assert.DoesNotContain("is a plan", (await actions.PauseAsync(ControlScope.Item("sc-9001"), "tester", CancellationToken.None)).Message);
        await controls.ClearAsync(ControlScope.Item("sc-9001"), CancellationToken.None);
        Assert.Equal(ControlState.Paused, await controls.EffectiveAsync("sc-9001", null, CancellationToken.None));
        await using (var db = contexts.CreateDbContext())
        {
            Assert.Empty(await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), CancellationToken.None, controls));
        }
        // Stop, with nothing to finish it here: the plan and its unfinished story are listed as being stopped, the plan first.
        Assert.True((await actions.StopAsync(ControlScope.Item("sc-500"), "tester", CancellationToken.None)).Ok);
        Assert.Equal(ControlState.Stopping, await controls.EffectiveAsync("sc-9001", null, CancellationToken.None));
        await using (var db = contexts.CreateDbContext())
        {
            Assert.Equal([500, 9001], await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), CancellationToken.None, controls));
        }

        var rows = await new DashboardData(contexts, TimeProvider.System).ActiveItemsAsync(CancellationToken.None);
        var view = rows.Single(r => r.ExternalId == "sc-500").Plan!;
        Assert.Equal(["failed: escalated", "held by sc-9001 (escalated)"], view.Stories.Select(s => s.Waits));
        Assert.Equal(["First story", "Second story"], view.Stories.Select(s => s.Title));
        Assert.True(view.Stuck);
        Assert.Equal((planId, ControlState.Stopping), (rows.Single(r => r.ExternalId == "sc-9001").PlanItemId, rows.Single(r => r.ExternalId == "sc-9001").Control));
    }
}
