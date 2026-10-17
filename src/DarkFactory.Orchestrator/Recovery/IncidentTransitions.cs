using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>
/// E3: every change of a hot-file incident's state (and every new generation of it) is also an append-only ledger row,
/// <see cref="Step"/>, in the same transaction as the change — Held → Assessing → Correcting → Reassessing → Resolved, or
/// Escalated, and a human re-arm's new generation. Hooked into <see cref="LedgerDbContext"/>'s save, so no writer of an
/// incident can change it without its row. The row is anchored, as the assessment rows are, on the incident's first
/// contributing merge's work item (else the oldest work item of the repository; an incident with neither has no row).
/// </summary>
internal static class IncidentTransitions
{
    public const string Step = "recovery-incident-state";

    internal static void Append(LedgerDbContext db) => AppendCore(db, id => db.WorkItems.Find(id),
        repo => db.WorkItems.OrderBy(i => i.Id).FirstOrDefault(i => i.Repo.ToLower() == repo));

    internal static async Task AppendAsync(LedgerDbContext db, CancellationToken ct)
    {
        // Resolve the anchors first (async), then append synchronously from what was found.
        var anchors = new Dictionary<long, WorkItem?>();
        var byRepo = new Dictionary<string, WorkItem?>();
        foreach (var (incident, _, _) in Changes(db))
        {
            if (AnchorOf(incident) is { } id) anchors[id] = await db.WorkItems.FindAsync([id], ct);
            else byRepo[incident.Repo] = await db.WorkItems.OrderBy(i => i.Id).FirstOrDefaultAsync(i => i.Repo.ToLower() == incident.Repo, ct);
        }
        AppendCore(db, id => anchors.GetValueOrDefault(id), repo => byRepo.GetValueOrDefault(repo));
    }

    private static void AppendCore(LedgerDbContext db, Func<long, WorkItem?> item, Func<string, WorkItem?> oldest)
    {
        foreach (var (incident, from, generation) in Changes(db).ToList())
        {
            var anchor = AnchorOf(incident) is { } id ? item(id) : oldest(incident.Repo);
            if (anchor is null) continue;
            var reason = incident.Error is null ? "" : "; " + (incident.Error.Length <= 300 ? incident.Error : incident.Error[..300] + "…");
            var detail = $"incident {(incident.Id == 0 ? "new" : incident.Id.ToString())} {incident.Repo} generation "
                + (generation is { } old && old != incident.Generation ? $"{old} → {incident.Generation}" : incident.Generation.ToString())
                + $": {from ?? "(new)"} → {incident.State}{reason}";
            // A save retried on the same context (a lost concurrency race) must not append the row twice.
            if (db.ChangeTracker.Entries<LedgerEntry>().Any(e => e.State == EntityState.Added && e.Entity.Step == Step && e.Entity.Detail == detail)) continue;
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = anchor.Id, State = anchor.State, Step = Step, Detail = detail,
                RecordedAt = DateTimeOffset.UtcNow, Outcome = StepOutcomes.Of(anchor.State, anchor.State, Step, detail) });
        }
    }

    private static IEnumerable<(HotFileIncident Incident, string? From, int? Generation)> Changes(LedgerDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<HotFileIncident>())
        {
            if (entry.State == EntityState.Added) yield return (entry.Entity, null, null);
            else if (entry.State == EntityState.Modified)
            {
                var from = entry.Property(i => i.State).OriginalValue;
                var generation = entry.Property(i => i.Generation).OriginalValue;
                if (from != entry.Entity.State || generation != entry.Entity.Generation) yield return (entry.Entity, from, generation);
            }
        }
    }

    private static long? AnchorOf(HotFileIncident incident)
    {
        // A snapshot that does not read (a seeded or damaged row) anchors on the repository instead; it never fails the save.
        try { return HotFileSnapshot.Parse(incident.Snapshot).Merges?.FirstOrDefault()?.Item; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NotSupportedException) { return null; }
    }
}
