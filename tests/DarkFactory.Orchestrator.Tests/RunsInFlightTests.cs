using System.Collections.Concurrent;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25991: several runs in flight at once — the real intake loop dispatching real pipelines (fake workers, one shared ledger)
/// with <c>Intake:MaxConcurrentRuns</c> 2. What stops new work (the freeze, the usage pause, a factory-wide failure of one run)
/// stops it for the poll while the runs in flight reach their next check and pause themselves; and the dashboard's data, read
/// from that ledger alone, shows each run with its phase and its worker slot — held, or waited for. Nothing here sleeps: every
/// step of a run the test waits on is signalled by the fake it runs through.
/// </summary>
public class RunsInFlightTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly WorkStory Story = new(1, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/1", ItemNaming.Shortcut);
    private static readonly TimeSpan Signal = TimeSpan.FromSeconds(60);

    /// <summary>One item's worker: its first session says it started, then ends when — and how — the test says.</summary>
    private sealed class Seat(int id)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkerResult> Ends { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkerResult Done { get; } = Ok with { SessionId = $"sess-{id}" };
        public FakeWorker Worker => _worker ??= new FakeWorker(
            async call =>
            {
                await call.OnSession(Done.SessionId!, CancellationToken.None);
                Started.TrySetResult();
                return await Ends.Task;
            },
            Reports(Done), Reports(Done));
        private FakeWorker? _worker;
        public FakeWorkspaces Workspaces { get; } = new();
        /// <summary>The run's sandbox set-up (its first worker phase's start), when a test wants it to fail.</summary>
        public Func<CancellationToken, Task>? EnterSandbox { get; set; }

        public void Finish() => Ends.TrySetResult(Done);
    }

    /// <summary>The run's log; <see cref="Waiting"/> completes once a run has logged that it waits for a slot.</summary>
    private sealed class SignallingLog : TextWriter
    {
        private readonly Lock _gate = new();
        private readonly System.Text.StringBuilder _line = new();
        public volatile bool WaitLogged;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _line.Append(value);
                if (value == '\n')
                {
                    WaitLogged |= _line.ToString().Contains(": waiting for ", StringComparison.Ordinal);
                    _line.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Controls that say when a run that logged its wait for a slot has read its item's controls since: the wait's row is in
    /// the ledger by then (it is written between the two).
    /// </summary>
    private sealed class WaitSignal(IControls inner, SignallingLog log) : IControls
    {
        public TaskCompletionSource Recorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The usage pause a run wrote (its worker hit the limit), once it has.</summary>
        public TaskCompletionSource<Control> UsagePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct)
        {
            if (log.WaitLogged)
            {
                Recorded.TrySetResult();
            }
            return inner.EffectiveAsync(externalId, epicId, ct);
        }

        public Task<Control?> GetAsync(string scope, CancellationToken ct) => inner.GetAsync(scope, ct);
        public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<Control?> UsagePauseAsync(CancellationToken ct) => inner.UsagePauseAsync(ct);
        public async Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct)
        {
            var pause = await inner.PauseForUsageAsync(resumeAt, reason, ct);
            UsagePaused.TrySetResult(pause);
            return pause;
        }

        public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) =>
            inner.FreezeAsync(trigger, detail, readChangedAt, ct);
        public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => inner.SetAsync(scope, state, by, ct);
        public Task ClearAsync(string scope, CancellationToken ct) => inner.ClearAsync(scope, ct);
    }

    /// <summary>A factory: one ledger, its controls, a pool of worker slots, and the intake loop over real pipelines.</summary>
    private sealed class Factory : IItemRunner
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private readonly ConcurrentDictionary<int, Seat> _seats = new();

        public Factory(int slots, params int[] ready)
        {
            Slots = TestSlots.Pool(Directory.CreateTempSubdirectory("df-runs-in-flight-").FullName, slots);
            Source = IntakeDispatchTests.ListingSource.Listing(ready);
            Controls = new WaitSignal(new LedgerControls(Contexts, Time), Log);
            Status = new IntakeStatus(Time);
            Status.Changed += () =>
            {
                if (Status.FactoryError is not null)
                {
                    FactoryFailed.TrySetResult();
                }
            };
            Loop = new IntakeLoop([new IntakeLane(Source, this)], new IntakeOptions(TimeSpan.FromMinutes(1), MaxConcurrentRuns: 2), Time,
                NullLogger<IntakeLoop>.Instance, Controls, status: Status);
        }

        public FakeTimeProvider Time { get; } = new(T0);
        public LedgerDbContextFactory Contexts => new(_options);
        public WorkerSlots Slots { get; }
        public InProcessRunLocks Locks { get; } = new();
        public SignallingLog Log { get; } = new();
        public WaitSignal Controls { get; }
        public IWorkSource Source { get; }
        public IntakeStatus Status { get; }
        public IntakeLoop Loop { get; }
        public ConcurrentQueue<int> Dispatched { get; } = new();
        /// <summary>Completes once the loop has shown a factory-wide error.</summary>
        public TaskCompletionSource FactoryFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Seat Seat(int id) => _seats.GetOrAdd(id, i => new Seat(i));

        /// <summary>The dashboard's data over this ledger; a run is alive while its item's run lock is held.</summary>
        public DashboardData Dashboard => new(Contexts, Time, liveRuns: Locks.HeldAsync);

        public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
        {
            await using var db = Contexts.CreateDbContext();
            return await RunPipeline.InFlightAsync(new WorkLedger(db, Time), ItemNaming.Shortcut, ct, Controls);
        }

        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Dispatched.Enqueue(id);
            var seat = Seat(id);
            await using var db = Contexts.CreateDbContext();
            return await new RunPipeline(new FakeWorkSource(Story), new WorkLedger(db, Time), Locks, seat.Workspaces, seat.Worker, new FakePullRequests(), Sandbox, Log,
                controls: Controls, controlPollInterval: TimeSpan.FromMilliseconds(5), slots: Slots, enterSandbox: seat.EnterSandbox).RunAsync(id, ct);
        }

        public async Task<PipelineRow> Row(string id) => (await Dashboard.ActiveItemsAsync(CancellationToken.None)).Single(r => r.ExternalId == id);

        public async Task<List<LedgerEntry>> Rows(string id)
        {
            await using var db = Contexts.CreateDbContext();
            return await db.WorkItems.SingleOrDefaultAsync(i => i.ExternalId == id) is { } item
                ? await db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync()
                : [];
        }

        public async Task<List<WorkState>> States(params string[] ids)
        {
            var states = new List<WorkState>();
            foreach (var id in ids)
            {
                states.Add((await Last(id)).State);
            }
            return states;
        }

        public async Task<(WorkState State, string? Detail)> Last(string id) =>
            (await Rows(id)).Last(r => r.Step is null) is var last ? (last.State, last.Detail) : default;
    }

    [Fact]
    public async Task A_freeze_with_two_runs_in_flight_starts_no_new_item_pauses_both_at_their_next_check_and_both_resume_after_continue()
    {
        var f = new Factory(slots: 2, 1, 2, 3);
        var poll = f.Loop.PollOnceAsync(CancellationToken.None);
        await Task.WhenAll(f.Seat(1).Started.Task, f.Seat(2).Started.Task).WaitAsync(Signal);

        // Two runs in flight, each in its own worker slot: the page's data shows both, with their phase and their slot.
        var (one, two) = (await f.Row("sc-1"), await f.Row("sc-2"));
        Assert.All([one, two], r => Assert.Equal((WorkState.Implement, true, "implementing"), (r.State, r.Run!.Live, r.Run.Phase)));
        Assert.All([one, two], r => Assert.True(r.Run!.Holds));
        Assert.Equal(["holds slot 1 (_dftest) to implement", "holds slot 2 (_dftest2) to implement"], new[] { one.Run!.SlotText, two.Run!.SlotText }.Order());

        // The factory freezes (the evaluator's row; here as a human sees it written). Both workers go on to the end of their turn.
        await f.Controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
        f.Seat(1).Finish();
        f.Seat(2).Finish();
        await poll.WaitAsync(Signal);

        // No new item was dispatched, and both runs paused at their next check — nothing pushed, sessions and slots kept.
        Assert.Equal([1, 2], f.Dispatched.Order());
        Assert.Empty(f.Seat(3).Worker.Calls);
        Assert.Empty(await f.Rows("sc-3"));
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await f.Last("sc-1"));
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await f.Last("sc-2"));
        Assert.All([f.Seat(1), f.Seat(2)], s => Assert.DoesNotContain(s.Workspaces.Calls, c => c.StartsWith("push ", StringComparison.Ordinal)));
        // Shown: what holds new work and what lifts it; each paused item with why, alive no more, its slot its own but not held.
        var hold = Assert.Single(f.Status.Deferrals);
        Assert.Equal((ControlScope.Freeze, true), (hold.Id, hold.Hold));
        Assert.Contains($"the factory is frozen ({FreezeTrigger.MainRed})", hold.Message);
        Assert.Contains("factory continue --freeze", hold.Message);
        Assert.Null(f.Status.FactoryError);
        Assert.Empty(f.Status.ItemErrors);
        foreach (var row in new[] { await f.Row("sc-1"), await f.Row("sc-2") })
        {
            Assert.Equal((false, $"paused in Implement: {RunPipeline.FreezePaused}", false, false), (row.Run!.Live, row.Run.Phase, row.Run.Holds, row.Run.Stale));
            Assert.EndsWith(", not held", row.Run.SlotText);
        }

        // Still frozen: a poll dispatches nothing at all.
        await f.Loop.PollOnceAsync(CancellationToken.None).WaitAsync(Signal);
        Assert.Equal([1, 2], f.Dispatched.Order());

        // A human's Continue: both resume (the work each worker finished is pushed), and the item held back is started.
        Assert.True((await new ControlActions(f.Controls, f.Contexts).ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);
        f.Seat(3).Finish();
        await f.Loop.PollOnceAsync(CancellationToken.None).WaitAsync(Signal);

        Assert.Equal([1, 1, 2, 2, 3], f.Dispatched.Order());
        Assert.Equal([WorkState.Review, WorkState.Review, WorkState.Review], await f.States("sc-1", "sc-2", "sc-3"));
        Assert.All([f.Seat(1), f.Seat(2)], s => Assert.Single(s.Worker.Calls)); // resumed where it stopped: no second session
        Assert.Empty(f.Status.Deferrals);
    }

    [Fact]
    public async Task A_usage_pause_set_by_one_run_starts_no_new_item_pauses_the_run_beside_it_and_lifts_by_itself()
    {
        var f = new Factory(slots: 2, 1, 2, 3);
        var poll = f.Loop.PollOnceAsync(CancellationToken.None);
        await Task.WhenAll(f.Seat(1).Started.Task, f.Seat(2).Started.Task).WaitAsync(Signal);

        // sc-1's worker hits the plans' limit: its run pauses the factory for usage. sc-2's worker ends its turn after that.
        f.Seat(1).Ends.SetResult(new WorkerResult("sess-1", 1, true, "success", "failed", "", RateLimited: true));
        var paused = await f.Controls.UsagePaused.Task.WaitAsync(Signal);
        f.Seat(2).Finish();
        var resumeAt = await poll.WaitAsync(Signal);

        Assert.Equal([1, 2], f.Dispatched.Order()); // sc-3 was ready, and is not started into the pause
        Assert.Empty(await f.Rows("sc-3"));
        Assert.Equal((WorkState.Paused, RunPipeline.UsagePaused), await f.Last("sc-1"));
        Assert.Equal((WorkState.Paused, RunPipeline.UsagePaused), await f.Last("sc-2"));
        Assert.Equal(paused.ResumeAt, resumeAt); // the loop wakes when it lifts
        var hold = Assert.Single(f.Status.Deferrals);
        Assert.Equal((ControlScope.Usage, true), (hold.Id, hold.Hold));
        Assert.Contains($"it lifts by itself at {paused.ResumeAt:u}", hold.Message);
        Assert.Contains("factory continue --usage", hold.Message);
        Assert.Null(f.Status.FactoryError);
        Assert.Empty(f.Status.ItemErrors); // neither run failed: no item is counted against

        // The pause lifts by itself: both runs resume, and the ready item is started.
        f.Time.SetUtcNow(paused.ResumeAt!.Value.AddSeconds(1));
        f.Seat(3).Finish();
        await f.Loop.PollOnceAsync(CancellationToken.None).WaitAsync(Signal);

        Assert.Equal([WorkState.Review, WorkState.Review, WorkState.Review], await f.States("sc-1", "sc-2", "sc-3"));
        Assert.Equal(2, f.Seat(1).Worker.Calls.Count); // its limited session is resumed
        Assert.Empty(f.Status.Deferrals);
    }

    [Fact]
    public async Task A_stale_helper_in_one_slot_is_shown_once_stops_new_dispatches_and_is_no_item_failure_of_either_run()
    {
        var f = new Factory(slots: 2, 1, 2, 3);
        // sc-1's slot is not ready (its installed helper is stale): found by its run's sandbox set-up, under that slot's lock —
        // while sc-2's worker runs in the other slot.
        f.Seat(1).EnterSandbox = async ct =>
        {
            await f.Seat(2).Started.Task.WaitAsync(Signal, ct);
            throw new FactoryUnavailableException("the worker sandbox: Stale helper: re-run `sudo scripts/setup-worker-user.sh`", new InvalidOperationException("Stale helper"));
        };
        var poll = f.Loop.PollOnceAsync(CancellationToken.None);
        await f.Seat(2).Started.Task.WaitAsync(Signal);
        // sc-1's run has failed for the factory's reason by the time its lock is free again; sc-2's run goes on meanwhile.
        await f.FactoryFailed.Task.WaitAsync(Signal);
        Assert.False(poll.IsCompleted);
        Assert.True((await f.Row("sc-2")).Run!.Holds);
        f.Seat(2).Finish();
        await poll.WaitAsync(Signal);

        Assert.Equal([1, 2], f.Dispatched.Order()); // nothing more was started this poll
        Assert.Equal(1, f.Status.FactoryError!.Count);
        Assert.Equal("sc-1 could not run: the worker sandbox: Stale helper: re-run `sudo scripts/setup-worker-user.sh`", f.Status.FactoryError.Message);
        Assert.Empty(f.Status.ItemErrors); // not sc-1's failure, and never sc-2's
        // sc-1 is left where it was, nothing recorded against it but that its phase ended; sc-2 finished as it would alone.
        Assert.Equal(WorkState.Implement, (await f.Last("sc-1")).State);
        Assert.Empty(f.Seat(1).Worker.Calls);
        Assert.Equal(SlotUseKind.None, SlotUse.Of(await f.Rows("sc-1")).Kind);
        Assert.Equal(WorkState.Review, (await f.Last("sc-2")).State);

        // The helper re-installed: the next poll goes on with sc-1 and starts sc-3, and the error is gone.
        f.Seat(1).EnterSandbox = null;
        f.Seat(1).Finish();
        f.Seat(3).Finish();
        await f.Loop.PollOnceAsync(CancellationToken.None).WaitAsync(Signal);

        Assert.Null(f.Status.FactoryError);
        Assert.Equal([WorkState.Review, WorkState.Review, WorkState.Review], await f.States("sc-1", "sc-2", "sc-3"));
    }

    [Fact]
    public async Task With_one_slot_and_two_runs_the_page_data_shows_one_holding_it_and_the_other_waiting_for_it()
    {
        var f = new Factory(slots: 1, 1, 2);
        var poll = f.Loop.PollOnceAsync(CancellationToken.None);
        var first = await Task.WhenAny(f.Seat(1).Started.Task, f.Seat(2).Started.Task).WaitAsync(Signal);
        var (holder, waiter) = first == f.Seat(1).Started.Task ? (1, 2) : (2, 1);
        await f.Controls.Recorded.Task.WaitAsync(Signal); // the other run's wait is on the record

        var (holding, waiting) = (await f.Row($"sc-{holder}"), await f.Row($"sc-{waiter}"));
        Assert.Equal((true, "implementing", "holds slot 1 (_dftest) to implement"), (holding.Run!.Live, holding.Run.Phase, holding.Run.SlotText));
        Assert.Equal((true, "implementing", "waiting for slot 1 (_dftest) to implement"), (waiting.Run!.Live, waiting.Run.Phase, waiting.Run.SlotText));
        Assert.True(waiting.Run.Waits);
        Assert.Empty(f.Seat(waiter).Worker.Calls);
        // One row for the wait, however long it lasts.
        Assert.Single(await f.Rows($"sc-{waiter}"), r => r.Step == RunPipeline.Steps.SlotWait);

        f.Seat(holder).Finish();
        await f.Seat(waiter).Started.Task.WaitAsync(Signal);
        Assert.Equal("holds slot 1 (_dftest) to implement", (await f.Row($"sc-{waiter}")).Run!.SlotText);
        f.Seat(waiter).Finish();
        await poll.WaitAsync(Signal);

        foreach (var id in new[] { "sc-1", "sc-2" })
        {
            var row = await f.Row(id);
            Assert.Equal((WorkState.Review, false, "review", "slot 1 (_dftest), not held"), (row.State, row.Run!.Live, row.Run.Phase, row.Run.SlotText));
            // A wait's start (only where there was one), the slot's taking and its release: one row each per worker phase.
            Assert.Equal(id == $"sc-{waiter}" ? [RunPipeline.Steps.SlotWait, RunPipeline.Steps.SlotHeld, RunPipeline.Steps.SlotReleased] : new[] { RunPipeline.Steps.SlotHeld, RunPipeline.Steps.SlotReleased },
                (await f.Rows(id)).Where(r => SlotUse.IsStep(r.Step)).Select(r => r.Step));
        }
    }

    [Fact]
    public async Task A_run_that_died_holding_a_slot_is_shown_stale_not_holding()
    {
        var f = new Factory(slots: 2);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var ledger = new WorkLedger(db, f.Time);
            var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-9", "Crashed", Sandbox.FullName, null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Slot, null, "2 _dftest2", CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.SlotHeld, null, SlotUse.Detail(2, "_dftest2", "implement"), CancellationToken.None);
        }

        // No run holds its lock: the row the dead run left is said as what it is.
        var dead = (await f.Row("sc-9")).Run!;
        Assert.Equal((false, false, true), (dead.Live, dead.Holds, dead.Stale));
        // Only what is known: not that the slot is free (another run may hold it now), nor what the next run does.
        Assert.Equal("no run now: the last one ended holding slot 2 (_dftest2) without recording it (crashed or killed)", dead.SlotText);

        // The same rows under a live run (its lock held, in whatever process) are a hold.
        await using var held = await f.Locks.TryAcquireAsync((await f.Row("sc-9")).Id, CancellationToken.None);
        var alive = (await f.Row("sc-9")).Run!;
        Assert.Equal((true, true, false, "holds slot 2 (_dftest2) to implement"), (alive.Live, alive.Holds, alive.Stale, alive.SlotText));
    }

    /// <summary>Puts <paramref name="id"/> in the ledger as a run that died after its Implement phase recorded Review, holding slot <paramref name="slot"/>, left it.</summary>
    private static async Task<WorkItem> DiedHolding(Factory f, string id, int slot, bool recordedReview = true)
    {
        await using var db = f.Contexts.CreateDbContext();
        var ledger = new WorkLedger(db, f.Time);
        var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, id, "Crashed", Sandbox.FullName, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        var user = slot == 1 ? "_dftest" : $"_dftest{slot}";
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Slot, null, $"{slot} {user}", CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.SlotHeld, null, SlotUse.Detail(slot, user, "implement"), CancellationToken.None);
        if (recordedReview)
        {
            await ledger.RecordAsync(item, WorkState.Review, null, null, CancellationToken.None);
        }
        return item;
    }

    [Fact]
    public async Task A_hold_an_earlier_run_left_before_the_items_state_moved_on_is_not_shown_held_by_its_next_live_run()
    {
        // The ledger's last slot-use row is a hold, but Review was recorded after it: that hold was the Implement phase's, whose
        // run died before its release was written. A live run of the item now (its lock held) does not hold the slot.
        var f = new Factory(slots: 2);
        var item = await DiedHolding(f, "sc-9", slot: 2);
        await using var held = await f.Locks.TryAcquireAsync(item.Id, CancellationToken.None);

        var run = (await f.Row("sc-9")).Run!;
        Assert.Equal((true, "review", false, false, false), (run.Live, run.Phase, run.Holds, run.Waits, run.Stale));
        Assert.Equal("slot 2 (_dftest2), not held", run.SlotText);
    }

    [Fact]
    public async Task A_run_ends_on_the_record_the_slot_use_an_earlier_run_left_before_it_does_anything_else()
    {
        var f = new Factory(slots: 2);
        await DiedHolding(f, "sc-1", slot: 2);

        await f.RunAsync(1, CancellationToken.None);

        var rows = await f.Rows("sc-1");
        var released = Assert.Single(rows, r => r.Step == RunPipeline.Steps.SlotReleased);
        Assert.Equal(("2 _dftest2 implement not recorded: ended with an earlier run", StepOutcome.Passed), (released.Detail, released.Outcome));
        Assert.Equal(rows.FindIndex(r => r.Step == RunPipeline.Steps.SlotHeld) + 2, rows.IndexOf(released)); // right after the Review row
        Assert.Equal(SlotUseKind.None, SlotUse.Of(rows).Kind);
        // A second run finds nothing to end: one row per earlier run's leftover, not one per run.
        await f.RunAsync(1, CancellationToken.None);
        Assert.Single(await f.Rows("sc-1"), r => r.Step == RunPipeline.Steps.SlotReleased);
    }

    [Fact]
    public async Task A_run_resuming_an_item_whose_earlier_run_died_inside_its_phase_ends_that_hold_on_the_record_before_its_own()
    {
        var f = new Factory(slots: 2);
        await DiedHolding(f, "sc-2", slot: 2, recordedReview: false);
        f.Seat(2).Finish();

        await f.RunAsync(2, CancellationToken.None);

        var uses = (await f.Rows("sc-2")).Where(r => SlotUse.IsStep(r.Step)).Select(r => (r.Step, r.Detail)).ToList();
        Assert.Equal([
            (RunPipeline.Steps.SlotHeld, "2 _dftest2 implement"),
            (RunPipeline.Steps.SlotReleased, "2 _dftest2 implement not recorded: ended with an earlier run"),
            (RunPipeline.Steps.SlotHeld, "2 _dftest2 implement"),
            (RunPipeline.Steps.SlotReleased, "2 _dftest2 implement"),
        ], uses);
        Assert.Equal(WorkState.Review, (await f.Last("sc-2")).State);
    }

    [Fact]
    public async Task Two_items_shown_holding_one_slot_show_only_the_newer_live_hold_and_the_other_as_stale()
    {
        var f = new Factory(slots: 2);
        var older = await DiedHolding(f, "sc-7", slot: 1, recordedReview: false);
        var newer = await DiedHolding(f, "sc-8", slot: 1, recordedReview: false);
        await using var olderLock = await f.Locks.TryAcquireAsync(older.Id, CancellationToken.None);
        await using var newerLock = await f.Locks.TryAcquireAsync(newer.Id, CancellationToken.None);

        var (a, b) = ((await f.Row("sc-7")).Run!, (await f.Row("sc-8")).Run!);
        Assert.Equal((true, false, false, true), (b.Holds, b.Stale, a.Holds, a.Stale));
        Assert.Equal("holds slot 1 (_dftest) to implement", b.SlotText);
        Assert.Equal("another item's run holds slot 1 (_dftest) now: an earlier run of this one ended holding it without recording it (crashed or killed)", a.SlotText);

        // The newer hold's run is not alive: the older live one is the slot's holder, the newer record is stale.
        await newerLock!.DisposeAsync();
        (a, b) = ((await f.Row("sc-7")).Run!, (await f.Row("sc-8")).Run!);
        Assert.Equal((true, false, false, true), (a.Holds, a.Stale, b.Holds, b.Stale));
    }

    [Fact]
    public async Task A_failed_read_of_which_runs_are_alive_shows_the_rows_with_liveness_unknown()
    {
        var f = new Factory(slots: 2);
        await DiedHolding(f, "sc-9", slot: 2, recordedReview: false);
        var data = new DashboardData(f.Contexts, f.Time, liveRuns: _ => throw new InvalidOperationException("pg_locks unreadable"));

        var run = Assert.Single(await data.ActiveItemsAsync(CancellationToken.None)).Run!;
        Assert.Equal((null, true, "holds slot 2 (_dftest2) to implement"), (run.Live, run.Holds, run.SlotText));
    }

    /// <summary>Rows as a ledger holds them, oldest first: (state, step, detail); step null = a transition.</summary>
    private static List<LedgerEntry> History(params (WorkState State, string? Step, string? Detail)[] rows) =>
        [.. rows.Select((r, i) => new LedgerEntry { Id = i + 1, State = r.State, Step = r.Step, Detail = r.Detail, Outcome = StepOutcome.Passed })];

    [Fact]
    public void The_phase_is_the_state_and_what_the_ledger_says_the_item_is_at_in_it()
    {
        const WorkState gate = WorkState.MergeGate;
        Assert.Equal("intake", RunView.PhaseOf(WorkState.Intake, History((WorkState.Intake, null, null))));
        Assert.Equal("implementing", RunView.PhaseOf(WorkState.Implement, History((WorkState.Intake, null, null), (WorkState.Implement, null, null))));
        Assert.Equal("waiting for CI", RunView.PhaseOf(WorkState.CI, []));
        Assert.Equal("CI fix round", RunView.PhaseOf(WorkState.CIHealing, []));
        Assert.Equal("waiting on its stories", RunView.PhaseOf(WorkState.Planned, []));
        // The merge queue, from the item's own rows: approved and waiting, holding its repository's turn, or not queued yet.
        Assert.Equal("merge gate", RunView.PhaseOf(gate, History((gate, null, null))));
        Assert.Equal("merge queue: waiting for its turn", RunView.PhaseOf(gate, History((gate, null, null), (gate, RunPipeline.Steps.Queued, "abc"))));
        Assert.Equal("merge queue: its repository's turn",
            RunView.PhaseOf(gate, History((gate, null, null), (gate, RunPipeline.Steps.Queued, "abc"), (gate, RunPipeline.Steps.QueueTurn, "abc"))));
        // A pause says where and why — the pipeline's own word for it.
        Assert.Equal($"paused in Implement: {RunPipeline.UsagePaused}",
            RunView.PhaseOf(WorkState.Paused, History((WorkState.Intake, null, null), (WorkState.Implement, null, null), (WorkState.Paused, null, RunPipeline.UsagePaused))));
        Assert.Equal("paused: " + new string('x', 160) + "…", RunView.PhaseOf(WorkState.Paused, History((WorkState.Paused, null, new string('x', 400)))));
        Assert.Equal("escalated", RunView.PhaseOf(WorkState.Escalated, []));
    }

    [Fact]
    public void A_run_view_reads_the_items_slot_and_its_use_of_it_from_its_rows()
    {
        const WorkState at = WorkState.Implement;
        // No worker phase yet: no slot, nothing to say.
        var fresh = RunView.Of(WorkState.Intake, History((WorkState.Intake, null, null)), live: true);
        Assert.Equal((null, "", false, false), (fresh.Slot, fresh.SlotText, fresh.Holds, fresh.Waits));
        // Waiting for any free slot (an item with none yet), then for its own once it has one.
        Assert.Equal("waiting for a slot to implement",
            RunView.Of(at, History((at, null, null), (at, RunPipeline.Steps.SlotWait, SlotUse.Detail(null, null, "implement"))), true).SlotText);
        var own = RunView.Of(WorkState.Fixing, History((at, null, null), (at, RunPipeline.Steps.Slot, "3 _factory3"), (at, RunPipeline.Steps.SlotHeld, "3 _factory3 implement"),
            (at, RunPipeline.Steps.SlotReleased, "3 _factory3 implement"), (WorkState.Fixing, null, null),
            (WorkState.Fixing, RunPipeline.Steps.SlotWait, "3 _factory3 run the review fix")), true);
        Assert.Equal((3, "_factory3", true, "waiting for slot 3 (_factory3) to run the review fix"), (own.Slot, own.SlotUser, own.Waits, own.SlotText));
        // A wait that ended without the slot (a Pause, the freeze): neither holding nor waiting.
        var ended = RunView.Of(WorkState.Paused, History((at, null, null), (at, RunPipeline.Steps.SlotWait, "any - implement"),
            (at, RunPipeline.Steps.SlotReleased, "any - not taken: the wait to implement ended"), (WorkState.Paused, null, RunPipeline.FreezePaused)), false);
        Assert.Equal((false, false, false, ""), (ended.Holds, ended.Waits, ended.Stale, ended.SlotText));
        // An item worked before slots, or by a one-slot factory, with no slot rows at all: slot 1's.
        var old = RunView.Of(WorkState.Review, History((at, null, null), (at, RunPipeline.Steps.WorkerStarted, "4321"), (WorkState.Review, null, null)), null);
        Assert.Equal((1, null, "slot 1, not held"), (old.Slot, old.SlotUser, old.SlotText));
        // Liveness unknown: the rows' word stands. Known dead: it is stale.
        var held = History((at, null, null), (at, RunPipeline.Steps.SlotHeld, "1 _factory implement"));
        Assert.True(RunView.Of(at, held, null).Holds);
        Assert.Equal((false, true), (RunView.Of(at, held, false).Holds, RunView.Of(at, held, false).Stale));
    }

    [Fact]
    public async Task An_items_first_worker_phase_in_a_factory_with_slots_prepares_a_fresh_worktree_and_records_none_lost()
    {
        // The slot's rows are written as the phase starts; they are not work of the attempt, so Implement does not take the
        // attempt for a resumed one, look for a worktree that never was and record it lost.
        var f = new Factory(slots: 2, 1);
        var seat = f.Seat(1);
        seat.Finish();
        await using var db = f.Contexts.CreateDbContext();
        var workspaces = new FakeWorkspaces(worktreeExists: false);

        var outcome = await new RunPipeline(new FakeWorkSource(Story), new WorkLedger(db, f.Time), f.Locks, workspaces, seat.Worker, new FakePullRequests(), Sandbox,
            TextWriter.Null, slots: f.Slots).RunAsync(1, CancellationToken.None);

        Assert.Equal((WorkState.Review, (string?)null), (outcome.State, outcome.Error));
        Assert.StartsWith("prepare ", workspaces.Calls[0]);
        Assert.DoesNotContain(await f.Rows("sc-1"), r => r.Step == RunPipeline.Steps.WorktreeLost);
        Assert.Equal([RunPipeline.Steps.Slot, RunPipeline.Steps.SlotHeld], (await f.Rows("sc-1")).Where(r => r.Step is not null).Select(r => r.Step).Skip(1).Take(2));
    }
}
