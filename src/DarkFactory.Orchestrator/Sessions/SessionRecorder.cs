using System.Threading.Channels;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>A stored session event as viewers receive it.</summary>
public sealed record SessionEventMessage(long Sequence, string Type, string? Subtype, string Payload, DateTimeOffset ReceivedAt);

/// <summary>Delivers newly stored session events to live viewers.</summary>
public interface ISessionEventPublisher
{
    /// <summary>
    /// Runs <paramref name="commit"/>, which stores a batch of events of the session row
    /// <paramref name="workerSessionId"/>, then delivers the stored batch to the viewers of
    /// <paramref name="claudeSessionId"/> (nobody while it is null). A viewer joining meanwhile
    /// gets each event exactly once: in its backlog or live.
    /// </summary>
    Task CommitAndPublishAsync(long workerSessionId, string? claudeSessionId, Func<Task<IReadOnlyList<SessionEventMessage>>> commit);
}

/// <summary>No live viewers (the one-shot <c>factory run</c>): events are only stored.</summary>
public sealed class StoreOnlyPublisher : ISessionEventPublisher
{
    public static readonly StoreOnlyPublisher Instance = new();

    public Task CommitAndPublishAsync(long workerSessionId, string? claudeSessionId, Func<Task<IReadOnlyList<SessionEventMessage>>> commit) =>
        commit();
}

