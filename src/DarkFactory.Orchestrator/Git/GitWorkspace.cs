using System.Diagnostics;
using System.Text;
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

    /// <summary>Commits any worker changes and pushes the branch. Returns false when there is nothing to push.</summary>
    Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct);

    /// <summary>The commit the worktree's HEAD is at (after <see cref="CommitAndPushAsync"/>: the commit it pushed).</summary>
    Task<string> HeadAsync(Workspace workspace, CancellationToken ct);

    /// <summary>Removes a story's worktree; the clone and any pushed branch stay.</summary>
    Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct);
}

/// <summary>Runs one git command in <paramref name="cwd"/> with extra environment (null for none).</summary>
public delegate Task<string> GitCommand(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args, CancellationToken ct);

/// <summary>
/// Keeps one clone per target repo under the work root and gives each story its
/// own git worktree on <c>factory/sc-&lt;id&gt;</c>. Network git calls authenticate
/// with an installation token passed through git's environment-based config, so
/// the token never appears in argv, remotes or files.
/// With a sandbox, each fresh worktree is shared with the worker user; owner-side git
/// on it always names the git dir explicitly, so a worker-edited <c>.git</c> file can't
/// point the owner's git at a repository (config, hooks) the worker controls.
/// </summary>
public sealed class GitWorkspace(
    string workRoot,
    Func<RepoRef, string> remoteUrl,
    Func<RepoRef, CancellationToken, Task<string?>> token,
    GitCommand? git = null,
    IWorkerSandbox? sandbox = null)
    : IRepoWorkspace
{
    private readonly GitCommand _git = git ?? RunGitAsync;

    public const string BranchPrefix = "factory/";
    private const string CommitterName = "dark-factory[bot]";
    private const string CommitterEmail = "dark-factory@users.noreply.github.com";

    public static string GitHubRemote(RepoRef repo) => $"https://github.com/{repo.Owner}/{repo.Name}.git";

    public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) =>
        CreateWorktreeAsync(repo, branch, baseBranch => $"origin/{baseBranch}", ct);

    public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) =>
        CreateWorktreeAsync(repo, branch, _ => $"origin/{branch}", ct);

    private async Task<Workspace> CreateWorktreeAsync(RepoRef repo, string branch, Func<string, string> startPoint, CancellationToken ct)
    {
        EnsureFactoryBranch(branch);
        var clone = ClonePath(repo);
        var auth = await AuthEnvironment(repo, ct);
        if (!Directory.Exists(Path.Combine(clone, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
            await Git(Path.GetDirectoryName(clone)!, auth, ct, "clone", remoteUrl(repo), clone);
        }
        else
        {
            await Git(clone, auth, ct, "fetch", "--prune", "origin");
            await Git(clone, auth, ct, "remote", "set-head", "origin", "--auto");
        }

        var baseBranch = await BaseBranchAsync(clone, ct);
        var worktree = WorktreePath(repo, branch);
        await DeleteWorktreeAsync(clone, worktree, ct);
        Directory.CreateDirectory(worktree);
        try
        {
            if (sandbox is not null)
            {
                // Share the empty directory and let the checkout inherit the ACL; sharing afterwards
                // would follow committed symlinks out of the worktree.
                await sandbox.ShareAsync(worktree, ct);
            }
            await Git(clone, null, ct, "worktree", "add", "-B", branch, worktree, startPoint(baseBranch));
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

    private string WorktreesRoot => Path.Combine(workRoot, "worktrees");

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

    public async Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct)
    {
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
        await Git(dir, await AuthEnvironment(repo, ct), ct,
            [.. tree, "push", "--force", "origin", $"HEAD:refs/heads/{workspace.Branch}"]);
        return true;
    }

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

    private async Task<Dictionary<string, string>?> AuthEnvironment(RepoRef repo, CancellationToken ct)
    {
        var value = await token(repo, ct);
        if (value is null)
        {
            return null;
        }
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{value}"));
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}",
            // Don't let the owner's credential helper substitute their own identity.
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = "",
        };
    }

    private Task<string> Git(string cwd, Dictionary<string, string>? env, CancellationToken ct, params string[] args) =>
        _git(cwd, env, args, ct);

    public static async Task<string> RunGitAsync(string cwd, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string> args, CancellationToken ct)
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
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args.FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('='))} failed ({p.ExitCode}): {(await stderr).Trim()}");
        }
        return await stdout;
    }
}
