using System.CommandLine;

namespace DarkFactory.Orchestrator;

/// <summary>Command-line surface. Handlers are injected so argument handling is testable.</summary>
public static class FactoryCli
{
    public const int DefaultSetupPort = 47821;

    public static RootCommand Build(
        Func<WorkSources.ItemRef, bool, CancellationToken, Task<int>> run,
        Func<string, int, bool, CancellationToken, Task<int>> setupGitHubApp,
        Func<RepoRef, CancellationToken, Task<int>> protectRepo,
        Func<CancellationToken, Task<int>> work,
        Func<CancellationToken, Task<int>> setDashboardPassword,
        Func<string, string, CancellationToken, Task<int>> control,
        Func<Guid, bool, CancellationToken, Task<int>>? kanbanKey = null,
        Func<CancellationToken, Task<int>>? recoveryStatus = null)
    {
        var storyArgument = new Argument<WorkSources.ItemRef?>("item-id")
        {
            Description = "The work item's id: a Shortcut story, e.g. sc-1234 or 1234, a GitHub issue's work item, e.g. gh-12, or a Kanban item's, e.g. kb-7. The prefix names its work source, which must be configured",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (WorkSources.ItemNaming.ParseAny(token) is { } item)
                {
                    return item;
                }
                result.AddError($"'{token}' is not a work item id (expected sc-<number>, <number>, gh-<number> or kb-<number>).");
                return null;
            },
        };
        var ignoreScopeOption = new Option<bool>("--ignore-scope")
        {
            Description = "Claim and resume the item even if it is outside its work source's watch scope (the other claim checks still apply)",
        };
        var runCommand = new Command("run", "Run one work item (a Shortcut story, a released GitHub issue, or a Kanban item) through Intake → Implement → Review → CI → MergeGate → Merge, resuming from its last ledger state.")
        {
            storyArgument,
            ignoreScopeOption,
        };
        runCommand.SetAction((parse, ct) => run(parse.GetValue(storyArgument)!, parse.GetValue(ignoreScopeOption), ct));

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
        var gateOption = new Option<bool>("--gate")
        {
            Description = "Register the merge gate's own App instead (the only credential that can merge; keychain github-gate-app-*)",
        };
        var setupCommand = new Command("setup", "Register the factory GitHub App via the manifest flow and store its key in the keychain.")
        {
            nameOption,
            portOption,
            gateOption,
        };
        setupCommand.SetAction((parse, ct) =>
        {
            var gate = parse.GetValue(gateOption);
            // The default name is the workers' App's; the gate App needs its own (App names are unique on github.com).
            var name = gate && parse.GetResult(nameOption) is null or { Implicit: true } ? $"dark-factory-gate-{Environment.UserName}" : parse.GetValue(nameOption)!;
            return setupGitHubApp(name, parse.GetValue(portOption), gate, ct);
        });

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

        var workCommand = new Command("work", "Run the long-running factory host until Ctrl-C: the dashboard and session hub on 127.0.0.1:Factory:HostPort (plus Dashboard:BindAddress), and the intake loop that polls every configured work source in turn (Shortcut, KanbanBoard, GitHub issues: any of them, at least one) and runs each ready item, one at a time unless Intake:MaxConcurrentRuns says more.");
        workCommand.SetAction((_, ct) => work(ct));

        var setPasswordCommand = new Command("set-password", "Set the dashboard login password (read without echo; its hash goes to the keychain).");
        setPasswordCommand.SetAction((_, ct) => setDashboardPassword(ct));

