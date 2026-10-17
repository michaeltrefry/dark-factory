using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Shared plumbing of the live <c>factory work</c> acceptance tests (AT2–AT5, AT7; docs/acceptance.md). Each test runs
/// against a throwaway ledger database and its own work root under the configured one, so it never reads or writes the
/// owner's real ledger, controls or worktrees; it holds the configured work root's <see cref="WorkerLock"/> — every worker
/// slot's (sc-25989) — for its whole run, so no real run's worker phase can share a single-tenant sandbox user with it (a real
/// run waits at its worker phases meanwhile; the test skips if one holds a lock when it starts).
/// </summary>
internal sealed class E2e : IAsyncDisposable
{
    public const string TestPassword = "dark-factory-e2e-dashboard";

    private readonly NpgsqlConnectionStringBuilder _admin;
    private readonly string _database;
    private readonly IDisposable? _realRootLock;
    internal EvaluationWorkRoot? EvaluationRoot { get; private set; }

    private E2e(NpgsqlConnectionStringBuilder admin, string database, IDisposable? realRootLock, string workRoot)
    {
        (_admin, _database, _realRootLock, WorkRoot) = (admin, database, realRootLock, workRoot);
        Ledger = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString;
    }

    /// <summary>The throwaway ledger's connection string.</summary>
    public string Ledger { get; }

    /// <summary>This test's own work root (clones, worktrees, run lock, pause flags).</summary>
    public string WorkRoot { get; }

    /// <summary>The dashboard password hash the throwaway host logs in with (never the owner's keychain entry).</summary>
    public static readonly Lazy<string> PasswordHash = new(() => DashboardAuth.HashPassword(TestPassword));

    /// <summary>
    /// The credential of the work source a test works an item of, and nothing of the other sources (sc-25982): a Kanban test
    /// runs on a machine with no Shortcut token, and the reverse.
    /// </summary>
    public static void RequireSource(ItemNaming source)
    {
        if (source == ItemNaming.Shortcut)
        {
            Harness.RequireSecret(o => o.ShortcutApiToken);
        }
        else if (source == ItemNaming.Kanban)
        {
            Harness.RequireSecret(o => o.KanbanWriteToken);
        }
        // A GitHub issue's item needs the workers' GitHub App, which every test needs.
    }

    /// <summary>Checks the live prerequisites, takes the configured work root's run lock and creates the throwaway ledger.</summary>
    /// <param name="source">The work source the test's item is of (default: a Shortcut story): only its credential is asked for.</param>
    public static async Task<E2e> StartAsync(string prefix, ItemNaming? source = null)
    {
        Harness.RequireOptIn();
        RequireSource(source ?? ItemNaming.Shortcut);
        Harness.RequireSecret(o => o.RouterKey);
        Harness.RequireSecret(o => o.GitHubAppId);
        Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        // `factory work` runs the whole pipeline, review and merge gate included, which needs the gate's own App.
        Harness.RequireSecret(o => o.GitHubGateAppId);
        Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        Harness.RequireReviewSettings();
        Harness.RequireClaude();
        await Harness.RequireRouterAsync();
        // P4b-AT7: the machine's own settings (config and env) are the defaults before any test overrides them. The keychain's
        // write token is not read here: an AT7 run's options hide it (Options).
        var configured = FactoryOptions.LoadConfiguration();
        RequireDefaultsWhenAsked(configured, new FactoryOptions(configured, new OverlaySecrets(new NoSecrets(), new Dictionary<string, string>())));

        return await CreateIsolatedAsync(prefix, AcquireRealLocks());
    }

