using System.Text;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-26129: what the Swift reader records for XCTest, and XCTest's tests of a module found by syntax.</summary>
public class SwiftXCTestDiscoveryTests
{
    private static XCTestModule Discover(params (string Path, string Source)[] files) =>
        SwiftXCTest.Discover(files.Select(f => (f.Path, SwiftSource.Parse(f.Source))).ToList());

    [Fact]
    public void Types_inheritance_modifiers_signature_tails_and_conditional_blocks_are_recorded()
    {
        const string source = """
            import XCTest
            @MainActor final class Sub<T: Equatable>: Base<T>, Sendable where T: Hashable {
                override func testA() async throws {}
                private static func testB() -> Int { 1 }
                #if os(macOS)
                func testC() {}
                #endif
                func testD<U>() {}
            }
            extension Sub { func testE() {} }
            """;

        var file = SwiftSource.Parse(source);

        var sub = file.Types[0];
        Assert.Equal(("class", "Sub", "Base<T>,Sendable", true, "MainActor"), (sub.Keyword, sub.Name, string.Join(",", sub.Inherits), sub.Generic, sub.Attributes.Single().Name));
        Assert.Equal(("extension", "Sub"), (file.Types[1].Keyword, file.Types[1].Name));
        var funcs = file.Funcs.ToDictionary(f => f.Name);
        Assert.Equal(("override", "async throws", false), (string.Join(",", funcs["testA"].Modifiers), funcs["testA"].Tail, funcs["testA"].Conditional));
        Assert.Equal(("private,static", "-> Int"), (string.Join(",", funcs["testB"].Modifiers), funcs["testB"].Tail));
        Assert.True(funcs["testC"].Conditional);
        Assert.True(funcs["testD"].Generic);
        Assert.Same(file.Types[1], funcs["testE"].Container);
    }

    [Fact]
    public void A_parameterized_tests_arguments_are_read_without_layout_or_comments()
    {
        var a = SwiftSource.Parse("@Test(\"x\", arguments: [1, 2]) func f(n: Int) {}").Tests.Single();
        var b = SwiftSource.Parse("@Test(\"x\",\n  arguments: [1, /* two */ 2]\n) func f(n: Int) {}").Tests.Single();
        var c = SwiftSource.Parse("@Test(\"x\", arguments: [1, 2, 3]) func f(n: Int) {}").Tests.Single();

        Assert.Equal("arguments : [ 1 , 2 ]", a.TestArguments);
        Assert.Equal(a.TestArguments, b.TestArguments);
        Assert.NotEqual(a.TestArguments, c.TestArguments);
        Assert.Null(SwiftSource.Parse("@Test(\"arguments: in a name\") func f() {}").Tests.Single().TestArguments);
    }

    [Fact]
    public void A_direct_subclass_runs_its_instance_test_methods_with_no_parameter_async_and_throws_allowed()
    {
        var module = Discover(("A.swift", """
            import XCTest
            final class WordsTests: XCTestCase {
                func testBlank() {}
                func testAsync() async throws {}
                func testWith(value: Int) {}
                static func testStatic() {}
                class func testClass() {}
                func helper() {}
                @Test func swiftTesting() {}
            }
            struct Helper { func testNotXCTest() {} }
            final class Plain { func testNotATestClass() {} }
            final class Object: NSObject { func testNotATestClassEither() {} }
            func testFree() {}
            """));

        Assert.Empty(module.Problems);
        Assert.Equal(["WordsTests/testBlank()", "WordsTests/testAsync()"], module.Tests.Select(t => t.Key));
    }

    [Fact]
    public void A_subclass_runs_what_it_inherits_too_and_a_method_it_overrides_once()
    {
        var module = Discover(
            ("Base.swift", "import XCTest\nclass BaseCase: XCTestCase {\n    func testShared() {}\n    func testOverridden() {}\n}\n"),
            ("Sub.swift", "@testable import Words\nfinal class SubTests: BaseCase {\n    override func testOverridden() {}\n    func testOwn() {}\n}\nfinal class Deeper: SubTests {}\n"));

        Assert.Empty(module.Problems);
        Assert.Equal(
        [
            ("BaseCase/testShared()", "Base.swift", "BaseCase"), ("BaseCase/testOverridden()", "Base.swift", "BaseCase"),
            ("SubTests/testOverridden()", "Sub.swift", "SubTests"), ("SubTests/testOwn()", "Sub.swift", "SubTests"), ("SubTests/testShared()", "Base.swift", "BaseCase"),
            ("Deeper/testOverridden()", "Sub.swift", "SubTests"), ("Deeper/testOwn()", "Sub.swift", "SubTests"), ("Deeper/testShared()", "Base.swift", "BaseCase"),
        ], module.Tests.Select(t => (t.Key, t.Path, t.Method.Container!.Name)));
    }

