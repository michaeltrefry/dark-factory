using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25381: <c>factory/gate.yaml</c> version 2 — path tiers derived from the diff, and the risk threshold.</summary>
public class GatePolicyTests
{
    private static readonly GatePolicy Policy = GatePolicy.Parse(TestPolicies.Standard());

    [Fact]
    public void The_standard_policy_parses_into_its_tiers_and_risk_threshold()
    {
        Assert.Equal(["factory/gate.yaml", ".github/workflows/", "CODEOWNERS", ".github/CODEOWNERS", "docs/CODEOWNERS", "factory/prompts/"],
            Policy.TierRules[Tier.Sealed].Paths.Select(p => p.Text));
        Assert.Empty(Policy.TierRules[Tier.Normal].Paths);
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.SecurityReview, GateChecks.RiskThreshold, GateChecks.NewTestsFailOnBase],
            GateChecks.All.Where(Policy.TierRules[Tier.Protected].Checks.Contains));
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.RiskThreshold, GateChecks.NewTestsFailOnBase],
            GateChecks.All.Where(Policy.TierRules[Tier.Normal].Checks.Contains));
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass], GateChecks.All.Where(Policy.TierRules[Tier.Free].Checks.Contains));
        Assert.Equal(new RiskThreshold(400, 20, 3), Policy.Risk);
    }

    /// <summary>Every way the policy can be corrupt or miss a field: each is invalid (the gate blocks and escalates on it).</summary>
    public static TheoryData<string, string> Invalid()
    {
        var ok = TestPolicies.Standard();
        string Replace(string from, string to) =>
            ok.Contains(from, StringComparison.Ordinal) ? ok.Replace(from, to, StringComparison.Ordinal) : throw new InvalidOperationException(from);
        string Without(string line) => Replace(line + "\n", "");
        return new()
        {
            { "", "one YAML mapping" },
            { "- a\n- b\n", "one YAML mapping" },
            { "version: [1\n", "not valid YAML" },
            { "version: 1\nrequire:\n  ci: green\n  review: pass\n", "version 1, which this gate no longer accepts" },
            { Replace("version: 2", "version: 3"), "version must be 2" },
            { Without("version: 2"), "the top level is missing 'version'" },
            { ok + "\nbypass: true\n", "unknown key 'bypass' in the top level" },
            { ok[..ok.IndexOf("risk:", StringComparison.Ordinal)], "the top level is missing 'risk'" },
            { Replace("version: 2\n", "version: 2\nversion: 2\n"), "not valid YAML" },
            { Replace("  sealed:", "  sealed2:"), "unknown key 'sealed2' in tiers" },
            { Replace("  free:\n    paths: [docs/, tests/]\n    checks: [ci-green, review-pass]\n", ""), "tiers is missing 'free'" },
            { Replace("  normal:\n    checks:", "  normal:\n    paths: [src/]\n    checks:"), "unknown key 'paths' in tiers.normal" },
            { Replace("    paths: [docs/, tests/]\n", ""), "tiers.free is missing 'paths'" },
            { Replace("    paths: [docs/, tests/]", "    paths: []"), "tiers.free.paths must be a non-empty list" },
            { Replace("    paths: [docs/, tests/]", "    paths: docs/"), "tiers.free.paths must be a non-empty list" },
            { Replace("[ci-green, review-pass]\nrisk", "[ci-green]\nrisk"), "tiers.free.checks must include review-pass" },
            { Replace("[ci-green, review-pass]\nrisk", "[review-pass]\nrisk"), "tiers.free.checks must include ci-green" },
            { Replace("[ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]", "[ci-green, review-pass, risk-threshold, new-tests-fail-on-base]"),
                "tiers.protected.checks must include security-review" },
            { Replace("[ci-green, review-pass]\nrisk", "[ci-green, review-pass, skip-ci]\nrisk"), "unknown check 'skip-ci'" },
            { Replace("paths: [factory/gate.yaml, ", "paths: ["), "the sealed tier must cover factory/gate.yaml" },
            { Replace(".github/workflows/, ", ""), "the sealed tier must cover .github/workflows/" },
            { Replace(" CODEOWNERS,", ""), "the sealed tier must cover CODEOWNERS" },
            { Replace("factory/prompts/]", "factory/prompt/]"), "the sealed tier must cover factory/prompts/" },
            // Naming files in a sealed directory does not seal the directory: every other file in it would be normal.
            { Replace(".github/workflows/, ", ".github/workflows/ci.yml, "), "the sealed tier must cover .github/workflows/" },
            { Replace(".github/workflows/, ", ".github/workflows/*.yml, .github/workflows/*.yaml, "), "the sealed tier must cover .github/workflows/" },
            { Replace("factory/prompts/]", "factory/prompts/review.md]"), "the sealed tier must cover factory/prompts/" },
            { Replace("factory/prompts/]", "factory/prompts/*]"), "the sealed tier must cover factory/prompts/" },
            { Replace("[ci-green, review-pass, risk-threshold, new-tests-fail-on-base]", "[ci-green, review-pass, new-tests-fail-on-base]"),
                "tiers.normal.checks must include risk-threshold" },
            { Replace("[ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]", "[ci-green, review-pass, security-review, new-tests-fail-on-base]"),
                "tiers.protected.checks must include risk-threshold" },
            // sc-25382: a code change must add a test that fails on the base; the policy cannot drop that floor.
            { Replace("[ci-green, review-pass, risk-threshold, new-tests-fail-on-base]", "[ci-green, review-pass, risk-threshold]"),
                "tiers.normal.checks must include new-tests-fail-on-base" },
            { Replace("[ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]", "[ci-green, review-pass, security-review, risk-threshold]"),
                "tiers.protected.checks must include new-tests-fail-on-base" },
            { Replace("infra/", "infra/../src/"), "invalid segment '..'" },
            { Replace("infra/", "/infra/"), "'/infra/' is not a path pattern" },
            { Replace("infra/", "infra**/"), "invalid segment 'infra**'" },
            { Replace("max_changed_lines: 400", "max_changed_lines: 0"), "risk.max_changed_lines must be a whole number of at least 1" },
            { Replace("max_changed_files: 20", "max_changed_files: lots"), "risk.max_changed_files must be a whole number" },
            { Replace("max_fix_rounds: 3", "max_fix_rounds: -1"), "risk.max_fix_rounds must be a whole number of at least 0" },
            { Replace("\n  max_fix_rounds: 3", ""), "risk is missing 'max_fix_rounds'" },
            { Replace("risk:\n", "risk: none\n").Split("  max_changed_lines")[0], "risk in the top level must be a mapping" },
        };
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_corrupt_or_incomplete_policy_is_invalid(string yaml, string reason)
    {
        var ex = Assert.Throws<GatePolicyException>(() => GatePolicy.Parse(yaml));
        Assert.Contains(reason, ex.Message);
    }

    [Theory]
    [InlineData("factory/gate.yaml", Tier.Sealed)]
    [InlineData(".github/workflows/ci.yml", Tier.Sealed)]
    [InlineData("CODEOWNERS", Tier.Sealed)]
    [InlineData(".github/CODEOWNERS", Tier.Sealed)]
    [InlineData("docs/CODEOWNERS", Tier.Sealed)] // sealed wins over free's docs/
    [InlineData("factory/prompts/security.md", Tier.Sealed)]
    [InlineData("./factory/gate.yaml", Tier.Sealed)] // a leading ./ is not a different file
    [InlineData("docs/../factory/gate.yaml", Tier.Sealed)] // nor is a detour through a free directory
    [InlineData("tests//../.github/workflows/x.yml", Tier.Sealed)]
    [InlineData("../factory/gate.yaml", Tier.Sealed)] // above the root: not a repository path, so sealed
    [InlineData("docs/../../x.md", Tier.Sealed)]
    [InlineData("/etc/passwd", Tier.Sealed)]
    [InlineData("src/auth/Login.cs", Tier.Protected)]
    [InlineData("db/migrations/001_init.sql", Tier.Protected)]
    [InlineData("infra/main.tf", Tier.Protected)]
    [InlineData("src/billing/payments/Charge.cs", Tier.Protected)]
    [InlineData("tests/auth/LoginTests.cs", Tier.Protected)] // protected wins over free's tests/
    [InlineData("docs/../src/auth/Login.cs", Tier.Protected)]
    [InlineData("docs/guide.md", Tier.Free)]
    [InlineData("tests/WordCountTests.cs", Tier.Free)]
    [InlineData("./docs/guide.md", Tier.Free)]
    [InlineData("src/WordCount.cs", Tier.Normal)]
    [InlineData("README.md", Tier.Normal)]
    [InlineData(".github/dependabot.yml", Tier.Normal)]
    [InlineData("factory/notes.txt", Tier.Normal)]
    // Sealed and protected patterns ignore case, free ones do not: case can only move a path to a stricter tier.
    [InlineData("Factory/gate.yaml", Tier.Sealed)]
    [InlineData("codeowners", Tier.Sealed)]
    [InlineData(".GitHub/Workflows/deploy.yml", Tier.Sealed)]
    [InlineData("src/Auth/Login.cs", Tier.Protected)]
    [InlineData("Infra/main.tf", Tier.Protected)]
    [InlineData("Docs/guide.md", Tier.Normal)]
    [InlineData("Tests/WordCountTests.cs", Tier.Normal)]
    [InlineData("docs", Tier.Normal)] // the directory pattern docs/ matches what is under it, not a file named docs
    [InlineData("infrastructure/main.tf", Tier.Normal)]
    public void Each_path_is_in_the_first_tier_that_matches_it(string path, Tier tier) => Assert.Equal(tier, Policy.TierOf(path).Tier);

    [Fact]
    public void Patterns_match_within_a_segment_and_across_segments()
    {
        static bool Match(string pattern, string path) => PathPattern.TryParse(pattern, out _)!.Matches(path);
        Assert.True(Match("**/*.sql", "a/b/c.sql"));
        Assert.True(Match("**/*.sql", "c.sql"));
        Assert.False(Match("*.sql", "a/c.sql"));
        Assert.True(Match("src/*/Auth.cs", "src/x/Auth.cs"));
        Assert.False(Match("src/*/Auth.cs", "src/x/y/Auth.cs"));
        Assert.True(Match("src/**", "src/a/b"));
        Assert.True(Match("./src/a?.cs", "src/ab.cs"));
        Assert.False(Match("src/a?.cs", "src/a/.cs"));
        Assert.False(Match("src/a.cs", "src/aXcs")); // '.' is literal
        Assert.False(Match("src/a.cs", "SRC/a.cs"));
        Assert.True(PathPattern.TryParse("src/a.cs", out _, ignoreCase: true)!.Matches("SRC/A.cs"));
    }

    [Theory]
    [InlineData(".github/workflows/", true)]
    [InlineData(".github/", true)] // an ancestor directory covers it too
    [InlineData(".github/**", true)]
    [InlineData(".github/workflows/**", true)]
    [InlineData("**/workflows/", true)]
    [InlineData(".github/*/", true)]
    [InlineData("**", true)]
    [InlineData(".github/workflows/ci.yml", false)] // files in it, not everything under it
    [InlineData(".github/workflows/*", false)] // not nested directories
    [InlineData(".github/workflows/*.yml", false)]
    [InlineData(".github/workflows/nested/", false)]
    [InlineData(".github/workflow/", false)]
    [InlineData("src/", false)]
    public void A_pattern_covers_a_directory_only_when_it_matches_everything_under_it(string pattern, bool covers)
    {
        Assert.Equal(covers, PathPattern.TryParse(pattern, out _)!.CoversEverythingUnder(".github/workflows/"));
        // The check agrees with matching: a covering pattern matches paths no file-naming pattern could anticipate.
        if (covers)
        {
            Assert.True(PathPattern.TryParse(pattern, out _)!.Matches($".github/workflows/{Guid.NewGuid():N}/{Guid.NewGuid():N}.yaml"));
        }
    }

    [Theory]
    [InlineData(".github/workflows/", ".github/")]
    [InlineData(".github/workflows/", ".github/**")]
    [InlineData("factory/prompts/", "factory/")]
    public void A_sealed_tier_may_cover_its_directories_with_a_broader_pattern(string from, string to)
    {
        var policy = GatePolicy.Parse(TestPolicies.Standard().Replace(from + ",", to + ",", StringComparison.Ordinal).Replace(from + "]", to + "]", StringComparison.Ordinal));
        Assert.Equal(Tier.Sealed, policy.TierOf(from + "nested/zz-probe.md").Tier);
    }

    [Fact]
    public void A_rename_counts_as_touching_both_paths_so_moving_a_sealed_file_out_is_sealed()
    {
        const string diff = """
            diff --git a/factory/prompts/security.md b/docs/security.md
            similarity index 100%
            rename from factory/prompts/security.md
            rename to docs/security.md
            """;
        var change = Policy.Classify(diff);

        Assert.Equal([("docs/security.md", Tier.Free), ("factory/prompts/security.md", Tier.Sealed)], change.Paths.Select(p => (p.Path, p.Tier)));
        Assert.Equal(2, change.ChangedFiles);
        Assert.Contains(GateChecks.SecurityReview, change.RequiredChecks);
    }

    [Fact]
    public void A_deleted_file_counts_as_touched()
    {
        const string diff = """
            diff --git a/.github/workflows/ci.yml b/.github/workflows/ci.yml
            deleted file mode 100644
            index 1..0
            --- a/.github/workflows/ci.yml
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -on: push
            -jobs: {}
            """;
        var change = Policy.Classify(diff);

        Assert.Equal((".github/workflows/ci.yml", Tier.Sealed), (change.Paths.Single().Path, change.Paths.Single().Tier));
        Assert.Equal(2, change.ChangedLines);
    }

    [Fact]
    public void Quoted_paths_are_unquoted_before_they_are_classified()
    {
        // git C-quotes a path with special characters; the octal escapes are UTF-8 bytes (here "é").
        const string diff = """
            diff --git "a/docs/caf\303\251.md" "b/factory/prompts/caf\303\251.md"
            similarity index 100%
            rename from "docs/caf\303\251.md"
            rename to "factory/prompts/caf\303\251.md"
            diff --git a/src/with space.cs b/src/with space.cs
            --- a/src/with space.cs
            +++ b/src/with space.cs
            @@ -1 +1 @@
            -a
            +b
            """;
        var change = Policy.Classify(diff);

        Assert.Equal([("docs/café.md", Tier.Free), ("factory/prompts/café.md", Tier.Sealed), ("src/with space.cs", Tier.Normal)],
            change.Paths.Select(p => (p.Path, p.Tier)));
    }

    [Fact]
    public void A_line_added_inside_a_hunk_that_looks_like_a_header_is_not_a_touched_path()
    {
        var diff = TestPolicies.Diff("docs/guide.md") + "+++ b/factory/gate.yaml\n+rename to factory/gate.yaml\n";
        var change = Policy.Classify(diff);

        Assert.Equal(["docs/guide.md"], change.Paths.Select(p => p.Path));
        Assert.Equal(3, change.ChangedLines);
    }

    [Theory]
    [InlineData("@@ -1 +1 @@\n-a\n+b\n", Tier.Sealed)]
    [InlineData("Binary files differ\n", Tier.Sealed)]
    [InlineData("", Tier.Normal)]
    [InlineData(" \n", Tier.Normal)]
    public void A_diff_with_no_readable_path_is_sealed_and_an_empty_one_normal(string diff, Tier tier) =>
        Assert.Equal(tier, Policy.Classify(diff).Paths.Single().Tier);

    [Fact]
    public void The_required_checks_are_the_union_of_the_touched_tiers()
    {
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass], GateChecks.All.Where(Policy.Classify(TestPolicies.Diff("docs/a.md")).Requires));
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.RiskThreshold, GateChecks.NewTestsFailOnBase],
            GateChecks.All.Where(Policy.Classify(TestPolicies.Diff("docs/a.md") + TestPolicies.Diff("src/a.cs")).Requires));
        Assert.Equal(GateChecks.All, GateChecks.All.Where(Policy.Classify(TestPolicies.Diff("src/a.cs") + TestPolicies.Diff("src/auth/a.cs")).Requires));
        Assert.Equal(["src/auth/a.cs (protected)"],
            Policy.SecurityReviewPaths(Policy.Classify(TestPolicies.Diff("src/a.cs") + TestPolicies.Diff("src/auth/a.cs"))).Select(p => p.ToString()));
    }
}

