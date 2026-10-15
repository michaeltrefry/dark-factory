using System.Text;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26134 (feature-end fixes, round 2): an Xcode project's base that does not build with the PR's test files, macOS and iOS,
/// explained by SwiftPM's rules (S2) — not-built, error and retries — from xcodebuild's real output: Fixtures/xcode-build holds
/// the standard output of <c>factory-xcodebuild --gate build-for-testing … -quiet</c> recorded with Xcode 26.5 (17F42) from the
/// fixture projects (the worktree's path and the owner's home scrubbed), each test file below exactly as it was built.
/// </summary>
public class XcodeBaseBuildTests
{
    private static readonly RepoRef Repo = new("acme", "app");

    /// <summary>The worktree the outputs were recorded in, as the scrubbed fixtures name it.</summary>
    private const string Root = "/opt/dark-factory/work/worktrees/acme/app/gate-sc-1-base";

    private static readonly XcodeNewTests Strategy = new();

    private static string Recorded(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xcode-build", name + ".txt"));

    private static IReadOnlyList<BuildError> Errors(string name) => Strategy.ParseBuildErrors(Recorded(name), [Root]);

    // ---- The test files as the recordings built them -------------------------------------------------------------------

    private const string CountFile = "WordsTests/CountTests.swift";

    /// <summary>mac-missing-function: one test calls a function the base lacks, one only what the base has.</summary>
    private const string CountTests = """
        import Testing
        @testable import Words

        struct CountTests {
            @Test func counts() {
                #expect(wordCount("a b") == 2)
            }

            @Test func blank() {
                #expect(isBlank(" "))
            }
        }

        """;

    /// <summary>mac-stages: two types whose headers the base cannot build (the first stage), a free test, and — added after the recording, below every recorded error — a helper the second stage names.</summary>
    private const string StagedTests = """
        import Testing
        @testable import Words

        extension Counter {
            @Test func counts() { #expect(count() == 2) }
        }

        struct Shape: Sized {
            @Test func sized() { #expect(true) }
        }

        @Test func plain() { #expect(isBlank(" ")) }
        func padded() -> String { padding(1) }

        """;

    /// <summary>mac-production (with Words/Words.swift broken on the base).</summary>
    private const string BlankTests = "import Testing\n@testable import Words\n\n@Test func blank() { #expect(isBlank(\" \")) }\n";

    private const string IncrementFile = "TallyTests/IncrementTests.swift";

    /// <summary>ios-missing-function: XCTest; one method calls a function the base lacks.</summary>
    private const string IncrementTests = """
        import XCTest
        @testable import Tally

        final class IncrementTests: XCTestCase {
            func testNextAddsOne() {
                XCTAssertEqual(Counter.next(41), 42)
            }

            func testPreviousTakesOne() {
                XCTAssertEqual(Counter.previous(42), 41)
            }
        }

        """;

    private const string CounterWithPrevious = "enum Counter {\n    static let start = 0\n\n    static func next(_ value: Int) -> Int { value + 1 }\n\n    static func previous(_ value: Int) -> Int { value - 1 }\n}\n";

    private static FakeTestRunner Mac(params (string Path, string? Content)[] head) =>
        XcodeIdentifyTests.Runner(head: [(XcodeFixture.Library, XcodeFixture.Fixed), ("Words/Count.swift", "public func wordCount(_ s: String) -> Int { s.split(separator: \" \").count }\n"), .. head]);

    private static FakeTestRunner Ios(params (string Path, string? Content)[] head) =>
        CargoIdentifyTests.With(CargoIdentifyTests.Runner(XcodeIosFixture.Files()), [(XcodeIosFixture.Counter, CounterWithPrevious), .. head]);

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(true), Repo, FakeTestRunner.BaseSha, "head1", "gate-sc-1", null, CancellationToken.None);

    private static string Stage(TestRunSpec spec) => spec.Name["gate-sc-1-".Length..];

