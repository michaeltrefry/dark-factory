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
        var directory = Path.Combine(spoolRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
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
            Directory.Delete(directory, recursive: true);
            throw;
        }
        return new PrepareSpoolSession(this, directory, worktree, onDownloaded, onFailed, log);
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
    /// Answers one request of a session in <paramref name="worktree"/>: <c>ok</c> and the worker plan
    /// (<see cref="PrepareSteps.WorkerPlan"/>), or <c>error</c> and why — NUL-terminated fields. A fetch that fails is answered with
    /// <see cref="ArtifactFetchException.WorkerMessage"/> (never the served bytes' hash); the full message goes to
    /// <paramref name="onFailed"/> (the ledger).
    /// </summary>
    internal async Task<string> AnswerAsync(string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct,
        Action<string>? onFailed = null)
    {
        var (recipe, error) = ReadRecipe(worktree);
        if (recipe is null)
        {
            return Error(error ?? $"there is no {PrepareRecipe.Path} in the worktree");
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

/// <summary>A worker session's request spool (<see cref="WorkerPrepare.OpenAsync"/>), polled until disposed; its directory is then removed.</summary>
public sealed class PrepareSpoolSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly WorkerPrepare _prepare;
    private readonly string _worktree;
    private readonly Func<ArtifactDownloaded, CancellationToken, Task>? _onDownloaded;
    private readonly Action<string>? _onFailed;
    private readonly TextWriter _log;

    internal PrepareSpoolSession(WorkerPrepare prepare, string directory, string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded,
        Action<string>? onFailed, TextWriter log)
    {
        (_prepare, Directory, _worktree, _onDownloaded, _onFailed, _log) = (prepare, directory, worktree, onDownloaded, onFailed, log);
        _loop = Task.Run(LoopAsync);
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
                await PollOnceAsync(_stop.Token);
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
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
                    answer = await _prepare.AnswerAsync(_worktree, _onDownloaded, ct, _onFailed);
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
        _stop.Dispose();
    }
}
