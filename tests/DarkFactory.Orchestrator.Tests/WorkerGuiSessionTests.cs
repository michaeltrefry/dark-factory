using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>All launchd answers are fake and every signal is SafeHelper's test-owned seam. No GUI login or host change.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkerGuiSessionTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-gui-").FullName;

    private async Task<(int Code, string Out, string Error)> RunAsync(FakeGuiSession session, string? listing = null)
    {
        var helper = SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Identity = FakeIdentity.SandboxRole,
            GuiSession = session,
            FakeListing = listing,
            LogOnly = true,
        });
        using var process = Process.Start(new ProcessStartInfo(helper.Path, ["/bin/echo", "worker-started"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        await process.StandardInput.WriteAsync("\n");
        await process.StandardInput.FlushAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(helper.Refusals);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public void Launch_goes_through_the_root_trampoline_argv_never_a_worker_user_or_the_owner_s_bootstrap()
    {
        var sandbox = new WorkerSandbox("_factory2", "/installed/helper-2");
        Assert.Equal(["-n", "-u", "root", "/installed/helper-2", "claude", "--version"],
            sandbox.BuildLaunchArguments("claude", ["--version"]));
    }

    [Theory]
    [InlineData("501", "Aqua")]
    [InlineData("450", "Background")]
    [InlineData("", "Aqua")]
    [InlineData("450\n501", "Aqua")]
    public async Task A_shared_owner_or_non_gui_bootstrap_refuses_before_the_worker_starts(string uid, string manager)
    {
        var result = await RunAsync(new(uid, manager));
        Assert.Equal(78, result.Code);
        Assert.DoesNotContain("worker-started", result.Out);
        Assert.Contains("own Aqua", result.Error);
    }

    [Theory]
    [InlineData("gui/450 = {\n type = login\n creator = loginwindow[400001]\n security context = {\n uid = 501\n asid = 100004\n }\n}")]
    [InlineData("gui/450 = {\n type = user\n creator = loginwindow[400001]\n security context = {\n uid = 450\n asid = 100004\n }\n}")]
    [InlineData("gui/450 = {\n type = login\n creator = worker[400001]\n security context = {\n uid = 450\n asid = 100004\n }\n}")]
    [InlineData("gui/450 = {\n type = login\n creator = loginwindow[1]\n security context = {\n uid = 450\n asid = 100004\n }\n}")]
    [InlineData("unknown format")]
    public async Task Unknown_or_mismatched_gui_provenance_never_grants_an_exception(string domain)
    {
        var result = await RunAsync(new(Domain: domain));
        Assert.Equal(78, result.Code);
        Assert.DoesNotContain("worker-started", result.Out);
    }

    [Fact]
    public async Task Duplicate_gui_identity_metadata_is_not_a_proven_login()
    {
        var session = new FakeGuiSession();
        var result = await RunAsync(session with { Domain = session.Listing + session.Listing });
        Assert.Equal(78, result.Code);
        Assert.DoesNotContain("worker-started", result.Out);
    }

    [Fact]
    public async Task An_own_uid_aqua_login_with_authoritative_loginwindow_creator_can_run()
    {
        var result = await RunAsync(new());
        Assert.Equal(0, result.Code);
        Assert.Equal("worker-started\n", result.Out);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task The_sweep_spares_exactly_the_proven_loginwindow_creator_and_kills_the_rest()
    {
        // The listing holds the user's proven loginwindow creator (pid 400001, launchd's child) beside an orphan of the run.
        var helper = SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Identity = FakeIdentity.SandboxRole,
            GuiSession = new(),
            FakeListing = "echo '400001 1'; echo '400002 400001'",
        });
        using var process = Process.Start(new ProcessStartInfo(helper.Path, ["/bin/echo", "worker-started"])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        await process.StandardInput.WriteAsync("\n");
        await process.StandardInput.FlushAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(helper.Refusals);
        // The creator was never even offered to the seam: the helper spared it itself, by its proven identity.
        Assert.DoesNotContain(helper.TargetLog, l => l.Contains("400001"));
        // The orphan beside it was offered to the sweep (the seam skips it: no such real pid in this test's process table).
        Assert.Contains(helper.TargetLog, l => l.StartsWith("skip KILL 400002"));
        await stdout; Assert.Empty(await stderr);
    }

    [Fact]
    public async Task A_creator_that_changes_or_a_pid_reused_during_cleanup_refuses_the_sweep_instead_of_killing()
    {
        var result = await RunAsync(new FakeGuiSession
        {
            LaterCreatorIdentity = "450 Wed Oct 21 09:00:00 2026 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow",
        }, listing: "echo '400001 1'");

        Assert.Equal(78, result.Code); // fail closed: no unproven uid sweep, never a login torn down on a doubt
        Assert.Contains("login creator changed during worker cleanup", result.Error);
    }

    [Fact]
    public async Task A_domain_whose_provenance_changes_during_cleanup_refuses_the_sweep_too()
    {
        var session = new FakeGuiSession();
        var result = await RunAsync(session with { LaterDomain = session.Listing.Replace("uid = 450", "uid = 501") },
            listing: "echo '400001 1'");

        Assert.Equal(78, result.Code);
        Assert.Contains("login creator changed during worker cleanup", result.Error);
    }

    [Fact]
    public void Session_identity_and_root_privilege_drop_precede_protocol_and_worker_execution()
    {
        Assert.True(SafeHelper.SourceContainsAfter("exec /bin/launchctl asuser", "while IFS= read -r line"));
        Assert.True(SafeHelper.SourceContainsAfter("\n    verify_gui_session\n", "while IFS= read -r line"));
        Assert.True(SafeHelper.SourceContains("sandbox_owner="));
        Assert.True(SafeHelper.SourceContains("creator = loginwindow"));
    }

    private async Task<(int Code, string Out, string Error)> RunRootAsync(FakeRootTrampoline root,
        string owner = "df-test-owner", string ownerUid = "501", FakeGuiSession? gui = null, bool statusOnly = false, bool extraArgument = false)
    {
        var helper = SafeHelper.Create(_dir, new SafeHelperOptions
        {
            Identity = new("_dftest", "0", new Dictionary<string, string> { ["_dftest"] = "450", ["df-test-owner"] = "501" }, "450"),
            GuiSession = gui ?? new(), RootTrampoline = root, LogOnly = true,
        });
        var envFile = Path.Combine(_dir, "hostile-env");
        File.WriteAllText(envFile, "echo BASH_ENV_EXECUTED\nexit 94\n");
        var args = statusOnly ? (extraArgument ? new[] { "__factory_session_status", "extra" } : ["__factory_session_status"])
            : ["/bin/echo", "spaces stay one argument", "--flag"];
        var start = new ProcessStartInfo(helper.Path, args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["SUDO_USER"] = owner;
        start.Environment["SUDO_UID"] = ownerUid;
        start.Environment["BASH_ENV"] = envFile;
        start.Environment["PATH"] = "/attacker/bin";
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync("protocol-unread-by-root\n");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task Fake_root_adopts_then_drops_uid_preserving_arguments_and_unread_protocol_with_a_fixed_path()
    {
        var result = await RunRootAsync(new());
        Assert.Multiple(
            () => Assert.Equal(77, result.Code),
            () => Assert.Contains("dropped-to=_dftest\npath=/usr/bin:/bin:/usr/sbin:/sbin\n", result.Out),
            () => Assert.Contains("arg=<spaces stay one argument>\narg=<--flag>", result.Out),
            () => Assert.Contains("protocol=<protocol-unread-by-root>", result.Out),
            () => Assert.DoesNotContain("BASH_ENV_EXECUTED", result.Out),
            () => Assert.Empty(result.Error));
    }

    [Theory]
    [InlineData("outsider", "501")]
    [InlineData("df-test-owner", "450")]
    [InlineData("df-test-owner", "0")]
    [InlineData("df-test-owner", "502")]
    public async Task Fake_root_refuses_an_unpinned_owner_before_adoption_or_protocol(string owner, string uid)
    {
        var result = await RunRootAsync(new(), owner, uid);
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Empty(result.Out),
            () => Assert.Contains("trampoline identity", result.Error));
    }

    [Fact]
    public async Task Fake_root_refuses_an_uninstalled_executable_path_before_adoption()
    {
        var result = await RunRootAsync(new(InstalledPath: "/tmp/worker-helper"));
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Empty(result.Out),
            () => Assert.Contains("uninstalled session trampoline path", result.Error));
    }

    [Theory]
    [InlineData("/", "writeattr")]
    [InlineData("/usr", "chown")]
    [InlineData("/usr/local", "writesecurity")]
    [InlineData("/usr/local/libexec", "unknown_right")]
    [InlineData(SafeHelper.RootHelperDirectory, "add_file,delete_child")]
    [InlineData(SafeHelper.RootHelperPath, "write")]
    public async Task Fake_root_refuses_each_untrusted_acl_before_adoption(string path, string rights)
    {
        var result = await RunRootAsync(new(UnsafePath: path, Rights: rights));
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Empty(result.Out));
    }

    [Theory]
    [InlineData("501:755", false)]
    [InlineData("0:775", false)]
    [InlineData("0:755", true)]
    public async Task Fake_root_refuses_untrusted_ownership_mode_or_unreadable_acl(string metadata, bool unreadable)
    {
        var result = await RunRootAsync(new(UnsafePath: "/usr", Rights: "read", Metadata: metadata, ListingFails: unreadable));
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Empty(result.Out));
    }

    [Theory]
    [InlineData("400002 450 501 S", "400001 450 450 S")]
    [InlineData("400001 450 450 S", "400002 501 450 S")]
    [InlineData("400001 450 450 S\n400002 450 450 S", "400001 450 450 S")]
    [InlineData("400001 450 450 S", "malformed")]
    [InlineData("400001 450 450 S", "400001 450 450 unknown")]
    [InlineData("", "400001 450 450 S")]
    [InlineData("", "")]
    public async Task Fake_root_quarantines_an_unexpected_child_exit_unless_both_uid_views_prove_only_the_creator(
        string realProcesses, string effectiveProcesses)
    {
        var result = await RunRootAsync(new() { RealProcesses = realProcesses, EffectiveProcesses = effectiveProcesses });
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Contains("root could not prove worker cleanup", result.Error));
    }

    [Fact]
    public async Task Fake_root_quarantines_when_the_independent_process_read_fails()
    {
        var result = await RunRootAsync(new() { ProcessReadFails = true });
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Contains("root could not prove worker cleanup", result.Error));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fake_root_quarantines_when_the_gui_generation_or_creator_start_changes(bool domainChanged)
    {
        var gui = new FakeGuiSession();
        gui = domainChanged ? gui with { LaterDomain = gui.Listing.Replace("asid = 100004", "asid = 100005") }
            : gui with { LaterCreatorIdentity = gui.CreatorIdentity.Replace("04:59:11", "05:00:00") };
        var result = await RunRootAsync(new(), gui: gui);
        Assert.Multiple(
            () => Assert.Equal(78, result.Code),
            () => Assert.Contains("root could not prove worker cleanup", result.Error));
    }

    [Theory]
    [InlineData(false, 77)]
    [InlineData(true, 78)]
    public async Task Fake_root_skips_cleanup_proof_only_for_the_exact_single_readonly_status_query(bool extraArgument, int expected)
    {
        var result = await RunRootAsync(new() { ProcessReadFails = true }, statusOnly: true, extraArgument: extraArgument);
        Assert.Equal(expected, result.Code);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void Ac2_acceptance_requires_a_meaningful_owner_baseline_and_actual_worker_refusal(
        bool ownerResolved, bool workerRefused, bool accepted)
    {
        IReadOnlyList<MachLookupProbe.Outcome> Rows(int uid, bool resolved) => MachLookupProbe.Services
            .Select(name => new MachLookupProbe.Outcome(name, uid, resolved, resolved ? 0 : 1100, resolved)).ToArray();
        var problem = MachLookupProbe.AcceptanceProblem(Rows(501, ownerResolved), [Rows(450, !workerRefused)]);
        Assert.Equal(accepted, problem is null);
    }

    [Fact]
    public void Ac2_acceptance_rejects_same_uid_incomplete_and_absent_evidence()
    {
        IReadOnlyList<MachLookupProbe.Outcome> owner = MachLookupProbe.Services
            .Select(name => new MachLookupProbe.Outcome(name, 501, true, 0, true)).ToArray();
        var refused = owner.Select(o => o with { LookedUp = false, ReleasedWithoutSend = false }).ToArray();
        Assert.All(new[]
        {
            MachLookupProbe.AcceptanceProblem(owner, [refused]),
            MachLookupProbe.AcceptanceProblem(owner, []),
            MachLookupProbe.AcceptanceProblem(owner.Take(1).ToArray(), [refused]),
        }, problem => Assert.StartsWith("INCONCLUSIVE AC2:", problem));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_creator_quarantines_the_slot_across_instances_while_a_registered_detached_worker_survives(bool domainChanges)
    {
        var detached = OwnProcess.StartDetached(_dir);
        try
        {
            var registry = Path.Combine(_dir, "registered");
            SafeHelper.Register(registry, detached);
            var session = new FakeGuiSession();
            session = domainChanges
                ? session with { LaterDomain = session.Listing.Replace("uid = 450", "uid = 501") }
                : session with { LaterCreatorIdentity = "450 Wed Oct 21 09:00:00 2026 /System/Library/CoreServices/loginwindow.app/Contents/MacOS/loginwindow" };
            var helper = SafeHelper.Create(_dir, new SafeHelperOptions
            {
                Identity = FakeIdentity.SandboxRole, GuiSession = session, Registry = registry,
            });
            var slotLock = WorkerLock.For(_dir);
            var sandbox = new WorkerSandbox("_dftest", helper.Path, SandboxSupport.FakeSudo(_dir)) { Lock = slotLock };
            var result = await sandbox.RunAsync("/", "/bin/echo", ["worker-started"], CancellationToken.None);
            Assert.True(result.ExitCode == 78 && File.Exists(sandbox.QuarantinePath)
                && OwnProcess.StateOf(detached.Pid) is not "" and not "Z", "changed provenance must quarantine, not equate exit with cleanup");
            var nextProcess = new WorkerSandbox("_dftest", helper.Path, sandbox.SudoPath) { Lock = new WorkerLock(slotLock.Path) };
            var refused = await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() =>
                nextProcess.RunAsync("/", "/bin/echo", ["must-not-run"], CancellationToken.None));
            Assert.Contains("independently verify", refused.Message);
            // A different slot remains usable. No setup or host user is involved: fake sudo and SafeHelper only.
            var otherHelper = SafeHelper.Create(_dir);
            var other = new WorkerSandbox("other-test-slot", otherHelper.Path, SandboxSupport.FakeSudo(_dir))
                { Lock = new WorkerLock(Path.Combine(_dir, "other.lock")) };
            var otherResult = await other.RunAsync("/", "/bin/echo", ["other-slot-ran"], CancellationToken.None);
            Assert.Equal("other-slot-ran\n", otherResult.Stdout);
            // Explicit test-owned recovery proof, NOT a helper exit: terminate exactly the registered detached process.
            detached.KillIfStillRunning();
            await OwnProcess.WaitUntilDeadAsync(detached);
            File.Delete(sandbox.QuarantinePath!);
            var recoveredHelper = SafeHelper.Create(_dir, new SafeHelperOptions { Identity = FakeIdentity.SandboxRole, GuiSession = new() });
            var recovered = new WorkerSandbox("_dftest", recoveredHelper.Path, sandbox.SudoPath) { Lock = slotLock };
            var clean = await recovered.RunAsync("/", "/bin/echo", ["recovered"], CancellationToken.None);
            Assert.True(clean.ExitCode == 0 && !File.Exists(recovered.QuarantinePath), "only explicitly recovered slot may start again");
        }
        finally { detached.KillIfStillRunning(); }
    }

    [Fact]
    public async Task A_prelaunch_marker_blocks_a_new_process_even_without_an_exit_callback()
    {
        var helper = SafeHelper.Create(_dir);
        var workerLock = WorkerLock.For(_dir);
        var sandbox = new WorkerSandbox("test-slot", helper.Path, SandboxSupport.FakeSudo(_dir)) { Lock = workerLock };
        await workerLock.HoldAsync(async () =>
        {
            var process = sandbox.Start("/", "/bin/true", [], new Dictionary<string, string>());
            await process.WaitForExitAsync();
            // Simulate owner death between launch and EndAsync: the durable pre-launch intent still exists.
            process.Dispose();
            return true;
        }, CancellationToken.None);
        var replacement = new WorkerSandbox("test-slot", helper.Path, sandbox.SudoPath) { Lock = new WorkerLock(workerLock.Path) };
        await Assert.ThrowsAsync<DarkFactory.Orchestrator.WorkSources.FactoryUnavailableException>(() => replacement.RunAsync("/", "/bin/true", [], CancellationToken.None));
    }

    [Fact]
    public async Task An_overlapping_rescue_helper_keeps_the_marker_until_both_completions_are_proven()
    {
        var helper = SafeHelper.Create(_dir);
        var slotLock = WorkerLock.For(_dir);
        var sandbox = new WorkerSandbox("test-slot", helper.Path, SandboxSupport.FakeSudo(_dir)) { Lock = slotLock };
        await slotLock.HoldAsync(async () =>
        {
            var first = sandbox.Start("/", "/bin/true", [], new Dictionary<string, string>());
            var second = sandbox.Start("/", "/bin/true", [], new Dictionary<string, string>());
            await first.WaitForExitAsync();
            await second.WaitForExitAsync();
            await sandbox.EndAsync(first);
            var stillPending = File.Exists(sandbox.QuarantinePath);
            await sandbox.EndAsync(second);
            Assert.True(stillPending && !File.Exists(sandbox.QuarantinePath), "an earlier completion must not clear a rescue helper's durable intent");
            return true;
        }, CancellationToken.None);
    }

    // ---- The AC2 lookup probe (sc-26789, S3): tiers, and that it can only ever look up and release ----

    [Fact]
    public void The_mach_probe_source_only_looks_up_and_releases_and_never_sends_a_message()
    {
        Assert.Contains("bootstrap_look_up", MachLookupProbe.Source);
        Assert.Contains("mach_port_deallocate", MachLookupProbe.Source);
        // Nothing is ever sent to a service, so no owner session content can be read or changed.
        Assert.DoesNotContain("mach_msg", MachLookupProbe.Source);
        Assert.DoesNotContain("xpc", MachLookupProbe.Source);
        Assert.DoesNotContain("NSPasteboard", MachLookupProbe.Source);
        Assert.DoesNotContain("system(", MachLookupProbe.Source);
    }

    [Fact]
    public void Probe_output_reads_as_exactly_two_tiers_and_never_overstates_a_successful_lookup()
    {
        const string output = """
            probe com.apple.pasteboard.1 uid=498 REFUSED kr=1100
            probe com.apple.distnoted uid=498 SUCCEEDED port=4321
            probe com.apple.distnoted released-without-send
            probe com.apple.windowserver uid=498 REFUSED kr=1102
            """;

        var outcomes = MachLookupProbe.Parse(output);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Contains(MachLookupProbe.Services, s => s == o.Service));
        var pasteboard = outcomes.Single(o => o.Service == "com.apple.pasteboard.1");
        Assert.False(pasteboard.LookedUp);
        Assert.Equal(1100, pasteboard.Kr);
        Assert.Equal("refused", pasteboard.Tier); // this lookup gave the worker's domain nothing
        var distnoted = outcomes.Single(o => o.Service == "com.apple.distnoted");
        Assert.True(distnoted.LookedUp);
        Assert.True(distnoted.ReleasedWithoutSend);
        // A send right existed: nothing about isolation is proven — never recorded as AC2 evidence.
        Assert.StartsWith("succeeded-unverified", distnoted.Tier);
        Assert.Contains("not AC2 evidence by itself", distnoted.Tier);
    }

    [Theory]
    [InlineData("probe com.apple.distnoted uid=498 SUCCEEDED port=5")]
    [InlineData("probe com.apple.distnoted released-without-send")]
    [InlineData("anything else")]
    [InlineData("probe com.apple.distnoted uid=498 REFUSED kr=1100\nprobe com.apple.distnoted released-without-send")]
    public void Probe_output_that_is_not_the_probe_s_own_words_is_refused(string output)
    {
        Assert.ThrowsAny<Exception>(() => MachLookupProbe.Parse(output));
    }
}
