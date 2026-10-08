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
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Shared plumbing of the live <c>factory work</c> acceptance tests (AT2–AT5, AT7; docs/acceptance.md). Each test runs
/// against a throwaway ledger database and its own work root under the configured one, so it never reads or writes the
/// owner's real ledger, controls or worktrees; it holds the configured work root's <see cref="WorkerLock"/> for its whole
/// run, so no real sandboxed run can share the single-tenant <c>_factory</c> user with it (it skips if one is running).
/// </summary>
internal sealed class E2e : IAsyncDisposable
{
    public const string TestPassword = "dark-factory-e2e-dashboard";

    private readonly NpgsqlConnectionStringBuilder _admin;
    private readonly string _database;
    private readonly WorkerLock? _realRootLock;

    private E2e(NpgsqlConnectionStringBuilder admin, string database, WorkerLock? realRootLock, string workRoot)
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

    /// <summary>Checks the live prerequisites, takes the configured work root's run lock and creates the throwaway ledger.</summary>
    public static async Task<E2e> StartAsync(string prefix)
    {
        Harness.RequireOptIn();
        Harness.RequireSecret(o => o.ShortcutApiToken);
        Harness.RequireSecret(o => o.RouterKey);
        Harness.RequireSecret(o => o.GitHubAppId);
        Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        // `factory work` runs the whole pipeline, review and merge gate included, which needs the gate's own App.
        Harness.RequireSecret(o => o.GitHubGateAppId);
        Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        Harness.RequireReviewPanel();
        Harness.RequireClaude();
        await Harness.RequireRouterAsync();

        var realRoot = Harness.Options.WorkRoot;
        WorkerLock? realLock = null;
        if (Harness.Options.WorkerSandbox is not null)
        {
            try
            {
                realLock = WorkerLock.Acquire(realRoot);
            }
            catch (InvalidOperationException ex)
            {
                Assert.Skip($"A real sandboxed factory run holds the work root: stop `factory work`/`factory run` first. {ex.Message}");
            }
        }
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
            Assert.Skip($"Ledger Postgres not reachable ({ex.Message}); run `docker compose up -d`.");
        }
        var workRoot = Path.Combine(realRoot, "e2e", database);
        var e2e = new E2e(admin, database, realLock, workRoot);
        await LedgerMigrations.MigrateAsync(e2e.Ledger, CancellationToken.None);
        return e2e;
    }

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
    public FactoryOptions Options(IDictionary<string, string?>? overrides = null)
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
        return new FactoryOptions(config, new OverlaySecrets(new MacKeychain(),
            new Dictionary<string, string> { [SecretAccounts.DashboardPasswordHash] = PasswordHash.Value }));
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
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var conn = new NpgsqlConnection(_admin.ConnectionString);
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }
        finally
        {
            _realRootLock?.Dispose();
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
        using var http = new HttpClient { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        http.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        var story = await http.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        var workflow = await http.GetFromJsonAsync<JsonElement>($"workflows/{story.GetProperty("workflow_id").GetInt64()}", ct);
        var stateId = story.GetProperty("workflow_state_id").GetInt64();
        var state = workflow.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("id").GetInt64() == stateId).GetProperty("name").GetString()!;
        var links = story.GetProperty("external_links").EnumerateArray().Select(l => l.GetString()!).ToList();
        return (state, links);
    }

    /// <summary>Every PR (any state) whose head is the item's <c>factory/sc-&lt;id&gt;</c> branch.</summary>
    public static async Task<List<JsonElement>> PullRequestsAsync(RepoRef repo, int storyId, CancellationToken ct)
    {
        using var github = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
        var app = new GitHubApp(github, Harness.Options.GitHubAppId, Harness.Options.GitHubAppPrivateKeyPem, TimeProvider.System);
        var token = (await app.CreateInstallationTokenAsync(repo, ct)).Token;
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=all&head={Uri.EscapeDataString($"{repo.Owner}:{StoryId.BranchName(storyId)}")}",
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
        new(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseAddress) };

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

/// <summary>Secrets from <paramref name="inner"/> (the owner's keychain) except the accounts overridden here; writes go nowhere.</summary>
internal sealed class OverlaySecrets(ISecretStore inner, IReadOnlyDictionary<string, string> overrides) : ISecretStore
{
    public string? Get(string account) => overrides.TryGetValue(account, out var value) ? value : inner.Get(account);

    public void Set(string account, string value) => throw new NotSupportedException("The acceptance harness never writes the keychain.");
}
