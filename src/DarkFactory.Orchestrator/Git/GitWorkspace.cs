using System.Diagnostics;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Git;

/// <param name="GitDir">The worktree's git admin dir in the owner's clone, recorded before the worker runs.</param>
public sealed record Workspace(string Path, string Branch, string BaseBranch, string GitDir);

public interface IRepoWorkspace
{
    Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct);

    /// <summary>
    /// Re-creates a story's worktree from its already-pushed branch (<c>origin/&lt;branch&gt;</c>)
    /// instead of the base branch, so pushed work is continued rather than redone.
    /// </summary>
    Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct);

    /// <summary>
    /// Returns a story's existing worktree as left behind by an interrupted run, without
    /// touching it, or null when it is gone or no longer on <paramref name="branch"/>.
    /// </summary>
    Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct);

    /// <summary>
    /// Commits any worker changes and pushes the branch. Returns false when there is nothing to push. The push token is minted only
    /// with a <paramref name="grant"/> (<see cref="Ledger.WorkLedger.GrantPushAsync"/>: the worker sessions whose work this publishes
    /// are untainted, E4).
    /// </summary>
    Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct);

    /// <summary>The commit the worktree's HEAD is at (after <see cref="CommitAndPushAsync"/>: the commit it pushed).</summary>
    Task<string> HeadAsync(Workspace workspace, CancellationToken ct);

    /// <summary>
    /// Merges the base branch as last fetched (<c>origin/&lt;base&gt;</c>; <see cref="RestoreAsync"/> fetches) into the
    /// worktree's branch, owner-side (no worker runs git), committing the merge when it is clean. A conflicted merge is left in
    /// progress with its conflict markers in the files; a <see cref="CommitAndPushAsync"/> after they are resolved commits it.
    /// </summary>
    Task<Gate.BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct);

    /// <summary>Pushes the worktree's HEAD to its <c>factory/*</c> branch as a fast-forward only (never forced): a branch that moved on origin refuses it.</summary>
    Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct);

    /// <summary>Of <paramref name="paths"/>, those whose content at commit <paramref name="sha"/> (in the clone) still has a conflict marker line.</summary>
    Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Removes a story's worktree; the clone and any pushed branch stay.</summary>
    Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct);
}

/// <summary>Runs one git command in <paramref name="cwd"/> with extra environment (null for none).</summary>
public delegate Task<string> GitCommand(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args, CancellationToken ct);

/// <summary>
/// Runs one git command in <paramref name="cwd"/>, handing its standard output, as bytes, to <paramref name="read"/> as it arrives.
/// <paramref name="read"/> answers true when it read to the end (git's exit status is then checked) or false when it stopped early
/// (git, still running, is stopped with its whole process tree and its status is not checked).
/// </summary>
public delegate Task GitStreamCommand(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args,
    Func<Stream, CancellationToken, Task<bool>> read, CancellationToken ct);

