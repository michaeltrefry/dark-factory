using System.Diagnostics;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

return await FactoryCli.Build(RunAsync, SetupGitHubAppAsync, ProtectRepoAsync).Parse(args).InvokeAsync();

static async Task<int> RunAsync(int storyId, CancellationToken ct)
{
    var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new MacKeychain());
    try
    {
        var outcome = await FactoryRunner.RunAsync(options, storyId, Console.Out, ct);
        return outcome.Succeeded ? 0 : 1;
    }
    catch (MissingCredentialException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static async Task<int> SetupGitHubAppAsync(string name, int port, CancellationToken ct)
{
    using var http = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
    await new GitHubAppSetup(http, new MacKeychain(), Console.Out).RunAsync(name, port, ct);
    return 0;
}

// Rulesets need repo admin, which the App deliberately lacks, so this uses the owner's own GitHub token.
static async Task<int> ProtectRepoAsync(RepoRef repo, CancellationToken ct)
{
    var token = RepoProtection.ResolveAdminToken(Environment.GetEnvironmentVariable, GhAuthToken);
    if (token is null)
    {
        Console.Error.WriteLine("No admin GitHub token: set GH_TOKEN or run `gh auth login`.");
        return 2;
    }
    using var http = new HttpClient { BaseAddress = GitHubApp.DefaultBaseAddress };
    return await RepoProtection.RunAsync(http, token, repo, Console.Out, Console.Error, ct);
}

static string? GhAuthToken()
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
