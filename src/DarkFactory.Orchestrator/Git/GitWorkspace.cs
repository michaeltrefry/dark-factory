using System.Diagnostics;
using System.Text;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Git;

public sealed record Workspace(string Path, string Branch, string BaseBranch);

public interface IRepoWorkspace
{
    Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct);

    /// <summary>Commits any worker changes and pushes the branch. Returns false when there is nothing to push.</summary>
    Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct);

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
/// </summary>
public sealed class GitWorkspace(
    string workRoot,
    Func<RepoRef, string> remoteUrl,
    Func<RepoRef, CancellationToken, Task<string?>> token,
    GitCommand? git = null)
    : IRepoWorkspace
{
    private readonly GitCommand _git = git ?? RunGitAsync;

    public const string BranchPrefix = "factory/";
    private const string CommitterName = "dark-factory[bot]";
    private const string CommitterEmail = "dark-factory@users.noreply.github.com";

    public static string GitHubRemote(RepoRef repo) => $"https://github.com/{repo.Owner}/{repo.Name}.git";

    public async Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct)
    {
        EnsureFactoryBranch(branch);
        var clone = Path.Combine(workRoot, "repos", repo.Owner, repo.Name);
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

        var baseBranch = (await Git(clone, null, ct, "symbolic-ref", "--short", "refs/remotes/origin/HEAD"))
            .Trim()["origin/".Length..];

        var worktree = Path.Combine(workRoot, "worktrees", repo.Owner, repo.Name, branch.Replace('/', '-'));
        if (Directory.Exists(worktree))
        {
            await Git(clone, null, ct, "worktree", "remove", "--force", worktree);
        }
        await Git(clone, null, ct, "worktree", "prune");
        await Git(clone, null, ct, "worktree", "add", "-B", branch, worktree, $"origin/{baseBranch}");
        return new Workspace(worktree, branch, baseBranch);
    }

    public async Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct)
    {
        EnsureFactoryBranch(workspace.Branch);
        var dir = workspace.Path;
        await Git(dir, null, ct, "add", "-A");
        if ((await Git(dir, null, ct, "status", "--porcelain")).Trim().Length > 0)
        {
            await Git(dir, null, ct, "-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}",
                "commit", "-m", message);
        }
        var ahead = int.Parse((await Git(dir, null, ct, "rev-list", "--count", $"origin/{workspace.BaseBranch}..HEAD")).Trim());
        if (ahead == 0)
        {
            return false;
        }
        await Git(dir, await AuthEnvironment(repo, ct), ct,
            "push", "--force", "origin", $"HEAD:refs/heads/{workspace.Branch}");
        return true;
    }

    public async Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
    {
        var clone = Path.Combine(workRoot, "repos", repo.Owner, repo.Name);
        if (Directory.Exists(workspace.Path))
        {
            await Git(clone, null, ct, "worktree", "remove", "--force", workspace.Path);
        }
        await Git(clone, null, ct, "worktree", "prune");
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
