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
}
