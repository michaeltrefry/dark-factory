using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25873: <c>factory/gate.yaml</c> version 4 — a changeset pull request's own size limits (<c>max_changeset_lines</c>,
/// <c>max_changeset_files</c>), applied by <c>risk-threshold</c> only to a pull request the ledger knows as a plan's changeset.
/// </summary>
public class ChangesetPolicyTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly PullFacts Pull = new(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", "base", null);
    private static readonly CiFacts Green = new(Head, [new CheckFact("build", true, "success")]);
    private static readonly ReviewVerdict Full = ReviewPanel.Decide(Head, [],
        ReviewRoles.All.Select(role => new RoleReview(role, "claude-opus-5-5", "high", "s", "p", [], "ok")).ToList());
    private static readonly NewTestsResult PassingTests = new("base", Head, NewTestsOutcome.Pass,
        "1 new test(s) fail on the base and pass on the head: W.Tests.New", "dotnet-xunit", "ran", "ran", []);

    private static readonly string V2 = TestPolicies.Standard();
    private static readonly string V3 = TestPolicies.V3("high", "off");
    /// <summary>Ordinary limits 400 lines / 20 files; a changeset's 1000 lines / 30 files.</summary>
    private static readonly string V4 = TestPolicies.V4(1000, 30);

    private static string Lines(int lines) => TestPolicies.Diff("src/a.cs", lines);

    private static string Files(int files) => string.Concat(Enumerable.Range(0, files).Select(i => TestPolicies.Diff($"src/a{i}.cs")));

    private static GateDecision Evaluate(string policy, string diff, bool changeset, int fixRounds = 0, CodeGraphRisk? risk = null)
    {
        var change = new ChangeFacts(diff, null, fixRounds, changeset);
        return MergeGate.Evaluate(policy, null, TestPolicies.Counting(Pull, change), change, Green, [Full], PassingTests, risk ?? TestRisk.None(Head, diff));
    }

    [Fact]
    public void A_version_4_policy_parses_its_changeset_limits_beside_the_ordinary_ones()
    {
        Assert.Equal(new RiskThreshold(400, 20, 3, BlastRadius.High, null, 1000, 30), GatePolicy.Parse(V4).Risk);
        // A non-default value is the one read.
        var other = GatePolicy.Parse(TestPolicies.V4(77, 5)).Risk;
        Assert.Equal((77, 5), (other.MaxChangesetLines, other.MaxChangesetFiles));
        Assert.Equal((77, "max_changeset_lines"), other.LinesLimit(changeset: true));
        Assert.Equal((5, "max_changeset_files"), other.FilesLimit(changeset: true));
        Assert.Equal((400, "max_changed_lines"), other.LinesLimit(changeset: false));
        Assert.Equal((20, "max_changed_files"), other.FilesLimit(changeset: false));
        Assert.Equal(4, GatePolicy.Version);
        Assert.Equal([2, 3, 4], GatePolicy.Versions);
    }

    [Fact]
    public void Versions_2_and_3_parse_as_before_with_no_changeset_limit()
    {
        Assert.Equal(new RiskThreshold(400, 20, 3), GatePolicy.Parse(V2).Risk);
        Assert.Equal(new RiskThreshold(400, 20, 3, BlastRadius.High), GatePolicy.Parse(V3).Risk);
        // With no changeset limit a changeset is held to the ordinary one.
        Assert.Equal((400, "max_changed_lines"), GatePolicy.Parse(V3).Risk.LinesLimit(changeset: true));
        Assert.Equal((20, "max_changed_files"), GatePolicy.Parse(V2).Risk.FilesLimit(changeset: true));
    }

    [Fact]
    public void The_changeset_limits_are_independent_of_the_ordinary_ones()
    {
        // No cross-key rule: a changeset limit below the per-story one is a tighter gate, which a policy may always ask for.
        var tighter = GatePolicy.Parse(TestPolicies.V4(maxChangesetLines: 1, maxChangesetFiles: 1)).Risk;
        Assert.Equal((1, 1, 400, 20), (tighter.MaxChangesetLines, tighter.MaxChangesetFiles, tighter.MaxChangedLines, tighter.MaxChangedFiles));
    }

    public static TheoryData<string, string> InvalidV4() => new()
    {
        { V4.Replace("\n  max_changeset_lines: 1000", "", StringComparison.Ordinal), "risk is missing 'max_changeset_lines'" },
        { V4.Replace("\n  max_changeset_files: 30", "", StringComparison.Ordinal), "risk is missing 'max_changeset_files'" },
        { V4.Replace("\n  max_blast_radius: high", "", StringComparison.Ordinal), "risk is missing 'max_blast_radius'" },
        { V4.Replace("\n  max_hotspot: off", "", StringComparison.Ordinal), "risk is missing 'max_hotspot'" },
        { TestPolicies.V4(0, 30), "risk.max_changeset_lines must be a whole number of at least 1" },
        { TestPolicies.V4(1000, 0), "risk.max_changeset_files must be a whole number of at least 1" },
        { V4.Replace("max_changeset_lines: 1000", "max_changeset_lines: -5", StringComparison.Ordinal), "risk.max_changeset_lines must be a whole number" },
        { V4.Replace("max_changeset_lines: 1000", "max_changeset_lines: off", StringComparison.Ordinal), "risk.max_changeset_lines must be a whole number" },
        { V4.Replace("max_changeset_files: 30", "max_changeset_files: 1.5", StringComparison.Ordinal), "risk.max_changeset_files must be a whole number" },
        { V4.Replace("max_changeset_files: 30", "max_changeset_files: [30]", StringComparison.Ordinal), "risk.max_changeset_files must be a value" },
        { V4 + "\n  max_changeset_commits: 9", "unknown key 'max_changeset_commits' in risk" },
        // Each version's key set is strict: a changeset key is unknown to versions 2 and 3.
        { V2 + "\n  max_changeset_lines: 1000", "unknown key 'max_changeset_lines' in risk" },
        { V2 + "\n  max_changeset_files: 30", "unknown key 'max_changeset_files' in risk" },
        { V3 + "\n  max_changeset_lines: 1000", "unknown key 'max_changeset_lines' in risk" },
        { V3 + "\n  max_changeset_files: 30", "unknown key 'max_changeset_files' in risk" },
        { V3 + "\n  max_changeset_lines: 1000\n  max_changeset_files: 30", "unknown key 'max_changeset_lines' in risk" },
        { V4.Replace("version: 4", "version: 5", StringComparison.Ordinal), "version must be 2, 3 or 4" },
    };

    [Theory]
    [MemberData(nameof(InvalidV4))]
    public void A_policy_with_a_missing_malformed_or_misplaced_changeset_limit_is_invalid(string yaml, string reason)
    {
        var ex = Assert.Throws<GatePolicyException>(() => GatePolicy.Parse(yaml));
        Assert.Contains(reason, ex.Message);
        // Invalid means no merge (fail closed), for a changeset pull request as for any other.
        Assert.Equal(GateOutcome.Blocked, Evaluate(yaml, Lines(1), changeset: true).Outcome);
        Assert.Equal(GateOutcome.Blocked, Evaluate(yaml, Lines(1), changeset: false).Outcome);
    }

    [Fact]
    public void Under_version_4_a_changeset_pull_request_is_sized_by_the_changeset_limits_and_says_so()
    {
        // Over max_changed_lines (400), within max_changeset_lines (1000).
        var lines = Evaluate(V4, Lines(401), changeset: true);
        Assert.Equal(GateOutcome.Merge, lines.Outcome);
        Assert.Contains("401 changed lines within max_changeset_lines 1000, 1 files within max_changeset_files 30", lines.Detail);
        Assert.StartsWith($"Merge {Head[..12]} (changeset pull request): ", lines.Detail);
        Assert.Equal(GateOutcome.Merge, Evaluate(V4, Lines(1000), changeset: true).Outcome);
        // Over max_changed_files (20), within max_changeset_files (30).
        Assert.Equal(GateOutcome.Merge, Evaluate(V4, Files(21), changeset: true).Outcome);
        Assert.Equal(GateOutcome.Merge, Evaluate(V4, Files(30), changeset: true).Outcome);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void Under_version_4_a_changeset_pull_request_over_its_own_limit_fails_naming_that_limit()
    {
        var lines = Evaluate(V4, Lines(1001), changeset: true);
        Assert.Equal(GateOutcome.Blocked, lines.Outcome);
        Assert.Equal("risk threshold: 1001 changed lines exceed max_changeset_lines 1000 (a changeset pull request)", Assert.Single(lines.Reasons));
        Assert.StartsWith($"Blocked {Head[..12]} (changeset pull request): ", lines.Detail);

        var files = Evaluate(V4, Files(31), changeset: true);
        Assert.Equal(GateOutcome.Blocked, files.Outcome);
        Assert.Equal("risk threshold: 31 changed files exceed max_changeset_files 30 (a changeset pull request)", Assert.Single(files.Reasons));

        // A changeset limit below the ordinary one binds a changeset (and only a changeset).
        var tighter = TestPolicies.V4(maxChangesetLines: 100, maxChangesetFiles: 2);
        Assert.Contains("200 changed lines exceed max_changeset_lines 100", Evaluate(tighter, Lines(200), changeset: true).Detail);
        Assert.Contains("3 changed files exceed max_changeset_files 2", Evaluate(tighter, Files(3), changeset: true).Detail);
        Assert.Equal(GateOutcome.Merge, Evaluate(tighter, Lines(200), changeset: false).Outcome);
    }

    [Fact]
    public void Under_version_4_an_ordinary_pull_request_is_sized_by_the_ordinary_limits_as_before()
    {
        var lines = Evaluate(V4, Lines(401), changeset: false);
        Assert.Equal(GateOutcome.Blocked, lines.Outcome);
        Assert.Equal("risk threshold: 401 changed lines exceed max_changed_lines 400", Assert.Single(lines.Reasons));
        Assert.DoesNotContain("changeset", lines.Detail);
        Assert.Equal("risk threshold: 21 changed files exceed max_changed_files 20", Assert.Single(Evaluate(V4, Files(21), changeset: false).Reasons));

        var within = Evaluate(V4, Lines(400), changeset: false);
        Assert.Equal(GateOutcome.Merge, within.Outcome);
        Assert.Contains("risk within threshold (400 changed lines, 1 files, 0 fix rounds, ", within.Detail);
        Assert.DoesNotContain("changeset", within.Detail);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Under_versions_2_and_3_a_changeset_pull_request_is_held_to_the_ordinary_limits(int version)
    {
        var policy = version == 2 ? V2 : V3;
        foreach (var changeset in new[] { false, true })
        {
            Assert.Equal(GateOutcome.Merge, Evaluate(policy, Lines(400), changeset).Outcome);
            Assert.Equal(GateOutcome.Merge, Evaluate(policy, Files(20), changeset).Outcome);
            var lines = Evaluate(policy, Lines(401), changeset);
            Assert.Equal(GateOutcome.Blocked, lines.Outcome);
            Assert.StartsWith("risk threshold: 401 changed lines exceed max_changed_lines 400", Assert.Single(lines.Reasons));
            Assert.StartsWith("risk threshold: 21 changed files exceed max_changed_files 20", Assert.Single(Evaluate(policy, Files(21), changeset).Reasons));
        }
        // An ordinary pull request's words are exactly the earlier ones; a changeset's say which limits held it and why.
        Assert.Equal("risk threshold: 401 changed lines exceed max_changed_lines 400", Assert.Single(Evaluate(policy, Lines(401), changeset: false).Reasons));
        Assert.EndsWith("(a changeset pull request, held to the ordinary limits: this policy's version has no max_changeset_lines/max_changeset_files)",
            Assert.Single(Evaluate(policy, Lines(401), changeset: true).Reasons));
    }

    [Fact]
    public void The_rest_of_the_risk_threshold_is_the_same_for_a_changeset_pull_request()
    {
        foreach (var changeset in new[] { false, true })
        {
            // Fix rounds.
            Assert.Equal("risk threshold: 4 fix rounds exceed max_fix_rounds 3", Assert.Single(Evaluate(V4, Lines(1), changeset, fixRounds: 4).Reasons));
            // CodeGraph's blast radius floor: a critical file always fails.
            var critical = Evaluate(V4, Lines(1), changeset, risk: TestRisk.With(Head, Lines(1), _ => BlastRadius.Critical, _ => 0));
            Assert.Equal(GateOutcome.Blocked, critical.Outcome);
            Assert.Contains("blast radius", Assert.Single(critical.Reasons));
            // The hotspot limit, where the policy sets one.
            var hot = Evaluate(V4.Replace("max_hotspot: off", "max_hotspot: 0.5", StringComparison.Ordinal), Lines(1), changeset,
                risk: TestRisk.With(Head, Lines(1), _ => BlastRadius.Low, _ => 0.9));
            Assert.Contains("over max_hotspot 0.5", Assert.Single(hot.Reasons));
            // CodeGraph not read at all still fails the check.
            var change = new ChangeFacts(Lines(1), null, 0, changeset);
            Assert.Equal(GateOutcome.Blocked,
                MergeGate.Evaluate(V4, null, TestPolicies.Counting(Pull, change), change, Green, [Full], PassingTests).Outcome);
        }
    }

    [Fact]
    public void The_changeset_fact_skips_no_other_rule()
    {
        // Sealed and protected paths, a missing verdict and a free path's lighter checks are judged as for any pull request.
        Assert.Contains("touches sealed path(s)", Evaluate(V4, TestPolicies.Diff(".github/workflows/ci.yml"), changeset: true).Detail);
        Assert.Contains("touches protected path(s)", Evaluate(V4, TestPolicies.Diff("src/auth/Login.cs"), changeset: true).Detail);
        var change = new ChangeFacts(Lines(1), null, 0, Changeset: true);
        Assert.Equal(GateOutcome.ReviewHead, MergeGate.Evaluate(V4, null, TestPolicies.Counting(Pull, change), change, Green, []).Outcome);
        // A free-tier change needs no risk-threshold whichever limits there are: its size is not judged, for either kind.
        Assert.Equal(GateOutcome.Merge, Evaluate(V4, TestPolicies.Diff("docs/a.md", 5000), changeset: true).Outcome);
        Assert.Equal(GateOutcome.Merge, Evaluate(V4, TestPolicies.Diff("docs/a.md", 5000), changeset: false).Outcome);
    }

    [Fact]
    public void A_gate_row_of_a_changeset_decision_keeps_its_typed_outcome()
    {
        Assert.Equal(StepOutcome.Passed, StepOutcomes.Of(WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.GateDecision,
            Evaluate(V4, Lines(401), changeset: true).Detail));
        Assert.Equal(StepOutcome.GateRejected, StepOutcomes.Of(WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.GateDecision,
            Evaluate(V4, Lines(1001), changeset: true).Detail));
    }
}

/// <summary>
/// sc-25873 through the real pipeline: the gate learns "this is a changeset pull request" from the ledger (a plan item with a pushed
/// changeset), never from a branch name; and a plan whose repository's CI cannot report on its stories is stopped before it is spent.
/// </summary>
public partial class ChangesetPipelineTests
{
    /// <summary>
    /// A version 4 policy whose free tier (the simulated stories change <c>docs/</c>) also requires <c>risk-threshold</c>, so the
    /// size limits are judged: the ordinary ones 1 line / 1 file — one story's worth — and the changeset's as given.
    /// </summary>
    private static string SizedPolicy(int maxChangesetLines, int maxChangesetFiles, int version = 4)
    {
        var policy = version == 4 ? TestPolicies.V4(maxChangesetLines, maxChangesetFiles, maxLines: 1, maxFiles: 1)
            : TestPolicies.Standard(maxLines: 1, maxFiles: 1).Replace("version: 2", "version: 3", StringComparison.Ordinal)
                + "\n  max_blast_radius: high\n  max_hotspot: off";
        const string free = "    paths: [docs/, tests/]\n    checks: [ci-green, review-pass]";
        Assert.Contains(free, policy);
        return policy.Replace(free, "    paths: [docs/, tests/]\n    checks: [ci-green, review-pass, risk-threshold]", StringComparison.Ordinal);
    }

    /// <summary>
    /// A harness whose repository has <paramref name="policy"/>, and whose CodeGraph names the simulated commits it is asked about
    /// (they are names, not SHAs, so the stock answers name none), so <c>risk-threshold</c> has its inputs and judges the size.
    /// </summary>
    private static Harness Sized(string policy)
    {
        var h = new Harness();
        h.Repo.PolicyText = policy;
        h.CodeGraph.Impact = (path, sha) => C7Answers.ParsedImpactFile("Sandbox", path, sha, false, "none") with { CommitSha = sha };
        return h;
    }

    private static async Task<string> LastGate(Harness h, int id = PlanId) => (await h.Steps(id, RunPipeline.Steps.GateDecision))[^1]!;

    [Fact]
    public async Task A_plans_changeset_pull_request_is_sized_by_the_changeset_limits_and_its_stories_by_the_ordinary_ones()
    {
        var h = Sized(SizedPolicy(maxChangesetLines: 10, maxChangesetFiles: 10));

        // Each story changes one file by one line: within the ordinary limits, into a base named factory/changeset-….
        var ids = await h.ChangesetOpen();
        foreach (var id in ids)
        {
            var story = await LastGate(h, id);
            Assert.StartsWith("Merge ", story);
            Assert.DoesNotContain("changeset", story);
            Assert.Contains("risk within threshold (1 changed lines, 1 files, ", story);
        }

        // The changeset pull request carries both: two lines in two files, over the ordinary limits and within its own.
        var merged = await h.Run();

        Ended(h, merged, WorkState.Watch);
        var gate = await LastGate(h);
        Assert.StartsWith("Merge ", gate);
        Assert.Contains(GateDecision.ChangesetMark, gate);
        Assert.Contains("2 changed lines within max_changeset_lines 10, 2 files within max_changeset_files 10", gate);
        Assert.Equal((ChangesetBranch, Main), (h.Repo.Merges[^1].Head, h.Repo.Merges[^1].Base));
    }

    [Fact]
    public async Task A_changeset_pull_request_over_its_changeset_limit_escalates_the_plan_item_naming_that_limit()
    {
        var h = Sized(SizedPolicy(maxChangesetLines: 1, maxChangesetFiles: 10));
        await h.ChangesetOpen();

        var blocked = await h.Run();

        Assert.Equal(WorkState.Escalated, blocked.State);
        Assert.Contains("risk threshold: 2 changed lines exceed max_changeset_lines 1 (a changeset pull request)", blocked.Error);
        Assert.DoesNotContain("max_changed_lines", blocked.Error);
        Assert.DoesNotContain(h.Repo.Merges, m => m.Base == Main);
        Assert.Equal(StepOutcome.GateRejected, (await h.Rows()).Last(r => r.Step == RunPipeline.Steps.GateDecision).Outcome);
    }

    [Fact]
    public async Task Under_a_version_3_policy_a_changeset_pull_request_is_held_to_the_ordinary_limits()
    {
        var h = Sized(SizedPolicy(0, 0, version: 3));
        await h.ChangesetOpen();

        var blocked = await h.Run();

        Assert.Equal(WorkState.Escalated, blocked.State);
        Assert.Contains("risk threshold: 2 changed lines exceed max_changed_lines 1 (a changeset pull request, held to the ordinary limits", blocked.Error);
        Assert.Contains("risk threshold: 2 changed files exceed max_changed_files 1", blocked.Error);
        Assert.DoesNotContain(h.Repo.Merges, m => m.Base == Main);
    }

    [Fact]
    public async Task A_story_whose_pull_request_merely_targets_a_changeset_branch_gets_the_ordinary_limits()
    {
        // The changeset limits would let two lines through; the story's pull request — head factory/sc-<id>, base
        // factory/changeset-sc-500 — is no changeset by the ledger, so the ordinary limit of one line holds it.
        var h = Sized(SizedPolicy(maxChangesetLines: 10, maxChangesetFiles: 10));
        h.Repo.Work = branch => [$"docs/{branch.Replace('/', '-')}.md|one", $"docs/{branch.Replace('/', '-')}.md|two"];
        var ids = await h.Plan(Two);
        Assert.True((await h.Run()).Succeeded, h.Logged);

        var story = await h.Run(ids[0]);

        Assert.Equal(WorkState.Escalated, story.State);
        Assert.Contains("risk threshold: 2 changed lines exceed max_changed_lines 1", story.Error);
        Assert.DoesNotContain("changeset pull request", story.Error);
        Assert.DoesNotContain(GateDecision.ChangesetMark, await LastGate(h, ids[0]));
        Assert.Empty(h.Repo.Merges);
    }

    [Fact]
    public async Task An_item_of_no_plan_gets_the_ordinary_limits_whatever_changeset_limits_the_policy_has()
    {
        var h = Sized(SizedPolicy(maxChangesetLines: 10, maxChangesetFiles: 10));
        h.Repo.Work = branch => [$"docs/{branch.Replace('/', '-')}.md|one", $"docs/{branch.Replace('/', '-')}.md|two"];
        h.Board.Add(77, FakeShortcutBoard.FactoryTeam, epic: 42, name: "A story of no plan");

        var outcome = await h.Run(77);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("risk threshold: 2 changed lines exceed max_changed_lines 1", outcome.Error);
        Assert.DoesNotContain(GateDecision.ChangesetMark, await LastGate(h, 77));
    }

    // ---- the CI-trigger check in Plan ----

    private const string CiFile = ".github/workflows/ci.yml";

    private static IReadOnlyList<WorkflowFile> Workflow(string text, string path = CiFile) => [new WorkflowFile(path, text)];

    /// <summary>Runs the plan item with a planner that would answer <see cref="Two"/>.</summary>
    private static Task<RunOutcome> PlanRun(Harness h)
    {
        h.Planner ??= new FakeWorker(Reports(new WorkerResult("plan-sess", 0, false, "success", Two, ""))) { Tools = WorkerTools.ReadOnly };
        return h.Run();
    }

    private static List<JsonNode?> StoriesCreated(Harness h) =>
        [.. h.Board.Requests.Where(r => r.Method == HttpMethod.Post && r.PathAndQuery.StartsWith("/api/v3/stories", StringComparison.Ordinal)
            && !r.PathAndQuery.Contains("/comments", StringComparison.Ordinal)).Select(r => JsonNode.Parse(r.Body!))];

    [Fact]
    public async Task A_plan_for_a_repository_whose_only_workflow_filters_pull_requests_to_main_is_escalated_before_anything_is_spent()
    {
        var h = new Harness();
        h.Repo.Workflows = Workflow("name: ci\non:\n  pull_request:\n    branches: [main]\njobs: {}\n");

        var outcome = await PlanRun(h);

        Assert.Equal(WorkState.Escalated, outcome.State);
        // The reason names the repository, the changeset branch, the workflow files read and the exact fix.
        Assert.Contains($"in {Sandbox}: no CI workflow runs for a pull request into {ChangesetBranch}", outcome.Error);
        Assert.Contains($"Fix: add `factory/**` to the pull_request branches filter of {CiFile}. Workflows read on the default branch: "
            + $"{CiFile} (its pull_request branches filter leaves out {ChangesetBranch}). Every story of the plan would open its pull request "
            + $"against {ChangesetBranch}, get no CI check", outcome.Error);
        // Nothing was spent or created: no planner session, no story, no changeset branch.
        Assert.Empty(h.Planner!.Calls);
        Assert.Empty(StoriesCreated(h));
        Assert.Empty(h.Repo.BranchesCreated);
        Assert.DoesNotContain(ChangesetBranch, h.Repo.Branches.Keys);
        var rows = await h.Rows();
        Assert.DoesNotContain(rows, r => r.Step is RunPipeline.Steps.Plan or RunPipeline.Steps.PlanCreating or RunPipeline.Steps.PlanStories or RunPipeline.Steps.Changeset);
        var row = Assert.Single(rows, r => r.Step == RunPipeline.Steps.CiTriggers);
        Assert.StartsWith($"{CiTriggerCheck.BlockedPrefix}{Sandbox}: no CI workflow runs", row.Detail);
        Assert.Equal(StepOutcome.GateRejected, row.Outcome);
        // The board item carries it.
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.Contains("add `factory/**` to the pull_request branches filter", comment);

        // Fixed on the default branch, the plan item's next run plans as usual.
        h.Repo.Workflows = Workflow("name: ci\non:\n  pull_request:\n    branches: [main, 'factory/**']\njobs: {}\n");
        var planned = await PlanRun(h);
        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        Assert.Single(h.Planner.Calls);
        Assert.Equal(2, StoriesCreated(h).Count);
        Assert.Contains(ChangesetBranch, h.Repo.Branches.Keys);
        Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CiTriggers);
    }

    [Theory]
    [InlineData("on:\n  pull_request:\n    branches: [main, 'factory/**']\n")]
    [InlineData("on:\n  pull_request:\n")]
    [InlineData("on: pull_request\n")]
    [InlineData("on: [push, pull_request]\n")]
    // A push workflow reports on the story's own branch, whatever its pull_request filter says.
    [InlineData("on:\n  pull_request:\n    branches: [main]\n  push:\n")]
    public async Task A_plan_for_a_repository_whose_ci_runs_for_its_stories_plans_normally_and_records_nothing(string workflow)
    {
        var h = new Harness();
        h.Repo.Workflows = Workflow(workflow);

        var planned = await PlanRun(h);

        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.CiTriggers);
        Assert.Equal(1, h.Repo.WorkflowReads);
        Assert.Contains($"[plan] CI triggers of {Sandbox}: {CiFile} runs for a pull request into {ChangesetBranch}", h.Logged);
        // Later runs of the plan (its changeset branch is on origin) do not read the workflows again.
        Assert.True((await h.Run()).Succeeded, h.Logged);
        Assert.Equal(1, h.Repo.WorkflowReads);
    }

    [Fact]
    public async Task One_workflow_that_runs_is_enough_whatever_the_others_say()
    {
        var h = new Harness();
        h.Repo.Workflows =
        [
            new WorkflowFile(".github/workflows/a.yml", "on:\n  pull_request:\n    branches: [main]\n"),
            new WorkflowFile(".github/workflows/b.yml", "on: [push\n"),
            new WorkflowFile(".github/workflows/c.yml", "on:\n  pull_request:\n    branches: ['factory/**']\n"),
        ];

        var planned = await PlanRun(h);

        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.CiTriggers);
    }

    [Theory]
    [InlineData("name: deploy\non: workflow_dispatch\njobs: {}\n", "no pull_request or push trigger")]
    [InlineData("on:\n  schedule:\n    - cron: '0 3 * * *'\n", "no pull_request or push trigger")]
    [InlineData("on: [release, workflow_call]\n", "no pull_request or push trigger")]
    [InlineData("on:\n  push:\n    tags: ['v*']\n", "its push trigger is for tags only")]
    public async Task A_repository_whose_workflows_have_no_pull_request_or_push_branch_trigger_plans_normally_with_a_warning(string workflow, string why)
    {
        // A dispatch-only deploy, a scheduled bot, a tag release: nothing says its CI is GitHub Actions (it may be another
        // service's), so the plan is not refused with a fix that would be wrong for it.
        var h = new Harness();
        h.Repo.Workflows = Workflow(workflow);

        var planned = await PlanRun(h);

        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        var row = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CiTriggers);
        Assert.Equal($"{CiTriggerCheck.WarningPrefix}{Sandbox}: whether any CI workflow runs for a pull request into {ChangesetBranch} cannot be told: "
            + "no workflow has a pull_request trigger or a push trigger for branches, so CI may be another service's. "
            + $"Workflows read on the default branch: {CiFile} ({why})", row.Detail);
        Assert.Equal(StepOutcome.Failed, row.Outcome);
        Assert.Contains($"[plan] warning: CI triggers of {Sandbox}: ", h.Logged);
        Assert.Single(h.Planner!.Calls);
        Assert.Equal(2, StoriesCreated(h).Count);
        Assert.Contains(ChangesetBranch, h.Repo.Branches.Keys);
        Assert.Empty(h.Comments(PlanId));
    }

    [Fact]
    public async Task A_plan_whose_changeset_pull_request_could_get_no_ci_is_escalated_before_anything_is_spent_with_the_fix_naming_the_default_branch()
    {
        // Every story's pull request (base: the changeset branch) gets CI; the one that takes the changeset to the default branch
        // does not. The default branch is the one GitHub names in the same read, not a guess.
        var h = new Harness();
        h.Repo.WorkflowsDefaultBranch = "trunk";
        h.Repo.Workflows = Workflow("name: ci\non:\n  pull_request:\n    branches: ['factory/**']\njobs: {}\n");

        var outcome = await PlanRun(h);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains($"in {Sandbox}: no CI workflow runs for a pull request into trunk. Fix: add `trunk` to the pull_request branches filter of {CiFile}. "
            + $"Workflows read on the default branch: {CiFile} (its pull_request branches filter leaves out trunk). "
            + $"The plan's stories would be built and merged into {ChangesetBranch}, and then the changeset pull request", outcome.Error);
        Assert.DoesNotContain("Every story of the plan would open", outcome.Error);
        Assert.Empty(h.Planner!.Calls);
        Assert.Empty(StoriesCreated(h));
        Assert.Empty(h.Repo.BranchesCreated);
        var rows = await h.Rows();
        Assert.DoesNotContain(rows, r => r.Step is RunPipeline.Steps.Plan or RunPipeline.Steps.PlanCreating or RunPipeline.Steps.PlanStories or RunPipeline.Steps.Changeset);
        var row = Assert.Single(rows, r => r.Step == RunPipeline.Steps.CiTriggers);
        Assert.StartsWith($"{CiTriggerCheck.BlockedPrefix}{Sandbox}: no CI workflow runs for a pull request into trunk. Fix: add `trunk`", row.Detail);
        Assert.Equal(StepOutcome.GateRejected, row.Outcome);
        Assert.Contains("add `trunk` to the pull_request branches filter", Assert.Single(h.Comments(PlanId)));

        // With the default branch in the filter too, the plan item's next run plans as usual.
        h.Repo.Workflows = Workflow("name: ci\non:\n  pull_request:\n    branches: [trunk, 'factory/**']\njobs: {}\n");
        var planned = await PlanRun(h);
        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        Assert.Contains($"[plan] CI triggers of {Sandbox}: {CiFile} runs for a pull request into trunk", h.Logged);
    }

    [Fact]
    public async Task A_changeset_pull_request_whose_ci_cannot_be_told_is_a_warning_when_the_stories_ci_runs()
    {
        var h = new Harness();
        h.Repo.Workflows =
        [
            new WorkflowFile(CiFile, "on:\n  pull_request:\n    branches: ['factory/**']\n"),
            new WorkflowFile(".github/workflows/odd.yml", "on:\n  pull_request:\n    branches: ['[main']\n"),
        ];

        var planned = await PlanRun(h);

        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        Assert.StartsWith($"{CiTriggerCheck.WarningPrefix}{Sandbox}: whether any CI workflow runs for a pull request into {Main} cannot be told. "
            + "Workflows read on the default branch: .github/workflows/odd.yml (its pull_request branches filter could not be read); ",
            Assert.Single(await h.Steps(PlanId, RunPipeline.Steps.CiTriggers)));
    }

    [Fact]
    public async Task The_board_comment_of_a_stop_keeps_the_fix_however_many_workflows_the_repository_has()
    {
        // 50 workflows, each named in full, would push a fix written after them past the comment's clip of the reason.
        var h = new Harness();
        h.Repo.Workflows = [.. Enumerable.Range(0, 50).Select(i =>
            new WorkflowFile($".github/workflows/a-rather-long-workflow-file-name-for-deployment-stage-{i:00}.yml", "on:\n  pull_request:\n    branches: [main]\n"))];

        var outcome = await PlanRun(h);

        Assert.Equal(WorkState.Escalated, outcome.State);
        const string fix = "Fix: add `factory/**` to the pull_request branches filter of .github/workflows/a-rather-long-workflow-file-name-for-deployment-stage-00.yml";
        var comment = Assert.Single(h.Comments(PlanId));
        Assert.Contains(fix, comment);
        Assert.Contains("(or likewise in 47 more). Workflows read on the default branch: ", comment);
        Assert.Contains("; … 40 more. Every story of the plan would open its pull request", comment);
        Assert.Contains("Fix the workflow on the default branch, then run the plan item again.", comment);
        Assert.True(comment.IndexOf(fix, StringComparison.Ordinal) < comment.IndexOf("Workflows read on the default branch", StringComparison.Ordinal));
        Assert.True(outcome.Error!.Length < LedgerReport.MaxReasonLength, outcome.Error.Length.ToString());
    }

    // ---- which pull request is a changeset's ----

    [Fact]
    public void A_pull_request_is_a_changesets_only_for_an_item_with_a_changeset_and_only_when_it_targets_the_base_that_changeset_recorded()
    {
        var changeset = new Plans.Changeset(Sandbox.ToString(), ChangesetBranch, Main, "base0");
        var pull = new PullFacts(1, "https://github.com/o/r/pull/1", true, false, false, "head", Main, "base0", null);

        Assert.True(RunPipeline.IsChangesetPull(changeset, pull with { BaseRef = Main }));
        // Retargeted (or adopted with another base): not the changeset pull request the limits are for.
        Assert.False(RunPipeline.IsChangesetPull(changeset, pull with { BaseRef = "release" }));
        Assert.False(RunPipeline.IsChangesetPull(changeset, pull with { BaseRef = ChangesetBranch }));
        Assert.False(RunPipeline.IsChangesetPull(changeset, pull with { BaseRef = "Main" }));
        // No changeset in the ledger: whatever the base is called.
        Assert.False(RunPipeline.IsChangesetPull(null, pull with { BaseRef = Main }));
        Assert.False(RunPipeline.IsChangesetPull(null, pull with { BaseRef = ChangesetBranch }));
    }

    public static TheoryData<string, string> CannotTell() => new()
    {
        { "on: [push\n", $"{CiFile} (not valid YAML)" },
        { "jobs: {}\n", $"{CiFile} (no `on:` key)" },
        { "on:\n  pull_request:\n    branches: [main]\n    branches-ignore: [dev]\n", "has both branches and branches-ignore" },
        { "on:\n  workflow_run:\n    workflows: [build]\n", "a trigger this check does not read" },
    };

    [Theory]
    [MemberData(nameof(CannotTell))]
    public async Task A_workflow_that_cannot_be_understood_is_a_recorded_warning_and_the_plan_goes_on(string workflow, string why)
    {
        var h = new Harness();
        h.Repo.Workflows = Workflow(workflow);

        var planned = await PlanRun(h);

        Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
        var row = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CiTriggers);
        Assert.StartsWith($"{CiTriggerCheck.WarningPrefix}{Sandbox}: whether any CI workflow runs for a pull request into {ChangesetBranch} cannot be told", row.Detail);
        Assert.Contains(why, row.Detail);
        Assert.Equal(StepOutcome.Failed, row.Outcome);
        Assert.Contains($"[plan] warning: CI triggers of {Sandbox}: ", h.Logged);
        // Recorded before the planner session, which then ran.
        Assert.Single(h.Planner!.Calls);
        var rows = await h.Rows();
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.CiTriggers) < rows.FindIndex(r => r.Step == RunPipeline.Steps.Plan));
        Assert.Equal(2, StoriesCreated(h).Count);
    }

    [Fact]
    public async Task A_repository_with_no_workflow_file_or_whose_workflows_cannot_be_read_is_a_warning_not_a_stop()
    {
        var none = new Harness();
        none.Repo.Workflows = [];
        Assert.Equal(WorkState.Planned, (await PlanRun(none)).State);
        Assert.Contains("no workflow file under .github/workflows/ on the default branch",
            Assert.Single(await none.Steps(PlanId, RunPipeline.Steps.CiTriggers)));

        var unread = new Harness();
        unread.Repo.Workflows = [new WorkflowFile(CiFile, null, "GitHub answered 500")];
        Assert.Equal(WorkState.Planned, (await PlanRun(unread)).State);
        Assert.Contains($"{CiFile} (could not be read: GitHub answered 500)", Assert.Single(await unread.Steps(PlanId, RunPipeline.Steps.CiTriggers)));

        // GitHub not answering the listing at all — an error, or its client's own timeout — never fails the plan.
        foreach (var failure in new Exception[] { new HttpRequestException("connection refused"), new InvalidOperationException("GitHub list failed: 403"),
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") })
        {
            var down = new Harness();
            down.Repo.WorkflowsThrow = failure;
            var planned = await PlanRun(down);
            Assert.Equal((WorkState.Planned, (string?)null), (planned.State, planned.Error));
            var detail = Assert.Single(await down.Steps(PlanId, RunPipeline.Steps.CiTriggers));
            Assert.StartsWith(CiTriggerCheck.WarningPrefix, detail);
            Assert.Contains($"the workflows could not be read ({failure.Message})", detail);
        }
    }
}
