using System.Diagnostics;
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
    public void A_base_that_does_not_build_with_the_new_tests_counts_as_failing_there()
    {
        var result = Judge(TestRunReport.Failed(TestRunStatus.BuildFailed, "CS0117: 'TextTools' does not contain 'Trim'"), Run(TestRunStatus.Ran, ("T.A", "passed")));
        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal(("not-built", "build-failed"), (result.Tests.Single().Base, result.BaseRun));
        Assert.Contains("1 new test(s) pass on the head head1, and the base base0 does not build with them: T.A", result.Reason);
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

        var steps = await new XunitNewTests().StepsAsync(source, FakeTestRunner.BaseSha, ["N.A.F", "N.A+B.G"], CancellationToken.None);

        Assert.Equal([TestPhase.Restore, TestPhase.Build, TestPhase.Test], steps.Select(s => s.Phase));
        Assert.All(steps, s => Assert.Equal("dotnet", s.Program));
        Assert.Equal(["restore"], steps[0].Args);
        Assert.Equal(["build", "--no-restore"], steps[1].Args);
        Assert.Equal(["test", "--no-build", "--results-directory", ".factory-test-results", "--report-xunit-trx",
            "--filter-method", "N.A.F", "--filter-method", "N.A+B.G"], steps[2].Args);
    }

    [Fact]
    public async Task Under_vstest_the_tests_are_one_fully_qualified_name_filter_with_the_trx_logger()
    {
        var runner = new FakeTestRunner();
        runner.BaseFiles.Remove("global.json");
        var source = new TestSource(runner, new RepoRef("a", "b"), FakeTestRunner.BaseSha, "head");

        var steps = await new XunitNewTests().StepsAsync(source, FakeTestRunner.BaseSha, ["N.A.F", "N.A+B.G"], CancellationToken.None);

        Assert.Equal(["test", "--no-build", "--results-directory", ".factory-test-results", "--logger", "trx",
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

        await git.RemoveAsync(Repo, ws, ct);
        Assert.False(Directory.Exists(ws.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => git.PrepareCommitAsync(Repo, "factory-sc-1", baseSha, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => git.PrepareCommitAsync(Repo, "../x", baseSha, ct));
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

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named()
    {
        var remote = Path.Combine(_root, "remote.git");
        var seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", remote);
        TestGit.Run(_root, "init", "-b", "main", seed);
        void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(seed, path))!);
            File.WriteAllText(Path.Combine(seed, path), content);
        }
        Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        Write("W.slnx", """<Solution><Project Path="src/W/W.csproj" /><Project Path="tests/W.Tests/W.Tests.csproj" /></Solution>""");
        Write("src/W/W.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        Write("src/W/Text.cs", "namespace W; public static class Text { public static int Words(string s) => s.Split(' ').Length; }");
        Write("tests/W.Tests/W.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><Using Include="Xunit" /></ItemGroup>
              <ItemGroup><PackageReference Include="xunit.v3" Version="4.0.1" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../../src/W/W.csproj" /></ItemGroup>
            </Project>
            """);
        Write("tests/W.Tests/TextTests.cs", "namespace W.Tests; public class TextTests { [Fact] public void Two_words() => Assert.Equal(2, Text.Words(\"a b\")); }");
        TestGit.Run(seed, "add", "-A");
        TestGit.Run(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "base");
        var baseSha = TestGit.Run(seed, "rev-parse", "HEAD").Trim();
        TestGit.Run(seed, "push", "-q", remote, "main");

        TestGit.Run(seed, "checkout", "-q", "-b", "factory/sc-5");
        Write("src/W/Text.cs", "namespace W; public static class Text { public static int Words(string s) => s.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length; }");
        Write("tests/W.Tests/WhitespaceTests.cs", """
            namespace W.Tests;
            public class WhitespaceTests
            {
                [Fact] public void Blank_has_no_words() => Assert.Equal(0, Text.Words("  "));
                [Fact] public void One_word() => Assert.Equal(1, Text.Words("a"));
            }
            """);
        TestGit.Run(seed, "add", "-A");
        TestGit.Run(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "fix");
        var headSha = TestGit.Run(seed, "rev-parse", "HEAD").Trim();
        TestGit.Run(seed, "push", "-q", remote, "factory/sc-5");

        var git = new GitWorkspace(Path.Combine(_root, "work"), _ => remote, (_, _) => Task.FromResult<string?>(null));
        var runner = new SandboxTestRunner(git, sandbox: null, TimeSpan.FromMinutes(10));

        var result = await NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, baseSha, headSha, "gate-sc-5", null, CancellationToken.None);

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
