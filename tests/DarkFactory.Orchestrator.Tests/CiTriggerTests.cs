using System.Net;
using System.Security.Cryptography;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25873: whether a repository's workflows can report on a plan's story pull requests (base: the plan's changeset branch, head:
/// the story's own branch) — the <c>on:</c> shapes, GitHub's branch filter matching, and "cannot tell" for anything else.
/// </summary>
public class CiTriggerTests
{
    private const string Base = "factory/changeset-sc-500";
    private const string Head = "factory/sc-500";
    private const string File = ".github/workflows/ci.yml";

    private static CiTriggerCheck Check(params string[] workflows) =>
        CiTriggers.Check([.. workflows.Select((text, i) => new WorkflowFile(i == 0 ? File : $".github/workflows/w{i}.yml", text))], Base, Head);

    /// <summary>Every shape of <c>on:</c> and what it means for a pull request from <see cref="Head"/> into <see cref="Base"/>.</summary>
    public static TheoryData<string, CiTriggerOutcome, string> Shapes() => new()
    {
        // ---- a trigger that fires ----
        { "on: pull_request\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "on: pull_request_target\n", CiTriggerOutcome.Runs, "pull_request_target, no branches filter" },
        { "on: [push, pull_request]\n", CiTriggerOutcome.Runs, "no branches filter" },
        { "on:\n  - workflow_dispatch\n  - pull_request\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "on:\n  pull_request:\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "on:\n  pull_request: ~\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "on:\n  pull_request: {}\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "'on':\n  pull_request:\n    types: [opened, synchronize]\n    paths: ['src/**']\n", CiTriggerOutcome.Runs, "pull_request, no branches filter" },
        { "on:\n  pull_request:\n    branches: [main, 'factory/**']\n", CiTriggerOutcome.Runs, $"its branches filter lets {Base} through" },
        { "on:\n  pull_request:\n    branches:\n      - main\n      - factory/**\n", CiTriggerOutcome.Runs, "lets" },
        { "on:\n  pull_request:\n    branches: 'factory/**'\n", CiTriggerOutcome.Runs, "lets" },
        { "on:\n  pull_request:\n    branches: ['**']\n", CiTriggerOutcome.Runs, "lets" },
        { "on:\n  pull_request:\n    branches: ['factory/changeset-*']\n", CiTriggerOutcome.Runs, "lets" },
        { "on:\n  pull_request:\n    branches: [main, 'factory/*']\n", CiTriggerOutcome.Runs, "lets" }, // one segment: * stops only at a slash
        { "on:\n  pull_request:\n    branches: ['!main', '**']\n", CiTriggerOutcome.Runs, "lets" },
        { "on:\n  pull_request:\n    branches-ignore: [main, 'release/**']\n", CiTriggerOutcome.Runs, $"its branches-ignore filter lets {Base} through" },
        { "on:\n  pull_request_target:\n    branches: ['factory/**']\n", CiTriggerOutcome.Runs, "pull_request_target" },
        // A push workflow's checks land on the story's head commit, which is where the gate reads CI.
        { "on: push\n", CiTriggerOutcome.Runs, "no branches filter" },
        { "on:\n  push:\n    branches: ['factory/**']\n  pull_request:\n    branches: [main]\n", CiTriggerOutcome.Runs, $"lets {Head} through" },
        { "on:\n  push:\n    paths: ['src/**']\n", CiTriggerOutcome.Runs, "no branches filter" },
        { "on:\n  push:\n    branches-ignore: [main]\n", CiTriggerOutcome.Runs, $"its branches-ignore filter lets {Head} through" },

        // ---- certain that nothing fires ----
        { "on:\n  pull_request:\n    branches: [main]\n", CiTriggerOutcome.Never, $"{File} (its pull_request branches filter leaves out {Base})" },
        { "on:\n  pull_request:\n    branches: main\n", CiTriggerOutcome.Never, "its pull_request branches filter leaves out" },
        { "on:\n  pull_request:\n    branches: [main, 'release/**', 'factory/*/*']\n", CiTriggerOutcome.Never, "leaves out" },
        { "on:\n  pull_request:\n    branches: ['factory/sc-*']\n", CiTriggerOutcome.Never, "leaves out" },
        { "on:\n  pull_request:\n    branches: ['*']\n", CiTriggerOutcome.Never, "leaves out" }, // * does not cross the slash
        { "on:\n  pull_request:\n    branches: ['factory/**', '!factory/changeset-*']\n", CiTriggerOutcome.Never, "leaves out" },
        { "on:\n  pull_request:\n    branches: ['Factory/**']\n", CiTriggerOutcome.Never, "leaves out" }, // case-sensitive
        { "on:\n  pull_request:\n    branches-ignore: ['factory/**']\n", CiTriggerOutcome.Never, $"its pull_request branches-ignore filter leaves out {Base}" },
        { "on:\n  pull_request_target:\n    branches: [main]\n", CiTriggerOutcome.Never, "its pull_request_target branches filter leaves out" },
        { "on:\n  pull_request:\n    branches: [main]\n  push:\n    branches: [main]\n", CiTriggerOutcome.Never, "its pull_request branches filter leaves out" },
        { "on:\n  pull_request:\n    branches: [main]\n  push:\n    tags: ['v*']\n", CiTriggerOutcome.Never, "its pull_request branches filter leaves out" },
        { "on:\n  push:\n    branches: [main]\n", CiTriggerOutcome.Never, $"its push branches filter leaves out {Head}" },
        { "on:\n  push:\n    tags: ['v*']\n", CiTriggerOutcome.Never, "its push trigger is for tags only" },
        // No pull request or push trigger at all.
        { "on: workflow_dispatch\n", CiTriggerOutcome.Never, "no pull_request or push trigger" },
        { "on:\n  schedule:\n    - cron: '0 3 * * *'\n  workflow_dispatch:\n", CiTriggerOutcome.Never, "no pull_request or push trigger" },
        { "on: [release, workflow_call]\n", CiTriggerOutcome.Never, "no pull_request or push trigger" },

        // ---- cannot tell ----
        { "on: [push\n", CiTriggerOutcome.Unknown, "not valid YAML" },
        { "", CiTriggerOutcome.Unknown, "no `on:` key" },
        { "- a\n- b\n", CiTriggerOutcome.Unknown, "no `on:` key" },
        { "name: ci\njobs: {}\n", CiTriggerOutcome.Unknown, "no `on:` key" },
        { "on:\n", CiTriggerOutcome.Unknown, "its `on:` could not be read" },
        { "on: [[push]]\n", CiTriggerOutcome.Unknown, "its `on:` could not be read" },
        { "on:\n  pull_request: [main]\n", CiTriggerOutcome.Unknown, "its pull_request trigger could not be read" },
        { "on:\n  pull_request:\n    branches: [main]\n    branches-ignore: [dev]\n", CiTriggerOutcome.Unknown, "has both branches and branches-ignore" },
        { "on:\n  pull_request:\n    branches: []\n", CiTriggerOutcome.Unknown, "its pull_request branches filter could not be read" },
        { "on:\n  pull_request:\n    branches: [[main]]\n", CiTriggerOutcome.Unknown, "its pull_request branches filter could not be read" },
        { "on:\n  pull_request:\n    branches: {a: b}\n", CiTriggerOutcome.Unknown, "its pull_request branches filter could not be read" },
        { "on:\n  pull_request:\n    branches: ['[main']\n", CiTriggerOutcome.Unknown, "its pull_request branches filter could not be read" },
        { "on:\n  pull_request:\n    branches-ignore: ['!main']\n", CiTriggerOutcome.Unknown, "its pull_request branches-ignore filter could not be read" },
        // An event this check does not model (it may put a check on the head): not certain, so not a stop.
        { "on:\n  workflow_run:\n    workflows: [build]\n", CiTriggerOutcome.Unknown, "a trigger this check does not read" },
        { "on: [create, workflow_dispatch]\n", CiTriggerOutcome.Unknown, "a trigger this check does not read" },
        { "on:\n  pull_request:\n    branches: [main]\n  check_suite:\n", CiTriggerOutcome.Unknown, "a trigger this check does not read" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Each_shape_of_on_is_read_as_github_reads_it(string workflow, CiTriggerOutcome outcome, string why)
    {
        var check = Check(workflow);
        Assert.Equal(outcome, check.Outcome);
        Assert.Contains(why, check.Reason);
    }

    [Theory]
    // pattern list, branch, runs?
    [InlineData("main", "main", true)]
    [InlineData("main", "main2", false)]
    [InlineData("*", "main", true)]
    [InlineData("*", "factory/sc-1", false)]
    [InlineData("**", "factory/sc-1", true)]
    [InlineData("factory/*", "factory/sc-1", true)]
    [InlineData("factory/*", "factory/a/b", false)]
    [InlineData("factory/**", "factory/a/b", true)]
    [InlineData("factory/**", "factory", false)]
    [InlineData("**/sc-*", "factory/sc-12", true)]
    [InlineData("release/v?1", "release/1", true)] // ? : zero or one of the character before
    [InlineData("release/v?1", "release/v1", true)]
    [InlineData("release/v?1", "release/vv1", false)]
    [InlineData("rel+ease", "relllease", true)] // + : one or more of the character before
    [InlineData("rel+ease", "reease", false)]
    [InlineData("release/[0-9].x", "release/3.x", true)]
    [InlineData("release/[0-9].x", "release/a.x", false)]
    [InlineData("release/[0-9]+", "release/123", true)]
    [InlineData("a.b", "aXb", false)] // a dot is a dot
    [InlineData(@"\*star", "*star", true)]
    [InlineData(@"\*star", "xstar", false)]
    [InlineData("releases/**-alpha", "releases/a/b-alpha", true)]
    // Order matters: a later matching pattern overrides an earlier one.
    [InlineData("factory/**|!factory/changeset-*", "factory/changeset-sc-1", false)]
    [InlineData("factory/**|!factory/changeset-*", "factory/sc-1", true)]
    [InlineData("!factory/changeset-*|factory/**", "factory/changeset-sc-1", true)]
    [InlineData("releases/**|!releases/**-alpha|releases/**", "releases/x-alpha", true)]
    [InlineData("!main", "factory/sc-1", false)] // only exclusions: nothing is included
    public void Branch_filters_match_as_github_matches_them(string patterns, string branch, bool runs)
    {
        Assert.Equal(runs, CiTriggers.Matches(patterns.Split('|'), branch));
        // branches-ignore with the same plain patterns is the opposite.
        if (!patterns.Contains('!'))
        {
            Assert.Equal(!runs, CiTriggers.Matches(patterns.Split('|'), branch, ignoring: true));
        }
    }

    [Theory]
    [InlineData("?abc")] // nothing before the ?
    [InlineData("+abc")]
    [InlineData("a**?")]
    [InlineData("[abc")]
    [InlineData("[]")]
    [InlineData("[^a]")]
    [InlineData("a\\")]
    [InlineData("")]
    public void A_pattern_that_cannot_be_translated_is_cannot_tell(string pattern)
    {
        Assert.Null(CiTriggers.Matches([pattern], "main"));
        Assert.Null(CiTriggers.Matches(["main", pattern], "main"));
    }

    [Fact]
    public void A_pattern_longer_than_the_bound_and_a_negation_in_branches_ignore_are_cannot_tell()
    {
        Assert.Null(CiTriggers.Matches([new string('a', 257)], "main"));
        Assert.Null(CiTriggers.Matches(["!main"], "main", ignoring: true));
    }

    [Fact]
    public void The_repository_answer_is_never_only_when_every_workflow_certainly_does_not_run()
    {
        const string filtered = "on:\n  pull_request:\n    branches: [main]\n";
        const string runs = "on: pull_request\n";
        const string broken = "on: [push\n";
        const string manual = "on: workflow_dispatch\n";

        Assert.Equal(CiTriggerOutcome.Never, Check(filtered, manual).Outcome);
        // One workflow that runs is enough, whatever else there is.
        Assert.Equal(CiTriggerOutcome.Runs, Check(filtered, broken, runs).Outcome);
        // One that cannot be read leaves it unknown: it might be the one that runs.
        Assert.Equal(CiTriggerOutcome.Unknown, Check(filtered, broken).Outcome);
        Assert.Equal(CiTriggerOutcome.Unknown, CiTriggers.Check([new WorkflowFile(File, filtered), new WorkflowFile(".github/workflows/b.yml", null, "GitHub answered 500")], Base, Head).Outcome);
        // No workflow file at all: CI may be another service's.
        var none = CiTriggers.Check([], Base, Head);
        Assert.Equal(CiTriggerOutcome.Unknown, none.Outcome);
        Assert.Contains("no workflow file under .github/workflows/", none.Reason);
    }

    [Fact]
    public void A_stop_names_every_file_read_and_the_exact_fix()
    {
        var check = Check("on:\n  pull_request:\n    branches: [main]\n", "on: workflow_dispatch\n", "on:\n  pull_request_target:\n    branches-ignore: ['factory/**']\n");

        Assert.Equal(CiTriggerOutcome.Never, check.Outcome);
        Assert.Equal($"no CI workflow runs for a pull request into {Base}. Workflows read on the default branch: "
            + $"{File} (its pull_request branches filter leaves out {Base}); .github/workflows/w1.yml (no pull_request or push trigger); "
            + $".github/workflows/w2.yml (its pull_request_target branches-ignore filter leaves out {Base}). "
            + $"Fix: add `factory/**` to the pull_request branches filter of {File}, or take the pattern that matches {Base} out of the "
            + "pull_request_target branches-ignore filter of .github/workflows/w2.yml", check.Reason);
        Assert.Equal(CiTriggerCheck.BlockedPrefix + check.Reason, check.ToDetail());

        // No workflow has a pull request trigger to widen: the fix is to add one.
        var manual = Check("on: workflow_dispatch\n");
        Assert.Equal(CiTriggerOutcome.Never, manual.Outcome);
        Assert.Contains($"Fix: add a pull_request trigger with no branches filter, or one whose branches list has `factory/**`, to a CI workflow (e.g. {File})", manual.Reason);
    }

    [Fact]
    public void A_reason_repeats_nothing_of_a_workflows_text_and_no_unusual_file_name()
    {
        var check = CiTriggers.Check([new WorkflowFile(".github/workflows/@everyone <b>.yml", "on:\n  pull_request:\n    branches: ['@everyone-secret-branch']\n")], Base, Head);

        Assert.Equal(CiTriggerOutcome.Never, check.Outcome);
        Assert.DoesNotContain("@everyone", check.Reason);
        Assert.Contains("(a workflow file with an unusual name)", check.Reason);
        // A read error is bounded.
        var unread = CiTriggers.Check([new WorkflowFile(File, null, new string('x', 5000))], Base, Head);
        Assert.True(unread.Reason.Length < 600, unread.Reason.Length.ToString());
    }

    [Theory]
    [InlineData("blocked: o/r: no CI workflow runs", StepOutcome.GateRejected)]
    [InlineData("warning: o/r: whether any CI workflow runs cannot be told", StepOutcome.Failed)]
    [InlineData(null, StepOutcome.Failed)]
    public void A_ci_triggers_row_has_a_typed_outcome(string? detail, StepOutcome outcome)
    {
        Assert.Equal(outcome, StepOutcomes.Of(WorkState.Plan, WorkState.Plan, RunPipeline.Steps.CiTriggers, detail));
        Assert.Equal(outcome == StepOutcome.GateRejected, CiTriggerCheck.IsBlocked(detail));
    }

    [Fact]
    public void Only_a_certain_never_is_recorded_as_blocked()
    {
        Assert.StartsWith(CiTriggerCheck.BlockedPrefix, new CiTriggerCheck(CiTriggerOutcome.Never, "x").ToDetail());
        Assert.StartsWith(CiTriggerCheck.WarningPrefix, new CiTriggerCheck(CiTriggerOutcome.Unknown, "x").ToDetail());
    }

    // ---- the gate's GitHub client ----

    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private const string Repo = "/repos/michaeltrefry/dark-factory-sandbox";

    private static (GitHubGate Gate, FakeApi Api) Gate(Action<FakeApi> routes)
    {
        var api = new FakeApi()
            .On($"GET {Repo}/installation", HttpStatusCode.OK, """{"id":555}""")
            .On("POST /app/installations/555/access_tokens", _ => FakeApi.Json(HttpStatusCode.Created,
                $$"""{"token":"ghs_gate","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}"""));
        routes(api);
        var client = api.Client("https://api.github.com/");
        return (new GitHubGate(client, new GitHubApp(client, "4242", Key.ExportRSAPrivateKeyPem(), TimeProvider.System)), api);
    }

    [Fact]
    public async Task Reads_the_default_branchs_workflow_files_with_a_read_only_token()
    {
        var (gate, api) = Gate(a => a
            .On($"GET {Repo}/contents/.github/workflows", HttpStatusCode.OK, """
                [{"name":"release.yaml","path":".github/workflows/release.yaml","type":"file","size":20},
                 {"name":"ci.yml","path":".github/workflows/ci.yml","type":"file","size":30},
                 {"name":"README.md","path":".github/workflows/README.md","type":"file","size":5},
                 {"name":"huge.yml","path":".github/workflows/huge.yml","type":"file","size":9999999},
                 {"name":"gone.yml","path":".github/workflows/gone.yml","type":"file","size":3},
                 {"name":"scripts","path":".github/workflows/scripts","type":"dir","size":0}]
                """)
            .On($"GET {Repo}/contents/.github/workflows/ci.yml", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("on: pull_request\n") })
            .On($"GET {Repo}/contents/.github/workflows/release.yaml", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("on: release\n") })
            .On($"GET {Repo}/contents/.github/workflows/gone.yml", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }));

        var files = await gate.GetWorkflowsAsync(Sandbox, CancellationToken.None);

        Assert.Equal(
        [
            new WorkflowFile(".github/workflows/ci.yml", "on: pull_request\n"),
            new WorkflowFile(".github/workflows/gone.yml", null, "GitHub answered 500"),
            new WorkflowFile(".github/workflows/huge.yml", null, $"larger than {GitHubGate.MaxWorkflowBytes} bytes"),
            new WorkflowFile(".github/workflows/release.yaml", "on: release\n"),
        ], files);
        // The default branch (no ref), raw file bodies, and a token that can only read.
        Assert.All(api.Requests.Where(r => r.PathAndQuery.Contains("/contents/")), r => Assert.DoesNotContain("ref=", r.PathAndQuery));
        Assert.Contains("raw", api.Requests.First(r => r.PathAndQuery.EndsWith("/ci.yml")).Headers["Accept"]);
        var permissions = System.Text.Json.JsonDocument.Parse(api.Requests.First(r => r.PathAndQuery.EndsWith("/access_tokens")).Body!).RootElement.GetProperty("permissions");
        Assert.Equal("read", permissions.GetProperty("contents").GetString());
        Assert.All(permissions.EnumerateObject(), p => Assert.Equal("read", p.Value.GetString()));
        // What was read decides: one workflow runs for every pull request.
        Assert.Equal(CiTriggerOutcome.Runs, CiTriggers.Check(files, "factory/changeset-sc-1", "factory/sc-1").Outcome);
    }

    [Fact]
    public async Task A_repository_with_no_workflow_directory_has_no_workflow_files_and_an_unreadable_listing_throws()
    {
        var (none, _) = Gate(a => a.On($"GET {Repo}/contents/.github/workflows", HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        Assert.Empty(await none.GetWorkflowsAsync(Sandbox, CancellationToken.None));

        var (refused, _) = Gate(a => a.On($"GET {Repo}/contents/.github/workflows", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.GetWorkflowsAsync(Sandbox, CancellationToken.None));
        Assert.Contains("403", ex.Message);

        // `.github/workflows` that is a file, not a directory.
        var (file, _) = Gate(a => a.On($"GET {Repo}/contents/.github/workflows", HttpStatusCode.OK, """{"name":"workflows","type":"file","size":1}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => file.GetWorkflowsAsync(Sandbox, CancellationToken.None));
    }

    [Fact]
    public async Task More_workflow_files_than_the_bound_are_reported_as_not_read_in_full()
    {
        var entries = string.Join(",", Enumerable.Range(0, GitHubGate.MaxWorkflows + 3).Select(i => $$"""{"name":"w{{i:000}}.yml","type":"file","size":1}"""));
        var (gate, api) = Gate(a =>
        {
            a.On($"GET {Repo}/contents/.github/workflows", HttpStatusCode.OK, $"[{entries}]");
            foreach (var i in Enumerable.Range(0, GitHubGate.MaxWorkflows + 3))
            {
                a.On($"GET {Repo}/contents/.github/workflows/w{i:000}.yml",
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("on:\n  pull_request:\n    branches: [main]\n") });
            }
        });

        var files = await gate.GetWorkflowsAsync(Sandbox, CancellationToken.None);

        Assert.Equal(GitHubGate.MaxWorkflows + 1, files.Count);
        Assert.Equal(new WorkflowFile(".github/workflows", null, $"more than {GitHubGate.MaxWorkflows} workflow files, the first {GitHubGate.MaxWorkflows} read"), files[^1]);
        // Not every file was read, so it is never a certain "no workflow runs".
        Assert.Equal(CiTriggerOutcome.Unknown, CiTriggers.Check(files, "factory/changeset-sc-1", "factory/sc-1").Outcome);
        Assert.Equal(GitHubGate.MaxWorkflows + 1, api.Requests.Count(r => r.PathAndQuery.Contains("/contents/")));
    }
}
