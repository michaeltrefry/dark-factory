// Runs the real RunPipeline for one story against a Postgres ledger, a local bare git
// "remote", a fake `claude` script and file-backed Shortcut/PR stand-ins under <work-dir>.
// Usage: DarkFactory.CrashHost <ledger-connection-string> <work-dir> <story-id>
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

var (connection, workDir, storyId) = (args[0], args[1], int.Parse(args[2]));

await LedgerMigrations.MigrateAsync(connection, CancellationToken.None);
await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(connection));

var pipeline = new RunPipeline(
    new FileStories(Path.Combine(workDir, "comments.log")),
    new WorkLedger(db, TimeProvider.System),
    new PostgresRunLocks(connection),
    new GitWorkspace(Path.Combine(workDir, "work"), _ => Path.Combine(workDir, "origin.git"), (_, _) => Task.FromResult<string?>(null)),
    new ClaudeWorker(Path.Combine(workDir, "fake-claude.sh"), new Uri("http://127.0.0.1:9/"), "rk_test", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(2)),
    new FilePullRequests(Path.Combine(workDir, "prs.log")),
    new RepoRef("acme", "widgets"),
    Console.Out,
    new SessionRecorder(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(connection)),
        new NoCost(), TimeProvider.System, Console.Out, costRetryDelays: []));

var outcome = await pipeline.RunAsync(storyId, CancellationToken.None);
Console.WriteLine($"outcome: {outcome}");
return outcome.Succeeded ? 0 : 1;

sealed class NoCost : ISessionCostSource
{
    public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionCost?>(null);
}

/// <summary>A one-story board: comments go to a file, other board writes are no-ops.</summary>
sealed class FileStories(string commentsPath) : IWorkSource
{
    public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) =>
        Task.FromResult(new WorkSpec(new WorkStory(id, "Add fix.txt", "Create fix.txt.", "bug", $"https://app.shortcut.com/test/story/{id}"), null, []));

    public Task CommentAsync(int id, string text, CancellationToken ct) =>
        File.AppendAllTextAsync(commentsPath, $"{id}\t{text.ReplaceLineEndings(" ")}\n", ct);

    public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
    public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => Task.FromResult(ClaimResult.Ok);
    public Task<bool> InScopeAsync(int id, CancellationToken ct) => Task.FromResult(true);
    public Task ReleaseAsync(int id, CancellationToken ct) => Task.CompletedTask;
    public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => Task.CompletedTask;
    public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>Like GitHub: one open PR per head branch; opening again returns the existing one.</summary>
sealed class FilePullRequests(string path) : IPullRequests
{
    public async Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct) : [];
        if (lines.Select(l => l.Split('\t')).FirstOrDefault(p => p[0] == head) is { } existing)
        {
            return existing[1];
        }
        var url = $"https://github.com/{repo}/pull/{lines.Length + 1}";
        await File.AppendAllTextAsync(path, $"{head}\t{url}\n", ct);
        return url;
    }
}
