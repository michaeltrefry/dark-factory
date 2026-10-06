using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.GitHub;

return await FactoryCli.Build(RunAsync, SetupGitHubAppAsync).Parse(args).InvokeAsync();

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
