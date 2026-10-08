using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Authenticates as the factory's GitHub App: signs an app JWT with the app's
/// private key and exchanges it for an installation token restricted to one
/// repository with contents + pull_requests write. Every call mints a fresh token;
/// nothing is cached across runs.
/// </summary>
public sealed class GitHubApp(HttpClient http, string appId, string privateKeyPem, TimeProvider time)
{
    public static readonly Uri DefaultBaseAddress = new("https://api.github.com/");

    public static readonly IReadOnlyDictionary<string, string> TokenPermissions = new Dictionary<string, string>
    {
        ["contents"] = "write",
        ["pull_requests"] = "write",
    };

    /// <summary>Longest installation-token lifetime the factory accepts (GitHub issues 1-hour tokens).</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(1);

    // Tolerates clock drift between this Mac and GitHub when checking expires_at.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public string CreateJwt()
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        // Back-date iat for clock drift; GitHub caps exp at 10 minutes.
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = appId }));
        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <param name="permissions">The token's permissions; default <see cref="TokenPermissions"/>. Never more than the App was granted.</param>
    public async Task<InstallationToken> CreateInstallationTokenAsync(RepoRef repo, CancellationToken ct,
        IReadOnlyDictionary<string, string>? permissions = null)
    {
        var jwt = CreateJwt();

        using var lookup = Request(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/installation", "Bearer", jwt);
        using var lookupResponse = await http.SendAsync(lookup, ct);
        if (lookupResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"The GitHub App is not installed on {repo}. Install it, then retry.");
        }
        await EnsureSuccess(lookupResponse, "look up installation", ct);
        var installation = await lookupResponse.Content.ReadFromJsonAsync<InstallationDto>(ct);

        using var create = Request(HttpMethod.Post, $"app/installations/{installation!.Id}/access_tokens", "Bearer", jwt);
        create.Content = JsonContent.Create(new { repositories = new[] { repo.Name }, permissions = permissions ?? TokenPermissions });
        using var createResponse = await http.SendAsync(create, ct);
        await EnsureSuccess(createResponse, "create installation token", ct);
        var token = await createResponse.Content.ReadFromJsonAsync<AccessTokenDto>(ct);
        if (token!.ExpiresAt > time.GetUtcNow() + MaxTokenLifetime + ClockSkew)
        {
            throw new InvalidOperationException(
                $"GitHub issued an installation token for {repo} expiring at {token.ExpiresAt:O}, beyond the {MaxTokenLifetime.TotalMinutes:0}-minute limit.");
        }
        return new InstallationToken(token.Token, token.ExpiresAt);
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, string scheme, string credential)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("dark-factory", "0.1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    internal static async Task EnsureSuccess(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"GitHub {action} failed: {(int)response.StatusCode} {body}");
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record InstallationDto([property: JsonPropertyName("id")] long Id);

    private sealed record AccessTokenDto(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
