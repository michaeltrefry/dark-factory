using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>The hand-written iOS app the sc-26132 tests use (Fixtures/xcode-ios): an app target, an XCTest unit-test target it hosts and a UI-test target.</summary>
internal static class XcodeIosFixture
{
    public const string Pbxproj = "Tally.xcodeproj/project.pbxproj";
    public const string Scheme = "Tally.xcodeproj/xcshareddata/xcschemes/Tally.xcscheme";
    public const string Counter = "Tally/Counter.swift";

    /// <summary>The fix the head makes: the button adds one.</summary>
    public const string Fixed = "enum Counter {\n    static let start = 0\n\n    static func next(_ value: Int) -> Int { value + 1 }\n}\n";

    public const string UnitTestFile = "TallyTests/IncrementTests.swift";

    public const string UnitTest = "import XCTest\n@testable import Tally\n\nfinal class IncrementTests: XCTestCase {\n    func testNextAddsOne() {\n        XCTAssertEqual(Counter.next(41), 42)\n    }\n}\n";

    public const string UnitTestId = "TallyTests/IncrementTests/testNextAddsOne()";

    public const string UiTestFile = "TallyUITests/IncrementUITests.swift";

    public const string UiTest = "import XCTest\n\nfinal class IncrementUITests: XCTestCase {\n    func testTappingIncrementShowsOne() {\n"
        + "        let app = XCUIApplication()\n        app.launch()\n        let label = app.staticTexts[\"count\"]\n"
        + "        XCTAssertTrue(label.waitForExistence(timeout: 30))\n        app.buttons[\"increment\"].tap()\n"
        + "        XCTAssertEqual(label.label, \"Count: 1\")\n    }\n}\n";

    public const string UiTestId = "TallyUITests/IncrementUITests/testTappingIncrementShowsOne()";

    private static readonly string[] Paths = [Pbxproj, Scheme, "Tally/Tally.swift", Counter, "TallyTests/TallyTests.swift", "TallyUITests/TallyUITests.swift"];

    public static string Read(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xcode-ios", path));

    public static (string Path, string Content)[] Files(Func<string, string>? project = null) =>
        [.. Paths.Select(p => (p, p == Pbxproj && project is not null ? project(Read(p)) : Read(p)))];

    /// <summary>The head: the fix, a new unit test and a new UI test.</summary>
    public static (string Path, string? Content)[] Head => [(Counter, Fixed), (UnitTestFile, UnitTest), (UiTestFile, UiTest)];
}

/// <summary>
/// sc-26132: an iOS app's unit-test and UI-test targets run on a simulator destination — which platform a target is tested on,
/// the steps (the run's own simulator, one helper run for everything that needs it booted), the session's results, and E7.
/// </summary>
public class XcodeIosTests
{
    private static readonly RepoRef Repo = new("acme", "tally");
    private static readonly XcodeNewTests Strategy = new();

    private static FakeTestRunner Runner(Func<string, string>? project = null, params (string Path, string? Content)[] head)
    {
        var runner = CargoIdentifyTests.Runner(XcodeIosFixture.Files(project));
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        return CargoIdentifyTests.With(runner, head.Length == 0 ? XcodeIosFixture.Head : head);
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner, bool swift = true) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(swift), Repo, FakeTestRunner.BaseSha, "head1", "gate-sc-1", null, CancellationToken.None);

