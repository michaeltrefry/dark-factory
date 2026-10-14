using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

/// <summary>
/// A sandbox user's lock (sc-25988, E6) — one per worker slot (sc-25989: <see cref="Worker.WorkerSlots"/> hands the slots out;
/// what is said here of "the sandbox user" holds for each slot's): the launch helper kills every process of its worker user when
/// it exits, so a second run of that helper beside a worker would destroy that worker. Whatever runs as the worker user therefore holds this lock for as long
/// as it does: a worker session (implement, a fix round, a CI or conflict fix, a triage, a planner), the gate's sandboxed test
/// runs, and every deletion or stop done through the helper. It is held for such a phase only — not for a whole run, which waits
/// on reviews, CI and the merge queue without it — and it is waited for, never failed on, so runs overlap outside those phases.
/// It is a lock file (<see cref="FileName"/> in the work root), held exclusively for as long as it is open, so it holds between
/// processes (<c>factory work</c> and a <c>factory run</c>) as well as between the runs of one; the OS releases it if its holder dies.
/// Re-entrant per async flow (<see cref="HoldAsync{T}"/>): a phase holds it, and what the phase calls — the sandbox's own
/// commands, a seat inside the run — finds it held rather than waiting on itself. Value equality by path.
/// <para>It is never released under a launch helper a hold started: a helper that has not exited when its phase gives up on it
/// (it would not stop within the grace) is handed to <see cref="KeepUntil"/>, and the lock file then stays open — against every
/// run and process — until that helper is gone, since its exit is still to kill every process of the sandbox user.</para>
/// <para>No wait is silent, and a lock that cannot be had is the factory's failure, not an item's: a wait is reported once it has
/// lasted <see cref="ReportEvery"/> and again each time that much more has passed (<see cref="WaitReport"/>, with why the last try
/// failed — .NET reports a held lock and any other I/O error on an existing file alike, so a persistent error is retried and
/// shows there); a lock file that cannot be opened for another reason (a work root that cannot be made, no access) is a
/// <see cref="FactoryUnavailableException"/> (E10).</para>
/// </summary>
public sealed record WorkerLock(string Path)
{
    public const string FileName = ".factory-run.lock";

    /// <summary>How long a waiter sleeps between tries when the caller names no interval.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a wait lasts before it is reported, and between its later reports; also between the reports of a lock kept for a helper.</summary>
    public static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(30);

    /// <summary>The line a wait of <paramref name="waited"/> for the lock at <paramref name="path"/> is reported with; <paramref name="why"/> is the last try's error.</summary>
    public static string WaitReport(string path, TimeSpan waited, string why) =>
        $"[lock] waited {(long)waited.TotalSeconds} s for the sandbox user's lock {path} and still waiting (another run's worker phase holds it, "
        + $"a launch helper that would not stop is still alive, or the lock file cannot be opened): {why}";

    /// <summary>The line a lock kept for <paramref name="what"/> (<see cref="KeepUntil"/>) is reported with: when its phase ends, and every <see cref="ReportEvery"/> after.</summary>
    public static string KeptReport(string path, TimeSpan kept, string what) =>
        $"[lock] the sandbox user's lock {path} stays held, {(long)kept.TotalSeconds} s after its phase ended: {what} is still alive, and its exit "
        + "kills every process of the sandbox user. No worker phase of any run starts until it is gone; kill it if it never exits.";

    /// <summary>The holds of the current async flow, by lock path. A hold that has ended is not one, whoever still carries it.</summary>
    private static readonly AsyncLocal<IReadOnlyList<Hold>?> Flow = new();

    private sealed class Hold(string path)
    {
        public string Path => path;
        public volatile bool Ended;

        /// <summary>What the lock is kept for after the phase (<see cref="KeepUntil"/>).</summary>
        public List<Kept> Kept { get; } = [];
    }

    /// <summary>Something a hold started that outlives it: the lock stays until <paramref name="Gone"/> completes.</summary>
    private sealed record Kept(Task Gone, string What, Action<string> Report, TimeSpan Every);

    /// <summary>The lock of the work root <paramref name="workRoot"/>: its first (or only) sandbox user's, slot 1's.</summary>
    public static WorkerLock For(string workRoot) => For(workRoot, 1);

