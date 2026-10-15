using System.Net;
using System.Security.Cryptography;
using DarkFactory.Orchestrator.Gateway;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>One harness download (sc-26232), recorded on the item's ledger as an <c>artifact-fetch</c> checkpoint: what was fetched, its SHA-256 and size.</summary>
public sealed record ArtifactDownloaded(string Name, Uri Url, string Sha256, long Bytes)
{
    public string ToDetail() => $"fetched {Name} {Url} sha256={Sha256} bytes={Bytes}";
}

/// <summary>
/// Fetches a recipe's artifacts into the read-only cache (sc-26232) — owner-side, the harness alone (no sandbox user downloads one):
/// an artifact the cache already holds (verified whole) is used as it is; any other is downloaded over the gateway's artifact client
/// (<see cref="OutboundHttp.ArtifactDownload"/>: public addresses only, checked and pinned at connect, no proxy) and kept only when
/// it matches the recipe. The rules: https only, and the URL checked before anything is resolved (<see cref="ArtifactUrls"/>); the
/// host's every address public (<see cref="ArtifactAddresses.ChooseAsync"/>, before the request and again in the connect callback);
/// at most <see cref="MaxRedirects"/> redirects, each new URL checked the same way (never to http); a status other than 200 refused;
/// a declared length over <c>max_bytes</c> refused before the body is read, and the body counted while it streams (refused past
/// <c>max_bytes</c>); hashed while it streams into an owner-only temporary file, which is cached only when its SHA-256 is the
/// recipe's (else refused naming both); the whole download bounded by <see cref="Timeout"/>. Every failure is an
/// <see cref="ArtifactFetchException"/> naming the artifact — never a pass.
/// </summary>
public sealed class ArtifactFetcher(HttpClient http, IArtifactResolver resolver, ArtifactCache cache, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    public const int MaxRedirects = 5;

    public TimeSpan Timeout => timeout ?? DefaultTimeout;

    public ArtifactCache Cache => cache;

    /// <summary>
    /// The cached path of every artifact <paramref name="recipe"/> declares (by name), downloading those the cache lacks;
    /// <paramref name="onDownloaded"/> is told of each download (not of a cache hit).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EnsureAsync(PrepareRecipe recipe,
        Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in recipe.Artifacts)
        {
            paths[artifact.Name] = await EnsureAsync(artifact, onDownloaded, ct);
        }
        return paths;
    }

    public async Task<string> EnsureAsync(PrepareArtifact artifact, Func<ArtifactDownloaded, CancellationToken, Task>? onDownloaded, CancellationToken ct)
    {
        try
        {
            if (await cache.TryGetAsync(artifact, ct) is { } cached)
            {
                return cached;
            }
            var temp = Path.Combine(cache.Root, $".download-{Guid.NewGuid():N}");
            try
            {
                var (bytes, fileName) = await DownloadAsync(artifact, temp, ct);
                var path = await cache.StoreAsync(artifact, temp, fileName, ct);
                if (onDownloaded is not null)
                {
                    await onDownloaded(new ArtifactDownloaded(artifact.Name, artifact.Url, artifact.Sha256, bytes), ct);
                }
                return path;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left behind: swept by the next cache's first use once it is older than a download can take.
                }
            }
        }
        catch (ArtifactFetchException)
        {
            throw;
        }
        catch (WorkSources.FactoryUnavailableException)
        {
            throw; // the cache's root could not be set up: the factory's failure, never this artifact's (or an item's)
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArtifactFetchException($"artifact {artifact.Name} ({artifact.Url}) could not be fetched: {Why(ex)}", ex);
        }
    }

    private static string Why(Exception ex) => ex switch
    {
        OperationCanceledException => "it timed out",
        HttpRequestException { InnerException: ArtifactAddressException address } => address.Message,
        HttpRequestException { InnerException: { } inner } http => $"{http.Message} ({inner.Message})",
        _ => ex.Message,
    };

    /// <summary>A new file only the owner can read (the download, until it is checked and cached).</summary>
    private static FileStreamOptions OwnerOnly()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    private async Task<(long Bytes, string? FileName)> DownloadAsync(PrepareArtifact artifact, string temp, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Timeout);
        var url = artifact.Url;
        for (var redirects = 0; ; redirects++)
        {
            if (ArtifactUrls.Problem(url) is { } problem)
            {
                throw new ArtifactFetchException($"artifact {artifact.Name}: {url} {problem}");
            }
            // The host's addresses are judged before the request (a clear refusal); the connect callback judges them again and
            // connects to the address it judged.
            try
            {
                await ArtifactAddresses.ChooseAsync(url.IdnHost, resolver, bounded.Token);
            }
            catch (ArtifactAddressException refused)
            {
                throw new ArtifactFetchException($"artifact {artifact.Name}: {refused.Message}");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptEncoding.Clear();
            request.Headers.UserAgent.ParseAdd("dark-factory-artifacts/1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= MaxRedirects)
                {
                    throw new ArtifactFetchException($"artifact {artifact.Name}: more than {MaxRedirects} redirects from {artifact.Url}");
                }
                if (response.Headers.Location is not { } location)
                {
                    throw new ArtifactFetchException($"artifact {artifact.Name}: {url} answered {status} with no Location");
                }
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new ArtifactFetchException($"artifact {artifact.Name}: {url} answered {status} {response.ReasonPhrase}");
            }
            if (response.Content.Headers.ContentLength is { } length && length > artifact.MaxBytes)
            {
                throw new ArtifactFetchException($"artifact {artifact.Name}: {url} is {length} bytes, more than its max_bytes {artifact.MaxBytes}");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var body = await response.Content.ReadAsStreamAsync(bounded.Token))
            await using (var file = new FileStream(temp, OwnerOnly()))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await body.ReadAsync(buffer, bounded.Token)) > 0)
                {
                    total += read;
                    if (total > artifact.MaxBytes)
                    {
                        throw new ArtifactFetchException($"artifact {artifact.Name}: {url} sent more than its max_bytes {artifact.MaxBytes}");
                    }
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), bounded.Token);
                }
            }
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (sha != artifact.Sha256)
            {
                throw new ArtifactFetchException($"artifact {artifact.Name}: {url} has SHA-256 {sha}, not the recipe's {artifact.Sha256}; nothing was cached",
                    workerMessage: $"artifact {artifact.Name}: {url} does not match the pinned sha256 {artifact.Sha256}; nothing was cached");
            }
            return (total, Uri.UnescapeDataString(artifact.Url.Segments[^1]).TrimEnd('/'));
        }
    }
}
