using System.Globalization;
using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

public sealed record RecoveryStatusReport(DateTimeOffset ReadAt, IReadOnlyList<RepositoryRecoveryStatus> Repositories, string? Note = null);
public sealed record CorrectionStatus(string Item, int Attempt, string State, string? PullRequest);
public sealed record RepositoryRecoveryStatus(long Incident, string Repo, int Generation, string Stage, string Hold,
    string Attempts, string Evidence, string Assessment, string? Blocker, string Remedy,
    IReadOnlyList<string> HotFiles, IReadOnlyList<string> HeldItems, IReadOnlyList<string> Redirects,
    IReadOnlyList<CorrectionStatus> Corrections, IReadOnlyList<string> Controls);

/// <summary>Read-only operator projection. Never captures evidence, contacts a board, dispatches recovery or writes controls.
/// A read is an observation, not authorization: evidence freshness is deliberately never inferred from wall-clock age.</summary>
public sealed class RecoveryStatusReader(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time)
{
    public const int MaxRepositories = 100;
    private static readonly WorkState[] Finished = [WorkState.Done, WorkState.Cancelled, WorkState.Watch];
    internal static string Text(string? text, int limit = 600) => UntrustedText.Inert(text is null ? "unavailable" : text.Length > limit ? text[..limit] + "… [cut]" : text);