    private static TestRunReport Passing(TestRunSpec spec) => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);

    private static TestRunReport Failing(TestRunSpec spec) => FakeTestRunner.Report(spec.Steps, TestCaseResult.Failed);

    /// <summary>swiftc's error at <paramref name="text"/> in <paramref name="path"/> as the run built it (the head's, or the retry's replacement).</summary>
    private static BuildError At(TestRunSpec spec, string path, string head, string text, string message)
    {
        var content = spec.Replacements.TryGetValue(path, out var replaced) ? replaced : head;
        var offset = content.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"{text} is not in the run's {path}");
        var lineStart = content.LastIndexOf('\n', Math.Max(offset - 1, 0)) is var nl and >= 0 && offset > 0 ? nl + 1 : 0;
        return new BuildError(path, content.AsSpan(0, offset).Count('\n') + 1, Encoding.UTF8.GetByteCount(content.AsSpan(lineStart, offset - lineStart)) + 1, "swiftc", message);
    }

    // ---- Reading xcodebuild's output ----------------------------------------------------------------------------------

    public static TheoryData<string, BuildError[]> RecordedOutputs => new()
    {
        // swiftc's located errors: the path made relative to the worktree; the "Failed frontend command:" line and the
        // compiler invocation after it, the excerpt lines and the notes are not errors.
        { "mac-missing-function", [new BuildError(CountFile, 6, 17, "swiftc", "cannot find 'wordCount' in scope")] },
        { "mac-stages", [new BuildError(CountFile, 4, 11, "swiftc", "cannot find type 'Counter' in scope"), new BuildError(CountFile, 8, 15, "swiftc", "cannot find type 'Sized' in scope")] },
        { "mac-production", [new BuildError("Words/Words.swift", 4, 31, "swiftc", "cannot find 'undefinedThing' in scope")] },
        { "ios-missing-function", [new BuildError(IncrementFile, 10, 32, "swiftc", "type 'Counter' has no member 'previous'")] },
        { "ios-production", [new BuildError("Tally/Counter.swift", 2, 24, "swiftc", "cannot find 'zero' in scope")] },
        // What names no file of the worktree: the linker, a missing build input (printed twice: read once), a failed run-script phase.
        { "mac-link", [new BuildError(null, 0, 0, "swiftc", $"{Root}/Words.xcodeproj: WordsTests: clang: error: linker command failed with exit code 1 (use -v to see invocation)")] },
        { "mac-missing-input", [new BuildError(null, 0, 0, "swiftc", $"Build input file cannot be found: '{Root}/WordsTests/WordsTests.swift'. Did you forget to declare this file as an output of a script phase or custom build rule which produces it? (in target 'WordsTests' from project 'Words')")] },
        { "mac-script-phase", [new BuildError(null, 0, 0, "xcodebuild", "Command PhaseScriptExecution failed with a nonzero exit code")] },
    };

    [Theory]
    [MemberData(nameof(RecordedOutputs))]
    public void Xcodes_recorded_build_output_is_read_into_its_errors(string recording, BuildError[] expected)
    {
        Assert.Equal(expected, Errors(recording));
    }

    [Fact]
    public void An_error_xcode_prints_under_the_standardized_path_of_a_worktree_under_private_is_placed_in_it()
    {
        // Xcode prints /tmp/… for a worktree whose real path is /private/tmp/… (recorded: mac-missing-function was built in /private/tmp).
        var real = SandboxTestRunner.RealPath(Path.GetTempPath());
        Assert.SkipUnless(real.StartsWith("/private/", StringComparison.Ordinal), "the temp directory is not under /private");
        var root = Path.Combine(real, "gate-sc-1-base");
        var log = Recorded("mac-missing-function").Replace(Root, root["/private".Length..]);

        Assert.Equal([new BuildError(CountFile, 6, 17, "swiftc", "cannot find 'wordCount' in scope")], Strategy.ParseBuildErrors(log, [root]));
    }

    // ---- Explaining a base that does not build -------------------------------------------------------------------------

    [Fact]
    public async Task On_macos_a_new_test_calling_code_the_base_lacks_is_not_built_and_the_other_gets_its_real_result_from_a_retry()
    {
        var runner = Mac((CountFile, CountTests));
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt([.. Errors("mac-missing-function")]),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("WordsTests/CountTests/blank()", "failed", "passed"), ("WordsTests/CountTests/counts()", "not-built", "passed")],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)));
        Assert.Equal(("build-failed", "ran"), (result.BaseRun, result.BaseRetry));
        Assert.Equal([$"{CountFile}:5 func counts"], result.BaseRemoved);
        var retry = runner.Runs.Single(r => Stage(r) == "base-retry");
        Assert.Equal(["WordsTests/CountTests/blank()"], FakeTestRunner.Filtered(retry.Steps));
        Assert.DoesNotContain("wordCount", retry.Replacements[CountFile]);
        Assert.Contains("@Test func blank()", retry.Replacements[CountFile]);
    }

    [Fact]
    public async Task Swiftcs_stages_are_followed_through_three_retries_type_headers_first_then_a_helper()
    {
        var runner = Mac(("Words/Counter.swift", "public struct Counter { public init() {} }\n"), (CountFile, StagedTests));
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt([.. Errors("mac-stages")]),
            // Without the two types, the next stage: the helper's body.
            "base-retry" => FakeTestRunner.NotBuilt(At(spec, CountFile, StagedTests, "padding(1)", "cannot find 'padding' in scope")),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("WordsTests/Counter/counts()", "not-built"), ("WordsTests/Shape/sized()", "not-built"), ("WordsTests/plain()", "failed")],
            result.Tests.Select(t => (t.Test, t.Base)));
        var last = runner.Runs.Single(r => Stage(r) == "base-retry-2");
        Assert.Equal(["WordsTests/plain()"], FakeTestRunner.Filtered(last.Steps));
        Assert.Equal("import Testing\n@testable import Words\n\n\n\n\n\n@Test func plain() { #expect(isBlank(\" \")) }\n\n", last.Replacements[CountFile]);
    }

    [Fact]
    public async Task On_ios_a_new_xctest_method_calling_code_the_base_lacks_is_not_built_and_each_retry_is_a_run_with_a_fresh_device()
    {
        var runner = Ios((IncrementFile, IncrementTests));
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt([.. Errors("ios-missing-function")]),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("TallyTests/IncrementTests/testNextAddsOne()", "failed"), ("TallyTests/IncrementTests/testPreviousTakesOne()", "not-built")],
            result.Tests.Select(t => (t.Test, t.Base)));
        Assert.Equal([$"{IncrementFile}:9 func testPreviousTakesOne"], result.BaseRemoved);
        // Each run — the base, its retry, the head — creates its own device and removes it; none is reused.
        var devices = runner.Runs.Select(r => (Stage(r), Device(r, "prepare"), Device(r, "remove"))).ToList();
        Assert.Equal(["base", "head", "base-retry"], devices.Select(d => d.Item1));
        Assert.All(devices, d => Assert.Equal(d.Item2, d.Item3));
        Assert.Equal(3, devices.Select(d => d.Item2).Distinct().Count());
        // The build is its own step, its output kept as the build log (what the explainer reads), between prepare and the session.
        var retry = runner.Runs.Single(r => Stage(r) == "base-retry");
        var build = retry.Steps.Single(s => s.Phase == TestPhase.Build);
        Assert.Equal(("xcode-build-0", "build-for-testing"), (build.CaptureOutput, build.Args[1]));
        Assert.Equal(["TallyTests/IncrementTests/testNextAddsOne()"], FakeTestRunner.Filtered(retry.Steps));
    }

    private static string Device(TestRunSpec spec, string command) =>
        spec.Steps.Single(s => s.Program == XcodeNewTests.Bash && s.Args[3] == command).Args[4];

    public static TheoryData<string, string, string> ProductionBroken => new()
    {
        { "mac-production", "Words/Words.swift(4,31): swiftc cannot find 'undefinedThing' in scope", "mac" },
        { "ios-production", "Tally/Counter.swift(2,24): swiftc cannot find 'zero' in scope", "ios" },
    };

    [Theory]
    [MemberData(nameof(ProductionBroken))]
    public async Task A_base_whose_production_code_does_not_build_is_an_error_with_no_retry(string recording, string named, string platform)
    {
        var runner = platform == "mac" ? Mac(("WordsTests/BlankTests.swift", BlankTests)) : Ios((IncrementFile, XcodeIosFixture.UnitTest));
        runner.Answer = spec => Stage(spec) == "head" ? Passing(spec) : FakeTestRunner.NotBuilt([.. Errors(recording)]);

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Error, $"{result.Outcome}: {result.Reason}");
        Assert.Contains($"for a reason that says nothing about the new tests (not a compiler error in those files): {named}", result.Reason);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Theory]
    [InlineData("mac-script-phase", "Command PhaseScriptExecution failed with a nonzero exit code")]
    [InlineData("mac-link", "clang: error: linker command failed")]
    [InlineData("mac-missing-input", "Build input file cannot be found")]
    public async Task A_new_tests_own_error_beside_one_xcode_reports_outside_the_test_code_is_still_an_error(string recording, string named)
    {
        var runner = Mac((CountFile, CountTests));
        runner.Answer = spec => Stage(spec) == "head" ? Passing(spec) : FakeTestRunner.NotBuilt([.. Errors("mac-missing-function"), .. Errors(recording)]);

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Error, $"{result.Outcome}: {result.Reason}");
        Assert.Contains(named, result.Reason);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Fact]
    public async Task An_error_in_a_test_file_the_pr_did_not_change_is_an_error()
    {
        var runner = Mac((CountFile, CountTests));
        runner.Answer = spec => Stage(spec) == "head" ? Passing(spec)
            : FakeTestRunner.NotBuilt([new BuildError(XcodeFixture.TestFile, 6, 9, "swiftc", "invalid redeclaration of 'notBlank()'"), .. Errors("mac-missing-function")]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains(XcodeFixture.TestFile, result.Reason);
    }
}
