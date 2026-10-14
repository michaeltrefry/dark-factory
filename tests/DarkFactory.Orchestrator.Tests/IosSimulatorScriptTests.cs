using System.Diagnostics;
using System.Text.RegularExpressions;
using DarkFactory.Analyzers;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// A copy of <c>scripts/factory-ios-simulator</c> (as compiled in, <see cref="XcodeNewTests.SimulatorScript"/>) whose
/// <c>launchctl=</c> and <c>xcrun=</c> lines name stubs: a fake device set in a file (<see cref="Devices"/>: "name udid" per line),
/// every simctl call recorded (<see cref="Calls"/>), and the device directories CoreSimulator would make under <see cref="Home"/>.
/// Never the real simulator, never a sandbox user. Stub switches (environment): <c>STUB_NO_RUNTIME</c>, <c>STUB_BOOT_HANG</c>
/// (bootstatus never finishes in time), <c>STUB_BOOT_FAIL</c> (boot fails, the device not booted), <c>STUB_FOREIGN_SET</c>
/// (CoreSimulator names another data path), <c>STUB_NO_DIR</c> (no device directory in this user's set).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class SimulatorScriptStub
{
    public SimulatorScriptStub(string dir, string manager = "Aqua")
    {
        Dir = dir;
        Directory.CreateDirectory(Home);
        var launchctl = SandboxSupport.Executable(dir, $"stub-launchctl-{Guid.NewGuid():N}", $"#!/bin/sh\n[ \"$1\" = managername ] && echo {manager}\n");
        var xcrun = SandboxSupport.Executable(dir, $"stub-xcrun-{Guid.NewGuid():N}", $$"""
            #!/bin/bash
            echo "$*" >> '{{Calls}}'
            devices='{{Devices}}'
            set_dir="$HOME/Library/Developer/CoreSimulator/Devices"
            touch "$devices"
            new_udid() { printf '%04X%04X-0000-4000-8000-%04X%04X%04X\n' $RANDOM $RANDOM $RANDOM $RANDOM $RANDOM; }
            case "$1 $2" in
                "simctl list")
                    case "$3" in
                        runtimes) echo "== Runtimes =="; [ -n "${STUB_NO_RUNTIME:-}" ] || echo "iOS 26.5 (26.5 - 23F77) - com.apple.CoreSimulator.SimRuntime.iOS-26-5" ;;
                        devicetypes) echo "iPhone 17 Pro (com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro)"; echo "iPhone 17 (com.apple.CoreSimulator.SimDeviceType.iPhone-17)" ;;
                        devices) echo "== Devices =="; echo "-- iOS 26.5 --"; while read -r name udid; do echo "    $name ($udid) (Shutdown) "; done < "$devices" ;;
                        -j)
                            echo '{ "devices" : { "com.apple.CoreSimulator.SimRuntime.iOS-26-5" : ['
                            while read -r name udid; do
                                path="$set_dir/$udid/data"; [ -z "${STUB_FOREIGN_SET:-}" ] || path="/Users/Shared/elsewhere/$udid/data"
                                # as simctl prints it: one key per line, "/" written as "\/"
                                printf '      {\n        "dataPath" : "%s",\n        "dataPathSize" : 1,\n        "udid" : "%s",\n        "name" : "%s"\n      },\n' "${path//\//\\/}" "$udid" "$name"
                            done < "$devices"
                            echo '] } }' ;;
                    esac ;;
                "simctl create") u=$(new_udid); echo "$3 $u" >> "$devices"; [ -n "${STUB_NO_DIR:-}" ] || mkdir -p "$set_dir/$u/data"; echo "$u" ;;
                "simctl delete") grep -v " $3\$" "$devices" > "$devices.new"; mv "$devices.new" "$devices" ;;
                "simctl boot") [ -z "${STUB_BOOT_FAIL:-}" ] || exit 1 ;;
                "simctl bootstatus") [ -z "${STUB_BOOT_HANG:-}" ] || sleep 8 ;;
                "simctl shutdown") ;;
                "xcresulttool get") echo '{"testNodes":[]}' ;;
                *) exit 9 ;; # anything else (a clone, a rename, …) is not the script's to run
            esac
            """);
        var text = XcodeNewTests.SimulatorScript; // what the gate runs: the repo's script, compiled in
        Assert.Single(text.Split('\n'), l => l == "launchctl=/bin/launchctl");
        Assert.Single(text.Split('\n'), l => l == "xcrun=/usr/bin/xcrun");
        Text = text.Replace("\nlaunchctl=/bin/launchctl\n", $"\nlaunchctl={launchctl}\n").Replace("\nxcrun=/usr/bin/xcrun\n", $"\nxcrun={xcrun}\n");
    }

    public string Dir { get; }

    /// <summary>The script's text with the stubs in place: run as <c>/bin/bash -c &lt;Text&gt; factory-ios-simulator …</c>, as the gate runs it.</summary>
    public string Text { get; }

    /// <summary>The HOME the script must be run with (its device set and records are under it).</summary>
    public string Home => Path.Combine(Dir, "home");

    public string Devices => Path.Combine(Dir, "devices");

    public string Calls => Path.Combine(Dir, "calls");

    public string Records => Path.Combine(Home, "Library", "Caches", "dark-factory-gate-devices");

    public string[] DeviceLines => File.Exists(Devices) ? File.ReadAllLines(Devices) : [];

    public string[] CallLines => File.Exists(Calls) ? File.ReadAllLines(Calls) : [];
}

