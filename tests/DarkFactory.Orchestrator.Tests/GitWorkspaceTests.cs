using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Real git against a local bare "remote"; no network.</summary>
public class GitWorkspaceTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _root = Directory.CreateTempSubdirectory("df-git-").FullName;
    private readonly string _remote;

    public GitWorkspaceTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", _remote);
        Git(_root, "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", _remote, "main");
    }

    private GitWorkspace Workspace() => new(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>("tok"));

    [Fact]
    public async Task Prepare_creates_worktree_on_factory_branch_from_default_branch()
    {
        var ws = await Workspace().PrepareAsync(Repo, "factory/sc-1", CancellationToken.None);

        Assert.Equal("main", ws.BaseBranch);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
        Assert.Equal("factory/sc-1", Git(ws.Path, "branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Commit_and_push_publishes_worker_changes_to_the_factory_branch()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-2", CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-2: Fix", CancellationToken.None));

        Assert.Equal("sc-2: Fix", Git(_remote, "log", "-1", "--format=%s", "factory/sc-2").Trim());
        Assert.Equal("dark-factory[bot]", Git(_remote, "log", "-1", "--format=%an", "factory/sc-2").Trim());
    }

    [Fact]
    public async Task Rerun_prepares_a_fresh_worktree_and_push_without_changes_returns_false()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-3", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "scratch.txt"), "x");

        var second = await workspace.PrepareAsync(Repo, "factory/sc-3", CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(second.Path, "scratch.txt")));
        Assert.False(await workspace.CommitAndPushAsync(Repo, second, "nothing", CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_branches_outside_factory_prefix()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Workspace().PrepareAsync(Repo, "main", CancellationToken.None));
        Assert.Contains("factory/", ex.Message);
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
