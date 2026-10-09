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

    /// <summary>
    /// <c>Router:BaseUrl</c> (default <c>http://localhost:8080</c>): the Weave router every model call goes through. An absolute
    /// http(s) URL naming no model provider (<see cref="Gateway.ProviderMarkers"/>, the gateway lint's list): the lint sees only the
    /// code, so a provider host given through configuration is refused here (Phase 1 E1), and the factory refuses to start.
    /// </summary>
    public Uri RouterBaseUrl
    {
        get
        {
            var text = config["Router:BaseUrl"] ?? "http://localhost:8080";
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException($"Router:BaseUrl must be an absolute http(s) URL, not '{text}'.");
            }
            return Gateway.ProviderMarkers.In(text) is { } provider
                ? throw new InvalidOperationException(
                    $"Router:BaseUrl names a model provider ('{provider}'): every model call goes through the Weave router only (Phase 1 E1).")
                : url;
        }
    }

    /// <summary><c>Factory:DefaultRepo</c> (default the sandbox repo): an <c>owner/name</c>, else the factory refuses to start.</summary>
    public RepoRef DefaultRepo
    {
        get
        {
            try
            {
                return RepoRef.Parse(config["Factory:DefaultRepo"] ?? "michaeltrefry/dark-factory-sandbox");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Factory:DefaultRepo: {ex.Message}", ex);
            }
        }
    }

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

    /// <summary><c>Intake:PollSeconds</c> (&gt; 0, default 60): how often <c>factory work</c> polls the board.</summary>
    public TimeSpan PollInterval => Positive("Intake:PollSeconds", 60, TimeSpan.FromSeconds);

    /// <summary><c>Intake:MaxItemFailures</c>: runs of one item in a row that may fail before <c>factory work</c> gives up on it (E10).</summary>
    public int MaxItemFailures => config.GetValue("Intake:MaxItemFailures", WorkSources.IntakeOptions.DefaultMaxItemFailures) is var n && n > 0
        ? n
        : throw new InvalidOperationException("Intake:MaxItemFailures must be at least 1.");

    /// <summary>
    /// The automatic freeze's thresholds (<see cref="Controls.FreezeOptions"/>): <c>Freeze:MaxConsecutiveFailures</c> (≥ 1),
    /// <c>Freeze:HotFileMerges</c> (≥ 2), <c>Freeze:HotFileWindowHours</c> (&gt; 0), <c>Freeze:CostRisingRounds</c> (≥ 1),
    /// <c>Freeze:CheckFailedEvaluations</c> (≥ 1), <c>Freeze:CheckFailedMinutes</c> (&gt; 0). A value out of range throws (the
    /// factory refuses to start).
    /// </summary>
    public Controls.FreezeOptions Freeze => new()
    {
        MaxConsecutiveFailures = AtLeast("Freeze:MaxConsecutiveFailures", Controls.FreezeOptions.DefaultMaxConsecutiveFailures, 1),
        HotFileMerges = AtLeast("Freeze:HotFileMerges", Controls.FreezeOptions.DefaultHotFileMerges, 2),
        HotFileWindow = config.GetValue("Freeze:HotFileWindowHours", Controls.FreezeOptions.DefaultHotFileWindowHours) is var hours && hours > 0
            ? TimeSpan.FromHours(hours)
            : throw new InvalidOperationException("Freeze:HotFileWindowHours must be more than 0."),
        CostRisingRounds = AtLeast("Freeze:CostRisingRounds", Controls.FreezeOptions.DefaultCostRisingRounds, 1),
        CheckFailedEvaluations = AtLeast("Freeze:CheckFailedEvaluations", Controls.FreezeOptions.DefaultCheckFailedEvaluations, 1),
        CheckFailedWindow = Positive("Freeze:CheckFailedMinutes", Controls.FreezeOptions.DefaultCheckFailedMinutes, TimeSpan.FromMinutes),
    };

    /// <summary>
    /// The repos the factory is configured to work on (<see cref="DefaultRepo"/> and <see cref="WatchedIssueRepos"/>): the
    /// freeze's main-red trigger reads these and the repos of non-terminal items.
    /// </summary>
    public IReadOnlyList<string> ConfiguredRepos => [DefaultRepo.FullName, .. WatchedIssueRepos.Select(r => r.FullName)];

    private int AtLeast(string key, int fallback, int min) =>
        config.GetValue(key, fallback) is var n && n >= min ? n : throw new InvalidOperationException($"{key} must be at least {min}, not {n}.");

    /// <summary>A duration key: more than 0 (0 or less would make a tight loop or an instant timeout), else the factory refuses to start.</summary>
    private TimeSpan Positive(string key, double fallback, Func<double, TimeSpan> unit) =>
        config.GetValue(key, fallback) is var n && n > 0 ? unit(n) : throw new InvalidOperationException($"{key} must be more than 0, not {n}.");

    /// <summary><c>Usage:PollSeconds</c> (&gt; 0, default 60): how often <c>factory work</c> reads the router's subscription usage.</summary>
    public TimeSpan UsagePollInterval => Positive("Usage:PollSeconds", 60, TimeSpan.FromSeconds);

    /// <summary>
    /// <c>Router:CostSettleSeconds</c>: after the router first reports a session's cost, wait this long and read it once
    /// more (a session's last requests can be committed later); see <see cref="Sessions.SessionRecorder"/>.
    /// </summary>
    public TimeSpan CostSettleDelay => config.GetValue("Router:CostSettleSeconds", Sessions.SessionRecorder.DefaultCostSettleDelay.TotalSeconds) is var s && s >= 0
        ? TimeSpan.FromSeconds(s)
        : throw new InvalidOperationException("Router:CostSettleSeconds must not be negative.");

    /// <summary>
    /// <c>Factory:HostPort</c> (0–65535, 0 = any free port; default 47822): port of the <c>factory work</c> host (dashboard and session hub), on
    /// 127.0.0.1 and <see cref="DashboardBindAddress"/>.
    /// </summary>
    public int HostPort => config.GetValue("Factory:HostPort", 47822) is var port && port is >= 0 and <= 65535
        ? port
        : throw new InvalidOperationException($"Factory:HostPort must be a port from 0 (any free port) to 65535, not {port}.");

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

    /// <summary><c>Worker:TimeoutMinutes</c> (&gt; 0, default 30): the longest one worker session may run.</summary>
    public TimeSpan WorkerTimeout => Positive("Worker:TimeoutMinutes", 30, TimeSpan.FromMinutes);

    /// <summary>
    /// <c>Controls:MaxReadFailures</c> (≥ 1, default <see cref="RunPipeline.DefaultMaxControlReadFailures"/>): reads of an item's
    /// controls in a row that may fail while its worker or the gate's test runs go on; the next counts as a Pause.
    /// </summary>
    public int MaxControlReadFailures => AtLeast("Controls:MaxReadFailures", RunPipeline.DefaultMaxControlReadFailures, 1);

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
    /// (<c>Correctness</c>, <c>SpecConformance</c>, <c>Security</c>) per role, falling back to <c>Review:Models</c> (default
    /// <see cref="Gate.ReviewPanelModels.DefaultReviewers"/>), every entry a <see cref="Gate.ReviewModels.FloorText"/>
    /// (<see cref="Gate.ReviewModels.MeetsReviewFloor"/>); <c>Review:Confirm:Models</c> for the second model that checks a
    /// blocking finding (default <see cref="Gate.ReviewPanelModels.DefaultConfirmers"/>), every entry a Claude model. An entry
    /// that breaks its rule throws <see cref="ReviewConfigurationException"/> (the factory refuses to start).
    /// </summary>
    public Gate.ReviewPanelModels ReviewPanel
    {
        get
        {
            var shared = Models("Review:Models", Gate.ReviewPanelModels.DefaultReviewers, Gate.ReviewModels.MeetsReviewFloor, Gate.ReviewModels.FloorText);
            var roles = Gate.ReviewRoles.All.ToDictionary(r => r,
                r => Models($"Review:{Gate.ReviewRoles.ConfigName(r)}:Models", shared, Gate.ReviewModels.MeetsReviewFloor, Gate.ReviewModels.FloorText));
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

    /// <summary><c>Review:TimeoutMinutes</c> (&gt; 0, default 10): the longest one reviewer call may take.</summary>
    public TimeSpan ReviewTimeout => Positive("Review:TimeoutMinutes", 10, TimeSpan.FromMinutes);

    /// <summary><c>Gate:CiPollSeconds</c> (&gt; 0, default 30): how often CI on the PR's head is read while it runs (0 would poll GitHub in a tight loop).</summary>
    public TimeSpan CiPollInterval => Positive("Gate:CiPollSeconds", GateStage.DefaultCiPollInterval.TotalSeconds, TimeSpan.FromSeconds);

    /// <summary><c>Gate:CiTimeoutMinutes</c> (&gt; 0, default 30): CI still running after this escalates the item.</summary>
    public TimeSpan CiTimeout => Positive("Gate:CiTimeoutMinutes", GateStage.DefaultCiTimeout.TotalMinutes, TimeSpan.FromMinutes);

    /// <summary>
    /// <c>Gate:TestTimeoutMinutes</c> (&gt; 0, default 20): one sandboxed run (restore, build, the new tests) of the
    /// <c>new-tests-fail-on-base</c> check; a run still going after this fails the check.
    /// </summary>
    public TimeSpan TestTimeout => Positive("Gate:TestTimeoutMinutes", Gate.SandboxTestRunner.DefaultTimeout.TotalMinutes, TimeSpan.FromMinutes);

    /// <summary>
    /// Reads every non-secret setting that has a rule, so a value out of range fails at start-up with its key named
    /// (<see cref="InvalidOperationException"/>; the review panel's models are checked apart, <see cref="ReviewPanel"/>), not
    /// mid-run. <c>factory run</c> and <c>factory work</c> call it before anything else and exit 2 on a failure.
    /// </summary>
    public void ValidateSettings()
    {
        _ = (RouterBaseUrl, DefaultRepo, WorkerSandbox, WorkerAuth, WatchScope, WatchedIssueRepos, PollInterval, MaxItemFailures);
        _ = (Freeze, UsagePollInterval, CostSettleDelay, HostPort, WorkerTimeout, MaxControlReadFailures, PauseGrace, StuckDetection);
        _ = (QuietThreshold, Metrics, ReviewTimeout, CiPollInterval, CiTimeout, TestTimeout);
    }

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