/// <summary>
/// sc-26132: <c>scripts/factory-ios-simulator</c>, run as the current user over <see cref="SimulatorScriptStub"/> — never the real
/// simulator, never a sandbox user.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class IosSimulatorScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-ios-sim-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SimulatorScriptStub Stub(string manager = "Aqua") => new(_dir, manager);

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(SimulatorScriptStub stub, string[] args, Dictionary<string, string>? env = null)
    {
        // As the gate runs it: bash -c <text> factory-ios-simulator <command> …, in the run's worktree.
        var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", stub.Text, "factory-ios-simulator" }.Concat(args))
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["HOME"] = stub.Home;
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

    private const string Run = "dark-factory-gate-0123456789abcdef";

    private string Results => Path.Combine(_dir, "results");

    /// <summary>A record as prepare writes it: the absolute path of the run's results directory.</summary>
    private void Record(SimulatorScriptStub stub, string device, string resultsDirectory)
    {
        Directory.CreateDirectory(stub.Records);
        File.WriteAllText(Path.Combine(stub.Records, device), resultsDirectory + "\n");
    }

    [Theory]
    [InlineData("prepare", new[] { Run, "results" })]
    [InlineData("session", new[] { Run, "results", "/nonexistent/wrapper", "720" })]
    public async Task Outside_a_gui_login_session_simulator_work_is_refused_naming_why_and_nothing_is_touched(string command, string[] args)
    {
        var stub = Stub(manager: "Background");

        var (exit, stdout, _) = await RunAsync(stub, [command, .. args]);

        Assert.Equal(XcodeNewTests.SimulatorUnavailableExit, exit);
        Assert.Contains("refusing iOS simulator work: launchctl managername is 'Background', not 'Aqua'.", stdout);
        Assert.Contains("The factory must run inside the owner's GUI login session", stdout);
        Assert.Contains("for the gate's iOS simulator checks", stdout);
        Assert.Empty(stub.CallLines);
    }

    [Fact]
    public async Task Without_an_ios_runtime_prepare_fails_naming_it()
    {
        var (exit, stdout, _) = await RunAsync(Stub(), ["prepare", Run, "results"], new() { ["STUB_NO_RUNTIME"] = "1" });

        Assert.Equal(XcodeNewTests.SimulatorUnavailableExit, exit);
        Assert.Contains("no iOS Simulator runtime is installed for this user", stdout);
    }

    [Fact]
    public async Task Prepare_creates_a_fresh_device_for_the_run_from_the_runtime_and_never_clones_or_reuses_one()
    {
        var stub = Stub();
        File.WriteAllLines(stub.Devices,
        [
            "dark-factory-gate-template-iOS-26-5 11111111-0000-4000-8000-000000000002", // an earlier version's kept template
            "Mine 11111111-0000-4000-8000-000000000003",                                 // not the gate's: left alone
        ]);

        var (exit, stdout, stderr) = await RunAsync(stub, ["prepare", Run, "results"]);

        Assert.True(exit == 0, stdout + stderr);
        var created = Assert.Single(stub.DeviceLines, l => l.StartsWith(Run + " ", StringComparison.Ordinal)).Split(' ')[1];
        Assert.Equal(["Mine", Run], stub.DeviceLines.Select(l => l.Split(' ')[0]));
        Assert.Contains($"factory-ios-simulator: device {Run} {created} (com.apple.CoreSimulator.SimDeviceType.iPhone-17, com.apple.CoreSimulator.SimRuntime.iOS-26-5), created for this run", stdout);
        // The device is made from the runtime by this run: nothing of a device the user already had (a template one of its worker
        // sessions could have written) is copied into it.
        Assert.Contains("simctl create dark-factory-gate-0123456789abcdef com.apple.CoreSimulator.SimDeviceType.iPhone-17 com.apple.CoreSimulator.SimRuntime.iOS-26-5", stub.CallLines);
        Assert.DoesNotContain(stub.CallLines, c => c.StartsWith("simctl clone", StringComparison.Ordinal) || c.StartsWith("simctl boot", StringComparison.Ordinal));
        Assert.DoesNotContain("clone", XcodeNewTests.SimulatorScript.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Select(l => l.Split('#')[0]).SelectMany(l => Regex.Split(l, @"\W+")));
        // Recorded: the run's results directory (made now), by which a later run knows this one is still going.
        Assert.Equal(SandboxTestRunner.RealPath(Results) + "\n", File.ReadAllText(Path.Combine(stub.Records, Run)));
        Assert.True(Directory.Exists(Results));
    }

    [Fact]
    public async Task A_device_already_named_as_the_run_is_never_reused()
    {
        var stub = Stub();
        File.WriteAllLines(stub.Devices, [$"{Run} 11111111-0000-4000-8000-000000000009"]); // planted under the run's name

        var (exit, stdout, _) = await RunAsync(stub, ["prepare", Run, "results"]);

        Assert.Equal(70, exit);
        Assert.Contains($"a device named {Run} already exists; the gate never reuses a device", stdout);
        Assert.DoesNotContain(stub.CallLines, c => c.StartsWith("simctl create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepare_deletes_only_the_devices_and_records_of_runs_that_are_over()
    {
        var stub = Stub();
        var live = Directory.CreateDirectory(Path.Combine(_dir, "other-worktree", ".factory-test-results-live")).FullName;
        var gone = Path.Combine(_dir, "deleted-worktree", ".factory-test-results-gone");
        File.WriteAllLines(stub.Devices,
        [
            "dark-factory-gate-1111111111111111 11111111-0000-4000-8000-000000000001", // a run still going (RunAs=none, several runs)
            "dark-factory-gate-2222222222222222 11111111-0000-4000-8000-000000000002", // a run whose worktree is gone
            "dark-factory-gate-3333333333333333 11111111-0000-4000-8000-000000000003", // no record: cut short before recording
            "Mine 11111111-0000-4000-8000-000000000004",
        ]);
        Record(stub, "dark-factory-gate-1111111111111111", live);
        Record(stub, "dark-factory-gate-2222222222222222", gone);
        Record(stub, "dark-factory-gate-4444444444444444", live); // a run going that has not created its device yet
        Record(stub, "dark-factory-gate-5555555555555555", gone); // a run over, its device already gone

        var (exit, stdout, stderr) = await RunAsync(stub, ["prepare", Run, "results"]);

        Assert.True(exit == 0, stdout + stderr);
        Assert.Equal(["dark-factory-gate-1111111111111111", "Mine", Run], stub.DeviceLines.Select(l => l.Split(' ')[0]));
        Assert.Equal([Run, "dark-factory-gate-1111111111111111", "dark-factory-gate-4444444444444444"],
            Directory.GetFiles(stub.Records).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("STUB_FOREIGN_SET", "is not in this user's own device set: CoreSimulator names its data path '/Users/Shared/elsewhere/")]
    [InlineData("STUB_NO_DIR", "is not in this user's own device set: no directory ")]
    public async Task A_device_outside_the_users_own_set_is_deleted_and_fails_the_run(string stub, string why)
    {
        var s = Stub();

        var (exit, stdout, _) = await RunAsync(s, ["prepare", Run, "results"], new() { [stub] = "1" });

        Assert.Equal(70, exit);
        Assert.Contains(why, stdout);
        Assert.Empty(s.DeviceLines);
        Assert.False(File.Exists(Path.Combine(s.Records, Run)));
    }

    [Fact]
    public async Task The_session_boots_runs_each_test_alone_on_the_device_reads_its_bundle_and_shuts_down()
    {
        var stub = Stub();
        File.WriteAllLines(stub.Devices, [$"{Run} 22222222-0000-4000-8000-000000000001"]);
        var wrapperCalls = Path.Combine(_dir, "wrapper-calls");
        var wrapper = SandboxSupport.Executable(_dir, "stub-wrapper", $"#!/bin/bash\necho \"$*\" >> '{wrapperCalls}'\ncase \"$*\" in *b\\(\\)*) exit 65 ;; esac\n");

        var (exit, stdout, stderr) = await RunAsync(stub,
            ["session", Run, "results", wrapper, "720", "-project", "A.xcodeproj", "A", "ATests/a()", "00000000000000aa", "-workspace", "W.xcworkspace", "W", "AUITests/C/b()", "00000000000000bb"]);

        Assert.True(exit == 0, stdout + stderr);
        Assert.Equal(
        [
            "factory-ios-simulator: booted dark-factory-gate-0123456789abcdef 22222222-0000-4000-8000-000000000001 in 0 s",
            "factory-ios-simulator: result 00000000000000aa test-exit 0 xcresulttool-exit 0",
            "factory-ios-simulator: result 00000000000000bb test-exit 65 xcresulttool-exit 0",
        ], stdout.TrimEnd('\n').Split('\n').Select(l => Regex.Replace(l, " in [0-9]+ s$", " in 0 s")));
        Assert.Equal(
        [
            "--gate test-without-building -project A.xcodeproj -scheme A -destination platform=iOS Simulator,id=22222222-0000-4000-8000-000000000001 -only-testing:ATests/a() -resultBundlePath results/xcode-test-00000000000000aa.xcresult -quiet",
            "--gate test-without-building -workspace W.xcworkspace -scheme W -destination platform=iOS Simulator,id=22222222-0000-4000-8000-000000000001 -only-testing:AUITests/C/b() -resultBundlePath results/xcode-test-00000000000000bb.xcresult -quiet",
        ], File.ReadAllLines(wrapperCalls));
        Assert.Equal("{\"testNodes\":[]}\n", File.ReadAllText(Path.Combine(_dir, "results", "xcode-test-00000000000000aa.json")));
        // Booted, then waited for with bootstatus WITHOUT -b (the wait polled, nothing signalled); shut down at the end.
        Assert.Contains("simctl boot 22222222-0000-4000-8000-000000000001", stub.CallLines);
        Assert.Contains("simctl bootstatus 22222222-0000-4000-8000-000000000001", stub.CallLines);
        Assert.Equal("simctl shutdown 22222222-0000-4000-8000-000000000001", stub.CallLines[^1]);
    }

    [Theory]
    [InlineData("STUB_BOOT_HANG", "1", "did not boot within 1 s")]
    [InlineData("STUB_BOOT_FAIL", "30", "failed to boot (boot-failed)")]
    public async Task A_simulator_that_does_not_boot_fails_the_session_on_its_output_and_runs_no_test(string stub, string seconds, string why)
    {
        var s = Stub();
        File.WriteAllLines(s.Devices, [$"{Run} 22222222-0000-4000-8000-000000000001"]);
        var clock = Stopwatch.StartNew();

        var (exit, stdout, _) = await RunAsync(s, ["session", Run, "results", "/nonexistent/wrapper", seconds, "-project", "A.xcodeproj", "A", "ATests/a()", "00000000000000aa"],
            new() { [stub] = "1" });

        Assert.Equal(70, exit);
        // On standard output: the gate keeps a session's output, so this is the reason its check gives.
        Assert.Contains($"factory-ios-simulator: error: the simulator {Run} (22222222-0000-4000-8000-000000000001) {why}", stdout);
        Assert.DoesNotContain("result", stdout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}"); // a failed boot does not wait out its deadline
    }

    [Theory]
    [InlineData("prepare", "Mine", "refusing the device name 'Mine'")]
    [InlineData("remove", "dark-factory-gate-template-iOS-26-5", "refusing the device name")]
    [InlineData("session", "dark-factory-gate-0123456789abcdef", "the device dark-factory-gate-0123456789abcdef does not exist")]
    public async Task Only_the_runs_own_existing_device_is_used_or_removed(string command, string device, string why)
    {
        var stub = Stub();
        File.WriteAllLines(stub.Devices, ["Mine 11111111-0000-4000-8000-000000000003", "dark-factory-gate-template-iOS-26-5 11111111-0000-4000-8000-000000000004"]);
        string[] args = command switch
        {
            "prepare" => [command, device, "results"],
            "session" => [command, device, "results", "/nonexistent/wrapper", "720"],
            _ => [command, device],
        };

        var (exit, stdout, _) = await RunAsync(stub, args);

        Assert.NotEqual(0, exit);
        Assert.Contains(why, stdout);
        Assert.Equal(2, stub.DeviceLines.Length);
    }

    [Fact]
    public async Task Remove_deletes_the_runs_device_and_its_record()
    {
        var stub = Stub();
        File.WriteAllLines(stub.Devices, ["Mine 11111111-0000-4000-8000-000000000003", $"{Run} 22222222-0000-4000-8000-000000000001"]);
        Record(stub, Run, Results);

        var (exit, stdout, stderr) = await RunAsync(stub, ["remove", Run]);

        Assert.True(exit == 0, stdout + stderr);
        Assert.Equal(["Mine 11111111-0000-4000-8000-000000000003"], stub.DeviceLines);
        Assert.False(File.Exists(Path.Combine(stub.Records, Run)));
    }

    /// <summary>
    /// The gateway lint cannot read the script (it is a resource read at run time and passed to <c>/bin/bash -c</c>), so this reads
    /// it as the lint reads a launcher's arguments: it runs no network program and no launcher but awk (no shell, interpreter, sudo
    /// or env), by name or by path, and names no URL. Only its own messages (double-quoted text) and comments are not read.
    /// </summary>
    [Fact]
    public void The_compiled_in_script_runs_no_network_program_and_no_launcher_but_awk()
    {
        var code = string.Join('\n', XcodeNewTests.SimulatorScript.Split('\n')
            .Skip(1) // the #! line: bash runs it via -c, never this line
            .Select(l => l.TrimStart().StartsWith('#') ? "" : Regex.Replace(l, " #.*$", ""))
            .Select(l => Regex.Replace(l, "(fail [0-9]+|echo) \"(?:[^\"\\\\]|\\\\.)*\"", m => m.Groups[1].Value + " \"\"")));
        var words = Regex.Matches(code, @"[A-Za-z0-9_.+-]+").Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbidden = GatewayAnalyzer.NetworkPrograms.Concat(GatewayAnalyzer.Launchers.Where(l => l != "awk")).ToList();

        Assert.DoesNotContain(forbidden, words.Contains);
        Assert.DoesNotContain("://", code);
        // Every program it runs by path, and nothing it runs is outside that list (its stubs replace launchctl and xcrun in tests).
        Assert.Equal(["/bin/cat", "/bin/launchctl", "/bin/mkdir", "/bin/rm", "/bin/sleep", "/usr/bin/awk", "/usr/bin/grep", "/usr/bin/head", "/usr/bin/sed",
            "/usr/bin/tail", "/usr/bin/tr", "/usr/bin/wc", "/usr/bin/xcrun"],
            Regex.Matches(code, @"(?<![\w/])/(?:usr/)?s?bin/[\w.+-]+").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));
    }
}
