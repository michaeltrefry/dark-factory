using System.Diagnostics;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25382: the gate's new-tests-fail-on-base check through the pipeline (fake test runs; nothing executes).</summary>
public class NewTestsGateTests
{
    private const string PolicyStandardNormal = "src/x.cs";

    private static async Task<List<NewTestsResult>> Results(Harness h) =>
        (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.NewTests).Select(r => NewTestsResult.FromDetail(r.Detail)!).ToList();

    [Fact]
    public async Task A_new_test_that_fails_on_the_base_and_passes_on_the_head_passes_the_check_and_merges()
    {
        var h = new Harness();

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);

        // The base run is the base commit with the PR's test files applied; the head run is the head as pushed.
        Assert.Equal(2, h.TestRunner.Runs.Count);
        var (baseRun, headRun) = (h.TestRunner.Runs[0], h.TestRunner.Runs[1]);
        Assert.Equal(("gate-sc-77-base", FakeTestRunner.BaseSha, Sha1), (baseRun.Name, baseRun.Commit, baseRun.OverlayFrom));
        Assert.Equal([FakeTestRunner.NewTestFile], baseRun.OverlayPaths);
        Assert.Empty(baseRun.DeletePaths);
        Assert.Equal(("gate-sc-77-head", Sha1, (string?)null), (headRun.Name, headRun.Commit, headRun.OverlayFrom));
        Assert.All(h.TestRunner.Runs, run => Assert.Equal([FakeTestRunner.NewTest], FakeTestRunner.Filtered(run.Steps)));
        // Each run writes its results to a fresh random directory, which no commit can have filled in advance.
        Assert.All(h.TestRunner.Runs, run => Assert.Matches("^\\.factory-test-results-[0-9a-f]{32}$", run.ResultsDirectory));
        Assert.NotEqual(baseRun.ResultsDirectory, headRun.ResultsDirectory);

