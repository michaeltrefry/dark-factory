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

    private const string Anchor = "kill_pids() {";

    [Theory]
    [InlineData("real-primitive")] // the production send_signal back in place of the seam
    [InlineData("raw-kill")] // a signal that bypasses the seam
    [InlineData("pkill")]
    [InlineData("killall")]
    [InlineData("killpg")]
    [InlineData("perl-kill")]
    [InlineData("launchctl")]
    [InlineData("osascript")]
    [InlineData("timeout")]
    [InlineData("eval")]
    [InlineData("ansi-c")] // $'\x6bill' is kill
    [InlineData("split-quotes")] // ki""ll is kill
    [InlineData("backslash")] // k\ill is kill
    [InlineData("after-quoted-hash")] // a # inside quotes does not start a comment
    [InlineData("sh-c")]
    [InlineData("awk-system")]
    [InlineData("sudo")]
    [InlineData("ps")]
    [InlineData("uid-listing")] // the production ps -U/-u enumeration back
    [InlineData("no-seam")]
    public void Entry_point_refuses_a_copy_that_can_signal_or_list_outside_the_seam(string mutation)
    {
        var copy = Copy(new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, Registry = Path.Combine(_dir, "reg") });
        var seam = Regex.Match(copy, "# >>> SafeHelper seam.*?# <<< SafeHelper seam", RegexOptions.Singleline).Value;
        Assert.NotEmpty(seam);
        Assert.Contains(Anchor, copy);
        string Before(string code) => copy.Replace(Anchor, code + "\n" + Anchor);
        var mutated = mutation switch
        {
            "real-primitive" => copy.Replace(seam, "# >>> SafeHelper seam\n# <<< SafeHelper seam\n" + SafeHelper.SignalPrimitive),
            "raw-kill" => Before("kill -KILL \"$@\""),
            "pkill" => Before("pkill -U \"$self_uid\""),
            "killall" => Before("killall -u \"$sandbox_user\""),
            "killpg" => Before("/usr/local/bin/killpg 1"),
            "perl-kill" => Before("perl -e 'kill 9, -1'"),
            "launchctl" => Before("launchctl bootout gui/501"),
            "osascript" => Before("osascript -e 'tell application \"System Events\" to log out'"),
            "timeout" => Before("timeout -s KILL 1 sleep 5"),
            "eval" => Before("eval \"$cmd\""),
            "ansi-c" => Before("$'\\x6bill' -KILL -1"),
            "split-quotes" => Before("ki\"\"ll -KILL -1"),
            "backslash" => Before("k\\ill -KILL -1"),
            "after-quoted-hash" => Before("echo \"a # b\"; kill -KILL -1"),
            "sh-c" => Before("/bin/sh -c \"$cmd\""),
            "awk-system" => Before("awk 'BEGIN { system(\"true\") }'"),
            "sudo" => Before("sudo -n true"),
            "ps" => Before("ps -A -o pid="),
            "uid-listing" => Before(SafeHelper.ListPrimitive),
            "no-seam" => copy.Replace(seam, SafeHelper.SignalPrimitive.Replace("kill", ":")),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.NotEqual(copy, mutated);

        Assert.Throws<InvalidOperationException>(() => SafeHelper.AssertSafe(mutated));
    }

    [Theory]
    [InlineData("seam-allows-everything")]
    [InlineData("seam-no-refusal")]
    [InlineData("listing-whole-table")]
    public void Entry_point_refuses_a_mutation_that_rewrites_the_seam_or_the_listing(string mutation)
    {
        Func<string, string> mutate = mutation switch
        {
            "seam-allows-everything" => copy => copy.Replace("else out(\"refuse\", x)", "else out(\"kill\", x)"),
            "seam-no-refusal" => copy => copy.Replace("exit 99", ":"),
            "listing-whole-table" => copy => copy.Replace("(p == self || under(p, self) || index(reg, \" \" p \" \"))", "1"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var options = new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, Registry = Path.Combine(_dir, "reg") };
        Assert.NotEqual(Copy(options), mutate(Copy(options)));

        var ex = Assert.Throws<InvalidOperationException>(() => Copy(options with { Mutate = mutate }));
        Assert.Contains("not the generated one", ex.Message);
    }

    [Fact]
    public void Entry_point_refuses_a_fake_listing_that_signals()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Copy(new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, FakeListing = "kill -KILL -1; echo '400001 1'" }));
    }

    /// <summary>
    /// The production <c>send_signal</c>, run alone in bash with <c>kill</c> replaced by a function that only prints its
    /// arguments (the builtin disabled, PATH empty) and synthetic pids above macOS's pid limit: never pid or group 0 or 1,
    /// nor an empty target.
    /// </summary>
    [Theory]
    [InlineData("-KILL -- 400001", "args -KILL -- 400001")]
    [InlineData("-KILL -- -400001", "args -KILL -- -400001")]
    [InlineData("-STOP 400002", "args -STOP 400002")]
    [InlineData("400003", "args 400003")]
    [InlineData("-KILL -- 0", null)]
    [InlineData("-KILL -- 1", null)]
    [InlineData("-KILL -- -0", null)]
    [InlineData("-KILL -- -1", null)]
    [InlineData("-KILL -- 00", null)]
    [InlineData("-KILL -- -01", null)]
    [InlineData("-KILL -- ''", null)] // an unset pid
    [InlineData("-KILL -- -", null)] // "-$worker" with worker unset
    [InlineData("-KILL -- 400001 1", null)] // one bad target refuses the call
    [InlineData("-KILL -- x", null)]
    public async Task Production_send_signal_never_signals_pid_or_group_0_or_1(string args, string? expected)
    {
        Assert.True(SafeHelper.SourceContains(SafeHelper.SignalPrimitive));
        var script = "enable -n kill\nPATH=/nonexistent\nkill() { echo \"args $*\"; }\n"
            + SafeHelper.SignalPrimitive + "\n"
            + $"if send_signal {args}; then echo signalled; else echo refused; fi\n";
        var psi = new ProcessStartInfo("/bin/bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        Assert.True(p.ExitCode == 0, stderr);
        Assert.Equal(expected is null ? ["refused"] : [expected, "signalled"], stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var helper = SweepOver(detached.Pid);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.Equal(99, exitCode);
            Assert.Contains("test seam: refusing", stderr);
            Assert.Equal([$"KILL {detached.Pid}"], helper.Refusals);
            Assert.Contains($"refuse KILL {detached.Pid}", helper.TargetLog);
            Assert.DoesNotContain($"kill KILL {detached.Pid}", helper.TargetLog);
            await Task.Delay(200);
            Assert.True(detached.IsAlive, "the seam signalled a process outside the test");
        }
        finally
        {
            detached.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Seam_refuses_a_registry_pid_whose_start_time_no_longer_matches()
    {
        // The pid was registered, then reused by another process: the start time tells them apart.
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var registry = Path.Combine(_dir, "reg");
            File.WriteAllText(registry, $"{detached.Pid}|Thu Jan 1 00:00:00 1970\n");
            var helper = SweepOver(detached.Pid, registry);

            var (exitCode, _) = await RunAsync(helper);

            Assert.Equal(99, exitCode);
            Assert.Equal([$"KILL {detached.Pid}"], helper.Refusals);
            await Task.Delay(200);
            Assert.True(detached.IsAlive, "the seam killed a reused registry pid");
        }
        finally
        {
            detached.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Seam_kills_a_registry_pid_whose_start_time_matches()
    {
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var registry = Path.Combine(_dir, "reg");
            SafeHelper.Register(registry, detached);
            var helper = SweepOver(detached.Pid, registry);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(helper.Refusals);
            Assert.Contains($"kill KILL {detached.Pid}", helper.TargetLog);
            await OwnProcess.WaitUntilDeadAsync(detached);
        }
        finally
        {
            detached.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Seam_only_logs_another_process_of_this_test_host()
    {
        // Another test's process (a child of this host, not in the registry): neither killed nor a reason to refuse.
        using var sleeperProcess = Process.Start(new ProcessStartInfo("/bin/sleep", ["600"]) { RedirectStandardInput = true })!;
        var sleeper = OwnProcess.Of(sleeperProcess);
        try
        {
            var helper = SweepOver(sleeper.Pid);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(helper.Refusals);
            Assert.Contains($"skip KILL {sleeper.Pid}", helper.TargetLog);
            await Task.Delay(200);
            Assert.True(sleeper.IsAlive);
        }
        finally
        {
            sleeper.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Real_listing_never_hands_the_sweep_another_process_of_this_test_host()
    {
        // Another test's processes: a child of this host (what a double-forked one is until launchd adopts it, sc-25391) and one
        // already adopted. Neither is in this test's registry, so neither is ever listed — the seam then never has to re-classify
        // one that was reparented in between (a refusal: exit 99). The registry's own process and the helper's tree are listed.
        using var childProcess = Process.Start(new ProcessStartInfo("/bin/sleep", ["600"]) { RedirectStandardInput = true })!;
        var child = OwnProcess.Of(childProcess);
        var adopted = OwnProcess.StartDetached(_dir);
        var registered = OwnProcess.StartDetached(_dir);
        try
        {
            var registry = Path.Combine(_dir, "reg");
            SafeHelper.Register(registry, registered);
            using var probe = Process.Start(new ProcessStartInfo(SafeHelper.ListingProbe(_dir, registry))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdout = await probe.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = await probe.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(probe.ExitCode == 0, stderr);
            var listed = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')).ToList();

            Assert.Contains(listed, l => l[0] == registered.Pid.ToString());
            Assert.Contains(listed, l => l[0] == probe.Id.ToString()); // the helper's stand-in itself
            Assert.DoesNotContain(listed, l => l[0] == child.Pid.ToString());
            Assert.DoesNotContain(listed, l => l[0] == adopted.Pid.ToString());
            Assert.DoesNotContain(listed, l => l[0] == Environment.ProcessId.ToString());
            // Nothing else of this host: every pid listed is the registry's or in the probe's own tree.
            var parents = listed.ToDictionary(l => l[0], l => l[1]);
            Assert.All(listed, l =>
            {
                var p = l[0];
                for (var n = 0; p != probe.Id.ToString() && p != registered.Pid.ToString() && parents.ContainsKey(p) && n < 100; n++)
                {
                    p = parents[p];
                }
                Assert.True(p == probe.Id.ToString() || p == registered.Pid.ToString(), $"listed {l[0]} (parent {l[1]}) is neither the probe's nor the registry's");
            });
        }
        finally
        {
            child.KillIfStillRunning();
            adopted.KillIfStillRunning();
            registered.KillIfStillRunning();
        }
    }

    /// <summary>
    /// A log-only copy (<see cref="SafeHelperOptions.LogOnly"/>: nothing outside the helper's own tree is signalled, whatever
    /// the seam decides) whose last group kill targets group <paramref name="worker"/> instead (<c>worker</c> is set to it first),
    /// after the shell text <paramref name="extra"/>.
    /// </summary>
    private SafeHelper GroupKillOf(string worker, string? registry = null, string? extra = null)
    {
        const string groupKill = "send_signal -KILL -- \"-$worker\" || true # nothing the worker started outlives the run";
        Assert.True(SafeHelper.SourceContains(groupKill));
        return SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Registry = registry,
            LogOnly = true,
            Mutate = copy => copy.Replace(groupKill, $"{extra}worker={worker}; {groupKill}"),
        });
    }

    /// <summary>The process group of <paramref name="process"/> (for the detached process: its session's, whose leader is gone).</summary>
    private static string ProcessGroupOf(OwnProcess process)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "pgid=", "-p", process.Pid.ToString()]) { RedirectStandardOutput = true })!;
        var group = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        Assert.Matches("^[0-9]+$", group);
        Assert.True(int.Parse(group) > 1);
        return group;
    }

    [Fact]
    public async Task Seam_refuses_the_worker_group_when_a_member_is_not_the_tests_own()
    {
        // As after a pid-reuse race: the reaped worker's number now names a group with an unrelated member.
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var group = ProcessGroupOf(detached);
            var helper = GroupKillOf(group);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 99, $"exit {exitCode}: {stderr}");
            Assert.Contains("test seam: refusing", stderr);
            Assert.Equal([$"KILL -{group}"], helper.Refusals);
            Assert.True(detached.IsAlive);
        }
        finally
        {
            detached.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Seam_allows_the_worker_group_when_every_member_is_the_tests_own()
    {
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var registry = Path.Combine(_dir, "reg");
            SafeHelper.Register(registry, detached);
            var group = ProcessGroupOf(detached);
            var helper = GroupKillOf(group, registry);

            var (exitCode, stderr) = await RunAsync(helper);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(helper.Refusals);
            Assert.Contains($"log KILL -{group}", helper.TargetLog); // allowed; log-only, so not signalled
            Assert.True(detached.IsAlive);
        }
        finally
        {
            detached.KillIfStillRunning();
        }
    }

    [Theory]
    [InlineData("0", "", "-0")]
    [InlineData("1", "", "-1")]
    [InlineData("400001", "send_signal -KILL -- 1 || true; ", "1")]
    [InlineData("400001", "send_signal -KILL -- 0 || true; ", "0")]
    public async Task Seam_refuses_pid_or_group_0_or_1(string worker, string extra, string refused)
    {
        var helper = GroupKillOf(worker, extra: extra);

        var (exitCode, _) = await RunAsync(helper);

        Assert.Equal(99, exitCode);
        Assert.Equal([$"KILL {refused}"], helper.Refusals);
    }

    /// <summary>
    /// No test reaches the real helper except through <see cref="SafeHelper"/>:
    /// <list type="bullet">
    /// <item>only SafeHelper.cs builds a path into the repo's scripts directory for it (in a file that knows the repo root,
    /// every <c>"scripts"</c> segment counts, so a path built in two steps does too); SetupScriptTests only for
    /// setup-worker-user.sh, whose helper runs only through its fake sudo;</item>
    /// <item>the helper's text leaves SafeHelper only through a checked copy: <c>SafeHelper.Source</c> and <c>BuildCopy</c>
    /// appear nowhere else (but SafeHelperTests), and no test reads the orchestrator's embedded copy;</item>
    /// <item>the installed helper (<c>DefaultHelperPath</c>, its path) and a configured one (<c>Worker:LaunchHelper</c>,
    /// <c>options.WorkerSandbox</c>) appear only on known lines: argument building, ACL sharing, config defaults, a helper
    /// that does not exist, the opt-in live probes and the FACTORY_E2E-gated acceptance harness.</item>
    /// </list>
    /// The patterns are assembled so this file does not match them itself.
    /// </summary>
    [Fact]
    public void No_test_runs_the_real_helper_outside_the_safe_entry_point()
    {
        var helperName = "factory-worker" + "-launch";
        var scriptsSegment = "\"script" + "s\"";
        var setupAllowed = new Regex(Regex.Escape(scriptsSegment) + @"\s*,\s*""setup-worker-user\.sh""");
        var scriptsPathLiteral = new Regex("\"[^\"\\n]*scripts/" + Regex.Escape(helperName));
        var helperLiteral = new Regex("\"[^\"\\n]*" + Regex.Escape(helperName) + "[\"/]");
        string[] installedMarkers = ["Default" + "HelperPath", "libexec/" + "dark-factory"];
        var configured = new Regex(@"Worker(:|__)Launch" + "Helper|\\.Worker" + @"Sandbox\b(?![(.])");
        var sourceLeaks = new Regex(@"\bSafeHelper\.Sour" + @"ce\b|\bBuild" + @"Copy\b|GetManifest" + "Resource");
        const string entryPoint = "SafeHelper.cs";
        const string selfFile = "SafeHelperTests.cs";
        // file -> exact trimmed lines allowed to name the installed or a configured helper.
        var allowed = new Dictionary<string, string[]>
        {
            ["WorkerSandboxTests.cs"] =
            [
                "var sandbox = new WorkerSandbox(\"_factory\", WorkerSandbox." + installedMarkers[0] + ");",
                "Assert.Equal([\"-n\", \"-u\", \"_factory\", \"/usr/local/" + installedMarkers[1] + "/" + helperName + "\", \"claude\", \"-p\", \"x\"],",
                "() => new WorkerSandbox(Environment.UserName, WorkerSandbox." + installedMarkers[0] + ").ShareAsync(path, CancellationToken.None));",
                "await new WorkerSandbox(Environment.UserName, WorkerSandbox." + installedMarkers[0] + ").ShareAsync(path, CancellationToken.None);",
                "var sandbox = new WorkerSandbox(WorkerSandbox.DefaultUser, WorkerSandbox." + installedMarkers[0] + ");",
                "Assert.Contains(\"usage: " + helperName + "\", stderr);",
            ],
            ["CliAndConfigTests.cs"] =
            [
                "Assert.Equal(new Worker.WorkerSandbox(\"_factory\", \"/usr/local/" + installedMarkers[1] + "/" + helperName + "\"), options.Worker" + "Sandbox);",
                "var custom = Options(new() { [\"Worker:RunAs\"] = \"_df2\", [\"Worker:Launch" + "Helper\"] = \"/opt/h\", [\"Factory:WorkRoot\"] = \"/w\" });",
                "Assert.Equal(new Worker.WorkerSandbox(\"_df2\", \"/opt/h\"), custom.Worker" + "Sandbox);",
                "Assert.Null(options.Worker" + "Sandbox);",
                "Assert.Throws<InvalidOperationException>(() => Options(new() { [\"Worker:RunAs\"] = \"\" }).Worker" + "Sandbox);",
            ],
            ["IntakeLoopTests.cs"] = ["[\"Worker:Launch" + "Helper\"] = \"/nonexistent/df-test-helper\","],
            ["Harness.cs"] = ["if (Options.Worker" + "Sandbox is { } sandbox)"],
            ["E2e.cs"] = ["if (Harness.Options.Worker" + "Sandbox is not null)"],
            ["WalkingSkeletonTests.cs"] = ["var sandbox = Harness.Options.Worker" + "Sandbox;"],
            ["SetupScriptTests.cs"] =
            [
                "Assert.DoesNotContain(calls, c => c.Contains(\"" + helperName + "\")); // the helper (whose exit kills every worker process) never runs",
                "Assert.Contains(run.Calls, c => c.Contains(\"" + helperName + "\"));",
            ],
        };

        var testsRoot = Path.Combine(SandboxSupport.RepoRoot, "tests");
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}") && !f.Contains($"{separator}obj{separator}"))
            .ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == entryPoint);

        var violations = new List<string>();
        var used = new HashSet<string>();
        var entryPointPaths = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            var lines = text.Split('\n');
            var knowsRepoRoot = text.Contains("RepoRoot", StringComparison.Ordinal);
            var liveClass = Array.FindIndex(lines, l => l.Contains("public class LiveWorkerSandboxTests", StringComparison.Ordinal));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                var where = $"{Path.GetRelativePath(testsRoot, file)}:{i + 1}: {trimmed}";
                if (name == entryPoint)
                {
                    entryPointPaths += line.Contains(scriptsSegment, StringComparison.Ordinal) ? 1 : 0;
                    continue;
                }
                if (name == selfFile)
                {
                    continue;
                }
                if (sourceLeaks.IsMatch(line))
                {
                    violations.Add(where);
                }
                if (scriptsPathLiteral.IsMatch(line))
                {
                    violations.Add(where);
                }
                if (line.Contains(scriptsSegment, StringComparison.Ordinal) && knowsRepoRoot
                    && !(name == "SetupScriptTests.cs" && setupAllowed.IsMatch(line)))
                {
                    violations.Add(where);
                }
                var namesHelper = helperLiteral.IsMatch(line)
                    || installedMarkers.Any(m => line.Contains(m, StringComparison.Ordinal))
                    || configured.IsMatch(line);
                if (!namesHelper)
                {
                    continue;
                }
                var ok = allowed.TryGetValue(name, out var lineAllowList) && lineAllowList.Contains(trimmed);
                // The live probes' sandbox only behind their FACTORY_SANDBOX_LIVE gate.
                if (ok && trimmed.StartsWith("var sandbox = new WorkerSandbox(WorkerSandbox.DefaultUser", StringComparison.Ordinal))
                {
                    var before = string.Join('\n', lines[Math.Max(liveClass, 0)..i]);
                    ok = liveClass >= 0 && i > liveClass && before.Contains("\"FACTORY_SANDBOX_LIVE\") != \"1\"", StringComparison.Ordinal);
                }
                // The argument-building sandbox: used only for its arguments, never run.
                if (ok && trimmed.StartsWith("var sandbox = new WorkerSandbox(\"_factory\"", StringComparison.Ordinal))
                {
                    for (var j = i + 1; j < lines.Length && lines[j].TrimEnd() != "    }"; j++)
                    {
                        foreach (Match use in Regex.Matches(lines[j], @"\bsandbox\.(\w+)"))
                        {
                            ok &= use.Groups[1].Value is "SudoPath" or "BuildLaunchArguments";
                        }
                    }
                }
                if (ok)
                {
                    used.Add($"{name}|{trimmed}");
                }
                else
                {
                    violations.Add(where);
                }
            }
        }

        Assert.Equal(1, entryPointPaths); // the one path to the real helper
        Assert.True(violations.Count == 0, "violations:\n" + string.Join("\n", violations));
        // Every allowance still names a real line (none left over to be reused).
        var unused = allowed.SelectMany(kv => kv.Value.Select(l => $"{kv.Key}|{l}")).Where(a => !used.Contains(a)).ToList();
        Assert.True(unused.Count == 0, "allowances that match no line: " + string.Join("; ", unused));
    }
}
