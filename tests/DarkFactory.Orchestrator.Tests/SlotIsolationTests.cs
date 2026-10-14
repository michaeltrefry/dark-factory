using System.Diagnostics;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25990 (E7): worker slots are isolated for writing. A worktree is one slot's user's to write — the push of worker work is
/// refused when its write grants name anyone but that user and the owner — and a slot's MCP session directory is its own
/// user's alone to read. The decisions are read from <c>ls -led</c> listings: fakes here, and on macOS real ones of the test's
/// own directories with the role accounts <c>daemon</c> and <c>nobody</c> standing in for two slots' users (an ACL entry must
/// name an existing user; nothing runs as them). No launch helper, no sudo.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class SlotIsolationTests
{
    private static readonly RepoRef Repo = new("acme", "widgets");
    private readonly string _root = Directory.CreateTempSubdirectory("df-isolation-").FullName;

    private const string Write = "list,add_file,search,delete,add_subdirectory,delete_child,readattr,writeattr,readextattr,writeextattr,readsecurity,file_inherit,directory_inherit";
    private const string Read = "list,search,readattr,readextattr,readsecurity,file_inherit,directory_inherit";

    /// <summary><c>ls -led</c> of a worktree with <paramref name="entries"/> as its ACL (numbered here), mode <paramref name="mode"/>.</summary>
    private static string Listing(string mode, params string[] entries) =>
        $"{mode}{(entries.Length > 0 ? "+" : " ")} 3 owner  staff  96 Oct 13 12:00 /work/worktrees/o/r/factory-sc-7\n"
        + string.Concat(entries.Select((e, n) => $" {n}: {e}\n"));

    /// <summary>A worktree as <see cref="WorkerSandbox.ShareAsync"/> leaves it for <paramref name="user"/> under a work root every slot's user reads.</summary>
    private static string SharedWith(string user, params string[] more) => Listing("drwxr-xr-x",
        [$"user:{Environment.UserName} allow {Write}", $"user:{user} allow {Write}", .. more,
            $"user:_dftest inherited allow {Read}", $"user:_dftest2 inherited allow {Read}"]);

    // ---- Who may write a worktree ----

    [Fact]
    public void Slot_1s_user_has_no_write_entry_on_a_worktree_created_for_slot_2()
    {
        var pool = TestSlots.Pool(_root, 2);
        var listing = SharedWith("_dftest2");

        // Slot 2's user and the owner write it; slot 1's user only reads it (the work root's inherited entry: the accepted residual).
        Assert.Null(WorkerSlots.WritersBeside(listing, "_dftest2"));
        Assert.Equal(2, pool.SharedWith("/wt", listing)!.Number);
        Assert.False(WorkerSandbox.Grants(listing, "_dftest", "add_file"));
        Assert.True(WorkerSandbox.Grants(listing, "_dftest", "search"));
        // So it is not slot 1's to push from: for slot 1 the writer is another slot's user.
        Assert.Equal("its ACL lets user:_dftest2 write it", WorkerSlots.WritersBeside(listing, "_dftest"));
    }

    [Theory]
    [InlineData("user:_dftest allow list,add_file,search", "its ACL lets user:_dftest write it")] // another slot's user
    [InlineData("user:_dftest inherited allow list,search,delete_child", "its ACL lets user:_dftest write it")]
    [InlineData("user:_dftest3 allow add_subdirectory", "its ACL lets user:_dftest3 write it")] // a slot above Worker:Slots
    [InlineData("user:someone allow write", "its ACL lets user:someone write it")]
    [InlineData("group:staff allow list,add_file", "its ACL lets group:staff write it")]
    [InlineData("ABCDEF01-2345-6789-ABCD-EF0123456789 allow delete", "its ACL lets ABCDEF01-2345-6789-ABCD-EF0123456789 write it")] // an entry ls could not name
    [InlineData("user:_dftest deny add_file", null)] // a deny grants nothing
    [InlineData("user:_dftest allow list,search,readattr,readextattr,readsecurity", null)] // reading is not writing
    public void A_worktrees_write_grants_may_name_only_its_own_slots_user_and_the_owner(string entry, string? why) =>
        Assert.Equal(why, WorkerSlots.WritersBeside(SharedWith("_dftest2", entry), "_dftest2"));

    [Theory]
    [InlineData("drwxrwxrwx", "its mode (drwxrwxrwx) lets every user write it")]
    [InlineData("drwxr-xrwx", "its mode (drwxr-xrwx) lets every user write it")]
    [InlineData("drwxr-xr-x", null)]
    [InlineData("drwx------", null)]
    public void A_worktree_everyone_may_write_by_its_mode_is_refused_too(string mode, string? why) =>
        Assert.Equal(why, WorkerSlots.WritersBeside(Listing(mode), "_dftest2"));

    [Theory]
    [InlineData("")]
    [InlineData("ls: /wt: No such file or directory\n")]
    [InlineData("-rw-r--r--  1 owner  staff  9 Oct 13 12:00 /wt\n")] // not a directory
    public void A_listing_that_shows_no_directory_fails_closed(string listing) =>
        Assert.Contains("could not be read", WorkerSlots.WritersBeside(listing, "_dftest2"));

    [Fact]
    public async Task The_pool_asks_about_the_slot_the_push_runs_in_and_refuses_another_slots_worktree()
    {
        var listing = SharedWith("_dftest2");
        var reads = 0;
        var pool = new WorkerSlots(TestSlots.Pool(_root, 2).All)
        {
            ListAcl = (_, _) => { reads++; return Task.FromResult(listing); },
            AclRetry = TimeSpan.Zero,
            CloseLocks = (_, _) => Task.CompletedTask,
        };
        Task<bool> In(int slot) => pool.HoldAsync(slot, async _ => { await pool.EnsureOnlyWorkerWritesAsync("/wt", CancellationToken.None); return true; },
            CancellationToken.None);

        Assert.True(await In(2));
        Assert.Equal(1, reads); // a clean listing is read once

        // The same worktree is not slot 1's: its session did not write it under a grant of its own.
        var refused = await Assert.ThrowsAsync<WorktreeWritersException>(() => In(1));
        Assert.Contains("Refusing to push the work in /wt: its ACL lets user:_dftest2 write it. Only _dftest (", refused.Message);
        Assert.Contains("Nothing was committed or pushed", refused.Message);

        // Slot 1's user given a write entry beside slot 2's: refused for slot 2 as well, after the listing was read again
        // (ls names a user by its UUID while the directory service is slow, so one bad read does not decide).
        (listing, reads) = (SharedWith("_dftest2", $"user:_dftest allow {Write}"), 0);
        Assert.Contains("user:_dftest write it", (await Assert.ThrowsAsync<WorktreeWritersException>(() => In(2))).Message);
        Assert.Equal(3, reads);

        // No slot held, several configured: nothing is judged as "some" user's.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.EnsureOnlyWorkerWritesAsync("/wt", CancellationToken.None));
    }

    [Fact]
    public async Task A_writer_ls_named_by_uuid_once_is_read_again_and_a_listing_that_cannot_be_read_refuses_the_push()
    {
        var listings = new Queue<string>([
            SharedWith("_dftest2").Replace("user:_dftest2 allow", "ABCDEF01-2345-6789-ABCD-EF0123456789 allow", StringComparison.Ordinal),
            SharedWith("_dftest2"),
        ]);
        await WorkerSlots.EnsureOnlyWriterAsync("/wt", "_dftest2", (_, _) => Task.FromResult(listings.Dequeue()), TimeSpan.Zero, CancellationToken.None);
        Assert.Empty(listings);

        var unreadable = await Assert.ThrowsAsync<WorktreeWritersException>(() => WorkerSlots.EnsureOnlyWriterAsync("/wt", "_dftest2",
            (_, _) => throw new InvalidOperationException("/bin/ls failed (1): Permission denied"), TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("who may write it could not be read (/bin/ls failed (1): Permission denied)", unreadable.Message);
    }

    // ---- Who may read a slot's MCP session directory ----

    private const string OwnDirectory = "drwx------+ 2 owner  staff  64 Oct 13 12:00 /work/mcp-sessions-2\n"
        + " 0: user:_dftest2 allow search\n"
        + " 1: user:_dftest2 allow read,readattr,readextattr,readsecurity,file_inherit,only_inherit\n";
    private const string OwnFile = "-rw-------+ 1 owner  staff  312 Oct 13 12:00 /work/mcp-sessions-2/sc-7.0123.json\n"
        + " 0: user:_dftest2 inherited allow read,readattr,readextattr,readsecurity\n";

    [Fact]
    public void A_slots_session_directory_and_file_are_open_to_its_own_user_and_nobody_else()
    {
        Assert.Null(WorkerSandbox.OpenToOthers(OwnDirectory, "_dftest2"));
        Assert.Null(WorkerSandbox.OpenToOthers(OwnFile, "_dftest2"));
        Assert.True(WorkerSandbox.Grants(OwnDirectory, "_dftest2", "search"));
        Assert.False(WorkerSandbox.Grants(OwnDirectory, "_dftest2", "list")); // it opens a file it is given; it cannot list the directory
        Assert.True(WorkerSandbox.Grants(OwnFile, "_dftest2", "read"));
        // Another slot's user holds no entry at all, and to it the directory is open to someone else.
        Assert.False(WorkerSandbox.Grants(OwnFile, "_dftest", "read"));
        Assert.False(WorkerSandbox.Grants(OwnDirectory, "_dftest", "search"));
        Assert.Equal("its ACL also allows user:_dftest2", WorkerSandbox.OpenToOthers(OwnFile, "_dftest"));
        // The owner's own entry is no one else's.
        Assert.Null(WorkerSandbox.OpenToOthers(OwnFile + $" 1: user:{Environment.UserName} allow read,write\n", "_dftest2"));
    }

    [Theory]
    [InlineData(" 1: user:_dftest inherited allow read,readattr\n", "its ACL also allows user:_dftest")] // the work root's entry for slot 1's user
    [InlineData(" 1: user:_dftest inherited allow readattr\n", "its ACL also allows user:_dftest")] // any right at all
    [InlineData(" 1: group:staff allow read\n", "its ACL also allows group:staff")]
    [InlineData(" 1: ABCDEF01-2345-6789-ABCD-EF0123456789 allow read\n", "its ACL also allows ABCDEF01-2345-6789-ABCD-EF0123456789")]
    [InlineData(" 1: something ls printed that is no entry\n", "an ACL line could not be read ('1: something ls printed that is no entry')")]
    [InlineData(" 1: user:_dftest deny read\n", null)] // a deny opens nothing
    public void A_session_file_any_other_principal_may_read_is_open_to_others(string entry, string? why) =>
        Assert.Equal(why, WorkerSandbox.OpenToOthers(OwnFile + entry, "_dftest2"));

    [Theory]
    [InlineData("-rw-r-----+ 1 owner  staff  312 Oct 13 12:00 f\n", "its mode (-rw-r-----+) gives its group or everyone access")]
    [InlineData("-rw----r-- 1 owner  staff  312 Oct 13 12:00 f\n", "its mode (-rw----r--) gives its group or everyone access")]
    [InlineData("drwx--x---+ 2 owner  staff  64 Oct 13 12:00 d\n", "its mode (drwx--x---+) gives its group or everyone access")]
    [InlineData("", "its `ls -led` listing could not be read")]
    [InlineData("ls: f: No such file or directory\n", "its `ls -led` listing could not be read")]
    [InlineData("-rw------- 1 owner  staff  312 Oct 13 12:00 f\n", null)]
    [InlineData("-rw-------@ 1 owner  staff  312 Oct 13 12:00 f\n", null)]
    public void A_session_file_its_mode_opens_or_whose_listing_cannot_be_read_is_open_to_others(string listing, string? why) =>
        Assert.Equal(why, WorkerSandbox.OpenToOthers(listing, "_dftest2"));

    [Fact]
    public void Only_slots_above_one_restrict_their_session_directory_and_each_to_its_own_user()
    {
        // E9: one slot is the sandbox before slots — the directory is read through the work root's inherited entry, unchanged.
        var one = TestSlots.Pool(_root, 1);
        Assert.Null(SandboxTriageRunner.SessionDirectoryRestriction(one, one.All[0]));
        // Several: every slot's directory, slot 1's included, is made its own user's alone (the work root's entries name them all).
        var two = TestSlots.Pool(_root, 2);
        Assert.All(two.All, slot =>
        {
            var restrict = SandboxTriageRunner.SessionDirectoryRestriction(two, slot);
            Assert.Same(slot.Sandbox, Assert.IsType<WorkerSandbox>(restrict!.Target));
        });
    }

    // ---- The real thing, on macOS, with two role accounts for two slots' users ----

    private static WorkerSlots RoleAccountPool(string root) => new([
        new WorkerSlot(1, new WorkerSandbox("daemon", TestSlots.NoHelper) { Lock = WorkerLock.For(root, 1) }),
        new WorkerSlot(2, new WorkerSandbox("nobody", TestSlots.NoHelper + "-2") { Lock = WorkerLock.For(root, 2) })]);

    /// <summary>Reads <paramref name="path"/>'s ACL until <paramref name="settled"/> (ls prints a UUID for a user while the directory service is slow on a loaded host).</summary>
    private static async Task<string> AclAsync(string path, Func<string, bool> settled)
    {
        var acl = "";
        for (var read = 0; read < 50 && !settled(acl); read++)
        {
            acl = await WorkerSandbox.ListAclAsync(path, CancellationToken.None);
        }
        return acl;
    }

    private static void Chmod(params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/chmod", args) { RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task A_slots_session_directory_is_its_own_users_alone_to_read_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // The directory as a work root set up for two slots leaves it: an inherited read entry for each slot's user.
        var directory = Directory.CreateDirectory(Path.Combine(_root, "mcp-sessions-2")).FullName;
        foreach (var user in new[] { "daemon", "nobody" })
        {
            Chmod("+a", $"user:{user} allow list,search,read,readattr,readextattr,readsecurity,file_inherit,directory_inherit", directory);
        }
        var slot2 = RoleAccountPool(_root).All[1].Sandbox;

        await slot2.RestrictToWorkerAsync(directory, CancellationToken.None);

        var file = Path.Combine(directory, "sc-7.json");
        File.WriteAllText(file, "{}");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var (ofDirectory, ofFile) = (await AclAsync(directory, a => a.Contains("user:nobody", StringComparison.Ordinal)),
            await AclAsync(file, a => a.Contains("user:nobody", StringComparison.Ordinal)));
        // Slot 2's user searches the directory and reads the file made in it; slot 1's user has no entry on either.
        Assert.True(WorkerSandbox.Grants(ofDirectory, "nobody", "search"), ofDirectory);
        Assert.True(WorkerSandbox.Grants(ofFile, "nobody", "read"), ofFile);
        Assert.DoesNotContain("daemon", ofDirectory + ofFile);
        Assert.Null(WorkerSandbox.OpenToOthers(ofDirectory, "nobody"));
        Assert.Null(WorkerSandbox.OpenToOthers(ofFile, "nobody"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        // Done again for the next session (every session of the slot does it): the same two entries, not four.
        await slot2.RestrictToWorkerAsync(directory, CancellationToken.None);
        Assert.Equal(2, (await AclAsync(directory, a => a.Contains("user:nobody", StringComparison.Ordinal))).Split('\n').Count(l => l.Contains(" allow ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_push_from_a_real_worktree_is_refused_once_another_slots_user_may_write_it_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        var remote = Path.Combine(_root, "remote.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", remote);
        Git(_root, "init", "-b", "main", seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "init");
        Git(seed, "push", remote, "main");
        var pool = RoleAccountPool(_root);
        var workspace = new GitWorkspace(Path.Combine(_root, "work"), _ => remote, (_, _) => Task.FromResult<string?>(null), sandbox: pool);
        Task<T> In<T>(int slot, Func<Task<T>> work) => pool.HoldAsync(slot, _ => work(), CancellationToken.None);

        // Slot 2's worktree: shared with slot 2's user by the owner's chmod, before the checkout.
        var story = await In(2, () => workspace.PrepareAsync(Repo, "factory/sc-9", CancellationToken.None));
        var acl = await AclAsync(story.Path, a => a.Contains("user:nobody allow", StringComparison.Ordinal));
        Assert.Contains("user:nobody allow", acl);
        Assert.DoesNotContain("user:daemon", acl); // slot 1's user has no entry, so no write entry
        Assert.Null(WorkerSlots.WritersBeside(acl, "nobody"));

        File.WriteAllText(Path.Combine(story.Path, "work.txt"), "slot 2's\n");
        // A phase of slot 1 is not this worktree's: refused, nothing staged.
        var foreign = await Assert.ThrowsAsync<WorktreeWritersException>(
            () => In(1, () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None)));
        Assert.Contains("user:nobody", foreign.Message);
        // Slot 1's user given a write entry (what only the owner or root could do): slot 2's own push is refused too.
        Chmod("+a", "user:daemon allow add_file,delete_child", story.Path);
        var crossed = await Assert.ThrowsAsync<WorktreeWritersException>(
            () => In(2, () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None)));
        Assert.Contains("its ACL lets user:daemon write it", crossed.Message);
        Assert.Equal("", Git(_root, "--git-dir", remote, "branch", "--list", "factory/sc-9").Trim());
        // Without it, slot 2 pushes its work.
        Chmod("-a", "user:daemon allow add_file,delete_child", story.Path);
        Assert.True(await In(2, () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None)));
        Assert.NotEqual("", Git(_root, "--git-dir", remote, "branch", "--list", "factory/sc-9").Trim());
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
