using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal static string Prefix(long incident) => $"{{\"Incident\":{incident},";
    internal static string ItemMark(string item) => $",\"Item\":{JsonSerializer.Serialize(item)},";
    // Group on the code-written identity prefix in SQL; never materialize historical generations or repositories.
    internal static async Task<RecoveryRedirect[]> LatestAsync(LedgerDbContext db, HotFileIncident incident, CancellationToken ct)
    {
        var prefix = Prefix(incident.Id);
        var scoped = db.LedgerEntries.AsNoTracking().Where(e => e.Step == Step && e.Detail != null && e.Detail.StartsWith(prefix));
        var latest = scoped.GroupBy(e => e.Detail!.Substring(0, e.Detail.IndexOf(",\"Repo\":")))
            .Select(g => g.Max(e => e.Id));
        var details = await scoped.Where(e => latest.Contains(e.Id)).OrderBy(e => e.Id)
            .Select(e => e.Detail).Take(RecoverySnapshot.MaxPending + 1).ToListAsync(ct);
        if (details.Count > RecoverySnapshot.MaxPending) throw new InvalidOperationException("Too many affected-work dispositions.");
        return details.Select(d => Parse(d) ?? throw new InvalidOperationException("Unreadable disposition.")).ToArray();
    }
    public static RecoveryRedirect? Parse(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<RecoveryRedirect>(json); }
        catch (JsonException) { return null; }
    }
}

