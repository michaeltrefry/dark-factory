using System.CommandLine;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator;

/// <summary>Command-line surface. Handlers are injected so argument handling is testable.</summary>
public static class FactoryCli
{
    public const int DefaultSetupPort = 47821;

    public static RootCommand Build(
        Func<int, bool, CancellationToken, Task<int>> run,
        Func<string, int, CancellationToken, Task<int>> setupGitHubApp,
        Func<RepoRef, CancellationToken, Task<int>> protectRepo,
        Func<CancellationToken, Task<int>> work,
        Func<CancellationToken, Task<int>> setDashboardPassword,
        Func<string, string, CancellationToken, Task<int>> control)
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
        var ignoreScopeOption = new Option<bool>("--ignore-scope")
        {
            Description = "Claim and resume the story even if it is outside the Shortcut watch scope (the other claim checks still apply)",
        };
        var runCommand = new Command("run", "Run one Shortcut story through Intake → Implement → Review, resuming from its last ledger state.")
        {
            storyArgument,
            ignoreScopeOption,
        };
        runCommand.SetAction((parse, ct) => run(parse.GetValue(storyArgument), parse.GetValue(ignoreScopeOption), ct));

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

        var workCommand = new Command("work", "Run the long-running factory host until Ctrl-C: the dashboard and session hub on 127.0.0.1:Factory:HostPort (plus Dashboard:BindAddress), and the intake loop that polls the watched Shortcut scope and runs each ready (To Do) story, one at a time.");
        workCommand.SetAction((_, ct) => work(ct));

        var setPasswordCommand = new Command("set-password", "Set the dashboard login password (read without echo; its hash goes to the keychain).");
        setPasswordCommand.SetAction((_, ct) => setDashboardPassword(ct));

        return new RootCommand("Dark Factory orchestrator")
        {
            runCommand,
            workCommand,
            ControlCommand("pause", "Pause a scope: nothing new is claimed or started there, and running workers stop after their current tool call (session and worktree kept).", control),
            ControlCommand("continue", "Continue a paused scope: paused items resume from the ledger (an interrupted worker resumes its own Claude session).", control),
            ControlCommand("stop", "Stop every active item in a scope: kill its worker, mark it Cancelled, turn its open PR back into a draft and move its story to the Backlog with a comment. Nothing is merged or deleted.", control),
            new Command("github-app", "GitHub App management") { setupCommand },
            new Command("github-repo", "Target repository management") { protectCommand },
            new Command("dashboard", "Dashboard management") { setPasswordCommand },
        };
    }

    /// <summary><c>factory &lt;action&gt; --item sc-N | --epic N | --factory</c>; the handler gets the action and the control scope.</summary>
    private static Command ControlCommand(string action, string description, Func<string, string, CancellationToken, Task<int>> control)
    {
        var item = new Option<int?>("--item")
        {
            Description = "One Shortcut story, e.g. sc-1234",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (StoryId.TryParse(token, out var id))
                {
                    return id;
                }
                result.AddError($"'{token}' is not a Shortcut story id (expected sc-<number> or <number>).");
                return null;
            },
        };
        var epic = new Option<long?>("--epic") { Description = "Every story of one Shortcut epic (its id)" };
        var factory = new Option<bool>("--factory") { Description = "The whole factory" };
        var command = new Command(action, description) { item, epic, factory };
        command.Validators.Add(result =>
        {
            var given = new[] { result.GetResult(item) is not null, result.GetResult(epic) is not null, result.GetResult(factory) is not null }.Count(x => x);
            if (given != 1)
            {
                result.AddError("Give exactly one of --item sc-N, --epic N or --factory.");
            }
            else if (result.GetValue(epic) is <= 0)
            {
                result.AddError("--epic must be a positive epic id.");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var scope = parse.GetValue(item) is { } story ? Controls.ControlScope.Item(StoryId.Format(story))
                : parse.GetValue(epic) is { } epicId ? Controls.ControlScope.Epic(epicId)
                : Controls.ControlScope.Factory;
            return control(action, scope, ct);
        });
        return command;
    }
}
