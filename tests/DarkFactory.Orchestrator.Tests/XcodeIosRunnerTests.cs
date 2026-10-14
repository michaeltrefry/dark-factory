using System.Diagnostics;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-26132: the real toolchain — the repo's xcodebuild wrapper and simulator script over the real xcodebuild and simulator, as
/// the OWNER (never a sandbox user), building the fixture iOS app for its own simulator and running a new unit test and a new UI
/// test alone on the base and the head, judged from the real result bundles. Skipped without Xcode, an iOS Simulator runtime or a
/// GUI login session. The simulators it creates in the owner's device set (the template too, when there was none) and the
/// DerivedData the wrapper made are deleted afterwards.
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
    }

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
            foreach (var name in new[] { "gate-sc-6-base", "gate-sc-6-head" })
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
            new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null, TimeSpan.FromMinutes(30)),
            NewTestsCheck.StrategiesFor(swift: true, wrapper), Repo, baseSha, headSha, "gate-sc-6", null, TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([(XcodeIosFixture.UnitTestId, "failed", "passed"), (XcodeIosFixture.UiTestId, "failed", "passed")],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.All(result.Tests, t => Assert.Equal((1, 1), (t.BaseCases.Count, t.HeadCases.Count)));
        // Each run's own device is gone again; of what the gate made only the kept template (one) remains.
        var left = GateDevices().Select(d => d.Name).ToList();
        Assert.DoesNotContain(left, n => Regex.IsMatch(n, "^dark-factory-gate-[0-9a-f]{16}$"));
        Assert.Single(left, n => n.StartsWith("dark-factory-gate-template-", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}