        // The executed per-test results are in the ledger, before the gate decision that uses them (E5).
        var result = Assert.Single(await Results(h));
        Assert.Equal((NewTestsOutcome.Pass, FakeTestRunner.BaseSha, Sha1), (result.Outcome, result.BaseSha, result.HeadSha));
        var test = Assert.Single(result.Tests);
        Assert.Equal((FakeTestRunner.NewTest, TestCaseResult.Failed, TestCaseResult.Passed), (test.Test, test.Base, test.Head));
        Assert.Equal([new TestCaseResult(FakeTestRunner.NewTest, TestCaseResult.Failed)], test.BaseCases);
        var rows = await h.Rows();
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.NewTests) < rows.FindIndex(r => r.Step == RunPipeline.Steps.GateDecision));
        Assert.Contains($"1 new test(s) fail on the base base0 and pass on the head {Sha1[..12]}: {FakeTestRunner.NewTest}", rows.Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
    }

    [Fact]
    public async Task A_new_test_that_already_passes_on_the_base_is_rejected_with_the_test_named()
    {
        var h = new Harness();
        h.TestRunner.Answer = spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains($"new-tests-fail-on-base (rejected): new test(s) already pass on the base base0 (with the PR's test files), so they check nothing: {FakeTestRunner.NewTest}",
            outcome.Error);
        var result = Assert.Single(await Results(h));
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal((TestCaseResult.Passed, TestCaseResult.Passed), (result.Tests.Single().Base, result.Tests.Single().Head));
        Assert.StartsWith("Blocked ", (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
    }

    [Fact]
    public async Task A_new_test_that_fails_on_the_head_is_rejected()
    {
        var h = new Harness();
        h.TestRunner.Answer = spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Failed);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains($"new test(s) do not pass on the head {Sha1[..12]}: {FakeTestRunner.NewTest} (failed)", outcome.Error);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_code_change_that_adds_no_test_fails_the_check_and_runs_nothing()
    {
        var h = new Harness();
        h.TestRunner.HeadFiles = _ => new Dictionary<string, string>(h.TestRunner.BaseFiles) { ["src/Widgets/TextTools.cs"] = "namespace Widgets; // changed" };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("new-tests-fail-on-base (no-tests): the PR adds no test", outcome.Error);
        Assert.Empty(h.TestRunner.Runs);
        Assert.Equal(NewTestsOutcome.NoTests, Assert.Single(await Results(h)).Outcome);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_change_only_in_a_tier_that_does_not_require_the_check_needs_no_new_test()
    {
        var h = new Harness();
        h.GitHub.Diff = head => TestPolicies.Diff("docs/guide.md", marker: $"change at {head}");
        h.TestRunner.HeadFiles = _ => new Dictionary<string, string>(h.TestRunner.BaseFiles) { ["docs/guide.md"] = "words" };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(h.TestRunner.Runs);
        Assert.Empty(await Results(h));
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task Tests_of_a_stack_the_gate_cannot_run_fail_the_check_as_unsupported()
    {
        var h = new Harness();
        h.TestRunner.HeadFiles = _ => new Dictionary<string, string>(h.TestRunner.BaseFiles) { ["web/src/app.test.ts"] = "test('x', () => {})" };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("new-tests-fail-on-base (unsupported)", outcome.Error);
        Assert.Contains("web/src/app.test.ts", outcome.Error);
        Assert.Empty(h.TestRunner.Runs);
    }

    [Fact]
    public async Task A_test_run_that_cannot_execute_fails_the_check_and_is_recorded_as_an_error()
    {
        var h = new Harness();
        h.TestRunner.RunThrows = new InvalidOperationException("Worker sandbox user '_factory' is not usable");

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("new-tests-fail-on-base (error): the new tests could not be run: Worker sandbox user '_factory' is not usable", outcome.Error);
        Assert.Equal(NewTestsOutcome.Error, Assert.Single(await Results(h)).Outcome);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_result_recorded_for_the_same_base_and_head_is_reused_after_an_interruption()
    {
        var h = new Harness();
        using var interrupt = new CancellationTokenSource();
        h.GitHub.MergeThrows = new OperationCanceledException(interrupt.Token);
        h.GitHub.OnMergeCall = interrupt.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(ct: interrupt.Token));
        Assert.Equal(2, h.TestRunner.Runs.Count);
        (h.GitHub.MergeThrows, h.GitHub.OnMergeCall) = (null, null);

        var resumed = await h.Run();

        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal(2, h.TestRunner.Runs.Count); // not run again: the ledger holds the executed results
        Assert.Single(await Results(h));
        Assert.Equal(2, (await h.Rows()).Count(r => r.Step == RunPipeline.Steps.GateDecision));
    }

    [Fact]
    public async Task A_new_head_runs_the_tests_again()
    {
        var h = new Harness();
        // GitHub refuses the merge: the head moved to ShaB after the gate; that head is reviewed and tested again.
        h.GitHub.HeadMovesBeforeMerge = ShaB;

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([Sha1, Sha1, ShaB, ShaB], h.TestRunner.Runs.Select(r => r.OverlayFrom ?? r.Commit));
        Assert.Equal([Sha1, ShaB], (await Results(h)).Select(r => r.HeadSha));
        Assert.Equal([$"merge 1 {Sha1}", $"merge 1 {ShaB}"], h.Merges);
    }

    [Theory]
    [InlineData(ControlState.Stopping, WorkState.Cancelled)]
    [InlineData(ControlState.Paused, WorkState.Paused)]
    public async Task A_stop_or_pause_while_the_tests_run_cancels_them_and_records_no_result(ControlState control, WorkState state)
    {
        var h = new Harness { ControlPoll = TimeSpan.FromMilliseconds(20) };
        var cancelled = false;
        h.TestRunner.Running = async (spec, ct) =>
        {
            await h.Controls.SetAsync(ControlScope.Item("sc-77"), control, "tester", CancellationToken.None);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct); // a long run, unless the control cuts it off
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }
            return FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        };

        var outcome = await h.Run();

        Assert.Equal(state, outcome.State);
        Assert.True(cancelled); // the running base run was cut off, and the head run never started
        Assert.Single(h.TestRunner.Runs);
        Assert.Empty(await Results(h));
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.GateDecision);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public void The_policy_floor_keeps_the_check_on_normal_and_protected()
    {
        Assert.Contains(GateChecks.NewTestsFailOnBase, GatePolicy.FloorChecks[Tier.Normal]);
        Assert.Contains(GateChecks.NewTestsFailOnBase, GatePolicy.FloorChecks[Tier.Protected]);
        Assert.DoesNotContain(GateChecks.NewTestsFailOnBase, GatePolicy.FloorChecks[Tier.Free]);
        Assert.DoesNotContain(GateChecks.NewTestsFailOnBase, GatePolicy.FloorChecks[Tier.Sealed]);
        Assert.True(GatePolicy.Parse(TestPolicies.Standard()).Classify(TestPolicies.Diff(PolicyStandardNormal)).Requires(GateChecks.NewTestsFailOnBase));
    }
}

/// <summary>sc-25382: finding the tests a PR adds, and judging the two runs (fake runner; nothing executes).</summary>
public class NewTestsCheckTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private const string Head = "head1";

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static FakeTestRunner WithHead(params (string Path, string Content)[] files)
    {
        var runner = new FakeTestRunner();
        runner.HeadFiles = _ =>
        {
            var head = new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal);
            foreach (var (path, content) in files)
            {
                head[path] = content;
            }
            return head;
        };
        return runner;
    }

    [Fact]
    public async Task New_facts_and_theories_are_named_as_xunit_names_them_and_existing_ones_are_not_new()
    {
        var runner = WithHead((FakeTestRunner.NewTestFile, """
            using Xunit;
            namespace Widgets.Tests
            {
                public class Outer
                {
                    [Fact] public void Kept() { }
                    [Xunit.FactAttribute] public void Qualified() { }
                    public class Inner { [Theory, InlineData(1)] public void Rows(int x) { } }
                    public void NotATest() { }
                }
            }
            """));
        runner.BaseFiles[FakeTestRunner.NewTestFile] = """
            namespace Widgets.Tests { public class Outer { [Fact] public void Kept() { } } }
            """;

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal(["Widgets.Tests.Outer.Qualified", "Widgets.Tests.Outer+Inner.Rows"], result.Tests.Select(t => t.Test));
        Assert.Equal(["Widgets.Tests.Outer.Qualified", "Widgets.Tests.Outer+Inner.Rows"], FakeTestRunner.Filtered(runner.Runs[0].Steps));
    }

    [Fact]
    public async Task A_test_moved_to_another_file_is_not_new_and_a_renamed_test_file_is_moved_in_the_base_run()
    {
        var runner = new FakeTestRunner();
        runner.BaseFiles["tests/Widgets.Tests/OldTests.cs"] = "namespace Widgets.Tests; public class WordCountTests { [Fact] public void Old() { } }";
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles)
        {
            [FakeTestRunner.NewTestFile] = "namespace Widgets.Tests; public class WordCountTests { [Fact] public void Old() { } [Fact] public void Whitespace_is_not_a_word() { } }",
        };
        runner.Changes = [new ChangedFile('R', "tests/Widgets.Tests/OldTests.cs", FakeTestRunner.NewTestFile)];

        var result = await Check(runner);

        Assert.Equal([FakeTestRunner.NewTest], result.Tests.Select(t => t.Test));
        Assert.Equal([FakeTestRunner.NewTestFile], runner.Runs[0].OverlayPaths);
        Assert.Equal(["tests/Widgets.Tests/OldTests.cs"], runner.Runs[0].DeletePaths);
    }

    [Fact]
    public async Task Every_changed_file_of_a_test_project_is_applied_to_the_base_and_nothing_else()
    {
        var runner = WithHead(
            (FakeTestRunner.NewTestFile, FakeTestRunner.NewTestSource),
            ("tests/Widgets.Tests/Data/words.txt", "a b"),
            ("src/Widgets/TextTools.cs", "namespace Widgets; // the fix"));

        await Check(runner);

        Assert.Equal(["tests/Widgets.Tests/Data/words.txt", FakeTestRunner.NewTestFile], runner.Runs[0].OverlayPaths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_new_test_project_is_applied_to_the_base_whole_and_run_through_its_own_project_file()
    {
        const string project = "tests/Widgets.Tests/Nested.Tests/Nested.Tests.csproj";
        var runner = WithHead((project, FakeTestRunner.XunitProject), ("tests/Widgets.Tests/Nested.Tests/NestedTests.cs", "namespace N; public class NestedTests { [Fact] public void Works() { } }"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal([project, "tests/Widgets.Tests/Nested.Tests/NestedTests.cs"], runner.Runs[0].OverlayPaths.Order(StringComparer.Ordinal));
        // Its own (the deepest) project, not the enclosing one, and no solution: the base's solution does not list it.
        Assert.All(runner.Runs, run => Assert.Equal(["build", "--no-restore", $"./{project}", $"-flp:errorsonly;logfile={run.ResultsDirectory}/build-errors-0.log"],
            run.Steps.Single(s => s.Phase == TestPhase.Build).Args));
    }

    [Fact]
    public async Task New_rows_on_an_existing_theory_cannot_be_isolated_and_fail_the_check()
    {
        const string before = "namespace W; public class T { [Theory] [InlineData(1)] public void Rows(int x) { } }";
        var runner = WithHead((FakeTestRunner.NewTestFile, before.Replace("[InlineData(1)]", "[InlineData(1)]\n[InlineData(2)]")));
        runner.BaseFiles[FakeTestRunner.NewTestFile] = before;

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("new data rows on the existing theory W.T.Rows cannot be run apart from its old rows", result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task A_changed_theory_body_with_the_same_rows_is_not_a_new_test()
    {
        const string before = "namespace W; public class T { [Theory] [InlineData(1)] public void Rows(int x) { } }";
        var runner = WithHead((FakeTestRunner.NewTestFile, before.Replace("{ }", "{ _ = x; }")));
        runner.BaseFiles[FakeTestRunner.NewTestFile] = before;

        Assert.Equal(NewTestsOutcome.NoTests, (await Check(runner)).Outcome);
    }

    [Fact]
    public async Task A_test_method_outside_an_xunit_project_is_another_framework_and_unsupported()
    {
        var runner = WithHead(("tests/Legacy.Tests/LegacyTests.cs", "namespace L; public class LegacyTests { [Test] public void Works() { } }"),
            ("tests/Legacy.Tests/Legacy.Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"NUnit\" Version=\"4.0.0\" /></ItemGroup></Project>"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("tests/Legacy.Tests/LegacyTests.cs has test method L.LegacyTests.Works outside an xUnit test project", result.Reason);
    }

    [Theory]
    [InlineData("web/src/app.test.ts", true)]
    [InlineData("pkg/server/handler_test.go", true)]
    [InlineData("tests/test_api.py", true)]
    [InlineData("spec/models/user_spec.rb", true)]
    [InlineData("src/Widgets.Tests/Helpers.fs", true)]
    [InlineData("docs/testing.md", false)] // not code
    [InlineData("src/Latest/Contest.ts", false)]
    [InlineData("src/Widgets/TextTools.cs", false)]
    public void Test_files_of_any_stack_are_recognized(string path, bool looksLikeTest) => Assert.Equal(looksLikeTest, NewTestsCheck.LooksLikeTest(path));

    private static TestRunReport Run(TestRunStatus status, params (string Test, string Outcome)[] cases) =>
        new(status, cases.GroupBy(c => c.Test).ToDictionary(g => g.Key, g => (IReadOnlyList<TestCaseResult>)g.Select(c => new TestCaseResult($"{c.Test}(case)", c.Outcome)).ToList()), "log");

    private static NewTestsResult Judge(TestRunReport baseRun, TestRunReport headRun, params string[] tests) =>
        NewTestsCheck.Judge(FakeTestRunner.BaseSha, Head, "dotnet-xunit", tests.Length == 0 ? ["T.A"] : tests, baseRun, headRun);

    [Fact]
    public void A_base_that_does_not_build_is_no_evidence_until_its_errors_are_examined()
    {
        var result = Judge(TestRunReport.Failed(TestRunStatus.BuildFailed, "CS0117: 'TextTools' does not contain 'Trim'"), Run(TestRunStatus.Ran, ("T.A", "passed")));
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Equal("build-failed", result.BaseRun);
        Assert.Contains("the base base0 does not build with the PR's test files and its build errors were not examined", result.Reason);
    }

    [Fact]
    public void A_new_test_with_no_result_on_a_base_that_ran_is_an_error_not_a_pass()
    {
        // E.g. a new test project the base's build never included, a filter that matched nothing, or a crashed test host.
        var result = Judge(Run(TestRunStatus.Ran, ("T.B", "failed")), Run(TestRunStatus.Ran, ("T.A", "passed"), ("T.B", "passed")), "T.A", "T.B");
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Equal(["not-run", "failed"], result.Tests.Select(t => t.Base));
        Assert.Equal("new test(s) were not executed on the base base0 (with the PR's test files), which is no evidence they fail there: T.A", result.Reason);
    }

    [Fact]
    public void A_new_test_skipped_on_the_base_is_rejected_and_named()
    {
        var result = Judge(Run(TestRunStatus.Ran, ("T.A", "skipped"), ("T.B", "failed"), ("T.C", "skipped"), ("T.C", "failed")),
            Run(TestRunStatus.Ran, ("T.A", "passed"), ("T.B", "passed"), ("T.C", "passed")), "T.A", "T.B", "T.C");
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        // A theory with a failing case fails on the base even when another case is skipped.
        Assert.Equal("new test(s) are skipped on the base base0, which does not show they fail there: T.A", result.Reason);
    }

    private const string TrimFile = "tests/Widgets.Tests/TrimTests.cs";
    private const string TestA = "Widgets.Tests.TrimTests.A";
    private const string TestB = "Widgets.Tests.TrimTests.B";

    /// <summary>Two new tests: A calls an API the base lacks (line 6), B only one the base has (line 9).</summary>
    private const string TwoTests = """
        namespace Widgets.Tests;

        public class TrimTests
        {
            [Fact]
            public void A() => Assert.Equal("a", TextTools.Trim(" a "));

            [Fact]
            public void B() => Assert.Equal(1, TextTools.WordCount("a"));
        }
        """;

    /// <summary>
    /// A runner over <see cref="TwoTests"/> (or <paramref name="source"/>) whose head run passes every test, whose first base
    /// run does not build with <paramref name="first"/>, and whose base retry (it has replacements) answers <paramref name="retry"/>.
    /// </summary>
    private static FakeTestRunner NotBuildingOnTheBase(BuildError[] first, Func<TestRunSpec, TestRunReport> retry, string source = TwoTests)
    {
        var runner = WithHead((TrimFile, source));
        runner.Answer = spec => spec.OverlayFrom is null ? FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed)
            : spec.Replacements.Count == 0 ? FakeTestRunner.NotBuilt(first)
            : retry(spec);
        return runner;
    }

    private static int LineOf(string text, string fragment) => text.Split('\n').ToList().FindIndex(l => l.Contains(fragment)) + 1;

    [Fact]
    public async Task A_test_that_passes_on_the_base_cannot_hide_behind_another_tests_compile_error()
    {
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 6)], spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal($"new test(s) already pass on the base base0 (with the PR's test files), so they check nothing: {TestB}", result.Reason);
        Assert.Equal([(TestA, "not-built"), (TestB, "passed")], result.Tests.Select(t => (t.Test, t.Base)));
        // One retry of the base: the PR's test files without A (whose own code does not compile there), running just B.
        Assert.Equal(3, runner.Runs.Count);
        var retry = runner.Runs[2];
        Assert.Equal(("gate-sc-1-base-retry", FakeTestRunner.BaseSha, Head), (retry.Name, retry.Commit, retry.OverlayFrom));
        Assert.Equal([TestB], FakeTestRunner.Filtered(retry.Steps));
        Assert.Equal([TrimFile], retry.OverlayPaths);
        Assert.DoesNotContain("void A()", retry.Replacements[TrimFile]);
        Assert.Contains("[Fact]\n    public void B() => Assert.Equal(1, TextTools.WordCount(\"a\"));", retry.Replacements[TrimFile]);
        Assert.Equal(("build-failed", "ran"), (result.BaseRun, result.BaseRetry));
        Assert.Equal([$"{TrimFile}:5 A()"], result.BaseRemoved);
    }

    [Fact]
    public async Task A_test_whose_own_code_does_not_compile_on_the_base_fails_there_and_the_others_get_real_results()
    {
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 6)], spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Failed));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal($"2 new test(s) fail on the base base0 (1 of them do not compile there: {TestA}) and pass on the head head1: {TestA}, {TestB}", result.Reason);
        Assert.Equal(["not-built", "failed"], result.Tests.Select(t => t.Base));
    }

    [Fact]
    public async Task Every_new_test_in_a_type_whose_declaration_does_not_compile_on_the_base_fails_there_without_a_retry()
    {
        var source = TwoTests.Replace("public class TrimTests", "public class TrimTests : TrimFixture");
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 3, "CS0246")], _ => throw new UnreachableException(), source);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal(["not-built", "not-built"], result.Tests.Select(t => t.Base));
        Assert.Equal(2, runner.Runs.Count);
        Assert.Null(result.BaseRetry);
    }

    [Fact]
    public async Task Tests_the_base_retry_still_cannot_build_stay_unproven_and_are_named()
    {
        // The using of a new namespace hides every body error (the compiler stops at declarations): the retry drops it,
        // and then A's own body does not compile either. B never ran on the base, so nothing shows it fails there.
        var source = "using Widgets.Extra;\n" + TwoTests.Replace("TextTools.Trim", "Trimmer.Trim");
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 1, "CS0234")],
            spec => FakeTestRunner.NotBuilt(FakeTestRunner.CompilerError(TrimFile, LineOf(spec.Replacements[TrimFile], "void A()"), "CS0103")), source);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal([(TestA, "not-built"), (TestB, "unproven")], result.Tests.Select(t => (t.Test, t.Base)));
        Assert.StartsWith("new test(s) could not be shown to fail on the base base0: it does not build with the PR's test files for a reason outside their own declarations (", result.Reason);
        Assert.EndsWith($": {TestB}", result.Reason);
        Assert.Equal([TestA, TestB], FakeTestRunner.Filtered(runner.Runs[2].Steps));
        Assert.DoesNotContain("using Widgets.Extra;", runner.Runs[2].Replacements[TrimFile]);
        Assert.Equal("build-failed", result.BaseRetry);
        Assert.Equal([$"{TrimFile}:1 using Widgets.Extra;"], result.BaseRemoved);
        Assert.Equal(3, runner.Runs.Count); // one retry at most
    }

    [Fact]
    public async Task A_compiler_error_outside_every_member_leaves_the_other_tests_unproven_without_a_retry()
    {
        var source = "[assembly: Widgets.Marker]\n" + TwoTests;
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 1, "CS0246"), FakeTestRunner.CompilerError(TrimFile, 7)],
            _ => throw new UnreachableException(), source);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal(["not-built", "unproven"], result.Tests.Select(t => t.Base));
        Assert.Contains($"{TrimFile}(1,5): CS0246 does not compile", result.Reason);
        Assert.EndsWith($": {TestB}", result.Reason);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Theory]
    [InlineData("src/Widgets/TextTools.cs", 1, "CS0117")] // production code: not one of the PR's test files
    [InlineData(TrimFile, 6, "xUnit1004")] // not a compiler error
    [InlineData(null, 0, "MSB4019")] // no file at all
    [InlineData(TrimFile, 99, "CS0117")] // not a position in the file that was built
    public async Task A_base_build_error_that_says_nothing_about_the_new_tests_is_an_error(string? path, int line, string code)
    {
        var runner = NotBuildingOnTheBase([FakeTestRunner.CompilerError(TrimFile, 6), new BuildError(path, line, 5, code, "broken")], _ => throw new UnreachableException());

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("for a reason that says nothing about the new tests (not a compiler error in those files): ", result.Reason);
        Assert.Contains($"{code} broken", result.Reason);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Fact]
    public async Task A_base_build_that_names_no_error_is_an_error()
    {
        var runner = NotBuildingOnTheBase([], _ => throw new UnreachableException());

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("the build failed without naming an error", result.Reason);
    }

    [Fact]
    public void Build_errors_are_read_from_an_errors_only_log_relative_to_the_worktree()
    {
        const string log = """
                 1>/work/wt/tests/W.Tests/T.cs(1,9): error CS0234: The namespace 'W.Extra' is missing (are you missing a reference?) [/work/wt/tests/W.Tests/W.Tests.csproj]
                 1>/real/wt/tests/W.Tests/T.cs(6,47,6,51): error CS0117: 'Text' does not contain 'Trim' [/real/wt/tests/W.Tests/W.Tests.csproj]
            /work/elsewhere/x.cs(2,1): error CS1002: ; expected [/work/elsewhere/p.csproj]
            /work/wt/tests/W.Tests/W.Tests.csproj : error NU1105: Unable to find project information
            MSBUILD : error MSB1009: Project file does not exist.

            """;

        var errors = new XunitNewTests().ParseBuildErrors(log, ["/work/wt", "/real/wt"]);

        Assert.Equal(
        [
            new BuildError("tests/W.Tests/T.cs", 1, 9, "CS0234", "The namespace 'W.Extra' is missing (are you missing a reference?)"),
            new BuildError("tests/W.Tests/T.cs", 6, 47, "CS0117", "'Text' does not contain 'Trim'"),
            new BuildError(null, 2, 1, "CS1002", "; expected"),
            new BuildError(null, 0, 0, "NU1105", "Unable to find project information"),
            new BuildError(null, 0, 0, "MSB1009", "Project file does not exist."),
        ], errors);
    }

    [Theory]
    [InlineData(TestRunStatus.RestoreFailed, "restore-failed")]
    [InlineData(TestRunStatus.TimedOut, "timed-out")]
    [InlineData(TestRunStatus.NoResults, "no-results")]
    public void A_base_run_that_produced_no_evidence_is_an_error_not_a_pass(TestRunStatus status, string text)
    {
        var result = Judge(TestRunReport.Failed(status, "nuget.org unreachable"), Run(TestRunStatus.Ran, ("T.A", "passed")));
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains($"the base base0 with the PR's test files could not be tested ({text}): nuget.org unreachable", result.Reason);
    }

    [Theory]
    [InlineData(TestRunStatus.BuildFailed)]
    [InlineData(TestRunStatus.RestoreFailed)]
    [InlineData(TestRunStatus.TimedOut)]
    public void A_head_that_could_not_be_tested_fails_the_check(TestRunStatus status) =>
        Assert.Equal(NewTestsOutcome.Error, Judge(Run(TestRunStatus.Ran, ("T.A", "failed")), TestRunReport.Failed(status, "x")).Outcome);

    [Fact]
    public void A_new_test_that_is_skipped_or_missing_on_the_head_does_not_pass_there()
    {
        var result = Judge(Run(TestRunStatus.Ran, ("T.A", "failed")), Run(TestRunStatus.Ran, ("T.A", "skipped")), "T.A", "T.B");
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Contains("do not pass on the head head1: T.A (skipped), T.B (not-run)", result.Reason);
    }

    [Fact]
    public void Each_new_test_is_judged_and_only_the_ones_passing_on_the_base_are_named()
    {
        var result = Judge(Run(TestRunStatus.Ran, ("T.A", "failed"), ("T.B", "passed"), ("T.C", "passed"), ("T.C", "failed")),
            Run(TestRunStatus.Ran, ("T.A", "passed"), ("T.B", "passed"), ("T.C", "passed"), ("T.C", "passed")), "T.A", "T.B", "T.C");
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        // A theory counts as failing on the base when any of its cases fails there.
        Assert.EndsWith("so they check nothing: T.B", result.Reason);
        Assert.Equal(["failed", "passed", "failed"], result.Tests.Select(t => t.Base));
    }

    [Fact]
    public void The_result_round_trips_through_the_ledger_detail()
    {
        var result = Judge(Run(TestRunStatus.Ran, ("T.A", "failed")), Run(TestRunStatus.Ran, ("T.A", "passed")));
        Assert.Equivalent(result, NewTestsResult.FromDetail(result.ToDetail()), strict: true);
        Assert.Null(NewTestsResult.FromDetail("not json"));
        Assert.Contains("\"base_cases\":[{\"name\":\"T.A(case)\",\"outcome\":\"failed\"}]", result.ToDetail());
    }
}

