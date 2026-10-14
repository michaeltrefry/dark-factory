using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator;

/// <summary>Production wiring for <c>factory run</c> and <c>factory work</c>; shared by the CLI and the acceptance harness.</summary>
public static class FactoryRunner
{
    /// <summary>
    /// <c>factory work</c>'s start-up check (E10: fail fast on a missing credential or a bad scope rather than on the first ready
    /// item). Every setting with a rule; the credentials every run needs (session costs come from the router, pushes and pull
    /// requests need the GitHub App, the merge gate its own App, the dashboard its login); no retired review-model setting; then
    /// the work sources (sc-25982): at least one must be configured (<see cref="WorkSourceRegistry.Configured"/> — any non-empty
    /// set, KanbanBoard alone included; none is refused with <see cref="WorkSourceRegistry.NothingWatched"/>, which names every
    /// source's settings and how work already in flight is finished by id meanwhile), a watched source
    /// must have its credential, and each configured source's scope is checked on its board (<see cref="CheckWatchScopeAsync"/>).
    /// A source that has a credential and watches nothing is a note on <paramref name="stderr"/>: it is not polled for new work
    /// and its scope is not checked (no board request), but the loop still finishes its items already in flight
    /// (<see cref="WorkSourceRegistry.Lanes"/>). Returns why the factory cannot start (exit code 2), or null.
    /// </summary>
    public static async Task<string?> CheckWorkStartAsync(FactoryOptions options, WorkSourceRegistry sources, TextWriter stderr, CancellationToken ct)
    {
        IReadOnlyList<IWorkSource> configured;
        try
        {
            options.ValidateSettings();
            _ = (options.RouterKey, options.GitHubAppId, options.GitHubAppPrivateKeyPem, options.DashboardPasswordHash);
            _ = (options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem);
            options.RejectReviewModelSettings();
            if (CodeGraphTokenWarning(options) is { } codeGraphWarning)
            {
                stderr.WriteLine(codeGraphWarning);
            }
            _ = Dashboard.DashboardBinding.Addresses(options.DashboardBindAddress);
            configured = sources.Configured;
            foreach (var note in sources.Unwatched)
            {
                stderr.WriteLine(note);
            }
            if (configured.Count == 0)
            {
                return WorkSourceRegistry.NothingWatched;
            }
            if (Issues.IssueIntake.UnsandboxedRefusal(options.WorkerSandbox, options.WatchedIssueRepos) is { } unsandboxed)
            {
                return unsandboxed;
            }
            if (SlotsNote(options.WorkerSlotCount, options.MaxConcurrentRuns) is { } slotsNote)
            {
                stderr.WriteLine(slotsNote);
            }
        }
        catch (Exception ex) when (ex is MissingCredentialException or ReviewConfigurationException or InvalidOperationException)
        {
            return ex.Message;
        }
        foreach (var source in configured)
        {
            if (await CheckWatchScopeAsync(source, ct) is { } scopeError)
            {
                return scopeError;
            }
        }
        return null;
    }

    /// <summary>
    /// <c>factory work</c>'s start-up note when <c>Worker:Slots</c> is larger than <c>Intake:MaxConcurrentRuns</c> (sc-25989), or
    /// null: allowed — the slots this process cannot fill serve a <c>factory run</c> beside it — but worth saying, since by itself
    /// <c>factory work</c> then never has more than <paramref name="runs"/> worker sessions at a time. (More runs than slots is
    /// the ordinary case: runs overlap outside their worker phases too.)
    /// </summary>
    internal static string? SlotsNote(int slots, int runs) => slots > runs
        ? $"note: Worker:Slots is {slots} but Intake:MaxConcurrentRuns is {runs}: factory work runs at most {runs} item{(runs == 1 ? "" : "s")} at once, "
            + $"so it uses at most {runs} worker slot{(runs == 1 ? "" : "s")} at a time (the others serve a `factory run` beside it); raise Intake:MaxConcurrentRuns to use them"
        : null;

