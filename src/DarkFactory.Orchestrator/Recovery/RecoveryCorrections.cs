using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>The internal work identity and its immutable, incident-bound grant. No external board owns this key.</summary>
public sealed class RecoveryCorrection
{
    public int Id { get; set; }
    public long IncidentId { get; set; }
    public int Generation { get; set; }
    public int Attempt { get; set; }
    public required string Repo { get; set; }
    public required string Base { get; set; }
    public required string Snapshot { get; set; }
    public required string MaterialSnapshot { get; set; }
    public required string MaterialHash { get; set; }
    public required string EvidenceHash { get; set; }
    public required string Paths { get; set; }
    public required string Spec { get; set; }
    public required string State { get; set; }
    public long Version { get; set; }
}

/// <summary>Captured contributing item/plan ancestry, not a new manual control. Human controls are only read.</summary>
public sealed class RecoveryAncestor
{
    public int CorrectionId { get; set; }
    public long WorkItemId { get; set; }
    public required string Scope { get; set; }
    public RecoveryCorrection Correction { get; set; } = null!;
}

public static class RecoveryCorrections
{
    public const string Step = "recovery-correction";
    public const string StateStep = "recovery-correction-state";
    public const string RefusalStep = "recovery-correction-refused";

    internal static async Task RecordRefusalAsync(LedgerDbContext db, HotFileIncident incident, string reason, DateTimeOffset now,
        CancellationToken ct, string? externalId = null)
    {
        var own = externalId is null ? null : await db.WorkItems.SingleOrDefaultAsync(i => i.Source == ItemNaming.Recovery.Source && i.ExternalId == externalId, ct);
        var anchorId = own?.Id ?? HotFileSnapshot.Parse(incident.Snapshot).Merges[0].Item;
        var anchor = own ?? await db.WorkItems.SingleAsync(i => i.Id == anchorId, ct);
        var detail = $"incident {incident.Id} generation {incident.Generation}, {externalId ?? "materialization"}: {reason}";
        if (await db.LedgerEntries.AnyAsync(e => e.WorkItemId == anchorId && e.Step == RefusalStep && e.Detail == detail, ct)) return;
        db.LedgerEntries.Add(new() { WorkItemId = anchorId, State = anchor.State, Step = RefusalStep, Detail = detail,
            RecordedAt = now, Outcome = StepOutcomes.Of(anchor.State, anchor.State, RefusalStep, detail) });
    }

