using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26126 (P5 S0 spike): reads the output of <c>Fixtures/ios-probe/probe.sh</c> — a simulator booted and one XCTest unit
/// test and one XCUITest UI test run on it — and names what is missing when a step fails. The live run is
/// <c>LiveWorkerSandboxTests.A_sandbox_user_boots_a_simulator_and_runs_a_unit_test_and_a_ui_test</c>; the owner-side run and
/// the findings are in CLAUDE.md ("iOS simulator and UI tests as a sandbox user").
/// </summary>
internal static partial class SimulatorProbe
{
    /// <summary>The fixture directory as copied next to the test assembly.</summary>
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ios-probe");

    /// <summary>The probe's verdict: <c>unit</c> and <c>ui</c> are <c>pass</c>, <c>fail</c> or <c>not-run</c>.</summary>
    public sealed record Result(string Unit, string Ui, IReadOnlyList<string> Steps, IReadOnlyList<string> Missing);

    /// <summary>Known causes, each a pattern over the probe's whole output and what it says is missing.</summary>
    private static readonly (Regex Pattern, string Missing)[] Causes =
    [
        (new(@"xcodebuild-version FAILED[\s\S]*license", RegexOptions.IgnoreCase),
            "the Xcode license is not accepted (system-wide; the owner runs `sudo xcodebuild -license accept`)"),
        (new(@"first-launch FAILED"),
            "Xcode's first launch is not done (system-wide; the owner runs `sudo xcodebuild -runFirstLaunch`)"),
        (new(@"(CoreSimulatorService|Unable to locate device set|Failed to initialize simulator device set|connection (became invalid|interrupted|refused)|Unable to boot the Simulator launchd|Unable to lookup in current state)", RegexOptions.IgnoreCase),
            "the CoreSimulator service for this user (a per-user launchd service: the user has no launchd bootstrap/session of its own under sudo — `launchctl asuser <uid>` as root, or a login session of that user)"),
        // Only after a test step failed: the probe's own header prints the DevToolsSecurity status on every run.
        (new(@"(build-for-testing|unit-test|ui-test) FAILED[\s\S]*(DevToolsSecurity|_developer|[Aa]uthoriz|Interaction is not allowed|no UI access)"),
            "developer-tools authorization (the user is not in the _developer group and DevToolsSecurity is disabled; enabling either is a system change for the owner)"),
        (new(@"(automation mode|automationmodetool|testmanagerd|Failed to (establish|initialize) (communication|for UI testing)|kAXErrorServerNotFound|UI testing (failed|is not available))", RegexOptions.IgnoreCase),
            "UI automation (XCUITest's testmanagerd / automation mode): typically an Aqua (GUI login) session of this user"),
        (new(@"Permission denied|Operation not permitted"),
            "a file permission (see the failing step's output)"),
        (new(@"probe: timed out after"),
            "a step that did not finish in its time (see which step timed out)"),
        (new(@"no-runtime FAILED"),
            "an iOS simulator runtime or iPhone device type visible to this user (runtimes are system-wide under /Library/Developer/CoreSimulator)"),
    ];

    [GeneratedRegex(@"^probe-result: unit=(\S+) ui=(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^probe: (\S+) (ok|FAILED)\b.*$", RegexOptions.Multiline)]
    private static partial Regex StepLine();

    /// <summary>Parses the probe's output; with no result line (the probe did not run to its end) both are <c>not-run</c>.</summary>
    public static Result Read(string output)
    {
        var result = ResultLine().Match(output);
        var steps = StepLine().Matches(output).Select(m => m.Value.Trim()).ToList();
        var failed = steps.Any(s => s.Contains(" FAILED", StringComparison.Ordinal)) || !result.Success;
        var missing = failed
            ? Causes.Where(c => c.Pattern.IsMatch(output)).Select(c => c.Missing).Distinct().ToList()
            : [];
        return new Result(result.Success ? result.Groups[1].Value : "not-run", result.Success ? result.Groups[2].Value : "not-run", steps, missing);
    }

