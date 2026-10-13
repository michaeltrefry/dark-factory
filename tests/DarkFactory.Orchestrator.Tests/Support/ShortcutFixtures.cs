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

/// <summary>
/// Forwards to the real API and records each call, scrubbed of personal data and of every board story that is not the
/// recording's own (<see cref="KeepStories"/>): a story listing (a team's or an epic's stories) is stored — and handed to the
/// adapter under recording, so the recording and its replay see the same board — with only those stories, each cut to the fields
/// the adapter reads (<see cref="TrimListing"/>). The repository is public; the board's other stories never enter a fixture.
/// </summary>
public sealed partial class RecordingHandler(ShortcutFixture fixture) : DelegatingHandler(new HttpClientHandler())
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"/(groups|epics)/[^/]+/stories$")]
    private static partial Regex StoryListing();

    /// <summary>What a listed story keeps: the fields <c>ShortcutWorkSource</c>'s story reads, nothing else of the board's.</summary>
    public static readonly IReadOnlyList<string> ListedStoryFields =
    [
        "id", "name", "story_type", "app_url", "archived", "workflow_id", "workflow_state_id", "group_id", "epic_id", "owner_ids",
        "labels", "external_links", "story_links", "estimate", "external_id",
    ];

    /// <summary>The most stories a recorded listing may hold (<see cref="TrimListing"/> keeps the recording's own few).</summary>
    public const int MaxListedStories = 10;

    public string? KeepTeam { get; init; }

    /// <summary>
    /// The ids of the recording's own throwaway stories: the only stories a recorded listing keeps. The recorder adds each story
    /// it creates before the call that lists it; empty, every listing is recorded empty.
    /// </summary>
    public HashSet<int> KeepStories { get; init; } = [];

    /// <summary>Whether <paramref name="path"/> is a story listing: <c>groups/{id}/stories</c> or <c>epics/{id}/stories</c>.</summary>
    public static bool IsStoryListing(string path) => StoryListing().IsMatch(path);

    /// <summary>
    /// <paramref name="stories"/> (a listing's response) with only the stories whose id is in <paramref name="keep"/>, in the
    /// listing's order, each with only <see cref="ListedStoryFields"/>. Also what trims an existing recording offline.
    /// </summary>
    public static JsonArray TrimListing(JsonArray stories, IReadOnlySet<int> keep) =>
        new(stories.Where(s => s is JsonObject o && o["id"] is JsonValue id && id.TryGetValue<int>(out var n) && keep.Contains(n))
            .Select(s => (JsonNode)new JsonObject(ListedStoryFields.Where(f => s!.AsObject().ContainsKey(f))
                .Select(f => KeyValuePair.Create(f, s![f]?.DeepClone()))))
            .ToArray());

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

    /// <summary>Keeps only what the factory may publish: the member's id and names, the watched team, and its own stories of a listing.</summary>
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
        if (IsStoryListing(path) && json is JsonArray stories)
        {
            return TrimListing(stories, KeepStories);
        }
        return json;
    }
}