    /// <summary>Synthetic local evaluation: router, real sandbox and isolated ledger only; no board or gate Apps.</summary>
    public static async Task<E2e> StartEvaluationAsync(string prefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Harness.RequireOptIn();
        Harness.RequireSecret(o => o.RouterKey);
        using (var http = OutboundHttp.RouterApi(Harness.Options.RouterBaseUrl, TimeSpan.FromSeconds(3)))
        {
            await EvaluationSetup.RequireRouterAsync(async token =>
            {
                using var response = await http.GetAsync(Harness.Options.RouterBaseUrl, token);
            }, ct); // Evaluation failure, not shared harness Skip; bounded prerequisite, no inference.
        }
        ct.ThrowIfCancellationRequested();
        return await EvaluationSetup.StartAsync(Harness.Options, AcquireRealLocks, async isolated =>
        {
            // Unlike the full-pipeline harness, take every sandbox lock BEFORE probing a helper (which sweeps its user).
            // Probe through the isolated root's sandbox locks, not the already exclusively held real-root files.
            // The real-root locks stay held too, excluding every real worker for the entire arm.
            var options = isolated.Options();
            await isolated.EvaluationRoot!.RunAsync(async () =>
            {
                foreach (var slot in options.WorkerSlots!.All)
                {
                    ct.ThrowIfCancellationRequested();
                    await slot.Sandbox.EnsureReadyAsync(options.WorkerAuth, options.ClaudePath, ct);
                }
            });
        }, held => CreateIsolatedAsync(prefix, held, ct, evaluation: true), ct);
    }

    private static IDisposable AcquireRealLocks()
    {
        RealLocks? realLock = null;
        if (Harness.Options.WorkerSandbox is not null)
        {
            realLock = new RealLocks();
            try
            {
                foreach (var slot in Harness.Options.WorkerSlots!.All)
                {
                    realLock.Add(WorkerLock.Acquire(Harness.Options.WorkRoot, slot.Number));
                }
            }
            catch (InvalidOperationException ex)
            {
                realLock.Dispose();
                Assert.Skip($"A real factory run's worker phase holds the work root: stop `factory work`/`factory run` first. {ex.Message}");
            }
        }
        return realLock ?? (IDisposable)new RealLocks();
    }

