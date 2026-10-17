using System.Globalization;
using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

public sealed record RecoveryStatusReport(DateTimeOffset ReadAt, IReadOnlyList<RepositoryRecoveryStatus> Repositories, string? Note = null)
{
    public int Page { get; init; }
    public bool HasMore { get; init; }
}
public sealed record CorrectionStatus(string Item, int Attempt, string State, string? PullRequest);
public sealed record RepositoryRecoveryStatus(long Incident, string Repo, int Generation, string Stage, string Hold,
    string Attempts, string Evidence, string Assessment, string? Blocker, string Remedy,
    IReadOnlyList<string> HotFiles, IReadOnlyList<string> HeldItems, IReadOnlyList<string> Redirects,
    IReadOnlyList<CorrectionStatus> Corrections, IReadOnlyList<string> Controls);

/// <summary>Read-only operator projection. Never captures evidence, contacts a board, dispatches recovery or writes controls.
/// A read is an observation, not authorization. Answer persistence precedes acceptance: deserialization proves neither.</summary>
public sealed class RecoveryStatusReader(IDbContextFactory<LedgerDbContext> contexts, TimeProvider time)
{
    public const int MaxRepositories = 100;
    private const int MaxHotFiles = 101;
    private const int MaxPlans = 1000;
    private const int MaxDependents = MaxRepositories * (RecoverySnapshot.MaxPending + 1);
    private static readonly WorkState[] Finished = [WorkState.Done, WorkState.Cancelled, WorkState.Watch];
    internal static string Text(string? text, int limit = 600) => UntrustedText.Inert(text is null ? "unavailable" : text.Length > limit ? text[..limit] + "… [cut]" : text);

    public Task<RecoveryStatusReport> ReadAsync(CancellationToken ct) => ReadPageAsync(0, ct);

    public async Task<RecoveryStatusReport> ReadPageAsync(int page, CancellationToken ct)
    {
        if (page < 0 || page > int.MaxValue / MaxRepositories) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct);
        var latest = db.HotFileIncidents.GroupBy(i => i.Repo.ToLower()).Select(g => g.Max(i => i.Id));
        var incidents = await db.HotFileIncidents.AsNoTracking().Where(i => latest.Contains(i.Id))
            .OrderBy(i => i.State == "Resolved").ThenBy(i => i.Repo).Skip(page * MaxRepositories)
            .Take(MaxRepositories + 1).ToListAsync(ct);
        var hasMore = incidents.Count > MaxRepositories;
        incidents = incidents.Take(MaxRepositories).ToList();
        if (incidents.Count == 0) return new(time.GetUtcNow(), []) { Page = page };
        var incidentIds = incidents.Select(i => i.Id).ToArray();
        var repos = incidents.Select(i => i.Repo.ToLowerInvariant()).ToArray();
        var notes = new List<string>();
        if (hasMore) notes.Add($"More repositories: use the next page or factory recovery status --page {page + 1}.");

