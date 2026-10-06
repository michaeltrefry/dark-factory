using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Writes work items and their ledger rows. Each transition is checked against
/// <see cref="Lifecycle"/> before anything is written, then committed as one row
/// together with the item's current state (E2, E3).
/// </summary>
public sealed class WorkLedger(LedgerDbContext db, TimeProvider time)
{
    /// <summary>
    /// Returns the item, creating it in <see cref="WorkState.Intake"/> with its Intake row when new.
    /// An existing item is returned as stored, unchanged: take the item's run lock, then
    /// <see cref="RefreshAsync"/> it, before writing to it.
    /// </summary>
    public async Task<WorkItem> GetOrCreateAsync(string source, string externalId, string title, string repo, string? intakeDetail, CancellationToken ct)
    {
        if (await db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct) is { } existing)
        {
            return existing;
        }
        var now = time.GetUtcNow();
        var item = new WorkItem
        {
            Source = source,
            ExternalId = externalId,
            Title = title,
            Repo = repo,
            State = WorkState.Intake,
            CreatedAt = now,
            UpdatedAt = now,
        };
        item.Entries.Add(new LedgerEntry { State = WorkState.Intake, RecordedAt = now, Detail = intakeDetail });
        db.WorkItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException)
        {
            // A concurrent run created it first (unique Source+ExternalId): use theirs.
            db.Entry(item).State = EntityState.Detached;
            foreach (var entry in item.Entries)
            {
                db.Entry(entry).State = EntityState.Detached;
            }
            var winner = await db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);
            if (winner is null)
            {
                throw;
            }
            return winner;
        }
    }

    /// <summary>
    /// Re-reads the item (another run may have moved it since it was loaded) and updates its
    /// title and repo. Call while holding the item's run lock.
    /// </summary>
    public async Task RefreshAsync(WorkItem item, string title, string repo, CancellationToken ct)
    {
        await db.Entry(item).ReloadAsync(ct);
        if (item.Title == title && item.Repo == repo)
        {
            return;
        }
        var (oldTitle, oldRepo, oldUpdated, oldVersion) = (item.Title, item.Repo, item.UpdatedAt, item.Version);
        item.Title = title;
        item.Repo = repo;
        item.UpdatedAt = time.GetUtcNow();
        item.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            (item.Title, item.Repo, item.UpdatedAt, item.Version) = (oldTitle, oldRepo, oldUpdated, oldVersion);
            throw;
        }
    }

    /// <summary>
    /// Moves the item to <paramref name="state"/>. An illegal transition throws
    /// <see cref="IllegalTransitionException"/> and writes nothing.
    /// </summary>
    public async Task<LedgerEntry> RecordAsync(WorkItem item, WorkState state, string? claudeSessionId, string? detail, CancellationToken ct)
    {
        Lifecycle.Ensure(item.State, state, await ContextAsync(item, ct));
        return await AppendAsync(item, state, null, claudeSessionId, detail, ct);
    }

    /// <summary>Records a completed sub-step of the item's current state; the state does not change.</summary>
    public Task<LedgerEntry> CheckpointAsync(WorkItem item, string step, string? claudeSessionId, string? detail, CancellationToken ct) =>
        AppendAsync(item, item.State, step, claudeSessionId, detail, ct);

    /// <summary>The item's current state as stored, or null when there is no such item. Reads only.</summary>
    public async Task<WorkState?> StateOfAsync(string source, string externalId, CancellationToken ct) =>
        (await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct))?.State;
    /// <summary>The item, or null if the ledger has never seen it. Nothing is written.</summary>
    public Task<WorkItem?> FindAsync(string source, string externalId, CancellationToken ct) =>
        db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);

    /// <summary>The source's items currently in one of <paramref name="states"/>, oldest first.</summary>
    public async Task<List<WorkItem>> ItemsInAsync(string source, IReadOnlySet<WorkState> states, CancellationToken ct)
    {
        var wanted = states.ToList();
        return await db.WorkItems.Where(x => x.Source == source && wanted.Contains(x.State)).OrderBy(x => x.Id).ToListAsync(ct);
    }

    /// <summary>Every row of the item, oldest first.</summary>
    public Task<List<LedgerEntry>> HistoryAsync(WorkItem item, CancellationToken ct) =>
        db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);

    public async Task<TransitionContext> ContextAsync(WorkItem item, CancellationToken ct) =>
        TransitionContext.From((await HistoryAsync(item, ct)).Where(e => e.Step is null).Select(e => e.State).ToList());

    private async Task<LedgerEntry> AppendAsync(WorkItem item, WorkState state, string? step, string? claudeSessionId, string? detail, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var entry = new LedgerEntry
        {
            WorkItemId = item.Id,
            State = state,
            Step = step,
            RecordedAt = now,
            ClaudeSessionId = claudeSessionId,
            Detail = detail,
        };
        var (previousState, previousUpdatedAt, previousVersion) = (item.State, item.UpdatedAt, item.Version);
        db.LedgerEntries.Add(entry);
        item.State = state;
        item.UpdatedAt = now;
        item.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Nothing was written: leave neither a tracked row nor an in-memory state the ledger doesn't have.
            db.Entry(entry).State = EntityState.Detached;
            (item.State, item.UpdatedAt, item.Version) = (previousState, previousUpdatedAt, previousVersion);
            throw;
        }
        return entry;
    }
}
