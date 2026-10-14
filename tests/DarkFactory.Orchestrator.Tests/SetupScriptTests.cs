using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The toolchain check in <c>scripts/setup-worker-user.sh</c>, run without root: its functions are
/// lifted from the script and run as the current user against a <see cref="SafeHelper"/> copy of the repo's helper, with
/// sudo replaced by a pass-through function. The copy's <c>sandbox_user</c> is not us, so it never kills by uid.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SetupScriptTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-setup-").FullName;

    private string SlowTool => SandboxSupport.Executable(_dir, "slow-tool.sh", "#!/bin/sh\nsleep 1 && echo ok\n");

    [Fact]
    public async Task Piping_the_variable_block_in_closes_stdin_and_the_helper_kills_the_tool()
    {
        var (exitCode, stdout, _) = await BashAsync($"printf '\\n' | '{SafeHelper.Create(_dir).Path}' '{SlowTool}'");

        Assert.Equal(137, exitCode);
        Assert.DoesNotContain("ok", stdout);
    }

    [Fact]
    public async Task Toolchain_check_holds_stdin_open_until_the_tool_answers()
    {
        var tool = SlowTool;

        var (exitCode, stdout, stderr) = await CheckToolchainAsync(tool);

        Assert.True(exitCode == 0, stderr);
        Assert.Contains($"ok    {tool}: ok", stdout);
    }

    [Fact]
    public async Task Toolchain_check_reports_each_failure_and_how_to_fix_it()
    {
        var good = SlowTool;
        var bad = SandboxSupport.Executable(_dir, "missing-tool.sh", "#!/bin/sh\necho 'not installed' >&2\nexit 127\n");

        var (exitCode, stdout, stderr) = await CheckToolchainAsync(good, bad);

        Assert.Equal(1, exitCode);
        Assert.Contains($"ok    {good}: ok", stdout);
        Assert.Contains($"FAIL  {bad} (exit 127): not installed", stderr);
        Assert.Contains("re-run this script", stderr);
    }

    [Fact]
    public async Task A_missing_optional_tool_is_reported_with_what_needs_it_and_never_fails_the_setup()
    {
        var good = SlowTool;
        var bad = SandboxSupport.Executable(_dir, "missing-tool.sh", "#!/bin/sh\necho 'not installed' >&2\nexit 127\n");

        var (exitCode, stdout, stderr) = await RunCheckAsync("check_optional_toolchain 'Rust repos only' ", good, bad);

        Assert.True(exitCode == 0, stderr);
        Assert.Contains($"ok    {good}: ok", stdout);
        Assert.Contains($"none  {bad} (exit 127): not available to _factory (Rust repos only)", stdout);
    }

    [Fact]
    public async Task Remove_worker_login_deletes_only_the_credentials_and_never_runs_the_helper_or_the_setup()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--remove-worker-login");

        Assert.True(exitCode == 0, output);
        Assert.Contains("ok    _dftest holds no Claude login", output);
        Assert.False(File.Exists(run.CredentialsFile), "the credentials file was not removed");
        Assert.Equal(0, run.KeychainItems);
        var calls = run.Calls;
        Assert.DoesNotContain(calls, c => c.Contains("factory-worker-launch")); // the helper (whose exit kills every worker process) never runs
        Assert.DoesNotContain(calls, c => c.StartsWith("KILL ")); // pkill, killall and pgrep are fakes that only record
        Assert.DoesNotContain(calls, c => !c.StartsWith("sudo ") && !c.StartsWith("security ")); // no installs, dscl, ACLs, sudoers
        Assert.All(calls.Where(c => c.StartsWith("sudo ")), c =>
            Assert.Matches(@"^sudo -u _dftest env -i HOME=/Users/_dftest PATH=\S+ (/bin/rm|/bin/test|/usr/bin/security) ", c));
    }

    [Fact]
    public async Task A_normal_run_warns_that_it_kills_every_worker_process_before_the_helper_runs_and_keeps_the_login()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync();

        Assert.True(exitCode == 0, output);
        var warning = output.IndexOf("KILLS EVERY", StringComparison.Ordinal);
        var firstHelperResult = output.IndexOf("    ok    ", StringComparison.Ordinal);
        Assert.True(warning >= 0 && firstHelperResult > warning, output);
        Assert.Contains(run.Calls, c => c.Contains("factory-worker-launch"));
        Assert.True(File.Exists(run.CredentialsFile), "a normal run removed the worker's credentials file");
        Assert.Equal(2, run.KeychainItems);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("security "));
    }

    [Fact]
    public async Task Setup_installs_the_helper_pinned_to_the_worker_and_its_uid_which_the_orchestrator_accepts()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync();

        Assert.True(exitCode == 0, output);
        var installed = File.ReadAllText(run.InstalledHelper); // only read: never run
        Assert.Contains("\nsandbox_user=_dftest\n", installed);
        Assert.Contains("\nsandbox_uid=450\n", installed);
        Assert.Null(Worker.WorkerSandbox.StaleHelperReason(installed, "_dftest"));
    }

    // ---- Worker slots (sc-25989): --slots <n> sets up n users, each with a helper copy pinned to itself ----

    [Fact]
    public async Task Setup_with_two_slots_creates_two_users_each_with_its_own_uid_home_acls_claude_and_pinned_helper()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        var calls = run.Calls;
        // Each slot's helper is installed at its own path, pinned to its own user and uid, and is the one the orchestrator accepts for that user only.
        var first = File.ReadAllText(run.InstalledHelperOf(1)); // only read: never run
        var second = File.ReadAllText(run.InstalledHelperOf(2));
        Assert.Contains("\nsandbox_user=_dftest\n", first);
        Assert.Contains("\nsandbox_uid=450\n", first);
        Assert.Contains("\nsandbox_user=_dftest2\n", second);
        Assert.Contains("\nsandbox_uid=451\n", second);
        Assert.Null(Worker.WorkerSandbox.StaleHelperReason(first, "_dftest"));
        Assert.Null(Worker.WorkerSandbox.StaleHelperReason(second, "_dftest2"));
        Assert.NotNull(Worker.WorkerSandbox.StaleHelperReason(first, "_dftest2"));
        // The paths are the ones the orchestrator derives for the slots.
        Assert.Contains(calls, c => c.StartsWith("install ") && c.EndsWith(" " + Worker.WorkerSlots.HelperOf(WholeScriptRun.HelperPath, 1)));
        Assert.Contains(calls, c => c.StartsWith("install ") && c.EndsWith(" " + Worker.WorkerSlots.HelperOf(WholeScriptRun.HelperPath, 2)));
        // One sudoers rule per slot: that slot's helper, as that slot's user only.
        Assert.Equal(
            [$"owner ALL=(_dftest) NOPASSWD: {WholeScriptRun.HelperPath}", $"owner ALL=(_dftest2) NOPASSWD: {WholeScriptRun.HelperPath}-2"],
            run.Sudoers.Split('\n').Where(l => l.Contains("NOPASSWD")));
        // Each user: its home made its own and closed, the owner's home denied, the work root readable.
        foreach (var user in new[] { "_dftest", "_dftest2" })
        {
            Assert.Contains($"chown {user}:{user} /Users/{user}", calls);
            Assert.Contains($"chmod 0700 /Users/{user}", calls);
            Assert.Contains(calls, c => c.StartsWith($"chmod +a user:{user} deny list,search,read,") && c.EndsWith($" {_dir}/ownerhome"));
            Assert.Contains(calls, c => c.StartsWith($"chmod +a user:{user} allow list,search,read,") && c.EndsWith($" {_dir}/work/root"));
            // Its own Claude Code install, and the toolchain checked through its own helper as itself.
            Assert.Contains(calls, c => c.StartsWith($"sudo -u {user} env -i HOME=/Users/{user} ") && c.Contains("claude.ai/install.sh"));
        }
        Assert.Contains(calls, c => c.StartsWith($"sudo -u owner sudo -n -u _dftest {WholeScriptRun.HelperPath} "));
        Assert.Contains(calls, c => c.StartsWith($"sudo -u owner sudo -n -u _dftest2 {WholeScriptRun.HelperPath}-2 "));
        // Never one slot's helper as the other slot's user.
        Assert.DoesNotContain(calls, c => c.Contains($"-u _dftest2 {WholeScriptRun.HelperPath} ") || c.Contains($"-u _dftest {WholeScriptRun.HelperPath}-2 "));
        Assert.Contains("Set Worker__Slots=2", output);
        Assert.Contains("--slots 2 --remove-worker-login", output);
    }

    [Fact]
    public async Task Setup_without_slots_is_the_one_user_the_one_helper_at_the_same_path_and_the_one_rule()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync();

        Assert.True(exitCode == 0, output);
        Assert.Single(run.Calls, c => c.StartsWith("install ") && c.Contains("factory-worker-launch"));
        Assert.Contains(run.Calls, c => c.StartsWith("install ") && c.EndsWith(" " + WholeScriptRun.HelperPath));
        Assert.Equal([$"owner ALL=(_dftest) NOPASSWD: {WholeScriptRun.HelperPath}"], run.Sudoers.Split('\n').Where(l => l.Contains("NOPASSWD")));
        // Nothing is set up for a second user (the only mention of it is the read that finds no such account).
        Assert.DoesNotContain(run.Calls, c => c.Contains("_dftest2") && c != "dscl . -read /Users/_dftest2");
        Assert.DoesNotContain("Worker__Slots", output);
        Assert.DoesNotContain("No longer set up", output);
    }

    /// <summary>The removals of installed helpers the run made (the fake <c>rm</c>'s record of every path outside the temp directory).</summary>
    private static string[] Removals(IEnumerable<string> calls) => [.. calls.Where(c => c.StartsWith("rm "))];

    private static string[] HelperRemovals(int from) =>
        [.. Enumerable.Range(from, 8 - from + 1).Select(n => $"rm -f -- {WholeScriptRun.HelperPath}-{n}")];

    [Fact]
    public async Task A_rerun_with_fewer_slots_removes_the_higher_slots_helpers_by_their_exact_paths_and_says_which_users_it_left()
    {
        // Left installed, slot 2's helper would still pass the orchestrator's start-up check (which only reads it) with
        // Worker:Slots still 2, although its sudoers rule is gone: the factory would fail in its first worker phase instead.
        var run = new WholeScriptRun(_dir);
        var (firstExit, firstOutput) = await run.RunAsync("--slots", "2");
        Assert.True(firstExit == 0, firstOutput);
        // Two slots set up: only the helpers of slots 3 to 8 are named, never slot 1's or slot 2's.
        Assert.Equal(HelperRemovals(3), Removals(run.Calls));
        Assert.DoesNotContain("No longer set up", firstOutput);
        var before = run.Calls.Length;

        var (exitCode, output) = await run.RunAsync();

        Assert.True(exitCode == 0, output);
        // Exactly the fixed paths of slots 2 to 8, each by name: no glob, no directory, no -r, and never slot 1's helper.
        Assert.Equal(HelperRemovals(2), Removals(run.Calls.Skip(before)));
        Assert.Equal([$"owner ALL=(_dftest) NOPASSWD: {WholeScriptRun.HelperPath}"], run.Sudoers.Split('\n').Where(l => l.Contains("NOPASSWD")));
        // The rules are replaced before the helpers go.
        var calls = run.Calls.Skip(before).ToList();
        Assert.True(calls.FindIndex(c => c.StartsWith("install ") && c.EndsWith("/sudoers.d/dark-factory")) < calls.FindIndex(c => c.StartsWith("rm ")), output);
        // The account stays, and the output says so, by name (and names no user that never existed).
        Assert.Contains("No longer set up (more slots than --slots 1): _dftest2\n", output);
        Assert.Contains("has no\n    launch helper and no sudoers rule now: nothing can be run as it", output);
        Assert.DoesNotContain("_dftest3", output);
        Assert.DoesNotContain(calls, c => c.StartsWith("dscl . -delete"));
    }

    [Fact]
    public async Task Setup_with_all_eight_slots_removes_no_helper()
    {
        // The loop over the slots above n is empty at n = 8 (a `seq 9 8` would count down on macOS and name slot 9 and slot 8).
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "8");

        Assert.True(exitCode == 0, output);
        Assert.Empty(Removals(run.Calls));
        Assert.Equal(8, run.Sudoers.Split('\n').Count(l => l.Contains("NOPASSWD")));
        Assert.DoesNotContain("No longer set up", output);
    }

    [Fact]
    public async Task Setup_refuses_two_slots_whose_users_share_a_uid_before_installing_anything()
    {
        // Each helper sweeps by its pinned uid: two slots on one uid would kill each other's workers.
        var run = new WholeScriptRun(_dir, secondUid: "450");

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.Equal(1, exitCode);
        Assert.Contains("_dftest2 has uid 450, which another slot's worker user already has", output);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("install ") || c.StartsWith("visudo ") || c.StartsWith("sudo "));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("two")]
    public async Task Setup_refuses_a_slot_count_outside_1_to_8(string slots)
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", slots);

        Assert.Equal(64, exitCode);
        Assert.Contains("--slots must be a whole number from 1 to 8", output);
        Assert.DoesNotContain(run.Calls, c => !c.StartsWith("dscl ")); // nothing but (at most) reads
    }

    [Fact]
    public async Task Remove_worker_login_with_slots_removes_each_users_login_and_still_runs_no_helper()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--remove-worker-login");

        Assert.True(exitCode == 0, output);
        Assert.Contains("ok    _dftest holds no Claude login", output);
        Assert.Contains("ok    _dftest2 holds no Claude login", output);
        Assert.False(File.Exists(run.CredentialsFile) || File.Exists(run.CredentialsFileOf(2)), "a credentials file was not removed");
        Assert.DoesNotContain(run.Calls, c => c.Contains("factory-worker-launch"));
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("KILL "));
        Assert.All(run.Calls.Where(c => c.StartsWith("sudo ")), c =>
            Assert.Matches(@"^sudo -u (_dftest|_dftest2) env -i HOME=/Users/\1 PATH=\S+ (/bin/rm|/bin/test|/usr/bin/security) ", c));
    }

    [Theory]
    [InlineData("89")] // an existing role account outside the range setup allocates (e.g. _spotlight's)
    [InlineData("501")] // a login account
    public async Task Setup_refuses_a_worker_whose_uid_is_outside_400_499_before_installing_anything(string uid)
    {
        var run = new WholeScriptRun(_dir, uid);

        var (exitCode, output) = await run.RunAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains($"_dftest has uid '{uid}', outside 400-499", output);
        Assert.False(File.Exists(run.InstalledHelper));
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("install ") || c.StartsWith("sudo "));
    }

    // ---- The Swift toolchain (sc-26130): the xcodebuild wrapper, and swift/xcodebuild checked as every slot's user ----

    private static readonly string[] SwiftChecks =
        ["/usr/bin/swift --version", "/usr/bin/xcodebuild -version", "/usr/bin/xcodebuild -checkFirstLaunchStatus", "/usr/bin/xcodebuild -license check"];

    [Fact]
    public async Task Setup_installs_the_xcodebuild_wrapper_unedited_root_owned_where_the_workers_rule_names_it()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Single(run.Calls, c => c == $"install -o root -g wheel -m 0755 {SandboxSupport.RepoRoot}/scripts/factory-xcodebuild {Worker.SwiftToolchain.DefaultWrapperPath}");
        Assert.Null(Worker.SwiftToolchain.StaleWrapperReason(File.ReadAllText(run.InstalledWrapper))); // only read: never run
        Assert.Contains($"Bash({Worker.SwiftToolchain.DefaultWrapperPath}:*)", Worker.ClaudeWorker.AllowedTools);
        // No sudoers rule for it: workers run it as themselves.
        Assert.DoesNotContain("factory-xcodebuild", run.Sudoers);
    }

    [Fact]
    public async Task Without_swift_the_swift_toolchain_of_each_user_is_only_reported_and_a_missing_one_never_fails_the_setup()
    {
        // A .NET/Rust-only factory needs no Xcode (E7).
        var run = new WholeScriptRun(_dir, swiftFailsFor: "_dftest2");

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Contains("Checking the optional Swift toolchain as _dftest2 through the helper", output);
        Assert.Contains("ok    /usr/bin/xcodebuild -checkFirstLaunchStatus: fake 1.0", output); // _dftest's
        Assert.Contains("none  /usr/bin/xcodebuild -checkFirstLaunchStatus (exit 69): not available to _dftest2 (Swift repos only", output);
    }

    [Fact]
    public async Task With_swift_every_users_swift_toolchain_is_checked_through_its_own_helper()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.True(exitCode == 0, output);
        foreach (var (user, helper) in new[] { ("_dftest", WholeScriptRun.HelperPath), ("_dftest2", WholeScriptRun.HelperPath + "-2") })
        {
            Assert.Contains($"Checking the Swift toolchain as {user} through the helper (--swift: required)", output);
            Assert.All(SwiftChecks, check => Assert.Contains($"sudo -u owner sudo -n -u {user} {helper} {check}", run.Calls));
        }
    }

    [Fact]
    public async Task With_swift_a_slot_whose_xcode_first_launch_is_not_done_fails_the_setup_naming_its_user_and_slot()
    {
        var run = new WholeScriptRun(_dir, swiftFailsFor: "_dftest2");

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.Equal(1, exitCode);
        Assert.Contains("FAIL  /usr/bin/xcodebuild -checkFirstLaunchStatus (exit 69): first launch tasks are not done", output);
        Assert.Contains("The Swift toolchain is not usable by _dftest2 (worker slot 2)", output);
        Assert.Contains("sudo xcodebuild -runFirstLaunch", output);
        Assert.DoesNotContain("The Swift toolchain is not usable by _dftest ", output); // slot 1's passed
    }

    /// <summary>
    /// The whole <c>setup-worker-user.sh</c>, run as the current user (never root, never real sudo) with every privileged
    /// or system-changing command replaced on PATH by a fake that records its call. Worker <c>_dftest</c>, whose home
    /// <c>/Users/_dftest</c> the fake sudo maps to a temp dir holding a Claude credentials file and a keychain with two
    /// <c>Claude Code-credentials</c> items, and uid <paramref name="uid"/> (dscl). The fake sudo answers a helper invocation
    /// itself; nothing ever execs a helper. The fake install keeps a copy of the installed helper (<see cref="InstalledHelper"/>).
    /// With <c>--slots 2</c> the second worker is <c>_dftest2</c> (uid <paramref name="secondUid"/>), with a home and a login of
    /// its own; each installed helper copy is kept (<see cref="InstalledHelperOf"/>), and the sudoers file (<see cref="Sudoers"/>).
    /// </summary>
    private sealed class WholeScriptRun
    {
        private readonly string _dir;
        private readonly string _log;
        private readonly string _items;
        private readonly string _sudoers;
        private readonly string _users;

        /// <summary>Where setup installs slot 1's helper (its default).</summary>
        public const string HelperPath = "/usr/local/libexec/dark-factory/factory-worker-launch";

        /// <param name="swiftFailsFor">A worker user (<c>_dftest</c> or <c>_dftest2</c>) whose Xcode first launch the fake helper reports as not done (exit 69).</param>
        public WholeScriptRun(string dir, string uid = "450", string secondUid = "451", string? swiftFailsFor = null)
        {
            _dir = dir;
            InstalledHelper = Path.Combine(dir, "installed-helper");
            InstalledWrapper = Path.Combine(dir, "installed-xcodebuild-wrapper");
            var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
            var home = Path.Combine(dir, "home");
            foreach (var each in new[] { home, home + "2" })
            {
                Directory.CreateDirectory(Path.Combine(each, ".claude"));
                Directory.CreateDirectory(Path.Combine(each, "Library", "Keychains"));
                File.WriteAllText(Path.Combine(each, ".claude", ".credentials.json"), "{}");
                File.WriteAllText(Path.Combine(each, "Library", "Keychains", "login.keychain-db"), "");
            }
            CredentialsFile = CredentialsFileOf(1);
            _sudoers = Path.Combine(dir, "installed-sudoers");
            _log = Path.Combine(dir, "calls.log");
            _items = Path.Combine(dir, "keychain-items");
            // The accounts that exist: the first worker's (as before slots); every other is created by the run that sets it up.
            _users = Path.Combine(dir, "users");
            File.WriteAllText(_users, "_dftest\n");
            File.WriteAllText(_items, "2");
            File.WriteAllText(_log, "");

            SandboxSupport.Executable(bin, "sudo", $$"""
                #!/bin/bash
                echo "sudo $*" >>'{{_log}}'
                for a in "$@"; do
                    case "$a" in
                        */factory-worker-launch | */factory-worker-launch-[0-9])
                            IFS= read -r _
                            case "$* " in *"-u {{swiftFailsFor ?? "nobody-fails"}} "*-checkFirstLaunchStatus*) echo "first launch tasks are not done" >&2; exit 69 ;; esac
                            echo "fake 1.0"; exit 0 ;;
                    esac
                done
                [ "$1" = -u ] && { [ "$2" = _dftest ] || [ "$2" = _dftest2 ]; } && [ "$3" = env ] || exit 0
                home='{{home}}'
                [ "$2" = _dftest ] || home='{{home}}2'
                user=$2
                shift 3
                while [ "$1" = -i ] || [[ $1 == *=* ]]; do shift; done
                args=()
                for a in "$@"; do args+=("${a//\/Users\/$user/$home}"); done
                case "${args[0]}" in
                    /bin/rm) ;; # the real rm, on the mapped path in the fake home (the fake rm below is for the script's own calls)
                    /bin/test) args[0]=test ;;
                    /usr/bin/security) args[0]='{{bin}}/security' ;;
                    *) exit 0 ;; # e.g. the Claude Code installer: recorded, not run
                esac
                exec "${args[@]}"
                """);
            SandboxSupport.Executable(bin, "security", $$"""
                #!/bin/bash
                echo "security $*" >>'{{_log}}'
                [ "$1" = delete-generic-password ] || exit 1
                n=$(cat '{{_items}}')
                [ "$n" -gt 0 ] || exit 44
                echo $((n - 1)) >'{{_items}}'
                """);
            SandboxSupport.Executable(bin, "uname", "#!/bin/sh\necho Darwin\n");
            SandboxSupport.Executable(bin, "id", "#!/bin/sh\n[ \"$1\" = -u ] && echo 0\nexit 0\n");
            SandboxSupport.Executable(bin, "stat", "#!/bin/sh\necho root\n");
            SandboxSupport.Executable(bin, "dscl", $$"""
                #!/bin/sh
                echo "dscl $*" >>'{{_log}}'
                case "$*" in
                    *NFSHomeDirectory*) echo "NFSHomeDirectory: {{dir}}/ownerhome" ;;
                    *PrimaryGroupID*) echo "PrimaryGroupID: 450" ;;
                    */Users/_dftest2\ UniqueID*) echo "UniqueID: {{secondUid}}" ;;
                    */Users/_dftest[3-8]\ UniqueID*) n="$*"; n=${n#*/Users/_dftest}; echo "UniqueID: $((449 + ${n%% *}))" ;;
                    *UniqueID*) echo "UniqueID: {{uid}}" ;;
                    ". -create /Users/"*) [ $# -eq 3 ] && echo "${3#/Users/}" >>'{{_users}}' ;;
                    ". -read /Users/"*) [ $# -eq 3 ] && { grep -qx "${3#/Users/}" '{{_users}}' || exit 1; } ;;
                esac
                exit 0
                """);
            // The script's own removals: recorded and NOT done unless the path is under the temp directory (its mktemp
            // files). Whatever else it would remove - an installed helper, as root in the real run - only shows in the log.
            SandboxSupport.Executable(bin, "rm", $$"""
                #!/bin/bash
                for a in "$@"; do
                    case "$a" in
                        -*) ;;
                        /tmp/?* | /private/tmp/?* | /var/folders/?* | /private/var/folders/?*) ;;
                        *) echo "rm $*" >>'{{_log}}'; exit 0 ;;
                    esac
                done
                exec /bin/rm "$@"
                """);
            SandboxSupport.Executable(bin, "install", $$"""
                #!/bin/bash
                echo "install $*" >>'{{_log}}'
                case "${@: -1}" in
                    */factory-worker-launch) cp "${@: -2:1}" '{{InstalledHelper}}' ;;
                    */factory-worker-launch-[0-9]) dest="${@: -1}"; cp "${@: -2:1}" '{{InstalledHelper}}'"-${dest##*-}" ;;
                    */sudoers.d/dark-factory) cp "${@: -2:1}" '{{_sudoers}}' ;;
                    */factory-xcodebuild) cp "${@: -2:1}" '{{InstalledWrapper}}' ;;
                esac
                """);
            foreach (var name in new[] { "chown", "chmod", "visudo", "mkdir", "curl" })
            {
                SandboxSupport.Executable(bin, name, $"#!/bin/sh\necho \"{name} $*\" >>'{_log}'\n");
            }
            foreach (var name in new[] { "pkill", "killall", "pgrep" })
            {
                SandboxSupport.Executable(bin, name, $"#!/bin/sh\necho \"KILL {name} $*\" >>'{_log}'\n");
            }
        }

        public string CredentialsFile { get; }

        public string InstalledHelper { get; }

        /// <summary>The copy the fake install kept of the installed xcodebuild wrapper.</summary>
        public string InstalledWrapper { get; }

        /// <summary>The copy the fake install kept of slot <paramref name="slot"/>'s installed helper.</summary>
        public string InstalledHelperOf(int slot) => slot == 1 ? InstalledHelper : $"{InstalledHelper}-{slot}";

        /// <summary>Slot <paramref name="slot"/>'s worker's Claude credentials file, in its fake home.</summary>
        public string CredentialsFileOf(int slot) => Path.Combine(_dir, slot == 1 ? "home" : $"home{slot}", ".claude", ".credentials.json");

        /// <summary>The sudoers file setup installed (the fake install's copy).</summary>
        public string Sudoers => File.ReadAllText(_sudoers);

        public int KeychainItems => int.Parse(File.ReadAllText(_items).Trim());

        public string[] Calls => File.ReadAllLines(_log);

        public async Task<(int ExitCode, string Output)> RunAsync(params string[] flags)
        {
            var script = Path.Combine(SandboxSupport.RepoRoot, "scripts", "setup-worker-user.sh");
            var bin = Path.Combine(_dir, "bin");
            var args = string.Join(' ', flags.Select(f => $"'{f}'"));
            // Refuse to run unless every privileged command resolves to its fake.
            var (exitCode, stdout, stderr) = await BashAsync(_dir, $$"""
                export PATH='{{bin}}':/usr/bin:/bin
                for c in sudo id uname dscl chown chmod install visudo mkdir stat security pkill killall pgrep rm; do
                    [ "$(command -v "$c")" = '{{bin}}'/"$c" ] || { echo "fake $c not first on PATH" >&2; exit 99; }
                done
                /bin/bash '{{script}}' --owner owner --worker _dftest --work-root '{{_dir}}/work/root' {{args}} 2>&1
                """);
            return (exitCode, stdout + stderr);
        }
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> CheckToolchainAsync(params string[] tools) => RunCheckAsync("check_toolchain ", tools);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunCheckAsync(string call, params string[] tools)
    {
        var script = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "setup-worker-user.sh"));
        var functions = string.Join("\n", new[] { "with_open_stdin", "check_toolchain", "check_optional_toolchain" }.Select(name =>
        {
            var match = Regex.Match(script, $@"^{name}\(\) \{{.*?^\}}$", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(match.Success, $"{name} not found in setup-worker-user.sh");
            return match.Value;
        }));
        var harness = $$"""
            sudo() { while :; do case "$1" in -n) shift ;; -u) shift 2 ;; *) break ;; esac; done; "$@"; }
            owner=owner worker=_factory sudoers=/nonexistent worker_home=/nonexistent
            helper='{{SafeHelper.Create(_dir).Path}}'
            {{functions}}
            {{call}}{{string.Join(' ', tools.Select(t => $"'{t}'"))}}
            """;
        return BashAsync(harness);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> BashAsync(string script) => BashAsync(_dir, script);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> BashAsync(string dir, string script)
    {
        var psi = new ProcessStartInfo("/bin/bash", ["-c", script])
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout, await stderr);
    }
}
