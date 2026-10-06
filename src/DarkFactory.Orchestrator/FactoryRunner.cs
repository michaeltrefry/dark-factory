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

        using var shortcutHttp = new HttpClient { BaseAddress = ShortcutClient.DefaultBaseAddress };
        using var githubHttp = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);

        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        await db.Database.MigrateAsync(ct);

        var pipeline = new RunPipeline(
            new ShortcutClient(shortcutHttp, shortcutToken),
            new WorkLedger(db, TimeProvider.System),
            new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
                async (repo, c) => (await app.CreateInstallationTokenAsync(repo, c)).Token),
            new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout),
            new GitHubPullRequests(githubHttp, app),
            options.DefaultRepo,
            log);

        return await pipeline.RunAsync(storyId, ct);
    }
}
