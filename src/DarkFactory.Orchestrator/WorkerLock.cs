namespace DarkFactory.Orchestrator;

/// <summary>
/// One factory run per work root at a time. The sandbox launch helper kills every process of the
/// worker user when it exits, and the startup sweep deletes every worktree, so a second concurrent
/// run would destroy the first one's worker and worktree. Held for the whole run; the OS releases
/// it if the orchestrator dies.
/// </summary>
public sealed class WorkerLock : IDisposable
{
    public const string FileName = ".factory-run.lock";

    private readonly FileStream _file;

    private WorkerLock(FileStream file) => _file = file;

    /// <summary>Takes the lock or fails fast if another run holds it.</summary>
    public static WorkerLock Acquire(string workRoot)
    {
        Directory.CreateDirectory(workRoot);
        var path = Path.Combine(workRoot, FileName);
        try
        {
            // FileShare.None is an exclusive, non-blocking flock on Unix.
            return new WorkerLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another factory run is using {workRoot} ({path} is locked); only one worker may run at a time.", ex);
        }
    }

    public void Dispose() => _file.Dispose();
}
