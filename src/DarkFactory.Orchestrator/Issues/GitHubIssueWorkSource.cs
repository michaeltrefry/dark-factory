using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Issues;

/// <summary>
/// What the issue intake (<see cref="IssueIntake"/>) reads and writes on the GitHub issue board: an optional capability of the issue
/// source's <see cref="IWorkSource"/>, so the intake touches the board only through its work source (E6) — the triage comment and the
/// route's label (the writes), the issues, comments, permissions and gate policy it routes by (the reads). The Shortcut board has no
/// intake triage and does not implement it.
/// </summary>
public interface IIssueIntakeSource : IWorkSource
{
    /// <summary>The id of the App whose tokens write the factory's comments (its own comments are recognised by it).</summary>
    long AppId { get; }

    /// <summary>Open issues of <paramref name="repo"/> updated at or after <paramref name="since"/>, least recently updated first.</summary>
    Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct);

    /// <summary>Every comment of the issue, oldest first.</summary>
    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct);

    /// <summary>The user's permission on the repo.</summary>
    Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct);

    /// <summary>The repo's <c>factory/gate.yaml</c> on its default branch, or null when it has none.</summary>
    Task<string?> GatePolicyAsync(RepoRef repo, CancellationToken ct);

    /// <summary>Posts the orchestrator's triage comment (attributed) on the issue; returns its id.</summary>
    Task<long> PostTriageAsync(RepoRef repo, int number, string body, CancellationToken ct);

    /// <summary>Puts the route's <paramref name="label"/> on the issue and takes off the other route labels it <paramref name="carries"/>.</summary>
    Task LabelRouteAsync(RepoRef repo, int number, string label, IReadOnlyCollection<string> carries, CancellationToken ct);
}

