using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Writes work items and their ledger rows. Each transition is checked against
/// <see cref="Lifecycle"/> before anything is written, then committed as one row
/// together with the item's current state (E2, E3).
/// </summary>
public sealed class WorkLedger(LedgerDbContext db, TimeProvider time)
{
    /// <summary>Returns the item, creating it in <see cref="WorkState.Intake"/> with its Intake row when new.</summary>
    public async Task<WorkItem> GetOrCreateAsync(string source, string externalId, string title, string repo, string? intakeDetail, CancellationToken ct)
    {
        var item = await db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);
        var now = time.GetUtcNow();
        if (item is null)
        {
            item = new WorkItem
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
        }
        else
        {
            item.Title = title;
            item.Repo = repo;
            item.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return item;
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
        db.LedgerEntries.Add(entry);
        item.State = state;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return entry;
    }
}
