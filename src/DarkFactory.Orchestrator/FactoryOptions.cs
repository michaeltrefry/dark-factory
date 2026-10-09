using DarkFactory.Orchestrator.Shortcut;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator;

/// <summary>
/// Orchestrator configuration. Non-secret values come from configuration
/// (environment variables using <c>__</c> as separator, or user-secrets).
/// Secrets come from configuration, a well-known environment variable, or the
/// macOS keychain (service <c>dark-factory</c>) — never from files in the repo.
/// Secrets are resolved lazily so a command only fails on the credential it needs.
/// </summary>
public sealed class FactoryOptions(IConfiguration config, ISecretStore secrets)
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory";

    public Uri RouterBaseUrl => new(config["Router:BaseUrl"] ?? "http://localhost:8080");

    public RepoRef DefaultRepo => RepoRef.Parse(config["Factory:DefaultRepo"] ?? "michaeltrefry/dark-factory-sandbox");

    /// <summary>
    /// Clones and worktrees. Sandboxed, it must be readable by the worker user and outside both
    /// homes (the owner's is closed to the worker; the worker's own could be swapped under the
    /// owner's git), so the default is the root-anchored dir <c>setup-worker-user.sh</c> creates.
    /// </summary>
    public string WorkRoot => config["Factory:WorkRoot"]
        ?? (WorkerSandbox is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dark-factory")
            : DefaultSandboxWorkRoot);

    public const string DefaultSandboxWorkRoot = "/opt/dark-factory/work";

    /// <summary>Claude Code as the worker sees it; sandboxed, it resolves on the helper's PATH (worker's <c>~/.local/bin</c> first).</summary>
    public string ClaudePath => config["Worker:ClaudePath"] ?? "claude";

    /// <summary>
    /// <c>Worker:RunAs</c> (default <c>_factory</c>) runs workers as that macOS user through
    /// <c>Worker:LaunchHelper</c>; <c>none</c> runs them as the owner (development only).
    /// </summary>
    public Worker.WorkerSandbox? WorkerSandbox => (config["Worker:RunAs"] ?? Worker.WorkerSandbox.DefaultUser) switch
    {
        "none" => null,
        "" => throw new InvalidOperationException("Worker:RunAs must be a macOS user name or 'none'."),
        var user => new Worker.WorkerSandbox(user, config["Worker:LaunchHelper"] ?? Worker.WorkerSandbox.DefaultHelperPath),
    };

    /// <summary><c>Worker:Auth</c> = <c>router-key</c> (default) or <c>claude-login</c> (weaker, violates E5); see <see cref="Worker.WorkerAuth"/>.</summary>
    public Worker.WorkerAuth WorkerAuth => (config["Worker:Auth"] ?? "router-key") switch
    {
        "claude-login" => Worker.WorkerAuth.ClaudeLogin,
        "router-key" => Worker.WorkerAuth.RouterKey,
        var other => throw new InvalidOperationException($"Worker:Auth must be 'router-key' or 'claude-login', not '{other}'."),
    };

    /// <summary><c>Shortcut:Watch:Teams</c> / <c>Shortcut:Watch:Epics</c>; empty watches nothing.</summary>
    public WatchScope WatchScope => WatchScope.From(config);

    /// <summary>
    /// <c>GitHub:Watch:Repos</c>: the repos whose issues <c>factory work</c> triages (sc-25385), comma-separated <c>owner/name</c>
    /// (or an array section). Empty (the default) watches no issues.
    /// </summary>
    public IReadOnlyList<RepoRef> WatchedIssueRepos
    {
        get
        {
            var section = config.GetSection("GitHub:Watch:Repos");
            var raw = section.Value is { } single ? single.Split(',') : section.GetChildren().Select(c => c.Value ?? "");
            return raw.Select(v => v.Trim()).Where(v => v.Length > 0)
                .Select(v =>
                {
                    try
                    {
                        return RepoRef.Parse(v);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidOperationException($"GitHub:Watch:Repos: {ex.Message}", ex);
                    }
                })
                .DistinctBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary><c>Intake:PollSeconds</c>: how often <c>factory work</c> polls the board.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(config.GetValue("Intake:PollSeconds", 60));

    /// <summary><c>Intake:MaxItemFailures</c>: runs of one item in a row that may fail before <c>factory work</c> gives up on it (E10).</summary>
    public int MaxItemFailures => config.GetValue("Intake:MaxItemFailures", WorkSources.IntakeOptions.DefaultMaxItemFailures) is var n && n > 0
        ? n
        : throw new InvalidOperationException("Intake:MaxItemFailures must be at least 1.");

    /// <summary>
    /// The automatic freeze's thresholds (<see cref="Controls.FreezeOptions"/>): <c>Freeze:MaxConsecutiveFailures</c> (≥ 1),
    /// <c>Freeze:HotFileMerges</c> (≥ 2), <c>Freeze:HotFileWindowHours</c> (&gt; 0), <c>Freeze:CostRisingRounds</c> (≥ 1). A value
    /// out of range throws (the factory refuses to start).
    /// </summary>
    public Controls.FreezeOptions Freeze => new()
    {
        MaxConsecutiveFailures = AtLeast("Freeze:MaxConsecutiveFailures", Controls.FreezeOptions.DefaultMaxConsecutiveFailures, 1),
        HotFileMerges = AtLeast("Freeze:HotFileMerges", Controls.FreezeOptions.DefaultHotFileMerges, 2),
        HotFileWindow = config.GetValue("Freeze:HotFileWindowHours", Controls.FreezeOptions.DefaultHotFileWindowHours) is var hours && hours > 0
            ? TimeSpan.FromHours(hours)
            : throw new InvalidOperationException("Freeze:HotFileWindowHours must be more than 0."),
        CostRisingRounds = AtLeast("Freeze:CostRisingRounds", Controls.FreezeOptions.DefaultCostRisingRounds, 1),
    };

    private int AtLeast(string key, int fallback, int min) =>
        config.GetValue(key, fallback) is var n && n >= min ? n : throw new InvalidOperationException($"{key} must be at least {min}.");

    /// <summary><c>Usage:PollSeconds</c>: how often <c>factory work</c> reads the router's subscription usage.</summary>
    public TimeSpan UsagePollInterval => TimeSpan.FromSeconds(config.GetValue("Usage:PollSeconds", 60));

    /// <summary>
    /// <c>Router:CostSettleSeconds</c>: after the router first reports a session's cost, wait this long and read it once
    /// more (a session's last requests can be committed later); see <see cref="Sessions.SessionRecorder"/>.
    /// </summary>
    public TimeSpan CostSettleDelay => config.GetValue("Router:CostSettleSeconds", Sessions.SessionRecorder.DefaultCostSettleDelay.TotalSeconds) is var s && s >= 0
        ? TimeSpan.FromSeconds(s)
        : throw new InvalidOperationException("Router:CostSettleSeconds must not be negative.");

    /// <summary>Port of the <c>factory work</c> host (dashboard and session hub), on 127.0.0.1 and <see cref="DashboardBindAddress"/>.</summary>
    public int HostPort => config.GetValue("Factory:HostPort", 47822);

    /// <summary>
    /// <c>Dashboard:BindAddress</c>: one private-network address (assigned to a local interface) the host
    /// also listens on, beside 127.0.0.1; unset = loopback only. Validated by <see cref="Dashboard.DashboardBinding"/>.
    /// </summary>
    public string? DashboardBindAddress => NullIfBlank(config["Dashboard:BindAddress"]);

    /// <summary><c>Dashboard:HostName</c>: an extra Host header the dashboard answers to (e.g. a Tailscale MagicDNS name).</summary>
    public string? DashboardHostName => NullIfBlank(config["Dashboard:HostName"]);

    /// <summary>The dashboard login's password hash (<c>factory dashboard set-password</c>).</summary>
    public string DashboardPasswordHash =>
        Secret("Dashboard:PasswordHash", null, SecretAccounts.DashboardPasswordHash, "dashboard password hash (run `factory dashboard set-password`)");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public TimeSpan WorkerTimeout => TimeSpan.FromMinutes(config.GetValue("Worker:TimeoutMinutes", 30));

    /// <summary>
    /// How long a paused worker may take to reach its next tool boundary before it is stopped anyway. It must be longer
    /// than the longest tool call (<see cref="RunPipeline.LongestToolCall"/>), or a pause could kill a legitimate one mid-way.
    /// </summary>
    public TimeSpan PauseGrace
    {
        get
        {
            var grace = TimeSpan.FromSeconds(config.GetValue("Worker:PauseGraceSeconds", (int)RunPipeline.DefaultPauseGrace.TotalSeconds));
            return grace > RunPipeline.LongestToolCall
                ? grace
                : throw new InvalidOperationException(
                    $"Worker:PauseGraceSeconds must be more than {RunPipeline.LongestToolCall.TotalSeconds:0} (the longest tool call), not {grace.TotalSeconds:0}.");
        }
    }

    /// <summary>
    /// When a running worker counts as stuck and is interrupted (sc-25388, <see cref="Sessions.StuckDetector"/>):
    /// <c>Worker:StuckRepeats</c> (≥ 2, default 5) near-identical turns or cycles in a row, each at least
    /// <c>Worker:StuckSimilarity</c> (0 &lt; s ≤ 1, default 0.96) alike. A value out of range throws (the factory refuses to start).
    /// </summary>
    public Sessions.StuckDetection StuckDetection => new Sessions.StuckDetection(
        config.GetValue("Worker:StuckRepeats", Sessions.StuckDetection.DefaultRepeats),
        config.GetValue("Worker:StuckSimilarity", Sessions.StuckDetection.DefaultSimilarity)).Validate();

    /// <summary>
    /// <c>Worker:QuietMinutes</c> (&gt; 0, default 10): a running session with no event for this long is marked quiet on the
    /// dashboard. Silence alone never interrupts a worker (sc-25388).
    /// </summary>
    public TimeSpan QuietThreshold => config.GetValue("Worker:QuietMinutes", Dashboard.DashboardData.DefaultQuietThreshold.TotalMinutes) is var m && m > 0
        ? TimeSpan.FromMinutes(m)
        : throw new InvalidOperationException("Worker:QuietMinutes must be more than 0.");

    /// <summary>
    /// Pause flags the worker's PreToolUse hook checks: under the owner-owned work root, readable but not
    /// writable by the worker user.
    /// </summary>
    public string PauseFlagDirectory => Path.Combine(WorkRoot, "controls");

    public string LedgerConnectionString => config.GetLedgerConnectionString();

    public string RouterKey => Secret("Router:Key", "FACTORY_ROUTER_KEY", SecretAccounts.RouterKey, "router key");

    public string ShortcutApiToken =>
        Secret("Shortcut:ApiToken", "SHORTCUT_API_TOKEN", SecretAccounts.ShortcutApiToken, "Shortcut API token");

    public string GitHubAppId => Secret("GitHub:AppId", null, SecretAccounts.GitHubAppId, "GitHub App id");

    public string GitHubAppPrivateKeyPem =>
        Secret("GitHub:PrivateKeyPem", null, SecretAccounts.GitHubAppPrivateKey, "GitHub App private key");

    /// <summary>The merge gate's App (<c>factory github-app setup --gate</c>): the merge-capable credential only the gate holds.</summary>
    public string GitHubGateAppId => Secret("GitHub:Gate:AppId", null, SecretAccounts.GitHubGateAppId, "GitHub gate App id (run `factory github-app setup --gate`)");

    public string GitHubGateAppPrivateKeyPem =>
        Secret("GitHub:Gate:PrivateKeyPem", null, SecretAccounts.GitHubGateAppPrivateKey, "GitHub gate App private key (run `factory github-app setup --gate`)");

    /// <summary>
    /// The review panel's model lists, each comma-separated and in order of preference: <c>Review:&lt;Role&gt;:Models</c>
    /// (<c>Correctness</c>, <c>SpecConformance</c>, <c>Security</c>) per role, falling back to <c>Review:Models</c> — no
    /// default, every entry a <see cref="Gate.ReviewModels.FloorText"/> (<see cref="Gate.ReviewModels.MeetsReviewFloor"/>);
    /// <c>Review:Confirm:Models</c> for the second model that checks a blocking finding (default
    /// <see cref="Gate.ReviewPanelModels.DefaultConfirmers"/>), every entry a Claude model. A role with no reviewer model, or an
    /// entry that breaks its rule, throws <see cref="ReviewConfigurationException"/> (the factory refuses to start).
    /// </summary>
    public Gate.ReviewPanelModels ReviewPanel
    {
        get
        {
            var shared = Models("Review:Models", [], Gate.ReviewModels.MeetsReviewFloor, Gate.ReviewModels.FloorText);
            var roles = Gate.ReviewRoles.All.ToDictionary(r => r, r =>
            {
                var key = $"Review:{Gate.ReviewRoles.ConfigName(r)}:Models";
                var models = Models(key, shared, Gate.ReviewModels.MeetsReviewFloor, Gate.ReviewModels.FloorText);
                return models.Count > 0 ? models : throw new ReviewConfigurationException(
                    $"No {r} reviewer model is configured: reviewers must be a {Gate.ReviewModels.FloorText} and there is no default; "
                    + $"set Review:Models (or {key}) to one the router routes.");
            });
            return new Gate.ReviewPanelModels(roles,
                Models("Review:Confirm:Models", Gate.ReviewPanelModels.DefaultConfirmers, Gate.ReviewModels.IsClaude, "Claude model"));
        }
    }

    private IReadOnlyList<string> Models(string key, IReadOnlyList<string> fallback, Func<string, bool> eligible, string rule)
    {
        var models = config[key] is { } list && !string.IsNullOrWhiteSpace(list)
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : fallback;
        return models.FirstOrDefault(m => !eligible(m)) is { } bad
            ? throw new ReviewConfigurationException($"{key}: '{bad}' is not a {rule}.")
            : models;
    }

    /// <summary>
    /// The items the metrics leave out (<see cref="Ledger.MetricsOptions"/>): <c>Metrics:SandboxRepos</c> (comma-separated <c>owner/name</c>,
    /// default the factory's sandbox repo) and <c>Metrics:DemoMarkers</c> (comma-separated title markers, default <c>[demo]</c>, <c>[sandbox]</c>).
    /// </summary>
    public Ledger.MetricsOptions Metrics => new(
        List("Metrics:SandboxRepos") ?? Ledger.MetricsOptions.DefaultSandboxRepos,
        List("Metrics:DemoMarkers") ?? Ledger.MetricsOptions.DefaultDemoMarkers);

    private List<string>? List(string key) => config[key] is { } list && !string.IsNullOrWhiteSpace(list)
        ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        : null;

    /// <summary><c>Review:TimeoutMinutes</c> (default 10): the longest one reviewer call may take.</summary>
    public TimeSpan ReviewTimeout => TimeSpan.FromMinutes(config.GetValue("Review:TimeoutMinutes", 10));

    /// <summary><c>Gate:CiPollSeconds</c> (default 30): how often CI on the PR's head is read while it runs.</summary>
    public TimeSpan CiPollInterval => TimeSpan.FromSeconds(config.GetValue("Gate:CiPollSeconds", GateStage.DefaultCiPollInterval.TotalSeconds));

    /// <summary><c>Gate:CiTimeoutMinutes</c> (default 30): CI still running after this escalates the item.</summary>
    public TimeSpan CiTimeout => TimeSpan.FromMinutes(config.GetValue("Gate:CiTimeoutMinutes", GateStage.DefaultCiTimeout.TotalMinutes));

    /// <summary>
    /// <c>Gate:TestTimeoutMinutes</c> (default 20): one sandboxed run (restore, build, the new tests) of the
    /// <c>new-tests-fail-on-base</c> check; a run still going after this fails the check.
    /// </summary>
    public TimeSpan TestTimeout => TimeSpan.FromMinutes(config.GetValue("Gate:TestTimeoutMinutes", Gate.SandboxTestRunner.DefaultTimeout.TotalMinutes));

    public bool TryGet(Func<FactoryOptions, string> secret, out string? value)
    {
        try
        {
            value = secret(this);
            return true;
        }
        catch (MissingCredentialException)
        {
            value = null;
            return false;
        }
    }

    private string Secret(string configKey, string? envVar, string keychainAccount, string description)
    {
        var value = config[configKey];
        if (string.IsNullOrWhiteSpace(value) && envVar is not null)
        {
            value = Environment.GetEnvironmentVariable(envVar);
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            value = secrets.Get(keychainAccount);
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            var sources = envVar is null
                ? $"config '{configKey}' or keychain account '{keychainAccount}' (service '{SecretAccounts.Service}')"
                : $"env {envVar}, config '{configKey}', or keychain account '{keychainAccount}' (service '{SecretAccounts.Service}')";
            throw new MissingCredentialException($"Missing {description}: set {sources}.");
        }
        return value.Trim();
    }

    public static IConfiguration LoadConfiguration() =>
        new ConfigurationBuilder()
            .AddUserSecrets<FactoryOptions>(optional: true)
            .AddEnvironmentVariables()
            .Build();
}

public sealed class MissingCredentialException(string message) : Exception(message);

/// <summary>The review panel's model settings are missing or break its rule (<see cref="FactoryOptions.ReviewPanel"/>): the factory refuses to start.</summary>
public sealed class ReviewConfigurationException(string message) : Exception(message);

public static class ConfigurationExtensions
{
    public static string GetLedgerConnectionString(this IConfiguration config) =>
        config.GetConnectionString("Ledger") ?? FactoryOptions.DefaultConnectionString;
}