public sealed class RecoveryContinuationDeferredException(string message) : Exception(message);

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
        if (correction.State == "Failed" && (incident.State is not ("Escalated" or "CorrectionFailed") || rows.Any(e => e.Step == "recovery-reassessment"))) return;
        // A failure of an earlier generation was decided before a human re-armed the incident; it spends nothing now.
        if (correction.State == "Failed" && correction.Generation != incident.Generation) return;
        if (correction.State == "Failed"
            && correction.Attempt - await RecoveryCorrections.BaselineAsync(db, incident, ct) >= RecoveryCorrections.CorrectionLimit)
        {
            Escalate(incident, "Correction budget exhausted");
            await db.SaveChangesAsync(ct);
            return;
        }
        var merges = old.Merges.ToList();
        if (merge is not null)
        {
            var files = MergeFiles.FromDetail(rows.LastOrDefault(e => e.Step == RunPipeline.Steps.MergeFiles && e.Id < merge.Id)?.Detail);
            if (files is null || files.Base != correction.Base || merge.Detail is null)
                throw new InvalidOperationException("Delivered correction has no exact base/merge provenance.");
            merges.Add(new(merge.Id, item.Id, merge.Detail, files.Base, files.Files));
        }
        if (!await RefreshAsync(db, incident, merges, $"Correction attempt {correction.Attempt} observed", ct)) return;
        var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
        db.LedgerEntries.Add(new() { WorkItemId = item.Id, State = item.State, Step = "recovery-reassessment",
            Detail = $"incident {incident.Id}: correction attempt {correction.Attempt} observed for snapshot {snapshot.Id}",
            RecordedAt = time.GetUtcNow(), Outcome = StepOutcome.Passed });
        if (merge is not null)
        {
            // A newer delivery updates the same incident/item disposition; an old snapshot can never release it.
            foreach (var value in await RecoveryRedirect.LatestAsync(db, incident, ct))
            {
                var mark = RecoveryRedirect.ItemMark(value.Item);
                var prefix = RecoveryRedirect.Prefix(incident.Id);
                var row = await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Step && e.Detail != null
                    && e.Detail.StartsWith(prefix) && e.Detail.Contains(mark)).OrderByDescending(e => e.Id).FirstAsync(ct);
                db.LedgerEntries.Add(new() { WorkItemId = row.WorkItemId, State = row.State, Step = RecoveryRedirect.Step,
                    Detail = JsonSerializer.Serialize(value with { Commit = merge.Detail }), RecordedAt = time.GetUtcNow(), Outcome = StepOutcome.Passed });
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
        var mergeItems = trigger.Merges.Select(m => m.Item).Distinct().ToArray();
        var recoveryItems = await db.WorkItems.Where(w => mergeItems.Contains(w.Id) && w.Source == ItemNaming.Recovery.Source)
            .Select(w => w.Id).ToListAsync(ct);
        var correctionCommit = trigger.Merges.LastOrDefault(m => recoveryItems.Contains(m.Item))?.Commit;
        // Conservatively restart every pending coding item. Unpublished edits cannot prove non-overlap; no old conversation
        // is retained merely because a reviewer did not name it. Plan parents are evaluated, never implemented.
        foreach (var evidence in material.Evidence.Where(e => e.Category == "pending"))
        {
            var externalId = RecoveryDecision.PendingItem(evidence)!;
            if (answer.Decision == "safe")
            {
                using var pending = JsonDocument.Parse(evidence.Content);
                if (!pending.RootElement.TryGetProperty("requiredDiscardRestart", out var discard) || !discard.GetBoolean()) continue;
                var prefix = RecoveryRedirect.Prefix(incident.Id);
                var mark = RecoveryRedirect.ItemMark(externalId);
                if (await db.LedgerEntries.AnyAsync(e => e.Step == RecoveryRedirect.Step && e.Detail != null
                    && e.Detail.StartsWith(prefix) && e.Detail.Contains(mark), ct)) continue;
            }
            var item = await db.WorkItems.SingleOrDefaultAsync(i => i.ExternalId == externalId, ct);
            if (item is not null && (item.State is WorkState.Planned or WorkState.Plan or WorkState.Merge or WorkState.Watch
                or WorkState.Done or WorkState.Cancelled || item.Source == ItemNaming.Recovery.Source)) continue;
            var anchor = item ?? await db.WorkItems.SingleAsync(w => w.Id == trigger.Merges[0].Item, ct);
            var context = Worker.PromptFence.Block("recovery-context", JsonSerializer.Serialize(answer.Findings));
            var redirect = new RecoveryRedirect(incident.Id, externalId, incident.Repo, bases[0],
                correctionCommit, context);
            var detail = JsonSerializer.Serialize(redirect);
            if (await db.LedgerEntries.AnyAsync(e => e.WorkItemId == anchor.Id && e.Step == RecoveryRedirect.Step && e.Detail == detail, ct)) continue;
            db.LedgerEntries.Add(new() { WorkItemId = anchor.Id, State = anchor.State, Step = RecoveryRedirect.Step, Detail = detail,
                RecordedAt = now, Outcome = StepOutcome.Deferred });
        }
    }

    /// <summary>
    /// Only a safe, current incident can hand a not-yet-consumed disposition to a run. The caller still composes every control.
    /// <para>A disposition the item has already acted on (its exact <see cref="RecoveryRedirect.Started"/> row exists) is
    /// returned as recorded, with no capture, no lock and no write: the restarted attempt needs its commit and context, and
    /// later evidence (another item's merge moving the base, a pending item advancing) is not about whether that restart was
    /// authorized. One the item has outgrown without restarting (it is merged, finished or a plan coordinator now) is void:
    /// null. One whose incident is no longer the repository's newest, while that newest incident is resolved, is returned
    /// without capture too: the newer safe assessment saw this item pending, and a fresh restart from the current base only
    /// discards work. Only the newest incident's own unconsumed disposition is revalidated against fresh evidence, and only
    /// then may a mismatch send that incident back to Refreshing (a fresh assessment, bounded by the refresh budget): an old
    /// incident is never reopened, so a repository is never held by a generation nothing advances.</para>
    /// </summary>
    public async Task<RecoveryRedirect?> RedirectAsync(string externalId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var mark = RecoveryRedirect.ItemMark(externalId);
        var row = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RecoveryRedirect.Step
            && e.Detail != null && e.Detail.Contains(mark)).OrderByDescending(e => e.Id)
            .Select(e => new { e.Detail }).FirstOrDefaultAsync(ct);
        var redirect = RecoveryRedirect.Parse(row?.Detail);
        if (redirect is null || redirect.Item != externalId) return null;
        if (await db.LedgerEntries.AsNoTracking().AnyAsync(e => e.Step == RecoveryRedirect.Started && e.Detail == row!.Detail, ct))
            return redirect;
        var state = await db.WorkItems.AsNoTracking().Where(i => i.ExternalId == externalId && i.Source != ItemNaming.Recovery.Source)
            .Select(i => (WorkState?)i.State).FirstOrDefaultAsync(ct);
        if (state is WorkState.Merge or WorkState.Watch or WorkState.Done or WorkState.Cancelled or WorkState.Planned or WorkState.Plan)
            return null;
        await using var repoLock = await locks.TryAcquireAsync(LockKey(redirect.Repo), ct);
        if (repoLock is null) throw new RecoveryContinuationDeferredException("Required redirect is temporarily unverifiable: repository lock is held.");
        var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == redirect.Incident, ct);
        if (incident.State != "Resolved") throw new RecoveryContinuationDeferredException("Required redirect awaits a safe current assessment.");
        var newest = await db.HotFileIncidents.AsNoTracking().Where(i => i.Repo == incident.Repo)
            .OrderByDescending(i => i.Generation).ThenByDescending(i => i.Id).FirstAsync(ct);
        if (newest.Id != incident.Id)
        {
            if (newest.State == "Resolved") return redirect;
            throw new RecoveryContinuationDeferredException("Required redirect awaits the repository's newer incident.");
        }
        try
        {
            if (capture is null) throw new InvalidOperationException("Redirect requires current pinned recovery evidence.");
            var answer = HotFileAssessment.FromDetail(incident.Answer);
            if (answer?.Material is null || !await ContinuationMatchesAsync(db, incident, answer.Material,
                await capture(HotFileSnapshot.Parse(incident.Snapshot), ct), ct))
                throw new InvalidOperationException("Safe redirect evidence changed before continuation; fresh assessment required.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            incident.State = "Refreshing";
            incident.Error = "Required redirect is not currently authorized: " + ex.Message;
            incident.Version++;
            await db.SaveChangesAsync(ct);
            throw new RecoveryContinuationDeferredException(incident.Error);
        }
        return redirect;
    }
    // Durable per-item consumption admits only ledgered continuation progress. Source requirements, relationships,
    // delivery tips and merged evidence are still compared on every authorization, including after a slot wait.
    private static async Task<bool> ContinuationMatchesAsync(LedgerDbContext db, HotFileIncident incident,
        string baseline, string current, CancellationToken ct)
    {
        if (baseline == current) return true;
        var before = RecoverySnapshot.FromDetail(baseline);
        var after = RecoverySnapshot.FromDetail(current);
        if (before is not { Complete: true } || after is not { Complete: true }
            || before.IncidentSnapshot != after.IncidentSnapshot || before.Repo != after.Repo
            || before.Generation != after.Generation
            || JsonSerializer.Serialize(before.BaseTips) != JsonSerializer.Serialize(after.BaseTips)) return false;
        var prefix = RecoveryRedirect.Prefix(incident.Id);
        var consumed = await db.LedgerEntries.AsNoTracking().Where(e => e.Step == RecoveryRedirect.Started
            && e.Detail != null && e.Detail.StartsWith(prefix)).Select(e => e.Detail).Distinct().Take(RecoverySnapshot.MaxPending + 1).ToListAsync(ct);
        if (consumed.Count > RecoverySnapshot.MaxPending) return false;
        var redirects = await RecoveryRedirect.LatestAsync(db, incident, ct);
        var remaining = after.Evidence.ToList();
        foreach (var old in before.Evidence)
        {
            var next = remaining.FirstOrDefault(e => e.Id == old.Id);
            if (old.Category == "pending") next ??= remaining.FirstOrDefault(e => e.Category == "pending" && RecoveryDecision.PendingItem(e) == RecoveryDecision.PendingItem(old));
            next ??= remaining.FirstOrDefault(e => e.Category == old.Category && e.Content == old.Content);
            if (next is null) return false;
            remaining.Remove(next);
            if (old == next) continue;
            if (old.Category != "pending")
            {
                if (old.Category == "spec" && old.Content == next.Content) continue;
                return false;
            }
            var item = RecoveryDecision.PendingItem(old);
            if (item is null || RecoveryDecision.PendingItem(next) != item) return false;
            var redirect = redirects.SingleOrDefault(d => d.Item == item);
            var started = redirect is not null && consumed.Contains(JsonSerializer.Serialize(redirect));
            var a = JsonNode.Parse(old.Content)!.AsObject();
            var b = JsonNode.Parse(next.Content)!.AsObject();
            if (a["rows"] is not JsonArray ar || b["rows"] is not JsonArray br) return false;
            if (br.Count < ar.Count || ar.Where((r, n) => !JsonNode.DeepEquals(r, br[n])).Any()) return false;
            var added = br.Skip(ar.Count).ToArray();
            var newlyClaimed = a["Id"]?.GetValue<long>() == 0;
            if (!started && added.Any(r => newlyClaimed
                ? r?["Step"]?.GetValue<string>() is not (null or RunPipeline.Steps.Claimed) || r?["State"]?.GetValue<int>() is not ((int)WorkState.Intake or (int)WorkState.Implement)
                : r?["Step"] is not null || r?["Detail"]?.GetValue<string>() != "unpaused")) return false;
            if (!newlyClaimed && a["Version"] is { } av && b["Version"] is { } bv
                && bv.GetValue<long>() != av.GetValue<long>() + added.Length + (started ? 1 : 0)) return false;
            if (started)
            {
                var pushed = added.LastOrDefault(r => r?["Step"]?.GetValue<string>() == RunPipeline.Steps.Pushed)?["Detail"]?.GetValue<string>();
                if (b["head"] is { } head && pushed is not null && head.GetValue<string>() != pushed) return false;
                foreach (var key in new[] { "head", "headState", "requiredDiscardRestart", "pull" }) { a.Remove(key); b.Remove(key); }
            }
            if (newlyClaimed) { a.Remove("Id"); b.Remove("Id"); a.Remove("EpicId"); b.Remove("EpicId"); }
            foreach (var key in new[] { "rows", "Version", "State" }) { a.Remove(key); b.Remove(key); }
            if (!JsonNode.DeepEquals(a, b)) return false;
        }
        return remaining.All(e => e.Category == "disposition" && redirects.Any(d => e.Content == JsonSerializer.Serialize(d)));
    }

}
