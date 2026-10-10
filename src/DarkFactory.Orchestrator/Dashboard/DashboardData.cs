using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>One active work item as the pipeline view shows it.</summary>
public sealed record PipelineRow(
    long Id,
    string ExternalId,
    string Title,
    string Repo,
    WorkState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PullRequestUrl,
    decimal? CostUsd,
    IReadOnlyList<SessionLink> Sessions,
    long? EpicId = null,
    ControlState Control = ControlState.Running,
    CloseoutStatus? FailedCloseout = null)
{
    /// <summary>Whether the factory will try the failed closeout again (the next poll), or has left it to a human.</summary>
    public bool CloseoutRetrying => FailedCloseout is { Attempts: < RunPipeline.MaxCloseoutAttempts };
}

/// <summary>
/// A worker session of an item; <see cref="ClaudeSessionId"/> is null until the worker reports it. <see cref="LastEventAt"/>: when
/// its latest stream event was stored (read for running sessions only).
/// </summary>
public sealed record SessionLink(string? ClaudeSessionId, int Attempt, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? ExitStatus, decimal? CostUsd,
    DateTimeOffset? LastEventAt = null)
{
    public bool Running => EndedAt is null;

    /// <summary>
    /// A running session with no event (nor, before its first, since it started) for <paramref name="threshold"/> at
    /// <paramref name="now"/> is quiet (sc-25388). Only marked on the dashboard: silence alone never interrupts a worker.
    /// </summary>
    public bool QuietAt(DateTimeOffset now, TimeSpan threshold) => Running && now - (LastEventAt ?? StartedAt) >= threshold;
}

public sealed record SessionHeader(long WorkItemId, string ExternalId, string Title, string Repo, SessionLink Session);

/// <summary>
/// The dashboard's reads of the ledger. Read-only: the dashboard's only writes are the controls
/// (<see cref="Controls.ControlActions"/>, E8).
/// </summary>
public interface IDashboardData
{
    /// <summary>Items not Done or Cancelled, oldest first, each with the control that applies to it.</summary>
    Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct);

    Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct);

    /// <summary>Every scope's control (factory, epics, items).</summary>
    Task<IReadOnlyList<Control>> ControlsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Control>>([]);

    /// <summary>How long a running session may go without an event before the pages mark it quiet (<c>Worker:QuietMinutes</c>).</summary>
    TimeSpan QuietThreshold => DashboardData.DefaultQuietThreshold;

    /// <summary>The factory's metrics over the ledger (<see cref="LedgerMetrics"/>; N/A when unmeasured, sandbox and demo items left out).</summary>
    Task<IReadOnlyList<Metric>> MetricsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Metric>>([]);
}

