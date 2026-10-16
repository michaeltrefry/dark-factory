using DarkFactory.Orchestrator.Gate;
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

    /// <summary>
    /// A fake xcodebuild: <c>-checkFirstLaunchStatus</c> exits <paramref name="firstLaunch"/>, <c>-license check</c> <paramref name="license"/>,
    /// <c>-showsdks</c> lists <paramref name="sdks"/> (as Xcode prints them: <c>-sdk &lt;name&gt;&lt;version&gt;</c>).
    /// </summary>
    private string Xcodebuild(int firstLaunch = 0, int license = 0, string sdks = "macosx iphonesimulator") => SandboxSupport.Executable(_dir, $"xcodebuild-{Guid.NewGuid():N}", $$"""
        #!/bin/sh
        case "$1" in
            -version) echo 'Xcode 26.5' ;;
            -showsdks) for s in {{sdks}}; do printf '\tSDK 26.5\t-sdk %s26.5\n' "$s"; done ;;
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

    /// <summary>A fake launchctl: <c>managername</c> answers <paramref name="manager"/> (a GUI login session's is <c>Aqua</c>).</summary>
    private string Launchctl(string manager = "Aqua") => SandboxSupport.Executable(_dir, $"launchctl-{Guid.NewGuid():N}",
        $"#!/bin/sh\n[ \"$1\" = managername ] || exit 2\necho {manager}\n");

    internal const string IosRuntimes = """
        {"runtimes":[{"isAvailable":true,"version":"26.5","identifier":"com.apple.CoreSimulator.SimRuntime.iOS-26-5","platform":"iOS","name":"iOS 26.5"}]}
        """;

    internal const string IphoneTypes = """
        {"devicetypes":[{"productFamily":"iPhone","identifier":"com.apple.CoreSimulator.SimDeviceType.iPhone-17","name":"iPhone 17"}]}
        """;

    /// <summary>A fake xcrun: <c>simctl list runtimes -j</c> prints <paramref name="runtimes"/>, <c>simctl list devicetypes -j</c> <paramref name="types"/>; anything else exits 2.</summary>
    private string Xcrun(string runtimes = IosRuntimes, string types = IphoneTypes)
    {
        var (r, t) = (Path.Combine(_dir, $"runtimes-{Guid.NewGuid():N}.json"), Path.Combine(_dir, $"types-{Guid.NewGuid():N}.json"));
        File.WriteAllText(r, runtimes);
        File.WriteAllText(t, types);
        return SandboxSupport.Executable(_dir, $"xcrun-{Guid.NewGuid():N}",
            $"#!/bin/sh\n[ \"$1 $2 $4\" = 'simctl list -j' ] || exit 2\ncase \"$3\" in runtimes) cat '{r}' ;; devicetypes) cat '{t}' ;; *) exit 2 ;; esac\n");
    }

    private SwiftToolchain Toolchain(int firstLaunch = 0, int license = 0, int swiftExit = 0, string? wrapper = null, string sdks = "macosx iphonesimulator",
        string manager = "Aqua", string runtimes = IosRuntimes, string types = IphoneTypes) =>
        new(Swift(swiftExit), Xcodebuild(firstLaunch, license, sdks), wrapper ?? InstalledWrapper(), LaunchctlPath: Launchctl(manager), XcrunPath: Xcrun(runtimes, types));

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
        async _ => { await FactoryRunner.EnsureSandboxReadyAsync(pool, Claude, CancellationToken.None); return true; },
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
            Assert.Contains($"{helper} {swift.XcodebuildPath} -showsdks", ran);
            Assert.Contains($"{helper} {swift.WrapperPath}", ran);
            Assert.Contains($"{helper} {swift.LaunchctlPath} managername", ran); // its launchd session, as that user (sc-26132)
            // Its simulator runtimes and device types, as that user, after the session check (sc-26134): nothing is booted.
            Assert.Equal([$"{helper} {swift.LaunchctlPath} managername", $"{helper} {swift.XcrunPath} simctl list runtimes -j", $"{helper} {swift.XcrunPath} simctl list devicetypes -j"],
                ran.Where(l => l.Contains(swift.LaunchctlPath, StringComparison.Ordinal) || l.Contains(swift.XcrunPath, StringComparison.Ordinal)));
        }
        // Each slot's probe ran through its own helper only.
        Assert.DoesNotContain(HelpersRan, l => l.StartsWith("_dftest ", StringComparison.Ordinal) && l.Contains(second.XcodebuildPath, StringComparison.Ordinal));
    }

    public static TheoryData<string, string> SecondSlotFailures => new()
    {
        { "first-launch", "-checkFirstLaunchStatus` as _dftest2 exited 69 (first launch tasks are not done): Xcode's first launch has not been completed; run `sudo xcodebuild -runFirstLaunch`" },
        { "license", "-license check` as _dftest2 exited 69 (You have not agreed to the Xcode license agreements.): Xcode's license has not been accepted; run `sudo xcodebuild -license accept`" },
        { "swift", "--version` as _dftest2 exited 127" },
        { "no-ios-simulator", "-showsdks` as _dftest2 lists no iOS Simulator SDK (-sdk iphonesimulator…): install the iOS platform (`sudo xcodebuild -downloadPlatform iOS`)" },
        { "no-sdks", "-showsdks` as _dftest2 lists no macOS SDK (-sdk macosx…) and no iOS Simulator SDK (-sdk iphonesimulator…)" },
        { "wrapper", "the xcodebuild wrapper" },
        // sc-26132 (S0's requirement (1)): outside a GUI login session no simulator boots, so the slot is refused here, not item by item.
        { "not-aqua", "managername` as _dftest2 answers 'Background', not 'Aqua', so the gate's iOS simulator checks cannot run as it" },
        // sc-26134: CoreSimulator as the slot's user, booting nothing.
        { "no-ios-runtime", "simctl list runtimes -j` as _dftest2 lists no available iOS simulator runtime, so the gate's iOS simulator checks cannot run as it: install an iOS simulator runtime (`sudo xcodebuild -downloadPlatform iOS`)" },
        { "unavailable-runtime", "simctl list runtimes -j` as _dftest2 lists no available iOS simulator runtime" },
        { "no-iphone", "simctl list devicetypes -j` as _dftest2 lists no iPhone simulator device type" },
        { "simctl-fails", "simctl list runtimes -j` as _dftest2 answers no JSON runtimes list" },
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
            "no-ios-simulator" => Toolchain(sdks: "macosx driverkit"),
            "no-sdks" => Toolchain(sdks: ""),
            "not-aqua" => Toolchain(manager: "Background"),
            "no-ios-runtime" => Toolchain(runtimes: IosRuntimes.Replace("\"platform\":\"iOS\"", "\"platform\":\"watchOS\"")),
            "unavailable-runtime" => Toolchain(runtimes: IosRuntimes.Replace("\"isAvailable\":true", "\"isAvailable\":false")),
            "no-iphone" => Toolchain(types: IphoneTypes.Replace("\"productFamily\":\"iPhone\"", "\"productFamily\":\"iPad\"")),
            "simctl-fails" => Toolchain(runtimes: "CoreSimulatorService connection became invalid"),
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
        if (what == "not-aqua")
        {
            // CoreSimulator is asked only once the user is known to be in a GUI login session (sc-26134).
            Assert.DoesNotContain(HelpersRan, l => l.StartsWith("_dftest2 ", StringComparison.Ordinal) && l.Contains("simctl", StringComparison.Ordinal));
        }
        Assert.Contains(what == "not-aqua"
            ? "run the factory inside the owner's GUI login session (a Terminal or a LaunchAgent of the owner logged in at the console), not from ssh or as a LaunchDaemon — CLAUDE.md, \"iOS simulator and UI tests as a sandbox user\""
            : "sudo scripts/setup-worker-user.sh --slots 2 --swift", ex.Message);
    }

    [Fact]
    public async Task With_one_slot_the_failure_names_its_user()
    {
        // The fake helper answers `id -un` with its own file name: name it after us, the one slot's user.
        var helper = SandboxSupport.Executable(_dir, Environment.UserName, InstalledFor(Environment.UserName, "450", RepoHelper));
        var sandbox = new WorkerSandbox(Environment.UserName, helper, SandboxSupport.FakeSudo(_dir), RepoHelper) { Swift = Toolchain(firstLaunch: 69) };

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() =>
            FactoryRunner.EnsureSandboxReadyAsync(sandbox, Claude, CancellationToken.None));

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
    public void With_worker_swift_every_slots_sandbox_carries_the_default_toolchain_and_without_it_none()
    {
        // Worker:Swift's reading and validation are FactoryOptionsTests'; this is what it gives the slot pool. Off (E7): no toolchain.
        var off = Options([]);
        Assert.All(off.WorkerSlots!.All, s => Assert.Null(s.Sandbox.Swift));
        // On: the system's swift and xcodebuild, the installed wrapper, in every slot.
        var on = Options(new() { ["Worker:Swift"] = "true", ["Worker:Slots"] = "2" });
        Assert.All(on.WorkerSlots!.All, s => Assert.Equal(new SwiftToolchain("/usr/bin/swift", "/usr/bin/xcodebuild", SwiftToolchain.DefaultWrapperPath), s.Sandbox.Swift));
    }

    private static FactoryOptions Options(Dictionary<string, string?> config) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), new InMemorySecrets());

    [Fact]
    public void An_implementer_runs_xcodebuild_only_through_the_wrappers_absolute_path_and_raw_xcodebuild_is_denied()
    {
        Assert.Equal(["Bash(/usr/local/libexec/dark-factory/factory-xcodebuild:*)"], ClaudeWorker.XcodebuildTools);
        Assert.Equal([.. ClaudeWorker.AllowedTools, .. ClaudeWorker.XcodebuildTools], ClaudeWorker.SwiftAllowedTools);
        Assert.Equal(ClaudeWorker.SwiftAllowedTools, WorkerTools.SwiftImplementer.Allowed);
        Assert.Same(WorkerTools.SwiftImplementer, WorkerTools.ImplementerFor(true));
        // No allow rule names xcodebuild or xcrun but the wrapper's.
        Assert.All(ClaudeWorker.SwiftAllowedTools.Where(a => a.Contains("xcodebuild", StringComparison.Ordinal) || a.Contains("xcrun", StringComparison.Ordinal)),
            a => Assert.Equal(ClaudeWorker.XcodebuildTools[0], a));
        var swiftArgs = ClaudeWorker.BuildArguments("p", tools: WorkerTools.SwiftImplementer).ToList();
        Assert.Contains(ClaudeWorker.XcodebuildTools[0], swiftArgs);
        Assert.Equal(["Bash(xcodebuild:*)", "Bash(/usr/bin/xcodebuild:*)", "Bash(xcrun:*)", "Bash(/usr/bin/xcrun:*)"], ClaudeWorker.RawXcodebuildTools);
        var args = ClaudeWorker.BuildArguments("p").ToList();
        var denied = args.Skip(args.IndexOf("--disallowedTools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.All(ClaudeWorker.RawXcodebuildTools, rule => Assert.Contains(rule, denied));
        Assert.All(Taint.WebTools, rule => Assert.Contains(rule, denied));
        // Triage and the planner get none of it.
        Assert.DoesNotContain(ClaudeWorker.BuildArguments("p", null, null, WorkerTools.ReadOnly, "/wt/triage"), a => a.Contains("xcodebuild", StringComparison.Ordinal));
        // A Swift factory's prompts name the wrapper as the way to build an Xcode project, macOS app or iOS app on the simulator.
        Assert.Contains(SwiftToolchain.DefaultWrapperPath + " build|test -scheme <scheme> -destination 'platform=macOS'", RunPipeline.BuildsAndPassesFor(true));
        Assert.Contains("-destination 'platform=iOS Simulator,name=<device>'", RunPipeline.BuildsAndPassesFor(true));
    }

    [Fact]
    public void A_swift_factorys_prompts_say_what_the_shell_denies_and_where_a_simulator_name_comes_from()
    {
        // sc-26134: a live iOS worker (sc-26248) spent 9 of 26 tool calls on git, raw xcrun simctl and ls outside the worktree,
        // all denied. The Swift clause says so up front; the .NET/Rust clause is unchanged (E7, pinned by the test below).
        var clause = RunPipeline.BuildsAndPassesFor(true);
        Assert.EndsWith("the only xcodebuild command allowed). " + RunPipeline.SwiftSandboxNote, clause);
        Assert.Contains("there is no git", clause);
        Assert.Contains("no raw `xcodebuild`, `xcrun` or `simctl`", clause);
        Assert.Contains("nothing outside this worktree can be read or listed", clause);
        Assert.Contains("take `<device>` from the repository's CI workflow or README", clause);
        Assert.Contains("the wrapper's xcodebuild error lists the available destinations", clause);
        Assert.False(RunPipeline.SwiftSandboxNote.EndsWith('.'), "every prompt puts its own full stop after the clause");
        Assert.DoesNotContain(RunPipeline.SwiftSandboxNote, RunPipeline.BuildsAndPassesFor(false));

        var spec = new WorkSpec(new WorkStory(1, "Story", "Do it.", "feature", "https://example.test/1", ItemNaming.Shortcut), null, []);
        var repo = RepoRef.Parse("o/r");
        foreach (var swift in new[] { false, true })
        {
            string[] prompts =
            [
                RunPipeline.BuildPrompt(spec, repo, swift),
                RunPipeline.BuildFixPrompt(spec, repo, 1, [], swift: swift),
                RunPipeline.BuildCiFixPrompt(spec, repo, 1, new string('a', 40), [], swift: swift),
                RunPipeline.BuildConflictFixPrompt(spec, repo, 1, new BaseUpdate(BaseUpdate.Kinds.Conflict, new string('a', 40), new string('b', 40), Files: ["f"]), swift: swift),
            ];
            Assert.All(prompts, prompt => Assert.Equal(swift, prompt.ReplaceLineEndings(" ").Replace("  ", " ").Contains(RunPipeline.SwiftSandboxNote + ".", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Without_worker_swift_only_the_xcode_wrapper_rule_and_prompt_clause_are_absent_and_the_always_on_swift_parts_are_there()
    {
        // With Worker:Swift unset no rule or prompt names the xcodebuild wrapper. What every factory gets whatever the setting —
        // an E7 exception like cargo's tools (sc-25877), confirmed by the owner 2026-10-15 (sc-26134): SwiftPM's two allow rules,
        // the Swift text of the build-and-test clause, raw xcodebuild/xcrun denied, and SwiftNewTests among the strategies.
        string[] alwaysOn = ["Read", "Edit", "Write", "Glob", "Grep", "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(dotnet restore:*)",
            "Bash(cargo build:*)", "Bash(cargo check:*)", "Bash(cargo test:*)", "Bash(cargo fetch:*)",
            "Bash(swift build:*)", "Bash(swift test:*)"];
        const string alwaysOnBuildsAndPasses =
            "the build and the tests pass (`dotnet build` and `dotnet test`; in a Rust package `cargo build` and `cargo test`; in a Swift package `swift build` and `swift test`)";
        var unset = Options([]);
        Assert.False(unset.WorkerSwift);
        Assert.Equal(alwaysOn, ClaudeWorker.AllowedTools);
        Assert.Equal(alwaysOn, ClaudeWorker.AllowedToolsFor(unset.WorkerSwift));
        Assert.Same(WorkerTools.Implementer, WorkerTools.ImplementerFor(unset.WorkerSwift));
        Assert.Equal(alwaysOn, WorkerTools.Implementer.Allowed);
        Assert.DoesNotContain(ClaudeWorker.BuildArguments("p"), a => a.Contains("factory-xcodebuild", StringComparison.Ordinal));
        Assert.All(ClaudeWorker.RawXcodebuildTools, rule => Assert.Contains(rule, WorkerTools.Implementer.Denied));
        Assert.Contains(NewTestsCheck.StrategiesFor(unset.WorkerSwift), s => s is SwiftNewTests);
        Assert.DoesNotContain(NewTestsCheck.StrategiesFor(unset.WorkerSwift), s => s is XcodeNewTests);
        Assert.Equal(alwaysOnBuildsAndPasses, RunPipeline.BuildsAndPasses);
        Assert.Equal(alwaysOnBuildsAndPasses, RunPipeline.BuildsAndPassesFor(unset.WorkerSwift));

        // Every implementing prompt, built as a factory without Worker:Swift builds it, names no xcodebuild; with it, each does.
        var spec = new WorkSpec(new WorkStory(1, "Story", "Do it.", "feature", "https://example.test/1", ItemNaming.Shortcut), null, []);
        var repo = RepoRef.Parse("o/r");
        foreach (var swift in new[] { false, true })
        {
            string[] prompts =
            [
                RunPipeline.BuildPrompt(spec, repo, swift),
                RunPipeline.BuildFixPrompt(spec, repo, 1, [], swift: swift),
                RunPipeline.BuildCiFixPrompt(spec, repo, 1, new string('a', 40), [], swift: swift),
                RunPipeline.BuildConflictFixPrompt(spec, repo, 1, new BaseUpdate(BaseUpdate.Kinds.Conflict, new string('a', 40), new string('b', 40), Files: ["f"]), swift: swift),
            ];
            Assert.All(prompts, prompt => Assert.Equal(swift, prompt.Contains("factory-xcodebuild", StringComparison.Ordinal)));
            Assert.All(prompts, prompt => Assert.Contains(RunPipeline.BuildsAndPassesFor(swift), prompt.ReplaceLineEndings(" ").Replace("  ", " ")));
        }
        Assert.DoesNotContain("xcodebuild", RunPipeline.BuildPrompt(spec, repo));
        // Production wires both from the one setting: the implementer's tools, and the pipeline's prompts and repo-settings taint.
        var runner = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator", "FactoryRunner.cs"));
        Assert.Contains("pauseFlagDirectory: options.PauseFlagDirectory, tools: WorkerTools.ImplementerFor(options.WorkerSwift, options.WorkerPrepare)),", runner);
        Assert.Contains("swift: options.WorkerSwift,", runner);
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
        Assert.NotNull(Taint.OfRepoSettings(dir, ClaudeWorker.SwiftAllowedTools));
    }

    [Fact]
    public void A_repos_allow_rule_for_the_wrapper_taints_only_where_the_worker_does_not_already_have_it()
    {
        var dir = Directory.CreateTempSubdirectory("df-settings-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude/settings.json"), """{"permissions":{"allow":["RULE"]}}""".Replace("RULE", ClaudeWorker.XcodebuildTools[0]));

        // Worker:Swift on: the worker has the rule already.
        Assert.Null(Taint.OfRepoSettings(dir, ClaudeWorker.AllowedToolsFor(true)));
        // Off (the default): the repository would grant what the factory does not (E7), so it taints.
        Assert.NotNull(Taint.OfRepoSettings(dir, ClaudeWorker.AllowedToolsFor(false)));
        Assert.NotNull(Taint.OfRepoSettings(dir));
    }
}
