using System.Diagnostics;
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
            $"Restore /bin/bash -c {script} factory-ios-simulator prepare {device} 600",
            $"Build {wrapper} --gate build-for-testing -project Tally.xcodeproj -scheme Tally -destination platform=iOS Simulator,name={device} -quiet",
            $"Test /bin/bash -c {script} factory-ios-simulator session {device} .factory-test-results-x {wrapper} 600 "
                + $"-project Tally.xcodeproj Tally {XcodeIosFixture.UnitTestId} {Hash(XcodeIosFixture.UnitTestId)} -project Tally.xcodeproj Tally {XcodeIosFixture.UiTestId} {Hash(XcodeIosFixture.UiTestId)}",
            $"Cleanup /bin/bash -c {script} factory-ios-simulator remove {device}",
            $"Cleanup {wrapper} clean-derived-data",
        ], steps.Select(s => $"{s.Phase} {SandboxTestRunner.Describe(s)}"));
        Assert.Equal(XcodeNewTests.SimulatorScript, steps[3].Args[1]);
        var session = steps[5];
        Assert.Equal((XcodeNewTests.SessionOutput, "xcode-test-*.json"), (session.CaptureOutput, session.ResultFilePattern));
        Assert.Equal([XcodeIosFixture.UnitTestId, XcodeIosFixture.UiTestId], FakeTestRunner.Filtered(steps));
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
        const string refusal = "factory-ios-simulator: refusing iOS simulator work: launchctl managername is 'Background', not 'Aqua'. The factory must run inside the owner's GUI login session";
        var runner = Runner();
        runner.Answer = _ => TestRunReport.Failed(TestRunStatus.RestoreFailed, $"$ /bin/bash -c <9000 characters> factory-ios-simulator prepare d 600 → exit 69\n{refusal}");

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

