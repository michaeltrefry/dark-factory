using DarkFactory.Orchestrator.Controls;
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
    public static IWorkSource CreateWorkSource(FactoryOptions options, HttpClient shortcutHttp) =>
        new ShortcutWorkSource(shortcutHttp, options.ShortcutApiToken, options.WatchScope);

    /// <summary>
    /// <c>factory work</c>'s start-up check, through the work source (E6): its credentials resolve and every watched
    /// team and epic exists. Returns what is wrong, or null.
    /// </summary>
    public static async Task<string?> CheckWatchScopeAsync(FactoryOptions options, IWorkSource source, CancellationToken ct)
    {
        try
        {
            if (options.WatchScope.IsEmpty)
            {
                return null;
            }
            await source.ValidateScopeAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is MissingCredentialException or InvalidOperationException or HttpRequestException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// <c>factory run</c>'s and <c>factory work</c>'s start-up check for <c>Worker:Auth=router-key</c> (the default): the
    /// worker holds no provider credential, so the router must have at least one plan enrolled for the router key, or
    /// every worker model call fails. A failure, not a warning (E10): without a plan no worker can do anything, and an
    /// unreadable usage report (router down, bad key) leaves enrollment unverified, so it fails too.
    /// Returns what is wrong, or null (always null for <c>claude-login</c>).
    /// </summary>
    public static async Task<string?> CheckRouterEnrollmentAsync(FactoryOptions options, IUsageSource usage, CancellationToken ct)
    {
        if (options.WorkerAuth != WorkerAuth.RouterKey)
        {
            return null;
        }
        const string remedy = "Enroll a plan on the router for this key (`router login claude` and/or `router login codex`), "
            + "or set Worker__Auth=claude-login (weaker: the worker then holds a Claude login, which E5 forbids).";
        SubscriptionUsage report;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            report = await usage.GetUsageAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return $"Worker:Auth=router-key, but the router's enrolled plans could not be read from {options.RouterBaseUrl} "
                + $"(GET /v1/subscriptions/usage: {ex.Message}). Check that the router is up and Router:Key is right. {remedy}";
        }
        if (report.EnrolledCredentials > 0)
        {
            return null;
        }
        var unroutable = report.UnroutableEnrolledCredentials.ToList();
        return unroutable.Count > 0
            ? $"Worker:Auth=router-key, but every enrolled plan the router at {options.RouterBaseUrl} lists for this router key is "
                + $"not routable ({string.Join(", ", unroutable.Select(c => $"{c.Provider} {c.State ?? "unknown state"}"))}), so every "
                + "worker model call would fail. Reconnect the plan on the router (`router login claude` for a Claude plan, "
                + "`router login codex` for a Codex plan), then start again."
            : $"Worker:Auth=router-key, but the router at {options.RouterBaseUrl} lists no enrolled, enabled plan for this router key "
                + $"(GET /v1/subscriptions/usage has no managed or shared credential), so every worker model call would fail. {remedy}";
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
        var pauseGrace = options.PauseGrace;

        // Sandboxed, the worker user is single-tenant (every helper exit kills all of its processes),
        // so one sandboxed run per machine, taken before anything runs through the helper.
        using var sandboxLock = sandbox is null ? null : await FactoryWide("the worker run lock", () => Task.FromResult(WorkerLock.Acquire(options.WorkRoot)));
        if (sandbox is not null)
        {
            await EnsureSandboxReadyAsync(sandbox, options.WorkerAuth, ct);
        }

        using var githubHttp = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        using var routerHttp = new HttpClient { BaseAddress = options.RouterBaseUrl };
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);

        await FactoryWide("the ledger", async () => { await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct); return true; });
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var ledger = new WorkLedger(db, TimeProvider.System);

        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (repo, c) => (await app.CreateInstallationTokenAsync(repo, c)).Token, sandbox: sandbox);
        // Worktrees no run will resume (left by a killed cleanup or a worker that would not stop).
        await FactoryWide("the worktree sweep", async () =>
        {
            await workspaces.SweepOrphansAsync((name, c) => RunPipeline.WorktreeIsResumableAsync(ledger, name, c), ct);
            return true;
        });

        var pipeline = new RunPipeline(
            source,
            ledger,
            new PostgresRunLocks(options.LedgerConnectionString),
            workspaces,
            new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, sandbox,
                pauseFlagDirectory: options.PauseFlagDirectory),
            new GitHubPullRequests(githubHttp, app),
            options.DefaultRepo,
            log,
            // Events are stored only; a running `factory work` host relays them to its viewers (LISTEN/NOTIFY).
            new SessionRecorder(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)),
                new RouterClient(routerHttp, routerKey), TimeProvider.System, log),
            ignoreScope: ignoreScope,
            controls: Controls(options),
            pauseGrace: pauseGrace);

        return await pipeline.RunAsync(storyId, ct);
    }

    /// <summary>The sandbox readiness probe, a factory-wide failure (E10): a stale helper fails the factory once, not every item.</summary>
    internal static Task EnsureSandboxReadyAsync(WorkerSandbox sandbox, WorkerAuth auth, CancellationToken ct) =>
        FactoryWide("the worker sandbox", async () => { await sandbox.EnsureReadyAsync(auth, ct); return true; });

    /// <summary>Runs a set-up step every item shares; its failure is the factory's, not the item's (E10).</summary>
    private static async Task<T> FactoryWide<T>(string what, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"{what}: {ex.Message}", ex);
        }
    }

    /// <summary>The ledger's controls table (Pause/Continue/Stop), shared by every process.</summary>
    public static IControls Controls(FactoryOptions options) =>
        new LedgerControls(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)), TimeProvider.System);

    /// <summary>
    /// <c>factory pause|continue|stop</c>: writes the scope's control, and for Stop finishes each item's stop
    /// at once when no run holds it (a running item is stopped by its own run within about a second).
    /// </summary>
    public static async Task<ControlResult> ControlAsync(FactoryOptions options, string action, string scope, string by, TextWriter log, CancellationToken ct)
    {
        await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        using var shortcutHttp = new HttpClient { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        // Stop needs the board and GitHub, and an epic scope the board (for items with no epic in the ledger);
        // Pause and Continue otherwise only write the control.
        var source = action == "stop" || ControlScope.EpicOf(scope) is not null ? CreateWorkSource(options, shortcutHttp) : null;
        var stops = action == "stop" ? new FactoryItemStops(options, source!, log) : null;
        var actions = new ControlActions(Controls(options), contexts, stops, source, new PostgresRunLocks(options.LedgerConnectionString));
        return action switch
        {
            "pause" => await actions.PauseAsync(scope, by, ct),
            "continue" => await actions.ContinueAsync(scope, by, ct),
            "stop" => await actions.StopAsync(scope, by, ct),
            _ => throw new ArgumentException($"Unknown control action '{action}'.", nameof(action)),
        };
    }
}

/// <summary>Production <see cref="IItemStops"/>: an <see cref="ItemStopper"/> over the Postgres ledger, Shortcut and GitHub, per call.</summary>
public sealed class FactoryItemStops(FactoryOptions options, IWorkSource source, TextWriter log) : IItemStops
{
    public async Task<ControlResult> StopAsync(int storyId, CancellationToken ct)
    {
        using var githubHttp = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var app = new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System);
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var stopper = new ItemStopper(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            new GitHubPullRequests(githubHttp, app), FactoryRunner.Controls(options), log);
        return await stopper.StopAsync(storyId, ct);
    }
}

/// <summary>The intake loop's runner: the production pipeline over the Postgres ledger.</summary>
public sealed class FactoryItemRunner(FactoryOptions options, IWorkSource source, TextWriter log) : IItemRunner
{
    public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
    {
        // `factory work` migrated the ledger (LedgerMigrations) before the host started.
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, FactoryRunner.Controls(options));
    }

    public Task<RunOutcome> RunAsync(int id, CancellationToken ct) => FactoryRunner.RunAsync(options, source, id, ignoreScope: false, log, ct);

    public async Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.GiveUpAsync(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            id, reason, log, ct);
    }
}