/// <summary>sc-25381: the merge gate enforces each tier's checks and the risk threshold, from the diff at the head.</summary>
public class MergeGateTierTests
{
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Policy = TestPolicies.Standard();
    private static readonly PullFacts Pull = new(1, "https://github.com/o/r/pull/1", true, false, false, Head, "main", "base", null);
    private static readonly CiFacts Green = new(Head, [new CheckFact("build", true, "success")]);
    private static readonly CiFacts Red = new(Head, [new CheckFact("build", true, "failure")]);
    private static readonly string[] Implementer = ["claude-sonnet-4-5"];
    private static RoleReview Review(string role) => new(role, "gpt-5.5", "gpt-5.5", "openai", "s", "p", [], "ok");

    /// <summary>A passing verdict on the head with every role (security included); no risky paths claimed.</summary>
    private static readonly ReviewVerdict Full = ReviewPanel.Decide(Head, [], ReviewRoles.All.Select(Review).ToList());

    /// <summary>The new-tests check passed for exactly this base and head (sc-25382).</summary>
    private static readonly NewTestsResult PassingTests = new("base", Head, NewTestsOutcome.Pass,
        "1 new test(s) fail on the base and pass on the head: W.Tests.New", "dotnet-xunit", "ran", "ran", []);

    private static GateDecision Evaluate(string diff, int fixRounds = 0, CiFacts? ci = null, ReviewVerdict? verdict = null, string? policy = null,
        NewTestsResult? tests = null, bool testsRun = true) =>
        MergeGate.Evaluate(policy ?? Policy, null, TestPolicies.Counting(Pull, new ChangeFacts(diff, null, 0)), new ChangeFacts(diff, null, fixRounds), ci ?? Green, verdict is null ? [Full] : [verdict], Implementer,
            testsRun ? tests ?? PassingTests : null);