/// <summary>sc-25382: the xUnit strategy's commands and result parsing.</summary>
public class XunitNewTestsTests
{
    [Fact]
    public async Task Under_the_testing_platform_each_test_is_a_filter_method_with_an_xunit_trx_report()
    {
        var runner = new FakeTestRunner();
        var source = new TestSource(runner, new RepoRef("a", "b"), FakeTestRunner.BaseSha, "head");

        var steps = await new XunitNewTests().StepsAsync(source, FakeTestRunner.BaseSha, ["N.A.F", "N.A+B.G", "M.C.H"], Projects, ".r", CancellationToken.None);

        // Per project (no solution file: a new test project runs on the base too), every restore, then every build, then the tests.
        Assert.Equal([TestPhase.Restore, TestPhase.Restore, TestPhase.Build, TestPhase.Build, TestPhase.Test, TestPhase.Test], steps.Select(s => s.Phase));
        Assert.All(steps, s => Assert.Equal("dotnet", s.Program));
        Assert.Equal(["restore", "./tests/A.Tests/A.Tests.csproj"], steps[0].Args);
        Assert.Equal(["restore", "./tests/M.Tests/M.Tests.csproj"], steps[1].Args);
        Assert.Equal(["build", "--no-restore", "./tests/A.Tests/A.Tests.csproj", "-flp:errorsonly;logfile=.r/build-errors-0.log"], steps[2].Args);
        Assert.Equal(["build", "--no-restore", "./tests/M.Tests/M.Tests.csproj", "-flp:errorsonly;logfile=.r/build-errors-1.log"], steps[3].Args);
        Assert.Equal(["test", "--project", "./tests/A.Tests/A.Tests.csproj", "--no-build", "--results-directory", ".r/0", "--report-xunit-trx",
            "--filter-method", "N.A.F", "--filter-method", "N.A+B.G"], steps[4].Args);
        Assert.Equal(["test", "--project", "./tests/M.Tests/M.Tests.csproj", "--no-build", "--results-directory", ".r/1", "--report-xunit-trx",
            "--filter-method", "M.C.H"], steps[5].Args);
        Assert.Matches(new XunitNewTests().BuildLogPattern.Replace("*", ".*"), "build-errors-1.log");
    }

