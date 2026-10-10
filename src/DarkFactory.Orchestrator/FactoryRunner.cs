using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
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
            if (source.Naming == ItemNaming.Shortcut ? options.WatchScope.IsEmpty : options.WatchedIssueRepos.Count == 0)
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

    /// <summary>
    /// <c>factory run</c>'s exit code: 0 the run succeeded, 1 it did not, 2 it could not start — a retired review-model
    /// setting is set (checked first, before any network call), a credential is missing, or
    /// <paramref name="checkEnrollment"/> found the router unusable — with the reason on <paramref name="stderr"/>.
    /// </summary>
    public static async Task<int> RunCommandAsync(FactoryOptions options, Func<CancellationToken, Task<string?>> checkEnrollment,
        Func<CancellationToken, Task<RunOutcome>> run, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            options.ValidateSettings();
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
        try
        {
            options.RejectReviewModelSettings();
            if (await checkEnrollment(ct) is { } enrollmentError)
            {
                stderr.WriteLine(enrollmentError);
                return 2;
            }
            return (await run(ct)).Succeeded ? 0 : 1;
        }
        catch (Exception ex) when (ex is MissingCredentialException or ReviewConfigurationException)
        {
            stderr.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <param name="ignoreScope"><c>factory run --ignore-scope</c>: claim and resume the story even outside the watch scope.</param>
    public static async Task<RunOutcome> RunAsync(FactoryOptions options, int storyId, bool ignoreScope, TextWriter log, CancellationToken ct)
    {
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        return await RunAsync(options, CreateWorkSource(options, shortcutHttp), storyId, ignoreScope, log, ct);
    }

    /// <summary><c>factory run &lt;item&gt;</c> for an item of any source: a Shortcut story (<c>sc-N</c>) or a GitHub issue (<c>gh-N</c>).</summary>
    public static async Task<RunOutcome> RunAsync(FactoryOptions options, ItemRef item, bool ignoreScope, TextWriter log, CancellationToken ct)
    {
        if (item.Naming == ItemNaming.Shortcut)
        {
            return await RunAsync(options, item.Id, ignoreScope, log, ct);
        }
        using var githubHttp = OutboundHttp.GitHubApi();
        return await RunAsync(options, CreateIssueSource(options, githubHttp), item.Id, ignoreScope, log, ct);
    }

    /// <summary>The GitHub issue work source over the watched repos (<c>GitHub:Watch:Repos</c>), as the workers' App.</summary>
    public static Issues.GitHubIssueWorkSource CreateIssueSource(FactoryOptions options, HttpClient githubHttp) =>
        new(IssuesClient(options, githubHttp), Contexts(options), options.WatchedIssueRepos);

    private static GitHubIssuesClient IssuesClient(FactoryOptions options, HttpClient githubHttp) =>
        new(githubHttp, new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System));

    /// <summary>
    /// A freeze evaluator over the ledger and the gate App's view of GitHub (main-red), for the intake loop's per-poll check. Its
    /// HttpClient lives as long as the evaluator (the host's life).
    /// </summary>
    public static FactoryFreeze CreateFreeze(FactoryOptions options)
    {
        var http = OutboundHttp.GitHubApi();
        var gateApp = new GitHubApp(http, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System);
        return new FactoryFreeze(Contexts(options), Controls(options), options.Freeze, TimeProvider.System, new GitHubGate(http, gateApp),
            options.ConfiguredRepos, FreezeCheckFailures.Process);
    }

    private static LedgerDbContextFactory Contexts(FactoryOptions options) =>
        new(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));

    /// <summary>
    /// <c>factory work</c>'s GitHub issue intake, one poll (<see cref="Issues.IssueIntake"/>): triage runs in the sandbox like any
    /// worker (<see cref="SandboxTriageRunner"/>), one at a time with the item runs (the intake loop runs both).
    /// </summary>
    public static async Task PollIssuesAsync(FactoryOptions options, IntakeStatus status, TextWriter log, CancellationToken ct)
    {
        using var githubHttp = OutboundHttp.GitHubApi();
        var intake = new Issues.IssueIntake(CreateIssueSource(options, githubHttp), options.WatchedIssueRepos, Contexts(options),
            new PostgresRunLocks(options.LedgerConnectionString), Controls(options), new SandboxTriageRunner(options, log), status,
            options.MaxItemFailures, TimeProvider.System, log);
        await intake.PollAsync(ct);
    }

    public static Task<RunOutcome> RunAsync(FactoryOptions options, IWorkSource source, int storyId, bool ignoreScope, TextWriter log, CancellationToken ct) =>
        RunAsync(options, source, storyId, ignoreScope, log, ct, null);

    /// <param name="adjustGate">Acceptance harness only: wraps the production gate stage (e.g. to push after the verdict).</param>
    internal static async Task<RunOutcome> RunAsync(FactoryOptions options, IWorkSource source, int storyId, bool ignoreScope, TextWriter log,
        CancellationToken ct, Func<GateStage, GateStage>? adjustGate)
    {
        // Resolve every credential before touching the ledger so a missing one fails fast.
        var routerKey = options.RouterKey;
        var appId = options.GitHubAppId;
        var appKey = options.GitHubAppPrivateKeyPem;
        var gateAppId = options.GitHubGateAppId;
        var gateAppKey = options.GitHubGateAppPrivateKeyPem;
        options.RejectReviewModelSettings();
        var sandbox = options.WorkerSandbox;
        var pauseGrace = options.PauseGrace;
        var freezeOptions = options.Freeze;
        var stuck = options.StuckDetection;
        var maxControlReadFailures = options.MaxControlReadFailures;

        // Sandboxed, the worker user is single-tenant (every helper exit kills all of its processes),
        // so one sandboxed run per machine, taken before anything runs through the helper.
        using var sandboxLock = sandbox is null ? null : await FactoryWide("the worker run lock", () => Task.FromResult(WorkerLock.Acquire(options.WorkRoot)));
        if (sandbox is not null)
        {
            await EnsureSandboxReadyAsync(sandbox, options.WorkerAuth, options.ClaudePath, ct);
        }

        using var githubHttp = OutboundHttp.GitHubApi();
        using var routerHttp = OutboundHttp.RouterApi(options.RouterBaseUrl);
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);
        // The merge-capable credential: a separate App only the gate uses (workers' pushes and PRs use the one above).
        var gateApp = new GitHubApp(githubHttp, gateAppId, gateAppKey, TimeProvider.System);
        using var reviewerHttp = ReviewerHttp(options);

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

        var gateGitHub = new GitHubGate(githubHttp, gateApp);
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        // One upstream registry: the reviewers' tools and the review's overlay wait (sc-25708) share its CodeGraph client.
        var upstreams = CreateUpstreams(options, codeGraphHttp, kanbanHttp, log);
        var reviewer = new RouterReviewer(reviewerHttp, routerKey, tools: new ReviewTools(workspaces, upstreams.CodeGraph, upstreams.Kanban, log));
        var gate = CreateGate(options, gateGitHub, reviewer, workspaces, sandbox, upstreams.CodeGraph);
        if (adjustGate is not null)
        {
            gate = adjustGate(gate);
        }
        var controls = Controls(options);
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
                new RouterClient(routerHttp, routerKey), TimeProvider.System, log, costSettleDelay: options.CostSettleDelay),
            ignoreScope: ignoreScope,
            controls: controls,
            pauseGrace: pauseGrace,
            gate: gate,
            // Before every dispatch (sc-25387): a frozen factory defers the run.
            // Failed checks are counted per process (FreezeCheckFailures.Process), across runs and the intake loop's polls.
            freeze: new FactoryFreeze(Contexts(options), controls, freezeOptions, TimeProvider.System, gate.GitHub, options.ConfiguredRepos,
                FreezeCheckFailures.Process),
            // A looping worker is interrupted at its next tool boundary and its round fails (sc-25388).
            stuck: stuck,
            maxControlReadFailures: maxControlReadFailures);

        return await pipeline.RunAsync(storyId, ct);
    }

    /// <summary>
    /// The reviewers' tools (sc-25705, sc-25706): <c>read_file</c>, <c>list_files</c> and <c>grep</c> from the gate's clone (owner-side git, by object), the
    /// CodeGraph tools with the owner's CodeGraph token. Without a token the factory still starts (only the reviewers use CodeGraph):
    /// every CodeGraph tool then answers an error result, recorded in the verdict, and this says so once.
    /// </summary>
    internal static ReviewTools CreateReviewTools(FactoryOptions options, IReviewFiles files, HttpClient codeGraphHttp, TextWriter log,
        HttpClient? kanbanHttp = null)
    {
        var upstreams = CreateUpstreams(options, codeGraphHttp, kanbanHttp, log);
        return new ReviewTools(files, upstreams.CodeGraph, upstreams.Kanban, log);
    }

    /// <summary>
    /// The upstream MCP registry (sc-25707), shared by the reviewers' tool loop and the loopback MCP proxy: CodeGraph with the owner's
    /// CodeGraph token, Kanban (at <c>Kanban:McpUrl</c>, over <paramref name="kanbanHttp"/>) with the owner's Kanban token. Each is
    /// optional: without its token it is left out (reviewers' CodeGraph tools then answer an error, and no Kanban tool is offered),
    /// and this says so once.
    /// </summary>
    internal static Mcp.McpUpstreams CreateUpstreams(FactoryOptions options, HttpClient codeGraphHttp, HttpClient? kanbanHttp, TextWriter log)
    {
        CodeGraph.CodeGraphMcpClient? codeGraph = null;
        if (options.TryGet(o => o.CodeGraphToken, out var token))
        {
            codeGraph = new CodeGraph.CodeGraphMcpClient(codeGraphHttp, token!);
        }
        else
        {
            log.WriteLine("[review] no CodeGraph token (CodeGraph:Token, env FACTORY_CODEGRAPH_TOKEN or keychain account "
                + $"'{SecretAccounts.CodeGraphToken}'): reviewers' CodeGraph tools will answer an error");
        }
        Mcp.McpUpstream? kanban = null;
        if (kanbanHttp is not null && options.TryGet(o => o.KanbanToken, out var kanbanToken))
        {
            // Kanban:McpUrl is the endpoint itself: requests go to the client's base address.
            kanban = new Mcp.McpUpstream(Mcp.McpServers.Kanban, new Mcp.McpHttpClient(kanbanHttp, kanbanToken!, "Kanban", ""),
                Mcp.McpServers.KanbanTools, repoScoped: false);
        }
        else if (kanbanHttp is not null)
        {
            log.WriteLine("[mcp] no Kanban token (Kanban:Token, env FACTORY_KANBAN_TOKEN or keychain account "
                + $"'{SecretAccounts.KanbanToken}'): no Kanban upstream");
        }
        return new Mcp.McpUpstreams(codeGraph, kanban);
    }

    /// <summary>The reviewers' router client: one reviewer call may take <c>Review:TimeoutMinutes</c>.</summary>
    internal static HttpClient ReviewerHttp(FactoryOptions options) => OutboundHttp.RouterApi(options.RouterBaseUrl, options.ReviewTimeout);

    /// <summary>
    /// The merge gate as configured: CI on the PR's head polled every <c>Gate:CiPollSeconds</c> until <c>Gate:CiTimeoutMinutes</c>,
    /// the new tests run sandboxed for up to <c>Gate:TestTimeoutMinutes</c> each, and each review waits up to
    /// <c>CodeGraph:OverlayTimeoutMinutes</c> for CodeGraph's overlay of the head (sc-25708; without a CodeGraph token the wait ends at
    /// once, recorded as unavailable).
    /// </summary>
    internal static GateStage CreateGate(FactoryOptions options, IGateGitHub github, IReviewer reviewer, GitWorkspace workspaces,
        WorkerSandbox? sandbox, ICodeGraphOverlays? codeGraph = null) =>
        new(github, reviewer, options.CiPollInterval, options.CiTimeout, Tests: new SandboxTestRunner(workspaces, sandbox, options.TestTimeout),
            Overlays: new ReviewOverlays(codeGraph, options.CodeGraphOverlayTimeout));

    /// <summary>The sandbox readiness probe, a factory-wide failure (E10): a stale helper fails the factory once, not every item.</summary>
    internal static Task EnsureSandboxReadyAsync(WorkerSandbox sandbox, WorkerAuth auth, string claudePath, CancellationToken ct) =>
        FactoryWide("the worker sandbox", async () => { await sandbox.EnsureReadyAsync(auth, claudePath, ct); return true; });

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
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        // Stop needs the board and GitHub, and an epic scope the board (for items with no epic in the ledger);
        // Pause and Continue otherwise only write the control.
        var source = action == "stop" || ControlScope.EpicOf(scope) is not null ? CreateWorkSource(options, shortcutHttp) : null;
        using var githubHttp = OutboundHttp.GitHubApi();
        var stops = action == "stop" ? new FactoryItemStops(options, source!, log, CreateIssueSource(options, githubHttp)) : null;
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