    [Theory]
    [InlineData("final class T: SharedCase { func testA() {} }", "T/testA()", "its class T: it inherits SharedCase, which this module does not declare")]
    [InlineData("final class T: XCTestCase { private func testA() {} }", "T/testA()", "it is private")]
    [InlineData("final class T: XCTestCase { fileprivate func testA() {} }", "T/testA()", "it is private")]
    [InlineData("final class T: XCTestCase { func testA<U>() {} }", "T/testA()", "it is generic")]
    [InlineData("final class T: XCTestCase { @objc(other) func testA() {} }", "T/testA()", "Objective-C name")]
    [InlineData("@objc(Other) final class T: XCTestCase { func testA() {} }", "T/testA()", "Objective-C name")]
    [InlineData("final class T: XCTestCase {\n#if os(macOS)\nfunc testA() {}\n#endif\n}", "T/testA()", "under #if")]
    [InlineData("#if DEBUG\nfinal class T: XCTestCase { func testA() {} }\n#endif", "T/testA()", "its class T is declared under #if")]
    [InlineData("final class T: XCTestCase { func testA() -> Int { 1 } }", "T/testA()", "its signature ends '-> Int'")]
    [InlineData("final class T: XCTestCase { func `testA`() {} }", "T/`testA`()", "backticked")]
    [InlineData("final class T<U>: XCTestCase { func testA() {} }", "T/testA()", "its class T is generic")]
    [InlineData("enum Outer { final class T: XCTestCase { func testA() {} } }", "Outer/T/testA()", "nested in another type")]
    [InlineData("final class T: XCTestCase {}\nextension T { func testA() {} }", "T/testA()", "declared in an extension of T")]
    [InlineData("extension XCTestCase { func testEverywhere() {} }", "XCTestCase/testEverywhere()", "declared in an extension of XCTestCase")]
    [InlineData("#if os(Linux)\nfinal class T: XCTestCase { func testA() {} }\n#else\nfinal class T: XCTestCase { func testA() {} }\n#endif", "T/testA()", "declared 2 times")]
    public void A_test_looking_method_the_gate_cannot_run_apart_is_a_problem_never_left_out(string declaration, string key, string why)
    {
        var module = Discover(("A.swift", "import XCTest\n" + declaration + "\n"));

        Assert.Empty(module.Tests);
        Assert.Contains(module.Problems, p => p.Key == key && p.Path == "A.swift" && p.Why.Contains(why, StringComparison.Ordinal));
        Assert.Contains(key, module.Candidates);
    }

    [Fact]
    public void A_subclass_of_a_class_the_gate_cannot_read_is_a_problem_for_what_it_would_inherit()
    {
        var module = Discover(("A.swift", "import XCTest\n#if DEBUG\nclass Base: XCTestCase { func testA() {} }\n#endif\nfinal class Sub: Base {}\n"));

        Assert.Empty(module.Tests);
        Assert.Contains(module.Problems, p => p.Key == "Base/testA()");
        Assert.Contains(module.Problems, p => p.Key == "Sub/(inherited from Base)" && p.Why.Contains("under #if", StringComparison.Ordinal));
    }
}