public sealed class DashboardData(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time, TimeSpan? quietThreshold = null,
    MetricsOptions? metrics = null, TimeSpan? metricsTtl = null) : IDashboardData
{
    private readonly LedgerMetrics _metrics = new(contexts, metrics ?? MetricsOptions.Default);
    private readonly TimeSpan _metricsTtl = metricsTtl ?? DefaultMetricsTtl;
    private readonly SemaphoreSlim _metricsGate = new(1, 1);
    private (IReadOnlyList<Metric> Value, DateTimeOffset At)? _cachedMetrics;

    /// <summary>
    /// How long computed metrics are served before they are computed again: they read every item's rows, so they are not recomputed on
    /// every ledger change the pipeline page reloads on.
    /// </summary>
    public static readonly TimeSpan DefaultMetricsTtl = TimeSpan.FromSeconds(60);

    /// <summary>The metrics, computed at most once per <see cref="DefaultMetricsTtl"/> (<c>metricsTtl</c>) for every page and viewer.</summary>
    public async Task<IReadOnlyList<Metric>> MetricsAsync(CancellationToken ct)
    {
        await _metricsGate.WaitAsync(ct);
        try
        {
            if (_cachedMetrics is { } cached && time.GetUtcNow() - cached.At < _metricsTtl)
            {
                return cached.Value;
            }
            var value = await _metrics.ComputeAsync(ct);
            _cachedMetrics = (value, time.GetUtcNow());
            return value;
        }
        finally
        {
            _metricsGate.Release();
        }
    }

    /// <summary>
    /// <c>Worker:QuietMinutes</c>'s default: the longest legitimate tool call (<see cref="RunPipeline.LongestToolCall"/>, a long
    /// <c>dotnet test</c>) streams nothing for that long, so only a silence past it is worth a look.
    /// </summary>
    public static readonly TimeSpan DefaultQuietThreshold = RunPipeline.LongestToolCall;

    private static readonly WorkState[] Finished = [WorkState.Done, WorkState.Cancelled];

    public TimeSpan QuietThreshold { get; } = quietThreshold ?? DefaultQuietThreshold;

    public async Task<IReadOnlyList<Control>> ControlsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Controls.AsNoTracking().OrderBy(c => c.Scope).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var items = await db.WorkItems.AsNoTracking().Where(i => !Finished.Contains(i.State)).OrderBy(i => i.Id).ToListAsync(ct);
        var controls = (await db.Controls.AsNoTracking().ToListAsync(ct)).ToDictionary(c => c.Scope);
        var now = time.GetUtcNow();
        ControlState ControlOf(WorkItem i) =>
            controls.GetValueOrDefault(ControlScope.Item(i.ExternalId))?.State == ControlState.Stopping ? ControlState.Stopping
            : new[] { ControlScope.Factory, ControlScope.Usage, ControlScope.Freeze, ControlScope.Item(i.ExternalId), i.EpicId is { } e ? ControlScope.Epic(e) : "" }
                .Any(s => controls.GetValueOrDefault(s)?.PausesAt(now) == true) ? ControlState.Paused
            : ControlState.Running;
        var ids = items.Select(i => i.Id).ToList();
        var rows = await db.WorkerSessions.AsNoTracking().Where(s => ids.Contains(s.WorkItemId)).OrderBy(s => s.Id).ToListAsync(ct);
        var lastEvents = await LastEventsAsync(db, rows.Where(s => s.EndedAt is null).Select(s => s.Id), ct);
        var sessions = rows.ToLookup(s => s.WorkItemId);
        var links = (await db.LedgerEntries.AsNoTracking()
                .Where(e => ids.Contains(e.WorkItemId) && e.Step == RunPipeline.Steps.Linked)
                .OrderBy(e => e.Id)
                .ToListAsync(ct))
            .GroupBy(e => e.WorkItemId)
            .ToDictionary(g => g.Key, g => g.Last().Detail);
        var closeouts = await new WorkLedger(db, time).CloseoutRowsAsync(items.Where(i => i.State == WorkState.Watch).Select(i => i.Id).ToList(), ct);
        return items.Select(i => new PipelineRow(
                i.Id, i.ExternalId, i.Title, i.Repo, i.State, i.CreatedAt, i.UpdatedAt,
                PullRequestUrl(links.GetValueOrDefault(i.Id)),
                // Spend per item (E9, reporting only): the sum of its sessions' measured router costs, null (N/A) when none is
                // measured; the page labels a partial sum with how many sessions it covers (Format.ItemCost, E5).
                sessions[i.Id].Any(s => s.CostUsd is not null) ? sessions[i.Id].Where(s => s.CostUsd is not null).Sum(s => s.CostUsd!.Value) : null,
                sessions[i.Id].Select(s => Link(s, lastEvents.GetValueOrDefault(s.Id))).ToList(),
                i.EpicId,
                ControlOf(i),
                i.State == WorkState.Watch && RunPipeline.CloseoutOf(closeouts[i.Id]) is { Posted: false, Attempts: > 0 } failed ? failed : null))
            .ToList();
    }

    public async Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var session = await db.WorkerSessions.AsNoTracking().SingleOrDefaultAsync(s => s.ClaudeSessionId == claudeSessionId, ct);
        if (session is null)
        {
            return null;
        }
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Id == session.WorkItemId, ct);
        var lastEvents = await LastEventsAsync(db, session.EndedAt is null ? [session.Id] : [], ct);
        return new SessionHeader(item.Id, item.ExternalId, item.Title, item.Repo, Link(session, lastEvents.GetValueOrDefault(session.Id)));
    }

    /// <summary>When each of <paramref name="sessionIds"/>' latest event was stored (its highest sequence: the unique index).</summary>
    private static async Task<Dictionary<long, DateTimeOffset>> LastEventsAsync(LedgerDbContext db, IEnumerable<long> sessionIds, CancellationToken ct)
    {
        var last = new Dictionary<long, DateTimeOffset>();
        foreach (var id in sessionIds)
        {
            if (await db.SessionEvents.AsNoTracking().Where(e => e.WorkerSessionId == id).OrderByDescending(e => e.Sequence)
                    .Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct) is { } at)
            {
                last[id] = at;
            }
        }
        return last;
    }

    private static SessionLink Link(WorkerSession s, DateTimeOffset? lastEventAt) =>
        new(s.ClaudeSessionId, s.Attempt, s.StartedAt, s.EndedAt, s.ExitStatus, s.CostUsd, s.EndedAt is null ? lastEventAt : null);

    /// <summary>The <c>linked</c> checkpoint's detail is "&lt;PR url&gt; &lt;branch url&gt;".</summary>
    private static string? PullRequestUrl(string? linkedDetail) =>
        linkedDetail?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : null;
}

/// <summary>
/// Ledger changes to work items (any state, checkpoint or session-cost write, from any process),
/// relayed by <see cref="Sessions.SessionEventRelay"/>. A null item id means "anything may have
/// changed" (notifications were missed while the relay reconnected).
/// </summary>
public sealed class PipelineChanges
{
    /// <summary>Raised on the relay's thread: handlers must only schedule their work.</summary>
    public event Action<long?>? Changed;

    public void Notify(long? workItemId)
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<long?>>() ?? [])
        {
            try
            {
                handler(workItemId);
            }
            catch (Exception)
            {
                // one broken viewer must not stop the others (or the relay)
            }
        }
    }
}
