using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator;

/// <summary>Production wiring for <c>factory run</c>; shared by the CLI and the acceptance harness.</summary>
public static class FactoryRunner
{
    public static async Task<RunOutcome> RunAsync(FactoryOptions options, int storyId, TextWriter log, CancellationToken ct)
    {
        // Resolve every credential before touching the ledger so a missing one fails fast.
        var shortcutToken = options.ShortcutApiToken;
        var routerKey = options.RouterKey;
        var appId = options.GitHubAppId;
        var appKey = options.GitHubAppPrivateKeyPem;
        var sandbox = options.WorkerSandbox;

        // Sandboxed, the worker user is single-tenant (every helper exit kills all of its processes),
        // so one sandboxed run per machine, taken before anything runs through the helper.
        using var sandboxLock = sandbox is null ? null : WorkerLock.Acquire(options.WorkRoot);
        if (sandbox is not null)
        {
            await sandbox.EnsureReadyAsync(ct);
        }

        using var shortcutHttp = new HttpClient { BaseAddress = ShortcutClient.DefaultBaseAddress };
        using var githubHttp = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);

        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        await db.Database.MigrateAsync(ct);
        var ledger = new WorkLedger(db, TimeProvider.System);

        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (repo, c) => (await app.CreateInstallationTokenAsync(repo, c)).Token, sandbox: sandbox);
        // Worktrees no run will resume (left by a killed cleanup or a worker that would not stop).
        await workspaces.SweepOrphansAsync((name, c) => RunPipeline.WorktreeIsResumableAsync(ledger, name, c), ct);

        var pipeline = new RunPipeline(
            new ShortcutClient(shortcutHttp, shortcutToken),
            ledger,
            new PostgresRunLocks(options.LedgerConnectionString),
            workspaces,
            new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, sandbox),
            new GitHubPullRequests(githubHttp, app),
            options.DefaultRepo,
            log);

        return await pipeline.RunAsync(storyId, ct);
    }
}