        // Fixed query count, independent of the number of selected repositories. Every materialization is bounded;
        // a bound reached is visible (not evidence of no work/controls). Latest-only entry queries avoid history scans
        // being materialized, including long-lived plans, redirects and attempt reservations.
        var corrections = await db.RecoveryCorrections.AsNoTracking().Where(c => incidentIds.Contains(c.IncidentId))
            .OrderByDescending(c => c.Id).Take(MaxRepositories * 11 + 1).ToListAsync(ct);
        var correctionIds = corrections.Select(c => c.Id).ToArray();
        var correcting = corrections.Select(c => ItemNaming.Recovery.Format(c.Id)).ToArray();
        var contributorIds = incidents.SelectMany(i => Trigger(i)?.Merges.Select(m => m.Item) ?? []).Distinct().ToArray();
        var work = await db.WorkItems.AsNoTracking().Where(w =>
                (repos.Contains(w.Repo.ToLower()) && !Finished.Contains(w.State)) || contributorIds.Contains(w.Id)
                || (w.Source == ItemNaming.Recovery.Source && correcting.Contains(w.ExternalId)))
            .OrderBy(w => w.Id).Take(MaxDependents + 1).ToListAsync(ct);
        var plans = await db.WorkItems.AsNoTracking().Where(w => !Finished.Contains(w.State)
                && db.LedgerEntries.Any(e => e.WorkItemId == w.Id && e.Step == RunPipeline.Steps.PlanStories))
            .OrderBy(w => w.Id).Take(MaxPlans + 1).ToListAsync(ct);
        var planIds = plans.Select(p => p.Id).Concat(work.Where(w => w.PlanItemId != null).Select(w => w.PlanItemId!.Value)).Distinct().ToArray();
        var parents = await db.WorkItems.AsNoTracking().Where(w => planIds.Contains(w.Id)).Take(MaxDependents + 1).ToListAsync(ct);
        var planRows = await db.LedgerEntries.AsNoTracking().Where(e => planIds.Contains(e.WorkItemId)
                && (e.Step == RunPipeline.Steps.Plan || e.Step == RunPipeline.Steps.PlanStories))
            .GroupBy(e => new { e.WorkItemId, e.Step }).Select(g => g.OrderByDescending(e => e.Id).First()).Take(MaxDependents + 1).ToListAsync(ct);
        var memberships = new List<(string Item, string Repo, WorkItem Parent)>();
        foreach (var parent in plans.Take(MaxPlans))
        {
            var children = planRows.SingleOrDefault(e => e.WorkItemId == parent.Id && e.Step == RunPipeline.Steps.PlanStories)?.Detail?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var recorded = PlanRelease.RecordedRepos(planRows.SingleOrDefault(e => e.WorkItemId == parent.Id && e.Step == RunPipeline.Steps.Plan)?.Detail,
                children, RepoRef.Parse(parent.Repo));
            if (recorded is null) { notes.Add($"Plan {Text(parent.ExternalId)} membership/repository ownership unavailable."); continue; }
            memberships.AddRange(recorded.Stories.Where(s => repos.Contains(s.Repo.ToLowerInvariant())).Select(s => (s.Id, s.Repo, parent)));
        }
        var childNames = memberships.Select(m => m.Item).Distinct().ToArray();
        var childItems = await db.WorkItems.AsNoTracking().Where(w => childNames.Contains(w.ExternalId)).Take(MaxDependents + 1).ToListAsync(ct);
        var redirectPrefixes = incidentIds.Select(RecoveryRedirect.Prefix).ToArray();
        var correctionWorkIds = work.Where(w => w.Source == ItemNaming.Recovery.Source && correcting.Contains(w.ExternalId)).Select(w => w.Id).ToArray();
        var correctionRows = await db.LedgerEntries.AsNoTracking().Where(e => correctionWorkIds.Contains(e.WorkItemId)
                && (e.Step == RunPipeline.Steps.Linked || e.Step == null))
            .GroupBy(e => new { e.WorkItemId, e.Step }).Select(g => g.OrderByDescending(e => e.Id).First()).Take(MaxDependents + 1).ToListAsync(ct);
        var redirectRows = await db.LedgerEntries.AsNoTracking().Where(e => e.Detail != null && e.Detail.StartsWith("{\"Incident\":") && e.Detail.Contains(",")
                && redirectPrefixes.Contains(e.Detail.Substring(0, e.Detail.IndexOf(",") + 1))
                && (e.Step == RecoveryRedirect.Step || e.Step == RecoveryRedirect.Started))
            .GroupBy(e => new { e.WorkItemId, e.Step }).Select(g => g.OrderByDescending(e => e.Id).First()).Take(MaxDependents + 1).ToListAsync(ct);
        var ancestors = await db.RecoveryAncestors.AsNoTracking().Where(a => correctionIds.Contains(a.CorrectionId))
            .OrderBy(a => a.CorrectionId).ThenBy(a => a.WorkItemId).Take(MaxDependents + 1).ToListAsync(ct);
        var scopes = new HashSet<string> { ControlScope.Factory, ControlScope.Freeze, ControlScope.Usage };
        scopes.UnionWith(repos.Select(r => ControlScope.Repository(RepoRef.Parse(r))));
        foreach (var item in work.Concat(parents).Concat(childItems)) AddScopes(scopes, item);
        scopes.UnionWith(childNames.Select(ControlScope.Item));
        scopes.UnionWith(ancestors.Select(a => a.Scope));
        var controls = await db.Controls.AsNoTracking().Where(c => scopes.Contains(c.Scope)).Take(MaxDependents + 1).ToListAsync(ct);
        var attempts = await db.RecoveryAssessmentAttempts.AsNoTracking().Where(a => incidentIds.Contains(a.IncidentId))
            .OrderByDescending(a => a.Id).Take(MaxRepositories * 12 + 1).ToListAsync(ct);
        // Re-read the latest identity per selected repository, not just the old incident's version: a successor may
        // have been inserted without changing the resolved row. This remains an observation, never a release grant.
        var current = await db.HotFileIncidents.AsNoTracking().Where(i => repos.Contains(i.Repo.ToLower()) && latest.Contains(i.Id))
            .Select(i => new { i.Id, i.Repo, i.Version, i.State, i.Snapshot }).Take(MaxRepositories + 1).ToListAsync(ct);
        if (work.Count > MaxDependents || plans.Count > MaxPlans || parents.Count > MaxDependents || planRows.Count > MaxDependents
            || childItems.Count > MaxDependents || correctionRows.Count > MaxDependents || redirectRows.Count > MaxDependents
            || ancestors.Count > MaxDependents || controls.Count > MaxDependents || corrections.Count > MaxRepositories * 11
            || attempts.Count > MaxRepositories * 12)
            notes.Add("Dependent ledger rows exceed display bounds; held work, controls or provenance may be incomplete (not authorization). Narrow the repository page; inspect the ledger for omitted rows.");

