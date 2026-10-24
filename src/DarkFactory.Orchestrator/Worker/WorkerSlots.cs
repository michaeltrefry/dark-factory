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
/// The directory <see cref="Path"/> is writable by a principal that is not one of this factory's sandbox users (a slot above
/// <c>Worker:Slots</c>, two slots' users, a group, an entry <c>ls</c> could not name): it is deleted as no one — not as another
/// slot's user, and not by the owner walking a tree that user wrote. A deletion that must happen fails with this; clean-up that
/// may be left (<see cref="WorkerSlots.TryHoldForAsync"/>, so every sweep) leaves the directory and says so once.
/// </summary>
public sealed class ForeignWriterException(string path, string reason) : InvalidOperationException($"{path} {reason}")
{
    public string Path => path;

    /// <summary>Why, as the rest of a sentence about the directory: <c>is shared for writing with …</c>, then the two ways to get rid of it.</summary>
    public string Reason => reason;
}

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
        // Slot 1's lock file stays where it was before slots, in a directory every sandbox user reads; the others' are closed
        // to them (EnsureLocksClosedAsync), which that directory cannot be.
        if (slots.Skip(1).Any(s => SameDirectory(s.Lock!, slots[0].Lock!)))
        {
            throw new ArgumentException("A slot above 1 keeps its lock in a directory of its own, not beside slot 1's (WorkerLock.For).", nameof(slots));
        }
        All = slots;
    }

    private static bool SameDirectory(WorkerLock a, WorkerLock b) =>
        string.Equals(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path), StringComparison.Ordinal);

    public IReadOnlyList<WorkerSlot> All { get; }

    public int Count => All.Count;

    /// <summary>Slot <paramref name="slot"/>'s sandbox user: <paramref name="first"/> for slot 1, then <c>&lt;first&gt;2</c>, <c>&lt;first&gt;3</c>, … (as <c>setup-worker-user.sh</c> names them).</summary>
    public static string UserOf(string first, int slot) => slot == 1 ? first : $"{first}{slot}";

    /// <summary>Slot <paramref name="slot"/>'s launch helper: <paramref name="first"/> for slot 1, then <c>&lt;first&gt;-2</c>, <c>&lt;first&gt;-3</c>, … (as <c>setup-worker-user.sh</c> installs them).</summary>
    public static string HelperOf(string first, int slot) => slot == 1 ? first : $"{first}-{slot}";

    /// <summary>
    /// Slot <paramref name="slot"/>'s own directory of the work root for <paramref name="name"/> (sc-25990: its MCP session files,
    /// its triage worktrees, its planner seats): <paramref name="name"/> itself for slot 1 — where it was before slots — then
    /// <c>&lt;name&gt;-2</c>, <c>&lt;name&gt;-3</c>, …, as the helpers are named.
    /// </summary>
    public static string DirectoryOf(string name, int slot) => slot == 1 ? name : $"{name}-{slot}";

    /// <summary>What the owner runs to (re)install every slot's helper: <c>sudo scripts/setup-worker-user.sh</c>, with <c>--slots n</c> for more than one.</summary>
    public static string ReinstallCommand(int slots) =>
        slots == 1 ? WorkerSandbox.ReinstallCommand : $"{WorkerSandbox.ReinstallCommand} --slots {slots}";

    /// <summary>
    /// The configured pool: <paramref name="count"/> slots over the work root <paramref name="workRoot"/>, slot 1 being
    /// <paramref name="user"/> through <paramref name="helperPath"/> under the work root's lock as before slots.
    /// </summary>
    /// <paramref name="swift"/>: the Swift toolchain every slot's user must have (<c>Worker:Swift</c>, sc-26130), or null.
    public static WorkerSlots Create(string user, string helperPath, string workRoot, int count, SwiftToolchain? swift = null) =>
        new([.. Enumerable.Range(1, count).Select(n => new WorkerSlot(n,
            new WorkerSandbox(UserOf(user, n), HelperOf(helperPath, n)) { Lock = WorkerLock.For(workRoot, n), Reinstall = ReinstallCommand(count), Swift = swift }))]);

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
        await EnsureLocksClosedAsync(CancellationToken.None);
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
            await EnsureLocksClosedAsync(ct);
            return one.Lock is null ? await phase(one) : await one.Lock.HoldAsync(() =>
            {
                one.Sandbox.RequireCleanupProven();
                return phase(one);
            }, ct, whileWaiting, retry, report, reportEvery);
        }
        await EnsureLocksClosedAsync(ct);
        var candidates = (order ?? []).Where(n => n >= 1 && n <= Count).Concat(Enumerable.Range(1, Count)).Distinct().Select(n => All[n - 1]).ToList();
        var every = reportEvery ?? WorkerLock.ReportEvery;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reported = TimeSpan.Zero;
        var reportedQuarantines = new HashSet<int>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var why = "";
            var quarantined = 0;
            foreach (var candidate in candidates)
            {
                // Only judge the marker after taking this slot's lock: a running helper also has
                // a pending marker, which is not a quarantine until its phase has ended.
                var (got, result, busy) = await candidate.Lock!.TryHoldAsync(async () =>
                {
                    if (candidate.Sandbox.QuarantinePath is { } marker && File.Exists(marker))
                        return (Quarantined: true, Result: default(T));
                    return (Quarantined: false, Result: await phase(candidate));
                });
                if (got)
                {
                    if (!result.Quarantined) return result.Result!;
                    quarantined++;
                    if (reportedQuarantines.Add(candidate.Number)) (report ?? Console.Error.WriteLine)($"[sandbox] leaving quarantined worker slot {candidate.Number} ({candidate.Sandbox.User}): {candidate.Sandbox.QuarantinePath}; owner cleanup proof required");
                    continue;
                }
                why = busy;
            }
            if (quarantined == candidates.Count) candidates[0].Sandbox.RequireCleanupProven();
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

    /// <summary>Whether this pool has closed its slots' lock directory to the sandbox users (once is enough: only the owner changes it).</summary>
    private volatile bool _locksClosed;

    /// <summary>How a lock directory is closed to every sandbox user (<see cref="WorkerSandbox.CloseToWorkerAsync(string, CancellationToken)"/>: owner-side, no root, read back); tests give their own.</summary>
    internal Func<string, CancellationToken, Task>? CloseLocks { get; init; }

    /// <summary>
    /// Closes the directory the locks of the slots above 1 are in (<see cref="WorkerLock.SlotDirectoryName"/>) to every sandbox
    /// user, before this pool takes any of them: the work root's inherited ACL entry would let each slot's user read another
    /// slot's lock file, and <c>flock</c> takes an exclusive lock through a read-only descriptor, so code one slot's worker runs
    /// could hold another slot's lock and make that slot look busy. As <see cref="Git.GitWorkspace"/> closes its clone locks:
    /// owner-side (<c>chmod -N</c>, mode 0700), read back, and a directory that cannot be closed is a broken set-up, not an
    /// item's failure (<see cref="FactoryUnavailableException"/>: no slot's lock is taken). Slot 1's lock file is not in it
    /// (it is where it was before slots), and a one-slot pool has no such directory.
    /// </summary>
    public async Task EnsureLocksClosedAsync(CancellationToken ct)
    {
        if (_locksClosed)
        {
            return;
        }
        foreach (var directory in All.Skip(1).Select(s => Path.GetDirectoryName(s.Lock!.Path)!).Distinct(StringComparer.Ordinal))
        {
            try
            {
                Directory.CreateDirectory(directory);
                await (CloseLocks ?? (OperatingSystem.IsMacOS() ? All[0].Sandbox.CloseToWorkerAsync : CloseByModeAsync))(directory, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new WorkSources.FactoryUnavailableException($"the worker slots' locks: {ex.Message}", ex);
            }
        }
        _locksClosed = true;
    }

    /// <summary>
    /// Off macOS — where no sandbox user can be set up (<c>setup-worker-user.sh</c> is macOS only) and <c>chmod -N</c> does not
    /// exist: the tests' other hosts — a directory is closed by its mode alone: 0700, read back.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Task CloseByModeAsync(string path, CancellationToken ct)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, ownerOnly);
        return File.GetUnixFileMode(path) == ownerOnly
            ? Task.CompletedTask
            : throw new InvalidOperationException($"{path} is still open to others than its owner after mode 0700.");
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
    public Task EnsureReadyAsync(string claudePath, CancellationToken ct)
    {
        var slot = Held ?? (Count == 1 ? All[0] : throw new InvalidOperationException("No worker slot is held: the readiness probe is of the held slot."));
        return NamedAsync(slot, () => slot.Sandbox.EnsureReadyAsync(claudePath, ct));
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

    /// <summary>
    /// The worktree's only writers are the owner and the user of the slot the calling flow holds (<see cref="Current"/>): the push
    /// of a worker phase is of its own slot's work. With several slots the whole tree is judged, not only its root
    /// (<see cref="WorktreeWriters.Inside"/>): what a worker made in it is that user's own to open to another slot's. With one
    /// slot there is no other slot's user to open it to, and only the root's listing is read.
    /// </summary>
    public Task EnsureOnlyWorkerWritesAsync(string path, CancellationToken ct) =>
        EnsureOnlyWriterAsync(path, Current.User, ListAcl, AclRetry, ct, inside: Count > 1 ? EntryAcl : null,
            owners: Count > 1 ? EntryOwners : null);

    /// <summary>How one entry's ACL inside a worktree is read (<see cref="WorktreeWriters.NativeAcl"/>); tests give their own.</summary>
    internal Func<string, string?> EntryAcl { get; init; } = WorktreeWriters.NativeAcl;

    /// <summary>For a slot's user, the reader of who owns one entry inside a worktree (<see cref="WorktreeWriters.NativeOwners"/>); tests give their own.</summary>
    internal Func<string, Func<string, string?>> EntryOwners { get; init; } = WorktreeWriters.NativeOwners;

    /// <summary>
    /// Throws (<see cref="WorktreeWritersException"/>) unless <paramref name="listAcl"/>'s <c>ls -led</c> of the worktree
    /// <paramref name="path"/> shows no writer but its owner and <paramref name="user"/> (<see cref="WritersBeside"/>) and — with
    /// <paramref name="inside"/>, the reader of one entry's ACL: several worker slots — nothing inside it does either
    /// (<see cref="WorktreeWriters.Inside"/>; the root's group bit is then judged too) — nor, with <paramref name="owners"/>,
    /// does anyone but the owner and <paramref name="user"/> own an entry inside it (sc-26134). Read up to <see cref="PushAclReads"/>
    /// times, <paramref name="retry"/> apart, before a writer fails it (<c>ls</c> prints an entry's UUID instead of its user name,
    /// and an ACL entry has no name, while the directory service does not answer): a refusal escalates the item and its finished
    /// work is thrown away with its worktree, so one slow answer must not decide. A listing that cannot be read fails it at once.
    /// </summary>
    internal static async Task EnsureOnlyWriterAsync(string path, string user, Func<string, CancellationToken, Task<string>> listAcl,
        TimeSpan retry, CancellationToken ct, Func<string, string?>? inside = null, Func<string, Func<string, string?>>? owners = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            string listing;
            try
            {
                listing = await listAcl(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new WorktreeWritersException(path, user, $"who may write it could not be read ({ex.Message})");
            }
            var why = WritersBeside(listing, user, groupToo: inside is not null);
            if (why is null && inside is not null)
            {
                // Off the caller's thread: a walk of the whole tree, a system call or two per entry.
                // The owner reader is made again each attempt: the user's lookup may not have answered the last time.
                why = await Task.Run(() => WorktreeWriters.Inside(path, user, inside, ct, owners?.Invoke(user)), ct);
            }
            if (why is null)
            {
                return;
            }
            if (attempt >= PushAclReads)
            {
                throw new WorktreeWritersException(path, user, why);
            }
            await Task.Delay(retry, ct);
        }
    }

    /// <summary>How many times who may write a worktree is read before a writer refuses its push (with <see cref="AclRetry"/>'s 200 ms: about 2 s).</summary>
    internal const int PushAclReads = 10;

    /// <summary>
    /// Why <paramref name="lsOutput"/> (macOS <c>ls -led</c> of a worktree: its long listing line, then one line per ACL entry)
    /// lets anyone but the owner and <paramref name="user"/> write the directory, or null: no ACL entry grants another principal
    /// (another slot's user, any other user, a group, an entry ls could not name) a right to write, and its mode does not let
    /// everyone write. The work root's inherited read entries, which every slot's user has, are not write grants (the accepted
    /// cross-slot read). Fails closed: a listing with no long listing line, and any later line that is neither an <c>allow</c>
    /// nor a <c>deny</c> entry (a principal with a space in its name, a format this does not know), are each a reason.
    /// <para>The group's write bit is judged only with <paramref name="groupToo"/> (several worker slots): then it is refused
    /// like everyone's, since nothing here proves that the directory's group holds no other slot's user (the owner makes the
    /// directory, 0755 under the usual umask 022). With one slot it is deliberately not judged: there is no other slot's user
    /// to be in the group, and an owner whose umask is 002 would have every push refused for nothing.</para>
    /// </summary>
    public static string? WritersBeside(string lsOutput, string user, bool groupToo = false)
    {
        var lines = lsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var first = lines.FirstOrDefault() ?? "";
        if (!Regex.IsMatch(first, @"^d[r-][w-][xsS-][r-][w-][xsS-][r-][w-][xtT-]"))
        {
            return "who may write it could not be read (its `ls -led` listing has no directory line)";
        }
        if (first[8] == 'w')
        {
            return $"its mode ({first.Split(' ')[0]}) lets every user write it";
        }
        if (groupToo && first[5] == 'w')
        {
            return $"its mode ({first.Split(' ')[0]}) lets its group write it";
        }
        if (lines.Skip(1).FirstOrDefault(line => !AnyAclEntry.IsMatch(line)) is { } unread)
        {
            return $"an ACL line could not be read ('{unread.Trim()}')";
        }
        var others = Writers(lsOutput).Where(w => w != $"user:{user}").ToList();
        return others.Count == 0 ? null : $"its ACL lets {string.Join(", ", others)} write it";
    }

    /// <summary>One ACL entry of <c>ls -le</c>, allowing or denying: what every line after the first must be for <see cref="WritersBeside"/> to trust the listing.</summary>
    private static readonly Regex AnyAclEntry = new(@"^\s*[0-9]+:\s+\S+\s+(?:inherited\s+)?(?:allow|deny)\s+\S+\s*$");

    /// <summary>
    /// Makes <paramref name="path"/> the held slot's user's alone to read (<see cref="WorkerSandbox.RestrictToWorkerAsync(string, CancellationToken)"/>):
    /// a slot's MCP session directory, which no other slot's user may read.
    /// </summary>
    public Task RestrictToWorkerAsync(string path, CancellationToken ct) => Current.RestrictToWorkerAsync(path, ct);

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
            await ThrowIfAHigherSlotsAsync(path, ct);
            await All[0].Sandbox.DeleteAsWorkerAsync(path, ct);
            return;
        }
        // The locks' directory is closed by now or by the hold below: a flow that holds a slot took it through this pool.
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
        TryHoldForAsync(path, slot => slot.Sandbox.DeleteAsWorkerAsync(path, ct), ct);

    /// <summary>
    /// Runs <paramref name="phase"/> holding the slot that deletes <paramref name="path"/> — the one it was shared with
    /// (<see cref="SharedWith"/>), else the one the flow holds, else any — if that slot is free now (or the flow holds it), and
    /// answers true; false, running nothing, while another run has it. For clean-up outside a worker phase, which must not wait
    /// out another run's session and which a later sweep does as well.
    /// </summary>
    public async Task<bool> TryHoldForAsync(string path, Func<WorkerSlot, Task> phase, CancellationToken ct)
    {
        WorkerSlot? target;
        try
        {
            if (Count == 1)
            {
                await ThrowIfAHigherSlotsAsync(path, ct);
            }
            else
            {
                await EnsureLocksClosedAsync(ct);
            }
            target = Count == 1 ? All[0] : await SharedWithAsync(path, ct) ?? Held;
            Foreign.TryRemove(path, out _);
        }
        catch (ForeignWriterException ex)
        {
            // Not this factory's to delete, as anyone: left where it is, and said once per directory (a sweep finds it again
            // in every run's first worker phase).
            if (Foreign.TryAdd(path, ex.Reason))
            {
                Report(LeftReport(path, ex.Reason));
            }
            return false;
        }
        foreach (var slot in target is null ? All : [target])
        {
            if (slot.Lock is null)
            {
                await phase(slot);
                return true;
            }
            var (got, removed, _) = await slot.Lock.TryHoldAsync(async () =>
            {
                if (slot.Sandbox.QuarantinePath is { } marker && File.Exists(marker))
                {
                    Report($"[cleanup] leaving {path}: worker {slot} is quarantined ({marker}); owner cleanup proof required");
                    return false;
                }
                await phase(slot);
                return true;
            });
            if (got && removed) return true;
        }
        return false;
    }

    /// <summary>The directories this process left because a user that is not this factory's may write them, each with why (said once each).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Foreign = new(StringComparer.Ordinal);

    /// <summary>Where a left directory is reported (standard error; tests give their own).</summary>
    internal Action<string> Report { get; init; } = Console.Error.WriteLine;

    /// <summary>The line a directory left for a foreign writer is reported with (<paramref name="reason"/>: <see cref="ForeignWriterException.Reason"/>, which names the two ways to get rid of it).</summary>
    public static string LeftReport(string path, string reason) => $"[cleanup] leaving {path}: it {reason}";

    /// <summary>
    /// Why the last <see cref="TryHoldForAsync"/> of <paramref name="path"/> in this process left it for good — a user that is
    /// not one of this factory's may write it (<see cref="ForeignWriterException.Reason"/>) — or null: it was not tried, was
    /// deleted, or was left only because its slot was busy.
    /// </summary>
    public static string? LeftForeign(string path) => Foreign.GetValueOrDefault(path);

    /// <summary>How the owner gets rid of a directory this factory deletes as no one (the end of every <see cref="ForeignWriterException"/>).</summary>
    private static string Remedy(string path) =>
        "It is not deleted as another user, nor by the owner (who would walk a tree that user wrote), and no later sweep deletes it as things are. "
        + "To get rid of it: set Worker:Slots back so that user is one of this factory's (the next worktree sweep then deletes it as that user), "
        + $"or remove it by hand as root (`sudo rm -rf -- '{path}'`).";

    /// <summary>
    /// With one slot (no directory's ACL decides anything: everything is slot 1's, as before slots): throws when
    /// <paramref name="path"/> is writable by a higher slot's user of this factory's naming (<c>&lt;user&gt;2</c> …
    /// <c>&lt;user&gt;8</c>: <c>Worker:Slots</c> was lowered to 1 with that slot's work on disk) — slot 1's user could not delete
    /// it, and the owner would. Nothing else is judged here, and a listing that cannot be read (no <c>ls -le</c>) judges nothing.
    /// </summary>
    private async Task ThrowIfAHigherSlotsAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        string listing;
        try
        {
            listing = await ListAcl(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }
        var higher = Writers(listing).Where(w => Enumerable.Range(2, MaxSlots - 1).Any(n => w == $"user:{UserOf(All[0].User, n)}")).ToList();
        if (higher.Count > 0)
        {
            throw new ForeignWriterException(path,
                $"is shared for writing with {string.Join(", ", higher)}, a higher worker slot's sandbox user, and Worker:Slots is 1 ({All[0].User}). "
                + Remedy(path));
        }
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

    /// <summary>How long a directory's ACL read waits before the next one (tests give their own).</summary>
    internal TimeSpan AclRetry { get; init; } = TimeSpan.FromMilliseconds(200);

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
        var writers = Writers(lsOutput);
        if (writers.Count == 0)
        {
            return null;
        }
        // Any other principal that may write — another user, a group, an entry ls could not name — is not a slot's user.
        var slots = writers.Select(principal => All.FirstOrDefault(s => principal == $"user:{s.User}")).ToList();
        if (writers.Count > 1 || slots[0] is null)
        {
            throw new ForeignWriterException(path,
                $"is shared for writing with {string.Join(", ", writers)}, not with one of this factory's {Count} sandbox users "
                + $"({string.Join(", ", All.Select(s => s.User))}). " + Remedy(path));
        }
        return slots[0];
    }

    /// <summary>The principals, other than the owner, that <paramref name="lsOutput"/> (<c>ls -led</c>) grants a right to write.</summary>
    private static List<string> Writers(string lsOutput) =>
        [.. lsOutput.Split('\n').Skip(1).Select(line => AclEntry.Match(line))
            .Where(m => m.Success && m.Groups[2].Value.Split(',').Any(WriteRights.Contains))
            .Select(m => m.Groups[1].Value)
            .Where(principal => principal != $"user:{Environment.UserName}")
            .Distinct(StringComparer.Ordinal)];
}
