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

    private const string Token = "ghs_FakeInstallationToken123";
    private static readonly string BasicAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"x-access-token:{Token}"));

    private readonly List<(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env)> _gitCalls = [];
    private int _tokenRequests;

    private GitWorkspace Workspace() => new(Path.Combine(_root, "work"), _ => _remote,
        (_, _) =>
        {
            _tokenRequests++;
            return Task.FromResult<string?>(Token);
        },
        (cwd, env, args, ct) =>
        {
            _gitCalls.Add((args, env));
            return GitWorkspace.RunGitAsync(cwd, env, args, ct);
        });

    /// <summary>Git subcommands that talk to the remote and so must carry the App token.</summary>
    private static bool IsNetworkCall(IReadOnlyList<string> args) =>
        Subcommand(args) is "clone" or "fetch" or "push" or "pull" or "ls-remote"
        || (Subcommand(args) == "remote" && args.Contains("--auto"));

    /// <summary>The git subcommand, skipping global options such as <c>-c key=value</c>.</summary>
    private static string Subcommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-c")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }
        return "";
    }

    [Fact]
    public async Task Every_network_git_call_carries_the_app_token_and_no_call_puts_it_in_argv_or_config()
    {
        var workspace = Workspace();
        await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None); // clone
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None); // fetch + set-head
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-9: Fix", CancellationToken.None)); // push

        var network = _gitCalls.Where(c => IsNetworkCall(c.Args)).ToList();
        Assert.Equal(["clone", "fetch", "remote", "push"], network.Select(c => Subcommand(c.Args)));
        Assert.All(network, c =>
        {
            Assert.NotNull(c.Env);
            Assert.Equal("http.https://github.com/.extraheader", c.Env["GIT_CONFIG_KEY_0"]);
            Assert.Equal($"AUTHORIZATION: basic {BasicAuth}", c.Env["GIT_CONFIG_VALUE_0"]);
            Assert.Equal(("credential.helper", ""), (c.Env["GIT_CONFIG_KEY_1"], c.Env["GIT_CONFIG_VALUE_1"]));
        });
        Assert.Equal(3, _tokenRequests); // one per PrepareAsync, one for the push

        Assert.All(_gitCalls, c => Assert.DoesNotContain(c.Args, a => a.Contains(Token) || a.Contains(BasicAuth)));
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        foreach (var dir in new[] { clone, ws.Path })
        {
            var config = Git(dir, "config", "--list", "--show-origin");
            Assert.DoesNotContain(Token, config);
            Assert.DoesNotContain(BasicAuth, config);
            Assert.DoesNotContain("extraheader", config, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(_remote, Git(dir, "remote", "get-url", "origin").Trim());
        }
    }

    [Fact]
    public async Task Remove_deletes_the_worktree()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-4", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.False(Directory.Exists(ws.Path));
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        Assert.Single(Git(clone, "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries)); // main clone only
    }

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
    public async Task Reopen_returns_the_interrupted_worktree_untouched()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-5", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "half-done.txt"), "wip");
        _gitCalls.Clear();

        var reopened = await workspace.ReopenAsync(Repo, "factory/sc-5", CancellationToken.None);

        Assert.Equal(first, reopened);
        Assert.True(File.Exists(Path.Combine(reopened!.Path, "half-done.txt")));
        Assert.DoesNotContain(_gitCalls, c => IsNetworkCall(c.Args) || Subcommand(c.Args) is "worktree" or "checkout" or "reset");
        Assert.True(await workspace.CommitAndPushAsync(Repo, reopened, "sc-5: Fix", CancellationToken.None));
    }

    [Fact]
    public async Task Reopen_returns_null_when_the_worktree_is_gone()
    {
        var workspace = Workspace();
        Assert.Null(await workspace.ReopenAsync(Repo, "factory/sc-6", CancellationToken.None)); // no clone yet
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-6", CancellationToken.None);
        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.Null(await workspace.ReopenAsync(Repo, "factory/sc-6", CancellationToken.None));
    }

    [Fact]
    public async Task Restore_recreates_a_lost_worktree_from_the_pushed_branch()
    {
        var workspace = Workspace();
        var first = await workspace.PrepareAsync(Repo, "factory/sc-7", CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "pushed.txt"), "done");
        Assert.True(await workspace.CommitAndPushAsync(Repo, first, "sc-7: Fix", CancellationToken.None));
        var pushed = Git(_remote, "rev-parse", "factory/sc-7").Trim();
        await workspace.RemoveAsync(Repo, first, CancellationToken.None);

        var restored = await workspace.RestoreAsync(Repo, "factory/sc-7", CancellationToken.None);

        Assert.Equal(first, restored);
        Assert.Equal(pushed, Git(restored.Path, "rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(restored.Path, "pushed.txt")));
        // Pushing again changes nothing on the remote.
        await workspace.CommitAndPushAsync(Repo, restored, "sc-7: Fix", CancellationToken.None);
        Assert.Equal(pushed, Git(_remote, "rev-parse", "factory/sc-7").Trim());
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
