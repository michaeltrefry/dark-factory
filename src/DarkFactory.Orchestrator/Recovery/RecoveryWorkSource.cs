using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>Ledger-only work source: model data is untrusted and every side effect stays in the ledger, never a board.</summary>
public sealed class RecoveryWorkSource(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time) : IWorkSource
{
    public ItemNaming Naming => ItemNaming.Recovery;
    public string ScopeReturnHint => "Recovery requires a current incident-bound authorization";

    public async Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var candidates = await db.RecoveryCorrections.AsNoTracking().Where(c => c.State == "Pending").OrderBy(c => c.Id).ToListAsync(ct);
        var ready = new List<int>();
        var controls = new LedgerControls(contexts, time);
        foreach (var c in candidates)
            if (await ValidAsync(db, c.Id, ct) is not null
                && await controls.EffectiveInRepositoryAsync(Naming.Format(c.Id), null, c.Repo, ct) == ControlState.Running)
                ready.Add(c.Id);
        return ready;
    }

    internal static async Task<RecoveryCorrection?> ValidAsync(LedgerDbContext db, int id, CancellationToken ct)
    {
        var correction = await RecoveryCorrections.Grants(db, ItemNaming.Recovery.Format(id)).SingleOrDefaultAsync(ct);
        if (correction is null) return null;
        var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == correction.IncidentId, ct);
        try
        {
            var answer = HotFileAssessment.FromDetail(incident.Answer) ?? throw new InvalidOperationException("Missing accepted assessment.");
            var trigger = HotFileSnapshot.Parse(incident.Snapshot);
            var material = RecoveryCorrections.ValidateAssessment(incident, answer, trigger);
            var paths = answer.CorrectionFindings.SelectMany(f => f.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (correction.Snapshot != trigger.Id || correction.MaterialSnapshot != material.Id || correction.MaterialHash != answer.MaterialHash
                || !trigger.Merges.All(m => m.Base == correction.Base) || !material.BaseTips.ContainsKey(correction.Base)
                || correction.Paths != JsonSerializer.Serialize(paths)
                || correction.Spec != RecoveryCorrections.Spec(incident, correction.Attempt, correction.Base, paths, answer)) return null;
            return correction;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException) { return null; }
    }

    public async Task<ClaimResult> AuthorizeDispatchAsync(int id, RepoRef repository, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var c = await ValidAsync(db, id, ct);
        if (c is not null && RepoRef.Parse(c.Repo).Is(repository)) return ClaimResult.Ok;
        const string reason = "Correction repository, snapshot or repair envelope does not match its incident grant.";
        if (await db.RecoveryCorrections.SingleOrDefaultAsync(c => c.Id == id, ct) is { } refused)
        {
            var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == refused.IncidentId, ct);
            await RecoveryCorrections.RecordRefusalAsync(db, incident, reason, time.GetUtcNow(), ct, Naming.Format(id));
            await db.SaveChangesAsync(ct);
        }
        return ClaimResult.Refused(reason);
    }

    public async Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var c = await ValidAsync(db, id, ct);
        if (c is null) return ClaimResult.Refused("Recovery grant is stale, stopped, or outside its recorded envelope.");
        if (await new LedgerControls(contexts, time).EffectiveInRepositoryAsync(Naming.Format(id), null, c.Repo, ct) != ControlState.Running)
            return ClaimResult.Refused("Recovery is held by an inherited control or another incident.");
        if (c.State == "Pending")
        {
            c.State = "Claimed"; c.Version++;
            await db.SaveChangesAsync(ct);
        }
        return ClaimResult.Ok;
    }
    public async Task<bool> InScopeAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await ValidAsync(db, id, ct) is not null;
    }
    public Task ValidateScopeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ReleaseAsync(int id, CancellationToken ct) => Task.CompletedTask;
    public async Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Stop bookkeeping must still read a settled item. Execution/claim separately require a valid grant.
        var c = await db.RecoveryCorrections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException($"No recovery item {Naming.Format(id)} exists.");
        return new(new WorkStory(id, $"Repair hot-file incident {c.IncidentId} (attempt {c.Attempt})", c.Spec, "bug", "", Naming), null, []);
    }
    public async Task<string?> DeliveryBaseAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await ValidAsync(db, id, ct) ?? throw new InvalidOperationException("Recovery grant is invalid.")).Base;
    }
    public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => UpdateAsync(id,
        state switch { BoardState.Claimed => "Claimed", BoardState.Stopped => "Stopped", _ => "Merged" }, comment, ct);
    private async Task UpdateAsync(int id, string state, string? comment, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var c = await db.RecoveryCorrections.SingleAsync(c => c.Id == id, ct);
        if (c.State == "Stopped" || c.State == state) return;
        c.State = state; c.Version++;
        if (state == "Stopped")
        {
            var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == c.IncidentId, ct);
            incident.State = "Escalated"; incident.Error = "Correction stopped; repository remains held. No automated replacement is authorized.";
            incident.Version++;
        }
        await AppendAsync(db, c, RecoveryCorrections.StateStep, state + (comment is null ? "" : ": " + comment), ct);
        await db.SaveChangesAsync(ct);
    }
    public Task CommentAsync(int id, string text, CancellationToken ct) => RecordAsync(id, "recovery-comment", text, ct);
    public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => RecordAsync(id, "recovery-links", JsonSerializer.Serialize(urls), ct);
    private async Task RecordAsync(int id, string step, string detail, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var c = await db.RecoveryCorrections.SingleAsync(c => c.Id == id, ct);
        await AppendAsync(db, c, step, detail, ct);
        await db.SaveChangesAsync(ct);
    }
    private async Task AppendAsync(LedgerDbContext db, RecoveryCorrection c, string step, string detail, CancellationToken ct)
    {
        var item = await db.WorkItems.SingleOrDefaultAsync(i => i.Source == Naming.Source && i.ExternalId == Naming.Format(c.Id), ct);
        // Before pipeline intake, use a contributing item's append-only ledger, never synthesize a board item.
        item ??= await db.WorkItems.SingleAsync(i => i.Id == db.RecoveryAncestors.Where(a => a.CorrectionId == c.Id).Select(a => a.WorkItemId).Min(), ct);
        if (await db.LedgerEntries.AnyAsync(e => e.WorkItemId == item.Id && e.Step == step && e.Detail == detail, ct)) return;
        db.LedgerEntries.Add(new() { WorkItemId = item.Id, State = item.State, Step = step, Detail = detail, RecordedAt = time.GetUtcNow(),
            Outcome = StepOutcomes.Of(item.State, item.State, step, detail) });
    }
    public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
        throw new NotSupportedException("A recovery item cannot plan children.");
}
