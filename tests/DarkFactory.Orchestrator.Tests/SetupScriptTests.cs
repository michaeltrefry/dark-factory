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
        // No installs, dscl writes, ACLs or sudoers: only the read of the home the worker's account records (sc-26579).
        Assert.DoesNotContain(calls, c => !c.StartsWith("sudo ") && !c.StartsWith("security ") && c != "dscl . -read /Users/_dftest NFSHomeDirectory");
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
        Assert.Contains("\nsandbox_owner=owner\n", installed);
        Assert.Null(Worker.WorkerSandbox.StaleHelperReason(installed, "_dftest"));
    }

    [Theory]
    [InlineData("501:755", "read")]
    [InlineData("0:777", "read")]
    [InlineData("0:755", "write")]
    [InlineData("0:755", "writesecurity")]
    [InlineData("0:755", "unknown")]
    public async Task Setup_refuses_untrusted_root_trampoline_ancestor_before_installing_sudoers(string metadata, string rights)
    {
        var run = new WholeScriptRun(_dir, unsafeLauncherPath: "/usr/local", launcherMetadata: metadata, launcherRights: rights);
        var (code, output) = await run.RunAsync();
        Assert.Equal(1, code);
        Assert.Contains("untrusted launch helper path", output);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("visudo ") || (c.StartsWith("install ") && c.EndsWith("/sudoers.d/dark-factory")));
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
            [$"owner ALL=(root) NOPASSWD: {WholeScriptRun.HelperPath}", $"owner ALL=(root) NOPASSWD: {WholeScriptRun.HelperPath}-2"],
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
        Assert.Contains(calls, c => c.StartsWith($"sudo -u owner sudo -n -u root {WholeScriptRun.HelperPath} "));
        Assert.Contains(calls, c => c.StartsWith($"sudo -u owner sudo -n -u root {WholeScriptRun.HelperPath}-2 "));
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
        Assert.Equal([$"owner ALL=(root) NOPASSWD: {WholeScriptRun.HelperPath}"], run.Sudoers.Split('\n').Where(l => l.Contains("NOPASSWD")));
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
        Assert.Equal([$"owner ALL=(root) NOPASSWD: {WholeScriptRun.HelperPath}"], run.Sudoers.Split('\n').Where(l => l.Contains("NOPASSWD")));
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

    // ---- Worker homes on a factory volume (sc-26579): --home-root <dir> puts each slot's home at <dir>/<worker> ----

    /// <summary>The calls that create or change an account, a home or anything installed (none may precede a refusal).</summary>
    private static bool Changes(string c) =>
        c.StartsWith("dscl . -create") || c.StartsWith("chown ") || c.StartsWith("chmod ") || c.StartsWith("mkdir ")
        || c.StartsWith("install ") || c.StartsWith("sudo ") || c.StartsWith("visudo ");

    /// <summary>
    /// Makes <c>factory</c> in the temp directory (the home root's parent, which setup never creates) and returns its real
    /// path (no symlink in it: /var is /private/var on macOS), the form setup records homes in.
    /// </summary>
    private async Task<string> FactoryDirAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "factory"));
        var (exitCode, stdout, stderr) = await BashAsync($"cd -P '{_dir}/factory' && pwd -P");
        Assert.True(exitCode == 0, stderr);
        return stdout.Trim();
    }

    [Fact]
    public async Task Setup_with_a_home_root_puts_each_slots_home_there_owned_by_its_user_and_closed()
    {
        var homes = $"{await FactoryDirAsync()}/homes";
        var run = new WholeScriptRun(_dir, existingHome: $"{homes}/_dftest");

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--home-root", homes + "/");

        Assert.True(exitCode == 0, output);
        var calls = run.Calls.ToList();
        // The volume is checked before anything changes; the home root is made root's, and each home is the user's own, 0700.
        var check = calls.IndexOf("diskutil info /dev/disk9s1");
        Assert.True(check >= 0 && check < calls.FindIndex(Changes), output);
        Assert.Contains($"install -d -o root -g wheel -m 0755 {homes}", calls);
        Assert.Contains($"dscl . -create /Users/_dftest2 NFSHomeDirectory {homes}/_dftest2", calls);
        foreach (var user in new[] { "_dftest", "_dftest2" })
        {
            Assert.Contains($"chown {user}:{user} {homes}/{user}", calls);
            Assert.Contains($"chmod 0700 {homes}/{user}", calls);
            Assert.Contains(calls, c => c.StartsWith($"sudo -u {user} env -i HOME={homes}/{user} ") && c.Contains("claude.ai/install.sh"));
        }
        Assert.DoesNotContain(calls, c => c.Contains("/Users/_dftest") && !c.StartsWith("dscl . -read /Users/") && !c.StartsWith("dscl . -create /Users/"));
    }

    [Fact]
    public async Task Setup_without_a_home_root_keeps_homes_in_users_and_reads_no_volume()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Contains("dscl . -create /Users/_dftest2 NFSHomeDirectory /Users/_dftest2", run.Calls);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("diskutil ") || c.StartsWith("stat -f %Sd"));
    }

    [Theory]
    [InlineData("Disabled", "APFS", "/dev/disk9s1", "ignores ownership (Owners: Disabled). Enable it, then re-run: sudo diskutil enableOwnership '/Volumes/Factory'")]
    [InlineData("", "APFS", "/dev/disk9s1", "ignores ownership (Owners: )")]
    [InlineData("Enabled", "ExFAT", "/dev/disk9s1", "(Factory, mounted at /Volumes/Factory, /dev/disk9s1) is ExFAT, not APFS. Put the homes on a local APFS volume")]
    [InlineData("Enabled", "APFS", "/dev/disk4s2", "is not on a local disk volume (device 'disk9s1' is unknown to diskutil)")]
    public async Task Setup_refuses_a_home_root_on_a_volume_that_is_not_local_apfs_with_owners_before_any_account_changes(
        string owners, string personality, string node, string expected)
    {
        var homes = $"{await FactoryDirAsync()}/homes";
        var run = new WholeScriptRun(_dir, homeVolume: WholeScriptRun.HomeVolume(owners, personality, node));

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--home-root", homes);

        Assert.Equal(1, exitCode);
        Assert.Contains($"--home-root {homes}: ", output);
        Assert.Contains(expected, output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Theory]
    [InlineData("/Users/_dftest", "{home root}", "_dftest already exists with its home at '/Users/_dftest', not {home root}/_dftest", "(no --home-root)")]
    [InlineData("/Volumes/Old/homes/_dftest", "{home root}", "its home at '/Volumes/Old/homes/_dftest', not {home root}/_dftest", "(--home-root /Volumes/Old/homes)")]
    [InlineData("/Volumes/Old/homes/_dftest", null, "its home at '/Volumes/Old/homes/_dftest', not /Users/_dftest", "(--home-root /Volumes/Old/homes)")]
    public async Task Setup_refuses_to_move_an_existing_workers_home_before_any_account_changes(
        string recorded, string? homeRoot, string expected, string remedy)
    {
        var root = $"{await FactoryDirAsync()}/homes";
        var run = new WholeScriptRun(_dir, existingHome: recorded);

        var (exitCode, output) = homeRoot is null ? await run.RunAsync("--slots", "2") : await run.RunAsync("--slots", "2", "--home-root", root);

        Assert.Equal(1, exitCode);
        Assert.Contains(expected.Replace("{home root}", root) + ": setup never moves a home.", output);
        Assert.Contains($"Re-run with the home root it was set up with {remedy}", output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Setup_refuses_a_home_root_that_others_than_root_may_write()
    {
        var homes = $"{await FactoryDirAsync()}/homes";
        var run = new WholeScriptRun(_dir, existingHome: $"{homes}/_dftest", writablePath: homes);

        var (exitCode, output) = await run.RunAsync("--home-root", homes);

        Assert.Equal(1, exitCode);
        Assert.Contains($"{homes} must be owned by root and writable by no one else", output);
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("dscl . -create") || c.StartsWith("chown ") || c.StartsWith("sudo "));
    }

    [Fact]
    public async Task Setup_refuses_a_home_root_below_a_directory_others_may_write_before_any_account_changes()
    {
        // A worker that may write a directory above the home root could move it away and put its own in its place.
        var factory = await FactoryDirAsync();
        var run = new WholeScriptRun(_dir, existingHome: $"{factory}/homes/_dftest", writablePath: factory);

        var (exitCode, output) = await run.RunAsync("--home-root", $"{factory}/homes");

        Assert.Equal(1, exitCode);
        Assert.Contains($"{factory} (owner 'root', mode '775') must be owned by root or owner and writable by no one", output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Setup_refuses_a_home_root_whose_parent_does_not_exist_and_creates_nothing_above_it()
    {
        // Under /Volumes an unmounted volume's directory is missing: creating it would put the homes on the boot disk.
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--home-root", $"{_dir}/unmounted/homes");

        Assert.Equal(1, exitCode);
        Assert.Contains($"{_dir}/unmounted does not exist. Mount the volume (or create the directory the homes go in) first", output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Setup_refuses_a_home_root_under_volumes_that_is_not_on_the_volume_mounted_there()
    {
        // /Volumes itself is on the boot disk: a home root right under it, whose volume is not mounted, must not land there.
        Assert.SkipUnless(Directory.Exists("/Volumes") && !Directory.Exists("/Volumes/dftest-not-mounted"), "needs macOS's /Volumes");
        var run = new WholeScriptRun(_dir); // the fake diskutil answers a volume mounted at /Volumes/Factory

        var (exitCode, output) = await run.RunAsync("--home-root", "/Volumes/dftest-not-mounted");

        Assert.Equal(1, exitCode);
        Assert.Contains("not on a volume mounted at /Volumes/dftest-not-mounted. Mount that volume first.", output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Setup_refuses_a_home_root_that_is_a_symlink()
    {
        // stat would read the link, not the volume it leads to (e.g. one with Owners: Disabled).
        var factory = await FactoryDirAsync();
        Directory.CreateDirectory(Path.Combine(_dir, "elsewhere"));
        File.CreateSymbolicLink(Path.Combine(_dir, "factory", "homes"), Path.Combine(_dir, "elsewhere"));
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--home-root", $"{_dir}/factory/homes");

        Assert.Equal(1, exitCode);
        Assert.Contains($"--home-root {factory}/homes is a symlink or not a directory", output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Setup_resolves_a_home_root_through_a_symlinked_parent_and_records_the_real_path()
    {
        var factory = await FactoryDirAsync();
        File.CreateSymbolicLink(Path.Combine(_dir, "linked"), Path.Combine(_dir, "factory"));
        var run = new WholeScriptRun(_dir, existingHome: $"{factory}/homes/_dftest");

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--home-root", $"{_dir}/linked/homes");

        Assert.True(exitCode == 0, output);
        Assert.Contains($"stat -f %Sd {factory}", run.Calls); // the volume of the directory itself, not of the link
        Assert.Contains($"dscl . -create /Users/_dftest2 NFSHomeDirectory {factory}/homes/_dftest2", run.Calls);
    }

    [Theory]
    [InlineData("relative/homes", "--home-root must be an absolute directory")]
    [InlineData("/", "--home-root must be an absolute directory")]
    [InlineData("/Volumes/Factory/../homes", "--home-root must be an absolute directory")]
    [InlineData("/Volumes/Factory/my homes", "--home-root must be an absolute directory")]
    [InlineData("{dir}/ownerhome/homes", "--home-root must be outside the owner's home")]
    [InlineData("{dir}/work/root/homes", "--home-root must be outside the owner's home (no worker can enter it) and the work root")]
    public async Task Setup_refuses_a_home_root_it_cannot_use(string homeRoot, string expected)
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--home-root", homeRoot.Replace("{dir}", _dir));

        Assert.Equal(64, exitCode);
        Assert.Contains(expected, output);
        Assert.DoesNotContain(run.Calls, Changes);
    }

    [Fact]
    public async Task Remove_worker_login_uses_the_home_the_account_records_whatever_the_home_root()
    {
        // The recorded home is the fake home directory itself, so the real rm acts on it.
        var recorded = Path.Combine(_dir, "home");
        var run = new WholeScriptRun(_dir, existingHome: recorded);

        var (exitCode, output) = await run.RunAsync("--remove-worker-login", "--home-root", "/Volumes/Elsewhere");

        Assert.True(exitCode == 0, output);
        Assert.False(File.Exists(run.CredentialsFile), "the credentials file in the recorded home was not removed");
        Assert.All(run.Calls.Where(c => c.StartsWith("sudo ")), c => Assert.StartsWith($"sudo -u _dftest env -i HOME={recorded} ", c));
        Assert.DoesNotContain(run.Calls, c => c.StartsWith("diskutil "));
    }

    // ---- The Swift toolchain (sc-26130): the xcodebuild wrapper, and swift/xcodebuild checked as every slot's user ----

    private static readonly string[] SwiftChecks =
        ["/usr/bin/swift --version", "/usr/bin/xcodebuild -version", "/usr/bin/xcodebuild -checkFirstLaunchStatus", "/usr/bin/xcodebuild -license check",
            "/usr/bin/xcodebuild -showsdks"];

    /// <summary>Local simulator commands that remote-only Swift setup must never run.</summary>
    private static readonly string[] SimulatorChecks = ["/bin/launchctl managername", "/usr/bin/xcrun simctl list runtimes -j", "/usr/bin/xcrun simctl list devicetypes -j"];

    /// <summary>
    /// <c>xcrun simctl list runtimes -j</c> as Xcode 26.5 prints it (cut): an iOS runtime, available, whose nested device types
    /// carry fields of their own — only the runtime's own fields count.
    /// </summary>
    internal const string SimRuntimes = """
        {
          "runtimes" : [
            {
              "isAvailable" : true,
              "version" : "26.5",
              "supportedDeviceTypes" : [
                {
                  "name" : "iPhone 17 Pro",
                  "productFamily" : "iPhone"
                }
              ],
              "identifier" : "com.apple.CoreSimulator.SimRuntime.iOS-26-5",
              "platform" : "iOS",
              "name" : "iOS 26.5"
            }
          ]
        }
        """;

    internal const string SimDeviceTypes = """
        {
          "devicetypes" : [
            {
              "productFamily" : "iPhone",
              "identifier" : "com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro",
              "name" : "iPhone 17 Pro"
            }
          ]
        }
        """;

    [Fact]
    public async Task Remote_ios_swift_setup_succeeds_without_aqua_or_local_simulators()
    {
        var run = new WholeScriptRun(_dir, notAquaFor: "_dftest", noRuntimeFor: "_dftest2", noIphoneFor: "_dftest2", macosOnly: true);

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.True(exitCode == 0, output);
        Assert.DoesNotContain(run.Calls, c => SimulatorChecks.Any(check => c.Contains(check, StringComparison.Ordinal)));
        Assert.All(SwiftChecks, check => Assert.Contains(run.Calls, c => c.Contains(check, StringComparison.Ordinal)));
    }

    public static TheoryData<string, string> SimulatorFailures => new()
    {
        { "not-aqua", "FAIL  /bin/launchctl managername is 'Background', not 'Aqua': run this script from the owner's GUI login session" },
        { "no-runtime", "FAIL  /usr/bin/xcrun simctl list runtimes -j lists no available iOS simulator runtime (sudo xcodebuild -downloadPlatform iOS)" },
        { "no-iphone", "FAIL  /usr/bin/xcrun simctl list devicetypes -j lists no iPhone simulator device type (sudo xcodebuild -downloadPlatform iOS)" },
    };

    [Theory]
    [MemberData(nameof(SimulatorFailures))]
    public async Task With_swift_a_slot_that_cannot_make_an_iphone_simulator_still_passes_remote_setup(string what, string why)
    {
        var run = new WholeScriptRun(_dir, notAquaFor: what == "not-aqua" ? "_dftest2" : null, noRuntimeFor: what == "no-runtime" ? "_dftest2" : null,
            noIphoneFor: what == "no-iphone" ? "_dftest2" : null);

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.True(exitCode == 0, output);
        Assert.DoesNotContain(why, output);
        Assert.DoesNotContain(run.Calls, c => SimulatorChecks.Any(check => c.Contains(check, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Setup_installs_factory_prepare_unedited_root_owned_where_the_workers_rule_names_it()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Single(run.Calls, c => c == $"install -o root -g wheel -m 0755 {SandboxSupport.RepoRoot}/scripts/factory-prepare {Prepare.PrepareTool.InstalledPath}");
        Assert.Null(Prepare.PrepareTool.StaleReason(File.ReadAllText(run.InstalledPrepare))); // only read: never run
        Assert.Contains($"Bash({Prepare.PrepareTool.InstalledPath}:*)", Worker.ClaudeWorker.AllowedToolsFor(swift: false, prepare: true));
        Assert.DoesNotContain(Prepare.PrepareTool.AllowRule, Worker.ClaudeWorker.AllowedToolsFor(swift: true, prepare: false));
        // No sudoers rule for it: workers run it as themselves.
        Assert.DoesNotContain("factory-prepare", run.Sudoers);
    }

    [Fact]
    public async Task Setup_installs_the_xcodebuild_wrapper_unedited_root_owned_where_the_workers_rule_names_it()
    {
        var run = new WholeScriptRun(_dir);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Single(run.Calls, c => c == $"install -o root -g wheel -m 0755 {SandboxSupport.RepoRoot}/scripts/factory-xcodebuild {Worker.SwiftToolchain.DefaultWrapperPath}");
        Assert.Null(Worker.SwiftToolchain.StaleWrapperReason(File.ReadAllText(run.InstalledWrapper))); // only read: never run
        Assert.Contains($"Bash({Worker.SwiftToolchain.DefaultWrapperPath}:*)", Worker.ClaudeWorker.SwiftAllowedTools);
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
        Assert.Contains("ok    /usr/bin/xcodebuild -showsdks: ", output);
    }

    [Fact]
    public async Task Without_swift_and_with_no_xcode_selected_the_swift_check_is_skipped()
    {
        var run = new WholeScriptRun(_dir, xcodeSelected: false);

        var (exitCode, output) = await run.RunAsync("--slots", "2");

        Assert.True(exitCode == 0, output);
        Assert.Contains("Checking the optional Swift toolchain as _dftest: skipped: no Xcode selected (xcode-select -p fails)", output);
        Assert.Contains("Checking the optional Swift toolchain as _dftest2: skipped: no Xcode selected (xcode-select -p fails)", output);
        Assert.DoesNotContain(run.Calls, c => c.Contains("xcodebuild -", StringComparison.Ordinal) || c.Contains("/usr/bin/swift", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_swift_and_no_xcode_selected_the_check_still_runs_and_fails()
    {
        // --swift never skips: a missing Xcode is exactly what it must report.
        var run = new WholeScriptRun(_dir, xcodeSelected: false, swiftFailsFor: "_dftest");

        var (exitCode, output) = await run.RunAsync("--swift");

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("skipped: no Xcode selected", output);
        Assert.Contains("The Swift toolchain is not usable by: _dftest (worker slot 1)", output);
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
            Assert.All(SwiftChecks, check => Assert.Contains($"sudo -u owner sudo -n -u root {helper} {check}", run.Calls));
            Assert.DoesNotContain(run.Calls, c => SimulatorChecks.Any(check => c.Contains(check, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task With_swift_a_slot_whose_xcode_first_launch_is_not_done_fails_the_setup_naming_its_user_and_slot()
    {
        var run = new WholeScriptRun(_dir, swiftFailsFor: "_dftest2");

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.Equal(1, exitCode);
        Assert.Contains("FAIL  /usr/bin/xcodebuild -checkFirstLaunchStatus (exit 69): first launch tasks are not done", output);
        Assert.Contains("The Swift toolchain is not usable by: _dftest2 (worker slot 2)\n", output);
        Assert.Contains("sudo xcodebuild -runFirstLaunch", output);
        Assert.DoesNotContain("_dftest (worker slot 1)", output); // slot 1's passed
    }

    [Fact]
    public async Task With_swift_every_slot_is_checked_and_every_failing_user_is_named_at_the_end()
    {
        // Slot 1's first launch is not done and slot 2 lacks the macOS SDK: both are checked, both named, exit 1.
        var run = new WholeScriptRun(_dir, swiftFailsFor: "_dftest", sdksMissingFor: "_dftest2");

        var (exitCode, output) = await run.RunAsync("--slots", "2", "--swift");

        Assert.Equal(1, exitCode);
        Assert.All(SwiftChecks, check => Assert.Contains($"sudo -u owner sudo -n -u root {WholeScriptRun.HelperPath}-2 {check}", run.Calls));
        Assert.Contains("FAIL  /usr/bin/xcodebuild -showsdks lists no macosx SDK", output);
        Assert.DoesNotContain("ok    SDK macosx", output);
        Assert.Contains("The Swift toolchain is not usable by: _dftest (worker slot 1); _dftest2 (worker slot 2)\n", output);
        Assert.Contains("Done. Worker user: _dftest2", output); // slot 2 was set up after slot 1 failed
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
        private readonly string _homes;

        /// <summary>Where setup installs slot 1's helper (its default).</summary>
        public const string HelperPath = "/usr/local/libexec/dark-factory/factory-worker-launch";

        /// <param name="swiftFailsFor">A worker user (<c>_dftest</c> or <c>_dftest2</c>) whose Xcode first launch the fake helper reports as not done (exit 69).</param>
        /// <param name="sdksMissingFor">A worker user whose <c>xcodebuild -showsdks</c> lists no macOS SDK.</param>
        /// <param name="xcodeSelected">Whether the fake <c>xcode-select -p</c> answers (false: it fails, as with no Xcode).</param>
        /// <param name="notAquaFor">A worker user whose <c>launchctl managername</c> answers <c>Background</c> (no GUI login session).</param>
        /// <param name="noRuntimeFor">A worker user whose <c>xcrun simctl list runtimes -j</c> lists only an unavailable iOS runtime.</param>
        /// <param name="noIphoneFor">A worker user whose <c>xcrun simctl list devicetypes -j</c> lists only an iPad.</param>
        /// <param name="existingHome">The home the existing first worker's account records (sc-26579).</param>
        /// <param name="homeVolume">What the fake <c>diskutil info</c> prints for the volume every path is on (default: <see cref="HomeVolume"/>()).</param>
        /// <param name="writablePath">A path the fake <c>stat -f %Lp</c> gives mode 775 (every other: 755).</param>
        public WholeScriptRun(string dir, string uid = "450", string secondUid = "451", string? swiftFailsFor = null,
            string? sdksMissingFor = null, bool xcodeSelected = true, bool macosOnly = false, string? notAquaFor = null, string? noRuntimeFor = null, string? noIphoneFor = null,
            string existingHome = "/Users/_dftest", string? homeVolume = null, string? writablePath = null,
            string? unsafeLauncherPath = null, string launcherMetadata = "0:755", string launcherRights = "read")
        {
            _dir = dir;
            InstalledHelper = Path.Combine(dir, "installed-helper");
            InstalledWrapper = Path.Combine(dir, "installed-xcodebuild-wrapper");
            InstalledPrepare = Path.Combine(dir, "installed-factory-prepare");
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
                            probe_user=_dftest
                            case "$a" in *-2) probe_user=_dftest2 ;; esac
                            case "$probe_user $* " in "{{swiftFailsFor ?? "nobody-fails"}} "*-checkFirstLaunchStatus*) echo "first launch tasks are not done" >&2; exit 69 ;; esac
                            case "$probe_user $* " in
                                "{{sdksMissingFor ?? "nobody-fails"}} "*-showsdks*) printf 'iOS Simulator SDKs:\n\tSimulator - iOS 26.5\t-sdk iphonesimulator26.5\n'; exit 0 ;;
                                {{(macosOnly ? "*-showsdks*) printf 'macOS SDKs:\\n\\tmacOS 26.5\\t-sdk macosx26.5\\n'; exit 0 ;;" : "")}}
                                *-showsdks*) printf 'macOS SDKs:\n\tmacOS 26.5\t-sdk macosx26.5\niOS Simulator SDKs:\n\tSimulator - iOS 26.5\t-sdk iphonesimulator26.5\n'; exit 0 ;;
                                "{{notAquaFor ?? "nobody-fails"}} "*"/bin/launchctl managername"*) echo Background; exit 0 ;;
                                *"/bin/launchctl managername"*) echo Aqua; exit 0 ;;
                                "{{noRuntimeFor ?? "nobody-fails"}} "*"simctl list runtimes -j"*) printf '%s\n' '{{SimRuntimes.Replace("\"isAvailable\" : true", "\"isAvailable\" : false")}}'; exit 0 ;;
                                *"simctl list runtimes -j"*) printf '%s\n' '{{SimRuntimes}}'; exit 0 ;;
                                "{{noIphoneFor ?? "nobody-fails"}} "*"simctl list devicetypes -j"*) printf '%s\n' '{{SimDeviceTypes.Replace("\"productFamily\" : \"iPhone\"", "\"productFamily\" : \"iPad\"")}}'; exit 0 ;;
                                *"simctl list devicetypes -j"*) printf '%s\n' '{{SimDeviceTypes}}'; exit 0 ;;
                            esac
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
            // stat: the owner of a path (root), its mode (775 for writablePath, else 755), and the disk device it is on.
            SandboxSupport.Executable(bin, "stat", $$"""
                #!/bin/sh
                echo "stat $*" >>'{{_log}}'
                case "$2" in
                    %u:%Lp) [ "$3" = '{{unsafeLauncherPath ?? "/nonexistent"}}' ] && echo '{{launcherMetadata}}' || echo '0:755' ;;
                    %Sd) echo disk9s1 ;;
                    %Lp) [ "$3" = '{{writablePath ?? "/nonexistent"}}' ] && echo 775 || echo 755 ;;
                    *) echo root ;;
                esac
                """);
            SandboxSupport.Executable(bin, "ls", $$"""
                #!/bin/sh
                echo "ls $*" >>'{{_log}}'
                echo 'drwxr-xr-x+ 2 root wheel 64 Jan 1 00:00 trusted-path'
                if [ "$2" = '{{unsafeLauncherPath ?? "/nonexistent"}}' ]; then
                    echo ' 0: user:outsider allow {{launcherRights}}'
                fi
                """);
            SandboxSupport.Executable(bin, "diskutil", $$"""
                #!/bin/sh
                echo "diskutil $*" >>'{{_log}}'
                [ "$*" = "info /dev/disk9s1" ] || exit 1
                printf '%s\n' '{{homeVolume ?? HomeVolume()}}'
                """);
            // The homes accounts record: the owner's, the first worker's (existingHome), and each one a run creates.
            _homes = Path.Combine(dir, "homes.recorded");
            File.WriteAllText(_homes, $"owner {dir}/ownerhome\n_dftest {existingHome}\n");
            SandboxSupport.Executable(bin, "dscl", $$"""
                #!/bin/sh
                echo "dscl $*" >>'{{_log}}'
                case "$*" in
                    ". -create /Users/"*" NFSHomeDirectory "*) echo "${3#/Users/} $5" >>'{{_homes}}' ;;
                    *NFSHomeDirectory*)
                        u=${3#/Users/}
                        h=$(awk -v u="$u" '$1 == u { h = $2 } END { print h }' '{{_homes}}')
                        echo "NFSHomeDirectory: ${h:-/Users/$u}" ;;
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
                    */factory-prepare) cp "${@: -2:1}" '{{InstalledPrepare}}' ;;
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
            SandboxSupport.Executable(bin, "xcode-select", xcodeSelected
                ? "#!/bin/sh\n[ \"$1\" = -p ] && echo /Applications/Xcode.app/Contents/Developer\n"
                : "#!/bin/sh\necho 'xcode-select: error: unable to get active developer directory' >&2\nexit 2\n");
        }

        /// <summary><c>diskutil info</c> of a volume (macOS 26's layout, cut), local unless <paramref name="node"/> says otherwise.</summary>
        public static string HomeVolume(string owners = "Enabled", string personality = "APFS", string node = "/dev/disk9s1") => $"""
               Device Identifier:         disk9s1
               Device Node:               {node}
               Volume Name:               Factory
               Mounted:                   Yes
               Mount Point:               /Volumes/Factory
               File System Personality:   {personality}
               Owners:                    {owners}
               Device Location:           Internal
            """;

        public string CredentialsFile { get; }

        public string InstalledHelper { get; }

        /// <summary>The copy the fake install kept of the installed xcodebuild wrapper.</summary>
        public string InstalledWrapper { get; }

        /// <summary>The copy the fake install kept of the installed factory-prepare (sc-26232).</summary>
        public string InstalledPrepare { get; }

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
                for c in sudo id uname dscl chown chmod install visudo mkdir stat ls diskutil security pkill killall pgrep rm xcode-select; do
                    [ "$(command -v "$c")" = '{{bin}}'/"$c" ] || { echo "fake $c not first on PATH" >&2; exit 99; }
                done
                /bin/bash '{{script}}' --owner owner --worker _dftest --work-root '{{_dir}}/work/root' {{args}} 2>&1
                """);
            return (exitCode, stdout + stderr);
        }
    }

    /// <summary>sc-26197: Worker:Auth=claude-login is retired; the setup script names it nowhere.</summary>
    [Fact]
    public void The_setup_script_names_no_claude_login_mode()
    {
        var script = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "scripts", "setup-worker-user.sh"));
        Assert.DoesNotContain("claude-login", script, StringComparison.OrdinalIgnoreCase);
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