/// <summary>
/// GitHub issues as a work source (sc-25385), behind <see cref="IWorkSource"/> like the Shortcut board (E6). An item is a watched
/// repo's issue that <see cref="IssueIntake"/> triaged and released (<see cref="IssueSteps.Released"/>); its id is the issue's
/// ledger key (<c>gh-&lt;key&gt;</c>). Its spec is the released triage only — never the issue's own title or body (E4) — and
/// names the issue the PR closes on merge (<see cref="WorkStory.Closes"/>). Board writes are the orchestrator's, with an
/// issues-only token (<see cref="GitHubIssuesClient"/>): the <c>factory-claimed</c> label, comments, and closing the issue once
/// merged (GitHub's closing keyword in the PR does it first; closing here is the fallback, e.g. for a PR merged into a branch that
/// is not the default).
/// </summary>
public sealed class GitHubIssueWorkSource(IGitHubIssues issues, IDbContextFactory<LedgerDbContext> contexts, IReadOnlyList<RepoRef> watched,
    TimeProvider? time = null) : IIssueIntakeSource
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ItemNaming Naming => ItemNaming.GitHubIssue;

    /// <summary>
    /// Claim refusals in a row before the run escalates a released issue (<see cref="IWorkSource.MaxClaimRefusals"/>): a refused
    /// claim is listed again on the next poll (<see cref="ListReadyAsync"/>), each refusal recorded, and the last one escalates.
    /// </summary>
    public const int ClaimRefusals = 3;

    public int? MaxClaimRefusals => ClaimRefusals;

    public string ScopeReturnHint => "Add its repo back to GitHub:Watch:Repos";

    public long AppId => issues.AppId;

    public Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset since, CancellationToken ct) =>
        issues.ListUpdatedAsync(repo, since, ct);

    public Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct) =>
        issues.ListCommentsAsync(repo, number, ct);

    public Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct) => issues.PermissionAsync(repo, login, ct);

    public Task<string?> GatePolicyAsync(RepoRef repo, CancellationToken ct) => issues.GetFileAsync(repo, Gate.GatePolicy.Path, ct);

    public Task<long> PostTriageAsync(RepoRef repo, int number, string body, CancellationToken ct) =>
        issues.CommentAsync(repo, number, WorkSourceComments.Attributed(body), ct);

    public async Task LabelRouteAsync(RepoRef repo, int number, string label, IReadOnlyCollection<string> carries, CancellationToken ct)
    {
        await issues.AddLabelsAsync(repo, number, [label], ct);
        // A re-triage that changed the route takes the other route's label off.
        foreach (var stale in new[] { IssueLabels.AwaitingApproval, IssueLabels.NeedsHuman }.Where(l => l != label && carries.Contains(l)))
        {
            await issues.RemoveLabelAsync(repo, number, stale, ct);
        }
    }

    /// <summary>
    /// Released items not running yet, in a watched repo: parked (or new) in Intake with a release recorded after their last
    /// transition, or released and parked by a refused claim (<see cref="RunPipeline.ClaimRefused"/>) fewer than
    /// <see cref="ClaimRefusals"/> times in a row, so a refusal is retried on the next poll rather than never.
    /// </summary>
    public async Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ledger = new WorkLedger(db, _time);
        var ready = new List<int>();
        foreach (var item in await ledger.ItemsInAsync(Naming.Source, new HashSet<WorkState> { WorkState.Paused, WorkState.Intake }, ct))
        {
            var history = await ledger.HistoryAsync(item, ct);
            var last = history.FindLastIndex(e => e.Step is null);
            var waitsInIntake = item.State == WorkState.Intake
                || TransitionContext.From(history.Where(e => e.Step is null).Select(e => e.State).ToList()).PausedFrom == WorkState.Intake;
            var released = history.Skip(last + 1).Any(e => e.Step == IssueSteps.Released)
                || (last >= 0 && history[last] is { State: WorkState.Paused } paused && RunPipeline.IsClaimRefused(paused.Detail)
                    && history.Any(e => e.Step == IssueSteps.Released) && RunPipeline.ClaimRefusalsInARow(history) < ClaimRefusals);
            if (waitsInIntake && released && Naming.TryParse(item.ExternalId, out var id)
                && await RowAsync(db, id, ct) is { } row && Watched(row.Repo))
            {
                ready.Add(id);
            }
        }
        return ready;
    }

    /// <summary>
    /// Claims a released issue: refuses (writing nothing) an unknown key, an unreleased item (it awaits triage or approval), a
    /// closed issue, or (unless <paramref name="ignoreScope"/>) a repo no longer watched; else labels it <c>factory-claimed</c> and
    /// reads the label back.
    /// </summary>
    public async Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await RowAsync(db, id, ct) is not { } row)
        {
            return ClaimResult.Refused($"{Naming.Format(id)} is no GitHub issue the factory knows");
        }
        if (await ReleasedAsync(db, id, ct) is null)
        {
            return ClaimResult.Refused("the issue is not released: it awaits its triage or a collaborator's approval");
        }
        var repo = RepoRef.Parse(row.Repo);
        if (!ignoreScope && !Watched(row.Repo))
        {
            return ClaimResult.Refused("the issue's repo is outside the watch scope");
        }
        var issue = await issues.GetAsync(repo, row.Number, ct);
        if (!issue.Open)
        {
            return ClaimResult.Refused("the issue is closed");
        }
        if (issue.Labels.Contains(IssueLabels.Claimed))
        {
            return ClaimResult.Ok;
        }
        await issues.AddLabelsAsync(repo, row.Number, [IssueLabels.Claimed], ct);
        var after = await issues.GetAsync(repo, row.Number, ct);
        return after.Labels.Contains(IssueLabels.Claimed)
            ? ClaimResult.Ok
            : ClaimResult.Refused($"the claim did not stick (read back labels [{string.Join(", ", after.Labels)}])");
    }

    /// <summary>In scope while the issue's repo and the released triage's target are watched repos.</summary>
    public async Task<bool> InScopeAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await RowAsync(db, id, ct) is { } row && Watched(row.Repo)
            && (await ReleasedAsync(db, id, ct) is not { Target: { } target } || Watched(target));
    }

    /// <summary>Reads each watched repo's issues (the App is installed there and may read them); throws naming the first that fails.</summary>
    public async Task ValidateScopeAsync(CancellationToken ct)
    {
        foreach (var repo in watched)
        {
            try
            {
                await issues.ListUpdatedAsync(repo, _time.GetUtcNow(), ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"GitHub:Watch:Repos: cannot read the issues of {repo}: {ex.Message}", ex);
            }
        }
    }

    public async Task ReleaseAsync(int id, CancellationToken ct)
    {
        var (repo, number) = await IssueAsync(id, ct);
        await issues.RemoveLabelAsync(repo, number, IssueLabels.Claimed, ct);
    }

    /// <summary>
    /// The released triage as the item's spec (E4: the implementing worker never sees the issue's own text). An item not released
    /// yet (e.g. <c>factory run gh-N</c> before approval) gets a placeholder its claim then refuses.
    /// </summary>
    public async Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await RowAsync(db, id, ct) ?? throw new InvalidOperationException($"{Naming.Format(id)} is no GitHub issue the factory knows.");
        var url = $"https://github.com/{row.Repo}/issues/{row.Number}";
        var closes = $"{row.Repo}#{row.Number}";
        if (await ReleasedAsync(db, id, ct) is not { Triage: { } triage } released)
        {
            return new WorkSpec(new WorkStory(id, $"{row.Repo}#{row.Number}", $"Repo: {row.Repo}\n\n(not released: awaiting triage or approval)", "issue",
                url, Naming, closes), null, []);
        }
        // The "Repo:" line comes first, so nothing in the triage text can name another repo (RepoResolver takes the first).
        var description = $"""
            Repo: {released.Target ?? row.Repo}

            Triaged {triage.Type.ToString().ToLowerInvariant()} from GitHub issue {closes} (triage {released.Hash}). This is the
            approved triage: the issue's own text is not part of the work.

            Summary:
            {triage.Summary}
            """;
        if (triage.Fix is { } fix)
        {
            description += $"\n\nProposed fix:\n{fix.Description}\n\nPaths it changes: {string.Join(", ", fix.Paths)}";
        }
        // An issue has no estimate: it is complex unless labelled simple (WorkerModelClass.Coding, E8). Labels are set by people with
        // triage access to the repo, never by an outsider's issue text.
        var labels = (await issues.GetAsync(RepoRef.Parse(row.Repo), row.Number, ct)).Labels;
        return new WorkSpec(new WorkStory(id, triage.Title, description, triage.Type.ToString().ToLowerInvariant(), url, Naming, closes, labels),
            null, []);
    }

    /// <summary>
    /// Claimed: the triage's route labels come off (it is being built). Merged: the issue is closed if the PR's closing keyword has
    /// not closed it, and the claim label comes off. Stopped: the claim label comes off and the comment is posted.
    /// </summary>
    public async Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct)
    {
        if (state == BoardState.Stopped && string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Stopping an item needs a comment saying why.", nameof(comment));
        }
        var (repo, number) = await IssueAsync(id, ct);
        switch (state)
        {
            case BoardState.Claimed:
                await issues.RemoveLabelAsync(repo, number, IssueLabels.AwaitingApproval, ct);
                await issues.RemoveLabelAsync(repo, number, IssueLabels.NeedsHuman, ct);
                break;
            case BoardState.Merged:
                if ((await issues.GetAsync(repo, number, ct)).Open)
                {
                    await issues.CloseAsync(repo, number, ct);
                }
                await issues.RemoveLabelAsync(repo, number, IssueLabels.Claimed, ct);
                break;
            case BoardState.Stopped:
                await issues.RemoveLabelAsync(repo, number, IssueLabels.Claimed, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
        if (comment is not null)
        {
            await CommentAsync(id, comment, ct);
        }
    }

    public async Task CommentAsync(int id, string text, CancellationToken ct)
    {
        var (repo, number) = await IssueAsync(id, ct);
        await issues.CommentAsync(repo, number, WorkSourceComments.Attributed(text), ct);
    }

    /// <summary>Comments the links on the issue, unless a comment of the factory's App already carries every one of them.</summary>
    public async Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var (repo, number) = await IssueAsync(id, ct);
        var ours = (await issues.ListCommentsAsync(repo, number, ct)).Where(c => c.AppId == issues.AppId).ToList();
        if (ours.Any(c => urls.All(u => c.Body.Contains(u, StringComparison.Ordinal))))
        {
            return;
        }
        await issues.CommentAsync(repo, number, WorkSourceComments.Attributed(
            $"The factory is working on this issue:\n\n{string.Join("\n", urls.Select(u => $"- {u}"))}"), ct);
    }

    public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
        throw new NotSupportedException("A GitHub issue has no plan children: plans are Shortcut stories.");

    private bool Watched(string repo) => watched.Any(r => string.Equals(r.FullName, repo, StringComparison.OrdinalIgnoreCase));

    private static Task<GitHubIssue?> RowAsync(LedgerDbContext db, int id, CancellationToken ct) =>
        db.GitHubIssues.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);

    private async Task<(RepoRef Repo, int Number)> IssueAsync(int id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await RowAsync(db, id, ct) ?? throw new InvalidOperationException($"{Naming.Format(id)} is no GitHub issue the factory knows.");
        return (RepoRef.Parse(row.Repo), row.Number);
    }

    private async Task<TriageRecord?> ReleasedAsync(LedgerDbContext db, int id, CancellationToken ct)
    {
        var (source, externalId) = (Naming.Source, Naming.Format(id));
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.Source == source && i.ExternalId == externalId, ct);
        if (item is null)
        {
            return null;
        }
        var history = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
        return IssueIntake.ReleasedTriage(history);
    }
}
