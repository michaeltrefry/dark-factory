using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Acquires bytes only: nothing in an Actions runner archive is extracted or executed here.</summary>
public static class GuiCollectorArchive
{
    public const long MaxBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private const int MetadataBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly Uri Latest = new("https://api.github.com/repos/actions/runner/releases/latest");

    /// <summary>
    /// Reads the official release metadata, then copies and verifies its exact osx-arm64 asset into a new private child of
    /// <paramref name="directory"/>. A supplied local archive needs the very same published checksum, never a self-issued hash.
    /// The supplied GitHub client is for the API only; public downloads use a separate credential-free, DNS-pinned transport.
    /// </summary>
    public static async Task<string> AcquireAsync(HttpClient github, string directory, string? suppliedArchive, CancellationToken ct)
    {
        using var download = OutboundHttp.ArtifactDownload(DnsArtifactResolver.Instance);
        return await AcquireAsync(github, download, directory, suppliedArchive, ct);
    }

    // Both clients are injectable so tests cannot accidentally download anything. Download clients must disable automatic
    // redirects, cookies and proxying and carry no default credentials; production gets exactly ArtifactTransport's handler.
    internal static async Task<string> AcquireAsync(HttpClient github, HttpClient download, string directory,
        string? suppliedArchive, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("dark-factory-gui-collector");
        using var metadata = await github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (metadata.StatusCode != HttpStatusCode.OK || metadata.RequestMessage?.RequestUri is { } final && final != Latest)
            throw new InvalidOperationException("The official Actions runner release metadata was refused or redirected.");
        await using var metadataStream = await metadata.Content.ReadAsStreamAsync(ct);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(metadataStream, MetadataBytes, ct));
        var asset = Select(json.RootElement);

