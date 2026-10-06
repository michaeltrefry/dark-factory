using System.CommandLine;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator;

/// <summary>Command-line surface. Handlers are injected so argument handling is testable.</summary>
public static class FactoryCli
{
    public const int DefaultSetupPort = 47821;

    public static RootCommand Build(
        Func<int, CancellationToken, Task<int>> run,
        Func<string, int, CancellationToken, Task<int>> setupGitHubApp)
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
        var runCommand = new Command("run", "Run one Shortcut story through Intake → Implement → Review.") { storyArgument };
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

        return new RootCommand("Dark Factory orchestrator")
        {
            runCommand,
            new Command("github-app", "GitHub App management") { setupCommand },
        };
    }
}
