using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator;

/// <summary>Production wiring for <c>factory run</c> and <c>factory work</c>; shared by the CLI and the acceptance harness.</summary>
public static class FactoryRunner
{
    public static ShortcutWorkSource CreateWorkSource(FactoryOptions options, HttpClient shortcutHttp) =>
        new(shortcutHttp, options.ShortcutApiToken, options.WatchScope);

    /// <summary>
    /// <c>factory work</c>'s start-up check: the Shortcut token resolves and every watched team and epic exists.
    /// Returns what is wrong, or null.
    /// </summary>
    public static async Task<string?> CheckWatchScopeAsync(FactoryOptions options, HttpClient shortcutHttp, CancellationToken ct)
    {
        try
        {
            var scope = options.WatchScope;
            if (scope.IsEmpty)
            {
                return null;
            }
            await CreateWorkSource(options, shortcutHttp).ValidateScopeAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is MissingCredentialException or InvalidOperationException or HttpRequestException)
        {
            return ex.Message;
        }
    }

    /// <param name="ignoreScope"><c>factory run --ignore-scope</c>: claim and resume the story even outside the watch scope.</param>
    public static async Task<RunOutcome> RunAsync(FactoryOptions options, int storyId, bool ignoreScope, TextWriter log, CancellationToken ct)
    {
        using var shortcutHttp = new HttpClient { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        return await RunAsync(options, CreateWorkSource(options, shortcutHttp), storyId, ignoreScope, log, ct);
    }

    public static async Task<RunOutcome> RunAsync(FactoryOptions options, IWorkSource source, int storyId, bool ignoreScope, TextWriter log, CancellationToken ct)
    {
        // Resolve every credential before touching the ledger so a missing one fails fast.
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

        using var githubHttp = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        using var routerHttp = new HttpClient { BaseAddress = options.RouterBaseUrl };
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);

        await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var ledger = new WorkLedger(db, TimeProvider.System);

        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (repo, c) => (await app.CreateInstallationTokenAsync(repo, c)).Token, sandbox: sandbox);
        // Worktrees no run will resume (left by a killed cleanup or a worker that would not stop).
        await workspaces.SweepOrphansAsync((name, c) => RunPipeline.WorktreeIsResumableAsync(ledger, name, c), ct);

        var pipeline = new RunPipeline(
            source,
            ledger,
            new PostgresRunLocks(options.LedgerConnectionString),
            workspaces,
            new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, sandbox),
            new GitHubPullRequests(githubHttp, app),
            options.DefaultRepo,
            log,
            // Events are stored only; a running `factory work` host relays them to its viewers (LISTEN/NOTIFY).
            new SessionRecorder(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)),
                new RouterClient(routerHttp, routerKey), TimeProvider.System, log),
            ignoreScope: ignoreScope);

        return await pipeline.RunAsync(storyId, ct);
    }
}

/// <summary>The intake loop's runner: the production pipeline over the Postgres ledger.</summary>
public sealed class FactoryItemRunner(FactoryOptions options, IWorkSource source, TextWriter log) : IItemRunner
{
    public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
    {
        // `factory work` migrated the ledger (LedgerMigrations) before the host started.
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct);
    }

    public Task<RunOutcome> RunAsync(int id, CancellationToken ct) => FactoryRunner.RunAsync(options, source, id, ignoreScope: false, log, ct);
}
