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
    [InlineData("group:Domain Users allow add_file", "an ACL line could not be read ('2: group:Domain Users allow add_file')")] // a principal with a space: not skipped
    [InlineData("something ls printed that is no entry", "an ACL line could not be read ('2: something ls printed that is no entry')")]
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
    [InlineData("drwxrwxr-x", true, "its mode (drwxrwxr-x) lets its group write it")]
    [InlineData("drwxrwsr-x", true, "its mode (drwxrwsr-x) lets its group write it")]
    [InlineData("drwxrwxrwx", true, "its mode (drwxrwxrwx) lets every user write it")]
    [InlineData("drwxr-xr-x", true, null)]
    [InlineData("drwxrwxr-x", false, null)] // one slot: no other slot's user to be in the group, so the bit is not judged
    public void With_several_slots_a_worktree_its_group_may_write_by_its_mode_is_refused_and_with_one_the_group_bit_is_not_judged(
        string mode, bool several, string? why) =>
        Assert.Equal(why, WorkerSlots.WritersBeside(Listing(mode), "_dftest2", groupToo: several));

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
        var (reads, inside) = (0, new List<string>());
        var owners = new List<string>();
        // A real (test-made) directory for what is inside it; its root's listing is the fake one.
        var wt = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(wt, "work.txt"), "x");
        var pool = new WorkerSlots(TestSlots.Pool(_root, 2).All)
        {
            ListAcl = (_, _) => { reads++; return Task.FromResult(listing); },
            EntryAcl = path => { inside.Add(path); return null; },
            EntryOwners = user => { owners.Add(user); return _ => null; },
            AclRetry = TimeSpan.Zero,
            CloseLocks = (_, _) => Task.CompletedTask,
        };
        Task<bool> In(int slot) => pool.HoldAsync(slot, async _ => { await pool.EnsureOnlyWorkerWritesAsync(wt, CancellationToken.None); return true; },
            CancellationToken.None);

        Assert.True(await In(2));
        Assert.Equal(1, reads); // a clean listing is read once
        Assert.Equal([Path.Combine(wt, "work.txt")], inside); // and, with several slots, what is inside it is read too
        Assert.Equal(["_dftest2"], owners); // and who owns it, for the held slot's user (sc-26134)

        // The same worktree is not slot 1's: its session did not write it under a grant of its own.
        var refused = await Assert.ThrowsAsync<WorktreeWritersException>(() => In(1));
        Assert.Contains($"Refusing to push the work in {wt}: its ACL lets user:_dftest2 write it. Only _dftest (", refused.Message);
        Assert.Contains("Nothing was committed or pushed", refused.Message);
        // What the refusal costs and what to do about it is said: the escalation comment is this message.
        Assert.Contains("the session's work is discarded with the worktree", refused.Message);
        Assert.Contains("a re-run does it again in a fresh worktree", refused.Message);

        // Slot 1's user given a write entry beside slot 2's: refused for slot 2 as well, after the listing was read again
        // (ls names a user by its UUID while the directory service is slow, so one bad read does not decide).
        (listing, reads) = (SharedWith("_dftest2", $"user:_dftest allow {Write}"), 0);
        Assert.Contains("user:_dftest write it", (await Assert.ThrowsAsync<WorktreeWritersException>(() => In(2))).Message);
        // A refusal escalates the item and throws its finished work away, so it is not decided in a hurry: 10 reads, 200 ms
        // apart in production (about 2 s).
        Assert.Equal(10, reads);
        Assert.Equal(10, WorkerSlots.PushAclReads);
        Assert.Equal(TimeSpan.FromMilliseconds(200), TestSlots.Pool(_root, 2).AclRetry);

        // No slot held, several configured: nothing is judged as "some" user's.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.EnsureOnlyWorkerWritesAsync(wt, CancellationToken.None));
    }

    [Fact]
    public async Task With_one_slot_nothing_inside_the_worktree_is_read()
    {
        // E9 and cost: one sandbox user has no other slot's user to open a file to. The root's listing is read, as before.
        var (reads, inside) = (0, 0);
        var wt = Directory.CreateDirectory(Path.Combine(_root, "wt1")).FullName;
        File.WriteAllText(Path.Combine(wt, "work.txt"), "x");
        File.SetUnixFileMode(Path.Combine(wt, "work.txt"), (UnixFileMode)0b110_110_110); // 0666: refused with several slots
        var pool = new WorkerSlots(TestSlots.Pool(_root, 1).All)
        {
            ListAcl = (_, _) => { reads++; return Task.FromResult(SharedWith("_dftest")); },
            EntryAcl = _ => { inside++; return null; },
            EntryOwners = _ => { inside++; return _ => null; },
        };

        await pool.EnsureOnlyWorkerWritesAsync(wt, CancellationToken.None);

        Assert.Equal((1, 0), (reads, inside));
    }

    // ---- What is inside a worktree (several slots) ----

    private const string OwnerEntry = "user:061A2C51-FCC7-422E-A1E4-DB5F649417DC:OWNER:501:allow,inherited:read,write,execute,delete,append,delete_child,readattr,writeattr,readextattr,writeextattr,readsecurity";
    private const string Slot2Entry = "user:D2D26795-72EA-4E64-9D1C-B817ADC81AF0:_dftest2:451:allow,inherited:read,write,execute,delete,append,delete_child,readattr,writeattr,readextattr,writeextattr,readsecurity";
    private const string Slot1Reads = "user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow,inherited:read,execute,readattr,readextattr,readsecurity";

    /// <summary>An entry's ACL as <c>acl_to_text</c> prints it: what a file made in slot 2's worktree inherits, then <paramref name="more"/>.</summary>
    private static string Acl(params string[] more) =>
        "!#acl 1\n" + string.Concat(new[] { OwnerEntry.Replace("OWNER", Environment.UserName, StringComparison.Ordinal), Slot2Entry, Slot1Reads }.Concat(more).Select(e => e + "\n"));

    [Theory]
    [InlineData(null, null)] // what the worktree's root gives every file made in it: the owner and its slot's user write, the other slot's reads
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:read,write", "lets user:_dftest write it (write)")] // another slot's user
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow,inherited:read,append", "lets user:_dftest write it (append)")]
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:delete", "lets user:_dftest write it (delete)")]
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:read,writesecurity", "lets user:_dftest write it (writesecurity)")] // could grant itself the rest
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:chown", "lets user:_dftest write it (chown)")]
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:read,some_new_right", "lets user:_dftest write it (some_new_right)")] // a right this does not know
    [InlineData("group:ABCDEFAB-CDEF-ABCD-EFAB-CDEF0000000C:everyone:12:allow:read,write", "lets group:everyone write it (write)")]
    [InlineData("group:ABCDEFAB-CDEF-ABCD-EFAB-CDEF00000014:staff:20:allow,file_inherit:write", "lets group:staff write it (write)")]
    [InlineData("user:ABCDEF01-2345-6789-ABCD-EF0123456789:::allow:write", "lets user:ABCDEF01-2345-6789-ABCD-EF0123456789 write it (write)")] // a user the directory service did not name
    [InlineData("group:ABCDEFAB-CDEF-ABCD-EFAB-CDEF00000014:OWNER:20:allow:write", "lets group:OWNER write it (write)")] // a group named like the owner is not the owner
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:deny:write,delete", null)] // a deny grants nothing
    [InlineData("group:ABCDEFAB-CDEF-ABCD-EFAB-CDEF0000000C:everyone:12:allow:read,execute,readattr,readextattr,readsecurity", null)] // reading is not writing
    [InlineData("something that is no entry", "has a line that could not be read ('something that is no entry')")]
    [InlineData("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:permit:write", "has a line that could not be read ('user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:permit:write')")]
    public void An_entry_inside_a_worktree_may_be_written_only_by_its_slots_user_and_the_owner_by_its_acl(string? entry, string? why) =>
        Assert.Equal(why, WorktreeWriters.AclWriter(
            Acl(entry is null ? [] : [entry.Replace("OWNER", Environment.UserName, StringComparison.Ordinal)]), "_dftest2")
            ?.Replace(Environment.UserName, "OWNER", StringComparison.Ordinal));

    [Fact]
    public void The_same_acl_is_another_slots_to_refuse()
    {
        // Slot 2's file is not slot 1's to push: for slot 1 the writer is another slot's user.
        Assert.Null(WorktreeWriters.AclWriter(Acl(), "_dftest2"));
        Assert.Equal("lets user:_dftest2 write it (write)", WorktreeWriters.AclWriter(Acl(), "_dftest"));
    }

    [Fact]
    public void A_tree_is_walked_whole_without_following_links_and_the_first_open_entry_is_named()
    {
        var wt = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(wt, "a", "b", "c")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "x"), "");
        File.SetUnixFileMode(outside, (UnixFileMode)0b111_111_111);
        foreach (var file in new[] { Path.Combine(wt, ".git"), Path.Combine(wt, ".hidden"), Path.Combine(wt, "a", "one"), Path.Combine(deep, "two") })
        {
            File.WriteAllText(file, "");
            File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);
        }
        foreach (var directory in new[] { Path.Combine(wt, "a"), Path.Combine(wt, "a", "b"), deep })
        {
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);
        }
        Directory.CreateSymbolicLink(Path.Combine(wt, "a", "out"), outside);
        Directory.CreateSymbolicLink(Path.Combine(wt, "a", "dangling"), Path.Combine(_root, "no-such"));
        var asked = new List<string>();
        string? Walk(Func<string, string?>? aclOf = null) =>
            WorktreeWriters.Inside(wt, "_dftest2", path => { asked.Add(Path.GetRelativePath(wt, path)); return aclOf?.Invoke(path); }, CancellationToken.None);

        Assert.Null(Walk());
        // Every entry that is not a link, hidden ones and the worktree's `.git` file included; no link, nothing behind one.
        Assert.Equal([".git", ".hidden", "a", "a/b", "a/b/c", "a/b/c/two", "a/one"], asked.Order(StringComparer.Ordinal));

        // The deepest file opened to everyone, to its group; a directory; an ACL on one file; an ACL that cannot be read.
        File.SetUnixFileMode(Path.Combine(deep, "two"), (UnixFileMode)0b110_100_110);
        Assert.Equal("a/b/c/two in it has a mode (0646) that lets every user write it", Walk());
        File.SetUnixFileMode(Path.Combine(deep, "two"), (UnixFileMode)0b110_110_100);
        Assert.Equal("a/b/c/two in it has a mode (0664) that lets its group write it", Walk());
        File.SetUnixFileMode(Path.Combine(deep, "two"), (UnixFileMode)0b110_100_100);
        File.SetUnixFileMode(Path.Combine(wt, "a", "b"), (UnixFileMode)0b111_101_111);
        Assert.Equal("a/b in it has a mode (0757) that lets every user write it", Walk());
        File.SetUnixFileMode(Path.Combine(wt, "a", "b"), (UnixFileMode)0b111_101_101);
        Assert.Equal("the ACL of a/one in it lets user:_dftest write it (write)",
            Walk(path => path.EndsWith("one", StringComparison.Ordinal) ? Acl("user:D2D26795-72EA-4E64-9D1C-B817ADC81AF1:_dftest:450:allow:write") : Acl()));
        Assert.Equal("a/one in it could not be read (its ACL could not be read (errno 13)), so who may write it is not known",
            Walk(path => path.EndsWith("one", StringComparison.Ordinal) ? throw new IOException("its ACL could not be read (errno 13)") : null));
        // A directory the owner cannot list (a worker's own, closed): fail closed.
        File.SetUnixFileMode(Path.Combine(wt, "a", "b"), UnixFileMode.None);
        Assert.StartsWith("a/b in it could not be read (", Walk());
        File.SetUnixFileMode(Path.Combine(wt, "a", "b"), (UnixFileMode)0b111_101_101);
        // A directory that vanishes while the tree is read (something still writes it): fail closed.
        Assert.StartsWith("a/b/c in it could not be read (",
            Walk(path => { if (path.EndsWith("c", StringComparison.Ordinal)) { Directory.Delete(deep, recursive: true); } return null; }));
        Assert.Null(Walk());
        // A worktree that is gone, and a cancelled walk.
        Assert.StartsWith("the directory itself in it could not be read (", WorktreeWriters.Inside(Path.Combine(_root, "gone"), "_dftest2", _ => null, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => WorktreeWriters.Inside(wt, "_dftest2", _ => null, new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// sc-26134: with an owner reader the walk also refuses an entry owned by anyone but the owner and the slot's user — whose
    /// owner may change it whatever its mode (another slot's user can own one after Xcode made a directory 0777 for the length
    /// of a build, or through a hard link) — before its mode is judged; a link is still neither judged nor entered.
    /// </summary>
    [Fact]
    public void The_walk_refuses_an_entry_owned_by_anyone_but_the_owner_and_the_slots_user()
    {
        var wt = Directory.CreateDirectory(Path.Combine(_root, "owned")).FullName;
        var deep = Directory.CreateDirectory(Path.Combine(wt, "a", "b")).FullName;
        File.WriteAllText(Path.Combine(deep, "theirs"), "");
        File.WriteAllText(Path.Combine(wt, "mine"), "");
        Directory.CreateSymbolicLink(Path.Combine(wt, "link"), deep);
        var asked = new List<string>();
        string? Walk(Func<string, string?> ownerOf) => WorktreeWriters.Inside(wt, "_dftest2", _ => null, CancellationToken.None,
            path => { asked.Add(Path.GetRelativePath(wt, path)); return ownerOf(path); });

        Assert.Null(Walk(_ => null));
        Assert.Equal(["a", "a/b", "a/b/theirs", "mine"], asked.Order(StringComparer.Ordinal)); // every entry but the link
        Assert.Equal("a/b/theirs in it is owned by uid 451, neither the owner nor _dftest2, and its owner may change it",
            Walk(path => path.EndsWith("theirs", StringComparison.Ordinal) ? "is owned by uid 451, neither the owner nor _dftest2, and its owner may change it" : null));
        // The owner is judged before the mode: a foreign 0644 file is refused for its owner, not passed for its mode.
        File.SetUnixFileMode(Path.Combine(wt, "mine"), (UnixFileMode)0b110_110_110);
        Assert.Equal("mine in it is someone else's", Walk(path => path.EndsWith("mine", StringComparison.Ordinal) ? "is someone else's" : null));
        Assert.Equal("mine in it has a mode (0666) that lets every user write it", Walk(_ => null));
    }

    /// <summary>The push's owner reader on macOS: the real owner of each entry (lstat), the slot's user looked up by name, fail closed.</summary>
    [Fact]
    public void The_native_owner_reader_allows_the_owner_and_the_slots_user_and_refuses_root_and_an_unknown_user()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The sandbox users, and so the owner check, are macOS-only.");
        }
        var wt = Directory.CreateDirectory(Path.Combine(_root, "native-owned")).FullName;
        var mine = Path.Combine(wt, "mine");
        File.WriteAllText(mine, "");
        var me = WorktreeWriters.UidOf(Environment.UserName);
        Assert.NotNull(me);
        Assert.Equal(me, WorktreeWriters.OwnerOf(mine));
        Assert.Equal(0u, WorktreeWriters.OwnerOf("/"));
        Assert.Equal(0u, WorktreeWriters.UidOf("root"));
        Assert.Null(WorktreeWriters.UidOf("_dftest_no_such_user"));
        Assert.Null(WorktreeWriters.OwnerOf(Path.Combine(wt, "gone")));

        // The owner's own files pass, for any slot's user that exists ("daemon" stands in for one).
        Assert.Null(WorktreeWriters.Inside(wt, "daemon", _ => null, CancellationToken.None, WorktreeWriters.NativeOwners("daemon")));
        // A slot's user that cannot be looked up: nothing can be judged.
        Assert.Equal("mine in it could not be judged: the user _dftest_no_such_user could not be looked up, so who may own what is in it is not known",
            WorktreeWriters.Inside(wt, "_dftest_no_such_user", _ => null, CancellationToken.None, WorktreeWriters.NativeOwners("_dftest_no_such_user")));
        // A hard link to another user's file (root's, 0644): its mode lets no one else write, but its owner may.
        File.Delete(mine);
        File.CreateSymbolicLink(Path.Combine(wt, "a-link-to-it"), "/private/etc/hosts"); // a link: not judged
        Assert.Null(WorktreeWriters.Inside(wt, "daemon", _ => null, CancellationToken.None, WorktreeWriters.NativeOwners("daemon")));
        using (var ln = System.Diagnostics.Process.Start("/bin/ln", ["/private/etc/hosts", Path.Combine(wt, "hosts")]))
        {
            ln.WaitForExit();
        }
        if (!File.Exists(Path.Combine(wt, "hosts")))
        {
            Assert.Skip("This volume does not let /private/etc/hosts be hard-linked into a temporary directory.");
        }
        Assert.Equal("hosts in it is owned by uid 0, neither the owner nor daemon, and its owner may change it",
            WorktreeWriters.Inside(wt, "daemon", _ => null, CancellationToken.None, WorktreeWriters.NativeOwners("daemon")));
        // With the slot's user being that owner (root, here), it passes.
        Assert.Null(WorktreeWriters.Inside(wt, "root", _ => null, CancellationToken.None, WorktreeWriters.NativeOwners("root")));
        File.Delete(Path.Combine(wt, "hosts")); // the link only: root's file is untouched
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
    public void With_several_slots_every_slots_session_directory_slot_1s_included_is_restricted_to_its_own_user()
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

    [Fact]
    public void A_seat_is_given_the_directories_and_the_user_of_the_slot_it_runs_in_and_of_no_other()
    {
        // AC3's production wiring: the triage's and the planner's runner take the seat's worktree root, session directory, user
        // and restriction from SlotSeat.Of, for the slot the seat holds.
        var two = TestSlots.Pool(_root, 2);
        var (first, second) = (two.All[0], two.All[1]);
        foreach (var (name, own) in new[] { (SandboxTriageRunner.TriageWorktrees, "triage-worktrees"), (SandboxPlanRunner.PlanWorktrees, "plan-worktrees") })
        {
            var seat2 = SlotSeat.Of("/work", name, two, second);
            Assert.Equal(($"{own}-2", Path.Combine("/work", "mcp-sessions-2"), "_dftest2"), (seat2.Worktrees, seat2.Sessions, seat2.User));
            Assert.Same(second.Sandbox, seat2.Restrict!.Target); // made slot 2's user's alone, not slot 1's
            var seat1 = SlotSeat.Of("/work", name, two, first);
            Assert.Equal((own, Path.Combine("/work", "mcp-sessions"), "_dftest"), (seat1.Worktrees, seat1.Sessions, seat1.User));
            Assert.Same(first.Sandbox, seat1.Restrict!.Target);
            // The only other roots a seat ever looks into (a triage, for its own issue's leftover): the other slots', never its own.
            Assert.Equal([own], SlotSeat.OthersWorktrees(name, two, second));
            Assert.Equal([$"{own}-2"], SlotSeat.OthersWorktrees(name, two, first));
            // One slot, and unsandboxed: where it was before slots, restricted to nobody.
            var one = TestSlots.Pool(_root, 1);
            Assert.Equal(new SlotSeat(own, Path.Combine("/work", "mcp-sessions"), "_dftest", null), SlotSeat.Of("/work", name, one, one.All[0]));
            Assert.Equal(new SlotSeat(own, Path.Combine("/work", "mcp-sessions"), null, null), SlotSeat.Of("/work", name, null, null));
        }
    }

    [Fact]
    public void The_two_production_runners_take_every_seat_directory_from_the_seat_of_their_slot()
    {
        // The runners' own code (they need a sandbox, GitHub and the ledger to run): nothing in FactoryRunner.cs names a slot's
        // seat or session directory, or its restriction, except SlotSeat itself and the definitions.
        var source = File.ReadAllText(Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator", "FactoryRunner.cs"));
        var code = string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        int Count(string text) => System.Text.RegularExpressions.Regex.Matches(code, System.Text.RegularExpressions.Regex.Escape(text)).Count;
        Assert.Equal(1, Count("McpProxySessions.DirectoryOf(")); // SlotSeat.Of
        Assert.Equal(4, Count("WorkerSlots.DirectoryOf(")); // SlotSeat.Of, SlotSeat.OthersWorktrees, and the two names' definitions
        Assert.Equal(2, Count("SessionDirectoryRestriction(")); // its definition, and SlotSeat.Of
        Assert.Equal(0, Count("TriageWorktreesOf(") - 1 + Count("PlanWorktreesOf(") - 1); // each only defined (tests and docs name them)
        Assert.Equal(2, Count("SlotSeat.Of(options.WorkRoot, ")); // the triage's runner and the planner's
        Assert.Contains("var seat = SlotSeat.Of(options.WorkRoot, TriageWorktrees, slots, slot);", code);
        Assert.Contains("var seat = SlotSeat.Of(options.WorkRoot, PlanWorktrees, slots, slot);", code);
        Assert.Equal(2, Count("worktreesDirectory: ")); // each runner's workspace: the seat's root (and, the triage's, the other slots' through SeatsIn)
        Assert.Contains("worktreesDirectory: seat.Worktrees, shareWithWorker: false);", code);
        Assert.Contains("worktreesDirectory: worktrees, shareWithWorker: false);", code);
        Assert.Contains("var workspaces = SeatsIn(seat.Worktrees);", code);
        Assert.Equal(2, Count("new Mcp.McpProxySessions(proxy, seat.Sessions, seat.User, owner: ItemNaming.ParseAny(item.ExternalId),"));
        Assert.Equal(2, Count("restrict: seat.Restrict))"));
        Assert.Equal(1, Count("StartMcpProxyAsync(upstreams, seat.Sessions, live, ct);"));
        Assert.Equal(1, Count("Mcp.McpProxySessions.SweepAsync(seat.Sessions, live, ct);"));
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
        Chmod("-a", "user:daemon allow add_file,delete_child", story.Path);
        // What is INSIDE it is its maker's to open (the root's ACL is untouched): a nested file slot 1's user is let write by an
        // ACL entry, by everyone's mode bit, by the group's — each refused, by the ACL the kernel holds for the file itself.
        var deep = Directory.CreateDirectory(Path.Combine(story.Path, "src", "deep")).FullName;
        var nested = Path.Combine(deep, "nested.txt");
        File.WriteAllText(nested, "slot 2's\n");
        Assert.Contains(":nobody:", WorktreeWriters.NativeAcl(nested)); // it inherited its slot's user's entry from the root
        async Task<string> RefusedAsync() => (await Assert.ThrowsAsync<WorktreeWritersException>(
            () => In(2, () => workspace.CommitAndPushAsync(Repo, story, "sc-9: work", TestGrants.Untainted, CancellationToken.None)))).Message;
        Chmod("+a", "user:daemon allow write", nested);
        Assert.Contains("the ACL of src/deep/nested.txt in it lets user:daemon write it (write)", await RefusedAsync());
        Chmod("-a", "user:daemon allow write", nested);
        Chmod("+a", "everyone allow append", nested);
        Assert.Contains("the ACL of src/deep/nested.txt in it lets group:everyone write it (append)", await RefusedAsync());
        Chmod("-a", "everyone allow append", nested);
        Chmod("+a", "user:daemon allow add_file", deep);
        Assert.Contains("the ACL of src/deep in it lets user:daemon write it (write)", await RefusedAsync());
        Chmod("-a", "user:daemon allow add_file", deep);
        Chmod("o+w", nested);
        Assert.Contains("src/deep/nested.txt in it has a mode (0646) that lets every user write it", await RefusedAsync());
        Chmod("o-w,g+w", nested);
        Assert.Contains("src/deep/nested.txt in it has a mode (0664) that lets its group write it", await RefusedAsync());
        Chmod("g-w", nested);
        // The other slot's user reading it (the work root's inherited entry, the accepted residual) refuses nothing.
        Chmod("+a", "user:daemon allow read,readattr,readextattr,readsecurity", nested);
        Assert.Equal("", Git(_root, "--git-dir", remote, "branch", "--list", "factory/sc-9").Trim());
        // Without any of it, slot 2 pushes its work.
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
