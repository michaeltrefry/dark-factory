using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// One worker slot (sc-25989): a sandbox user with its own launch helper and its own lock (<see cref="WorkerSandbox.Lock"/>).
/// Slot 1 is the sandbox as it was before slots — <c>Worker:RunAs</c>, <c>Worker:LaunchHelper</c>, the work root's
/// <see cref="WorkerLock.FileName"/>.
/// </summary>
public sealed record WorkerSlot(int Number, WorkerSandbox Sandbox)
{
    public string User => Sandbox.User;

    public WorkerLock? Lock => Sandbox.Lock;

    public override string ToString() => $"slot {Number} ({User})";
}

/// <summary>
/// An item's recorded slot is not one of the configured slots (<c>Worker:Slots</c> was lowered while the item had work in a
/// higher one): its session and worktrees belong to a sandbox user this factory no longer runs anything as. The item's failure,
/// with the remedy; nothing of it is run or deleted as another user.
/// </summary>
public sealed class SlotUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The pool of worker slots (sc-25989, E6/E7): <c>Worker:Slots</c> sandbox users, each single-tenant — its launch helper's exit
/// kills every process of that user and of no other — so each has its own lock (<see cref="WorkerLock"/>), and two runs are in a
/// worker phase at once exactly when they hold different slots. <see cref="HoldAsync{T}"/> hands out a slot: a named one (an
/// item's recorded slot, waited for however long it is busy — its session, its worktree and whatever a crashed run left are that
/// slot's user's), or any free one. Everything done as a sandbox user is done by the slot the calling flow holds
/// (<see cref="Current"/>): a pool is the <see cref="IWorkerSandbox"/> of a workspace and the sandbox of a worker or a test run,
/// resolved per call.
/// <para>With one slot (the default) it is the single sandbox user as before: nothing is recorded differently on disk, the lock
/// is the one lock file, and a short command that holds no slot takes the lock itself (<see cref="Current"/>).</para>
/// <para>Deletions (<see cref="DeleteAsWorkerAsync"/>, <see cref="TryDeleteAsWorkerAsync"/>) go to the slot whose user the
/// directory was shared with for writing — read from the directory's own ACL (<see cref="SharedWith"/>), which only the owner
/// can change — under that slot's lock: for an item's worktree that is the slot the ledger recorded for the item. A directory
/// shared with no slot (a triage's or a planner's read-only worktree) was written by no worker, and any slot may delete it.</para>
/// </summary>
public sealed class WorkerSlots : IWorkerSandbox
{
    /// <summary>The most slots a factory may have (<c>Worker:Slots</c>, and <c>setup-worker-user.sh --slots</c>).</summary>
    public const int MaxSlots = 8;

    public WorkerSlots(IReadOnlyList<WorkerSlot> slots)
    {
        if (slots.Count is < 1 or > MaxSlots || slots.Select((s, i) => s.Number == i + 1).Contains(false))
        {
            throw new ArgumentException($"A slot pool holds slots 1 to n in order, n from 1 to {MaxSlots}.", nameof(slots));
        }
        if (slots.Count > 1 && (slots.Any(s => s.Lock is null)
            || slots.DistinctBy(s => s.User, StringComparer.Ordinal).Count() != slots.Count
            || slots.DistinctBy(s => s.Sandbox.HelperPath, StringComparer.Ordinal).Count() != slots.Count
            || slots.DistinctBy(s => s.Lock!.Path, StringComparer.Ordinal).Count() != slots.Count))
        {
            throw new ArgumentException("Several slots need a sandbox user, a launch helper and a lock each, none shared.", nameof(slots));
        }
        All = slots;
    }

    public IReadOnlyList<WorkerSlot> All { get; }

    public int Count => All.Count;

    /// <summary>Slot <paramref name="slot"/>'s sandbox user: <paramref name="first"/> for slot 1, then <c>&lt;first&gt;2</c>, <c>&lt;first&gt;3</c>, … (as <c>setup-worker-user.sh</c> names them).</summary>
    public static string UserOf(string first, int slot) => slot == 1 ? first : $"{first}{slot}";

