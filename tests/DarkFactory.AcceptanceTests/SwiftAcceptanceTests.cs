using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 5's epic acceptance tests (epic sc-26124, sc-26134; docs/acceptance.md, "Phase 5"): Swift and Xcode changes through the
/// whole pipeline. Live: each is skipped unless <c>FACTORY_E2E=1</c>, its own story variable, <c>Worker:Swift=true</c> and what it
/// names besides. Each runs <c>factory run</c> (production wiring, a throwaway ledger and work root, <see cref="E2e"/>) on a To Do
/// story. P5-AT1 is the real thing end to end (a real implementer, the real panel); the others replace the panel with
/// <see cref="SeededReviewer"/> and seed the Swift change on the PR branch (as the workers' App) before the first review, like
/// P4-AT4, so the gate's own rule decides. The merging tests write the target's default branch through the gate; the others close
/// their pull request and delete the branch.
/// </summary>
public class SwiftAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(120);

    private sealed record Run(E2e E2e, FactoryOptions Options, int StoryId, SandboxRepo Repo, bool Merges, CancellationTokenSource Timeout)
        : IAsyncDisposable
    {
        public CancellationToken Ct => Timeout.Token;

        public async ValueTask DisposeAsync()
        {
            if (!Merges)
            {
                await Repo.CloseItemAsync(StoryId);
            }
            Repo.Dispose();
            Timeout.Dispose();
            await E2e.DisposeAsync();
        }
    }

    private static async Task<Run> StartAsync(string variable, string prefix, Func<FactoryOptions, RepoRef> repo, bool merges, string purpose)
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story(variable, purpose);
        var e2e = await E2e.StartAsync(prefix);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var options = e2e.Options();
            SwiftLive.RequireSwift(options);
            timeout.CancelAfter(RunTimeout);
            var target = repo(options);
            await SwiftLive.RequireGateReadyAsync(options, target, timeout.Token);
            return new Run(e2e, options, storyId, new SandboxRepo(options, target), merges, timeout);
        }
        catch
        {
            timeout.Dispose();
            await e2e.DisposeAsync();
            throw;
        }
    }

    private static async Task<RunOutcome> FactoryRunAsync(Run run, Func<GateStage, GateStage>? adjust = null)
    {
        using var shortcutHttp = OutboundHttp.ShortcutApi();
        return await FactoryRunner.RunAsync(run.Options, WorkSourceRegistry.Shortcut(run.Options, shortcutHttp), run.StoryId, ignoreScope: true,
            Console.Out, run.Ct, adjust);
    }

    /// <summary>
    /// The item merged through the gate in <c>run.Repo</c> with a new test <paramref name="isTest"/> picks, judged by
    /// <paramref name="strategy"/> (<see cref="SwiftLive.MergedProblems"/>), every row's typed outcome, and the pull request merged on
    /// GitHub at the head the gate passed, its merge commit the Merge row's. Returns the history and the merged pull request.
    /// </summary>
    private static async Task<(List<LedgerEntry> History, NewTestsResult NewTests, JsonElement Pr)> AssertMergedAsync(Run run, RunOutcome outcome,
        string strategy, Func<NewTestResult, bool> isTest)
    {
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        var history = await run.E2e.HistoryAsync(run.StoryId, run.Ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.True(run.Repo.Repo.Is(await SwiftLive.ItemRepoAsync(run.E2e.Ledger, StoryId.Format(run.StoryId), run.Ct)),
            $"sc-{run.StoryId} was not worked in {run.Repo.Repo}: give the story a `Repo: {run.Repo.Repo}` line");
        var problems = SwiftLive.MergedProblems(history, [strategy], isTest);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        var newTests = NewTestsResult.FromDetail(history.Last(e => e.Step == RunPipeline.Steps.NewTests).Detail)!;
        var pr = await ReviewGateTests.MergedPullRequestAsync(run.Repo.Repo, run.StoryId, outcome.PullRequestUrl!, run.Ct);
        Assert.Equal(history.Last(e => e.Step == RunPipeline.Steps.GatePassed).Detail, pr.GetProperty("head").GetProperty("sha").GetString());
        Assert.Equal(history.Last(e => e.Step is null && e.State == WorkState.Merge).Detail, pr.GetProperty("merge_commit_sha").GetString());
        return (history, newTests, pr);
    }

    /// <summary>The item escalated at the gate with <paramref name="reason"/> and nothing merged; the history and the last <c>gate</c> row.</summary>
    private static async Task<(List<LedgerEntry> History, LedgerEntry Gate)> AssertBlockedAsync(Run run, RunOutcome outcome, string reason)
    {
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        var history = await run.E2e.HistoryAsync(run.StoryId, run.Ct);
        ReviewGateTests.AssertTypedOutcomes(history);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], history.Where(e => e.Step is null).Select(e => e.State).TakeLast(2));
        Assert.DoesNotContain(history, e => e.Step is null && e.State == WorkState.Merge);
        Assert.DoesNotContain(history, e => e.Step == RunPipeline.Steps.GatePassed);
        var gate = history.Last(e => e.Step == RunPipeline.Steps.GateDecision);
        Assert.StartsWith($"{GateOutcome.Blocked} ", gate.Detail);
        Assert.Contains(reason, gate.Detail);
        Assert.Equal(StepOutcome.GateRejected, gate.Outcome);
        var url = RunPipeline.LinkedPullRequestUrl(history);
        var pr = (await E2e.PullRequestsAsync(run.Repo.Repo, run.StoryId, run.Ct)).Single(p => p.GetProperty("html_url").GetString() == url);
        Assert.Equal(JsonValueKind.Null, pr.GetProperty("merged_at").ValueKind);
        return (history, gate);
    }

    /// <summary>A reviewer that pushes P5-AT2/P5-AT5's change (<see cref="SwiftSeeds.MergingChange"/>) before its first review.</summary>
    private static SeededReviewer SeedMergingChange(Run run, (string Source, string SourceText, string Test, string TestText, string TestId) change) =>
        new(beforeFirst: async (_, ct) =>
        {
            var token = await run.Repo.AppTokenAsync(ct);
            var branch = StoryId.BranchName(run.StoryId);
            await run.Repo.PutFileAsync(token, branch, change.Source, change.SourceText, "acceptance: a Swift function the new test calls", ct);
            await run.Repo.PutFileAsync(token, branch, change.Test, change.TestText, "acceptance: an XCTest test that fails on the base and passes on the head", ct);
        });

    private static Task<string> RequirePackageAsync(Run run) =>
        SwiftLive.RequireFileOnMainAsync(run.Repo, $"{SwiftSeeds.PackageDir}/Package.swift",
            $"copy tests/DarkFactory.AcceptanceTests/Seeds/{SwiftSeeds.PackageSeed} to {SwiftSeeds.PackageDir}/ by an owner's pull request", run.Ct);

    /// <summary>
    /// P5-AT1: a LensWorks change with a new Swift Testing test merges with no human. <c>factory run</c> on a To Do story whose
    /// <c>Repo:</c> line names <c>michaeltrefry/LensWorks</c> (macOS app <c>native/Compositor.xcodeproj</c>, scheme
    /// <c>Compositor</c>, Swift Testing unit target <c>CompositorTests</c>): a real implementer writes the change and its test, the
    /// real panel reviews it on the high class, CI runs, and the gate's new-tests check runs the new test with xcodebuild as the
    /// sandbox user on macOS — failing on the base, passing on the head — and merges. Needs CodeGraph to index Swift (C3): before,
    /// the gate blocks every Swift change on risk-threshold, and this test skips saying so.
    /// </summary>
    [Fact]
    public async Task P5_AT1_a_lensworks_change_with_a_new_swift_testing_test_merges_with_no_human()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_LENSWORKS_STORY", "df_e2e_p5at1", _ => SwiftLive.LensWorks, merges: true,
            "a To Do story with a `Repo: michaeltrefry/LensWorks` line asking for a small change with a new Swift Testing test in CompositorTests");
        await SwiftLive.RequireSwiftIndexedAsync(run.Options, run.Repo.Repo, await SwiftLive.SwiftFileOnMainAsync(run.Repo, "native/", run.Ct), run.Ct);

        var outcome = await FactoryRunAsync(run);

        var (history, newTests, pr) = await AssertMergedAsync(run, outcome, new XcodeNewTests().Name,
            t => t.Test.StartsWith($"{SwiftLive.LensWorksTarget}/", StringComparison.Ordinal));
        // The real panel, on the high class, passed the head the gate merged.
        var verdict = RunPipeline.Verdicts(history).Last();
        Assert.True(verdict.Passed, verdict.Summary);
        Assert.Equal(newTests.HeadSha, verdict.HeadSha);
        ReviewGateTests.AssertHighClassPanel(verdict);
        // The new test's target is the macOS unit-test target, at the merge commit.
        var test = newTests.Tests.First(t => t.Test.StartsWith($"{SwiftLive.LensWorksTarget}/", StringComparison.Ordinal) && t.Head == TestCaseResult.Passed);
        var target = await SwiftLive.TargetAtAsync(run.Repo, pr.GetProperty("merge_commit_sha").GetString()!, test.Test, run.Ct);
        Assert.Equal((XcodeProject.MacOS, XcodeTarget.UnitTestBundle), target);
        Assert.Equal("Done", (await E2e.StoryAsync(run.StoryId, run.Ct)).State);
    }

    /// <summary>
    /// P5-AT2: a SwiftPM package change with a new XCTest test merges. Before the first review the test pushes, into the sandbox's
    /// package <c>e2e-swift/</c> (<see cref="SwiftSeeds.PackageSeed"/>, on main by the runbook's one-time set-up), a source file with a
    /// new function and an XCTest test calling it (<see cref="SwiftSeeds.MergingChange"/>): <c>not-built</c> on the base, passed on
    /// the head, judged by the SwiftPM strategy as the worker user. The gate merges; the sandbox's main then holds both files (one
    /// pair per run, marked safe to delete). Needs CodeGraph to index Swift (C3).
    /// </summary>
    [Fact]
    public async Task P5_AT2_a_swiftpm_change_with_a_new_xctest_test_merges()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_SWIFTPM_STORY", "df_e2e_p5at2", o => o.DefaultRepo, merges: true,
            "a To Do bug story whose fix lands in normal code of the sandbox repo (the gate will merge it)");
        await RequirePackageAsync(run);
        var change = SwiftSeeds.MergingChange(SwiftSeeds.RunId());
        await SwiftLive.RequireSwiftIndexedAsync(run.Options, run.Repo.Repo, $"{SwiftSeeds.PackageDir}/Sources/DfE2eSwift/DfE2eSwift.swift", run.Ct);

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = SeedMergingChange(run, change) });

        var (_, newTests, pr) = await AssertMergedAsync(run, outcome, new SwiftNewTests().Name, t => t.Test == change.TestId);
        var seeded = Assert.Single(newTests.Tests, t => t.Test == change.TestId);
        Assert.Equal((NewTestsCheck.NotBuilt, TestCaseResult.Passed), (seeded.Base, seeded.Head));
        var files = await run.Repo.TreeFilesAsync(await run.Repo.AppTokenAsync(run.Ct), pr.GetProperty("base").GetProperty("ref").GetString()!, run.Ct);
        Assert.Contains(change.Source, files);
        Assert.Contains(change.Test, files);
    }

    /// <summary>
    /// Raises the iOS app's run count on the PR branch and pushes <paramref name="test"/> (made for the new count), before the first
    /// review; returns the reviewer and a box the test id lands in.
    /// </summary>
    private static async Task<(SeededReviewer Reviewer, Func<string?> TestId)> SeedIosAsync(Run run,
        Func<string, int, string, (string Test, string TestText, string TestId)> test)
    {
        var dir = SwiftLive.IosDir();
        var counter = await SwiftLive.RequireFileOnMainAsync(run.Repo, SwiftSeeds.CounterFile(dir),
            $"copy tests/DarkFactory.AcceptanceTests/Seeds/{SwiftSeeds.IosSeed} to {dir}/ by an owner's pull request", run.Ct);
        Assert.True(SwiftSeeds.CountOf(counter) is not null, $"{SwiftSeeds.CounterFile(dir)} on main holds no `static let count = N` line");
        string? id = null;
        var reviewer = new SeededReviewer(beforeFirst: async (_, ct) =>
        {
            var token = await run.Repo.AppTokenAsync(ct);
            var branch = StoryId.BranchName(run.StoryId);
            // The count main holds now (another run may have merged since the test started), plus one.
            var current = await run.Repo.FileTextAsync(token, "main", SwiftSeeds.CounterFile(dir), ct);
            var count = SwiftSeeds.CountOf(current)!.Value + 1;
            var seeded = test(dir, count, SwiftSeeds.RunId());
            await run.Repo.PutFileAsync(token, branch, SwiftSeeds.CounterFile(dir), SwiftSeeds.WithCount(current, count),
                $"acceptance: the e2e run count is {count}", ct);
            await run.Repo.PutFileAsync(token, branch, seeded.Test, seeded.TestText, "acceptance: a test that the run count is reached", ct);
            id = seeded.TestId;
        });
        return (reviewer, () => id);
    }

    /// <summary>
    /// P5-AT3: an iOS app change with a new unit test, run in the simulator, merges. The iOS repository is
    /// <c>FACTORY_E2E_P5_IOS_REPO</c> (default: the sandbox), holding the app seed (<see cref="SwiftSeeds.IosSeed"/>, an app target,
    /// the unit-test target <c>TallyTests</c> it hosts and the UI-test target <c>TallyUITests</c>, all <c>SDKROOT = iphoneos</c>) at
    /// <c>FACTORY_E2E_P5_IOS_DIR</c> (default <c>e2e-ios</c>). Before the first review the test raises the app's run count
    /// (<c>Tally/E2eRun.swift</c>) on the PR branch and adds an XCTest unit test that the count is reached: it fails on the base
    /// (one lower) and passes on the head, each run on the run's own fresh iOS Simulator as the sandbox user. The gate merges.
    /// Needs the owner logged in at the console (an Aqua session) and CodeGraph to index Swift (C3).
    /// </summary>
    [Fact]
    public async Task P5_AT3_an_ios_app_change_with_a_new_unit_test_run_in_the_simulator_merges()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_IOS_UNIT_STORY", "df_e2e_p5at3", SwiftLive.IosRepo, merges: true,
            "a To Do bug story whose Repo: line names the iOS repository (default: the sandbox) and whose fix lands in its normal code");
        await SwiftLive.RequireSwiftIndexedAsync(run.Options, run.Repo.Repo, SwiftSeeds.CounterFile(SwiftLive.IosDir()), run.Ct);
        var (reviewer, testId) = await SeedIosAsync(run, SwiftSeeds.IosUnitTest);

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = reviewer });

        var (_, newTests, pr) = await AssertMergedAsync(run, outcome, new XcodeNewTests().Name, t => t.Test == testId());
        var seeded = Assert.Single(newTests.Tests, t => t.Test == testId());
        Assert.Equal((TestCaseResult.Failed, TestCaseResult.Passed), (seeded.Base, seeded.Head));
        Assert.Equal((XcodeProject.IOS, XcodeTarget.UnitTestBundle),
            await SwiftLive.TargetAtAsync(run.Repo, pr.GetProperty("merge_commit_sha").GetString()!, seeded.Test, run.Ct));
    }

    /// <summary>
    /// P5-AT4: an iOS UI test runs in the simulator and merges. As P5-AT3, but the new test is an XCUITest in <c>TallyUITests</c>:
    /// it launches the app on the run's own simulator and reads the run count the app shows (<c>e2e-runs</c>), so it fails on the
    /// base and passes on the head. The way the S0 spike found (sc-26126): every simulator step that needs it booted in one launch
    /// helper run, a fresh device per run in the sandbox user's own device set, deleted after.
    /// </summary>
    [Fact]
    public async Task P5_AT4_an_ios_ui_test_runs_in_the_simulator_and_merges()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_IOS_UI_STORY", "df_e2e_p5at4", SwiftLive.IosRepo, merges: true,
            "a To Do bug story whose Repo: line names the iOS repository (default: the sandbox) and whose fix lands in its normal code");
        await SwiftLive.RequireSwiftIndexedAsync(run.Options, run.Repo.Repo, SwiftSeeds.CounterFile(SwiftLive.IosDir()), run.Ct);
        var (reviewer, testId) = await SeedIosAsync(run, SwiftSeeds.IosUiTest);

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = reviewer });

        var (_, newTests, pr) = await AssertMergedAsync(run, outcome, new XcodeNewTests().Name, t => t.Test == testId());
        var seeded = Assert.Single(newTests.Tests, t => t.Test == testId());
        Assert.Equal((TestCaseResult.Failed, TestCaseResult.Passed), (seeded.Base, seeded.Head));
        Assert.Equal((XcodeProject.IOS, XcodeTarget.UITestBundle),
            await SwiftLive.TargetAtAsync(run.Repo, pr.GetProperty("merge_commit_sha").GetString()!, seeded.Test, run.Ct));
    }

    /// <summary>
    /// P5-AT5, first part: while CodeGraph does not index Swift the gate refuses a Swift change. The same change as P5-AT2, whose
    /// new test passes the new-tests check, so nothing but risk-threshold can block it: the last <c>gate</c> row is Blocked with the
    /// reason <c>CodeGraph does not index the language of a changed code file</c> naming the Swift files, its CodeGraph inputs say
    /// <c>language not indexed by CodeGraph</c>, and nothing merges. Skips once CodeGraph indexes Swift (after C3 the refusal of
    /// Swift cannot be shown).
    /// </summary>
    [Fact]
    public async Task P5_AT5_the_gate_refuses_a_swift_change_while_codegraph_does_not_index_swift()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_UNINDEXED_STORY", "df_e2e_p5at5a", o => o.DefaultRepo, merges: false,
            "a To Do bug story whose fix lands in normal code of the sandbox repo");
        await RequirePackageAsync(run);
        var (indexed, said) = await SwiftLive.LanguageIndexedAsync(run.Options, run.Repo.Repo, $"{SwiftSeeds.PackageDir}/Sources/DfE2eSwift/DfE2eSwift.swift", run.Ct);
        if (indexed)
        {
            Assert.Skip($"{said}: the refusal of an unindexed Swift change can only be shown before C3 (run the second part instead).");
        }
        var change = SwiftSeeds.MergingChange(SwiftSeeds.RunId());

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = SeedMergingChange(run, change) });

        var (history, gate) = await AssertBlockedAsync(run, outcome, SwiftLive.UnindexedReason);
        // Nothing else blocked it: the new test passed the new-tests check.
        var newTests = NewTestsResult.FromDetail(history.Last(e => e.Step == RunPipeline.Steps.NewTests).Detail)!;
        Assert.True(newTests.Outcome == NewTestsOutcome.Pass, $"{newTests.Outcome}: {newTests.Reason}");
        Assert.Contains(SwiftLive.UnindexedMark, gate.Detail);
        Assert.Contains(change.Source, gate.Detail);
        Assert.Contains($"{change.Source} (Swift", outcome.Error);
    }

    /// <summary>
    /// P5-AT5, second part: once CodeGraph indexes Swift (C3, sc-26137, deployed with <c>languageIndexed</c>) the gate accepts the
    /// same change: risk-threshold reads each Swift file's blast radius, names no file as not indexed, and the change merges. Skips
    /// until CodeGraph says it indexes Swift.
    /// </summary>
    [Fact]
    public async Task P5_AT5_the_gate_accepts_the_swift_change_once_codegraph_indexes_swift()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_INDEXED_STORY", "df_e2e_p5at5b", o => o.DefaultRepo, merges: true,
            "a To Do bug story whose fix lands in normal code of the sandbox repo (the gate will merge it)");
        await RequirePackageAsync(run);
        await SwiftLive.RequireSwiftIndexedAsync(run.Options, run.Repo.Repo, $"{SwiftSeeds.PackageDir}/Sources/DfE2eSwift/DfE2eSwift.swift", run.Ct);
        var change = SwiftSeeds.MergingChange(SwiftSeeds.RunId());

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = SeedMergingChange(run, change) });

        var (history, _, _) = await AssertMergedAsync(run, outcome, new SwiftNewTests().Name, t => t.Test == change.TestId);
        var gate = history.Last(e => e.Step == RunPipeline.Steps.GateDecision);
        Assert.DoesNotContain(SwiftLive.UnindexedMark, gate.Detail);
        // risk-threshold read CodeGraph's blast radius of the Swift files at the head (CodeGraphRisk.Describe lists them).
        Assert.Contains("codegraph blast radius at", gate.Detail);
        Assert.Contains(change.Source, gate.Detail);
    }

    /// <summary>
    /// P5-AT6: a new Swift test that already passes on the base is refused. Before the first review the test pushes a Swift Testing
    /// test asserting nothing the change does (<see cref="SwiftSeeds.AlreadyPassing"/>) into the sandbox's package; the SwiftPM
    /// strategy runs it on the base, where it passes, and the gate rejects it by name (<c>gate_rejected</c>). Nothing merges.
    /// The story's own .NET tests must each fail on main (as P4-AT4's rejection): the result names the first failing strategy.
    /// </summary>
    [Fact]
    public async Task P5_AT6_a_new_swift_test_that_already_passes_on_the_base_is_gate_rejected()
    {
        await using var run = await StartAsync("FACTORY_E2E_P5_SWIFT_PASSING_STORY", "df_e2e_p5at6", o => o.DefaultRepo, merges: false,
            "a To Do bug story whose fix lands in normal code of the sandbox repo, each of its new xUnit tests failing on main");
        await RequirePackageAsync(run);
        var seed = SwiftSeeds.AlreadyPassing;
        var reviewer = new SeededReviewer(beforeFirst: async (_, ct) =>
        {
            var token = await run.Repo.AppTokenAsync(ct);
            await run.Repo.PutFileAsync(token, StoryId.BranchName(run.StoryId), seed.Test, seed.TestText,
                "acceptance: a Swift test that already passes on the base", ct);
        });

        var outcome = await FactoryRunAsync(run, gate => gate with { Reviewer = reviewer });

        var (history, _) = await AssertBlockedAsync(run, outcome, $"{GateChecks.NewTestsFailOnBase} ({NewTestsOutcome.Rejected})");
        var newTests = history.Last(e => e.Step == RunPipeline.Steps.NewTests);
        Assert.Equal(StepOutcome.GateRejected, newTests.Outcome);
        var result = Assert.IsType<NewTestsResult>(NewTestsResult.FromDetail(newTests.Detail));
        Assert.True(result.Outcome == NewTestsOutcome.Rejected, $"{result.Outcome}: {result.Reason}");
        Assert.Equal(new SwiftNewTests().Name, result.Strategy);
        var passing = Assert.Single(result.Tests, t => t.Test == seed.TestId);
        Assert.Equal((TestCaseResult.Passed, TestCaseResult.Passed), (passing.Base, passing.Head));
        Assert.Contains(seed.TestId, result.Reason);
    }
}