        var kanbanItemArgument = new Argument<Guid>("item-id")
        {
            Description = "The item's id on KanbanBoard (a GUID); left out with --epic",
            Arity = ArgumentArity.ZeroOrOne,
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (Guid.TryParse(token, out var id))
                {
                    return id;
                }
                result.AddError($"'{token}' is not a KanbanBoard item id (expected a GUID).");
                return default;
            },
        };
        var kanbanEpicOption = new Option<Guid?>("--epic")
        {
            Description = "An epic's id on KanbanBoard (a GUID), in place of an item's: print the epic's key (kb-<number>), giving it one if the factory has read no item of the epic yet; `factory pause|stop --epic kb-<number>` then takes it",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (Guid.TryParse(token, out var id))
                {
                    return id;
                }
                result.AddError($"'{token}' is not a KanbanBoard epic id (expected a GUID).");
                return null;
            },
        };
        var kanbanKeyCommand = new Command("key",
            "Print the work item id (kb-<number>) of a KanbanBoard item, giving it one if the factory has not seen the item yet; `factory run kb-<number>` then runs it. `factory work` gives every item it lists its id by itself: this is for an item it has not listed. With --epic <epic id> instead of an item: print the key of a KanbanBoard epic (also kb-<number>, a number space of its own), giving it one if the factory has read no item of the epic yet, so that the epic can be paused or stopped (`factory pause --epic kb-<number>`) before its first item runs.")
        {
            kanbanItemArgument,
            kanbanEpicOption,
        };
        kanbanKeyCommand.Validators.Add(result =>
        {
            // Exactly one of the two: an item id, or --epic with an epic id.
            var item = result.GetResult(kanbanItemArgument) is { Tokens.Count: > 0 };
            var epic = result.GetResult(kanbanEpicOption) is not null;
            if (item == epic)
            {
                result.AddError("Give exactly one of an item id (a GUID) or --epic <epic id>.");
            }
        });
        kanbanKeyCommand.SetAction((parse, ct) => kanbanKey is null ? Task.FromResult(1)
            : parse.GetValue(kanbanEpicOption) is { } epic ? kanbanKey(epic, true, ct)
            : kanbanKey(parse.GetValue(kanbanItemArgument), false, ct));

        var recoveryStatusCommand = new Command("status", "Report repository incidents, blockers, correction PRs and controls from the ledger only; never advances recovery.");
        recoveryStatusCommand.SetAction((_, ct) => recoveryStatus?.Invoke(ct) ?? Task.FromResult(1));
        return new RootCommand("Dark Factory orchestrator")
        {
            runCommand,
            new Command("recovery", "Read-only repository recovery observations") { recoveryStatusCommand },
            new Command("kanban", "KanbanBoard work items") { kanbanKeyCommand },
            workCommand,
            ControlCommand("pause", "Pause a scope: nothing new is claimed or started there, and running workers stop after their current tool call (session and worktree kept).", control),
            ControlCommand("continue", "Continue a paused scope: paused items resume from the ledger (an interrupted worker resumes its own Claude session).", control),
            ControlCommand("stop", "Stop every active item in a scope: kill its worker, mark it Cancelled, turn its open PR back into a draft and move it back to its board's Backlog with a comment, releasing the claim. Nothing is merged or deleted.", control),
            new Command("github-app", "GitHub App management") { setupCommand },
            new Command("github-repo", "Target repository management") { protectCommand },
            new Command("dashboard", "Dashboard management") { setPasswordCommand },
        };
    }

    /// <summary><c>factory &lt;action&gt; --item sc-N|gh-N|kb-N | --epic N|kb-N | --factory</c>; the handler gets the action and the control scope.</summary>
    private static Command ControlCommand(string action, string description, Func<string, string, CancellationToken, Task<int>> control)
    {
        var item = new Option<WorkSources.ItemRef?>("--item")
        {
            Description = "One work item: a Shortcut story, e.g. sc-1234, a GitHub issue's, e.g. gh-12, or a Kanban item's, e.g. kb-7. A plan item's control applies to the plan and its stories",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (WorkSources.ItemNaming.ParseAny(token) is { } parsed)
                {
                    return parsed;
                }
                result.AddError($"'{token}' is not a work item id (expected sc-<number>, <number>, gh-<number> or kb-<number>).");
                return null;
            },
        };
        var epic = new Option<WorkSources.EpicRef?>("--epic")
        {
            Description = "Every item of one epic: a Shortcut epic by its id, e.g. 12, or a Kanban epic by the key the factory gave it, e.g. kb-3 (the dashboard shows it)",
            CustomParser = result =>
            {
                var token = result.Tokens.Single().Value;
                if (WorkSources.ItemNaming.ParseEpic(token) is { } parsed)
                {
                    return parsed;
                }
                result.AddError($"'{token}' is not an epic (expected a Shortcut epic's id, <number>, or a Kanban epic's key, kb-<number>).");
                return null;
            },
        };
        var repository = new Option<string?>("--repo")
        {
            Description = "One repository's human control (owner/name); Continue does not resolve an automatic recovery hold",
            CustomParser = result =>
            {
                var name = result.Tokens.Single().Value.ToLowerInvariant();
                if (Controls.ControlScope.RepositoryOf("repo:" + name) is not null) return "repo:" + name;
                result.AddError("Expected --repo owner/name.");
                return null;
            },
        };
        var factory = new Option<bool>("--factory") { Description = "The whole factory" };
        var usage = new Option<bool>("--usage") { Description = "The factory's usage pause (plans exhausted): lift it before its reset time" };
        var freeze = new Option<bool>("--freeze") { Description = "The factory's automatic freeze: clear it (only later events can freeze it again)" };
        var command = new Command(action, description) { item, epic, repository, factory };
        if (action == "continue")
        {
            command.Options.Add(usage);
            command.Options.Add(freeze);
        }
        command.Validators.Add(result =>
        {
            var given = new[] { result.GetResult(item) is not null, result.GetResult(epic) is not null, result.GetResult(factory) is not null, result.GetResult(usage) is not null,
                    result.GetResult(freeze) is not null, result.GetResult(repository) is not null }
                .Count(x => x);
            if (given != 1)
            {
                result.AddError(action == "continue"
                    ? "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic <epic> (N or kb-N), --repo <owner/name>, --factory, --usage or --freeze."
                    : "Give exactly one of --item <id> (sc-N, gh-N or kb-N), --epic <epic> (N or kb-N), --repo <owner/name> or --factory.");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var scope = parse.GetValue(item) is { } story ? Controls.ControlScope.Item(story.ToString())
                : parse.GetValue(epic) is { } named ? Controls.ControlScope.Epic(named)
                : parse.GetValue(repository) is { } repoScope ? repoScope
                : parse.GetValue(usage) ? Controls.ControlScope.Usage
                : parse.GetValue(freeze) ? Controls.ControlScope.Freeze
                : Controls.ControlScope.Factory;
            return control(action, scope, ct);
        });
        return command;
    }
}
