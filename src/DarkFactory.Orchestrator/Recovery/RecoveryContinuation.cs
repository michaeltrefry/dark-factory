using System.Text.Json;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using DarkFactory.Orchestrator.Controls;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>Append-only affected-work decisions. Their identity is incident + item; they are evidence, never human controls.</summary>
public sealed record RecoveryRedirect(long Incident, string Item, string Repo, string Base, string? Commit, string Context)
{
    public const string Step = "recovery-redirect";
    public const string Started = "recovery-restarted";
    public static RecoveryRedirect? Parse(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<RecoveryRedirect>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed partial class HotFileRecovery
{
    // Called under the repository lock. Merge observation alone never resolves a hold. It creates new immutable assessment
    // input, including the correction's executed results and pending dispositions, before dispatching a fresh session.
    private async Task RefreshAfterCorrectionAsync(LedgerDbContext db, HotFileIncident incident, CancellationToken ct)
    {
        var correction = await db.RecoveryCorrections.Where(c => c.IncidentId == incident.Id).OrderByDescending(c => c.Attempt).FirstOrDefaultAsync(ct);
        if (correction is null || correction.State is not ("Merged" or "Failed")) return;
        var item = await db.WorkItems.SingleOrDefaultAsync(i => i.Source == ItemNaming.Recovery.Source && i.ExternalId == ItemNaming.Recovery.Format(correction.Id), ct);
        if (item is null) return;
        var rows = await db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
        var old = HotFileSnapshot.Parse(incident.Snapshot);
        var merge = rows.LastOrDefault(e => e.Step == null && e.State == WorkState.Merge);
        if (correction.State == "Merged" && (merge is null || !rows.Any(e => e.Step == "recovery-delivered"))) return;
        if (merge is not null && old.Merges.Any(m => m.Row == merge.Id)) return;
        if (correction.State == "Failed" && incident.State != "Escalated") return;
        var merges = old.Merges.ToList();
        if (merge is not null)
        {
            var files = MergeFiles.FromDetail(rows.LastOrDefault(e => e.Step == RunPipeline.Steps.MergeFiles && e.Id < merge.Id)?.Detail);
            if (files is null || files.Base != correction.Base || merge.Detail is null)
                throw new InvalidOperationException("Delivered correction has no exact base/merge provenance.");
            merges.Add(new(merge.Id, item.Id, merge.Detail, files.Base, files.Files));
        }
        var snapshot = new HotFileSnapshot(Guid.NewGuid().ToString("N"), incident.Repo, incident.Generation, merges, Hash(merges));
        incident.Snapshot = JsonSerializer.Serialize(snapshot);
        incident.EvidenceHash = snapshot.EvidenceHash;
        incident.State = "Reassessing";
        incident.Attempts = 0;
        incident.NextAttemptAt = null;
        incident.Answer = null;
        incident.Version++;
        if (merge is not null)
        {
            // A newer delivery updates the same incident/item disposition; an old snapshot can never release it.
            var redirects = await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Step).OrderBy(e => e.Id).ToListAsync(ct);
            foreach (var row in redirects.Select(e => (Row: e, Value: RecoveryRedirect.Parse(e.Detail)))
                .Where(x => x.Value?.Incident == incident.Id).GroupBy(x => x.Value!.Item).Select(g => g.Last()))
            {
                var value = row.Value! with { Commit = merge.Detail };
                db.LedgerEntries.Add(new() { WorkItemId = row.Row.WorkItemId, State = row.Row.State, Step = RecoveryRedirect.Step,
                    Detail = JsonSerializer.Serialize(value), RecordedAt = time.GetUtcNow(), Outcome = StepOutcome.Passed });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    internal static async Task RecordRedirectsAsync(LedgerDbContext db, HotFileIncident incident, HotFileAssessment answer, DateTimeOffset now, CancellationToken ct)
    {
        var material = RecoverySnapshot.FromDetail(answer.Material);
        if (material is not { Complete: true }) return;
        var trigger = HotFileSnapshot.Parse(incident.Snapshot);
        var bases = trigger.Merges.Select(m => m.Base).Distinct().ToArray();
        if (bases.Length != 1) throw new InvalidOperationException("Redirect requires one exact delivery base.");
        // Conservatively restart every pending coding item. Unpublished edits cannot prove non-overlap; no old conversation
        // is retained merely because a reviewer did not name it. Plan parents are evaluated, never implemented.
        foreach (var evidence in material.Evidence.Where(e => e.Category == "pending"))
        {
            var externalId = RecoveryDecision.PendingItem(evidence)!;
            var item = await db.WorkItems.SingleOrDefaultAsync(i => i.ExternalId == externalId, ct);
            if (item is null || item.State is WorkState.Planned or WorkState.Plan or WorkState.Merge or WorkState.Watch
                or WorkState.Done or WorkState.Cancelled || item.Source == ItemNaming.Recovery.Source) continue;
            var context = Worker.PromptFence.Block("recovery-context", JsonSerializer.Serialize(answer.Findings));
            var redirect = new RecoveryRedirect(incident.Id, externalId, incident.Repo, bases[0],
                trigger.Merges.LastOrDefault(m => db.WorkItems.Any(w => w.Id == m.Item && w.Source == ItemNaming.Recovery.Source))?.Commit, context);
            var detail = JsonSerializer.Serialize(redirect);
            if (await db.LedgerEntries.AnyAsync(e => e.WorkItemId == item.Id && e.Step == RecoveryRedirect.Step && e.Detail == detail, ct)) continue;
            db.LedgerEntries.Add(new() { WorkItemId = item.Id, State = item.State, Step = RecoveryRedirect.Step, Detail = detail,
                RecordedAt = now, Outcome = StepOutcome.Deferred });
        }
    }

    /// <summary>Only a safe, current incident can hand a disposition to a run. The caller still composes every control.</summary>
    public async Task<RecoveryRedirect?> RedirectAsync(string externalId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RecoveryRedirect.Step
            && db.WorkItems.Any(w => w.Id == e.WorkItemId && w.ExternalId == externalId)).OrderBy(e => e.Id).ToListAsync(ct);
        var redirect = rows.Select(e => RecoveryRedirect.Parse(e.Detail)).LastOrDefault();
        if (redirect is null) return null;
        await using var repoLock = await locks.TryAcquireAsync(LockKey(redirect.Repo), ct);
        if (repoLock is null) return null;
        var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == redirect.Incident, ct);
        if (incident.State != "Resolved") return null;
        var detail = JsonSerializer.Serialize(redirect);
        if (!await db.LedgerEntries.AnyAsync(e => e.Step == RecoveryRedirect.Started && e.Detail == detail, ct))
        {
            if (capture is null) throw new InvalidOperationException("Redirect requires current pinned recovery evidence.");
            var answer = HotFileAssessment.FromDetail(incident.Answer);
            if (answer?.Material is null || await capture(HotFileSnapshot.Parse(incident.Snapshot), ct) != answer.Material)
            {
                incident.State = "Held";
                incident.Error = "Safe redirect evidence changed before continuation; fresh assessment required.";
                incident.Version++;
                await db.SaveChangesAsync(ct);
                return null;
            }
        }
        return redirect;
    }
}
