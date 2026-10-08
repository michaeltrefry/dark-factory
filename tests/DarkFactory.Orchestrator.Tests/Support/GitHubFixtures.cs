using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// One GitHub API call of a fixture. <see cref="Token"/> names the permissions the call's installation token must have been minted
/// with (e.g. <c>issues:write</c>); <see cref="Raw"/> is a non-JSON response body (a raw file).
/// </summary>
public sealed record GitHubInteraction(string Method, string Path, string Token, JsonNode? Request, int Status, JsonNode? Response, string? Raw = null);

/// <summary>A GitHub conversation (<c>Fixtures/github</c>), in the shapes of GitHub's REST API (version 2022-11-28).</summary>
public sealed class GitHubFixture
{
    public string Source { get; set; } = "";
    public List<GitHubInteraction> Interactions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static GitHubFixture Load(string name) =>
        JsonSerializer.Deserialize<GitHubFixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", name)), Json)!;
}

/// <summary>
/// Replays a GitHub fixture strictly, as the factory's App sees GitHub: the installation lookup and token mint are answered here
/// (each token is named after the permissions it was minted with, so each call's token is checked against the fixture's), and
/// every other request must match the next recorded call's method, path with query, token and JSON body.
/// </summary>
public sealed class GitHubReplayHandler(GitHubFixture fixture) : HttpMessageHandler
{
    private int _next;

    public bool AllReplayed => _next == fixture.Interactions.Count;

    /// <summary>Every token minted: the repositories and permissions asked for.</summary>
    public List<string> Mints { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        if (request.Method == HttpMethod.Get && path.EndsWith("/installation", StringComparison.Ordinal))
        {
            return Respond(200, """{"id":1}""");
        }
        if (request.Method == HttpMethod.Post && path == "/app/installations/1/access_tokens")
        {
            var mint = JsonNode.Parse(body!)!;
            var permissions = mint["permissions"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}");
            var label = string.Join(",", permissions);
            Mints.Add($"{string.Join(",", mint["repositories"]!.AsArray().Select(r => (string?)r))} {label}");
            return Respond(201, $$"""{"token":"ghs-{{label}}","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}""");
        }
        Assert.True(_next < fixture.Interactions.Count, $"unexpected extra call {request.Method} {path}");
        var expected = fixture.Interactions[_next++];
        Assert.Equal($"{expected.Method} {expected.Path}", $"{request.Method} {path}");
        Assert.Equal($"Bearer ghs-{expected.Token}", request.Headers.Authorization?.ToString());
        var json = body is null ? null : JsonNode.Parse(body);
        Assert.True(JsonNode.DeepEquals(expected.Request, json),
            $"{request.Method} {path} body differs.\nexpected: {expected.Request?.ToJsonString()}\nactual:   {json?.ToJsonString()}");
        return Respond(expected.Status, expected.Raw ?? expected.Response?.ToJsonString() ?? "");
    }

    private static HttpResponseMessage Respond(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
