using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>
/// An artifact could not be fetched, verified, unpacked or cached; the message says why (never a pass: the gate's check is an
/// <c>error</c>). <see cref="WorkerMessage"/> is what a worker session's <c>factory-prepare</c> is told: the same, except where the
/// full message holds what the served bytes were (their SHA-256), which stays on the ledger and in the gate's result.
/// </summary>
public sealed class ArtifactFetchException(string message, Exception? inner = null, string? workerMessage = null) : Exception(message, inner)
{
    public string WorkerMessage { get; } = workerMessage ?? message;
}

/// <summary>One file or directory of a cached artifact's content: its path ('/'-separated, relative to the content), and for a file its size, SHA-256 and whether it is executable.</summary>
public sealed record CachedEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("dir")] bool Directory,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("exec")] bool Executable);

/// <summary>A cache entry's marker, written last (its presence means the entry is complete): what was downloaded, how it was laid out, and every entry.</summary>
public sealed record CacheManifest(
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("unpack")] string Unpack,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("entries")] IReadOnlyList<CachedEntry> Entries);

/// <summary>
/// The harness-owned artifact cache (sc-26232): <c>&lt;WorkRoot&gt;/artifact-cache</c>, content-addressed — one entry per
/// downloaded SHA-256 and layout (<c>&lt;sha256&gt;-&lt;unpack&gt;/content</c>, with its <see cref="CacheManifest"/> beside it). It is
/// the owner's: every sandbox user reads it, none writes it. The root is opened read-only to all
/// (<see cref="WorkerSandbox.OpenReadOnlyToAllAsync"/>: on macOS no ACL entry at all — the work root's inherited ones removed, so
/// nothing made in it inherits one — and mode <c>0755</c>), read back with <c>ls -led</c>; every directory in an entry is
/// <c>0755</c> and every file <c>0644</c> (<c>0755</c> when the archive marked it executable). Before an entry is used it is verified
/// whole (<see cref="TryGetAsync"/>): the root still read-only, nothing in the entry writable by anyone but the owner (the mode bits
/// and every entry's own ACL, <see cref="WorktreeWriters"/>), no link anywhere, and the content exactly its manifest — every path,
/// size and SHA-256 (and for an artifact kept as downloaded, the SHA-256 the recipe pins). An entry that definitely fails (a
/// mismatch) is deleted and fetched again; one that cannot be read to tell is an error and is left as it is; a partial one (no
/// manifest) is never used. A verified entry is not hashed again by the same cache while its manifest is the same file
/// (<see cref="Verifications"/>): the root and every entry are the owner's alone to write, and an entry is replaced only by a new
/// manifest. The root's set-up failing is the factory's failure (<see cref="FactoryUnavailableException"/>), never an item's. Each
/// cache's first use sweeps the leftovers of downloads and stores that died (older than <see cref="ArtifactFetcher.DefaultTimeout"/>).
/// Only downloads are cached: nothing a build makes ever is.
/// </summary>
public sealed class ArtifactCache(string root)
{
    /// <summary>The cache's directory under a work root.</summary>
    public static string RootOf(string workRoot) => System.IO.Path.Combine(workRoot, "artifact-cache");

    public const string ManifestName = "manifest.json";
    public const string ContentName = "content";

    /// <summary>The most entries an archive may hold.</summary>
    public const int MaxEntries = 200_000;

    public string Root => root;

    private readonly SemaphoreSlim _rootOnce = new(1, 1);
    private bool _rootReady;

    /// <summary>Entries this cache verified in full, by entry directory: the manifest's identity when it was (its modification time and length).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, string Path)> _verified = new(StringComparer.Ordinal);

    /// <summary>How many full verifications (every file hashed) this cache made: a test's count.</summary>
    internal int Verifications;

    /// <summary>The entry directory of <paramref name="artifact"/>.</summary>
    public string EntryOf(PrepareArtifact artifact) => System.IO.Path.Combine(root, $"{artifact.Sha256}-{UnpackName(artifact.Unpack)}");

