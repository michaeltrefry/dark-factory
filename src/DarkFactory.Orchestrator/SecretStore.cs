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
        var encoded = Base64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var (exit, _) = Run("add-generic-password", "-U", "-s", SecretAccounts.Service, "-a", account, "-w", encoded);
        if (exit != 0)
        {
            throw new InvalidOperationException($"security add-generic-password failed for '{account}' (exit {exit}).");
        }
    }

    // Values written by this class carry a "b64:" prefix; values the owner adds by
    // hand with `security add-generic-password -w <value>` are plain text.
    private const string Base64Prefix = "b64:";

    private static string Decode(string stored) =>
        stored.StartsWith(Base64Prefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[Base64Prefix.Length..]))
            : stored;

    private static (int Exit, string Stdout) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout);
    }
}