/// <summary>
/// Persists worker sessions and their stream-json events (E7): <see cref="StartAsync"/> opens
/// a <see cref="SessionCapture"/> per worker run, which stores every stdout line in order and,
/// when the run ends, the exit status and the router's cost for the session (E9).
/// </summary>
public sealed class SessionRecorder(
    IDbContextFactory<LedgerDbContext> contexts,
    ISessionEventPublisher publisher,
    ISessionCostSource costs,
    TimeProvider time,
    TextWriter log,
    int capacity = 4096,
    IReadOnlyList<TimeSpan>? costRetryDelays = null)
{
    /// <summary>The router commits a session's cost shortly after its last request; wait this long for it.</summary>
    private static readonly TimeSpan[] DefaultCostRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly IDbContextFactory<LedgerDbContext> _contexts = contexts;
    private readonly ISessionEventPublisher _publisher = publisher;
    private readonly ISessionCostSource _costs = costs;
    private readonly TimeProvider _time = time;
    private readonly TextWriter _log = log;
    private readonly int _capacity = capacity;
    private readonly IReadOnlyList<TimeSpan> _costRetryDelays = costRetryDelays ?? DefaultCostRetryDelays;

    /// <summary>
    /// Opens the capture of one worker run. Resuming <paramref name="resumeSessionId"/> continues
    /// that session's row and sequence; otherwise a new session row is created.
    /// </summary>
    public async Task<SessionCapture> StartAsync(long workItemId, string? resumeSessionId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var session = resumeSessionId is null
            ? null
            : await db.WorkerSessions.SingleOrDefaultAsync(s => s.ClaudeSessionId == resumeSessionId, ct);
        long next = 1;
        if (session is null)
        {
            session = new WorkerSession
            {
                WorkItemId = workItemId,
                ClaudeSessionId = resumeSessionId,
                Attempt = await db.WorkerSessions.CountAsync(s => s.WorkItemId == workItemId, ct) + 1,
                StartedAt = _time.GetUtcNow(),
            };
            db.WorkerSessions.Add(session);
        }
        else
        {
            next = (await db.SessionEvents.Where(e => e.WorkerSessionId == session.Id).MaxAsync(e => (long?)e.Sequence, ct) ?? 0) + 1;
            (session.EndedAt, session.ExitCode, session.ExitStatus) = (null, null, null);
        }
        await db.SaveChangesAsync(ct);
        return new SessionCapture(this, session.Id, workItemId, session.ClaudeSessionId, next);
    }

    /// <summary>
    /// One worker run's events. <see cref="OnLineAsync"/> only queues the line (a bounded channel),
    /// so the worker's stdout reader is not held up by the database; a separate writer stores the
    /// lines in order. When the queue is full the reader waits rather than drop a line, and a
    /// failed store fails the reader's next line rather than lose events silently.
    /// </summary>
    public sealed class SessionCapture : IAsyncDisposable
    {
        private const int MaxBatch = 256;
        private readonly SessionRecorder _recorder;
        private readonly long _workItemId;
        private readonly Channel<(string Line, DateTimeOffset At)> _lines;
        private readonly Task _writer;
        private long _next;

        internal SessionCapture(SessionRecorder recorder, long workerSessionId, long workItemId, string? claudeSessionId, long next)
        {
            _recorder = recorder;
            WorkerSessionId = workerSessionId;
            _workItemId = workItemId;
            ClaudeSessionId = claudeSessionId;
            _next = next;
            _lines = Channel.CreateBounded<(string, DateTimeOffset)>(
                new BoundedChannelOptions(recorder._capacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            _writer = Task.Run(WriteAllAsync);
        }

        public long WorkerSessionId { get; }

        /// <summary>Null until the stream reports it (or the run resumes a known session).</summary>
        public string? ClaudeSessionId { get; private set; }

        /// <summary>The <see cref="WorkerCallbacks.OnLine"/> tap.</summary>
        public ValueTask OnLineAsync(string line, CancellationToken ct) =>
            _lines.Writer.WriteAsync((line, _recorder._time.GetUtcNow()), ct);

        /// <summary>
        /// Stores the remaining lines, then the end of the run and, with <paramref name="fetchCost"/>,
        /// the router's cost for the session. A missing cost is logged, never fatal: spend is reporting only (E9).
        /// </summary>
        public async Task CompleteAsync(int? exitCode, string exitStatus, bool fetchCost, CancellationToken ct)
        {
            _lines.Writer.TryComplete();
            await _writer;
            await using var db = await _recorder._contexts.CreateDbContextAsync(ct);
            var session = await db.WorkerSessions.SingleAsync(s => s.Id == WorkerSessionId, ct);
            (session.EndedAt, session.ExitCode, session.ExitStatus) = (_recorder._time.GetUtcNow(), exitCode, exitStatus);
            await db.SaveChangesAsync(ct);
            if (fetchCost && session.ClaudeSessionId is { } sid && await FetchCostAsync(sid, ct) is { } cost)
            {
                session.CostUsd = cost.ActualCostUsdMicros / 1_000_000m;
                session.RouterRequestCount = cost.RequestCount;
                await db.SaveChangesAsync(ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_lines.Writer.TryComplete())
            {
                try
                {
                    await _writer;
                }
                catch (Exception ex)
                {
                    _recorder._log.WriteLine($"[session] storing events of session {ClaudeSessionId ?? WorkerSessionId.ToString()} failed: {ex.Message}");
                }
            }
        }

        private async Task<SessionCost?> FetchCostAsync(string sessionId, CancellationToken ct)
        {
            var delays = _recorder._costRetryDelays;
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    if (await _recorder._costs.GetSessionCostAsync(sessionId, ct) is { } cost)
                    {
                        return cost;
                    }
                    if (attempt >= delays.Count)
                    {
                        _recorder._log.WriteLine($"[session] router has no cost for session {sessionId} yet");
                        return null;
                    }
                    await Task.Delay(delays[attempt], _recorder._time, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _recorder._log.WriteLine($"[session] router cost for session {sessionId} unavailable: {ex.Message}");
                return null;
            }
        }

        private async Task WriteAllAsync()
        {
            try
            {
                var batch = new List<(string Line, DateTimeOffset At)>(MaxBatch);
                while (await _lines.Reader.WaitToReadAsync())
                {
                    batch.Clear();
                    while (batch.Count < MaxBatch && _lines.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }
                    await StoreAsync(batch);
                }
            }
            catch (Exception ex)
            {
                // The worker's next line throws this, so its run fails instead of losing events.
                _lines.Writer.TryComplete(ex);
                throw;
            }
        }

        private async Task StoreAsync(List<(string Line, DateTimeOffset At)> batch)
        {
            string? learned = null;
            var events = new List<SessionEvent>(batch.Count);
            foreach (var (line, at) in batch)
            {
                var parsed = StreamJsonEvent.Parse(line);
                if (ClaudeSessionId is null && learned is null)
                {
                    learned = parsed.SessionId;
                }
                events.Add(new SessionEvent
                {
                    WorkerSessionId = WorkerSessionId,
                    WorkItemId = _workItemId,
                    Sequence = _next + events.Count,
                    Type = parsed.Type,
                    Subtype = parsed.Subtype,
                    Payload = line.Replace('\0', '�'), // Postgres text cannot hold NUL
                    ReceivedAt = at,
                });
            }
            await _recorder._publisher.CommitAndPublishAsync(WorkerSessionId, ClaudeSessionId ?? learned, async () =>
            {
                await using var db = await _recorder._contexts.CreateDbContextAsync();
                db.SessionEvents.AddRange(events);
                if (learned is not null)
                {
                    (await db.WorkerSessions.SingleAsync(s => s.Id == WorkerSessionId)).ClaudeSessionId = learned;
                }
                await db.SaveChangesAsync();
                return events.Select(e => new SessionEventMessage(e.Sequence, e.Type, e.Subtype, e.Payload, e.ReceivedAt)).ToList();
            });
            _next += events.Count;
            ClaudeSessionId ??= learned;
        }
    }
}
