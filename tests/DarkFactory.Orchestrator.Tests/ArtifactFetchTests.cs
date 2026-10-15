using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DarkFactory.Orchestrator.Prepare;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>An HTTP server faked as a handler: answers by URL, and records every request (nothing reaches a network).</summary>
internal sealed class FakeArtifactServer : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public void Serve(string url, byte[] body, bool declareLength = true) => _routes[url] = () =>
    {
        HttpContent content = declareLength ? new ByteArrayContent(body) : new StreamContent(new Unseekable(body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    };

    /// <summary>A body whose length is not known up front (no Content-Length).</summary>
    private sealed class Unseekable(byte[] body) : Stream
    {
        private readonly MemoryStream _inner = new(body);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Redirect(string url, string location, HttpStatusCode status = HttpStatusCode.Found) => _routes[url] = () =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    };

    public void Status(string url, HttpStatusCode status) => _routes[url] = () => new HttpResponseMessage(status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_routes.TryGetValue(request.RequestUri!.ToString(), out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// sc-26232: the harness's artifact downloads — https only, public addresses only (redirects and DNS answers included), bounded,
/// hash-pinned — and the content-addressed, read-only cache they fill (verified whole before every use, never a partial entry).
/// </summary>
public sealed class ArtifactFetchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("df-artifacts-").FullName;
    private readonly FakeArtifactServer _server = new();
    private readonly FakeResolver _resolver = new() { ["files.example"] = ["93.184.216.34"], ["other.example"] = ["2606:4700::1111"], ["internal.example"] = ["10.0.0.9"] };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ArtifactFetcher Fetcher(TimeSpan? timeout = null) =>
        new(new HttpClient(_server), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root)), timeout);

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static PrepareArtifact Artifact(string url, byte[] body, ArtifactUnpack unpack = ArtifactUnpack.None, long? maxBytes = null, long? maxUnpacked = null) =>
        new("tool", new Uri(url), Sha256(body), maxBytes ?? 1 << 20, unpack, maxUnpacked ?? 4 << 20);

    internal static byte[] Zip(params (string Name, string? Content, int Mode)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, mode) in entries)
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = mode << 16;
                if (content is not null)
                {
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(content);
                }
            }
        }
        return memory.ToArray();
    }

    internal static byte[] TarGz(params TarEntry[] entries)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
            }
        }
        return memory.ToArray();
    }

    internal static PaxTarEntry File(string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = mode };

    [Fact]
    public async Task An_artifact_is_downloaded_once_verified_cached_read_only_and_then_served_from_the_cache()
    {
        var body = Encoding.UTF8.GetBytes("the sdk");
        _server.Serve("https://files.example/sdk.bin", body);
        var artifact = Artifact("https://files.example/sdk.bin", body);
        var downloads = new List<ArtifactDownloaded>();

        var path = await Fetcher().EnsureAsync(artifact, (d, _) => { downloads.Add(d); return Task.CompletedTask; }, CancellationToken.None);
        var again = await Fetcher().EnsureAsync(artifact, (d, _) => { downloads.Add(d); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(path, again);
        Assert.Equal(Path.Combine(ArtifactCache.RootOf(_root), $"{artifact.Sha256}-none", "content", "sdk.bin"), path);
        Assert.Equal("the sdk", System.IO.File.ReadAllText(path));
        Assert.Single(_server.Requests); // the second came from the cache
        var download = Assert.Single(downloads);
        Assert.Equal($"fetched tool https://files.example/sdk.bin sha256={artifact.Sha256} bytes=7", download.ToDetail());
        Assert.Equal(Ledger.StepOutcome.Passed, Ledger.StepOutcomes.Of(Ledger.WorkState.Review, Ledger.WorkState.Review, RunPipeline.Steps.ArtifactFetch, download.ToDetail()));
        Assert.Equal(Ledger.StepOutcome.Failed, Ledger.StepOutcomes.Of(Ledger.WorkState.Implement, Ledger.WorkState.Implement, RunPipeline.Steps.ArtifactFetch, "failed: no"));
        if (!OperatingSystem.IsWindows())
        {
            // Everyone reads it; only the owner writes it — the root, the entry, the content and the file.
            var readOnly = WorkerSandbox.ReadOnlyToOthersMode;
            Assert.Equal(readOnly, System.IO.File.GetUnixFileMode(ArtifactCache.RootOf(_root)));
            Assert.Equal(readOnly, System.IO.File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, System.IO.File.GetUnixFileMode(path));
        }
        // No download or partial directory is left beside the entry.
        Assert.Equal([$"{artifact.Sha256}-none"], Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_body_whose_sha256_is_not_the_recipes_is_refused_and_nothing_is_cached()
    {
        _server.Serve("https://files.example/sdk.bin", "tampered"u8.ToArray());
        var artifact = Artifact("https://files.example/sdk.bin", "the sdk"u8.ToArray());

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() => Fetcher().EnsureAsync(artifact, null, CancellationToken.None));

        Assert.Contains($"not the recipe's {artifact.Sha256}", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_body_over_max_bytes_is_refused_by_its_declared_length_or_while_it_streams(bool declareLength)
    {
        var body = new byte[2048];
        _server.Serve("https://files.example/big.bin", body, declareLength);
        var artifact = Artifact("https://files.example/big.bin", body, maxBytes: 1024);

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() => Fetcher().EnsureAsync(artifact, null, CancellationToken.None));

        Assert.Contains("max_bytes 1024", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)));
    }

    [Fact]
    public async Task A_host_whose_dns_answers_a_private_address_is_refused_before_any_request()
    {
        var body = "x"u8.ToArray();
        _server.Serve("https://internal.example/x", body);

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact("https://internal.example/x", body), null, CancellationToken.None));

        Assert.Contains("internal.example resolves to 10.0.0.9", refused.Message);
        Assert.Empty(_server.Requests);
    }

    [Theory]
    [InlineData("http://files.example/x", "must be an https URL")]
    [InlineData("https://internal.example/x", "resolves to 10.0.0.9")]
    [InlineData("https://127.0.0.1/x", "loopback")]
    [InlineData("https://[fd00::1]/x", "unique-local")]
    [InlineData("https://localhost/x", "local host")]
    public async Task A_redirect_is_checked_like_the_first_url_and_never_followed_to_a_refused_one(string location, string why)
    {
        var body = "x"u8.ToArray();
        _server.Redirect("https://files.example/x", location);
        _server.Serve(location, body);

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact("https://files.example/x", body), null, CancellationToken.None));

        Assert.Contains(why, refused.Message);
        Assert.Equal([new Uri("https://files.example/x")], _server.Requests);
    }

    [Fact]
    public async Task Redirects_are_followed_to_a_checked_url_a_bounded_number_of_times()
    {
        var body = "the sdk"u8.ToArray();
        _server.Redirect("https://files.example/x", "/y", HttpStatusCode.MovedPermanently);
        _server.Redirect("https://files.example/y", "https://other.example/z", HttpStatusCode.TemporaryRedirect);
        _server.Serve("https://other.example/z", body);
        _server.Redirect("https://files.example/loop", "https://files.example/loop");

        var path = await Fetcher().EnsureAsync(Artifact("https://files.example/x", body), null, CancellationToken.None);
        var loop = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact("https://files.example/loop", "other"u8.ToArray()), null, CancellationToken.None));

        Assert.Equal("the sdk", System.IO.File.ReadAllText(path));
        Assert.Contains($"more than {ArtifactFetcher.MaxRedirects} redirects", loop.Message);
        Assert.Equal(3 + ArtifactFetcher.MaxRedirects + 1, _server.Requests.Count);
    }

    [Fact]
    public async Task A_status_other_than_200_and_a_timeout_are_refusals()
    {
        _server.Status("https://files.example/gone", HttpStatusCode.Forbidden);

        var forbidden = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact("https://files.example/gone", "x"u8.ToArray()), null, CancellationToken.None));
        Assert.Contains("answered 403", forbidden.Message);

        var slow = new ArtifactFetcher(new HttpClient(new SlowHandler()), _resolver, new ArtifactCache(ArtifactCache.RootOf(_root)), TimeSpan.FromMilliseconds(200));
        var timedOut = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            slow.EnsureAsync(Artifact("https://files.example/slow", "x"u8.ToArray()), null, CancellationToken.None));
        Assert.Contains("timed out", timedOut.Message);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_zip_is_unpacked_with_its_executable_bits_and_a_tar_gz_likewise()
    {
        var zip = Zip(("sdk/", null, 0x41ED), ("sdk/include/dng.h", "#pragma once", 0x81A4), ("sdk/bin/tool", "#!/bin/sh", 0x81ED));
        _server.Serve("https://files.example/sdk.zip", zip);
        var tar = TarGz(new PaxTarEntry(TarEntryType.Directory, "lib/"), File("lib/a.txt", "a"), File("./lib/run", "r", UnixFileMode.UserRead | UnixFileMode.UserExecute));
        _server.Serve("https://files.example/lib.tar.gz", tar);

        var sdk = await Fetcher().EnsureAsync(Artifact("https://files.example/sdk.zip", zip, ArtifactUnpack.Zip), null, CancellationToken.None);
        var lib = await Fetcher().EnsureAsync(Artifact("https://files.example/lib.tar.gz", tar, ArtifactUnpack.TarGz), null, CancellationToken.None);

        Assert.Equal("#pragma once", System.IO.File.ReadAllText(Path.Combine(sdk, "sdk/include/dng.h")));
        Assert.Equal("a", System.IO.File.ReadAllText(Path.Combine(lib, "lib/a.txt")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(WorkerSandbox.ReadOnlyToOthersMode, System.IO.File.GetUnixFileMode(Path.Combine(sdk, "sdk/bin/tool")));
            Assert.Equal(WorkerSandbox.ReadOnlyToOthersMode, System.IO.File.GetUnixFileMode(Path.Combine(lib, "lib/run")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                System.IO.File.GetUnixFileMode(Path.Combine(sdk, "sdk/include/dng.h")));
        }
    }

    public static TheoryData<string, byte[], ArtifactUnpack, string> BadArchives => new()
    {
        { "slip.zip", Zip(("../escape.txt", "x", 0x81A4)), ArtifactUnpack.Zip, "not a plain relative path" },
        { "abs.zip", Zip(("/etc/x", "x", 0x81A4)), ArtifactUnpack.Zip, "not a plain relative path" },
        { "link.zip", Zip(("link", "/etc/passwd", 0xA1FF)), ArtifactUnpack.Zip, "a link or a device" },
        { "dup.zip", Zip(("a", "1", 0x81A4), ("a", "2", 0x81A4)), ArtifactUnpack.Zip, "holds a twice" },
        { "link.tar.gz", TarGz(new PaxTarEntry(TarEntryType.SymbolicLink, "l") { LinkName = "/etc/passwd" }), ArtifactUnpack.TarGz, "SymbolicLink" },
        { "hard.tar.gz", TarGz(new PaxTarEntry(TarEntryType.HardLink, "h") { LinkName = "x" }), ArtifactUnpack.TarGz, "HardLink" },
        { "slip.tar.gz", TarGz(File("a/../../x", "x")), ArtifactUnpack.TarGz, "not a plain relative path" },
        { "bomb.zip", Zip(("big", new string('z', 10_000), 0x81A4)), ArtifactUnpack.Zip, "max_unpacked_bytes" },
    };

    [Theory]
    [MemberData(nameof(BadArchives))]
    public async Task An_archive_with_a_link_an_escape_a_duplicate_or_too_much_content_is_not_unpacked_or_cached(string name, byte[] archive, ArtifactUnpack unpack, string why)
    {
        _server.Serve($"https://files.example/{name}", archive);

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact($"https://files.example/{name}", archive, unpack, maxUnpacked: 1000), null, CancellationToken.None));

        Assert.Contains(why, refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task A_changed_partial_or_linked_entry_is_never_used_and_is_fetched_again()
    {
        var zip = Zip(("sdk/a.h", "a", 0x81A4), ("sdk/b.h", "b", 0x81A4));
        _server.Serve("https://files.example/sdk.zip", zip);
        var artifact = Artifact("https://files.example/sdk.zip", zip, ArtifactUnpack.Zip);
        var path = await Fetcher().EnsureAsync(artifact, null, CancellationToken.None);
        var entry = Path.GetDirectoryName(path)!;

        // Changed content (the owner could; no sandbox user can): verified against the manifest, found changed, fetched again.
        System.IO.File.SetUnixFileMode(Path.Combine(path, "sdk/a.h"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        System.IO.File.WriteAllText(Path.Combine(path, "sdk/a.h"), "changed");
        System.IO.File.SetUnixFileMode(Path.Combine(path, "sdk/a.h"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Equal(path, await Fetcher().EnsureAsync(artifact, null, CancellationToken.None));
        Assert.Equal("a", System.IO.File.ReadAllText(Path.Combine(path, "sdk/a.h")));
        Assert.Equal(2, _server.Requests.Count);

        // A file planted beside the manifest's, a link inside, a group-writable file, a missing manifest: each fails verification.
        foreach (var damage in new Action[]
        {
            () => System.IO.File.WriteAllText(Path.Combine(path, "sdk/extra"), "x"),
            () => System.IO.File.CreateSymbolicLink(Path.Combine(path, "sdk/link"), "/etc/hosts"),
            () => System.IO.File.SetUnixFileMode(Path.Combine(path, "sdk/b.h"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.GroupRead),
            () => System.IO.File.Delete(Path.Combine(entry, ArtifactCache.ManifestName)),
        })
        {
            var requests = _server.Requests.Count;
            damage();
            Assert.Null(await new ArtifactCache(ArtifactCache.RootOf(_root)).TryGetAsync(artifact, CancellationToken.None));
            Assert.False(Directory.Exists(entry)); // deleted, never used
            Assert.Equal(path, await Fetcher().EnsureAsync(artifact, null, CancellationToken.None));
            Assert.Equal(requests + 1, _server.Requests.Count);
        }
    }

    [Fact]
    public async Task The_cache_root_loses_the_work_roots_inherited_entries_and_an_entry_another_user_may_write_is_not_used_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // As setup leaves a work root: an inherited entry for a slot's user (the role account nobody stands in; nothing runs as it).
        Chmod("+a", "user:nobody allow list,search,read,readattr,readextattr,readsecurity,file_inherit,directory_inherit", _root);
        var body = "the sdk"u8.ToArray();
        _server.Serve("https://files.example/sdk.bin", body);
        var artifact = Artifact("https://files.example/sdk.bin", body);

        var path = await Fetcher().EnsureAsync(artifact, null, CancellationToken.None);

        var listing = await WorkerSandbox.ListAsync(ArtifactCache.RootOf(_root), CancellationToken.None);
        Assert.True(WorkerSandbox.IsReadOnlyToOthers(listing), listing);
        Assert.Null(WorktreeWriters.NativeAcl(path)); // nothing made in it inherits an entry
        // An entry a sandbox user could write (by an ACL entry the owner adds here): never used, deleted, fetched again.
        Chmod("+a", "user:nobody allow write", path);
        Assert.Null(await new ArtifactCache(ArtifactCache.RootOf(_root)).TryGetAsync(artifact, CancellationToken.None));
        Assert.Equal(path, await Fetcher().EnsureAsync(artifact, null, CancellationToken.None));
        Assert.Null(WorktreeWriters.NativeAcl(path));
        // A root someone opened up again is closed again by the next process's cache, and read back so.
        Chmod("+a", "user:nobody allow add_file", ArtifactCache.RootOf(_root));
        await new ArtifactCache(ArtifactCache.RootOf(_root)).EnsureRootAsync(CancellationToken.None);
        Assert.True(WorkerSandbox.IsReadOnlyToOthers(await WorkerSandbox.ListAsync(ArtifactCache.RootOf(_root), CancellationToken.None)));
    }

    private static void Chmod(params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/chmod", args) { RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }
}
