using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>
/// A stop that cannot be finished yet (sc-25876: a plan item whose stories are not all stopped — one is running, so its own run
/// stops it, or its stop failed). The item keeps its Stopping control; the next run, poll or <c>factory stop</c> goes on.
/// </summary>
public sealed class StopPendingException(string message) : Exception(message);

/// <summary>
/// Stops an item whose worker is no longer running: turns its open PRs back into drafts, moves its story to the
/// Backlog with a comment (releasing the claim), records Cancelled and clears its Stopping control. Each step is
/// a checkpoint before the next (E3), so a stop that fails half way (e.g. GitHub down) is finished by the next
/// attempt without repeating what was done; until then the item keeps its Stopping control, which every run and
/// poll honours first. Nothing is merged, closed or deleted: the branch and PR stay.
/// A plan item (sc-25876: its ledger records the stories of its plan) is stopped with its unfinished stories, each stopped the same
/// way on its own rows first (<see cref="StopStoriesAsync"/>); the plan item is cancelled, and its control cleared, only once
/// every one of them is. A plan of several repositories (sc-25873) has a changeset in each: the open pull request of every one is
/// drafted, and its record (<c>plan-delivery</c>) and comment say which repositories' changesets are on their default branch and
/// which are not — those merged stay merged.
/// </summary>
public sealed class ItemStopper(IWorkSource source, WorkLedger ledger, IRunLocks locks, IPullRequests pullRequests, IControls controls, TextWriter log)
{
    /// <summary>
    /// Stops the item now unless a run holds it, in which case that run stops it (it watches the item's control).
    /// An item whose last worker was started by a run that crashed is not finished here (this process cannot stop
    /// that worker): it keeps its Stopping control and the next run of it stops the worker, then finishes the stop.
    /// Returns what happened; not <see cref="ControlResult.Ok"/> when the stop is left unfinished — also for a plan item whose run is
    /// active, which finishes the stop only when its evaluation ends.
    /// </summary>
    public async Task<ControlResult> DraftRepositoryAsync(int storyId, RepoRef repository, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        if (await ledger.FindAsync(source.Naming.Source, id, ct) is not { } item)
            return new ControlResult(true, $"{id}: no coordinator in the ledger to draft.");
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
            return new ControlResult(false, $"{id}: repository stop NOT finished: its run is active; the repository stays manually paused. Retry `factory stop --repo {repository}` after that run stops at its control boundary.");
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
        var history = await ledger.HistoryAsync(item, ct);
        if (RunPipeline.CrashedWorkerMayBeRunning(history))
            return new ControlResult(false, $"{id}: repository stop NOT finished: a crashed run may have left a worker. The repository stays manually paused; stop that worker before retrying the repository Stop.");
        // A coordinator is not cancelled, and no item Stopping control is written: those would cascade to other repositories.
        foreach (var changeset in Changeset.All(history).Where(c => RepoRef.Parse(c.Repo).Is(repository)))
        {
            var urls = await pullRequests.ConvertOpenToDraftAsync(repository, changeset.Branch, ct);
            await ledger.CheckpointAsync(item, "repository-prs-drafted", null, $"{repository}: {string.Join(' ', urls)}", ct);
        }
        return new ControlResult(true, $"{id}: only {repository}'s changeset PRs drafted; coordinator retained, other repositories unchanged.");
    }

