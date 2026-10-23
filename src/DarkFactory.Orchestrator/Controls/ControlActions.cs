using DarkFactory.Orchestrator.Ledger;
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
    /// <summary>Stops one item of any source (sc-25982), on the source its id names.</summary>
    Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct);

    /// <summary>Draft only this repository's changeset PRs of a retained multi-repository coordinator.</summary>
    Task<ControlResult> DraftRepositoryAsync(ItemRef item, RepoRef repository, CancellationToken ct) =>
        Task.FromResult(new ControlResult(false, $"{item}: repository stop NOT finished: scoped changeset drafting is unavailable; the repository stays manually paused."));
}

/// <summary>
/// Pause, Continue and Stop at a scope: what <c>factory pause|continue|stop</c> and the dashboard's buttons do.
/// Pause and Continue only write the scope's control; running pipelines (any process) honour it within about a
/// second, and the intake loop resumes a user-paused item once nothing pauses it. Stop marks each non-terminal
/// item in the scope Stopping, then stops it at once if no run holds it (with <paramref name="stops"/>); a running
/// item is stopped by its own run. Without <paramref name="stops"/> the next run or poll of the item stops it.
/// A plan item's scope (sc-25876) is its own <c>item:&lt;id&gt;</c>, and reaches its stories: Pause and Continue through
/// <see cref="IControls.EffectiveAsync"/>, Stop through <see cref="ItemStopper"/>, which stops the plan's unfinished stories before the
/// plan item and leaves the stop unfinished (not <see cref="ControlResult.Ok"/>) while one of them is still running.
/// An epic scope names an epic of one source (<see cref="EpicRef"/>, sc-25985: <c>epic:12</c> a Shortcut epic, <c>epic:kb-3</c> a
/// Kanban epic) and reaches that source's items only. Active items of that source the ledger has no epic for (recorded before
/// epics were) are looked up on their own board (<paramref name="sources"/>: the source of a naming, or null when it is not
/// configured; sc-25982) and their epic recorded under their run lock (<paramref name="locks"/>); any that cannot be resolved are
/// reported, never silently skipped (E10). An epic its source says cannot exist (<see cref="IWorkSource.KnowsEpicAsync"/>: a Kanban
/// key the ledger never gave out) is refused before anything is written — also when that source is not configured, from the
/// ledger alone (<see cref="WorkSourceKind.KnowsEpic"/>).
/// </summary>
public sealed class ControlActions(
    IControls controls, IDbContextFactory<LedgerDbContext> contexts, IItemStops? stops = null, Func<ItemNaming, IWorkSource?>? sources = null,
    IRunLocks? locks = null)
{
    public async Task<ControlResult> PauseAsync(string scope, string by, CancellationToken ct)
    {
        if (scope == ControlScope.Usage)
        {
            return UsageOnlyContinues;
        }
        if (scope == ControlScope.Freeze)
        {
            return FreezeOnlyContinues;
        }
        if (ControlScope.EpicOf(scope) is { } named && await UnknownEpicAsync(named, "paused", ct) is { } refusal)
        {
            return refusal;
        }
        await controls.SetAsync(scope, ControlState.Paused, by, ct);
        var message = $"{scope} paused: nothing new starts there, and running workers stop at their next tool call.";
        if (ControlScope.EpicOf(scope) is not { } epic)
        {
            return new ControlResult(true, ControlScope.ItemOf(scope) is { } item && await IsPlanAsync(item, ct)
                ? $"{message} {item} is a plan: none of its stories is released or claimed, and those in flight pause too, until it is continued."
                : message);
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
        var row = await controls.GetAsync(scope, ct);
        if (row?.State == ControlState.Stopping)
        {
            return new ControlResult(false, $"{scope} is being stopped; it cannot be continued.");
        }
        if (scope == ControlScope.Freeze && row?.State is null or ControlState.Running)
        {
            // A Continue resets what the triggers count from: one with no freeze to clear would silently discard that evidence.
            return new ControlResult(false, "the factory is not frozen");
        }
        await controls.SetAsync(scope, ControlState.Running, by, ct);
        return new ControlResult(true, scope == ControlScope.Usage
            ? "usage pause lifted early: work starts again now (a worker that hits the limit again pauses the factory once more)."
            : scope == ControlScope.Freeze
            ? "freeze cleared: work starts again now. Only what happens from now on (new escalations, merges, fix rounds) can freeze the factory again."
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
        if (scope == ControlScope.Freeze)
        {
            return FreezeOnlyContinues;
        }
        var stories = new List<ItemRef>();
        var retainedPlans = new List<string>();
        if (ControlScope.RepositoryOf(scope) is not null)
        {
            // Hold future releases/delivery here without turning a repository Stop into a global plan Stop.
            // Only a human Continue lifts this manual pause; cancelled children are never resurrected by it.
            await controls.SetAsync(scope, ControlState.Paused, by, ct);
        }
        var unknown = new List<string>();
        if (ControlScope.ItemOf(scope) is { } story)
        {
            if (stops is not null && sources is not null && sources(story.Naming) is null)
            {
                // Before anything is written: a stop that cannot reach the item's board would leave it Stopping with nothing to
                // finish it (no lane runs a source without its credential) and no Continue to take it back.
                return new ControlResult(false, $"{story}: NOT stopped, and nothing was written. {WorkSourceRegistry.NeedsCredential(story.Naming)}");
            }
            stories.Add(story);
        }
        else
        {
            var epic = ControlScope.EpicOf(scope);
            if (epic is not null && await UnknownEpicAsync(epic, "stopped", ct) is { } refusal)
            {
                return refusal;
            }
            await using var db = await contexts.CreateDbContextAsync(ct);
            // The factory scope stops every source's items; an epic scope only those of the epic's own source (the same number
            // is another epic in another source).
            var (epicSource, epicNumber) = (epic?.Naming.Source, epic?.Id);
            var repository = ControlScope.RepositoryOf(scope)?.Key;
            var items = await db.WorkItems.AsNoTracking()
                .Where(i => i.State != WorkState.Done && i.State != WorkState.Cancelled
                    && (repository == null || i.Repo.ToLower() == repository
                        || db.LedgerEntries.Any(e => e.WorkItemId == i.Id && e.Step == RunPipeline.Steps.PlanStories))
                    && (epicSource == null || (i.Source == epicSource && i.EpicId == epicNumber)))
                .OrderBy(i => i.Id)
                .Select(i => new { i.Id, i.Source, i.ExternalId, i.Repo })
                .ToListAsync(ct);
            if (repository is not null)
            {
                var ids = items.Select(i => i.Id).ToArray();
                var rows = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct);
                items.RemoveAll(i =>
                {
                    var history = rows.Where(e => e.WorkItemId == i.Id).ToList();
                    var children = Plans.PlanRelease.StoriesOf(history.LastOrDefault(e => e.Step == RunPipeline.Steps.PlanStories)?.Detail);
                    if (children.Count == 0) return false;
                    var repos = Plans.PlanRelease.RecordedRepos(history.LastOrDefault(e => e.Step == RunPipeline.Steps.Plan)?.Detail,
                        children, RepoRef.Parse(i.Repo));
                    if (repos is not null && repos.Order.All(r => RepoRef.Parse(r).Key == repository)) return false;
                    if (repos is not null && !repos.Order.Any(r => RepoRef.Parse(r).Key == repository)) return true;
                    retainedPlans.Add(i.ExternalId);
                    return true;
                });
            }
            var found = items.Select(i => ItemNaming.ParseAny(i.ExternalId) is { } r && r.Naming.Source == i.Source ? r : null).ToList();
            if (epic is not null)
            {
                // An item is stopped through its own control, so one in the epic is stopped whether or not its epic could be recorded.
                var resolved = await ResolveUnknownEpicsAsync(epic, ct);
                found.AddRange(resolved.InEpic.Select(ItemNaming.ParseAny));
                unknown = resolved.Unknown;
            }
            stories.AddRange(found.OfType<ItemRef>().Distinct());
        }
        // A recovery identity exists before intake, and its contributing plan may already be completed.
        // Include persisted ancestry, not only active WorkItems, so Stop cannot leave a pending replacement behind.
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var repository = ControlScope.RepositoryOf(scope)?.Key;
            var correctionIds = await db.RecoveryCorrections.AsNoTracking()
                .Where(c => (c.State == "Pending" || c.State == "Claimed")
                    && (scope == ControlScope.Factory || repository != null && c.Repo == repository
                        || db.RecoveryAncestors.Any(a => a.CorrectionId == c.Id && a.Scope == scope)))
                .Select(c => c.Id).ToListAsync(ct);
            stories.AddRange(correctionIds.Select(id => new ItemRef(ItemNaming.Recovery, id)));
        }
        var ok = unknown.Count == 0;
        var results = new List<string>();
        if (ControlScope.RepositoryOf(scope) is not null)
            results.Add($"{scope} manually paused until human Continue; only this repository's active items are stopped."
                + (retainedPlans.Count == 0 ? "" : $" Multi-repository plans retained: {string.Join(", ", retainedPlans)}; other repositories continue, cancelled children keep their plans incomplete."));
        foreach (var item in stories.Distinct())
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
                // The item keeps its Stopping control: the intake loop's next poll (or a retry) finishes the stop — unless its
                // source has no credential: no lane runs that source's items, so nothing retries until the credential is back.
                ok = false;
                results.Add(ex is MissingCredentialException
                    ? $"{item}: stop NOT finished ({ex.Message}); it stays Stopping, and nothing finishes the stop until that credential is set "
                        + "(then the next poll or `factory stop` does)."
                    : $"{item}: stop NOT finished ({ex.Message}); it stays Stopping and the next poll or `factory stop` retries it.");
            }
        }
        foreach (var plan in retainedPlans)
        {
            try
            {
                var drafted = stops is null
                    ? new ControlResult(false, $"{plan}: repository stop NOT finished: retry with a configured stop handler to draft its selected changeset PRs.")
                    : await stops.DraftRepositoryAsync(ItemNaming.ParseAny(plan)!, ControlScope.RepositoryOf(scope)!, ct);
                ok &= drafted.Ok;
                results.Add(drafted.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ok = false;
                results.Add($"{plan}: repository stop NOT finished ({ex.Message}); the repository stays manually paused. Retry `factory stop --repo` to finish drafting.");
            }
        }
        if (unknown.Count > 0)
        {
            results.Add($"{unknown.Count} item(s) with an unknown epic NOT stopped: {string.Join(", ", unknown)} "
                + "(the board could not be read); stop them with `factory stop --item` or retry.");
        }
        else if (stories.Count == 0 && results.Count == 0)
        {
            return new ControlResult(true, $"{scope}: no active items to stop.");
        }
        return new ControlResult(ok, string.Join('\n', results));
    }

    /// <summary>
    /// Whether the item is a plan item whose stories are on the board (sc-25876: its ledger has a <c>plan-stories</c> row): a control
    /// on it applies to those stories too (<see cref="IControls.EffectiveAsync"/>), and its Stop stops them (<see cref="ItemStopper"/>).
    /// </summary>
    private async Task<bool> IsPlanAsync(ItemRef item, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var id = item.ToString();
        return await db.LedgerEntries.AsNoTracking().AnyAsync(e => e.Step == RunPipeline.Steps.PlanStories
            && db.WorkItems.Any(i => i.Id == e.WorkItemId && i.ExternalId == id && i.Source == item.Naming.Source), ct);
    }

    /// <summary>The usage pause is set by the factory itself (<see cref="UsagePause"/>); a user may only lift it early.</summary>
    private static readonly ControlResult UsageOnlyContinues =
        new(false, "The usage pause is set and lifted by the factory; it can only be continued early (`factory continue --usage`). Use --factory to pause or stop the whole factory.");

    /// <summary>The freeze is set by the factory itself (<see cref="FactoryFreeze"/>); a human only clears it.</summary>
    private static readonly ControlResult FreezeOnlyContinues =
        new(false, "The freeze is set by the factory when a trigger holds; it can only be cleared (`factory continue --freeze`). Use --factory to pause or stop the whole factory.");

    /// <summary>
    /// The refusal of an epic scope whose epic its source says cannot exist (<see cref="IWorkSource.KnowsEpicAsync"/>), or null:
    /// nothing is written for it. A source that is not configured is not asked; its kind answers from the ledger alone
    /// (<see cref="WorkSourceKind.KnowsEpic"/>: no credential needed), so a key the ledger never gave out is refused there too,
    /// while a known epic's scope is written whether or not its board can be reached (its items are reported as unknown as before).
    /// </summary>
    private async Task<ControlResult?> UnknownEpicAsync(EpicRef epic, string done, CancellationToken ct)
    {
        var kind = WorkSourceRegistry.KindOf(epic.Naming);
        var known = sources?.Invoke(epic.Naming) is { } source
            ? await source.KnowsEpicAsync(epic.Id, ct)
            : kind.KnowsEpic is null || await kind.KnowsEpic(contexts, epic.Id, ct);
        return known
            ? null
            : new ControlResult(false, $"{ControlScope.Epic(epic)}: NOT {done}, and nothing was written. The factory has seen no "
                + $"{kind.Name} epic {epic}: an epic gets its key when the factory first reads an item of it "
                + "(the dashboard's pipeline page shows it beside the item), or ahead of that from "
                + "`factory kanban key --epic <epic GUID>`.");
    }

    private sealed record EpicResolution(List<string> InEpic, List<string> Unknown, List<string> Running);

    /// <summary>
    /// Looks up the epic of every active item of the epic's source the ledger has none for and records it (under the item's run
    /// lock; a running item's own run records it when it starts). Returns the items found in <paramref name="epic"/>,
    /// those whose epic could not be read, and those in the epic that are running with no epic recorded.
    /// </summary>
    private async Task<EpicResolution> ResolveUnknownEpicsAsync(EpicRef epic, CancellationToken ct)
    {
        var resolution = new EpicResolution([], [], []);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ledger = new WorkLedger(db, TimeProvider.System);
        var epicSource = epic.Naming.Source;
        var items = await db.WorkItems
            .Where(i => i.Source == epicSource && i.State != WorkState.Done && i.State != WorkState.Cancelled && i.EpicId == null)
            .OrderBy(i => i.Id)
            .ToListAsync(ct);
        foreach (var item in items)
        {
            // The item's own board, found from its ledger row; one that is not configured cannot be asked.
            if (ItemNaming.ParseAny(item.ExternalId) is not { } named || named.Naming.Source != item.Source || sources?.Invoke(named.Naming) is not { } source)
            {
                resolution.Unknown.Add(item.ExternalId);
                continue;
            }
            long? itemEpic;
            try
            {
                itemEpic = (await source.ReadSpecAsync(named.Id, ct)).Epic?.Id;
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
            if (itemEpic == epic.Id)
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
