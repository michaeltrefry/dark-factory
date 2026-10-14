using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26126 (P5 S0 spike): reads the output of <c>Fixtures/ios-probe/probe.sh</c> — a simulator booted and one XCTest unit
/// test and one XCUITest UI test run on it — and names what is missing when a step fails. The live run is
/// <c>LiveWorkerSandboxTests.A_sandbox_user_boots_a_simulator_and_runs_a_unit_test_and_a_ui_test</c>; the findings are in
/// CLAUDE.md ("iOS simulator and UI tests as a sandbox user").
/// </summary>
internal static partial class SimulatorProbe
{
    /// <summary>The fixture directory as copied next to the test assembly.</summary>
    public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ios-probe");

    /// <summary>The probe's verdict: <c>unit</c> and <c>ui</c> are <c>pass</c>, <c>fail</c> or <c>not-run</c>.</summary>
    public sealed record Result(string Unit, string Ui, IReadOnlyList<string> Steps, IReadOnlyList<string> Missing);

    /// <summary>
    /// Known causes: the steps whose failure they explain (null: any step), a pattern over THAT step's own lines only — its
    /// <c>FAILED</c> line and its output block (<c>--- step output ---</c> … <c>--- end ---</c>), never the rest of the run's
    /// output — and what they say is missing.
    /// </summary>
    private static readonly (string[]? Steps, Regex Pattern, string Missing)[] Causes =
    [
        (["xcodebuild-version"], new(@"license", RegexOptions.IgnoreCase),
            "the Xcode license is not accepted (system-wide; the owner runs `sudo xcodebuild -license accept`)"),
        (["first-launch"], new(@"FAILED"),
            "Xcode's first launch is not done (system-wide; the owner runs `sudo xcodebuild -runFirstLaunch`)"),
        (null, new(@"(CoreSimulatorService|Unable to locate device set|Failed to initialize simulator device set|connection (became invalid|interrupted|refused)|Unable to boot the Simulator launchd|Unable to lookup in current state)", RegexOptions.IgnoreCase),
            "the CoreSimulator service for this user (a per-user launchd service: the user has no launchd bootstrap/session of its own under sudo — `launchctl asuser <uid>` as root, or a login session of that user)"),
        (["build-for-testing", "unit-test", "ui-test"], new(@"(DevToolsSecurity|_developer|[Aa]uthoriz|Interaction is not allowed|no UI access)"),
            "developer-tools authorization (the user is not in the _developer group and DevToolsSecurity is disabled; enabling either is a system change for the owner)"),
        (null, new(@"(automation mode|automationmodetool|testmanagerd|Failed to (establish|initialize) (communication|for UI testing)|kAXErrorServerNotFound|UI testing (failed|is not available))", RegexOptions.IgnoreCase),
            "UI automation (XCUITest's testmanagerd / automation mode): typically an Aqua (GUI login) session of this user"),
        (null, new(@"Permission denied|Operation not permitted"),
            "a file permission (see the failing step's output)"),
        (["no-runtime"], new(@"FAILED"),
            "an iOS simulator runtime or iPhone device type visible to this user (runtimes are system-wide under /Library/Developer/CoreSimulator)"),
        (["device-set"], new(@"FAILED"),
            "the device in this user's own device set (~<user>/Library/Developer/CoreSimulator/Devices, in a home this user owns: see the step's output)"),
    ];

    [GeneratedRegex(@"^probe-result: unit=(\S+) ui=(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^probe: (\S+) (ok|FAILED)\b.*$", RegexOptions.Multiline)]
    private static partial Regex StepLine();

    [GeneratedRegex(@"^probe: (\S+) started\s*$", RegexOptions.Multiline)]
    private static partial Regex StartedLine();

    [GeneratedRegex(@"^probe: launchd manager (\S+)", RegexOptions.Multiline)]
    private static partial Regex ManagerLine();

