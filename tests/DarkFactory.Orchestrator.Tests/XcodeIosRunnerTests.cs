using DarkFactory.Orchestrator.Tests.Support;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26132: the real toolchain — the repo's xcodebuild wrapper and simulator script over the real xcodebuild and simulator, as
/// the OWNER (never a sandbox user), building the fixture iOS app for its own simulator and running a new unit test and a new UI
/// test alone on the base and the head, judged from the real result bundles — each run on a FRESH device it creates and deletes,
/// within the default <c>Gate:TestTimeoutMinutes</c>. Skipped without Xcode, an iOS Simulator runtime or a GUI login session. Any
/// simulator it leaves in the owner's device set (a run cut short) and the DerivedData the wrapper made are deleted afterwards.
/// </summary>
public class XcodeIosRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "tally");
    private readonly string _root = Directory.CreateTempSubdirectory("df-iosrun-").FullName;
    private readonly string _remote;
    private readonly string _seed;
    private readonly HashSet<string> _devicesBefore;

    public XcodeIosRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
        _devicesBefore = GateDevices().Select(d => d.Udid).ToHashSet(StringComparer.Ordinal);
        _physicalRoot = Physical(_root);
    }

    private readonly string _physicalRoot;

    /// <summary>
    /// The gate devices this test's runs made and left: a new one whose record names a results directory under this test's own
    /// root. Another gate run of the owner's (a live factory's, another test's) has devices and records of its own at the same
    /// time, which are never this test's to assert on or delete.
    /// </summary>
    private List<(string Name, string Udid)> OwnDevices()
    {
        var own = GateDeviceRecords.Under(GateDeviceRecords.Directory, _physicalRoot);
        return GateDevices().Where(d => !_devicesBefore.Contains(d.Udid) && own.Contains(d.Name)).ToList();
    }

    public void Dispose()
    {
        // Only the devices this test made (their records point into its root): never another run's.
        foreach (var (_, udid) in OwnDevices())
        {
            Output(XcodeNewTests.Xcrun, "simctl", "shutdown", udid);
            Output(XcodeNewTests.Xcrun, "simctl", "delete", udid);
        }
        var worktrees = Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name);
        if (Directory.Exists(worktrees))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var name in new[] { "gate-sc-6-base", "gate-sc-6-head", "gate-sc-6-base-retry", "gate-sc-6-base-retry-2", "gate-sc-6-base-retry-3" })
            {
                var derived = SwiftToolchain.DerivedDataOf(home, $"{Physical(worktrees)}/{name}");
                if (Directory.Exists(derived))
                {
                    Directory.Delete(derived, recursive: true);
                }
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Output(string program, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var stderr = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            stderr.Wait();
            return p.ExitCode == 0 ? output : "";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    /// <summary>The verified lwrunner user's devices named as the gate names its own (dark-factory-gate-…): name and UDID.</summary>
    private static List<(string Name, string Udid)> GateDevices() =>
        GuiTestExecution.UnavailableReason is null && OperatingSystem.IsMacOS()
            ? Regex.Matches(Output(XcodeNewTests.Xcrun, "simctl", "list", "devices"), @"^    (dark-factory-gate-\S+) \(([0-9A-F-]+)\)", RegexOptions.Multiline)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList()
            : [];

    private static readonly Lazy<string?> Missing = new(() =>
        GuiTestExecution.UnavailableReason is { } placement ? placement :
        !OperatingSystem.IsMacOS() || Output(XcodeNewTests.Xcodebuild, "-version").Length == 0 ? "Xcode is not installed"
        : !Output(XcodeNewTests.Xcrun, "simctl", "list", "runtimes").Split('\n').Any(l => l.StartsWith("iOS ", StringComparison.Ordinal) && !l.Contains("unavailable"))
            ? "no iOS Simulator runtime is installed"
        : Output("/bin/launchctl", "managername").Trim() != "Aqua" ? "not in a GUI (Aqua) login session: no simulator can boot"
        : null);

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

    private static string Physical(string directory)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/pwd", ["-P"]) { WorkingDirectory = directory, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return output;
    }

    [Fact]
    public async Task With_xcode_and_a_simulator_a_new_ios_unit_test_and_ui_test_run_alone_on_the_base_and_the_head_and_are_judged()
    {
        Assert.SkipWhen(Missing.Value is not null, Missing.Value ?? "");
        foreach (var (path, content) in XcodeIosFixture.Files())
        {
            Write(path, content);
        }
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-6");
        foreach (var (path, content) in XcodeIosFixture.Head)
        {
            Write(path, content!);
        }
        var headSha = CommitAndPush("fix", "factory/sc-6");
        var wrapper = Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild");

        var result = await NewTestsCheck.RunAsync(
            new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null, SandboxTestRunner.DefaultTimeout),
            NewTestsCheck.StrategiesFor(swift: true, wrapper), Repo, baseSha, headSha, "gate-sc-6", line => TestContext.Current.TestOutputHelper?.WriteLine(line),
            TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([(XcodeIosFixture.UnitTestId, "failed", "passed"), (XcodeIosFixture.UiTestId, "failed", "passed")],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.All(result.Tests, t => Assert.Equal((1, 1), (t.BaseCases.Count, t.HeadCases.Count)));
        // Each run's own fresh device is gone again, and so is its record: the gate keeps nothing between runs.
        Assert.Empty(OwnDevices());
        Assert.Empty(GateDeviceRecords.Under(GateDeviceRecords.Directory, _physicalRoot));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    private Task<NewTestsResult> Check(string baseSha, string headSha) => NewTestsCheck.RunAsync(
        new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null, SandboxTestRunner.DefaultTimeout),
        NewTestsCheck.StrategiesFor(swift: true, Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild")), Repo, baseSha, headSha, "gate-sc-6",
        line => TestContext.Current.TestOutputHelper?.WriteLine(line), TestContext.Current.CancellationToken);

    /// <summary>
    /// sc-26134: on the simulator destination, a new XCTest method calling a function the PR adds does not build on the base —
    /// not-built there, from xcodebuild's real errors — and the other new method gets its real result from a retry without it, a
    /// run of its own on a fresh device of its own; a base whose production code does not build is an error.
    /// </summary>
    [Fact]
    public async Task With_xcode_and_a_simulator_a_new_ios_test_calling_code_the_base_lacks_is_not_built_there_and_a_base_that_does_not_build_is_an_error()
    {
        Assert.SkipWhen(Missing.Value is not null, Missing.Value ?? "");
        foreach (var (path, content) in XcodeIosFixture.Files())
        {
            Write(path, content);
        }
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-6");
        Write(XcodeIosFixture.Counter, "enum Counter {\n    static let start = 0\n\n    static func next(_ value: Int) -> Int { value + 1 }\n\n"
            + "    static func previous(_ value: Int) -> Int { value - 1 }\n}\n");
        Write(XcodeIosFixture.UnitTestFile, "import XCTest\n@testable import Tally\n\nfinal class IncrementTests: XCTestCase {\n    func testNextAddsOne() {\n"
            + "        XCTAssertEqual(Counter.next(41), 42)\n    }\n\n    func testPreviousTakesOne() {\n        XCTAssertEqual(Counter.previous(42), 41)\n    }\n}\n");
        var head = CommitAndPush("previous", "factory/sc-6");

        var pass = await Check(baseSha, head);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal([("TallyTests/IncrementTests/testNextAddsOne()", "failed", "passed"), ("TallyTests/IncrementTests/testPreviousTakesOne()", "not-built", "passed")],
            pass.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.Equal(("build-failed", "ran"), (pass.BaseRun, pass.BaseRetry));
        Assert.Equal([$"{XcodeIosFixture.UnitTestFile}:9 func testPreviousTakesOne"], pass.BaseRemoved);
        Assert.Empty(OwnDevices());

        // A base whose production code does not build: an error, naming the base's file.
        TestGit.Run(_seed, "checkout", "-q", "main");
        Write(XcodeIosFixture.Counter, "enum Counter {\n    static let start = zero\n\n    static func next(_ value: Int) -> Int { value }\n}\n");
        var broken = CommitAndPush("broken base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-8");
        Write(XcodeIosFixture.Counter, XcodeIosFixture.Fixed);
        Write(XcodeIosFixture.UnitTestFile, XcodeIosFixture.UnitTest);
        var fixing = CommitAndPush("fix the build", "factory/sc-8");

        var error = await Check(broken, fixing);

        Assert.True(error.Outcome == NewTestsOutcome.Error, $"{error.Outcome}: {error.Reason}");
        Assert.Contains("says nothing about the new tests (not a compiler error in those files): Tally/Counter.swift(2,24): swiftc cannot find 'zero' in scope", error.Reason);
        Assert.Equal(("build-failed", "ran", (string?)null), (error.BaseRun, error.HeadRun, error.BaseRetry));
        Assert.Empty(OwnDevices());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}

/// <summary>
/// The records <c>scripts/factory-ios-simulator</c> keeps of its runs' devices: <c>~/Library/Caches/dark-factory-gate-devices/&lt;name&gt;</c>,
/// whose first line is the absolute physical path of that run's results directory.
/// </summary>
public static class GateDeviceRecords
{
    public static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "dark-factory-gate-devices");

    /// <summary>The device names whose record names a results directory under <paramref name="root"/> (a physical path), or <paramref name="root"/> itself.</summary>
    public static HashSet<string> Under(string records, string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(records))
        {
            return names;
        }
        var prefix = root.TrimEnd('/') + "/";
        foreach (var record in System.IO.Directory.GetFiles(records, "dark-factory-gate-*"))
        {
            string first;
            try
            {
                first = File.ReadLines(record).FirstOrDefault() ?? "";
            }
            catch (IOException)
            {
                continue; // removed meanwhile by its own run
            }
            if (first == prefix.TrimEnd('/') || first.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(Path.GetFileName(record));
            }
        }
        return names;
    }
}

public sealed class GateDeviceRecordsTests : IDisposable
{
    private readonly string _dir = System.IO.Directory.CreateTempSubdirectory("df-gate-records-").FullName;

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Only_records_naming_a_results_directory_under_the_root_are_the_roots()
    {
        var records = System.IO.Directory.CreateDirectory(Path.Combine(_dir, "records")).FullName;
        void Record(string name, string text) => File.WriteAllText(Path.Combine(records, name), text);
        Record("dark-factory-gate-0000000000000001", "/private/var/t/df-iosrun-a/work/worktrees/acme/tally/gate-sc-6-base/.factory-test-results-x\n");
        Record("dark-factory-gate-0000000000000002", "/private/var/t/df-iosrun-a\n");
        Record("dark-factory-gate-0000000000000003", "/private/var/t/df-iosrun-ab/work/x\n"); // a sibling sharing the prefix
        Record("dark-factory-gate-0000000000000004", "/opt/dark-factory/work/worktrees/o/n/gate-sc-9-head/r\n"); // a live factory's run
        Record("dark-factory-gate-0000000000000005", "");
        Record("not-a-record", "/private/var/t/df-iosrun-a/x\n");

        Assert.Equal(["dark-factory-gate-0000000000000001", "dark-factory-gate-0000000000000002"],
            GateDeviceRecords.Under(records, "/private/var/t/df-iosrun-a/").Order(StringComparer.Ordinal));
        Assert.Equal(["dark-factory-gate-0000000000000004"], GateDeviceRecords.Under(records, "/opt/dark-factory/work"));
        Assert.Empty(GateDeviceRecords.Under(Path.Combine(_dir, "none"), "/private/var/t/df-iosrun-a"));
    }
}
