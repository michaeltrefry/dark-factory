using System.Collections.Concurrent;
using System.Reflection;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
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

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IWorkSource.ListReadyAsync) => Task.FromResult(Ready),
            $"get_{nameof(IWorkSource.Naming)}" => ItemNaming.Shortcut,
            _ => throw new NotSupportedException(method.Name),
        };

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

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(inFlight);

        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Started.Enqueue(id);
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
                return EndAtOnce ? Ok : await _runs.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
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

    private static IntakeLoop Loop(IWorkSource source, IItemRunner runner, int concurrent, IntakeStatus? status = null, int maxFailures = 3) =>
        new(source, runner, new IntakeOptions(TimeSpan.FromSeconds(60), maxFailures, concurrent), TimeProvider.System, NullLogger<IntakeLoop>.Instance,
            status: status);

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
        var loop = new IntakeLoop(ListingSource.Listing(3, 7), runner, new IntakeOptions(TimeSpan.FromSeconds(60)), TimeProvider.System, NullLogger<IntakeLoop>.Instance);
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
}