    /// <summary>
    /// The lock of slot <paramref name="slot"/>'s sandbox user in the work root <paramref name="workRoot"/> (sc-25989): slot 1's is
    /// <see cref="FileName"/>, the file it was before slots (E9), in the work root; slot n's is <c>.factory-run-n.lock</c> in the
    /// work root's <see cref="SlotDirectoryName"/> — a directory the pool closes to every sandbox user before it takes one
    /// (<see cref="Worker.WorkerSlots.EnsureLocksClosedAsync"/>), since a user that can read a lock file can hold its lock.
    /// </summary>
    public static WorkerLock For(string workRoot, int slot) => new(slot == 1
        ? System.IO.Path.Combine(System.IO.Path.GetFullPath(workRoot), FileName)
        : System.IO.Path.Combine(System.IO.Path.GetFullPath(workRoot), SlotDirectoryName, $".factory-run-{slot}.lock"));

    /// <summary>The directory, in the work root, of the lock files of the slots above 1: closed to every sandbox user.</summary>
    public const string SlotDirectoryName = "slot-locks";

    /// <summary>
    /// Whether the current async flow holds the lock: it is inside a <see cref="HoldAsync{T}"/> of it that has not ended. A task
    /// started inside a hold and still running after it (a watcher, a recorder) no longer holds it.
    /// </summary>
    public bool IsHeld => Flow.Value?.Any(h => h.Path == Path && !h.Ended) == true;

    /// <summary>Throws unless the current flow holds the lock: <paramref name="what"/> would run as the worker user without it.</summary>
    public void Require(string what)
    {
        if (!IsHeld)
        {
            throw new InvalidOperationException(
                $"Refusing {what}: the worker lock {Path} is not held, and a run of the launch helper beside another run's worker would kill it.");
        }
    }

    /// <summary>
    /// Keeps the lock past the end of the current flow's hold until <paramref name="gone"/> completes: for a launch helper the hold
    /// started that is still alive when its phase gives up on it (<paramref name="what"/> names it, with its pid). The hold ends for
    /// the flow as usual; the lock file stays open, so no other run or process gets the sandbox user, until then. Reported to
    /// <paramref name="report"/> (standard error when null) when the phase ends and every <paramref name="reportEvery"/>
    /// (<see cref="ReportEvery"/> when null) after, with <see cref="KeptReport"/>. Answers false, keeping nothing, when the flow
    /// holds no lock.
    /// </summary>
    public bool KeepUntil(Task gone, string what, Action<string>? report = null, TimeSpan? reportEvery = null)
    {
        if (Flow.Value?.FirstOrDefault(h => h.Path == Path && !h.Ended) is not { } hold)
        {
            return false;
        }
        lock (hold.Kept)
        {
            hold.Kept.Add(new Kept(gone, what, report ?? Console.Error.WriteLine, reportEvery ?? ReportEvery));
        }
        return true;
    }

