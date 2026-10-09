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
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-9: Fix", TestGrants.Untainted, CancellationToken.None)); // push

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

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-2: Fix", TestGrants.Untainted, CancellationToken.None));

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
        Assert.False(await workspace.CommitAndPushAsync(Repo, second, "nothing", TestGrants.Untainted, CancellationToken.None));
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

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-6: Fix", TestGrants.Untainted, CancellationToken.None));

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
        var sandbox = new DarkFactory.Orchestrator.Worker.WorkerSandbox(Environment.UserName, SafeHelper.Create(_root).Path, SandboxSupport.FakeSudo(_root));

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
        Assert.True(await workspace.CommitAndPushAsync(Repo, reopened, "sc-5: Fix", TestGrants.Untainted, CancellationToken.None));
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
        Assert.True(await workspace.CommitAndPushAsync(Repo, first, "sc-7: Fix", TestGrants.Untainted, CancellationToken.None));
        var pushed = Git(_remote, "rev-parse", "factory/sc-7").Trim();
        await workspace.RemoveAsync(Repo, first, CancellationToken.None);

        var restored = await workspace.RestoreAsync(Repo, "factory/sc-7", CancellationToken.None);

        Assert.Equal(first, restored);
        Assert.Equal(pushed, Git(restored.Path, "rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(restored.Path, "pushed.txt")));
        // Pushing again changes nothing on the remote.
        await workspace.CommitAndPushAsync(Repo, restored, "sc-7: Fix", TestGrants.Untainted, CancellationToken.None);
        Assert.Equal(pushed, Git(_remote, "rev-parse", "factory/sc-7").Trim());
    }

    // ---- sc-25384: the merge queue's update of a PR branch with its base ----

    /// <summary>Pushes a commit writing <paramref name="file"/> to the remote's main (another PR merging meanwhile).</summary>
    private string AdvanceMain(string file, string content)
    {
        var other = Path.Combine(_root, $"main-{Guid.NewGuid():N}");
        Git(_root, "clone", "-q", _remote, other);
        File.WriteAllText(Path.Combine(other, file), content);
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", $"main: {file}");
        Git(other, "push", "-q", "origin", "main");
        return Git(other, "rev-parse", "HEAD").Trim();
    }

    /// <summary>A pushed PR branch writing <paramref name="file"/>, its worktree removed (as when it reaches the merge queue).</summary>
    private async Task<string> PushBranch(GitWorkspace workspace, string branch, string file, string content)
    {
        var ws = await workspace.PrepareAsync(Repo, branch, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, file), content);
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, $"{branch}: change", TestGrants.Untainted, CancellationToken.None));
        await workspace.RemoveAsync(Repo, ws, CancellationToken.None);
        return Git(_remote, "rev-parse", branch).Trim();
    }

    [Fact]
    public async Task Merge_base_merges_the_moved_base_into_the_branch_and_pushes_it_as_a_fast_forward()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-20", "fix.txt", "fixed\n");
        var main = AdvanceMain("other.txt", "merged first\n");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-20", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);

        Assert.False(merge.Conflicted);
        Assert.False(merge.UpToDate);
        Assert.Equal(main, merge.BaseSha);
        Assert.Equal([head, main], Git(ws.Path, "rev-list", "--parents", "-n", "1", merge.Head).Trim().Split(' ')[1..]);
        _gitCalls.Clear();
        await workspace.PushAsync(Repo, ws, CancellationToken.None);
        Assert.Equal(merge.Head, Git(_remote, "rev-parse", "factory/sc-20").Trim());
        // A plain push (never forced), carrying the App token like every network call.
        var push = Assert.Single(_gitCalls, c => Subcommand(c.Args) == "push");
        Assert.DoesNotContain("--force", push.Args);
        Assert.NotNull(push.Env);

        // Already up to date: nothing to merge.
        var again = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.True(again.UpToDate);
        Assert.Equal(merge.Head, again.Head);
    }

    [Fact]
    public async Task Merge_base_reports_a_conflict_and_leaves_the_merge_for_the_fixer_to_resolve()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-21", "README.md", "hello from the PR\n");
        AdvanceMain("README.md", "hello from main\n");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-21", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);

        Assert.Equal(["README.md"], merge.Conflicts);
        Assert.Equal(head, merge.Head);
        Assert.True(GitWorkspace.HasConflictMarker(File.ReadAllText(Path.Combine(ws.Path, "README.md"))));

        // Pushed with the markers left in: they are found in the pushed commit.
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-21: merge", TestGrants.Untainted, CancellationToken.None));
        var marked = Git(_remote, "rev-parse", "factory/sc-21").Trim();
        Assert.Equal(["README.md"], await workspace.ConflictMarkersAsync(Repo, marked, ["README.md"], CancellationToken.None));

        // The fixer resolves it: the commit completes the merge (two parents) and has no marker left.
        File.WriteAllText(Path.Combine(ws.Path, "README.md"), "hello from both\n");
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-21: resolve", TestGrants.Untainted, CancellationToken.None));
        var resolved = Git(_remote, "rev-parse", "factory/sc-21").Trim();
        Assert.Empty(await workspace.ConflictMarkersAsync(Repo, resolved, ["README.md"], CancellationToken.None));
    }

    [Fact]
    public async Task A_conflicted_merge_committed_as_is_keeps_both_parents()
    {
        var workspace = Workspace();
        var head = await PushBranch(workspace, "factory/sc-23", "README.md", "pr\n");
        var main = AdvanceMain("README.md", "main\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-23", CancellationToken.None);
        await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        File.WriteAllText(Path.Combine(ws.Path, "README.md"), "both\n");

        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-23: resolve", TestGrants.Untainted, CancellationToken.None));

        var pushed = Git(_remote, "rev-parse", "factory/sc-23").Trim();
        Assert.Equal([head, main], Git(ws.Path, "rev-list", "--parents", "-n", "1", pushed).Trim().Split(' ')[1..]);
    }

    [Fact]
    public async Task The_fast_forward_push_refuses_a_branch_that_moved_on_the_remote()
    {
        var workspace = Workspace();
        await PushBranch(workspace, "factory/sc-22", "fix.txt", "fixed\n");
        AdvanceMain("other.txt", "x\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-22", CancellationToken.None);
        await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        // Someone pushes to the PR branch after the queue fetched it.
        var outside = Path.Combine(_root, "outside");
        Git(_root, "clone", "-q", "-b", "factory/sc-22", _remote, outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "y\n");
        Git(outside, "add", ".");
        Git(outside, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "outside");
        Git(outside, "push", "-q", "origin", "factory/sc-22");
        var moved = Git(_remote, "rev-parse", "factory/sc-22").Trim();

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PushAsync(Repo, ws, CancellationToken.None));
        Assert.Equal(moved, Git(_remote, "rev-parse", "factory/sc-22").Trim());
    }

    // ---- owner-side git isolated from the owner's own git config (OwnerGit) ----

    [Fact]
    public void Owner_git_isolation_adds_its_environment_and_config_ahead_of_every_call()
    {
        Assert.Equal(["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "rerere.enabled=false", "-c", "filter.lfs.smudge=",
            "-c", "filter.lfs.clean=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false"], OwnerGit.ConfigArgs);
        var (env, args) = OwnerGit.Isolate(new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1" }, ["merge", "x"]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_LFS_SKIP_SMUDGE"] = "1",
            ["GIT_CONFIG_COUNT"] = "1",
        }, env);
        Assert.Equal([.. OwnerGit.ConfigArgs, "merge", "x"], args);
        Assert.Equal(OwnerGit.Environment, OwnerGit.Isolate(null, []).Env);
        Assert.Throws<ArgumentException>(() => OwnerGit.Isolate(new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = "/home/x/.gitconfig" }, []));
    }

    [Fact]
    public async Task Every_git_call_of_every_workspace_operation_is_isolated_from_the_owners_git_config()
    {
        var workspace = Workspace();
        await PushBranch(workspace, "factory/sc-31", "README.md", "pr\n");
        var main = AdvanceMain("other.txt", "main\n");
        var ws = await workspace.RestoreAsync(Repo, "factory/sc-31", CancellationToken.None);
        Assert.NotNull(await workspace.ReopenAsync(Repo, "factory/sc-31", CancellationToken.None));
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        await workspace.PushAsync(Repo, ws, CancellationToken.None);
        await workspace.ConflictMarkersAsync(Repo, merge.Head, ["README.md"], CancellationToken.None);
        await workspace.ChangedFilesAsync(Repo, main, merge.Head, CancellationToken.None);
        await workspace.FilesAsync(Repo, merge.Head, CancellationToken.None);
        var run = await workspace.PrepareCommitAsync(Repo, "gate-sc-31-base", main, CancellationToken.None);
        await workspace.OverlayAsync(run, merge.Head, ["README.md"], ["other.txt"], CancellationToken.None,
            new Dictionary<string, string> { ["README.md"] = "replaced\n" });
        await workspace.RemoveAsync(Repo, run, CancellationToken.None);
        await workspace.SweepOrphansAsync((_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.Superset(new HashSet<string> { "clone", "fetch", "remote", "worktree", "rev-parse", "symbolic-ref", "add", "status", "commit", "rev-list",
            "push", "merge", "diff", "ls-tree", "cat-file", "checkout", "rm", "hash-object", "update-index", "checkout-index" },
            _gitCalls.Select(c => Subcommand(c.Args)).ToHashSet());
        Assert.All(_gitCalls, c =>
        {
            Assert.Equal(OwnerGit.ConfigArgs, c.Args.Take(OwnerGit.ConfigArgs.Count));
            Assert.NotNull(c.Env);
            Assert.All(OwnerGit.Environment, e => Assert.Equal(e.Value, c.Env[e.Key]));
        });
        // The token-carrying push keeps its auth alongside the isolation.
        Assert.All(_gitCalls.Where(c => Subcommand(c.Args) == "push"), c => Assert.Equal("credential.helper", c.Env!["GIT_CONFIG_KEY_1"]));
    }

    /// <summary>A script under the test root that records it ran (a file named <paramref name="marker"/> in <c>markers/</c>), then runs <paramref name="then"/>.</summary>
    private string Script(string marker, string then)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "scripts")).FullName;
        var markers = Directory.CreateDirectory(Path.Combine(_root, "markers")).FullName;
        var path = Path.Combine(dir, marker);
        File.WriteAllText(path, $"#!/bin/sh\ntouch '{Path.Combine(markers, marker)}'\n{then}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private void Hook(string hooksDir, string prefix, params string[] names)
    {
        Directory.CreateDirectory(hooksDir);
        foreach (var name in names)
        {
            File.Copy(Script($"{prefix}-{name}", "exit 0"), Path.Combine(hooksDir, name), overwrite: true);
            File.SetUnixFileMode(Path.Combine(hooksDir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static readonly string[] HookNames = ["post-checkout", "post-merge", "pre-commit", "commit-msg", "post-commit", "pre-push", "pre-merge-commit"];

    [Fact]
    public async Task Owner_side_git_runs_no_hook_filter_merge_driver_or_fsmonitor_that_owner_config_or_a_pr_could_select()
    {
        // The owner's system and global git config (as git would find them through HOME and GIT_CONFIG_SYSTEM), each defining
        // a hooks path, filter and merge drivers that a PR's .gitattributes can select.
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        Hook(Path.Combine(_root, "global-hooks"), "global-hook", HookNames);
        File.WriteAllText(Path.Combine(home, ".gitconfig"), $"""
            [core]
                hooksPath = {Path.Combine(_root, "global-hooks")}
            [filter "evil"]
                smudge = {Script("global-smudge", "cat")}
                clean = {Script("global-clean", "cat")}
            [merge "evil"]
                driver = {Script("global-merge", "exit 1")}
            """);
        var system = Path.Combine(_root, "system.gitconfig");
        File.WriteAllText(system, $"""
            [filter "sysevil"]
                smudge = {Script("system-smudge", "cat")}
                clean = {Script("system-clean", "cat")}
            [merge "sysevil"]
                driver = {Script("system-merge", "exit 1")}
            """);
        IReadOnlyDictionary<string, string> OwnerEnvironment(IReadOnlyDictionary<string, string>? env) =>
            new Dictionary<string, string>(env ?? new Dictionary<string, string>())
            {
                ["HOME"] = home,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
                ["GIT_CONFIG_SYSTEM"] = system,
            };
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(Token),
            (cwd, env, args, ct) => GitWorkspace.RunGitAsync(cwd, OwnerEnvironment(env), args, ct));

        var first = await workspace.PrepareAsync(Repo, "factory/sc-30", CancellationToken.None);
        // The clone's own config and hooks dir: an LFS filter and an fsmonitor configured there, and hooks in .git/hooks.
        var clone = Path.Combine(_root, "work", "repos", Repo.Owner, Repo.Name);
        Git(clone, "config", "filter.lfs.smudge", Script("clone-lfs-smudge", "cat"));
        Git(clone, "config", "filter.lfs.clean", Script("clone-lfs-clean", "cat"));
        Git(clone, "config", "core.fsmonitor", Script("clone-fsmonitor", "exit 1"));
        Hook(Path.Combine(clone, ".git", "hooks"), "clone-hook", HookNames);
        // The PR selects every driver for its files.
        File.WriteAllText(Path.Combine(first.Path, ".gitattributes"), "*.evil filter=evil merge=evil\n*.sys filter=sysevil merge=sysevil\n*.lfs filter=lfs\n");
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(first.Path, file), "pr\n");
        }
        Assert.True(await workspace.CommitAndPushAsync(Repo, first, "sc-30: change", TestGrants.Untainted, CancellationToken.None));
        await workspace.RemoveAsync(Repo, first, CancellationToken.None);
        // Main changes the same files (the base update conflicts in them), and moves on.
        var other = Path.Combine(_root, "main-30");
        Git(_root, "clone", "-q", _remote, other);
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(other, file), "main\n");
        }
        Git(other, "add", ".");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "main: same files");
        Git(other, "push", "-q", "origin", "main");

        var ws = await workspace.RestoreAsync(Repo, "factory/sc-30", CancellationToken.None);
        var merge = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.Equal(["a.evil", "b.sys", "c.lfs"], merge.Conflicts.Order());
        foreach (var file in new[] { "a.evil", "b.sys", "c.lfs" })
        {
            File.WriteAllText(Path.Combine(ws.Path, file), "both\n");
        }
        Assert.True(await workspace.CommitAndPushAsync(Repo, ws, "sc-30: resolve", TestGrants.Untainted, CancellationToken.None));
        var update = await workspace.MergeBaseAsync(Repo, ws, CancellationToken.None);
        Assert.True(update.UpToDate);
        await workspace.PushAsync(Repo, ws, CancellationToken.None);

        var markers = Path.Combine(_root, "markers");
        Assert.Empty(Directory.GetFiles(markers).Select(Path.GetFileName));
        // The setup is live: the same git without the isolation runs the owner's filter on the PR's file.
        File.Delete(Path.Combine(ws.Path, "a.evil"));
        await GitWorkspace.RunGitAsync(ws.Path, OwnerEnvironment(null), ["checkout", "--", "a.evil"], CancellationToken.None);
        Assert.Contains("global-smudge", Directory.GetFiles(markers).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> main\n", true)]
    [InlineData("<<<<<<<\n", true)]
    [InlineData("x <<<<<<< not at the start\n===\n", false)]
    [InlineData("plain\n=======\n", false)]
    public void Conflict_markers_are_lines_git_writes(string text, bool marked) => Assert.Equal(marked, GitWorkspace.HasConflictMarker(text));

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
