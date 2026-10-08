using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Issues;

/// <summary>Checkpoint names of a GitHub issue's work item before it is built (its triage and approval), all in Intake or Paused.</summary>
public static class IssueSteps
{
    /// <summary>A triage of one issue version and its route, decided; Detail is the <see cref="TriageRecord"/> JSON. Recorded before anything is posted.</summary>
    public const string Triaged = "triaged";

    /// <summary>The triage worker's Claude session started; Detail is the issue version.</summary>
    public const string TriageSession = "triage-session";

    /// <summary>The triage comment is on the issue; Detail is "&lt;triage hash&gt; &lt;comment id&gt;".</summary>
    public const string TriageComment = "triage-comment";

    /// <summary>The route's label is on the issue; Detail is "&lt;triage hash&gt; &lt;label&gt;".</summary>
    public const string Labeled = "labeled";

    /// <summary>A collaborator's <c>Approved</c> bound to the current triage; Detail is the <see cref="ApprovalRecord"/> JSON.</summary>
    public const string Approved = "approved";

    /// <summary>An <c>Approved</c> comment that releases nothing; Detail is the <see cref="ApprovalRecord"/> JSON with why.</summary>
    public const string ApprovalIgnored = "approval-ignored";

    /// <summary>
    /// The item may be built from the triage it names: Detail is "&lt;triage hash&gt; auto" (a collaborator's issue with an apparent fix)
    /// or "&lt;triage hash&gt; approved by &lt;login&gt;". That triage fixes the item's scope: later edits to the issue change nothing.
    /// </summary>
    public const string Released = "released";
}

/// <summary>One <c>Approved</c> comment and what it did: bound to <see cref="TriageHash"/> (the triage comment before it), or why not.</summary>
public sealed record ApprovalRecord(long CommentId, string Approver, string Permission, string? TriageHash, string? Ignored)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ApprovalRecord FromJson(string json) => JsonSerializer.Deserialize<ApprovalRecord>(json, Json)!;
}

