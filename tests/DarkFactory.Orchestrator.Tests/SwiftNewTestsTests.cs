using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-26127: the lexer-level reader that finds a Swift file's functions, their types and attributes (nothing compiled).</summary>
public class SwiftSourceTests
{
    private static string[] Tests(string source) =>
        SwiftSource.Parse(source).Tests.Select(t => string.Join("/", t.Types.Append(t.Signature))).ToArray();

    [Fact]
    public void Test_functions_are_found_at_the_top_level_and_in_types_extensions_and_nested_types()
    {
        const string source = """
            import Testing
            @testable import Words

            @Test func blank() { #expect(count("") == 0) }
            @Test("a display name", .tags(.fast)) func named() async throws {}
            @Testing.Test static func qualified() {}
            struct Suite {
                @Test @MainActor func onMain() {}
                @Suite struct Inner {
                    @Test func deep() {}
                }
                func helper() {}
            }
            extension Outer.Inner {
                @Test func extended() {}
            }
            final class Box: Base {
                class func make() -> Box { Box() }
                @Test func boxed() {}
            }
            enum Cases { case a; @Test func inEnum() {} }
            actor Counter { @Test func isolated() {} }
            """;

        Assert.Equal(["blank()", "named()", "qualified()", "Suite/onMain()", "Suite/Inner/deep()", "Outer/Inner/extended()", "Box/boxed()", "Cases/inEnum()",
            "Counter/isolated()"], Tests(source));
        Assert.Equal(["Testing", "Words"], SwiftSource.Parse(source).Imports);
    }

    [Fact]
    public void An_attribute_in_a_comment_or_any_string_literal_is_not_a_test_and_hides_no_brace()
    {
        const string source = """"
            // @Test func lineComment() {
            /* @Test func block() { /* nested */ @Test func stillComment() { */
            let a = "@Test func inString() {"
            let b = """
                @Test func inMultiline() {
                "quoted" "" }
                """
            let c = #"@Test func inRaw() { \(not) "inner" "#
            let d = ##"a "# @Test func f() { "##
            let e = "\(values.map { "}" + "@Test func g() {" }) {"
            let f = #"\#(x.map { "{" })"#
            let r = #/ \{ @Test func h() /#
            @Test func real() {}
            """";

        Assert.Equal(["real()"], Tests(source));
    }

    [Fact]
    public void Function_property_initializer_and_closure_bodies_are_skipped()
    {
        const string source = """
            struct S {
                var computed: Int { func notInAGetter() {}; return 1 }
                init() { struct Local { func local() {} } }
                func helper() { func nested() {} }
                let closure = { func inClosure() {} }
                #if os(Linux)
                @Test func linuxOnly() {}
                #endif
                private(set) var x = 0
                @Test func after() {}
            }
            """;

        Assert.Equal(["S/linuxOnly()", "S/after()"], Tests(source));
        Assert.Equal(["helper", "linuxOnly", "after"], SwiftSource.Parse(source).Funcs.Select(f => f.Name));
    }

    /// <summary>
    /// An @Test the reader does not put on a function it records is never dropped silently: the file is unreadable (so the
    /// check is unsupported). Swift Testing refuses a local @Test function anyway; the rest is what the reader cannot follow.
    /// </summary>
    [Theory]
    [InlineData("@Test\n#if os(macOS)\nfunc mac() {}\n#endif\n")]
    [InlineData("@Test func f<C: Collection<[Int]>>() {}\n@Test func g() {}")]
    [InlineData("@Test struct NotAFunction {}")]
    [InlineData("struct S { @Test }")]
    [InlineData("@Test let notAFunction = 1")]
    [InlineData("@Testing.Test\nimport Foundation")]
    public void An_at_test_dropped_before_a_function_the_reader_records_makes_the_file_unreadable(string source)
    {
        var e = Assert.Throws<FormatException>(() => SwiftSource.Parse(source));

        Assert.Contains("is on no function this reader understands", e.Message);
    }

    /// <summary>
    /// A body the reader skips whole (a getter, an initializer, a function, a closure) is walked by no scope, so an @Test in
    /// it reaches no reset of pending attributes: only the file-wide count sees it. Swift Testing refuses a local @Test
    /// function anyway; the file is unreadable (so the check is unsupported), never one with fewer tests.
    /// </summary>
    [Theory]
    [InlineData("struct S { var computed: Int { @Test func notInAGetter() {}; return 1 } }")]
    [InlineData("struct S { var x: Int { get { @Test func lost() {}; return 1 } } }")]
    [InlineData("struct S { init() { struct Local { @Test func local() {} } } }")]
    [InlineData("struct S { func helper() { @Test func nested() {} } }")]
    [InlineData("let closure = { @Testing.Test func inClosure() {} }")]
    public void An_at_test_in_a_skipped_body_fails_the_files_attribute_count(string source)
    {
        var e = Assert.Throws<FormatException>(() => SwiftSource.Parse(source));

        Assert.Contains("1 @Test attribute(s) but 0 on the test functions", e.Message);
    }