        var now = time.GetUtcNow();
        var rows = new List<RepositoryRecoveryStatus>();
        foreach (var incident in incidents)
        {
            var trigger = Trigger(incident);
            var answer = HotFileAssessment.FromDetail(incident.Answer);
            var material = RecoverySnapshot.FromDetail(answer?.Material);
            // Only label recorded bindings. Acceptance belongs to the state machine, not this read model.
            var bound = trigger is not null && answer?.Snapshot == trigger.Id && answer.Generation == incident.Generation
                && answer.Repo == incident.Repo && answer.EvidenceHash == incident.EvidenceHash
                && material?.IncidentSnapshot == trigger.Id && material.Id == answer.MaterialSnapshot
                && material.Repo == incident.Repo && material.Generation == incident.Generation;
            var ownCorrections = corrections.Where(c => c.IncidentId == incident.Id).OrderBy(c => c.Attempt).ToArray();
            var correctionViews = ownCorrections.Take(10).Select(c =>
            {
                var item = work.FirstOrDefault(w => w.Source == ItemNaming.Recovery.Source && w.ExternalId == ItemNaming.Recovery.Format(c.Id));
                var link = correctionRows.FirstOrDefault(e => e.WorkItemId == item?.Id && e.Step == RunPipeline.Steps.Linked)?.Detail;
                var failure = correctionRows.FirstOrDefault(e => e.WorkItemId == item?.Id && e.Step == null && e.State is WorkState.Escalated or WorkState.Cancelled);
                return new CorrectionStatus(ItemNaming.Recovery.Format(c.Id), c.Attempt, Text(c.State) + (item is null ? "; work item unavailable" : $"; {item.State}")
                    + (failure is null ? "" : "; blocker (ledger row " + failure.Id + "): " + Text(failure.Detail)), Pull(link?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), incident.Repo));
            }).ToArray();
            var redirects = redirectRows.Where(e => e.Step == RecoveryRedirect.Step).Select(e => (Row: e, Redirect: RecoveryRedirect.Parse(e.Detail)))
                .Where(x => x.Redirect?.Incident == incident.Id).Select(x =>
                {
                    var r = x.Redirect!;
                    var restarted = redirectRows.Any(e => e.WorkItemId == x.Row.WorkItemId && e.Step == RecoveryRedirect.Started && e.Detail == x.Row.Detail);
                    var state = restarted ? "restarted (durable consumption recorded)" : r.Commit is not null ? "delivered " + Text(r.Commit) + "; restart pending"
                        : incident.State == "Resolved" ? "safe resolution recorded; restart pending (no correction delivery required)" : "waiting for incident resolution / delivery";
                    return $"{Text(r.Item)}: {state}; {Text(r.Context)}";
                }).ToArray();
            var ownScopes = new HashSet<string> { ControlScope.Factory, ControlScope.Repository(RepoRef.Parse(incident.Repo)), ControlScope.Freeze, ControlScope.Usage };
            var ordinary = work.Where(w => RepoRef.Parse(w.Repo).Is(RepoRef.Parse(incident.Repo)) && !Finished.Contains(w.State) && w.Source != ItemNaming.Recovery.Source).ToArray();
            foreach (var item in ordinary.Concat(work.Where(w => trigger?.Merges.Any(m => m.Item == w.Id) == true
                    || ownCorrections.Any(c => w.ExternalId == ItemNaming.Recovery.Format(c.Id))))) AddScopes(ownScopes, item);
            foreach (var parent in parents.Where(p => ordinary.Any(w => w.PlanItemId == p.Id))) AddScopes(ownScopes, parent);
            foreach (var m in memberships.Where(m => RepoRef.Parse(m.Repo).Is(RepoRef.Parse(incident.Repo)))) AddScopes(ownScopes, m.Parent);
            foreach (var child in childItems.Where(w => RepoRef.Parse(w.Repo).Is(RepoRef.Parse(incident.Repo)))) AddScopes(ownScopes, child);
            ownScopes.UnionWith(memberships.Where(m => RepoRef.Parse(m.Repo).Is(RepoRef.Parse(incident.Repo))).Select(m => ControlScope.Item(m.Item)));
            ownScopes.UnionWith(ancestors.Where(a => ownCorrections.Any(c => c.Id == a.CorrectionId)).Select(a => a.Scope));
            var heldControls = controls.Where(c => ownScopes.Contains(c.Scope) && Holding(c, now)).Select(DescribeControl).ToArray();
            var rechecked = current.SingleOrDefault(i => RepoRef.Parse(i.Repo).Is(RepoRef.Parse(incident.Repo)));
            var raced = rechecked is null || rechecked.Id != incident.Id || rechecked.Version != incident.Version
                || rechecked.State != incident.State || rechecked.Snapshot != incident.Snapshot;
            var hold = raced ? "state changed during read; refresh required (not authorization)"
                : incident.State == "Resolved" ? "automatic hold resolved; " + (heldControls.Length > 0 ? "human or other controls still hold work" : "manual controls checked at observation time only")
                : "automatic repository hold; ordinary work remains held";
            var evidence = bound
                ? $"recorded snapshot {Text(material!.Id)}; hash {Text(answer!.MaterialHash)}; " + (material.Complete ? "recorded complete" : "incomplete: " + string.Join("; ", material.Missing.Select(m => Text(m.Category) + ": " + Text(m.Reason))))
                    + "; base tips " + string.Join(", ", material.BaseTips.Select(t => Text(t.Key) + "@" + Text(t.Value)))
                : "snapshot / assessment evidence unavailable or not bound to this generation";
            evidence += "; ledger provenance only, not live-validated; recorded answer is not proof of acceptance";
            var assessment = $"incident session {Text(incident.Session)}; prompt {Text(incident.Prompt)}; " + (answer is null ? "assessment result unavailable; cost N/A"
                : $"recorded result {Text(answer.Decision)}; session {Text(answer.Session)}; class {Text(answer.ServedClass)}; prompt {Text(answer.Prompt)}; cost "
                    + (answer.CostUsd is { } cost ? cost.ToString("C4", CultureInfo.GetCultureInfo("en-US")) : "N/A")
                    + (answer.Problem is null ? "" : "; recorded problem: " + Text(answer.Problem)));
            if (answer?.Findings.Count > 0) assessment += "; recorded findings (not acceptance): " + string.Join("; ", answer.Findings.Take(10)
                .Select(f => Text(f.Kind) + ": " + Text(f.Issue) + "; expected " + Text(f.ExpectedBehavior)));
            var reservations = attempts.Where(a => a.IncidentId == incident.Id).Take(12).ToArray();
            if (reservations.Length > 0) assessment += "; last reservations: " + string.Join("; ", reservations.Select(a => $"{Text(a.Snapshot)} #{a.Number} {Text(a.State)} session {Text(a.Session)}; result {(a.Result is null ? "unavailable" : "recorded (not acceptance)")}; lease until {a.LeaseUntil:u}"));
            var held = new List<string>();
            foreach (var item in ordinary)
            {
                var applicable = ItemScopes(item, incident.Repo, parents);
                // A listed/parked child may not have acquired PlanItemId yet; recorded membership still composes its parent control.
                foreach (var membership in memberships.Where(m => m.Item == item.ExternalId)) AddScopes(applicable, membership.Parent);
                if (incident.State != "Resolved" || controls.Any(c => applicable.Contains(c.Scope) && Holding(c, now)))
                    held.Add($"{Text(item.ExternalId)} ({item.State}; {(incident.State != "Resolved" ? "automatic incident hold" : "applicable manual/other control")})");
            }
            foreach (var membership in memberships.Where(m => RepoRef.Parse(m.Repo).Is(RepoRef.Parse(incident.Repo))))
            {
                if (childItems.Any(w => w.ExternalId == membership.Item) || ordinary.Any(w => w.ExternalId == membership.Item)) continue;
                var applicable = ItemScopes(membership.Parent, membership.Repo, parents);
                applicable.Add(ControlScope.Item(membership.Item));
                if (incident.State != "Resolved" || controls.Any(c => applicable.Contains(c.Scope) && Holding(c, now)))
                    held.Add($"{Text(membership.Item)} (not started; plan {Text(membership.Parent.ExternalId)}; {Text(membership.Repo)})");
            }
            var hotFiles = trigger?.Merges.SelectMany(m => m.Files.Select(f => (m.Base, File: f))).Distinct()
                .Select(pair => Text(pair.Base) + ": " + Text(pair.File)).ToArray();
            rows.Add(new(incident.Id, incident.Repo, incident.Generation, Text(incident.State), hold,
                $"assessment {incident.Attempts}/{HotFileRecovery.AssessmentLimit}, correction {RecoveryCorrections.UsedInGeneration(ownCorrections, incident)}/{RecoveryCorrections.CorrectionLimit}, refresh {incident.Refreshes}/{HotFileRecovery.RefreshLimit}",
                evidence, assessment, incident.Error is null ? null : Text(incident.Error),
                incident.State == "Escalated" ? $"Named human remedy: inspect the blocker and correction PR and fix the named evidence/gate/environment problem, then re-arm a fresh assessment with fresh budgets: factory recovery reassess --repo {incident.Repo} (or the dashboard's Reassess on this incident). A Continue does not reset exhausted budgets or resolve an incident, and a re-arm clears no Pause or Stop. To cancel affected work instead: factory stop --repo {incident.Repo}. Nothing retries automatically after exhaustion."
                    : "Only a human Continue of the named manual scope clears its Pause; it does not resolve an automatic incident.",
                hotFiles is null ? ["hot files unavailable"] : hotFiles.Take(MaxHotFiles).Concat(hotFiles.Length > MaxHotFiles ? [$"{hotFiles.Length - MaxHotFiles} hot file/base pairs omitted"] : []).ToArray(),
                held.Take(RecoverySnapshot.MaxPending).Concat(held.Count > RecoverySnapshot.MaxPending ? [$"{held.Count - RecoverySnapshot.MaxPending} held items omitted"] : []).ToArray(),
                redirects, correctionViews, heldControls));
        }
        return new(now, rows, notes.Count == 0 ? null : string.Join(" ", notes.Distinct())) { Page = page, HasMore = hasMore };
    }

    private static bool Holding(Control c, DateTimeOffset now) => c.PausesAt(now) || c.State == ControlState.Stopping;
    private static string DescribeControl(Control c) => $"{Text(c.Scope)}: {(c.Scope is ControlScope.Freeze or ControlScope.Usage ? "automatic control" : "human Pause / Stop")} {c.State}; {Text(c.Reason)}; by {Text(c.ChangedBy)} at {c.ChangedAt:u}";
    private static void AddScopes(HashSet<string> scopes, WorkItem item)
    {
        scopes.Add(ControlScope.Item(item.ExternalId));
        if (ItemNaming.EpicOf(item.ExternalId, item.EpicId) is { } epic) scopes.Add(ControlScope.Epic(epic));
    }
    private static HashSet<string> ItemScopes(WorkItem item, string repo, IReadOnlyList<WorkItem> parents)
    {
        var scopes = new HashSet<string> { ControlScope.Factory, ControlScope.Freeze, ControlScope.Usage, ControlScope.Repository(RepoRef.Parse(repo)) };
        AddScopes(scopes, item);
        foreach (var parent in parents.Where(p => p.Id == item.PlanItemId)) AddScopes(scopes, parent);
        return scopes;
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
