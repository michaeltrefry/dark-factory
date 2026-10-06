using System.Diagnostics;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.Extensions.Hosting;

return await FactoryCli.Build(RunAsync, SetupGitHubAppAsync, ProtectRepoAsync, WorkAsync, SetDashboardPasswordAsync).Parse(args).InvokeAsync();

static async Task<int> RunAsync(int storyId, bool ignoreScope, CancellationToken ct)
{
    var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new MacKeychain());
    try
    {
        var outcome = await FactoryRunner.RunAsync(options, storyId, ignoreScope, Console.Out, ct);
        return outcome.Succeeded ? 0 : 1;
    }
    catch (MissingCredentialException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static async Task<int> WorkAsync(CancellationToken ct)
{
    var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new MacKeychain());
    try
    {
        // Fail fast on a missing credential or a bad scope rather than on the first ready item.
        // Session costs come from the router; intake needs Shortcut and the GitHub App; the dashboard its login.
        _ = (options.ShortcutApiToken, options.RouterKey, options.GitHubAppId, options.GitHubAppPrivateKeyPem, options.DashboardPasswordHash);
        _ = DashboardBinding.Addresses(options.DashboardBindAddress);
        if (options.WatchScope.IsEmpty)
        {
            Console.Error.WriteLine("Watch scope is empty (set Shortcut:Watch:Teams and/or Shortcut:Watch:Epics); nothing will be picked up.");
        }
    }
    catch (Exception ex) when (ex is MissingCredentialException or InvalidOperationException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    using (var shortcutHttp = new HttpClient { BaseAddress = ShortcutWorkSource.DefaultBaseAddress })
    {
        if (await FactoryRunner.CheckWatchScopeAsync(options, shortcutHttp, ct) is { } scopeError)
        {
            Console.Error.WriteLine(scopeError);
            return 2;
        }
    }
    await LedgerMigrations.MigrateAsync(options.LedgerConnectionString, ct);
    // One process: the session hub and its relay, plus the intake loop polling the watch scope.
    await using var app = FactoryHost.BuildWork(options);
    await app.StartAsync(ct);
    Console.WriteLine($"factory work: dashboard on {string.Join(", ", app.Addresses())} (session hub {SessionHub.Path}); intake polling every {options.PollInterval}; Ctrl-C stops");
    await app.WaitForShutdownAsync(ct);
    return 0;
}

static Task<int> SetDashboardPasswordAsync(CancellationToken ct) =>
    Task.FromResult(SetPassword.Run(new MacKeychain(), SetPassword.ReadConsoleSecret, Console.Out, Console.Error));

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
