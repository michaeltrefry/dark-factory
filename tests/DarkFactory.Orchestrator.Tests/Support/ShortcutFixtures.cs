using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>One recorded Shortcut API call. Request headers (the token) are never recorded.</summary>
public sealed record Interaction(string Method, string Path, JsonNode? Request, int Status, JsonNode? Response);

/// <summary>
/// A recorded Shortcut conversation: <see cref="Parameters"/> carries ids the scenario used
/// (e.g. the throwaway parent story), <see cref="Interactions"/> every call in order.
/// </summary>
public sealed class ShortcutFixture
{
    public string Source { get; set; } = "";
    public Dictionary<string, JsonNode?> Parameters { get; set; } = new();
    public List<Interaction> Interactions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Directory([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here))!, "Fixtures", "shortcut");

    public static ShortcutFixture Load(string name) =>
        JsonSerializer.Deserialize<ShortcutFixture>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcut", name)), Json)!;

    public void Save(string name) =>
        File.WriteAllText(Path.Combine(Directory(), name), JsonSerializer.Serialize(this, Json) + "\n");

    public int Int(string parameter) => Parameters[parameter]!.GetValue<int>();
}

/// <summary>
/// Replays a fixture strictly: each request must match the next recorded call's method, path
/// and JSON body (deep-equal), so the adapter's requests are pinned to what the real API accepted.
/// </summary>
public sealed class ReplayHandler(ShortcutFixture fixture) : HttpMessageHandler
{
    private int _next;

    public bool AllReplayed => _next == fixture.Interactions.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.True(request.Headers.Contains("Shortcut-Token"), "every Shortcut call carries the token header");
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        Assert.True(_next < fixture.Interactions.Count, $"unexpected extra call {request.Method} {path}");
        var expected = fixture.Interactions[_next++];
        Assert.Equal($"{expected.Method} {expected.Path}", $"{request.Method} {path}");
        Assert.True(JsonNode.DeepEquals(expected.Request, body),
            $"{request.Method} {path} body differs.\nexpected: {expected.Request?.ToJsonString()}\nactual:   {body?.ToJsonString()}");
        return new HttpResponseMessage((HttpStatusCode)expected.Status)
        {
            Content = new StringContent(expected.Response?.ToJsonString() ?? "", Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Forwards to the real API and records each call, scrubbed of personal data.</summary>
public sealed partial class RecordingHandler(ShortcutFixture fixture) : DelegatingHandler(new HttpClientHandler())
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public string? KeepTeam { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        var response = await base.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = text.Length == 0 ? null : JsonNode.Parse(Email().Replace(text, "redacted@example.com"));
        json = Scrub(path, json);
        fixture.Interactions.Add(new Interaction(request.Method.Method, path, body, (int)response.StatusCode, json));
        var replay = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(json?.ToJsonString() ?? "", Encoding.UTF8, "application/json") };
        response.Dispose();
        return replay;
    }

    /// <summary>Keeps only what the factory may publish: the member's id and names, and the watched team.</summary>
    private JsonNode? Scrub(string path, JsonNode? json)
    {
        if (path.EndsWith("/member") && json is JsonObject member)
        {
            return new JsonObject { ["id"] = member["id"]?.DeepClone(), ["mention_name"] = member["mention_name"]?.DeepClone(), ["name"] = member["name"]?.DeepClone() };
        }
        if (path.EndsWith("/groups") && json is JsonArray groups)
        {
            return new JsonArray(groups.Where(g => (string?)g!["mention_name"] == KeepTeam)
                .Select(g => (JsonNode)new JsonObject { ["id"] = g!["id"]!.DeepClone(), ["mention_name"] = g["mention_name"]!.DeepClone(), ["name"] = g["name"]!.DeepClone() })
                .ToArray());
        }
        return json;
    }
}