/// <summary>sc-26129: XCTest and parameterized Swift Testing tests of a SwiftPM package — found, run and read.</summary>
public class SwiftXCTestIdentifyTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string Head = "head1";
    private const string TestFile = SwiftIdentifyTests.TestFile;
    private const string LegacyFile = "Tests/WordsTests/LegacyTests.swift";
    private const string Legacy = "import XCTest\n@testable import Words\n\nclass LegacyTests: XCTestCase {\n    func testTwo() { XCTAssertEqual(count(\"a b\"), 2) }\n}\n";

    private static FakeTestRunner Runner(params (string Path, string? Content)[] head)
    {
        var runner = SwiftIdentifyTests.Runner(head);
        runner.BaseFiles[LegacyFile] = Legacy;
        return runner;
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static async Task<NewTestSet> Identify(FakeTestRunner runner)
    {
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, Head, CancellationToken.None);
        return await new SwiftNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head), changes, CancellationToken.None);
    }

    [Fact]
    public void An_xctest_id_is_swiftpms_and_its_steps_run_xctest_alone_with_the_output_kept()
    {
        Assert.Equal("WordsTests.LegacyTests/testBlank", SwiftNewTests.XCTestId("WordsTests", "LegacyTests", "testBlank"));
        Assert.True(SwiftNewTests.IsXCTestId("WordsTests.LegacyTests/testBlank"));
        Assert.False(SwiftNewTests.IsXCTestId("WordsTests.many(n:)"));
        Assert.Matches("^swift-xctest-[0-9a-f]{16}-576f7264735465737473\\.out$", SwiftNewTests.XCTestOutputName("WordsTests.LegacyTests/testBlank"));
    }

    [Fact]
    public async Task A_new_xctest_method_is_found_named_as_swiftpm_names_it_and_run_with_xctest_alone()
    {
        var runner = Runner(("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary),
            (LegacyFile, Legacy.Replace("    func testTwo()", "    func testBlank() async throws { XCTAssertEqual(count(\"\"), 0) }\n    func testTwo()")));

        var set = await Identify(runner);
        var result = await Check(runner);
        var steps = await new SwiftNewTests().StepsAsync(null!, Head, set.Tests, set.Projects, ".results", CancellationToken.None);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests.LegacyTests/testBlank"], set.Tests);
        Assert.Equal([new ChangedFile('M', LegacyFile, LegacyFile)], set.TestFiles);
        Assert.Equal((LegacyFile, "LegacyTests/testBlank()"), ((SwiftNewTestSet)set).Locations["WordsTests.LegacyTests/testBlank"]);
        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        var test = Assert.Single(steps, s => s.Phase == TestPhase.Test);
        Assert.Equal(@"test --package-path . --build-system native --skip-build --disable-swift-testing --filter ^WordsTests\.LegacyTests\/testBlank(/|$)", string.Join(' ', test.Args));
        Assert.Equal((null, SwiftNewTests.XCTestOutputName("WordsTests.LegacyTests/testBlank")), (test.ResultFilePattern, test.CaptureOutput));
    }

    [Fact]
    public async Task A_new_method_of_a_base_class_is_a_new_test_of_every_subclass_in_the_target()
    {
        const string sub = "import XCTest\nfinal class MoreTests: LegacyTests {}\n";
        var runner = Runner((LegacyFile, Legacy.Replace("    func testTwo()", "    func testBlank() {}\n    func testTwo()")));
        runner.BaseFiles["Tests/WordsTests/MoreTests.swift"] = sub;

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        // MoreTests is unchanged, but inherits the new method: XCTest runs it there too.
        Assert.Equal(["WordsTests.LegacyTests/testBlank", "WordsTests.MoreTests/testBlank"], set.Tests);
        Assert.Equal((LegacyFile, "LegacyTests/testBlank()"), ((SwiftNewTestSet)set).Locations["WordsTests.MoreTests/testBlank"]);
    }

    [Fact]
    public async Task A_new_subclass_runs_its_inherited_tests_as_new_ones()
    {
        var set = await Identify(Runner(("Tests/WordsTests/MoreTests.swift", "import XCTest\nfinal class MoreTests: LegacyTests {}\n")));

        Assert.Equal(["WordsTests.MoreTests/testTwo"], set.Tests);
        Assert.Empty(set.Unisolable);
    }

    [Fact]
    public async Task An_unchanged_xctest_method_and_one_whose_body_changed_are_not_new()
    {
        var set = await Identify(Runner((LegacyFile, Legacy.Replace("2) }", "2, \"two\") }"))));

        Assert.Empty(set.Tests);
        Assert.Empty(set.Unisolable);
    }

    [Fact]
    public async Task A_new_test_method_whose_class_inherits_a_class_another_module_declares_is_unsupported_never_left_out()
    {
        var runner = Runner(("Tests/WordsTests/Shared.swift", "import XCTest\nimport TestSupport\nfinal class SharedTests: SupportCase { func testA() {} }\n"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("WordsTests.SharedTests/testA() in Tests/WordsTests/Shared.swift is a new XCTest test method the gate cannot run apart: its class SharedTests: it inherits SupportCase", result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task An_unreadable_file_of_the_target_makes_a_new_xctest_test_unsupported()
    {
        var runner = Runner((LegacyFile, Legacy.Replace("    func testTwo()", "    func testBlank() {}\n    func testTwo()")));
        runner.BaseFiles["Tests/WordsTests/Broken.swift"] = "let s = \"open\n";

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("Tests/WordsTests/Broken.swift cannot be read as Swift source, so which XCTest classes inherit its new tests cannot be told", result.Reason);
    }

    [Fact]
    public async Task A_new_parameterized_test_is_one_test_with_all_its_cases()
    {
        var runner = Runner(("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary),
            (TestFile, SwiftIdentifyTests.BaseTests + "@Test(arguments: [\"\", \" \"]) func blank(text: String) { #expect(count(text) == 0) }\n"));

        var set = await Identify(runner);
        var result = await Check(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests.blank(text:)"], set.Tests);
        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.All(runner.Runs, r => Assert.Equal(["WordsTests.blank(text:)"], FakeTestRunner.Filtered(r.Steps)));
    }

    [Theory]
    [InlineData("[1, 2]", "[1, 2, 3]", true)]
    [InlineData("[1, 2]", "[1,\n  2]", false)]
    public async Task An_existing_parameterized_test_whose_arguments_change_is_unsupported(string before, string after, bool unsupported)
    {
        const string test = "@Test(arguments: ARGS) func many(n: Int) { #expect(n > 0) }\n";
        var runner = Runner((TestFile, SwiftIdentifyTests.BaseTests + test.Replace("ARGS", after).Replace("n > 0", "n >= 1")));
        runner.BaseFiles[TestFile] = SwiftIdentifyTests.BaseTests + test.Replace("ARGS", before);

        var set = await Identify(runner);

        Assert.Empty(set.Tests);
        if (unsupported)
        {
            Assert.Contains("WordsTests.many(n:) in Tests/WordsTests/WordsTests.swift is an existing parameterized test whose arguments: the pull request changes", Assert.Single(set.Unisolable));
        }
        else
        {
            Assert.Empty(set.Unisolable);
        }
    }

    [Fact]
    public async Task A_new_xctest_method_calling_code_the_base_lacks_is_not_built()
    {
        var tests = Legacy.Replace("    func testTwo()", "    func testTrims() { XCTAssertEqual(trim(\" a \"), \"a\") }\n    func testTwo()");
        var runner = Runner(("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary), (LegacyFile, tests));
        runner.Answer = spec => spec.Name.EndsWith("-head", StringComparison.Ordinal) ? FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed)
            : spec.Name.EndsWith("-base", StringComparison.Ordinal) ? FakeTestRunner.NotBuilt(At(tests, "trim("))
            : FakeTestRunner.Report(spec.Steps, TestCaseResult.Failed);

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("WordsTests.LegacyTests/testTrims", "not-built")], result.Tests.Select(t => (t.Test, t.Base)));
    }

    private static BuildError At(string content, string text)
    {
        var offset = content.IndexOf(text, StringComparison.Ordinal);
        var lineStart = content.LastIndexOf('\n', offset) + 1;
        return new BuildError(LegacyFile, content.AsSpan(0, offset).Count('\n') + 1, Encoding.UTF8.GetByteCount(content.AsSpan(lineStart, offset - lineStart)) + 1, "swiftc", "cannot find 'trim' in scope");
    }
}

/// <summary>sc-26129: XCTest's console output, and Swift Testing's report of a parameterized test and of a test with several issues.</summary>
public class SwiftXCTestOutputTests
{
    private static readonly SwiftNewTests Strategy = new();
    private const string Id = "LibTests.Sub/testSub";

    private static TestResults Parse(string output, int exit, string id = Id) =>
        Strategy.ParseResults([new ResultFile(SwiftNewTests.XCTestOutputName(id), output) { ExitCode = exit }]);

    // Swift 6.3.2 on macOS, swift test --disable-swift-testing --filter …: XCTest's own lines (paths shortened).
    private const string DarwinFailed = """
        Test Suite 'Selected tests' started at 2026-10-14 15:41:05.527.
        Test Suite 'LibPackageTests.xctest' started at 2026-10-14 15:41:05.527.
        Test Suite 'Sub' started at 2026-10-14 15:41:05.527.
        Test Case '-[LibTests.Sub testSub]' started.
        /work/Tests/LibTests/X.swift:4: error: -[LibTests.Sub testSub] : XCTAssertEqual failed: ("4") is not equal to ("5")
        Test Case '-[LibTests.Sub testSub]' failed (0.045 seconds).
        Test Suite 'Sub' failed at 2026-10-14 15:41:05.572.
        	 Executed 1 test, with 1 failure (0 unexpected) in 0.045 (0.045) seconds
        """;

    private const string DarwinSkipped = """
        Test Case '-[LibTests.Sub testSub]' started.
        /work/Tests/LibTests/Y.swift:4: -[LibTests.Sub testSub] : Test skipped - nope
        Test Case '-[LibTests.Sub testSub]' skipped (0.001 seconds).
        	 Executed 1 test, with 1 test skipped and 0 failures (0 unexpected) in 0.001 (0.001) seconds
        """;

    // swift-corelibs-xctest (Linux): the class unqualified, the start time on the started line.
    private const string LinuxPassed = """
        Test Suite 'Selected tests' started at 2026-10-14 15:41:05.527
        Test Case 'Sub.testSub' started at 2026-10-14 15:41:05.527
        Test Case 'Sub.testSub' passed (0.001 seconds)
        Test Suite 'Selected tests' passed at 2026-10-14 15:41:05.528
        	 Executed 1 test, with 0 failures (0 unexpected) in 0.001 (0.001) seconds
        """;

    [Theory]
    [InlineData(DarwinFailed, 1, TestCaseResult.Failed)]
    [InlineData(DarwinSkipped, 0, TestCaseResult.Skipped)]
    [InlineData(LinuxPassed, 0, TestCaseResult.Passed)]
    public void Xctests_own_lines_give_the_test_its_outcome_on_apple_platforms_and_linux(string output, int exit, string outcome)
    {
        var results = Parse(output, exit);

        Assert.Empty(results.Unreadable);
        Assert.Equal([new TestCaseResult(Id, outcome)], results.Cases[Id]);
    }

    [Fact]
    public void A_filter_that_matched_nothing_is_no_case_and_an_expected_failure_is_no_pass()
    {
        var none = Parse("Test Suite 'Selected tests' started at 2026-10-14 15:42:46.526.\n\t Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.000) seconds\n", 0);
        var expected = Parse(LinuxPassed + "\nnote: XCTExpectFailure: expected failure recorded\n", 0);

        Assert.Empty(none.Unreadable);
        Assert.Empty(none.Cases);
        Assert.Equal(TestCaseResult.Skipped, expected.Cases[Id].Single().Outcome);
    }

    [Theory]
    [InlineData(LinuxPassed, 1, "swift test exited 1, which does not agree with its 0 failing case(s)")]
    [InlineData(DarwinFailed, 0, "swift test exited 0, which does not agree with its 1 failing case(s)")]
    [InlineData("Test Case 'Other.testX' started at x\nTest Case 'Other.testX' passed (0.001 seconds)\n", 0, "it reports LibTests.Other/testX, which is not the test its step ran")]
    [InlineData("Test Case 'Sub.testSub' passed (0.001 seconds)\n", 0, "ends without having started")]
    [InlineData(LinuxPassed + "\nTest Case 'Sub.testSub' passed (0.001 seconds)\n", 0, "ends twice")]
    [InlineData("Test Case 'a b c' started.\nTest Case 'a b c' passed (0.001 seconds).\n", 0, "is not named as XCTest names one")]
    public void Output_that_cannot_be_trusted_is_unreadable_never_a_result(string output, int exit, string why)
    {
        var results = Parse(output, exit);

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
    }

    [Fact]
    public void An_xctest_output_is_no_swift_testing_step_without_a_report()
    {
        var results = Strategy.ParseResults(
        [
            new ResultFile(SwiftNewTests.XCTestOutputName(Id), LinuxPassed) { ExitCode = 0 },
            new ResultFile(SwiftNewTests.ReportName("LibTests.free()"), SwiftRunOutputTests.Passing.Replace("WordsTests", "LibTests").Replace("blank()", "free()")),
            new ResultFile(SwiftNewTests.OutputName("LibTests.free()"), "") { ExitCode = 0 },
        ]);

        Assert.Empty(results.Unreadable);
        Assert.Equal([Id, "LibTests.free()"], results.Cases.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Swift 6.3.2's report of a parameterized test (its cases are one testcase) with two failing cases: failures counts issues.</summary>
    internal const string ParameterizedFailing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="TestResults" errors="0" tests="1" failures="2" skipped="0" time="0.000532083">
            <testcase classname="LibTests.S" name="pairs(_:s:)" time="0.000309792" >
              <failure message="Expectation failed: (x &#8594; 2) != 2 (error)" />
              <failure message="Expectation failed: (x &#8594; 2) != 2 (error)" />
            </testcase>
          </testsuite>
        </testsuites>
        """;

    internal const string ParameterizedPassing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="TestResults" errors="0" tests="1" failures="0" skipped="0" time="0.000318542">
            <testcase classname="LibTests.S" name="pairs(_:s:)" time="0.000109666" />
          </testsuite>
        </testsuites>
        """;

    [Theory]
    [InlineData(ParameterizedFailing, 1, TestCaseResult.Failed)]
    [InlineData(ParameterizedPassing, 0, TestCaseResult.Passed)]
    public void A_parameterized_tests_report_and_one_with_several_issues_are_read(string report, int exit, string outcome)
    {
        const string id = "LibTests.S/pairs(_:s:)";
        var results = Strategy.ParseResults([new ResultFile(SwiftNewTests.ReportName(id), report), new ResultFile(SwiftNewTests.OutputName(id), "") { ExitCode = exit }]);

        Assert.Empty(results.Unreadable);
        Assert.Equal([new TestCaseResult(id, outcome)], results.Cases[id]);
    }
}

/// <summary>sc-26129: XCTest and parameterized tests of an Xcode project's unit-test target.</summary>
public class XcodeXCTestTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string Head = "head1";
    private const string LegacyFile = "WordsTests/LegacyTests.swift";
    private const string Legacy = "import XCTest\n@testable import Words\n\nclass LegacyTests: XCTestCase {\n    func testOld() { XCTAssertFalse(isBlank(\"a\")) }\n}\n";

    private static FakeTestRunner Runner(params (string Path, string? Content)[] head)
    {
        var runner = XcodeIdentifyTests.Runner(head: head);
        runner.BaseFiles[LegacyFile] = Legacy;
        return runner;
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(true), Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static async Task<NewTestSet> Identify(FakeTestRunner runner)
    {
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, Head, CancellationToken.None);
        return await new XcodeNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head), changes, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_xctest_method_is_found_named_as_xcodebuild_names_it_and_run_alone()
    {
        var runner = Runner((XcodeFixture.Library, XcodeFixture.Fixed),
            (LegacyFile, Legacy.Replace("    func testOld()", "    func testBlank() throws { XCTAssertTrue(isBlank(\" \")) }\n    func testOld()")));

        var set = await Identify(runner);
        var result = await Check(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/LegacyTests/testBlank()"], set.Tests);
        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Contains(runner.Runs[0].Steps, s => s.Args.Contains("-only-testing:WordsTests/LegacyTests/testBlank()"));
    }

    [Fact]
    public async Task A_new_method_of_a_base_class_is_a_new_test_of_its_subclass_and_an_unknown_base_class_is_unsupported()
    {
        var runner = Runner((LegacyFile, Legacy.Replace("    func testOld()", "    func testBlank() {}\n    func testOld()")),
            ("WordsTests/MoreTests.swift", "import XCTest\nfinal class MoreTests: LegacyTests {}\n"));
        var unknown = Runner(("WordsTests/MoreTests.swift", "import XCTest\nfinal class MoreTests: SupportCase { func testA() {} }\n"));

        var set = await Identify(runner);
        var refused = await Check(unknown);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/LegacyTests/testBlank()", "WordsTests/MoreTests/testBlank()", "WordsTests/MoreTests/testOld()"], set.Tests);
        Assert.Equal(NewTestsOutcome.Unsupported, refused.Outcome);
        Assert.Contains("WordsTests/MoreTests/testA() in WordsTests/MoreTests.swift is a new XCTest test method the gate cannot run apart: its class MoreTests: it inherits SupportCase", refused.Reason);
    }

    [Fact]
    public async Task A_new_parameterized_test_runs_and_changed_arguments_of_an_existing_one_are_unsupported()
    {
        const string test = "import Testing\n@testable import Words\n\nstruct Params {\n    @Test(arguments: ARGS) func blank(text: String) { #expect(isBlank(text)) }\n}\n";
        var added = Runner(("WordsTests/Params.swift", test.Replace("ARGS", "[\"\", \" \"]")));
        var changed = Runner(("WordsTests/Params.swift", test.Replace("ARGS", "[\"\", \" \", \"\\t\"]")));
        changed.BaseFiles["WordsTests/Params.swift"] = test.Replace("ARGS", "[\"\", \" \"]");

        var set = await Identify(added);
        var changedSet = await Identify(changed);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/Params/blank(text:)"], set.Tests);
        Assert.Empty(changedSet.Tests);
        Assert.Contains("WordsTests/Params/blank(text:) in WordsTests/Params.swift is an existing parameterized test whose arguments: the pull request changes", Assert.Single(changedSet.Unisolable));
    }

    private static TestResults Parse(string id, int testExit, string fixture) => new XcodeNewTests().ParseResults(
    [
        new ResultFile(XcodeNewTests.OutputName(id), "") { ExitCode = testExit },
        new ResultFile(XcodeNewTests.JsonName(id), XcodeFixture.Json(fixture)) { ExitCode = 0 },
    ]);

    [Theory]
    // Xcode 26.5's xcresulttool, recorded from the fixture project with an XCTest class and a parameterized suite (the device id scrubbed).
    [InlineData("WordsTests/Legacy/testOld()", 0, "xctest-passed.json", new[] { TestCaseResult.Passed })]
    [InlineData("WordsTests/Legacy/testOld()", 65, "xctest-failed.json", new[] { TestCaseResult.Failed })]
    [InlineData("WordsTests/Legacy/testSkips()", 0, "xctest-skipped.json", new[] { TestCaseResult.Skipped })]
    [InlineData("WordsTests/Legacy/testInherited()", 65, "xctest-inherited.json", new[] { TestCaseResult.Failed })]
    // A parameterized test: the test case, then each argument's case.
    [InlineData("WordsTests/Params/allBlank(_:)", 0, "parameterized-passed.json", new[] { TestCaseResult.Passed, TestCaseResult.Passed, TestCaseResult.Passed })]
    [InlineData("WordsTests/Params/blanks(text:)", 65, "parameterized-mixed.json", new[] { TestCaseResult.Failed, TestCaseResult.Passed, TestCaseResult.Passed, TestCaseResult.Failed })]
    public void A_bundles_xctest_and_parameterized_cases_are_read(string id, int exit, string fixture, string[] outcomes)
    {
        var results = Parse(id, exit, fixture);

        Assert.Empty(results.Unreadable);
        Assert.Equal(outcomes, results.Cases[id].Select(c => c.Outcome));
    }

    [Fact]
    public void A_parameterized_test_passes_only_when_every_case_did()
    {
        var mixed = Parse("WordsTests/Params/blanks(text:)", 65, "parameterized-mixed.json");
        var report = new TestRunReport(TestRunStatus.Ran, mixed.Cases, "ran");

        var result = NewTestsCheck.Judge("base0", "head1", "xcode-swift-testing", ["WordsTests/Params/blanks(text:)"], report, report);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Contains("do not pass on the head", result.Reason);
    }
}