/// <summary>
/// Keeps one clone per target repo under the work root and gives each story its
/// own git worktree on <c>factory/sc-&lt;id&gt;</c>. Network git calls authenticate
/// with an installation token passed through git's environment-based config, so
/// the token never appears in argv, remotes or files.
/// With a sandbox, each fresh worktree is shared with the worker user (unless <paramref name="shareWithWorker"/> is false: a
/// read-only session's worktree, which the worker user reads through the work root's inherited read entry and cannot write); owner-side git
/// on it always names the git dir explicitly, so a worker-edited <c>.git</c> file can't
/// point the owner's git at a repository (config, hooks) the worker controls. <paramref name="worktreesDirectory"/> is the work
/// root's directory the worktrees live in (and <see cref="SweepOrphansAsync"/> sweeps).
/// </summary>
public sealed class GitWorkspace(
    string workRoot,
    Func<RepoRef, string> remoteUrl,
    Func<RepoRef, CancellationToken, Task<string?>> token,
    GitCommand? git = null,
    IWorkerSandbox? sandbox = null,
    string worktreesDirectory = GitWorkspace.ItemWorktrees,
    bool shareWithWorker = true,
    GitStreamCommand? gitStream = null)
    : IRepoWorkspace, Gate.IReviewFiles
{
    private readonly GitCommand _git = git ?? RunGitAsync;
    private readonly GitStreamCommand _gitStream = gitStream ?? RunGitStreamAsync;

    public const string BranchPrefix = "factory/";

    /// <summary>The work root's directory of the items' worktrees (a triage's live apart, <see cref="SandboxTriageRunner.TriageWorktrees"/>).</summary>
    public const string ItemWorktrees = "worktrees";
    private const string CommitterName = "dark-factory[bot]";
    private const string CommitterEmail = "dark-factory@users.noreply.github.com";

    /// <summary>The repository's github.com remote (<see cref="GitRemoteReads.GitHubRemote"/>: the gateway owns it).</summary>
    public static string GitHubRemote(RepoRef repo) => GitRemoteReads.GitHubRemote(repo);

    public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) =>
        CreateWorktreeAsync(repo, branch, baseBranch => $"origin/{baseBranch}", ct);

    public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) =>
        CreateWorktreeAsync(repo, branch, _ => $"origin/{branch}", ct);

    private async Task<Workspace> CreateWorktreeAsync(RepoRef repo, string branch, Func<string, string> startPoint, CancellationToken ct)
    {
        EnsureFactoryBranch(branch);
        var clone = await FetchAsync(repo, ct);
        var baseBranch = await BaseBranchAsync(clone, ct);
        return await AddWorktreeAsync(clone, WorktreePath(repo, branch), branch, baseBranch, ["-B", branch], startPoint(baseBranch), ct);
    }

    /// <summary>Clones the repo on first use, else fetches (pruning) and refreshes <c>origin/HEAD</c>; returns the clone's path.</summary>
    private async Task<string> FetchAsync(RepoRef repo, CancellationToken ct)
    {
        var clone = ClonePath(repo);
        var auth = await AuthEnvironment(repo, ct);
        if (!Directory.Exists(Path.Combine(clone, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
            await Git(Path.GetDirectoryName(clone)!, auth, ct, GitRemoteReads.Clone(remoteUrl(repo), clone));
        }
        else
        {
            await Git(clone, auth, ct, GitRemoteReads.Fetch());
            await Git(clone, auth, ct, GitRemoteReads.RefreshHead());
        }
        return clone;
    }

    private async Task<Workspace> AddWorktreeAsync(string clone, string worktree, string branch, string baseBranch, string[] mode, string startPoint,
        CancellationToken ct)
    {
        await DeleteWorktreeAsync(clone, worktree, ct);
        Directory.CreateDirectory(worktree);
        try
        {
            if (sandbox is not null && shareWithWorker)
            {
                // Share the empty directory and let the checkout inherit the ACL; sharing afterwards
                // would follow committed symlinks out of the worktree.
                await sandbox.ShareAsync(worktree, ct);
            }
            await Git(clone, null, ct, ["worktree", "add", .. mode, worktree, startPoint]);
            var gitDir = await AdminDirAsync(clone, worktree, ct)
                ?? throw new InvalidOperationException($"git worktree add left no admin dir for {worktree}.");
            return new Workspace(worktree, branch, baseBranch, gitDir);
        }
        catch
        {
            await DeleteWorktreeAsync(clone, worktree, CancellationToken.None);
            throw;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RunWorktreeName = new("^[a-z0-9][a-z0-9-]*$");

    /// <summary>
    /// A fresh throwaway worktree named <paramref name="name"/> at <paramref name="commit"/> (detached, no branch), shared
    /// with the worker user like any worktree: for a sandboxed run of code the gate must not trust (sc-25382). The commit
    /// must already be in the clone (<see cref="ChangedFilesAsync"/> fetches). Remove it with <see cref="RemoveAsync"/>.
    /// </summary>
    public async Task<Workspace> PrepareCommitAsync(RepoRef repo, string name, string commit, CancellationToken ct)
    {
        if (!RunWorktreeName.IsMatch(name) || name.StartsWith("factory-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not a run worktree name (lowercase letters, digits, '-'; never a story's factory-*).", nameof(name));
        }
        var clone = ClonePath(repo);
        var resolved = (await Git(clone, null, ct, "rev-parse", "--verify", "--end-of-options", $"{commit}^{{commit}}")).Trim();
        return await AddWorktreeAsync(clone, WorktreePath(repo, name), "(detached)", await BaseBranchAsync(clone, ct), ["--detach"], resolved, ct);
    }

    /// <summary>
    /// Writes <paramref name="paths"/> as they are at <paramref name="commit"/> over the worktree, removes
    /// <paramref name="deletes"/>, then writes each of <paramref name="replacements"/> (one of <paramref name="paths"/>, by
    /// path) with the given content instead. Owner-side git only, with the clone-side admin dir and literal pathspecs, so
    /// nothing is written through a link the commit holds; call it before anything runs in the worktree.
    /// </summary>
    public async Task OverlayAsync(Workspace workspace, string commit, IReadOnlyList<string> paths, IReadOnlyList<string> deletes, CancellationToken ct,
        IReadOnlyDictionary<string, string>? replacements = null)
    {
        string[] tree = ["--literal-pathspecs", $"--git-dir={workspace.GitDir}", $"--work-tree={workspace.Path}"];
        if (paths.Count > 0)
        {
            await Git(workspace.Path, null, ct, [.. tree, "checkout", commit, "--", .. paths]);
        }
        if (deletes.Count > 0)
        {
            await Git(workspace.Path, null, ct, [.. tree, "rm", "-q", "-r", "--ignore-unmatch", "--", .. deletes]);
        }
        if (replacements is not { Count: > 0 })
        {
            return;
        }
        if (replacements.Keys.FirstOrDefault(p => !paths.Contains(p, StringComparer.Ordinal)) is { } stray)
        {
            throw new ArgumentException($"'{stray}' is not one of the overlaid paths.", nameof(replacements));
        }
        // The content goes into the object store from an owner-only temporary file, then git writes it into the worktree.
        var staging = Directory.CreateTempSubdirectory("df-overlay-");
        try
        {
            foreach (var (path, content, n) in replacements.OrderBy(r => r.Key, StringComparer.Ordinal).Select((r, n) => (r.Key, r.Value, n)))
            {
                var file = Path.Combine(staging.FullName, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await File.WriteAllTextAsync(file, content, ct);
                var blob = (await Git(workspace.Path, null, ct, [.. tree, "hash-object", "-w", "--no-filters", "--", file])).Trim();
                await Git(workspace.Path, null, ct, [.. tree, "update-index", "--cacheinfo", "100644", blob, path]);
                await Git(workspace.Path, null, ct, [.. tree, "checkout-index", "-f", "--", path]);
            }
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The files a PR changes, <c>git diff --name-status -M base...head</c> on the clone (fetched first): each with git's
    /// status letter and its old and new path.
    /// </summary>
    public async Task<IReadOnlyList<Gate.ChangedFile>> ChangedFilesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
    {
        var clone = await FetchAsync(repo, ct);
        var output = await Git(clone, null, ct, "diff", "--name-status", "-M", "-z", "--no-color", "--end-of-options", $"{baseSha}...{headSha}");
        return ParseNameStatus(output);
    }

    /// <summary>Parses <c>git diff --name-status -z</c>: a status field, then one path (two for a rename or copy), NUL-separated.</summary>
    public static IReadOnlyList<Gate.ChangedFile> ParseNameStatus(string output)
    {
        var fields = output.Split('\0');
        var files = new List<Gate.ChangedFile>();
        for (var i = 0; i + 1 < fields.Length && fields[i].Length > 0;)
        {
            var status = fields[i][0];
            if (status is 'R' or 'C')
            {
                files.Add(new Gate.ChangedFile(status, status == 'C' ? null : fields[i + 1], fields[i + 2]));
                i += 3;
                continue;
            }
            var path = fields[i + 1];
            files.Add(status switch
            {
                'A' => new Gate.ChangedFile(status, null, path),
                'D' => new Gate.ChangedFile(status, path, null),
                _ => new Gate.ChangedFile(status, path, path),
            });
            i += 2;
        }
        return files;
    }

    /// <summary>Every file path in <paramref name="sha"/>'s tree, read from the clone.</summary>
    public async Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct)
    {
        var output = await Git(ClonePath(repo), null, ct, "ls-tree", "-r", "-z", "--name-only", "--full-tree", "--end-of-options", sha);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// <paramref name="path"/>'s content at <paramref name="sha"/> from the clone, or null when that tree has no regular file
    /// there (a symlink, submodule or directory is never read).
    /// </summary>
    public async Task<string?> ReadFileAsync(RepoRef repo, string sha, string path, CancellationToken ct)
    {
        var clone = ClonePath(repo);
        var entry = await Git(clone, null, ct, "--literal-pathspecs", "ls-tree", "-z", "--full-tree", "--end-of-options", sha, "--", path);
        // "<mode> <type> <object>\t<path>"
        var line = entry.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var tab = line?.IndexOf('\t') ?? -1;
        if (line is null || tab < 0 || line[(tab + 1)..] != path || line[..tab].Split(' ') is not [var mode, "blob", var blob] || mode == "120000")
        {
            return null;
        }
        return await Git(clone, null, ct, "cat-file", "blob", blob);
    }

    // ---- a reviewer's read_file, list_files and grep (sc-25706): objects of the clone, never a checkout ----

    private static readonly System.Text.RegularExpressions.Regex FullSha = new("^[0-9a-f]{40}$");

    /// <summary>
    /// How long one git command of a reviewer's read may run (each on its own clock, apart from the review's): a read still running
    /// then is stopped and answers an error.
    /// </summary>
    public static readonly TimeSpan ReviewReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The review read timeout this workspace applies (<see cref="ReviewReadTimeout"/>; tests shorten it).</summary>
    internal TimeSpan ReviewReadLimit { get; init; } = ReviewReadTimeout;

    /// <summary>
    /// At most this many bytes of a <c>list_files</c> listing, or of <c>grep</c>'s kept lines, are read from git before it is
    /// stopped (the answer then says there were more).
    /// </summary>
    public const int MaxReviewReadBytes = Gate.ReviewTools.MaxResultBytes * 4;

    /// <summary>At most this many bytes of one <c>grep</c> output line (path, line number and text) are kept; the rest is skipped.</summary>
    public const int MaxGrepRecordBytes = 8 * 1024;

    /// <summary>A listed path longer than this many bytes ends the listing (the answer then says there were more).</summary>
    private const int MaxPathBytes = 4096;

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>One git command of a reviewer's read, run under its own <see cref="ReviewReadLimit"/> as well as <paramref name="ct"/>.</summary>
    private async Task<T> ReviewReadAsync<T>(string what, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ReviewReadLimit);
        try
        {
            return await read(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"git {what} did not finish within {ReviewReadLimit.TotalSeconds:0.###} s.");
        }
    }

    private Task<string> ReviewGit(string what, string cwd, CancellationToken ct, params string[] args) =>
        ReviewReadAsync(what, limit => Git(cwd, null, limit, args), ct);

    /// <summary>
    /// A reviewer's git read whose output is streamed to <paramref name="read"/> (<see cref="GitStreamCommand"/>), isolated like
    /// <see cref="Git"/>; true when <paramref name="read"/> read it to the end.
    /// </summary>
    private Task<bool> ReviewGitStream(string what, string cwd, string[] args, Func<Stream, CancellationToken, Task<bool>> read, CancellationToken ct) =>
        ReviewReadAsync(what, async limit =>
        {
            var (isolatedEnv, isolatedArgs) = OwnerGit.Isolate(null, args);
            var whole = false;
            await _gitStream(cwd, isolatedEnv, isolatedArgs, async (stream, token) => whole = await read(stream, token), limit);
            return whole;
        }, ct);

    /// <summary>
    /// The clone, with <paramref name="sha"/> in it: cloned or fetched first when it is not (the PR head is on a
    /// <c>factory/*</c> branch, the base on the base branch). <paramref name="sha"/> must be a full lowercase commit id.
    /// </summary>
    private async Task<string> CloneWithAsync(RepoRef repo, string sha, CancellationToken ct)
    {
        if (!FullSha.IsMatch(sha))
        {
            throw new ArgumentException($"'{sha}' is not a full commit id.", nameof(sha));
        }
        var clone = ClonePath(repo);
        if (Directory.Exists(Path.Combine(clone, ".git")))
        {
            try
            {
                await ReviewGit("cat-file", clone, ct, "cat-file", "-e", "--end-of-options", $"{sha}^{{commit}}");
                return clone;
            }
            catch (InvalidOperationException ex) when (ExitCode(ex) is not null)
            {
                // Not fetched yet.
            }
        }
        return await FetchAsync(repo, ct);
    }

    /// <summary>
    /// A regular file's text at <paramref name="sha"/> (<see cref="Gate.IReviewFiles.ReadAsync"/>): read by blob id from the clone,
    /// its size checked before it is read. Read as bytes: a NUL byte or invalid UTF-8 makes it binary (so UTF-16 text is binary).
    /// </summary>
    async Task<string?> Gate.IReviewFiles.ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct)
    {
        var clone = await CloneWithAsync(repo, sha, ct);
        var entry = await ReviewGit("ls-tree", clone, ct, "--literal-pathspecs", "ls-tree", "-l", "-z", "--full-tree", "--end-of-options", sha, "--", path);
        // "<mode> <type> <object> <size>\t<path>" (the size right-aligned with spaces)
        var line = entry.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var tab = line?.IndexOf('\t') ?? -1;
        if (line is null || tab < 0 || line[(tab + 1)..] != path
            || line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var mode, "blob", var blob, var size] || mode == "120000")
        {
            return null;
        }
        if (!long.TryParse(size, out var bytes) || bytes > Gate.ReviewTools.MaxFileBytes)
        {
            throw new InvalidOperationException($"{path} is {size} bytes, more than the {Gate.ReviewTools.MaxFileBytes} a reviewer reads.");
        }
        using var content = new MemoryStream();
        var whole = await ReviewGitStream("cat-file", clone, ["cat-file", "blob", blob], async (stdout, token) =>
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await stdout.ReadAsync(buffer, token)) > 0)
            {
                if (content.Length + n > Gate.ReviewTools.MaxFileBytes)
                {
                    return false;
                }
                content.Write(buffer, 0, n);
            }
            return true;
        }, ct);
        if (!whole)
        {
            throw new InvalidOperationException($"{path} is more than the {Gate.ReviewTools.MaxFileBytes} bytes a reviewer reads.");
        }
        var raw = content.GetBuffer().AsSpan(0, (int)content.Length);
        if (raw.Contains((byte)0))
        {
            throw new Gate.BinaryFileException(path);
        }
        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new Gate.BinaryFileException(path);
        }
    }

    /// <summary>
    /// <c>git ls-tree -r</c> on the commit (<see cref="Gate.IReviewFiles.ListAsync"/>), streamed: git is stopped once
    /// <see cref="Gate.ReviewTools.MaxListedFiles"/> paths are read and another follows, or <see cref="MaxReviewReadBytes"/> are read.
    /// </summary>
    async Task<Gate.Bounded<string>> Gate.IReviewFiles.ListAsync(RepoRef repo, string sha, string? directory, CancellationToken ct)
    {
        var clone = await CloneWithAsync(repo, sha, ct);
        string[] args = directory is null
            ? ["ls-tree", "-r", "-z", "--name-only", "--full-tree", "--end-of-options", sha]
            : ["--literal-pathspecs", "ls-tree", "-r", "-z", "--name-only", "--full-tree", "--end-of-options", sha, "--", directory];
        var paths = new List<string>();
        var kept = 0;
        var whole = await ReviewGitStream("ls-tree", clone, args, (stdout, token) => ReadRecordsAsync(stdout, 0, MaxPathBytes, (record, cut) =>
        {
            kept += record.Length;
            if (cut || paths.Count == Gate.ReviewTools.MaxListedFiles || kept > MaxReviewReadBytes)
            {
                return false;
            }
            paths.Add(System.Text.Encoding.UTF8.GetString(record));
            return true;
        }, token), ct);
        return new Gate.Bounded<string>(paths, More: !whole);
    }

    /// <summary>
    /// <c>git grep</c> on the commit's tree (<see cref="Gate.IReviewFiles.GrepAsync"/>): POSIX extended regular expression, text files
    /// only (<c>-I</c>), no textconv, at most <paramref name="perFile"/> lines a file. git answers 1 for no match: an empty list.
    /// Streamed: each output line keeps at most <see cref="MaxGrepRecordBytes"/> bytes, and git is stopped once
    /// <see cref="Gate.ReviewTools.MaxGrepMatches"/> matches are read and another follows, or <see cref="MaxReviewReadBytes"/> are kept.
    /// </summary>
    async Task<Gate.Bounded<Gate.GrepMatch>> Gate.IReviewFiles.GrepAsync(RepoRef repo, string sha, string pattern, string? directory, int perFile,
        CancellationToken ct)
    {
        var clone = await CloneWithAsync(repo, sha, ct);
        var matches = new List<Gate.GrepMatch>();
        var kept = 0;
        bool whole;
        try
        {
            whole = await ReviewGitStream("grep", clone, ["--literal-pathspecs", "grep", "-I", "-n", "-z", "--no-color", "--no-textconv", "-E",
                $"--max-count={perFile}", "-e", pattern, "--end-of-options", sha, "--", .. directory is null ? Array.Empty<string>() : [directory]],
                (stdout, token) => ReadRecordsAsync(stdout, (byte)'\n', MaxGrepRecordBytes, (record, _) =>
                {
                    if (ParseGrepRecord(System.Text.Encoding.UTF8.GetString(record), sha) is not { } match)
                    {
                        return true;
                    }
                    kept += record.Length;
                    if (matches.Count == Gate.ReviewTools.MaxGrepMatches || kept > MaxReviewReadBytes)
                    {
                        return false;
                    }
                    matches.Add(match);
                    return true;
                }, token), ct);
        }
        catch (InvalidOperationException ex) when (ExitCode(ex) == 1)
        {
            return new Gate.Bounded<Gate.GrepMatch>([], More: false);
        }
        return new Gate.Bounded<Gate.GrepMatch>(matches, More: !whole);
    }

    /// <summary>
    /// Splits <paramref name="stream"/> at <paramref name="separator"/>, handing each record to <paramref name="record"/> (at most
    /// <paramref name="maxRecord"/> bytes of it kept, the rest skipped, which its second argument says), which answers false to
    /// stop. True when the stream was read to its end.
    /// </summary>
    private static async Task<bool> ReadRecordsAsync(Stream stream, byte separator, int maxRecord, Func<byte[], bool, bool> record,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var current = new MemoryStream();
        var cut = false;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            while (start < n)
            {
                var end = Array.IndexOf(buffer, separator, start, n - start);
                var take = (end < 0 ? n : end) - start;
                var room = maxRecord - (int)current.Length;
                current.Write(buffer, start, Math.Min(take, room));
                cut |= take > room;
                if (end < 0)
                {
                    break;
                }
                if (!record(current.ToArray(), cut))
                {
                    return false;
                }
                current.SetLength(0);
                cut = false;
                start = end + 1;
            }
        }
        return current.Length == 0 || record(current.ToArray(), cut);
    }

    /// <summary>
    /// Parses <c>git grep -n -z</c> on a commit: one line per match, <c>&lt;sha&gt;:&lt;path&gt;\0&lt;line&gt;\0&lt;text&gt;</c> (the NULs
    /// stand where git would print colons, so a path or text holding one parses).
    /// </summary>
    public static IReadOnlyList<Gate.GrepMatch> ParseGrep(string output, string sha) =>
        output.Split('\n').Select(record => ParseGrepRecord(record, sha)).OfType<Gate.GrepMatch>().ToList();

    /// <summary>One line of <see cref="ParseGrep"/>'s output, or null when it is not a match line.</summary>
    private static Gate.GrepMatch? ParseGrepRecord(string record, string sha)
    {
        var fields = record.Split('\0', 3);
        return fields.Length < 3 || !fields[0].StartsWith(sha + ":", StringComparison.Ordinal) || !int.TryParse(fields[1], out var line)
            ? null
            : new Gate.GrepMatch(fields[0][(sha.Length + 1)..], line, fields[2].TrimEnd('\r'));
    }

    /// <summary>Reads the branch through the clone-side admin dir, never the worktree's worker-writable <c>.git</c> file.</summary>
    public async Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct)
    {
        EnsureFactoryBranch(branch);
        var clone = ClonePath(repo);
        var worktree = WorktreePath(repo, branch);
        if (!Directory.Exists(worktree) || await AdminDirAsync(clone, worktree, ct) is not { } gitDir)
        {
            return null;
        }
        var head = (await Git(clone, null, ct, $"--git-dir={gitDir}", "symbolic-ref", "--quiet", "--short", "HEAD")).Trim();
        return head == branch ? new Workspace(worktree, branch, await BaseBranchAsync(clone, ct), gitDir) : null;
    }

    /// <summary>
    /// The worktree's admin dir in the clone (<c>.git/worktrees/&lt;name&gt;</c>), found from the clone's
    /// side — its <c>gitdir</c> file names the worktree — or null when the clone has none for it.
    /// </summary>
    private async Task<string?> AdminDirAsync(string clone, string worktree, CancellationToken ct)
    {
        var admin = Path.Combine(clone, ".git", "worktrees", Path.GetFileName(worktree));
        var gitdirFile = Path.Combine(admin, "gitdir");
        if (!File.Exists(gitdirFile)
            || Canonical(File.ReadAllText(gitdirFile).Trim()) != Canonical(Path.Combine(worktree, ".git")))
        {
            return null;
        }
        return (await Git(clone, null, ct, $"--git-dir={admin}", "rev-parse", "--absolute-git-dir")).Trim();
    }

    /// <summary>Full path with macOS's /var → /private/var link folded, so git's and .NET's spellings compare equal.</summary>
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith("/private/", StringComparison.Ordinal) ? full["/private".Length..] : full;
    }

    private async Task<string> BaseBranchAsync(string clone, CancellationToken ct) =>
        (await Git(clone, null, ct, "symbolic-ref", "--short", "refs/remotes/origin/HEAD")).Trim()["origin/".Length..];

    private string ClonePath(RepoRef repo) => Path.Combine(workRoot, "repos", repo.Owner, repo.Name);

    private string WorktreesRoot => Path.Combine(workRoot, worktreesDirectory);

    private string WorktreePath(RepoRef repo, string branch) =>
        Path.Combine(WorktreesRoot, repo.Owner, repo.Name, branch.Replace('/', '-'));

    /// <summary>
    /// Deletes every worktree under the work root that <paramref name="keep"/> (given the worktree's
    /// directory name, e.g. <c>factory-sc-7</c>) does not claim for a resumable run: ones left behind
    /// when cleanup was killed or a worker could not be stopped.
    /// </summary>
    public async Task SweepOrphansAsync(Func<string, CancellationToken, Task<bool>> keep, CancellationToken ct)
    {
        if (!Directory.Exists(WorktreesRoot))
        {
            return;
        }
        foreach (var repoDir in Directory.GetDirectories(WorktreesRoot).SelectMany(Directory.GetDirectories))
        {
            var clone = Path.Combine(workRoot, "repos", Path.GetFileName(Path.GetDirectoryName(repoDir))!, Path.GetFileName(repoDir));
            foreach (var worktree in Directory.GetDirectories(repoDir))
            {
                if (!await keep(Path.GetFileName(worktree), ct))
                {
                    await DeleteWorktreeAsync(clone, worktree, ct);
                }
            }
        }
    }

    public async Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct)
    {
        // No grant, no push token (E4): only the ledger's taint check issues one.
        ArgumentNullException.ThrowIfNull(grant);
        EnsureFactoryBranch(workspace.Branch);
        var dir = workspace.Path;
        string[] tree = [$"--git-dir={workspace.GitDir}", $"--work-tree={dir}"];
        await Git(dir, null, ct, [.. tree, "add", "-A"]);
        if ((await Git(dir, null, ct, [.. tree, "status", "--porcelain"])).Trim().Length > 0)
        {
            await Git(dir, null, ct, [.. tree, "-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}",
                "commit", "-m", message]);
        }
        var ahead = int.Parse((await Git(dir, null, ct, [.. tree, "rev-list", "--count", $"origin/{workspace.BaseBranch}..HEAD"])).Trim());
        if (ahead == 0)
        {
            return false;
        }
        await Git(dir, await AuthEnvironment(repo, ct), ct, GitRemoteWrites.Push(tree, workspace.Branch, force: true));
        return true;
    }

    public async Task<Gate.BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
    {
        EnsureFactoryBranch(workspace.Branch);
        var dir = workspace.Path;
        string[] tree = [$"--git-dir={workspace.GitDir}", $"--work-tree={dir}"];
        var upstream = $"origin/{workspace.BaseBranch}";
        var baseSha = (await Git(dir, null, ct, [.. tree, "rev-parse", "--verify", "--end-of-options", $"{upstream}^{{commit}}"])).Trim();
        var before = await HeadAsync(workspace, ct);
        if (int.Parse((await Git(dir, null, ct, [.. tree, "rev-list", "--count", $"HEAD..{baseSha}"])).Trim()) == 0)
        {
            return new Gate.BaseMerge(baseSha, before, [], UpToDate: true);
        }
        try
        {
            await Git(dir, null, ct, [.. tree, "-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}",
                "merge", "--no-ff", "--no-edit", "-m", $"Merge {workspace.BaseBranch} into {workspace.Branch}", baseSha]);
        }
        catch (InvalidOperationException)
        {
            var conflicts = (await Git(dir, null, ct, [.. tree, "diff", "--name-only", "-z", "--diff-filter=U"]))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (conflicts.Length == 0)
            {
                throw;
            }
            return new Gate.BaseMerge(baseSha, before, conflicts);
        }
        return new Gate.BaseMerge(baseSha, await HeadAsync(workspace, ct), []);
    }

    public async Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
    {
        EnsureFactoryBranch(workspace.Branch);
        await Git(workspace.Path, await AuthEnvironment(repo, ct), ct,
            GitRemoteWrites.Push([$"--git-dir={workspace.GitDir}", $"--work-tree={workspace.Path}"], workspace.Branch, force: false));
    }

    public async Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var marked = new List<string>();
        foreach (var path in paths)
        {
            // Read from the commit in the clone (never through the worker-writable worktree): a regular file only.
            if (await ReadFileAsync(repo, sha, path, ct) is { } text && HasConflictMarker(text))
            {
                marked.Add(path);
            }
        }
        return marked;
    }

    /// <summary>Whether <paramref name="text"/> has a line git writes around a conflict (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; </c> or <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; </c>).</summary>
    public static bool HasConflictMarker(string text) =>
        text.Split('\n').Any(line => line.TrimEnd('\r') is var l
            && (l == "<<<<<<<" || l == ">>>>>>>" || l.StartsWith("<<<<<<< ", StringComparison.Ordinal) || l.StartsWith(">>>>>>> ", StringComparison.Ordinal)));

    public async Task<string> HeadAsync(Workspace workspace, CancellationToken ct) =>
        (await Git(workspace.Path, null, ct, $"--git-dir={workspace.GitDir}", $"--work-tree={workspace.Path}", "rev-parse", "HEAD")).Trim();

    public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct) =>
        DeleteWorktreeAsync(ClonePath(repo), workspace.Path, ct);

    /// <summary>
    /// Deletes the worktree directory without asking git (which would read the worker-writable
    /// <c>.git</c> file), then prunes the clone's worktree list. Sandboxed, the worker user deletes
    /// first (its processes are dead by then: the launch helper kills them all when a run ends), so the
    /// owner only removes what is left rather than walking a tree the worker controlled.
    /// </summary>
    private async Task DeleteWorktreeAsync(string clone, string worktree, CancellationToken ct)
    {
        if (Directory.Exists(worktree))
        {
            if (sandbox is not null)
            {
                await sandbox.DeleteAsWorkerAsync(worktree, ct);
            }
            if (Directory.Exists(worktree))
            {
                Directory.Delete(worktree, recursive: true);
            }
        }
        if (Directory.Exists(Path.Combine(clone, ".git")))
        {
            await Git(clone, null, ct, "worktree", "prune");
        }
    }

    private static void EnsureFactoryBranch(string branch)
    {
        if (!branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to use branch '{branch}': workers only push to {BranchPrefix}*.");
        }
    }

    /// <summary>The installation token's git credentials (<see cref="GitRemoteWrites.Credentials"/>), or none without a token.</summary>
    private async Task<Dictionary<string, string>?> AuthEnvironment(RepoRef repo, CancellationToken ct) =>
        await token(repo, ct) is { } value ? GitRemoteWrites.Credentials(value) : null;

    /// <summary>Every git call of this class, isolated from the owner's own git config (<see cref="OwnerGit.Isolate"/>).</summary>
    private Task<string> Git(string cwd, Dictionary<string, string>? env, CancellationToken ct, params string[] args)
    {
        var (isolatedEnv, isolatedArgs) = OwnerGit.Isolate(env, args);
        return _git(cwd, isolatedEnv, isolatedArgs, ct);
    }

    public static Task<string> RunGitAsync(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args, CancellationToken ct) =>
        RunGitAsync(cwd, env, args, null, ct);

    /// <summary>
    /// <see cref="RunGitAsync(string, IReadOnlyDictionary{string, string}?, IReadOnlyList{string}, CancellationToken)"/>, handing the
    /// started git to <paramref name="started"/> (tests). Cancelled, git is stopped with its whole process tree before this throws.
    /// </summary>
    internal static async Task<string> RunGitAsync(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args,
        Action<Process>? started, CancellationToken ct)
    {
        using var p = Process.Start(GitStartInfo(cwd, env, args))!;
        started?.Invoke(p);
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(p);
            throw;
        }
        ThrowIfFailed(p, args, await stderr);
        return await stdout;
    }

    /// <summary>
    /// The default <see cref="GitStreamCommand"/>. Cancelled, or <paramref name="read"/> failing, git is stopped with its whole
    /// process tree before this throws.
    /// </summary>
    public static async Task RunGitStreamAsync(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args,
        Func<Stream, CancellationToken, Task<bool>> read, CancellationToken ct)
    {
        using var p = Process.Start(GitStartInfo(cwd, env, args))!;
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (!await read(p.StandardOutput.BaseStream, ct))
            {
                await StopAsync(p);
                return;
            }
            await p.WaitForExitAsync(ct);
        }
        catch
        {
            await StopAsync(p);
            throw;
        }
        ThrowIfFailed(p, args, await stderr);
    }

    /// <summary>Stops a git this class started (with anything it started), then waits for it to exit.</summary>
    private static async Task StopAsync(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        await p.WaitForExitAsync(CancellationToken.None);
    }

    private static void ThrowIfFailed(Process p, IReadOnlyList<string> args, string stderr)
    {
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed ({p.ExitCode}): {stderr.Trim()}")
            {
                Data = { [ExitCodeKey] = p.ExitCode },
            };
        }
    }

    private static ProcessStartInfo GitStartInfo(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v;
        }
        return psi;
    }

    /// <summary>The <see cref="Exception.Data"/> key of a failed git command's exit status.</summary>
    public const string ExitCodeKey = "git-exit-code";

    /// <summary>A failed git command's exit status (<c>git grep</c> answers 1 for "no line matched"), or null for any other exception.</summary>
    public static int? ExitCode(Exception ex) => ex.Data[ExitCodeKey] as int?;
}
