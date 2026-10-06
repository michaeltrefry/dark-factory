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
    [InlineData(WorkerAuth.RouterKey, new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "DOTNET_CLI_USE_MSBUILD_SERVER", "HOME", "MSBUILDDISABLENODEREUSE", "PATH" })]
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

    /// <summary>
    /// A copy of the helper whose "every process of the sandbox user" (kill -1) is narrowed to the pids
    /// listed in <paramref name="registry"/>, so the uid-wide kill can be exercised as the current user
    /// without signalling anything else of theirs.
    /// </summary>
    private string HelperWithRegistryKill(string registry, string sandboxUser)
    {
        var source = File.ReadAllText(SandboxSupport.Helper);
        var copy = source
            .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={sandboxUser}\n")
            .Replace("kill -KILL -1 ", $"kill -KILL $(cat '{registry}') ");
        Assert.Contains($"\nsandbox_user={sandboxUser}\n", copy);
        Assert.Contains($"kill -KILL $(cat '{registry}') ", copy);
        Assert.DoesNotContain("kill -KILL -1", copy);
        return SandboxSupport.Executable(_dir, "registry-helper", copy);
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
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
        {
            await Task.Delay(50);
        }
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());

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
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
        {
            await Task.Delay(50);
        }
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
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
/// Live probes against the real sandbox user. They skip unless this Mac has run
/// <c>scripts/setup-worker-user.sh</c> (user, helper and sudoers rule in place).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class LiveWorkerSandboxTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");

    private static async Task<WorkerSandbox> RequireSandboxAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The worker sandbox is macOS-only.");
        }
        var sandbox = new WorkerSandbox(WorkerSandbox.DefaultUser, WorkerSandbox.DefaultHelperPath);
        try
        {
            await sandbox.EnsureReadyAsync(CancellationToken.None);
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
