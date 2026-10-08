using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Real git against a local bare "remote"; no network.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
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
        // The pushed commit is the one a fix round waits for on the PR (sc-25380).
        Assert.Equal(Git(_remote, "rev-parse", "factory/sc-2").Trim(), await workspace.HeadAsync(ws, CancellationToken.None));
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

    private sealed class FakeSandbox(bool deleteRemovesNothing = false) : DarkFactory.Orchestrator.Worker.IWorkerSandbox
    {
        public List<string> Calls { get; } = [];
        public Task ShareAsync(string path, CancellationToken ct)
        {
            Calls.Add($"share {path} {(Directory.EnumerateFileSystemEntries(path).Any() ? "non-empty" : "empty")}");
            return Task.CompletedTask;
        }
        public Task DeleteAsWorkerAsync(string path, CancellationToken ct)
        {
            Calls.Add($"delete {path}");
            if (deleteRemovesNothing)
            {
                return Task.CompletedTask;
            }
            foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(path, recursive: true);
            return Task.CompletedTask;
        }
    }

    private GitWorkspace Workspace(DarkFactory.Orchestrator.Worker.IWorkerSandbox sandbox, GitCommand? git = null) =>
        new(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token), git, sandbox);

    private string ClonePath => Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);

    private int WorktreeCount() => Git(ClonePath, "worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public async Task Prepare_shares_the_fresh_worktree_while_empty_and_records_its_git_dir()
    {
        var sandbox = new FakeSandbox();
        var ws = await Workspace(sandbox).PrepareAsync(Repo, "factory/sc-5", CancellationToken.None);

        // Shared before the checkout, so the checkout inherits the ACL instead of a recursive chmod.
        Assert.Equal([$"share {ws.Path} empty"], sandbox.Calls);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
        Assert.Equal(Path.Combine(ClonePath, ".git", "worktrees", "factory-sc-5"), ws.GitDir.Replace("/private/var/", "/var/"));
    }

    [Fact]
    public async Task Owner_git_ignores_a_worker_rewritten_dot_git_file()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-6", CancellationToken.None);
        // The worker points the worktree's .git file at a repo whose hooks it controls.
        var evil = Path.Combine(_root, "evil");
        Git(_root, "init", evil);
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(evil, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(ws.Path, ".git"), $"gitdir: {Path.Combine(evil, ".git")}\n");
        File.WriteAllText(Path.Combine(ws.Path, "fix.txt"), "fixed\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-6: Fix", CancellationToken.None));

        Assert.False(File.Exists(marker));
        Assert.Equal("sc-6: Fix", Git(_remote, "log", "-1", "--format=%s", "factory/sc-6").Trim());
    }

    [Fact]
    public async Task Reopen_finds_the_git_dir_from_the_clone_not_a_worker_rewritten_dot_git_file()
    {
        var workspace = Workspace();
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-8", CancellationToken.None);
        // An interrupted worker points .git at its own repo, on the same branch name.
        var evil = Path.Combine(_root, "evil");
        Git(_root, "init", "-b", "factory/sc-8", evil);
        File.WriteAllText(Path.Combine(ws.Path, ".git"), $"gitdir: {Path.Combine(evil, ".git")}\n");

        var reopened = await workspace.ReopenAsync(Repo, "factory/sc-8", CancellationToken.None);

        Assert.Equal(ws.GitDir, reopened?.GitDir);
    }

    [Fact]
    public async Task Remove_deletes_as_the_worker_first_then_the_owner_removes_what_is_left()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-7", CancellationToken.None);
        var locked = Directory.CreateDirectory(Path.Combine(ws.Path, "obj", "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "f"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.Equal([$"share {ws.Path} empty", $"delete {ws.Path}"], sandbox.Calls);
        Assert.False(Directory.Exists(ws.Path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Remove_deletes_as_the_worker_even_when_the_owner_could_delete_everything()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        // The owner never walks the worker-controlled tree before the worker has emptied it.
        Assert.Equal([$"share {ws.Path} empty", $"delete {ws.Path}"], sandbox.Calls);
    }

    [Fact]
    public async Task Remove_has_the_owner_delete_whatever_the_worker_left()
    {
        var sandbox = new FakeSandbox(deleteRemovesNothing: true);
        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-10", CancellationToken.None);

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);

        Assert.False(Directory.Exists(ws.Path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Prepare_removes_the_worktree_when_a_step_after_worktree_add_fails()
    {
        var sandbox = new FakeSandbox();
        var workspace = Workspace(sandbox, (cwd, env, args, ct) => args.Contains("--absolute-git-dir")
            ? throw new InvalidOperationException("rev-parse exploded")
            : GitWorkspace.RunGitAsync(cwd, env, args, ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PrepareAsync(Repo, "factory/sc-11", CancellationToken.None));

        var path = Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name, "factory-sc-11");
        Assert.False(Directory.Exists(path));
        Assert.Equal(1, WorktreeCount());
    }

    [Fact]
    public async Task Sweep_deletes_worktrees_no_run_will_resume_and_keeps_the_rest()
    {
        var workspace = Workspace();
        var orphan = await workspace.PrepareAsync(Repo, "factory/sc-12", CancellationToken.None);
        var resumable = await workspace.PrepareAsync(Repo, "factory/sc-13", CancellationToken.None);
        var asked = new List<string>();

        await Workspace().SweepOrphansAsync((name, _) =>
        {
            asked.Add(name);
            return Task.FromResult(name == "factory-sc-13");
        }, CancellationToken.None);

        Assert.Equal(["factory-sc-12", "factory-sc-13"], asked.Order());
        Assert.False(Directory.Exists(orphan.Path));
        Assert.True(File.Exists(Path.Combine(resumable.Path, "README.md")));
        Assert.Equal(2, WorktreeCount()); // the clone and the kept worktree
    }

    [Fact]
    public async Task Sweep_without_any_worktrees_does_nothing()
    {
        await Workspace().SweepOrphansAsync((_, _) => throw new InvalidOperationException("no worktree to ask about"), CancellationToken.None);
    }

    [Fact]
    public async Task Sharing_a_checkout_never_grants_acl_entries_on_targets_of_committed_symlinks()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // A target repo commits symlinks pointing out of the worktree (e.g. at the clone's hooks).
        var outsideDir = Directory.CreateDirectory(Path.Combine(_root, "outside-dir")).FullName;
        var outsideFile = Path.Combine(_root, "outside-file");
        File.WriteAllText(outsideFile, "secret\n");
        var seed = Path.Combine(_root, "seed2");
        Git(_root, "clone", _remote, seed);
        File.CreateSymbolicLink(Path.Combine(seed, "dir-link"), outsideDir);
        File.CreateSymbolicLink(Path.Combine(seed, "file-link"), outsideFile);
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "symlinks");
        Git(seed, "push", "origin", "main");
        // A real sandbox sharing with the current user (chmod needs an existing user); deletes run the helper unsudoed.
        var sandbox = new DarkFactory.Orchestrator.Worker.WorkerSandbox(Environment.UserName, SandboxSupport.Helper, SandboxSupport.FakeSudo(_root));

        var workspace = Workspace(sandbox);
        var ws = await workspace.PrepareAsync(Repo, "factory/sc-14", CancellationToken.None);

        Assert.True(new FileInfo(Path.Combine(ws.Path, "dir-link")).LinkTarget is not null);
        Assert.DoesNotContain("allow", AclListing(outsideDir));
        Assert.DoesNotContain("allow", AclListing(outsideFile));
        Assert.DoesNotContain("allow", AclListing(Path.Combine(ClonePath, ".git", "hooks")));
        // The checkout itself got the entries, by inheritance.
        Assert.Contains($"user:{Environment.UserName} inherited allow", AclListing(Path.Combine(ws.Path, "README.md")));

        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);
        Assert.False(Directory.Exists(ws.Path));
        Assert.True(File.Exists(outsideFile) && Directory.Exists(outsideDir));
    }

    private static string AclListing(string path)
    {
        var psi = new ProcessStartInfo("/bin/ls", ["-led", path]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
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
