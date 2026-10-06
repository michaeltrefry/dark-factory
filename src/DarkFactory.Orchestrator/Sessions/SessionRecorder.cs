using System.Threading.Channels;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>A stored session event as viewers receive it.</summary>
public sealed record SessionEventMessage(long Sequence, string Type, string? Subtype, string Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// Persists worker sessions and their stream-json events (E7): <see cref="StartAsync"/> opens
/// a <see cref="SessionCapture"/> per worker run, which stores every stdout line in order and,
/// when the run ends, the exit status and the router's cost for the session (E9).
/// Storing is all it does: an insert trigger NOTIFYs <see cref="SessionEventRelay.Channel"/>, and the
/// <c>factory work</c> host relays stored events to live viewers, whichever process wrote them.
/// </summary>
public sealed class SessionRecorder(
    IDbContextFactory<LedgerDbContext> contexts,
    ISessionCostSource costs,
    TimeProvider time,
    TextWriter log,
    int capacity = SessionRecorder.DefaultCapacity,
    IReadOnlyList<TimeSpan>? costRetryDelays = null)
{
    /// <summary>Queued lines per run. A stream-json line can be large (tool results), so this stays modest.</summary>
    public const int DefaultCapacity = 256;

    /// <summary>
    /// The router commits a session's cost asynchronously, some time after its last request
    /// (the acceptance harness allows ~60 s): retry this long while it is missing or still zero.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultCostRetryDelays =
        [.. new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s)), .. Enumerable.Repeat(TimeSpan.FromSeconds(5), 9)];

    private readonly IDbContextFactory<LedgerDbContext> _contexts = contexts;
    private readonly ISessionCostSource _costs = costs;
    private readonly TimeProvider _time = time;
    private readonly TextWriter _log = log;
    private readonly int _capacity = capacity;
    private readonly IReadOnlyList<TimeSpan> _costRetryDelays = costRetryDelays ?? DefaultCostRetryDelays;

    /// <summary>
    /// Opens the capture of one worker run. Resuming <paramref name="resumeSessionId"/> continues
    /// that session's row and sequence; otherwise a new session row is created. A crash can leave
    /// the session id checkpointed in the ledger before its row holds it: then the item's newest
    /// row, if it has no session id, is that session's row and is continued.
    /// </summary>
    public async Task<SessionCapture> StartAsync(long workItemId, string? resumeSessionId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        WorkerSession? session = null;
        if (resumeSessionId is not null)
        {
            session = await db.WorkerSessions.SingleOrDefaultAsync(s => s.ClaudeSessionId == resumeSessionId, ct);
            if (session is null
                && await db.WorkerSessions.Where(s => s.WorkItemId == workItemId).OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct)
                    is { ClaudeSessionId: null } unnamed)
            {
                session = unnamed;
                session.ClaudeSessionId = resumeSessionId;
            }
        }
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
        private string? _claudeSessionId;

        internal SessionCapture(SessionRecorder recorder, long workerSessionId, long workItemId, string? claudeSessionId, long next)
        {
            _recorder = recorder;
            WorkerSessionId = workerSessionId;
            _workItemId = workItemId;
            _claudeSessionId = claudeSessionId;
            _next = next;
            _lines = Channel.CreateBounded<(string, DateTimeOffset)>(
                new BoundedChannelOptions(recorder._capacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            _writer = Task.Run(WriteAllAsync);
        }

        public long WorkerSessionId { get; }

        /// <summary>Null until the stream reports it (or the run resumes a known session).</summary>
        public string? ClaudeSessionId => Volatile.Read(ref _claudeSessionId);

        /// <summary>The <see cref="WorkerCallbacks.OnLine"/> tap.</summary>
        public ValueTask OnLineAsync(string line, CancellationToken ct) =>
            _lines.Writer.WriteAsync((line, _recorder._time.GetUtcNow()), ct);

        /// <summary>
        /// Stores the session id on the row now, ahead of the queued line that carries it, so a crash
        /// after the caller checkpoints the id cannot leave the row unnamed. Call it before that checkpoint.
        /// </summary>
        public async Task SetClaudeSessionIdAsync(string sessionId, CancellationToken ct)
        {
            await using var db = await _recorder._contexts.CreateDbContextAsync(ct);
            await db.WorkerSessions.Where(s => s.Id == WorkerSessionId && s.ClaudeSessionId == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaudeSessionId, sessionId), ct);
            Interlocked.CompareExchange(ref _claudeSessionId, sessionId, null);
        }

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

        /// <summary>
        /// The router's committed cost: retried while it is missing (404) or still zero (not fully
        /// committed), up to the recorder's retry delays. Null if it never arrives.
        /// </summary>
        private async Task<SessionCost?> FetchCostAsync(string sessionId, CancellationToken ct)
        {
            var delays = _recorder._costRetryDelays;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (await _recorder._costs.GetSessionCostAsync(sessionId, ct) is { ActualCostUsdMicros: > 0 } cost)
                    {
                        return cost;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _recorder._log.WriteLine($"[session] router cost for session {sessionId}: {ex.Message}");
                }
                if (attempt >= delays.Count)
                {
                    _recorder._log.WriteLine($"[session] router has no committed cost for session {sessionId}; left unset");
                    return null;
                }
                await Task.Delay(delays[attempt], _recorder._time, ct);
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
            // One transaction; the session_events insert trigger NOTIFYs the live relay when it commits.
            await using (var db = await _recorder._contexts.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                db.SessionEvents.AddRange(events);
                await db.SaveChangesAsync();
                if (learned is not null)
                {
                    await db.WorkerSessions.Where(s => s.Id == WorkerSessionId && s.ClaudeSessionId == null)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaudeSessionId, learned));
                }
                await tx.CommitAsync();
            }
            _next += events.Count;
            if (learned is not null)
            {
                Interlocked.CompareExchange(ref _claudeSessionId, learned, null);
            }
        }
    }
}
