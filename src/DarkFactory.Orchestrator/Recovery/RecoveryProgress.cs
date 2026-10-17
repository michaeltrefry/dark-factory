using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>A dispatched session is a durable, leased claim, even if its caller never receives its answer.</summary>
public sealed class RecoveryAssessmentAttempt
{
    public long Id { get; set; }
    public long IncidentId { get; set; }
    public required string Snapshot { get; set; }
    public required string Session { get; set; }
    public int Number { get; set; }
    public required string State { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
    public string? Result { get; set; }
    public long Version { get; set; }
}

internal sealed class RecoveryEvidenceChangedException(string message) : Exception(message);

public sealed partial class HotFileRecovery
{
    internal const int AssessmentLimit = 3;
    internal const int RefreshLimit = 3;
    internal static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(15); // five-minute capture + ten-minute conversation
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(attempt == 1 ? 30 : 120);

    private static void Escalate(HotFileIncident incident, string reason)
    {
        incident.State = "Escalated";
        incident.Error = reason + "; repository remains held.";
        incident.NextAttemptAt = null;
        incident.Version++;
    }

    // Only called with the repository's advisory claim. Snapshot identity changes, but incident identity and lifetime budgets do not.
    private async Task<bool> RefreshAsync(LedgerDbContext db, HotFileIncident incident, IReadOnlyList<HotFileMerge> merges,
        string reason, CancellationToken ct)
    {
        if (incident.Refreshes >= RefreshLimit)
        {
            Escalate(incident, "Evidence refresh budget exhausted: " + reason);
            await db.SaveChangesAsync(ct);
            return false;
        }
        var snapshot = new HotFileSnapshot(Guid.NewGuid().ToString("N"), incident.Repo, incident.Generation, merges, Hash(merges));
        incident.Snapshot = JsonSerializer.Serialize(snapshot);
        incident.EvidenceHash = snapshot.EvidenceHash;
        incident.Refreshes++;
        incident.Attempts = 0;
        incident.State = "Reassessing";
        incident.Answer = null;
        incident.Session = null;
        incident.NextAttemptAt = null;
        incident.Error = reason;
        incident.Version++;
        return true;
    }

    private async Task RefreshStaleAsync(LedgerDbContext db, HotFileIncident incident, CancellationToken ct)
    {
        var old = HotFileSnapshot.Parse(incident.Snapshot);
        var current = await EvidenceAsync(db, incident.Repo, ct);
        // Preserve old contributing evidence after the rolling window expires, and coalesce new merges/hot files.
        var merges = old.Merges.Concat(current).GroupBy(m => m.Row).Select(g => g.Last()).OrderBy(m => m.Row).ToArray();
        await RefreshAsync(db, incident, merges, incident.Error ?? "Evidence authority changed", ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileCorrectionAsync(LedgerDbContext db, HotFileIncident incident, CancellationToken ct)
    {
        if (incident.State != "Correcting") return;
        var c = await db.RecoveryCorrections.Where(c => c.IncidentId == incident.Id).OrderByDescending(c => c.Attempt).FirstOrDefaultAsync(ct);
        if (c is null) { Escalate(incident, "Correcting incident has no reserved correction"); await db.SaveChangesAsync(ct); return; }
        var item = await db.WorkItems.SingleOrDefaultAsync(i => i.ExternalId == "rc-" + c.Id, ct);
        if (c.State == "Stopped" || item?.State == WorkState.Cancelled
            || await db.RecoveryAncestors.AnyAsync(a => a.CorrectionId == c.Id && db.WorkItems.Any(w => w.Id == a.WorkItemId && w.State == WorkState.Cancelled), ct))
        {
            Escalate(incident, "Correction or required ancestor stopped; no automated replacement is authorized");
            await db.SaveChangesAsync(ct);
        }
        else if (item?.State == WorkState.Escalated && c.State is "Pending" or "Claimed")
        {
            // A crash between pipeline escalation and its source comment must not strand Correcting forever.
            c.State = "Failed"; c.Version++;
            incident.State = "CorrectionFailed"; incident.Version++;
            await db.SaveChangesAsync(ct);
        }
    }
}