    private static async Task<E2e> CreateIsolatedAsync(string prefix, IDisposable realLock, CancellationToken ct = default, bool evaluation = false)
    {
        var realRoot = Harness.Options.WorkRoot;
        var admin = new NpgsqlConnectionStringBuilder(Harness.Options.LedgerConnectionString);
        var database = $"{prefix}_{Guid.NewGuid():N}";
        try
        {
            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", conn);
            await create.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            realLock?.Dispose();
            if (evaluation)
            { throw new InvalidOperationException($"Evaluation ledger prerequisite unavailable ({ex.GetType().Name}); campaign incomplete (no automatic retry)."); }
            Assert.Skip($"Ledger Postgres not reachable ({ex.Message}); run `docker compose up -d`.");
        }
        var workRoot = Path.Combine(realRoot, "e2e", database);
        var e2e = new E2e(admin, database, realLock, workRoot);
        try
        {
            // CREATE DATABASE above is uncancelled until ownership is established, so a cancelled command cannot
            // leave an ambiguously-created database. Everything after ownership cancels through the usual cleanup.
            if (evaluation) { e2e.EvaluationRoot = EvaluationWorkRoot.Create(Path.Combine(realRoot, "e2e"), database); }
            ct.ThrowIfCancellationRequested();
            await LedgerMigrations.MigrateAsync(e2e.Ledger, ct);
            await using (var ledger = new NpgsqlConnection(e2e.Ledger))
            {
                await ledger.OpenAsync(ct);
                await using var restart = new NpgsqlCommand(
                    $"ALTER TABLE kanban_items ALTER COLUMN \"Id\" RESTART WITH {KanbanKeyStart(DateTimeOffset.UtcNow)}", ledger);
                await restart.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            await e2e.DisposeAsync();
            throw;
        }
        return e2e;
    }

    /// <summary>
    /// The first Kanban key of a throwaway ledger (sc-25992). A Kanban item's branch is <c>factory/kb-&lt;key&gt;</c>, and keys are
    /// per ledger: started at 1, every live run would name its first item <c>kb-1</c> and push to the branch, and adopt the open
    /// pull request, that an earlier run left on the sandbox (P4b-AT4 leaves a draft). So each throwaway ledger starts its keys
    /// at the seconds since 2026-01-01 UTC — distinct for runs started a second apart, and an int until 2094.
    /// </summary>
    internal static int KanbanKeyStart(DateTimeOffset now) =>
        checked((int)Math.Max(1, (now - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / TimeSpan.TicksPerSecond));

    /// <summary>
    /// The watch scope from <c>FACTORY_E2E_WATCH_TEAM</c> / <c>FACTORY_E2E_WATCH_EPIC</c> (at least one), as config keys.
    /// </summary>
    public static Dictionary<string, string?> WatchScope()
    {
        var team = Environment.GetEnvironmentVariable("FACTORY_E2E_WATCH_TEAM");
        var epic = Environment.GetEnvironmentVariable("FACTORY_E2E_WATCH_EPIC");
        if (string.IsNullOrWhiteSpace(team) && string.IsNullOrWhiteSpace(epic))
        {
            Assert.Skip("Set FACTORY_E2E_WATCH_TEAM (team mention name or id) and/or FACTORY_E2E_WATCH_EPIC (epic id): the watch scope of the test host.");
        }
        return new() { ["Shortcut:Watch:Teams"] = team ?? "", ["Shortcut:Watch:Epics"] = epic ?? "" };
    }

    /// <summary>A To Do story named by <paramref name="variable"/>.</summary>
    public static int Story(string variable, string purpose)
    {
        var value = Harness.RequireEnv(variable, purpose);
        Assert.True(StoryId.TryParse(value, out var id), $"{variable}='{value}' is not a story id");
        return id;
    }

    /// <summary>
    /// The configured options (env, user secrets, the owner's keychain for router/Shortcut/GitHub) with this test's
    /// ledger, work root, a free dashboard port, a short poll, and the test dashboard password hash layered on top.
    /// </summary>
    /// <param name="without">Keychain accounts the options must not find (e.g. the Shortcut token, for a host that must start without one).</param>
    public FactoryOptions Options(IDictionary<string, string?>? overrides = null, params string[] without)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = Ledger,
            ["Factory:WorkRoot"] = WorkRoot,
            ["Factory:HostPort"] = "0",
            ["Intake:PollSeconds"] = "10",
            ["Usage:PollSeconds"] = "10",
            // Blank, so the overlay secret store's test hash below is what the host checks logins against.
            ["Dashboard:PasswordHash"] = "",
        };
        foreach (var (k, v) in overrides ?? new Dictionary<string, string?>())
        {
            values[k] = v;
        }
        var config = new ConfigurationBuilder()
            .AddConfiguration(FactoryOptions.LoadConfiguration())
            .AddInMemoryCollection(values)
            .Build();
        var secrets = new Dictionary<string, string> { [SecretAccounts.DashboardPasswordHash] = PasswordHash.Value };
        foreach (var account in without)
        {
            secrets[account] = ""; // blank is missing
        }
        if (AskedForDefaults())
        {
            // P4b-AT7: a Kanban write token in the keychain would give the Kanban source an intake lane; the run hides it, as a
            // Kanban test hides the Shortcut token. One from config or the environment still fails the guard below.
            secrets[SecretAccounts.KanbanWriteToken] = "";
        }
        var options = new FactoryOptions(config, new OverlaySecrets(new MacKeychain(), secrets));
        RequireDefaultsWhenAsked(config, options);
        return options;
    }

    /// <summary>
    /// The environment variable that makes a run a P4b-AT7 run (docs/acceptance.md, "P4b-AT7 — defaults"): set to <c>1</c>, every
    /// live test's options must carry Phase 4b's defaults (<see cref="DefaultsProblems"/>), or the test fails before it starts.
    /// </summary>
    public const string DefaultsVariable = "FACTORY_E2E_P4B_DEFAULTS";

