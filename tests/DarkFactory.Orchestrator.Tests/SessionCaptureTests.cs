using DarkFactory.Orchestrator.WorkSources;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Real <c>claude -p --output-format stream-json --verbose</c> output recorded through the router
/// (one Glob tool call in an empty directory, then <c>--resume</c> of the same session), paths scrubbed.
/// </summary>
internal static class StreamJsonFixture
{
    public const string SessionId = "12a3eaf6-53f1-4b28-b3e9-2ffa84fad4c2";

    public static string[] Fresh => Lines("stream-json-fresh.jsonl");
    public static string[] Resume => Lines("stream-json-resume.jsonl");

    private static string[] Lines(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

public class StreamJsonEventTests
{
    [Fact]
    public void Recorded_fixture_lines_classify_including_tool_calls_and_tool_results()
    {
        var events = StreamJsonFixture.Fresh.Select(StreamJsonEvent.Parse).ToList();

        Assert.Equal(11, events.Count);
        Assert.Equal(("system", "init"), (events[0].Type, events[0].Subtype));
        Assert.Contains(events, e => e is { Type: "assistant", Subtype: "tool_use" });
        Assert.Contains(events, e => e is { Type: "user", Subtype: "tool_result" });
        Assert.Contains(events, e => e is { Type: "rate_limit_event", Subtype: null });
        Assert.Equal(("result", "success"), (events[^1].Type, events[^1].Subtype));
        Assert.All(events, e => Assert.Equal(StreamJsonFixture.SessionId, e.SessionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Warning: something on stdout")]
    [InlineData("{broken")]
    [InlineData("[1,2]")]
    public void Unparseable_lines_are_raw_events(string line)
    {
        Assert.Equal(new StreamJsonEvent(StreamJsonEvent.Raw, null, null), StreamJsonEvent.Parse(line));
    }

    [Fact]
    public void Router_cost_is_awaited_for_about_a_minute_by_default()
    {
        // The router commits a session's cost asynchronously; the acceptance harness allows ~60 s.
        var total = SessionRecorder.DefaultCostRetryDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.InRange(total, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(75));
    }
}

/// <summary>
/// Session capture against the compose Postgres (localhost:5434), and the session hub on Kestrel fed
/// by the LISTEN/NOTIFY relay.
/// </summary>
public sealed class SessionCaptureTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;
    private CookieContainer _cookies = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_sessions");
        _cs = _db.ConnectionString;
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private LedgerDbContext Context() => new(LedgerDbContext.PostgresOptions(_cs));

    private LedgerDbContextFactory Contexts() => new(LedgerDbContext.PostgresOptions(_cs));

    /// <summary>A recorder of its own, as a separate <c>factory run</c> process would have.</summary>
    private SessionRecorder Recorder(ISessionCostSource costs, int capacity = SessionRecorder.DefaultCapacity, TimeSpan? settle = null) =>
        new(Contexts(), costs, TimeProvider.System, TextWriter.Null, capacity, costRetryDelays: [TimeSpan.Zero, TimeSpan.Zero],
            costSettleDelay: settle ?? TimeSpan.Zero);

    private async Task<WorkItem> ItemAsync(int story = 1)
    {
        await using var db = Context();
        return await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", $"sc-{story}", "t", "acme/widgets", null, CancellationToken.None);
    }

    private async Task<List<SessionEvent>> EventsAsync()
    {
        await using var db = Context();
        return await db.SessionEvents.OrderBy(e => e.Id).ToListAsync();
    }

    private static readonly SessionCost FixtureCost = new(StreamJsonFixture.SessionId, 3, 429606);

    [Fact]
    public async Task Every_fixture_line_is_stored_in_order_without_gaps_and_the_ended_session_holds_the_router_cost()
    {
        var item = await ItemAsync();
        // The router has no telemetry when the session ends (404), then no request yet, then the cost.
        var costs = new FakeCosts(null, FixtureCost with { RequestCount = 0, ActualCostUsdMicros = 0 }, FixtureCost);
        // A one-slot queue: the reader waits for the database instead of dropping lines.
        await using var capture = await Recorder(costs, capacity: 1).StartAsync(item.Id, null, CancellationToken.None);
        foreach (var line in StreamJsonFixture.Fresh)
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        var events = await EventsAsync();
        Assert.Equal(StreamJsonFixture.Fresh, events.Select(e => e.Payload));
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(
            ["system", "assistant", "assistant", "assistant", "rate_limit_event", "user", "system", "system", "assistant", "assistant", "result"],
            events.Select(e => e.Type));
        Assert.Equal("tool_use", events[3].Subtype);
        Assert.Equal("tool_result", events[5].Subtype);
        Assert.All(events, e => Assert.Equal(item.Id, e.WorkItemId));

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(StreamJsonFixture.SessionId, session.ClaudeSessionId);
        Assert.Equal((item.Id, 1, 0, "succeeded"), (session.WorkItemId, session.Attempt, session.ExitCode, session.ExitStatus));
        Assert.NotNull(session.EndedAt);
        Assert.Equal(0.429606m, session.CostUsd);
        Assert.Equal(3, session.RouterRequestCount);
        Assert.Equal(4, costs.Calls); // three tries until recorded, then the settle re-read
    }

    [Fact]
    public async Task A_cost_with_no_recorded_request_is_left_unset_after_the_retries()
    {
        var item = await ItemAsync();
        // A 200 with request_count 0 is not recorded spend, whatever the cost says.
        var costs = new FakeCosts(FixtureCost with { RequestCount = 0, ActualCostUsdMicros = 0 });
        await using var capture = await Recorder(costs).StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(("succeeded", (decimal?)null), (session.ExitStatus, session.CostUsd));
        Assert.Equal(3, costs.Calls); // the first try and two retries
    }

    [Fact]
    public async Task A_session_served_at_zero_cost_is_stored_as_zero_and_not_retried_past_the_settle_re_read()
    {
        // Turns served on the router's local model are priced at $0: a recorded actual cost of 0 is a real cost.
        var item = await ItemAsync();
        var zero = FixtureCost with { RequestCount = 2, ActualCostUsdMicros = 0 };
        var costs = new FakeCosts(null, zero, zero, FixtureCost);
        var recorder = new SessionRecorder(Contexts(), costs, TimeProvider.System, TextWriter.Null,
            costRetryDelays: [.. Enumerable.Repeat(TimeSpan.Zero, 5)], costSettleDelay: TimeSpan.Zero);
        await using var capture = await recorder.StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal((0m, 2L), (session.CostUsd, session.RouterRequestCount));
        Assert.Equal(3, costs.Calls); // the 404, the recorded zero, the settle re-read
    }

    [Fact]
    public async Task Requests_recorded_after_the_first_answer_are_kept_by_the_settle_re_read()
    {
        var item = await ItemAsync();
        var settle = TimeSpan.FromMilliseconds(300);
        var costs = new FakeCosts(FixtureCost with { RequestCount = 1, ActualCostUsdMicros = 1000 }, FixtureCost);
        await using var capture = await Recorder(costs, settle: settle).StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal((0.429606m, 3L), (session.CostUsd, session.RouterRequestCount));
        Assert.Equal(2, costs.Calls);
        Assert.True(costs.CalledAt[1] - costs.CalledAt[0] >= settle - TimeSpan.FromMilliseconds(20),
            $"the re-read came {costs.CalledAt[1] - costs.CalledAt[0]} after the first answer");
    }

    [Fact]
    public async Task A_settle_re_read_with_nothing_recorded_keeps_the_first_answer()
    {
        var item = await ItemAsync();
        var costs = new FakeCosts(FixtureCost with { RequestCount = 1, ActualCostUsdMicros = 1000 }, null);
        await using var capture = await Recorder(costs).StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal((0.001m, 1L), (session.CostUsd, session.RouterRequestCount));
    }

    [Fact]
    public async Task A_caller_giving_up_during_the_settle_wait_still_keeps_the_recorded_cost()
    {
        // The failed/stopped end paths give the session end a time limit; it can run out during the settle wait.
        var item = await ItemAsync();
        // A slow session end (here the router's answer; on a loaded machine the database writes too): slower than the 500 ms
        // this test once waited before giving up, which then landed before the cost was stored.
        var costs = new FakeCosts(FixtureCost) { Delay = TimeSpan.FromMilliseconds(750) };
        var settle = TimeSpan.FromMinutes(5);
        var time = new SettleSignal(settle);
        var recorder = new SessionRecorder(Contexts(), costs, time, TextWriter.Null, costRetryDelays: [TimeSpan.Zero, TimeSpan.Zero], costSettleDelay: settle);
        await using var capture = await recorder.StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var completing = capture.CompleteAsync(0, "error", fetchCost: true, cts.Token);
        // The caller gives up once the settle wait has begun — not after a guessed delay: on a loaded machine the session end's
        // database writes and cost read can take longer than any fixed delay, and a caller that gives up before the cost is
        // stored is a different case (the cost is then not kept, by design: the end paths are bounded).
        await time.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing.WaitAsync(TimeSpan.FromSeconds(60)));

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(("error", 0.429606m, 3L), (session.ExitStatus, session.CostUsd, session.RouterRequestCount));
        Assert.Equal(1, costs.Calls); // no settle re-read after the caller gave up
    }

    [Fact]
    public async Task Resumed_session_continues_its_row_and_sequence_and_keeps_unparseable_lines()
    {
        var item = await ItemAsync();
        var recorder = Recorder(new FakeCosts(FixtureCost));
        long firstRow;
        await using (var first = await recorder.StartAsync(item.Id, null, CancellationToken.None))
        {
            firstRow = first.WorkerSessionId;
            foreach (var line in StreamJsonFixture.Fresh)
            {
                await first.OnLineAsync(line, CancellationToken.None);
            }
            await first.CompleteAsync(null, "cancelled", fetchCost: false, CancellationToken.None);
        }

        await using (var resumed = await recorder.StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None))
        {
            Assert.Equal(firstRow, resumed.WorkerSessionId);
            await resumed.OnLineAsync("Warning: not json", CancellationToken.None);
            foreach (var line in StreamJsonFixture.Resume)
            {
                await resumed.OnLineAsync(line, CancellationToken.None);
            }
            await resumed.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);
        }

        var events = await EventsAsync();
        Assert.Equal([.. StreamJsonFixture.Fresh, "Warning: not json", .. StreamJsonFixture.Resume], events.Select(e => e.Payload));
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(StreamJsonEvent.Raw, events[11].Type);
        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(("succeeded", 0.429606m), (session.ExitStatus, session.CostUsd));
    }

    /// <summary>
    /// The crash window: the ledger already holds the session id (so the next run resumes it), but the
    /// orchestrator died before the session row was named. The resume continues that row and its sequence.
    /// </summary>
    [Fact]
    public async Task Resuming_a_session_whose_row_was_never_named_continues_that_row()
    {
        var item = await ItemAsync();
        var recorder = Recorder(new FakeCosts(FixtureCost));
        long crashedRow;
        await using (var crashed = await recorder.StartAsync(item.Id, null, CancellationToken.None))
        {
            crashedRow = crashed.WorkerSessionId;
            await crashed.OnLineAsync("starting up", CancellationToken.None);
            await crashed.OnLineAsync("still no session id", CancellationToken.None);
        } // killed: no CompleteAsync
        await using (var db = Context())
        {
            Assert.Null((await db.WorkerSessions.SingleAsync()).ClaudeSessionId);
        }

        await using (var resumed = await recorder.StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None))
        {
            Assert.Equal(crashedRow, resumed.WorkerSessionId);
            await resumed.OnLineAsync(StreamJsonFixture.Resume[0], CancellationToken.None);
            await resumed.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);
        }

