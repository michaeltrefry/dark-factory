using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkerHeadlessTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-headless-").FullName;

    private async Task<(int Code, string Out, string Error)> Run(FakeBootstrap bootstrap, SafeHelper? helper = null)
    {
        helper ??= SafeHelper.Create(_dir, new()
        {
            Identity = bootstrap.Root
                ? new("_dftest", "0", new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" }, "450")
                : FakeIdentity.SandboxRole with { UidsByName = new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" } },
            Bootstrap = bootstrap,
        });
        var start = new ProcessStartInfo(helper.Path, ["/bin/echo", "worker-started"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["SUDO_USER"] = bootstrap.Owner;
        start.Environment["SUDO_UID"] = bootstrap.OwnerUid;
        start.Environment["BASH_ENV"] = SandboxSupport.Executable(_dir, "hostile-env", "echo HOSTILE_ENV\n");
        using var p = Process.Start(start)!;
        await p.StandardInput.WriteAsync("\n");
        await p.StandardInput.FlushAsync();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(helper.Refusals);
        return (p.ExitCode, await stdout, await stderr);
    }

    [Theory]
    [InlineData("501", "Aqua", false)]
    [InlineData("451", "Background", false)]
    [InlineData("450", "Aqua", false)]
    [InlineData("450", "Background", true)]
    [InlineData("450\n501", "Background", false)]
    public async Task Inherited_other_user_or_gui_bootstrap_never_starts_program(string uid, string name, bool gui)
    {
        var result = await Run(new(uid, name, gui));
        Assert.Equal(78, result.Code);
        Assert.DoesNotContain("worker-started", result.Out);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("user/450 = {\n type = login\n security context = {\n uid = 450\n }\n}")]
    [InlineData("user/450 = {\n type = user\n security context = {\n uid = 501\n }\n}")]
    [InlineData("user/450 = {\n type = user\n security context = {\n uid = 450\n uid = 450\n }\n}")]
    public async Task Manager_uid_alone_is_not_proof_of_non_gui_domain(string domain)
    {
        var result = await Run(new(Domain: domain));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Own_pinned_non_gui_user_domain_runs_with_no_loginwindow_exception()
    {
        var result = await Run(new());
        Assert.Equal(0, result.Code);
        Assert.Equal("worker-started\n", result.Out);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("451", "_dftest")]
    [InlineData("450", "_dftest2")]
    public async Task Correct_domain_metadata_cannot_replace_the_pinned_effective_identity(string uid, string user)
    {
        var helper = SafeHelper.Create(_dir, new()
        {
            Identity = new(user, uid, new Dictionary<string, string> { [user] = "450" }, "450"),
            Bootstrap = new(),
            Mutate = text => text.Replace("\nsandbox_user=_dftest2\n", "\nsandbox_user=_dftest\n"),
        });
        var result = await Run(new(), helper);
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public void Sandbox_invokes_only_pinned_root_trampoline()
    {
        Assert.Equal(["-n", "-u", "root", "/installed/helper", "claude", "--version"],
            new WorkerSandbox("_factory", "/installed/helper").BuildLaunchArguments("claude", ["--version"]));
    }

    [Fact]
    public async Task Root_adopts_fixed_user_then_drops_identity_before_protocol_or_program()
    {
        var result = await Run(new(Root: true));
        Assert.Equal(77, result.Code);
        Assert.Contains("dropped-to=_dftest path=/usr/bin:/bin:/usr/sbin:/sbin", result.Out);
        Assert.Contains("arg=</bin/echo>", result.Out);
        Assert.Contains("protocol=<>", result.Out);
        Assert.DoesNotContain("HOSTILE_ENV", result.Out);
    }

    [Theory]
    [InlineData("outsider", "501")]
    [InlineData("df-test-owner", "450")]
    [InlineData("df-test-owner", "0")]
    [InlineData("df-test-owner", "502")]
    public async Task Root_refuses_unpinned_invoker_before_adoption(string owner, string uid)
    {
        var result = await Run(new(Root: true, Owner: owner, OwnerUid: uid));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Theory]
    [InlineData("/", "write")]
    [InlineData("/usr/local", "writesecurity")]
    [InlineData(SafeHelper.RootHelperDirectory, "writeattr")]
    [InlineData(SafeHelper.RootHelperPath, "unknown_right")]
    public async Task Root_refuses_nonroot_acl_authority_before_adoption(string path, string rights)
    {
        var result = await Run(new(Root: true, UnsafePath: path, Rights: rights));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Theory]
    [InlineData("501:755")]
    [InlineData("0:777")]
    public async Task Root_refuses_untrusted_path_modes_or_ownership(string metadata)
    {
        var result = await Run(new(Root: true, UnsafePath: "/usr/local", Metadata: metadata));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Root_refuses_uninstalled_path_before_uid_drop()
    {
        var result = await Run(new(Root: true, InstalledPath: "/tmp/untrusted-helper"));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Root_refuses_symlink_ancestor_before_uid_drop()
    {
        var result = await Run(new(Root: true, SymlinkPath: "/usr/local"));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Root_refuses_even_own_gui_domain_before_uid_drop()
    {
        var result = await Run(new(Root: true, GuiExists: true));
        Assert.Equal(78, result.Code);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Read_only_nonroot_acl_and_other_slot_processes_do_not_quarantine_this_slot()
    {
        var result = await Run(new(Root: true, UnsafePath: "/usr/local", Rights: "read,execute,readsecurity",
            Processes: "1 0 0 S\n789 451 451 S"));
        Assert.Equal(77, result.Code);
    }

    [Theory]
    [InlineData("123 450 450 S", false)]
    [InlineData("123 501 450 S", false)]
    [InlineData("malformed", false)]
    [InlineData("", true)]
    [InlineData("", false)]
    public async Task Cleanup_uncertainty_quarantines_only_this_slot_durably(string processes, bool failure)
    {
        var boot = new FakeBootstrap(Root: true, Processes: processes, ProcessFailure: failure);
        var helper = SafeHelper.Create(_dir, new()
        {
            Identity = new("_dftest", "0", new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" }, "450"),
            Bootstrap = boot,
        });
        var first = await Run(boot, helper);
        Assert.Equal(78, first.Code);
        Assert.Contains("quarantined", first.Error);
        var again = await Run(boot, helper);
        Assert.Equal(78, again.Code);
        Assert.Empty(again.Out); // persisted before the uid drop, not an in-memory process flag
        var otherSlot = await Run(new(Root: true));
        Assert.Equal(77, otherSlot.Code);
    }

    private WorkerSandbox RootSandbox(bool failCleanup = true, bool gui = false)
    {
        var helper = SafeHelper.Create(_dir, new()
        {
            Identity = new("_dftest", "0", new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" }, "450"),
            Bootstrap = new(Root: true, RunWorker: true, ProcessFailure: failCleanup, GuiExists: gui),
        });
        var sudo = SandboxSupport.Executable(_dir, "root-sudo", "#!/bin/sh\nshift 3\nexport SUDO_USER=df-test-owner SUDO_UID=501\nexec \"$@\"\n");
        return new WorkerSandbox("_dftest", helper.Path, sudo);
    }

    [Fact]
    public async Task Successful_command_then_root_cleanup_uncertainty_is_infrastructure_and_keeps_quarantine()
    {
        var sandbox = RootSandbox();
        var error = await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() =>
            sandbox.RunAsync(_dir, "/bin/echo", ["successful-command"], CancellationToken.None));
        Assert.Contains("sandbox", error.Message);
        Assert.True(File.Exists(sandbox.HelperPath + ".quarantine"));
        await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() =>
            sandbox.RunAsync(_dir, "/bin/echo", ["must-not-run"], CancellationToken.None));
    }

    [Fact]
    public async Task Successful_worker_then_root_cleanup_uncertainty_is_not_an_application_result()
    {
        var sandbox = RootSandbox();
        var cli = SandboxSupport.Executable(_dir, "successful-claude", "#!/bin/sh\necho '{\"type\":\"system\",\"session_id\":\"s\"}'\necho '{\"type\":\"result\",\"session_id\":\"s\",\"is_error\":false,\"result\":\"done\"}'\n");
        var worker = new ClaudeWorker(cli, SandboxSupport.Router, "k", TimeSpan.FromSeconds(20), sandbox);
        await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() =>
            worker.RunAsync(_dir, "p", null, WorkerModelClass.Mid, null, CancellationToken.None));
        Assert.True(File.Exists(sandbox.HelperPath + ".quarantine"));
    }

    [Fact]
    public async Task Gate_execution_reports_cleanup_uncertainty_as_infrastructure_not_a_judged_test()
    {
        var sandbox = RootSandbox();
        var git = new DarkFactory.Orchestrator.Git.GitWorkspace(_dir, _ => "/unused", (_, _) => Task.FromResult<string?>(null));
        var runner = new DarkFactory.Orchestrator.Gate.SandboxTestRunner(git, sandbox, TimeSpan.FromSeconds(20));
        var method = typeof(DarkFactory.Orchestrator.Gate.SandboxTestRunner).GetMethod("RunStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var task = (Task<(int, string, bool, string?)>)method.Invoke(runner,
            [_dir, new DarkFactory.Orchestrator.Gate.TestStep(DarkFactory.Orchestrator.Gate.TestPhase.Test, "/bin/echo", ["test-passed"]), TimeSpan.FromSeconds(20), CancellationToken.None])!;
        await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() => task);
        Assert.True(File.Exists(sandbox.HelperPath + ".quarantine"));
    }

    [Fact]
    public async Task Program_exit_78_with_proven_bootstrap_and_cleanup_is_an_ordinary_application_result()
    {
        var sandbox = RootSandbox(failCleanup: false);
        var app = SandboxSupport.Executable(_dir, "exit78", "#!/bin/sh\necho app-ran\necho 'factory-worker-launch: slot remains quarantined' >&2\nexit 78\n");
        var (code, output, error) = await sandbox.RunAsync(_dir, app, [], CancellationToken.None);
        Assert.Equal(78, code);
        Assert.Equal("app-ran\n", output);
        Assert.Contains("slot remains quarantined", error);
        Assert.False(File.Exists(sandbox.HelperPath + ".quarantine"));
    }

    [Fact]
    public async Task Emergency_stop_bypasses_quarantine_only_for_cleanup_and_preserves_another_slots_process()
    {
        var registry = Path.Combine(_dir, "emergency-registry");
        var helper = SafeHelper.Create(_dir, new()
        {
            Identity = new("_dftest", "0", new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" }, "450"),
            Bootstrap = new(Root: true, RunWorker: true), Registry = registry,
            Mutate = text => text.Replace("    while IFS= read -r _; do :; done", "    while IFS= read -r _; do :; done\n    if [ \"${cleanup_only:-0}\" != 1 ]; then while :; do :; done; fi"),
        });
        var sudo = SandboxSupport.Executable(_dir, "emergency-sudo", "#!/bin/sh\nshift 3\nexport SUDO_USER=df-test-owner SUDO_UID=501\nexec \"$@\"\n");
        var sandbox = new WorkerSandbox(Environment.UserName, helper.Path, sudo) { StopGrace = TimeSpan.FromSeconds(3) };
        var pidFile = Path.Combine(_dir, "emergency-worker");
        var cli = SandboxSupport.Executable(_dir, "wedged-worker", $"#!/bin/sh\necho {OwnProcess.ShellRecord("$$")} > '{pidFile}'\necho '{{\"type\":\"system\",\"session_id\":\"s\"}}'\nexec /bin/sleep 600\n");
        using var detachedProcess = Process.Start(new ProcessStartInfo("/usr/bin/perl", ["-MPOSIX", "-e", "POSIX::setsid(); exec('/bin/sleep', '600')"]) { RedirectStandardInput = true })!;
        using var otherProcess = Process.Start(new ProcessStartInfo("/bin/sleep", ["600"]))!;
        var detached = OwnProcess.Of(detachedProcess);
        var other = OwnProcess.Of(otherProcess);
        SafeHelper.Register(registry, detached);
        var worker = new ClaudeWorker(cli, SandboxSupport.Router, "k", TimeSpan.FromSeconds(30), sandbox, stopGrace: TimeSpan.FromMilliseconds(300));
        using var cancel = new CancellationTokenSource();
        var running = worker.RunAsync(_dir, "p", null, WorkerModelClass.Mid, null, cancel.Token);
        var own = await SandboxSupport.WaitForPidAsync(pidFile, running);
        SafeHelper.Register(registry, own);
        try
        {
            Assert.True(File.Exists(helper.Path + ".quarantine"));
            await cancel.CancelAsync();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(WorkerStillRunning.IsMarked(error));
            await OwnProcess.WaitUntilDeadAsync(own, detached);
            Assert.True(other.IsAlive);
            Assert.Contains(detached.Pid, helper.Targets);
            Assert.Empty(helper.Refusals);
        }
        finally
        {
            own.KillIfStillRunning(); detached.KillIfStillRunning(); other.KillIfStillRunning();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pipeline_retains_a_successful_session_on_cleanup_failure_without_done_escalation_push_or_test_judgment(bool alreadyQuarantined)
    {
        var sandbox = RootSandbox();
        if (alreadyQuarantined) File.WriteAllText(sandbox.HelperPath + ".quarantine", "");
        var cli = SandboxSupport.Executable(_dir, "pipeline-claude", "#!/bin/sh\necho '{\"type\":\"system\",\"session_id\":\"s\"}'\necho '{\"type\":\"result\",\"session_id\":\"s\",\"is_error\":false,\"result\":\"done\"}'\n");
        var worker = new ClaudeWorker(cli, SandboxSupport.Router, "k", TimeSpan.FromSeconds(20), sandbox);
        using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var workspaces = new RunPipelineTests.FakeWorkspaces { Root = _dir };
        Directory.CreateDirectory(Path.Combine(_dir, "factory", "sc-77"));
        var source = new FakeWorkSource(new(77, "Change", "Implement change", "bug", "https://example.test/story", ItemNaming.Shortcut));
        var pipeline = new RunPipeline(source, ledger, new InProcessRunLocks(), workspaces, worker,
            new RunPipelineTests.FakePullRequests(), RunPipelineTests.Sandbox, TextWriter.Null, slots: sandbox);
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => pipeline.RunAsync(77, CancellationToken.None));
        var rows = await db.LedgerEntries.ToListAsync();
        Assert.DoesNotContain(rows, row => row.State == WorkState.Escalated || row.Step is RunPipeline.Steps.WorkerDone or RunPipeline.Steps.Pushed or "new-tests");
        Assert.Equal(WorkState.Implement, (await db.WorkItems.SingleAsync()).State);
        Assert.DoesNotContain(workspaces.Calls, call => call.StartsWith("push") || call.StartsWith("remove"));
        Assert.True(File.Exists(sandbox.HelperPath + ".quarantine"));
    }

    [Fact]
    public async Task Bootstrap_refusal_before_program_is_infrastructure_even_without_a_quarantine_marker()
    {
        var sandbox = RootSandbox(failCleanup: false, gui: true);
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => sandbox.RunAsync(_dir, "/bin/echo", ["must-not-run"], CancellationToken.None));
        Assert.False(File.Exists(sandbox.HelperPath + ".quarantine")); // refusal before adoption, not an application exit 78
    }

    [Fact]
    public async Task Cleanup_only_dispatch_refuses_a_caller_selected_program_before_uid_drop()
    {
        var sandbox = RootSandbox(failCleanup: false);
        var start = new ProcessStartInfo(sandbox.SudoPath, ["-n", "-u", "root", sandbox.HelperPath, "__factory_cleanup", Guid.NewGuid().ToString("N"), "/bin/echo", "must-not-run"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(78, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("cleanup takes only", await error);
    }

    [Fact]
    public async Task An_unconfirmed_gate_bootstrap_is_stopped_or_marks_its_worktree_unsafe_to_delete()
    {
        var pidFile = Path.Combine(_dir, "unconfirmed-bootstrap");
        var sudo = SandboxSupport.Executable(_dir, "unconfirmed-sudo", $"#!/bin/sh\necho {OwnProcess.ShellRecord("$$")} > '{pidFile}'\nexec /bin/sleep 600 </dev/null\n");
        var sandbox = new WorkerSandbox(Environment.UserName, SafeHelper.Create(_dir).Path, sudo);
        var git = new DarkFactory.Orchestrator.Git.GitWorkspace(_dir, _ => "/unused", (_, _) => Task.FromResult<string?>(null));
        var runner = new DarkFactory.Orchestrator.Gate.SandboxTestRunner(git, sandbox, TimeSpan.FromSeconds(30));
        var method = typeof(DarkFactory.Orchestrator.Gate.SandboxTestRunner).GetMethod("RunStepAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        using var cancel = new CancellationTokenSource();
        var task = (Task<(int, string, bool, string?)>)method.Invoke(runner,
            [_dir, new DarkFactory.Orchestrator.Gate.TestStep(DarkFactory.Orchestrator.Gate.TestPhase.Test, "/bin/echo", ["never-started"]), TimeSpan.FromSeconds(30), cancel.Token])!;
        var own = await SandboxSupport.WaitForPidAsync(pidFile, task);
        try
        {
            await cancel.CancelAsync();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(WorkerStillRunning.IsMarked(error));
            Assert.True(own.IsAlive);
        }
        finally { own.KillIfStillRunning(); }
    }
}
