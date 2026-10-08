using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <see cref="SafeHelper"/>, the one way tests run the launch helper: it refuses a copy that could still signal or list the
/// user's processes, its seam never signals a process that is not the test's own, and no test reaches the real helper
/// another way. Every process these tests could signal is one they started themselves.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SafeHelperTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-safe-helper-").FullName;

    private string Copy(SafeHelperOptions options) =>
        SafeHelper.BuildCopy(options, Path.Combine(_dir, "t"), Path.Combine(_dir, "r"), options.FakeListing is null ? null : Path.Combine(_dir, "p"));

    [Fact]
    public void Every_copy_the_entry_point_builds_passes_its_own_check()
    {
        SafeHelper.AssertSafe(Copy(new SafeHelperOptions()));
        SafeHelper.AssertSafe(Copy(new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, Registry = Path.Combine(_dir, "reg") }));
        SafeHelper.AssertSafe(Copy(new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, FakeListing = "echo '400001 1'" }));
    }

    [Theory]
    [InlineData("real-primitive")] // the production send_signal back in place of the seam
    [InlineData("raw-kill")] // a signal that bypasses the seam
    [InlineData("pkill")]
    [InlineData("killall")]
    [InlineData("uid-listing")] // the production ps -U/-u enumeration back
    [InlineData("no-seam")]
    public void Entry_point_refuses_a_copy_that_can_signal_or_list_outside_the_seam(string mutation)
    {
        var copy = Copy(new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, Registry = Path.Combine(_dir, "reg") });
        var seam = Regex.Match(copy, "# >>> SafeHelper seam.*?# <<< SafeHelper seam", RegexOptions.Singleline).Value;
        var listing = Regex.Match(copy, @"^list_uid_procs\(\) \{.*?^\}$", RegexOptions.Singleline | RegexOptions.Multiline).Value;
        Assert.NotEmpty(seam);
        Assert.NotEmpty(listing);
        var mutated = mutation switch
        {
            "real-primitive" => copy.Replace(seam, "# >>> SafeHelper seam\n# <<< SafeHelper seam\n" + SafeHelper.SignalPrimitive),
            "raw-kill" => copy.Replace("kill_pids() {", "kill -KILL \"$@\"\nkill_pids() {"),
            "pkill" => copy.Replace("kill_pids() {", "pkill -U \"$self_uid\"\nkill_pids() {"),
            "killall" => copy.Replace("kill_pids() {", "killall -u \"$sandbox_user\"\nkill_pids() {"),
            "uid-listing" => copy.Replace(listing, SafeHelper.ListPrimitive),
            "no-seam" => copy.Replace(seam, SafeHelper.SignalPrimitive.Replace("kill", ":")),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.NotEqual(copy, mutated);

        Assert.Throws<InvalidOperationException>(() => SafeHelper.AssertSafe(mutated));
    }

    /// <summary>
    /// A process of the current user outside this test host's tree (double-forked and setsid()'d, so reparented to
    /// launchd) — what any other process of the owner's session looks like to the seam. Sleeps 600 s.
    /// </summary>
    private (int Pid, string Start) StartDetached()
    {
        var pidFile = Path.Combine(_dir, $"detached-{Guid.NewGuid():N}");
        using (var p = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e",
            "my $f = shift; exit 0 if fork; POSIX::setsid(); exit 0 if fork; "
            + "open(STDIN, '<', '/dev/null'); open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); "
            + "open(my $h, '>', \"$f.tmp\"); print $h \"$$\\n\"; close $h; rename(\"$f.tmp\", $f); exec('/bin/sleep', '600');",
            pidFile]))!)
        {
            p.WaitForExit();
        }
        for (var i = 0; i < 200 && !File.Exists(pidFile); i++)
        {
            Thread.Sleep(25);
        }
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        return (pid, SafeHelper.StartTime(pid));
    }

    /// <summary>Cleanup of a process this test started, only while it is still that process (same start time).</summary>
    private static void KillOwn(int pid, string start)
    {
        if (SandboxSupport.IsAlive(pid) && SafeHelper.StartTime(pid) == start)
        {
            SandboxSupport.KillQuietly(pid);
        }
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(SafeHelper helper)
    {
        var psi = new ProcessStartInfo(helper.Path, ["/bin/sh", "-c", "exit 0"]) { RedirectStandardInput = true, RedirectStandardError = true };
        psi.Environment.Remove("SUDO_USER");
        psi.Environment.Remove("SUDO_UID");
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();
        return (p.ExitCode, stderr);
    }

    /// <summary>A copy whose uid sweep runs and whose (fake) listing names <paramref name="pid"/>, as a mis-scoped sweep would.</summary>
    private SafeHelper SweepOver(int pid, string? registry = null) => SafeHelper.Create(_dir, new SafeHelperOptions
    {
        Identity = FakeIdentity.SandboxRole,
        FakeListing = $"echo '{pid} 1'",
        Registry = registry,
    });

    [Fact]
    public async Task Seam_refuses_with_99_and_signals_nothing_when_asked_to_kill_an_owner_process_outside_the_test()
    {
        var (pid, start) = StartDetached();
        try
        {
            var helper = SweepOver(pid);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.Equal(99, exitCode);
            Assert.Contains("test seam: refusing", stderr);
            Assert.Equal([$"KILL {pid}"], helper.Refusals);
            Assert.Contains($"refuse KILL {pid}", helper.TargetLog);
            Assert.DoesNotContain($"kill KILL {pid}", helper.TargetLog);
            await Task.Delay(200);
            Assert.True(SandboxSupport.IsAlive(pid), "the seam signalled a process outside the test");
            Assert.Equal(start, SafeHelper.StartTime(pid));
        }
        finally
        {
            KillOwn(pid, start);
        }
    }

    [Fact]
    public async Task Seam_refuses_a_registry_pid_whose_start_time_no_longer_matches()
    {
        // The pid was registered, then reused by another process: the start time tells them apart.
        var (pid, start) = StartDetached();
        try
        {
            var registry = Path.Combine(_dir, "reg");
            File.WriteAllText(registry, $"{pid}|Thu Jan 1 00:00:00 1970\n");
            var helper = SweepOver(pid, registry);

            var (exitCode, _) = await RunAsync(helper);

            Assert.Equal(99, exitCode);
            Assert.Equal([$"KILL {pid}"], helper.Refusals);
            await Task.Delay(200);
            Assert.True(SandboxSupport.IsAlive(pid), "the seam killed a reused registry pid");
        }
        finally
        {
            KillOwn(pid, start);
        }
    }

    [Fact]
    public async Task Seam_kills_a_registry_pid_whose_start_time_matches()
    {
        var (pid, start) = StartDetached();
        try
        {
            var registry = Path.Combine(_dir, "reg");
            SafeHelper.Register(registry, pid);
            var helper = SweepOver(pid, registry);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(helper.Refusals);
            Assert.Contains($"kill KILL {pid}", helper.TargetLog);
            await SandboxSupport.WaitUntilDeadAsync(pid);
        }
        finally
        {
            KillOwn(pid, start);
        }
    }

    [Fact]
    public async Task Seam_only_logs_another_process_of_this_test_host()
    {
        // Another test's process (a child of this host, not in the registry): neither killed nor a reason to refuse.
        using var sleeper = Process.Start(new ProcessStartInfo("/bin/sleep", ["600"]) { RedirectStandardInput = true })!;
        try
        {
            var helper = SweepOver(sleeper.Id);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(helper.Refusals);
            Assert.Contains($"skip KILL {sleeper.Id}", helper.TargetLog);
            await Task.Delay(200);
            Assert.True(SandboxSupport.IsAlive(sleeper.Id));
        }
        finally
        {
            SandboxSupport.KillQuietly(sleeper.Id);
        }
    }

    /// <summary>
    /// No test reaches the real helper except through <see cref="SafeHelper"/>: only SafeHelper.cs builds a path into the
    /// repo's scripts directory for it, SetupScriptTests only for setup-worker-user.sh (whose helper runs only through its
    /// fake sudo), and the installed helper is named only by the tests that never execute it (argument building, ACLs, config)
    /// or that are opt-in live probes. The patterns are assembled so this file does not match them itself.
    /// </summary>
    [Fact]
    public void No_test_runs_the_real_helper_outside_the_safe_entry_point()
    {
        var helperName = "factory-worker" + "-launch";
        var scriptsSegment = "\"script" + "s\"";
        var setupAllowed = new Regex(Regex.Escape(scriptsSegment) + @"\s*,\s*""setup-worker-user\.sh""");
        var scriptsPathLiteral = new Regex("\"[^\"\\n]*scripts/" + Regex.Escape(helperName));
        string[] installedMarkers = ["Default" + "HelperPath", "libexec/" + "dark-factory"];
        string[] installedAllowed = ["WorkerSandboxTests.cs", "CliAndConfigTests.cs"];
        const string entryPoint = "SafeHelper.cs";

        var testsRoot = Path.Combine(SandboxSupport.RepoRoot, "tests");
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}") && !f.Contains($"{separator}obj{separator}"))
            .ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == entryPoint);

        var violations = new List<string>();
        var entryPointPaths = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var where = $"{Path.GetRelativePath(testsRoot, file)}:{i + 1}: {line.Trim()}";
                if (name == entryPoint)
                {
                    entryPointPaths += line.Contains(scriptsSegment, StringComparison.Ordinal) ? 1 : 0;
                    continue;
                }
                if (scriptsPathLiteral.IsMatch(line))
                {
                    violations.Add(where);
                }
                // The repo's scripts directory (a temp dir named scripts is fine), or any scripts segment next to the helper's name.
                var intoRepoScripts = line.Contains(scriptsSegment, StringComparison.Ordinal)
                    && (line.Contains("RepoRoot", StringComparison.Ordinal) || line.Contains(helperName, StringComparison.Ordinal));
                if (intoRepoScripts && !(name == "SetupScriptTests.cs" && setupAllowed.IsMatch(line)))
                {
                    violations.Add(where);
                }
                if (installedMarkers.Any(m => line.Contains(m, StringComparison.Ordinal)) && !installedAllowed.Contains(name))
                {
                    violations.Add(where);
                }
            }
        }

        Assert.Equal(1, entryPointPaths); // the one path to the real helper
        Assert.Empty(violations);
    }
}