/// <summary>
/// The GitHub issue intake (sc-25385), run by the intake loop before it lists the issue source's ready items
/// (<see cref="IntakeLane.Prepare"/>). It polls each watched repo's issues updated since its cursor (ledger-backed, so a restart
/// re-reads only what it has not finished) and, per issue:
/// <list type="number">
/// <item>gives the issue a work item (<c>gh-&lt;key&gt;</c>) and, for a version (title and body) not triaged yet, runs the triage
/// worker, reads the author's permission and routes it (<see cref="IssueRouting"/>), recording the <see cref="IssueSteps.Triaged"/>
/// row before anything is posted;</item>
/// <item>posts the triage comment and the route's label as the orchestrator, with an issues-only token (never the model, E4),
/// each checkpointed; a retried post finds the comment already there by its marker, so nothing is posted twice;</item>
/// <item>builds a collaborator's issue with an apparent fix (<see cref="IssueSteps.Released"/>: the item stays in Intake for the
/// pipeline), and parks every other route (Paused, <c>parked</c>);</item>
/// <item>releases a parked, releasable triage on a collaborator's exact <c>Approved</c> (<see cref="IssueComments.IsApproval"/>)
/// posted after its triage comment and bound to it; any other <c>Approved</c> is recorded as ignored, once.</item>
/// </list>
/// A released item's scope is the approved triage: later edits are not triaged again. A triage that keeps failing is recorded as
/// a failed triage after <c>maxFailures</c> tries and routed to a human (E10). A worker refused for usage pauses the factory.
/// </summary>
public sealed class IssueIntake(
    IGitHubIssues issues,
    IReadOnlyList<RepoRef> watched,
    IDbContextFactory<LedgerDbContext> contexts,
    IRunLocks locks,
    IControls controls,
    ITriageRunner triage,
    IntakeStatus status,
    int maxFailures,
    TimeProvider time,
    TextWriter log)
{
    public static readonly ItemNaming Naming = ItemNaming.GitHubIssue;

    /// <summary>Detail of the Paused row (and its parked checkpoint) an issue's new work item waits in until a triage releases it.</summary>
    public const string AwaitingTriage = "awaiting triage";

    /// <summary>One poll of every watched repo. Throws only for a failure every issue shares (GitHub or the ledger unreachable).</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        foreach (var repo in watched)
        {
            if (!await PollRepoAsync(repo, ct))
            {
                return; // the factory paused for usage: nothing more this poll
            }
        }
    }

    private async Task<bool> PollRepoAsync(RepoRef repo, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var cursor = await db.GitHubIssueCursors.SingleOrDefaultAsync(c => c.Repo == repo.FullName, ct);
        if (cursor is null)
        {
            // First poll of a repo: start from now. Issues opened before the factory watched it are not triaged until they change.
            db.GitHubIssueCursors.Add(new GitHubIssueCursor { Repo = repo.FullName, Since = time.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            log.WriteLine($"[issues] watching {repo} from now on");
            return true;
        }
        var listed = await issues.ListUpdatedAsync(repo, cursor.Since, ct);
        var since = cursor.Since;
        var done = true;
        foreach (var issue in listed.OrderBy(i => i.UpdatedAt))
        {
            var outcome = await ProcessAsync(db, repo, issue, ct);
            if (outcome == Outcome.UsagePaused)
            {
                await SaveCursorAsync(db, cursor, since, ct);
                return false;
            }
            // The cursor moves past an issue only once every issue before it is finished; later ones are idempotent to redo.
            done &= outcome == Outcome.Done;
            if (done && issue.UpdatedAt > since)
            {
                since = issue.UpdatedAt;
            }
        }
        await SaveCursorAsync(db, cursor, since, ct);
        return true;
    }

    private static async Task SaveCursorAsync(LedgerDbContext db, GitHubIssueCursor cursor, DateTimeOffset since, CancellationToken ct)
    {
        if (since != cursor.Since)
        {
            cursor.Since = since;
            await db.SaveChangesAsync(ct);
        }
    }

    private enum Outcome
    {
        Done,
        /// <summary>Not finished this poll (running, controlled, or failed): it is read again next poll.</summary>
        Again,
        UsagePaused,
    }

    private async Task<Outcome> ProcessAsync(LedgerDbContext db, RepoRef repo, IssueFacts issue, CancellationToken ct)
    {
        var key = await KeyAsync(db, repo, issue.Number, ct);
        var id = Naming.Format(key);
        var ledger = new WorkLedger(db, time);
        var item = await ledger.GetOrCreateAsync(Naming.Source, id, $"{repo}#{issue.Number}", repo.FullName, $"issue: {issue.HtmlUrl}", ct);
        if (Lifecycle.IsTerminal(item.State))
        {
            return Outcome.Done;
        }
        if (await controls.EffectiveAsync(item.ExternalId, null, ct) != ControlState.Running)
        {
            return Outcome.Again;
        }
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            return Outcome.Again; // a run holds it
        }
        await db.Entry(item).ReloadAsync(ct);
        try
        {
            var outcome = await TriageAndRouteAsync(ledger, item, repo, issue, ct);
            if (outcome == Outcome.Done)
            {
                status.ItemOk(id);
            }
            return outcome;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !IntakeLoop.IsFactoryWide(ex))
        {
            log.WriteLine($"[issues] {id} ({repo}#{issue.Number}) failed: {ex.Message}; retried next poll");
            status.ItemFailed(id, $"{ex.GetType().Name}: {ex.Message}");
            return Outcome.Again;
        }
    }

    private async Task<Outcome> TriageAndRouteAsync(WorkLedger ledger, WorkItem item, RepoRef repo, IssueFacts issue, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(item, ct);
        if (history.Any(e => e.Step == IssueSteps.Released))
        {
            // Released: the approved triage is the item's scope; the issue's later edits and comments change nothing.
            return Outcome.Done;
        }
        if (item.State == WorkState.Intake)
        {
            // Nothing runs the item until a triage releases it: it waits parked (never resumed by itself), and the source lists it
            // as ready once released (GitHubIssueWorkSource.ListReadyAsync).
            await ledger.RecordAsync(item, WorkState.Paused, null, AwaitingTriage, ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Parked, null, AwaitingTriage, ct);
            history = await ledger.HistoryAsync(item, ct);
        }
        var version = IssueHashes.Version(issue.Title, issue.Body);
        var record = Latest(history);
        if (record is null || record.Version != version)
        {
            var (triaged, usagePaused) = await TriageAsync(ledger, item, repo, issue, version, ct);
            if (triaged is null)
            {
                return usagePaused ? Outcome.UsagePaused : Outcome.Again;
            }
            record = triaged;
            await ledger.CheckpointAsync(item, IssueSteps.Triaged, null, record.ToJson(), ct);
            await ledger.RefreshAsync(item, record.Triage?.Title ?? item.Title, record.Target ?? item.Repo, null, ct);
            log.WriteLine($"[issues] {item.ExternalId} ({repo}#{issue.Number}) triaged {record.Hash}: {IssueComments.RouteName(record.Route)}");
            history = await ledger.HistoryAsync(item, ct);
        }
        var since = history.FindLastIndex(e => e.Step == IssueSteps.Triaged);
        var steps = history.Skip(since + 1).ToList();
        var comments = await issues.ListCommentsAsync(repo, issue.Number, ct);

        if (!steps.Any(e => e.Step == IssueSteps.TriageComment))
        {
            var posted = comments.FirstOrDefault(c => IssueComments.TriageHash(c, issues.AppId) == record.Hash)?.Id
                ?? await issues.CommentAsync(repo, issue.Number, IssueComments.Triage(record), ct);
            await ledger.CheckpointAsync(item, IssueSteps.TriageComment, null, $"{record.Hash} {posted}", ct);
            comments = await issues.ListCommentsAsync(repo, issue.Number, ct);
        }
        if (IssueRouting.Label(record.Route) is { } label && !steps.Any(e => e.Step == IssueSteps.Labeled))
        {
            await issues.AddLabelsAsync(repo, issue.Number, [label], ct);
            // A re-triage that changed the route takes the other route's label off.
            foreach (var stale in new[] { IssueLabels.AwaitingApproval, IssueLabels.NeedsHuman }.Where(l => l != label && issue.Labels.Contains(l)))
            {
                await issues.RemoveLabelAsync(repo, issue.Number, stale, ct);
            }
            await ledger.CheckpointAsync(item, IssueSteps.Labeled, null, $"{record.Hash} {label}", ct);
        }

        if (record.Route == IssueRoute.Build)
        {
            await ledger.CheckpointAsync(item, IssueSteps.Released, null, $"{record.Hash} auto", ct);
            log.WriteLine($"[issues] {item.ExternalId} released to build ({record.Hash})");
            return Outcome.Done;
        }
        if (!steps.Any(e => e.Step == RunPipeline.Steps.Parked))
        {
            if (item.State != WorkState.Paused)
            {
                await ledger.RecordAsync(item, WorkState.Paused, null, IssueComments.RouteName(record.Route), ct);
            }
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Parked, null, $"{IssueComments.RouteName(record.Route)}: {record.Why}", ct);
        }
        await ApprovalsAsync(ledger, item, repo, record, comments, ct);
        return Outcome.Done;
    }

    /// <summary>
    /// Runs the triage worker on this version and routes it; returns the record to write, or none when the worker was refused for
    /// usage (<c>UsagePaused</c>: the factory is now paused) or failed and will be tried again. After <c>maxFailures</c> failures in
    /// a row the failure itself is the triage (routed to a human), so a broken issue is neither retried forever nor left silent (E10).
    /// </summary>
    private async Task<(TriageRecord? Record, bool UsagePaused)> TriageAsync(WorkLedger ledger, WorkItem item, RepoRef repo, IssueFacts issue,
        string version, CancellationToken ct)
    {
        Triage? parsed = null;
        string? error = null;
        try
        {
            var result = await triage.RunAsync(item, repo, TriagePrompt.Build(repo, issue, watched),
                (session, c) => ledger.CheckpointAsync(item, IssueSteps.TriageSession, session, version, c), ct);
            if (result.UsageLimited && await controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, ct) is { State: ControlState.Paused } pause)
            {
                // Nothing recorded: the version is triaged again once the pause lifts.
                log.WriteLine($"[issues] triage worker hit a router exhaustion or rate-limit error; factory paused for usage until {pause.ResumeAt:u}");
                return (null, true);
            }
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"triage worker failed (exit {result.ExitCode}, result {result.ResultSubtype ?? "none"}): {result.ResultText} {result.StderrTail}".Trim());
            }
            try
            {
                parsed = TriageParser.Parse(result.ResultText);
            }
            catch (TriageFormatException ex)
            {
                error = $"the triage worker's answer could not be read: {ex.Message}";
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !IntakeLoop.IsFactoryWide(ex))
        {
            var failures = status.ItemFailed(item.ExternalId, $"{ex.GetType().Name}: {ex.Message}");
            log.WriteLine($"[issues] {item.ExternalId} triage failed ({failures} in a row): {ex.Message}");
            if (failures < maxFailures)
            {
                return (null, false);
            }
            error = $"the triage failed {failures} times in a row; last error: {ex.Message}";
        }
        var author = issue.AuthorIsBot ? RepoPermission.None : await issues.PermissionAsync(repo, issue.Author, ct);
        GatePolicy? policy = null;
        string? policyError = null;
        if (parsed is { Buildable: true } && author.IsCollaborator && IssueRouting.Target(parsed, watched).Target is { } target)
        {
            try
            {
                policy = await issues.GetFileAsync(target, GatePolicy.Path, ct) is { } yaml ? GatePolicy.Parse(yaml) : null;
            }
            catch (GatePolicyException ex)
            {
                policyError = ex.Message;
            }
        }
        var route = IssueRouting.Decide(parsed, error, author, watched, policy, policyError);
        return (TriageRecord.Create(version, parsed, error, issue.Author, author, route), false);
    }

    /// <summary>
    /// Records each <c>Approved</c> comment not seen before, oldest first: the first that a collaborator posted after the current
    /// triage's comment (bound to it: the last triage comment before the approval shows the current triage) releases a releasable
    /// triage; every other one is recorded as ignored, with why.
    /// </summary>
    private async Task ApprovalsAsync(WorkLedger ledger, WorkItem item, RepoRef repo, TriageRecord record, IReadOnlyList<IssueComment> comments,
        CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(item, ct);
        var seen = history.Where(e => e.Step is IssueSteps.Approved or IssueSteps.ApprovalIgnored && e.Detail is not null)
            .Select(e => ApprovalRecord.FromJson(e.Detail!).CommentId).ToHashSet();
        var ordered = comments.OrderBy(c => c.Id).ToList();
        foreach (var approval in ordered.Where(c => IssueComments.IsApproval(c) && c.AppId != issues.AppId && !seen.Contains(c.Id)))
        {
            var bound = ordered.LastOrDefault(c => c.Id < approval.Id && IssueComments.TriageHash(c, issues.AppId) is not null) is { } shown
                ? IssueComments.TriageHash(shown, issues.AppId)
                : null;
            var permission = approval.AuthorIsBot ? RepoPermission.None : await issues.PermissionAsync(repo, approval.Author, ct);
            var ignored = bound is null ? "no triage comment came before it"
                : bound != record.Hash ? $"it approves triage {bound}, but the issue changed since and the current triage is {record.Hash}"
                : !permission.IsCollaborator ? $"{approval.Author} is not a collaborator ({permission})"
                : !record.Releasable ? $"the triage cannot be built as it stands: {record.Why}"
                : null;
            var approvalRecord = new ApprovalRecord(approval.Id, approval.Author, permission.ToString(), bound, ignored);
            if (ignored is not null)
            {
                await ledger.CheckpointAsync(item, IssueSteps.ApprovalIgnored, null, approvalRecord.ToJson(), ct);
                log.WriteLine($"[issues] {item.ExternalId}: ignored Approved by {approval.Author}: {ignored}");
                continue;
            }
            await ledger.CheckpointAsync(item, IssueSteps.Approved, null, approvalRecord.ToJson(), ct);
            await ledger.CheckpointAsync(item, IssueSteps.Released, null, $"{record.Hash} approved by {approval.Author}", ct);
            log.WriteLine($"[issues] {item.ExternalId} approved by {approval.Author}; released to build ({record.Hash})");
            return;
        }
    }

    /// <summary>The latest triage recorded for the item, or null.</summary>
    public static TriageRecord? Latest(List<LedgerEntry> history) =>
        history.LastOrDefault(e => e.Step == IssueSteps.Triaged) is { Detail: { } json } ? TriageRecord.FromJson(json) : null;

    /// <summary>The triage the item was released to build from (its scope), or null while it is not released.</summary>
    public static TriageRecord? ReleasedTriage(List<LedgerEntry> history)
    {
        if (history.LastOrDefault(e => e.Step == IssueSteps.Released)?.Detail?.Split(' ')[0] is not { } hash)
        {
            return null;
        }
        return history.Where(e => e.Step == IssueSteps.Triaged && e.Detail is not null).Select(e => TriageRecord.FromJson(e.Detail!))
            .LastOrDefault(r => r.Hash == hash);
    }

    /// <summary>The ledger key of the repo's issue, created on first sight.</summary>
    public static async Task<int> KeyAsync(LedgerDbContext db, RepoRef repo, int number, CancellationToken ct)
    {
        if (await db.GitHubIssues.AsNoTracking().SingleOrDefaultAsync(i => i.Repo == repo.FullName && i.Number == number, ct) is { } known)
        {
            return known.Id;
        }
        var row = new GitHubIssue { Repo = repo.FullName, Number = number, FirstSeenAt = DateTimeOffset.UtcNow };
        db.GitHubIssues.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            return (await db.GitHubIssues.AsNoTracking().SingleAsync(i => i.Repo == repo.FullName && i.Number == number, ct)).Id;
        }
    }
}