    private static readonly Dictionary<string, string> Projects = new()
    {
        ["N.A.F"] = "tests/A.Tests/A.Tests.csproj", ["N.A+B.G"] = "tests/A.Tests/A.Tests.csproj", ["M.C.H"] = "tests/M.Tests/M.Tests.csproj",
    };

    [Fact]
    public async Task Under_vstest_the_tests_are_one_fully_qualified_name_filter_with_the_trx_logger()
    {
        var runner = new FakeTestRunner();
        runner.BaseFiles.Remove("global.json");
        var source = new TestSource(runner, new RepoRef("a", "b"), FakeTestRunner.BaseSha, "head");

        var steps = await new XunitNewTests().StepsAsync(source, FakeTestRunner.BaseSha, ["N.A.F", "N.A+B.G"], Projects, ".r", CancellationToken.None);

        Assert.Equal(["test", "./tests/A.Tests/A.Tests.csproj", "--no-build", "--results-directory", ".r/0", "--logger", "trx",
            "--filter", "FullyQualifiedName=N.A.F|FullyQualifiedName=N.A+B.G"], steps[2].Args);
    }

    [Theory]
    [InlineData("""{ "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }""", true)]
    [InlineData("""{ "sdk": { "version": "10.0.100" } }""", false)]
    [InlineData("""{ "test": { "runner": "VSTest" } }""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void The_testing_platform_comes_from_global_json(string? json, bool platform) => Assert.Equal(platform, XunitNewTests.UsesTestingPlatform(json));

    /// <summary>The shape xUnit v3's TRX report has (trimmed from a real run): cases joined to their method by test id.</summary>
    private const string Trx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
        <UnitTestResult testName="N.Sub.A+Inner.G" outcome="Passed" testId="id-g" />
        <UnitTestResult testName="N.Sub.A.Th(x: 1)" outcome="Passed" testId="id-th1" />
        <UnitTestResult testName="N.Sub.A.Th(x: 2)" outcome="Failed" testId="id-th2"><Output><ErrorInfo><Message>Assert.Equal() Failure</Message></ErrorInfo></Output></UnitTestResult>
        <UnitTestResult testName="N.Sub.A.Skipped" outcome="NotExecuted" testId="id-s" />
        </Results><TestDefinitions>
        <UnitTest name="N.Sub.A+Inner.G" id="id-g"><Execution id="id-g" /><TestMethod className="N.Sub.A+Inner" name="G" /></UnitTest>
        <UnitTest name="N.Sub.A.Th(x: 1)" id="id-th1"><TestMethod className="N.Sub.A" name="Th" /></UnitTest>
        <UnitTest name="N.Sub.A.Th(x: 2)" id="id-th2"><TestMethod className="N.Sub.A" name="Th" /></UnitTest>
        <UnitTest name="N.Sub.A.Skipped" id="id-s"><TestMethod className="N.Sub.A" name="Skipped" /></UnitTest>
        </TestDefinitions></TestRun>
        """;

    [Fact]
    public void Trx_results_are_grouped_by_test_method_with_every_case()
    {
        var results = new XunitNewTests().ParseResults([Trx, "<not xml", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"]);

        Assert.Equal(["N.Sub.A+Inner.G", "N.Sub.A.Skipped", "N.Sub.A.Th"], results.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([new TestCaseResult("N.Sub.A.Th(x: 1)", "passed"), new TestCaseResult("N.Sub.A.Th(x: 2)", "failed")], results["N.Sub.A.Th"]);
        Assert.Equal("skipped", results["N.Sub.A.Skipped"].Single().Outcome);
    }

    [Fact]
    public void Test_methods_are_found_in_file_scoped_namespaces_and_generic_or_nested_types()
    {
        var methods = XunitNewTests.TestMethods("""
            namespace A.B;
            public class Box<T> { [Fact] public void Holds() { } }
            public record R { public class S { [SkippableFact] public void Maybe() { } } }
            public class Artifacts { [Artifact] public void NotATest() { } }
            """);

        Assert.Equal(["A.B.Box`1.Holds", "A.B.R+S.Maybe"], methods.Select(m => m.Id));
    }
}

/// <summary>sc-25382: the gate's own clone — a commit's files, the PR's changes, and throwaway worktrees for test runs.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class GateCloneTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _root = Directory.CreateTempSubdirectory("df-gateclone-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public GateCloneTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Commit(string message, params (string Path, string? Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_seed, path);
            if (content is null)
            {
                File.Delete(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        TestGit.Run(_seed, "add", "-A");
        TestGit.Run(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        return TestGit.Run(_seed, "rev-parse", "HEAD").Trim();
    }

    private GitWorkspace Workspace() => new(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null));

    [Fact]
    public async Task The_clone_reads_changes_files_and_contents_and_a_run_worktree_is_the_base_with_the_test_files_applied()
    {
        var baseSha = Commit("base", ("src/a.cs", "a1"), ("tests/T/old.cs", "old"), ("tests/T/gone.cs", "gone"));
        TestGit.Run(_seed, "push", "-q", _remote, "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-1");
        File.CreateSymbolicLink(Path.Combine(_seed, "link.cs"), "/etc/hosts");
        TestGit.Run(_seed, "mv", "tests/T/old.cs", "tests/T/renamed.cs");
        var headSha = Commit("head", ("src/a.cs", "a2"), ("tests/T/new.cs", "new"), ("tests/T/gone.cs", null));
        TestGit.Run(_seed, "push", "-q", _remote, "factory/sc-1");
        var git = Workspace();
        var ct = CancellationToken.None;

        var changes = await git.ChangedFilesAsync(Repo, baseSha, headSha, ct);

        Assert.Equal(
        [
            new ChangedFile('A', null, "link.cs"), new ChangedFile('M', "src/a.cs", "src/a.cs"), new ChangedFile('D', "tests/T/gone.cs", null),
            new ChangedFile('A', null, "tests/T/new.cs"), new ChangedFile('R', "tests/T/old.cs", "tests/T/renamed.cs"),
        ], changes.OrderBy(c => c.NewPath ?? c.OldPath, StringComparer.Ordinal));
        Assert.Equal(["link.cs", "src/a.cs", "tests/T/new.cs", "tests/T/renamed.cs"], await git.FilesAsync(Repo, headSha, ct));
        Assert.Equal("a1", await git.ReadFileAsync(Repo, baseSha, "src/a.cs", ct));
        Assert.Null(await git.ReadFileAsync(Repo, baseSha, "tests/T/new.cs", ct));
        Assert.Null(await git.ReadFileAsync(Repo, headSha, "link.cs", ct)); // a symlink is never read
        Assert.Null(await git.ReadFileAsync(Repo, headSha, "tests", ct)); // nor a directory

        var ws = await git.PrepareCommitAsync(Repo, "gate-sc-1-base", baseSha, ct);
        await git.OverlayAsync(ws, headSha, ["tests/T/new.cs", "tests/T/renamed.cs"], ["tests/T/old.cs", "tests/T/gone.cs"], ct);

        Assert.Equal("a1", File.ReadAllText(Path.Combine(ws.Path, "src/a.cs"))); // the base's code
        Assert.Equal("new", File.ReadAllText(Path.Combine(ws.Path, "tests/T/new.cs"))); // the PR's tests
        Assert.Equal("old", File.ReadAllText(Path.Combine(ws.Path, "tests/T/renamed.cs")));
        Assert.False(File.Exists(Path.Combine(ws.Path, "tests/T/old.cs")));
        Assert.False(File.Exists(Path.Combine(ws.Path, "tests/T/gone.cs")));
        Assert.Equal(baseSha, TestGit.Run(ws.Path, $"--git-dir={ws.GitDir}", "rev-parse", "HEAD").Trim()); // detached at the base

        // A retry's overlay: an applied file written with given content (through git); only an applied file can be.
        await git.OverlayAsync(ws, headSha, ["tests/T/new.cs"], [], ct, new Dictionary<string, string> { ["tests/T/new.cs"] = "stripped\n" });
        Assert.Equal("stripped\n", File.ReadAllText(Path.Combine(ws.Path, "tests/T/new.cs")));
        await Assert.ThrowsAsync<ArgumentException>(() => git.OverlayAsync(ws, headSha, ["tests/T/new.cs"], [], ct, new Dictionary<string, string> { ["src/a.cs"] = "x" }));
        Assert.Equal("a1", File.ReadAllText(Path.Combine(ws.Path, "src/a.cs")));

        await git.RemoveAsync(Repo, ws, ct);
        Assert.False(Directory.Exists(ws.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => git.PrepareCommitAsync(Repo, "factory-sc-1", baseSha, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => git.PrepareCommitAsync(Repo, "../x", baseSha, ct));
    }

    private const string PlantedTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
        <UnitTestResult testName="W.T.A" outcome="Failed" testId="a" />
        </Results><TestDefinitions><UnitTest name="W.T.A" id="a"><TestMethod className="W.T" name="A" /></UnitTest></TestDefinitions></TestRun>
        """;

    [Fact]
    public async Task Result_files_a_commit_holds_are_never_read_and_a_results_directory_already_there_fails_the_run()
    {
        // The PR's commit plants result files where a run might look: the old fixed directory, and a name it might be given.
        var sha = Commit("planted", ("src/a.cs", "a"), (".factory-test-results/x.trx", PlantedTrx), (".factory-test-results-planted/x.trx", PlantedTrx));
        TestGit.Run(_seed, "push", "-q", _remote, "main");
        var git = Workspace();
        await git.ChangedFilesAsync(Repo, sha, sha, CancellationToken.None); // fetches the commit into the clone
        var runner = new SandboxTestRunner(git, sandbox: null, TimeSpan.FromMinutes(1));
        TestRunSpec Spec(string results) => new("gate-sc-1-head", sha, null, [], [], [], new XunitNewTests(), results);

        // No steps run: anything read would be the commit's.
        var report = await runner.RunAsync(Repo, Spec(NewTestsCheck.NewResultsDirectory()), CancellationToken.None);

        Assert.Equal(TestRunStatus.NoResults, report.Status);
        Assert.Empty(report.Results);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Repo, Spec(".factory-test-results-planted"), CancellationToken.None));
        Assert.Contains("results directory .factory-test-results-planted already exists", refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Repo, Spec("../elsewhere"), CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
        Assert.NotEqual(NewTestsCheck.NewResultsDirectory(), NewTestsCheck.NewResultsDirectory());
    }

    [Fact]
    public void Name_status_output_is_parsed_with_renames_and_copies()
    {
        Assert.Equal(
            [new ChangedFile('M', "a b.cs", "a b.cs"), new ChangedFile('R', "o.cs", "n.cs"), new ChangedFile('C', null, "c.cs"), new ChangedFile('A', null, "x")],
            GitWorkspace.ParseNameStatus("M\0a b.cs\0R087\0o.cs\0n.cs\0C100\0src.cs\0c.cs\0A\0x\0"));
        Assert.Empty(GitWorkspace.ParseNameStatus(""));
    }
}

/// <summary>
/// sc-25382 end to end with the real runner on a local repo — unsandboxed (<c>Worker:RunAs=none</c>: the owner runs the
/// commands, as the development mode does; the sandboxed launch is the epic's live acceptance run): real git worktrees, real
/// <c>dotnet restore/build/test</c> of an xUnit v3 project under Microsoft.Testing.Platform, real TRX parsing.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SandboxTestRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _root = Directory.CreateTempSubdirectory("df-testrun-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public SandboxTestRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_seed, path))!);
        File.WriteAllText(Path.Combine(_seed, path), content);
    }

    private string CommitAndPush(string message, string branch)
    {
        TestGit.Run(_seed, "add", "-A");
        TestGit.Run(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        TestGit.Run(_seed, "push", "-q", _remote, branch);
        return TestGit.Run(_seed, "rev-parse", "HEAD").Trim();
    }

    private const string XunitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
          <ItemGroup><Using Include="Xunit" /></ItemGroup>
          <ItemGroup><PackageReference Include="xunit.v3" Version="4.0.1" /></ItemGroup>
          <ItemGroup><ProjectReference Include="../../src/W/W.csproj" /></ItemGroup>
        </Project>
        """;

    /// <summary>The base: <c>Text.Words</c> splits on every space (so blanks count as words), one test project, a solution.</summary>
    private string Base()
    {
        Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Write("W.slnx", """<Solution><Project Path="src/W/W.csproj" /><Project Path="tests/W.Tests/W.Tests.csproj" /></Solution>""");
        Write("src/W/W.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("src/W/Text.cs", "namespace W; public static class Text { public static int Words(string s) => s.Split(' ').Length; }");
        Write("tests/W.Tests/W.Tests.csproj", XunitProject);
        Write("tests/W.Tests/TextTests.cs", "namespace W.Tests; public class TextTests { [Fact] public void Two_words() => Assert.Equal(2, Text.Words(\"a b\")); }");
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        return baseSha;
    }

    private Task<NewTestsResult> Check(string baseSha, string headSha)
    {
        var git = new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null));
        var runner = new SandboxTestRunner(git, sandbox: null, TimeSpan.FromMinutes(10));
        return NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, baseSha, headSha, "gate-sc-5", null, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named()
    {
        var baseSha = Base();
        Write("src/W/Text.cs", "namespace W; public static class Text { public static int Words(string s) => s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length; }");
        Write("tests/W.Tests/WhitespaceTests.cs", """
            namespace W.Tests;
            public class WhitespaceTests
            {
                [Fact] public void Blank_has_no_words() => Assert.Equal(0, Text.Words("  "));
                [Fact] public void One_word() => Assert.Equal(1, Text.Words("a"));
            }
            """);
        var headSha = CommitAndPush("fix", "factory/sc-5");

        var result = await Check(baseSha, headSha);

        Assert.True(result.Outcome == NewTestsOutcome.Rejected, $"{result.Outcome}: {result.Reason}");
        Assert.Equal(("ran", "ran"), (result.BaseRun, result.HeadRun));
        Assert.EndsWith("so they check nothing: W.Tests.WhitespaceTests.One_word", result.Reason);
        Assert.Equal(
            [("W.Tests.WhitespaceTests.Blank_has_no_words", "failed", "passed"), ("W.Tests.WhitespaceTests.One_word", "passed", "passed")],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)));
        // Only the new tests ran, and both throwaway worktrees are gone.
        Assert.All(result.Tests, t => Assert.Single(t.HeadCases));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    [Fact]
    public async Task In_a_new_test_project_a_test_using_a_new_api_does_not_hide_one_that_already_passes_on_the_base()
    {
        // The PR adds Text.Trim and a test project the base's solution does not list; one of its tests calls Trim (which
        // does not compile on the base), one tests the Words fix, and one passes on the base already.
        var baseSha = Base();
        Write("src/W/Text.cs", """
            namespace W;
            public static class Text
            {
                public static int Words(string s) => s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length;
                public static string Trim(string s) => s.Trim();
            }
            """);
        Write("tests/W2.Tests/W2.Tests.csproj", XunitProject);
        Write("tests/W2.Tests/TextTests.cs", """
            using W;
            namespace W2.Tests;
            public class TextTests
            {
                [Fact] public void Trims() => Assert.Equal("a", Text.Trim(" a "));
                [Fact] public void Blank_has_no_words() => Assert.Equal(0, Text.Words("  "));
                [Fact] public void One_word() => Assert.Equal(1, Text.Words("a"));
            }
            """);
        var headSha = CommitAndPush("trim", "factory/sc-5");

        var result = await Check(baseSha, headSha);

        Assert.True(result.Outcome == NewTestsOutcome.Rejected, $"{result.Outcome}: {result.Reason}");
        Assert.EndsWith("so they check nothing: W2.Tests.TextTests.One_word", result.Reason);
        Assert.Equal(("build-failed", "ran", "ran"), (result.BaseRun, result.BaseRetry, result.HeadRun));
        Assert.Equal(["tests/W2.Tests/TextTests.cs:5 Trims()"], result.BaseRemoved);
        Assert.Equal(
        [
            ("W2.Tests.TextTests.Trims", "not-built", "passed"), ("W2.Tests.TextTests.Blank_has_no_words", "failed", "passed"),
            ("W2.Tests.TextTests.One_word", "passed", "passed"),
        ], result.Tests.Select(t => (t.Test, t.Base, t.Head)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}

internal static class TestGit
{
    public static string Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }
}
