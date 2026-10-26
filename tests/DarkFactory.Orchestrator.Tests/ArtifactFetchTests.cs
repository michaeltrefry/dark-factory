using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task The_production_transport_checks_the_hosts_addresses_again_at_connect_and_follows_no_proxy_or_redirect()
    {
        // sc-26134: the transport OutboundHttp.ArtifactDownload builds. Its connect callback resolves the host again and refuses a
        // private address before any socket is opened (DNS rebinding after the downloader's own check), so nothing is connected here.
        var resolver = new FakeResolver { ["rebound.example"] = ["127.0.0.1"] };
        using var handler = Gateway.ArtifactTransport.Handler(resolver);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
        using var client = new HttpClient(handler, disposeHandler: false);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://rebound.example/a.txt"));

        var refused = Assert.IsType<Gateway.ArtifactAddressException>(ex.InnerException);
        Assert.Contains("127.0.0.1", refused.Message);
        Assert.Equal(["rebound.example"], resolver.Asked);
    }

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

    internal static byte[] Tar(params TarEntry[] entries)
    {
        using var memory = new MemoryStream();
        using (var tar = new TarWriter(memory, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
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
        Assert.Contains($"has SHA-256 {Sha256("tampered"u8.ToArray())}", refused.Message); // the full reason: the ledger's and the gate's
        // What a worker is told: never the served bytes' hash.
        Assert.Equal($"artifact tool: https://files.example/sdk.bin does not match the pinned sha256 {artifact.Sha256}; nothing was cached", refused.WorkerMessage);
        Assert.DoesNotContain(Sha256("tampered"u8.ToArray()), refused.WorkerMessage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)));
    }

    [Fact]
    public async Task A_verified_entry_is_not_hashed_again_by_the_same_cache_until_its_manifest_changes()
    {
        var body = "the sdk"u8.ToArray();
        _server.Serve("https://files.example/sdk.bin", body);
        var artifact = Artifact("https://files.example/sdk.bin", body);
        var cache = new ArtifactCache(ArtifactCache.RootOf(_root));
        var fetcher = new ArtifactFetcher(new HttpClient(_server), _resolver, cache);

        var path = await fetcher.EnsureAsync(artifact, null, CancellationToken.None);
        Assert.Equal(1, cache.Verifications); // the store's own read-back
        Assert.Equal(path, await cache.TryGetAsync(artifact, CancellationToken.None));
        Assert.Equal(path, await fetcher.EnsureAsync(artifact, null, CancellationToken.None));
        Assert.Equal(1, cache.Verifications);

        // A new manifest (the entry replaced) is verified in full again; another cache verifies for itself.
        var manifest = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, ArtifactCache.ManifestName);
        System.IO.File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow.AddMinutes(-3));
        Assert.Equal(path, await cache.TryGetAsync(artifact, CancellationToken.None));
        Assert.Equal(2, cache.Verifications);
        var other = new ArtifactCache(ArtifactCache.RootOf(_root));
        Assert.Equal(path, await other.TryGetAsync(artifact, CancellationToken.None));
        Assert.Equal(1, other.Verifications);
        Assert.Single(_server.Requests);
    }

    private static byte[] SizedArtifact(ArtifactUnpack unpack) => unpack switch
    {
        ArtifactUnpack.None => "abcdef"u8.ToArray(),
        ArtifactUnpack.Zip => Zip(("a", "abc", 0x81A4), ("b", "def", 0x81A4)),
        ArtifactUnpack.Tar => Tar(File("a", "abc"), File("b", "def")),
        ArtifactUnpack.TarGz => TarGz(File("a", "abc"), File("b", "def")),
        _ => throw new ArgumentOutOfRangeException(nameof(unpack)),
    };

    public static TheoryData<ArtifactUnpack, bool, int> CurrentLimits => new()
    {
        { ArtifactUnpack.None, false, 0 }, { ArtifactUnpack.None, false, 1 }, { ArtifactUnpack.None, false, 2 },
        { ArtifactUnpack.Zip, false, 0 }, { ArtifactUnpack.Zip, false, 1 }, { ArtifactUnpack.Zip, false, 2 },
        { ArtifactUnpack.Tar, false, 0 }, { ArtifactUnpack.Tar, false, 1 }, { ArtifactUnpack.Tar, false, 2 },
        { ArtifactUnpack.TarGz, false, 0 }, { ArtifactUnpack.TarGz, false, 1 }, { ArtifactUnpack.TarGz, false, 2 },
        { ArtifactUnpack.Zip, true, 0 }, { ArtifactUnpack.Zip, true, 1 }, { ArtifactUnpack.Zip, true, 2 },
        { ArtifactUnpack.Tar, true, 0 }, { ArtifactUnpack.Tar, true, 1 }, { ArtifactUnpack.Tar, true, 2 },
        { ArtifactUnpack.TarGz, true, 0 }, { ArtifactUnpack.TarGz, true, 1 }, { ArtifactUnpack.TarGz, true, 2 },
    };

    [Theory]
    [MemberData(nameof(CurrentLimits))]
    public async Task Current_recipe_limits_apply_equally_to_fresh_warm_and_memoized_artifacts(ArtifactUnpack unpack, bool unpackedLimit, int cacheState)
    {
        // 0: no entry; 1: a previous process's entry; 2: this cache's fully verified memoized hit.
        var body = SizedArtifact(unpack);
        const string url = "https://files.example/tool";
        _server.Serve(url, body);
        var artifact = Artifact(url, body, unpack, maxBytes: body.Length, maxUnpacked: 6);
        var cache = new ArtifactCache(ArtifactCache.RootOf(_root));
        var fetcher = new ArtifactFetcher(new HttpClient(_server), _resolver, cache);
        string? cachedPath = null;
        if (cacheState != 0)
        {
            cachedPath = await fetcher.EnsureAsync(artifact, null, CancellationToken.None); // exact limits pass
            var manifest = JsonSerializer.Deserialize<CacheManifest>(System.IO.File.ReadAllText(Path.Combine(cache.EntryOf(artifact), ArtifactCache.ManifestName)))!;
            Assert.Equal((long)body.Length, manifest.DownloadedBytes);
            if (cacheState == 1)
            {
                cache = new ArtifactCache(cache.Root);
                fetcher = new ArtifactFetcher(new HttpClient(_server), _resolver, cache);
            }
        }
        var stricter = unpackedLimit ? artifact with { MaxUnpackedBytes = 5 } : artifact with { MaxBytes = body.Length - 1 };

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() => fetcher.EnsureAsync(stricter, null, CancellationToken.None));

        Assert.Contains(unpackedLimit ? "max_unpacked_bytes" : "max_bytes", refused.Message);
        Assert.Single(_server.Requests); // warm hits reject without another download
        if (cacheState != 0)
        {
            Assert.True(Directory.Exists(cache.EntryOf(artifact))); // the entry is valid, just outside this recipe's limits
            Assert.Equal(cachedPath, await fetcher.EnsureAsync(artifact, null, CancellationToken.None));
            Assert.Single(_server.Requests);
            Assert.Equal(1, cache.Verifications); // including the memoized rejection: no rehash required
        }
        else
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Root));
            Assert.NotEmpty(await fetcher.EnsureAsync(artifact, null, CancellationToken.None)); // boundary passes fresh too
            Assert.Equal(2, _server.Requests.Count);
        }
    }

    [Theory]
    [InlineData(ArtifactUnpack.None, "missing")]
    [InlineData(ArtifactUnpack.Zip, "missing")]
    [InlineData(ArtifactUnpack.Tar, "null")]
    [InlineData(ArtifactUnpack.TarGz, "negative")]
    public async Task A_legacy_or_invalid_download_size_is_refetched_never_trusted(ArtifactUnpack unpack, string damage)
    {
        var body = SizedArtifact(unpack);
        const string url = "https://files.example/tool";
        _server.Serve(url, body);
        var artifact = Artifact(url, body, unpack);
        var cache = new ArtifactCache(ArtifactCache.RootOf(_root));
        var fetcher = new ArtifactFetcher(new HttpClient(_server), _resolver, cache);
        var path = await fetcher.EnsureAsync(artifact, null, CancellationToken.None);
        var manifestPath = Path.Combine(cache.EntryOf(artifact), ArtifactCache.ManifestName);
        var manifest = JsonNode.Parse(System.IO.File.ReadAllText(manifestPath))!.AsObject();
        if (damage == "missing")
        {
            manifest.Remove("downloaded_bytes");
        }
        else
        {
            manifest["downloaded_bytes"] = damage == "null" ? null : JsonValue.Create(-1L);
        }
        System.IO.File.WriteAllText(manifestPath, manifest.ToJsonString());
        System.IO.File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddMinutes(-3));

        Assert.Null(await cache.TryGetAsync(artifact, CancellationToken.None));
        Assert.False(Directory.Exists(cache.EntryOf(artifact)));
        Assert.Equal(path, await fetcher.EnsureAsync(artifact, null, CancellationToken.None));
        Assert.Equal(2, _server.Requests.Count);
        var repaired = JsonSerializer.Deserialize<CacheManifest>(System.IO.File.ReadAllText(manifestPath))!;
        Assert.Equal((long)body.Length, repaired.DownloadedBytes);
    }

    [Fact]
    public async Task An_entry_that_cannot_be_read_to_verify_is_an_error_and_is_not_deleted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix modes.");
        }
        var zip = Zip(("sdk/a.h", "a", 0x81A4));
        _server.Serve("https://files.example/sdk.zip", zip);
        var artifact = Artifact("https://files.example/sdk.zip", zip, ArtifactUnpack.Zip);
        var path = await Fetcher().EnsureAsync(artifact, null, CancellationToken.None);
        var file = Path.Combine(path, "sdk/a.h");
        System.IO.File.SetUnixFileMode(file, UnixFileMode.None); // the owner cannot read it: an I/O failure, not a mismatch
        try
        {
            var unreadable = await Assert.ThrowsAsync<ArtifactFetchException>(() => new ArtifactCache(ArtifactCache.RootOf(_root)).TryGetAsync(artifact, CancellationToken.None));
            Assert.Contains("could not be read to verify it, so it was neither used nor deleted", unreadable.Message);
            Assert.True(System.IO.File.Exists(file)); // left as it is
            var fetch = await Assert.ThrowsAsync<ArtifactFetchException>(() => Fetcher().EnsureAsync(artifact, null, CancellationToken.None));
            Assert.Contains("neither used nor deleted", fetch.Message);
            Assert.Single(_server.Requests); // never fetched again over it
        }
        finally
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        Assert.Equal(path, await new ArtifactCache(ArtifactCache.RootOf(_root)).TryGetAsync(artifact, CancellationToken.None));
    }

    [Fact]
    public async Task The_first_use_of_a_cache_sweeps_downloads_and_stores_a_dead_process_left_and_only_those()
    {
        var root = ArtifactCache.RootOf(_root);
        Directory.CreateDirectory(root);
        var old = DateTime.UtcNow - ArtifactFetcher.DefaultTimeout - TimeSpan.FromMinutes(1);
        var staleDownload = Path.Combine(root, ".download-0123");
        System.IO.File.WriteAllText(staleDownload, "half");
        System.IO.File.SetLastWriteTimeUtc(staleDownload, old);
        var stalePartial = Directory.CreateDirectory(Path.Combine(root, ".partial-0123")).FullName;
        System.IO.File.WriteAllText(Path.Combine(stalePartial, "x"), "x");
        Directory.SetLastWriteTimeUtc(stalePartial, old);
        var liveDownload = Path.Combine(root, ".download-4567"); // another process's download, still being written
        System.IO.File.WriteAllText(liveDownload, "live");
        var livePartial = Directory.CreateDirectory(Path.Combine(root, ".partial-4567")).FullName;
        var entry = Directory.CreateDirectory(Path.Combine(root, "old-entry")).FullName; // not a leftover's name
        Directory.SetLastWriteTimeUtc(entry, old);

        await new ArtifactCache(root).EnsureRootAsync(CancellationToken.None);

        Assert.False(System.IO.File.Exists(staleDownload));
        Assert.False(Directory.Exists(stalePartial));
        Assert.True(System.IO.File.Exists(liveDownload));
        Assert.True(Directory.Exists(livePartial));
        Assert.True(Directory.Exists(entry));
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
    [InlineData("https://other.example:8443/x", "names port 8443")]
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

    [Theory]
    [InlineData(ArtifactUnpack.Tar, ".")]
    [InlineData(ArtifactUnpack.Tar, "./")]
    [InlineData(ArtifactUnpack.TarGz, ".")]
    [InlineData(ArtifactUnpack.TarGz, "./")]
    public async Task A_tar_directory_root_marker_is_skipped_and_its_children_are_unpacked(ArtifactUnpack unpack, string rootName)
    {
        TarEntry[] entries = [new PaxTarEntry(TarEntryType.Directory, rootName), File("./lib/a.txt", "a")];
        var body = unpack == ArtifactUnpack.Tar ? Tar(entries) : TarGz(entries);
        const string url = "https://files.example/root-tar";
        _server.Serve(url, body);
        var artifact = Artifact(url, body, unpack);

        var path = await Fetcher().EnsureAsync(artifact, null, CancellationToken.None);

        Assert.Equal("a", System.IO.File.ReadAllText(Path.Combine(path, "lib/a.txt")));
        var manifest = JsonSerializer.Deserialize<CacheManifest>(System.IO.File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, ArtifactCache.ManifestName)))!;
        Assert.Equal(["lib", "lib/a.txt"], manifest.Entries.Select(e => e.Path)); // root never becomes an entry
        Assert.Equal(path, await Fetcher().EnsureAsync(artifact, null, CancellationToken.None));
        Assert.Single(_server.Requests);
    }

    public static TheoryData<ArtifactUnpack, TarEntryType, string> UnsafeTarRoots
    {
        get
        {
            var data = new TheoryData<ArtifactUnpack, TarEntryType, string>();
            foreach (var unpack in new[] { ArtifactUnpack.Tar, ArtifactUnpack.TarGz })
            {
                foreach (var type in new[] { TarEntryType.RegularFile, TarEntryType.SymbolicLink, TarEntryType.HardLink })
                {
                    data.Add(unpack, type, ".");
                    data.Add(unpack, type, "./");
                }
                foreach (var name in new[] { "../", "./../", "./../escape", "/" })
                {
                    data.Add(unpack, TarEntryType.Directory, name);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeTarRoots))]
    public async Task Only_a_directory_root_marker_is_skipped_never_a_root_file_link_or_traversal(ArtifactUnpack unpack, TarEntryType type, string name)
    {
        var entry = new PaxTarEntry(type, name);
        if (type == TarEntryType.RegularFile)
        {
            entry.DataStream = new MemoryStream("x"u8.ToArray());
        }
        if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            entry.LinkName = "../escape";
        }
        var body = unpack == ArtifactUnpack.Tar ? Tar(entry) : TarGz(entry);
        const string url = "https://files.example/unsafe-root-tar";
        _server.Serve(url, body);

        var refused = await Assert.ThrowsAsync<ArtifactFetchException>(() =>
            Fetcher().EnsureAsync(Artifact(url, body, unpack), null, CancellationToken.None));

        Assert.Contains("not a plain relative path", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArtifactCache.RootOf(_root)));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "escape")));
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

    [Fact]
    public async Task An_archive_with_nested_directories_unpacks_owner_only_writable_with_no_acl_entry_anywhere_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("ACLs via chmod +a are macOS-only.");
        }
        // The work root carries the inherited read entry setup gives it; nothing in the cache may inherit it, at any depth.
        Chmod("+a", "user:nobody allow list,search,read,readattr,readextattr,readsecurity,file_inherit,directory_inherit", _root);
        var zip = Zip(("a/b/c/deep.h", "deep", 0x81A4), ("a/b/run", "#!/bin/sh", 0x81ED), ("x/", null, 0x41ED));
        _server.Serve("https://files.example/nested.zip", zip);
        var tar = TarGz(File("p/q/r/deep.txt", "deep"), new PaxTarEntry(TarEntryType.Directory, "p/empty/"));
        _server.Serve("https://files.example/nested.tar.gz", tar);

        var fromZip = await Fetcher().EnsureAsync(Artifact("https://files.example/nested.zip", zip, ArtifactUnpack.Zip), null, CancellationToken.None);
        var fromTar = await Fetcher().EnsureAsync(Artifact("https://files.example/nested.tar.gz", tar, ArtifactUnpack.TarGz), null, CancellationToken.None);

        var directory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var file = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        foreach (var content in new[] { fromZip, fromTar })
        {
            var entry = Path.GetDirectoryName(content)!;
            Assert.Null(WorktreeWriters.Inside(entry, Environment.UserName, WorktreeWriters.NativeAcl, CancellationToken.None));
            foreach (var path in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories).Prepend(entry))
            {
                Assert.Null(WorktreeWriters.NativeAcl(path));
            }
        }
        foreach (var nested in new[] { "a", "a/b", "a/b/c", "x" })
        {
            Assert.Equal(directory, System.IO.File.GetUnixFileMode(Path.Combine(fromZip, nested)));
        }
        foreach (var nested in new[] { "p", "p/q", "p/q/r", "p/empty" })
        {
            Assert.Equal(directory, System.IO.File.GetUnixFileMode(Path.Combine(fromTar, nested)));
        }
        Assert.Equal(file, System.IO.File.GetUnixFileMode(Path.Combine(fromZip, "a/b/c/deep.h")));
        Assert.Equal(directory, System.IO.File.GetUnixFileMode(Path.Combine(fromZip, "a/b/run"))); // executable: 0755
        Assert.Equal(file, System.IO.File.GetUnixFileMode(Path.Combine(fromTar, "p/q/r/deep.txt")));
    }

    private static void Chmod(params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/chmod", args) { RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }
}
