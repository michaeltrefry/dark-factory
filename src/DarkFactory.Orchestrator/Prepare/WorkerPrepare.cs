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
/// Downloading a recipe-declared artifact is not untrusted input in the E4 sense: it is hash-pinned by a reviewed file, and
/// fetched by the harness, never shown to a model. The recipe text in a worktree is the worker's own edit (it is reviewed, and the
/// gate runs each commit's own recipe); what a worker could make a step run, it could equally make <c>cargo build</c>'s build
/// script run (CLAUDE.md's residual).
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
    /// The recipe in <paramref name="worktree"/>, read owner-side without trusting the tree: <c>.factory</c> must not be a link and
    /// the file is read as <see cref="SafeFile"/> reads (no link, no FIFO, at most 1 MiB). (null, null) when there is none.
    /// </summary>
    public static (PrepareRecipe? Recipe, string? Error) ReadRecipe(string worktree)
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
        var read = SafeFile.Read(Path.Combine(worktree, PrepareRecipe.Path));
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
        CancellationToken ct)
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
        return new PrepareSpoolSession(this, directory, worktree, onDownloaded, log);
    }

    /// <summary>The prompt's paragraph for a session whose worktree has a recipe: the command, with this session's spool.</summary>
    public static string Note(string spool) =>
        $"\n\nThis repository has a prepare recipe ({PrepareRecipe.Path}). Before you build or test, run `{PrepareTool.InstalledPath} --spool {spool}` "
        + "from the repository root, and run it again after you change the recipe: the factory fetches the recipe's artifacts (you cannot "
        + "download them yourself) and the command then runs the recipe's steps. It can take several minutes, so give that Bash call a "
        + "timeout of 600000 ms.";

    /// <summary>
    /// Answers one request of a session in <paramref name="worktree"/>: <c>ok</c> and the worker plan
    /// (<see cref="PrepareSteps.WorkerPlan"/>), or <c>error</c> and why — NUL-terminated fields.
    /// </summary>
    internal async Task<string> AnswerAsync(string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct)
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
        catch (Exception ex) when (ex is ArtifactFetchException or PrepareRecipeException)
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
    private readonly TextWriter _log;

    internal PrepareSpoolSession(WorkerPrepare prepare, string directory, string worktree, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, TextWriter log)
    {
        (_prepare, Directory, _worktree, _onDownloaded, _log) = (prepare, directory, worktree, onDownloaded, log);
        _loop = Task.Run(LoopAsync);
    }

    public string Directory { get; }

    /// <summary>How many requests have been answered.</summary>
    public int Answered { get; private set; }

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
                answer = refusal is null
                    ? await _prepare.AnswerAsync(_worktree, _onDownloaded, ct)
                    : $"error\0{refusal}\0";
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
