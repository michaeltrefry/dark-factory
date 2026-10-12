using System.Diagnostics;
using System.Text;

namespace DarkFactory.Orchestrator;

/// <summary>Holds secrets outside the repository (the macOS login keychain in production).</summary>
public interface ISecretStore
{
    string? Get(string account);
    void Set(string account, string value);
}

public static class SecretAccounts
{
    public const string Service = "dark-factory";
    public const string RouterKey = "router-key";
    public const string ShortcutApiToken = "shortcut-api-token";
    public const string GitHubAppId = "github-app-id";
    public const string GitHubAppSlug = "github-app-slug";
    /// <summary>Stored base64-encoded (by <see cref="MacKeychain"/>) so the multi-line PEM round-trips through <c>security -w</c>.</summary>
    public const string GitHubAppPrivateKey = "github-app-private-key";
    /// <summary>The merge gate's own App (<c>factory github-app setup --gate</c>): the only credential that can merge.</summary>
    public const string GitHubGateAppId = "github-gate-app-id";
    public const string GitHubGateAppSlug = "github-gate-app-slug";
    public const string GitHubGateAppPrivateKey = "github-gate-app-private-key";
    /// <summary>The dashboard login's ASP.NET Identity (PBKDF2) password hash, written by <c>factory dashboard set-password</c>.</summary>
    public const string DashboardPasswordHash = "dashboard-password-hash";
    /// <summary>The reviewers' CodeGraph token (sc-25705): owner-side only, optional.</summary>
    public const string CodeGraphToken = "codegraph-token";
    /// <summary>The Kanban upstream's personal access token (sc-25707): owner-side only, optional.</summary>
    public const string KanbanToken = "kanban-token";
}

/// <summary>Generic passwords in the login keychain under service <c>dark-factory</c>, via <c>/usr/bin/security</c>.</summary>
public sealed class MacKeychain : ISecretStore
{
    public string? Get(string account)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        var (exit, stdout) = Run("find-generic-password", "-s", SecretAccounts.Service, "-a", account, "-w");
        return exit == 0 ? Decode(stdout.TrimEnd('\n')) : null;
    }

    public void Set(string account, string value)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The keychain secret store requires macOS.");
        }
        var (args, stdin) = BuildSetCommand(account, value);
        var (exit, _) = Run(stdin, args);
        if (exit != 0)
        {
            throw new InvalidOperationException($"security add-generic-password failed for '{account}' (exit {exit}).");
        }
    }

    /// <summary>
    /// <c>security -i</c> reads the add command from stdin, so the secret never appears in
    /// argv (visible to <c>ps</c>). The base64 value and account need no quoting.
    /// </summary>
    public static (string[] Args, string Stdin) BuildSetCommand(string account, string value)
    {
        if (account.Length == 0 || !account.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException($"Invalid keychain account name '{account}'.", nameof(account));
        }
        var encoded = Base64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var line = $"add-generic-password -U -s {SecretAccounts.Service} -a {account} -w {encoded}\n";
        if (line.Length > MaxInteractiveLine)
        {
            throw new ArgumentException(
                $"Secret for '{account}' is too long for `security -i` ({line.Length} > {MaxInteractiveLine} chars).", nameof(value));
        }
        return (["-i"], line);
    }

    // `security -i` drops lines over its ~4 KiB input buffer (3728 chars measured OK, 4228 rejected).
    // A 2048-bit GitHub App PEM encodes to about 2.3 KiB.
    public const int MaxInteractiveLine = 3700;

    // Values written by this class carry a "b64:" prefix; values the owner adds by
    // hand with `security add-generic-password -w <value>` are plain text.
    private const string Base64Prefix = "b64:";

    private static string Decode(string stored) =>
        stored.StartsWith(Base64Prefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[Base64Prefix.Length..]))
            : stored;

    private static (int Exit, string Stdout) Run(params string[] args) => Run(null, args);

    private static (int Exit, string Stdout) Run(string? stdin, string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
}
