using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>What a control action did. <see cref="Ok"/> is false when it was refused or did not take full effect.</summary>
public sealed record ControlResult(bool Ok, string Message)
{
    /// <summary><c>factory pause|continue|stop</c>'s exit code: non-zero on a refusal or an unfinished action.</summary>
    public int ExitCode => Ok ? 0 : 1;
}

/// <summary>Stops one item now if no run holds it (<see cref="ItemStopper.StopAsync"/>); returns what happened.</summary>
public interface IItemStops
{
    /// <summary>Stops one Shortcut story.</summary>
    Task<ControlResult> StopAsync(int storyId, CancellationToken ct);

    /// <summary>
    /// Stops one item of any source. The default stops a Shortcut story with <see cref="StopAsync(int, CancellationToken)"/> and
    /// leaves another source's item to its next run (its Stopping control is already set), saying so.
    /// </summary>
    Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct) =>
        item.Naming == ItemNaming.Shortcut
            ? StopAsync(item.Id, ct)
            : Task.FromResult(new ControlResult(true, $"{item}: stop requested; its next run stops it."));
}

/// <summary>
/// Pause, Continue and Stop at a scope: what <c>factory pause|continue|stop</c> and the dashboard's buttons do.
/// Pause and Continue only write the scope's control; running pipelines (any process) honour it within about a
/// second, and the intake loop resumes a user-paused item once nothing pauses it. Stop marks each non-terminal
/// item in the scope Stopping, then stops it at once if no run holds it (with <paramref name="stops"/>); a running
/// item is stopped by its own run. Without <paramref name="stops"/> the next run or poll of the item stops it.
/// At epic scope, active items the ledger has no epic for (recorded before epics were) are looked up on the board
/// (<paramref name="source"/>) and their epic recorded under their run lock (<paramref name="locks"/>); any that
/// cannot be resolved are reported, never silently skipped (E10).
/// </summary>
public sealed class ControlActions(
    IControls controls, IDbContextFactory<LedgerDbContext> contexts, IItemStops? stops = null, IWorkSource? source = null, IRunLocks? locks = null)
{
    public async Task<ControlResult> PauseAsync(string scope, string by, CancellationToken ct)
    {
        if (scope == ControlScope.Usage)
        {
            return UsageOnlyContinues;
        }
        await controls.SetAsync(scope, ControlState.Paused, by, ct);
        var message = $"{scope} paused: nothing new starts there, and running workers stop at their next tool call.";
        if (ControlScope.EpicOf(scope) is not { } epic)
        {
            return new ControlResult(true, message);
        }
        var resolved = await ResolveUnknownEpicsAsync(epic, ct);
        // A running item's run watches the epic it read when it started, so one recorded only now is not paused by this.
        var notPaused = resolved.Unknown.Concat(resolved.Running).ToList();
        return notPaused.Count == 0
            ? new ControlResult(true, message)
            : new ControlResult(false, $"{message}\n{notPaused.Count} item(s) with an unknown epic NOT paused: {string.Join(", ", notPaused)} "
                + "(the board could not be read, or the item is running); pause them with `factory pause --item`.");
    }

    public async Task<ControlResult> ContinueAsync(string scope, string by, CancellationToken ct)
    {
        if ((await controls.GetAsync(scope, ct))?.State == ControlState.Stopping)
        {
            return new ControlResult(false, $"{scope} is being stopped; it cannot be continued.");
        }
        await controls.SetAsync(scope, ControlState.Running, by, ct);
        return new ControlResult(true, scope == ControlScope.Usage
            ? "usage pause lifted early: work starts again now (a worker that hits the limit again pauses the factory once more)."
            : $"{scope} continued.");
    }

    public async Task<ControlResult> StopAsync(string scope, string by, CancellationToken ct)
    {
        if (!ControlScope.IsValid(scope))
        {
            throw new ArgumentException($"'{scope}' is not a control scope.", nameof(scope));
        }
        if (scope == ControlScope.Usage)
        {
            return UsageOnlyContinues;
        }
        var stories = new List<ItemRef>();
        var unknown = new List<string>();
        if (ControlScope.ItemOf(scope) is { } story)
        {
            stories.Add(story);
        }
        else
        {
            var epic = ControlScope.EpicOf(scope);
            await using var db = await contexts.CreateDbContextAsync(ct);
            // The factory scope stops every source's items; an epic is a Shortcut epic, so only stories are in one.
            var items = await db.WorkItems.AsNoTracking()
                .Where(i => (epic == null || i.Source == RunPipeline.Source) && i.State != WorkState.Done && i.State != WorkState.Cancelled
                    && (epic == null || i.EpicId == epic))
                .OrderBy(i => i.Id)
                .Select(i => new { i.Source, i.ExternalId })
                .ToListAsync(ct);
            var found = items.Select(i => ItemNaming.ParseAny(i.ExternalId) is { } r && r.Naming.Source == i.Source ? r : null).ToList();
            if (epic is { } epicId)
            {
                // An item is stopped through its own control, so one in the epic is stopped whether or not its epic could be recorded.
                var resolved = await ResolveUnknownEpicsAsync(epicId, ct);
                found.AddRange(resolved.InEpic.Select(e => ItemNaming.Shortcut.TryParse(e, out var id) ? new ItemRef(ItemNaming.Shortcut, id) : null));
                unknown = resolved.Unknown;
            }
            stories.AddRange(found.OfType<ItemRef>().Distinct());
        }
        var ok = unknown.Count == 0;
        var results = new List<string>();
        foreach (var item in stories)
        {
            await controls.SetAsync(ControlScope.Item(item.ToString()), ControlState.Stopping, by, ct);
            if (stops is null)
            {
                results.Add($"{item}: stop requested; its next run stops it.");
                continue;
            }
            try
            {
                var stopped = await stops.StopAsync(item, ct);
                ok &= stopped.Ok;
                results.Add(stopped.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The item keeps its Stopping control: the intake loop's next poll (or a retry) finishes the stop.
                ok = false;
                results.Add($"{item}: stop NOT finished ({ex.Message}); it stays Stopping and the next poll or `factory stop` retries it.");
            }
        }
        if (unknown.Count > 0)
        {
            results.Add($"{unknown.Count} item(s) with an unknown epic NOT stopped: {string.Join(", ", unknown)} "
                + "(the board could not be read); stop them with `factory stop --item` or retry.");
        }
        else if (stories.Count == 0)
        {
            return new ControlResult(true, $"{scope}: no active items to stop.");
        }
        return new ControlResult(ok, string.Join('\n', results));
    }

    /// <summary>The usage pause is set by the factory itself (<see cref="UsagePause"/>); a user may only lift it early.</summary>
    private static readonly ControlResult UsageOnlyContinues =
        new(false, "The usage pause is set and lifted by the factory; it can only be continued early (`factory continue --usage`). Use --factory to pause or stop the whole factory.");

    private sealed record EpicResolution(List<string> InEpic, List<string> Unknown, List<string> Running);

    /// <summary>
    /// Looks up the epic of every active item the ledger has none for and records it (under the item's run lock;
    /// a running item's own run records it when it starts). Returns the items found in <paramref name="epic"/>,
    /// those whose epic could not be read, and those in the epic that are running with no epic recorded.
    /// </summary>
    private async Task<EpicResolution> ResolveUnknownEpicsAsync(long epic, CancellationToken ct)
    {
        var resolution = new EpicResolution([], [], []);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ledger = new WorkLedger(db, TimeProvider.System);
        var items = await db.WorkItems
            .Where(i => i.Source == RunPipeline.Source && i.State != WorkState.Done && i.State != WorkState.Cancelled && i.EpicId == null)
            .OrderBy(i => i.Id)
            .ToListAsync(ct);
        foreach (var item in items)
        {
            if (source is null || !StoryId.TryParse(item.ExternalId, out var id))
            {
                resolution.Unknown.Add(item.ExternalId);
                continue;
            }
            long? itemEpic;
            try
            {
                itemEpic = (await source.ReadSpecAsync(id, ct)).Epic?.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolution.Unknown.Add(item.ExternalId);
                continue;
            }
            if (itemEpic is null)
            {
                continue; // in no epic
            }
            var recorded = false;
            if (locks is not null)
            {
                await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
                if (runLock is not null)
                {
                    await db.Entry(item).ReloadAsync(ct);
                    if (item.EpicId is null)
                    {
                        await ledger.RefreshAsync(item, item.Title, item.Repo, itemEpic, ct);
                    }
                    recorded = true;
                }
            }
            if (itemEpic == epic)
            {
                resolution.InEpic.Add(item.ExternalId);
                if (!recorded)
                {
                    resolution.Running.Add(item.ExternalId);
                }
            }
        }
        return resolution;
    }
}