    /// <summary>Called under the incident's repository lock; grant, ancestry and incident state save atomically.</summary>
    public static async Task<RecoveryCorrection> MaterializeAsync(LedgerDbContext db, HotFileIncident incident,
        HotFileAssessment answer, GatePolicy policy, DateTimeOffset now, CancellationToken ct, int attempt = 1)
    {
        var existing = await db.RecoveryCorrections.SingleOrDefaultAsync(c => c.IncidentId == incident.Id && c.Attempt == attempt, ct);
        // Replays never revive a stopped/failed item or mint a second authority for the same attempt.
        if (existing is not null) return existing;
        if (attempt is < 1 or > 2 || attempt == 2 && !await db.RecoveryCorrections.AnyAsync(c => c.IncidentId == incident.Id
                && c.Attempt == 1 && (c.State == "Failed" || c.State == "Merged" || c.State == "Revoked"), ct))
            throw new InvalidOperationException("Correction attempt is not authorized by a failed prior repair or exceeds two attempts.");
        if (await db.RecoveryCorrections.AnyAsync(c => c.IncidentId == incident.Id && (c.State == "Stopped"
            || db.WorkItems.Any(w => w.ExternalId == "rc-" + c.Id && w.State == WorkState.Cancelled)), ct))
            throw new InvalidOperationException("A correction was stopped; no replacement repair is authorized.");
        var trigger = HotFileSnapshot.Parse(incident.Snapshot);
        var material = ValidateAssessment(incident, answer, trigger);
        await HotFileRecovery.RecordRedirectsAsync(db, incident, answer, now, ct);
        var bases = trigger.Merges.Select(m => m.Base).Distinct(StringComparer.Ordinal).ToArray();
        if (bases.Length != 1 || !material.BaseTips.ContainsKey(bases[0]) || ItemNaming.IsChangesetBranch(bases[0]))
            throw new InvalidOperationException("Correction requires one recorded delivery base and its pinned tip.");
        var paths = answer.CorrectionFindings.SelectMany(f => f.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        ValidateEnvelope(paths, policy);
        var ancestors = new Dictionary<long, WorkItem>();
        foreach (var id in trigger.Merges.Select(m => m.Item).Distinct())
        {
            long? current = id;
            var chain = new HashSet<long>();
            while (current is { } ancestorId)
            {
                if (!chain.Add(ancestorId)) throw new InvalidOperationException("Contributing plan ancestry is cyclic.");
                var item = await db.WorkItems.SingleAsync(i => i.Id == ancestorId, ct);
                if (item.State == WorkState.Cancelled) throw new InvalidOperationException("A contributing item or plan was stopped; no replacement repair is authorized.");
                ancestors[item.Id] = item;
                current = item.PlanItemId;
            }
        }
        var correction = new RecoveryCorrection
        {
            IncidentId = incident.Id, Generation = incident.Generation, Attempt = attempt, Repo = trigger.Repo, Base = bases[0],
            Snapshot = trigger.Id, MaterialSnapshot = material.Id, MaterialHash = answer.MaterialHash!, EvidenceHash = trigger.EvidenceHash,
            Paths = JsonSerializer.Serialize(paths), State = "Pending", Version = 1,
            Spec = Spec(incident, attempt, bases[0], paths, answer)
        };
        db.RecoveryCorrections.Add(correction);
        foreach (var item in ancestors.Values)
        {
            db.RecoveryAncestors.Add(new() { Correction = correction, WorkItemId = item.Id, Scope = Controls.ControlScope.Item(item.ExternalId) });
            if (ItemNaming.EpicOf(item.ExternalId, item.EpicId) is { } epic)
                db.RecoveryAncestors.Add(new() { Correction = correction, WorkItemId = item.Id, Scope = Controls.ControlScope.Epic(epic) });
            db.RecoveryAncestors.Add(new() { Correction = correction, WorkItemId = item.Id, Scope = Controls.ControlScope.Repository(RepoRef.Parse(item.Repo)) });
        }
        var anchor = ancestors[trigger.Merges[0].Item];
        db.LedgerEntries.Add(new() { WorkItemId = anchor.Id, State = anchor.State, Step = Step, RecordedAt = now,
            Detail = JsonSerializer.Serialize(new { incident = incident.Id, incident.Generation, attempt, repo = correction.Repo,
                @base = correction.Base, snapshot = correction.Snapshot, material = correction.MaterialSnapshot, paths }), Outcome = StepOutcome.Passed });
        incident.State = "Correcting";
        incident.Error = null;
        incident.Version++;
        return correction;
    }

    internal static string Spec(HotFileIncident incident, int attempt, string deliveryBase, IReadOnlyList<string> paths, HotFileAssessment answer) =>
        $"Repo: {incident.Repo}\nIncident: {incident.Id}, generation {incident.Generation}, repair attempt {attempt}.\n"
        + $"Delivery base: {deliveryBase}. Repair only these explicit paths: {string.Join(", ", paths)}.\n"
        + "Implement the bounded repair and regression tests described by this assessment data. Do not widen its envelope.\n"
        + Worker.PromptFence.Block("recovery-findings", JsonSerializer.Serialize(answer.CorrectionFindings))
        + Worker.PromptFence.Block("prior-recovery", JsonSerializer.Serialize(RecoverySnapshot.FromDetail(answer.Material)?.Evidence.Where(e => e.Category == "recovery")));

    internal static RecoverySnapshot ValidateAssessment(HotFileIncident incident, HotFileAssessment answer, HotFileSnapshot trigger, bool correction = true)
    {
        if (JsonSerializer.Serialize(answer) != incident.Answer || trigger.Repo != incident.Repo || trigger.Generation != incident.Generation
            || trigger.EvidenceHash != incident.EvidenceHash
            || (correction ? incident.State is not ("Unsafe" or "Correcting") || answer.Decision != "unsafe" : incident.State != "Assessing")
            || answer.ServedClass != "high"
            || answer.Repo != incident.Repo || answer.Generation != incident.Generation || answer.Snapshot != trigger.Id
            || answer.EvidenceHash != incident.EvidenceHash || answer.Material is null
            || RecoverySnapshot.Hash(answer.Material) != answer.MaterialHash || answer.Session != incident.Session)
            throw new InvalidOperationException("Correction assessment does not bind the current incident, session and material.");
        var material = RecoverySnapshot.FromDetail(answer.Material)
            ?? throw new InvalidOperationException("Correction material is unreadable.");
        if (!material.Complete || material.Id != answer.MaterialSnapshot || material.IncidentSnapshot != trigger.Id
            || material.Repo != incident.Repo || material.Generation != incident.Generation)
            throw new InvalidOperationException("Correction material is incomplete or belongs to another generation.");
        // Revalidate the structured decision contract, including citations/classification, rather than trusting cached projections.
        var decision = JsonSerializer.Serialize(new { version = 1, snapshot = answer.Snapshot, generation = answer.Generation,
            repo = answer.Repo, decision = answer.Decision, evidenceHash = answer.EvidenceHash, materialSnapshot = answer.MaterialSnapshot,
            citations = answer.Citations, findings = answer.Findings.Select(f => new { kind = f.Kind, blocking = f.Blocking, issue = f.Issue,
                expectedBehavior = f.ExpectedBehavior, tests = f.Tests, paths = f.Paths, citations = f.Citations, pendingItems = f.PendingItems }) });
        var parsed = RecoveryDecision.Parse(decision, trigger, material);
        if (correction && parsed.CorrectionFindings.Count == 0
            || JsonSerializer.Serialize(parsed.CorrectionFindings) != JsonSerializer.Serialize(answer.CorrectionFindings)
            || JsonSerializer.Serialize(parsed.PendingDispositions) != JsonSerializer.Serialize(answer.PendingDispositions))
            throw new InvalidOperationException("No exact validated merged-defect correction disposition.");
        return material;
    }

    internal static void ValidateEnvelope(IReadOnlyList<string> paths, GatePolicy policy)
    {
        if (paths.Count is < 1 or > 20 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count
            || paths.Any(p => string.IsNullOrEmpty(p) || RepoPath.Normalize(p) != p || p.Contains('\\')
                || p.IndexOfAny(['*', '?', '[', ']']) >= 0 || p.Split('/').Any(c => c is "." or "..")
                || policy.TierOf(p).Tier is Tier.Sealed or Tier.Protected))
            throw new InvalidOperationException("Correction envelope has an escaping, non-explicit, sealed or protected path.");
    }

    // A delivered item's bookkeeping needs no execution authority. This predicate grants no worker or merge path:
    // it applies only after the ledger's Merge transition (bookkeeping may itself be paused) and exempts only its own incident.
    internal static IQueryable<RecoveryCorrection> Delivered(LedgerDbContext db, string externalId)
    {
        int? id = ItemNaming.Recovery.TryParse(externalId, out var key) ? key : null;
        return db.RecoveryCorrections.Where(c => id != null && c.Id == id
            && db.WorkItems.Any(w => w.Source == ItemNaming.Recovery.Source && w.ExternalId == externalId
                && (w.State == WorkState.Merge || w.State == WorkState.Watch || w.State == WorkState.Paused)
                && db.LedgerEntries.Any(e => e.WorkItemId == w.Id && e.Step == null && e.State == WorkState.Merge)));
    }

    /// <summary>Structural grant predicate shared by dispatch and the effective-control query. Never vouches for another incident.</summary>
    internal static IQueryable<RecoveryCorrection> Grants(LedgerDbContext db, string externalId, string? repo = null)
    {
        int? id = ItemNaming.Recovery.TryParse(externalId, out var key) ? key : null;
        return db.RecoveryCorrections.Where(c => id != null && c.Id == id && (c.State == "Pending" || c.State == "Claimed")
            && (repo == null || c.Repo == repo)
            && !db.WorkItems.Any(w => w.Source == ItemNaming.Recovery.Source && w.ExternalId == externalId && w.State == WorkState.Cancelled)
            && db.HotFileIncidents.Any(i => i.Id == c.IncidentId && i.State == "Correcting" && i.Repo == c.Repo
                && i.Generation == c.Generation && i.EvidenceHash == c.EvidenceHash)
            && !db.HotFileIncidents.Any(i => i.Repo == c.Repo && i.State != "Resolved" && i.Id != c.IncidentId)
            && !db.RecoveryAncestors.Any(a => a.CorrectionId == c.Id
                && db.WorkItems.Any(w => w.Id == a.WorkItemId && w.State == WorkState.Cancelled)));
    }
}