        Assert.Equal([1L, 2L, 3L], (await EventsAsync()).Select(e => e.Sequence));
        await using (var db = Context())
        {
            var session = await db.WorkerSessions.SingleAsync();
            Assert.Equal((StreamJsonFixture.SessionId, 1), (session.ClaudeSessionId, session.Attempt));
        }
    }

    [Fact]
    public async Task The_session_row_is_named_before_the_ledger_checkpoints_the_session()
    {
        // The worker reports the session id before any line carries it, so only the pipeline's
        // OnSession can have named the row by the time the checkpoint is written.
        string? rowAtCheckpoint = "unset";
        var worker = new CallbackWorker(async (callbacks, ct) =>
        {
            await callbacks.OnSession!("sess-named", ct);
            await using var db = Context();
            rowAtCheckpoint = (await db.WorkerSessions.SingleAsync(ct)).ClaudeSessionId;
            await callbacks.OnLine!("""{"type":"result","subtype":"success","is_error":false,"session_id":"sess-named"}""", ct);
            return new WorkerResult("sess-named", 0, false, "success", "done", "");
        });
        await using var ledgerDb = Context();
        var outcome = await new RunPipeline(Stories(), new WorkLedger(ledgerDb, TimeProvider.System), new PostgresRunLocks(_cs),
            new Workspaces(), worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null, Recorder(new FakeCosts(FixtureCost)))
            .RunAsync(8, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("sess-named", rowAtCheckpoint);
    }

    /// <summary>
    /// E4 / AC2: Ctrl-C lands after the worker's WebFetch line was read and stored but while its taint write is in flight (the
    /// cancelled token aborts it; the same holds when the callback is never reached). The re-run replays the stored stream, taints
    /// the session and refuses to resume it: no further worker call, no push.
    /// </summary>
    [Fact]
    public async Task A_web_fetch_whose_taint_was_cut_off_by_ctrl_c_is_found_in_the_stored_stream_and_the_resume_is_refused()
    {
        const string sid = "sess-web";
        using var ctrlC = new CancellationTokenSource();
        var calls = 0;
        var worker = new CallbackWorker(async (callbacks, ct) =>
        {
            calls++;
            await callbacks.OnSession!(sid, ct);
            await callbacks.OnLine!($$"""{"type":"system","subtype":"init","session_id":"{{sid}}"}""", ct);
            await callbacks.OnLine!(
                $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"WebFetch","input":{"url":"https://example.com"}}]},"session_id":"{{{sid}}}"}""",
                ct);
            ctrlC.Cancel();
            await callbacks.OnUntrusted!(sid, "web:WebFetch", ctrlC.Token);
            throw new InvalidOperationException("the taint write was expected to be cut off");
        });
        var workspaces = new Workspaces();
        var recorder = Recorder(new FakeCosts(FixtureCost));
        await using (var ledgerDb = Context())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RunPipeline(Stories(), new WorkLedger(ledgerDb, TimeProvider.System),
                new PostgresRunLocks(_cs), workspaces, worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null, recorder)
                .RunAsync(1, ctrlC.Token));
        }
        await using (var db = Context())
        {
            Assert.Empty(await db.SessionTaints.ToListAsync()); // the window: the use is only in the stored stream
            Assert.Contains(await db.SessionEvents.Select(e => e.Payload).ToListAsync(), p => p.Contains("\"WebFetch\""));
        }

        var stories = Stories();
        await using var rerunDb = Context();
        var outcome = await new RunPipeline(stories, new WorkLedger(rerunDb, TimeProvider.System), new PostgresRunLocks(_cs), workspaces, worker,
            new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null, recorder).RunAsync(1, CancellationToken.None);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(1, calls);
        Assert.Equal(0, workspaces.Pushes);
        Assert.Contains($"{sid} is tainted (web:WebFetch)", Assert.Single(stories.Comments));
        await using var check = Context();
        var taint = await check.SessionTaints.SingleAsync();
        Assert.Equal((sid, "web:WebFetch"), (taint.ClaudeSessionId, taint.Reason));
    }

    [Fact]
    public async Task A_failed_session_records_its_end_without_waiting_on_a_hung_router()
    {
        var worker = new CallbackWorker(async (callbacks, ct) =>
        {
            await callbacks.OnSession!("sess-failed", ct);
            await callbacks.OnLine!("""{"type":"system","subtype":"init","session_id":"sess-failed"}""", ct);
            throw new InvalidOperationException("worker blew up");
        });
        var router = new HungCosts();
        var recorder = new SessionRecorder(Contexts(), router, TimeProvider.System, TextWriter.Null);
        await using var ledgerDb = Context();
        var pipeline = new RunPipeline(Stories(), new WorkLedger(ledgerDb, TimeProvider.System), new PostgresRunLocks(_cs),
            new Workspaces(), worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null, recorder,
            failedSessionEndTimeout: TimeSpan.FromMilliseconds(300));

        var outcome = await pipeline.RunAsync(9, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.True(router.Calls > 0);
        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(("error", (decimal?)null), (session.ExitStatus, session.CostUsd));
    }

    /// <summary>
    /// A viewer that joins a running session gets its stored backlog, then every later event live, once each and in order.
    /// "Live" is proved by order, not by a wall-clock bet (an absolute 1 s bound failed under full-suite load): after the
    /// viewer joined, the worker writes its next line only once the viewer has received the one before, so an event held
    /// back until the session ends, or never relayed, stalls the session and fails the test. The wait per event is a
    /// generous liveness bound (<see cref="LiveDeliveryTimeout"/>), and the failure says how long the viewer was waited for.
    /// </summary>
    [Fact]
    public async Task Joining_a_running_session_gets_its_backlog_then_each_live_event_before_the_next_is_written_once_each()
    {
        await using var host = await StartHostAsync();
        var app = host.App;

        var clock = Stopwatch.StartNew();
        Viewer? joined = null;
        string? notLive = null;
        var worker = new FixtureWorker(clock, firstBatch: 5)
        {
            // After the worker wrote line i (0-based): the viewer must have it before line i + 1 is written.
            Written = async (i, ct) =>
            {
                var waited = Stopwatch.StartNew();
                while (joined!.Received.Count < i + 1)
                {
                    if (waited.Elapsed > LiveDeliveryTimeout)
                    {
                        notLive = $"event {i + 1} was not delivered within {waited.Elapsed} of being written, while the session was " +
                            $"still running (the viewer had {joined.Received.Count} events)";
                        throw new TimeoutException(notLive);
                    }
                    await Task.Delay(10, ct);
                }
            },
        };
        await using var ledgerDb = Context();
        var pipeline = new RunPipeline(Stories(), new WorkLedger(ledgerDb, TimeProvider.System), new PostgresRunLocks(_cs),
            new Workspaces(), worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null,
            app.Services.GetRequiredService<SessionRecorder>());
        var run = Task.Run(() => pipeline.RunAsync(7, CancellationToken.None));

        // The session is running and its first five events are stored.
        await worker.FirstBatchSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () => (await EventsAsync()).Count == 5);

        await using var viewer = await Viewer.JoinAsync(app, _cookies, StreamJsonFixture.SessionId, clock);
        joined = viewer;

        worker.Release.SetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Null(notLive);
        Assert.True(outcome.Succeeded, outcome.Error);
        await Task.Delay(200); // nothing may arrive twice

        var got = viewer.Received.ToList();
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), got.Select(r => r.Event.Sequence));
        Assert.Equal(StreamJsonFixture.Fresh, got.Select(r => r.Event.Payload));
        // Each live event reached the viewer before the worker wrote the next line.
        foreach (var (e, at) in got.Skip(5).SkipLast(1))
        {
            var next = worker.EmittedAt[(int)e.Sequence];
            Assert.True(at < next, $"event {e.Sequence} arrived at {at}, after the worker wrote event {e.Sequence + 1} at {next}");
        }

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal((StreamJsonFixture.SessionId, "succeeded", 0.429606m), (session.ClaudeSessionId, session.ExitStatus, session.CostUsd));
    }

    /// <summary>
    /// How long one live event may take from the worker's line to the viewer: a liveness bound, not a latency target. The
    /// path (recorder → insert → NOTIFY → relay → broadcaster → SignalR) takes milliseconds when idle and has taken over a
    /// second under full-suite load; an event that is not relayed live never arrives while the session waits, whatever the bound.
    /// </summary>
    private static readonly TimeSpan LiveDeliveryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The backlog→live handoff under load: a recorder outside the host (as a separate
    /// <c>factory run</c>) writes without pause while viewers join mid-stream; the host relays via
    /// NOTIFY. Each viewer gets exactly 1..N. Small pages make backlog and live sends interleave.
    /// The writer never pauses, and it stops only a fixed tail after the last viewer has joined, so
    /// every join lands mid-stream however long it takes (a cold first join can take seconds).
    /// </summary>
    [Fact]
    public async Task Viewers_joining_mid_stream_get_every_event_exactly_once_via_the_notify_relay()
    {
        const int gap = 100;  // events stored between one join and the next
        const int tail = 100; // events written after the last viewer joined
        const string sid = "sess-race";
        await using var host = await StartHostAsync(new SessionHubOptions { PageSize = 7 });
        var app = host.App;
        var item = await ItemAsync();
        await using var capture = await Recorder(new FakeCosts(FixtureCost)).StartAsync(item.Id, null, CancellationToken.None);
        await capture.SetClaudeSessionIdAsync(sid, CancellationToken.None);

        var lastJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            var stopAt = int.MaxValue;
            var i = 0;
            while (i < stopAt)
            {
                i++;
                await capture.OnLineAsync($$"""{"type":"assistant","session_id":"{{sid}}","n":{{i}}}""", CancellationToken.None);
                if (stopAt == int.MaxValue && lastJoined.Task.IsCompleted)
                {
                    stopAt = i + tail;
                }
                Assert.True(i < 100_000, "the last viewer never joined");
                await Task.Delay(Random.Shared.Next(1, 6));
            }
            await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);
            return i;
        });

        async Task<int> StoredAsync() => (await EventsAsync()).Count;
        await WaitUntilAsync(async () => await StoredAsync() >= 1);
        await using var early = await Viewer.JoinAsync(app, _cookies, sid);
        var mark = await StoredAsync();
        await WaitUntilAsync(async () => await StoredAsync() >= mark + gap);
        await using var middle = await Viewer.JoinAsync(app, _cookies, sid);
        mark = await StoredAsync();
        await WaitUntilAsync(async () => await StoredAsync() >= mark + gap);
        await using var late = await Viewer.JoinAsync(app, _cookies, sid);
        Assert.False(writer.IsCompleted, "the writer finished before the last viewer joined: nothing was mid-stream");
        lastJoined.SetResult();
        var total = await writer.WaitAsync(TimeSpan.FromSeconds(30));

        await WaitForEveryEventAsync([early, middle, late], total);
        await Task.Delay(300); // nothing may arrive twice
        foreach (var viewer in new[] { early, middle, late })
        {
            Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), viewer.Received.Select(r => r.Event.Sequence));
        }
    }

    [Fact]
    public async Task Joining_twice_on_one_connection_sends_the_backlog_once()
    {
        await using var host = await StartHostAsync();
        var app = host.App;
        var item = await ItemAsync();
        await using var capture = await Recorder(new FakeCosts(FixtureCost)).StartAsync(item.Id, null, CancellationToken.None);
        foreach (var line in StreamJsonFixture.Fresh.Take(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await WaitUntilAsync(async () => (await EventsAsync()).Count == 4);

        await using var viewer = await Viewer.JoinAsync(app, _cookies, StreamJsonFixture.SessionId);
        await viewer.Connection.InvokeAsync("JoinSession", StreamJsonFixture.SessionId);
        foreach (var line in StreamJsonFixture.Fresh.Skip(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count >= 11));
        await Task.Delay(300);
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), viewer.Received.Select(r => r.Event.Sequence));

        // A session's hub state lives only while it has viewers.
        var broadcaster = app.Services.GetRequiredService<SessionBroadcaster>();
        Assert.Equal(1, broadcaster.FeedCount);
        await viewer.Connection.DisposeAsync();
        await WaitUntilAsync(() => Task.FromResult(broadcaster.FeedCount == 0));
    }

    [Fact]
    public async Task A_dropped_listen_connection_is_reopened_and_viewers_catch_up_from_the_ledger()
    {
        await using var host = await StartHostAsync();
        var app = host.App;
        var item = await ItemAsync();
        await using var capture = await Recorder(new FakeCosts(FixtureCost)).StartAsync(item.Id, null, CancellationToken.None);
        await capture.SetClaudeSessionIdAsync("sess-relay", CancellationToken.None);
        async Task WriteAsync(int from, int to)
        {
            for (var i = from; i <= to; i++)
            {
                await capture.OnLineAsync($$"""{"type":"assistant","n":{{i}}}""", CancellationToken.None);
            }
            await WaitUntilAsync(async () => (await EventsAsync()).Count == to);
        }

        await WriteAsync(1, 10);
        await using var viewer = await Viewer.JoinAsync(app, _cookies, "sess-relay");
        await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count == 10));

        // Kill the relay's LISTEN backend; these commits notify nobody (the relay waits 1 s to reconnect).
        await using (var admin = new NpgsqlConnection(_cs))
        {
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand(
                $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = '{SessionEventRelay.ApplicationName}' AND datname = current_database()",
                admin);
            Assert.Equal(1L, await kill.ExecuteScalarAsync());
        }
        await WriteAsync(11, 20);
        await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count == 20));

        // And live notifications flow again.
        await WriteAsync(21, 25);
        await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count == 25));
        await Task.Delay(200);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), viewer.Received.Select(r => r.Event.Sequence));
    }

    [Theory]
    [InlineData("127.0.0.1", null, true, HttpStatusCode.OK)]            // non-browser client: no Origin
    [InlineData("localhost", null, true, HttpStatusCode.OK)]
    [InlineData("127.0.0.1", "same", true, HttpStatusCode.OK)]          // a page served by the host itself
    [InlineData("127.0.0.1", null, false, HttpStatusCode.Unauthorized)] // not logged in
    [InlineData("127.0.0.1", "http://evil.example", true, HttpStatusCode.Forbidden)]
    [InlineData("127.0.0.1", "null", true, HttpStatusCode.Forbidden)]
    [InlineData("rebound.evil.example", null, true, HttpStatusCode.BadRequest)] // DNS rebinding: wrong Host
    public async Task Hub_accepts_only_logged_in_loopback_hosts_and_same_origin_browsers(string host, string? origin, bool loggedIn, HttpStatusCode expected)
    {
        await using var running = await StartHostAsync();
        var app = running.App;
        var address = new Uri(app.Address());
        using var http = DashboardLogin.Client(app.Address(), loggedIn ? await DashboardLogin.LoginAsync(app.Address()) : null);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, SessionHub.Path + "/negotiate?negotiateVersion=1"));
        request.Headers.Host = $"{host}:{address.Port}";
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin == "same" ? $"http://{host}:{address.Port}" : origin);
        }

        using var response = await http.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>Stops the host (closing hub connections while its services live) before disposing it.</summary>
    private sealed class RunningHost(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private async Task<RunningHost> StartHostAsync(SessionHubOptions? hubOptions = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = _cs,
            ["Factory:HostPort"] = "0",
            ["Router:CostSettleSeconds"] = "0",
        }).Build();
        var app = FactoryHost.Build(new FactoryOptions(config, DashboardLogin.Secrets()), services =>
        {
            services.AddSingleton<ISessionCostSource>(new FakeCosts(FixtureCost));
            if (hubOptions is not null)
            {
                services.AddSingleton(hubOptions);
            }
        });
        await app.StartAsync();
        // Logged in once up front: a login (PBKDF2) per viewer would slow joins that race a running writer.
        _cookies = await DashboardLogin.LoginAsync(app.Address());
        return new RunningHost(app);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits until every viewer has <paramref name="total"/> events. Fails at once when the hub disconnected one (it would never
    /// get the rest), and when no viewer has received an event for 15 s (a notification that was missed: nothing more would come);
    /// not on a short fixed deadline, since how far behind the viewers are when the writer stops depends on how loaded the machine
    /// is. Bounded overall all the same (<paramref name="overall"/>, 120 s by default: a viewer that keeps receiving something but
    /// never all would otherwise wait for ever), and failing at once when a viewer has received more than <paramref name="total"/>
    /// (an event delivered twice: the wait would end with the duplicate counted as one of the expected).
    /// </summary>
    private static async Task WaitForEveryEventAsync(IReadOnlyList<Viewer> viewers, int total, TimeSpan? overall = null)
    {
        string Counts() => string.Join(", ", viewers.Select(v => v.Received.Count)) + $" of {total}";
        var seen = Counts();
        var progressed = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + (overall ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            for (var i = 0; i < viewers.Count; i++)
            {
                Assert.True(viewers[i].Received.Count <= total, $"viewer {i} received more than {total} events: {Counts()}");
            }
            if (viewers.All(v => v.Received.Count == total))
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, $"not every viewer received every event within {(overall ?? TimeSpan.FromSeconds(120)).TotalSeconds:0} s: {Counts()} events");
            for (var i = 0; i < viewers.Count; i++)
            {
                Assert.True(viewers[i].Disconnected is null, $"viewer {i} was disconnected by the hub ({viewers[i].Disconnected}): {Counts()} events");
            }
            if (Counts() is var now && now != seen)
            {
                (seen, progressed) = (now, DateTime.UtcNow);
            }
            Assert.True(DateTime.UtcNow - progressed < TimeSpan.FromSeconds(15), $"no viewer received an event for 15 s: {seen} events");
            await Task.Delay(20);
        }
    }

    /// <summary>A viewer holding <paramref name="count"/> events and no connection: what the wait reads, alone.</summary>
    private static Viewer Holding(int count)
    {
        var viewer = new Viewer { Connection = null! };
        for (var i = 0; i < count; i++)
        {
            viewer.Received.Enqueue((null!, TimeSpan.Zero));
        }
        return viewer;
    }

    [Fact]
    public async Task The_wait_for_every_event_fails_at_once_on_a_viewer_with_more_events_than_were_written()
    {
        var clock = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => WaitForEveryEventAsync([Holding(3), Holding(4)], 3));

        Assert.Contains("viewer 1 received more than 3 events: 3, 4 of 3", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task The_wait_for_every_event_is_bounded_overall()
    {
        // Progress keeps coming (the 15 s no-progress check never trips), but never every event: the overall deadline ends it.
        var slow = Holding(0);
        using var stop = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && slow.Received.Count < 1000)
            {
                slow.Received.Enqueue((null!, TimeSpan.Zero));
                await Task.Delay(50);
            }
        });

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => WaitForEveryEventAsync([slow], 1000, TimeSpan.FromSeconds(1)));
        await stop.CancelAsync();
        await feeder;

        Assert.Contains("not every viewer received every event within 1 s", failure.Message);
    }

    private sealed class Viewer : IAsyncDisposable
    {
        public required HubConnection Connection { get; init; }
        public ConcurrentQueue<(SessionEventMessage Event, TimeSpan At)> Received { get; } = new();

        /// <summary>Why the connection closed while the test was still watching (null while open, or once the test closed it).</summary>
        public string? Disconnected { get; private set; }

        private bool _disposing;

        public static async Task<Viewer> JoinAsync(WebApplication app, CookieContainer cookies, string sessionId, Stopwatch? clock = null)
        {
            clock ??= Stopwatch.StartNew();
            var viewer = new Viewer { Connection = new HubConnectionBuilder().WithUrl(app.Address() + SessionHub.Path, o => o.Cookies = cookies).Build() };
            viewer.Connection.On<string, SessionEventMessage[]>(SessionHub.EventsMethod, (id, events) =>
            {
                Assert.Equal(sessionId, id);
                foreach (var e in events)
                {
                    viewer.Received.Enqueue((e, clock.Elapsed));
                }
            });
            viewer.Connection.Closed += ex =>
            {
                if (!viewer._disposing)
                {
                    viewer.Disconnected = ex?.Message ?? "closed by the server";
                }
                return Task.CompletedTask;
            };
            await viewer.Connection.StartAsync();
            await viewer.Connection.InvokeAsync("JoinSession", sessionId);
            return viewer;
        }

        public ValueTask DisposeAsync()
        {
            _disposing = true;
            return Connection.DisposeAsync();
        }
    }

    private sealed class FakeCosts(params SessionCost?[] answers) : ISessionCostSource
    {
        public int Calls;
        public readonly System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset> CallTimes = new();

        public DateTimeOffset[] CalledAt => [.. CallTimes];

        /// <summary>How long each answer takes.</summary>
        public TimeSpan Delay { get; init; }

        public async Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct)
        {
            CallTimes.Enqueue(DateTimeOffset.UtcNow);
            var answer = answers[Math.Min(Interlocked.Increment(ref Calls), answers.Length) - 1];
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }
            return answer;
        }
    }

    /// <summary>The system clock, saying when a wait of <paramref name="settle"/> starts on it: the recorder's settle wait has begun.</summary>
    private sealed class SettleSignal(TimeSpan settle) : TimeProvider
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == settle)
            {
                Waiting.TrySetResult();
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>A router that never answers until the caller gives up.</summary>
    private sealed class HungCosts : ISessionCostSource
    {
        public int Calls;

        public async Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private sealed class CallbackWorker(Func<WorkerCallbacks, CancellationToken, Task<WorkerResult>> run) : IWorker
    {
        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, string modelClass,
            WorkerCallbacks? callbacks, CancellationToken ct)
        {
            await callbacks!.OnStarted!(1, ct);
            return await run(callbacks, ct);
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>
    /// Writes the recorded fixture: a first batch, then (once released) the rest, 50 ms apart — each only once
    /// <see cref="Written"/>, when set, has returned for the line before.
    /// </summary>
    private sealed class FixtureWorker(Stopwatch clock, int firstBatch) : IWorker
    {
        /// <summary>Called with a line's index after each line of the released part is written.</summary>
        public Func<int, CancellationToken, Task>? Written { get; init; }

        public TaskCompletionSource FirstBatchSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan[] EmittedAt { get; } = new TimeSpan[StreamJsonFixture.Fresh.Length];

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, string modelClass,
            WorkerCallbacks? callbacks, CancellationToken ct)
        {
            await callbacks!.OnStarted!(1, ct);
            var lines = StreamJsonFixture.Fresh;
            for (var i = 0; i < lines.Length; i++)
            {
                if (i == firstBatch)
                {
                    FirstBatchSent.SetResult();
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                else if (i > firstBatch)
                {
                    await Task.Delay(50, ct);
                }
                EmittedAt[i] = clock.Elapsed;
                await callbacks.OnLine!(lines[i], ct);
                if (i == 0)
                {
                    await callbacks.OnSession!(StreamJsonFixture.SessionId, ct);
                }
                if (i >= firstBatch && Written is not null)
                {
                    await Written(i, ct);
                }
            }
            return new WorkerResult(StreamJsonFixture.SessionId, 0, false, "success", "done", "");
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    private static FakeWorkSource Stories() =>
        new(new WorkStory(1, "List files", "List the files.", "feature", "https://app.shortcut.com/t/story/1", ItemNaming.Shortcut));

    private sealed class Workspaces : IRepoWorkspace
    {
        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}");
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult(Ws(branch));
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult(Ws(branch));
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult<Workspace?>(Ws(branch));
        public Task<BranchStart> BranchStartAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CreateBranchAsync(RepoRef repo, string branch, string sha, CancellationToken ct) => throw new NotSupportedException();
        public int Pushes { get; private set; }
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct)
        {
            Pushes++;
            return Task.FromResult(true);
        }
        public Task<string> HeadAsync(Workspace workspace, CancellationToken ct) => Task.FromResult("head");
        public Task<Gate.BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PullRequests : IPullRequests
    {
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct) =>
            Task.FromResult("https://github.com/acme/widgets/pull/1");

        public Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>
/// One slow viewer must not hold up a session's other viewers: sends are bounded and the laggard
/// is disconnected. Fake hub clients, real ledger.
/// </summary>
public sealed class SessionBroadcasterTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_broadcast");
        _cs = _db.ConnectionString;
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_cs));
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private static readonly SessionHubOptions Fast = new()
    {
        LiveSendTimeout = TimeSpan.FromMilliseconds(200),
        BacklogSendTimeout = TimeSpan.FromMilliseconds(200),
    };

    private async Task<(SessionRecorder.SessionCapture Capture, long Row)> SessionAsync(string sid, int lines)
    {
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs));
        await using var db = contexts.CreateDbContext();
        var item = await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-5", "t", "acme/widgets", null, CancellationToken.None);
        var capture = await new SessionRecorder(contexts, new NoCosts(), TimeProvider.System, TextWriter.Null).StartAsync(item.Id, sid, CancellationToken.None);
        await WriteAsync(capture, 1, lines);
        return (capture, capture.WorkerSessionId);
    }

    private async Task WriteAsync(SessionRecorder.SessionCapture capture, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            await capture.OnLineAsync($$"""{"type":"assistant","n":{{i}}}""", CancellationToken.None);
        }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_cs));
            if (await db.SessionEvents.CountAsync() == to)
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "events not stored");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_viewer_that_stops_reading_is_dropped_and_the_others_keep_receiving()
    {
        var (capture, row) = await SessionAsync("sess-slow", 3);
        await using var _ = capture;
        var hub = new FakeHub();
        var broadcaster = new SessionBroadcaster(hub, new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), Fast, TextWriter.Null);
        var slowAborted = false;
        await broadcaster.JoinAsync("fast", "sess-slow", () => { }, CancellationToken.None);
        await broadcaster.JoinAsync("slow", "sess-slow", () => slowAborted = true, CancellationToken.None);
        hub.Hang("slow"); // takes its backlog, then stops reading

        await WriteAsync(capture, 4, 5);
        var clock = Stopwatch.StartNew();
        await broadcaster.NotifyAsync(row, 5).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"a slow viewer held the relay for {clock.Elapsed}");
        Assert.True(slowAborted);

        await WriteAsync(capture, 6, 6);
        await broadcaster.NotifyAsync(row, 6).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], hub.Received("fast"));
        Assert.Equal([1L, 2L, 3L], hub.Received("slow"));
    }

    [Fact]
    public async Task A_joiner_that_does_not_take_its_backlog_is_dropped_without_holding_the_session()
    {
        var (capture, row) = await SessionAsync("sess-stuck", 3);
        await using var _ = capture;
        var hub = new FakeHub();
        hub.Hang("stuck");
        var broadcaster = new SessionBroadcaster(hub, new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), Fast, TextWriter.Null);
        var aborted = false;

        await Assert.ThrowsAsync<HubException>(() =>
            broadcaster.JoinAsync("stuck", "sess-stuck", () => aborted = true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(aborted);
        Assert.Equal(0, broadcaster.FeedCount);
        await broadcaster.JoinAsync("ok", "sess-stuck", () => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1L, 2L, 3L], hub.Received("ok"));
    }

    private sealed class NoCosts : ISessionCostSource
    {
        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionCost?>(null);
    }

    /// <summary>Records what each connection is sent; a hung connection never completes a send.</summary>
    private sealed class FakeHub : IHubContext<SessionHub>
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _received = new();
        private readonly ConcurrentDictionary<string, bool> _hung = new();

        public FakeHub() => Clients = new FakeClients(this);

        public void Hang(string connectionId) => _hung[connectionId] = true;

        public long[] Received(string connectionId) => [.. _received.GetOrAdd(connectionId, _ => new())];

        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class FakeClients(FakeHub hub) : IHubClients
        {
            public ISingleClientProxy Client(string connectionId) => new Proxy(hub, connectionId);
            IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Client(connectionId);
            public IClientProxy All => throw new NotSupportedException();
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy Group(string groupName) => throw new NotSupportedException();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class Proxy(FakeHub hub, string connectionId) : ISingleClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                if (hub._hung.ContainsKey(connectionId))
                {
                    return new TaskCompletionSource().Task; // ignores cancellation, like a stuck socket write
                }
                var queue = hub._received.GetOrAdd(connectionId, _ => new());
                foreach (var e in (IEnumerable<SessionEventMessage>)args[1]!)
                {
                    queue.Enqueue(e.Sequence);
                }
                return Task.CompletedTask;
            }

            public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