/// <summary>
/// Production <see cref="IItemStops"/>: an <see cref="ItemStopper"/> over the Postgres ledger, the item's work source (Shortcut, or
/// <paramref name="issues"/> for a GitHub issue) and GitHub, per call.
/// </summary>
public sealed class FactoryItemStops(FactoryOptions options, IWorkSource source, TextWriter log, IWorkSource? issues = null) : IItemStops
{
    public Task<ControlResult> StopAsync(int storyId, CancellationToken ct) => StopAsync(source, storyId, ct);

    public Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct) =>
        new[] { source, issues }.OfType<IWorkSource>().FirstOrDefault(s => s.Naming == item.Naming) is { } owner
            ? StopAsync(owner, item.Id, ct)
            : Task.FromResult(new ControlResult(true, $"{item}: stop requested; its next run stops it."));

    private async Task<ControlResult> StopAsync(IWorkSource owner, int id, CancellationToken ct)
    {
        using var githubHttp = OutboundHttp.GitHubApi();
        var app = new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System);
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var stopper = new ItemStopper(owner, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            new GitHubPullRequests(githubHttp, app), FactoryRunner.Controls(options), log);
        return await stopper.StopAsync(id, ct);
    }
}

/// <summary>
/// Production <see cref="Issues.ITriageRunner"/>: one triage session as a sandboxed worker, like an item's run — the worker run lock,
/// the sandbox readiness check (both factory-wide failures, E10), the router-only worker, its events stored (E7) — in a worktree whose
/// owner-side git holds a contents-read token only (<see cref="Issues.WorkerTriageRunner.TriageWorkspaceToken"/>). The worker is
/// read-only and reads only its own worktree (<see cref="WorkerTools.ReadOnly"/>: Read, Glob, Grep inside it; E4), and never runs unsandboxed (refused, factory-wide). Its
/// worktree lives under its own root (<see cref="TriageWorktrees"/>) that no item's run uses, swept of leftovers before each triage
/// (triages run one at a time, under the worker run lock), and is not shared for writing: the worker user reads it through the work
/// root's inherited read entry (<c>setup-worker-user.sh</c>) and cannot write it.
/// </summary>
public sealed class SandboxTriageRunner(FactoryOptions options, TextWriter log) : Issues.ITriageRunner
{
    /// <summary>The work root's directory of triage worktrees, apart from the items' <c>worktrees</c>.</summary>
    public const string TriageWorktrees = "triage-worktrees";

