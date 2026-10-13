using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
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
/// every one of them is.
/// </summary>
public sealed class ItemStopper(IWorkSource source, WorkLedger ledger, IRunLocks locks, IPullRequests pullRequests, IControls controls, TextWriter log)
{
    /// <summary>
    /// Stops the item now unless a run holds it, in which case that run stops it (it watches the item's control).
    /// An item whose last worker was started by a run that crashed is not finished here (this process cannot stop
    /// that worker): it keeps its Stopping control and the next run of it stops the worker, then finishes the stop.
    /// Returns what happened; not <see cref="ControlResult.Ok"/> when the stop is left unfinished.
    /// </summary>
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
            return new ControlResult(true, $"{id} is running; its run stops it at once.");
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
    /// stopped yet throws <see cref="StopPendingException"/> with nothing of the plan item itself changed.
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
        var by = await StoppedByAsync(item, ct);
        var history = await ledger.HistoryAsync(item, ct);
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

    /// <summary>What stopping a plan item found and did to its stories (<see cref="StopStoriesAsync"/>).</summary>
    /// <param name="Stopped">Cancelled: by this stop, or before it.</param>
    /// <param name="Merged">Merged by the factory (or Done): untouched.</param>
    /// <param name="Unstarted">In the backlog, never released (or moved back there): nothing written.</param>
    /// <param name="Pending">Not stopped yet, each with why.</param>
    private sealed record PlanStop(List<string> Stopped, List<string> Merged, List<string> Unstarted, List<string> Pending);

