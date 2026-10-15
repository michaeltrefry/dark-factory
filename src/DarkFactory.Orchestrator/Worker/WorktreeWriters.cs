using System.Runtime.InteropServices;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// Who may write what is INSIDE a worktree (sc-25990, E4/E7), read owner-side before worker work is pushed from it with several
/// worker slots. The worktree's root is the owner's, so only the owner or root changes who may write it
/// (<see cref="WorkerSlots.WritersBeside"/>); but the files and directories a worker makes in it are that sandbox user's own, and
/// code it runs — a build script's <c>chmod -R a+w</c>, a tool with umask 0, a <c>chmod +a</c> — can open them to another slot's
/// user without touching the root. The work root lets every slot's user search and read down to them, so a session of another
/// slot (possibly tainted) could then change files this slot's push would publish under its own, untainted grant.
/// <para><see cref="Inside"/> walks the whole tree — ignored files too: a build input another slot could have planted is as
/// much a way in as a tracked file — without following a symbolic link (a link is not judged or entered: changing one takes
/// write on its directory, which is judged), and refuses the first entry whose mode lets its group or everyone write it, or whose
/// ACL (<see cref="NativeAcl"/>) allows anyone but the owner and the worktree's own slot's user anything beyond reading. The
/// group bit is refused outright, although setup gives each sandbox user a primary group of its own that no other slot's user
/// is in (<c>setup-worker-user.sh</c>) and a file made here takes its directory's group (the owner's): proving that a group
/// holds no other slot's user would take a membership lookup per group, and nothing the factory or a worker makes under the
/// usual umask (022) is group-writable. Fails closed: an entry that cannot be read, vanished while it was read, or whose ACL
/// cannot be read is a reason.</para>
/// <para>With an owner reader (<see cref="NativeOwners"/>, the push's) it also refuses an entry OWNED by anyone but the owner
/// and the slot's user (sc-26134): its owner may change it whatever its mode. Another slot's user can own one here when an
/// entry was writable to it for a while — Xcode makes what it creates 0777 whatever the umask, and the xcodebuild wrapper takes
/// that back only once its build has returned — or when this worktree's own worker hard-linked that user's file into it.</para>
/// <para>What it cannot see: a file of the owner's or the slot's user's that was writable while the session ran and was closed
/// again before the push — the check is of the tree as it is when no process of its slot's user is left (the launch helper's
/// exit kills them all).</para>
/// </summary>
internal static class WorktreeWriters
{
    /// <summary>The rights of an ACL entry (as <c>acl_to_text</c> names them) that let their holder change nothing: every other one, known or not, counts as writing.</summary>
    private static readonly HashSet<string> ReadRights =
        new(StringComparer.Ordinal) { "read", "execute", "readattr", "readextattr", "readsecurity" };

    /// <summary>
    /// Why something inside the worktree <paramref name="root"/> may be written by anyone but its owner and
    /// <paramref name="user"/>, or null. <paramref name="aclOf"/> reads one entry's ACL (<see cref="NativeAcl"/>; tests give
    /// their own). The root itself is not judged here (<see cref="WorkerSlots.WritersBeside"/> does).
    /// </summary>
    public static string? Inside(string root, string user, Func<string, string?> aclOf, CancellationToken ct, Func<string, string?>? ownerOf = null)
    {
        var pending = new Stack<string>([root]);
        var path = root;
        try
        {
            while (pending.TryPop(out var directory))
            {
                path = directory;
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    path = entry.FullName;
                    var attributes = entry.Attributes; // lstat: a link's own
                    if ((int)attributes == -1)
                    {
                        return $"{Relative(root, path)} in it could not be read (it is gone, or its name cannot be read back), so who may write it is not known";
                    }
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    if (ownerOf?.Invoke(path) is { } owner)
                    {
                        return $"{Relative(root, path)} in it {owner}";
                    }
                    var mode = entry.UnixFileMode;
                    if ((mode & UnixFileMode.OtherWrite) != 0)
                    {
                        return $"{Relative(root, path)} in it has a mode ({Octal(mode)}) that lets every user write it";
                    }
                    if ((mode & UnixFileMode.GroupWrite) != 0)
                    {
                        return $"{Relative(root, path)} in it has a mode ({Octal(mode)}) that lets its group write it";
                    }
                    if (aclOf(path) is { } acl && AclWriter(acl, user) is { } who)
                    {
                        return $"the ACL of {Relative(root, path)} in it {who}";
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(path);
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Relative(root, path)} in it could not be read ({ex.Message}), so who may write it is not known";
        }
    }

    private static string Relative(string root, string path) => path == root ? "the directory itself" : Path.GetRelativePath(root, path);

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8).PadLeft(4, '0');

