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

    private HashSet<(string Base, string File)> QualifyingPaths(IReadOnlyList<HotFileMerge> evidence,
        HashSet<(long Row, string Base, string File)> acknowledged) => evidence
        .SelectMany(m => m.Files.Select(f => (Merge: m, File: f)))
        .GroupBy(x => (x.Merge.Base, x.File))
        .Where(g => g.Select(x => x.Merge.Row).Distinct().Count() >= options.HotFileMerges
            && g.Any(x => !acknowledged.Contains((x.Merge.Row, x.Merge.Base, x.File))))
        .Select(g => g.Key).ToHashSet();

    // Trigger membership is narrower than semantic freshness: a changed base tip may require a new material
    // capture without making every merge on that base a contributor. Keep exact original tuples, overlapping
    // (base,file) tuples, and newly qualifying hot paths, using detection's acknowledgement rule.
    private async Task<HotFileMerge[]> RefreshEvidenceAsync(LedgerDbContext db, HotFileSnapshot snapshot, CancellationToken ct)
    {
        var current = await EvidenceAsync(db, snapshot.Repo, ct);
        var resolved = await db.HotFileIncidents.AsNoTracking()
            .Where(i => i.Repo == snapshot.Repo && i.State == "Resolved").Select(i => i.Snapshot).ToListAsync(ct);
        var acknowledged = resolved.SelectMany(s => HotFileSnapshot.Parse(s).Merges)
            .SelectMany(m => m.Files.Select(f => (m.Row, m.Base, File: f))).ToHashSet();
        var paths = QualifyingPaths(current, acknowledged);
        paths.UnionWith(snapshot.Merges.SelectMany(m => m.Files.Select(f => (m.Base, File: f))));
        var additions = current.Select(m => m with { Files = m.Files.Where(f => paths.Contains((m.Base, f))).ToArray() })
            .Where(m => m.Files.Count > 0);
        return snapshot.Merges.Concat(additions).GroupBy(m => m.Row)
            .Select(g => g.First() with { Files = g.SelectMany(m => m.Files).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() })
            .OrderBy(m => m.Row).ToArray();
    }

    private async Task RefreshStaleAsync(LedgerDbContext db, HotFileIncident incident, CancellationToken ct)
    {
        var old = HotFileSnapshot.Parse(incident.Snapshot);
        var merges = await RefreshEvidenceAsync(db, old, ct);
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
            if (await RecoveryCorrections.HasUncertainPrAsync(db, item.Id, ct))
                RecoveryCorrections.HoldUncertainPr(incident, c);
            else
            {
                c.State = "Failed"; c.Version++;
                incident.State = "CorrectionFailed"; incident.Version++;
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
