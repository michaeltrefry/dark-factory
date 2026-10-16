using System.Text;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>
/// The worker side of a prepare recipe (sc-26232, <c>Worker:Prepare</c>): implementers have no MCP and gain no network-reaching tool,
/// so they never download an artifact themselves. Two paths bring one into the read-only cache, both owner-side through the same
/// <see cref="ArtifactFetcher"/> and its rules:
/// <list type="number">
/// <item>Before every implementing session of a worktree that has a recipe, the pipeline fetches the recipe's artifacts
/// (<see cref="PrefetchAsync"/>), so most sessions never need to ask. A failure is logged and checkpointed, not fatal: the
/// session may be the one that fixes the recipe.</item>
/// <item>A session that changed the recipe (or whose prefetch failed) runs <c>factory-prepare --spool &lt;dir&gt;</c>
/// (<see cref="PrepareTool"/>), which writes a request into the session's spool (<see cref="OpenAsync"/>): a directory of the
/// owner's that only this session's sandbox user may add files to (<see cref="WorkerSandbox.OpenSpoolToWorkerAsync"/>) and that
/// the owner process polls while the session runs. The owner reads the recipe from the worktree itself (never through a link),
/// applies every download rule, fills the cache and answers with the steps to run — every placeholder expanded — or why not;
/// <c>factory-prepare</c> waits for the answer (bounded) and runs the steps as the worker.</item>
/// </list>
/// What this does NOT give: a reviewed recipe. The recipe a session's <c>factory-prepare</c> asks for is the worktree's as it is
/// then — the worker's own, unreviewed edit, at whatever URLs and hashes it names — and its steps' output (and so whatever the
/// artifacts it pinned make them print) reaches the session, as a build's own output does. Accepted, the residual of a build's
/// own code (CLAUDE.md, "Prepare recipes"): a worker could equally make <c>cargo build</c>'s build script fetch and print anything;
/// the session is not tainted for it, and what it then pushes goes through review (the recipe path is on the security-review
/// floor) and the gate, which runs each commit's own recipe. The worker is told only what its own recipe asked for: a hash
/// mismatch is answered "does not match the pinned sha256", never the served bytes' hash (that stays on the ledger).
/// </summary>
public sealed class WorkerPrepare(ArtifactFetcher fetcher, string spoolRoot, TextWriter log)
{
    /// <summary>How often a session's spool is read for requests.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The spool root under a work root: <c>0711</c> and no ACL entry, so a sandbox user can reach its own session's directory by name but list none.</summary>
    public static string SpoolRootOf(string workRoot) => Path.Combine(workRoot, "prepare-spool");

    /// <summary>The first line of a request <c>factory-prepare</c> writes, and its last (a request without it is not complete yet).</summary>
    public const string RequestHeader = "factory-prepare-request 1";

    public const string RequestEnd = "end";