    /// <summary>
    /// Runs <paramref name="phase"/> holding the lock, waiting for it (until <paramref name="ct"/> is cancelled) while another run
    /// or process has it. While it waits, <paramref name="whileWaiting"/> runs after every failed try (every <paramref name="retry"/>,
    /// <see cref="DefaultRetry"/> when null) and may throw to give up the wait — a run honours Pause and Stop there — and the wait
    /// is reported to <paramref name="report"/> (standard error when null) every <paramref name="reportEvery"/>
    /// (<see cref="ReportEvery"/> when null) with <see cref="WaitReport"/>. A flow that already holds the lock runs
    /// <paramref name="phase"/> at once.
    /// </summary>
    /// <exception cref="FactoryUnavailableException">The lock file cannot be opened, and not because it is held.</exception>
    public async Task<T> HoldAsync<T>(Func<Task<T>> phase, CancellationToken ct, Func<CancellationToken, Task>? whileWaiting = null, TimeSpan? retry = null,
        Action<string>? report = null, TimeSpan? reportEvery = null)
    {
        if (IsHeld)
        {
            return await phase();
        }
        var every = reportEvery ?? ReportEvery;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reported = TimeSpan.Zero;
        FileStream file;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryOpen(out var why) is { } opened)
            {
                file = opened;
                break;
            }
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (waited - reported >= every)
            {
                reported = waited;
                (report ?? Console.Error.WriteLine)(WaitReport(Path, waited, why));
            }
            if (whileWaiting is not null)
            {
                await whileWaiting(ct);
            }
            await Task.Delay(retry ?? DefaultRetry, ct);
        }
        return await HeldAsync(file, phase);
    }

    /// <summary>Runs <paramref name="phase"/> as this flow's hold of the lock, which <paramref name="file"/> (open, exclusive) is.</summary>
    private async Task<T> HeldAsync<T>(FileStream file, Func<Task<T>> phase)
    {
        var hold = new Hold(Path);
        try
        {
            // Set in this method, which the phase is called from: it flows into the phase and not back to the caller.
            Flow.Value = [.. Flow.Value ?? [], hold];
            return await phase();
        }
        finally
        {
            hold.Ended = true;
            Kept[] alive;
            lock (hold.Kept)
            {
                alive = [.. hold.Kept.Where(k => !k.Gone.IsCompleted)];
            }
            if (alive.Length == 0)
            {
                file.Dispose();
            }
            else
            {
                // A helper this hold started is still alive: the flow goes on (it has given up on it), the lock does not.
                _ = ReleaseWhenGoneAsync(file, alive);
            }
        }
    }

    /// <summary>Closes the lock file once everything in <paramref name="alive"/> is gone, reporting each meanwhile (never silent).</summary>
    private async Task ReleaseWhenGoneAsync(FileStream file, Kept[] alive)
    {
        try
        {
            await Task.WhenAll(alive.Select(async kept =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                while (true)
                {
                    try
                    {
                        kept.Report(KeptReport(Path, System.Diagnostics.Stopwatch.GetElapsedTime(started), kept.What));
                    }
                    catch (Exception)
                    {
                        // A report that cannot be written neither releases the lock early nor keeps it for ever.
                    }
                    if (await Task.WhenAny(kept.Gone, Task.Delay(kept.Every)) == kept.Gone)
                    {
                        return;
                    }
                }
            }));
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary><see cref="HoldAsync{T}"/> for a phase with no result.</summary>
    public Task HoldAsync(Func<Task> phase, CancellationToken ct, Func<CancellationToken, Task>? whileWaiting = null, TimeSpan? retry = null,
        Action<string>? report = null, TimeSpan? reportEvery = null) =>
        HoldAsync(async () => { await phase(); return true; }, ct, whileWaiting, retry, report, reportEvery);

    /// <summary>
    /// Runs <paramref name="phase"/> holding the lock if it is free now (or this flow holds it already) and answers true; answers
    /// false, running nothing, when another run or process has it — for clean-up that a later sweep does as well.
    /// </summary>
    public async Task<bool> TryHoldAsync(Func<Task> phase) =>
        (await TryHoldAsync(async () => { await phase(); return true; })).Held;

    /// <summary>
    /// <see cref="TryHoldAsync(Func{Task})"/> for a phase with a result: whether the lock was had (<c>Why</c>: the error that
    /// says it is held, when not), and then what <paramref name="phase"/> answered.
    /// </summary>
    public async Task<(bool Held, T? Result, string Why)> TryHoldAsync<T>(Func<Task<T>> phase)
    {
        if (IsHeld)
        {
            return (true, await phase(), "");
        }
        if (TryOpen(out var why) is not { } file)
        {
            return (false, default, why);
        }
        return (true, await HeldAsync(file, phase), "");
    }

    /// <summary>
    /// Takes the lock for as long as the answer is not disposed, or fails at once when it is held: for a process that must keep
    /// every sandboxed run out for its whole life (the acceptance harness), not for a run. <paramref name="slot"/>: which slot's
    /// sandbox user (sc-25989; a process that must keep out every slot takes each).
    /// </summary>
    public static IDisposable Acquire(string workRoot, int slot = 1)
    {
        var workerLock = For(workRoot, slot);
        return workerLock.TryOpen(out _) ?? throw new InvalidOperationException(
            $"Another factory run is using {workRoot} ({workerLock.Path} is locked): a worker phase of it holds the sandbox user.");
    }

    /// <summary>
    /// The lock file opened exclusively, or null while another holder has it (<paramref name="why"/>: the error that says so). Any
    /// other failure — the work root cannot be made, the file cannot be created or opened — is the factory's
    /// (<see cref="FactoryUnavailableException"/>): no run can use the sandbox user until it is fixed, and no item is to blame.
    /// </summary>
    private FileStream? TryOpen(out string why)
    {
        why = "";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // FileShare.None is an exclusive, non-blocking flock on Unix.
            return new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (File.Exists(Path))
        {
            // Held: by another run's worker phase, which ends. .NET reports a held lock and any other I/O error on an existing
            // file alike, so every one is tried again, and the wait's report carries the error.
            why = ex.Message;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            throw new FactoryUnavailableException($"the sandbox user's lock: {Path} cannot be opened ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }
}
