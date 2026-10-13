namespace DarkFactory.Orchestrator.Git;

/// <summary>
/// One writer per clone at a time (sc-25987): every run shares the one clone of a repository under the work root, and git does not
/// queue a second writer of its refs or worktree list — it fails (<c>cannot lock ref</c>, a clone into a directory another clone is
/// filling) or shows a half-finished state (a fetch that has pruned a ref and not yet written its replacement). So
/// <see cref="GitWorkspace"/> holds this around each git operation that changes a clone's refs or its worktree list (clone, fetch,
/// <c>remote set-head</c>, <c>worktree add</c> and <c>prune</c>, commit, merge, push) together with the reads that depend on what it
/// just wrote; reads by object (a commit's tree, files and diff) take no lock.
/// It is a lock file beside the clones (<c>&lt;work root&gt;/clone-locks/&lt;owner&gt;/&lt;name&gt;.lock</c>), held exclusively for as
/// long as it is open, so it holds between processes (<c>factory work</c> and a <c>factory run</c>) as well as between runs of one, and
/// the OS releases it if its holder dies. Never taken twice by one call chain: a second open would wait on the first for ever.
/// A wait is never silent: once it has lasted <see cref="ReportEvery"/>, and again each time that much more has passed, it is
/// reported with the lock file's path and why the last try failed (clean-up waits with no cancellation, so a stuck holder or an
/// I/O error that does not go away would otherwise hang a run with nothing logged).
/// The lock is an advisory <c>flock</c>, which any process that can open the file — even read-only — can take: <see cref="GitWorkspace"/>
/// closes the directory to the sandbox's worker user (<see cref="Worker.IWorkerSandbox.CloseToWorkerAsync"/>).
/// </summary>
public sealed class CloneLock : IDisposable
{
    /// <summary>The work root's directory of the clones' lock files.</summary>
    public const string DirectoryName = "clone-locks";

    /// <summary>How long a waiter sleeps between tries.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a wait lasts before it is reported, and between its later reports.</summary>
    public static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(30);

    /// <summary>The line a wait of <paramref name="waited"/> for the lock at <paramref name="path"/> is reported with; <paramref name="why"/> is the last try's error.</summary>
    public static string WaitReport(string path, TimeSpan waited, string why) =>
        $"[clone-lock] waited {(long)waited.TotalSeconds} s for {path} and still waiting (another run's git holds the clone, or the lock file cannot be opened): {why}";

    private readonly FileStream _file;

    private CloneLock(FileStream file) => _file = file;

    /// <summary>
    /// Takes the lock at <paramref name="path"/>, waiting (until <paramref name="ct"/> is cancelled) while another holder has it.
    /// A wait is reported to <paramref name="report"/> (standard error when null) every <paramref name="reportEvery"/>
    /// (<see cref="ReportEvery"/> when null) with <see cref="WaitReport"/>.
    /// </summary>
    public static async Task<CloneLock> AcquireAsync(string path, CancellationToken ct, TimeSpan? reportEvery = null, Action<string>? report = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var every = reportEvery ?? ReportEvery;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reported = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string why;
            try
            {
                // FileShare.None is an exclusive, non-blocking flock on Unix (as WorkerLock's).
                return new CloneLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException ex) when (File.Exists(path))
            {
                // Held: by another run's git on this clone, which ends. .NET reports a held lock and any other I/O error on an
                // existing file alike, so every one is tried again, and the report below carries the error.
                why = ex.Message;
            }
            var waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (waited - reported >= every)
            {
                reported = waited;
                (report ?? Console.Error.WriteLine)(WaitReport(path, waited, why));
            }
            await Task.Delay(Retry, ct);
        }
    }

    public void Dispose() => _file.Dispose();
}
