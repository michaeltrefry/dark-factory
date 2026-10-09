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
    ControlState Control = ControlState.Running);

/// <summary>A worker session of an item; <see cref="ClaudeSessionId"/> is null until the worker reports it.</summary>
public sealed record SessionLink(string? ClaudeSessionId, int Attempt, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? ExitStatus, decimal? CostUsd)
{
    public bool Running => EndedAt is null;
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
}

public sealed class DashboardData(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time) : IDashboardData
{
    private static readonly WorkState[] Finished = [WorkState.Done, WorkState.Cancelled];

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
        var sessions = (await db.WorkerSessions.AsNoTracking().Where(s => ids.Contains(s.WorkItemId)).OrderBy(s => s.Id).ToListAsync(ct))
            .ToLookup(s => s.WorkItemId);
        var links = (await db.LedgerEntries.AsNoTracking()
                .Where(e => ids.Contains(e.WorkItemId) && e.Step == RunPipeline.Steps.Linked)
                .OrderBy(e => e.Id)
                .ToListAsync(ct))
            .GroupBy(e => e.WorkItemId)
            .ToDictionary(g => g.Key, g => g.Last().Detail);
        return items.Select(i => new PipelineRow(
                i.Id, i.ExternalId, i.Title, i.Repo, i.State, i.CreatedAt, i.UpdatedAt,
                PullRequestUrl(links.GetValueOrDefault(i.Id)),
                // Spend per item (E9, reporting only): the sum of its sessions' router costs.
                sessions[i.Id].Any(s => s.CostUsd is not null) ? sessions[i.Id].Sum(s => s.CostUsd ?? 0) : null,
                sessions[i.Id].Select(Link).ToList(),
                i.EpicId,
                ControlOf(i)))
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
        return new SessionHeader(item.Id, item.ExternalId, item.Title, item.Repo, Link(session));
    }

    private static SessionLink Link(WorkerSession s) => new(s.ClaudeSessionId, s.Attempt, s.StartedAt, s.EndedAt, s.ExitStatus, s.CostUsd);

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