    /// <summary>
    /// The start-up check of one configured work source, through the source (E6): its credentials resolve and every part of its
    /// watch scope exists on its board. Returns what is wrong, or null.
    /// </summary>
    public static async Task<string?> CheckWatchScopeAsync(IWorkSource source, CancellationToken ct)
    {
        try
        {
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
    /// <c>factory run</c>'s exit code: 0 the run succeeded, 1 it did not (also a Kanban item that could not be read or is no
    /// item the factory can work, <see cref="Kanban.KanbanItemException"/>/<see cref="Kanban.KanbanException"/>: its message on
    /// <paramref name="stderr"/>, no stack trace), 2 it could not start — a retired review-model
    /// setting is set (checked first, before any network call), the item's work source is not configured
    /// (<paramref name="requireSource"/>, sc-25982: before any network call too; the reason names the settings that configure it,
    /// and with no source configured at all those of every source), a credential is missing, or
    /// <paramref name="checkEnrollment"/> found the router unusable — with the reason on <paramref name="stderr"/>.
    /// </summary>
    /// <param name="requireSource">Throws <see cref="MissingCredentialException"/> unless the item's source can be reached (<see cref="WorkSourceRegistry.Of"/>).</param>
    public static async Task<int> RunCommandAsync(FactoryOptions options, Func<CancellationToken, Task<string?>> checkEnrollment,
        Func<CancellationToken, Task<RunOutcome>> run, TextWriter stderr, CancellationToken ct, Action? requireSource = null)
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
            requireSource?.Invoke();
            if (CodeGraphTokenWarning(options) is { } warning)
            {
                stderr.WriteLine(warning);
            }
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
        catch (Exception ex) when (ex is Kanban.KanbanItemException or Kanban.KanbanException)
        {
            // Raised reading a Kanban item's spec, before the pipeline's own handling (inside it, a failed step escalates the item
            // and the run returns an outcome): an unknown kb-<n>, an item with no known repository, or the board refusing the read.
            // The message says what to do.
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The start-up warning of <c>factory run</c> and <c>factory work</c> when no CodeGraph token is configured (sc-25709: the factory still
    /// starts, but <c>risk-threshold</c> cannot read CodeGraph, so the gate merges nothing that needs it), or null with a token.
    /// </summary>
    internal static string? CodeGraphTokenWarning(FactoryOptions options) =>
        options.TryGet(o => o.CodeGraphToken, out _) ? null
            : $"no CodeGraph token ({MergeGate.CodeGraphTokenSources}): the merge gate will merge no change whose tier requires risk-threshold";

    /// <summary>
    /// <c>factory run &lt;item&gt;</c> for an item of any source — a Shortcut story (<c>sc-N</c>), a GitHub issue (<c>gh-N</c>) or a
    /// Kanban item (<c>kb-N</c>) — on the source its id names (<see cref="WorkSourceRegistry.Of"/>, sc-25982).
    /// </summary>
    /// <param name="ignoreScope"><c>factory run --ignore-scope</c>: claim and resume the item even outside its source's watch scope.</param>
    /// <exception cref="MissingCredentialException">The item's source is not configured.</exception>
    public static async Task<RunOutcome> RunAsync(FactoryOptions options, ItemRef item, bool ignoreScope, TextWriter log, CancellationToken ct)
    {
        using var sources = new WorkSourceRegistry(options);
        return await RunAsync(options, sources.Of(item.Naming), item.Id, ignoreScope, log, ct);
    }

    /// <summary>
    /// <c>factory kanban key &lt;item id&gt;</c>: the <c>kb-&lt;n&gt;</c> of a KanbanBoard item by its board id, given to it now if the
    /// factory has not seen the item before (<see cref="Kanban.KanbanWorkSource.KeyOfAsync"/>: the item must be on the board; nothing
    /// is written to it) — what <c>factory run kb-&lt;n&gt;</c> then takes. <c>factory work</c>'s intake loop gives every item it
    /// lists its key by itself (sc-25982); this is for an item no poll has listed: one run by hand before the loop saw it, or one
    /// the loop never lists (outside the watched projects, run with <c>--ignore-scope</c>; not <c>Ready</c> yet).
    /// </summary>
    public static async Task<string> KanbanKeyAsync(FactoryOptions options, Guid itemId, CancellationToken ct)
    {
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var source = WorkSourceRegistry.Kanban(options, kanbanHttp);
        await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
        return ItemNaming.Kanban.Format(await source.KeyOfAsync(itemId, ct));
    }

    /// <summary>
    /// <c>factory kanban key --epic &lt;epic id&gt;</c> (sc-25985): the key <c>kb-&lt;n&gt;</c> of a KanbanBoard epic by its board id,
    /// given to it now if the factory has read no item of it yet (<see cref="Kanban.KanbanWorkSource.EpicKeyOfAsync"/>: the epic
    /// must be on the board; nothing is written to it) — what <c>factory pause|stop --epic kb-&lt;n&gt;</c> then takes, so an epic
    /// can be held before its first item is claimed. Failures are reported as the item form's (<see cref="IsKanbanKeyFailure"/>).
    /// </summary>
    public static async Task<string> KanbanEpicKeyAsync(FactoryOptions options, Guid epicId, CancellationToken ct)
    {
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var source = WorkSourceRegistry.Kanban(options, kanbanHttp);
        await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
        return ItemNaming.Kanban.Epic(await source.EpicKeyOfAsync(epicId, ct)).ToString();
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a failure <c>factory kanban key</c> reports by its message with exit code 1, rather than
    /// a stack trace: the board refusing or not answering (<see cref="Kanban.KanbanException"/> and any other
    /// <see cref="InvalidOperationException"/>, <see cref="HttpRequestException"/>, an HTTP timeout — a
    /// <see cref="TaskCanceledException"/> the caller did not ask for), or the ledger failing (<see cref="System.Data.Common.DbException"/>,
    /// Npgsql's included, and EF's <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>). A cancellation the caller asked
    /// for (Ctrl-C: <paramref name="ct"/>) is not one.
    /// </summary>
    public static bool IsKanbanKeyFailure(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => ex is TaskCanceledException && !ct.IsCancellationRequested,
        _ => ex is InvalidOperationException or HttpRequestException or System.Data.Common.DbException
            or Microsoft.EntityFrameworkCore.DbUpdateException,
    };

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
    /// worker (<see cref="SandboxTriageRunner"/>), before the poll's item runs and under the sandbox user's lock like their worker phases.
    /// </summary>
    public static async Task PollIssuesAsync(FactoryOptions options, IntakeStatus status, TextWriter log, CancellationToken ct)
    {
        using var githubHttp = OutboundHttp.GitHubApi();
        var intake = new Issues.IssueIntake(WorkSourceRegistry.Issues(options, githubHttp), options.WatchedIssueRepos, Contexts(options),
            new PostgresRunLocks(options.LedgerConnectionString), Controls(options), new SandboxTriageRunner(options, log), status,
            options.MaxItemFailures, TimeProvider.System, log);
        await intake.PollAsync(ct);
    }

    public static Task<RunOutcome> RunAsync(FactoryOptions options, IWorkSource source, int storyId, bool ignoreScope, TextWriter log, CancellationToken ct) =>
        RunAsync(options, source, storyId, ignoreScope, log, ct, null);

    /// <param name="adjustGate">Acceptance harness only: wraps the production gate stage (e.g. to push after the verdict).</param>
    /// <param name="testSandbox">Tests only: the sandbox in place of the configured one (a fake helper that is the current one for itself).</param>
    internal static async Task<RunOutcome> RunAsync(FactoryOptions options, IWorkSource source, int storyId, bool ignoreScope, TextWriter log,
        CancellationToken ct, Func<GateStage, GateStage>? adjustGate, WorkerSandbox? testSandbox = null)
    {
        // Resolve every credential before touching the ledger so a missing one fails fast.
        var routerKey = options.RouterKey;
        var appId = options.GitHubAppId;
        var appKey = options.GitHubAppPrivateKeyPem;
        var gateAppId = options.GitHubGateAppId;
        var gateAppKey = options.GitHubGateAppPrivateKeyPem;
        options.RejectReviewModelSettings();
        // The worker slots (sc-25989): one sandbox user each. A test's sandbox is a pool of that one slot.
        var slots = testSandbox is null ? options.WorkerSlots : Worker.WorkerSlots.Single(testSandbox);
        var pauseGrace = options.PauseGrace;
        var freezeOptions = options.Freeze;
        var stuck = options.StuckDetection;
        var maxControlReadFailures = options.MaxControlReadFailures;

        // Before the ledger or the board is touched: an installed helper that is not the current one fails the factory here, with
        // nothing claimed or recorded (the rest of the readiness check runs the helper, so it waits for the first worker phase).
        await EnsureHelperCurrentAsync(slots, ct);
        await FactoryWide("the ledger", async () => { await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct); return true; });
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var ledger = new WorkLedger(db, TimeProvider.System);
        // Sandboxed, each worker user is single-tenant (every helper exit kills all of its processes), so whatever runs through a
        // helper holds that slot's lock (WorkerLock, sc-25988) — for that phase only, not for the run: the pipeline takes the
        // item's slot (waiting) around each worker phase, and a run that reaches none — a review, a CI or merge-queue wait, a plan
        // item waiting on its stories — never takes one, probes no sandbox and sweeps no worktree.

        using var githubHttp = OutboundHttp.GitHubApi();
        using var routerHttp = OutboundHttp.RouterApi(options.RouterBaseUrl);
        var app = new GitHubApp(githubHttp, appId, appKey, TimeProvider.System);
        // The merge-capable credential: a separate App only the gate uses (workers' pushes and PRs use the one above).
        var gateApp = new GitHubApp(githubHttp, gateAppId, gateAppKey, TimeProvider.System);
        using var reviewerHttp = ReviewerHttp(options);

        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (repo, c) => (await app.CreateInstallationTokenAsync(repo, c)).Token, sandbox: slots);
        var runLocks = new PostgresRunLocks(options.LedgerConnectionString);
        // What the run's first worker phase starts with, under its slot's lock (both run the launch helper; either failing is
        // the factory's, not the item's, E10): the readiness probe of that slot's sandbox, then the sweep of worktrees no run will
        // resume (left by a killed cleanup, a worker that would not stop, or a clean-up that found the sandbox user busy) — and
        // none a live run owns (sc-25987): another run's gate test worktrees or base-update worktree are its item's, guarded by
        // that item's run lock; this run's own item is guarded the same way (its pipeline holds that lock), so it replaces its
        // own leftovers itself. A leftover another slot's user wrote is deleted as that user, if that slot is free now, and is
        // otherwise left to a sweep that finds it free (WorkerSlots.TryDeleteAsWorkerAsync); one a user outside the pool wrote
        // is left for the owner, reported once, and never fails the sweep.
        async Task EnterSandboxAsync(CancellationToken c)
        {
            if (slots is not null)
            {
                await EnsureSandboxReadyAsync(slots, options.WorkerAuth, options.ClaudePath, c);
            }
            await FactoryWide("the worktree sweep", async () =>
            {
                await workspaces.SweepOrphansAsync((name, token) => RunPipeline.WorktreeIsResumableAsync(ledger, name, token),
                    new LiveRuns(ledger, runLocks), c);
                return true;
            });
        }

        var gateGitHub = new GitHubGate(githubHttp, gateApp);
        using var codeGraphHttp = OutboundHttp.CodeGraphApi(options.CodeGraphBaseUrl);
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        // One upstream registry: the reviewers' tools and the review's overlay wait (sc-25708) share its CodeGraph client.
        var upstreams = CreateUpstreams(options, codeGraphHttp, kanbanHttp, log);
        var reviewer = new RouterReviewer(reviewerHttp, routerKey, tools: new ReviewTools(workspaces, upstreams.CodeGraph, upstreams.Kanban, log));
        var gate = CreateGate(options, gateGitHub, reviewer, workspaces, slots, upstreams.CodeGraph);
        if (adjustGate is not null)
        {
            gate = adjustGate(gate);
        }
        var controls = Controls(options);
        var pipeline = new RunPipeline(
            source,
            ledger,
            runLocks,
            workspaces,
            new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, slots,
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
            maxControlReadFailures: maxControlReadFailures,
            // A plan item's planner session (sc-25870): read-only, in its own throwaway worktree, CodeGraph and Kanban through the proxy.
            planner: new SandboxPlanRunner(options, slots, app, upstreams, log),
            // The only repositories a plan naming several may name (sc-25874).
            configuredRepos: options.ConfiguredRepos.Select(RepoRef.Parse).ToList(),
            // The worker slots, one held per worker phase (sc-25988, sc-25989); unsandboxed there is none.
            slots: slots,
            enterSandbox: EnterSandboxAsync);

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
    /// once, recorded as unavailable), and <c>risk-threshold</c> reads CodeGraph's blast radius and hotspots through the same client
    /// (sc-25709; without a CodeGraph token there is none, and the check fails wherever a tier requires it).
    /// </summary>
    internal static GateStage CreateGate(FactoryOptions options, IGateGitHub github, IReviewer reviewer, GitWorkspace workspaces,
        WorkerSlots? slots, ICodeGraphOverlays? codeGraph = null) =>
        new(github, reviewer, options.CiPollInterval, options.CiTimeout, Tests: new SandboxTestRunner(workspaces, slots, options.TestTimeout),
            Overlays: new ReviewOverlays(codeGraph, options.CodeGraphOverlayTimeout),
            Risk: codeGraph is ICodeGraphRisk risk ? new CodeGraphRiskReader(risk, options.CodeGraphOverlayTimeout) : null);

    /// <summary>
    /// The sandbox readiness probe of the slot the calling flow holds (<see cref="WorkerSlots.EnsureReadyAsync"/>; a failure names
    /// the slot when there are several), a factory-wide failure (E10): a stale helper fails the factory once, not every item.
    /// </summary>
    internal static Task EnsureSandboxReadyAsync(WorkerSlots slots, WorkerAuth auth, string claudePath, CancellationToken ct) =>
        FactoryWide("the worker sandbox", async () => { await slots.EnsureReadyAsync(auth, claudePath, ct); return true; });

    /// <summary>
    /// The part of the readiness check that runs no helper (<see cref="WorkerSlots.EnsureHelpersCurrentAsync"/>: every slot's
    /// installed launch helper is readable, the current one and pinned to that slot's user; the first that is not is named by its
    /// slot), a factory-wide failure (E10). Needs no lock and kills nothing, so it runs
    /// where nothing has been claimed yet: at <c>factory work</c>'s start-up (<see cref="CheckHelperAsync"/>) and at the start of
    /// every run. Nothing to check unsandboxed.
    /// </summary>
    internal static Task EnsureHelperCurrentAsync(WorkerSlots? slots, CancellationToken ct) => slots is null
        ? Task.CompletedTask
        : FactoryWide("the worker sandbox", async () => { await slots.EnsureHelpersCurrentAsync(ct); return true; });

    /// <summary>
    /// <c>factory work</c>'s start-up check of every slot's launch helper: why an installed one cannot be used (missing, unreadable,
    /// not the current <c>scripts/factory-worker-launch</c> or pinned to another user — naming the slot when there are several —
    /// with the command that installs them all), or null. It reads the files and runs
    /// nothing — the helper's exit kills every process of the worker user, so the probe and the worker user's Claude Code version
    /// are checked in the first worker phase, under the sandbox user's lock.
    /// </summary>
    public static async Task<string?> CheckHelperAsync(WorkerSlots? slots, CancellationToken ct)
    {
        try
        {
            await EnsureHelperCurrentAsync(slots, ct);
            return null;
        }
        catch (FactoryUnavailableException ex)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }

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
    /// at once when no run holds it (a running item is stopped by its own run within about a second). An item of any source
    /// (sc-25982): Pause and Continue only write its control; Stop needs the item's own board (its source, from the registry) and
    /// GitHub, and an epic scope the board of each item whose epic the ledger does not have.
    /// </summary>
    /// <param name="sources">Tests: the registry, in place of one over <paramref name="options"/>.</param>
    /// <exception cref="MissingCredentialException">
    /// Stop of one item whose source is not configured: nothing is written (a wider Stop reports such an item as not stopped).
    /// </exception>
    public static async Task<ControlResult> ControlAsync(FactoryOptions options, string action, string scope, string by, TextWriter log, CancellationToken ct,
        WorkSourceRegistry? sources = null)
    {
        using var own = sources is null ? new WorkSourceRegistry(options) : null;
        sources ??= own!;
        if (action == "stop" && ControlScope.ItemOf(scope) is { } item)
        {
            // Before anything is written: a stop that could not reach the item's board would leave it Stopping with nothing to finish it.
            _ = sources.Of(item.Naming);
        }
        await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var stops = action == "stop" ? new FactoryItemStops(options, sources, log) : null;
        var actions = new ControlActions(Controls(options), contexts, stops, sources.Find, new PostgresRunLocks(options.LedgerConnectionString));
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
/// Production <see cref="IItemStops"/>: an <see cref="ItemStopper"/> over the Postgres ledger, the item's own work source
/// (<paramref name="sources"/>, by the naming of its id — an id taken from its ledger row or typed by a person; sc-25982) and GitHub,
/// per call. An item whose source is not configured throws (<see cref="MissingCredentialException"/>, naming the settings): it
/// keeps its Stopping control, and <see cref="ControlActions.StopAsync"/> reports it as not stopped.
/// </summary>
public sealed class FactoryItemStops(FactoryOptions options, WorkSourceRegistry sources, TextWriter log) : IItemStops
{
    public Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct) => StopAsync(sources.Of(item.Naming), item.Id, ct);

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
/// Production <see cref="Issues.ITriageRunner"/>: one triage session as a sandboxed worker, like an item's worker phase — the sandbox
/// user's lock held from the readiness check to the worktree's removal (<see cref="WorkerLock"/>, sc-25988: waited for while another
/// run's worker phase has it, e.g. a <c>factory run</c> beside <c>factory work</c>), the sandbox readiness check (a factory-wide
/// failure, E10), the router-only worker, its events stored (E7) — in a worktree whose
/// owner-side git holds a contents-read token only (<see cref="Issues.WorkerTriageRunner.TriageWorkspaceToken"/>). The worker is
/// read-only and reads only its own worktree (<see cref="WorkerTools.ReadOnly"/>: Read, Glob, Grep inside it; E4), and never runs unsandboxed (refused, factory-wide). Its
/// worktree lives under its own root (<see cref="TriageWorktrees"/>) that no item's run uses, swept of leftovers before each triage
/// (nothing resumes a triage; what another live triage owns — its issue item's run lock is held — is kept, sc-25987), and is not
/// shared for writing: the worker user reads it through the work root's inherited read entry (<c>setup-worker-user.sh</c>) and
/// cannot write it. With several worker slots (sc-25990) the seat is the slot's: its worktree root (<see cref="TriageWorktreesOf"/>)
/// and its MCP session directory (<see cref="Mcp.McpProxySessions.DirectoryOf"/>) are that slot's own, and its two sweeps are of
/// those two directories only — a triage and a planner in two slots at once never sweep or use each other's.
/// </summary>
public sealed class SandboxTriageRunner(FactoryOptions options, TextWriter log) : Issues.ITriageRunner
{
    /// <summary>The work root's directory of triage worktrees, apart from the items' <c>worktrees</c> (slot 1's; slot n's: <see cref="TriageWorktreesOf"/>).</summary>
    public const string TriageWorktrees = "triage-worktrees";

    /// <summary>Worker slot <paramref name="slot"/>'s directory of triage worktrees (sc-25990): <c>triage-worktrees</c> for slot 1, as before slots, then <c>triage-worktrees-2</c>, ….</summary>
    public static string TriageWorktreesOf(int slot) => WorkerSlots.DirectoryOf(TriageWorktrees, slot);

    public async Task<WorkerResult> RunAsync(WorkItem item, RepoRef repo, string prompt, Func<string, CancellationToken, Task> onSession,
        Func<string, string, CancellationToken, Task> onTaint, CancellationToken ct)
    {
        var routerKey = options.RouterKey;
        var slots = options.WorkerSlots;
        if (slots is null)
        {
            // factory work refuses to start like this (Program); a triage reached any other way is refused here, factory-wide.
            var refusal = Issues.IssueIntake.UnsandboxedRefusal(null, [repo])!;
            throw new FactoryUnavailableException($"the issue triage: {refusal}", new InvalidOperationException(refusal));
        }
        // The whole seat is one sandbox user's — any free slot's (sc-25989): nothing resumes a triage and its worktree is shared
        // with no worker for writing, so it needs no slot of its own and none is recorded — the probe, the sweeps (deletions as
        // that user), the session, the worktree's removal.
        // The wait for it is never blind: logged when it starts and every WorkerLock.ReportEvery after, ended by the host's
        // shutdown, and ended too once a control holds the issue's item (a Pause of the factory or the item, the usage pause, the
        // freeze, a Stop — what the intake checks before it starts a triage): the triage is then put off, not failed
        // (TriageDeferredException), and the intake takes the issue up again on a later poll.
        var controls = FactoryRunner.Controls(options);
        var waiting = false;
        return await slots.HoldAsync(null, slot => RunLockedAsync(slots, slot, routerKey, item, repo, prompt, onSession, onTaint, ct), ct, async c =>
        {
            if (!waiting)
            {
                waiting = true;
                log.WriteLine(slots.Count > 1
                    ? $"[lock] {item.ExternalId}: its triage is waiting for a free worker slot (other runs' worker phases hold all {slots.Count})"
                    : $"[lock] {item.ExternalId}: its triage is waiting for the sandbox user (another run's worker phase holds {slots.All[0].Lock?.Path})");
            }
            if (await controls.EffectiveAsync(item.ExternalId, null, c) is var state and not ControlState.Running)
            {
                throw new Issues.TriageDeferredException(
                    $"the triage of {item.ExternalId} was waiting for the sandbox user when a control ({state}) held it; it is taken up again once nothing does");
            }
        }, RunPipeline.DefaultControlPollInterval, report: log.WriteLine);
    }

    private async Task<WorkerResult> RunLockedAsync(WorkerSlots slots, WorkerSlot slot, string routerKey, WorkItem item, RepoRef repo, string prompt,
        Func<string, CancellationToken, Task> onSession, Func<string, string, CancellationToken, Task> onTaint, CancellationToken ct)
    {
        await FactoryRunner.EnsureSandboxReadyAsync(slots, options.WorkerAuth, options.ClaudePath, ct);
        using var githubHttp = OutboundHttp.GitHubApi();
        using var routerHttp = OutboundHttp.RouterApi(options.RouterBaseUrl);
        var app = new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System);
        // The seat is the held slot's (sc-25990): its own root of triage worktrees, its own directory of session files.
        var seat = SlotSeat.Of(options.WorkRoot, TriageWorktrees, slots, slot);
        GitWorkspace SeatsIn(string worktrees) => new(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (r, c) => (await app.CreateInstallationTokenAsync(r, c, Issues.WorkerTriageRunner.TriageWorkspaceToken)).Token, sandbox: slots,
            worktreesDirectory: worktrees, shareWithWorker: false);
        var workspaces = SeatsIn(seat.Worktrees);
        // Whatever a crashed or unstoppable triage left in this slot's root is swept (nothing resumes a triage), this issue's own
        // included: the intake holds its item's run lock around this call. Another live triage's worktree and session file are its
        // item's and are kept, and another slot's directories are that slot's seats' to sweep.
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        var live = Leftovers(db, options, item);
        try
        {
            await workspaces.SweepOrphansAsync((_, _) => Task.FromResult(false), live, ct);
            // The one thing of another slot's root this seat touches: a worktree an earlier, dead triage of this same issue left
            // there (a triage takes any free slot, and the clone would refuse this one's branch while that worktree has it).
            await RemoveOwnLeftoversAsync(SlotSeat.OthersWorktrees(TriageWorktrees, slots, slot).Select(SeatsIn),
                ItemNaming.ParseAny(item.ExternalId), live, ct);
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
        await using var proxy = await StartMcpProxyAsync(upstreams, seat.Sessions, live, ct);
        var tools = TriageTools(upstreams, proxy is not null);
        var worker = new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, routerKey, options.WorkerAuth, options.WorkerTimeout, slots,
            pauseFlagDirectory: options.PauseFlagDirectory, tools: tools);
        var sessions = new SessionRecorder(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)),
            new RouterClient(routerHttp, routerKey), TimeProvider.System, log, costSettleDelay: options.CostSettleDelay);
        return await new Issues.WorkerTriageRunner(workspaces, worker, sessions, log,
            proxy is null ? null : new Mcp.McpProxySessions(proxy, seat.Sessions, seat.User, owner: ItemNaming.ParseAny(item.ExternalId),
                restrict: seat.Restrict))
            .RunAsync(item, repo, prompt, onSession, onTaint, ct);
    }

    /// <summary>
    /// How <paramref name="slot"/>'s MCP session directory is made its user's alone to read (sc-25990), or null with one slot:
    /// the directory is then as it was before slots (every sandbox user there is — the one — reads it through the work root's
    /// inherited entry, E9).
    /// </summary>
    internal static Func<string, CancellationToken, Task>? SessionDirectoryRestriction(WorkerSlots slots, WorkerSlot slot) =>
        slots.Count > 1 ? slot.Sandbox.RestrictToWorkerAsync : null;

    /// <summary>
    /// Removes from each of <paramref name="others"/> (the other slots' seat roots) what an earlier, dead session of
    /// <paramref name="self"/> left there, and nothing else: every other name is kept. <paramref name="self"/>'s run lock is the
    /// asking run's (<paramref name="live"/>), and a seat's worktree is shared with no worker for writing, so no slot's user wrote it.
    /// </summary>
    internal static async Task RemoveOwnLeftoversAsync(IEnumerable<GitWorkspace> others, ItemRef? self, LiveRuns live, CancellationToken ct)
    {
        if (self is null)
        {
            return;
        }
        foreach (var other in others)
        {
            await other.SweepOrphansAsync((name, _) => Task.FromResult(ItemNaming.OwnerOfLeftover(name) != self), live, ct);
        }
    }

    /// <summary>
    /// Whose leftovers a seat's sweep may delete (sc-25987), asked by a session of <paramref name="item"/> whose run holds that
    /// item's run lock: its own (an earlier, dead run's), and any other item's that no live run holds the run lock of.
    /// </summary>
    internal static LiveRuns Leftovers(LedgerDbContext db, FactoryOptions options, WorkItem item) =>
        new(new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString), ItemNaming.ParseAny(item.ExternalId));

    /// <summary>
    /// The triage worker's tools: <see cref="WorkerTools.ReadOnly"/>, plus (with the proxy) exactly the triage profile's MCP tools
    /// (<see cref="Mcp.McpProfile.Triage"/>) — its <c>--allowedTools</c> names no other MCP tool.
    /// </summary>
    internal static WorkerTools TriageTools(Mcp.McpUpstreams upstreams, bool proxied) =>
        proxied ? WorkerTools.ReadOnly.WithMcp(Mcp.McpProfile.Triage.ToolRules(upstreams)) : WorkerTools.ReadOnly;

    /// <summary>
    /// Sweeps <paramref name="configs"/> of a crashed process's session files (a live run's are kept: <paramref name="live"/>) and
    /// starts the loopback MCP proxy when CodeGraph is configured (else null). Either failing is factory-wide
    /// (<see cref="FactoryUnavailableException"/>, E10), never the issue's.
    /// </summary>
    internal static async Task<Mcp.McpProxy?> StartMcpProxyAsync(Mcp.McpUpstreams upstreams, string configs, LiveRuns live, CancellationToken ct,
        Func<Mcp.McpUpstreams, CancellationToken, Task<Mcp.McpProxy>>? start = null)
    {
        try
        {
            await Mcp.McpProxySessions.SweepAsync(configs, live, ct);
            return upstreams.CodeGraph is null ? null : await (start ?? Mcp.McpProxy.StartAsync)(upstreams, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"the MCP proxy: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// The directories of one read-only seat — a triage's or a planner's — and whose they are (sc-25990): everything the two
/// production runners (<see cref="SandboxTriageRunner"/>, <see cref="SandboxPlanRunner"/>) know about where a seat lives comes
/// from <see cref="Of"/>, given the slot the seat runs in, so neither can sweep, use or open to its worker another slot's.
/// </summary>
/// <param name="Worktrees">The seat's root of worktrees under the work root (<c>triage-worktrees</c>, <c>plan-worktrees-2</c>, …).</param>
/// <param name="Sessions">The seat's directory of MCP session files (<see cref="Mcp.McpProxySessions.DirectoryOf"/>).</param>
/// <param name="User">The sandbox user the session runs as, which must be able to read its session file; null unsandboxed.</param>
/// <param name="Restrict">How <paramref name="Sessions"/> is made <paramref name="User"/>'s alone to read (<see cref="SandboxTriageRunner.SessionDirectoryRestriction"/>); null with one slot and unsandboxed.</param>
internal sealed record SlotSeat(string Worktrees, string Sessions, string? User, Func<string, CancellationToken, Task>? Restrict)
{
    /// <summary>
    /// The seat of <paramref name="slot"/> for the kind of seat whose slot-1 root is <paramref name="worktrees"/>; with no slot
    /// (unsandboxed, <c>Worker:RunAs=none</c>) the directories are slot 1's, where they were before slots, and nobody's alone.
    /// </summary>
    public static SlotSeat Of(string workRoot, string worktrees, WorkerSlots? slots, WorkerSlot? slot) => new(
        WorkerSlots.DirectoryOf(worktrees, slot?.Number ?? 1),
        Mcp.McpProxySessions.DirectoryOf(workRoot, slot?.Number ?? 1),
        slot?.User,
        slots is null || slot is null ? null : SandboxTriageRunner.SessionDirectoryRestriction(slots, slot));

    /// <summary>The same kind of seat's roots in every slot but <paramref name="slot"/> (what a triage looks through for its own issue's leftover, and nothing else).</summary>
    public static IEnumerable<string> OthersWorktrees(string worktrees, WorkerSlots slots, WorkerSlot slot) =>
        slots.All.Where(other => other.Number != slot.Number).Select(other => WorkerSlots.DirectoryOf(worktrees, other.Number));
}

/// <summary>
/// Production <see cref="Plans.IPlanRunner"/> (sc-25870, E2): the planner's seat inside an item's run, whose Plan step holds the sandbox
/// user's lock around it and has checked the sandbox (a worker phase, sc-25988; the seat takes the lock itself, waiting, if a caller does not hold it). Confined like the triage worker (<see cref="SandboxTriageRunner"/>): a read-only worker
/// (<see cref="WorkerTools.ReadOnly"/>: Read, Glob, Grep inside its own worktree, no settings file loaded) in a worktree under its own
/// root (<see cref="PlanWorktrees"/>, never the items' <c>worktrees</c>) whose owner-side git holds a contents-read token only and which
/// is not shared with the worker user for writing; and, when an upstream is configured, the loopback MCP proxy this process hosts for
/// the session, granted exactly <see cref="Mcp.McpProfile.Planner"/> (the allowlisted CodeGraph tools and Kanban read tools) over the
/// plan's repository set (sc-25874: several repositories give a seat of one checkout each and a grant over them all). The
/// upstream tokens stay here; the session gets a credential revoked when it ends, in a file (never argv). Sandboxed, leftovers of a
/// crashed run (plan worktrees and seats, session files) are swept first — this plan item's own, and any other item's whose run
/// lock no live run holds (sc-25987: another live planner's seat or a live triage's session file is kept); unsandboxed
/// (<c>Worker:RunAs=none</c>, development only) nothing is swept, as before. With several worker slots (sc-25990) the seat's
/// root (<see cref="PlanWorktreesOf"/>) and its MCP session directory are its slot's own — the plan item's slot, so a re-run
/// finds what an earlier run of it left — and the two sweeps are of those two directories only.
/// </summary>
public sealed class SandboxPlanRunner(FactoryOptions options, WorkerSlots? slots, GitHubApp app, Mcp.McpUpstreams upstreams, TextWriter log)
    : Plans.IPlanRunner
{
    /// <summary>The work root's directory of plan worktrees, apart from the items' <c>worktrees</c> and the triage's (slot 1's; slot n's: <see cref="PlanWorktreesOf"/>).</summary>
    public const string PlanWorktrees = "plan-worktrees";

    /// <summary>Worker slot <paramref name="slot"/>'s directory of plan worktrees and seats (sc-25990): <c>plan-worktrees</c> for slot 1, as before slots, then <c>plan-worktrees-2</c>, ….</summary>
    public static string PlanWorktreesOf(int slot) => WorkerSlots.DirectoryOf(PlanWorktrees, slot);

    public Task<T> RunAsync<T>(WorkItem item, IReadOnlyList<RepoRef> repos, Func<Plans.PlannerSeat, CancellationToken, Task<T>> session,
        CancellationToken ct) =>
        slots is null
            ? RunLockedAsync(null, item, repos, session, ct)
            : slots.HoldAsync(null, slot => RunLockedAsync(slot, item, repos, session, ct), ct);

    /// <param name="slot">The slot the seat is in: the plan item's, which its Plan step holds (sc-25989); null unsandboxed.</param>
    private async Task<T> RunLockedAsync<T>(WorkerSlot? slot, WorkItem item, IReadOnlyList<RepoRef> repos,
        Func<Plans.PlannerSeat, CancellationToken, Task<T>> session, CancellationToken ct)
    {
        var sandbox = slot?.Sandbox;
        // The seat's two directories are its slot's own (sc-25990); unsandboxed, and for slot 1, they are where they always were.
        var seat = SlotSeat.Of(options.WorkRoot, PlanWorktrees, slots, slot);
        // The token is minted per repository, contents-read only: a seat of several repositories fetches each with its own.
        var workspaces = new GitWorkspace(options.WorkRoot, GitWorkspace.GitHubRemote,
            async (r, c) => (await app.CreateInstallationTokenAsync(r, c, Issues.WorkerTriageRunner.TriageWorkspaceToken)).Token, sandbox: slots,
            worktreesDirectory: seat.Worktrees, shareWithWorker: false);
        Mcp.McpProxy? started;
        try
        {
            if (sandbox is not null)
            {
                // Nothing resumes a planner session: whatever a crashed or unstoppable one left is swept, unless a live run owns it.
                await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
                var live = SandboxTriageRunner.Leftovers(db, options, item);
                await workspaces.SweepOrphansAsync((_, _) => Task.FromResult(false), live, ct);
                await Mcp.McpProxySessions.SweepAsync(seat.Sessions, live, ct);
            }
            started = upstreams.All.Count == 0 ? null : await Mcp.McpProxy.StartAsync(upstreams, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FactoryUnavailableException($"the planner's set-up: {ex.Message}", ex);
        }
        await using var proxy = started;
        var worker = new ClaudeWorker(options.ClaudePath, options.RouterBaseUrl, options.RouterKey, options.WorkerAuth, options.WorkerTimeout, slots,
            pauseFlagDirectory: options.PauseFlagDirectory, tools: PlannerTools(upstreams, proxy is not null));
        return await new Plans.WorkerPlanRunner(workspaces, worker, log,
            proxy is null ? null : new Mcp.McpProxySessions(proxy, seat.Sessions, seat.User, owner: ItemNaming.ParseAny(item.ExternalId),
                restrict: seat.Restrict))
            .RunAsync(item, repos, session, ct);
    }

    /// <summary>
    /// The planner's tools: <see cref="WorkerTools.ReadOnly"/>, plus (with the proxy) exactly the planner profile's MCP tools on the
    /// configured upstreams (<see cref="Mcp.McpProfile.Planner"/>) — its <c>--allowedTools</c> names no other MCP tool.
    /// </summary>
    internal static WorkerTools PlannerTools(Mcp.McpUpstreams upstreams, bool proxied) =>
        proxied ? WorkerTools.ReadOnly.WithMcp(Mcp.McpProfile.Planner.ToolRules(upstreams)) : WorkerTools.ReadOnly;
}

/// <summary>The intake loop's runner: the production pipeline over the Postgres ledger, for one work source's items.</summary>
public sealed class FactoryItemRunner(FactoryOptions options, IWorkSource source, TextWriter log) : IItemRunner
{
    public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
    {
        // `factory work` migrated the ledger (LedgerMigrations) before the host started.
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), source.Naming, ct, FactoryRunner.Controls(options));
    }

    public async Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), source.Naming, ct, FactoryRunner.Controls(options));
    }

    public Task<RunOutcome> RunAsync(int id, CancellationToken ct) => FactoryRunner.RunAsync(options, source, id, ignoreScope: false, log, ct);

    public async Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.GiveUpAsync(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            id, reason, log, ct);
    }

    public async Task<string?> ClaimLostAsync(int id, string reason, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));
        return await RunPipeline.ClaimLostAsync(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(options.LedgerConnectionString),
            id, reason, log, ct, FactoryRunner.Controls(options));
    }
}
