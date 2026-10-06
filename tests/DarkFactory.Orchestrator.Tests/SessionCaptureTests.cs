using DarkFactory.Orchestrator.WorkSources;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
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
    private SessionRecorder Recorder(ISessionCostSource costs, int capacity = SessionRecorder.DefaultCapacity) =>
        new(Contexts(), costs, TimeProvider.System, TextWriter.Null, capacity, costRetryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

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
        // The router has no cost when the session ends, then a partly committed zero, then the cost.
        var costs = new FakeCosts(null, FixtureCost with { ActualCostUsdMicros = 0 }, FixtureCost);
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
        Assert.Equal(3, costs.Calls);
    }

    [Fact]
    public async Task A_cost_that_never_commits_is_left_unset_after_the_retries()
    {
        var item = await ItemAsync();
        var costs = new FakeCosts(FixtureCost with { ActualCostUsdMicros = 0 });
        await using var capture = await Recorder(costs).StartAsync(item.Id, StreamJsonFixture.SessionId, CancellationToken.None);
        await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal(("succeeded", (decimal?)null), (session.ExitStatus, session.CostUsd));
        Assert.Equal(3, costs.Calls); // the first try and two retries
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

    [Fact]
    public async Task Joining_a_running_session_gets_its_backlog_then_live_events_once_each_within_a_second()
    {
        await using var host = await StartHostAsync();
        var app = host.App;

        var clock = Stopwatch.StartNew();
        var worker = new FixtureWorker(clock, firstBatch: 5);
        await using var ledgerDb = Context();
        var pipeline = new RunPipeline(Stories(), new WorkLedger(ledgerDb, TimeProvider.System), new PostgresRunLocks(_cs),
            new Workspaces(), worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null,
            app.Services.GetRequiredService<SessionRecorder>());
        var run = Task.Run(() => pipeline.RunAsync(7, CancellationToken.None));

        // The session is running and its first five events are stored.
        await worker.FirstBatchSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () => (await EventsAsync()).Count == 5);

        await using var viewer = await Viewer.JoinAsync(app, _cookies, StreamJsonFixture.SessionId, clock);

        worker.Release.SetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Succeeded, outcome.Error);
        await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count >= StreamJsonFixture.Fresh.Length));
        await Task.Delay(200); // nothing may arrive twice

        var got = viewer.Received.ToList();
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), got.Select(r => r.Event.Sequence));
        Assert.Equal(StreamJsonFixture.Fresh, got.Select(r => r.Event.Payload));
        foreach (var (e, at) in got.Skip(5))
        {
            var latency = at - worker.EmittedAt[(int)e.Sequence - 1];
            Assert.True(latency < TimeSpan.FromSeconds(1), $"event {e.Sequence} arrived {latency} after the worker wrote it");
        }

        await using var db = Context();
        var session = await db.WorkerSessions.SingleAsync();
        Assert.Equal((StreamJsonFixture.SessionId, "succeeded", 0.429606m), (session.ClaudeSessionId, session.ExitStatus, session.CostUsd));
    }

    /// <summary>
    /// The backlog→live handoff under load: a recorder outside the host (as a separate
    /// <c>factory run</c>) writes without pause while viewers join mid-stream; the host relays via
    /// NOTIFY. Each viewer gets exactly 1..N. Small pages make backlog and live sends interleave.
    /// </summary>
    [Fact]
    public async Task Viewers_joining_mid_stream_get_every_event_exactly_once_via_the_notify_relay()
    {
        const int total = 300;
        const string sid = "sess-race";
        await using var host = await StartHostAsync(new SessionHubOptions { PageSize = 7 });
        var app = host.App;
        var item = await ItemAsync();
        await using var capture = await Recorder(new FakeCosts(FixtureCost)).StartAsync(item.Id, null, CancellationToken.None);
        await capture.SetClaudeSessionIdAsync(sid, CancellationToken.None);

        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= total; i++)
            {
                await capture.OnLineAsync($$"""{"type":"assistant","session_id":"{{sid}}","n":{{i}}}""", CancellationToken.None);
                await Task.Delay(Random.Shared.Next(1, 6));
            }
            await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);
        });

        await WaitUntilAsync(async () => (await EventsAsync()).Count >= 1);
        await using var early = await Viewer.JoinAsync(app, _cookies, sid);
        await WaitUntilAsync(async () => (await EventsAsync()).Count >= total / 3);
        await using var middle = await Viewer.JoinAsync(app, _cookies, sid);
        await WaitUntilAsync(async () => (await EventsAsync()).Count >= 2 * total / 3);
        await using var late = await Viewer.JoinAsync(app, _cookies, sid);
        Assert.False(writer.IsCompleted, "the writer finished before the last viewer joined: nothing was mid-stream");
        await writer.WaitAsync(TimeSpan.FromSeconds(30));

        foreach (var viewer in new[] { early, middle, late })
        {
            await WaitUntilAsync(() => Task.FromResult(viewer.Received.Count >= total));
        }
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

    private sealed class Viewer : IAsyncDisposable
    {
        public required HubConnection Connection { get; init; }
        public ConcurrentQueue<(SessionEventMessage Event, TimeSpan At)> Received { get; } = new();

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
            await viewer.Connection.StartAsync();
            await viewer.Connection.InvokeAsync("JoinSession", sessionId);
            return viewer;
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class FakeCosts(params SessionCost?[] answers) : ISessionCostSource
    {
        public int Calls;

        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(answers[Math.Min(Interlocked.Increment(ref Calls), answers.Length) - 1]);
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
        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
            WorkerCallbacks? callbacks, CancellationToken ct)
        {
            await callbacks!.OnStarted!(1, ct);
            return await run(callbacks, ct);
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>Writes the recorded fixture: a first batch, then (once released) the rest, 50 ms apart.</summary>
    private sealed class FixtureWorker(Stopwatch clock, int firstBatch) : IWorker
    {
        public TaskCompletionSource FirstBatchSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan[] EmittedAt { get; } = new TimeSpan[StreamJsonFixture.Fresh.Length];

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
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
            }
            return new WorkerResult(StreamJsonFixture.SessionId, 0, false, "success", "done", "");
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    private static FakeWorkSource Stories() =>
        new(new WorkStory(1, "List files", "List the files.", "feature", "https://app.shortcut.com/t/story/1"));

    private sealed class Workspaces : IRepoWorkspace
    {
        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}");
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult(Ws(branch));
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult(Ws(branch));
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult<Workspace?>(Ws(branch));
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct) => Task.FromResult(true);
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PullRequests : IPullRequests
    {
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct) =>
            Task.FromResult("https://github.com/acme/widgets/pull/1");
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