    private static readonly Regex RequestId = new(@"^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    private const UnixFileMode SpoolRootMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const UnixFileMode AnswerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public ArtifactFetcher Fetcher => fetcher;

    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;

    /// <summary>Whether a session's spool is polled in the background (always in production; tests that poll by hand turn it off).</summary>
    internal bool PollsInBackground { get; init; } = true;

    /// <summary>
    /// The recipe in <paramref name="worktree"/>, read owner-side without trusting the tree: <c>.factory</c> must not be a link, and
    /// the file is read through <c>.factory</c>'s own descriptor (<see cref="SafeFile.ReadAt"/>: the directory opened with
    /// <c>O_NOFOLLOW | O_DIRECTORY</c>, the file with <c>openat</c> from it — no link, no FIFO, at most 1 MiB), so a <c>.factory</c>
    /// the worker swaps for a link after the check is never followed. (null, null) when there is none.
    /// <paramref name="afterCheck"/> is a test seam, run between the check and the read.
    /// </summary>
    public static (PrepareRecipe? Recipe, string? Error) ReadRecipe(string worktree, Action? afterCheck = null)
    {
        var directory = new DirectoryInfo(Path.Combine(worktree, PrepareRecipe.Directory));
        if (directory.LinkTarget is not null)
        {
            return (null, $"{PrepareRecipe.Directory} is a symbolic link, so its recipe is not read");
        }
        if (!directory.Exists)
        {
            return (null, null);
        }
        afterCheck?.Invoke();
        var read = SafeFile.ReadAt(directory.FullName, Path.GetFileName(PrepareRecipe.Path));
        if (read.IsMissing)
        {
            return (null, null);
        }
        if (read.Refusal is { } refusal)
        {
            return (null, $"{PrepareRecipe.Path} {refusal}");
        }
        try
        {
            return (PrepareRecipe.Parse(read.Text!), null);
        }
        catch (PrepareRecipeException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Fetches the artifacts of <paramref name="worktree"/>'s recipe into the cache before a session starts; why it could not, or
    /// null (also when there is no recipe).
    /// </summary>
    public async Task<string?> PrefetchAsync(string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct)
    {
        var (recipe, error) = ReadRecipe(worktree);
        if (recipe is null)
        {
            return error;
        }
        try
        {
            await fetcher.EnsureAsync(recipe, onDownloaded, ct);
            return null;
        }
        catch (ArtifactFetchException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Opens a request spool for one session in <paramref name="worktree"/> (sandboxed: <paramref name="sandbox"/>'s user may add
    /// files to it and no one else may do anything; unsandboxed the worker is the owner) and polls it until disposed.
    /// </summary>
    public async Task<PrepareSpoolSession> OpenAsync(string worktree, WorkerSandbox? sandbox, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded,
        CancellationToken ct, Action<string>? onFailed = null)
    {
        Directory.CreateDirectory(spoolRoot);
        await WorkerSandbox.ResetAsync(spoolRoot, sandbox is null ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : SpoolRootMode, ct);
        SweepStale(spoolRoot, log);
        var id = Guid.NewGuid().ToString("N");
        // The session's lock is taken before its directory exists, so a sweep never finds a live session's directory unlocked.
        var held = TryLock(spoolRoot, id) ?? throw new IOException($"the new request spool's lock {LockOf(spoolRoot, id)} is held by another process");
        var directory = Path.Combine(spoolRoot, id);
        try
        {
            Directory.CreateDirectory(directory);
            if (sandbox is null)
            {
                await WorkerSandbox.ResetAsync(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, ct);
            }
            else
            {
                await sandbox.OpenSpoolToWorkerAsync(directory, ct);
            }
        }
        catch
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            Release(spoolRoot, id, held);
            throw;
        }
        return new PrepareSpoolSession(this, directory, worktree, onDownloaded, onFailed, log, held);
    }

    private static readonly Regex SpoolId = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>A session's lock file, beside its directory in the spool root: <c>&lt;id&gt;.lock</c>, the owner's alone (0600).</summary>
    internal static string LockOf(string root, string id) => Path.Combine(root, $"{id}.lock");

    /// <summary>
    /// The session <paramref name="id"/>'s lock, held (an exclusive <c>flock</c>: <c>FileShare.None</c> on Unix, between processes and
    /// within one), or null when another holder has it — a live session.
    /// </summary>
    internal static FileStream? TryLock(string root, string id)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            return new FileStream(LockOf(root, id), options);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Removes a held lock's file, then lets the lock go.</summary>
    internal static void Release(string root, string id, FileStream held)
    {
        try
        {
            File.Delete(LockOf(root, id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: the next sweep removes an unheld lock file.
        }
        held.Dispose();
    }

    /// <summary>
    /// Removes what a process that died left in the spool root (sc-26134): every session directory whose lock no live session holds
    /// (a live session takes its lock before it makes its directory and lets it go only after removing it), and every lock file no one
    /// holds whose directory is gone. Sessions overlap — several runs, and a <c>factory run</c> beside <c>factory work</c>, share the
    /// root — so nothing is removed from "no other session exists". A directory that cannot be removed is logged and left for the next sweep.
    /// </summary>
    internal static void SweepStale(string root, TextWriter log)
    {
        var ids = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>()
            .Select(n => n.EndsWith(".lock", StringComparison.Ordinal) ? n[..^".lock".Length] : n)
            .Where(n => SpoolId.IsMatch(n)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in ids)
        {
            if (TryLock(root, id) is not { } held)
            {
                continue; // a live session's
            }
            var directory = Path.Combine(root, id);
            try
            {
                if (Directory.Exists(directory))
                {
                    // The owner's directory: the worker could only add files to it, and links are removed, not followed.
                    Directory.Delete(directory, recursive: true);
                    log.WriteLine($"[prepare] removed the request spool {directory}, which a process that died left");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"[prepare] could not remove the stale request spool {directory}: {ex.Message}");
                held.Dispose();
                continue;
            }
            Release(root, id, held);
        }
    }

    /// <summary>
    /// The prompt's paragraph for every implementing session with <c>Worker:Prepare</c> on: the command, with this session's spool. It
    /// works once the worktree has a recipe — one it had, or one the session adds.
    /// </summary>
    public static string Note(string spool) =>
        $"\n\nIf this repository has a prepare recipe ({PrepareRecipe.Path}), or once you add one, run `{PrepareTool.InstalledPath} --spool {spool}` "
        + "from the repository root before you build or test, and run it again after you change the recipe: the factory fetches the recipe's "
        + "artifacts (you cannot download them yourself) and the command then runs the recipe's steps. Without a recipe it does nothing. It "
        + "can take several minutes, so give that Bash call a timeout of 600000 ms.";

    /// <summary>The most requests one session's spool is answered with a fetch; every later one is answered <c>error</c> at once.</summary>
    public const int MaxRequestsPerSession = 20;

    /// <summary>
    /// The most one session's requests may add to the never-evicted artifact cache (sc-26134), counted at each artifact's declared
    /// limits — <c>max_bytes</c>, plus <c>max_unpacked_bytes</c> for an archive — for every artifact a request names that the cache
    /// does not hold yet, before anything is downloaded: room for the largest artifact a recipe may declare, unpacked, twice (a
    /// re-pin). A request that would pass it is refused whole, nothing fetched.
    /// </summary>
    public const long MaxSessionBytes = 2 * (PrepareRecipe.MaxArtifactBytes + PrepareRecipe.MaxUnpackedLimit);

    /// <summary>
    /// What a request of <paramref name="recipe"/> may add to the cache: the declared limits of its artifacts the cache does not hold
    /// yet (<see cref="MaxSessionBytes"/>).
    /// </summary>
    internal long Cost(PrepareRecipe recipe) => recipe.Artifacts
        .Where(a => !Directory.Exists(fetcher.Cache.EntryOf(a)) && !File.Exists(fetcher.Cache.EntryOf(a)))
        .Sum(a => a.MaxBytes + (a.Unpack == ArtifactUnpack.None ? 0 : a.MaxUnpackedBytes));

    /// <summary>
    /// Answers one request of a session in <paramref name="worktree"/>: <c>ok</c> and the worker plan
    /// (<see cref="PrepareSteps.WorkerPlan"/>), or <c>error</c> and why — NUL-terminated fields. A fetch that fails is answered with
    /// <see cref="ArtifactFetchException.WorkerMessage"/> (never the served bytes' hash); the full message goes to
    /// <paramref name="onFailed"/> (the ledger). <paramref name="budget"/>: what the session's earlier requests counted against
    /// <see cref="MaxSessionBytes"/>; a request that would pass it is refused before anything is fetched.
    /// </summary>
    internal async Task<string> AnswerAsync(string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct,
        Action<string>? onFailed = null, SessionBudget? budget = null)
    {
        var (recipe, error) = ReadRecipe(worktree);
        if (recipe is null)
        {
            return Error(error ?? $"there is no {PrepareRecipe.Path} in the worktree");
        }
        if (budget is not null)
        {
            var cost = Cost(recipe);
            if (budget.Spent + cost > MaxSessionBytes)
            {
                var why = $"the artifacts this recipe names that are not cached yet may take {cost} bytes (max_bytes, plus max_unpacked_bytes "
                    + $"for an archive), and this session's requests have already counted {budget.Spent}: more than the {MaxSessionBytes} bytes "
                    + "a session may add to the artifact cache, so nothing was fetched";
                onFailed?.Invoke(why);
                log.WriteLine($"[prepare] a session's request was refused: {why}");
                return Error(why);
            }
            budget.Spent += cost;
        }
        try
        {
            var artifacts = await fetcher.EnsureAsync(recipe, onDownloaded, ct);
            return "ok\0" + PrepareSteps.WorkerPlan(recipe, worktree, artifacts);
        }
        catch (ArtifactFetchException ex)
        {
            onFailed?.Invoke(ex.Message);
            log.WriteLine($"[prepare] a session's request could not be prepared: {ex.Message}");
            return Error(ex.WorkerMessage);
        }
        catch (PrepareRecipeException ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Error(string why)
    {
        var text = why.Replace('\0', ' ').ReplaceLineEndings(" ");
        return $"error\0{(text.Length > 2000 ? text[..2000] + "…" : text)}\0";
    }

    /// <summary>One request file of the spool, if it is complete: its id, or null (not a request, or not written to its end yet).</summary>
    internal static string? CompleteRequest(string path, out string? refusal)
    {
        refusal = null;
        var name = Path.GetFileName(path);
        if (!name.StartsWith("request-", StringComparison.Ordinal) || !RequestId.IsMatch(name["request-".Length..]))
        {
            return null;
        }
        var read = SafeFile.Read(path);
        if (read.Refusal is { } why)
        {
            refusal = $"the request {why}";
            return name["request-".Length..];
        }
        var lines = (read.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2 && lines[0] == RequestHeader && lines[^1] == RequestEnd ? name["request-".Length..] : null;
    }

    /// <summary>
    /// Writes <paramref name="answer"/> as <c>response-&lt;id&gt;</c>, mode 0644, whole or not at all: into a new file of a random
    /// name first (created new, so never through anything the worker put in the spool), then renamed into place — a rename replaces
    /// whatever the worker may have put at that name (a link is replaced, never followed), and the worker sees the answer only
    /// complete.
    /// </summary>
    internal static void WriteAnswer(string directory, string id, string answer)
    {
        var temp = Path.Combine(directory, $".answer-{Guid.NewGuid():N}");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = AnswerMode;
        }
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(answer));
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, AnswerMode); // the umask may have taken read from the worker
        }
        File.Move(temp, Path.Combine(directory, $"response-{id}"), overwrite: true);
    }
}

/// <summary>What one session's requests have counted against <see cref="WorkerPrepare.MaxSessionBytes"/>.</summary>
internal sealed class SessionBudget
{
    public long Spent { get; set; }
}

/// <summary>A worker session's request spool (<see cref="WorkerPrepare.OpenAsync"/>), polled until disposed; its directory is then removed.</summary>
public sealed class PrepareSpoolSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly SessionBudget _budget = new();
    private readonly SemaphoreSlim _polling = new(1, 1);
    private readonly WorkerPrepare _prepare;
    private readonly string _worktree;
    private readonly Func<ArtifactDownloaded, CancellationToken, Task>? _onDownloaded;
    private readonly Action<string>? _onFailed;
    private readonly TextWriter _log;

    private readonly FileStream _held;

    internal PrepareSpoolSession(WorkerPrepare prepare, string directory, string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded,
        Action<string>? onFailed, TextWriter log, FileStream held)
    {
        (_prepare, Directory, _worktree, _onDownloaded, _onFailed, _log, _held) = (prepare, directory, worktree, onDownloaded, onFailed, log, held);
        _loop = prepare.PollsInBackground ? Task.Run(LoopAsync) : Task.CompletedTask;
    }

    public string Directory { get; }

    /// <summary>How many requests have been answered.</summary>
    public int Answered { get; private set; }

    /// <summary>How many requests were answered with a fetch (at most <see cref="WorkerPrepare.MaxRequestsPerSession"/>).</summary>
    public int Fetched { get; private set; }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_prepare.PollInterval);
        try
        {
            do
            {
                try
                {
                    await PollOnceAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
                {
                    // Whatever one poll meets, the session's later requests are still answered.
                    _log.WriteLine($"[prepare] a poll of the request spool failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One pass over the spool. Passes never overlap (a second caller waits for the first), so a request is answered exactly once,
    /// and every complete request a pass saw is answered (or failed to be written) when it returns.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        await _polling.WaitAsync(ct);
        try
        {
            await PollLockedAsync(ct);
        }
        finally
        {
            _polling.Release();
        }
    }

    private async Task PollLockedAsync(CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(Directory, "request-*").Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files)
        {
            if (WorkerPrepare.CompleteRequest(file, out var refusal) is not { } id || !_handled.Add(id))
            {
                continue;
            }
            string answer;
            try
            {
                if (refusal is not null)
                {
                    answer = $"error\0{refusal}\0";
                }
                else if (Fetched >= WorkerPrepare.MaxRequestsPerSession)
                {
                    answer = $"error\0this session has already asked {WorkerPrepare.MaxRequestsPerSession} times, the most a session may: nothing more is fetched for it\0";
                }
                else
                {
                    Fetched++;
                    answer = await _prepare.AnswerAsync(_worktree, _onDownloaded, ct, _onFailed, _budget);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                answer = $"error\0the factory could not answer: {ex.Message.ReplaceLineEndings(" ").Replace('\0', ' ')}\0";
            }
            try
            {
                WorkerPrepare.WriteAnswer(Directory, id, answer);
                Answered++;
                _log.WriteLine($"[prepare] answered request {id}: {answer.Split('\0')[0]}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.WriteLine($"[prepare] request {id} could not be answered: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.WriteLine($"[prepare] the request spool's poll ended with {ex.Message}");
        }
        try
        {
            // The owner's directory: the worker could only add files (never a subdirectory), and links are removed, not followed.
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.WriteLine($"[prepare] could not remove the request spool {Directory}: {ex.Message}");
        }
        // Its lock goes last: a directory left above is then the next sweep's to remove.
        WorkerPrepare.Release(Path.GetDirectoryName(Directory)!, Path.GetFileName(Directory), _held);
        _stop.Dispose();
        _polling.Dispose();
    }
}