    /// <summary>
    /// What is wrong with the ACL <paramref name="text"/> (<c>acl_to_text</c>: a <c>!#acl 1</c> line, then one line per entry,
    /// <c>user|group:&lt;uuid&gt;:&lt;name&gt;:&lt;id&gt;:allow|deny[,flags]:&lt;rights&gt;</c>) as the rest of a sentence about it,
    /// or null: every <c>allow</c> entry that names anyone but the owner and <paramref name="user"/> grants only rights to read.
    /// Fails closed: a line that is no entry, an entry with no name (the directory service did not answer) and a right this
    /// does not know all count against it.
    /// </summary>
    public static string? AclWriter(string text, string user)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("!#acl ", StringComparison.Ordinal))
            {
                continue;
            }
            var fields = line.Split(':');
            if (fields.Length != 6 || fields[0] is not ("user" or "group") || fields[4].Split(',')[0] is not ("allow" or "deny"))
            {
                return $"has a line that could not be read ('{line.Trim()}')";
            }
            if (fields[4].Split(',')[0] == "deny" || (fields[0] == "user" && (fields[2] == user || fields[2] == Environment.UserName)))
            {
                continue;
            }
            if (fields[5].Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(right => !ReadRights.Contains(right)) is { } right)
            {
                return $"lets {fields[0]}:{(fields[2].Length > 0 ? fields[2] : fields[1])} write it ({right})";
            }
        }
        return null;
    }

    /// <summary>
    /// The ACL of <paramref name="path"/> itself (never a link's target) as <c>acl_to_text</c> prints it, or null when it has
    /// none — read in this process (<c>acl_get_link_np</c>): one system call per entry, and no listing to parse a file name out
    /// of. Throws when it cannot be read. macOS only (the sandbox users and their ACLs are); elsewhere there is none to read.
    /// </summary>
    public static string? NativeAcl(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        var acl = acl_get_link_np(path, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            // ENOENT is how macOS says "no ACL" (the caller has just read the entry's mode, so it exists).
            var errno = Marshal.GetLastPInvokeError();
            return errno == Enoent ? null : throw new IOException($"its ACL could not be read (errno {errno})");
        }
        try
        {
            // Nearly every entry of a worktree holds the same ACL (what its root gives everything made in it), and printing one
            // asks the directory service for each principal's name: printed once per distinct ACL, by its external form.
            var key = ExternalForm(acl);
            if (key is not null && Printed.TryGetValue(key, out var printed))
            {
                return printed;
            }
            var text = acl_to_text(acl, IntPtr.Zero);
            if (text == IntPtr.Zero)
            {
                throw new IOException($"its ACL could not be printed (errno {Marshal.GetLastPInvokeError()})");
            }
            try
            {
                var result = Marshal.PtrToStringUTF8(text) ?? "";
                // Not one with a principal the directory service did not name: the next read may name it.
                if (key is not null && !result.Contains("::", StringComparison.Ordinal))
                {
                    if (Printed.Count >= MaxPrinted)
                    {
                        Printed.Clear();
                    }
                    Printed[key] = result;
                }
                return result;
            }
            finally
            {
                _ = acl_free(text);
            }
        }
        finally
        {
            _ = acl_free(acl);
        }
    }

    /// <summary>
    /// The owner reader of the push's walk (<see cref="Inside"/>): for one entry (never a link's target), why its owner is
    /// neither this process's user (the factory's owner, whose git checked the worktree out) nor <paramref name="user"/> (the
    /// slot's), or null. Fails closed: an entry whose owner cannot be read, and every entry when <paramref name="user"/> cannot
    /// be looked up, is a reason. macOS only (the sandbox users are); elsewhere it judges nothing.
    /// </summary>
    public static Func<string, string?> NativeOwners(string user)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return _ => null;
        }
        var self = geteuid();
        var slot = UidOf(user);
        return path =>
        {
            if (slot is null)
            {
                return $"could not be judged: the user {user} could not be looked up, so who may own what is in it is not known";
            }
            if (OwnerOf(path) is not { } owner)
            {
                return "could not be read (its owner is not known), so who may write it is not known";
            }
            return owner == self || owner == slot ? null : $"is owned by uid {owner}, neither the owner nor {user}, and its owner may change it";
        };
    }

    /// <summary>The uid that owns <paramref name="path"/> itself (<c>lstat</c>), or null when it cannot be read. macOS only.</summary>
    internal static uint? OwnerOf(string path)
    {
        // struct stat with 64-bit inodes (arm64's lstat, x86_64's lstat$INODE64): st_dev 4, st_mode 2, st_nlink 2, st_ino 8,
        // then st_uid at offset 16. The buffer is larger than the struct (144 bytes).
        var buffer = new byte[256];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? lstat_inode64(path, buffer) : lstat(path, buffer);
        return result == 0 ? BitConverter.ToUInt32(buffer, 16) : null;
    }

    /// <summary>The uid of the user named <paramref name="user"/> (<c>getpwnam</c>), or null when it is not known. macOS only.</summary>
    internal static uint? UidOf(string user)
    {
        lock (PasswordLock) // getpwnam answers in a buffer of its own
        {
            var entry = getpwnam(user);
            // struct passwd: char *pw_name, char *pw_passwd, then uid_t pw_uid at offset 16.
            return entry == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(entry, 16);
        }
    }

    private static readonly object PasswordLock = new();

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_inode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr getpwnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>ACLs already printed, by their external form (<see cref="NativeAcl"/>); bounded.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Printed = new(StringComparer.Ordinal);

    private const int MaxPrinted = 1024;

    /// <summary>The ACL's bytes as the system stores it (principals by UUID; no name is looked up), or null when it cannot be had: the ACL is then printed without the cache.</summary>
    private static string? ExternalForm(IntPtr acl)
    {
        var size = acl_size(acl);
        if (size <= 0 || size > 64 * 1024)
        {
            return null;
        }
        var buffer = new byte[size];
        var written = acl_copy_ext_native(buffer, acl, size);
        return written <= 0 || written > size ? null : Convert.ToBase64String(buffer, 0, (int)written);
    }

    private const int AclTypeExtended = 0x100;
    private const int Enoent = 2;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr acl_get_link_np([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr acl_to_text(IntPtr acl, IntPtr length);

    [DllImport("libc")]
    private static extern int acl_free(IntPtr obj);

    [DllImport("libc")]
    private static extern nint acl_size(IntPtr acl);

    [DllImport("libc")]
    private static extern nint acl_copy_ext_native(byte[] buffer, IntPtr acl, nint size);
}
