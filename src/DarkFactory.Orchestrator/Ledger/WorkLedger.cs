using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Writes work items and their ledger rows. Each transition is checked against
/// <see cref="Lifecycle"/> before anything is written, then committed as one row
/// together with the item's current state (E2, E3).
/// </summary>
public sealed class WorkLedger(LedgerDbContext db, TimeProvider time)
{
    /// <summary>
    /// Returns the item, creating it in <see cref="WorkState.Intake"/> with its Intake row when new.
    /// An existing item is returned as stored, unchanged: take the item's run lock, then
    /// <see cref="RefreshAsync"/> it, before writing to it.
    /// </summary>
    public async Task<WorkItem> GetOrCreateAsync(string source, string externalId, string title, string repo, string? intakeDetail, CancellationToken ct,
        long? epicId = null)
    {
        if (await db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct) is { } existing)
        {
            return existing;
        }
        var now = time.GetUtcNow();
        var item = new WorkItem
        {
            Source = source,
            ExternalId = externalId,
            Title = title,
            Repo = repo,
            EpicId = epicId,
            State = WorkState.Intake,
            CreatedAt = now,
            UpdatedAt = now,
        };
        item.Entries.Add(new LedgerEntry
        {
            State = WorkState.Intake, RecordedAt = now, Detail = intakeDetail, Outcome = StepOutcomes.Of(null, WorkState.Intake, null, intakeDetail),
        });
        db.WorkItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateException)
        {
            // A concurrent run created it first (unique Source+ExternalId): use theirs.
            db.Entry(item).State = EntityState.Detached;
            foreach (var entry in item.Entries)
            {
                db.Entry(entry).State = EntityState.Detached;
            }
            var winner = await db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);
            if (winner is null)
            {
                throw;
            }
            return winner;
        }
    }

    /// <summary>
    /// Re-reads the item (another run may have moved it since it was loaded) and updates its
    /// title, repo and epic. Call while holding the item's run lock.
    /// </summary>
    public async Task RefreshAsync(WorkItem item, string title, string repo, long? epicId, CancellationToken ct)
    {
        await db.Entry(item).ReloadAsync(ct);
        if (item.Title == title && item.Repo == repo && item.EpicId == epicId)
        {
            return;
        }
        var (oldTitle, oldRepo, oldEpic, oldUpdated, oldVersion) = (item.Title, item.Repo, item.EpicId, item.UpdatedAt, item.Version);
        item.Title = title;
        item.Repo = repo;
        item.EpicId = epicId;
        item.UpdatedAt = time.GetUtcNow();
        item.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            (item.Title, item.Repo, item.EpicId, item.UpdatedAt, item.Version) = (oldTitle, oldRepo, oldEpic, oldUpdated, oldVersion);
            throw;
        }
    }

    /// <summary>
    /// Moves the item to <paramref name="state"/>. An illegal transition throws
    /// <see cref="IllegalTransitionException"/> and writes nothing.
    /// </summary>
    public async Task<LedgerEntry> RecordAsync(WorkItem item, WorkState state, string? claudeSessionId, string? detail, CancellationToken ct)
    {
        Lifecycle.Ensure(item.State, state, await ContextAsync(item, ct));
        return await AppendAsync(item, state, null, claudeSessionId, detail, ct);
    }

    /// <summary>Records a completed sub-step of the item's current state; the state does not change.</summary>
    public Task<LedgerEntry> CheckpointAsync(WorkItem item, string step, string? claudeSessionId, string? detail, CancellationToken ct) =>
        AppendAsync(item, item.State, step, claudeSessionId, detail, ct);

    /// <summary>The item's current state as stored, or null when there is no such item. Reads only.</summary>
    public async Task<WorkState?> StateOfAsync(string source, string externalId, CancellationToken ct) =>
        (await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct))?.State;

    /// <summary>The id of an item's row (the key of its run lock, <see cref="IRunLocks"/>), or null when there is no such item. Reads only.</summary>
    public async Task<long?> IdOfAsync(WorkSources.ItemRef item, CancellationToken ct)
    {
        var (source, externalId) = (item.Naming.Source, item.ToString());
        return (await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct))?.Id;
    }

    /// <summary>The item, or null if the ledger has never seen it. Nothing is written.</summary>
    public Task<WorkItem?> FindAsync(string source, string externalId, CancellationToken ct) =>
        db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);

    /// <summary>Reloads an existing item under its run lock, without writing metadata.</summary>
    public Task ReloadAsync(WorkItem item, CancellationToken ct) => db.Entry(item).ReloadAsync(ct);

    /// <summary>The source's items not Done or Cancelled, oldest first.</summary>
    public Task<List<WorkItem>> ActiveItemsAsync(string source, CancellationToken ct) =>
        db.WorkItems.Where(x => x.Source == source && x.State != WorkState.Done && x.State != WorkState.Cancelled).OrderBy(x => x.Id).ToListAsync(ct);

    /// <summary>The source's items currently in one of <paramref name="states"/>, oldest first.</summary>
    public async Task<List<WorkItem>> ItemsInAsync(string source, IReadOnlySet<WorkState> states, CancellationToken ct)
    {
        var wanted = states.ToList();
        return await db.WorkItems.Where(x => x.Source == source && wanted.Contains(x.State)).OrderBy(x => x.Id).ToListAsync(ct);
    }

    /// <summary>
    /// The source's items (every source's when <paramref name="source"/> is null) on <paramref name="repo"/> currently in one of
    /// <paramref name="states"/>, oldest first, each with every row, read fresh and untracked (other runs, in any process, write
    /// them): what the merge queue is derived from (one queue per repo, whatever source its items came from).
    /// </summary>
    public async Task<List<(WorkItem Item, List<LedgerEntry> History)>> ItemsOnRepoAsync(string? source, string repo, IReadOnlySet<WorkState> states,
        CancellationToken ct)
    {
        var wanted = states.ToList();
        // One repository whatever case an item's `Repo:` line spells it in (GitHub's names are case-insensitive, and the queue's
        // lock key is lower-cased too): two spellings must never make two merge queues of one repository.
        var lower = repo.ToLowerInvariant();
        var items = await db.WorkItems.AsNoTracking().Where(x => (source == null || x.Source == source) && x.Repo.ToLower() == lower && wanted.Contains(x.State))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct);
        return items.Select(i => (i, rows.Where(r => r.WorkItemId == i.Id).ToList())).ToList();
    }

    /// <summary>
    /// The plan item whose newest <c>plan-stories</c> checkpoint lists <paramref name="externalId"/> (sc-25871), or null when no plan of
    /// this source created it. Read from the ledger alone: the board's marker on a story is text anyone may write or remove.
    /// </summary>
    public async Task<long?> PlanOfAsync(string source, string externalId, CancellationToken ct)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.Step == RunPipeline.Steps.PlanStories && e.Detail != null && e.Detail.Contains(externalId))
            .OrderByDescending(e => e.Id).Select(e => new { e.WorkItemId, e.Detail }).ToListAsync(ct);
        foreach (var row in rows.Where(r => Plans.PlanRelease.StoriesOf(r.Detail).Contains(externalId, StringComparer.Ordinal)))
        {
            if (await db.WorkItems.AsNoTracking().AnyAsync(i => i.Id == row.WorkItemId && i.Source == source, ct))
            {
                return row.WorkItemId;
            }
        }
        return null;
    }

    /// <summary>The item with that ledger id as stored (a story's plan item, <see cref="WorkItem.PlanItemId"/>), or null. Read fresh, untracked.</summary>
    public Task<WorkItem?> ItemAsync(long id, CancellationToken ct) =>
        db.WorkItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);

    /// <summary>The stories the plan item's newest <c>plan-stories</c> checkpoint lists (sc-25871); none when it has no such row.</summary>
    public async Task<IReadOnlySet<string>> PlanStoriesAsync(long planItemId, CancellationToken ct) =>
        Plans.PlanRelease.StoriesOf(await db.LedgerEntries.AsNoTracking()
            .Where(e => e.WorkItemId == planItemId && e.Step == RunPipeline.Steps.PlanStories)
            .OrderByDescending(e => e.Id).Select(e => e.Detail).FirstOrDefaultAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The changeset branch of the plan item <paramref name="planItemId"/> in <paramref name="repo"/> (sc-25872,
    /// <see cref="Plans.Changeset.Of"/>), read fresh from its <c>changeset</c> rows; null when it has none pushed there.
    /// <c>Recorded</c>: every changeset the plan item recorded, for any repository, pushed or not — empty only for a plan recorded
    /// before there were changesets, whose stories target the default branch.
    /// </summary>
    public async Task<(Plans.Changeset? Changeset, IReadOnlyList<Plans.Changeset> Recorded)> ChangesetOfAsync(long planItemId, RepoRef repo,
        CancellationToken ct)
    {
        var rows = await db.LedgerEntries.AsNoTracking()
            .Where(e => e.WorkItemId == planItemId && (e.Step == RunPipeline.Steps.Changeset || e.Step == RunPipeline.Steps.ChangesetPushed))
            .OrderBy(e => e.Id).ToListAsync(ct);
        return (Plans.Changeset.Of(rows, repo),
            [.. rows.Where(e => e.Step == RunPipeline.Steps.Changeset).Select(e => Plans.Changeset.FromDetail(e.Detail)).OfType<Plans.Changeset>()]);
    }

    /// <summary>Records that <paramref name="item"/> is a story of the plan item <paramref name="planItemId"/>. Call under the item's run lock.</summary>
    public async Task LinkPlanAsync(WorkItem item, long planItemId, CancellationToken ct)
    {
        if (item.PlanItemId == planItemId)
        {
            return;
        }
        var (oldPlan, oldUpdated, oldVersion) = (item.PlanItemId, item.UpdatedAt, item.Version);
        item.PlanItemId = planItemId;
        item.UpdatedAt = time.GetUtcNow();
        item.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            (item.PlanItemId, item.UpdatedAt, item.Version) = (oldPlan, oldUpdated, oldVersion);
            throw;
        }
    }

    /// <summary>
    /// The source's items named <paramref name="externalIds"/> that the ledger knows, each with every row, read fresh and untracked
    /// (their own runs write them): what a plan's release and closeout are decided and rendered from (sc-25871).
    /// </summary>
    public async Task<Dictionary<string, (WorkItem Item, List<LedgerEntry> History)>> ItemsByIdAsync(string source,
        IReadOnlyCollection<string> externalIds, CancellationToken ct)
    {
        var wanted = externalIds.Distinct(StringComparer.Ordinal).ToList();
        var items = await db.WorkItems.AsNoTracking().Where(x => x.Source == source && wanted.Contains(x.ExternalId)).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct);
        return items.ToDictionary(i => i.ExternalId, i => (i, rows.Where(r => r.WorkItemId == i.Id).ToList()), StringComparer.Ordinal);
    }

    /// <summary>
    /// The slot numbers 1 to <paramref name="slots"/> ordered by how many unfinished items (not Done, Cancelled or in Watch) the
    /// ledger has in each (<see cref="RunPipeline.SlotOf"/>'s last <see cref="RunPipeline.Steps.Slot"/> row), fewest first, ties by
    /// number: where an item with no slot yet is tried first (sc-25989), so slots fill evenly. Reads only.
    /// </summary>
    public async Task<IReadOnlyList<int>> SlotsByLoadAsync(int slots, CancellationToken ct)
    {
        var rows = await (from e in db.LedgerEntries.AsNoTracking()
                          join i in db.WorkItems.AsNoTracking() on e.WorkItemId equals i.Id
                          where e.Step == RunPipeline.Steps.Slot && i.State != WorkState.Done && i.State != WorkState.Cancelled && i.State != WorkState.Watch
                          orderby e.Id
                          select new { e.WorkItemId, e.Detail }).ToListAsync(ct);
        var load = rows.GroupBy(r => r.WorkItemId)
            .Select(g => int.TryParse(g.Last().Detail?.Split(' ')[0], out var slot) ? slot : 0)
            .GroupBy(slot => slot).ToDictionary(g => g.Key, g => g.Count());
        return [.. Enumerable.Range(1, slots).OrderBy(slot => load.GetValueOrDefault(slot)).ThenBy(slot => slot)];
    }

    /// <summary>Every row of the item, oldest first.</summary>
    public Task<List<LedgerEntry>> HistoryAsync(WorkItem item, CancellationToken ct) =>
        db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);

    /// <summary>
    /// The Merge transitions and closeout rows (<see cref="RunPipeline.CloseoutOf"/>) of each of <paramref name="itemIds"/>, oldest first,
    /// in one read (untracked).
    /// </summary>
    public async Task<ILookup<long, LedgerEntry>> CloseoutRowsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct) =>
        itemIds.Count == 0 ? Array.Empty<LedgerEntry>().ToLookup(e => e.WorkItemId)
            : (await db.LedgerEntries.AsNoTracking()
                .Where(e => itemIds.Contains(e.WorkItemId)
                    && ((e.Step == null && e.State == WorkState.Merge) || e.Step == RunPipeline.Steps.Closeout))
                .OrderBy(e => e.Id).ToListAsync(ct)).ToLookup(e => e.WorkItemId);

    /// <summary>The router cost of each of the item's worker sessions (null: not recorded), read fresh: the session recorder writes them.</summary>
    public async Task<IReadOnlyList<decimal?>> SessionCostsAsync(WorkItem item, CancellationToken ct) =>
        await db.WorkerSessions.AsNoTracking().Where(s => s.WorkItemId == item.Id).OrderBy(s => s.Id).Select(s => s.CostUsd).ToListAsync(ct);

    public async Task<TransitionContext> ContextAsync(WorkItem item, CancellationToken ct) =>
        TransitionContext.From((await HistoryAsync(item, ct)).Where(e => e.Step is null).Select(e => e.State).ToList());

    /// <summary>
    /// Marks the worker session <paramref name="sessionId"/> tainted (E4, <see cref="Worker.Taint"/>), committed before this returns.
    /// Sticky: an already tainted session keeps its first reason; nothing un-taints one.
    /// </summary>
    public async Task TaintSessionAsync(WorkItem? item, string sessionId, string reason, CancellationToken ct)
    {
        if (await TaintOfAsync(sessionId, ct) is not null)
        {
            return;
        }
        var taint = new SessionTaint { ClaudeSessionId = sessionId, WorkItemId = item?.Id, Reason = reason, TaintedAt = time.GetUtcNow() };
        db.SessionTaints.Add(taint);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Nothing was written: leave no tracked row behind for a later save to write.
            db.Entry(taint).State = EntityState.Detached;
            // A concurrent writer tainted it first: the session is tainted either way.
            if (ex is DbUpdateException && await TaintOfAsync(sessionId, ct) is not null)
            {
                return;
            }
            throw;
        }
        db.Entry(taint).State = EntityState.Detached;
    }

    /// <summary>
    /// Replays the stored stream of <paramref name="sessionId"/> (its <c>session_events</c>) through a <see cref="Worker.StreamJsonState"/>
    /// and taints the session for every web or MCP tool use found there (E4). It closes the window in which a run stopped after
    /// the line was stored but before its taint committed. A failed read throws, so a session whose stream cannot be checked is
    /// not resumed (E2).
    /// </summary>
    public async Task ReplayTaintsAsync(WorkItem? item, string sessionId, CancellationToken ct)
    {
        var state = new Worker.StreamJsonState();
        var rows = db.WorkerSessions.Where(s => s.ClaudeSessionId == sessionId).Select(s => s.Id);
        var lines = db.SessionEvents.AsNoTracking()
            .Where(e => rows.Contains(e.WorkerSessionId) && e.Type == "assistant")
            .OrderBy(e => e.WorkerSessionId).ThenBy(e => e.Sequence)
            .Select(e => e.Payload)
            .AsAsyncEnumerable();
        await foreach (var line in lines.WithCancellation(ct))
        {
            state.Accept(line);
        }
        foreach (var reason in state.UntrustedReads)
        {
            await TaintSessionAsync(item, sessionId, reason, ct);
        }
    }

    /// <summary>The session's taint, read fresh from the ledger, or null when it is not tainted.</summary>
    public Task<SessionTaint?> TaintOfAsync(string sessionId, CancellationToken ct) =>
        db.SessionTaints.AsNoTracking().SingleOrDefaultAsync(t => t.ClaudeSessionId == sessionId, ct);

    /// <summary>
    /// The right to push the work of <paramref name="sessions"/> (the worker sessions of the attempt being pushed): issued only when
    /// there is at least one and none is tainted. A tainted one throws <see cref="Worker.SessionTaintedException"/> (E4); none at
    /// all throws too, since a push whose provenance cannot be checked is refused (E2).
    /// </summary>
    public async Task<Worker.PushGrant> GrantPushAsync(IEnumerable<string?> sessions, CancellationToken ct)
    {
        var ids = sessions.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            throw new InvalidOperationException("No worker session vouches for this push, so its taint cannot be checked; refusing it (E2).");
        }
        foreach (var id in ids)
        {
            if (await TaintOfAsync(id, ct) is { } taint)
            {
                throw new Worker.SessionTaintedException(id, taint.Reason);
            }
        }
        return new Worker.PushGrant(ids);
    }

    private async Task<LedgerEntry> AppendAsync(WorkItem item, WorkState state, string? step, string? claudeSessionId, string? detail, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var entry = new LedgerEntry
        {
            WorkItemId = item.Id,
            State = state,
            Step = step,
            RecordedAt = now,
            ClaudeSessionId = claudeSessionId,
            Detail = detail,
            // The one place a row's outcome is decided (E7): a transition from the item's current state, or a checkpoint inside it.
            Outcome = StepOutcomes.Of(item.State, state, step, detail),
        };
        var (previousState, previousUpdatedAt, previousVersion) = (item.State, item.UpdatedAt, item.Version);
        db.LedgerEntries.Add(entry);
        item.State = state;
        item.UpdatedAt = now;
        item.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Nothing was written: leave neither a tracked row nor an in-memory state the ledger doesn't have.
            db.Entry(entry).State = EntityState.Detached;
            (item.State, item.UpdatedAt, item.Version) = (previousState, previousUpdatedAt, previousVersion);
            throw;
        }
        return entry;
    }
}
