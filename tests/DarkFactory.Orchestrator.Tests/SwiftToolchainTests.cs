using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The Swift toolchain of every sandbox user (sc-26130, <c>Worker:Swift</c>): the setting, the start-up read of the installed
/// xcodebuild wrapper, each slot's readiness probe (swift, xcodebuild, Xcode's first launch and license, the wrapper — as that slot's
/// user through its own fake helper, never sudo, never a sandbox user, never the real xcodebuild), the worker's one xcodebuild rule,
/// and that with the setting off (the default) nothing of Swift is checked (E7).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SwiftToolchainTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-swift-").FullName;

    private string Ran => Path.Combine(_dir, "ran");

    /// <summary>A fake launch helper as the repo would have it: reads the variable block, logs the program, then runs it as us.</summary>
    private string RepoHelper => "#!/bin/bash\nsandbox_user=_factory\nsandbox_uid=\nhelper_version=" + WorkerSandbox.HelperVersion
        + "\nwhile IFS= read -r line; do [ -z \"$line\" ] && break; done\necho \"${0##*/} $*\" >> '" + Ran + "'\n"
        + "case \"$1\" in /usr/bin/id) echo \"${0##*/}\" ;; *) exec \"$@\" ;; esac\n";

    private static string InstalledFor(string user, string uid, string repo) => repo
        .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={user}\n").Replace("\nsandbox_uid=\n", $"\nsandbox_uid={uid}\n");

    private string Claude => SandboxSupport.Executable(_dir, "fake-claude.sh", "#!/bin/sh\necho '2.1.291 (Claude Code)'\n");

    /// <summary>A fake xcodebuild: <c>-checkFirstLaunchStatus</c> exits <paramref name="firstLaunch"/>, <c>-license check</c> <paramref name="license"/>.</summary>
    private string Xcodebuild(int firstLaunch = 0, int license = 0) => SandboxSupport.Executable(_dir, $"xcodebuild-{Guid.NewGuid():N}", $$"""
        #!/bin/sh
        case "$1" in
            -version) echo 'Xcode 26.5' ;;
            -checkFirstLaunchStatus) [ {{firstLaunch}} -eq 0 ] || echo 'first launch tasks are not done' >&2; exit {{firstLaunch}} ;;
            -license) [ "$2" = check ] || exit 2; [ {{license}} -eq 0 ] || echo 'You have not agreed to the Xcode license agreements.' >&2; exit {{license}} ;;
            *) exit 2 ;;
        esac
        """);

    private string Swift(int exit = 0) => SandboxSupport.Executable(_dir, $"swift-{Guid.NewGuid():N}",
        $"#!/bin/sh\n[ \"$1\" = --version ] || exit 2\necho 'Apple Swift version 6.3.2'\nexit {exit}\n");

    /// <summary>The repo's wrapper as installed (a copy: with no arguments it only prints its usage).</summary>
    private string InstalledWrapper(string? text = null) => SandboxSupport.Executable(_dir, $"factory-xcodebuild-{Guid.NewGuid():N}",
        text ?? File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild")));

    private SwiftToolchain Toolchain(int firstLaunch = 0, int license = 0, int swiftExit = 0, string? wrapper = null) =>
        new(Swift(swiftExit), Xcodebuild(firstLaunch, license), wrapper ?? InstalledWrapper());

    /// <summary>Two slots, each its own fake helper and user, each with its own toolchain.</summary>
    private WorkerSlots Pool(SwiftToolchain? first, SwiftToolchain? second)
    {
        var sudo = SandboxSupport.FakeSudo(_dir);
        var repo = RepoHelper;
        var one = SandboxSupport.Executable(_dir, "_dftest", InstalledFor("_dftest", "450", repo));
        var two = SandboxSupport.Executable(_dir, "_dftest2", InstalledFor("_dftest2", "451", repo));
        return new WorkerSlots([
            new WorkerSlot(1, new WorkerSandbox("_dftest", one, sudo, repo) { Lock = WorkerLock.For(_dir, 1), Swift = first, Reinstall = WorkerSlots.ReinstallCommand(2) }),
            new WorkerSlot(2, new WorkerSandbox("_dftest2", two, sudo, repo) { Lock = WorkerLock.For(_dir, 2), Swift = second, Reinstall = WorkerSlots.ReinstallCommand(2) })]);
    }

    private Task ProbeAsync(WorkerSlots pool, int slot) => pool.HoldAsync(slot,
        async _ => { await FactoryRunner.EnsureSandboxReadyAsync(pool, WorkerAuth.RouterKey, Claude, CancellationToken.None); return true; },
        CancellationToken.None);

    private string[] HelpersRan => File.Exists(Ran) ? File.ReadAllLines(Ran) : [];

    [Fact]
    public async Task Each_slot_probes_swift_xcodebuild_xcodes_first_launch_and_license_and_the_wrapper_as_its_own_user()
    {
        var (first, second) = (Toolchain(), Toolchain());
        var pool = Pool(first, second);

        await ProbeAsync(pool, 1);
        await ProbeAsync(pool, 2);

        foreach (var (helper, swift) in new[] { ("_dftest", first), ("_dftest2", second) })
        {
            var ran = HelpersRan.Where(l => l.StartsWith(helper + " ", StringComparison.Ordinal)).ToList();
            Assert.Contains($"{helper} {swift.SwiftPath} --version", ran);
            Assert.Contains($"{helper} {swift.XcodebuildPath} -version", ran);
            Assert.Contains($"{helper} {swift.XcodebuildPath} -checkFirstLaunchStatus", ran);
            Assert.Contains($"{helper} {swift.XcodebuildPath} -license check", ran);
            Assert.Contains($"{helper} {swift.WrapperPath}", ran);
        }
        // Each slot's probe ran through its own helper only.
        Assert.DoesNotContain(HelpersRan, l => l.StartsWith("_dftest ", StringComparison.Ordinal) && l.Contains(second.XcodebuildPath, StringComparison.Ordinal));
    }

    public static TheoryData<string, string> SecondSlotFailures => new()
    {
        { "first-launch", "-checkFirstLaunchStatus` as _dftest2 exited 69 (first launch tasks are not done): Xcode's first launch has not been completed; run `sudo xcodebuild -runFirstLaunch`" },
        { "license", "-license check` as _dftest2 exited 69 (You have not agreed to the Xcode license agreements.): Xcode's license has not been accepted; run `sudo xcodebuild -license accept`" },
        { "swift", "--version` as _dftest2 exited 127" },
        { "wrapper", "the xcodebuild wrapper" },
    };

    [Theory]
    [MemberData(nameof(SecondSlotFailures))]
    public async Task A_slot_whose_swift_toolchain_is_not_usable_fails_the_factory_naming_the_slot_its_user_and_the_fix(string what, string why)
    {
        var second = what switch
        {
            "first-launch" => Toolchain(firstLaunch: 69),
            "license" => Toolchain(license: 69),
            "swift" => Toolchain(swiftExit: 127),
            // Installed, current by its text (so start-up passes), but not runnable as the user.
            _ => Toolchain() with { WrapperPath = Path.Combine(_dir, "not-executable") },
        };
        if (what == "wrapper")
        {
            File.WriteAllText(second.WrapperPath, File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild")));
        }
        var pool = Pool(Toolchain(), second);

        await ProbeAsync(pool, 1); // slot 1's is fine
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() => ProbeAsync(pool, 2));

        Assert.Contains("Worker slot 2 (_dftest2): Swift toolchain (Worker:Swift) of _dftest2", ex.Message);
        Assert.Contains(why, ex.Message);
        Assert.Contains("sudo scripts/setup-worker-user.sh --slots 2 --swift", ex.Message);
    }

    [Fact]
    public async Task With_one_slot_the_failure_names_its_user()
    {
        // The fake helper answers `id -un` with its own file name: name it after us, the one slot's user.
        var helper = SandboxSupport.Executable(_dir, Environment.UserName, InstalledFor(Environment.UserName, "450", RepoHelper));
        var sandbox = new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir), RepoHelper) { Swift = Toolchain(firstLaunch: 69) };

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(sandbox, WorkerAuth.RouterKey, Claude, CancellationToken.None));

        Assert.Contains($"Swift toolchain (Worker:Swift) of {Environment.UserName}: ", ex.Message);
        Assert.DoesNotContain("Worker slot", ex.Message);
        Assert.Contains("`sudo scripts/setup-worker-user.sh --swift`", ex.Message);
    }

    [Fact]
    public async Task Without_worker_swift_nothing_of_swift_or_xcode_is_checked_or_run()
    {
        // A .NET/Rust-only factory (E7): no wrapper installed, no Xcode — start-up and the probe pass, and nothing Swift ran.
        var pool = Pool(null, null);

        Assert.Null(await FactoryRunner.CheckHelperAsync(pool, CancellationToken.None));
        await ProbeAsync(pool, 1);
        await ProbeAsync(pool, 2);

        // Each slot ran its probe and its claude, nothing else.
        Assert.All(HelpersRan, l => Assert.True(l.EndsWith(" /usr/bin/id -un", StringComparison.Ordinal) || l.EndsWith("/fake-claude.sh --version", StringComparison.Ordinal), l));
        Assert.Equal(4, HelpersRan.Length);
    }

    public static TheoryData<string, string> BadWrappers => new()
    {
        { "missing", "cannot read the xcodebuild wrapper" },
        { "edited", "is stale (its text differs from the repo's scripts/factory-xcodebuild" },
    };

    [Theory]
    [MemberData(nameof(BadWrappers))]
    public async Task Start_up_fails_on_a_missing_or_stale_wrapper_naming_the_slot_and_running_nothing(string what, string why)
    {
        var wrapper = what == "missing"
            ? Path.Combine(_dir, "no-such-wrapper")
            : InstalledWrapper(File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild")) + "# edited\n");
        var pool = Pool(null, Toolchain(wrapper: wrapper));

        var error = await FactoryRunner.CheckHelperAsync(pool, CancellationToken.None);

        Assert.NotNull(error);
        Assert.StartsWith("Worker slot 2 (_dftest2): Swift toolchain (Worker:Swift): ", error);
        Assert.Contains(why, error);
        Assert.Contains(wrapper, error);
        Assert.Contains("`sudo scripts/setup-worker-user.sh --slots 2 --swift`", error);
        Assert.Empty(HelpersRan); // start-up only reads files
    }

    [Fact]
    public void Worker_swift_defaults_to_off_is_read_validated_and_refused_without_a_sandbox_user()
    {
        // Off (E7): the pool carries no toolchain.
        var off = Options([]);
        Assert.False(off.WorkerSwift);
        Assert.All(off.WorkerSlots!.All, s => Assert.Null(s.Sandbox.Swift));
        off.ValidateSettings();

        // On: every slot's sandbox carries the default toolchain (the system's swift and xcodebuild, the installed wrapper).
        var on = Options(new() { ["Worker:Swift"] = "true", ["Worker:Slots"] = "2" });
        Assert.True(on.WorkerSwift);
        Assert.All(on.WorkerSlots!.All, s => Assert.Equal(new SwiftToolchain("/usr/bin/swift", "/usr/bin/xcodebuild", SwiftToolchain.DefaultWrapperPath), s.Sandbox.Swift));
        on.ValidateSettings();
        Assert.True(Options(new() { ["Worker:Swift"] = "True" }).WorkerSwift);
        Assert.False(Options(new() { ["Worker:Swift"] = "false" }).WorkerSwift);

        foreach (var refused in new[] { "yes", "1", "", "on" })
        {
            Assert.Contains("Worker:Swift must be 'true' or 'false'",
                Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Swift"] = refused }).ValidateSettings()).Message);
        }
        Assert.Contains("Worker:Swift is true, but Worker:RunAs=none",
            Assert.Throws<InvalidOperationException>(() => Options(new() { ["Worker:Swift"] = "true", ["Worker:RunAs"] = "none" }).ValidateSettings()).Message);
        Options(new() { ["Worker:Swift"] = "false", ["Worker:RunAs"] = "none" }).ValidateSettings();
    }

    private static FactoryOptions Options(Dictionary<string, string?> config) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), new InMemorySecrets());

    [Fact]
    public void An_implementer_runs_xcodebuild_only_through_the_wrappers_absolute_path_and_raw_xcodebuild_is_denied()
    {
        Assert.Equal(["Bash(/usr/local/libexec/dark-factory/factory-xcodebuild:*)"], ClaudeWorker.XcodebuildTools);
        Assert.Contains(ClaudeWorker.XcodebuildTools[0], ClaudeWorker.AllowedTools);
        // No allow rule names xcodebuild or xcrun but the wrapper's.
        Assert.All(ClaudeWorker.AllowedTools.Where(a => a.Contains("xcodebuild", StringComparison.Ordinal) || a.Contains("xcrun", StringComparison.Ordinal)),
            a => Assert.Equal(ClaudeWorker.XcodebuildTools[0], a));
        Assert.Equal(["Bash(xcodebuild:*)", "Bash(/usr/bin/xcodebuild:*)", "Bash(xcrun:*)", "Bash(/usr/bin/xcrun:*)"], ClaudeWorker.RawXcodebuildTools);
        var args = ClaudeWorker.BuildArguments("p").ToList();
        var denied = args.Skip(args.IndexOf("--disallowedTools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.All(ClaudeWorker.RawXcodebuildTools, rule => Assert.Contains(rule, denied));
        Assert.All(Taint.WebTools, rule => Assert.Contains(rule, denied));
        // Triage and the planner get none of it.
        Assert.DoesNotContain(ClaudeWorker.BuildArguments("p", null, null, WorkerTools.ReadOnly, "/wt/triage"), a => a.Contains("xcodebuild", StringComparison.Ordinal));
        // The prompts name the wrapper as the way to build an Xcode project.
        Assert.Contains(SwiftToolchain.DefaultWrapperPath + " build|test", RunPipeline.BuildsAndPasses);
    }

    [Theory]
    [InlineData("Bash(xcodebuild:*)")]
    [InlineData("Bash(xcodebuild build:*)")]
    [InlineData("Bash(/usr/bin/xcodebuild:*)")]
    [InlineData("Bash(xcrun:*)")]
    [InlineData("Bash(factory-xcodebuild:*)")]
    public void A_repos_allow_rule_for_raw_xcodebuild_taints_the_session(string rule)
    {
        var dir = Directory.CreateTempSubdirectory("df-settings-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude/settings.json"), """{"permissions":{"allow":["RULE"]}}""".Replace("RULE", rule));

        Assert.NotNull(Taint.OfRepoSettings(dir));
    }

    [Fact]
    public void A_repos_allow_rule_for_the_wrapper_the_worker_already_has_does_not_taint()
    {
        var dir = Directory.CreateTempSubdirectory("df-settings-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude/settings.json"), """{"permissions":{"allow":["RULE"]}}""".Replace("RULE", ClaudeWorker.XcodebuildTools[0]));

        Assert.Null(Taint.OfRepoSettings(dir));
    }
}
