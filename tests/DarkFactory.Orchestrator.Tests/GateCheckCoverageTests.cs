using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25390 (E6): every gate check has a running test that makes it fail, checked in CI's <c>build-test</c>.</summary>
public sealed class GateCheckCoverageTests
{
    [Fact]
    public void Every_gate_check_has_a_running_test_that_makes_it_fail()
    {
        // Every test class but the seeded tags below, which exist to prove Find reads what does not run.
        var tests = GateCheckCoverage.Find(typeof(GateCheckCoverageTests).Assembly.GetTypes().Where(t => t != typeof(Seeded)));
        Assert.Empty(GateCheckCoverage.Problems(GateCheckCoverage.Registry, tests));
        // The registry follows the gate: a check added to GateChecks.All needs its failing-case test too.
        Assert.All(GateChecks.All, c => Assert.Contains(c, GateCheckCoverage.Registry));
    }

    private static readonly GateCheckTest CiRed = new("T.Red_ci_blocks", [GateChecks.CiGreen], null);
    private static readonly GateCheckTest ReviewFail = new("T.Failed_review_blocks", [GateChecks.ReviewPass], null);

    [Fact]
    public void Removing_the_failing_case_test_of_a_check_is_reported()
    {
        string[] registry = [GateChecks.CiGreen, GateChecks.ReviewPass];
        Assert.Empty(GateCheckCoverage.Problems(registry, [CiRed, ReviewFail]));

        Assert.Equal(["gate check 'review-pass' has no running test that makes it fail ([FailsGateCheck(\"review-pass\")])"],
            GateCheckCoverage.Problems(registry, [CiRed]));
    }

    [Fact]
    public void A_failing_case_test_that_would_not_run_or_names_an_unknown_check_is_reported()
    {
        string[] registry = [GateChecks.CiGreen, GateChecks.ReviewPass];
        Assert.Equal(["T.Failed_review_blocks would not run in CI: skipped", "gate check 'review-pass' has no running test that makes it fail ([FailsGateCheck(\"review-pass\")])"],
            GateCheckCoverage.Problems(registry, [CiRed, ReviewFail with { Runs = "skipped" }]));
        Assert.Equal(["T.Typo names 'ci-gren', which is not a registered gate check"],
            GateCheckCoverage.Problems(registry, [CiRed, ReviewFail, new("T.Typo", ["ci-gren"], null)]));
    }

    [Fact]
    public void Finding_reads_the_attribute_and_whether_xunit_runs_the_test()
    {
        var found = GateCheckCoverage.Find([typeof(Seeded)]).ToDictionary(t => t.Name);
        var fact = found[$"{typeof(Seeded).FullName}.{nameof(Seeded.Fact)}"];
        Assert.Equal([GateChecks.CiGreen, GateChecks.ReviewPass], fact.Checks.Order());
        Assert.Null(fact.Runs);
        Assert.Equal("not an xUnit [Fact] or [Theory]", found[$"{typeof(Seeded).FullName}.{nameof(Seeded.NotATest)}"].Runs);
        Assert.Equal("skipped", found[$"{typeof(Seeded).FullName}.{nameof(Seeded.Skipped)}"].Runs);
        Assert.Equal("explicit (not run by default)", found[$"{typeof(Seeded).FullName}.{nameof(Seeded.Explicit)}"].Runs);
    }

    /// <summary>Seeded tags for <see cref="Finding_reads_the_attribute_and_whether_xunit_runs_the_test"/>, left out of the coverage check (they test nothing).</summary>
    public sealed class Seeded
    {
        [Fact]
        [FailsGateCheck(GateChecks.CiGreen)]
        [FailsGateCheck(GateChecks.ReviewPass)]
        public void Fact()
        {
        }

        [FailsGateCheck(GateChecks.CiGreen)]
        internal void NotATest()
        {
        }

        [Fact(Skip = "seeded: a skipped failing-case test does not count")]
        [FailsGateCheck(GateChecks.CiGreen)]
        public void Skipped()
        {
        }

        [Fact(Explicit = true)]
        [FailsGateCheck(GateChecks.CiGreen)]
        public void Explicit()
        {
        }
    }
}