    /// <summary>
    /// Stops the unfinished stories of a plan item being stopped (sc-25876), each under its own run lock (one run per item) and on
    /// its own rows, like any item: pull requests back to draft, board Stopped with a comment, Cancelled. A story the factory merged
    /// is untouched; one that was never released (or waits in the backlog again) is left there with nothing written — the plan,
    /// once Cancelled, never releases it, and its claim is refused (<see cref="PlanRelease.PlanStoppedRefusal"/>). A story the plan
    /// released that nothing had started yet gets its ledger item here, so that it too is stopped and recorded. A story whose run
    /// is going is stopped by that run (it sees the plan's Stop through <see cref="IControls.EffectiveAsync"/>), and one whose worker
    /// a crashed run may have left by its next run: both are pending, and so is one whose stop failed.
    /// </summary>
    private async Task<PlanStop> StopStoriesAsync(WorkItem plan, IReadOnlyList<string> stories, List<LedgerEntry> history, string by, CancellationToken ct)
    {
        var result = new PlanStop([], [], [], []);
        var released = history.Where(e => e.Step == RunPipeline.Steps.PlanReleased)
            .Select(e => PlanRelease.StoriesOf(e.Detail).FirstOrDefault() ?? "").ToHashSet(StringComparer.Ordinal);
        var titles = PlanRelease.RecordedTitles(history.LastOrDefault(e => e.Step == RunPipeline.Steps.Plan)?.Detail, stories);
        var known = await ledger.ItemsByIdAsync(source.Naming.Source, stories, ct);
        foreach (var story in stories)
        {
            if (!source.Naming.TryParse(story, out var storyId))
            {
                throw new InvalidOperationException($"{plan.ExternalId}'s plan records '{story}', which is not a {source.Naming.Noun} id.");
            }
            var seen = known.TryGetValue(story, out var k) ? k.Item : null;
            if (seen is not null && (PlanRelease.Satisfied(k.History) || seen.State == WorkState.Done))
            {
                result.Merged.Add(story);
                continue;
            }
            if (seen is { State: WorkState.Cancelled })
            {
                result.Stopped.Add(story);
                continue;
            }
            try
            {
                if (seen is null && (!released.Contains(story) || await source.InBacklogAsync(storyId, ct)))
                {
                    result.Unstarted.Add(story);
                    continue;
                }
                var item = seen is null
                    ? await ledger.GetOrCreateAsync(source.Naming.Source, story, titles?.GetValueOrDefault(story) ?? $"Story of plan {plan.ExternalId}",
                        plan.Repo, $"released by its plan {plan.ExternalId}; stopped with it before it was started", ct, plan.EpicId)
                    : await ledger.FindAsync(source.Naming.Source, story, ct)
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
                if (PlanRelease.Satisfied(rows) || item.State == WorkState.Done)
                {
                    result.Merged.Add(story); // it merged meanwhile
                }
                else if (item.State == WorkState.Cancelled)
                {
                    result.Stopped.Add(story);
                }
                else if (RunPipeline.CrashedWorkerMayBeRunning(rows))
                {
                    result.Pending.Add($"{story}'s worker may still be running (the run that started it crashed): its next run stops the worker and the story");
                }
                else
                {
                    await StopItemAsync(item, storyId, $"{by} (with its plan {plan.ExternalId})", rows, null, ct);
                    result.Stopped.Add(story);
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
    /// Who stopped the item: whoever set its own Stopping control, else — for a story of a plan being stopped (sc-25876) — whoever
    /// set its plan's.
    /// </summary>
    private async Task<string> StoppedByAsync(WorkItem item, CancellationToken ct)
    {
        if ((await controls.GetAsync(ControlScope.Item(item.ExternalId), ct))?.ChangedBy is { } own)
        {
            return own;
        }
        return item.PlanItemId is { } planId && await ledger.ItemAsync(planId, ct) is { } plan
            && await controls.GetAsync(ControlScope.Item(plan.ExternalId), ct) is { State: ControlState.Stopping } row
                ? $"{row.ChangedBy} (with its plan {plan.ExternalId})"
                : "the factory";
    }

    /// <summary>The stop of one item on its own rows; <paramref name="plan"/>: what became of its stories, for a plan item.</summary>
    private async Task<string> StopItemAsync(WorkItem item, int storyId, string by, List<LedgerEntry> history, PlanStop? plan, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        var steps = history.Skip(history.FindLastIndex(e => e.Step is null) + 1).ToList();
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        var branch = source.Naming.BranchName(storyId);

        var drafted = steps.LastOrDefault(e => e.Step == RunPipeline.Steps.PrsDrafted)?.Detail;
        if (drafted is null)
        {
            var prs = await pullRequests.ConvertOpenToDraftAsync(RepoRef.Parse(item.Repo), branch, ct);
            drafted = prs.Count == 0 ? "none" : string.Join(' ', prs);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PrsDrafted, session, drafted, ct);
        }
        if (!steps.Any(e => e.Step == RunPipeline.Steps.StopReported))
        {
            var prs = drafted == "none" ? "It had no open pull request." : $"Its pull request ({drafted}) is a draft again.";
            var what = plan is null
                ? $"the factory cancelled its work on it (was {item.State}) and released its claim."
                : $"the factory cancelled the plan (was {item.State}) and released its claim. {Stories(plan)}";
            await source.ReportStateAsync(storyId, BoardState.Stopped,
                $"{WorkSourceComments.Author} {id} was stopped by {by}: {what} "
                + $"{prs} Branch {branch} is kept; nothing was merged or deleted.", ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.StopReported, session, by, ct);
        }
        await ledger.RecordAsync(item, WorkState.Cancelled, session, $"stopped by {by}", ct);
        await controls.ClearAsync(ControlScope.Item(item.ExternalId), ct);
        log.WriteLine($"[cancelled] {id} stopped by {by}; PRs drafted: {drafted}");
        return $"{id} stopped: Cancelled, PRs drafted: {drafted}, claim released." + (plan is null ? "" : $" {Stories(plan)}");
    }

    /// <summary>What became of a stopped plan's stories, as its comment and the stop's result say it (ids only).</summary>
    private static string Stories(PlanStop plan)
    {
        static string List(List<string> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);
        return $"Stories stopped with it: {List(plan.Stopped)}. Already merged (left as they are): {List(plan.Merged)}. "
            + $"Never started (left in the backlog; the factory will not release or start them): {List(plan.Unstarted)}.";
    }
}
