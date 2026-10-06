using System.CommandLine;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator;

/// <summary>Command-line surface. Handlers are injected so argument handling is testable.</summary>
public static class FactoryCli
{
    public const int DefaultSetupPort = 47821;

    public static RootCommand Build(
        Func<int, CancellationToken, Task<int>> run,
        Func<string, int, CancellationToken, Task<int>> setupGitHubApp,
        Func<RepoRef, CancellationToken, Task<int>> protectRepo,
        Func<CancellationToken, Task<int>> work)
    {
        var storyArgument = new Argument<int>("story-id")
        {
            Description = "Shortcut story id, e.g. sc-1234 or 1234",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (StoryId.TryParse(token, out var id))
                {
                    return id;
                }
                result.AddError($"'{token}' is not a Shortcut story id (expected sc-<number> or <number>).");
                return 0;
            },
        };
        var runCommand = new Command("run", "Run one Shortcut story through Intake → Implement → Review, resuming from its last ledger state.") { storyArgument };
        runCommand.SetAction((parse, ct) => run(parse.GetValue(storyArgument), ct));

        var nameOption = new Option<string>("--name")
        {
            Description = "GitHub App name (must be unique on github.com)",
            DefaultValueFactory = _ => $"dark-factory-{Environment.UserName}",
        };
        var portOption = new Option<int>("--port")
        {
            Description = "Localhost port for the manifest-flow callback",
            DefaultValueFactory = _ => DefaultSetupPort,
        };
        var setupCommand = new Command("setup", "Register the factory GitHub App via the manifest flow and store its key in the keychain.")
        {
            nameOption,
            portOption,
        };
        setupCommand.SetAction((parse, ct) => setupGitHubApp(parse.GetValue(nameOption)!, parse.GetValue(portOption), ct));

        var repoArgument = new Argument<RepoRef>("repo")
        {
            Description = "Target repository, owner/name",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                try
                {
                    return RepoRef.Parse(token);
                }
                catch (ArgumentException)
                {
                    result.AddError($"'{token}' is not a repository (expected owner/name).");
                    return null;
                }
            },
        };
        var protectCommand = new Command("protect",
            "Apply the factory rulesets (main requires a PR; only admins write outside factory/**). Uses the owner's GH_TOKEN/GITHUB_TOKEN or `gh auth token`.")
        {
            repoArgument,
        };
        protectCommand.SetAction((parse, ct) => protectRepo(parse.GetValue(repoArgument)!, ct));

        var workCommand = new Command("work", "Run the long-running factory host (session hub on 127.0.0.1:Factory:HostPort) until Ctrl-C.");
        workCommand.SetAction((_, ct) => work(ct));

        return new RootCommand("Dark Factory orchestrator")
        {
            runCommand,
            workCommand,
            new Command("github-app", "GitHub App management") { setupCommand },
            new Command("github-repo", "Target repository management") { protectCommand },
        };
    }
}
