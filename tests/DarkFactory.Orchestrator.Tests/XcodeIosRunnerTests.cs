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
        var records = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "dark-factory-gate-devices");
        _recordsBefore = Directory.Exists(records) ? Directory.GetFiles(records).ToHashSet(StringComparer.Ordinal) : [];
    }

    private readonly HashSet<string> _recordsBefore;

    public void Dispose()
    {
        // Only the devices this test made: the gate's names that were not there before.
        foreach (var (_, udid) in GateDevices().Where(d => !_devicesBefore.Contains(d.Udid)))
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

    /// <summary>The owner's devices named as the gate names its own (dark-factory-gate-…): name and UDID.</summary>
    private static List<(string Name, string Udid)> GateDevices() =>
        OperatingSystem.IsMacOS()
            ? Regex.Matches(Output(XcodeNewTests.Xcrun, "simctl", "list", "devices"), @"^    (dark-factory-gate-\S+) \(([0-9A-F-]+)\)", RegexOptions.Multiline)
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList()
            : [];

    private static readonly Lazy<string?> Missing = new(() =>
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
        Assert.Empty(GateDevices().Where(d => !_devicesBefore.Contains(d.Udid)));
        var records = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "dark-factory-gate-devices");
        Assert.Empty(Directory.Exists(records) ? Directory.GetFiles(records).Where(f => _recordsBefore.Contains(f) is false) : []);
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
        Assert.Empty(GateDevices().Where(d => !_devicesBefore.Contains(d.Udid)));

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
        Assert.Empty(GateDevices().Where(d => !_devicesBefore.Contains(d.Udid)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}
