using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The triage worktree's committed symlinks (E4): real git against local repos, no network. A link that resolves outside the
/// worktree is removed before the triage session starts; one that stays inside is kept.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class TriageWorktreeTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("df-triage-links-").FullName;

    private string Outside => Path.Combine(_root, "outside");

    public TriageWorktreeTests()
    {
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "secret\n");
    }

    /// <summary>A repo whose commit holds in-tree and out-of-tree symlinks.</summary>
    private string Seed()
    {
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "-q", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "inside.txt"), "inside\n");
        Directory.CreateDirectory(Path.Combine(seed, "docs"));
        File.CreateSymbolicLink(Path.Combine(seed, "docs", "in-link"), "../inside.txt");
        File.CreateSymbolicLink(Path.Combine(seed, "self"), ".");
        File.CreateSymbolicLink(Path.Combine(seed, "chain"), "self/docs/in-link");
        File.CreateSymbolicLink(Path.Combine(seed, "abs-out"), Path.Combine(Outside, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(seed, "dir-out"), Outside);
        File.CreateSymbolicLink(Path.Combine(seed, "dangling"), "no-such-file");
        // Lexically inside (wt/self/../sibling = wt/sibling), really the worktree's parent's sibling.
        File.CreateSymbolicLink(Path.Combine(seed, "sneaky"), "self/../sibling.txt");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "links");
        return seed;
    }

    [Fact]
    public void Every_symlink_that_resolves_outside_the_worktree_is_removed_and_reported_and_in_tree_ones_are_kept()
    {
        var worktree = Path.Combine(_root, "wt");
        Git(_root, "clone", "-q", Seed(), worktree);
        File.WriteAllText(Path.Combine(_root, "sibling.txt"), "sibling\n"); // sneaky's real target exists

        var removed = TriageWorktree.RemoveOutOfTreeSymlinks(worktree);

        Assert.Equal(
            [("abs-out", Path.Combine(Outside, "secret.txt")), ("dangling", "no-such-file"), ("dir-out", Outside), ("sneaky", "self/../sibling.txt")],
            removed.OrderBy(r => r.Link, StringComparer.Ordinal));
        Assert.All(["abs-out", "dangling", "dir-out", "sneaky"], l => Assert.False(Path.Exists(Path.Combine(worktree, l)) || IsLink(Path.Combine(worktree, l)), l));
        Assert.All(["docs/in-link", "self", "chain"], l => Assert.True(IsLink(Path.Combine(worktree, l)), l));
        // Only the links went: their targets are untouched.
        Assert.Equal("secret\n", File.ReadAllText(Path.Combine(Outside, "secret.txt")));
        Assert.Equal("sibling\n", File.ReadAllText(Path.Combine(_root, "sibling.txt")));
        Assert.Equal("inside\n", File.ReadAllText(Path.Combine(worktree, "chain")));
    }

    [Fact]
    public async Task The_triage_runner_removes_out_of_tree_symlinks_after_checkout_and_before_the_session_starts()
    {
        var remote = Path.Combine(_root, "remote.git");
        Git(_root, "clone", "-q", "--bare", Seed(), remote);
        var workspaces = new GitWorkspace(Path.Combine(_root, "work"), _ => remote, (_, _) => Task.FromResult<string?>("ghs_FakeToken"),
            worktreesDirectory: SandboxTriageRunner.TriageWorktrees, shareWithWorker: false);
        var worker = new LinkRecordingWorker();
        var log = new StringWriter();
        var item = new WorkItem { Id = 5, Source = "github", ExternalId = "gh-5", Title = "t", Repo = "acme/widgets" };

        var result = await new WorkerTriageRunner(workspaces, worker, null, log).RunAsync(item, new RepoRef("acme", "widgets"), "triage",
            (_, _) => Task.CompletedTask, (_, _, _) => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["chain", "docs/in-link", "self"], worker.LinksAtStart);
        Assert.Contains($"removed symlink abs-out -> {Path.Combine(Outside, "secret.txt")}", log.ToString());
        Assert.Contains("removed symlink dir-out", log.ToString());
    }

    /// <summary>A read-only worker that records the symlinks in its working directory when its session would start.</summary>
    private sealed class LinkRecordingWorker : IWorker
    {
        public List<string> LinksAtStart { get; } = [];

        public WorkerTools Tools => WorkerTools.ReadOnly;

        public Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks,
            CancellationToken ct)
        {
            LinksAtStart.AddRange(Links(workingDirectory).Select(p => Path.GetRelativePath(workingDirectory, p)).Order(StringComparer.Ordinal));
            return Task.FromResult(new WorkerResult("triage-sess", 0, false, "success", "{}", ""));
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Every symlink under <paramref name="directory"/>, never descending into one (<c>self</c> links to <c>.</c>); <c>.git</c> skipped.</summary>
    private static IEnumerable<string> Links(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).Where(e => Path.GetFileName(e) != ".git").SelectMany(e =>
            IsLink(e) ? [e] : Directory.Exists(e) ? Links(e) : []);

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
    }
}