    /// <summary>
    /// One row per tier of the standard policy: a path in it, whether each check is enforced for it, and whether a change
    /// passing every check merges (sealed and protected escalate instead).
    /// </summary>
    [Theory]
    [InlineData(".github/workflows/ci.yml", Tier.Sealed, true, true, true, false, false, false, "touches sealed path(s), which always escalate")]
    [InlineData("src/auth/Login.cs", Tier.Protected, true, true, true, true, true, false, "touches protected path(s), merged only after escalation")]
    [InlineData("src/WordCount.cs", Tier.Normal, true, true, false, true, true, true, null)]
    [InlineData("tests/WordCountTests.cs", Tier.Free, true, true, false, false, false, true, null)]
    public void Each_tier_enforces_its_required_checks(string path, Tier tier, bool ci, bool review, bool security, bool risk, bool newTests, bool merges,
        string? escalation)
    {
        Assert.Equal(tier, GatePolicy.Parse(Policy).TierOf(path).Tier);
        var diff = TestPolicies.Diff(path);

        // Every check passes: merge, or (sealed, protected) escalate with the tier as the only reason.
        var passing = Evaluate(diff);
        Assert.Equal(merges ? GateOutcome.Merge : GateOutcome.Blocked, passing.Outcome);
        if (escalation is not null)
        {
            Assert.Contains(escalation, Assert.Single(passing.Reasons));
        }

        // Each check failing on its own blocks exactly when the tier requires it.
        AssertEnforced(ci, Evaluate(diff, ci: Red), "CI failed");
        AssertEnforced(review, Evaluate(diff, verdict: Full with { Verdict = ReviewVerdict.Fail }), "is 'fail'");
        AssertEnforced(security, Evaluate(diff, verdict: Full with { Reviews = Full.Reviews.Where(r => r.Role != ReviewRoles.Security).ToList() }),
            "has no security review");
        AssertEnforced(risk, Evaluate(diff, fixRounds: 4), "risk threshold: 4 fix rounds exceed max_fix_rounds 3");
        AssertEnforced(risk, Evaluate(TestPolicies.Diff(path, lines: 401)), "risk threshold: 401 changed lines exceed max_changed_lines 400");
        AssertEnforced(risk, Evaluate(string.Concat(Enumerable.Range(0, 21).Select(i => TestPolicies.Diff(path.Insert(path.LastIndexOf('.'), $"{i}"))))),
            "risk threshold: 21 changed files exceed max_changed_files 20");
        // sc-25382: the new tests must have run for this base and head, and failed on the base.
        AssertEnforced(newTests, Evaluate(diff, testsRun: false), "new-tests-fail-on-base: the tests the PR adds were not run");
        AssertEnforced(newTests, Evaluate(diff, tests: PassingTests with { Outcome = NewTestsOutcome.Rejected, Reason = "W.Tests.Old already passes on the base" }),
            "new-tests-fail-on-base (rejected): W.Tests.Old already passes on the base");
        AssertEnforced(newTests, Evaluate(diff, tests: PassingTests with { HeadSha = "0123456789abcdef" }), "the tests were run for base...0123456789ab");
    }

