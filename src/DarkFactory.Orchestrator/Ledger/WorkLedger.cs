using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Writes work items and their state-change rows. Each state change is one
/// committed row; the item's current state is updated in the same transaction.
/// </summary>
public sealed class WorkLedger(LedgerDbContext db, TimeProvider time)
{
    public async Task<WorkItem> GetOrCreateAsync(string source, string externalId, string title, string repo, CancellationToken ct)
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

    public async Task<LedgerEntry> RecordAsync(WorkItem item, WorkState state, string? claudeSessionId, string? detail, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var entry = new LedgerEntry
        {
            WorkItemId = item.Id,
            State = state,
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