    /// <summary>One message for a failed probe: the verdict, the steps, what is missing (or that it is unknown), then the output.</summary>
    public static string Explain(string who, int exitCode, string stdout, string stderr)
    {
        var r = Read(stdout);
        var missing = r.Missing.Count == 0
            ? "no known cause matched: read the failing step's output below"
            : string.Join("; ", r.Missing);
        return $"{who}: unit test {r.Unit}, UI test {r.Ui} (exit {exitCode}). Missing: {missing}.\n"
            + $"Steps:\n  {string.Join("\n  ", r.Steps)}\n--- stdout ---\n{Tail(stdout)}\n--- stderr ---\n{Tail(stderr)}";
    }

    private static string Tail(string text) => text.Length <= 12_000 ? text : "…" + text[^12_000..];
}

public class SimulatorProbeTests
{
    [Fact]
    public void The_probe_script_is_valid_bash()
    {
        var psi = new ProcessStartInfo("/bin/bash", ["-n", Path.Combine(SimulatorProbe.FixtureDirectory, "probe.sh")])
        {
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }

    [Fact]
    public void The_fixture_has_the_app_its_unit_test_and_its_ui_test()
    {
        var dir = SimulatorProbe.FixtureDirectory;
        var project = File.ReadAllText(Path.Combine(dir, "ProbeApp.xcodeproj", "project.pbxproj"));
        Assert.Contains("com.apple.product-type.bundle.unit-test", project);
        Assert.Contains("com.apple.product-type.bundle.ui-testing", project);
        Assert.Contains("CODE_SIGN_IDENTITY = \"-\";", project); // ad hoc signing only (E3): no team, no certificate
        Assert.DoesNotMatch(@"DEVELOPMENT_TEAM = [A-Z0-9]{10};", project);
        Assert.True(File.Exists(Path.Combine(dir, "ProbeApp.xcodeproj", "xcshareddata", "xcschemes", "ProbeApp.xcscheme")));
        Assert.Contains("XCTAssertEqual(Counter.next(41), 42)", File.ReadAllText(Path.Combine(dir, "ProbeAppTests", "ProbeAppTests.swift")));
        Assert.Contains("XCUIApplication()", File.ReadAllText(Path.Combine(dir, "ProbeAppUITests", "ProbeAppUITests.swift")));
    }

    [Fact]
    public void A_passing_run_reads_as_both_passed_with_nothing_missing()
    {
        const string output = """
            probe: user michael uid 501 home /Users/michael tmpdir /var/folders/x/T/
            probe: simctl-boot ok (33s)
            probe: unit-test ok (8s)
            probe: ui-test ok (14s)
            probe: deleted device 5035E5C9-7E5E-48B0-BDD3-A19D46DA701E
            probe-result: unit=pass ui=pass
            """;

        var r = SimulatorProbe.Read(output);

        Assert.Equal(("pass", "pass"), (r.Unit, r.Ui));
        Assert.Empty(r.Missing);
        Assert.Equal(3, r.Steps.Count);
    }

    [Theory]
    [InlineData("probe: simctl-list FAILED (2s) exit 1\n--- simctl-list output ---\nUnable to locate device set: Failed to initialize simulator device set.\n--- end ---\nprobe-result: unit=not-run ui=not-run", "CoreSimulator service")]
    [InlineData("probe: first-launch FAILED (0s) exit 69\nprobe-result: unit=not-run ui=not-run", "first launch")]
    [InlineData("probe: ui-test FAILED (60s) exit 65\n--- ui-test output ---\nTimed out while enabling automation mode.\n--- end ---\nprobe-result: unit=pass ui=fail", "UI automation")]
    [InlineData("probe: unit-test FAILED (9s) exit 65\n--- unit-test output ---\nauthorization request denied by DevToolsSecurity\n--- end ---\nprobe-result: unit=fail ui=fail", "developer-tools authorization")]
    [InlineData("probe: simctl-boot FAILED (300s) exit 124\nprobe: timed out after 300s\nprobe-result: unit=not-run ui=not-run", "did not finish")]
    public void A_failed_run_names_what_is_missing(string output, string missing)
    {
        var r = SimulatorProbe.Read(output);

        Assert.Contains(r.Missing, m => m.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_cut_short_reads_as_not_run_and_says_no_cause_matched()
    {
        var message = SimulatorProbe.Explain("worker slot 1 (_factory)", 137, "probe: user _factory uid 498\n", "");

        Assert.Contains("unit test not-run, UI test not-run (exit 137)", message);
        Assert.Contains("no known cause matched", message);
    }
}
