using System.CommandLine;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.Orchestrator;

/// <summary>S1's owner-invoked runnable seam; does not claim an item, grant a push or merge, or bypass a pipeline decision.</summary>
public static class IosNewTestsCli
{
    public static Command Build(Func<RepoRef, IosNewTestsRequest, CancellationToken, Task<NewTestsResult>>? execute = null)
    {
        var template = new Command("workflow", "Print the sealed workflow to install on the target default branch as .github/workflows/factory-ios-new-tests.yml.");
        template.SetAction(_ => { Console.Write(RemoteIosWorkflow.Text); return 0; });
        var guiTemplate = new Command("gui-workflow", "Print the sealed workflow to install once on the central trusted GUI collector repository's default branch.");
        guiTemplate.SetAction(_ => { Console.Write(RemoteGuiWorkflow.Text); return 0; });
        var repo = new Argument<string>("repo");
        var @base = new Argument<string>("base-sha");
        var head = new Argument<string>("head-sha");
        var tests = new Argument<string[]>("tests") { Arity = ArgumentArity.OneOrMore,
            Description = "New XCTest identifiers of the e2e-ios Tally fixture, e.g. TallyTests/TallyTests/testNew" };
        var run = new Command("run", "Run the remote single-fixture check and print its NewTestsResult JSON. Requires the gate App's repo-scoped Actions-write authorization.")
            { repo, @base, head, tests };
        run.SetAction(async (parse, ct) =>
        {
            try
            {
                var result = await (execute ?? Execute)(RepoRef.Parse(parse.GetValue(repo)!),
                    new(parse.GetValue(@base)!, parse.GetValue(head)!, parse.GetValue(tests)!), ct);
                Console.WriteLine(result.ToDetail());
                return result.Passed ? 0 : 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine("iOS remote check unavailable (" + ex.GetType().Name + "); no result accepted.");
                return 1;
            }
        });
        return new Command("ios-new-tests", "Remote iOS walking skeleton (not the pipeline's general routing).") { template, guiTemplate, run };
    }

    private static async Task<NewTestsResult> Execute(RepoRef repo, IosNewTestsRequest request, CancellationToken ct)
    {
        var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new MacKeychain());
        options.ValidateSettings();
        using var http = OutboundHttp.GitHubApi();
        var app = new GitHubApp(http, options.GitHubGateAppId, options.GitHubGateAppPrivateKeyPem, TimeProvider.System);
        return await new RemoteIosNewTests(new IosWorkflowGitHub(http, app), Path.Combine(options.WorkRoot, "ios-dispatches"))
            { Timeout = options.TestTimeout }.RunAsync(repo, request, ct);
    }
}