    /// <summary>Slot <paramref name="slot"/>'s launch helper: <paramref name="first"/> for slot 1, then <c>&lt;first&gt;-2</c>, <c>&lt;first&gt;-3</c>, … (as <c>setup-worker-user.sh</c> installs them).</summary>
    public static string HelperOf(string first, int slot) => slot == 1 ? first : $"{first}-{slot}";

    /// <summary>What the owner runs to (re)install every slot's helper: <c>sudo scripts/setup-worker-user.sh</c>, with <c>--slots n</c> for more than one.</summary>
    public static string ReinstallCommand(int slots) =>
        slots == 1 ? WorkerSandbox.ReinstallCommand : $"{WorkerSandbox.ReinstallCommand} --slots {slots}";

    /// <summary>
    /// The configured pool: <paramref name="count"/> slots over the work root <paramref name="workRoot"/>, slot 1 being
    /// <paramref name="user"/> through <paramref name="helperPath"/> under the work root's lock as before slots.
    /// </summary>
    public static WorkerSlots Create(string user, string helperPath, string workRoot, int count) =>
        new([.. Enumerable.Range(1, count).Select(n => new WorkerSlot(n,
            new WorkerSandbox(UserOf(user, n), HelperOf(helperPath, n)) { Lock = WorkerLock.For(workRoot, n), Reinstall = ReinstallCommand(count) }))]);

    /// <summary>A pool of the one slot <paramref name="sandbox"/> is.</summary>
    public static WorkerSlots Single(WorkerSandbox sandbox) => new([new WorkerSlot(1, sandbox)]);

    /// <summary>A sandbox is the pool of its one slot (tests, and callers written before slots).</summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(sandbox))]
    public static implicit operator WorkerSlots?(WorkerSandbox? sandbox) => sandbox is null ? null : Single(sandbox);

    /// <summary>The slot whose lock the calling async flow holds, or null.</summary>
    public WorkerSlot? Held => All.FirstOrDefault(s => s.Lock?.IsHeld == true);

    /// <summary>The slot the calling flow was given without holding it (<see cref="AssignedAsync{T}"/>).</summary>
    private readonly AsyncLocal<WorkerSlot?> _assigned = new();

    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="slot"/> as the flow's slot although it does not hold its lock: for
    /// owner-side work on an item's worktree outside a worker phase (the merge queue's base update), whose fresh worktree is
    /// shared with the item's slot's user by the owner's own <c>chmod</c>. Nothing can be started as that user this way —
    /// <see cref="WorkerSandbox.Start"/> refuses without the lock, and the sandbox's short commands take it themselves, waiting.
    /// </summary>
    /// <exception cref="SlotUnavailableException"><paramref name="slot"/> is not configured.</exception>
    public async Task<T> AssignedAsync<T>(int slot, Func<Task<T>> work)
    {
        // Set in this async method, which the work is called from: it flows into the work and not back to the caller.
        _assigned.Value = Slot(slot);
        return await work();
    }

    /// <summary>
    /// The sandbox of the slot the calling flow holds: what runs as a sandbox user now. A flow that holds none has the slot it
    /// was given (<see cref="AssignedAsync{T}"/>), or with one slot that slot (its commands take the lock themselves, as before
    /// slots); with several and none given it has no sandbox user and this throws — nothing runs as "some" user.
    /// </summary>
    public WorkerSandbox Current => (Held ?? _assigned.Value ?? (Count == 1 ? All[0] : null))?.Sandbox
        ?? throw new InvalidOperationException(
            $"No worker slot is held: with {Count} slots (Worker:Slots), whatever runs as a sandbox user runs in the slot its worker phase holds.");