    [Theory]
    [InlineData("struct O {\n    let actor = 1\n    @Test func named() {}\n}\n", "O/named()")]
    [InlineData("struct O {\n    var actor: A!\n    @Test func named() {}\n}\n", "O/named()")]
    [InlineData("let actor = 3\n@Test func g() {}\n", "g()")]
    [InlineData("var actor: A\n@Test func g() {}\n", "g()")]
    [InlineData("let a = actor\nhelper()\n@Test func g() {}\n", "g()")]
    [InlineData("let a = actor\nstruct S { @Test func t() {} }\n", "S/t()")]
    [InlineData("actor Counter: Base { @Test func isolated() {} }\n", "Counter/isolated()")]
    [InlineData("actor Box<T> where T: Sendable { @Test func held() {} }\n", "Box/held()")]
    public void Actor_used_as_an_identifier_is_no_type_and_hides_no_test(string source, string test) =>
        Assert.Equal([test], Tests(source));

    [Theory]
    [InlineData("@Test(arguments: [1, 2]) func f(x: Int) {}", true, "f(x:)")]
    [InlineData("@Test(\"named\", arguments: zip(a, b)) func f(_ a: Int, b: Int) {}", true, "f(_:b:)")]
    [InlineData("@Test(\"arguments: in a name\") func f() {}", false, "f()")]
    [InlineData("@Test(.bug(\"x\", id: 1)) func f() {}", false, "f()")]
    public void A_test_is_parameterized_by_its_arguments_label_or_its_parameters(string source, bool parameterized, string signature)
    {
        var test = Assert.Single(SwiftSource.Parse(source).Tests);
        Assert.Equal((parameterized, signature), (test.IsParameterized, test.Signature));
    }

    [Fact]
    public void XCTest_methods_are_the_test_functions_of_types_in_a_file_that_imports_XCTest()
    {
        const string source = """
            import XCTest
            final class WordsTests: XCTestCase {
                func testBlank() {}
                func testWith(value: Int) {}
                func helper() {}
                @Test func swiftTesting() {}
            }
            func testFree() {}
            """;

        var file = SwiftSource.Parse(source);

        Assert.Equal(["testBlank"], file.XCTestMethods.Select(f => f.Name));
        Assert.Empty(SwiftSource.Parse(source.Replace("import XCTest", "import Foundation")).XCTestMethods);
    }

    [Theory]
    [InlineData("struct S {")]
    [InlineData("let s = \"open")]
    [InlineData("let s = \"\"\"\nnever closed")]
    [InlineData("/* never closed")]
    [InlineData("let s = \"\\(open\"")]
    [InlineData("func f() { ) }")]
    public void Source_that_cannot_be_read_throws_instead_of_holding_no_tests(string source) =>
        Assert.Throws<FormatException>(() => SwiftSource.Parse(source));

    [Fact]
    public void A_manifests_test_targets_are_read_with_whether_they_set_a_path()
    {
        const string manifest = """
            // swift-tools-version: 6.0
            import PackageDescription
            let package = Package(
                name: "Words",
                targets: [
                    .target(name: "Words", path: "Lib"),
                    .testTarget(name: "WordsTests", dependencies: [.target(name: "Words")]),
                    .testTarget(name: "MovedTests", dependencies: ["Words"], path: "Elsewhere"),
                    .testTarget(name: computed),
                ]
            )
            """;

        Assert.Equal([("WordsTests", false), ("MovedTests", true)], SwiftSource.TestTargets(manifest));
    }
}

