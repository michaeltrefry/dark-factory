using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25709: the merge gate's <c>risk-threshold</c> reads CodeGraph's blast radius (per touched file, for exactly the head) and hotspot
/// score (default branch, recorded with its commit). Code floor: a critical blast radius fails; <c>factory/gate.yaml</c> version 3 can
/// only tighten it (<c>max_blast_radius</c>) and opts in to a hotspot limit (<c>max_hotspot</c>, the hotspot score being CodeGraph's
/// riskScore). A file CodeGraph has no data for counts as none / 0; one it cannot read leaves the check pending until
/// <c>Gate:CiTimeoutMinutes</c>.
/// <para>
/// CodeGraph's tools are its C7 contract (sc-25735, not deployed yet): answers through <c>CommitAnswer.Create</c> (michaeltrefry/CodeGraph
/// PR #71), whose structured content is
/// <code>
/// var structured = new JsonObject { ["commitSha"] = commit.CommitSha, ["source"] = scope.Overlay is not null ? "overlay" : "default-branch" };
/// ...
/// return Result($"{commit.HeaderLine}\n\n{text}", structured, isError: false);
/// </code>
/// with the tool's own fields alongside (the C7 contract: <c>analyze_impact_file</c> → <c>{path, found, blastRadius, criticalCount, …}</c>,
/// <c>file_hotspot</c> → <c>{path, found, riskScore, concernScore, healthScore, churn365d, complexityScore, computedAt, commitSha}</c>);
/// a SHA with no ready overlay is C4's <c>notIndexed</c> (<see cref="FakeMcpServer.NotIndexed"/>), an unknown repository a C6-style error
/// (<see cref="FakeMcpServer.OverlayError"/>: <c>Error (&lt;code&gt;): &lt;message&gt;</c>), an unentitled token HTTP 403
/// <c>tool_not_entitled</c> (<see cref="FakeMcpServer.NotEntitled"/>).
/// </para>
/// </summary>
public class CodeGraphRiskTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "dddddddddddddddddddddddddddddddddddddddd";
    private const string IndexCommit = FakeCodeGraphRisk.IndexCommit;
    private const string Project = "Sandbox";
    private const string Listing = "- **Sandbox** [csharp]\n  Repo: https://github.com/o/r\n";
    private static readonly RepoRef Repo = new("o", "r");
    private static readonly CodeGraphOverlay Ready = new(CodeGraphOverlay.Ready, Head, 41, IndexCommit);

    // ---- factory/gate.yaml version 3 ----

    [Fact]
    public void Version_2_still_parses_and_runs_at_the_floor_with_no_hotspot_limit()
    {
        var risk = GatePolicy.Parse(TestPolicies.Standard()).Risk;
        Assert.Equal(new RiskThreshold(400, 20, 3, null, null), risk);
        Assert.Equal(BlastRadius.High, GatePolicy.BlastRadiusFloor);
    }

    [Theory]
    [InlineData("low", "off", BlastRadius.Low, null)]
    [InlineData("medium", "0.75", BlastRadius.Medium, 0.75)]
    [InlineData("high", "40", BlastRadius.High, 40.0)]
    [InlineData("high", "0", BlastRadius.High, 0.0)]
    public void Version_3_reads_max_blast_radius_and_the_opt_in_max_hotspot(string blast, string hotspot, BlastRadius radius, double? max)
    {
        Assert.Equal(new RiskThreshold(400, 20, 3, radius, max), GatePolicy.Parse(TestPolicies.V3(blast, hotspot)).Risk);
    }

    public static TheoryData<string, string> InvalidV3() => new()
    {
        { TestPolicies.V3("medium", "off").Replace("\n  max_blast_radius: medium", "", StringComparison.Ordinal), "risk is missing 'max_blast_radius'" },
        { TestPolicies.V3("medium", "off").Replace("\n  max_hotspot: off", "", StringComparison.Ordinal), "risk is missing 'max_hotspot'" },
        { TestPolicies.V3("huge", "off"), "risk.max_blast_radius must be one of low, medium, high" },
        { TestPolicies.V3("none", "off"), "risk.max_blast_radius must be one of low, medium, high" },
        { TestPolicies.V3("Medium", "off"), "risk.max_blast_radius must be one of low, medium, high" },
        { TestPolicies.V3("2", "off"), "risk.max_blast_radius must be one of low, medium, high" },
        { TestPolicies.V3("medium", "-1"), "risk.max_hotspot must be off or a number of at least 0" },
        { TestPolicies.V3("medium", "lots"), "risk.max_hotspot must be off or a number of at least 0" },
        { TestPolicies.V3("medium", "Off"), "risk.max_hotspot must be off or a number of at least 0" },
        { TestPolicies.V3("medium", "1e3"), "risk.max_hotspot must be off or a number of at least 0" },
        // Version 2 has no CodeGraph keys: one there is unknown (strict per-version key sets).
        { TestPolicies.Standard() + "\n  max_blast_radius: medium", "unknown key 'max_blast_radius' in risk" },
        { TestPolicies.V3("medium", "off").Replace("version: 3", "version: 5", StringComparison.Ordinal), "version must be 2, 3 or 4" },
    };

    [Theory]
    [MemberData(nameof(InvalidV3))]
    public void A_version_3_policy_with_a_missing_or_malformed_codegraph_limit_is_invalid(string yaml, string reason)
    {
        Assert.Contains(reason, Assert.Throws<GatePolicyException>(() => GatePolicy.Parse(yaml)).Message);
    }

    [Fact]
    [FailsGateCheck(GateCheckCoverage.FloorBlastRadius)]
    public void A_version_3_max_blast_radius_looser_than_the_floor_is_invalid()
    {
        var ex = Assert.Throws<GatePolicyException>(() => GatePolicy.Parse(TestPolicies.V3("critical", "off")));
        Assert.Contains("risk.max_blast_radius 'critical' is looser than the code floor (high: a critical blast radius always fails)", ex.Message);
    }

    // ---- the merge gate over CodeGraph's inputs ----

    private static readonly PullFacts Pull = new(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", "base", null);
    private static readonly CiFacts Green = new(Head, [new CheckFact("build", true, "success")]);
    private static readonly ReviewVerdict Full = ReviewPanel.Decide(Head, [],
        ReviewRoles.All.Select(r => new RoleReview(r, "claude-opus-5-5", "high", "s", "p", [], "ok")).ToList());
    private static readonly NewTestsResult PassingTests = new("base", Head, NewTestsOutcome.Pass, "1 new test(s) fail on the base and pass on the head: W.New",
        "dotnet-xunit", "ran", "ran", []);
    private static readonly string TwoFiles = TestPolicies.Diff("src/a.cs") + TestPolicies.Diff("src/b.cs");

    private static GateDecision Evaluate(CodeGraphRisk? risk, string? policy = null, string? diff = null, ReviewVerdict? verdict = null)
    {
        diff ??= TwoFiles;
        return MergeGate.Evaluate(policy ?? TestPolicies.Standard(), null, TestPolicies.Counting(Pull, new ChangeFacts(diff, null, 0)),
            new ChangeFacts(diff, null, 0), Green, [verdict ?? Full], PassingTests, risk);
    }

    private static CodeGraphRisk Radii(BlastRadius a, BlastRadius b = BlastRadius.Low, double hotA = 0.1, double hotB = 0.2) =>
        TestRisk.With(Head, TwoFiles, f => f == "src/a.cs" ? a : b, f => f == "src/a.cs" ? hotA : hotB);

    [Fact]
    [FailsGateCheck(GateCheckCoverage.FloorBlastRadius)]
    public void A_file_with_a_critical_blast_radius_fails_risk_threshold_at_the_floor()
    {
        // Version 2: no policy limit, the code floor alone.
        var decision = Evaluate(Radii(BlastRadius.Critical, BlastRadius.High));

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        var reason = Assert.Single(decision.Reasons);
        Assert.Equal($"risk threshold: blast radius at {Head[..12]} over the code floor (critical always fails): src/a.cs critical", reason);
        // The gate checkpoint records every input with the decision: each file's blast radius at the head, each hotspot with its commit.
        Assert.Contains($"| codegraph blast radius at {Head}: src/a.cs critical, src/b.cs high; "
            + $"hotspot score (riskScore) on the default branch: at {IndexCommit}: src/a.cs 0.1, src/b.cs 0.2", decision.Detail);
        // High is within the floor.
        Assert.Equal(GateOutcome.Merge, Evaluate(Radii(BlastRadius.High, BlastRadius.High)).Outcome);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void A_version_3_max_blast_radius_of_medium_fails_a_high_blast_radius_pr()
    {
        var medium = TestPolicies.V3("medium", "off");

        var decision = Evaluate(Radii(BlastRadius.Medium, BlastRadius.High), medium);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains($"risk threshold: blast radius at {Head[..12]} over max_blast_radius medium: src/b.cs high", Assert.Single(decision.Reasons));
        Assert.Equal(GateOutcome.Merge, Evaluate(Radii(BlastRadius.Medium, BlastRadius.Medium), medium).Outcome);
        // Version 3 at high keeps the floor: critical still fails.
        Assert.Contains("over max_blast_radius high: src/a.cs critical", Evaluate(Radii(BlastRadius.Critical), TestPolicies.V3("high", "off")).Detail);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void A_hotspot_score_over_an_opted_in_max_hotspot_fails_risk_threshold()
    {
        var decision = Evaluate(Radii(BlastRadius.Low, hotA: 0.9, hotB: 0.5), TestPolicies.V3("high", "0.5"));

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal($"risk threshold: hotspot score (riskScore) over max_hotspot 0.5: src/a.cs 0.9 on the default branch at {IndexCommit}",
            Assert.Single(decision.Reasons));
        // At the limit it passes; off (or version 2) the score is only recorded and reported.
        Assert.Equal(GateOutcome.Merge, Evaluate(Radii(BlastRadius.Low, hotA: 0.5, hotB: 0.5), TestPolicies.V3("high", "0.5")).Outcome);
        var off = Evaluate(Radii(BlastRadius.Low, hotA: 99), TestPolicies.V3("high", "off"));
        Assert.Equal(GateOutcome.Merge, off.Outcome);
        Assert.Contains("highest hotspot score 99 (src/a.cs), max_hotspot off", off.Detail);
        Assert.Equal(GateOutcome.Merge, Evaluate(Radii(BlastRadius.Low, hotA: 99)).Outcome);
    }

    [Fact]
    public void A_merge_names_the_worst_blast_radius_and_the_highest_hotspot()
    {
        var decision = Evaluate(Radii(BlastRadius.Medium, BlastRadius.High, 0.3, 0.7), TestPolicies.V3("high", "0.8"));

        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains("worst blast radius high (src/b.cs), highest hotspot score 0.7 (src/b.cs) within max_hotspot 0.8", decision.Detail);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void CodeGraph_inputs_that_could_not_be_read_leave_risk_threshold_pending_and_never_merge()
    {
        var unreadable = new CodeGraphRisk(Head, [], [], [$"blast radius: no ready CodeGraph overlay of {Head} (timed-out)"]);

        var decision = Evaluate(unreadable);

        Assert.Equal(GateOutcome.Pending, decision.Outcome);
        Assert.Equal($"risk threshold: CodeGraph could not be read for {Head[..12]}: blast radius: no ready CodeGraph overlay of {Head} (timed-out)",
            Assert.Single(decision.Reasons));
        // Anything else blocking: blocked at once (no wait), the unread inputs named too.
        var blocked = Evaluate(unreadable, verdict: Full with { Verdict = ReviewVerdict.Fail });
        Assert.Equal(GateOutcome.Blocked, blocked.Outcome);
        Assert.Contains(blocked.Reasons, r => r.StartsWith("risk threshold: CodeGraph could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_read_over_a_limit_blocks_at_once_even_when_another_could_not_be_read()
    {
        var partial = new CodeGraphRisk(Head, [new FileBlastRadius("src/a.cs", BlastRadius.Critical, true)], [],
            ["src/b.cs: analyze_impact_file could not be sent: connection refused"]);

        var decision = Evaluate(partial);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal([$"risk threshold: blast radius at {Head[..12]} over the code floor (critical always fails): src/a.cs critical",
            $"risk threshold: CodeGraph could not be read for {Head[..12]}: src/b.cs: analyze_impact_file could not be sent: connection refused"],
            decision.Reasons);
        // Nothing over a limit: pending.
        Assert.Equal(GateOutcome.Pending, Evaluate(partial with { BlastRadius = [new FileBlastRadius("src/a.cs", BlastRadius.Low, true)] }).Outcome);
    }

    [Fact]
    public void Evaluated_before_codegraph_is_read_a_change_another_rule_blocks_is_blocked_and_one_nothing_blocks_is_pending()
    {
        GateDecision Unread(ReviewVerdict verdict) => MergeGate.Evaluate(TestPolicies.Standard(), null,
            TestPolicies.Counting(Pull, new ChangeFacts(TwoFiles, null, 0)), new ChangeFacts(TwoFiles, null, 0), Green, [verdict], PassingTests,
            risk: null, riskUnread: true);

        var pending = Unread(Full);
        Assert.Equal(GateOutcome.Pending, pending.Outcome);
        Assert.Equal($"risk threshold: CodeGraph's inputs for {Head[..12]} are still to be read", Assert.Single(pending.Reasons));
        var blocked = Unread(Full with { Verdict = ReviewVerdict.Fail });
        Assert.Equal(GateOutcome.Blocked, blocked.Outcome);
        Assert.Equal($"the review of {Head[..12]} is 'fail'", Assert.Single(blocked.Reasons));
    }

    [Fact]
    public void A_large_prs_gate_row_stays_within_the_reports_budget_and_still_names_the_critical_file()
    {
        // 300 touched files with long paths: listing every one would put the gate row past the report's budget, which then leaves it out.
        var files = Enumerable.Range(0, 300).Select(i => $"src/{new string('d', 180)}/File{i:000}.cs").ToList();
        var diff = string.Concat(files.Select(f => TestPolicies.Diff(f)));
        var critical = files[150];
        var risk = TestRisk.With(Head, diff, f => f == critical ? BlastRadius.Critical : BlastRadius.Low, f => f == files[7] ? 0.9 : 0.1)
            with { Unreadable = [] };
        var decision = Evaluate(risk, TestPolicies.Standard(maxLines: 10_000, maxFiles: 1_000), diff);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains($"over the code floor (critical always fails): {critical} critical", decision.Detail);
        Assert.Contains($"{critical} critical", decision.Inputs);
        Assert.Contains($"{files[7]} 0.9", decision.Inputs); // the hottest
        Assert.Contains("… 279 more", decision.Inputs); // 300 - the critical one - the first 20 others
        Assert.True(decision.Detail.Length < 20_000, $"gate row of {decision.Detail.Length} chars");
        var row = new LedgerEntry { State = WorkState.MergeGate, Step = RunPipeline.Steps.GateDecision, Detail = decision.Detail, Outcome = StepOutcome.GateRejected };
        Assert.Contains($"{critical} critical", LedgerReport.Facts([row], []));
        // Unreadable entries are capped too.
        var many = new CodeGraphRisk(Head, [], [], Enumerable.Range(0, 50).Select(i => $"entry {i}").ToList());
        Assert.EndsWith("entry 19; … 30 more", many.UnreadableText);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void CodeGraph_inputs_not_read_read_for_another_commit_or_leaving_a_file_out_block()
    {
        Assert.Contains("CodeGraph is not configured: no CodeGraph token; set CodeGraph:Token, env FACTORY_CODEGRAPH_TOKEN, or keychain account "
            + "'codegraph-token' (service 'dark-factory')", Assert.Single(Evaluate(null).Reasons));
        Assert.Contains($"read for {Other[..12]}, not the head", Assert.Single(Evaluate(Radii(BlastRadius.Low) with { HeadSha = Other }).Reasons));
        var partial = Radii(BlastRadius.Low);
        Assert.Contains("CodeGraph's inputs leave out src/b.cs",
            Assert.Single(Evaluate(partial with { BlastRadius = partial.BlastRadius.Take(1).ToList() }).Reasons));
        Assert.Equal(GateOutcome.Blocked, Evaluate(partial with { Hotspots = partial.Hotspots.Skip(1).ToList() }).Outcome);
    }

    [Fact]
    public void A_change_whose_tiers_do_not_require_risk_threshold_does_not_use_codegraph()
    {
        var docs = TestPolicies.Diff("docs/a.md");
        var decision = Evaluate(null, diff: docs);
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.DoesNotContain("codegraph", decision.Detail);
    }

    // ---- the reader: CodeGraph's answers (C7 contract) ----

    /// <summary>An <c>analyze_impact_file</c> answer as C7 (michaeltrefry/CodeGraph PR #76) shapes it, field by field (<see cref="C7Answers"/>).</summary>
    private static JsonObject ImpactAnswer(string sha, string path, bool found, string blastRadius) =>
        C7Answers.ImpactFile(Project, path, sha, found, blastRadius);

    /// <summary>
    /// A <c>file_hotspot</c> answer as C7 (PR #76) shapes it (default branch only, <see cref="C7Answers"/>): not found → <c>commitSha</c>
    /// null, the index commit only in <c>indexCommitSha</c>.
    /// </summary>
    private static JsonObject HotspotAnswer(string path, bool found, double? riskScore, string? sha = IndexCommit, string source = "default-branch",
        string? indexCommitSha = IndexCommit) =>
        C7Answers.Hotspot(Project, path, found ? riskScore : null, sha, indexCommitSha, source);

    private static JsonObject WithPath(JsonObject answer, string path)
    {
        answer["structuredContent"]!.AsObject()["path"] = path;
        return answer;
    }

    private static JsonObject Without(JsonObject answer, string field)
    {
        answer["structuredContent"]!.AsObject().Remove(field);
        return answer;
    }

    private static CodeGraphRiskReader Reader(FakeMcpServer server) =>
        new(new CodeGraphMcpClient(server.Client("https://codegraph.test/"), "cg_test_token"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5));

    private static FakeMcpServer Server(Func<string, JsonObject, JsonObject> answer) => FakeMcpServer.CodeGraph(Listing, answer);

    [Fact]
    public async Task The_reader_asks_for_each_touched_file_at_the_head_and_on_the_default_branch_and_counts_no_data_as_none()
    {
        var server = Server((tool, args) => (tool, args["path"]!.GetValue<string>()) switch
        {
            ("analyze_impact_file", "src/a.cs") => ImpactAnswer(Head, "src/a.cs", true, "high"),
            ("analyze_impact_file", var p) => ImpactAnswer(Head, p, false, "none"), // e.g. a new file: no nodes
            ("file_hotspot", "src/a.cs") => HotspotAnswer("src/a.cs", true, 0.42),
            ("file_hotspot", var p) => HotspotAnswer(p, false, null),
            _ => FakeMcpServer.Text("unexpected"),
        });

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/New.cs"], CancellationToken.None);

        Assert.Empty(risk.Unreadable);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.High, true), new FileBlastRadius("src/New.cs", BlastRadius.None, false)], risk.BlastRadius);
        // C7 (PR #76) names no metrics commit for a file without metrics (commitSha null): its 0 is recorded at indexCommitSha.
        Assert.Equal([new FileHotspot("src/a.cs", 0.42, true, IndexCommit, IndexCommit), new FileHotspot("src/New.cs", 0, false, IndexCommit, IndexCommit)],
            risk.Hotspots);
        // The project pinned by exact repo URL, the head SHA and depth 3 for the blast radius; no SHA for the hotspot (default branch).
        Assert.Equal($$"""{"repo":"{{Project}}","path":"src/a.cs","sha":"{{Head}}","depth":3}""",
            server.Calls.First(c => c.Tool == "analyze_impact_file").Arguments.ToJsonString());
        Assert.Equal($$"""{"repo":"{{Project}}","path":"src/a.cs"}""", server.Calls.First(c => c.Tool == "file_hotspot").Arguments.ToJsonString());
        Assert.All(server.Api.Requests, r => Assert.Equal("Bearer cg_test_token", r.Headers["Authorization"]));
        // A found:false file counts as none / 0 at the gate: a version 3 low limit and a 0 hotspot limit still pass on it.
        var diff = TestPolicies.Diff("src/New.cs");
        var newOnly = await Reader(server).ReadAsync(Repo, Head, Ready, MergeGate.TouchedFiles(diff), CancellationToken.None);
        Assert.Equal(GateOutcome.Merge, Evaluate(newOnly, TestPolicies.V3("low", "0"), diff).Outcome);
    }

    public static TheoryData<string, string> UnreadableAnswers() => new()
    {
        { "not-indexed", "src/a.cs: analyze_impact_file is not indexed for the head (commit_not_indexed, status indexing)" },
        { "unknown-repo", "src/a.cs: analyze_impact_file (repo_not_found): Error (repo_not_found): Unknown repository 'Sandbox'." },
        { "not-entitled", "src/a.cs: not entitled to analyze_impact_file" },
        { "other-commit", $"src/a.cs: analyze_impact_file answered for {Other}, not the head" },
        { "unknown-radius", "src/a.cs: analyze_impact_file answered an unknown blast radius 'severe'" },
        { "no-found", "src/a.cs: analyze_impact_file answered no 'found'" },
        { "unreachable", "src/a.cs: analyze_impact_file could not be sent: " },
        { "hotspot-error", "src/a.cs: file_hotspot (repo_not_found): Error (repo_not_found): Unknown repository 'Sandbox'." },
        { "hotspot-not-entitled", "src/a.cs: not entitled to file_hotspot" },
        { "hotspot-overlay", "src/a.cs: file_hotspot answered from 'overlay', not the default branch" },
        { "hotspot-no-commit", "src/a.cs: file_hotspot named no commit (neither commitSha nor indexCommitSha)" },
        { "hotspot-no-score", "src/a.cs: file_hotspot answered no riskScore" },
        { "other-path", "src/a.cs: analyze_impact_file answered about 'src/b.cs', not src/a.cs" },
        { "no-path", "src/a.cs: analyze_impact_file answered about no path, not src/a.cs" },
        { "hotspot-other-path", "src/a.cs: file_hotspot answered about 'src/a.cs.bak', not src/a.cs" },
    };

    [Theory]
    [MemberData(nameof(UnreadableAnswers))]
    public async Task A_file_codegraph_cannot_read_is_unreadable(string kind, string why)
    {
        var server = Server((tool, args) => (kind, tool) switch
        {
            ("not-indexed", "analyze_impact_file") => FakeMcpServer.NotIndexed(Project, Head, "indexing", 41, IndexCommit),
            ("unknown-repo", "analyze_impact_file") or ("hotspot-error", "file_hotspot") =>
                FakeMcpServer.OverlayError("repo_not_found", "Unknown repository 'Sandbox'."),
            ("not-entitled", "analyze_impact_file") => throw FakeMcpServer.NotEntitled("analyze_impact_file"),
            ("hotspot-not-entitled", "file_hotspot") => throw FakeMcpServer.NotEntitled("file_hotspot"),
            ("other-commit", "analyze_impact_file") => ImpactAnswer(Other, "src/a.cs", true, "low"),
            ("unknown-radius", "analyze_impact_file") => ImpactAnswer(Head, "src/a.cs", true, "severe"),
            ("no-found", "analyze_impact_file") => WithPath(FakeMcpServer.CommitAnswer(Head, "no fields", overlay: true), "src/a.cs"),
            ("other-path", "analyze_impact_file") => ImpactAnswer(Head, "src/b.cs", true, "low"),
            ("no-path", "analyze_impact_file") => Without(ImpactAnswer(Head, "src/a.cs", true, "low"), "path"),
            ("hotspot-other-path", "file_hotspot") => HotspotAnswer("src/a.cs.bak", true, 0.1),
            ("unreachable", "analyze_impact_file") => throw new HttpRequestException("connection refused"),
            ("hotspot-overlay", "file_hotspot") => HotspotAnswer("src/a.cs", true, 0.1, source: "overlay"),
            ("hotspot-no-commit", "file_hotspot") => HotspotAnswer("src/a.cs", true, 0.1, sha: null, indexCommitSha: null),
            ("hotspot-no-score", "file_hotspot") => FakeMcpServer.Structured($"Commit: {IndexCommit}", new JsonObject
                { ["commitSha"] = IndexCommit, ["source"] = "default-branch", ["found"] = true, ["path"] = "src/a.cs" }),
            (_, "analyze_impact_file") => ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"),
            _ => HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.1),
        });

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.StartsWith(why, Assert.Single(risk.Unreadable));
        Assert.Equal(GateOutcome.Pending, Evaluate(risk, diff: TestPolicies.Diff("src/a.cs")).Outcome);
    }

    [Fact]
    public async Task Metrics_computed_before_codegraph_recorded_their_commit_are_used_at_the_index_commit_labelled_unknown()
    {
        // C7 (PR #76): `double? concernScore = metric is null || commitSha is null ? null : metric.ConcernScore;` — a found file whose
        // metrics predate recorded commits answers commitSha null; the index commit is still named in indexCommitSha.
        var server = Server((tool, args) => tool == "file_hotspot"
            ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.7, sha: null)
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.Empty(risk.Unreadable);
        Assert.Equal([new FileHotspot("src/a.cs", 0.7, true, IndexCommit, IndexCommit, VitalsCommitUnknown: true)], risk.Hotspots);
        Assert.Contains($"vitals commit unknown; index at {IndexCommit}: src/a.cs 0.7", risk.Describe());
        // The score counts: a version 3 hotspot limit below it fails the check.
        var diff = TestPolicies.Diff("src/a.cs");
        Assert.Equal(GateOutcome.Merge, Evaluate(risk, TestPolicies.V3("high", "1"), diff).Outcome);
        var over = Evaluate(risk, TestPolicies.V3("high", "0.5"), diff);
        Assert.NotEqual(GateOutcome.Merge, over.Outcome);
        // The block reason names the commit as the risk description does: never "at <index commit>" as if the vitals were computed there.
        Assert.Contains($"src/a.cs 0.7 on the default branch vitals commit unknown; index at {IndexCommit}", Assert.Single(over.Reasons));
    }

    [Fact]
    public async Task A_hotspot_read_in_a_wait_is_reused_by_its_index_commit_even_when_the_metrics_are_older()
    {
        // The metrics were computed at an older default-branch commit than the index's now (commitSha != indexCommitSha): the score is
        // recorded at its own commit, and the cache is keyed by the index commit — the commit the next read looks it up by (the overlay's base).
        var server = Server((tool, args) => tool == "file_hotspot"
            ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3, sha: Other)
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"));
        var reader = Reader(server);
        var cache = new HotspotCache();

        var first = await reader.ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None, cache);
        var second = await reader.ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None, cache);

        Assert.Equal([new FileHotspot("src/a.cs", 0.3, true, Other, IndexCommit)], first.Hotspots);
        Assert.Contains($"at {Other}: src/a.cs 0.3", first.Describe());
        Assert.Equal(first.Hotspots, second.Hotspots);
        Assert.Single(server.Calls, c => c.Tool == "file_hotspot");
    }

    [Fact]
    public async Task No_ready_overlay_of_the_head_leaves_the_inputs_unread_and_nothing_more_is_asked()
    {
        var server = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3)
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"));
        var timedOut = new CodeGraphOverlay(CodeGraphOverlay.TimedOut, Head, 41, Reason: "not ready within 10 min");

        var risk = await Reader(server).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);

        Assert.Equal($"blast radius: no ready CodeGraph overlay of {Head} (timed-out (overlay 41): not ready within 10 min)", Assert.Single(risk.Unreadable));
        Assert.Empty(risk.BlastRadius);
        // The reader stops at the first input it cannot read: the gate is pending anyway, and reads everything again.
        Assert.DoesNotContain(server.Calls, c => c.Tool is "analyze_impact_file" or "file_hotspot");
        Assert.Empty(risk.Hotspots);
        // A ready overlay of another commit is not the head's either.
        var other = await Reader(server).ReadAsync(Repo, Head, Ready with { HeadSha = Other }, ["src/a.cs"], CancellationToken.None);
        Assert.StartsWith("blast radius: no ready CodeGraph overlay", Assert.Single(other.Unreadable));
    }

    [Fact]
    public async Task The_reader_stops_at_the_first_file_it_cannot_read()
    {
        var server = Server((tool, args) => (tool, args["path"]!.GetValue<string>()) switch
        {
            ("analyze_impact_file", "src/a.cs") => throw new HttpRequestException("connection refused"),
            ("analyze_impact_file", var p) => ImpactAnswer(Head, p, true, "low"),
            (_, var p) => HotspotAnswer(p, true, 0.1),
        });

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None);

        Assert.StartsWith("src/a.cs: analyze_impact_file could not be sent", Assert.Single(risk.Unreadable));
        Assert.Equal(["src/a.cs"], server.Calls.Where(c => c.Tool is "analyze_impact_file" or "file_hotspot").Select(c => c.Arguments["path"]!.GetValue<string>()));
        Assert.False(risk.OverlayUnusable);
    }

    [Fact]
    public async Task An_answer_naming_the_asked_path_another_way_is_the_same_file()
    {
        var server = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer("./src/a.cs", true, 0.2) : ImpactAnswer(Head, "src/x/../a.cs", true, "low"));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.Empty(risk.Unreadable);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.Low, true)], risk.BlastRadius);
    }

    [Fact]
    public async Task A_not_indexed_answer_marks_the_head_overlay_unusable()
    {
        var server = Server((tool, args) => FakeMcpServer.NotIndexed(Project, Head, "stale", 41, IndexCommit));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.True(risk.OverlayUnusable);
        Assert.Contains("status stale", Assert.Single(risk.Unreadable));
    }

    [Fact]
    public async Task A_repository_codegraph_does_not_index_or_cannot_be_asked_about_is_unreadable()
    {
        var unindexed = FakeMcpServer.CodeGraph("- **Other** [csharp]\n  Repo: https://github.com/o/other\n", (_, _) => FakeMcpServer.Text("x"));
        Assert.Equal("o/r: repository not indexed in CodeGraph",
            Assert.Single((await Reader(unindexed).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None)).Unreadable));

        var down = new FakeMcpServer("/mcp", [], (_, _) => throw new HttpRequestException("connection refused"));
        Assert.StartsWith("CodeGraph could not be reached or asked: ",
            Assert.Single((await Reader(down).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None)).Unreadable));
    }

    // ---- the risk tools are the gate's and, through the proxy, the planner's (sc-25874) ----

    [Fact]
    public async Task The_risk_tools_are_on_no_reviewer_or_triage_tool_list_only_the_planners()
    {
        var riskTools = CodeGraphMcpClient.RiskTools;
        Assert.Equal(["analyze_impact_file", "file_hotspot"], riskTools);
        foreach (var tool in riskTools)
        {
            Assert.DoesNotContain(tool, ReviewTools.Names);
            Assert.DoesNotContain(tool, ReviewTools.CodeGraphTools);
            Assert.DoesNotContain(ReviewTools.Definitions, d => JsonSerializer.SerializeToElement(d).GetProperty("name").GetString() == tool);
            Assert.DoesNotContain(McpServers.KanbanTools, t => t == tool);
            Assert.DoesNotContain(tool, McpProfile.Triage.ToolsOf(McpServers.CodeGraph));
            Assert.DoesNotContain(tool, McpProfile.Triage.ToolsOf(McpServers.Kanban));
            // The one model-facing list that holds them: the planner profile's CodeGraph tools.
            Assert.Contains(tool, PlannerCodeGraph.Tools);
            Assert.Contains(tool, McpProfile.Planner.ToolsOf(McpServers.CodeGraph));
            Assert.DoesNotContain(tool, McpProfile.Planner.ToolsOf(McpServers.Kanban));
        }

        // A CodeGraph that lists them: the reviewers' client refuses them before sending anything; only the upstream registry (the
        // loopback proxy's, which serves them to a planner grant alone) lists them.
        var server = new FakeMcpServer("/mcp", [.. riskTools, ReviewTools.AnalyzeImpact], (_, _) => FakeMcpServer.Text("x"));
        var client = new CodeGraphMcpClient(server.Client("https://codegraph.test/"), "cg_test_token");
        var before = server.Api.Requests.Count;
        foreach (var tool in riskTools)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => client.CallAsync(tool, new JsonObject(), CancellationToken.None));
        }
        Assert.Equal(before, server.Api.Requests.Count);
        Assert.Equal([.. riskTools, ReviewTools.AnalyzeImpact], (await client.Upstream.ListToolsAsync(CancellationToken.None)).Select(t => t.Name));
    }

    // ---- through the pipeline ----

    [Fact]
    public async Task A_pr_touching_a_critical_file_escalates_and_the_gate_checkpoint_and_comment_name_it()
    {
        var h = new Harness();
        h.CodeGraph.Impact = (path, sha) => FakeCodeGraphRisk.Found(path, sha, path == "src/x.cs" ? BlastRadius.Critical : BlastRadius.Low);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        var reason = $"blast radius at {Sha1[..12]} over the code floor (critical always fails): src/x.cs critical";
        Assert.Contains(reason, outcome.Error);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Blocked ", gate.Detail);
        Assert.Contains(reason, gate.Detail);
        Assert.Contains($"codegraph blast radius at {Sha1}: src/x.cs critical", gate.Detail);
        Assert.Contains($"at {FakeCodeGraphRisk.IndexCommit}: src/x.cs 0 (no data)", gate.Detail);
        Assert.Contains(h.Stories.Comments, c => c.Contains("escalated") && c.Contains("src/x.cs critical"));
        // The reports render from the ledger (E8): the gate check row names the file and its blast radius.
        Assert.Contains(reason, LedgerReport.Facts(await h.Rows(), []));
        // The blast radius was read for the head only once its overlay was ready, the overlay recorded.
        Assert.Equal(Sha1, h.CodeGraph.Calls.Single(c => c.Tool == "request_overlay").Argument);
        Assert.Contains(await h.Rows(), r => r.Step == RunPipeline.Steps.CodeGraphOverlay && CodeGraphOverlay.FromDetail(r.Detail)!.IsReady);
    }

    [Fact]
    public async Task A_pr_adding_a_new_file_passes_risk_threshold_and_merges()
    {
        // A new file: CodeGraph has no nodes and no metrics for it. C7 (PR #76) answers analyze_impact_file found:false, blastRadius
        // none, commitSha = the head (pinned), and file_hotspot found:false with commitSha null and only indexCommitSha set — counted as
        // none / 0 (owner decision 2026-10-10), never unreadable.
        var h = new Harness();
        h.GitHub.Diff = head => $"diff --git a/src/New.cs b/src/New.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/New.cs\n@@ -0,0 +1 @@\n+{head}\n";
        h.CodeGraph.Hotspot = path => CodeGraphMcpClient.Hotspot(C7Answers.Result(C7Answers.Hotspot("Sandbox", path, null, commitSha: null)));

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains($"codegraph blast radius at {Sha1}: src/New.cs none (no data)", gate.Detail);
        Assert.Contains($"at {FakeCodeGraphRisk.IndexCommit}: src/New.cs 0 (no data)", gate.Detail);
    }

    [Fact]
    public async Task A_ready_overlay_the_review_recorded_for_the_head_is_reused_by_the_gate()
    {
        var review = new FakeCodeGraphRisk();
        var h = new Harness { Overlays = new ReviewOverlays(review, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)) };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        // The review asked for the head's overlay; the gate found it ready in the ledger and read the blast radius without asking again.
        Assert.Equal(Sha1, review.Calls.Single(c => c.Tool == "request_overlay").Argument);
        Assert.DoesNotContain(h.CodeGraph.Calls, c => c.Tool == "request_overlay");
        Assert.Contains(h.CodeGraph.Calls, c => c.Tool == "analyze_impact_file");
        Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CodeGraphOverlay);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains("worst blast radius none (src/x.cs)", gate.Detail);
    }

    [Fact]
    public async Task CodeGraph_unreadable_for_a_while_is_waited_for_like_pending_ci_then_the_pr_merges()
    {
        var h = new Harness();
        var reads = 0;
        h.CodeGraph.Hotspot = path => ++reads <= 3 ? throw new HttpRequestException("connection refused") : FakeCodeGraphRisk.Hot(path, 0.2);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.True(reads > 3);
        // Pending evaluations are not decisions: one gate row, the merge.
        Assert.StartsWith("Merge ", Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision).Detail);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CodeGraph_still_unreadable_at_the_ci_timeout_escalates()
    {
        // Fake time: every wait moves the clock on by itself, so the timeout is reached in steps, whatever the load.
        var h = new Harness { GateOnFakeClock = true, Time = new SteppingClock(Start, TimeSpan.FromMinutes(1)), CiTimeout = TimeSpan.FromMinutes(5) };
        h.CodeGraph.Impact = (_, sha) => FakeCodeGraphRisk.NotIndexed(sha, "indexing");

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("src/x.cs: analyze_impact_file is not indexed for the head (commit_not_indexed, status indexing)", outcome.Error);
        Assert.Contains("CodeGraph could still not be read after 00:05:00 (Gate:CiTimeoutMinutes)", outcome.Error);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Blocked ", gate.Detail);
        Assert.Equal(StepOutcome.GateRejected, gate.Outcome);
    }

    [Fact]
    public async Task The_ci_timeout_bounds_the_overlay_wait_inside_one_read_of_the_gate()
    {
        // The overlay is still indexing for longer than the CI timeout: the gate escalates at the CI timeout, not at the overlay's.
        var codeGraph = new FakeCodeGraphRisk
        {
            Overlay = sha => new OverlayAnswer(false, "queued", 7, "queued", sha, null, null),
            Status = id => new OverlayAnswer(false, "indexing", id, "indexing", Sha1, null, null),
        };
        var clock = new SteppingClock(Start, TimeSpan.FromMinutes(1));
        var h = new Harness
        {
            GateOnFakeClock = true, Time = clock, CiTimeout = TimeSpan.FromMinutes(5), CodeGraph = codeGraph,
            RiskReader = new CodeGraphRiskReader(codeGraph, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
        };

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("CodeGraph could still not be read after 00:05:00 (Gate:CiTimeoutMinutes)", outcome.Error);
        // Cut off at the CI timeout (a few steps of slack), never left to run to the overlay's hour.
        var elapsed = clock.GetUtcNow() - Start;
        Assert.True(elapsed < TimeSpan.FromMinutes(20), $"the gate's wait ran {elapsed} of fake time");
        Assert.True(codeGraph.Calls.Count(c => c.Tool == "get_overlay_status") < 20);
    }

    [Fact]
    public async Task No_ready_overlay_of_the_head_keeps_the_gate_pending_until_the_ci_timeout()
    {
        var h = new Harness { GateOnFakeClock = true, Time = new SteppingClock(Start, TimeSpan.FromMinutes(1)), CiTimeout = TimeSpan.FromMinutes(5) };
        h.CodeGraph.Overlay = _ => new OverlayAnswer(true, "Error (overlays_unavailable): overlays are off", null, null, null, null, null,
            ErrorCode: "overlays_unavailable");

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains($"blast radius: no ready CodeGraph overlay of {Sha1} (refused", outcome.Error);
        Assert.DoesNotContain(h.CodeGraph.Calls, c => c.Tool == "analyze_impact_file");
        var overlay = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CodeGraphOverlay);
        Assert.Equal(CodeGraphOverlay.Refused, CodeGraphOverlay.FromDetail(overlay.Detail)!.Outcome);
    }

    [Fact]
    public async Task An_overlay_refused_for_a_while_is_asked_for_again_each_poll_and_its_outcome_recorded_once()
    {
        var h = new Harness();
        var requests = 0;
        h.CodeGraph.Overlay = sha => ++requests <= 2
            ? new OverlayAnswer(true, "Error (overlays_unavailable): overlays are off", null, null, null, null, null, ErrorCode: "overlays_unavailable")
            : new OverlayAnswer(false, "ready", 7, "ready", sha, FakeCodeGraphRisk.IndexCommit, null);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(3, requests);
        // Refused twice, recorded once; then ready.
        Assert.Equal([CodeGraphOverlay.Refused, CodeGraphOverlay.Ready], (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.CodeGraphOverlay)
            .Select(r => CodeGraphOverlay.FromDetail(r.Detail)!.Outcome));
    }

    [Fact]
    public async Task A_recorded_ready_overlay_codegraph_no_longer_uses_is_requested_again_and_the_pr_merges()
    {
        // The review recorded a ready overlay of the head; then the default branch moved past its base, so CodeGraph answers the head's
        // reads not indexed (status stale) until a fresh overlay is requested.
        var h = new Harness { Overlays = new ReviewOverlays(new FakeCodeGraphRisk(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)) };
        var requested = false;
        h.CodeGraph.Overlay = sha =>
        {
            requested = true;
            return new OverlayAnswer(false, "ready", 8, "ready", sha, "fe11a0", null);
        };
        h.CodeGraph.Impact = (path, sha) => requested ? FakeCodeGraphRisk.Found(path, sha, BlastRadius.Low) : FakeCodeGraphRisk.NotIndexed(sha, "stale");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Equal(Sha1, h.CodeGraph.Calls.Single(c => c.Tool == "request_overlay").Argument);
        Assert.Equal(2, h.CodeGraph.Calls.Count(c => c.Tool == "analyze_impact_file"));
        // The review's overlay, then the gate's fresh one (not one row per poll).
        Assert.Equal([7L, 8L], (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.CodeGraphOverlay)
            .Select(r => CodeGraphOverlay.FromDetail(r.Detail)!.OverlayId));
    }

    [Fact]
    public async Task A_sealed_change_escalates_without_asking_codegraph()
    {
        // Sealed beside a normal file (which needs risk-threshold): escalated, CodeGraph never asked.
        var h = new Harness();
        h.GitHub.Diff = head => TestPolicies.Diff("factory/gate.yaml", marker: head) + TestPolicies.Diff("src/x.cs", marker: head);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("touches sealed path(s), which always escalate: factory/gate.yaml", outcome.Error);
        Assert.Empty(h.CodeGraph.Calls);
    }

    [Fact]
    public async Task A_change_whose_new_tests_cannot_run_escalates_without_asking_codegraph()
    {
        var tests = new Harness();
        tests.TestRunner.RunThrows = new InvalidOperationException("no runner");

        var failed = await tests.Run();

        Assert.Equal(WorkState.Escalated, failed.State);
        Assert.Contains(GateChecks.NewTestsFailOnBase, failed.Error);
        Assert.Empty(tests.CodeGraph.Calls);
    }

    [Fact]
    public async Task Hotspots_read_during_one_wait_are_not_asked_for_again()
    {
        var h = new Harness();
        h.GitHub.Diff = head => TestPolicies.Diff("src/a.cs", marker: head) + TestPolicies.Diff("src/b.cs", marker: head);
        var bReads = 0;
        h.CodeGraph.Hotspot = path => path == "src/b.cs" && ++bReads <= 2 ? throw new HttpRequestException("connection refused") : FakeCodeGraphRisk.Hot(path, 0.1);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(3, h.CodeGraph.Calls.Count(c => c == ("file_hotspot", "src/b.cs")));
        Assert.Equal(1, h.CodeGraph.Calls.Count(c => c == ("file_hotspot", "src/a.cs"))); // read once, at the overlay's base commit
    }

    [Fact]
    public async Task A_push_during_the_codegraph_wait_sends_the_new_head_to_review_and_never_merges_the_old_one()
    {
        var h = new Harness();
        var first = true;
        h.CodeGraph.Hotspot = path =>
        {
            if (first)
            {
                first = false;
                h.GitHub.Head = ShaB; // pushed while the gate waits
                throw new HttpRequestException("connection refused");
            }
            return FakeCodeGraphRisk.Hot(path, 0.1);
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([$"merge 1 {ShaB}"], h.Merges);
        Assert.Contains(await h.Rows(), r => r.Step is null && r.State == WorkState.Review && r.Detail?.StartsWith($"head moved to {ShaB} while the merge gate waited") == true);
        Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision && r.Detail!.StartsWith("Merge ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pr_closed_during_the_codegraph_wait_escalates_and_is_not_merged()
    {
        var h = new Harness();
        h.CodeGraph.Hotspot = _ =>
        {
            h.GitHub.Open = false;
            throw new HttpRequestException("connection refused");
        };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("is closed outside the factory", outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Equal(1, h.CodeGraph.Calls.Count(c => c.Tool == "file_hotspot"));
    }

    [Fact]
    public async Task Without_codegraph_risk_threshold_fails_wherever_it_is_required()
    {
        var h = new Harness { NoCodeGraph = true };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("CodeGraph is not configured", outcome.Error);
        Assert.Empty(h.Merges);
    }
}