    /// <summary>The configured slot <paramref name="number"/>.</summary>
    /// <exception cref="SlotUnavailableException">No such slot is configured.</exception>
    public WorkerSlot Slot(int number) => number >= 1 && number <= Count
        ? All[number - 1]
        : throw new SlotUnavailableException(
            $"worker slot {number} is not configured (Worker:Slots is {Count}): set Worker:Slots to at least {number} again, "
            + $"with its sandbox user set up (`{ReinstallCommand(Math.Max(number, 1))}`)");

    /// <summary>The line a wait of <paramref name="waited"/> for any free slot of <paramref name="slots"/> is reported with.</summary>
    public static string WaitReport(int slots, TimeSpan waited, string why) =>
        $"[lock] waited {(long)waited.TotalSeconds} s for a free worker slot and still waiting (other runs' worker phases hold all {slots}, "
        + $"a launch helper that would not stop is still alive, or a lock file cannot be opened): {why}";

    /// <summary>
    /// Runs <paramref name="phase"/> holding a slot's lock, waited for (until <paramref name="ct"/> is cancelled):
    /// <paramref name="slot"/> when given — only that one, however long it is busy — else the first free one, tried in
    /// <paramref name="order"/> (slot numbers; null: 1 to n), again and again while every one is held. A flow that already holds
    /// a slot runs <paramref name="phase"/> in it at once (and is refused if it asked for another: a flow is one sandbox user).
    /// The wait is as <see cref="WorkerLock.HoldAsync{T}"/>'s: <paramref name="whileWaiting"/> after every failed try (it may
    /// throw to give the wait up), reported every <paramref name="reportEvery"/>.
    /// </summary>
    /// <exception cref="SlotUnavailableException"><paramref name="slot"/> is not configured.</exception>
    public async Task<T> HoldAsync<T>(int? slot, Func<WorkerSlot, Task<T>> phase, CancellationToken ct,
        Func<CancellationToken, Task>? whileWaiting = null, TimeSpan? retry = null, Action<string>? report = null, TimeSpan? reportEvery = null,
        IReadOnlyList<int>? order = null)
    {
        if (Held is { } held)
        {
            return slot is { } other && other != held.Number
                ? throw new InvalidOperationException($"Refusing to use worker slot {other} inside a phase that holds {held}: a phase is one sandbox user's.")
                : await phase(held);
        }
        if (slot is not null || Count == 1)
        {
            var one = slot is { } number ? Slot(number) : All[0];
            return one.Lock is null ? await phase(one) : await one.Lock.HoldAsync(() => phase(one), ct, whileWaiting, retry, report, reportEvery);
        }
        var candidates = (order ?? []).Where(n => n >= 1 && n <= Count).Concat(Enumerable.Range(1, Count)).Distinct().Select(n => All[n - 1]).ToList();
        var every = reportEvery ?? WorkerLock.ReportEvery;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reported = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var why = "";
            foreach (var candidate in candidates)
            {
                var (got, result, busy) = await candidate.Lock!.TryHoldAsync(() => phase(candidate));
                if (got)
                {
                    return result!;
                }
                why = busy;
            }
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (waited - reported >= every)
            {
                reported = waited;
                (report ?? Console.Error.WriteLine)(WaitReport(Count, waited, why));
            }
            if (whileWaiting is not null)
            {
                await whileWaiting(ct);
            }
            await Task.Delay(retry ?? WorkerLock.DefaultRetry, ct);
        }
    }

    /// <summary>
    /// The start-up check of every slot's installed launch helper (<see cref="WorkerSandbox.EnsureHelperCurrentAsync"/>: it reads
    /// the file and runs nothing): each must be readable, the current <c>scripts/factory-worker-launch</c>, and pinned to its own
    /// slot's user. The first that is not fails, named by its slot when there are several.
    /// </summary>
    public async Task EnsureHelpersCurrentAsync(CancellationToken ct)
    {
        foreach (var slot in All)
        {
            await NamedAsync(slot, () => slot.Sandbox.EnsureHelperCurrentAsync(ct));
        }
    }

    /// <summary>
    /// The readiness probe (<see cref="WorkerSandbox.EnsureReadyAsync"/>: it runs the helper, so under the slot's lock) of the slot
    /// the calling flow holds — every run probes its own slot in its first worker phase, so each slot is probed before a worker
    /// runs in it. A failure names the slot when there are several.
    /// </summary>
    public Task EnsureReadyAsync(WorkerAuth auth, string claudePath, CancellationToken ct)
    {
        var slot = Held ?? (Count == 1 ? All[0] : throw new InvalidOperationException("No worker slot is held: the readiness probe is of the held slot."));
        return NamedAsync(slot, () => slot.Sandbox.EnsureReadyAsync(auth, claudePath, ct));
    }

    /// <summary>Runs <paramref name="check"/>, its failure led by <paramref name="slot"/> when the pool has several.</summary>
    private async Task NamedAsync(WorkerSlot slot, Func<Task> check)
    {
        try
        {
            await check();
        }
        catch (InvalidOperationException ex) when (Count > 1)
        {
            throw new InvalidOperationException($"Worker {slot}: {ex.Message}", ex);
        }
    }

    /// <summary>The held slot shares <paramref name="path"/> with its own user (<see cref="Current"/>): a worktree is one slot's for writing.</summary>
    public Task ShareAsync(string path, CancellationToken ct) => Current.ShareAsync(path, ct);

    /// <summary>Owner-side, and closed to every user but the owner whichever slot asks.</summary>
    public Task CloseToWorkerAsync(string path, CancellationToken ct) => All[0].Sandbox.CloseToWorkerAsync(path, ct);

    /// <summary>
    /// Deletes <paramref name="path"/> as the user it was shared with (<see cref="SharedWith"/>), under that slot's lock; a path
    /// shared with none is deleted by the held slot, or by any free one. A flow that holds no slot waits for the one it needs. A
    /// flow that holds another slot than the path's never waits for a second one (two such runs would wait on each other): it
    /// takes the path's slot only if it is free now, and otherwise throws — the owner then never walks a tree another slot's
    /// worker wrote; clean-up that may be left to a later sweep asks <see cref="TryDeleteAsWorkerAsync"/> instead.
    /// </summary>
    public async Task DeleteAsWorkerAsync(string path, CancellationToken ct)
    {
        if (Count == 1)
        {
            await All[0].Sandbox.DeleteAsWorkerAsync(path, ct);
            return;
        }
        var (held, owner) = (Held, await SharedWithAsync(path, ct));
        if (held is not null && (owner is null || owner == held))
        {
            await held.Sandbox.DeleteAsWorkerAsync(path, ct);
        }
        else if (held is null)
        {
            await HoldAsync(owner?.Number, async slot => { await slot.Sandbox.DeleteAsWorkerAsync(path, ct); return true; }, ct);
        }
        else if (!await owner!.Lock!.TryHoldAsync(() => owner.Sandbox.DeleteAsWorkerAsync(path, ct)))
        {
            throw new InvalidOperationException(
                $"{path} was shared with {owner}, whose worker phase another run holds, and this run holds {held}: it is not deleted as another user.");
        }
    }

    /// <summary>
    /// <see cref="DeleteAsWorkerAsync"/> without waiting: false, deleting nothing, while the slot the path needs (or, for a path
    /// shared with none and a flow holding none, every slot) is held by another run.
    /// </summary>
    public Task<bool> TryDeleteAsWorkerAsync(string path, CancellationToken ct) =>
        Count == 1 ? All[0].Sandbox.TryDeleteAsWorkerAsync(path, ct) : TryHoldForAsync(path, slot => slot.Sandbox.DeleteAsWorkerAsync(path, ct), ct);

    /// <summary>
    /// Runs <paramref name="phase"/> holding the slot that deletes <paramref name="path"/> — the one it was shared with
    /// (<see cref="SharedWith"/>), else the one the flow holds, else any — if that slot is free now (or the flow holds it), and
    /// answers true; false, running nothing, while another run has it. For clean-up outside a worker phase, which must not wait
    /// out another run's session and which a later sweep does as well.
    /// </summary>
    public async Task<bool> TryHoldForAsync(string path, Func<WorkerSlot, Task> phase, CancellationToken ct)
    {
        var target = Count == 1 ? All[0] : await SharedWithAsync(path, ct) ?? Held;
        foreach (var slot in target is null ? All : [target])
        {
            if (slot.Lock is null)
            {
                await phase(slot);
                return true;
            }
            if (await slot.Lock.TryHoldAsync(() => phase(slot)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The slot <paramref name="path"/> is shared with for writing (<see cref="SharedWith"/> of its <c>ls -led</c>), or null: none, or it is gone.</summary>
    private async Task<WorkerSlot?> SharedWithAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return null;
            }
            try
            {
                return SharedWith(path, await ListAcl(path, ct));
            }
            catch (InvalidOperationException) when (attempt < AclReads && Directory.Exists(path))
            {
                // ls prints an entry's UUID instead of its user name while the directory service does not answer: read again
                // before failing closed on a writer that is no slot's user.
                await Task.Delay(AclRetry, ct);
            }
        }
    }

    /// <summary>How many times a directory's ACL is read before a writer that is not a slot's user fails the deletion.</summary>
    private const int AclReads = 3;

    private static readonly TimeSpan AclRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>How a directory's ACL is read (<see cref="WorkerSandbox.ListAclAsync"/>: macOS <c>ls -led</c>); tests give their own listing.</summary>
    internal Func<string, CancellationToken, Task<string>> ListAcl { get; init; } = WorkerSandbox.ListAclAsync;

    /// <summary>One ACL entry of <c>ls -le</c> that allows something: its principal (<c>user:name</c>, <c>group:name</c>, or a bare UUID ls could not name) and its rights.</summary>
    private static readonly Regex AclEntry = new(@"^\s*[0-9]+:\s+(\S+)\s+(?:inherited\s+)?allow\s+(\S+)\s*$");

    /// <summary>The rights that let a user change what is in a directory (as macOS <c>ls -le</c> names them, for a directory and for a file).</summary>
    private static readonly HashSet<string> WriteRights =
        new(StringComparer.Ordinal) { "write", "append", "add_file", "add_subdirectory", "delete", "delete_child" };

    /// <summary>
    /// The slot whose user <paramref name="lsOutput"/> (macOS <c>ls -led</c> of the directory <paramref name="path"/>: its long
    /// listing line, then one line per ACL entry) grants a right to write, or null when it grants none but the owner one — the
    /// work root's inherited read entries, which every slot's user has, are not a share. The directory is the owner's, so only
    /// the owner (<see cref="WorkerSandbox.ShareAsync"/>) wrote that entry. Fails closed: a directory writable by a user that is
    /// not one of the pool's (a slot above <c>Worker:Slots</c>), or by two slots' users, throws rather than be deleted as the
    /// wrong user.
    /// </summary>
    public WorkerSlot? SharedWith(string path, string lsOutput)
    {
        var writers = lsOutput.Split('\n').Skip(1).Select(line => AclEntry.Match(line))
            .Where(m => m.Success && m.Groups[2].Value.Split(',').Any(WriteRights.Contains))
            .Select(m => m.Groups[1].Value)
            .Where(principal => principal != $"user:{Environment.UserName}")
            .Distinct(StringComparer.Ordinal).ToList();
        if (writers.Count == 0)
        {
            return null;
        }
        // Any other principal that may write — another user, a group, an entry ls could not name — is not a slot's user.
        var slots = writers.Select(principal => All.FirstOrDefault(s => principal == $"user:{s.User}")).ToList();
        if (writers.Count > 1 || slots[0] is null)
        {
            throw new InvalidOperationException(
                $"{path} is shared for writing with {string.Join(", ", writers)}, not with one of this factory's {Count} sandbox users "
                + $"({string.Join(", ", All.Select(s => s.User))}): raise Worker:Slots to cover that user again, or delete the directory by hand.");
        }
        return slots[0];
    }
}
