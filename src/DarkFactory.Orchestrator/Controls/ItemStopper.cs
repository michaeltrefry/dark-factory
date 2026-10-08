using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>
/// Stops an item whose worker is no longer running: turns its open PRs back into drafts, moves its story to the
/// Backlog with a comment (releasing the claim), records Cancelled and clears its Stopping control. Each step is
/// a checkpoint before the next (E3), so a stop that fails half way (e.g. GitHub down) is finished by the next
/// attempt without repeating what was done; until then the item keeps its Stopping control, which every run and
/// poll honours first. Nothing is merged, closed or deleted: the branch and PR stay.
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

    /// <summary>Stops the item. The caller holds its run lock and its worker is not running.</summary>
    public async Task<string> StopLockedAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        var scope = ControlScope.Item(item.ExternalId);
        if (Lifecycle.IsTerminal(item.State))
        {
            await controls.ClearAsync(scope, ct);
            return $"{id} is already {item.State}.";
        }
        var by = (await controls.GetAsync(scope, ct))?.ChangedBy ?? "the factory";
        var history = await ledger.HistoryAsync(item, ct);
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
            await source.ReportStateAsync(storyId, BoardState.Stopped,
                $"{WorkSourceComments.Author} {id} was stopped by {by}: the factory cancelled its work on it (was {item.State}) and released its claim. "
                + $"{prs} Branch {branch} is kept; nothing was merged or deleted.", ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.StopReported, session, by, ct);
        }
        await ledger.RecordAsync(item, WorkState.Cancelled, session, $"stopped by {by}", ct);
        await controls.ClearAsync(scope, ct);
        log.WriteLine($"[cancelled] {id} stopped by {by}; PRs drafted: {drafted}");
        return $"{id} stopped: Cancelled, PRs drafted: {drafted}, claim released.";
    }
}
