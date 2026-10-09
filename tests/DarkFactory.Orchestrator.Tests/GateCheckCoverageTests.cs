using System.Reflection;
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
    }

    /// <summary>
    /// sc-25391: a tag is not enough — every dedicated test, run with its check disabled through the test-only seam, fails an
    /// assertion (and passes with every check on). An empty tagged test, or one that asserts something else, is reported.
    /// </summary>
    [Fact]
    public async Task Every_tagged_test_fails_when_its_check_is_disabled()
    {
        var tests = GateCheckCoverage.Find(typeof(GateCheckCoverageTests).Assembly.GetTypes().Where(t => t != typeof(Seeded) && t != typeof(SeededBroken)));
        var problems = await GateCheckCoverage.BrokenCheckProblems(GateCheckCoverage.Registry, tests);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task A_tagged_test_that_passes_with_its_check_disabled_or_fails_anyway_is_reported()
    {
        GateCheckTest Seed(string method, string check) =>
            new($"SeededBroken.{method}", [check], null, typeof(SeededBroken).GetMethod(method));
        var tests = new[]
        {
            Seed(nameof(SeededBroken.Real), GateChecks.CiGreen),
            Seed(nameof(SeededBroken.Empty), GateChecks.CiGreen),
            Seed(nameof(SeededBroken.Unrelated), GateChecks.ReviewPass),
            Seed(nameof(SeededBroken.AlwaysFails), GateChecks.RiskThreshold),
            Seed(nameof(SeededBroken.Crashes), GateChecks.SecurityReview),
            Seed(nameof(SeededBroken.Rows), GateChecks.CiGreen),
        };

        Assert.Equal(
        [
            "SeededBroken.Empty still passes with 'ci-green' disabled: it does not make that check fail",
            "SeededBroken.Unrelated still passes with 'review-pass' disabled: it does not make that check fail",
            "SeededBroken.AlwaysFails fails with every check on, so its run here proves nothing: always",
            "SeededBroken.Crashes with 'security-review' disabled throws InvalidOperationException rather than failing an assertion: crash",
            "SeededBroken.Rows row 1 still passes with 'ci-green' disabled: it does not make that check fail",
        ], await GateCheckCoverage.BrokenCheckProblems(GateCheckCoverage.Registry, tests));
        // The seam is per async flow: disabled here, not in another flow running at the same time (another test, a host).
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var other = Task.Run(async () =>
        {
            await go.Task;
            return GateCheckSeam.Off(GateChecks.CiGreen);
        });
        using (GateCheckSeam.Disable(GateChecks.CiGreen))
        {
            Assert.True(GateCheckSeam.Off(GateChecks.CiGreen));
            Assert.False(GateCheckSeam.Off(GateChecks.ReviewPass));
            go.SetResult();
            Assert.False(await other);
        }
        Assert.False(GateCheckSeam.Off(GateChecks.CiGreen));
    }

    /// <summary>
    /// E2: the seam is no runtime bypass — internal, static, set only by <c>Disable</c>, which no production code calls, and reading no
    /// configuration, environment variable or command line.
    /// </summary>
    [Fact]
    public void The_seam_cannot_be_reached_from_configuration_the_environment_or_the_command_line()
    {
        var seam = typeof(GateCheckSeam);
        Assert.False(seam.IsPublic || seam.IsNestedPublic);
        Assert.True(seam.IsAbstract && seam.IsSealed); // static
        Assert.Empty(seam.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        var setters = seam.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.ReturnType == typeof(IDisposable));
        Assert.Equal([nameof(GateCheckSeam.Disable)], setters.Select(m => m.Name));

        var src = Path.Combine(RepoRoot(), "src");
        var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllText);
        var own = Path.Combine("DarkFactory.Orchestrator", "Gate", "GateCheckSeam.cs");
        Assert.Contains(own, sources.Keys);
        // No production code disables a check...
        Assert.Empty(sources.Where(s => s.Key != own && s.Value.Contains($"{nameof(GateCheckSeam)}.{nameof(GateCheckSeam.Disable)}", StringComparison.Ordinal)).Select(s => s.Key));
        // ...and the seam reads nothing a deployment could set.
        foreach (var input in new[] { "Environment", "Configuration", "GetEnvironmentVariable", "IOptions", "args", "Command", "File." })
        {
            Assert.DoesNotContain(input, sources[own], StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkFactory.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    [Fact]
    public void Every_check_the_gate_names_is_in_gate_checks_all_and_so_in_the_registry()
    {
        // A check added to GateChecks as a constant but left out of All (and so out of the registry and its coverage) fails here.
        var named = typeof(GateChecks).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
        Assert.Equal(named.Order(), GateChecks.All.Order());
        Assert.Equal(GateChecks.All, GateCheckCoverage.Registry.Take(GateChecks.All.Count));
    }

    private static readonly GateCheckTest CiRed = new("T.Red_ci_blocks", [GateChecks.CiGreen], null);
    private static readonly GateCheckTest ReviewFail = new("T.Failed_review_blocks", [GateChecks.ReviewPass], null);

    [Fact]
    public void Removing_the_failing_case_test_of_a_check_is_reported()
    {
        string[] registry = [GateChecks.CiGreen, GateChecks.ReviewPass];
        Assert.Empty(GateCheckCoverage.Problems(registry, [CiRed, ReviewFail]));

        Assert.Equal(["gate check 'review-pass' has no running test that makes it fail on its own ([FailsGateCheck(\"review-pass\")] as its only tag)"],
            GateCheckCoverage.Problems(registry, [CiRed]));
        // A test tagged with several checks does not stand in for a check's own test: it may fail on one of the others.
        Assert.Equal(["gate check 'review-pass' has no running test that makes it fail on its own ([FailsGateCheck(\"review-pass\")] as its only tag)"],
            GateCheckCoverage.Problems(registry, [CiRed, new("T.Every_check", [GateChecks.CiGreen, GateChecks.ReviewPass], null)]));
    }

    [Fact]
    public void A_failing_case_test_that_would_not_run_or_names_an_unknown_check_is_reported()
    {
        string[] registry = [GateChecks.CiGreen, GateChecks.ReviewPass];
        Assert.Equal(["T.Failed_review_blocks would not run in CI: skipped", "gate check 'review-pass' has no running test that makes it fail on its own ([FailsGateCheck(\"review-pass\")] as its only tag)"],
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

    /// <summary>
    /// Seeded tests for <see cref="A_tagged_test_that_passes_with_its_check_disabled_or_fails_anyway_is_reported"/>, run only by it
    /// (explicit: <c>dotnet test</c> leaves them out, and so does the coverage).
    /// </summary>
    public sealed class SeededBroken
    {
        /// <summary>Fails exactly when ci-green is disabled, as a real can-fail test does.</summary>
        [Fact(Explicit = true)]
        public void Real() => Assert.False(GateCheckSeam.Off(GateChecks.CiGreen));

        [Fact(Explicit = true)]
        public void Empty()
        {
        }

        [Fact(Explicit = true)]
        public void Unrelated() => Assert.False(GateCheckSeam.Off(GateChecks.CiGreen));

        [Fact(Explicit = true)]
        public void AlwaysFails() => Assert.Fail("always");

        [Fact(Explicit = true)]
        public void Crashes()
        {
            if (GateCheckSeam.Off(GateChecks.SecurityReview))
            {
                throw new InvalidOperationException("crash");
            }
        }

        [Theory(Explicit = true)]
        [InlineData(0)]
        [InlineData(1)]
        public async Task Rows(int row)
        {
            await Task.Yield();
            if (row == 0)
            {
                Assert.False(GateCheckSeam.Off(GateChecks.CiGreen));
            }
        }
    }
}
