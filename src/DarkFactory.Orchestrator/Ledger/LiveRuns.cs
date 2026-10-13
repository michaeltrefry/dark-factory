using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Whose a leftover on disk is (sc-25987, E6): a worktree, a gate test worktree, a planner's or triage's seat or an MCP session file
/// carries its item in its name (<see cref="ItemNaming.OwnerOfLeftover"/>, <see cref="ItemNaming.OwnerOfSessionFile"/>), and an
/// item's leftovers are a live run's exactly while that item's run lock (<see cref="IRunLocks"/>) is held. A clean-up never decides
/// that from "no other run exists": it asks here, and deletes only under the <see cref="LeftoverClaim"/> it is given — for another
/// item, that item's run lock, held until the deletion is done, so no run of it can start in between.
/// </summary>
/// <param name="idOf">The ledger's id of an item (the run lock's key), or null when the ledger has never seen it.</param>
/// <param name="self">
/// The item whose run is asking, when that run already holds its item's lock (a planner's seat inside its item's run, a triage
/// under the intake's): what an earlier, dead run of it left is this run's to delete. Null when the asker holds no item's lock
/// (the sweep at the start of a run, before its pipeline takes the lock).
/// </param>
public sealed class LiveRuns(Func<ItemRef, CancellationToken, Task<long?>> idOf, IRunLocks locks, ItemRef? self = null)
{
    /// <summary>Over the ledger: an item's id is its row's.</summary>
    public LiveRuns(WorkLedger ledger, IRunLocks locks, ItemRef? self = null) : this(ledger.IdOfAsync, locks, self)
    {
    }

    /// <summary>No run but the asker's exists (tests): every leftover may be deleted.</summary>
    public static LiveRuns None { get; } = new((_, _) => Task.FromResult<long?>(null), new NoLocks());

    /// <summary>
    /// A claim on <paramref name="owner"/>'s leftovers (dispose it once they are deleted), or null when a live run owns them — then
    /// nothing of them may be touched. A leftover naming no item, or an item the ledger does not know (so nothing can run it), is
    /// nobody's and is claimed without a lock.
    /// </summary>
    public async Task<LeftoverClaim?> TryClaimAsync(ItemRef? owner, CancellationToken ct)
    {
        if (owner is null || owner == self || await idOf(owner, ct) is not { } id)
        {
            return LeftoverClaim.Unlocked;
        }
        return await locks.TryAcquireAsync(id, ct) is { } held ? new LeftoverClaim(held) : null;
    }

    private sealed class NoLocks : IRunLocks
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(long workItemId, CancellationToken ct) =>
            throw new InvalidOperationException("LiveRuns.None knows no item, so it takes no lock.");
    }
}

/// <summary>The right to delete one item's leftovers (<see cref="LiveRuns.TryClaimAsync"/>); disposing it releases the item's run lock, if it holds one.</summary>
public sealed class LeftoverClaim(IAsyncDisposable? runLock) : IAsyncDisposable
{
    internal static LeftoverClaim Unlocked { get; } = new(null);

    /// <summary>
    /// Whether the claim holds the item's run lock: the item's state may have changed since it was last read (a run of it may have
    /// ended just before the lock was taken), so a rule read from the ledger is read again under it.
    /// </summary>
    public bool UnderRunLock => runLock is not null;

    public ValueTask DisposeAsync() => runLock?.DisposeAsync() ?? ValueTask.CompletedTask;
}
