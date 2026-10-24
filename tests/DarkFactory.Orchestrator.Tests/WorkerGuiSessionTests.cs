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