    private static void AssertEnforced(bool enforced, GateDecision decision, string reason)
    {
        if (enforced)
        {
            Assert.Equal(GateOutcome.Blocked, decision.Outcome);
            Assert.Contains(decision.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(decision.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_risk_threshold_holds_at_its_limits()
    {
        Assert.Equal(GateOutcome.Merge, Evaluate(TestPolicies.Diff("src/a.cs", lines: 400), fixRounds: 3).Outcome);
        Assert.Equal(GateOutcome.Merge, Evaluate(string.Concat(Enumerable.Range(0, 20).Select(i => TestPolicies.Diff($"src/a{i}.cs")))).Outcome);
        Assert.Equal(GateOutcome.Blocked, Evaluate(TestPolicies.Diff("src/a.cs"), fixRounds: 1, policy: TestPolicies.Standard(maxFixRounds: 0)).Outcome);
        // A free path in the same change does not lift the threshold off the normal one.
        Assert.Contains("changed lines exceed", Evaluate(TestPolicies.Diff("docs/a.md", lines: 300) + TestPolicies.Diff("src/a.cs", lines: 200)).Detail);
    }

    [Fact]
    public void A_sealed_path_escalates_even_when_the_review_and_the_pr_claim_otherwise()
    {
        // The verdict lists no risky path (the panel, like a plan or the PR, says nothing sensitive changed), every check
        // passes, and the sealed file is reached through a free directory and a leading ./: the gate's own classification
        // of the head's diff escalates it anyway.
        var claimed = Full with { RiskyPaths = [] };
        foreach (var path in new[] { "factory/gate.yaml", "./factory/gate.yaml", "docs/../factory/gate.yaml", "CODEOWNERS", ".github/workflows/release.yml" })
        {
            var decision = Evaluate(TestPolicies.Diff("docs/notes.md") + TestPolicies.Diff(path), verdict: claimed);
            Assert.Equal(GateOutcome.Blocked, decision.Outcome);
            Assert.Contains("touches sealed path(s), which always escalate", decision.Detail);
        }
        // With no verdict at all it escalates rather than asking for another review.
        var unreviewed = MergeGate.Evaluate(Policy, null, Pull with { ChangedFiles = 1 }, new ChangeFacts(TestPolicies.Diff("factory/gate.yaml"), null, 0), Green, [], Implementer);
        Assert.Equal(GateOutcome.Blocked, unreviewed.Outcome);
    }

    [Fact]
    public void A_sealed_tier_change_still_needs_its_checks_and_says_so()
    {
        var decision = Evaluate(TestPolicies.Diff(".github/workflows/ci.yml"), ci: Red,
            verdict: Full with { Reviews = Full.Reviews.Where(r => r.Role != ReviewRoles.Security).ToList() });
        Assert.Contains("sealed", decision.Detail);
        Assert.Contains("CI failed", decision.Detail);
        Assert.Contains("has no security review (security-review is required by .github/workflows/ci.yml (sealed))", decision.Detail);
    }

    [Fact]
    public void A_diff_that_cannot_be_read_blocks()
    {
        var decision = MergeGate.Evaluate(Policy, null, Pull, new ChangeFacts(null, "406 diff too large", 0), Green, [Full], Implementer);
        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains("could not be read (406 diff too large)", decision.Detail);
        Assert.Contains("touches sealed path(s)", Evaluate("@@ -1 +1 @@\n-a\n+b\n").Detail); // no readable path: sealed
    }

    [Theory]
    [InlineData(2, "it has 1 file(s), the PR 2 changed file(s)")] // GitHub left a file out of the diff
    [InlineData(0, "it has 1 file(s), the PR 0 changed file(s)")]
    [InlineData(null, "the PR an unread number of changed file(s)")]
    public void A_diff_whose_file_count_differs_from_the_prs_is_incomplete_and_blocks(int? prFiles, string reason)
    {
        var decision = MergeGate.Evaluate(Policy, null, Pull with { ChangedFiles = prFiles }, new ChangeFacts(TestPolicies.Diff("docs/a.md"), null, 0),
            Green, [Full], Implementer);
        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains($"the diff of {Head[..12]} is incomplete", decision.Detail);
        Assert.Contains(reason, decision.Detail);
        // The same diff with a matching count merges.
        Assert.Equal(GateOutcome.Merge, MergeGate.Evaluate(Policy, null, Pull with { ChangedFiles = 1 },
            new ChangeFacts(TestPolicies.Diff("docs/a.md"), null, 0), Green, [Full], Implementer).Outcome);
    }

    [Theory]
    [InlineData("scripts/deploy.sh", "scripts/deploy.sh (scripts)")]
    [InlineData("src/App/App.csproj", "src/App/App.csproj (dependencies)")]
    [InlineData("tests/TokenTests.cs", "tests/TokenTests.cs (security-sensitive code)")] // a free path the floor still matches
    public void A_path_on_the_code_floor_needs_the_security_review_whatever_its_tier(string path, string why)
    {
        var withoutSecurity = Full with { Reviews = Full.Reviews.Where(r => r.Role != ReviewRoles.Security).ToList() };
        var decision = Evaluate(TestPolicies.Diff(path), verdict: withoutSecurity);
        Assert.Equal(GateOutcome.Blocked, decision.Outcome);
        Assert.Contains($"has no security review (security-review is required by {why})", decision.Detail);
        Assert.Equal(GateOutcome.Merge, Evaluate(TestPolicies.Diff(path)).Outcome);
        Assert.Equal([why], GatePolicy.Parse(Policy).SecurityReviewReasons(GatePolicy.Parse(Policy).Classify(TestPolicies.Diff(path))));
    }

    [Fact]
    public void A_merge_names_the_tiers_and_checks_it_enforced()
    {
        var decision = Evaluate(TestPolicies.Diff("docs/a.md") + TestPolicies.Diff("src/a.cs", lines: 2), fixRounds: 1);
        Assert.Equal(GateOutcome.Merge, decision.Outcome);
        Assert.Contains("paths: free 1, normal 1; checks: ci-green, review-pass, risk-threshold, new-tests-fail-on-base", decision.Detail);
        Assert.Contains("1 new test(s) fail on the base and pass on the head: W.Tests.New", decision.Detail);
        Assert.Contains("risk within threshold (3 changed lines, 2 files, 1 fix rounds)", decision.Detail);
    }
}
