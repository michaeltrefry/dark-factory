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
    /// <summary>The item, or null if the ledger has never seen it. Nothing is written.</summary>
    public Task<WorkItem?> FindAsync(string source, string externalId, CancellationToken ct) =>
        db.WorkItems.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == externalId, ct);

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
        var items = await db.WorkItems.AsNoTracking().Where(x => (source == null || x.Source == source) && x.Repo == repo && wanted.Contains(x.State))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var rows = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.WorkItemId)).OrderBy(e => e.Id).ToListAsync(ct);
        return items.Select(i => (i, rows.Where(r => r.WorkItemId == i.Id).ToList())).ToList();
    }

    /// <summary>Every row of the item, oldest first.</summary>
    public Task<List<LedgerEntry>> HistoryAsync(WorkItem item, CancellationToken ct) =>
        db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);

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