/// <summary>sc-26127: which tests of a SwiftPM pull request are new, how Swift Testing names them, and the base run's overlay.</summary>
public class SwiftIdentifyTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string Head = "head1";

    internal const string Manifest = """
        // swift-tools-version: 6.0
        import PackageDescription
        let package = Package(
            name: "Words",
            targets: [
                .target(name: "Words"),
                .testTarget(name: "WordsTests", dependencies: ["Words"]),
            ]
        )
        """;

    internal const string BaseLibrary = "public func count(_ s: String) -> Int { s.split(separator: \" \", omittingEmptySubsequences: false).count }\n";
    internal const string FixedLibrary = "public func count(_ s: String) -> Int { s.split(separator: \" \").count }\n";
    internal const string TestFile = "Tests/WordsTests/WordsTests.swift";
    internal const string BaseTests = "import Testing\n@testable import Words\n\n@Test func two() { #expect(count(\"a b\") == 2) }\n";

    internal static FakeTestRunner Runner(params (string Path, string? Content)[] head)
    {
        var runner = CargoIdentifyTests.Runner(("Package.swift", Manifest), ("Sources/Words/Words.swift", BaseLibrary), (TestFile, BaseTests));
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        return CargoIdentifyTests.With(runner, head);
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static async Task<NewTestSet> Identify(FakeTestRunner runner)
    {
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, Head, CancellationToken.None);
        return await new SwiftNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head), changes, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_swift_testing_test_is_found_named_as_swift_testing_names_it_and_its_test_file_applied_to_the_base()
    {
        var runner = Runner(("Sources/Words/Words.swift", FixedLibrary),
            (TestFile, BaseTests + "@Test func blank() { #expect(count(\"\") == 0) }\nstruct Spaces { @Test func double() { #expect(count(\"a  b\") == 2) } }\n"));

        var set = await Identify(runner);
        var result = await Check(runner);

        Assert.Equal(["WordsTests.Spaces/double()", "WordsTests.blank()"], set.Tests);
        Assert.Equal("Package.swift", set.Projects["WordsTests.blank()"]);
        Assert.Empty(set.Unisolable);
        // The test file is the PR's test code; the production change stays the head's alone.
        Assert.Equal([new ChangedFile('M', TestFile, TestFile)], set.TestFiles);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal("swift-testing", result.Strategy);
        var (onBase, onHead) = (runner.Runs[0], runner.Runs[1]);
        Assert.Equal((FakeTestRunner.BaseSha, Head, TestFile), (onBase.Commit, onBase.OverlayFrom, Assert.Single(onBase.OverlayPaths)));
        Assert.Equal((Head, (string?)null), (onHead.Commit, onHead.OverlayFrom));
        Assert.All(runner.Runs, run => Assert.Equal(["WordsTests.Spaces/double()", "WordsTests.blank()"], FakeTestRunner.Filtered(run.Steps)));
    }

    [Fact]
    public async Task A_new_test_that_already_passes_on_the_base_is_rejected_and_named()
    {
        var runner = Runner((TestFile, BaseTests + "@Test func one() { #expect(count(\"a\") == 1) }\n"));
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.EndsWith("so they check nothing: WordsTests.one()", result.Reason);
    }

    [Fact]
    public async Task A_changed_or_renamed_test_and_one_that_moved_to_another_file_of_its_target_are_not_new()
    {
        var runner = Runner((TestFile, null), ("Tests/WordsTests/Moved.swift", BaseTests.Replace("== 2", "== 2, \"two words\"")));

        var set = await Identify(runner);

        Assert.Empty(set.Tests);
        Assert.Equal(NewTestsOutcome.NoTests, (await Check(runner)).Outcome);
    }

    [Fact]
    public async Task A_swift_pull_request_that_changes_only_production_code_has_no_tests_and_runs_nothing()
    {
        var runner = Runner(("Sources/Words/Words.swift", FixedLibrary));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.NoTests, result.Outcome);
        Assert.Empty(runner.Runs);
        Assert.Same(NewTestSet.None, await new SwiftNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head),
            [new ChangedFile('M', "README.md", "README.md")], CancellationToken.None));
    }

    [Theory]
    [InlineData("Tests/WordsTests/LegacyTests.swift", "import XCTest\nfinal class LegacyTests: SharedCase { func testBlank() {} }\n",
        "WordsTests.LegacyTests/testBlank() in Tests/WordsTests/LegacyTests.swift is a new XCTest test method the gate cannot run apart: its class LegacyTests: it inherits SharedCase, which this module does not declare")]
    [InlineData("Sources/Words/Inline.swift", "import Testing\n@Test func inProduction() {}\n",
        "Sources/Words/Inline.swift adds test(s) inProduction() the gate cannot run: it is outside the package's Tests/<target>/ directories")]
    [InlineData("Tests/OtherTests/OtherTests.swift", "import Testing\n@Test func undeclared() {}\n",
        "Package.swift at the head does not declare (as testTarget(name: \"…\") with a plain string) the test target OtherTests")]
    [InlineData(TestFile, BaseTests + "#if os(Linux)\n@Test func twice() {}\n#else\n@Test func twice() {}\n#endif\n", "WordsTests.twice() is declared more than once at the head")]
    [InlineData(TestFile, BaseTests + "@Test func `adds two words`() {}\n", "has a backticked name")]
    [InlineData(TestFile, BaseTests + "@Test func broken() { let s = \"open }\n", "Tests/WordsTests/WordsTests.swift cannot be read as Swift source")]
    [InlineData(TestFile, BaseTests + "@Test\n#if os(macOS)\nfunc mac() {}\n#endif\n",
        "Tests/WordsTests/WordsTests.swift cannot be read as Swift source (an @Test attribute near offset")]
    [InlineData(TestFile, BaseTests + "struct S { func helper() { @Test func nested() {} } }\n",
        "Tests/WordsTests/WordsTests.swift cannot be read as Swift source (it has 2 @Test attribute(s) but 1 on the test functions")]
    public async Task Tests_the_gate_cannot_isolate_yet_fail_the_check_as_unsupported_never_pass(string path, string content, string why)
    {
        var runner = Runner((path, content));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData("struct Names {\n    let actor = 1\n    @Test func named() {}\n}\n", "WordsTests.Names/named()")]
    [InlineData("struct Names {\n    var actor: Actor!\n    @Test func named() {}\n}\n", "WordsTests.Names/named()")]
    [InlineData("let actor = 3\n@Test func afterLet() {}\n", "WordsTests.afterLet()")]
    [InlineData("var actor: Actor? = nil\n@Test func afterVar() {}\n", "WordsTests.afterVar()")]
    public async Task A_test_after_actor_used_as_an_identifier_is_found(string added, string test)
    {
        var set = await Identify(Runner((TestFile, BaseTests + added)));

        Assert.Equal([test], set.Tests);
        Assert.Empty(set.Unisolable);
    }

    [Fact]
    public async Task A_test_in_an_extension_of_a_nested_type_is_named_by_every_type()
    {
        var set = await Identify(Runner((TestFile, BaseTests + "struct Outer { struct Inner {} }\nextension Outer.Inner { @Test func deep() {} }\n")));

        Assert.Equal(["WordsTests.Outer/Inner/deep()"], set.Tests);
        Assert.Empty(set.Unisolable);
    }

    [Fact]
    public async Task A_test_of_a_package_in_a_subdirectory_belongs_to_that_package_and_runs_there()
    {
        var runner = Runner(("sub/Tests/WordsTests/x.swift", "import Testing\n@Test func nested() {}\n"));
        runner.BaseFiles["sub/Package.swift"] = Manifest;
        runner.BaseFiles["sub/Sources/Words/Words.swift"] = BaseLibrary;

        var set = await Identify(runner);
        var steps = await new SwiftNewTests().StepsAsync(null!, Head, set.Tests, set.Projects, ".results", CancellationToken.None);

        Assert.Equal(["WordsTests.nested()"], set.Tests);
        Assert.Equal("sub/Package.swift", set.Projects["WordsTests.nested()"]);
        Assert.Empty(set.Unisolable);
        Assert.Equal([new ChangedFile('A', null, "sub/Tests/WordsTests/x.swift")], set.TestFiles);
        Assert.All(steps.Where(s => s.Program == SwiftNewTests.Swift && s.Phase != TestPhase.Toolchain),
            s => Assert.Equal("./sub", s.Args[s.Args.ToList().IndexOf("--package-path") + 1]));
        Assert.Equal(3, steps.Count(s => s.Args.Contains("--package-path")));
    }

    [Fact]
    public async Task A_swift_test_in_no_swiftpm_package_and_a_changed_test_looking_swift_file_there_are_unsupported()
    {
        var runner = CargoIdentifyTests.With(CargoIdentifyTests.Runner(("App/AppTests/OldTests.swift", "import XCTest\n")),
            ("App/AppTests/AppTests.swift", "import Testing\n@Test func inAnXcodeProject() {}\n"), ("App/AppTests/OldTests.swift", "import XCTest\n// changed\n"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("App/AppTests/AppTests.swift adds test(s) inAnXcodeProject() the gate cannot run: it is in no SwiftPM package", result.Reason);
        Assert.Contains("App/AppTests/OldTests.swift looks like a test file the gate cannot run", result.Reason);
    }

    [Fact]
    public async Task A_test_target_new_in_the_pull_request_or_at_a_custom_path_is_unsupported()
    {
        var withTarget = Manifest.Replace("dependencies: [\"Words\"]),", "dependencies: [\"Words\"]),\n        .testTarget(name: \"MoreTests\"),");
        var added = Runner(("Package.swift", withTarget), ("Tests/MoreTests/MoreTests.swift", "import Testing\n@Test func more() {}\n"));
        var moved = Runner(("Package.swift", Manifest.Replace("dependencies: [\"Words\"])", "dependencies: [\"Words\"], path: \"Tests/WordsTests\")")),
            (TestFile, BaseTests + "@Test func blank() {}\n"));
        var versioned = Runner(("Package@swift-5.9.swift", Manifest), (TestFile, BaseTests + "@Test func blank() {}\n"));

        Assert.Contains("the test target MoreTests is not declared in the base's Package.swift (a new test target)", (await Check(added)).Reason);
        Assert.Contains("Package.swift at the head sets a path: for the test target WordsTests", (await Check(moved)).Reason);
        Assert.Contains("has a version-specific manifest", (await Check(versioned)).Reason);
    }

    [Fact]
    public async Task A_swift_test_beside_dotnet_tests_is_judged_by_each_stack_for_its_own_tests()
    {
        var runner = Runner((TestFile, BaseTests + "@Test func blank() {}\n"));
        runner.BaseFiles[FakeTestRunner.Project] = FakeTestRunner.XunitProject;
        var swiftHead = runner.HeadFiles;
        runner.HeadFiles = sha => new Dictionary<string, string>(swiftHead(sha)) { [FakeTestRunner.NewTestFile] = FakeTestRunner.NewTestSource };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal("dotnet-xunit, swift-testing", result.Strategy);
        Assert.Equal([FakeTestRunner.NewTest, "WordsTests.blank()"], result.Tests.Select(t => t.Test));
    }
}

/// <summary>sc-26127: the steps a Swift run is made of, Swift Testing's report, and swift build's errors.</summary>
public class SwiftRunOutputTests
{
    private static readonly SwiftNewTests Strategy = new();

    [Fact]
    public async Task The_steps_probe_make_the_results_directory_resolve_build_and_run_each_new_test_by_its_exact_id()
    {
        var steps = await Strategy.StepsAsync(null!, "c0ffee", ["WordsTests.blank()", "WordsTests.Spaces/double()", "ToolTests.runs()"],
            new Dictionary<string, string> { ["WordsTests.blank()"] = "Package.swift", ["WordsTests.Spaces/double()"] = "Package.swift", ["ToolTests.runs()"] = "tools/cli/Package.swift" },
            ".results", CancellationToken.None);

        Assert.Equal(
        [
            "Toolchain swift --version",
            "Restore /bin/mkdir -p .results",
            "Restore swift package --package-path . resolve",
            "Restore swift package --package-path ./tools/cli resolve",
            "Build swift build --package-path . --build-system native --build-tests",
            "Build swift build --package-path ./tools/cli --build-system native --build-tests",
            @$"Test swift test --package-path . --build-system native --skip-build --disable-xctest --filter ^WordsTests\.blank\(\)(/|$) --xunit-output .results/{SwiftNewTests.ReportName("WordsTests.blank()")}",
            @$"Test swift test --package-path . --build-system native --skip-build --disable-xctest --filter ^WordsTests\.Spaces\/double\(\)(/|$) --xunit-output .results/{SwiftNewTests.ReportName("WordsTests.Spaces/double()")}",
            @$"Test swift test --package-path ./tools/cli --build-system native --skip-build --disable-xctest --filter ^ToolTests\.runs\(\)(/|$) --xunit-output .results/{SwiftNewTests.ReportName("ToolTests.runs()")}",
        ], steps.Select(s => $"{s.Phase} {s.Program} {string.Join(' ', s.Args)}"));
        Assert.Equal(["swift-build-0", "swift-build-1"], steps.Where(s => s.Phase == TestPhase.Build).Select(s => s.CaptureOutput));
        // Each test step reads exactly its own report (no wildcard another test's file could match) and keeps its exit code.
        Assert.Equal(new[] { "WordsTests.blank()", "WordsTests.Spaces/double()", "ToolTests.runs()" }.Select(id => ((string?)SwiftNewTests.ReportName(id), (string?)SwiftNewTests.OutputName(id))),
            steps.Where(s => s.Phase == TestPhase.Test).Select(s => (s.ResultFilePattern, s.CaptureOutput)));
        Assert.Matches(@"^swift-test-[0-9a-f]{16}\.xml$", SwiftNewTests.ReportName("WordsTests.blank()"));
        Assert.Equal(3, steps.Where(s => s.Phase == TestPhase.Test).Select(s => s.ResultFilePattern).Distinct().Count());
        // No step runs anything but swift and the one mkdir: the gateway lint's known local program is TestStep.Program.
        Assert.All(steps, s => Assert.Contains(s.Program, new[] { SwiftNewTests.Swift, SwiftNewTests.MakeDirectory }));
    }

    /// <summary>
    /// What swift test --xunit-output wrote with Swift 6.3.2 (Swift Testing), one report per step: a passing, a failing
    /// and a skipped test.
    /// </summary>
    internal const string Passing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="TestResults" errors="0" tests="1" failures="0" skipped="0" time="0.000256125">
            <testcase classname="WordsTests" name="blank()" time="0.000256125" />
          </testsuite>
        </testsuites>
        """;

    internal const string Failing = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="TestResults" errors="0" tests="1" failures="1" skipped="0" time="0.000361291">
            <testcase classname="WordsTests.Outer.Inner" name="deep()" time="0.000361291" >
              <failure message="Expectation failed: (count(&quot;a b&quot;) &#8594; 2) == 3 (error)" />
            </testcase>
          </testsuite>
        </testsuites>
        """;

    internal const string Skipped = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites>
          <testsuite name="TestResults" errors="0" tests="0" failures="0" skipped="1" time="0.0001">
            <testcase classname="WordsTests" name="off()" >
              <skipped>not yet</skipped>
            </testcase>
          </testsuite>
        </testsuites>
        """;

    /// <summary>A test step's report and its kept exit code, as the runner hands them to <see cref="SwiftNewTests.ParseResults"/>.</summary>
    private static ResultFile[] Step(string id, string report, int exit) =>
        [new ResultFile(SwiftNewTests.ReportName(id), report), new ResultFile(SwiftNewTests.OutputName(id), "") { ExitCode = exit }];

    [Fact]
    public void Swift_testings_report_gives_each_test_its_outcome_under_its_swift_testing_id()
    {
        var results = Strategy.ParseResults([.. Step("WordsTests.blank()", Passing, 0), .. Step("WordsTests.Outer/Inner/deep()", Failing, 1),
            .. Step("WordsTests.off()", Skipped, 0)]);

        Assert.Empty(results.Unreadable);
        Assert.Equal(TestCaseResult.Passed, results.Cases["WordsTests.blank()"].Single().Outcome);
        Assert.Equal(TestCaseResult.Failed, results.Cases["WordsTests.Outer/Inner/deep()"].Single().Outcome);
        Assert.Equal(TestCaseResult.Skipped, results.Cases["WordsTests.off()"].Single().Outcome);
    }

    [Theory]
    [InlineData("", "it is empty")]
    [InlineData("<testsuites><testsuite", "it is not well-formed XML")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"x\">]><testsuites/>", "it is not well-formed XML")]
    [InlineData("<assemblies/>", "root element <assemblies>")]
    [InlineData("<testsuites><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase classname=\"T\" name=\"a()\"/><testcase classname=\"T\" name=\"b()\"/></testsuite></testsuites>",
        "do not add up to their suite's counts")]
    [InlineData("<testsuites><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase classname=\"T\" name=\"a()\"><failure/></testcase></testsuite></testsuites>",
        "do not add up to their suite's counts")]
    [InlineData("<testsuites><testsuite tests=\"1\"><testcase classname=\"T\" name=\"a()\"/></testsuite></testsuites>", "has no count of its failures")]
    [InlineData("<testsuites><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"a()\"/></testsuite></testsuites>", "names no classname and name")]
    public void A_report_that_cannot_be_trusted_is_unreadable_never_a_result(string content, string why)
    {
        var results = Strategy.ParseResults(Step("T.a()", content, 0));

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
    }

    /// <summary>
    /// The base run runs the PR's test code, which can write files: a report one test writes for another, under a name no
    /// step asked for, or in a subdirectory, and a report its own step's exit code contradicts, are never results.
    /// </summary>
    [Fact]
    public void A_report_holding_another_tests_id_is_unreadable_never_that_tests_result()
    {
        // blank()'s step's report says deep() passed: deep() gets no result from it, and neither does blank().
        const string forged = """
            <testsuites><testsuite tests="1" failures="0" errors="0" skipped="0"><testcase classname="WordsTests.Outer.Inner" name="deep()"/></testsuite></testsuites>
            """;

        var results = Strategy.ParseResults(Step("WordsTests.blank()", forged, 0));

        Assert.Empty(results.Cases);
        Assert.Contains("it holds WordsTests.Outer/Inner/deep(), which is not the test its step ran", Assert.Single(results.Unreadable));
    }

    [Theory]
    [InlineData("swift-test-99.xml")]
    [InlineData("swift-test-0.xml")]
    [InlineData("sub/swift-test-0123456789abcdef.xml")]
    public void A_report_no_step_asked_for_is_unreadable(string name)
    {
        var results = Strategy.ParseResults([new ResultFile(name, Passing)]);

        Assert.Empty(results.Cases);
        Assert.Contains("it is not a report a test step asked for", Assert.Single(results.Unreadable));
    }

    [Theory]
    [InlineData("WordsTests.blank()", Passing, 1, "swift test exited 1, which does not agree with its 0 failing case(s)")]
    [InlineData("WordsTests.Outer/Inner/deep()", Failing, 0, "swift test exited 0, which does not agree with its 1 failing case(s)")]
    public void A_report_its_steps_exit_code_contradicts_is_unreadable(string id, string report, int exit, string why)
    {
        var results = Strategy.ParseResults(Step(id, report, exit));

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
    }

    [Fact]
    public void A_report_whose_steps_exit_code_is_unknown_and_a_step_with_no_report_are_unreadable()
    {
        var withoutExit = Strategy.ParseResults([new ResultFile(SwiftNewTests.ReportName("WordsTests.blank()"), Passing)]);
        var withoutReport = Strategy.ParseResults([new ResultFile(SwiftNewTests.OutputName("WordsTests.blank()"), "") { ExitCode = 0 }]);

        Assert.Empty(withoutExit.Cases);
        Assert.Contains("its step's exit code is not known", Assert.Single(withoutExit.Unreadable));
        Assert.Empty(withoutReport.Cases);
        Assert.Equal($"{SwiftNewTests.ReportName("WordsTests.blank()")}: the test step (exit 0) wrote no report", Assert.Single(withoutReport.Unreadable));
    }

    [Fact]
    public void Build_errors_are_read_from_swift_builds_output_with_files_made_relative_to_the_worktree()
    {
        const string log = """
            [4/5] Compiling WordsTests More.swift
            /work/wt/Tests/WordsTests/More.swift:3:28: error: cannot find 'trim' in scope
            1 | import Testing
            3 | @Test func off() { #expect(trim(" a ") == "a") }
              |                            `- error: cannot find 'trim' in scope
            /elsewhere/Dep.swift:1:1: error: outside
            error: fatalError
            /work/wt/Sources/Words/Words.swift:1:1: warning: unused
            """;

        Assert.Equal(
        [
            new BuildError("Tests/WordsTests/More.swift", 3, 28, "swiftc", "cannot find 'trim' in scope"),
            new BuildError(null, 1, 1, "swiftc", "outside"),
            new BuildError(null, 0, 0, "swiftc", "fatalError"),
        ], Strategy.ParseBuildErrors(log, ["/work/wt"]));
    }

    [Fact]
    public void The_filter_escapes_every_regex_character_of_the_id()
    {
        Assert.Equal(@"^My_Tests\.A\/B\/f\(x\:_\:\)(/|$)", SwiftNewTests.Filter("My_Tests.A/B/f(x:_:)"));
        Assert.Equal(["My_Tests.A/B/f(x:_:)"], FakeTestRunner.Filtered([new TestStep(TestPhase.Test, "swift",
            ["test", "--filter", SwiftNewTests.Filter("My_Tests.A/B/f(x:_:)"), "--xunit-output", "r.xml"])]));
    }
}

/// <summary>sc-26127: the walking skeleton through the pipeline — a SwiftPM change with a new Swift Testing test reaches Merge.</summary>
public class SwiftPipelineTests
{
    private static Harness SwiftHarness(string newTests)
    {
        var h = new Harness();
        var runner = h.TestRunner;
        runner.BaseFiles.Clear();
        runner.BaseFiles["Package.swift"] = SwiftIdentifyTests.Manifest;
        runner.BaseFiles["Sources/Words/Words.swift"] = SwiftIdentifyTests.BaseLibrary;
        runner.BaseFiles[SwiftIdentifyTests.TestFile] = SwiftIdentifyTests.BaseTests;
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal)
        {
            ["Sources/Words/Words.swift"] = SwiftIdentifyTests.FixedLibrary,
            [SwiftIdentifyTests.TestFile] = SwiftIdentifyTests.BaseTests + newTests,
        };
        h.GitHub.Diff = head => $"diff --git a/Sources/Words/Words.swift b/Sources/Words/Words.swift\n+change at {head}\n"
            + $"diff --git a/{SwiftIdentifyTests.TestFile} b/{SwiftIdentifyTests.TestFile}\n+test at {head}\n";
        // A CodeGraph that indexes Swift (C3, sc-26137): without it risk-threshold fails closed on every changed Swift file (sc-26133).
        h.CodeGraph.Impact = (path, sha) => C7Answers.ParsedImpactFile("Sandbox", path, sha, false, "none", languageIndexed: true, language: "swift");
        return h;
    }

    private static async Task<NewTestsResult> Result(Harness h) =>
        NewTestsResult.FromDetail((await h.Rows()).Single(r => r.Step == RunPipeline.Steps.NewTests).Detail)!;

    [Fact]
    public async Task A_swiftpm_change_with_a_new_swift_testing_test_failing_on_the_base_and_passing_on_the_head_merges()
    {
        var h = SwiftHarness("@Test func blank() { #expect(count(\"\") == 0) }\n");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
            await h.Transitions());
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        var result = await Result(h);
        Assert.Equal((NewTestsOutcome.Pass, "swift-testing"), (result.Outcome, result.Strategy));
        Assert.Equal([("WordsTests.blank()", TestCaseResult.Failed, TestCaseResult.Passed)], result.Tests.Select(t => (t.Test, t.Base, t.Head)));
        // The base run is the base with the PR's Swift test file applied, through swift's own steps.
        var baseRun = h.TestRunner.Runs[0];
        Assert.Equal((FakeTestRunner.BaseSha, Sha1, SwiftIdentifyTests.TestFile), (baseRun.Commit, baseRun.OverlayFrom, Assert.Single(baseRun.OverlayPaths)));
        Assert.Equal(SwiftNewTests.Swift, baseRun.Steps.Last().Program);
        Assert.Contains($"1 new test(s) fail on the base base0 and pass on the head {Sha1[..12]}: WordsTests.blank()",
            (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
    }

    [Fact]
    [FailsGateCheck(GateChecks.NewTestsFailOnBase)]
    public async Task A_swiftpm_change_whose_new_test_already_passes_on_the_base_is_rejected_and_not_merged()
    {
        var h = SwiftHarness("@Test func one() { #expect(count(\"a\") == 1) }\n");
        h.TestRunner.Answer = spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("new-tests-fail-on-base (rejected): new test(s) already pass on the base base0 (with the PR's test files), so they check nothing: WordsTests.one()",
            outcome.Error);
        Assert.Equal(NewTestsOutcome.Rejected, (await Result(h)).Outcome);
    }
}

/// <summary>sc-26127: the real toolchain — swift build and swift test through the gate's runner (skipped where swift is not installed).</summary>
public class SwiftRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-swiftrun-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public SwiftRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Lazy<bool> HasSwift = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(SwiftNewTests.Swift, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

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

    private Task<NewTestsResult> Check(string baseSha, string headSha) =>
        NewTestsCheck.RunAsync(new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null,
            TimeSpan.FromMinutes(10)), NewTestsCheck.Strategies, Repo, baseSha, headSha, "gate-sc-5", null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task With_swift_a_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named()
    {
        Assert.SkipUnless(HasSwift.Value, "swift is not installed");
        Write("Package.swift", SwiftIdentifyTests.Manifest);
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.BaseLibrary);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests);
        Write(".gitignore", ".build/\n");
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        const string newTests = "@Test func blank() { #expect(count(\"\") == 0) }\nstruct Spaces { @Test func double() { #expect(count(\"a  b\") == 2) } }\n";
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + newTests);
        var passing = CommitAndPush("fix", "factory/sc-5");

        var pass = await Check(baseSha, passing);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal(("ran", "ran", "swift-testing"), (pass.BaseRun, pass.HeadRun, pass.Strategy));
        Assert.Equal([("WordsTests.Spaces/double()", "failed", "passed"), ("WordsTests.blank()", "failed", "passed")], pass.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));

        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + newTests + "@Test func one() { #expect(count(\"a\") == 1) }\n");
        var vacuous = CommitAndPush("a test that checks nothing", "factory/sc-5");

        var rejected = await Check(baseSha, vacuous);

        Assert.True(rejected.Outcome == NewTestsOutcome.Rejected, $"{rejected.Outcome}: {rejected.Reason}");
        Assert.EndsWith("so they check nothing: WordsTests.one()", rejected.Reason);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    /// <summary>
    /// sc-26129: a new XCTest method (run with XCTest alone, read from XCTest's own lines — Apple's or Linux's) and a new
    /// parameterized Swift Testing test (one test, all its cases), each failing on the base and passing on the head; a new
    /// method a subclass inherits is judged in the subclass too (macOS: where the gate runs); a skipped XCTest test is no pass.
    /// </summary>
    [Fact]
    public async Task With_swift_a_new_xctest_method_and_a_new_parameterized_test_are_run_alone_and_judged()
    {
        Assert.SkipUnless(HasSwift.Value, "swift is not installed");
        Write("Package.swift", SwiftIdentifyTests.Manifest);
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.BaseLibrary);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests);
        Write(".gitignore", ".build/\n");
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary);
        const string legacy = "import XCTest\n@testable import Words\n\nclass LegacyTests: XCTestCase {\n    func testBlank() throws { XCTAssertEqual(count(\"\"), 0) }\n}\n";
        Write("Tests/WordsTests/LegacyTests.swift", legacy + (OperatingSystem.IsMacOS() ? "final class MoreTests: LegacyTests {}\n" : ""));
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + "@Test(arguments: [\"\", \"  \"]) func blanks(text: String) { #expect(count(text) == 0) }\n");
        var passing = CommitAndPush("fix", "factory/sc-5");

        var pass = await Check(baseSha, passing);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal(
            new[] { ("WordsTests.LegacyTests/testBlank", "failed", "passed"), ("WordsTests.blanks(text:)", "failed", "passed") }
                .Concat(OperatingSystem.IsMacOS() ? [("WordsTests.MoreTests/testBlank", "failed", "passed")] : []).OrderBy(t => t.Item1, StringComparer.Ordinal),
            pass.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.All(pass.Tests, t => Assert.Equal((1, 1), (t.BaseCases.Count, t.HeadCases.Count)));

        Write("Tests/WordsTests/LegacyTests.swift", legacy.Replace("    func testBlank()",
            "    func testOne() { XCTAssertEqual(count(\"a\"), 1) }\n    func testLater() throws { throw XCTSkip(\"not yet\") }\n    func testBlank()"));
        var vacuous = CommitAndPush("a test that checks nothing and one that skips", "factory/sc-5");

        var rejected = await Check(baseSha, vacuous);

        Assert.True(rejected.Outcome == NewTestsOutcome.Rejected, $"{rejected.Outcome}: {rejected.Reason}");
        Assert.Contains("so they check nothing: WordsTests.LegacyTests/testOne", rejected.Reason);
        Assert.Contains("new test(s) are skipped on the base", rejected.Reason);
        Assert.Contains("WordsTests.LegacyTests/testLater (skipped)", rejected.Reason);
    }
}
