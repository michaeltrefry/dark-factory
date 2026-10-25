using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Tests.Support;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25709: the merge gate's <c>risk-threshold</c> reads CodeGraph's blast radius (per touched file, for exactly the head) and hotspot
/// score (default branch, recorded with its commit). Code floor: a critical blast radius fails; <c>factory/gate.yaml</c> version 3 can
/// only tighten it (<c>max_blast_radius</c>) and opts in to a hotspot limit (<c>max_hotspot</c>, the hotspot score being CodeGraph's
/// riskScore). A file CodeGraph has no data for counts as none / 0. Since sc-26254 (the factory works with or without CodeGraph) a file
/// CodeGraph gives no input for — not configured, unreachable or refusing, the repository or the language not indexed, C4's notIndexed
/// with nothing on the default branch — is recorded as having no CodeGraph data and judged on its other inputs; never pending, never
/// blocked for it. An answer a reachable CodeGraph gives that cannot be used (another commit or file, an error, no found), once the
/// default-branch read did not replace it, is a misread and blocks (sc-26134).
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
    public void CodeGraph_inputs_that_could_not_be_read_are_recorded_as_no_codegraph_data_and_the_change_merges_on_its_other_inputs()
    {
        // sc-26254: no ready overlay of the head (e.g. timed out) — the gate neither waits nor blocks for it.
        var why = $"blast radius: no ready CodeGraph overlay of {Head} (timed-out)";
        var unreadable = new CodeGraphRisk(Head, [], [], [new NoCodeGraphData("src/a.cs", why), new NoCodeGraphData("src/b.cs", why)]);

        var decision = Evaluate(unreadable);

        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains($"no CodeGraph data: {why}: src/a.cs, src/b.cs", decision.Inputs);
        Assert.Contains("no CodeGraph data for 2 of 2 file(s), judged on size and fix rounds", decision.Detail);
        // Anything else blocking still blocks, the missing inputs recorded beside it.
        var blocked = Evaluate(unreadable, verdict: Full with { Verdict = ReviewVerdict.Fail });
        Assert.Equal(GateOutcome.Blocked, blocked.Outcome);
        Assert.Equal($"the review of {Head[..12]} is 'fail'", Assert.Single(blocked.Reasons));
        Assert.Contains($"no CodeGraph data: {why}", blocked.Detail);
    }

    [Fact]
    [FailsGateCheck(GateChecks.RiskThreshold)]
    public void Without_codegraph_data_the_size_limits_still_decide_risk_threshold()
    {
        // sc-26254: a file with no CodeGraph data is judged on its other inputs — a change over max_changed_lines still fails.
        var big = TestPolicies.Diff("src/a.cs", lines: 30);
        var policy = TestPolicies.Standard(maxLines: 10);

        var none = Evaluate(null, policy, big);

        Assert.Equal(GateOutcome.Blocked, none.Outcome);
        Assert.Equal("risk threshold: 30 changed lines exceed max_changed_lines 10", Assert.Single(none.Reasons));
        Assert.Contains("no CodeGraph data: CodeGraph is not configured (no CodeGraph token: ", none.Detail);
        // Within the limit, the same change without CodeGraph merges.
        Assert.Equal(GateOutcome.Merge, Evaluate(null, TestPolicies.Standard(maxLines: 100), big).Outcome);
    }

    [Fact]
    public void A_file_read_over_a_limit_blocks_even_when_another_has_no_codegraph_data()
    {
        var partial = new CodeGraphRisk(Head, [new FileBlastRadius("src/a.cs", BlastRadius.Critical, true)],
            [new FileHotspot("src/a.cs", 0.1, true, IndexCommit)],
            [new NoCodeGraphData("src/b.cs", "analyze_impact_file could not be sent: connection refused")]);

        var decision = Evaluate(partial);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal($"risk threshold: blast radius at {Head[..12]} over the code floor (critical always fails): src/a.cs critical",
            Assert.Single(decision.Reasons));
        Assert.Contains("no CodeGraph data: analyze_impact_file could not be sent: connection refused: src/b.cs", decision.Inputs);
        // Nothing over a limit: merged, the missing file recorded.
        var low = Evaluate(partial with { BlastRadius = [new FileBlastRadius("src/a.cs", BlastRadius.Low, true)] });
        Assert.Equal(GateOutcome.Merge, low.Outcome);
        Assert.Contains("no CodeGraph data for 1 of 2 file(s)", low.Detail);
    }

    [Fact]
    public void A_hotspot_codegraph_gave_is_held_to_max_hotspot_even_when_the_blast_radius_could_not_be_read()
    {
        var why = $"blast radius: no ready CodeGraph overlay of {Head} (refused)";
        var hotOnly = new CodeGraphRisk(Head, [], [new FileHotspot("src/a.cs", 0.9, true, IndexCommit), new FileHotspot("src/b.cs", 0.1, true, IndexCommit)],
            [new NoCodeGraphData("src/a.cs", why), new NoCodeGraphData("src/b.cs", why)]);

        var decision = Evaluate(hotOnly, TestPolicies.V3("high", "0.5"));

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal($"risk threshold: hotspot score (riskScore) over max_hotspot 0.5: src/a.cs 0.9 on the default branch at {IndexCommit}",
            Assert.Single(decision.Reasons));
        Assert.Equal(GateOutcome.Merge, Evaluate(hotOnly, TestPolicies.V3("high", "1")).Outcome);
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
            with { NoData = [] };
        var decision = Evaluate(risk, TestPolicies.Standard(maxLines: 10_000, maxFiles: 1_000), diff);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains($"over the code floor (critical always fails): {critical} critical", decision.Detail);
        Assert.Contains($"{critical} critical", decision.Inputs);
        Assert.Contains($"{files[7]} 0.9", decision.Inputs); // the hottest
        Assert.Contains("… 279 more", decision.Inputs); // 300 - the critical one - the first 20 others
        Assert.True(decision.Detail.Length < 20_000, $"gate row of {decision.Detail.Length} chars");
        var row = new LedgerEntry { State = WorkState.MergeGate, Step = RunPipeline.Steps.GateDecision, Detail = decision.Detail, Outcome = StepOutcome.GateRejected };
        Assert.Contains($"{critical} critical", LedgerReport.Facts([row], []));
        // Files with no CodeGraph data are capped too: per reason, and the reasons.
        var oneReason = Evaluate(null, TestPolicies.Standard(maxLines: 10_000, maxFiles: 1_000), diff);
        Assert.Equal(GateOutcome.Merge, oneReason.Outcome);
        Assert.Contains($"{files[19]}, … 280 more", oneReason.Inputs);
        Assert.True(oneReason.Detail.Length < 20_000, $"gate row of {oneReason.Detail.Length} chars");
        Assert.Contains("no CodeGraph data: CodeGraph is not configured", LedgerReport.Facts(
            [new LedgerEntry { State = WorkState.MergeGate, Step = RunPipeline.Steps.GateDecision, Detail = oneReason.Detail, Outcome = StepOutcome.Passed }], []));
        var many = new CodeGraphRisk(Head, [], [], Enumerable.Range(0, 50).Select(i => new NoCodeGraphData($"f{i}", $"entry {i}")).ToList());
        Assert.EndsWith("no CodeGraph data: entry 19: f19; … 30 more", many.NoDataText);
    }

    [Fact]
    public void CodeGraph_inputs_not_read_are_no_codegraph_data_and_inputs_read_for_another_commit_or_leaving_a_file_out_block()
    {
        var none = Evaluate(null);
        Assert.Equal(GateOutcome.Merge, none.Outcome);
        Assert.Contains("no CodeGraph data: CodeGraph is not configured (no CodeGraph token: CodeGraph:Token, env FACTORY_CODEGRAPH_TOKEN, or keychain "
            + "account 'codegraph-token' (service 'dark-factory')): src/a.cs, src/b.cs", none.Inputs);
        // sc-26134: inputs read for another head are a misread, not missing data — they block, whatever they say (low here).
        var other = Evaluate(Radii(BlastRadius.Low) with { HeadSha = Other });
        Assert.Equal(GateOutcome.Blocked, other.Outcome);
        Assert.Equal($"risk threshold: CodeGraph misanswered (CodeGraph's inputs were read for {Other[..12]}, not the head): src/a.cs, src/b.cs",
            Assert.Single(other.Reasons));
        Assert.DoesNotContain("no CodeGraph data", other.Detail);
        var partial = Radii(BlastRadius.Low);
        // sc-26134: a touched file the inputs leave out is a misread, never no data (it would fail open on a reader bug). A left-out
        // blast radius blocks under any policy.
        var leftOut = Evaluate(partial with { BlastRadius = partial.BlastRadius.Take(1).ToList() });
        Assert.Equal(GateOutcome.Blocked, leftOut.Outcome);
        Assert.Equal("risk threshold: CodeGraph misanswered (CodeGraph's inputs leave it out): src/b.cs", Assert.Single(leftOut.Reasons));
        Assert.DoesNotContain("no CodeGraph data", leftOut.Detail);
        // A left-out hotspot is a hotspot misread: it blocks where max_hotspot is set, and is only recorded with hotspots off or under v2.
        var noHotspot = partial with { Hotspots = partial.Hotspots.Skip(1).ToList() };
        Assert.Equal("risk threshold: CodeGraph misanswered (CodeGraph's inputs leave it out): src/a.cs",
            Assert.Single(Evaluate(noHotspot, TestPolicies.V3("high", "0.9")).Reasons));
        foreach (var policy in new[] { TestPolicies.V3("high", "off"), TestPolicies.Standard() })
        {
            var recorded = Evaluate(noHotspot, policy);
            Assert.Equal(GateOutcome.Merge, recorded.Outcome);
            Assert.Contains("; CodeGraph misanswered: CodeGraph's inputs leave it out (hotspot, not judged: no max_hotspot): src/a.cs", recorded.Inputs);
            Assert.DoesNotContain("no CodeGraph data", recorded.Detail);
        }
    }

    [Fact]
    public void A_change_whose_tiers_do_not_require_risk_threshold_does_not_use_codegraph()
    {
        var docs = TestPolicies.Diff("docs/a.md");
        var decision = Evaluate(null, diff: docs);
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.DoesNotContain("codegraph", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- languages CodeGraph does not index (sc-26133, P5 S7; CodeGraph's side is C3, sc-26137) ----

    private const string SwiftFile = "Sources/App/Counter.swift";

    [Fact]
    public void A_changed_file_whose_language_codegraph_does_not_index_is_recorded_as_no_codegraph_data_and_merges()
    {
        var diff = TestPolicies.Diff(SwiftFile);
        // CodeGraph has no data for it (found: false from both tools).
        var unindexed = new CodeGraphRisk(Head, [new FileBlastRadius(SwiftFile, BlastRadius.None, false)],
            [new FileHotspot(SwiftFile, 0, false, IndexCommit)], [], Unindexed: [new UnindexedFile(SwiftFile, "Swift", Flagged: true)]);

        var decision = Evaluate(unindexed, diff: diff);

        // sc-26254: recorded, not blocking (before: blocked closed).
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains($"no CodeGraph data: its language (Swift) is not indexed by CodeGraph: {SwiftFile}", decision.Inputs);
        Assert.Contains("no CodeGraph data for 1 of 1 file(s)", decision.Detail);
        // Its language is its one reason: not also "left out" (its inputs are set aside, not missing).
        Assert.DoesNotContain("leave it out", decision.Inputs);
        // An indexed file beside it is held to the floor.
        var both = TestPolicies.Diff(SwiftFile) + TestPolicies.Diff("src/a.cs");
        var critical = TestRisk.With(Head, both, f => f == "src/a.cs" ? BlastRadius.Critical : BlastRadius.None, _ => 0)
            with { Unindexed = [new UnindexedFile(SwiftFile, "Swift", Flagged: true)] };
        Assert.Equal($"risk threshold: blast radius at {Head[..12]} over the code floor (critical always fails): src/a.cs critical",
            Assert.Single(Evaluate(critical, diff: both).Reasons));
    }

    [Fact]
    public void Data_codegraph_answered_for_an_unindexed_file_is_kept_and_only_the_missing_part_is_no_data()
    {
        // sc-26254: the analyze_impact_file answer has graph data (found, critical) though file_hotspot says the language is not indexed:
        // the critical still blocks, and only the hotspot is recorded as missing, for its language.
        var diff = TestPolicies.Diff(SwiftFile);
        var risk = new CodeGraphRisk(Head, [new FileBlastRadius(SwiftFile, BlastRadius.Critical, true)],
            [new FileHotspot(SwiftFile, 0, false, IndexCommit)], [], Unindexed: [new UnindexedFile(SwiftFile, "Swift", Flagged: true)]);

        var decision = Evaluate(risk, diff: diff);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal($"risk threshold: blast radius at {Head[..12]} over the code floor (critical always fails): {SwiftFile} critical",
            Assert.Single(decision.Reasons));
        Assert.Contains($"no CodeGraph data: hotspot: its language (Swift) is not indexed by CodeGraph: {SwiftFile}", decision.Inputs);
        // Both answered with data: nothing is recorded as missing, and a found hotspot over an opted-in limit blocks too.
        var hotOnly = new CodeGraphRisk(Head, [new FileBlastRadius(SwiftFile, BlastRadius.None, false)],
            [new FileHotspot(SwiftFile, 0.9, true, IndexCommit)], [], Unindexed: [new UnindexedFile(SwiftFile, "Swift", Flagged: true)]);
        var hot = Evaluate(hotOnly, TestPolicies.V3("high", "0.5"), diff);
        Assert.Contains($"{SwiftFile} 0.9", Assert.Single(hot.Reasons));
        Assert.Contains($"no CodeGraph data: blast radius: its language (Swift) is not indexed by CodeGraph: {SwiftFile}", hot.Inputs);
    }

    [Theory]
    [InlineData("High", BlastRadius.High)]
    [InlineData("CRITICAL", BlastRadius.Critical)]
    [InlineData("low", BlastRadius.Low)]
    public void Codegraphs_blast_radius_is_read_in_any_case_and_the_policy_keeps_its_own_spelling(string answered, BlastRadius radius)
    {
        Assert.True(BlastRadii.TryParseAnswer(answered, out var parsed));
        Assert.Equal(radius, parsed);
        Assert.Equal(answered == BlastRadii.Name(radius), BlastRadii.TryParse(answered, out _));
    }

    [Fact]
    public async Task An_unrecognised_blast_radius_codegraph_answers_for_a_found_file_blocks_and_is_never_no_data()
    {
        var server = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.1)
            : args["path"]!.GetValue<string>() == "src/a.cs" ? ImpactAnswer(Head, "src/a.cs", true, "severe")
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "Critical"));
        var diff = TestPolicies.Diff("src/a.cs");

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);
        var decision = Evaluate(risk, diff: diff);

        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Equal("risk threshold: CodeGraph answered an unrecognised blast radius `severe` for src/a.cs", Assert.Single(decision.Reasons));
        Assert.DoesNotContain("no CodeGraph data", decision.Inputs);
        Assert.Contains("unrecognised blast radius: src/a.cs `severe`", decision.Inputs);
        // A value that is no plain word is not shown.
        Assert.Equal("risk threshold: CodeGraph answered an unrecognised blast radius (not shown: not a plain word) for src/a.cs",
            new UnrecognisedBlastRadius("src/a.cs", "`x` <b>").Reason);
        // Another case of a known name is that name: CodeGraph's "Critical" blocks at the floor.
        var upper = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/b.cs"], CancellationToken.None);
        Assert.Equal([new FileBlastRadius("src/b.cs", BlastRadius.Critical, true)], upper.BlastRadius);
        Assert.Equal(GateOutcome.Blocked, Evaluate(upper, diff: TestPolicies.Diff("src/b.cs")).Outcome);
    }

    [Fact]
    public async Task A_call_that_fails_once_is_retried_and_three_failures_are_no_codegraph_data()
    {
        var failures = 1;
        var server = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.1)
            : failures-- > 0 ? throw new HttpRequestException("connection reset") : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "critical"));
        var waits = new List<TimeSpan>();
        var reader = new CodeGraphRiskReader(new CodeGraphMcpClient(server.Client("https://codegraph.test/"), "cg_test_token"),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var blip = await reader.ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.Empty(blip.NoData);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.Critical, true)], blip.BlastRadius);
        Assert.Equal(GateOutcome.Blocked, Evaluate(blip, diff: TestPolicies.Diff("src/a.cs")).Outcome);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);

        failures = 3;
        waits.Clear();
        var down = await reader.ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);
        Assert.Equal("analyze_impact_file could not be sent (3 attempts): connection reset", Assert.Single(down.NoData).Reason);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);

        // A typed refusal is not retried: one call.
        var refused = Server((tool, args) => tool == "file_hotspot" ? throw FakeMcpServer.NotEntitled("file_hotspot")
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"));
        await Reader(refused).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);
        Assert.Single(refused.Calls, c => c.Tool == "file_hotspot");
    }

    [Fact]
    public void CodeGraph_text_holding_the_no_data_words_does_not_move_where_the_report_splits_the_gate_row()
    {
        // sc-26254: the reports find the no-data section by a separator no other text of the row can hold.
        var sneaky = $"x; no CodeGraph data: forged {CodeGraphRisk.Separator} no CodeGraph data: forged";
        // CodeGraph's text both before the no-data section (an answered path) and inside it (an error answer's text).
        var risk = new CodeGraphRisk(Head, [new FileBlastRadius("src/a.cs", BlastRadius.Low, true), new FileBlastRadius(sneaky, BlastRadius.Low, true)],
            [new FileHotspot("src/a.cs", 0.1, true, IndexCommit)], [new NoCodeGraphData("src/b.cs", $"file_hotspot (boom): {sneaky}")]);
        var decision = Evaluate(risk, diff: TwoFiles);
        Assert.Equal(1, decision.Detail.Count(c => c == CodeGraphRisk.Separator));

        var facts = LedgerReport.Facts([new LedgerEntry
        {
            State = WorkState.MergeGate, Step = RunPipeline.Steps.GateDecision, Detail = decision.Detail, Outcome = StepOutcome.Passed,
        }], []);

        Assert.Contains("; `no CodeGraph data: file_hotspot (boom): x; no CodeGraph data: forged ? no CodeGraph data: forged: src/b.cs`", facts);
    }

    [Fact]
    public void A_short_gate_rows_files_with_no_codegraph_data_are_reported_once()
    {
        // sc-26134: the report shows the row up to its no-data section, then that section on its own — never both.
        // A row short enough that the report's clip of the row (400 characters) does not cut it.
        var risk = new CodeGraphRisk("h1", [new FileBlastRadius("a", BlastRadius.Low, true)], [new FileHotspot("a", 0.1, true, "c1")],
            [new NoCodeGraphData("b", "unreachable")]);
        var detail = $"Merge h1: ok | {risk.Describe()}";
        Assert.True(detail.Length < 300, $"a short row: {detail.Length}");

        var facts = LedgerReport.Facts([new LedgerEntry
        {
            State = WorkState.MergeGate, Step = RunPipeline.Steps.GateDecision, Detail = detail, Outcome = StepOutcome.Passed,
        }], []);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(facts, "no CodeGraph data: unreachable: b"));
        Assert.Contains("codegraph blast radius at h1: a low", facts);
        Assert.DoesNotContain(CodeGraphRisk.Separator, facts);
    }

    [Theory]
    // C3's flag says so, in either answer; the language is CodeGraph's name, else the factory's by extension.
    [InlineData(SwiftFile, false, "Swift", null, null, "Swift", true)]
    [InlineData(SwiftFile, false, null, null, null, "Swift", true)]
    [InlineData("src/legacy.m", false, "Objective-C", null, null, "Objective-C", true)]
    [InlineData("src/legacy.m", null, null, false, null, "Objective-C", true)]
    [InlineData("tools/gen.zig", null, null, false, null, "language unknown", true)]
    [InlineData("tools/gen.zig", false, "Objective-C++", null, null, "Objective-C++", true)]
    // CodeGraph's name is shown only when inert (E3): anything else falls back to the factory's name by extension, or none.
    [InlineData(SwiftFile, false, "x\n`y", null, null, "Swift", true)]
    [InlineData("tools/gen.zig", false, "<img src=x>", null, null, "language unknown", true)]
    [InlineData(SwiftFile, null, null, false, "@user", "Swift", true)]
    // Both answers judged together: either "false" wins; "true" in one and no flag in the other is indexed.
    [InlineData(SwiftFile, true, "Swift", false, "Swift", "Swift", true)]
    [InlineData(SwiftFile, false, "Swift", true, "Swift", "Swift", true)]
    [InlineData(SwiftFile, null, null, true, "Swift", null, false)]
    [InlineData(SwiftFile, true, "Swift", null, null, null, false)]
    // C3's flag says indexed: never failed closed, whatever the extension.
    [InlineData(SwiftFile, true, "Swift", true, null, null, false)]
    // No flag (a CodeGraph before C3): a file of a language CodeGraph's main did not index, with no data, is unindexed; other files
    // are as before (E7).
    [InlineData(SwiftFile, null, null, null, null, "Swift", false)]
    [InlineData("Sources/App/COUNTER.SWIFT", null, null, null, null, "Swift", false)]
    [InlineData("src/legacy.m", null, null, null, null, "Objective-C", false)]
    [InlineData("src/bridge.mm", null, null, null, null, "Objective-C++", false)]
    [InlineData("app/src/Main.kt", null, null, null, null, "Kotlin", false)]
    [InlineData("build.gradle.kts", null, null, null, null, "Kotlin", false)]
    [InlineData("src/Main.scala", null, null, null, null, "Scala", false)]
    [InlineData("lib/main.dart", null, null, null, null, "Dart", false)]
    [InlineData("src/a.cs", null, null, null, null, null, false)]
    [InlineData("crates/words/src/lib.rs", null, null, null, null, null, false)]
    [InlineData("docs/notes.md", null, null, null, null, null, false)]
    public async Task The_reader_finds_the_files_whose_language_codegraph_does_not_index(string path, bool? impactIndexed, string? impactLanguage,
        bool? hotspotIndexed, string? hotspotLanguage, string? language, bool flagged)
    {
        var server = Server((tool, args) => tool == "analyze_impact_file"
            ? C7Answers.ImpactFile(Project, path, Head, false, "none", languageIndexed: impactIndexed, language: impactLanguage)
            : C7Answers.Hotspot(Project, path, null, languageIndexed: hotspotIndexed, language: hotspotLanguage));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, [path], CancellationToken.None);

        Assert.Empty(risk.NoData);
        Assert.Single(risk.BlastRadius);
        Assert.Single(risk.Hotspots);
        if (language is null)
        {
            Assert.Empty(risk.UnindexedFiles);
            Assert.Equal(GateOutcome.Merge, Evaluate(risk, diff: TestPolicies.Diff(path)).Outcome);
            return;
        }
        var file = Assert.Single(risk.UnindexedFiles);
        Assert.Equal(path, file.Path);
        Assert.Equal(language == "language unknown" ? null : language, file.Language);
        Assert.Equal(flagged, file.Flagged);
        // sc-26254: recorded as no CodeGraph data, naming the file and its language, and merged.
        var decision = Evaluate(risk, diff: TestPolicies.Diff(path));
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains($"no CodeGraph data: {file.Reason}: {path}", decision.Inputs);
        Assert.Contains($"its language ({(language == "language unknown" ? "unknown" : language)}) is not indexed by CodeGraph", file.Reason);
        Assert.Equal(!flagged, file.Reason.Contains("before C3", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_flag_a_swift_file_codegraph_has_graph_data_for_is_indexed()
    {
        // A CodeGraph before C3 with nodes for the file indexes its language: no claim is made against it.
        Assert.Null(LanguageCoverage.Of(SwiftFile, null, null, null, null, found: true));
        Assert.NotNull(LanguageCoverage.Of(SwiftFile, null, null, null, null, found: false));
        Assert.NotNull(LanguageCoverage.Of(SwiftFile, false, "Swift", null, null, found: true));
        Assert.Equal("Sources/App/Counter.swift (Swift; CodeGraph answered no languageIndexed flag (before C3))",
            LanguageCoverage.Of(SwiftFile, null, null, null, null, found: false)!.ToString());
    }

    [Fact]
    public void Codegraphs_word_replaces_the_inference_for_the_same_file_never_the_other_way_round()
    {
        var inferred = new UnindexedFile(SwiftFile, "Swift", Flagged: false);
        var flagged = new UnindexedFile(SwiftFile, "Swift", Flagged: true);

        var files = new List<UnindexedFile>();
        LanguageCoverage.Add(files, inferred);
        LanguageCoverage.Add(files, flagged);
        Assert.Equal(flagged, Assert.Single(files));

        LanguageCoverage.Add(files, inferred);
        Assert.Equal(flagged, Assert.Single(files));
    }

    [Theory]
    [InlineData("Swift", "Swift")]
    [InlineData("  C# ", "C#")]
    [InlineData("C++", "C++")]
    [InlineData("Objective-C_2.0", "Objective-C_2.0")]
    [InlineData("x\n`y", null)]
    [InlineData("Swift`", null)]
    [InlineData("Swift\u202e", null)]
    [InlineData("Swïft", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Codegraphs_language_name_is_shown_only_when_inert(string? language, string? shown)
    {
        Assert.Equal(shown, LanguageCoverage.Name(language));
        Assert.Equal(new string('A', LanguageCoverage.MaxNameLength), LanguageCoverage.Name(new string('A', 200)));
    }

    [Fact]
    public async Task A_file_codegraph_says_is_not_indexed_stays_unindexed_when_its_hotspot_cannot_be_read()
    {
        var server = Server((tool, args) => tool == "analyze_impact_file"
            ? C7Answers.ImpactFile(Project, SwiftFile, Head, false, "none", languageIndexed: false, language: "Swift")
            : FakeMcpServer.OverlayError("repo_not_found", "Unknown repository 'Sandbox'."));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, [SwiftFile], CancellationToken.None);

        // The reader records the unusable hotspot answer as a misread. Only analyze_impact_file said CodeGraph indexes no Swift, so the
        // hotspot's misread stands (sc-26134): recorded, and blocking only where max_hotspot is set.
        Assert.StartsWith("file_hotspot (repo_not_found)", Assert.Single(risk.Misreads).Reason);
        Assert.Equal(new UnindexedFile(SwiftFile, "Swift", true, ByImpact: true), Assert.Single(risk.UnindexedFiles));
        Assert.StartsWith("risk threshold: CodeGraph misanswered (file_hotspot (repo_not_found)",
            Assert.Single(Evaluate(risk, TestPolicies.V3("high", "0.9"), TestPolicies.Diff(SwiftFile)).Reasons));
        var decision = Evaluate(risk, diff: TestPolicies.Diff(SwiftFile));
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains("CodeGraph misanswered: file_hotspot (repo_not_found)", decision.Inputs);
        // The language is the reason the gate records for the file (its blast radius and hotspot mean nothing there).
        Assert.Contains($"no CodeGraph data: its language (Swift) is not indexed by CodeGraph: {SwiftFile}", decision.Inputs);
    }

    [Fact]
    public async Task A_cached_hotspot_keeps_its_unindexed_flag()
    {
        var server = Server((tool, args) => tool == "analyze_impact_file"
            ? C7Answers.ImpactFile(Project, "src/a.kt", Head, false, "none", languageIndexed: true, language: null)
            : C7Answers.Hotspot(Project, "src/a.kt", null, languageIndexed: false, language: "kotlin"));
        var cache = new HotspotCache();

        await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.kt"], CancellationToken.None, cache);
        var again = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.kt"], CancellationToken.None, cache);

        Assert.Single(server.Calls, c => c.Tool == "file_hotspot");
        Assert.Equal(new UnindexedFile("src/a.kt", "kotlin", true, ByHotspot: true), Assert.Single(again.UnindexedFiles));
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
        new(new CodeGraphMcpClient(server.Client("https://codegraph.test/"), "cg_test_token"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), FakeCodeGraphRisk.NoWait);

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

        Assert.Empty(risk.NoData);
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

    /// <summary>The amendment's causes (E5, sc-26254): CodeGraph refusing or unreachable, and C4's notIndexed with nothing on the default branch.</summary>
    public static TheoryData<string, string> NoDataAnswers() => new()
    {
        { "not-indexed", "blast radius: analyze_impact_file is not indexed for the head (commit_not_indexed, status indexing); on the default branch: analyze_impact_file is not indexed" },
        { "not-entitled", "not entitled to analyze_impact_file" },
        { "unreachable", "analyze_impact_file could not be sent (3 attempts): " },
        { "hotspot-not-entitled", "not entitled to file_hotspot" },
    };

    /// <summary>
    /// A reachable CodeGraph's unusable answers (sc-26134): misreads. A head blast radius answer is tried on the default branch first,
    /// which here misanswers too (each kind's answer is the same with or without <c>sha</c>).
    /// </summary>
    public static TheoryData<string, string> MisreadAnswers() => new()
    {
        { "unknown-repo", "analyze_impact_file (repo_not_found): Error (repo_not_found): Unknown repository 'Sandbox'." },
        { "other-commit", $"analyze_impact_file answered for {Other}, not the head" },
        { "no-found", "analyze_impact_file answered no 'found'" },
        { "hotspot-error", "file_hotspot (repo_not_found): Error (repo_not_found): Unknown repository 'Sandbox'." },
        { "hotspot-overlay", "file_hotspot answered from 'overlay', not the default branch" },
        { "hotspot-no-commit", "file_hotspot named no commit (neither commitSha nor indexCommitSha)" },
        { "hotspot-no-score", "file_hotspot answered no riskScore" },
        { "other-path", "analyze_impact_file answered about 'src/b.cs', not src/a.cs" },
        { "no-path", "analyze_impact_file answered about no path, not src/a.cs" },
        { "hotspot-other-path", "file_hotspot answered about 'src/a.cs.bak', not src/a.cs" },
    };

    [Theory]
    [MemberData(nameof(NoDataAnswers))]
    public async Task A_file_codegraph_cannot_give_data_for_has_no_codegraph_data_and_the_gate_merges_on_its_other_inputs(string kind, string why)
    {
        var risk = await Reader(UnusableServer(kind)).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        var noData = Assert.Single(risk.NoData);
        Assert.Equal("src/a.cs", noData.Path);
        Assert.StartsWith(why, noData.Reason);
        Assert.Empty(risk.Misreads);
        // The other tool's answer is still used: one of the two inputs is read whichever failed.
        Assert.Equal(1, risk.BlastRadius.Count + risk.Hotspots.Count);
        var decision = Evaluate(risk, diff: TestPolicies.Diff("src/a.cs"));
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains($"no CodeGraph data: {why}", decision.Inputs);
    }

    [Theory]
    [MemberData(nameof(MisreadAnswers))]
    public async Task A_file_a_reachable_codegraph_misanswers_is_a_misread_that_blocks_never_no_codegraph_data(string kind, string why)
    {
        var risk = await Reader(UnusableServer(kind)).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        // sc-26134: a misread is not missing data.
        Assert.Empty(risk.NoData);
        var misread = Assert.Single(risk.Misreads);
        Assert.Equal("src/a.cs", misread.Path);
        Assert.Contains(why, misread.Reason);
        Assert.Equal(1, risk.BlastRadius.Count + risk.Hotspots.Count);
        var hotspot = kind.StartsWith("hotspot-", StringComparison.Ordinal);
        Assert.Equal(hotspot, misread.Hotspot);
        var diff = TestPolicies.Diff("src/a.cs");
        // A blast-radius misread blocks under any policy; a hotspot misread only where the policy sets max_hotspot (sc-26134).
        foreach (var (policy, judgesHotspots) in new[] { (TestPolicies.Standard(), false), (TestPolicies.V3("high", "off"), false), (TestPolicies.V3("high", "0.9"), true) })
        {
            var decision = Evaluate(risk, policy, diff);
            Assert.Contains("; CodeGraph misanswered: ", decision.Inputs);
            Assert.Contains(why, decision.Inputs);
            Assert.DoesNotContain("no CodeGraph data", decision.Detail);
            if (hotspot && !judgesHotspots)
            {
                Assert.Equal(GateOutcome.Merge, decision.Outcome);
                Assert.Contains("(hotspot, not judged: no max_hotspot)", decision.Inputs);
                continue;
            }
            Assert.Equal(GateOutcome.Blocked, decision.Outcome);
            Assert.StartsWith("risk threshold: CodeGraph misanswered (", Assert.Single(decision.Reasons));
            Assert.Contains(why, decision.Reasons[0]);
            Assert.DoesNotContain("not judged", decision.Inputs);
        }
    }

    [Fact]
    public void A_language_flag_excuses_only_the_misread_of_the_tool_whose_answer_carried_it()
    {
        // sc-26134: languageIndexed: false in file_hotspot's answer does not excuse analyze_impact_file's misread, nor the other way round.
        CodeGraphRisk Risk(bool hotspotMisread, bool byImpact, bool byHotspot) => new(Head, [], [], [],
            Unindexed: [new UnindexedFile(SwiftFile, "Swift", true, ByImpact: byImpact, ByHotspot: byHotspot)],
            Misread: [new CodeGraphMisread(SwiftFile, "boom", hotspotMisread)]);
        var diff = TestPolicies.Diff(SwiftFile);
        var limit = TestPolicies.V3("high", "0.9");

        var impactKept = Evaluate(Risk(hotspotMisread: false, byImpact: false, byHotspot: true), limit, diff);
        Assert.Equal("risk threshold: CodeGraph misanswered (boom): " + SwiftFile, Assert.Single(impactKept.Reasons));
        var hotspotKept = Evaluate(Risk(hotspotMisread: true, byImpact: true, byHotspot: false), limit, diff);
        Assert.Equal("risk threshold: CodeGraph misanswered (boom): " + SwiftFile, Assert.Single(hotspotKept.Reasons));

        // The same tool's own flag explains its unusable answer: no data, its language the reason.
        foreach (var excused in new[] { Risk(false, true, false), Risk(true, false, true) })
        {
            var decision = Evaluate(excused, limit, diff);
            Assert.Equal(GateOutcome.Merge, decision.Outcome);
            Assert.DoesNotContain("misanswered", decision.Detail);
            Assert.Contains($"its language (Swift) is not indexed by CodeGraph: {SwiftFile}", decision.Inputs);
        }
    }

    private static FakeMcpServer UnusableServer(string kind) =>
        Server((tool, args) => (kind, tool) switch
        {
            ("not-indexed", "analyze_impact_file") => FakeMcpServer.NotIndexed(Project, Head, "indexing", 41, IndexCommit),
            ("unknown-repo", "analyze_impact_file") or ("hotspot-error", "file_hotspot") =>
                FakeMcpServer.OverlayError("repo_not_found", "Unknown repository 'Sandbox'."),
            ("not-entitled", "analyze_impact_file") => throw FakeMcpServer.NotEntitled("analyze_impact_file"),
            ("hotspot-not-entitled", "file_hotspot") => throw FakeMcpServer.NotEntitled("file_hotspot"),
            ("other-commit", "analyze_impact_file") => ImpactAnswer(Other, "src/a.cs", true, "low"),
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

    [Fact]
    public async Task An_unusable_head_answer_is_replaced_by_the_default_branch_read_for_that_file_only()
    {
        // sc-26134: the head misanswers src/a.cs (another commit); its default-branch blast radius is usable, so it is used — labelled
        // not the PR head and held to the floor — and src/b.cs is still read at the head.
        var radius = "low";
        var server = Server((tool, args) => (tool, args["path"]!.GetValue<string>(), args.ContainsKey("sha")) switch
        {
            ("file_hotspot", var p, _) => HotspotAnswer(p, true, 0.1),
            ("analyze_impact_file", "src/a.cs", true) => ImpactAnswer(Other, "src/a.cs", true, "low"),
            ("analyze_impact_file", var p, false) => C7Answers.ImpactFile(Project, p, IndexCommit, true, radius, overlay: false),
            (_, var p, _) => ImpactAnswer(Head, p, true, "medium"),
        });

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None);

        Assert.Empty(risk.NoData);
        Assert.Empty(risk.Misreads);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.Low, true, IndexCommit), new FileBlastRadius("src/b.cs", BlastRadius.Medium, true)],
            risk.BlastRadius);
        Assert.Single(server.Calls, c => c.Tool == "analyze_impact_file" && !c.Arguments.AsObject().ContainsKey("sha"));
        Assert.False(risk.OverlayUnusable);
        Assert.Equal(GateOutcome.Merge, Evaluate(risk).Outcome);
        // The default branch's critical blocks at the floor, named as read there.
        radius = "critical";
        var critical = Evaluate(await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None));
        Assert.Equal($"risk threshold: blast radius on the default branch as of {IndexCommit[..12]}, not the PR head, over the code floor (critical always fails): src/a.cs critical",
            Assert.Single(critical.Reasons));
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

        Assert.Empty(risk.NoData);
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
    public async Task A_hotspot_read_earlier_is_reused_by_its_index_commit_even_when_the_metrics_are_older()
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
    public async Task No_ready_overlay_of_the_head_reads_the_blast_radius_from_the_default_branch_labelled_and_held_to_the_floor()
    {
        // sc-26254: without the head's overlay the blast radius is read from the default-branch index (no sha), recorded with its commit
        // and labelled not the PR head — and held to the same limits.
        var radius = "low";
        var server = Server((tool, args) => (tool, args.ContainsKey("sha")) switch
        {
            ("file_hotspot", _) => HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3),
            ("analyze_impact_file", false) => C7Answers.ImpactFile(Project, args["path"]!.GetValue<string>(), IndexCommit, true, radius, overlay: false),
            _ => ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "low"),
        });
        var timedOut = new CodeGraphOverlay(CodeGraphOverlay.TimedOut, Head, 41, Reason: "not ready within 10 min");
        var diff = TestPolicies.Diff("src/a.cs");

        var risk = await Reader(server).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);

        Assert.Empty(risk.NoData);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.Low, true, IndexCommit)], risk.BlastRadius);
        Assert.Equal($$"""{"repo":"{{Project}}","path":"src/a.cs","depth":3}""",
            Assert.Single(server.Calls, c => c.Tool == "analyze_impact_file").Arguments.ToJsonString());
        Assert.Equal([new FileHotspot("src/a.cs", 0.3, true, IndexCommit, IndexCommit)], risk.Hotspots);
        var fine = Evaluate(risk, diff: diff);
        Assert.Equal(GateOutcome.Merge, fine.Outcome);
        Assert.Contains($"codegraph blast radius on the default branch as of {IndexCommit}, not the PR head: src/a.cs low", fine.Inputs);
        Assert.Contains($"worst blast radius low (src/a.cs, on the default branch as of {IndexCommit[..12]}, not the PR head)", fine.Detail);

        // A critical file on the default branch blocks at the floor, named as read there.
        radius = "critical";
        var critical = await Reader(server).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);
        var blocked = Evaluate(critical, diff: diff);
        Assert.Equal(GateOutcome.Blocked, blocked.Outcome);
        Assert.Equal($"risk threshold: blast radius on the default branch as of {IndexCommit[..12]}, not the PR head, over the code floor (critical always fails): src/a.cs critical",
            Assert.Single(blocked.Reasons));

        // A ready overlay of another commit is not the head's either: the default branch is read.
        var other = await Reader(server).ReadAsync(Repo, Head, Ready with { HeadSha = Other }, ["src/a.cs"], CancellationToken.None);
        Assert.Equal(IndexCommit, Assert.Single(other.BlastRadius).DefaultBranchCommit);

        // A default-branch read CodeGraph answers with an error is a misread (sc-26134), both reasons said; it blocks.
        var neither = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3)
            : FakeMcpServer.OverlayError("repo_not_found", "Unknown repository 'Sandbox'."));
        var none = await Reader(neither).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);
        Assert.Equal(new CodeGraphMisread("src/a.cs", $"blast radius: no ready CodeGraph overlay of {Head} (timed-out (overlay 41): not ready within 10 min); "
            + "on the default branch: analyze_impact_file (repo_not_found): Error (repo_not_found): Unknown repository 'Sandbox'."), Assert.Single(none.Misreads));
        Assert.Empty(none.NoData);
        Assert.Empty(none.BlastRadius);
        // Only C4's typed notIndexed on the default branch is no data there: nothing to read, not a misanswer.
        var notIndexed = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3)
            : FakeMcpServer.NotIndexed(Project, Head, "indexing", 41, IndexCommit));
        var nothing = await Reader(notIndexed).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);
        Assert.Empty(nothing.Misreads);
        Assert.Equal("src/a.cs", Assert.Single(nothing.NoData).Path);
    }

    [Fact]
    public async Task A_default_branch_answer_that_names_no_commit_or_comes_from_an_overlay_is_not_used()
    {
        var timedOut = new CodeGraphOverlay(CodeGraphOverlay.TimedOut, Head, 41, Reason: "not ready");
        var overlay = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3)
            : ImpactAnswer(Head, args["path"]!.GetValue<string>(), true, "critical"));
        var fromOverlay = await Reader(overlay).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);
        // sc-26134: a reachable CodeGraph misanswering the default-branch read is a misread, which blocks.
        Assert.Empty(fromOverlay.NoData);
        Assert.EndsWith("on the default branch: analyze_impact_file answered from 'overlay', not the default branch", Assert.Single(fromOverlay.Misreads).Reason);
        Assert.Equal(GateOutcome.Blocked, Evaluate(fromOverlay, diff: TestPolicies.Diff("src/a.cs")).Outcome);

        var unnamed = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.3)
            : FakeMcpServer.Structured("# File Blast Radius: Sandbox/src/a.cs", new JsonObject
                { ["source"] = "default-branch", ["path"] = "src/a.cs", ["found"] = true, ["blastRadius"] = "critical" }));
        var noCommit = await Reader(unnamed).ReadAsync(Repo, Head, timedOut, ["src/a.cs"], CancellationToken.None);
        Assert.EndsWith("on the default branch: analyze_impact_file named no commit", Assert.Single(noCommit.Misreads).Reason);
    }

    [Fact]
    public async Task A_call_that_cannot_be_made_ends_that_tools_calls_and_an_unusable_answer_concerns_its_file_only()
    {
        // CodeGraph unreachable for analyze_impact_file: asked once, every file recorded, the hotspots still read.
        var down = Server((tool, args) => (tool, args["path"]!.GetValue<string>()) switch
        {
            ("analyze_impact_file", _) => throw new HttpRequestException("connection refused"),
            (_, var p) => HotspotAnswer(p, true, 0.1),
        });

        var risk = await Reader(down).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None);

        // Tried 3 times in all (a blip is not missing data, sc-26254), then the tool ends: never once per file.
        Assert.Equal(CodeGraphRiskReader.Attempts, down.Calls.Count(c => c.Tool == "analyze_impact_file"));
        Assert.Equal(["src/a.cs", "src/b.cs"], risk.NoData.Select(n => n.Path));
        Assert.StartsWith("analyze_impact_file could not be sent (3 attempts): connection refused", risk.NoData[0].Reason);
        Assert.StartsWith("analyze_impact_file not asked: analyze_impact_file could not be sent", risk.NoData[1].Reason);
        Assert.Equal(2, risk.Hotspots.Count);
        Assert.False(risk.OverlayUnusable);

        // An answer for another commit (at the head and on the default branch) concerns that file only: the next file is asked and
        // used, and the misread blocks on its own (sc-26134: src/b.cs is low).
        var oneOff = Server((tool, args) => (tool, args["path"]!.GetValue<string>()) switch
        {
            ("analyze_impact_file", "src/a.cs") => ImpactAnswer(Other, "src/a.cs", true, "low"),
            ("analyze_impact_file", var p) => ImpactAnswer(Head, p, true, "low"),
            (_, var p) => HotspotAnswer(p, true, 0.1),
        });
        var partial = await Reader(oneOff).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None);
        Assert.Empty(partial.NoData);
        Assert.Equal("src/a.cs", Assert.Single(partial.Misreads).Path);
        Assert.Equal([new FileBlastRadius("src/b.cs", BlastRadius.Low, true)], partial.BlastRadius);
        Assert.Equal(GateOutcome.Blocked, Evaluate(partial).Outcome);

        // A head misread whose default-branch read cannot be made stays a misread (never laundered into no data by the outage).
        var thenDown = Server((tool, args) => (tool, args.ContainsKey("sha")) switch
        {
            ("analyze_impact_file", true) => ImpactAnswer(Other, args["path"]!.GetValue<string>(), true, "low"),
            ("analyze_impact_file", false) => throw new HttpRequestException("connection refused"),
            _ => HotspotAnswer(args["path"]!.GetValue<string>(), true, 0.1),
        });
        var lost = await Reader(thenDown).ReadAsync(Repo, Head, Ready, ["src/a.cs", "src/b.cs"], CancellationToken.None);
        var first = Assert.Single(lost.Misreads);
        Assert.Equal("src/a.cs", first.Path);
        Assert.Contains("on the default branch: analyze_impact_file could not be sent (3 attempts): connection refused", first.Reason);
        Assert.Equal("src/b.cs", Assert.Single(lost.NoData).Path);
        Assert.Equal(GateOutcome.Blocked, Evaluate(lost).Outcome);
    }

    [Fact]
    public async Task An_answer_naming_the_asked_path_another_way_is_the_same_file()
    {
        var server = Server((tool, args) => tool == "file_hotspot" ? HotspotAnswer("./src/a.cs", true, 0.2) : ImpactAnswer(Head, "src/x/../a.cs", true, "low"));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.Empty(risk.NoData);
        Assert.Equal([new FileBlastRadius("src/a.cs", BlastRadius.Low, true)], risk.BlastRadius);
    }

    [Fact]
    public async Task A_not_indexed_answer_marks_the_head_overlay_unusable()
    {
        var server = Server((tool, args) => FakeMcpServer.NotIndexed(Project, Head, "stale", 41, IndexCommit));

        var risk = await Reader(server).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);

        Assert.True(risk.OverlayUnusable);
        Assert.Contains("status stale", Assert.Single(risk.NoData, n => n.Reason.Contains("analyze_impact_file is not indexed for the head", StringComparison.Ordinal)).Reason);
    }

    [Fact]
    public async Task A_repository_codegraph_does_not_index_or_cannot_be_asked_about_has_no_codegraph_data()
    {
        var unindexed = FakeMcpServer.CodeGraph("- **Other** [csharp]\n  Repo: https://github.com/o/other\n", (_, _) => FakeMcpServer.Text("x"));
        Assert.Equal(new NoCodeGraphData("src/a.cs", "o/r: repository not indexed in CodeGraph"),
            Assert.Single((await Reader(unindexed).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None)).NoData));

        var down = new FakeMcpServer("/mcp", [], (_, _) => throw new HttpRequestException("connection refused"));
        var risk = await Reader(down).ReadAsync(Repo, Head, Ready, ["src/a.cs"], CancellationToken.None);
        Assert.StartsWith("CodeGraph could not be reached or asked: ", Assert.Single(risk.NoData).Reason);
        Assert.Equal(GateOutcome.Merge, Evaluate(risk, diff: TestPolicies.Diff("src/a.cs")).Outcome);
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
    public async Task A_pr_changing_a_swift_file_codegraph_does_not_index_merges_recording_no_codegraph_data()
    {
        static string SwiftDiff(string head) => TestPolicies.Diff("Sources/App/Counter.swift", marker: head);

        // A CodeGraph before C3 (no languageIndexed flag) with no data for the file: recorded, never blocking (sc-26254).
        var before = new Harness();
        before.GitHub.Diff = SwiftDiff;
        var merged = await before.Run();
        Assert.True(merged.Succeeded, merged.Error);
        Assert.Equal(WorkState.Watch, merged.State);
        var gate = Assert.Single(await before.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains("no CodeGraph data: its language (Swift) is not indexed by CodeGraph (CodeGraph answered no languageIndexed flag: before C3): "
            + "Sources/App/Counter.swift", gate.Detail);
        // The reports render it from the ledger (E8).
        Assert.Contains("no CodeGraph data: its language (Swift) is not indexed by CodeGraph", LedgerReport.Facts(await before.Rows(), []));

        // CodeGraph's C3 says it indexes Swift: its data is used.
        var after = new Harness();
        after.GitHub.Diff = SwiftDiff;
        after.CodeGraph.Impact = (path, sha) => C7Answers.ParsedImpactFile("Sandbox", path, sha, false, "none", languageIndexed: true, language: "swift");
        var indexed = await after.Run();
        Assert.True(indexed.Succeeded, indexed.Error);
        Assert.DoesNotContain("no CodeGraph data", Assert.Single(await after.Rows(), r => r.Step == RunPipeline.Steps.GateDecision).Detail);
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
    public async Task CodeGraph_unreachable_is_recorded_as_no_codegraph_data_and_the_pr_merges_without_waiting()
    {
        var h = new Harness();
        h.CodeGraph.Hotspot = _ => throw new HttpRequestException("connection refused");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        // Tried 3 times (the retries of a call that could not be made), then recorded: no wait for CodeGraph, no second read.
        Assert.Equal(CodeGraphRiskReader.Attempts, h.CodeGraph.Calls.Count(c => c.Tool == "file_hotspot"));
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains("no CodeGraph data: file_hotspot could not be sent (3 attempts): connection refused: src/x.cs", gate.Detail);
        // The blast radius CodeGraph did answer is still recorded.
        Assert.Contains($"codegraph blast radius at {Sha1}: src/x.cs none (no data)", gate.Detail);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_head_codegraph_answers_not_indexed_for_is_asked_for_once_more_then_merges_with_no_codegraph_data()
    {
        var h = new Harness();
        h.CodeGraph.Impact = (_, sha) => FakeCodeGraphRisk.NotIndexed(sha, "indexing");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        // The overlay requested, found unusable, requested once more — and each time the blast radius read from the default branch instead.
        Assert.Equal(2, h.CodeGraph.Calls.Count(c => c.Tool == "request_overlay"));
        Assert.Equal(2, h.CodeGraph.Calls.Count(c => c.Tool == "analyze_impact_file"));
        Assert.Equal(2, h.CodeGraph.Calls.Count(c => c.Tool == FakeCodeGraphRisk.DefaultBranchImpactCall));
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains($"codegraph blast radius on the default branch as of {FakeCodeGraphRisk.IndexCommit}, not the PR head: src/x.cs none (no data)", gate.Detail);
        Assert.DoesNotContain("no CodeGraph data", gate.Detail);
    }

    [Fact]
    public async Task An_overlay_still_indexing_is_waited_for_at_most_the_overlay_timeout_then_the_pr_merges()
    {
        // The overlay never becomes ready: the gate waits CodeGraph:OverlayTimeoutMinutes (3 min here), never Gate:CiTimeoutMinutes (an hour).
        var codeGraph = new FakeCodeGraphRisk
        {
            Overlay = sha => new OverlayAnswer(false, "queued", 7, "queued", sha, null, null),
            Status = id => new OverlayAnswer(false, "indexing", id, "indexing", Sha1, null, null),
        };
        var clock = new SteppingClock(Start, TimeSpan.FromMinutes(1));
        var h = new Harness
        {
            GateOnFakeClock = true, Time = clock, CiTimeout = TimeSpan.FromHours(1), CodeGraph = codeGraph,
            RiskReader = new CodeGraphRiskReader(codeGraph, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), FakeCodeGraphRisk.NoWait),
        };

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        var elapsed = clock.GetUtcNow() - Start;
        Assert.True(elapsed < TimeSpan.FromMinutes(30), $"the gate's wait ran {elapsed} of fake time");
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        // No overlay of the head: its blast radius is the default branch's, labelled so (sc-26254).
        Assert.DoesNotContain(codeGraph.Calls, c => c.Tool == "analyze_impact_file");
        Assert.Single(codeGraph.Calls, c => c.Tool == FakeCodeGraphRisk.DefaultBranchImpactCall);
        Assert.Contains($"codegraph blast radius on the default branch as of {FakeCodeGraphRisk.IndexCommit}, not the PR head: src/x.cs none (no data)", gate.Detail);
    }

    [Fact]
    public async Task The_ci_timeout_still_bounds_a_codegraph_that_hangs_and_the_pr_then_merges()
    {
        // A backstop: an overlay timeout longer than the CI timeout (an hour here, CI 5 min) — the read is cut off at the CI timeout.
        var codeGraph = new FakeCodeGraphRisk
        {
            Overlay = sha => new OverlayAnswer(false, "queued", 7, "queued", sha, null, null),
            Status = id => new OverlayAnswer(false, "indexing", id, "indexing", Sha1, null, null),
        };
        var clock = new SteppingClock(Start, TimeSpan.FromMinutes(1));
        var h = new Harness
        {
            GateOnFakeClock = true, Time = clock, CiTimeout = TimeSpan.FromMinutes(5), CodeGraph = codeGraph,
            RiskReader = new CodeGraphRiskReader(codeGraph, FakeCodeGraphRisk.FakeClockOverlayTimeout, TimeSpan.FromMinutes(1), FakeCodeGraphRisk.NoWait),
        };

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(outcome.Succeeded, outcome.Error);
        var elapsed = clock.GetUtcNow() - Start;
        Assert.True(elapsed < TimeSpan.FromMinutes(20), $"the gate's wait ran {elapsed} of fake time");
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains("no CodeGraph data: CodeGraph did not answer within 00:05:00 (Gate:CiTimeoutMinutes): src/x.cs", gate.Detail);
    }

    [Fact]
    public async Task An_overlay_codegraph_refuses_leaves_the_blast_radius_unread_and_the_pr_merges_at_once()
    {
        var h = new Harness();
        h.CodeGraph.Overlay = _ => new OverlayAnswer(true, "Error (overlays_unavailable): overlays are off", null, null, null, null, null,
            ErrorCode: "overlays_unavailable");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.DoesNotContain(h.CodeGraph.Calls, c => c.Tool == "analyze_impact_file");
        Assert.Single(h.CodeGraph.Calls, c => c.Tool == "request_overlay");
        var overlay = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.CodeGraphOverlay);
        Assert.Equal(CodeGraphOverlay.Refused, CodeGraphOverlay.FromDetail(overlay.Detail)!.Outcome);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains($"codegraph blast radius on the default branch as of {FakeCodeGraphRisk.IndexCommit}, not the PR head: src/x.cs none (no data)", gate.Detail);
    }

    [Fact]
    public async Task With_no_overlay_of_the_head_a_critical_file_on_the_default_branch_escalates()
    {
        // sc-26254: CodeGraph's cross-repo protection still applies where it has data, read from the default branch without the overlay.
        var h = new Harness();
        h.CodeGraph.Overlay = _ => new OverlayAnswer(true, "Error (overlays_unavailable): overlays are off", null, null, null, null, null,
            ErrorCode: "overlays_unavailable");
        h.CodeGraph.DefaultImpact = path => FakeCodeGraphRisk.FoundOnDefault(path, BlastRadius.Critical);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains($"blast radius on the default branch as of {FakeCodeGraphRisk.IndexCommit[..12]}, not the PR head, over the code floor (critical always fails): src/x.cs critical",
            outcome.Error);
    }

    [Fact]
    public async Task A_codegraph_blip_is_retried_and_the_data_it_then_answers_is_used()
    {
        // sc-26254: a transient failure is not missing data — tried again, and the critical file CodeGraph then answers blocks.
        var h = new Harness();
        var calls = 0;
        h.CodeGraph.Impact = (path, sha) => ++calls == 1 ? throw new HttpRequestException("connection reset") : FakeCodeGraphRisk.Found(path, sha, BlastRadius.Critical);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Equal(2, h.CodeGraph.Calls.Count(c => c.Tool == "analyze_impact_file"));
        Assert.Contains("over the code floor (critical always fails): src/x.cs critical", outcome.Error);
    }

    [Fact]
    public async Task A_hotspot_over_an_opted_in_limit_still_escalates_when_the_blast_radius_could_not_be_read()
    {
        var h = new Harness();
        h.GitHub.PolicyText = TestPolicies.V3("high", "0.5");
        h.CodeGraph.Overlay = _ => new OverlayAnswer(true, "Error (overlays_unavailable): overlays are off", null, null, null, null, null,
            ErrorCode: "overlays_unavailable");
        h.CodeGraph.Hotspot = path => FakeCodeGraphRisk.Hot(path, 0.9);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("hotspot score (riskScore) over max_hotspot 0.5: src/x.cs 0.9", outcome.Error);
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
        // The review's overlay, then the gate's fresh one.
        Assert.Equal([7L, 8L], (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.CodeGraphOverlay)
            .Select(r => CodeGraphOverlay.FromDetail(r.Detail)!.OverlayId));
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.DoesNotContain("no CodeGraph data", gate.Detail);
        Assert.Contains("worst blast radius low (src/x.cs)", gate.Detail);
    }

    [Theory]
    [InlineData("unavailable-refresh")]
    [InlineData("timeout-refresh")]
    [InlineData("timeout-first-read")]
    public async Task A_critical_observation_survives_a_stale_other_file_and_an_unavailable_or_timed_out_refresh(string failure)
    {
        // The review already recorded a ready overlay. The gate reads critical A, then B says it has gone stale. Neither a
        // replacement read that loses CodeGraph nor the CI backstop may turn A into missing data and allow a merge.
        var h = new Harness
        {
            GateOnFakeClock = true, CiTimeout = TimeSpan.FromMinutes(5),
            Overlays = new ReviewOverlays(new FakeCodeGraphRisk(), FakeCodeGraphRisk.FakeClockOverlayTimeout),
        };
        h.GitHub.Diff = head => TestPolicies.Diff("src/a.cs", marker: head) + TestPolicies.Diff("src/b.cs", marker: head);
        var refreshing = false;
        h.CodeGraph.Impact = (path, sha) =>
        {
            if (refreshing)
            {
                throw new HttpRequestException("refresh disconnected");
            }
            if (path == "src/a.cs")
            {
                return FakeCodeGraphRisk.Found(path, sha, BlastRadius.Critical);
            }
            if (failure == "timeout-first-read")
            {
                h.Time.Advance(h.CiTimeout);
            }
            return FakeCodeGraphRisk.NotIndexed(sha, "stale");
        };
        h.CodeGraph.DefaultImpact = _ => throw new HttpRequestException("default index disconnected");
        h.CodeGraph.Hotspot = path => path == "src/a.cs" && !refreshing
            ? FakeCodeGraphRisk.Hot(path, 0.1) : throw new HttpRequestException("hotspots disconnected");
        h.CodeGraph.Overlay = sha =>
        {
            refreshing = true;
            if (failure == "timeout-refresh")
            {
                h.Time.Advance(h.CiTimeout);
                return new OverlayAnswer(false, "indexing", 8, "indexing", sha, null, null);
            }
            throw new HttpRequestException("overlay refresh disconnected");
        };

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(60));
        var gate = (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.GateDecision);

        Assert.True(outcome.State == WorkState.Escalated && h.Merges.Count == 0
            && gate.Detail!.Contains("src/a.cs critical", StringComparison.Ordinal),
            $"{failure} must preserve critical A and reject the merge: {outcome.State}; {gate.Detail}");
        var missing = gate.Detail!.Split(CodeGraphRisk.NoDataMark, StringSplitOptions.None);
        Assert.True(missing.Length == 2 && missing[1].Contains("src/b.cs", StringComparison.Ordinal),
            $"The unanswered B must be recorded as no data: {gate.Detail}");
        if (failure != "timeout-first-read")
        {
            // Both inputs for A arrived before the refresh; only B is unknown. A first-read timeout leaves A's hotspot unanswered.
            Assert.DoesNotContain("src/a.cs", missing[1]);
        }
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("hotspot")]
    [InlineData("unrecognised")]
    [InlineData("misread")]
    public void An_unavailable_refresh_preserves_each_kind_of_blocking_observation(string observation)
    {
        const string a = "src/a.cs";
        const string b = "src/b.cs";
        var previous = new CodeGraphRisk(Head,
            [new FileBlastRadius(a, observation == "critical" ? BlastRadius.Critical : BlastRadius.Low, true)],
            [new FileHotspot(a, observation == "hotspot" ? 0.9 : 0.1, true, IndexCommit)], [],
            Unrecognised: observation == "unrecognised" ? [new UnrecognisedBlastRadius(a, "future")] : [],
            Misread: observation == "misread" ? [new CodeGraphMisread(a, "wrong commit")] : []);
        var refreshed = CodeGraphRisk.None(Head, [a, b], "refresh disconnected").PreserveObservations(previous);
        var decision = Evaluate(refreshed, TestPolicies.V3("high", "0.5"));

        Assert.True(decision.Outcome == GateOutcome.Blocked && decision.Detail.Contains(a, StringComparison.Ordinal), decision.Detail);
        Assert.Equal([new NoCodeGraphData(b, "refresh disconnected")], refreshed.NoData);
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
    public async Task Without_codegraph_the_pr_merges_recording_no_codegraph_data()
    {
        var h = new Harness { NoCodeGraph = true };

        var outcome = await h.Run();

        // sc-26254 (owner decision 2026-10-15): the factory works with or without CodeGraph.
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Single(h.Merges);
        var gate = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith("Merge ", gate.Detail);
        Assert.Contains("no CodeGraph data: CodeGraph is not configured (no CodeGraph token: ", gate.Detail);
        Assert.Contains("no CodeGraph data for 1 of 1 file(s), judged on size and fix rounds", gate.Detail);
        Assert.Contains("no CodeGraph data: CodeGraph is not configured", LedgerReport.Facts(await h.Rows(), []));
    }

    [Fact]
    public async Task Without_codegraph_a_change_over_the_size_limit_still_escalates()
    {
        var h = new Harness { NoCodeGraph = true };
        h.GitHub.PolicyText = TestPolicies.Standard(maxLines: 1);
        h.GitHub.Diff = head => TestPolicies.Diff("src/x.cs", lines: 5, marker: head);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("risk threshold: 5 changed lines exceed max_changed_lines 1", outcome.Error);
    }
}