    /// <summary>
    /// Parses the probe's output (or, for a run cut short, its progress file); with no result line (the probe did not run to
    /// its end) both are <c>not-run</c>.
    /// </summary>
    public static Result Read(string output)
    {
        output = output.Replace("\r\n", "\n");
        var result = ResultLine().Match(output);
        var stepLines = StepLine().Matches(output).ToList();
        var steps = stepLines.Select(m => m.Value.Trim()).ToList();
        var missing = new List<string>();
        foreach (var failed in stepLines.Where(m => m.Groups[2].Value == "FAILED"))
        {
            var name = failed.Groups[1].Value;
            var block = BlockOf(output, failed);
            missing.AddRange(Causes
                .Where(c => (c.Steps is null || c.Steps.Contains(name)) && c.Pattern.IsMatch(block))
                .Select(c => c.Missing));
        }
        // A step that started and never reported: the run was cut short in it (its helper killed it).
        foreach (Match started in StartedLine().Matches(output))
        {
            var name = started.Groups[1].Value;
            if (!stepLines.Any(f => f.Groups[1].Value == name && f.Index > started.Index))
            {
                missing.Add($"the step {name} to finish: the run was cut short in it (the live test's time ran out, or its helper was stopped)");
            }
        }
        var failedRun = steps.Any(s => s.Contains(" FAILED", StringComparison.Ordinal)) || !result.Success;
        // Only an Aqua (GUI login) session has been seen to work (CLAUDE.md, "iOS simulator and UI tests as a sandbox user").
        if (failedRun && ManagerLine().Match(output) is { Success: true } manager && manager.Groups[1].Value != "Aqua")
        {
            missing.Add($"an Aqua launchd session: this user's process ran under launchd manager `{manager.Groups[1].Value}`, not Aqua — "
                + "the factory must run inside the owner's GUI login session (not from ssh or as a LaunchDaemon)");
        }
        return new Result(
            result.Success ? result.Groups[1].Value : "not-run",
            result.Success ? result.Groups[2].Value : "not-run",
            steps,
            failedRun ? missing.Distinct().ToList() : []);
    }

    /// <summary>A failed step's own lines: its <c>FAILED</c> line and the output block right after it, if there is one.</summary>
    private static string BlockOf(string output, Match failed)
    {
        var after = failed.Index + failed.Length;
        var header = $"\n--- {failed.Groups[1].Value} output ---\n";
        if (!output.AsSpan(after).StartsWith(header, StringComparison.Ordinal))
        {
            return failed.Value;
        }
        var end = output.IndexOf("\n--- end ---", after + header.Length, StringComparison.Ordinal);
        return output[failed.Index..(end < 0 ? output.Length : end)];
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
            probe: launchd manager Aqua (Aqua = a GUI login session; Background/System = none)
            probe: simctl-boot started
            probe: simctl-boot ok (33s)
            probe: unit-test started
            probe: unit-test ok (8s)
            probe: ui-test started
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
    [InlineData("probe: device-set FAILED\n--- device-set output ---\nthe device X is not in this user's own device set\n--- end ---\nprobe-result: unit=not-run ui=not-run", "own device set")]
    public void A_failed_run_names_what_is_missing(string output, string missing)
    {
        var r = SimulatorProbe.Read(output);

        Assert.Contains(r.Missing, m => m.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>
    /// A cause is matched only within the failing step's own lines: here the build failed for a reason no cause knows, and the
    /// cleanup's delete failed with text the developer-tools cause would read as its own — which explains no build step.
    /// </summary>
    [Fact]
    public void A_cause_is_matched_only_in_the_failing_steps_own_output()
    {
        const string output = """
            probe: groups staff _developer
            probe: build-for-testing started
            probe: build-for-testing FAILED (12s) exit 65
            --- build-for-testing output ---
            error: no such module 'Probe'
            --- end ---
            probe: simctl-delete started
            probe: simctl-delete FAILED (1s) exit 1
            --- simctl-delete output ---
            Authorization was denied for the device
            --- end ---
            probe-result: unit=not-run ui=not-run
            """;

        var r = SimulatorProbe.Read(output);

        Assert.DoesNotContain(r.Missing, m => m.Contains("developer-tools", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_outside_an_Aqua_session_names_the_session()
    {
        const string output = """
            probe: launchd manager Background (Aqua = a GUI login session; Background/System = none)
            probe: simctl-list started
            probe: simctl-list FAILED (2s) exit 1
            --- simctl-list output ---
            something went wrong
            --- end ---
            probe-result: unit=not-run ui=not-run
            """;

        var r = SimulatorProbe.Read(output);

        Assert.Contains(r.Missing, m => m.Contains("launchd manager `Background`, not Aqua", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_cut_short_names_the_step_it_was_in()
    {
        const string progress = """
            probe: launchd manager Aqua (Aqua = a GUI login session; Background/System = none)
            probe: simctl-create started
            probe: simctl-create ok (1s)
            probe: simctl-boot started
            """;

        var r = SimulatorProbe.Read(progress);

        Assert.Equal(("not-run", "not-run"), (r.Unit, r.Ui));
        Assert.Equal(["the step simctl-boot to finish: the run was cut short in it (the live test's time ran out, or its helper was stopped)"], r.Missing);
    }

    [Fact]
    public void A_run_cut_short_before_any_step_reads_as_not_run_and_says_no_cause_matched()
    {
        var message = SimulatorProbe.Explain("worker slot 1 (_factory)", 137, "probe: user _factory uid 498\n", "");

        Assert.Contains("unit test not-run, UI test not-run (exit 137)", message);
        Assert.Contains("no known cause matched", message);
    }
}
