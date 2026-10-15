using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// The Phase 5 live tests' seeds and checks, in-process (not live, sc-26134): each seed is identified by the real strategy exactly as
/// the live test expects — the id it looks the test up by, nothing unisolable — on a base holding the runbook's one-time seed, so a
/// live run never fails on the harness itself.
/// </summary>
public class SwiftLiveTests
{
    private static readonly RepoRef Repo = new("o", "r");

    /// <summary>A base and a head as file maps; the changes are the head's files that differ from the base's (added or modified).</summary>
    private sealed class Trees(IReadOnlyDictionary<string, string> @base, IReadOnlyDictionary<string, string> head) : IGateTestRunner
    {
        private IReadOnlyDictionary<string, string> Of(string sha) => sha == "head" ? head : @base;

        public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>([.. head.Where(f => !@base.TryGetValue(f.Key, out var old) || old != f.Value)
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => @base.ContainsKey(f.Key) ? new ChangedFile('M', f.Key, f.Key) : new ChangedFile('A', null, f.Key))]);

        public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. Of(sha).Keys.Order(StringComparer.Ordinal)]);

        public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) =>
            Task.FromResult(Of(sha).TryGetValue(path, out var text) ? text : null);

        public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct) =>
            throw new NotSupportedException("identification runs nothing");
    }

    private static Dictionary<string, string> Seed(string seed, string into) =>
        SwiftSeeds.Files(seed, into).ToDictionary(f => f.Path, f => f.Content, StringComparer.Ordinal);

    private static async Task<NewTestSet> IdentifyAsync(INewTestStrategy strategy, Dictionary<string, string> @base, params (string Path, string Content)[] added)
    {
        var head = new Dictionary<string, string>(@base, StringComparer.Ordinal);
        foreach (var (path, content) in added)
        {
            head[path] = content;
        }
        var runner = new Trees(@base, head);
        var ct = TestContext.Current.CancellationToken;
        return await strategy.IdentifyAsync(new TestSource(runner, Repo, "base", "head"), await runner.ChangesAsync(Repo, "base", "head", ct), ct);
    }

    [Fact]
    public void The_seeds_are_where_the_runbook_says_and_hold_what_the_tests_seed_into()
    {
        var package = Seed(SwiftSeeds.PackageSeed, SwiftSeeds.PackageDir);
        Assert.Contains($"{SwiftSeeds.PackageDir}/Package.swift", package.Keys);
        Assert.Contains($"{SwiftSeeds.PackageDir}/Sources/DfE2eSwift/DfE2eSwift.swift", package.Keys);
        Assert.Contains($".testTarget(name: \"{SwiftSeeds.PackageTarget}\"", package[$"{SwiftSeeds.PackageDir}/Package.swift"]);
        var app = Seed(SwiftSeeds.IosSeed, SwiftSeeds.IosDir);
        Assert.Contains($"{SwiftSeeds.IosDir}/Tally.xcodeproj/project.pbxproj", app.Keys);
        Assert.Contains($"{SwiftSeeds.IosDir}/Tally.xcodeproj/xcshareddata/xcschemes/Tally.xcscheme", app.Keys);
        Assert.Equal(0, SwiftSeeds.CountOf(app[SwiftSeeds.CounterFile(SwiftSeeds.IosDir)]));
        Assert.Contains("accessibilityIdentifier(\"e2e-runs\")", app[$"{SwiftSeeds.IosDir}/Tally/Tally.swift"]);
    }

    [Fact]
    public async Task The_merging_swiftpm_change_has_one_new_xctest_test_the_swiftpm_strategy_names_as_the_live_tests_expect()
    {
        var change = SwiftSeeds.MergingChange("1a2b3c4d");
        Assert.NotEqual(change, SwiftSeeds.MergingChange("5e6f7a8b"));

        var set = await IdentifyAsync(new SwiftNewTests(xcode: true), Seed(SwiftSeeds.PackageSeed, SwiftSeeds.PackageDir),
            (change.Source, change.SourceText), (change.Test, change.TestText));

        Assert.Equal([change.TestId], set.Tests);
        Assert.Equal("DfE2eSwiftTests.P1a2b3c4dTests/testDoubles", change.TestId);
        Assert.Empty(set.Unisolable);
        // The Xcode strategy leaves a SwiftPM package's files to the SwiftPM one.
        var xcode = await IdentifyAsync(new XcodeNewTests(), Seed(SwiftSeeds.PackageSeed, SwiftSeeds.PackageDir),
            (change.Source, change.SourceText), (change.Test, change.TestText));
        Assert.Empty(xcode.Tests);
        Assert.Empty(xcode.Unisolable);
    }

    [Fact]
    public async Task The_already_passing_swift_test_is_one_new_swift_testing_test_named_as_the_live_test_expects()
    {
        var seed = SwiftSeeds.AlreadyPassing;

        var set = await IdentifyAsync(new SwiftNewTests(xcode: true), Seed(SwiftSeeds.PackageSeed, SwiftSeeds.PackageDir), (seed.Test, seed.TestText));

        Assert.Equal([seed.TestId], set.Tests);
        Assert.Equal("DfE2eSwiftTests.e2eAlreadyPassesOnTheBase()", seed.TestId);
        Assert.Empty(set.Unisolable);
    }

    [Fact]
    public async Task Without_the_package_on_the_base_the_merging_change_could_not_be_run_there()
    {
        // Why the package is a one-time seed of main and not part of each run: a test target the base lacks is unisolable.
        var change = SwiftSeeds.MergingChange("1a2b3c4d");
        var package = Seed(SwiftSeeds.PackageSeed, SwiftSeeds.PackageDir);

        var set = await IdentifyAsync(new SwiftNewTests(xcode: true), [], [.. package.Select(p => (p.Key, p.Value)), (change.Source, change.SourceText), (change.Test, change.TestText)]);

        Assert.NotEmpty(set.Unisolable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_ios_runs_raise_the_count_and_add_one_new_test_the_xcode_strategy_names_on_an_ios_target(bool ui)
    {
        var app = Seed(SwiftSeeds.IosSeed, SwiftSeeds.IosDir);
        var counter = SwiftSeeds.CounterFile(SwiftSeeds.IosDir);
        var raised = SwiftSeeds.WithCount(app[counter], 1);
        Assert.Equal(1, SwiftSeeds.CountOf(raised));
        Assert.Equal(app[counter].Replace("count = 0", "count = 1", StringComparison.Ordinal), raised);
        var test = ui ? SwiftSeeds.IosUiTest(SwiftSeeds.IosDir, 1, "1a2b3c4d") : SwiftSeeds.IosUnitTest(SwiftSeeds.IosDir, 1, "1a2b3c4d");

        var set = await IdentifyAsync(new XcodeNewTests(), app, (counter, raised), (test.Test, test.TestText));

        Assert.Equal([test.TestId], set.Tests);
        Assert.Empty(set.Unisolable);
        Assert.Equal(ui ? "TallyUITests/E2eUi1a2b3c4dUITests/testRunCountShown()" : "TallyTests/E2eUnit1a2b3c4dTests/testRunCountReached()", test.TestId);
        // The SwiftPM strategy leaves an Xcode project's files to the Xcode one.
        var swiftPm = await IdentifyAsync(new SwiftNewTests(xcode: true), app, (counter, raised), (test.Test, test.TestText));
        Assert.Empty(swiftPm.Tests);
        Assert.Empty(swiftPm.Unisolable);
        // The test's target runs on an iOS Simulator, as P5-AT3/P5-AT4 check at the merge commit.
        var projects = app.Where(f => f.Key.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal((XcodeProject.IOS, ui ? XcodeTarget.UITestBundle : XcodeTarget.UnitTestBundle), SwiftLive.TargetOf(projects, test.TestId));
        Assert.Null(SwiftLive.TargetOf(projects, "NoSuchTarget/T/test()"));
    }

    [Theory]
    [InlineData("enum E2eRun {\n    static let count = 41\n}\n", 41)]
    [InlineData("enum E2eRun {\n}\n", null)]
    public void The_run_count_is_read_from_the_counter_file(string text, int? count) => Assert.Equal(count, SwiftSeeds.CountOf(text));

    private static LedgerEntry Row(WorkState state, string? step = null, string? detail = null) =>
        new() { State = state, Step = step, Detail = detail, Outcome = StepOutcome.Passed };

    private static NewTestsResult Result(string outcome, string strategy, params NewTestResult[] tests) =>
        new("base1", "head1", outcome, "why", strategy, "ran", "ran", tests);

    private static List<LedgerEntry> Merged(NewTestsResult newTests, string gate) =>
    [
        Row(WorkState.Intake), Row(WorkState.Implement), Row(WorkState.Review), Row(WorkState.CI), Row(WorkState.MergeGate),
        Row(WorkState.MergeGate, RunPipeline.Steps.NewTests, newTests.ToDetail()),
        Row(WorkState.MergeGate, RunPipeline.Steps.GateDecision, gate),
        Row(WorkState.MergeGate, RunPipeline.Steps.GatePassed, "head1"),
        Row(WorkState.Merge, null, "merge1"), Row(WorkState.Watch),
    ];

    private static readonly NewTestResult Seeded = new("T/C/test()", NewTestsCheck.NotBuilt, TestCaseResult.Passed, [], []);

    [Fact]
    public void A_merged_ledger_with_a_new_swift_test_has_no_problems_and_each_missing_part_is_named()
    {
        var gate = $"{GateOutcome.Merge} head1: {GateChecks.NewTestsFailOnBase} | codegraph blast radius at head1: a.swift low";
        Assert.Empty(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Pass, "dotnet-xunit, xcode-swift-testing", Seeded), gate),
            ["xcode-swift-testing"], t => t.Test == "T/C/test()"));

        Assert.Contains(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Pass, "dotnet-xunit", Seeded), gate), ["xcode-swift-testing"], _ => true),
            p => p.Contains("none of xcode-swift-testing", StringComparison.Ordinal));
        Assert.Contains(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Pass, "swift-testing", Seeded with { Base = TestCaseResult.Passed }), gate),
            ["swift-testing"], _ => true), p => p.Contains("no expected new test", StringComparison.Ordinal));
        Assert.Contains(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Rejected, "swift-testing", Seeded), gate), ["swift-testing"], _ => true),
            p => p.StartsWith("new-tests is rejected", StringComparison.Ordinal));
        Assert.Contains(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Pass, "swift-testing", Seeded), $"{gate}; {SwiftLive.UnindexedMark}: a.swift (Swift)"),
            ["swift-testing"], _ => true), p => p.Contains("does not index", StringComparison.Ordinal));
        Assert.Contains(SwiftLive.MergedProblems(Merged(Result(NewTestsOutcome.Pass, "swift-testing", Seeded), $"{GateOutcome.Blocked} head1: no"),
            ["swift-testing"], _ => true), p => p.StartsWith("the last gate row is not a Merge decision", StringComparison.Ordinal));
        var escalated = Merged(Result(NewTestsOutcome.Pass, "swift-testing", Seeded), gate)[..^2];
        escalated.Add(Row(WorkState.Escalated));
        Assert.Contains(SwiftLive.MergedProblems(escalated, ["swift-testing"], _ => true), p => p.StartsWith("the transitions do not end", StringComparison.Ordinal));
    }

    private static ImpactFileAnswer Impact(bool? found, bool? indexed, string? language = null) =>
        new(false, "{}", "c1", null, null, found, "low", "a.swift", indexed, language);

    private static HotspotAnswer Hot(bool? found, bool? indexed, string? language = null) =>
        new(false, "{}", found == true ? "c1" : null, "default-branch", null, found, found == true ? 1 : null, "a.swift", "c1", indexed, language);

    [Fact]
    public void The_language_probe_is_the_gates_rule_over_both_answers_and_fails_on_an_error_or_an_unknown_file()
    {
        // C3, both flags: indexed when either says true and neither false.
        Assert.True(SwiftLive.ProbeOf("a.swift", Impact(true, true, "Swift"), Hot(true, null)).Indexed);
        // A false flag in EITHER answer is unindexed (the gate's rule): the impact answer's alone is enough.
        var (indexed, said) = SwiftLive.ProbeOf("a.swift", Impact(true, false, "Swift"), Hot(true, true, "Swift"));
        Assert.False(indexed);
        Assert.Contains("a.swift (Swift)", said);
        Assert.False(SwiftLive.ProbeOf("a.swift", Impact(true, true), Hot(true, false)).Indexed);
        // Before C3 (no flag): a Swift file CodeGraph has no data for is unindexed.
        Assert.False(SwiftLive.ProbeOf("a.swift", Impact(false, null), Hot(false, null)).Indexed);
        // A C4 notIndexed impact answer (no overlay of that commit) is not read: the hotspot answer decides.
        var notIndexed = Impact(null, null) with { IsError = true, NotIndexed = new CodeGraphNotIndexed("not_indexed", "none", "no overlay", null, null) };
        Assert.True(SwiftLive.ProbeOf("a.swift", notIndexed, Hot(true, true)).Indexed);
        // An error answer fails the probe rather than reading as "not indexed".
        Assert.Contains("file_hotspot answered an error", Assert.Throws<InvalidOperationException>(() =>
            SwiftLive.ProbeOf("a.swift", null, Hot(null, null) with { IsError = true, ErrorCode = "repo_not_found" })).Message);
        Assert.Contains("analyze_impact_file answered an error", Assert.Throws<InvalidOperationException>(() =>
            SwiftLive.ProbeOf("a.swift", Impact(null, null) with { IsError = true, ErrorCode = "invalid_ref" }, Hot(true, true))).Message);
        // An indexed language but a file CodeGraph does not know proves nothing.
        Assert.Contains("has no data for a.swift", Assert.Throws<InvalidOperationException>(() =>
            SwiftLive.ProbeOf("a.swift", Impact(false, true), Hot(false, true))).Message);
    }

    [Fact]
    public void P5_AT6_names_the_strategy_that_decided_when_it_is_not_the_expected_one()
    {
        Assert.Null(SwiftLive.RejectedByProblem(Result(NewTestsOutcome.Rejected, "swift-testing"), "swift-testing"));
        var problem = SwiftLive.RejectedByProblem(Result(NewTestsOutcome.Rejected, "dotnet-xunit"), "swift-testing");
        Assert.Contains("decided by strategy 'dotnet-xunit'", problem);
        Assert.Contains("add no .NET/xUnit test", problem);
    }

    private static readonly RepoRef Lens = SwiftLive.LensWorks;

    private static SwiftLive.RealItem Item(string id, long? epic, bool delivered = true, string source = "shortcut", RepoRef? repo = null, params string[] stories) =>
        new(id, repo ?? Lens, source, epic, delivered, stories);

    [Fact]
    public void P5_AT7_needs_a_real_lensworks_epic_listed_whole()
    {
        var ios = Item("sc-9", 7, repo: Repo);
        // Two LensWorks items of one epic, every item of it listed.
        Assert.Empty(SwiftLive.EpicProblems([Item("sc-1", 5), Item("sc-2", 5), ios], [Item("sc-1", 5), Item("sc-2", 5)]));
        // One LensWorks item alone is no epic.
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-1", 5), ios], [Item("sc-1", 5)]), p => p.StartsWith("no real LensWorks epic", StringComparison.Ordinal));
        // Two items of DIFFERENT epics are no epic.
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-1", 5), Item("sc-2", 6)], [Item("sc-1", 5), Item("sc-2", 6)]),
            p => p.StartsWith("no real LensWorks epic", StringComparison.Ordinal));
        // Two items with no epic are no epic.
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-1", null), Item("sc-2", null)], []), p => p.StartsWith("no real LensWorks epic", StringComparison.Ordinal));
        // An item of the epic in the ledger but not listed.
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-1", 5), Item("sc-2", 5)], [Item("sc-1", 5), Item("sc-2", 5), Item("sc-3", 5)]),
            p => p.Contains("holds sc-3 of the epic 5", StringComparison.Ordinal));
        // A listed item of the epic not delivered.
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-1", 5), Item("sc-2", 5, delivered: false)], [Item("sc-1", 5), Item("sc-2", 5)]),
            p => p.Contains("sc-2 of the epic 5 (shortcut) was not delivered", StringComparison.Ordinal));
        // The same epic number of another source is another epic.
        Assert.Empty(SwiftLive.EpicProblems([Item("sc-1", 5), Item("sc-2", 5)], [Item("sc-1", 5), Item("sc-2", 5), Item("kb-3", 5, source: "kanban")]));
        // A plan item with every story listed and merged.
        Assert.Empty(SwiftLive.EpicProblems([Item("sc-10", null, stories: ["sc-11", "sc-12"]), Item("sc-11", null), Item("sc-12", null)], []));
        // A plan whose story is not listed, or not merged.
        var missing = SwiftLive.EpicProblems([Item("sc-10", null, stories: ["sc-11", "sc-12"]), Item("sc-11", null)], []);
        Assert.Contains(missing, p => p.StartsWith("no real LensWorks epic", StringComparison.Ordinal));
        Assert.Contains(missing, p => p.Contains("sc-12, a story of the plan sc-10, is not listed", StringComparison.Ordinal));
        Assert.Contains(SwiftLive.EpicProblems([Item("sc-10", null, stories: ["sc-11"]), Item("sc-11", null, delivered: false)], []),
            p => p.Contains("sc-11, a story of the plan sc-10, was not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void P5_AT7_needs_an_ios_test_that_ran_and_failed_on_the_base()
    {
        Assert.Null(SwiftLive.IosProblem([new(Repo, "sc-9", "T/C/t()", XcodeProject.IOS, TestCaseResult.Failed)], Repo));
        // Not built on the base: the simulator never ran it there.
        Assert.Contains("was run and failed on the base",
            SwiftLive.IosProblem([new(Repo, "sc-9", "T/C/t()", XcodeProject.IOS, NewTestsCheck.NotBuilt)], Repo));
        // A macOS target, or another repository's iOS test, is not the iOS change.
        Assert.Contains("runs on an iOS Simulator", SwiftLive.IosProblem([new(Repo, "sc-9", "T/C/t()", XcodeProject.MacOS, TestCaseResult.Failed)], Repo));
        Assert.Contains("runs on an iOS Simulator", SwiftLive.IosProblem([new(Lens, "sc-1", "T/C/t()", XcodeProject.IOS, TestCaseResult.Failed)], Repo));
    }

    [Fact]
    public void The_live_checks_name_the_gates_own_words_for_an_unindexed_language()
    {
        var risk = new CodeGraphRisk("head1", [], [], [], Unindexed: [new UnindexedFile("a.swift", "Swift", Flagged: true)]);
        Assert.Contains(SwiftLive.UnindexedMark, risk.Describe());
        Assert.Contains("a.swift (Swift)", risk.Describe());
    }
}