/// <summary>
/// sc-26132: <c>scripts/factory-ios-simulator</c>, run as the current user on a COPY whose <c>launchctl=</c> and <c>xcrun=</c> lines
/// name stubs (a fake device set in a file) — never the real simulator, never a sandbox user.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class IosSimulatorScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-ios-sim-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Devices => Path.Combine(_dir, "devices");

    private string Calls => Path.Combine(_dir, "calls");

    private string Script(string manager = "Aqua")
    {
        var launchctl = SandboxSupport.Executable(_dir, "stub-launchctl", $"#!/bin/sh\n[ \"$1\" = managername ] && echo {manager}\n");
        // simctl over a "name udid" per line file; xcresulttool prints a document. Every call is recorded.
        var xcrun = SandboxSupport.Executable(_dir, "stub-xcrun", $$"""
            #!/bin/bash
            echo "$*" >> '{{Calls}}'
            devices='{{Devices}}'
            touch "$devices"
            new_udid() { printf '%04X%04X-0000-4000-8000-%04X%04X%04X\n' $RANDOM $RANDOM $RANDOM $RANDOM $RANDOM; }
            case "$1 $2" in
                "simctl list")
                    case "$3" in
                        runtimes) echo "== Runtimes =="; [ -n "${STUB_NO_RUNTIME:-}" ] || echo "iOS 26.5 (26.5 - 23F77) - com.apple.CoreSimulator.SimRuntime.iOS-26-5" ;;
                        devicetypes) echo "iPhone 17 Pro (com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro)"; echo "iPhone 17 (com.apple.CoreSimulator.SimDeviceType.iPhone-17)" ;;
                        devices) echo "== Devices =="; echo "-- iOS 26.5 --"; while read -r name udid; do echo "    $name ($udid) (Shutdown) "; done < "$devices" ;;
                    esac ;;
                "simctl create") u=$(new_udid); echo "$3 $u" >> "$devices"; echo "$u" ;;
                "simctl clone") u=$(new_udid); echo "$4 $u" >> "$devices"; echo "$u" ;;
                "simctl delete") grep -v " $3\$" "$devices" > "$devices.new"; mv "$devices.new" "$devices" ;;
                "simctl bootstatus") [ -z "${STUB_BOOT_HANG:-}" ] || sleep 30 ;;
                "simctl boot" | "simctl shutdown") ;;
                "xcresulttool get") echo '{"testNodes":[]}' ;;
                *) exit 9 ;;
            esac
            """);
        var text = XcodeNewTests.SimulatorScript; // what the gate runs: the repo's script, compiled in
        Assert.Single(text.Split('\n'), l => l == "launchctl=/bin/launchctl");
        Assert.Single(text.Split('\n'), l => l == "xcrun=/usr/bin/xcrun");
        return SandboxSupport.Executable(_dir, $"factory-ios-simulator-{Guid.NewGuid():N}",
            text.Replace("\nlaunchctl=/bin/launchctl\n", $"\nlaunchctl={launchctl}\n").Replace("\nxcrun=/usr/bin/xcrun\n", $"\nxcrun={xcrun}\n"));
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string script, string[] args, Dictionary<string, string>? env = null)
    {
        // As the gate runs it: bash -c <text> factory-ios-simulator <command> …
        var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", File.ReadAllText(script), "factory-ios-simulator" }.Concat(args))
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in env ?? [])
        {
            psi.Environment[key] = value;
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout, await stderr);
    }

    private string[] DeviceLines => File.Exists(Devices) ? File.ReadAllLines(Devices) : [];

    private const string Run = "dark-factory-gate-0123456789abcdef";

    [Theory]
    [InlineData("prepare", new[] { Run, "600" })]
    [InlineData("session", new[] { Run, "results", "/nonexistent/wrapper", "600" })]
    public async Task Outside_a_gui_login_session_simulator_work_is_refused_naming_why_and_nothing_is_touched(string command, string[] args)
    {
        var (exit, _, stderr) = await RunAsync(Script(manager: "Background"), [command, .. args]);

        Assert.Equal(69, exit);
        Assert.Contains("refusing iOS simulator work: launchctl managername is 'Background', not 'Aqua'.", stderr);
        Assert.Contains("The factory must run inside the owner's GUI login session", stderr);
        Assert.Contains("for the gate's iOS simulator checks", stderr);
        Assert.False(File.Exists(Calls), "simctl was run");
    }

    [Fact]
    public async Task Without_an_ios_runtime_prepare_fails_naming_it()
    {
        var (exit, _, stderr) = await RunAsync(Script(), ["prepare", Run, "600"], new() { ["STUB_NO_RUNTIME"] = "1" });

        Assert.Equal(69, exit);
        Assert.Contains("no iOS Simulator runtime is installed for this user", stderr);
    }

    [Fact]
    public async Task Prepare_keeps_one_template_clones_the_runs_device_and_deletes_only_what_earlier_runs_left()
    {
        File.WriteAllLines(Devices,
        [
            "dark-factory-gate-aaaaaaaaaaaaaaaa 11111111-0000-4000-8000-000000000001", // an earlier run's device
            "dark-factory-gate-template-iOS-25-0 11111111-0000-4000-8000-000000000002", // a template of a runtime gone
            "Mine 11111111-0000-4000-8000-000000000003",                                 // not the gate's: left alone
        ]);
        var script = Script();

        var (exit, stdout, stderr) = await RunAsync(script, ["prepare", Run, "600"]);

        Assert.True(exit == 0, stderr);
        Assert.Contains($"factory-ios-simulator: device {Run} ", stdout);
        Assert.Equal(["Mine", "dark-factory-gate-template-iOS-26-5", Run], DeviceLines.Select(l => l.Split(' ')[0]));
        var calls = File.ReadAllLines(Calls);
        Assert.Contains(calls, c => c == "simctl create dark-factory-gate-template-iOS-26-5 com.apple.CoreSimulator.SimDeviceType.iPhone-17 com.apple.CoreSimulator.SimRuntime.iOS-26-5");
        Assert.Contains(calls, c => c.StartsWith("simctl bootstatus ", StringComparison.Ordinal)); // the template's one first boot

        // The next run reuses the template: no create, no boot; the earlier run's device is swept.
        File.Delete(Calls);
        (exit, _, stderr) = await RunAsync(script, ["prepare", "dark-factory-gate-fedcba9876543210", "600"]);
        Assert.True(exit == 0, stderr);
        Assert.Equal(["Mine", "dark-factory-gate-template-iOS-26-5", "dark-factory-gate-fedcba9876543210"], DeviceLines.Select(l => l.Split(' ')[0]));
        Assert.DoesNotContain(File.ReadAllLines(Calls), c => c.StartsWith("simctl create", StringComparison.Ordinal) || c.StartsWith("simctl boot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_session_boots_runs_each_test_alone_on_the_device_reads_its_bundle_and_shuts_down()
    {
        File.WriteAllLines(Devices, [$"{Run} 22222222-0000-4000-8000-000000000001"]);
        var wrapperCalls = Path.Combine(_dir, "wrapper-calls");
        var wrapper = SandboxSupport.Executable(_dir, "stub-wrapper", $"#!/bin/bash\necho \"$*\" >> '{wrapperCalls}'\ncase \"$*\" in *b\\(\\)*) exit 65 ;; esac\n");

        var (exit, stdout, stderr) = await RunAsync(Script(),
            ["session", Run, "results", wrapper, "600", "-project", "A.xcodeproj", "A", "ATests/a()", "00000000000000aa", "-workspace", "W.xcworkspace", "W", "AUITests/C/b()", "00000000000000bb"]);

        Assert.True(exit == 0, stderr);
        Assert.Equal(
        [
            "factory-ios-simulator: booted dark-factory-gate-0123456789abcdef 22222222-0000-4000-8000-000000000001",
            "factory-ios-simulator: result 00000000000000aa test-exit 0 xcresulttool-exit 0",
            "factory-ios-simulator: result 00000000000000bb test-exit 65 xcresulttool-exit 0",
        ], stdout.TrimEnd('\n').Split('\n'));
        Assert.Equal(
        [
            "--gate test-without-building -project A.xcodeproj -scheme A -destination platform=iOS Simulator,id=22222222-0000-4000-8000-000000000001 -only-testing:ATests/a() -resultBundlePath results/xcode-test-00000000000000aa.xcresult -quiet",
            "--gate test-without-building -workspace W.xcworkspace -scheme W -destination platform=iOS Simulator,id=22222222-0000-4000-8000-000000000001 -only-testing:AUITests/C/b() -resultBundlePath results/xcode-test-00000000000000bb.xcresult -quiet",
        ], File.ReadAllLines(wrapperCalls));
        Assert.Equal("{\"testNodes\":[]}\n", File.ReadAllText(Path.Combine(_dir, "results", "xcode-test-00000000000000aa.json")));
        Assert.Equal("simctl shutdown 22222222-0000-4000-8000-000000000001", File.ReadAllLines(Calls)[^1]);
    }

    [Fact]
    public async Task A_simulator_that_does_not_boot_in_time_fails_the_session_and_runs_no_test()
    {
        File.WriteAllLines(Devices, [$"{Run} 22222222-0000-4000-8000-000000000001"]);

        var (exit, stdout, stderr) = await RunAsync(Script(), ["session", Run, "results", "/nonexistent/wrapper", "1", "-project", "A.xcodeproj", "A", "ATests/a()", "00000000000000aa"],
            new() { ["STUB_BOOT_HANG"] = "1" });

        Assert.Equal(70, exit);
        Assert.Contains($"factory-ios-simulator: error: the simulator {Run} (22222222-0000-4000-8000-000000000001) did not boot within 1 s", stderr);
        Assert.DoesNotContain("result", stdout);
    }

    [Theory]
    [InlineData("prepare", "Mine", "refusing the device name 'Mine'")]
    [InlineData("remove", "dark-factory-gate-template-iOS-26-5", "refusing the device name")]
    [InlineData("session", "dark-factory-gate-0123456789abcdef", "the device dark-factory-gate-0123456789abcdef does not exist")]
    public async Task Only_the_runs_own_existing_device_is_used_or_removed(string command, string device, string why)
    {
        File.WriteAllLines(Devices, ["Mine 11111111-0000-4000-8000-000000000003", "dark-factory-gate-template-iOS-26-5 11111111-0000-4000-8000-000000000004"]);
        string[] args = command switch
        {
            "prepare" => [command, device, "600"],
            "session" => [command, device, "results", "/nonexistent/wrapper", "600"],
            _ => [command, device],
        };

        var (exit, _, stderr) = await RunAsync(Script(), args);

        Assert.NotEqual(0, exit);
        Assert.Contains(why, stderr);
        Assert.Equal(2, DeviceLines.Length);
    }

    [Fact]
    public async Task Remove_deletes_the_runs_device()
    {
        File.WriteAllLines(Devices, ["Mine 11111111-0000-4000-8000-000000000003", $"{Run} 22222222-0000-4000-8000-000000000001"]);

        var (exit, _, stderr) = await RunAsync(Script(), ["remove", Run]);

        Assert.True(exit == 0, stderr);
        Assert.Equal(["Mine 11111111-0000-4000-8000-000000000003"], DeviceLines);
    }
}