    public async Task<WorkerResult> RunAsync(WorkItem item, RepoRef repo, string prompt, Func<string, CancellationToken, Task> onSession,
        Func<string, string, CancellationToken, Task> onTaint, CancellationToken ct)
    {
        var routerKey = options.RouterKey;
        var sandbox = options.WorkerSandbox;
        if (sandbox is null)
        {
            // factory work refuses to start like this (Program); a triage reached any other way is refused here, factory-wide.
            var refusal = Issues.IssueIntake.UnsandboxedRefusal(sandbox, [repo])!;
            throw new FactoryUnavailableException($"the issue triage: {refusal}", new InvalidOperationException(refusal));
        }
        using var sandboxLock = sandbox is null ? null : await FactoryWideStep("the worker run lock", () => WorkerLock.Acquire(options.WorkRoot));
        if (sandbox is not null)
        {
            await FactoryRunner.EnsureSandboxReadyAsync(sandbox, options.WorkerAuth, options.ClaudePath, ct);
        }
        using var githubHttp = OutboundHttp.GitHubApi();
        using var routerHttp = OutboundHttp.RouterApi(options.RouterBaseUrl);
        var app = new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System);
        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (r, c) => (await app.CreateInstallationTokenAsync(r, c, Issues.WorkerTriageRunner.TriageWorkspaceToken)).Token, sandbox: sandbox,
            worktreesDirectory: TriageWorktrees, shareWithWorker: false);
        // Under the worker run lock no other triage runs: whatever a crashed or unstoppable one left is swept (nothing resumes a triage).
        try
        {
            await workspaces.SweepOrphansAsync((_, _) => Task.FromResult(false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"the triage worktree sweep: {ex.Message}", ex);
        }
        // CodeGraph through the loopback MCP proxy this process hosts for the session (sc-25707), granted only the triage profile
        // (Mcp.McpProfile.Triage: the CodeGraph tools whose answers stay in the pinned project; no Kanban upstream at all — the issue
        // text is untrusted and the triage's answer is posted on the issue). The upstream token stays here; the session gets a
        // credential revoked when it ends, in a file (never argv).
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        var upstreams = FactoryRunner.CreateUpstreams(options, codeGraphHttp, kanbanHttp: null, log);
        var configs = Path.Combine(options.WorkRoot, Mcp.McpProxySessions.DirectoryName);
        await using var proxy = await StartMcpProxyAsync(upstreams, configs, ct);
        var tools = TriageTools(upstreams, proxy is not null);
        var worker = new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, sandbox,
            pauseFlagDirectory: options.PauseFlagDirectory, tools: tools);
        var sessions = new SessionRecorder(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)),
            new RouterClient(routerHttp, routerKey), TimeProvider.System, log, costSettleDelay: options.CostSettleDelay);
        return await new Issues.WorkerTriageRunner(workspaces, worker, sessions, log,
            proxy is null ? null : new Mcp.McpProxySessions(proxy, configs, sandbox!.User)).RunAsync(item, repo, prompt, onSession, onTaint, ct);
    }

    /// <summary>
    /// The triage worker's tools: <see cref="WorkerTools.ReadOnly"/>, plus (with the proxy) exactly the triage profile's MCP tools
    /// (<see cref="Mcp.McpProfile.Triage"/>) — its <c>--allowedTools</c> names no other MCP tool.
    /// </summary>
    internal static WorkerTools TriageTools(Mcp.McpUpstreams upstreams, bool proxied) =>
        proxied ? WorkerTools.ReadOnly.WithMcp(Mcp.McpProfile.Triage.ToolRules(upstreams)) : WorkerTools.ReadOnly;

    /// <summary>
    /// Sweeps <paramref name="configs"/> of a crashed process's session files (under the worker run lock: no other triage's file is
    /// live) and starts the loopback MCP proxy when CodeGraph is configured (else null). Either failing is factory-wide
    /// (<see cref="FactoryUnavailableException"/>, E10), never the issue's.
    /// </summary>
    internal static async Task<Mcp.McpProxy?> StartMcpProxyAsync(Mcp.McpUpstreams upstreams, string configs, CancellationToken ct,
        Func<Mcp.McpUpstreams, CancellationToken, Task<Mcp.McpProxy>>? start = null)
    {
        try
        {
            Mcp.McpProxySessions.Sweep(configs);
            return upstreams.CodeGraph is null ? null : await (start ?? Mcp.McpProxy.StartAsync)(upstreams, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"the MCP proxy: {ex.Message}", ex);
        }
    }

    private static Task<T> FactoryWideStep<T>(string what, Func<T> step)
    {
        try
        {
            return Task.FromResult(step());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"{what}: {ex.Message}", ex);
        }
    }
}

/// <summary>The intake loop's runner: the production pipeline over the Postgres ledger, for one work source's items.</summary>
public sealed class FactoryItemRunner(FactoryOptions options, IWorkSource source, TextWriter log) : IItemRunner
{
    public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
    {
        // `factory work` migrated the ledger (LedgerMigrations) before the host started.
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, FactoryRunner.Controls(options), naming: source.Naming);
    }

    public Task<RunOutcome> RunAsync(int id, CancellationToken ct) => FactoryRunner.RunAsync(options, source, id, ignoreScope: false, log, ct);

    public async Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.GiveUpAsync(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            id, reason, log, ct);
    }
}