    public async Task<RecoveryStatusReport> ReadAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = db.HotFileIncidents.GroupBy(i => i.Repo).Select(g => g.Max(i => i.Id));
        var incidents = await db.HotFileIncidents.AsNoTracking().Where(i => ids.Contains(i.Id)).OrderBy(i => i.Repo)
            .Take(MaxRepositories + 1).ToListAsync(ct);
        var truncated = incidents.Count > MaxRepositories;
        var rows = new List<RepositoryRecoveryStatus>();
        foreach (var incident in incidents.Take(MaxRepositories))
        {
            var trigger = Trigger(incident);
            var answer = HotFileAssessment.FromDetail(incident.Answer);
            var material = RecoverySnapshot.FromDetail(answer?.Material);
            var valid = trigger is not null && answer?.Snapshot == trigger.Id && answer.Generation == incident.Generation
                && answer.Repo == incident.Repo && answer.EvidenceHash == incident.EvidenceHash
                && material?.IncidentSnapshot == trigger.Id && material.Id == answer.MaterialSnapshot
                && material.Repo == incident.Repo && material.Generation == incident.Generation;
            var corrections = await db.RecoveryCorrections.AsNoTracking().Where(c => c.IncidentId == incident.Id)
                .OrderBy(c => c.Attempt).Take(11).ToListAsync(ct);
            var work = await db.WorkItems.AsNoTracking().Where(w => w.Repo.ToLower() == incident.Repo.ToLower() && !Finished.Contains(w.State))
                .OrderBy(w => w.Id).Take(RecoverySnapshot.MaxPending + 1).ToListAsync(ct);
            var correcting = corrections.Select(c => ItemNaming.Recovery.Format(c.Id)).ToArray();
            var correctionItems = await db.WorkItems.AsNoTracking().Where(w => w.Source == ItemNaming.Recovery.Source && correcting.Contains(w.ExternalId)).ToListAsync(ct);
            var correctionIds = correctionItems.Select(w => w.Id).ToArray();
            var links = await db.LedgerEntries.AsNoTracking().Where(e => correctionIds.Contains(e.WorkItemId) && e.Step == RunPipeline.Steps.Linked)
                .GroupBy(e => e.WorkItemId).Select(g => g.OrderByDescending(e => e.Id).First()).ToListAsync(ct);
            var failures = await db.LedgerEntries.AsNoTracking().Where(e => correctionIds.Contains(e.WorkItemId) && e.Step == null)
                .GroupBy(e => e.WorkItemId).Select(g => g.OrderByDescending(e => e.Id).First()).ToListAsync(ct);
            var correctionViews = corrections.Select(c =>
            {
                var item = correctionItems.FirstOrDefault(w => w.ExternalId == ItemNaming.Recovery.Format(c.Id));
                var link = links.FirstOrDefault(e => e.WorkItemId == item?.Id)?.Detail;
                var failure = failures.FirstOrDefault(e => e.WorkItemId == item?.Id && e.State is WorkState.Escalated or WorkState.Cancelled);
                return new CorrectionStatus(ItemNaming.Recovery.Format(c.Id), c.Attempt, Text(c.State) + (item is null ? "; work item unavailable" : $"; {item.State}")
                    + (failure is null ? "" : "; blocker (ledger row " + failure.Id + "): " + Text(failure.Detail)), Pull(link?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), incident.Repo));
            }).ToArray();
            string[] redirects;
            try { redirects = (await RecoveryRedirect.LatestAsync(db, incident, ct))
                .Select(r => $"{Text(r.Item)}: {(r.Commit is null ? "waiting for delivery" : "delivered " + Text(r.Commit))}; {Text(r.Context)}").ToArray(); }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException) { redirects = ["affected-work dispositions unavailable"]; }
            var scopes = new HashSet<string> { ControlScope.Factory, ControlScope.Repository(RepoRef.Parse(incident.Repo)), ControlScope.Freeze, ControlScope.Usage };
            foreach (var item in work.Concat(correctionItems))
            {
                scopes.Add(ControlScope.Item(item.ExternalId));
                if (ItemNaming.EpicOf(item.ExternalId, item.EpicId) is { } epic) scopes.Add(ControlScope.Epic(epic));
            }
            var contributorIds = trigger?.Merges.Select(m => m.Item).ToArray() ?? [];
            foreach (var contributor in await db.WorkItems.AsNoTracking().Where(w => contributorIds.Contains(w.Id)).ToListAsync(ct))
            {
                scopes.Add(ControlScope.Item(contributor.ExternalId));
                if (ItemNaming.EpicOf(contributor.ExternalId, contributor.EpicId) is { } epic) scopes.Add(ControlScope.Epic(epic));
            }
            var ancestors = await db.RecoveryAncestors.AsNoTracking().Where(a => corrections.Select(c => c.Id).Contains(a.CorrectionId)).Select(a => a.Scope).ToListAsync(ct);
            scopes.UnionWith(ancestors);
            var planIds = work.Where(w => w.PlanItemId != null).Select(w => w.PlanItemId!.Value).Distinct().ToArray();
            foreach (var plan in await db.WorkItems.AsNoTracking().Where(w => planIds.Contains(w.Id)).ToListAsync(ct)) scopes.Add(ControlScope.Item(plan.ExternalId));
            // Read controls after the incident and its dependents. A resolution can never synthesize Running or clear a Pause.
            var controls = await db.Controls.AsNoTracking().Where(c => scopes.Contains(c.Scope)).ToListAsync(ct);
            var heldControls = controls.Where(c => c.PausesAt(time.GetUtcNow()) || c.State == ControlState.Stopping)
                .Select(c => $"{Text(c.Scope)}: {(c.Scope is ControlScope.Freeze or ControlScope.Usage ? "automatic control" : "human Pause / Stop")} {c.State}; {Text(c.Reason)}; by {Text(c.ChangedBy)} at {c.ChangedAt:u}").ToArray();
            var current = await db.HotFileIncidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id, ct);
            var raced = current.Version != incident.Version || current.State != incident.State || current.Snapshot != incident.Snapshot;
            var hold = raced ? "state changed during read; refresh required (not authorization)"
                : incident.State == "Resolved" ? "automatic hold resolved; " + (heldControls.Length > 0 ? "human or other controls still hold work" : "manual controls checked at observation time only")
                : "automatic repository hold; ordinary work remains held";
            var evidence = valid
                ? $"snapshot {Text(material!.Id)}; hash {Text(answer!.MaterialHash)}; " + (material.Complete ? "complete" : "incomplete: " + string.Join("; ", material.Missing.Select(m => Text(m.Category) + ": " + Text(m.Reason))))
                    + "; base tips " + string.Join(", ", material.BaseTips.Select(t => Text(t.Key) + "@" + Text(t.Value)))
                : "snapshot / assessment evidence unavailable or not bound to this generation";
            evidence += "; ledger provenance only, not live-validated";
            var assessment = answer is null ? "assessment unavailable; cost N/A"
                : $"{Text(answer.Decision)}; session {Text(answer.Session ?? incident.Session)}; class {Text(answer.ServedClass)}; prompt {Text(answer.Prompt ?? incident.Prompt)}; cost "
                    + (answer.CostUsd is { } cost ? cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")) : "N/A");
            if (valid && answer!.Findings.Count > 0) assessment += "; validated findings: " + string.Join("; ", answer.Findings.Take(10)
                .Select(f => Text(f.Kind) + ": " + Text(f.Issue) + "; expected " + Text(f.ExpectedBehavior)));
            var attemptRows = await db.RecoveryAssessmentAttempts.AsNoTracking().Where(a => a.IncidentId == incident.Id)
                .OrderByDescending(a => a.Id).Select(a => new { a.Snapshot, a.Number, a.State, a.LeaseUntil }).Take(12).ToListAsync(ct);
            if (attemptRows.Count > 0) assessment += "; last reservations: " + string.Join("; ", attemptRows.Select(a => $"{Text(a.Snapshot)} #{a.Number} {Text(a.State)} lease until {a.LeaseUntil:u}"));
            rows.Add(new(incident.Id, incident.Repo, incident.Generation, Text(incident.State), hold,
                $"assessment {incident.Attempts}/{HotFileRecovery.AssessmentLimit}, correction {corrections.Select(c => c.Attempt).DefaultIfEmpty().Max()}/2, refresh {incident.Refreshes}/{HotFileRecovery.RefreshLimit}",
                evidence, assessment, incident.Error is null ? null : Text(incident.Error),
                incident.State == "Escalated" ? $"Named human remedy: inspect the blocker and correction PR, fix the named evidence/gate/environment problem. Continue does not reset exhausted budgets or resolve an incident. To cancel affected work: factory stop --repo {incident.Repo}. No automatic retry after exhaustion."
                    : "Only a human Continue of the named manual scope clears its Pause; it does not resolve an automatic incident.",
                trigger?.Merges.SelectMany(m => m.Files.Select(f => Text(m.Base) + ": " + Text(f))).Distinct().Take(101).ToArray() ?? ["hot files unavailable"],
                work.Take(RecoverySnapshot.MaxPending).Where(w => w.Source != ItemNaming.Recovery.Source).Select(w => $"{Text(w.ExternalId)} ({w.State})").Concat(work.Count > RecoverySnapshot.MaxPending ? ["more items not shown"] : []).ToArray(),
                redirects, correctionViews, heldControls));
        }
        return new(time.GetUtcNow(), rows, truncated ? $"More than {MaxRepositories} repositories; remaining recovery rows not shown." : null);
    }

    private static HotFileSnapshot? Trigger(HotFileIncident incident)
    {
        try
        {
            var trigger = HotFileSnapshot.Parse(incident.Snapshot);
            return trigger.Repo == incident.Repo && trigger.Generation == incident.Generation && trigger.EvidenceHash == incident.EvidenceHash && trigger.Merges is not null ? trigger : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    internal static string? Pull(string? link, string repo) => Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/" + repo + "/pull/", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(uri.AbsolutePath[(repo.Length + 7)..], out var number) && number > 0 ? uri.AbsoluteUri : null;
}
