using System.Text.Json;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

public sealed record ReassessResult(bool Ok, string Message);

/// <summary>
/// The human-only re-arm of an escalated repository incident: <c>factory recovery reassess --repo owner/name</c> (the owner's
/// CLI, the existing owner trust boundary) and the dashboard incident panel's Reassess (login + antiforgery). Nothing else calls
/// it — no scheduler, intake, pipeline or recovery stage (<c>RecoveryReassessTests</c> scans the source tree).
/// <para>Design: the SAME incident (its id, redirects, ancestry and refusal records stay) moves to a NEW generation with a new
/// snapshot of its recorded contributing merges, so every authority bound to the old generation — a correction grant, an
/// assessment answer, a reservation — is void by the generation checks that already exist, and the per-generation budgets start
/// again: 0/3 assessments, 0/3 evidence refreshes and 0/2 correction attempts (a correction counts against the generation it was
/// made in). One audited ledger row (<see cref="Step"/>: who, when, incident, old and new generation) is written with it, in
/// one transaction under the repository's advisory lock. It writes no control row: a manual Pause or Stop, the legacy freeze,
/// the usage pause and every other repository's incident are untouched, and a stopped correction or contributing item still
/// refuses any replacement repair (<see cref="RecoveryCorrections.MaterializeAsync"/>). The re-armed generation is then
/// assessed by the ordinary recovery stages (<c>factory work</c>'s scheduler, or a <c>factory run</c> in that repository).</para>
/// </summary>
public sealed class RecoveryReassessment(IDbContextFactory<LedgerDbContext> contexts, IRunLocks locks, TimeProvider time)
{
    public const string Step = "recovery-reassess";

    public async Task<ReassessResult> ReassessAsync(string repository, string by, CancellationToken ct)
    {
        string repo;
        try { repo = RepoRef.Parse(repository).Key; }
        catch (ArgumentException) { return new(false, $"'{repository}' is not a repository (owner/name); nothing was written."); }
        await using var held = await locks.TryAcquireAsync(HotFileRecovery.LockKey(repo), ct);
        if (held is null)
            return new(false, $"{repo}: another process is driving this repository's recovery right now; nothing was written. Try again.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var incidents = await db.HotFileIncidents.Where(i => i.Repo == repo).OrderBy(i => i.Generation).ThenBy(i => i.Id).ToListAsync(ct);
        if (incidents.Count == 0) return new(false, $"{repo} has no hot-file recovery incident; nothing was written.");
        // Every escalated incident of the repository holds it, the newest generation or an older one: each is re-armed.
        var escalated = incidents.Where(i => i.State == "Escalated").ToList();
        if (escalated.Count == 0)
        {
            var newest = incidents[^1];
            return new(false, $"{repo} incident {newest.Id} generation {newest.Generation} is {newest.State}, not Escalated: "
                + "only an escalated incident is re-armed; nothing was written.");
        }
        var now = time.GetUtcNow();
        var details = new List<string>();
        // A generation is unique per repository: each re-armed incident takes the next one after every incident's.
        var next = incidents.Max(i => i.Generation);
        foreach (var incident in escalated) details.Add(await RearmAsync(db, repo, incident, ++next, by, now, ct));
        await db.SaveChangesAsync(ct);
        return new(true, $"{repo}: {string.Join("; ", details)}.");
    }

    private static async Task<string> RearmAsync(LedgerDbContext db, string repo, HotFileIncident incident, int generation,
        string by, DateTimeOffset now, CancellationToken ct)
    {
        var old = HotFileSnapshot.Parse(incident.Snapshot);
        var snapshot = new HotFileSnapshot(Guid.NewGuid().ToString("N"), repo, generation, old.Merges, HotFileRecovery.Hash(old.Merges));
        var was = $"incident {incident.Id} generation {incident.Generation} ({Cut(incident.Error)})";
        incident.Generation = generation;
        incident.Snapshot = JsonSerializer.Serialize(snapshot);
        incident.EvidenceHash = snapshot.EvidenceHash;
        incident.State = "Held";
        incident.Attempts = 0;
        incident.Refreshes = 0;
        incident.Answer = null;
        incident.Session = null;
        incident.Prompt = null;
        incident.NextAttemptAt = null;
        incident.Error = $"Re-armed by {by} at {now:u}: fresh assessment of generation {generation}.";
        incident.Version++;
        // Authority of the earlier generation never carries over: a correction still waiting is revoked (its item stays held).
        foreach (var stale in await db.RecoveryCorrections.Where(c => c.IncidentId == incident.Id && c.Generation < generation
                     && (c.State == "Pending" || c.State == "Claimed")).ToListAsync(ct))
        {
            stale.State = "Revoked";
            stale.Version++;
        }
        var anchorId = old.Merges.FirstOrDefault()?.Item;
        var anchor = anchorId is { } id ? await db.WorkItems.SingleOrDefaultAsync(i => i.Id == id, ct)
            : await db.WorkItems.OrderBy(i => i.Id).FirstOrDefaultAsync(i => i.Repo.ToLower() == repo, ct);
        var detail = $"{was} re-armed as generation {generation} by {by} at {now:u}: fresh assessment with fresh budgets "
            + $"(assessment 0/{HotFileRecovery.AssessmentLimit}, correction 0/2, refresh 0/{HotFileRecovery.RefreshLimit}); "
            + "manual controls, the freeze and other incidents unchanged";
        if (anchor is not null)
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = anchor.Id, State = anchor.State, Step = Step, Detail = detail,
                RecordedAt = now, Outcome = StepOutcomes.Of(anchor.State, anchor.State, Step, detail) });
        return detail;
    }

    private static string Cut(string? text) => text is null ? "no reason recorded" : text.Length <= 300 ? text : text[..300] + "…";
}
