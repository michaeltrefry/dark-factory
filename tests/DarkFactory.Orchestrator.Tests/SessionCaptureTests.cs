using System.Collections.Concurrent;
using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}

/// <summary>Session capture against the compose Postgres (localhost:5434), and the session hub on Kestrel.</summary>
public sealed class SessionCaptureTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;

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

    private SessionRecorder Recorder(ISessionCostSource costs, int capacity = 4096) =>
        new(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), StoreOnlyPublisher.Instance, costs,
            TimeProvider.System, TextWriter.Null, capacity, costRetryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

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
        // The router has not committed the cost when the session ends; it has on the retry.
        var costs = new FakeCosts(null, FixtureCost);
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
        Assert.Equal(2, costs.Calls);
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

    [Fact]
    public async Task Joining_a_running_session_gets_its_backlog_then_live_events_once_each_within_a_second()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = _cs,
            ["Factory:HostPort"] = "0",
        }).Build();
        await using var app = FactoryHost.Build(new FactoryOptions(config, new InMemorySecrets()),
            services => services.AddSingleton<ISessionCostSource>(new FakeCosts(FixtureCost)));
        await app.StartAsync();

        var clock = Stopwatch.StartNew();
        var worker = new FixtureWorker(clock, firstBatch: 5);
        await using var ledgerDb = Context();
        var pipeline = new RunPipeline(new Stories(), new WorkLedger(ledgerDb, TimeProvider.System), new PostgresRunLocks(_cs),
            new Workspaces(), worker, new PullRequests(), new RepoRef("acme", "widgets"), TextWriter.Null,
            app.Services.GetRequiredService<SessionRecorder>());
        var run = Task.Run(() => pipeline.RunAsync(7, CancellationToken.None));

        // The session is running and its first five events are stored.
        await worker.FirstBatchSent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () => (await EventsAsync()).Count == 5);

        var received = new ConcurrentQueue<(SessionEventMessage Event, TimeSpan At)>();
        await using var client = new HubConnectionBuilder().WithUrl(app.Address() + SessionHub.Path).Build();
        client.On<string, SessionEventMessage[]>(SessionHub.EventsMethod, (sessionId, events) =>
        {
            Assert.Equal(StreamJsonFixture.SessionId, sessionId);
            foreach (var e in events)
            {
                received.Enqueue((e, clock.Elapsed));
            }
        });
        await client.StartAsync();
        await client.InvokeAsync("JoinSession", StreamJsonFixture.SessionId);

        worker.Release.SetResult();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Succeeded, outcome.Error);
        await WaitUntilAsync(() => Task.FromResult(received.Count >= StreamJsonFixture.Fresh.Length));
        await Task.Delay(200); // nothing may arrive twice

        var got = received.ToList();
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    private sealed class FakeCosts(params SessionCost?[] answers) : ISessionCostSource
    {
        public int Calls;

        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(answers[Math.Min(Interlocked.Increment(ref Calls), answers.Length) - 1]);
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

    private sealed class Stories : IStorySource
    {
        public Task<ShortcutStory> GetStoryAsync(int id, CancellationToken ct) =>
            Task.FromResult(new ShortcutStory(id, "List files", "List the files.", "feature", $"https://app.shortcut.com/t/story/{id}"));
        public Task AddCommentAsync(int id, string text, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Workspaces : IRepoWorkspace
    {
        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main");
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
