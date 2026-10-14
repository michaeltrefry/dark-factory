using System.Collections.Concurrent;
using System.Reflection;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25988: the intake loop runs up to <c>Intake:MaxConcurrentRuns</c> items at once — never more, each item once per poll,
/// failures counted per item — and what stops a poll's new work (a deferral by the freeze, a factory-wide failure) still does
/// with several runs in flight. With the default (1) it runs them one after another.
/// </summary>
public class IntakeDispatchTests
{
    /// <summary>A work source that only lists: <see cref="Ready"/> as ready, named like Shortcut stories.</summary>
    public class ListingSource : DispatchProxy
    {
        public IReadOnlyList<int> Ready { get; set; } = [];

        /// <summary>The items whose claim was renewed, once per renewal.</summary>
        public ConcurrentQueue<int> Renewed { get; } = new();

        private int _listings;

        /// <summary>How many times the ready items were listed.</summary>
        public int Listings => Volatile.Read(ref _listings);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IWorkSource.ListReadyAsync):
                    Interlocked.Increment(ref _listings);
                    return Task.FromResult(Ready);
                case $"get_{nameof(IWorkSource.Naming)}":
                    return ItemNaming.Shortcut;
                case nameof(IWorkSource.RenewClaimAsync):
                    Renewed.Enqueue((int)args![0]!);
                    return Task.FromResult(ClaimRenewal.Held);
                default:
                    throw new NotSupportedException(method.Name);
            }
        }

        public static ListingSource Of(IWorkSource source) => (ListingSource)(object)source;

        public static IWorkSource Listing(params int[] ready)
        {
            var source = Create<IWorkSource, ListingSource>();
            ((ListingSource)(object)source).Ready = ready;
            return source;
        }
    }

    /// <summary>Runs that start when the loop dispatches them and end when the test says how.</summary>
    private sealed class GatedRunner(params int[] inFlight) : IItemRunner
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<RunOutcome>> _runs = new();
        private int _running;
        private int _most;

        public ConcurrentQueue<int> Started { get; } = new();
        public ConcurrentQueue<int> GaveUp { get; } = new();
        public int Running => Volatile.Read(ref _running);
        public int Most => Volatile.Read(ref _most);
        /// <summary>When set, a run of the item ends at once this way instead of waiting for the test.</summary>
        public Func<int, Exception?>? Throws { get; set; }
        public bool EndAtOnce { get; set; }
        /// <summary>When set, a run ends only when the test ends it, also after its token is cancelled (a run finishing its step).</summary>
        public bool IgnoresCancellation { get; set; }
        /// <summary>Completes once a run has seen its token cancelled.</summary>
        public TaskCompletionSource SawCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(inFlight);

        private readonly ConcurrentDictionary<int, TaskCompletionSource> _startedAt = new();

        /// <summary>Completes once <paramref name="count"/> runs have started: a signal, so a test waits on no clock.</summary>
        public Task WhenStarted(int count)
        {
            var signal = _startedAt.GetOrAdd(count, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Started.Count >= count)
            {
                signal.TrySetResult();
            }
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Started.Enqueue(id);
            foreach (var (count, signal) in _startedAt)
            {
                if (Started.Count >= count)
                {
                    signal.TrySetResult();
                }
            }
            var now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, seen) != seen)
            {
            }
            try
            {
                if (Throws?.Invoke(id) is { } ex)
                {
                    throw ex;
                }
                if (EndAtOnce)
                {
                    return Ok;
                }
                var run = _runs.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                if (IgnoresCancellation)
                {
                    using var _ = ct.Register(() => SawCancellation.TrySetResult());
                    return await run;
                }
                return await run.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct)
        {
            GaveUp.Enqueue(id);
            return Task.FromResult<string?>("escalated");
        }

        private TaskCompletionSource<RunOutcome> Run(int id) => _runs.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void End(int id, RunOutcome? outcome = null) => Run(id).SetResult(outcome ?? Ok);

        public void Fail(int id, Exception ex) => Run(id).SetException(ex);
    }

    private static readonly RunOutcome Ok = new(1, WorkState.Review, null, null, null);

    private static IntakeLoop Loop(IWorkSource source, IItemRunner runner, int concurrent, IntakeStatus? status = null, int maxFailures = 3,
        IControls? controls = null) =>
        new([new IntakeLane(source, runner)], new IntakeOptions(TimeSpan.FromSeconds(60), maxFailures, concurrent), TimeProvider.System, NullLogger<IntakeLoop>.Instance,
            controls, status: status);

    /// <summary>The ledger's controls over a ledger of their own, with the context factory a Continue needs.</summary>
    private static (LedgerControls Controls, LedgerDbContextFactory Contexts) NewControls()
    {
        var contexts = new LedgerDbContextFactory(new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new LedgerControls(contexts, TimeProvider.System), contexts);
    }

    /// <summary>Controls that cannot be read once <see cref="Broken"/> is set.</summary>
    private sealed class BreakableControls(IControls inner) : IControls
    {
        public Exception? Broken { get; set; }

        private Task<T> Read<T>(Func<Task<T>> read) => Broken is { } ex ? Task.FromException<T>(ex) : read();

        public Task<Control?> GetAsync(string scope, CancellationToken ct) => Read(() => inner.GetAsync(scope, ct));
        public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct) => Read(() => inner.EffectiveAsync(externalId, epicId, ct));
        public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => Read(() => inner.ListAsync(ct));
        public Task<Control?> UsagePauseAsync(CancellationToken ct) => Read(() => inner.UsagePauseAsync(ct));
        public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) => inner.PauseForUsageAsync(resumeAt, reason, ct);
        public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) =>
            inner.FreezeAsync(trigger, detail, readChangedAt, ct);
        public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => inner.SetAsync(scope, state, by, ct);
        public Task ClearAsync(string scope, CancellationToken ct) => inner.ClearAsync(scope, ct);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(condition(), what);
    }

    /// <summary>Long enough for a dispatcher that would start one run too many to have done so.</summary>
    private static Task Settle() => Task.Delay(150);

    [Fact]
    public async Task Up_to_the_configured_number_of_items_run_at_once_in_the_polls_order_and_never_more()
    {
        var runner = new GatedRunner(1, 2);
        var poll = Loop(ListingSource.Listing(3, 4, 5), runner, concurrent: 2).PollOnceAsync(CancellationToken.None);

        // In-flight items first, two at once; the third waits for one of them to end.
        await Eventually(() => runner.Running == 2, "two runs start");
        await Settle();
        Assert.Equal([1, 2], runner.Started.Order());
        runner.End(1);
        await Eventually(() => runner.Started.Count == 3, "the next item starts when a run ends");
        await Settle();
        Assert.Equal([1, 2, 3], runner.Started.Order());
        runner.End(3);
        await Eventually(() => runner.Started.Count == 4, "and the next");
        runner.End(2);
        await Eventually(() => runner.Started.Count == 5, "and the last");
        Assert.False(poll.IsCompleted); // a poll ends when its runs have
        runner.End(4);
        runner.End(5);
        await poll;

        Assert.Equal([1, 2, 3, 4, 5], runner.Started.Order());
        Assert.Equal(2, runner.Most);
    }

    [Fact]
    public async Task By_default_items_run_one_after_another_in_order()
    {
        Assert.Equal(1, new IntakeOptions(TimeSpan.FromSeconds(60)).MaxConcurrentRuns);
        var runner = new GatedRunner(5, 3);
        var loop = new IntakeLoop([new IntakeLane(ListingSource.Listing(3, 7), runner)], new IntakeOptions(TimeSpan.FromSeconds(60)), TimeProvider.System, NullLogger<IntakeLoop>.Instance);
        var poll = loop.PollOnceAsync(CancellationToken.None);

        foreach (var (id, started) in new[] { (5, 1), (3, 2), (7, 3) })
        {
            await Eventually(() => runner.Started.Count == started, $"run {started} starts");
            await Settle();
            Assert.Equal((started, 1), (runner.Started.Count, runner.Running)); // nothing else beside it
            runner.End(id);
        }
        await poll;

        Assert.Equal([5, 3, 7], runner.Started);
        Assert.Equal(1, runner.Most);
    }

    [Fact]
    public async Task An_item_is_run_once_a_poll_however_often_it_is_listed_and_however_many_runs_there_is_room_for()
    {
        var runner = new GatedRunner(1, 1, 2) { EndAtOnce = true };

        await Loop(ListingSource.Listing(2, 3, 3, 1), runner, concurrent: 4).PollOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], runner.Started.Order());
    }

    [Fact]
    public async Task A_failing_run_is_counted_against_its_own_item_alone_whatever_runs_beside_it()
    {
        var runner = new GatedRunner(1, 2, 3) { EndAtOnce = true, Throws = id => id == 2 ? new InvalidOperationException("story 2 is gone") : null };
        var status = new IntakeStatus(TimeProvider.System);
        var loop = Loop(ListingSource.Listing(), runner, concurrent: 3, status);

        for (var poll = 1; poll <= 4; poll++)
        {
            await loop.PollOnceAsync(CancellationToken.None);
            Assert.Equal(poll, status.ItemErrors["sc-2"].Count);
            Assert.Equal(["sc-2"], status.ItemErrors.Keys);
            Assert.Equal(poll >= 3 ? new[] { 2 } : [], runner.GaveUp); // given up on once, at Intake:MaxItemFailures
        }
        Assert.Null(status.FactoryError);
        Assert.Equal(12, runner.Started.Count); // the others ran every poll
    }

    [Fact]
    public async Task A_run_the_freeze_defers_starts_no_new_item_while_the_runs_beside_it_finish()
    {
        var runner = new GatedRunner(1, 2);
        var status = new IntakeStatus(TimeProvider.System);
        var poll = Loop(ListingSource.Listing(3, 4), runner, concurrent: 2, status).PollOnceAsync(CancellationToken.None);
        await Eventually(() => runner.Running == 2, "two runs start");

        // The factory is frozen: the deferred run's place is not given to a ready item, nor to another in-flight item of its lane.
        runner.End(1, Ok with { Deferred = "factory frozen (main-red)" });
        await Settle();
        Assert.Equal([1, 2], runner.Started.Order());
        Assert.False(poll.IsCompleted);
        runner.End(2);
        await poll;

        Assert.Equal([1, 2], runner.Started.Order());
        Assert.Contains("sc-1 deferred: factory frozen (main-red)", status.DeferredRun!.Message);
    }

    [Fact]
    public async Task A_factory_wide_failure_starts_nothing_more_is_shown_once_and_escalates_no_item_even_when_two_runs_hit_it()
    {
        var runner = new GatedRunner(1, 2, 3);
        var status = new IntakeStatus(TimeProvider.System);
        var loop = Loop(ListingSource.Listing(4), runner, concurrent: 2, status, maxFailures: 1);
        var poll = loop.PollOnceAsync(CancellationToken.None);
        await Eventually(() => runner.Running == 2, "two runs start");

        runner.Fail(1, new FactoryUnavailableException("the worker sandbox: Stale helper", new InvalidOperationException("Stale helper")));
        await Settle();
        Assert.Equal([1, 2], runner.Started.Order());
        runner.Fail(2, new FactoryUnavailableException("the worker sandbox: Stale helper", new InvalidOperationException("Stale helper")));
        await poll;

        Assert.Equal([1, 2], runner.Started.Order());
        Assert.Equal(1, status.FactoryError!.Count); // one poll hit it, however many of its runs did
        Assert.Contains("sc-1 could not run: the worker sandbox: Stale helper", status.FactoryError.Message);
        Assert.Empty(status.ItemErrors);
        Assert.Empty(runner.GaveUp);
    }

    // sc-25991: what stops a poll's new work with runs in flight. These wait on signals only (a run's start, the poll's end).

    [Theory]
    [InlineData(ControlScope.Usage, "the factory is paused for usage (worker-rate-limited)", "factory continue --usage")]
    [InlineData(ControlScope.Factory, "the factory is paused", "factory continue --factory")]
    [InlineData(ControlScope.Freeze, "the factory is frozen (main-red)", "factory continue --freeze")]
    public async Task A_factory_wide_control_set_with_runs_in_flight_starts_no_ready_item_and_is_shown_with_what_lifts_it(string scope, string why, string lifts)
    {
        // In flight: 1, 2 and 5; ready: 3 and 4. Two at once.
        var runner = new GatedRunner(1, 2, 5);
        var status = new IntakeStatus(TimeProvider.System);
        var (controls, contexts) = NewControls();
        var loop = Loop(ListingSource.Listing(3, 4), runner, concurrent: 2, status, controls: controls);
        var poll = loop.PollOnceAsync(CancellationToken.None);
        await runner.WhenStarted(2);
        Assert.Empty(status.Deferrals);

        // Set while both run — by one of them (its worker hit the usage limit), the freeze evaluator, or a person.
        switch (scope)
        {
            case ControlScope.Usage:
                await controls.PauseForUsageAsync(DateTimeOffset.UtcNow.AddHours(1), "worker-rate-limited", CancellationToken.None);
                break;
            case ControlScope.Factory:
                await controls.SetAsync(ControlScope.Factory, ControlState.Paused, "tester", CancellationToken.None);
                break;
            default:
                await controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
                break;
        }
        // An in-flight item the poll listed is still run (its run pauses itself at its first check, or finishes its Stop)…
        runner.End(1);
        await runner.WhenStarted(3);
        Assert.Equal([1, 2, 5], runner.Started.Order());
        // …and no ready item is, whichever run ends: the hold is read before each would start.
        runner.End(5);
        runner.End(2);
        await poll.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([1, 2, 5], runner.Started.Order());
        var hold = Assert.Single(status.Deferrals);
        Assert.Equal((scope, true), (hold.Id, hold.Hold));
        Assert.StartsWith($"new work held: {why}; ", hold.Message);
        Assert.Contains(lifts, hold.Lifts);
        Assert.Null(status.DeferredRun); // no run was deferred: a hold is not one
        Assert.Null(status.FactoryError);
        Assert.Empty(status.ItemErrors);

        // Lifted: the next poll's first read drops the hold, and the ready items are started.
        Assert.True((await new ControlActions(controls, contexts).ContinueAsync(scope, "tester", CancellationToken.None)).Ok);
        runner.EndAtOnce = true;
        await loop.PollOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(status.Deferrals);
        Assert.Equal([1, 1, 2, 2, 3, 4, 5, 5], runner.Started.Order());
    }

    [Fact]
    public async Task Controls_that_cannot_be_read_before_a_ready_item_starts_are_the_factorys_failure_and_start_nothing_more()
    {
        var runner = new GatedRunner(1);
        var status = new IntakeStatus(TimeProvider.System);
        var controls = new BreakableControls(NewControls().Controls);
        var poll = Loop(ListingSource.Listing(3, 4), runner, concurrent: 1, status, maxFailures: 1, controls: controls).PollOnceAsync(CancellationToken.None);
        await runner.WhenStarted(1);

        controls.Broken = new InvalidOperationException("the controls table is gone");
        runner.End(1);
        await poll.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([1], runner.Started); // never dispatched blind (E2)
        Assert.Equal((1, "sc-3 not started: the controls could not be read: the controls table is gone"), (status.FactoryError!.Count, status.FactoryError.Message));
        Assert.Empty(status.ItemErrors);
        Assert.Empty(runner.GaveUp);
    }

    [Fact]
    public async Task A_factory_wide_failure_of_one_run_is_not_a_failure_of_the_run_beside_it_whose_own_failure_is_counted_against_it_alone()
    {
        var runner = new GatedRunner(1, 2, 3);
        var status = new IntakeStatus(TimeProvider.System);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        status.Changed += () =>
        {
            if (status.FactoryError is not null)
            {
                shown.TrySetResult();
            }
        };
        var poll = Loop(ListingSource.Listing(4), runner, concurrent: 2, status, maxFailures: 2).PollOnceAsync(CancellationToken.None);
        await runner.WhenStarted(2);

        // sc-1's slot has a stale helper (the factory's failure); sc-2, in flight beside it, then fails for a reason of its own —
        // once the factory's failure is shown, by when the poll has stopped dispatching.
        runner.Fail(1, new FactoryUnavailableException("the worker sandbox: Stale helper", new InvalidOperationException("Stale helper")));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(60));
        runner.Fail(2, new InvalidOperationException("story 2 is gone"));
        await poll.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([1, 2], runner.Started.Order()); // nothing more this poll
        Assert.Equal((1, "sc-1 could not run: the worker sandbox: Stale helper"), (status.FactoryError!.Count, status.FactoryError.Message));
        Assert.Equal(["sc-2"], status.ItemErrors.Keys); // sc-1 is not counted against, and sc-2 only for its own failure
        Assert.Equal((1, "InvalidOperationException: story 2 is gone"), (status.ItemErrors["sc-2"].Count, status.ItemErrors["sc-2"].Message));
        Assert.Empty(runner.GaveUp);
    }

    [Fact]
    public async Task Two_different_factory_wide_failures_in_one_poll_are_one_error_naming_both()
    {
        var status = new IntakeStatus(TimeProvider.System);

        for (var polls = 1; polls <= 2; polls++)
        {
            var runner = new GatedRunner(1, 2);
            var poll = Loop(ListingSource.Listing(), runner, concurrent: 2, status).PollOnceAsync(CancellationToken.None);
            await runner.WhenStarted(2);
            runner.Fail(1, new FactoryUnavailableException("the worker sandbox: Stale helper", new InvalidOperationException("Stale helper")));
            runner.Fail(2, new FactoryUnavailableException("the ledger migration: timed out", new TimeoutException("timed out")));
            await poll.WaitAsync(TimeSpan.FromSeconds(60));

            // One poll, one error: counted once however many of its runs failed, each reason said once.
            Assert.Equal(polls, status.FactoryError!.Count);
            Assert.Contains("sc-1 could not run: the worker sandbox: Stale helper", status.FactoryError.Message);
            Assert.Contains("sc-2 could not run: the ledger migration: timed out", status.FactoryError.Message);
            Assert.Contains("; also: ", status.FactoryError.Message);
            Assert.Empty(status.ItemErrors);
        }
    }

    [Fact]
    public async Task Every_run_the_freeze_defers_in_one_poll_is_shown_until_runs_are_dispatched_again()
    {
        var runner = new GatedRunner(1, 2);
        var status = new IntakeStatus(TimeProvider.System);
        var poll = Loop(ListingSource.Listing(3), runner, concurrent: 2, status).PollOnceAsync(CancellationToken.None);
        await runner.WhenStarted(2);

        runner.End(1, Ok with { Deferred = "factory frozen (main-red): main is red; `factory continue --freeze` clears it" });
        runner.End(2, Ok with { Deferred = "factory frozen (main-red): main is red; `factory continue --freeze` clears it" });
        await poll.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([1, 2], runner.Started.Order());
        Assert.Equal(["sc-1", "sc-2"], status.Deferrals.Select(d => d.Id).Order());
        Assert.All(status.Deferrals, d => Assert.Equal((false, 1), (d.Hold, d.Count)));
        Assert.All(status.Deferrals, d => Assert.Contains("factory continue --freeze", d.Message)); // each says what lifts it
        Assert.Contains(" deferred: factory frozen (main-red)", status.DeferredRun!.Message);

        // Dispatched again: none stands.
        var next = new GatedRunner(1, 2) { EndAtOnce = true };
        await Loop(ListingSource.Listing(3), next, concurrent: 2, status).PollOnceAsync(CancellationToken.None);
        Assert.Empty(status.Deferrals);
        Assert.Null(status.DeferredRun);
    }

    [Fact]
    public void The_status_keeps_each_hold_until_the_loop_reads_it_lifted_and_tells_viewers_only_of_changes()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var status = new IntakeStatus(time);
        var changes = 0;
        status.Changed += () => changes++;
        var first = time.GetUtcNow();

        status.Holding([(ControlScope.Usage, "paused for usage", "lifts at noon")]);
        status.Deferred("sc-1", "factory frozen");
        time.Advance(TimeSpan.FromMinutes(1));
        status.Holding([(ControlScope.Usage, "paused for usage", "lifts at noon")]); // read again, unchanged: nothing to tell
        Assert.Equal(2, changes);
        Assert.Equal([(ControlScope.Usage, true, first), ("sc-1", false, first)], status.Deferrals.Select(d => (d.Id, d.Hold, d.At)));
        Assert.Equal("new work held: paused for usage; lifts at noon", status.Deferrals[0].Message);

        // Runs dispatched again clear the runs' deferrals, never a control's hold; the loop's next read lifts that.
        status.NotDeferred();
        Assert.Equal([ControlScope.Usage], status.Deferrals.Select(d => d.Id));
        status.Holding([(ControlScope.Freeze, "frozen", "a Continue")]);
        Assert.Equal([ControlScope.Freeze], status.Deferrals.Select(d => d.Id));
        status.Holding([]);
        Assert.Empty(status.Deferrals);
        Assert.Equal(5, changes);

        // One poll's factory-wide error: one count, each reason once.
        status.FactoryFailed("sc-1 could not run: stale helper");
        status.FactoryFailedAlso("sc-2 could not run: stale helper too");
        status.FactoryFailedAlso("sc-2 could not run: stale helper too");
        Assert.Equal((1, "sc-1 could not run: stale helper; also: sc-2 could not run: stale helper too"), (status.FactoryError!.Count, status.FactoryError.Message));
    }

    [Fact]
    public async Task Shutting_down_ends_a_poll_with_runs_in_flight()
    {
        var runner = new GatedRunner(1, 2, 3);
        using var stop = new CancellationTokenSource();
        var poll = Loop(ListingSource.Listing(), runner, concurrent: 2).PollOnceAsync(stop.Token);
        await Eventually(() => runner.Running == 2, "two runs start");

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        Assert.Equal([1, 2], runner.Started.Order());
        Assert.Empty(runner.GaveUp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IntakeOptions.MaxConcurrentRunsLimit + 1)]
    public void The_loop_takes_no_number_of_runs_outside_its_range(int runs) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new IntakeOptions(TimeSpan.FromSeconds(60), MaxConcurrentRuns: runs));

    [Fact]
    public void The_factory_work_host_runs_as_many_items_at_once_as_configured()
    {
        static IntakeOptions Registered(Dictionary<string, string?> config)
        {
            var services = new ServiceCollection();
            services.AddIntake(new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), new InMemorySecrets()));
            return (IntakeOptions)services.Single(s => s.ServiceType == typeof(IntakeOptions)).ImplementationInstance!;
        }

        Assert.Equal(1, Registered([]).MaxConcurrentRuns);
        Assert.Equal(4, Registered(new() { ["Intake:MaxConcurrentRuns"] = "4" }).MaxConcurrentRuns);
    }

    // sc-25992: a poll does not wait for its runs. Each poll renews claims and dispatches into the capacity the runs still in
    // flight leave, never a second run of an item in flight. These wait on signals (a run's start, a poll's end), not a clock.

    [Fact]
    public async Task The_work_loop_polls_again_on_its_interval_and_starts_a_newly_ready_item_while_a_long_run_is_still_in_flight()
    {
        var runner = new GatedRunner(1);
        var source = ListingSource.Listing();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var loop = new IntakeLoop([new IntakeLane(source, runner)], new IntakeOptions(TimeSpan.FromSeconds(60), MaxConcurrentRuns: 2), time,
            NullLogger<IntakeLoop>.Instance);

        await loop.StartAsync(CancellationToken.None);
        await runner.WhenStarted(1);
        await Eventually(() => loop.Waits >= 1, "the first poll arms its wake-up with its run still in flight");
        ListingSource.Of(source).Ready = [5];
        time.Advance(TimeSpan.FromSeconds(60));
        await runner.WhenStarted(2);

        Assert.Equal([1, 5], runner.Started);
        Assert.Equal(["sc-1", "sc-5"], loop.RunsInFlight.Order());
        Assert.Equal([1, 1], ListingSource.Of(source).Renewed); // the second poll renewed sc-1's claim while it ran
        runner.End(1);
        runner.End(5);
        await loop.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(loop.RunsInFlight);
    }

    [Fact]
    public async Task A_poll_renews_the_claim_of_an_item_in_flight_and_never_starts_it_a_second_time()
    {
        var runner = new GatedRunner(1);
        var source = ListingSource.Listing(1);
        var loop = Loop(source, runner, concurrent: 3);

        var first = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await runner.WhenStarted(1);
        var second = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await second.Done.WaitAsync(TimeSpan.FromSeconds(60)); // its dispatch has ended: it passed over sc-1, in flight and listed again (in flight and ready)

        Assert.Equal([1], runner.Started);
        Assert.Equal([1, 1], ListingSource.Of(source).Renewed);
        Assert.False(first.Done.IsCompleted);
        runner.End(1);
        await first.Done.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal([1], runner.Started);
    }

    [Fact]
    public async Task A_poll_with_no_free_capacity_lists_nothing_and_the_earlier_poll_starts_its_next_item_when_a_run_ends()
    {
        var runner = new GatedRunner(1, 2);
        var source = ListingSource.Listing(5);
        var loop = Loop(source, runner, concurrent: 2);

        var first = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await runner.WhenStarted(2);
        var second = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await second.Done.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, ListingSource.Of(source).Listings); // every run was in flight: the second poll only renewed
        Assert.Equal([1, 2, 1, 2], ListingSource.Of(source).Renewed);
        Assert.Equal([1, 2], runner.Started.Order());
        runner.End(2);
        await runner.WhenStarted(3);
        Assert.Equal([1, 2, 5], runner.Started.Order());
        Assert.Equal(5, runner.Started.Last());
        runner.End(1);
        runner.End(5);
        await first.Done.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(2, runner.Most);
    }

    [Fact]
    public async Task With_one_run_at_a_time_a_poll_during_the_run_renews_claims_and_starts_nothing_and_the_order_is_unchanged()
    {
        var runner = new GatedRunner(1);
        var source = ListingSource.Listing(5);
        var loop = Loop(source, runner, concurrent: 1);

        var first = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await runner.WhenStarted(1);
        var second = await loop.StartPollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        await second.Done.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([1], runner.Started);
        Assert.Equal([1, 1], ListingSource.Of(source).Renewed);
        runner.End(1);
        await runner.WhenStarted(2);
        Assert.Equal([1, 5], runner.Started);
        runner.End(5);
        await first.Done.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, runner.Most);
    }

    [Fact]
    public async Task Stopping_the_work_loop_waits_for_the_runs_in_flight()
    {
        var runner = new GatedRunner(1) { IgnoresCancellation = true };
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var loop = new IntakeLoop([new IntakeLane(ListingSource.Listing(), runner)], new IntakeOptions(TimeSpan.FromSeconds(60), MaxConcurrentRuns: 2), time,
            NullLogger<IntakeLoop>.Instance);
        await loop.StartAsync(CancellationToken.None);
        await runner.WhenStarted(1);
        await Eventually(() => loop.Waits >= 1, "the poll is done and the loop waits with its run in flight");

        var stop = loop.StopAsync(CancellationToken.None);
        await runner.SawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(60));
        // The run is finishing its step: the loop has not ended under it.
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(300)));
        Assert.False(loop.ExecuteTask!.IsCompleted);
        runner.End(1);
        await stop.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(loop.ExecuteTask.IsCompleted);
        Assert.Empty(loop.RunsInFlight);
    }
}