    /// <summary>
    /// What makes <paramref name="options"/> differ from Phase 4b's defaults (sc-25992, P4b-AT7: "With default settings the Phase
    /// 1–4a live tests behave as before"): a watched Kanban project, a Kanban web URL, a Kanban write token (from config, env or
    /// keychain: with one the Kanban source has an intake lane), <c>Worker:Slots</c> other than 1, <c>Intake:MaxConcurrentRuns</c>
    /// other than 1. <c>Kanban:Token</c> and <c>Kanban:McpUrl</c> are Phase 3's (the reviewers' Kanban reads), not Phase 4b's, and
    /// stay allowed. Empty when the options are the defaults.
    /// </summary>
    public static List<string> DefaultsProblems(IConfiguration config, FactoryOptions options)
    {
        var problems = new List<string>();
        foreach (var key in new[] { "Kanban:Watch:Projects", "Kanban:WebUrl" })
        {
            if (!string.IsNullOrWhiteSpace(config[key]))
            {
                problems.Add($"{key} is set");
            }
        }
        if (options.TryGet(o => o.KanbanWriteToken, out _))
        {
            problems.Add("a Kanban write token is configured (Kanban:WriteToken, FACTORY_KANBAN_WRITE_TOKEN or keychain account kanban-write-token)");
        }
        if (options.WorkerSlotCount != 1)
        {
            problems.Add($"Worker:Slots is {options.WorkerSlotCount}, not 1");
        }
        if (options.MaxConcurrentRuns != 1)
        {
            problems.Add($"Intake:MaxConcurrentRuns is {options.MaxConcurrentRuns}, not 1");
        }
        return problems;
    }

    private static bool AskedForDefaults(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DefaultsVariable) == "1";

    /// <summary>Under <see cref="DefaultsVariable"/>=1, fails the test unless its options are the defaults (<see cref="DefaultsProblems"/>).</summary>
    public static void RequireDefaultsWhenAsked(IConfiguration config, FactoryOptions options,
        Func<string, string?>? environment = null)
    {
        if (!AskedForDefaults(environment))
        {
            return;
        }
        var problems = DefaultsProblems(config, options);
        Assert.True(problems.Count == 0,
            $"{DefaultsVariable}=1 is a P4b-AT7 run, which must use the default settings, but {string.Join("; ", problems)}. Unset them for this run.");
    }

    /// <summary>The env a child <c>factory work</c> needs for the same settings (<c>__</c> separators).</summary>
    public Dictionary<string, string> ChildEnvironment(IDictionary<string, string?> scope, int port) =>
        new Dictionary<string, string?>(scope)
        {
            ["ConnectionStrings:Ledger"] = Ledger,
            ["Factory:WorkRoot"] = WorkRoot,
            ["Factory:HostPort"] = port.ToString(),
            ["Intake:PollSeconds"] = "10",
            ["Dashboard:PasswordHash"] = PasswordHash.Value,
        }.ToDictionary(kv => kv.Key.Replace(":", "__"), kv => kv.Value ?? "");

    public LedgerDbContext Db() => new(LedgerDbContext.PostgresOptions(Ledger));

    /// <summary>The item's transition rows (no checkpoints), oldest first; empty while the ledger does not know it.</summary>
    public async Task<List<LedgerEntry>> TransitionsAsync(int storyId, CancellationToken ct) =>
        (await HistoryAsync(storyId, ct)).Where(e => e.Step is null).ToList();

