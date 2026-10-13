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
/// </summary>
public sealed class CloneLock : IDisposable
{
    /// <summary>The work root's directory of the clones' lock files.</summary>
    public const string DirectoryName = "clone-locks";

    /// <summary>How long a waiter sleeps between tries.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(50);

    private readonly FileStream _file;

    private CloneLock(FileStream file) => _file = file;

    /// <summary>Takes the lock at <paramref name="path"/>, waiting (until <paramref name="ct"/> is cancelled) while another holder has it.</summary>
    public static async Task<CloneLock> AcquireAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None is an exclusive, non-blocking flock on Unix (as WorkerLock's).
                return new CloneLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (File.Exists(path))
            {
                // Held: by another run's git on this clone, which ends.
            }
            await Task.Delay(Retry, ct);
        }
    }

    public void Dispose() => _file.Dispose();
}
