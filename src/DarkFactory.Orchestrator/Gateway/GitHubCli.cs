using System.Diagnostics;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The factory's only use of the GitHub CLI (sc-25390): <c>gh</c> is a GitHub client, so, like every other one, it is started
/// only here; the gateway lint (DF0001) fails the build on <c>gh</c>, <c>curl</c>, <c>wget</c> or <c>nc</c> started anywhere else.
/// </summary>
public static class GitHubCli
{
    /// <summary>The owner's own token from <c>gh auth token</c> (for <c>factory github-repo protect</c>), or null without one.</summary>
    public static string? AuthToken()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var token = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static ProcessStartInfo GuiEnvironmentStartInfo(string ownerToken, bool secret, string name)
    {
        var start = new ProcessStartInfo("gh") { UseShellExecute = false, WorkingDirectory = "/",
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { secret ? "secret" : "variable", "set", name, "--repo", "michaeltrefry/LensWorks", "--env", "factory-gui-dispatch" })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
        start.Environment["GH_TOKEN"] = ownerToken;
        start.Environment["GH_HOST"] = "github.com";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        return start;
    }

    /// <summary>The owner grants only a Gate-App key to the default-branch-protected hosted dispatcher environment.
    /// gh encrypts the secret for GitHub. Neither key nor owner PAT appears in argv, output, journal or a service environment.</summary>
    public static async Task SetGuiEnvironmentValueAsync(string ownerToken, bool secret, string name, string value, CancellationToken ct)
    {
        if (name is not ("FACTORY_GUI_APP_PRIVATE_KEY" or "FACTORY_GUI_APP_ID" or "FACTORY_GUI_CONFIGURATION_SHA256"))
            throw new InvalidOperationException("Unknown protected GUI environment value.");
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(GuiEnvironmentStartInfo(ownerToken, secret, name))
            ?? throw new InvalidOperationException("Could not start owner GitHub environment provisioning.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.StandardInput.WriteAsync(value); }
        finally { process.StandardInput.Close(); }
        await process.WaitForExitAsync(CancellationToken.None);
        await stdout;
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("GitHub refused the protected dispatcher environment value; no private response was retained.");
    }
}