    public static string UnpackName(ArtifactUnpack unpack) => unpack switch
    {
        ArtifactUnpack.None => "none",
        ArtifactUnpack.Zip => "zip",
        ArtifactUnpack.Tar => "tar",
        ArtifactUnpack.TarGz => "targz",
        _ => throw new ArgumentOutOfRangeException(nameof(unpack)),
    };

    /// <summary>
    /// Makes the root (once per instance), opens it read-only to all and sweeps the stale leftovers in it, then checks it reads back
    /// read-only. A root that cannot be made, opened or read back so is the factory's failure (<see cref="FactoryUnavailableException"/>:
    /// no item's), as any broken set-up is.
    /// </summary>
    public async Task EnsureRootAsync(CancellationToken ct)
    {
        await _rootOnce.WaitAsync(ct);
        try
        {
            if (!_rootReady)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(root);
                    await WorkerSandbox.OpenReadOnlyToAllAsync(root, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    throw new FactoryUnavailableException($"the artifact cache {root} cannot be made read-only to every user: {ex.Message}", ex);
                }
                SweepStale(DateTime.UtcNow - ArtifactFetcher.DefaultTimeout);
                _rootReady = true;
            }
        }
        finally
        {
            _rootOnce.Release();
        }
        string? problem;
        try
        {
            problem = await RootProblemAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new FactoryUnavailableException($"the artifact cache {root} cannot be read back: {ex.Message}", ex);
        }
        if (problem is not null)
        {
            throw new FactoryUnavailableException($"the artifact cache {root} {problem}", new InvalidOperationException(problem));
        }
    }

    /// <summary>
    /// Deletes the downloads (<c>.download-*</c>) and stores (<c>.partial-*</c>) left in the root by a process that died, last written
    /// before <paramref name="before"/> (a download is bounded by <see cref="ArtifactFetcher.DefaultTimeout"/>, so one that old is no
    /// live fetch's). Each is the owner's, and nothing else can write the root.
    /// </summary>
    internal void SweepStale(DateTime before)
    {
        IEnumerable<FileSystemInfo> leftovers;
        try
        {
            leftovers = new DirectoryInfo(root).EnumerateFileSystemInfos()
                .Where(i => i.Name.StartsWith(".download-", StringComparison.Ordinal) || i.Name.StartsWith(".partial-", StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var leftover in leftovers)
        {
            if (leftover.LastWriteTimeUtc < before)
            {
                Delete(leftover.FullName);
            }
        }
    }

    /// <summary>Why the root is not read-only to all (its listing, or on another OS its mode), or null.</summary>
    private async Task<string?> RootProblemAsync(CancellationToken ct)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return "is not a directory";
        }
        if (OperatingSystem.IsMacOS())
        {
            var listing = await WorkerSandbox.ListAsync(root, ct);
            return WorkerSandbox.IsReadOnlyToOthers(listing) ? null : $"is not read-only to every user (ls -led: {listing.Trim().ReplaceLineEndings("; ")})";
        }
        return info.UnixFileMode == WorkerSandbox.ReadOnlyToOthersMode ? null : $"has mode {Convert.ToString((int)info.UnixFileMode, 8)}, not 755";
    }

    /// <summary>
    /// The path <c>${artifact:…}</c> names for <paramref name="artifact"/> — its file (kept as downloaded) or its content directory
    /// (unpacked) — when the cache holds a verified, complete entry; null otherwise (an entry that definitely fails verification is
    /// deleted). An entry that cannot be read to tell throws <see cref="ArtifactFetchException"/> and is left as it is.
    /// </summary>
    public async Task<string?> TryGetAsync(PrepareArtifact artifact, CancellationToken ct)
    {
        await EnsureRootAsync(ct);
        var entry = EntryOf(artifact);
        if (!System.IO.Directory.Exists(entry) && !File.Exists(entry))
        {
            return null;
        }
        return Checked(artifact, entry, ct);
    }

    /// <summary><see cref="Verify"/>'s answer acted on: the path; null after deleting a mismatch; an unreadable entry thrown, not deleted.</summary>
    private string? Checked(PrepareArtifact artifact, string entry, CancellationToken ct)
    {
        var verified = Verify(artifact, entry, ct);
        if (verified.Path is { } path)
        {
            return path;
        }
        if (verified.Unreadable is { } why)
        {
            throw new ArtifactFetchException($"the cached {artifact.Name} ({entry}) could not be read to verify it, so it was neither used nor deleted: {why}");
        }
        Delete(entry);
        return null;
    }

    /// <summary>
    /// Caches the downloaded file <paramref name="download"/> (already checked against the recipe's SHA-256) for
    /// <paramref name="artifact"/>: laid out in a fresh partial directory, modes set, the manifest written last, then moved into place
    /// in one rename. Returns the verified path, as <see cref="TryGetAsync"/>.
    /// </summary>
    public async Task<string> StoreAsync(PrepareArtifact artifact, string download, string? fileName, CancellationToken ct)
    {
        await EnsureRootAsync(ct);
        var partial = System.IO.Path.Combine(root, $".partial-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(partial);
        try
        {
            SetMode(partial, WorkerSandbox.ReadOnlyToOthersMode);
            var content = System.IO.Path.Combine(partial, ContentName);
            System.IO.Directory.CreateDirectory(content);
            SetMode(content, WorkerSandbox.ReadOnlyToOthersMode);
            string? file = null;
            switch (artifact.Unpack)
            {
                case ArtifactUnpack.None:
                    file = SafeFileName(fileName);
                    var target = System.IO.Path.Combine(content, file);
                    File.Copy(download, target);
                    SetMode(target, FileMode(false));
                    break;
                case ArtifactUnpack.Zip:
                    UnpackZip(download, content, artifact.MaxUnpackedBytes, ct);
                    break;
                case ArtifactUnpack.Tar:
                case ArtifactUnpack.TarGz:
                    UnpackTar(download, content, artifact.Unpack == ArtifactUnpack.TarGz, artifact.MaxUnpackedBytes, ct);
                    break;
            }
            var manifest = new CacheManifest(artifact.Sha256, UnpackName(artifact.Unpack), file, Walk(content, ct));
            var manifestPath = System.IO.Path.Combine(partial, ManifestName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), ct);
            SetMode(manifestPath, FileMode(false));
            var entry = EntryOf(artifact);
            try
            {
                System.IO.Directory.Move(partial, entry);
            }
            catch (IOException) when (System.IO.Directory.Exists(entry))
            {
                // Another fetch of the same artifact won the race: its entry is used if it verifies, else replaced.
                if (Checked(artifact, entry, ct) is { } theirs)
                {
                    return theirs;
                }
                System.IO.Directory.Move(partial, entry);
            }
            _verified.TryRemove(entry, out _);
            return Verify(artifact, entry, ct) switch
            {
                { Path: { } path } => path,
                { Unreadable: { } why } => throw new ArtifactFetchException($"the cached {artifact.Name} could not be read back after it was written: {why}"),
                _ => throw new ArtifactFetchException($"the cached {artifact.Name} did not verify after it was written"),
            };
        }
        finally
        {
            if (System.IO.Directory.Exists(partial))
            {
                Delete(partial);
            }
        }
    }

    /// <summary>What <see cref="Verify"/> found: the entry's <see cref="Path"/>; or, both null, a definite mismatch; or why it could not be read to tell.</summary>
    internal sealed record Verified(string? Path, string? Unreadable)
    {
        public static readonly Verified Mismatch = new(null, null);
    }

    /// <summary>A link found in an entry: a definite mismatch (nothing cached holds one), not a read failure.</summary>
    private sealed class LinkInEntryException(string message) : IOException(message);

    /// <summary>
    /// A complete, unchanged entry's path, or a definite mismatch — a link in it, anything in it writable by anyone but the owner,
    /// no manifest or one that is not this SHA-256's and layout's, content other than the manifest (a path, size or hash) — or why it
    /// could not be read to tell (an I/O failure: the entry is then left as it is). An entry this cache verified in full is not
    /// hashed again while its manifest is the same file (modification time and length; .NET exposes no inode): only the owner writes
    /// the root and its entries, and an entry is replaced only with a new manifest.
    /// </summary>
    internal Verified Verify(PrepareArtifact artifact, string entry, CancellationToken ct)
    {
        try
        {
            var info = new DirectoryInfo(entry);
            if (!info.Exists || info.LinkTarget is not null || (info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return Verified.Mismatch;
            }
            var manifestPath = System.IO.Path.Combine(entry, ManifestName);
            var manifestInfo = new FileInfo(manifestPath);
            if (manifestInfo is not { Exists: true, LinkTarget: null })
            {
                return Verified.Mismatch;
            }
            if (_verified.TryGetValue(entry, out var known) && known.Ticks == manifestInfo.LastWriteTimeUtc.Ticks && known.Length == manifestInfo.Length)
            {
                return new Verified(known.Path, null);
            }
            Interlocked.Increment(ref Verifications);
            if (WorktreeWriters.NativeAcl(entry) is { } ownAcl && WorktreeWriters.AclWriter(ownAcl, Environment.UserName) is not null)
            {
                return Verified.Mismatch;
            }
            if (WorktreeWriters.Inside(entry, Environment.UserName, WorktreeWriters.NativeAcl, ct) is not null)
            {
                return Verified.Mismatch;
            }
            if (JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(manifestPath)) is not { } manifest
                || manifest.Sha256 != artifact.Sha256 || manifest.Unpack != UnpackName(artifact.Unpack) || manifest.Entries is null)
            {
                return Verified.Mismatch;
            }
            var content = System.IO.Path.Combine(entry, ContentName);
            var actual = Walk(content, ct);
            if (!actual.SequenceEqual(manifest.Entries))
            {
                return Verified.Mismatch;
            }
            string? path;
            if (artifact.Unpack == ArtifactUnpack.None)
            {
                path = manifest.File is { } file && actual is [{ Directory: false } only] && only.Path == file && only.Sha256 == artifact.Sha256
                    ? System.IO.Path.Combine(content, file)
                    : null;
            }
            else
            {
                path = content;
            }
            if (path is null)
            {
                return Verified.Mismatch;
            }
            _verified[entry] = (manifestInfo.LastWriteTimeUtc.Ticks, manifestInfo.Length, path);
            return new Verified(path, null);
        }
        catch (Exception ex) when (ex is LinkInEntryException or JsonException or NotSupportedException)
        {
            return Verified.Mismatch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Verified(null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Every entry under <paramref name="content"/>, in path order (files hashed); a link anywhere throws (it is never cached).</summary>
    internal static List<CachedEntry> Walk(string content, CancellationToken ct)
    {
        var entries = new List<CachedEntry>();
        var pending = new Stack<string>([content]);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                var relative = System.IO.Path.GetRelativePath(content, item.FullName).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.LinkTarget is not null)
                {
                    throw new LinkInEntryException($"{relative} is a link");
                }
                if (item is DirectoryInfo)
                {
                    entries.Add(new CachedEntry(relative, true, 0, null, false));
                    pending.Push(item.FullName);
                }
                else if (item is FileInfo file)
                {
                    using var stream = new FileStream(file.FullName, System.IO.FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    entries.Add(new CachedEntry(relative, false, file.Length, Convert.ToHexStringLower(SHA256.HashData(stream)),
                        (file.UnixFileMode & UnixFileMode.UserExecute) != 0));
                }
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return entries;
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static UnixFileMode FileMode(bool executable) => executable
        ? WorkerSandbox.ReadOnlyToOthersMode
        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>A file name for an artifact kept as downloaded: <paramref name="name"/> if it is a plain, safe name, else <c>artifact</c>.</summary>
    internal static string SafeFileName(string? name) =>
        name is { Length: > 0 and <= 255 } n && n != "." && n != ".." && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+')
            ? n
            : "artifact";

    /// <summary>
    /// An archive entry's name as a relative path inside the content ('/'-separated), or why it is refused: an absolute path, a
    /// <c>..</c> or empty segment, a backslash or a NUL, or longer than 1,024 characters.
    /// </summary>
    internal static string EntryPath(string name, out bool directory)
    {
        directory = name.EndsWith('/');
        var trimmed = name.TrimEnd('/');
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        if (trimmed.Length is 0 or > 1024 || trimmed.StartsWith('/') || trimmed.Contains('\\') || trimmed.Contains('\0')
            || trimmed.Split('/').Any(s => s is "" or "." or ".."))
        {
            throw new ArtifactFetchException($"the archive holds an entry whose name is not a plain relative path ('{name}'), so it is not unpacked");
        }
        return trimmed;
    }

    private static void UnpackZip(string archive, string content, long maxBytes, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new ArtifactFetchException($"the archive holds more than {MaxEntries} entries");
        }
        var budget = new Budget(maxBytes);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = EntryPath(entry.FullName, out var isDirectory);
            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var type = unixMode & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000))
            {
                throw new ArtifactFetchException($"the archive's entry {path} is not a regular file or directory (a link or a device), so it is not unpacked");
            }
            if (isDirectory || type == 0x4000)
            {
                MakeDirectory(content, path);
                continue;
            }
            using var source = entry.Open();
            Write(content, path, source, (unixMode & 0x49) != 0, budget, ct);
        }
    }

    private static void UnpackTar(string archive, string content, bool gzip, long maxBytes, CancellationToken ct)
    {
        using var file = File.OpenRead(archive);
        using Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TarReader(stream);
        var budget = new Budget(maxBytes);
        var count = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxEntries)
            {
                throw new ArtifactFetchException($"the archive holds more than {MaxEntries} entries");
            }
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
            {
                continue;
            }
            var path = EntryPath(entry.Name, out var isDirectory);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    MakeDirectory(content, path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile when !isDirectory:
                    using (var data = entry.DataStream ?? Stream.Null)
                    {
                        Write(content, path, data, (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0, budget, ct);
                    }
                    break;
                default:
                    throw new ArtifactFetchException($"the archive's entry {path} is a {entry.EntryType}, not a regular file or directory, so it is not unpacked");
            }
        }
    }

    /// <summary>How much an archive may still unpack to.</summary>
    private sealed class Budget(long limit)
    {
        public long Left = limit;

        public long Max { get; } = limit;
    }

    private static string Inside(string content, string path)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(content, path));
        return full.StartsWith(content + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new ArtifactFetchException($"the archive's entry {path} would leave the artifact's directory");
    }

    private static void MakeDirectory(string content, string path)
    {
        var full = Inside(content, path);
        var parts = path.Split('/');
        var current = content;
        foreach (var part in parts)
        {
            current = System.IO.Path.Combine(current, part);
            if (File.Exists(current))
            {
                throw new ArtifactFetchException($"the archive's entry {path} is under a file of the same name");
            }
            if (!System.IO.Directory.Exists(current))
            {
                System.IO.Directory.CreateDirectory(current);
                SetMode(current, WorkerSandbox.ReadOnlyToOthersMode);
            }
        }
        _ = full;
    }

    private static void Write(string content, string path, Stream source, bool executable, Budget budget, CancellationToken ct)
    {
        var full = Inside(content, path);
        if (path.Contains('/'))
        {
            MakeDirectory(content, path[..path.LastIndexOf('/')]);
        }
        if (File.Exists(full) || System.IO.Directory.Exists(full))
        {
            throw new ArtifactFetchException($"the archive holds {path} twice");
        }
        using (var target = new FileStream(full, System.IO.FileMode.CreateNew, FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                budget.Left -= read;
                if (budget.Left < 0)
                {
                    throw new ArtifactFetchException($"the archive unpacks to more than {budget.Max} bytes (max_unpacked_bytes)");
                }
                target.Write(buffer, 0, read);
            }
        }
        SetMode(full, FileMode(executable));
    }

    /// <summary>Deletes an entry or partial directory owner-side: every directory in it is the owner's and no one else can write it, so nothing in it was made by another user; links are removed, never followed.</summary>
    private void Delete(string path)
    {
        _verified.TryRemove(path, out _);
        try
        {
            if (new FileInfo(path) is { LinkTarget: not null } link)
            {
                link.Delete();
            }
            else if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: never used (it does not verify), replaced by the next store.
        }
    }
}
