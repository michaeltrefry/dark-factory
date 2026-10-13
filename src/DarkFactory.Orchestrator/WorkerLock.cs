namespace DarkFactory.Orchestrator;

/// <summary>
/// The sandbox user's lock (sc-25988, E6): the launch helper kills every process of the worker user when it exits, so a second
/// helper run beside a worker would destroy that worker. Whatever runs as the worker user therefore holds this lock for as long
/// as it does: a worker session (implement, a fix round, a CI or conflict fix, a triage, a planner), the gate's sandboxed test
/// runs, and every deletion or stop done through the helper. It is held for such a phase only — not for a whole run, which waits
/// on reviews, CI and the merge queue without it — and it is waited for, never failed on, so runs overlap outside those phases.
/// It is a lock file (<see cref="FileName"/> in the work root), held exclusively for as long as it is open, so it holds between
/// processes (<c>factory work</c> and a <c>factory run</c>) as well as between the runs of one; the OS releases it if its holder dies.
/// Re-entrant per async flow (<see cref="HoldAsync{T}"/>): a phase holds it, and what the phase calls — the sandbox's own
/// commands, a seat inside the run — finds it held rather than waiting on itself. Value equality by path.
/// </summary>
public sealed record WorkerLock(string Path)
{
    public const string FileName = ".factory-run.lock";

    /// <summary>How long a waiter sleeps between tries when the caller names no interval.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>The holds of the current async flow, by lock path. A hold that has ended is not one, whoever still carries it.</summary>
    private static readonly AsyncLocal<IReadOnlyList<Hold>?> Flow = new();

    private sealed class Hold(string path)
    {
        public string Path => path;
        public volatile bool Ended;
    }

    /// <summary>The lock of the work root <paramref name="workRoot"/>.</summary>
    public static WorkerLock For(string workRoot) => new(System.IO.Path.Combine(System.IO.Path.GetFullPath(workRoot), FileName));

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
    /// Runs <paramref name="phase"/> holding the lock, waiting for it (until <paramref name="ct"/> is cancelled) while another run
    /// or process has it. While it waits, <paramref name="whileWaiting"/> runs after every failed try (every <paramref name="retry"/>,
    /// <see cref="DefaultRetry"/> when null) and may throw to give up the wait — a run honours Pause and Stop there. A flow that
    /// already holds the lock runs <paramref name="phase"/> at once.
    /// </summary>
    public async Task<T> HoldAsync<T>(Func<Task<T>> phase, CancellationToken ct, Func<CancellationToken, Task>? whileWaiting = null, TimeSpan? retry = null)
    {
        if (IsHeld)
        {
            return await phase();
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        FileStream file;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryOpen() is { } opened)
            {
                file = opened;
                break;
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
            file.Dispose();
        }
    }

    /// <summary><see cref="HoldAsync{T}"/> for a phase with no result.</summary>
    public Task HoldAsync(Func<Task> phase, CancellationToken ct, Func<CancellationToken, Task>? whileWaiting = null, TimeSpan? retry = null) =>
        HoldAsync(async () => { await phase(); return true; }, ct, whileWaiting, retry);

    /// <summary>
    /// Runs <paramref name="phase"/> holding the lock if it is free now (or this flow holds it already) and answers true; answers
    /// false, running nothing, when another run or process has it — for clean-up that a later sweep does as well.
    /// </summary>
    public async Task<bool> TryHoldAsync(Func<Task> phase)
    {
        if (IsHeld)
        {
            await phase();
            return true;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (TryOpen() is not { } file)
        {
            return false;
        }
        await HeldAsync(file, async () => { await phase(); return true; });
        return true;
    }

    /// <summary>
    /// Takes the lock for as long as the answer is not disposed, or fails at once when it is held: for a process that must keep
    /// every sandboxed run out for its whole life (the acceptance harness), not for a run.
    /// </summary>
    public static IDisposable Acquire(string workRoot)
    {
        var workerLock = For(workRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(workerLock.Path)!);
        return workerLock.TryOpen() ?? throw new InvalidOperationException(
            $"Another factory run is using {workRoot} ({workerLock.Path} is locked): a worker phase of it holds the sandbox user.");
    }

    /// <summary>The lock file opened exclusively, or null while another holder has it.</summary>
    private FileStream? TryOpen()
    {
        try
        {
            // FileShare.None is an exclusive, non-blocking flock on Unix.
            return new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) when (File.Exists(Path))
        {
            // Held: by another run's worker phase, which ends. .NET reports a held lock and any other I/O error on an existing
            // file alike, so every one is tried again.
            return null;
        }
    }
}