    [Theory]
    [InlineData("SDKROOT = macosx;", false, "macOS", null, null)]
    [InlineData("SDKROOT = iphoneos;", false, "iOS", null, null)]
    [InlineData("SDKROOT = iphonesimulator;", true, "iOS", null, null)]
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator\";", false, "iOS", null, null)]
    // Both: a unit-test target on macOS (no simulator needed), a UI-test target on iOS (no macOS UI tests) — and the note says which.
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator macosx\";", false, "macOS", null, "builds for macOS and iOS: the gate runs its tests on macOS")]
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator macosx\";", true, "iOS", null, "builds for macOS and iOS: its UI tests run on an iOS Simulator")]
    [InlineData("SDKROOT = macosx;", true, null, "it is a macOS UI-test target: the gate runs no macOS UI tests", null)]
    [InlineData("SDKROOT = appletvos;", false, null, "it builds for the appletvos SDK: the gate runs macOS and iOS Simulator tests only", null)]
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = xros;", false, null, "it builds for xros only", null)]
    [InlineData("", false, null, "set neither by the target nor by the project", null)]
    public void Where_a_test_target_runs_is_read_from_its_sdk_and_its_kind(string settings, bool ui, string? platform, string? why, string? note)
    {
        var project = XcodeProject.Parse("Tally.xcodeproj", XcodeIosFixture.Read(XcodeIosFixture.Pbxproj).Replace("\t\t\t\tSDKROOT = iphoneos;\n", "\t\t\t\t" + settings + "\n"));

        var (actual, reason, said) = project.TestPlatform(project.Targets.Single(t => t.Name == (ui ? "TallyUITests" : "TallyTests")), "Debug");

        Assert.Equal(platform, actual);
        if (why is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(why, reason);
        }
        if (note is null)
        {
            Assert.Null(said);
        }
        else
        {
            Assert.Contains(note, said);
        }
    }

    [Fact]
    public async Task An_ios_apps_new_unit_test_and_new_ui_test_are_found_and_run_on_a_simulator()
    {
        var runner = Runner();

        var result = await Check(runner);

        Assert.True(result.Passed, result.Reason);
        Assert.Equal([(XcodeIosFixture.UnitTestId, TestCaseResult.Failed, TestCaseResult.Passed), (XcodeIosFixture.UiTestId, TestCaseResult.Failed, TestCaseResult.Passed)],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        // Only the test files are applied to the base: the app's code stays the base's.
        Assert.Equal([XcodeIosFixture.UnitTestFile, XcodeIosFixture.UiTestFile], runner.Runs[0].OverlayPaths.Order(StringComparer.Ordinal));
        // Both runs on the run's own simulator, never this Mac.
        Assert.All(runner.Runs, run => Assert.DoesNotContain(run.Steps, s => s.Args.Contains(XcodeNewTests.Destination)));
    }

    [Fact]
    public async Task The_steps_prepare_the_runs_own_simulator_build_for_it_and_run_every_test_in_one_session_then_remove_it()
    {
        var runner = Runner();
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, "head1", CancellationToken.None);
        var set = await Strategy.IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, "head1"), changes, CancellationToken.None);
        Assert.Empty(set.Unisolable);
        Assert.Equal("-project\nTally.xcodeproj\nTally\niOS", set.Projects[XcodeIosFixture.UnitTestId]);

        var steps = await Strategy.StepsAsync(null!, "c", set.Tests, set.Projects, ".factory-test-results-x", CancellationToken.None);

        var wrapper = SwiftToolchain.DefaultWrapperPath;
        var device = XcodeNewTests.DeviceName(".factory-test-results-x");
        Assert.Matches("^dark-factory-gate-[0-9a-f]{16}$", device);
        Assert.NotEqual(device, XcodeNewTests.DeviceName(".factory-test-results-y")); // one per run: the results directory is random
        var script = $"<{XcodeNewTests.SimulatorScript.Length} characters>";
        Assert.Equal(
        [
            "Toolchain /usr/bin/xcodebuild -version",
            "Toolchain /usr/bin/xcrun --find xcresulttool",
            $"Restore {wrapper} clean-derived-data",
            $"Restore /bin/bash -c {script} factory-ios-simulator prepare {device} .factory-test-results-x",
            $"Build {wrapper} --gate build-for-testing -project Tally.xcodeproj -scheme Tally -destination platform=iOS Simulator,name={device} -quiet",
            $"Test /bin/bash -c {script} factory-ios-simulator session {device} .factory-test-results-x {wrapper} 720 "
                + $"-project Tally.xcodeproj Tally {XcodeIosFixture.UnitTestId} {Hash(XcodeIosFixture.UnitTestId)} -project Tally.xcodeproj Tally {XcodeIosFixture.UiTestId} {Hash(XcodeIosFixture.UiTestId)}",
            $"Cleanup /bin/bash -c {script} factory-ios-simulator remove {device}",
            $"Cleanup {wrapper} clean-derived-data",
        ], steps.Select(s => $"{s.Phase} {SandboxTestRunner.Describe(s)}"));
        Assert.Equal(XcodeNewTests.SimulatorScript, steps[3].Args[1]);
        // A machine that cannot run a simulator at all is the factory's failure, not the item's (sc-26132); no other step says so.
        Assert.Equal([(int?)null, null, null, XcodeNewTests.SimulatorUnavailableExit, null, null, null, null], steps.Select(s => s.FactoryWideExit));
        var session = steps[5];
        Assert.Equal((XcodeNewTests.SessionOutput, "xcode-test-*.json"), (session.CaptureOutput, session.ResultFilePattern));
        Assert.Equal([XcodeIosFixture.UnitTestId, XcodeIosFixture.UiTestId], FakeTestRunner.Filtered(steps));
    }

    [Fact]
    public void A_runs_fresh_simulator_boots_within_the_runs_own_default_budget()
    {
        // Every run (the base's, the head's, a retry's) creates and boots its own fresh device inside its own Gate:TestTimeoutMinutes
        // (default SandboxTestRunner.DefaultTimeout, 20 min). The deadline is at least twice the slowest first boot seen as a sandbox
        // user (272 s, sc-26126), and leaves 8 minutes of the run's budget for the build and the tests.
        Assert.Equal(TimeSpan.FromMinutes(20), new FactoryOptions(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new InMemorySecrets()).TestTimeout);
        Assert.InRange(XcodeNewTests.BootTimeoutSeconds, 2 * 272, (int)(SandboxTestRunner.DefaultTimeout - TimeSpan.FromMinutes(8)).TotalSeconds);
    }

    private static string Hash(string id) => XcodeNewTests.BundleName(id)["xcode-test-".Length..^".xcresult".Length];

    private const string Passed = """{"testNodes":[{"nodeType":"Unit test bundle","name":"TallyTests","children":[{"nodeType":"Test Case","nodeIdentifier":"IncrementTests/testNextAddsOne()","result":"Passed"}]}]}""";
    private const string UiFailed = """{"testNodes":[{"nodeType":"UI test bundle","name":"TallyUITests","children":[{"nodeType":"Test Case","nodeIdentifier":"IncrementUITests/testTappingIncrementShowsOne()","result":"Failed"}]}]}""";

    private static string Line(string id, int test, int json = 0) => $"factory-ios-simulator: result {Hash(id)} test-exit {test} xcresulttool-exit {json}";

    private static ResultFile Session(int exit, params string[] lines) => new(XcodeNewTests.SessionOutput, string.Join('\n', lines) + "\n") { ExitCode = exit };

    private static ResultFile Json(string id, string json) => new($"xcode-test-{Hash(id)}.json", json);

    [Fact]
    public void A_sessions_lines_give_each_tests_exit_codes_and_its_json_file_its_cases()
    {
        var results = Strategy.ParseResults(
        [
            Session(0, "factory-ios-simulator: booted dark-factory-gate-x U", Line(XcodeIosFixture.UnitTestId, 0), Line(XcodeIosFixture.UiTestId, 65)),
            Json(XcodeIosFixture.UnitTestId, Passed),
            Json(XcodeIosFixture.UiTestId, UiFailed),
        ]);

        Assert.Empty(results.Unreadable);
        Assert.Equal([new TestCaseResult(XcodeIosFixture.UnitTestId, TestCaseResult.Passed)], results.Cases[XcodeIosFixture.UnitTestId]);
        Assert.Equal([new TestCaseResult(XcodeIosFixture.UiTestId, TestCaseResult.Failed)], results.Cases[XcodeIosFixture.UiTestId]);
    }

    [Fact]
    public void A_recorded_ios_ui_test_bundle_is_read_as_its_targets_test()
    {
        // Xcode 26.5's xcresulttool on an iPhone 17 simulator (iOS 26.5), from the fixture app's UI test (the device id scrubbed).
        const string id = "TallyUITests/TallyUITests/testLaunchShowsZero()";

        var results = Strategy.ParseResults([Session(0, Line(id, 0)), Json(id, XcodeFixture.Json("ios-ui-passed.json"))]);

        Assert.Empty(results.Unreadable);
        Assert.Equal([new TestCaseResult(id, TestCaseResult.Passed)], results.Cases[id]);
    }

    [Fact]
    public void A_failed_session_a_json_the_session_did_not_report_and_a_report_without_its_json_are_unreadable()
    {
        var failed = Strategy.ParseResults(
        [
            Session(70, "factory-ios-simulator: error: the simulator dark-factory-gate-x (U) did not boot within 600 s"),
        ]);
        Assert.Empty(failed.Cases);
        Assert.Equal(
            $"{XcodeNewTests.SessionOutput}: the iOS simulator session failed (exit 70): factory-ios-simulator: error: the simulator dark-factory-gate-x (U) did not boot within 600 s",
            Assert.Single(failed.Unreadable));

        var stray = Strategy.ParseResults([Session(0, Line(XcodeIosFixture.UnitTestId, 0)), Json(XcodeIosFixture.UiTestId, UiFailed)]);
        Assert.Empty(stray.Cases);
        Assert.Contains(stray.Unreadable, u => u.StartsWith($"xcode-test-{Hash(XcodeIosFixture.UiTestId)}.json: it is not the output of a test step", StringComparison.Ordinal));
        Assert.Contains(stray.Unreadable, u => u.Contains("its test step (exit 0) left no result bundle read"));

        // The exit codes must agree with the cases, as for a macOS test step; xcresulttool must have exited 0.
        Assert.Contains("xcodebuild exited 0, which does not agree with its 1 failing case(s)",
            Assert.Single(Strategy.ParseResults([Session(0, Line(XcodeIosFixture.UiTestId, 0)), Json(XcodeIosFixture.UiTestId, UiFailed)]).Unreadable));
        Assert.Contains("xcresulttool exited 1",
            Assert.Single(Strategy.ParseResults([Session(0, Line(XcodeIosFixture.UnitTestId, 0, 1)), Json(XcodeIosFixture.UnitTestId, Passed)]).Unreadable));
        // One test reported twice: never read.
        Assert.Contains(Strategy.ParseResults([Session(0, Line(XcodeIosFixture.UnitTestId, 0), Line(XcodeIosFixture.UnitTestId, 0)), Json(XcodeIosFixture.UnitTestId, Passed)]).Unreadable,
            u => u.Contains("twice"));
    }

    [Fact]
    public async Task A_refused_simulator_is_an_error_that_says_why()
    {
        const string refusal = "factory-ios-simulator: error: the simulator dark-factory-gate-0123456789abcdef (U) is not in this user's own device set: no directory /Users/_factory/Library/Developer/CoreSimulator/Devices/U";
        var runner = Runner();
        runner.Answer = _ => TestRunReport.Failed(TestRunStatus.RestoreFailed, $"$ /bin/bash -c <9000 characters> factory-ios-simulator prepare d .r → exit 70\n{refusal}");

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("could not be tested (restore-failed)", result.Reason);
        Assert.Contains(refusal, result.Reason);
    }

    [Theory]
    // A UI-test target of macOS: still never run (XCUITest on macOS needs automation mode and an Accessibility grant).
    [InlineData("SDKROOT = iphoneos;", "SDKROOT = macosx;", "TallyUITests of Tally.xcodeproj cannot be tested by the gate in its scheme's Debug configuration: it is a macOS UI-test target")]
    [InlineData("SDKROOT = iphoneos;", "SDKROOT = watchos;", "cannot be tested by the gate in its scheme's Debug configuration: it builds for the watchos SDK")]
    public async Task A_macos_ui_test_target_and_other_platforms_are_unsupported(string from, string to, string why)
    {
        var runner = Runner(p => p.Replace(from, to));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
    }

    [Fact]
    public async Task A_target_tested_on_ios_at_the_head_and_macos_at_the_base_is_unsupported()
    {
        var runner = Runner();
        var head = runner.HeadFiles;
        runner.BaseFiles[XcodeIosFixture.Pbxproj] = runner.BaseFiles[XcodeIosFixture.Pbxproj].Replace("SDKROOT = iphoneos;", "SDKROOT = macosx;");
        runner.HeadFiles = sha => new Dictionary<string, string>(head(sha)) { [XcodeIosFixture.Pbxproj] = XcodeIosFixture.Read(XcodeIosFixture.Pbxproj) };

        var result = await Check(runner, swift: true);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("the test target TallyTests of Tally.xcodeproj is tested on iOS at the head but on macOS at the base", result.Reason);
    }

    [Fact]
    public async Task A_unit_test_target_building_for_both_is_tested_on_macos_and_the_result_says_so()
    {
        var runner = Runner(p => p.Replace("SDKROOT = iphoneos;", "SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator macosx\";"),
            (XcodeIosFixture.Counter, XcodeIosFixture.Fixed), (XcodeIosFixture.UnitTestFile, XcodeIosFixture.UnitTest));

        var result = await Check(runner);

        Assert.True(result.Passed, result.Reason);
        Assert.EndsWith("(TallyTests builds for macOS and iOS: the gate runs its tests on macOS)", result.Reason);
        Assert.Contains(runner.Runs[0].Steps, s => s.Args.Contains(XcodeNewTests.Destination));
        Assert.DoesNotContain(runner.Runs[0].Steps, s => s.Program == XcodeNewTests.Bash);
    }

    [Fact]
    public async Task With_worker_swift_off_an_ios_apps_tests_are_unsupported()
    {
        var result = await Check(Runner(), swift: false);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("only in a factory with Worker:Swift on", result.Reason);
    }

    [Fact]
    public void A_steps_long_argument_is_shown_as_its_length_in_the_log()
    {
        var step = new TestStep(TestPhase.Test, "/bin/bash", ["-c", new string('x', 201), "short"]);

        Assert.Equal("/bin/bash -c <201 characters> short", SandboxTestRunner.Describe(step));
    }
}
