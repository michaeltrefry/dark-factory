using System.Net;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The usage pause (S9b): the router's usage report and a worker's exhaustion error pause the whole factory until the
/// plans reset, without a user's Pause/Continue and the usage pause lifting each other. Stub router, injected clock.
/// </summary>
public class UsagePauseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly WorkStory Story = new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");

    private const string RouterExhausted =
        """API Error: 429 {"type":"error","error":{"type":"api_error","message":"All enrolled subscription accounts are currently unavailable."}}""";

    private sealed class Ledgers
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Ledgers() => Db = new LedgerDbContext(_options);

        public FakeTimeProvider Time { get; } = new(T0);
        public LedgerDbContext Db { get; }
        public LedgerDbContextFactory Contexts => new(_options);
        public IControls Controls => new LedgerControls(Contexts, Time);
        public WorkLedger Ledger => new(Db, TimeProvider.System);
        public InProcessRunLocks Locks { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();

        public RunPipeline Pipeline(IWorkSource source, IWorker worker) =>
            new(source, Ledger, Locks, Workspaces, worker, Prs, Sandbox, TextWriter.Null, controls: Controls,
                controlPollInterval: TimeSpan.FromMilliseconds(10));

        public async Task<IReadOnlyList<int>> InFlight()
        {
            await using var db = new LedgerDbContext(_options);
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), CancellationToken.None, Controls);
        }

        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        public async Task<List<(WorkState, string?)>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => (r.State, r.Detail)).ToList();
    }

    private sealed class Runner(Ledgers l, IWorkSource source, IWorker worker) : IItemRunner
    {
        private int _completed;
        public int Completed => Volatile.Read(ref _completed);

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => l.InFlight();

        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            var outcome = await l.Pipeline(source, worker).RunAsync(id, ct);
            Interlocked.Increment(ref _completed);
            return outcome;
        }
    }

    /// <summary>A stub router usage route; each call answers with <see cref="Report"/> (or <see cref="Status"/>).</summary>
    private sealed class StubRouter
    {
        public string Report { get; set; } = Usage(allExhausted: false, resumesAt: null);
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public FakeApi Api { get; }

        public StubRouter() => Api = new FakeApi().On("GET /v1/subscriptions/usage", r =>
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal("rk", r.Headers[ClaudeWorker.RouterKeyHeader]);
            return FakeApi.Json(Status, Report);
        });

        public RouterClient Client => new(Api.Client("http://router.test/"), "rk");
    }

    /// <summary>The router's report shape (<c>GET /v1/subscriptions/usage</c>) with one Anthropic credential.</summary>
    private static string Usage(bool allExhausted, DateTimeOffset? resumesAt, int known = 1) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["as_of"] = T0,
            ["all_exhausted"] = allExhausted,
            ["resumes_at"] = resumesAt,
            ["known_credentials"] = known,
            ["observed_credentials"] = known,
            ["credentials"] = known == 0 ? Array.Empty<object>() : new object[]
            {
                new Dictionary<string, object?>
                {
                    ["provider"] = "anthropic", ["source"] = "presented", ["credential_key"] = "k1", ["routable"] = true,
                    ["observed"] = true, ["exhausted"] = allExhausted, ["resumes_at"] = resumesAt, ["overage_in_use"] = false,
                    ["windows"] = new { primary = new { utilization = 1.0, window_minutes = 300, reset_at = resumesAt, exhausted = allExhausted } },
                },
            },
        });

    private static UsageMonitor Monitor(StubRouter router, Ledgers l) =>
        new(router.Client, l.Controls, new UsageOptions(Interval), l.Time, NullLogger<UsageMonitor>.Instance);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task With_every_window_exhausted_until_T_no_step_dispatches_before_T_and_dispatch_resumes_at_T_by_itself()
    {
        var l = new Ledgers();
        var resetAt = T0 + TimeSpan.FromSeconds(150); // between two intake ticks
        // The stub keeps reporting the same reading after T, as a router that has routed nothing since would.
        var router = new StubRouter { Report = Usage(allExhausted: true, resumesAt: resetAt) };
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var worker = new FakeWorker(Reports(Ok));
        var runner = new Runner(l, source, worker);
        var loop = new IntakeLoop(source, runner, new IntakeOptions(Interval), l.Time, NullLogger<IntakeLoop>.Instance, l.Controls, Monitor(router, l));

        await loop.StartAsync(CancellationToken.None);
        await Eventually(() => router.Calls == 1);
        l.Time.Advance(Interval);
        await Eventually(() => router.Calls == 2);
        l.Time.Advance(Interval);
        await Eventually(() => router.Calls == 3);
        l.Time.Advance(resetAt - l.Time.GetUtcNow() - TimeSpan.FromSeconds(1)); // one second before T
        await Task.Delay(200);

        Assert.Empty(worker.Calls);
        Assert.Empty(await l.Db.WorkItems.ToListAsync());
        Assert.False(board.Story(101)["owner_ids"]!.AsArray().Any()); // not even claimed
        var pause = await l.Controls.GetAsync(ControlScope.Usage, CancellationToken.None);
        Assert.Equal((ControlState.Paused, UsagePause.UsageExhausted, resetAt), (pause!.State, pause.Reason, pause.ResumeAt));

        l.Time.Advance(TimeSpan.FromSeconds(1)); // T: no tick is due, nobody intervenes
        await Eventually(() => runner.Completed == 1);
        await loop.StopAsync(CancellationToken.None);

        Assert.Single(worker.Calls);
        Assert.Equal(WorkState.Review, (await l.Db.WorkItems.SingleAsync()).State);
        Assert.Equal(4, router.Calls); // polled again at T; the stale reading did not pause anew
    }

    [Fact]
    public async Task A_worker_failing_with_the_router_exhaustion_error_pauses_the_factory_instead_of_escalating_and_resumes_its_session()
    {
        var l = new Ledgers();
        var source = new FakeWorkSource(Story);
        var worker = new FakeWorker(
            Reports(new WorkerResult("sess-77", 1, true, "success", RouterExhausted, "")),
            Reports(Ok));

        var paused = await l.Pipeline(source, worker).RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.Contains("paused for usage", paused.Error);
        var transitions = await l.Transitions();
        Assert.Equal((WorkState.Paused, RunPipeline.UsagePaused), transitions[^1]);
        Assert.DoesNotContain(transitions, t => t.Item1 == WorkState.Escalated);
        Assert.Empty(source.Comments); // no escalation comment
        var checkpoint = (await l.Rows()).Last(r => r.Step == RunPipeline.Steps.UsagePause);
        Assert.Equal($"{UsagePause.WorkerRateLimited}; resumes at {T0 + UsagePause.InitialBackoff:u}", checkpoint.Detail);
        Assert.DoesNotContain(l.Workspaces.Calls, c => c.StartsWith("remove")); // worktree kept for the resume
        var pause = await l.Controls.UsagePauseAsync(CancellationToken.None);
        Assert.Equal((UsagePause.WorkerRateLimited, T0 + UsagePause.InitialBackoff), (pause!.Reason, pause.ResumeAt));

        // Nothing resumes it before the backoff ends; then it resumes by itself, in the same Claude session.
        Assert.Empty(await l.InFlight());
        l.Time.Advance(UsagePause.InitialBackoff);
        Assert.Equal([77], await l.InFlight());
        var resumed = await l.Pipeline(source, worker).RunAsync(77, CancellationToken.None);

        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal("sess-77", worker.Calls[1].Resume);
        Assert.Equal(WorkState.Review, resumed.State);
    }

    [Fact]
    public async Task A_rate_limit_api_error_in_the_stream_marks_the_failure_as_usage_limited()
    {
        var state = new StreamJsonState();
        state.Accept("""{"type":"system","subtype":"init","session_id":"s1"}""");
        state.Accept("""{"type":"assistant","session_id":"s1","error":"rate_limit","message":{"content":[{"type":"text","text":"API Error"}]}}""");
        Assert.True(state.RateLimited);

        Assert.True(new WorkerResult("s1", 1, true, "success", "failed", "", RateLimited: state.RateLimited).UsageLimited);
        Assert.True(new WorkerResult("s1", 1, true, "success", RouterExhausted, "").UsageLimited);
        Assert.True(new WorkerResult("s1", 1, true, "success", "Claude AI usage limit reached|1760000000", "").UsageLimited);
        Assert.True(new WorkerResult("s1", 1, true, null, null, "Error: Repeated 529 Overloaded errors").UsageLimited);
        Assert.False(new WorkerResult("s1", 1, true, "error_max_turns", "ran out of turns", "").UsageLimited);
        Assert.False(new WorkerResult("s1", 0, false, "success", "Added rate_limit_error handling", "").UsageLimited); // a success never is
        // A failed exit whose result is not an error is the model's prose, about rate limits or not: it escalates.
        Assert.False(new WorkerResult("s1", 1, false, "success", "Added rate_limit_error handling", "").UsageLimited);
    }

    [Fact]
    public async Task Backoff_doubles_on_each_repeat_up_to_an_hour_and_starts_over_after_a_quiet_hour()
    {
        var l = new Ledgers();
        var controls = l.Controls;
        var expected = new[] { 1, 2, 4, 8, 16, 32, 60, 60 };
        foreach (var minutes in expected)
        {
            var pause = await controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None);
            Assert.Equal(TimeSpan.FromMinutes(minutes), pause.Backoff);
            Assert.Equal(l.Time.GetUtcNow() + TimeSpan.FromMinutes(minutes), pause.ResumeAt);
            // A second worker hitting the limit while the pause holds does not extend it.
            Assert.Equal(pause.ResumeAt, (await controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None)).ResumeAt);
            l.Time.Advance(TimeSpan.FromMinutes(minutes));
        }
        l.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(UsagePause.InitialBackoff, (await controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None)).Backoff);

        // A known reset replaces a shorter backoff, and is never shortened by a later, earlier one.
        var reset = l.Time.GetUtcNow() + TimeSpan.FromHours(3);
        Assert.Equal(reset, (await controls.PauseForUsageAsync(reset, UsagePause.UsageExhausted, CancellationToken.None)).ResumeAt);
        Assert.Equal(reset, (await controls.PauseForUsageAsync(reset - TimeSpan.FromHours(1), UsagePause.UsageExhausted, CancellationToken.None)).ResumeAt);
    }

    [Fact]
    public async Task A_users_continue_does_not_lift_the_usage_pause_and_its_resume_does_not_lift_a_users_pause()
    {
        var l = new Ledgers();
        var controls = l.Controls;
        var actions = new ControlActions(controls, l.Contexts);
        await controls.PauseForUsageAsync(T0 + TimeSpan.FromMinutes(30), UsagePause.UsageExhausted, CancellationToken.None);
        await actions.PauseAsync(ControlScope.Factory, "tester", CancellationToken.None);

        await actions.ContinueAsync(ControlScope.Factory, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Paused, await controls.EffectiveAsync("sc-1", null, CancellationToken.None)); // usage still holds

        await actions.PauseAsync(ControlScope.Factory, "tester", CancellationToken.None);
        l.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(await controls.UsagePauseAsync(CancellationToken.None));
        Assert.Equal(ControlState.Paused, await controls.EffectiveAsync("sc-1", null, CancellationToken.None)); // the user's pause holds

        await actions.ContinueAsync(ControlScope.Factory, "tester", CancellationToken.None);
        Assert.Equal(ControlState.Running, await controls.EffectiveAsync("sc-1", null, CancellationToken.None));

        // Forcing it early is explicit; a user can neither pause nor stop the usage scope.
        await controls.PauseForUsageAsync(l.Time.GetUtcNow() + TimeSpan.FromMinutes(30), UsagePause.UsageExhausted, CancellationToken.None);
        Assert.False((await actions.PauseAsync(ControlScope.Usage, "tester", CancellationToken.None)).Ok);
        Assert.False((await actions.StopAsync(ControlScope.Usage, "tester", CancellationToken.None)).Ok);
        Assert.Equal(ControlState.Paused, await controls.EffectiveAsync("sc-1", null, CancellationToken.None));
        Assert.True((await actions.ContinueAsync(ControlScope.Usage, "tester", CancellationToken.None)).Ok);
        Assert.Equal(ControlState.Running, await controls.EffectiveAsync("sc-1", null, CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown")]     // no credential known yet (e.g. after a router restart)
    [InlineData("unreachable")] // the router answers with an error
    [InlineData("headroom")]
    [InlineData("stale")]       // exhausted until a reset already past
    public async Task Usage_that_is_unknown_or_not_exhausted_never_pauses(string reading)
    {
        var l = new Ledgers();
        var router = new StubRouter
        {
            Report = reading switch
            {
                "unknown" => Usage(allExhausted: false, resumesAt: null, known: 0),
                "stale" => Usage(allExhausted: true, resumesAt: T0 - TimeSpan.FromMinutes(1)),
                _ => Usage(allExhausted: false, resumesAt: null),
            },
            Status = reading == "unreachable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
        };

        await Monitor(router, l).CheckAsync(CancellationToken.None);

        Assert.Equal(1, router.Calls);
        Assert.Null(await l.Controls.GetAsync(ControlScope.Usage, CancellationToken.None));
    }

    [Fact]
    public async Task All_exhausted_with_no_reset_time_pauses_for_a_backoff_and_re_polls()
    {
        var l = new Ledgers();
        var router = new StubRouter { Report = Usage(allExhausted: true, resumesAt: null) };
        var monitor = Monitor(router, l);

        await monitor.CheckAsync(CancellationToken.None);
        var first = await l.Controls.UsagePauseAsync(CancellationToken.None);
        Assert.Equal((UsagePause.UsageExhausted, T0 + UsagePause.InitialBackoff), (first!.Reason, first.ResumeAt));

        l.Time.Advance(UsagePause.InitialBackoff);
        await monitor.CheckAsync(CancellationToken.None);
        Assert.Equal(l.Time.GetUtcNow() + 2 * UsagePause.InitialBackoff, (await l.Controls.UsagePauseAsync(CancellationToken.None))!.ResumeAt);
    }

    [Fact]
    public async Task A_running_worker_stops_at_its_next_tool_call_when_the_usage_pause_starts_and_the_item_resumes_after_it()
    {
        var l = new Ledgers();
        var source = new FakeWorkSource(Story);
        var worker = new PausableWorker();
        var run = l.Pipeline(source, worker).RunAsync(77, CancellationToken.None);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await l.Controls.PauseForUsageAsync(T0 + TimeSpan.FromHours(2), UsagePause.UsageExhausted, CancellationToken.None);
        var paused = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.Equal((WorkState.Paused, RunPipeline.UsagePaused), (await l.Transitions())[^1]);
        Assert.Empty(await l.InFlight());
        l.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal([77], await l.InFlight());
        Assert.True((await l.Pipeline(source, worker).RunAsync(77, CancellationToken.None)).Succeeded);
        Assert.Equal([null, "sess-77"], worker.Resumes);
    }

    /// <summary>Works in tool calls until asked to pause (then ends as the pause hook makes it); a resumed session finishes.</summary>
    private sealed class PausableWorker : IWorker
    {
        private int _pause;
        public List<string?> Resumes { get; } = [];
        public TaskCompletionSource Working { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks, CancellationToken ct)
        {
            Resumes.Add(resumeSessionId);
            await callbacks!.OnStarted!(WorkerPid, ct);
            await callbacks.OnSession!("sess-77", ct);
            if (resumeSessionId is not null)
            {
                return Ok;
            }
            Working.TrySetResult();
            while (Volatile.Read(ref _pause) == 0)
            {
                await Task.Delay(5, ct);
            }
            return new WorkerResult("sess-77", 0, false, "success", "", "", WorkerResult.HookStoppedReason);
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
        public void RequestPause(string workingDirectory) => Volatile.Write(ref _pause, 1);
        public void CancelPause(string workingDirectory) => Volatile.Write(ref _pause, 0);
    }

    [Fact]
    public async Task An_items_displayed_cost_is_the_sum_of_its_sessions_router_costs_and_the_usage_pause_shows_on_it()
    {
        var l = new Ledgers();
        var ledger = l.Ledger;
        var costed = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-1", "One", Sandbox.FullName, null, CancellationToken.None);
        var uncosted = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-2", "Two", Sandbox.FullName, null, CancellationToken.None);
        l.Db.WorkerSessions.AddRange(
            new WorkerSession { WorkItemId = costed.Id, ClaudeSessionId = "a", Attempt = 1, StartedAt = T0, CostUsd = 0.25m },
            new WorkerSession { WorkItemId = costed.Id, ClaudeSessionId = "b", Attempt = 2, StartedAt = T0, CostUsd = null }, // cost not in yet
            new WorkerSession { WorkItemId = costed.Id, ClaudeSessionId = "c", Attempt = 3, StartedAt = T0, CostUsd = 1.505m },
            new WorkerSession { WorkItemId = uncosted.Id, ClaudeSessionId = "d", Attempt = 1, StartedAt = T0 });
        await l.Db.SaveChangesAsync();
        await l.Controls.PauseForUsageAsync(T0 + TimeSpan.FromMinutes(5), UsagePause.UsageExhausted, CancellationToken.None);
        var data = new DashboardData(l.Contexts, l.Time);

        var rows = await data.ActiveItemsAsync(CancellationToken.None);

        Assert.Equal(1.755m, rows.Single(r => r.ExternalId == "sc-1").CostUsd);
        Assert.Null(rows.Single(r => r.ExternalId == "sc-2").CostUsd);
        Assert.All(rows, r => Assert.Equal(ControlState.Paused, r.Control));
        l.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.All(await data.ActiveItemsAsync(CancellationToken.None), r => Assert.Equal(ControlState.Running, r.Control));
    }

    private static (FakeShortcutBoard Board, ShortcutWorkSource Source) ReadyBoard()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        return (board, new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])));
    }

    [Fact]
    public async Task Continue_on_the_usage_pause_holds_against_the_reading_that_set_it_but_not_against_a_later_reset()
    {
        var l = new Ledgers();
        var resetAt = T0 + TimeSpan.FromHours(2);
        var router = new StubRouter { Report = Usage(allExhausted: true, resumesAt: resetAt) };
        var monitor = Monitor(router, l);
        var (_, source) = ReadyBoard();
        var worker = new FakeWorker(Reports(Ok));
        var loop = new IntakeLoop(source, new Runner(l, source, worker), new IntakeOptions(Interval), l.Time, NullLogger<IntakeLoop>.Instance,
            l.Controls, monitor);
        await monitor.CheckAsync(CancellationToken.None);
        Assert.Equal(resetAt, (await l.Controls.UsagePauseAsync(CancellationToken.None))?.ResumeAt);

        Assert.True((await new ControlActions(l.Controls, l.Contexts).ContinueAsync(ControlScope.Usage, "tester", CancellationToken.None)).Ok);
        await monitor.CheckAsync(CancellationToken.None); // the router still reports the same exhaustion until T

        Assert.Null(await l.Controls.UsagePauseAsync(CancellationToken.None));
        await loop.PollOnceAsync(CancellationToken.None); // reads the same report again first
        Assert.Equal(3, router.Calls);
        Assert.Single(worker.Calls);
        Assert.Null(await l.Controls.UsagePauseAsync(CancellationToken.None));

        // A later reset is a new exhaustion: it pauses again.
        router.Report = Usage(allExhausted: true, resumesAt: resetAt + TimeSpan.FromHours(1));
        await monitor.CheckAsync(CancellationToken.None);
        Assert.Equal(resetAt + TimeSpan.FromHours(1), (await l.Controls.UsagePauseAsync(CancellationToken.None))?.ResumeAt);
    }

    [Fact]
    public async Task The_worker_backstop_still_pauses_after_a_continue_on_the_usage_pause()
    {
        var l = new Ledgers();
        var resetAt = T0 + TimeSpan.FromHours(2);
        var router = new StubRouter { Report = Usage(allExhausted: true, resumesAt: resetAt) };
        var monitor = Monitor(router, l);
        await monitor.CheckAsync(CancellationToken.None);
        await new ControlActions(l.Controls, l.Contexts).ContinueAsync(ControlScope.Usage, "tester", CancellationToken.None);

        var pause = await l.Controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None);

        Assert.Equal((ControlState.Paused, T0 + UsagePause.InitialBackoff), (pause.State, pause.ResumeAt));
        Assert.Equal(T0 + UsagePause.InitialBackoff, (await l.Controls.UsagePauseAsync(CancellationToken.None))?.ResumeAt);
        // The worker proved the plans are still out, so the override is spent: the router's reading holds the pause to T.
        await monitor.CheckAsync(CancellationToken.None);
        Assert.Equal(resetAt, (await l.Controls.UsagePauseAsync(CancellationToken.None))?.ResumeAt);
    }

    /// <summary>A router that accepts the usage request and never answers.</summary>
    private sealed class HungUsage : IUsageSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<SubscriptionUsage> GetUsageAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_hung_router_holds_up_an_intake_poll_no_longer_than_the_usage_request_timeout()
    {
        var l = new Ledgers();
        var (_, source) = ReadyBoard();
        var worker = new FakeWorker(Reports(Ok));
        var usage = new HungUsage();
        var options = new UsageOptions(Interval);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout);
        var monitor = new UsageMonitor(usage, l.Controls, options, l.Time, NullLogger<UsageMonitor>.Instance);
        var loop = new IntakeLoop(source, new Runner(l, source, worker), new IntakeOptions(Interval), l.Time, NullLogger<IntakeLoop>.Instance,
            l.Controls, monitor);

        var poll = loop.PollOnceAsync(CancellationToken.None);
        await Eventually(() => usage.Calls == 1);
        l.Time.Advance(options.RequestTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.False(poll.IsCompleted);

        l.Time.Advance(TimeSpan.FromSeconds(1));
        await poll.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(worker.Calls); // usage unknown: the poll went on and dispatched
        Assert.Null(await l.Controls.GetAsync(ControlScope.Usage, CancellationToken.None));
    }

    /// <summary>Lets another writer commit just before the first save of the context it is attached to.</summary>
    private sealed class RaceOnce(Func<Task> write) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran)
            {
                Ran = true;
                await write();
            }
            return result;
        }
    }

    [Fact]
    public async Task A_backoff_write_that_races_a_known_reset_never_shortens_it_and_a_racing_continue_still_lands()
    {
        await using var pg = await TempPostgresDatabase.CreateAsync("df_usage");
        var plain = LedgerDbContext.PostgresOptions(pg.ConnectionString);
        await using (var db = new LedgerDbContext(plain))
        {
            await db.Database.MigrateAsync();
        }
        var time = new FakeTimeProvider(T0);
        var monitorSide = new LedgerControls(new LedgerDbContextFactory(plain), time);
        LedgerControls Raced(RaceOnce race) => new(new LedgerDbContextFactory(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(pg.ConnectionString).AddInterceptors(race).Options), time);
        // An earlier backoff pause, now over: the backstop's next write updates that row.
        await monitorSide.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None);
        time.Advance(UsagePause.InitialBackoff);
        var reset = time.GetUtcNow() + TimeSpan.FromHours(3);

        // The backstop read the expired row; the monitor's pause to the reset commits before the backstop saves.
        var race = new RaceOnce(() => monitorSide.PauseForUsageAsync(reset, UsagePause.UsageExhausted, CancellationToken.None));
        var pause = await Raced(race).PauseForUsageAsync(null, UsagePause.WorkerRateLimited, CancellationToken.None);

        Assert.True(race.Ran);
        Assert.Equal((UsagePause.UsageExhausted, reset), (pause.Reason, pause.ResumeAt));
        var stored = await monitorSide.GetAsync(ControlScope.Usage, CancellationToken.None);
        Assert.Equal((ControlState.Paused, UsagePause.UsageExhausted, reset), (stored!.State, stored.Reason, stored.ResumeAt));

        // A user's Continue that races another write is not lost to it (last writer wins).
        var continueRace = new RaceOnce(() => monitorSide.PauseForUsageAsync(reset + TimeSpan.FromHours(1), UsagePause.UsageExhausted, CancellationToken.None));
        await Raced(continueRace).SetAsync(ControlScope.Usage, ControlState.Running, "tester", CancellationToken.None);
        Assert.True(continueRace.Ran);
        Assert.Equal((ControlState.Running, "tester"), ((await monitorSide.GetAsync(ControlScope.Usage, CancellationToken.None))!.State,
            (await monitorSide.GetAsync(ControlScope.Usage, CancellationToken.None))!.ChangedBy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("factory")]
    [InlineData("epic")]
    [InlineData("item")]
    public async Task A_run_stopped_while_a_users_pause_and_the_usage_pause_both_hold_is_labelled_by_the_users_pause(string? userScope)
    {
        var l = new Ledgers();
        var source = new FakeWorkSource(Story) { Epic = new WorkEpic(5, "Phase 1", null, "https://app.shortcut.com/trefry/epic/5") };
        var worker = new PausableWorker();
        var run = l.Pipeline(source, worker).RunAsync(77, CancellationToken.None);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (userScope is not null)
        {
            var scope = userScope switch { "factory" => ControlScope.Factory, "epic" => ControlScope.Epic(5), _ => ControlScope.Item("sc-77") };
            await l.Controls.SetAsync(scope, ControlState.Paused, "tester", CancellationToken.None);
        }
        await l.Controls.PauseForUsageAsync(T0 + TimeSpan.FromHours(2), UsagePause.UsageExhausted, CancellationToken.None);
        var paused = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.Equal((WorkState.Paused, userScope is null ? RunPipeline.UsagePaused : RunPipeline.UserPaused), (await l.Transitions())[^1]);
        Assert.Equal(userScope is null, (await l.Rows()).Any(r => r.Step == RunPipeline.Steps.UsagePause));
    }
}
