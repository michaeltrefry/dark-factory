using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorArchiveTests
{
    private const string Name = "actions-runner-osx-arm64-2.321.0.tar.gz";
    private const string Url = "https://github.com/actions/runner/releases/download/v2.321.0/" + Name;
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("inert test bytes, not an actual runnable archive");
    private static string Hash => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    private static JsonObject Release() => new() {
        ["tag_name"] = "v2.321.0", ["draft"] = false, ["prerelease"] = false, ["body"] = "",
        ["assets"] = new JsonArray(new JsonObject { ["name"] = Name, ["size"] = Bytes.Length,
            ["browser_download_url"] = Url, ["digest"] = "sha256:" + Hash })
    };
    private static JsonObject Asset(JsonObject release) => release["assets"]!.AsArray()[0]!.AsObject();
    private static HttpResponseMessage Answer(JsonObject release) => new(HttpStatusCode.OK) { Content = new StringContent(release.ToJsonString()) };
    private static HttpResponseMessage Archive(byte[]? bytes = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Bytes) };
    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(answer(request));
        }
    }
    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gui-archive-tests-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private static HttpClient GitHub(JsonObject release) => new(new Handler(request => {
        Assert.Equal("https://api.github.com/repos/actions/runner/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        return Answer(release);
    }));

    [Fact]
    public async Task Official_digest_pins_inert_archive_and_receipt_in_a_fresh_private_child()
    {
        using var temp = new Temp();
        using var github = GitHub(Release());
        using var download = new HttpClient(new Handler(r => {
            Assert.Equal(Url, r.RequestUri!.AbsoluteUri);
            Assert.Null(r.Headers.Authorization);
            return Archive();
        }));
        var path = await GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default);
        Assert.Equal(Name, Path.GetFileName(path));
        Assert.NotEqual(temp.Root, Path.GetDirectoryName(path));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "verified-release.json")));
        Assert.Equal("2.321.0", receipt.RootElement.GetProperty("version").GetString());
        Assert.Equal(Hash, receipt.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(2, Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Official_checksum_table_is_accepted_alone_or_when_matching_the_asset_digest(bool digest)
    {
        using var temp = new Temp();
        var release = Release();
        if (!digest) Asset(release)["digest"] = null;
        release["body"] = $"## SHA-256 Checksums\n| File | SHA-256 |\n| --- | --- |\n| `{Name}` | `{Hash}` |\n";
        using var github = GitHub(release);
        using var download = new HttpClient(new Handler(_ => Archive()));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(await GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default)));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed-digest")]
    [InlineData("other-platform")]
    [InlineData("duplicate-asset")]
    [InlineData("duplicate-checksum")]
    [InlineData("conflicting-checksum")]
    [InlineData("prose-hash")]
    [InlineData("wrong-url")]
    [InlineData("wrong-version")]
    [InlineData("oversized")]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public async Task Missing_wrong_or_ambiguous_published_identity_is_refused_before_download(string mutation)
    {
        using var temp = new Temp();
        var release = Release();
        switch (mutation)
        {
            case "absent": Asset(release)["digest"] = null; break;
            case "malformed-digest": Asset(release)["digest"] = "sha256:no"; break;
            case "other-platform": Asset(release)["name"] = Name.Replace("arm64", "x64"); break;
            case "duplicate-asset": release["assets"]!.AsArray().Add(Asset(release).DeepClone()); break;
            case "duplicate-checksum": release["body"] = $"| {Name} | {Hash} |\n| {Name} | {Hash} |"; break;
            case "conflicting-checksum": release["body"] = $"| {Name} | {new string('0', 64)} |"; break;
            case "prose-hash": Asset(release)["digest"] = null; release["body"] = $"Checksum for {Name}: {Hash}"; break;
            case "wrong-url": Asset(release)["browser_download_url"] = Url.Replace("/actions/runner/", "/attacker/runner/"); break;
            case "wrong-version": release["tag_name"] = "v2.322.0"; break;
            case "oversized": Asset(release)["size"] = GuiCollectorArchive.MaxBytes + 1; break;
            case "draft": release["draft"] = true; break;
            case "prerelease": release["prerelease"] = true; break;
        }
        using var github = GitHub(release);
        var handler = new Handler(_ => throw new Exception("Must never download invalid metadata"));
        using var download = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Root));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length-header")]
    [InlineData("streamed-size")]
    [InlineData("truncated")]
    public async Task Wrong_hash_size_and_unbounded_streams_never_leave_an_archive(string problem)
    {
        using var temp = new Temp();
        using var github = GitHub(Release());
        using var download = new HttpClient(new Handler(_ => {
            if (problem == "length-header")
            {
                var result = Archive();
                result.Content.Headers.ContentLength = GuiCollectorArchive.MaxBytes + 1;
                return result;
            }
            var content = problem switch {
                "hash" => Enumerable.Repeat((byte)'x', Bytes.Length).ToArray(),
                "streamed-size" => Bytes.Concat(new byte[] { 1 }).ToArray(),
                _ => Bytes[..^1]
            };
            // StreamContent does not publish a Content-Length for a nonseekable stream: the copy itself must enforce size.
            return new(HttpStatusCode.OK) { Content = new StreamContent(new Nonseekable(content)) };
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Root));
    }

    private sealed class Nonseekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Official_asset_redirects_are_manual_and_never_receive_the_owner_API_token()
    {
        using var temp = new Temp();
        using var github = GitHub(Release());
        github.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "OWNER_SECRET");
        var calls = 0;
        using var download = new HttpClient(new Handler(r => {
            Assert.Null(r.Headers.Authorization);
            Assert.DoesNotContain("OWNER_SECRET", r.Headers.ToString());
            return ++calls == 1 ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/123?sig=test") : Archive();
        }));
        await GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/file")]
    [InlineData("https://user:password@release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com:8443/file")]
    [InlineData("https://127.0.0.1/file")]
    [InlineData("https://evil.example/file")]
    [InlineData("https://release-assets.githubusercontent.com/file#fragment")]
    public async Task Unsafe_redirect_is_refused_before_a_second_request(string redirect)
    {
        using var temp = new Temp();
        using var github = GitHub(Release());
        var handler = new Handler(_ => Redirect(redirect));
        using var download = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Equal(1, handler.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Root));
    }

    [Fact]
    public async Task Redirect_loop_and_download_client_with_credentials_fail_closed()
    {
        using var temp = new Temp();
        using var github = GitHub(Release());
        var handler = new Handler(_ => Redirect(Url));
        using var download = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Equal(6, handler.Calls);
        download.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "SECRET");
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Equal(6, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Supplied_archive_is_copied_only_when_it_matches_the_same_official_checksum(bool valid)
    {
        using var temp = new Temp();
        var source = Path.Combine(temp.Root, "operator-supplied.tar.gz");
        await File.WriteAllBytesAsync(source, valid ? Bytes : Enumerable.Repeat((byte)'x', Bytes.Length).ToArray());
        using var github = GitHub(Release());
        var handler = new Handler(_ => throw new Exception("Supplied file must not download"));
        using var download = new HttpClient(handler);
        if (valid)
        {
            var path = await GuiCollectorArchive.AcquireAsync(github, download, temp.Root, source, default);
            Assert.NotEqual(source, path);
            Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, source, default));
            Assert.Single(Directory.GetFileSystemEntries(temp.Root));
        }
        Assert.Equal(0, handler.Calls);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Supplied_file_cannot_bypass_missing_official_metadata_or_use_a_symlink()
    {
        using var temp = new Temp();
        var source = Path.Combine(temp.Root, "supplied.tar.gz");
        await File.WriteAllBytesAsync(source, Bytes);
        var release = Release();
        Asset(release)["digest"] = null;
        using var github = GitHub(release);
        using var download = new HttpClient(new Handler(_ => throw new Exception("No download")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, source, default));
        using var official = GitHub(Release());
        var link = Path.Combine(temp.Root, "link.tar.gz");
        File.CreateSymbolicLink(link, source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(official, download, temp.Root, link, default));
        Assert.Equal(2, Directory.GetFileSystemEntries(temp.Root).Length);
    }

    [Fact]
    public async Task Metadata_size_bound_and_caller_cancellation_prevent_any_download()
    {
        using var temp = new Temp();
        using var github = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) {
            Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) }));
        var handler = new Handler(_ => throw new Exception("No download"));
        using var download = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, cancelled.Token));
        Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Root));
        Assert.Equal(TimeSpan.FromMinutes(15), GuiCollectorArchive.Timeout);
        Assert.Equal(512L * 1024 * 1024, GuiCollectorArchive.MaxBytes);
    }

    [Fact]
    public async Task Metadata_redirect_is_not_accepted_as_official_release_metadata()
    {
        using var temp = new Temp();
        using var github = new HttpClient(new Handler(_ => Redirect("https://evil.example/release")));
        using var download = new HttpClient(new Handler(_ => throw new Exception("No download")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.AcquireAsync(github, download, temp.Root, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Root));
    }
}