    public async Task<ControlResult> StopAsync(int storyId, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        if (await ledger.FindAsync(source.Naming.Source, id, ct) is not { } item)
        {
            await controls.ClearAsync(ControlScope.Item(id), ct);
            return new ControlResult(true, $"{id} is not in the factory ledger; nothing to stop.");
        }
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            // A run that drives a worker or the gate watches the item's control and stops it at once. The run of a plan item that
            // waits on its stories (sc-25876) only evaluates them: it sees the Stop when the evaluation ends. Until then nothing of the plan is stopped, and the answer says so.
            return (await ledger.HistoryAsync(item, ct)).Any(e => e.Step == RunPipeline.Steps.PlanStories)
                ? new ControlResult(false,
                    $"{id}: stop NOT finished: it is a plan and a run of it is active. It stays Stopping; that run finishes the stop when its "
                    + $"evaluation ends, or the next run of it does (the intake loop's next poll, or `factory run {id}`). Until then its stories are not stopped.")
                : new ControlResult(true, $"{id} is running; its run stops it at once.");
        }
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
        if (!Lifecycle.IsTerminal(item.State) && RunPipeline.CrashedWorkerMayBeRunning(await ledger.HistoryAsync(item, ct)))
        {
            return new ControlResult(false,
                $"{id}: stop NOT finished: its worker may still be running (the run that started it crashed). It stays Stopping; "
                + $"the next run of it (the intake loop's next poll, or `factory run {id}`) stops the worker and finishes the stop.");
        }
        return new ControlResult(true, await StopLockedAsync(item, storyId, ct));
    }

    /// <summary>
    /// Stops the item. The caller holds its run lock and its worker is not running. A plan item whose stories cannot all be
    /// stopped yet throws <see cref="StopPendingException"/> with nothing of the plan item itself changed. A story with no Stop of
    /// its own that is stopped because its plan is (sc-25876) is stopped as the plan's cascade stops it (<see cref="StopStoryAsync"/>).
    /// </summary>
    public async Task<string> StopLockedAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        var scope = ControlScope.Item(item.ExternalId);
        if (Lifecycle.IsTerminal(item.State))
        {
            await controls.ClearAsync(scope, ct);
            return $"{id} is already {item.State}.";
        }
        var history = await ledger.HistoryAsync(item, ct);
        var own = await controls.GetAsync(scope, ct);
        if (own?.State != ControlState.Stopping && await StoppingPlanAsync(item, ct) is { } of)
        {
            return await StopStoryAsync(item, storyId, StoppedWith(of.By, of.Plan), history, ct) switch
            {
                StoryStop.Merged => $"{id} has merged; its plan {of.Plan.ExternalId} is being stopped and leaves it as it is.",
                StoryStop.MergedOutside =>
                    $"{id}: its pull request was merged outside the factory; its plan {of.Plan.ExternalId} is being stopped and leaves it as it is.",
                StoryStop.Returned => $"{id} stopped with its plan {of.Plan.ExternalId}: Cancelled; it was never started and is back in the backlog.",
                _ => $"{id} stopped with its plan {of.Plan.ExternalId}: Cancelled, claim released.",
            };
        }
        var by = own?.ChangedBy ?? "the factory";
        PlanStop? plan = null;
        if (PlanRelease.StoriesOf(history.LastOrDefault(e => e.Step == RunPipeline.Steps.PlanStories)?.Detail) is { Count: > 0 } stories)
        {
            plan = await StopStoriesAsync(item, stories, history, by, ct);
            if (plan.Pending.Count > 0)
            {
                throw new StopPendingException($"{plan.Pending.Count} of its stories not stopped yet: {string.Join("; ", plan.Pending)}");
            }
        }
        return await StopItemAsync(item, storyId, by, history, plan, ct);
    }

    /// <summary>Who stopped a story of a stopped plan, as its rows and comments say it.</summary>
    private static string StoppedWith(string by, WorkItem plan) => $"{by} (with its plan {plan.ExternalId})";

    /// <summary>The plan item the story belongs to while that is being stopped (its own <c>item:</c> scope is Stopping), and who stopped it.</summary>
    private async Task<(WorkItem Plan, string By)?> StoppingPlanAsync(WorkItem item, CancellationToken ct) =>
        item.PlanItemId is { } planId && await ledger.ItemAsync(planId, ct) is { } plan
            && await controls.GetAsync(ControlScope.Item(plan.ExternalId), ct) is { State: ControlState.Stopping } row
                ? (plan, row.ChangedBy)
                : null;

    /// <summary>What stopping a plan item found and did to its stories (<see cref="StopStoriesAsync"/>).</summary>
    /// <param name="Stopped">Cancelled: by this stop, or before it.</param>
    /// <param name="Returned">Released by the plan and never claimed: moved back to the backlog with a comment, Cancelled.</param>
    /// <param name="Merged">Merged by the factory (or Done): untouched.</param>
    /// <param name="MergedOutside">Escalated because its pull request was merged by someone else: its change is on the base branch, untouched.</param>
    /// <param name="Left">Released, unknown to the ledger, and no longer as the release left it (someone took, finished or archived it): untouched.</param>
    /// <param name="Unstarted">In the backlog, never released (or moved back there): nothing written.</param>
    /// <param name="Pending">Not stopped yet, each with why.</param>
    private sealed record PlanStop(List<string> Stopped, List<string> Returned, List<string> Merged, List<string> MergedOutside, List<string> Left,
        List<string> Unstarted, List<string> Pending)
    {
        public List<string> Of(StoryStop how) => how switch
        {
            StoryStop.Returned => Returned,
            StoryStop.Merged => Merged,
            StoryStop.MergedOutside => MergedOutside,
            _ => Stopped,
        };
    }

    /// <summary>What became of one story of a plan being stopped that the ledger knows (<see cref="StopStoryAsync"/>).</summary>
    private enum StoryStop
    {
        Stopped,
        Returned,
        Merged,
        MergedOutside,
    }

    /// <summary>
    /// Stops the unfinished stories of a plan item being stopped (sc-25876), each under its own run lock (one run per item) and on
    /// its own rows, like any item: pull requests back to draft, board Stopped with a comment, Cancelled. A story the factory merged
    /// is untouched, and so is one whose pull request someone merged outside the factory (its change is on the base branch); one
    /// that was never released (or waits in the backlog again) is left there with nothing written — the plan, once Cancelled, never
    /// releases it, and its claim is refused (<see cref="PlanRelease.PlanStoppedRefusal"/>). A story the plan released that the ledger
    /// does not know was never claimed: the factory did no work on it, so it is only moved back to the backlog (with a comment
    /// saying the plan was stopped, then a Cancelled item of its own) and only while the board still shows it as the release left it
    /// (<see cref="IWorkSource.ReturnUnstartedAsync"/>); one a person took, finished or archived meanwhile is theirs and is left as
    /// it is, with nothing written anywhere. A story whose run is going is stopped by that run (it sees the plan's Stop through
    /// <see cref="IControls.EffectiveAsync"/>), and one whose worker a crashed run may have left by its next run: both are pending,
    /// and so is one whose stop failed.
    /// </summary>
    private async Task<PlanStop> StopStoriesAsync(WorkItem plan, IReadOnlyList<string> stories, List<LedgerEntry> history, string by, CancellationToken ct)
    {
        var result = new PlanStop([], [], [], [], [], [], []);
        var released = history.Where(e => e.Step == RunPipeline.Steps.PlanReleased)
            .Select(e => PlanRelease.StoriesOf(e.Detail).FirstOrDefault() ?? "").ToHashSet(StringComparer.Ordinal);
        var titles = PlanRelease.RecordedTitles(history.LastOrDefault(e => e.Step == RunPipeline.Steps.Plan)?.Detail, stories);
        var repos = PlanRelease.RecordedRepos(history.LastOrDefault(e => e.Step == RunPipeline.Steps.Plan)?.Detail, stories, RepoRef.Parse(plan.Repo));
        var known = await ledger.ItemsByIdAsync(source.Naming.Source, stories, ct);
        foreach (var story in stories)
        {
            if (!source.Naming.TryParse(story, out var storyId))
            {
                throw new InvalidOperationException($"{plan.ExternalId}'s plan records '{story}', which is not a {source.Naming.Noun} id.");
            }
            var seen = known.TryGetValue(story, out var k) ? k.Item : null;
            if (seen is not null && Settled(seen, k.History) is { } settled)
            {
                result.Of(settled).Add(story);
                continue;
            }
            try
            {
                if (seen is null)
                {
                    if (!released.Contains(story) || await source.InBacklogAsync(storyId, ct))
                    {
                        result.Unstarted.Add(story);
                    }
                    else if (!await source.ReturnUnstartedAsync(storyId, PlanRelease.ReturnedComment(story, plan.ExternalId, by), ct))
                    {
                        result.Left.Add(story);
                    }
                    else
                    {
                        // The board first, then the record: a stop that dies between the two finds the story in the backlog again.
                        var returned = await ledger.GetOrCreateAsync(source.Naming.Source, story, titles?.GetValueOrDefault(story) ?? $"Story of plan {plan.ExternalId}",
                            repos?.RepoOf(story) ?? plan.Repo, PlanRelease.ReturnedUnstarted(plan.ExternalId), ct, plan.EpicId);
                        await using var held = await locks.TryAcquireAsync(returned.Id, ct);
                        await ledger.LinkPlanAsync(returned, plan.Id, ct);
                        result.Of(await StopStoryAsync(returned, storyId, StoppedWith(by, plan), await ledger.HistoryAsync(returned, ct), ct)).Add(story);
                    }
                    continue;
                }
                var item = await ledger.FindAsync(source.Naming.Source, story, ct)
                    ?? throw new InvalidOperationException($"{story} is no longer in the ledger.");
                await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
                if (runLock is null)
                {
                    result.Pending.Add($"{story} is running (its run stops it at once)");
                    continue;
                }
                await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
                if (item.PlanItemId is null)
                {
                    await ledger.LinkPlanAsync(item, plan.Id, ct);
                }
                var rows = await ledger.HistoryAsync(item, ct);
                if (Settled(item, rows) is null && RunPipeline.CrashedWorkerMayBeRunning(rows))
                {
                    result.Pending.Add($"{story}'s worker may still be running (the run that started it crashed): its next run stops the worker and the story");
                }
                else
                {
                    result.Of(await StopStoryAsync(item, storyId, StoppedWith(by, plan), rows, ct)).Add(story);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Pending.Add($"{story}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// What a story of a plan being stopped already is, when the stop has nothing to do to it: merged by the factory (or Done),
    /// Cancelled, or Escalated because its pull request was merged outside the factory (<see cref="PlanRelease.AsMergedOutside"/>,
    /// or into its plan's changeset branch, <see cref="PlanRelease.MergedOutsideChangeset"/>: its change is on the base branch, so
    /// neither its board story nor its item is touched). Null for a story still to be stopped.
    /// </summary>
    private static StoryStop? Settled(WorkItem item, IReadOnlyList<LedgerEntry> rows) =>
        PlanRelease.MergedByFactory(rows) || item.State == WorkState.Done ? StoryStop.Merged
        : item.State == WorkState.Cancelled ? PlanRelease.IsReturnedUnstarted(rows) ? StoryStop.Returned : StoryStop.Stopped
        : PlanRelease.MergedOutsideChangeset(rows) || PlanRelease.FailureOf(rows)?.As == PlanRelease.AsMergedOutside ? StoryStop.MergedOutside
        : null;

    /// <summary>
    /// Stops one story the ledger knows because its plan is being stopped (sc-25876); the caller holds its run lock and its worker is
    /// not running. A settled story (<see cref="Settled"/>) is left as it is. A story this stop already moved back to the backlog
    /// unstarted (<see cref="PlanRelease.ReturnedUnstarted"/>: its board was written before its item existed) is only recorded
    /// Cancelled. Any other is stopped like any item, on its own rows.
    /// </summary>
    private async Task<StoryStop> StopStoryAsync(WorkItem item, int storyId, string by, List<LedgerEntry> rows, CancellationToken ct)
    {
        if (Settled(item, rows) is { } settled)
        {
            return settled;
        }
        if (PlanRelease.IsReturnedUnstarted(rows))
        {
            await ledger.RecordAsync(item, WorkState.Cancelled, null, $"stopped by {by}", ct);
            await controls.ClearAsync(ControlScope.Item(item.ExternalId), ct);
            log.WriteLine($"[cancelled] {item.ExternalId} stopped by {by}; never started: back in the backlog");
            return StoryStop.Returned;
        }
        await StopItemAsync(item, storyId, by, rows, null, ct);
        return StoryStop.Stopped;
    }

    /// <summary>The stop of one item on its own rows; <paramref name="plan"/>: what became of its stories, for a plan item.</summary>
    private async Task<string> StopItemAsync(WorkItem item, int storyId, string by, List<LedgerEntry> history, PlanStop? plan, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        var steps = history.Skip(history.FindLastIndex(e => e.Step is null) + 1).ToList();
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        // The item's pull request is its own branch's, or — a plan item's — its changeset branch's (sc-25872).
        // Every changeset of the plan, wherever it is: the item's own repository is the one it works in now, which need not hold
        // one (sc-25873: a plan whose stories are all in another repository than the plan item's first, stopped outside a pass).
        var changesets = Plans.Changeset.All(history);
        var branch = changesets.FirstOrDefault()?.Branch ?? source.Naming.BranchName(storyId);
        // A plan's story the ledger knows but the factory never claimed (parked on a refused claim, e.g. a blocker that had not
        // merged): there is no work, claim or branch of it, and its comment does not say there was.
        var unclaimed = plan is null && item.PlanItemId is not null && !history.Any(e => e.Step == RunPipeline.Steps.Claimed);

        // A plan has a changeset branch in each repository it has a story in (sc-25873): the open pull request of every one is
        // drafted, in the changeset's own repository. An item with no changeset: its own branch's, in its own repository.
        var several = changesets.Count > 1 ? Plans.Changeset.States(history, RepoRef.Parse(item.Repo), item.State) : null;
        var drafted = steps.LastOrDefault(e => e.Step == RunPipeline.Steps.PrsDrafted)?.Detail;
        if (drafted is null)
        {
            var prs = new List<string>();
            foreach (var (repo, head) in changesets.Count == 0 ? [(RepoRef.Parse(item.Repo), branch)] : changesets.Select(c => (RepoRef.Parse(c.Repo), c.Branch)))
            {
                prs.AddRange(await pullRequests.ConvertOpenToDraftAsync(repo, head, ct));
            }
            drafted = prs.Count == 0 ? "none" : string.Join(' ', prs);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PrsDrafted, session, drafted, ct);
        }
        if (!steps.Any(e => e.Step == RunPipeline.Steps.StopReported))
        {
            var prs = drafted == "none" ? "It had no open pull request." : $"Its pull request ({drafted}) is a draft again.";
            var what = plan is null
                ? $"the factory cancelled its work on it (was {item.State}) and released its claim."
                : $"the factory cancelled the plan (was {item.State}) and released its claim. {Stories(plan)}";
            // Which repositories have the change and which do not: on the record before the board is told (E4).
            if (several is not null && !steps.Any(e => e.Step == RunPipeline.Steps.PlanDelivery))
            {
                await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanDelivery, session, Plans.Changeset.DeliveryOf(several), ct);
            }
            var kept = several is null ? $"Branch {branch} is kept; nothing was merged or deleted."
                : $"Branch {branch} is kept in each of {string.Join(", ", changesets.Select(c => c.Repo))}; the stop merged and deleted nothing. "
                    + Plans.Changeset.DeliveryNote(id, several, stopped: true);
            // What the stop could not stop or remove (sc-25989: the item's worker slot is no longer configured), from its own row.
            var left = steps.LastOrDefault(e => e.Step == RunPipeline.Steps.SlotLeft)?.Detail is { } stays ? $" {stays}" : "";
            await source.ReportStateAsync(storyId, BoardState.Stopped, unclaimed
                ? PlanRelease.NeverClaimedComment(id, by)
                : $"{WorkSourceComments.Author} {id} was stopped by {by}: {what} "
                    + $"{prs} {kept}{left}", ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.StopReported, session, by, ct);
        }
        await ledger.RecordAsync(item, WorkState.Cancelled, session, $"stopped by {by}", ct);
        await controls.ClearAsync(ControlScope.Item(item.ExternalId), ct);
        log.WriteLine($"[cancelled] {id} stopped by {by}; PRs drafted: {drafted}");
        return unclaimed
            ? $"{id} stopped: Cancelled; the factory never claimed it and did no work on it."
            : $"{id} stopped: Cancelled, PRs drafted: {drafted}, claim released." + (plan is null ? "" : $" {Stories(plan)}");
    }

    /// <summary>
    /// What became of a stopped plan's stories, as its comment and the stop's result say it (ids only; a shelved story in the
    /// board's own word, <see cref="IWorkSource.Shelved"/>: archived on Shortcut, Closed on Kanban).
    /// </summary>
    private string Stories(PlanStop plan)
    {
        static string List(List<string> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);
        static string Some(string what, List<string> ids) => ids.Count == 0 ? "" : $"{what}: {List(ids)}. ";
        return $"Stories stopped with it: {List(plan.Stopped)}. "
            + Some("Released but never started (moved back to the backlog; the factory did no work on them)", plan.Returned)
            + $"Already merged (left as they are): {List(plan.Merged)}. "
            + Some("Merged outside the factory (left as they are: their change is on the base branch)", plan.MergedOutside)
            + Some($"Taken, finished or {source.Shelved.Is} by someone else since their release (left as they are)", plan.Left)
            + $"Never started (left in the backlog; the factory will not release or start them): {List(plan.Unstarted)}.";
    }
}
