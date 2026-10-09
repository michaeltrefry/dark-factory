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

    /// <summary>Stands in for <c>sudo -n -u &lt;user&gt;</c>: checks those arguments, then runs the rest as the current user.</summary>
    public static string FakeSudo(string dir) => Executable(dir, "fake-sudo.sh",
        "#!/bin/sh\n[ \"$1\" = -n ] && [ \"$2\" = -u ] || exit 99\nshift 3\nexec \"$@\"\n");

    /// <summary>
    /// A sandbox whose helper is a <see cref="SafeHelper"/> copy (as <c>_factory</c>, which we are not: no uid sweep), with
    /// <see cref="LocalRegistry"/> as its registry.
    /// </summary>
    public static WorkerSandbox LocalSandbox(string dir) =>
        new("_factory", SafeHelper.Create(dir, new SafeHelperOptions { Registry = LocalRegistry(dir) }).Path, FakeSudo(dir));

    /// <summary>Where a worker of <see cref="LocalSandbox"/> records ("pid|start") a process it leaves behind for the helper to kill.</summary>
    public static string LocalRegistry(string dir) => Path.Combine(dir, "local-registry");

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

    /// <summary>
    /// The process whose "pid|start" record (<see cref="OwnProcess.ShellRecord"/>) a worker script wrote to
    /// <paramref name="pidFile"/>. Startup through the launch helper can take seconds on a loaded machine, so this waits up
    /// to 60 s (failing at once if <paramref name="run"/> ends first) and only accepts a complete line.
    /// </summary>
    public static async Task<OwnProcess> WaitForPidAsync(string pidFile, Task run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (File.Exists(pidFile) && File.ReadAllText(pidFile) is var text && text.EndsWith('\n') && text.Contains('|'))
            {
                return OwnProcess.Parse(text);
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
        Assert.Contains("factory-worker-launch:", stderr);
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
        var psi = new ProcessStartInfo(SafeHelper.Create(_dir).Path, args)
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

    private const string SelfExclusion = "[ \"$pid\" != \"$$\" ] || continue";

    /// <summary>
    /// A <see cref="SafeHelper"/> copy whose uid sweep runs (by default as the sandbox role
    /// account the sweep's checks expect) over the real process table restricted to this test host's processes and the
    /// <paramref name="registry"/>. The seam really kills only the helper, its tree and registry pids, so a sweep that targets
    /// the helper still kills it (exit 137), exactly as it would as <c>_factory</c>.
    /// </summary>
    /// <param name="sandboxRole">false: the real <c>id</c> and <c>sandbox_user=_factory</c> (we are not).</param>
    /// <param name="spareSelf">false: the mutation that drops the helper's self-exclusion (kill -1 semantics on macOS).</param>
    private SafeHelper HelperWithRegistryKill(string registry, bool sandboxRole = true, bool spareSelf = true)
    {
        Assert.True(SafeHelper.SourceContains(SelfExclusion));
        Assert.False(SafeHelper.SourceContains("kill -KILL -1"));
        return SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Identity = sandboxRole ? FakeIdentity.SandboxRole : null,
            Registry = registry,
            Mutate = spareSelf ? null : copy => copy.Replace(SelfExclusion, ":"),
        });
    }

    private static async Task<(int ExitCode, string Stdout, int HelperPid)> RunRegistryHelperAsync(SafeHelper helper, params string[] args)
    {
        var psi = new ProcessStartInfo(helper.Path, args) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stdout = await p.StandardOutput.ReadToEndAsync(); // stdin stays open: no Stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();
        return (p.ExitCode, stdout, p.Id);
    }

    /// <summary>A process of the test's own in a new session (perl setsid; sleeps 600 s).</summary>
    private static Process StartSetsidSleeper() =>
        Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e", "POSIX::setsid(); exec('/bin/sleep', '600')"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Uid_sweep_spares_the_helper_so_it_exits_with_the_workers_status(int workerExit)
    {
        var registry = Path.Combine(_dir, "uid-pids");
        using var sleeper = StartSetsidSleeper();
        var own = OwnProcess.Of(sleeper);
        SafeHelper.Register(registry, own);
        var helper = HelperWithRegistryKill(registry);

        var (exitCode, stdout, helperPid) = await RunRegistryHelperAsync(helper, "/bin/sh", "-c", $"echo ok; exit {workerExit}");

        Assert.Equal(workerExit, exitCode);
        Assert.Equal("ok\n", stdout);
        Assert.Contains(sleeper.Id, helper.Targets); // the sweep ran over the user's processes
        Assert.DoesNotContain(helperPid, helper.Targets);
        Assert.Empty(helper.Refusals);
        await OwnProcess.WaitUntilDeadAsync(own);
    }

    [Fact]
    public async Task Seam_catches_a_uid_sweep_that_targets_the_helper_itself()
    {
        // The old kill -1: on macOS it signals the sender too, so the helper died with 137 after every run.
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, spareSelf: false);

        var (exitCode, stdout, helperPid) = await RunRegistryHelperAsync(helper, "/bin/sh", "-c", "echo ok; exit 0");

        Assert.Equal("ok\n", stdout);
        Assert.Contains(helperPid, helper.Targets);
        Assert.Equal(137, exitCode);
    }

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
    /// A <see cref="SafeHelper"/> copy whose uid sweep reads no real process table: the listing is <paramref name="listing"/>
    /// (a shell snippet printing "pid ppid" lines, with the pass number in <c>$n</c>). Synthetic pids are above macOS's pid
    /// limit, so the seam can only log them.
    /// </summary>
    /// <param name="descendants">false: the mutation that drops the descendant exclusion.</param>
    private SafeHelper HelperWithFakeListing(string listing, bool descendants = true, FakeIdentity? identity = null)
    {
        Assert.True(SafeHelper.SourceContains(DescendantExclusion));
        return SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Identity = identity ?? FakeIdentity.SandboxRole,
            FakeListing = listing,
            Mutate = descendants ? null : copy => copy.Replace(DescendantExclusion, "print p"),
        });
    }

    private static async Task<(int ExitCode, string Stderr, int Passes, int[] Targets)> RunFakeListingHelperAsync(
        SafeHelper helper, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(helper.Path, ["/bin/sh", "-c", "exit 0"]) { RedirectStandardInput = true, RedirectStandardError = true };
        psi.Environment.Remove("SUDO_USER");
        psi.Environment.Remove("SUDO_UID");
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();
        return (p.ExitCode, stderr, helper.Passes, helper.Targets);
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
    /// The helper's second sweep check (by uid), through a copy with a fake <c>id</c> whose name check passes: unless the
    /// pinned <c>sandbox_uid</c> is set, in 400-499 (so not root, a login account such as the owner's, or another role
    /// account such as _spotlight), still <c>sandbox_user</c>'s uid and this helper's, and not the uid of whoever invoked
    /// sudo, the sweep is refused before the process table is ever read, and nothing is signalled.
    /// </summary>
    [Theory]
    [InlineData("michael", "501", "michael=501", "501", "", "", "pinned uid 501 is outside 400-499")] // sandbox_user renamed to the owner
    [InlineData("_spotlight", "89", "_spotlight=89", "89", "", "", "pinned uid 89 is outside 400-499")] // another role account
    [InlineData("_dftest", "0", "_dftest=0", "0", "", "", "pinned uid 0 is outside 400-499")] // root
    [InlineData("_dftest", "450", "_dftest=450", "", "", "", "no sandbox_uid pinned")] // never installed by setup
    [InlineData("_dftest", "450", "_dftest=451", "450", "", "", "_dftest's uid 451 is not the pinned 450")]
    [InlineData("_dftest", "452", "_dftest=450", "450", "", "", "effective uid 452 is not _dftest's (450)")]
    [InlineData("_dftest", "450", "", "450", "", "", "_dftest has no uid")]
    [InlineData("_dftest", "450", "_dftest=450", "450", "", "450", "SUDO_UID")]
    [InlineData("_dftest", "450", "_dftest=450,owner=450", "450", "owner", "", "invoking user owner's")]
    [InlineData("_dftest", "450", "_dftest=450", "450", "ghost", "", "invoking user ghost's")] // fails closed
    public async Task Uid_sweep_refuses_unless_it_runs_as_the_sandbox_role_accounts_own_uid(
        string user, string uid, string uids, string pinned, string sudoUser, string sudoUid, string reason)
    {
        var identity = new FakeIdentity(user, uid, uids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(e => e.Split('=')[0], e => e.Split('=')[1]), pinned);
        var env = new Dictionary<string, string>();
        if (sudoUser != "")
        {
            env["SUDO_USER"] = sudoUser;
        }
        if (sudoUid != "")
        {
            env["SUDO_UID"] = sudoUid;
        }
        var helper = HelperWithFakeListing(Machinery + "; echo '400001 1'", identity: identity);

        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(helper, env);

        Assert.Equal(0, exitCode);
        Assert.Contains($"refusing the {user} uid sweep", stderr);
        Assert.Contains(reason, stderr);
        Assert.Equal(0, passes); // the process table was never read
        Assert.Empty(targets);
        Assert.Empty(helper.Refusals);
    }

    [Fact]
    public async Task Uid_sweep_runs_as_the_sandbox_role_account_invoked_through_sudo_by_the_owner()
    {
        // The control for the refusals above: the production case (owner 501 sudo's to the role account 450).
        var identity = new FakeIdentity("_dftest", "450", new Dictionary<string, string> { ["_dftest"] = "450", ["owner"] = "501" }, "450");
        var helper = HelperWithFakeListing(Machinery + "; echo '400001 1'", identity: identity);

        var (exitCode, stderr, passes, targets) = await RunFakeListingHelperAsync(helper, new Dictionary<string, string> { ["SUDO_USER"] = "owner", ["SUDO_UID"] = "501" });

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr);
        Assert.Equal(2, passes);
        Assert.Equal([400001], targets);
    }

    /// <summary>
    /// perl: double-fork a grandchild that setsid()s away — a new session and process group, reparented
    /// to launchd, so neither the tree kill nor the group kill can reach it (as a detached build server
    /// would be) — records "pid|start time" in the registry and sleeps. The worker prints that record, then
    /// sleeps <paramref name="parentSleeps"/> seconds (0: exits at once).
    /// </summary>
    private static string[] SetsidGrandchild(string registry, int parentSleeps = 0) =>
    [
        "/usr/bin/perl", "-MPOSIX", "-e",
        "$| = 1; my $r = shift; my $pid = fork; "
        + "if (!$pid) { exit 0 if fork; POSIX::setsid(); open(STDOUT, '>', '/dev/null'); open(STDERR, '>', '/dev/null'); "
        + "my $ls = `/bin/ps -o lstart= -p $$`; $ls =~ s/\\s+/ /g; $ls =~ s/^ | $//g; "
        + "open(my $f, '>>', $r); print $f \"$$|$ls\\n\"; close $f; exec('/bin/sleep', '600'); } "
        + "waitpid($pid, 0); select(undef, undef, undef, 0.05) until -s $r; "
        + "open(my $g, '<', $r); print scalar(<$g>); sleep(shift);",
        registry, parentSleeps.ToString(),
    ];

    [Fact]
    public async Task Helper_kills_a_setsid_grandchild_through_the_sandbox_user_kill_when_the_worker_exits()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry);

        var psi = new ProcessStartInfo(helper.Path, SetsidGrandchild(registry)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        p.StandardInput.Close();

        Assert.Equal(0, p.ExitCode);
        Assert.Contains(grandchild.Pid, helper.Targets);
        Assert.DoesNotContain(p.Id, helper.Targets);
        await OwnProcess.WaitUntilDeadAsync(grandchild);
    }

    [Fact]
    public async Task Stopping_through_stdin_kills_a_setsid_grandchild_of_a_running_worker()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry);

        var psi = new ProcessStartInfo(helper.Path, SetsidGrandchild(registry, parentSleeps: 600)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        Assert.True(grandchild.IsAlive);

        p.StandardInput.Close(); // Stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);

        Assert.Equal(137, p.ExitCode); // the killed worker's status, reported by a helper that survived its sweep
        Assert.Contains(grandchild.Pid, helper.Targets);
        Assert.DoesNotContain(p.Id, helper.Targets);
        await OwnProcess.WaitUntilDeadAsync(grandchild);
    }

    [Fact]
    public async Task Helper_never_kills_by_uid_unless_it_runs_as_the_sandbox_user()
    {
        var registry = Path.Combine(_dir, "uid-pids");
        File.WriteAllText(registry, "");
        var helper = HelperWithRegistryKill(registry, sandboxRole: false); // sandbox_user=_factory, and we are not
        var psi = new ProcessStartInfo(helper.Path, SetsidGrandchild(registry)) { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var grandchild = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token);
            p.StandardInput.Close();
            await Task.Delay(500);

            Assert.True(grandchild.IsAlive, "the helper signalled by uid while not the sandbox user");
            Assert.Empty(helper.Targets);
        }
        finally
        {
            grandchild.KillIfStillRunning();
        }
    }

    [Fact]
    public async Task Closing_the_helpers_stdin_kills_the_whole_worker_tree()
    {
        using var p = LocalSandbox().Start(_dir, "/bin/sh",
            ["-c", $"sleep 600 & echo {OwnProcess.ShellRecord("$!")}; echo {OwnProcess.ShellRecord("$$")}; wait"], new Dictionary<string, string>());
        var child = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        var shell = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        Assert.True(child.IsAlive && shell.IsAlive);

        WorkerSandbox.Stop(p);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);

        await OwnProcess.WaitUntilDeadAsync(child, shell);
    }

    [Fact]
    public async Task Background_processes_do_not_outlive_the_worker()
    {
        // Reparented to launchd once the worker exits, so the seam allows the worker's group kill only because the worker
        // registered it (its own child, recorded before it exits).
        var registry = SandboxSupport.LocalRegistry(_dir);
        using var p = LocalSandbox().Start(_dir, "/bin/sh",
            ["-c", $"sleep 600 >/dev/null 2>&1 & r={OwnProcess.ShellRecord("$!")}; echo \"$r\" >>'{registry}'; echo \"$r\""],
            new Dictionary<string, string>());
        var orphan = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync())!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        WorkerSandbox.Stop(p);

        Assert.Equal(0, p.ExitCode);
        await OwnProcess.WaitUntilDeadAsync(orphan);
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
            $"#!/bin/sh\necho {OwnProcess.ShellRecord("$$")} > '{pidFile}'\necho '{{\"type\":\"system\",\"session_id\":\"s\"}}'\nsleep 600\n");
        var worker = new ClaudeWorker(claude, SandboxSupport.Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(5), LocalSandbox());
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(_dir, "p", null, null, cts.Token);
        var pid = await SandboxSupport.WaitForPidAsync(pidFile, run);

        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(WorkerStillRunning.IsMarked(ex)); // stopped: its worktree may go
        await OwnProcess.WaitUntilDeadAsync(pid);
    }

    [Fact]
    public async Task A_sandboxed_worker_that_will_not_stop_still_cancels_but_is_marked_still_running()
    {
        var pidFile = Path.Combine(_dir, "stuck-pid");
        // A "sudo" that ignores its stdin closing, as a wedged helper would.
        var stuckSudo = SandboxSupport.Executable(_dir, "stuck-sudo.sh",
            $"#!/bin/sh\necho {OwnProcess.ShellRecord("$$")} > '{pidFile}'\necho '{{\"type\":\"system\",\"session_id\":\"s\"}}'\nexec /bin/sleep 600 </dev/null\n");
        var sandbox = new WorkerSandbox("_factory", SafeHelper.Create(_dir).Path, stuckSudo);
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
            Assert.True(pid.IsAlive);
        }
        finally
        {
            pid.KillIfStillRunning();
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
        var helper = HelperWithRegistryKill(registry);
        using var orphanProcess = StartSetsidSleeper();
        var orphan = OwnProcess.Of(orphanProcess);
        SafeHelper.Register(registry, orphan);
        var worker = new ClaudeWorker("claude", SandboxSupport.Router, "k", WorkerAuth.ClaudeLogin, TimeSpan.FromMinutes(1),
            new WorkerSandbox(Environment.UserName, helper.Path, SandboxSupport.FakeSudo(_dir)));

        Assert.True(await worker.StopOrphanAsync(999_999, CancellationToken.None));
        await OwnProcess.WaitUntilDeadAsync(orphan);
    }

    [Fact]
    public async Task Stop_all_runs_nothing_when_the_worker_user_has_no_processes()
    {
        var marker = Path.Combine(_dir, "helper-ran");
        var sudo = SandboxSupport.Executable(_dir, "marking-sudo.sh", $"#!/bin/sh\ntouch '{marker}'\nexit 0\n");

        Assert.False(await new WorkerSandbox("_df_no_such_user", SafeHelper.Create(_dir).Path, sudo).StopAllAsync(CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Stop_all_kills_the_worker_users_processes_through_the_helper()
    {
        // As in the helper tests: the uid-wide kill narrowed to a registry, run as the current user.
        var registry = Path.Combine(_dir, "uid-pids");
        var helper = HelperWithRegistryKill(registry);
        using var sleeper = StartSetsidSleeper();
        var own = OwnProcess.Of(sleeper);
        SafeHelper.Register(registry, own);

        var stopped = await new WorkerSandbox(Environment.UserName, helper.Path, SandboxSupport.FakeSudo(_dir)).StopAllAsync(CancellationToken.None);

        Assert.True(stopped);
        await OwnProcess.WaitUntilDeadAsync(own);
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
            var options = new FactoryOptions(FactoryOptions.LoadConfiguration(), new Support.InMemorySecrets());
            await sandbox.EnsureReadyAsync(options.WorkerAuth, options.ClaudePath, CancellationToken.None);
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
                    Assert.True(await git.CommitAndPushAsync(Repo, ws, "sc-1: change", TestGrants.Untainted, ct)); // owner reads the worker's files
                    break;
                case "stop":
                    using (var p = sandbox.Start(ws.Path, "/bin/sh", ["-c", $"sleep 600 & echo {OwnProcess.ShellRecord("$!")}; wait"], new Dictionary<string, string>()))
                    {
                        var pid = OwnProcess.Parse((await p.StandardOutput.ReadLineAsync(ct))!);
                        WorkerSandbox.Stop(p);
                        await p.WaitForExitAsync(ct);
                        await OwnProcess.WaitUntilDeadAsync(pid);
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
