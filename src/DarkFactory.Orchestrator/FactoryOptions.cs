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

    /// <summary><c>Worker:Auth</c> = <c>claude-login</c> (default) or <c>router-key</c>; see <see cref="Worker.WorkerAuth"/>.</summary>
    public Worker.WorkerAuth WorkerAuth => (config["Worker:Auth"] ?? "claude-login") switch
    {
        "claude-login" => Worker.WorkerAuth.ClaudeLogin,
        "router-key" => Worker.WorkerAuth.RouterKey,
        var other => throw new InvalidOperationException($"Worker:Auth must be 'claude-login' or 'router-key', not '{other}'."),
    };

    /// <summary>Loopback port of the <c>factory work</c> host (session hub).</summary>
    public int HostPort => config.GetValue("Factory:HostPort", 47822);

    public TimeSpan WorkerTimeout => TimeSpan.FromMinutes(config.GetValue("Worker:TimeoutMinutes", 30));

    public string LedgerConnectionString => config.GetLedgerConnectionString();

    public string RouterKey => Secret("Router:Key", "FACTORY_ROUTER_KEY", SecretAccounts.RouterKey, "router key");

    public string ShortcutApiToken =>
        Secret("Shortcut:ApiToken", "SHORTCUT_API_TOKEN", SecretAccounts.ShortcutApiToken, "Shortcut API token");

    public string GitHubAppId => Secret("GitHub:AppId", null, SecretAccounts.GitHubAppId, "GitHub App id");

    public string GitHubAppPrivateKeyPem =>
        Secret("GitHub:PrivateKeyPem", null, SecretAccounts.GitHubAppPrivateKey, "GitHub App private key");

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

public static class ConfigurationExtensions
{
    public static string GetLedgerConnectionString(this IConfiguration config) =>
        config.GetConnectionString("Ledger") ?? FactoryOptions.DefaultConnectionString;
}