    public async Task<List<LedgerEntry>> HistoryAsync(int storyId, CancellationToken ct)
    {
        await using var db = Db();
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == StoryId.Format(storyId), ct);
        return item is null
            ? []
            : await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        async Task DropLedgerAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_admin.ConnectionString);
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }
        if (EvaluationRoot is { } owned)
        {
            await owned.DisposeAsync(DropLedgerAsync, _realRootLock);
        }
        else
        {
            try { await DropLedgerAsync(); }
            finally { _realRootLock?.Dispose(); }
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 2 s until it holds; fails naming <paramref name="what"/> after <paramref name="timeout"/>.</summary>
    public static async Task WaitForAsync(string what, TimeSpan timeout, Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Timed out after {timeout} waiting for {what}.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    // ---- Shortcut and GitHub readback (read-only) ----

    /// <summary>The story's workflow state name and external links, read straight from the Shortcut API.</summary>
    public static async Task<(string State, List<string> Links)> StoryAsync(int storyId, CancellationToken ct)
    {
        using var http = OutboundHttp.ShortcutApi();
        http.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        var story = await http.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        var workflow = await http.GetFromJsonAsync<JsonElement>($"workflows/{story.GetProperty("workflow_id").GetInt64()}", ct);
        var stateId = story.GetProperty("workflow_state_id").GetInt64();
        var state = workflow.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("id").GetInt64() == stateId).GetProperty("name").GetString()!;
        var links = story.GetProperty("external_links").EnumerateArray().Select(l => l.GetString()!).ToList();
        return (state, links);
    }

    /// <summary>The text of every comment on the story that is not deleted, oldest first, read straight from the Shortcut API.</summary>
    public static async Task<List<string>> StoryCommentsAsync(int storyId, CancellationToken ct)
    {
        using var http = OutboundHttp.ShortcutApi();
        http.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        var story = await http.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        return [.. story.GetProperty("comments").EnumerateArray()
            .Where(c => !(c.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True))
            .Select(c => c.GetProperty("text").GetString() ?? "")];
    }

    /// <summary>The router cost of each of the item's worker sessions, as <see cref="WorkLedger.SessionCostsAsync"/> reads them.</summary>
    public async Task<IReadOnlyList<decimal?>> SessionCostsAsync(int storyId, CancellationToken ct)
    {
        await using var db = Db();
        var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(storyId), ct);
        return await new WorkLedger(db, TimeProvider.System).SessionCostsAsync(item, ct);
    }

    /// <summary>Every PR (any state) whose head is the item's <c>factory/sc-&lt;id&gt;</c> branch.</summary>
    public static Task<List<JsonElement>> PullRequestsAsync(RepoRef repo, int storyId, CancellationToken ct) =>
        PullRequestsAsync(repo, StoryId.BranchName(storyId), ct);

    /// <summary>Every PR (any state) whose head is <paramref name="branch"/> (e.g. a plan's changeset branch).</summary>
    public static async Task<List<JsonElement>> PullRequestsAsync(RepoRef repo, string branch, CancellationToken ct)
    {
        using var github = OutboundHttp.GitHubApi();
        var app = new GitHubApp(github, Harness.Options.GitHubAppId, Harness.Options.GitHubAppPrivateKeyPem, TimeProvider.System);
        var token = (await app.CreateInstallationTokenAsync(repo, ct)).Token;
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=all&head={Uri.EscapeDataString($"{repo.Owner}:{branch}")}",
            "Bearer", token);
        using var response = await github.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();
    }

    // ---- AT7: the router accounts for every session ----

    /// <summary>
    /// AT7: every <c>worker_sessions</c> row in <paramref name="ledger"/> started at or after <paramref name="since"/> has a
    /// Claude session id the router reports with at least one request (recorded) and a cost &gt;= 0 (a session served on the
    /// router's local model legitimately costs $0). Returns the session ids checked.
    /// </summary>
    public static async Task<List<string>> AssertRouterAccountsForSessionsAsync(string ledger, DateTimeOffset since, CancellationToken ct)
    {
        await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(ledger));
        var sessions = await db.WorkerSessions.AsNoTracking().Where(s => s.StartedAt >= since).OrderBy(s => s.Id).ToListAsync(ct);
        Assert.NotEmpty(sessions);
        var checkedIds = new List<string>();
        foreach (var session in sessions)
        {
            Assert.False(session.ClaudeSessionId is null, $"worker_sessions row {session.Id} never got a Claude session id");
            var cost = await Harness.WaitForCostAsync(session.ClaudeSessionId!, Harness.Options.RouterKey, ct);
            Assert.True(cost is { RequestCount: > 0, ActualCostUsdMicros: >= 0 },
                $"router reports {cost?.RequestCount.ToString() ?? "nothing"} requests / {cost?.ActualCostUsdMicros.ToString() ?? "no"} cost micros for session {session.ClaudeSessionId}");
            checkedIds.Add(session.ClaudeSessionId!);
        }
        return checkedIds;
    }

    // ---- The dashboard, through its real login ----

    /// <summary>Logs in through the real login page and form; the container holds the session cookie.</summary>
    public static async Task<CookieContainer> LoginAsync(string baseAddress, CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var http = DashboardClient(baseAddress, cookies);
        var page = await http.GetStringAsync(DashboardAuth.LoginPath, ct);
        var token = WebUtility.HtmlDecode(Regex.Match(page, """<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(token), "the login page has no antiforgery token");
        using var response = await http.PostAsync(DashboardAuth.LoginPostPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = TestPassword,
            ["returnUrl"] = "",
            ["__RequestVerificationToken"] = token,
        }), ct);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString == "/",
            $"login failed: {(int)response.StatusCode} {response.Headers.Location}");
        return cookies;
    }

    public static HttpClient DashboardClient(string baseAddress, CookieContainer cookies) =>
        OutboundHttp.Dashboard(new Uri(baseAddress), cookies);

    /// <summary>
    /// Replays the session as a logged-in dashboard viewer does (the session hub's backlog) and returns the sequences
    /// received once they match every stored event of the session.
    /// </summary>
    public async Task<List<long>> ReplayAsync(string baseAddress, CookieContainer cookies, string claudeSessionId, CancellationToken ct)
    {
        await using var db = Db();
        var session = await db.WorkerSessions.AsNoTracking().SingleAsync(s => s.ClaudeSessionId == claudeSessionId, ct);
        var stored = await db.SessionEvents.AsNoTracking().Where(e => e.WorkerSessionId == session.Id).OrderBy(e => e.Sequence)
            .Select(e => e.Sequence).ToListAsync(ct);
        Assert.NotEmpty(stored);

        var received = new List<long>();
        var all = new TaskCompletionSource();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(baseAddress.TrimEnd('/') + SessionHub.Path, o => o.Cookies = cookies)
            .Build();
        connection.On<string, SessionEventMessage[]>(SessionHub.EventsMethod, (id, events) =>
        {
            lock (received)
            {
                received.AddRange(events.Select(e => e.Sequence));
                if (received.Count >= stored.Count)
                {
                    all.TrySetResult();
                }
            }
        });
        await connection.StartAsync(ct);
        await connection.InvokeAsync("JoinSession", claudeSessionId, ct);
        await all.Task.WaitAsync(TimeSpan.FromMinutes(1), ct);
        lock (received)
        {
            Assert.Equal(stored, received.Take(stored.Count));
            return [.. received];
        }
    }
}

/// <summary>The configured work root's lock of every worker slot, held together for a live test's whole run.</summary>
internal sealed class RealLocks : IDisposable
{
    private readonly List<IDisposable> _held = [];

    public void Add(IDisposable held) => _held.Add(held);

    public void Dispose() => _held.ForEach(held => held.Dispose());
}

/// <summary>Secrets from <paramref name="inner"/> (the owner's keychain) except the accounts overridden here; writes go nowhere.</summary>
/// <summary>A secret store that holds nothing (the AT7 guard's look at config and env alone).</summary>
internal sealed class NoSecrets : ISecretStore
{
    public string? Get(string account) => null;

    public void Set(string account, string value) => throw new NotSupportedException("The acceptance harness never writes the keychain.");
}

internal sealed class OverlaySecrets(ISecretStore inner, IReadOnlyDictionary<string, string> overrides) : ISecretStore
{
    public string? Get(string account) => overrides.TryGetValue(account, out var value) ? value : inner.Get(account);

    public void Set(string account, string value) => throw new NotSupportedException("The acceptance harness never writes the keychain.");
}
