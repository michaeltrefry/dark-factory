using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Sandbox test support: the repo's launch helper, run without sudo through a pass-through fake.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class SandboxSupport
{
    public static readonly Uri Router = new("http://localhost:8080/");

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkFactory.slnx")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
        }
    }

    public static string Helper => Path.Combine(RepoRoot, "scripts", "factory-worker-launch");

    /// <summary>Stands in for <c>sudo -n -u &lt;user&gt;</c>: checks those arguments, then runs the rest as the current user.</summary>
    public static string FakeSudo(string dir) => Executable(dir, "fake-sudo.sh",
        "#!/bin/sh\n[ \"$1\" = -n ] && [ \"$2\" = -u ] || exit 99\nshift 3\nexec \"$@\"\n");

    public static WorkerSandbox LocalSandbox(string dir) => new("_factory", Helper, FakeSudo(dir));

    public static string Executable(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The current user's home from the password database (what the helper must give the worker).</summary>
    public static string PasswdHome()
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", "eval echo ~$(id -un)"]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var home = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return home;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Cleanup for a test that failed to kill what it started (so the test run never hangs on it).</summary>
    public static void KillQuietly(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// The pid a worker script wrote to <paramref name="pidFile"/>. Startup through the launch helper can take
    /// seconds on a loaded machine, so this waits up to 60 s (failing at once if <paramref name="run"/> ends first)
    /// and only accepts a complete line.
    /// </summary>
    public static async Task<int> WaitForPidAsync(string pidFile, Task run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (File.Exists(pidFile) && File.ReadAllText(pidFile) is var text && text.EndsWith('\n') && int.TryParse(text.Trim(), out var pid))
            {
                return pid;
            }
            if (run.IsCompleted)
            {
                await run; // surfaces why the worker never started
                Assert.Fail($"the worker finished without writing {pidFile}");
            }
            Assert.True(DateTime.UtcNow < deadline, $"no pid in {pidFile} after 60 s");
            await Task.Delay(50);
        }
    }

    /// <summary>Asserts every pid dies within 5s; kills any survivor first so a failure can't leak it.</summary>
    public static async Task WaitUntilDeadAsync(params int[] pids)
    {
        try
        {
            await WaitUntilDeadCoreAsync(pids);
        }
        finally
        {
            foreach (var pid in pids)
            {
                KillQuietly(pid);
            }
        }
    }

    private static async Task WaitUntilDeadCoreAsync(int[] pids)
    {
        for (var i = 0; i < 50 && pids.Any(IsAlive); i++)
        {
            await Task.Delay(100);
        }
        Assert.All(pids, pid => Assert.False(IsAlive(pid), $"process {pid} survived the stop"));
    }
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class WorkerSandboxTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-sandbox-").FullName;

    [Fact]
    public void Launch_runs_the_helper_as_the_worker_user_through_noninteractive_sudo()
    {
        var sandbox = new WorkerSandbox("_factory", WorkerSandbox.DefaultHelperPath);

        Assert.Equal("/usr/bin/sudo", sandbox.SudoPath);
        Assert.Equal(["-n", "-u", "_factory", "/usr/local/libexec/dark-factory/factory-worker-launch", "claude", "-p", "x"],
            sandbox.BuildLaunchArguments("claude", ["-p", "x"]));
    }

    [Fact]
    public void Variable_block_is_key_value_lines_ending_in_a_blank_line_and_rejects_line_breaks()
    {
        Assert.Equal("A=1\nB=x: y\n\n", WorkerSandbox.BuildVariableBlock(new Dictionary<string, string> { ["A"] = "1", ["B"] = "x: y" }));
        Assert.Throws<ArgumentException>(() => WorkerSandbox.BuildVariableBlock(new Dictionary<string, string> { ["A"] = "1\nPATH=/evil" }));
    }

    [Theory]
    [InlineData(WorkerAuth.ClaudeLogin, new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH" })]
    [InlineData(WorkerAuth.RouterKey, new[] { "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH" })]
    public async Task Helper_gives_the_worker_exactly_the_allowlisted_environment(WorkerAuth auth, string[] expected)
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "ghp_owner_should_not_leak");
        try
        {
            using var p = LocalSandbox().Start(_dir, "/usr/bin/env", [], ClaudeWorker.BuildRouterVariables(SandboxSupport.Router, "rk_worker", auth));
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            WorkerSandbox.Stop(p);

            Assert.Equal(0, p.ExitCode);
            var env = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            Assert.Equal(expected, env.Keys.Order());
            Assert.Equal("http://localhost:8080", env["ANTHROPIC_BASE_URL"]);
            Assert.Equal("X-Weave-Router-Key: rk_worker", env["ANTHROPIC_CUSTOM_HEADERS"]);
            Assert.Equal(SandboxSupport.PasswdHome(), env["HOME"]);
            Assert.StartsWith(env["HOME"] + "/.local/bin:", env["PATH"]);
            // Build servers would otherwise detach and outlive the run.
            Assert.Equal(("1", "0"), (env["MSBUILDDISABLENODEREUSE"], env["DOTNET_CLI_USE_MSBUILD_SERVER"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    [Theory]
    [InlineData("OPENAI_API_KEY=sk-x\n\n")]
    [InlineData("ANTHROPIC_API_KEY=sk-ant-x\n\n")] // router-key mode sends ANTHROPIC_AUTH_TOKEN; a provider key never passes
    [InlineData("HOME=/Users/michael\n\n")]
    [InlineData("ANTHROPIC_BASE_URL=http://x\n")] // stdin closed before the terminating blank line
    [InlineData("ANTHROPIC_BASE_URL\n\n")] // no '=': env would run it as the program
    public async Task Helper_refuses_other_variables_and_an_unterminated_block_without_running_anything(string block)
    {
        var marker = Path.Combine(_dir, "ran");
        var (exitCode, stderr) = await RunHelperAsync(["/usr/bin/touch", marker], block);

        Assert.Equal(64, exitCode);
        Assert.Contains("factory-worker-launch", stderr);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("GH_TOKEN=leak")] // env would take it as a variable, bypassing the allowlist
    [InlineData("bin/mark")] // relative path, resolved against the worktree the worker controls
    [InlineData("-u")] // an env option
    public async Task Helper_refuses_a_program_that_env_would_not_run_as_given(string program)
    {
        var marker = Path.Combine(_dir, "ran");
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        SandboxSupport.Executable(Path.Combine(_dir, "bin"), "mark", $"#!/bin/sh\ntouch '{marker}'\n");

        var (exitCode, stderr) = await RunHelperAsync([program, "/usr/bin/touch", marker], "\n", closeStdin: false); // the worker would get to run

        Assert.Equal(64, exitCode);
        Assert.Contains("usage: factory-worker-launch", stderr);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Helper_runs_a_plain_command_name_from_its_path()
    {
        var marker = Path.Combine(_dir, "ran");
        var (exitCode, stderr) = await RunHelperAsync(["touch", marker], "\n", closeStdin: false);

        Assert.True(exitCode == 0, stderr);
        Assert.True(File.Exists(marker));
    }

    /// <param name="closeStdin">Close stdin right after the block (the helper then stops the worker at once).</param>
    private async Task<(int ExitCode, string Stderr)> RunHelperAsync(string[] args, string block, bool closeStdin = true)
    {
        var psi = new ProcessStartInfo(SandboxSupport.Helper, args)
        {
            WorkingDirectory = _dir,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        // The helper may refuse its arguments and exit before reading stdin; writing to it then
        // fails with a broken pipe. That is the helper's answer, not a test failure: the exit code
        // and stderr below are what decide.
        await IgnoringBrokenPipeAsync(async () =>
        {
            await p.StandardInput.WriteAsync(block);
            await p.StandardInput.FlushAsync();
        });
        if (closeStdin)
        {
            await IgnoringBrokenPipeAsync(() => { p.StandardInput.Close(); return Task.CompletedTask; });
        }
        var stderr = await p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        await IgnoringBrokenPipeAsync(() => { p.StandardInput.Close(); return Task.CompletedTask; });
        return (p.ExitCode, stderr);
    }

    private static async Task IgnoringBrokenPipeAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (IOException)
        {
        }
    }

    private const string KillPrimitive = "kill_pids() { kill -KILL \"$@\" 2>/dev/null || true; }";
    private const string SelfExclusion = "[ \"$pid\" != \"$$\" ] || continue";

    /// <summary>
    /// A copy of the helper whose uid sweep runs for real (as the current user: enumeration, exclusion,
    /// repeat-until-stable) with only its signal primitive replaced. The replacement logs every pid the
    /// sweep targets to <paramref name="registry"/>.log and really kills a target only if it is listed in
    /// <paramref name="registry"/> or is the helper itself or a child of it, so the sweep can't touch the
    /// rest of the user's session, but a sweep that targets the helper still kills it (exit 137), exactly
    /// as it would as <c>_factory</c>.
    /// </summary>
    /// <param name="spareSelf">false: the mutation that drops the helper's self-exclusion (kill -1 semantics on macOS).</param>
    private string HelperWithRegistryKill(string registry, string sandboxUser, bool spareSelf = true)
    {
        var seam = "kill_pids() { local p kill_ok; printf '%s\\n' \"$@\" >>'" + registry + ".log'; "
            + "kill_ok=\" $$ $(pgrep -P $$ | tr '\\n' ' ') $(tr '\\n' ' ' <'" + registry + "') \"; "
            + "for p in \"$@\"; do case \"$kill_ok\" in *\" $p \"*) kill -KILL \"$p\" 2>/dev/null || true ;; esac; done; }";
        var source = File.ReadAllText(SandboxSupport.Helper);
        Assert.Contains(KillPrimitive, source);
        Assert.Contains(SelfExclusion, source);
        Assert.DoesNotContain("kill -KILL -1", source);
        var copy = source
            .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={sandboxUser}\n")
            .Replace(KillPrimitive, seam);
        if (!spareSelf)
        {
            copy = copy.Replace(SelfExclusion, ":");
        }
        Assert.Contains($"\nsandbox_user={sandboxUser}\n", copy);
        Assert.Contains(seam, copy);
        return SandboxSupport.Executable(_dir, "registry-helper", copy);
    }

    private static int[] Targeted(string registry) =>
        File.Exists(registry + ".log") ? File.ReadAllLines(registry + ".log").Select(int.Parse).ToArray() : [];

    private async Task<(int ExitCode, string Stdout, int HelperPid)> RunRegistryHelperAsync(string helper, params string[] args)
    {
        var psi = new ProcessStartInfo(helper, args) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stdout = await p.StandardOutput.ReadToEndAsync(); // stdin stays open: no Stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();
        return (p.ExitCode, stdout, p.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Uid_sweep_spares_the_helper_so_it_exits_with_the_workers_status(int workerExit)
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, Environment.UserName);

        var (exitCode, stdout, helperPid) = await RunRegistryHelperAsync(helper, "/bin/sh", "-c", $"echo ok; exit {workerExit}");

        Assert.Equal(workerExit, exitCode);
        Assert.Equal("ok\n", stdout);
        var targeted = Targeted(registry);
        Assert.NotEmpty(targeted); // the sweep ran over the user's processes
        Assert.DoesNotContain(helperPid, targeted);
    }

    [Fact]
    public async Task Seam_catches_a_uid_sweep_that_targets_the_helper_itself()
    {
        // The old kill -1: on macOS it signals the sender too, so the helper died with 137 after every run.
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, Environment.UserName, spareSelf: false);

        var (exitCode, stdout, helperPid) = await RunRegistryHelperAsync(helper, "/bin/sh", "-c", "echo ok; exit 0");

        Assert.Equal("ok\n", stdout);
        Assert.Contains(helperPid, Targeted(registry));
        Assert.Equal(137, exitCode);
    }

    private const string ListPrimitive =
        "list_uid_procs() { { ps -U \"$self_uid\" -o pid=,ppid=; ps -u \"$self_uid\" -o pid=,ppid=; } 2>/dev/null || true; }";
    private const string DescendantExclusion = "if (q != root) print p";

    /// <summary>
    /// The sweep's machinery as the sandbox user sees it on pass <c>$n</c>: the helper ($$, parent 1), the
    /// $(...) subshell (child of $$), ps (child of the subshell), a nested subshell and its child, and a
    /// stage run straight from $$ — all with fresh pids every pass, as on the real machine.
    /// </summary>
    private const string Machinery =
        "s=$((500000 + n * 10)); echo \"$$ 1\"; echo \"$s $$\"; echo \"$((s + 1)) $s\"; "
        + "echo \"$((s + 2)) $s\"; echo \"$((s + 3)) $((s + 2))\"; echo \"$((s + 4)) $$\"";

    /// <summary>
    /// A copy of the helper whose uid sweep sends no signals and reads no real process table: the listing
    /// is <paramref name="listing"/> (a shell snippet printing "pid ppid" lines, with the pass number in
    /// <c>$n</c>; the pass count lands in <c>passes</c>) and the kill primitive only logs to <c>targets</c>.
    /// Synthetic pids are above macOS's pid limit, so even a leaked kill could not reach a real process.
    /// </summary>
    /// <param name="descendants">false: the mutation that drops the descendant exclusion.</param>
    private string HelperWithFakeListing(string listing, bool descendants = true)
    {
        var passes = Path.Combine(_dir, "passes");
        var targets = Path.Combine(_dir, "targets");
        File.WriteAllText(passes, "0\n");
        var fakeList = "list_uid_procs() { n=$(( $(cat '" + passes + "') + 1 )); echo \"$n\" >'" + passes + "'; " + listing + "; }";
        var logOnly = "kill_pids() { printf '%s\\n' \"$@\" >>'" + targets + "'; }";
        var source = File.ReadAllText(SandboxSupport.Helper);
        Assert.Contains(KillPrimitive, source);
        Assert.Contains(ListPrimitive, source);
        Assert.Contains(DescendantExclusion, source);
        var copy = source
            .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={Environment.UserName}\n")
            .Replace(KillPrimitive, logOnly)
            .Replace(ListPrimitive, fakeList);
        if (!descendants)
        {
            copy = copy.Replace(DescendantExclusion, "print p");
        }
        // The seam must hold before anything runs: no real listing, no signal primitive in the sweep.
        Assert.Contains(logOnly, copy);
        Assert.Contains(fakeList, copy);
        Assert.DoesNotContain("ps -U", copy);
        Assert.DoesNotContain("kill -KILL \"$@\"", copy);
        return SandboxSupport.Executable(_dir, "fake-listing-helper", copy);
    }

    private async Task<(int ExitCode, string Stderr, int Passes, int[] Targets)> RunFakeListingHelperAsync(string helper)
    {
        var psi = new ProcessStartInfo(helper, ["/bin/sh", "-c", "exit 0"]) { RedirectStandardInput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();
        var targets = Path.Combine(_dir, "targets");
        return (p.ExitCode, stderr, int.Parse(File.ReadAllText(Path.Combine(_dir, "passes")).Trim()),
            File.Exists(targets) ? File.ReadAllLines(targets).Select(int.Parse).ToArray() : []);
    }

    [Fact]
    public async Task Uid_sweep_ignores_its_own_machinery_and_settles_at_once()
    {
        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(HelperWithFakeListing(Machinery));

        Assert.Equal(0, exitCode);
        Assert.Empty(targets);
        Assert.Equal(1, passes);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Seam_catches_a_uid_sweep_that_counts_its_own_machinery()
    {
        // The bug seen as _factory: "processes still appearing after 20 sweeps" after every run.
        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(HelperWithFakeListing(Machinery, descendants: false));

        Assert.Equal(0, exitCode);
        Assert.Equal(20, passes);
        Assert.Contains("still appearing after 20 sweeps", stderr);
        Assert.Contains(500011, targets); // pass 1's ps
    }

    [Fact]
    public async Task Uid_sweep_kills_reparented_survivors_and_settles()
    {
        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(
            HelperWithFakeListing(Machinery + "; echo '400001 1'; echo '400002 400001'"));

        Assert.Equal(0, exitCode);
        Assert.Equal([400001, 400002], targets.Order());
        Assert.Equal(2, passes); // the second pass finds only what was already signalled
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task Uid_sweep_gives_up_with_a_warning_on_survivors_that_keep_appearing()
    {
        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(
            HelperWithFakeListing(Machinery + "; echo \"$((600000 + n)) 1\""));

        Assert.Equal(0, exitCode);
        Assert.Equal(20, passes);
        Assert.Equal(Enumerable.Range(600001, 20), targets.Order());
        Assert.Contains("still appearing after 20 sweeps", stderr);
    }

    /// <summary>
    /// perl: double-fork a grandchild that setsid()s away — a new session and process group, reparented
    /// to launchd, so neither the tree kill nor the group kill can reach it (as a detached build server
    /// would be) — records its pid in the registry and sleeps. The worker prints that pid, then sleeps
    /// <paramref name="parentSleeps"/> seconds (0: exits at once).
    /// </summary>
    private static string[] SetsidGrandchild(string registry, int parentSleeps = 0) =>
    [
        "/usr/bin/perl", "-MPOSIX", "-e",
        "$| = 1; my $r = shift; my $pid = fork; "
        + "if (!$pid) { exit 0 if fork; POSIX::setsid(); open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); "
        + "open(my $f, '>>', $r); print $f \"$$\\n\"; close $f; exec('/bin/sleep', '600'); } "
        + "waitpid($pid, 0); select(undef, undef, undef, 0.05) until -s $r; "
        + "open(my $g, '<', $r); my $gc = <$g>; print $gc; sleep(shift);",
        registry, parentSleeps.ToString(),
    ];

    [Fact]
    public async Task Helper_kills_a_setsid_grandchild_through_the_sandbox_user_kill_when_the_worker_exits()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, Environment.UserName);

        var psi = new ProcessStartInfo(helper, SetsidGrandchild(registry)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();

        Assert.Equal(0, p.ExitCode);
        Assert.Contains(grandchild, Targeted(registry));
        Assert.DoesNotContain(p.Id, Targeted(registry));
        await SandboxSupport.WaitUntilDeadAsync(grandchild);
    }

    [Fact]
    public async Task Stopping_through_stdin_kills_a_setsid_grandchild_of_a_running_worker()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, Environment.UserName);

        var psi = new ProcessStartInfo(helper, SetsidGrandchild(registry, parentSleeps: 600)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        Assert.True(SandboxSupport.IsAlive(grandchild));

        p.StandardInput.Close(); // Stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);

        Assert.Equal(137, p.ExitCode); // the killed worker's status, reported by a helper that survived its sweep
        Assert.Contains(grandchild, Targeted(registry));
        Assert.DoesNotContain(p.Id, Targeted(registry));
        await SandboxSupport.WaitUntilDeadAsync(grandchild);
    }

    [Fact]
    public async Task Helper_never_kills_by_uid_unless_it_runs_as_the_sandbox_user()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, "_factory"); // we are not _factory
        var psi = new ProcessStartInfo(helper, SetsidGrandchild(registry)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token);
            p.StandardInput.Close();
            await Task.Delay(500);

            Assert.True(SandboxSupport.IsAlive(grandchild), "the helper signalled by uid while not the sandbox user");
            Assert.Empty(Targeted(registry));
        }
        finally
        {
            SandboxSupport.KillQuietly(grandchild);
        }
    }

    [Fact]
    public async Task Closing_the_helpers_stdin_kills_the_whole_worker_tree()
    {
        using var p = LocalSandbox().Start(_dir, "/bin/sh", ["-c", "sleep 600 & echo $!; echo $$; wait"], new Dictionary<string, string>());
        var child = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        var shell = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        Assert.True(SandboxSupport.IsAlive(child) && SandboxSupport.IsAlive(shell));

        WorkerSandbox.Stop(p);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);

        await SandboxSupport.WaitUntilDeadAsync(child, shell);
    }

    [Fact]
    public async Task Background_processes_do_not_outlive_the_worker()
    {
        using var p = LocalSandbox().Start(_dir, "/bin/sh", ["-c", "sleep 600 >/dev/null 2>&1 & echo $!"], new Dictionary<string, string>());
        var orphan = int.Parse((await p.StandardOutput.ReadLineAsync())!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        WorkerSandbox.Stop(p);

        Assert.Equal(0, p.ExitCode);
        await SandboxSupport.WaitUntilDeadAsync(orphan);
    }

    [Fact]
    public async Task Sandboxed_claude_worker_runs_through_the_helper_with_only_router_variables()
    {
        var envDump = Path.Combine(_dir, "env.txt");
        // perl adds no variables of its own, so this is exactly what claude would see.
        var claude = SandboxSupport.Executable(_dir, "fake-claude.pl", $$"""
            #!/usr/bin/perl
            open(my $f, '>', '{{envDump}}'); print $f join("\n", sort keys %ENV); close $f;
            print qq({"type":"system","subtype":"init","session_id":"sess-sbx"}\n);
            print qq({"type":"result","subtype":"success","is_error":false,"session_id":"sess-sbx"}\n);
            """);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-should-not-leak");
        try
        {
            var worker = new ClaudeWorker(claude, SandboxSupport.Router, "rk_worker", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1), LocalSandbox());
            var result = await worker.RunAsync(_dir, "prompt", null, null, CancellationToken.None);

            Assert.True(result.Succeeded, result.StderrTail);
            Assert.Equal("sess-sbx", result.SessionId);
            Assert.Equal(["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH"], File.ReadAllText(envDump).Split('\n'));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public async Task Cancelling_a_sandboxed_worker_stops_it_through_the_helper()
    {
        var pidFile = Path.Combine(_dir, "pid");
        var claude = SandboxSupport.Executable(_dir, "slow-claude.sh",
            $"#!/bin/sh\necho $$ > '{pidFile}'\necho '{{\"type\":\"system\",\"session_id\":\"s\"}}'\nsleep 600\n");
        var worker = new ClaudeWorker(claude, SandboxSupport.Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(5), LocalSandbox());
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(_dir, "p", null, null, cts.Token);
        var pid = await SandboxSupport.WaitForPidAsync(pidFile, run);

        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(WorkerStillRunning.IsMarked(ex)); // stopped: its worktree may go
        await SandboxSupport.WaitUntilDeadAsync(pid);
    }

    [Fact]
    public async Task A_sandboxed_worker_that_will_not_stop_still_cancels_but_is_marked_still_running()
    {
        var pidFile = Path.Combine(_dir, "stuck-pid");
        // A "sudo" that ignores its stdin closing, as a wedged helper would.
        var stuckSudo = SandboxSupport.Executable(_dir, "stuck-sudo.sh",
            $"#!/bin/sh\necho $$ > '{pidFile}'\necho '{{\"type\":\"system\",\"session_id\":\"s\"}}'\nexec /bin/sleep 600 </dev/null\n");
        var sandbox = new WorkerSandbox("_factory", SandboxSupport.Helper, stuckSudo);
        var worker = new ClaudeWorker("claude", SandboxSupport.Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(5), sandbox,
            stopGrace: TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(_dir, "p", null, null, cts.Token);
        var pid = await SandboxSupport.WaitForPidAsync(pidFile, run);
        try
        {
            await cts.CancelAsync();

            // Still Ctrl-C to the caller (not an ordinary failure), flagged so nobody deletes the worktree under it.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(WorkerStillRunning.IsMarked(ex));
            Assert.True(SandboxSupport.IsAlive(pid));
        }
        finally
        {
            SandboxSupport.KillQuietly(pid);
        }
    }

    [Fact]
    public async Task Share_refuses_a_non_empty_directory()
    {
        var path = Directory.CreateDirectory(Path.Combine(_dir, "checkout")).FullName;
        File.WriteAllText(Path.Combine(path, "README.md"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WorkerSandbox(Environment.UserName, WorkerSandbox.DefaultHelperPath).ShareAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Sandboxed_orphan_stop_kills_the_worker_users_processes_whatever_pid_was_recorded()
    {
        // The recorded pid is sudo's (root-owned, and gone after a crash); the orphan is _factory's.
        var registry = Path.Combine(_dir, "uid-pids");
        var helper = HelperWithRegistryKill(registry, Environment.UserName);
        var orphan = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e", "POSIX::setsid(); exec('/bin/sleep', '600')"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        File.WriteAllText(registry, $"{orphan.Id}\n");
        var worker = new ClaudeWorker("claude", SandboxSupport.Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1),
            new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir)));

        Assert.True(await worker.StopOrphanAsync(999_999, CancellationToken.None));
        await SandboxSupport.WaitUntilDeadAsync(orphan.Id);
    }

    [Fact]
    public async Task Stop_all_runs_nothing_when_the_worker_user_has_no_processes()
    {
        var marker = Path.Combine(_dir, "helper-ran");
        var sudo = SandboxSupport.Executable(_dir, "marking-sudo.sh", $"#!/bin/sh\ntouch '{marker}'\nexit 0\n");

        Assert.False(await new WorkerSandbox("_df_no_such_user", SandboxSupport.Helper, sudo).StopAllAsync(CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Stop_all_kills_the_worker_users_processes_through_the_helper()
    {
        // As in the helper tests: the uid-wide kill narrowed to a registry, run as the current user.
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, Environment.UserName);
        var sleeper = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e", "POSIX::setsid(); exec('/bin/sleep', '600')"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        File.WriteAllText(registry, $"{sleeper.Id}\n");

        var stopped = await new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir)).StopAllAsync(CancellationToken.None);

        Assert.True(stopped);
        await SandboxSupport.WaitUntilDeadAsync(sleeper.Id);
    }

    [Fact]
    public async Task Share_grants_inheritable_acl_entries_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        var path = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        await new WorkerSandbox(Environment.UserName, WorkerSandbox.DefaultHelperPath).ShareAsync(path, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(path, "later"));

        var psi = new ProcessStartInfo("/bin/ls", ["-led", Path.Combine(path, "later")]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var listing = await p.StandardOutput.ReadToEndAsync();
        Assert.Contains($"user:{Environment.UserName} inherited allow", listing);
        Assert.Contains("delete_child", listing);
    }

    private WorkerSandbox LocalSandbox() => SandboxSupport.LocalSandbox(_dir);
}

/// <summary>
/// Live probes against the real sandbox user. Opt-in only (FACTORY_SANDBOX_LIVE=1): the installed helper
/// kills every process of that user, including an interactive <c>_factory</c> session such as its Claude
/// login. They also skip unless this Mac has run <c>scripts/setup-worker-user.sh</c>.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class LiveWorkerSandboxTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");

    private static async Task<WorkerSandbox> RequireSandboxAsync()
    {
        // Before anything touches the helper: even the readiness probe ends in its uid-wide kill.
        if (Environment.GetEnvironmentVariable("FACTORY_SANDBOX_LIVE") != "1")
        {
            Assert.Skip("Live sandbox test: set FACTORY_SANDBOX_LIVE=1 to run (kills every _factory process).");
        }
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The worker sandbox is macOS-only.");
        }
        var sandbox = new WorkerSandbox(WorkerSandbox.DefaultUser, WorkerSandbox.DefaultHelperPath);
        try
        {
            await sandbox.EnsureReadyAsync(new FactoryOptions(FactoryOptions.LoadConfiguration(), new Support.InMemorySecrets()).WorkerAuth, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
        }
        return sandbox;
    }

    private static readonly string OwnerHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Theory]
    [InlineData("/bin/ls", "")]
    [InlineData("/bin/ls", ".ssh")]
    [InlineData("/bin/ls", ".config/gh")]
    [InlineData("/bin/cat", "Library/Keychains/login.keychain-db")]
    public async Task Worker_is_denied_the_owners_home_ssh_keys_gh_config_and_login_keychain(string program, string relative)
    {
        var sandbox = await RequireSandboxAsync();

        var (exitCode, _, stderr) = await sandbox.RunAsync("/", program, [Path.Combine(OwnerHome, relative)], CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Permission denied", stderr);
    }

    /// <summary>
    /// AT6 (docs/acceptance.md): the installed helper, launched exactly as <see cref="ClaudeWorker"/> launches a sandboxed
    /// worker (<see cref="WorkerSandbox.Start"/> with <see cref="ClaudeWorker.BuildRouterVariables"/> for the configured
    /// router URL and <c>Worker:Auth</c>), gives the worker exactly the allowlisted environment, and nothing of the owner's.
    /// The router key is a probe value: the real key never needs to appear in test output.
    /// </summary>
    [Fact]
    public async Task Live_worker_environment_is_exactly_the_allowlist_for_the_configured_auth_mode()
    {
        var sandbox = await RequireSandboxAsync();
        var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new Support.InMemorySecrets());
        var auth = options.WorkerAuth;
        string[] expected = auth == WorkerAuth.RouterKey
            ? ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH"]
            : ["ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH"];
        const string probeKey = "rk_live_env_probe";
        Environment.SetEnvironmentVariable("GH_TOKEN", "ghp_owner_should_not_leak");
        try
        {
            using var p = sandbox.Start("/", "/usr/bin/env", [], ClaudeWorker.BuildRouterVariables(options.RouterBaseUrl, probeKey, auth));
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            WorkerSandbox.Stop(p);

            Assert.Equal(0, p.ExitCode);
            var env = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            Assert.Equal(expected, env.Keys.Order());
            Assert.Equal(options.RouterBaseUrl.ToString().TrimEnd('/'), env["ANTHROPIC_BASE_URL"].TrimEnd('/'));
            Assert.Equal($"{ClaudeWorker.RouterKeyHeader}: {probeKey}", env["ANTHROPIC_CUSTOM_HEADERS"]);
            if (auth == WorkerAuth.RouterKey)
            {
                Assert.Equal(probeKey, env["ANTHROPIC_AUTH_TOKEN"]);
            }
            Assert.DoesNotContain(OwnerHome, env["HOME"]);
            Assert.StartsWith(env["HOME"] + "/.local/bin:", env["PATH"]);
            Assert.DoesNotContain(OwnerHome, env["PATH"]);
            Assert.Equal(("1", "0"), (env["MSBUILDDISABLENODEREUSE"], env["DOTNET_CLI_USE_MSBUILD_SERVER"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", null);
        }
    }

    [Fact]
    public async Task Worker_cannot_read_the_owners_keychain_items()
    {
        var sandbox = await RequireSandboxAsync();

        var (exitCode, stdout, _) = await sandbox.RunAsync("/", "/usr/bin/security",
            ["find-generic-password", "-s", SecretAccounts.Service, "-w", Path.Combine(OwnerHome, "Library/Keychains/login.keychain-db")],
            CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(stdout.Trim());
    }

    /// <summary>
    /// AT6, <c>Worker:Auth=router-key</c> (E5): the worker user holds no Claude credential of its own — neither Claude
    /// Code's credentials file nor its keychain item (attributes only are looked up; no secret is read). Removed by
    /// <c>sudo scripts/setup-worker-user.sh --remove-worker-login</c>. Skips in <c>claude-login</c> mode, which needs one.
    /// </summary>
    [Fact]
    public async Task Worker_user_holds_no_claude_credential_in_router_key_mode()
    {
        var sandbox = await RequireSandboxAsync();
        if (new FactoryOptions(FactoryOptions.LoadConfiguration(), new Support.InMemorySecrets()).WorkerAuth != WorkerAuth.RouterKey)
        {
            Assert.Skip("Worker:Auth=claude-login: the worker user is expected to hold its own Claude login.");
        }

        var (exitCode, stdout, stderr) = await sandbox.RunAsync("/", "/bin/sh",
            ["-c", """
                found=
                if test -e "$HOME/.claude/.credentials.json"; then found="$found $HOME/.claude/.credentials.json"; fi
                kc="$HOME/Library/Keychains/login.keychain-db"
                if test -e "$kc" && /usr/bin/security find-generic-password -s 'Claude Code-credentials' "$kc" >/dev/null 2>&1; then
                    found="$found keychain:Claude Code-credentials"
                fi
                echo "found:$found"
                """],
            CancellationToken.None);

        Assert.True(exitCode == 0, stderr);
        Assert.Equal("found:", stdout.Trim());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("stop")]
    public async Task Worktree_written_by_the_worker_is_gone_after_the_run_ends(string ending)
    {
        var sandbox = await RequireSandboxAsync();
        var ct = CancellationToken.None;
        var workRoot = Path.Combine(FactoryOptions.DefaultSandboxWorkRoot, $"test-{Guid.NewGuid():N}");
        var remote = Path.Combine(workRoot, "remote.git");
        Directory.CreateDirectory(workRoot);
        try
        {
            SeedRemote(workRoot, remote);
            var git = new GitWorkspace(workRoot, _ => remote, (_, _) => Task.FromResult<string?>(null), sandbox: sandbox);
            var ws = await git.PrepareAsync(Repo, "factory/sc-1", ct);

            // The worker writes into its worktree, including a directory it then locks down.
            var (exitCode, _, stderr) = await sandbox.RunAsync(ws.Path, "/bin/sh",
                ["-c", "echo change >> README.md && mkdir -p obj/deep && echo x > obj/deep/f && chmod 500 obj/deep"], ct);
            Assert.True(exitCode == 0, stderr);

            switch (ending)
            {
                case "success":
                    Assert.True(await git.CommitAndPushAsync(Repo, ws, "sc-1: change", ct)); // owner reads the worker's files
                    break;
                case "stop":
                    using (var p = sandbox.Start(ws.Path, "/bin/sh", ["-c", "sleep 600 & echo $!; wait"], new Dictionary<string, string>()))
                    {
                        var pid = int.Parse((await p.StandardOutput.ReadLineAsync(ct))!);
                        WorkerSandbox.Stop(p);
                        await p.WaitForExitAsync(ct);
                        await SandboxSupport.WaitUntilDeadAsync(pid);
                    }
                    break;
            }

            await git.RemoveAsync(Repo, ws, ct);

            Assert.False(Directory.Exists(ws.Path));
        }
        finally
        {
            await sandbox.DeleteAsWorkerAsync(workRoot, ct);
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_setsid_grandchild_of_the_worker_is_dead_once_the_helper_exits()
    {
        var sandbox = await RequireSandboxAsync();
        var ct = CancellationToken.None;

        using var p = sandbox.Start("/", "/usr/bin/perl",
            ["-MPOSIX", "-e", "$| = 1; my $pid = fork; if (!$pid) { exit 0 if fork; POSIX::setsid(); print \"$$\\n\"; open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); exec('/bin/sleep', '600'); } waitpid($pid, 0);"],
            new Dictionary<string, string>());
        var grandchild = int.Parse((await p.StandardOutput.ReadLineAsync(ct))!);
        await p.WaitForExitAsync(ct);
        WorkerSandbox.Stop(p);

        // The owner can't signal _factory's processes, so ask ps whether it still exists.
        for (var i = 0; i < 50 && ProcessExists(grandchild); i++)
        {
            await Task.Delay(100, ct);
        }
        Assert.False(ProcessExists(grandchild), $"setsid grandchild {grandchild} outlived the helper");
        Assert.False(await sandbox.StopAllAsync(ct)); // nothing of _factory's left
    }

    private static bool ProcessExists(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-p", pid.ToString()]) { RedirectStandardOutput = true })!;
        ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return ps.ExitCode == 0;
    }

    private static void SeedRemote(string root, string remote)
    {
        var seed = Path.Combine(root, "seed");
        Run(root, "git", "init", "--bare", "-b", "main", remote);
        Run(root, "git", "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Run(seed, "git", "add", ".");
        Run(seed, "git", "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Run(seed, "git", "push", remote, "main");
    }

    private static void Run(string cwd, string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program, args) { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{program} {string.Join(' ', args)}: {error}");
    }
}