/// <summary>
/// P5-AT7 (sc-26134; docs/acceptance.md, "P5-AT7"): one real LensWorks epic and one real iOS change went through the factory. The
/// runbook runs them with <c>factory work</c> over a watch scope on the owner's real ledger; this test then checks that ledger,
/// read only (no write, no lock, no model): each item listed in <c>FACTORY_E2E_P5_AT7_ITEMS</c> merged through the gate with a new
/// Swift test, and the set holds a LensWorks item and an item of the iOS repository whose new test ran on an iOS Simulator.
/// </summary>
public class SwiftRealRunTests
{
    [Fact]
    public async Task P5_AT7_a_real_lensworks_epic_and_a_real_ios_change_went_through_the_factory()
    {
        Harness.RequireOptIn();
        var listed = Harness.RequireEnv("FACTORY_E2E_P5_AT7_ITEMS",
            "the ledger ids (sc-N or kb-N) of every item of the real LensWorks epic and of the real iOS change, comma-separated");
        Harness.RequireEnv("FACTORY_E2E_P5_IOS_REPO", "the iOS repository (owner/name) the real iOS change was made in");
        var ids = listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = Harness.Options;
        var iosRepo = SwiftLive.IosRepo(options);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await Harness.RequireLedgerAsync();

        var platforms = new List<(RepoRef Repo, string Id, string? Platform)>();
        foreach (var id in ids)
        {
            var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == id, ct);
            Assert.True(item is not null, $"the ledger ({options.LedgerConnectionString.Split(';')[0]}…) has no item {id}");
            var history = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item!.Id).OrderBy(e => e.Id).ToListAsync(ct);
            ReviewGateTests.AssertTypedOutcomes(history);
            if (history.Any(e => e.Step == RunPipeline.Steps.PlanStories))
            {
                // A plan item: it is delivered by its changeset pull request and closed out; its stories are listed and checked themselves.
                Assert.True(item!.State == WorkState.Done, $"{id} is a plan item in {item.State}, not Done");
                continue;
            }
            var problems = SwiftLive.MergedProblems(history, [new SwiftNewTests().Name, new XcodeNewTests().Name], _ => true);
            Assert.True(problems.Count == 0, $"{id}: {string.Join("; ", problems)}");
            var newTests = NewTestsResult.FromDetail(history.Last(e => e.Step == RunPipeline.Steps.NewTests).Detail)!;
            var repo = RepoRef.Parse(item!.Repo);
            using var github = new SandboxRepo(options, repo);
            var url = RunPipeline.LinkedPullRequestUrl(history);
            var pr = (await E2e.PullRequestsAsync(repo, $"factory/{id}", ct)).Single(p => p.GetProperty("html_url").GetString() == url);
            Assert.NotEqual(JsonValueKind.Null, pr.GetProperty("merged_at").ValueKind);
            var mergeCommit = pr.GetProperty("merge_commit_sha").GetString()!;
            Assert.Equal(history.Last(e => e.Step is null && e.State == WorkState.Merge).Detail, mergeCommit);
            foreach (var test in newTests.Tests.Where(t => t.Test.Contains('/') && t.Head == TestCaseResult.Passed && t.Base != TestCaseResult.Passed))
            {
                platforms.Add((repo, id, (await SwiftLive.TargetAtAsync(github, mergeCommit, test.Test, ct))?.Platform));
            }
            platforms.Add((repo, id, null));
        }

        Assert.True(platforms.Any(p => p.Repo.Is(SwiftLive.LensWorks)), $"none of {listed} is a LensWorks item");
        Assert.True(platforms.Any(p => p.Repo.Is(iosRepo) && p.Platform == XcodeProject.IOS),
            $"none of {listed} is an item of {iosRepo} whose new test ran on an iOS Simulator");
    }
}
