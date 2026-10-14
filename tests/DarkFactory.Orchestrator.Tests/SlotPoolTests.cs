using System.Collections.Concurrent;
using System.Reflection;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25989: the pool of worker slots. Each slot is one sandbox user with its own launch helper and lock; two runs are in a worker
/// phase at once exactly when they hold different slots; an item keeps the slot its first worker phase took (recorded in the
/// ledger), so its resume, the stop of a worker a crashed run left and the deletion of its worktree are that slot's user's, waited
/// for while the slot is busy. No test here runs a launch helper but a fake of the test's own (never the repo's, never sudo).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SlotPoolTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);
    private readonly string _root = Directory.CreateTempSubdirectory("df-slots-").FullName;

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(condition(), what);
    }

    // ---- The pool ----

    [Fact]
    public void A_configured_pool_names_each_slots_user_helper_and_lock_and_slot_1_is_the_sandbox_as_it_was()
    {
        var pool = WorkerSlots.Create("_factory", "/opt/h/launch", _root, 3);

        Assert.Equal(["_factory", "_factory2", "_factory3"], pool.All.Select(s => s.User));
        Assert.Equal(["/opt/h/launch", "/opt/h/launch-2", "/opt/h/launch-3"], pool.All.Select(s => s.Sandbox.HelperPath));
        Assert.Equal([".factory-run.lock", ".factory-run-2.lock", ".factory-run-3.lock"], pool.All.Select(s => Path.GetFileName(s.Lock!.Path)));
        Assert.Equal(WorkerLock.For(_root), pool.All[0].Lock);
        Assert.All(pool.All, s => Assert.Equal("sudo scripts/setup-worker-user.sh --slots 3", s.Sandbox.Reinstall));
        // One slot: exactly the sandbox before slots, the setup command included.
        var one = Assert.Single(WorkerSlots.Create("_factory", "/opt/h/launch", _root, 1).All);
        Assert.Equal(new WorkerSandbox("_factory", "/opt/h/launch") { Lock = WorkerLock.For(_root) }, one.Sandbox);
        Assert.Equal(WorkerSandbox.ReinstallCommand, one.Sandbox.Reinstall);
    }

    [Fact]
    public void A_pool_of_several_refuses_slots_that_share_a_user_a_helper_or_a_lock()
    {
        WorkerSlot Slot(int n, string user, string helper, int lockOf) =>
            new(n, new WorkerSandbox(user, helper) { Lock = WorkerLock.For(_root, lockOf) });

        Assert.Throws<ArgumentException>(() => new WorkerSlots([Slot(1, "_a", "/h1", 1), Slot(2, "_a", "/h2", 2)]));
        Assert.Throws<ArgumentException>(() => new WorkerSlots([Slot(1, "_a", "/h1", 1), Slot(2, "_b", "/h1", 2)]));
        Assert.Throws<ArgumentException>(() => new WorkerSlots([Slot(1, "_a", "/h1", 1), Slot(2, "_b", "/h2", 1)]));
        Assert.Throws<ArgumentException>(() => new WorkerSlots([Slot(1, "_a", "/h1", 1), new WorkerSlot(2, new WorkerSandbox("_b", "/h2"))]));
        Assert.Throws<ArgumentException>(() => new WorkerSlots([Slot(2, "_a", "/h1", 1)]));
        Assert.Throws<ArgumentException>(() => WorkerSlots.Create("_a", "/h", _root, WorkerSlots.MaxSlots + 1));
        _ = new WorkerSlots([Slot(1, "_a", "/h1", 1), Slot(2, "_b", "/h2", 2)]);
    }

    [Fact]
    public async Task Two_flows_hold_two_slots_at_once_and_a_third_waits_for_the_first_that_frees()
    {
        var pool = TestSlots.Pool(_root, 2);
        var (first, second, third) = (new TaskCompletionSource<int>(), new TaskCompletionSource<int>(), new TaskCompletionSource<int>());
        var (releaseFirst, releaseSecond) = (new TaskCompletionSource(), new TaskCompletionSource());
        var waits = new ConcurrentQueue<string>();

        var a = pool.HoldAsync(null, async slot => { first.SetResult(slot.Number); await releaseFirst.Task; return pool.Current.User; }, CancellationToken.None);
        Assert.Equal(1, await first.Task.WaitAsync(Soon));
        var b = pool.HoldAsync(null, async slot => { second.SetResult(slot.Number); await releaseSecond.Task; return pool.Current.User; }, CancellationToken.None);
        Assert.Equal(2, await second.Task.WaitAsync(Soon)); // slot 1 is held: the second flow is in slot 2 at the same time
        var c = pool.HoldAsync(null, slot => { third.SetResult(slot.Number); return Task.FromResult(pool.Current.User); }, CancellationToken.None,
            retry: Poll, report: waits.Enqueue, reportEvery: TimeSpan.Zero);
        await Eventually(() => !waits.IsEmpty, "the third flow reports its wait");
        Assert.False(third.Task.IsCompleted);
        Assert.Contains("for a free worker slot", waits.First());
        Assert.Contains("hold all 2", waits.First());
        Assert.Null(pool.Held); // this flow holds none of them

        releaseSecond.SetResult();
        Assert.Equal("_dftest2", await b.WaitAsync(Soon));
        Assert.Equal(2, await third.Task.WaitAsync(Soon)); // the one that freed
        Assert.Equal("_dftest2", await c.WaitAsync(Soon));
        releaseFirst.SetResult();
        Assert.Equal("_dftest", await a.WaitAsync(Soon));
    }

    [Fact]
    public async Task A_named_slot_is_waited_for_while_it_is_busy_even_though_another_is_free()
    {
        var pool = TestSlots.Pool(_root, 2);
        var busy = WorkerLock.Acquire(_root, 2); // another run's worker phase in slot 2
        var waits = new ConcurrentQueue<string>();
        var ran = new TaskCompletionSource<int>();

        var hold = pool.HoldAsync(2, slot => { ran.SetResult(slot.Number); return Task.FromResult(true); }, CancellationToken.None,
            retry: Poll, report: waits.Enqueue, reportEvery: TimeSpan.Zero);
        await Eventually(() => !waits.IsEmpty, "the wait for slot 2 is reported");
        Assert.False(ran.Task.IsCompleted); // slot 1 is free, and is not taken
        using (WorkerLock.Acquire(_root, 1))
        {
        }
        Assert.Contains(pool.All[1].Lock!.Path, waits.First());

        busy.Dispose();
        await hold.WaitAsync(Soon);
        Assert.Equal(2, await ran.Task);
    }

    [Fact]
    public async Task A_free_slot_is_taken_in_the_order_given_and_a_flow_inside_a_slot_stays_in_it()
    {
        var pool = TestSlots.Pool(_root, 3);

        Assert.Equal(3, await pool.HoldAsync(null, slot => Task.FromResult(slot.Number), CancellationToken.None, order: [3, 1, 2]));
        Assert.Equal(1, await pool.HoldAsync(null, slot => Task.FromResult(slot.Number), CancellationToken.None));
        // Inside a slot: any phase runs in it at once, its own included; another slot is refused (a flow is one sandbox user).
        await pool.HoldAsync(2, async held =>
        {
            Assert.Equal(2, pool.Held!.Number);
            Assert.Equal(2, await pool.HoldAsync(null, slot => Task.FromResult(slot.Number), CancellationToken.None, order: [1, 3]));
            Assert.Equal(2, await pool.HoldAsync(2, slot => Task.FromResult(slot.Number), CancellationToken.None));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.HoldAsync(1, slot => Task.FromResult(slot.Number), CancellationToken.None));
            Assert.Contains("Refusing to use worker slot 1 inside a phase that holds slot 2 (_dftest2)", ex.Message);
            return held.Number;
        }, CancellationToken.None);
        Assert.Null(pool.Held);
    }

    [Fact]
    public async Task What_runs_as_a_sandbox_user_is_the_held_slots_and_with_several_slots_a_flow_holding_none_has_no_sandbox_user()
    {
        var pool = TestSlots.Pool(_root, 2);

        Assert.Contains("No worker slot is held", Assert.Throws<InvalidOperationException>(() => pool.Current).Message);
        Assert.Equal("_dftest2", await pool.HoldAsync(2, _ => Task.FromResult(pool.Current.User), CancellationToken.None));
        // Given a slot without holding it (owner-side work on an item's worktree): its user is named, and nothing can be started as it.
        await pool.AssignedAsync(2, () =>
        {
            Assert.Equal("_dftest2", pool.Current.User);
            Assert.Null(pool.Held);
            Assert.Contains("is not held", Assert.Throws<InvalidOperationException>(() => pool.Current.Start("/", "/usr/bin/true", [], new Dictionary<string, string>())).Message);
            return Task.FromResult(true);
        });
        Assert.Throws<InvalidOperationException>(() => pool.Current); // the assignment did not flow back
        // One slot: the sandbox as before slots, whose commands take the lock themselves.
        Assert.Equal("_dftest", TestSlots.Pool(_root, 1).Current.User);
        Assert.Contains("worker slot 3 is not configured (Worker:Slots is 2)", Assert.Throws<SlotUnavailableException>(() => pool.Slot(3)).Message);
    }

    // ---- Which slot a directory is deleted in: the one it was shared with ----

    private const string ReadOnlyListing = """
        drwx------+ 3 owner  staff  96 Oct 13 12:00 /work/worktrees/o/r/factory-triage-gh-1
         0: user:_dftest inherited allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit
         1: user:_dftest2 inherited allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit
        """;

    private static string SharedListing(string user) => $"""
        drwxr-xr-x+ 3 owner  staff  96 Oct 13 12:00 /work/worktrees/o/r/factory-sc-7
         0: user:{Environment.UserName} allow list,add_file,search,delete,add_subdirectory,delete_child,readattr,writeattr,readextattr,writeextattr,readsecurity,file_inherit,directory_inherit
         1: user:{user} allow list,add_file,search,delete,add_subdirectory,delete_child,readattr,writeattr,readextattr,writeextattr,readsecurity,file_inherit,directory_inherit
         2: user:_dftest inherited allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit
         3: user:_dftest2 inherited allow list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit
        """;

    [Fact]
    public void A_directory_is_the_slots_whose_user_its_acl_lets_write_and_the_work_roots_read_entries_are_no_share()
    {
        var pool = TestSlots.Pool(_root, 2);

        Assert.Equal(1, pool.SharedWith("/wt", SharedListing("_dftest"))!.Number);
        Assert.Equal(2, pool.SharedWith("/wt", SharedListing("_dftest2"))!.Number);
        Assert.Null(pool.SharedWith("/wt", ReadOnlyListing));
        Assert.Null(pool.SharedWith("/wt", "drwx------  3 owner  staff  96 Oct 13 12:00 /wt\n"));
        // Fails closed: a user above Worker:Slots, or two slots' users, is never deleted as "some" slot's user.
        var above = Assert.Throws<InvalidOperationException>(() => pool.SharedWith("/wt", SharedListing("_dftest3")));
        Assert.Contains("/wt is shared for writing with user:_dftest3, not with one of this factory's 2 sandbox users (_dftest, _dftest2)", above.Message);
        Assert.Contains("raise Worker:Slots", above.Message);
        Assert.Throws<InvalidOperationException>(() => pool.SharedWith("/wt",
            SharedListing("_dftest") + "\n 4: user:_dftest2 allow list,add_file,search,delete_child"));
        // So is an entry ls could not name (it prints the UUID while the directory service does not answer), and a group's.
        Assert.Throws<InvalidOperationException>(() => pool.SharedWith("/wt",
            ReadOnlyListing + "\n 2: FFFFEEEE-DDDD-CCCC-BBBB-AAAA000001C3 allow list,add_file,search,delete_child"));
        Assert.Throws<InvalidOperationException>(() => pool.SharedWith("/wt", ReadOnlyListing + "\n 2: group:staff allow list,add_file"));
    }

    /// <summary>
    /// A pool whose helpers are fakes of this test (each appends its slot's number to <see cref="Ran"/> and runs the command as the
    /// current user) behind a fake sudo, and whose ACL reads answer <paramref name="listing"/>: which slot's helper a deletion runs
    /// through, and under which lock.
    /// </summary>
    private WorkerSlots FakeHelperPool(int count, Func<string, string> listing)
    {
        var sudo = SandboxSupport.FakeSudo(_root);
        return new WorkerSlots([.. Enumerable.Range(1, count).Select(n => new WorkerSlot(n, new WorkerSandbox(TestSlots.Pool(_root, count).All[n - 1].User,
            SandboxSupport.Executable(_root, $"fake-helper-{n}.sh",
                $"#!/bin/sh\nwhile IFS= read -r line; do [ -z \"$line\" ] && break; done\necho {n} >> '{Ran}'\nexec \"$@\"\n"), sudo)
        { Lock = WorkerLock.For(_root, n) }))])
        {
            ListAcl = (path, _) => Task.FromResult(listing(path)),
        };
    }

    private string Ran => Path.Combine(_root, "helpers-ran");

    private string[] HelpersRan => File.Exists(Ran) ? File.ReadAllLines(Ran) : [];

    private string NewDirectory(string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(path, "file"), "x");
        return path;
    }

    [Fact]
    public async Task A_worktree_is_deleted_through_the_helper_of_the_slot_it_was_shared_with_whichever_slot_asks()
    {
        var pool = FakeHelperPool(2, _ => SharedListing("_dftest2"));
        var (outside, inSlot1) = (NewDirectory("outside"), NewDirectory("in-slot-1"));

        // A flow that holds no slot waits for (and takes) slot 2, the directory's.
        await pool.DeleteAsWorkerAsync(outside, CancellationToken.None);
        Assert.False(Directory.Exists(outside));
        Assert.Equal(["2"], HelpersRan);
        // A flow that holds slot 1 (a sweep in another item's worker phase) still deletes it as slot 2's user, free now.
        await pool.HoldAsync(1, _ => pool.TryDeleteAsWorkerAsync(inSlot1, CancellationToken.None), CancellationToken.None);
        Assert.False(Directory.Exists(inSlot1));
        Assert.Equal(["2", "2"], HelpersRan);
    }

    [Fact]
    public async Task A_worktree_of_a_busy_slot_is_left_alone_by_a_run_in_another_slot_and_never_deleted_as_another_user()
    {
        var pool = FakeHelperPool(2, _ => SharedListing("_dftest2"));
        var worktree = NewDirectory("slot-2s");
        using var busy = WorkerLock.Acquire(_root, 2); // slot 2's item is in a worker session

        // Clean-up that a later sweep does as well: nothing deleted, no helper run, nothing waited for.
        Assert.False(await pool.TryDeleteAsWorkerAsync(worktree, CancellationToken.None));
        Assert.False(await pool.HoldAsync(1, _ => pool.TryDeleteAsWorkerAsync(worktree, CancellationToken.None), CancellationToken.None));
        // A deletion that must happen, from inside slot 1: refused rather than done as slot 1's user (or waited for: two such runs would deadlock).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pool.HoldAsync(1, async _ => { await pool.DeleteAsWorkerAsync(worktree, CancellationToken.None); return true; }, CancellationToken.None));

        Assert.Contains("was shared with slot 2 (_dftest2)", ex.Message);
        Assert.Contains("this run holds slot 1 (_dftest)", ex.Message);
        Assert.True(Directory.Exists(worktree));
        Assert.Empty(HelpersRan);
    }

    [Fact]
    public async Task A_sweep_in_one_slot_leaves_a_busy_slots_leftover_worktree_and_deletes_it_as_that_slots_user_once_it_is_free()
    {
        // The real workspace over the pool: a leftover worktree of an item of slot 2 (no run will resume it), found by the sweep
        // another item's first worker phase runs in slot 1.
        var pool = FakeHelperPool(2, _ => SharedListing("_dftest2"));
        var workRoot = Path.Combine(_root, "work");
        var leftover = Directory.CreateDirectory(Path.Combine(workRoot, GitWorkspace.ItemWorktrees, "o", "r", "factory-sc-9")).FullName;
        File.WriteAllText(Path.Combine(leftover, "file"), "x");
        var git = new GitWorkspace(workRoot, _ => "unused", (_, _) => Task.FromResult<string?>(null), sandbox: pool);
        Task Sweep() => pool.HoldAsync(1, async _ =>
        {
            await git.SweepOrphansAsync((_, _) => Task.FromResult(false), LiveRuns.None, CancellationToken.None);
            return true;
        }, CancellationToken.None);

        var busy = WorkerLock.Acquire(_root, 2); // slot 2's item is in a worker session
        await Sweep().WaitAsync(Soon); // never waits for slot 2
        Assert.True(File.Exists(Path.Combine(leftover, "file")), "a busy slot's leftover was deleted from another slot");
        Assert.Empty(HelpersRan);

        busy.Dispose();
        await Sweep().WaitAsync(Soon);
        Assert.False(Directory.Exists(leftover));
        Assert.Equal(["2"], HelpersRan); // through slot 2's helper, never slot 1's
    }

    [Fact]
    public async Task A_directory_shared_with_no_slot_is_deleted_by_the_held_slot_or_by_any_free_one()
    {
        var pool = FakeHelperPool(2, _ => ReadOnlyListing);
        var (held, free) = (NewDirectory("triage-held"), NewDirectory("triage-free"));

        await pool.HoldAsync(2, async _ => { await pool.DeleteAsWorkerAsync(held, CancellationToken.None); return true; }, CancellationToken.None);
        Assert.Equal(["2"], HelpersRan);
        using (WorkerLock.Acquire(_root, 1)) // slot 1 is busy: the free one does it
        {
            Assert.True(await pool.TryDeleteAsWorkerAsync(free, CancellationToken.None));
        }
        Assert.Equal(["2", "2"], HelpersRan);
        Assert.False(Directory.Exists(held) || Directory.Exists(free));
    }

    [Fact]
    public async Task A_fresh_worktree_is_shared_with_the_held_slots_user_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // Real macOS role accounts stand in for the two sandbox users: the ACL entry must name an existing user. Nothing runs as them.
        var pool = new WorkerSlots([
            new WorkerSlot(1, new WorkerSandbox("daemon", TestSlots.NoHelper) { Lock = WorkerLock.For(_root, 1) }),
            new WorkerSlot(2, new WorkerSandbox("nobody", TestSlots.NoHelper + "-2") { Lock = WorkerLock.For(_root, 2) })]);
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;

        await pool.HoldAsync(2, async _ => { await pool.ShareAsync(worktree, CancellationToken.None); return true; }, CancellationToken.None);

        var acl = "";
        for (var read = 0; read < 50 && !acl.Contains("user:nobody allow", StringComparison.Ordinal); read++)
        {
            // ls prints an entry's UUID instead of its name while the directory service is slow to answer (a loaded test host).
            acl = await WorkerSandbox.ListAclAsync(worktree, CancellationToken.None);
        }
        Assert.Contains("user:nobody allow", acl);
        Assert.DoesNotContain("user:daemon", acl);
        Assert.Equal(2, pool.SharedWith(worktree, acl)!.Number); // and read back from the directory itself: slot 2's
    }

    // ---- Readiness: every slot's installed helper, by name ----

    /// <summary>A helper as the repo has it, and as setup installs it for <paramref name="user"/> (never run here: only read).</summary>
    private static string RepoHelper(int version = WorkerSandbox.HelperVersion) =>
        $"#!/bin/bash\nsandbox_user=_factory\nsandbox_uid=\nhelper_version={version}\nexec \"$@\"\n";

    private static string InstalledFor(string user, string uid, string? repo = null) => (repo ?? RepoHelper())
        .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={user}\n").Replace("\nsandbox_uid=\n", $"\nsandbox_uid={uid}\n");

    /// <summary>Two slots whose installed helpers are the files <c>helper</c> and <c>helper-2</c> under the test's directory (<paramref name="second"/>: slot 2's text, null: not installed).</summary>
    private WorkerSlots InstalledPool(string? second)
    {
        var first = Path.Combine(_root, "helper");
        File.WriteAllText(first, InstalledFor("_dftest", "450"));
        if (second is not null)
        {
            File.WriteAllText(WorkerSlots.HelperOf(first, 2), second);
        }
        return new WorkerSlots([.. Enumerable.Range(1, 2).Select(n => new WorkerSlot(n,
            new WorkerSandbox(WorkerSlots.UserOf("_dftest", n), WorkerSlots.HelperOf(first, n), HelperSource: RepoHelper())
            {
                Lock = WorkerLock.For(_root, n),
                Reinstall = WorkerSlots.ReinstallCommand(2),
            }))]);
    }

    [Fact]
    public async Task Start_up_passes_when_every_slots_helper_is_current_and_pinned_to_its_own_user()
    {
        var pool = InstalledPool(InstalledFor("_dftest2", "451"));

        Assert.Null(await FactoryRunner.CheckHelperAsync(pool, CancellationToken.None));
        await pool.EnsureHelpersCurrentAsync(CancellationToken.None);
    }

    public static TheoryData<string, string> BadSecondHelpers => new()
    {
        { "missing", "cannot read the launch helper" },
        { "stale", "its helper_version 3 is older than 4" },
        { "edited", "its text differs from the repo helper" },
        { "other-user", "its sandbox_user is not _dftest2" }, // slot 1's helper copied over slot 2's: pinned to another user
        { "unpinned", "it has no pinned sandbox_uid" },
    };

    [Theory]
    [MemberData(nameof(BadSecondHelpers))]
    public async Task Start_up_fails_naming_the_slot_whose_installed_helper_is_stale_missing_or_pinned_to_another_user(string what, string why)
    {
        var pool = InstalledPool(what switch
        {
            "missing" => null,
            "stale" => InstalledFor("_dftest2", "451", RepoHelper(version: 3)),
            "edited" => InstalledFor("_dftest2", "451") + "# edited\n",
            "other-user" => InstalledFor("_dftest", "450"),
            _ => InstalledFor("_dftest2", ""),
        });

        var error = await FactoryRunner.CheckHelperAsync(pool, CancellationToken.None);

        Assert.NotNull(error);
        Assert.StartsWith("Worker slot 2 (_dftest2): ", error);
        Assert.Contains(why, error);
        Assert.Contains(WorkerSlots.HelperOf(Path.Combine(_root, "helper"), 2), error);
        // What the owner runs, with the slot count the script needs.
        Assert.Contains("`sudo scripts/setup-worker-user.sh --slots 2`", error);
        // And every run's start fails the same way, the factory's failure (E10), before anything is claimed.
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() => FactoryRunner.EnsureHelperCurrentAsync(pool, CancellationToken.None));
        Assert.Contains("Worker slot 2 (_dftest2): ", ex.Message);
    }

    [Fact]
    public async Task With_one_slot_a_stale_helper_is_reported_as_before_slots()
    {
        var helper = Path.Combine(_root, "helper");
        File.WriteAllText(helper, InstalledFor("_dftest", "450", RepoHelper(version: 3)));
        var pool = WorkerSlots.Single(new WorkerSandbox("_dftest", helper, HelperSource: RepoHelper()));

        var error = await FactoryRunner.CheckHelperAsync(pool, CancellationToken.None);

        Assert.StartsWith("Stale helper: the installed launch helper", error);
        Assert.Contains("re-run `sudo scripts/setup-worker-user.sh` to install it", error);
        Assert.DoesNotContain("--slots", error);
    }

    [Fact]
    public async Task The_readiness_probe_is_of_the_held_slot_through_its_own_helper_and_names_it_when_it_fails()
    {
        // Slot 1's helper answers as its user; slot 2's answers as someone else (its sudoers rule or its pin is wrong).
        var sudo = SandboxSupport.FakeSudo(_root);
        var repo = "#!/bin/bash\nsandbox_user=_factory\nsandbox_uid=\nhelper_version=" + WorkerSandbox.HelperVersion
            + "\nwhile IFS= read -r line; do [ -z \"$line\" ] && break; done\necho \"$0\" >> '" + Ran + "'\n"
            + "case \"$1\" in /usr/bin/id) echo \"${0##*/}\" ;; *) exec \"$@\" ;; esac\n";
        var first = SandboxSupport.Executable(_root, "_dftest", InstalledFor("_dftest", "450", repo));
        var second = SandboxSupport.Executable(_root, "someone-else", InstalledFor("_dftest2", "451", repo));
        var claude = SandboxSupport.Executable(_root, "fake-claude.sh", "#!/bin/sh\necho '2.1.291 (Claude Code)'\n");
        var pool = new WorkerSlots([
            new WorkerSlot(1, new WorkerSandbox("_dftest", first, sudo, repo) { Lock = WorkerLock.For(_root, 1) }),
            new WorkerSlot(2, new WorkerSandbox("_dftest2", second, sudo, repo) { Lock = WorkerLock.For(_root, 2) })]);

        await pool.HoldAsync(1, async _ => { await FactoryRunner.EnsureSandboxReadyAsync(pool, WorkerAuth.RouterKey, claude, CancellationToken.None); return true; },
            CancellationToken.None);
        Assert.All(HelpersRan, ran => Assert.Equal(first, ran)); // only slot 1's helper ran for slot 1's probe
        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() => pool.HoldAsync(2,
            async _ => { await FactoryRunner.EnsureSandboxReadyAsync(pool, WorkerAuth.RouterKey, claude, CancellationToken.None); return true; }, CancellationToken.None));

        Assert.Contains("Worker slot 2 (_dftest2): Worker sandbox user '_dftest2' is not usable through " + second, ex.Message);
        Assert.Contains(second, HelpersRan);
    }

    // ---- The pipeline: a slot per item, recorded, and kept ----

    /// <summary>A probe on a seam of the pipeline: each call's name with the slot the calling flow held.</summary>
    private sealed class SlotProbe(WorkerSlots slots)
    {
        public ConcurrentQueue<(string Call, int? Slot)> Calls { get; } = new();

        public T Wrap<T>(T target) where T : class
        {
            var proxy = DispatchProxy.Create<T, Probe<T>>();
            var probe = (Probe<T>)(object)proxy;
            probe.Target = target;
            probe.On = call => Calls.Enqueue((call, slots.Held?.Number));
            return proxy;
        }

        public List<int?> Of(string call) => [.. Calls.Where(c => c.Call == call).Select(c => c.Slot)];
    }

    private static RunPipeline Pipeline(Harness h, WorkerSlots slots, SlotProbe probe, IWorker worker, TextWriter log,
        Func<CancellationToken, Task>? enterSandbox = null) =>
        new(h.Stories, h.Ledger, h.Locks, probe.Wrap<IRepoWorkspace>(h.Workspaces), probe.Wrap(worker), h.Prs, Sandbox, log,
            controls: h.Controls, controlPollInterval: Poll, maxControlReadFailures: 2, slots: slots, enterSandbox: enterSandbox);

    private static async Task<List<string?>> SlotRows(Harness h) =>
        [.. (await h.Ledger.HistoryAsync((await h.Ledger.FindAsync(ItemNaming.Shortcut.Source, "sc-77", CancellationToken.None))!, CancellationToken.None))
            .Where(e => e.Step == RunPipeline.Steps.Slot).Select(e => e.Detail)];

    private sealed class SyncWriter : StringWriter
    {
        private readonly Lock _gate = new();

        public override void Write(char value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                base.Write(value);
            }
        }

        public bool Logged(string text)
        {
            lock (_gate)
            {
                return ToString().Contains(text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task With_two_slots_two_items_are_in_a_worker_session_at_the_same_time_each_as_its_own_sandbox_user()
    {
        var slots = TestSlots.Pool(_root, 2);
        var (a, b) = (new Harness(), new Harness());
        var (probeA, probeB) = (new SlotProbe(slots), new SlotProbe(slots));
        var (aIn, bIn, release) = (new TaskCompletionSource<string>(), new TaskCompletionSource<string>(), new TaskCompletionSource());
        var entered = new ConcurrentQueue<int?>();
        FakeWorker Worker(TaskCompletionSource<string> inSession) => new(async call =>
        {
            inSession.SetResult(slots.Current.User); // the sandbox user this session would run as
            await release.Task;
            return await ReportsModel(ImplementerModel)(call);
        });
        Task Entered(CancellationToken _)
        {
            entered.Enqueue(slots.Held?.Number);
            return Task.CompletedTask;
        }

        var runA = Task.Run(() => Pipeline(a, slots, probeA, Worker(aIn), TextWriter.Null, Entered).RunAsync(77, CancellationToken.None));
        Assert.Equal("_dftest", await aIn.Task.WaitAsync(Soon));
        var runB = Task.Run(() => Pipeline(b, slots, probeB, Worker(bIn), TextWriter.Null, Entered).RunAsync(77, CancellationToken.None));
        // B's session starts while A's is still running: two worker sessions at once, as two sandbox users.
        Assert.Equal("_dftest2", await bIn.Task.WaitAsync(Soon));
        Assert.False(runA.IsCompleted);

        release.SetResult();
        var (outcomeA, outcomeB) = (await runA, await runB);

        Assert.Equal((WorkState.Review, (string?)null), (outcomeA.State, outcomeA.Error));
        Assert.Equal((WorkState.Review, (string?)null), (outcomeB.State, outcomeB.Error));
        // The slot is on each item's ledger, and everything done as a sandbox user for the item was done in it.
        Assert.Equal(["1 _dftest"], await SlotRows(a));
        Assert.Equal(["2 _dftest2"], await SlotRows(b));
        foreach (var (probe, slot) in new[] { (probeA, 1), (probeB, 2) })
        {
            Assert.Equal([slot], probe.Of("IWorker.RunAsync"));
            Assert.Equal([slot], probe.Of("IRepoWorkspace.RemoveAsync"));
            // The worktree's set-up (a fresh one, or one found there) too: no call of the workspace was made in the other slot.
            Assert.All(probe.Calls.Where(c => c.Call.StartsWith("IRepoWorkspace.", StringComparison.Ordinal)), c => Assert.Equal(slot, c.Slot));
        }
        // Each run's sandbox set-up (the readiness probe, the sweep) ran in its own slot.
        Assert.Equal([1, 2], entered.Order());
        Assert.Null(slots.Held);
    }

    [Fact]
    public async Task The_slot_is_recorded_under_its_lock_before_the_items_first_worktree_or_session_exists()
    {
        var slots = TestSlots.Pool(_root, 2);
        var h = new Harness();
        var probe = new SlotProbe(slots);
        List<string?>? atSetUp = null;

        var outcome = await Pipeline(h, slots, probe, new FakeWorker(ReportsModel(ImplementerModel)), TextWriter.Null,
            async _ => atSetUp = await SlotRows(h)).RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Equal(["1 _dftest"], atSetUp); // already there when the run's first worker phase set the sandbox up
        Assert.Equal(["1 _dftest"], await SlotRows(h)); // once: later phases of the item read it
    }

    [Fact]
    public async Task An_item_interrupted_in_slot_2_resumes_its_session_in_slot_2_waiting_for_it_while_it_is_busy()
    {
        var slots = TestSlots.Pool(_root, 2);
        var h = new Harness();
        var probe = new SlotProbe(slots);
        using var ctrlC = new CancellationTokenSource();
        var worker = new FakeWorker(
            async call =>
            {
                await call.OnSession("sess-77", CancellationToken.None);
                ctrlC.Cancel();
                throw new OperationCanceledException(ctrlC.Token);
            },
            ReportsModel(ImplementerModel));

        // Its first worker phase finds slot 1 busy (another item's session) and takes slot 2; the run is then interrupted.
        using (WorkerLock.Acquire(_root, 1))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => Pipeline(h, slots, probe, worker, TextWriter.Null).RunAsync(77, ctrlC.Token));
        }
        Assert.Equal(WorkState.Paused, (await h.Transitions())[^1]);
        Assert.Equal(["2 _dftest2"], await SlotRows(h));
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("remove")); // its worktree is kept, in slot 2

        // Now slot 1 is free and slot 2 is busy: the resume waits for slot 2 and starts nothing in slot 1.
        var log = new SyncWriter();
        var busy = WorkerLock.Acquire(_root, 2);
        var run = Task.Run(() => Pipeline(h, slots, probe, worker, log).RunAsync(77, CancellationToken.None));
        await Eventually(() => log.Logged("sc-77: waiting for worker slot 2 (_dftest2) (another run's worker phase holds"), "the resume waits for slot 2");
        Assert.Single(worker.Calls);
        using (WorkerLock.Acquire(_root, 1)) // free the whole time
        {
        }
        busy.Dispose();
        var outcome = await run;

        Assert.Equal((WorkState.Review, (string?)null), (outcome.State, outcome.Error));
        Assert.Equal("sess-77", worker.Calls[1].Resume); // the same session, which lives in slot 2's user's home
        Assert.Equal([2, 2], probe.Of("IWorker.RunAsync"));
        Assert.Equal([2], probe.Of("IRepoWorkspace.RemoveAsync")); // and its worktree is deleted as slot 2's user
        Assert.Equal(["2 _dftest2"], await SlotRows(h)); // not recorded again
        Assert.True(log.Logged("sc-77: worker slot 2 (_dftest2) is free; going on to implement"));
    }

    [Fact]
    public async Task A_worker_a_crashed_run_left_in_slot_2_is_stopped_through_slot_2_only_and_the_stop_waits_for_it()
    {
        // The item's worktree is on disk, shared with slot 2's user (as its first worker phase left it).
        var slots = new WorkerSlots(TestSlots.Pool(_root, 2).All) { ListAcl = (_, _) => Task.FromResult(SharedListing("_dftest2")) };
        var h = new Harness();
        h.Workspaces.Root = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(h.Workspaces.Root, "factory", "sc-77"));
        var probe = new SlotProbe(slots);
        var worker = new FakeWorker(ReportsModel(ImplementerModel)) { OrphanAlive = true };
        using (WorkerLock.Acquire(_root, 1))
        {
            Assert.Equal(WorkState.Review, (await Pipeline(h, slots, probe, worker, TextWriter.Null).RunAsync(77, CancellationToken.None)).State);
        }
        // A later run of the item died with its worker on record; the item is then stopped while nothing drives it.
        var item = (await h.Ledger.FindAsync(ItemNaming.Shortcut.Source, "sc-77", CancellationToken.None))!;
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, null, WorkerPid.ToString(), CancellationToken.None);
        await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);
        var log = new SyncWriter();
        var busy = WorkerLock.Acquire(_root, 2); // another item's session in slot 2

        var run = Task.Run(() => Pipeline(h, slots, probe, worker, log).RunAsync(77, CancellationToken.None));
        await Eventually(() => log.Logged("a stop: waiting for worker slot 2 (_dftest2)"), "the stop waits for slot 2");
        Assert.Empty(worker.OrphanStops); // slot 1 is free: a kill through its helper would hit nothing of this item
        busy.Dispose();
        var outcome = await run;

        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Equal([WorkerPid], worker.OrphanStops);
        Assert.Equal([2], probe.Of("IWorker.StopOrphanAsync"));
        // The stop's clean-up runs outside a worker phase: the worktree is still deleted in the slot it was shared with.
        Assert.Equal(2, probe.Of("IRepoWorkspace.RemoveAsync")[^1]);
    }

    [Fact]
    public async Task An_item_whose_slot_is_no_longer_configured_fails_saying_so_and_nothing_of_it_runs_as_another_user()
    {
        var h = new Harness();
        var three = TestSlots.Pool(_root, 3);
        using (WorkerLock.Acquire(_root, 1))
        using (WorkerLock.Acquire(_root, 2))
        using (var ctrlC = new CancellationTokenSource())
        {
            var interrupted = new FakeWorker(async call =>
            {
                await call.OnSession("sess-77", CancellationToken.None);
                ctrlC.Cancel();
                throw new OperationCanceledException(ctrlC.Token);
            });
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => Pipeline(h, three, new SlotProbe(three), interrupted, TextWriter.Null).RunAsync(77, ctrlC.Token));
        }
        Assert.Equal(["3 _dftest3"], await SlotRows(h));

        // Worker:Slots lowered to 2 while the item is paused in slot 3.
        var two = TestSlots.Pool(_root, 2);
        var probe = new SlotProbe(two);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));
        var outcome = await Pipeline(h, two, probe, worker, TextWriter.Null).RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("sc-77 was started in worker slot 3 (_dftest3), but Worker:Slots is 2", outcome.Error);
        Assert.Contains("Set Worker:Slots to at least 3 again", outcome.Error);
        Assert.Empty(worker.Calls);
        Assert.Empty(probe.Of("IWorker.StopOrphanAsync"));
    }

    [Fact]
    public async Task A_one_slot_factory_records_no_slot_and_an_item_worked_before_slots_is_slot_1s()
    {
        // Worker:Slots unset: nothing new on the ledger (E9).
        var one = TestSlots.Pool(_root, 1);
        var h = new Harness();
        Assert.Equal(WorkState.Review, (await Pipeline(h, one, new SlotProbe(one), new FakeWorker(ReportsModel(ImplementerModel)), TextWriter.Null)
            .RunAsync(77, CancellationToken.None)).State);
        Assert.Empty(await SlotRows(h));

        // Slots are then raised to 2 with the item's worker on record from before: it is _dftest's (slot 1's), busy or not.
        var item = (await h.Ledger.FindAsync(ItemNaming.Shortcut.Source, "sc-77", CancellationToken.None))!;
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, null, WorkerPid.ToString(), CancellationToken.None);
        await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);
        var two = TestSlots.Pool(_root, 2);
        var probe = new SlotProbe(two);
        var worker = new FakeWorker(ReportsModel(ImplementerModel)) { OrphanAlive = true };
        var log = new SyncWriter();
        var busy = WorkerLock.Acquire(_root, 1);

        var run = Task.Run(() => Pipeline(h, two, probe, worker, log).RunAsync(77, CancellationToken.None));
        await Eventually(() => log.Logged("waiting for worker slot 1 (_dftest)"), "the stop waits for slot 1");
        Assert.Empty(worker.OrphanStops);
        busy.Dispose();

        Assert.Equal(WorkState.Cancelled, (await run).State);
        Assert.Equal([1], probe.Of("IWorker.StopOrphanAsync"));
        Assert.Equal(1, RunPipeline.SlotOf(await h.Ledger.HistoryAsync(item, CancellationToken.None)));
    }

    [Fact]
    public async Task An_item_with_no_slot_yet_goes_to_the_slot_with_the_fewest_unfinished_items()
    {
        var slots = TestSlots.Pool(_root, 2);
        var h = new Harness();
        // Two other items already live in slot 1 (waiting on a review, say: slot 1 is free right now), none in slot 2.
        foreach (var other in new[] { "sc-1", "sc-2" })
        {
            var seeded = await h.Ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, other, other, Sandbox.FullName, null, CancellationToken.None);
            await h.Ledger.RecordAsync(seeded, WorkState.Implement, null, null, CancellationToken.None);
            await h.Ledger.CheckpointAsync(seeded, RunPipeline.Steps.Slot, null, "1 _dftest", CancellationToken.None);
        }
        Assert.Equal([2, 1], await h.Ledger.SlotsByLoadAsync(2, CancellationToken.None));

        var outcome = await Pipeline(h, slots, new SlotProbe(slots), new FakeWorker(ReportsModel(ImplementerModel)), TextWriter.Null).RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Equal(["2 _dftest2"], await SlotRows(h));
        // A finished item no longer counts against its slot.
        var done = (await h.Ledger.FindAsync(ItemNaming.Shortcut.Source, "sc-1", CancellationToken.None))!;
        await h.Ledger.RecordAsync(done, WorkState.Cancelled, null, null, CancellationToken.None);
        Assert.Equal([1, 2], await h.Ledger.SlotsByLoadAsync(2, CancellationToken.None));
    }

    [Fact]
    public void The_recorded_slot_is_the_last_slot_row_and_slot_1_for_an_item_worked_before_slots()
    {
        static LedgerEntry Row(string step, string? detail) =>
            new() { WorkItemId = 1, State = WorkState.Implement, Step = step, Detail = detail, Outcome = StepOutcome.Passed };

        Assert.Null(RunPipeline.SlotOf([]));
        Assert.Null(RunPipeline.SlotOf([Row(RunPipeline.Steps.Claimed, null)]));
        Assert.Equal(1, RunPipeline.SlotOf([Row(RunPipeline.Steps.WorkerStarted, "4242")]));
        Assert.Equal(2, RunPipeline.SlotOf([Row(RunPipeline.Steps.Slot, "2 _factory2"), Row(RunPipeline.Steps.WorkerStarted, "4242")]));
        Assert.Equal(3, RunPipeline.SlotOf([Row(RunPipeline.Steps.Slot, "2 _factory2"), Row(RunPipeline.Steps.Slot, "3 _factory3")]));
        Assert.Equal(StepOutcome.Passed, StepOutcomes.Of(WorkState.Implement, WorkState.Implement, RunPipeline.Steps.Slot, "2 _factory2"));
    }
}