        // The destination never reuses a caller's directory or a name supplied by metadata. Only this child receives
        // untrusted bytes. Scripts and other setup inputs remain outside it. It remains private after verification.
        var parent = Path.GetFullPath(directory);
        Directory.CreateDirectory(parent);
        var child = Path.Combine(parent, "runner-archive-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(child) || File.Exists(child)) throw new IOException("The archive directory must be fresh.");
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Runner archive acquisition requires Unix directory modes.");
        Directory.CreateDirectory(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A work root can carry inherited ACL entries: mode 0700 alone would not make this directory private on macOS.
        try
        {
            await DarkFactory.Orchestrator.Worker.WorkerSandbox.CloseDirectoryToOwnerAsync(child, ct);
            if (Directory.EnumerateFileSystemEntries(child).Any()) throw new IOException("The archive directory is not empty.");
            var target = Path.Combine(child, asset.Name);
            try
            {
                await using var output = new FileStream(target, new FileStreamOptions {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                if (suppliedArchive is not null)
                {
                    var source = new FileInfo(suppliedArchive);
                    if (!source.Exists || source.LinkTarget is not null || (source.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                        throw new InvalidOperationException("The supplied runner archive must be a regular file, not a link.");
                    if (source.Length != asset.Size || source.Length > MaxBytes)
                        throw new InvalidOperationException("The supplied runner archive has the wrong published size.");
                    await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await CopyVerifiedAsync(input, output, asset.Hash, asset.Size, ct);
                }
                else
                {
                    using var answer = await DownloadAsync(download, asset.Url, ct);
                    if (answer.Content.Headers.ContentLength is { } length && (length > MaxBytes || length != asset.Size))
                        throw new InvalidOperationException("The runner download has the wrong published size.");
                    await using var input = await answer.Content.ReadAsStreamAsync(ct);
                    await CopyVerifiedAsync(input, output, asset.Hash, asset.Size, ct);
                }
            }
            catch { File.Delete(target); throw; }
            // The metadata's selected version and official hash are pinned in an adjacent owner-only receipt, not
            // recomputed into a new trust root. Consumers can retain this even when latest moves on.
            var receipt = JsonSerializer.Serialize(new { version = asset.Name["actions-runner-osx-arm64-".Length..^7],
                archive = asset.Name, sha256 = asset.Hash, bytes = asset.Size, metadata = Latest.AbsoluteUri,
                download = asset.Url.AbsoluteUri, supplied = suppliedArchive is not null });
            var receiptPath = Path.Combine(child, "verified-release.json");
            await File.WriteAllTextAsync(receiptPath, receipt, ct);
            File.SetUnixFileMode(receiptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return target;
        }
        catch
        {
            // Only our own fixed-name inert archive is ever written here; no archive entry is unpacked.
            File.Delete(Path.Combine(child, asset.Name));
            File.Delete(Path.Combine(child, "verified-release.json"));
            if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            throw;
        }
    }

    public sealed record VerifiedSnapshot(string Path, string Sha256, long Bytes);

    /// <summary>Reuses the bounded official release proof without materializing a runner archive in memory. The private
    /// acquired snapshot is retained in place for root's no-follow handoff; there is no second whole-archive copy.</summary>
    public static async Task<VerifiedSnapshot> VerifySnapshotAsync(string archivePath, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        ct = deadline.Token;
        var archive = RegularFile(archivePath);
        var receipt = RegularFile(System.IO.Path.Combine(archive.DirectoryName!, "verified-release.json"));
        if (receipt.Length > MetadataBytes) throw new InvalidOperationException("The verified archive receipt exceeds its bound.");
        await using var proofStream = new FileStream(receipt.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var proof = JsonDocument.Parse(await ReadBoundedAsync(proofStream, MetadataBytes, ct));
        var root = proof.RootElement;
        var expected = root.GetProperty("sha256").GetString();
        var bytes = root.GetProperty("bytes").GetInt64();
        if (root.GetProperty("archive").GetString() != archive.Name || root.GetProperty("metadata").GetString() != Latest.AbsoluteUri
            || expected is null || !Regex.IsMatch(expected, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || bytes is <= 0 or > MaxBytes || archive.Length != bytes)
            throw new InvalidOperationException("The runner snapshot does not match its pinned official release proof.");
        await using var input = new FileStream(archive.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await CopyVerifiedAsync(input, Stream.Null, expected, bytes, ct);
        return new(archive.FullName, expected, bytes);
    }

    static FileInfo RegularFile(string path)
    {
        var file = new FileInfo(System.IO.Path.GetFullPath(path));
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Runner snapshot and proof must be regular files, never links.");
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new InvalidOperationException("Runner snapshot parents must be canonical, never links.");
        return file;
    }

    private sealed record Asset(string Name, Uri Url, string Hash, long Size);

    private static Asset Select(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidOperationException("The latest Actions runner release is not a stable published release.");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("The official Actions runner version is invalid.");
        var name = $"actions-runner-osx-arm64-{tag[1..]}.tar.gz";
        var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The release must contain exactly one matching osx-arm64 runner archive.");
        var asset = matches[0];
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxBytes) throw new InvalidOperationException("The official runner archive exceeds the size bound or is empty.");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "", UriKind.Absolute);
        if (ArtifactUrls.Problem(url) is not null || url.Host != "github.com" || url.Query.Length != 0 ||
            url.AbsolutePath != $"/actions/runner/releases/download/{tag}/{name}")
            throw new InvalidOperationException("The runner asset does not name the official Actions runner release download.");

        string? digest = null;
        if (asset.TryGetProperty("digest", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            var text = value.GetString() ?? "";
            if (!Regex.IsMatch(text, @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("The runner asset has an invalid official SHA256 digest.");
            digest = text[7..].ToLowerInvariant();
        }
        var body = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        var checksums = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            // Only a checksum-table row naming this exact asset counts; hashes mentioned in prose or for another platform do not.
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim().Trim('`')).ToArray();
            if (cells.Length == 2 && cells.Contains(name, StringComparer.Ordinal))
            {
                var hash = cells[0] == name ? cells[1] : cells[0];
                if (!Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("The official runner checksum table has an invalid matching row.");
                checksums.Add(hash.ToLowerInvariant());
            }
        }
        if (checksums.Count > 1 || digest is not null && checksums.Count == 1 && digest != checksums[0])
            throw new InvalidOperationException("The official runner checksums are ambiguous or disagree.");
        var expected = digest ?? checksums.SingleOrDefault() ?? throw new InvalidOperationException("No official SHA256 checksum was published for the runner archive.");
        return new(name, url, expected, size);
    }

    private static async Task<HttpResponseMessage> DownloadAsync(HttpClient client, Uri url, CancellationToken ct)
    {
        if (client.DefaultRequestHeaders.Any()) throw new InvalidOperationException("The runner download client must carry no default headers or credentials.");
        for (var redirects = 0; ; redirects++)
        {
            if (ArtifactUrls.Problem(url) is not null || url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                throw new InvalidOperationException("The runner download redirect is not an official public GitHub asset URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } final && final != url)
            { response.Dispose(); throw new InvalidOperationException("The runner download transport followed an automatic redirect."); }
            if (response.StatusCode == HttpStatusCode.OK) return response;
            var location = response.Headers.Location;
            var status = (int)response.StatusCode;
            response.Dispose();
            if (status is not (301 or 302 or 303 or 307 or 308) || location is null || redirects >= MaxRedirects)
                throw new InvalidOperationException("The runner download was refused or exceeded its redirect bound.");
            url = new Uri(url, location);
        }
    }

    internal static async Task CopyVerifiedAsync(Stream input, Stream output, string expectedHash, long expectedBytes, CancellationToken ct)
    {
        if (!Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) || expectedBytes is <= 0 or > MaxBytes)
            throw new InvalidOperationException("The archive proof does not contain a bounded pinned size and SHA256.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > MaxBytes || total > expectedBytes) throw new InvalidOperationException("The runner archive exceeds its published size or the 512 MiB bound.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != expectedBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner archive does not match the official published size and SHA256 checksum.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (result.Length + count > limit) throw new InvalidOperationException("The runner release metadata exceeds its bound.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
