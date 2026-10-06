using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A stateful in-memory Shortcut API (the subset <see cref="ShortcutWorkSource"/> uses): stories
/// change when PUT, team listings page by limit/offset and epic listings return every story, like the
/// real API. Every request is recorded.
/// </summary>
public sealed class FakeShortcutBoard : HttpMessageHandler
{
    public const string Me = "00000000-0000-4000-8000-00000000000a";
    public const string FactoryTeam = "00000000-0000-4000-8000-0000000000f1";
    public const string OtherTeam = "00000000-0000-4000-8000-0000000000f2";
    public const long Workflow = 500000005, Backlog = 500000006, ToDo = 500000007, InProgress = 500000008, Done = 500000010;

    private readonly ConcurrentDictionary<int, JsonObject> _stories = new();
    private readonly List<JsonObject> _links = [];
    private int _nextId = 9000;
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Misbehaving API: team and epic listings return every story, ignoring the team/epic asked for.</summary>
    public bool IgnoreListingFilter { get; set; }

    /// <summary>Misbehaving API: a PUT accepts but does not apply label changes.</summary>
    public bool DropLabelsOnPut { get; set; }

    /// <summary>Fails the Nth (1-based) <c>POST stories</c> with a 500 once; 0 = never.</summary>
    public int FailStoryCreate { get; set; }
    private int _storyCreates;

    /// <summary>Called with each request before it is served; may return a response to send instead.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public JsonObject Story(int id) => _stories[id];

    public IReadOnlyCollection<JsonObject> Stories => _stories.Values.ToList();

    public IReadOnlyList<JsonObject> Links
    {
        get
        {
            lock (_links)
            {
                return _links.ToList();
            }
        }
    }

    public void Add(int id, string team, long state = ToDo, int? epic = null, string name = "Whitespace counts as a word") =>
        _stories[id] = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = "WordCount(\"  \") returns 1.",
            ["story_type"] = "bug",
            ["app_url"] = $"https://app.shortcut.com/trefry/story/{id}",
            ["archived"] = false,
            ["workflow_id"] = Workflow,
            ["workflow_state_id"] = state,
            ["group_id"] = team,
            ["epic_id"] = epic,
            ["owner_ids"] = new JsonArray(),
            ["labels"] = new JsonArray(),
            ["external_links"] = new JsonArray(),
            ["story_links"] = new JsonArray(),
        };

    public HttpClient Client() => new(this) { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath["/api/v3/".Length..];
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri.PathAndQuery, [], body));
        if (Intercept?.Invoke(request) is { } intercepted)
        {
            return intercepted;
        }
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var json = body is null ? null : JsonNode.Parse(body)!.AsObject();
        var segments = path.Split('/');
        return (request.Method.Method, segments) switch
        {
            ("GET", ["member"]) => Ok(new JsonObject { ["id"] = Me }),
            ("GET", ["groups"]) => Ok(JsonNode.Parse($$"""[{"id":"{{FactoryTeam}}","mention_name":"darkfactory"},{"id":"{{OtherTeam}}","mention_name":"other"}]""")),
            ("GET", ["workflows"]) => Ok(JsonNode.Parse($$"""
                [{"id":{{Workflow}},"name":"Standard","states":[{"id":{{Backlog}},"name":"Backlog"},{"id":{{ToDo}},"name":"To Do"},
                  {"id":{{InProgress}},"name":"In Progress"},{"id":500000009,"name":"In Review"},{"id":{{Done}},"name":"Done"}]}]
                """)),
            ("GET", ["groups", var team, "stories"]) => Ok(Page(
                Sorted().Where(s => IgnoreListingFilter || (string?)s["group_id"] == team),
                int.Parse(query["offset"] ?? "0"), int.Parse(query["limit"] ?? "1000"))),
            ("GET", ["epics", var epic, "stories"]) => Ok(new JsonArray(
                Sorted().Where(s => IgnoreListingFilter || (int?)s["epic_id"] == int.Parse(epic)).Select(WithLinks).ToArray())),
            ("GET", ["epics", var epic]) when int.Parse(epic) < 1000 => Ok(new JsonObject
            {
                ["id"] = int.Parse(epic),
                ["name"] = $"Epic {epic}",
                ["description"] = "Epic description.",
                ["app_url"] = $"https://app.shortcut.com/trefry/epic/{epic}",
            }),
            ("GET", ["epics", var epic, "documents"]) when int.Parse(epic) < 1000 => Ok(new JsonArray()),
            ("GET", ["stories", var id]) when _stories.TryGetValue(int.Parse(id), out var s) => Ok(WithLinks(s)),
            ("PUT", ["stories", var id]) when _stories.TryGetValue(int.Parse(id), out var s) => Ok(Update(s, json!)),
            ("POST", ["stories", var id, "comments"]) when _stories.ContainsKey(int.Parse(id)) => Ok(new JsonObject { ["id"] = 1 }, HttpStatusCode.Created),
            ("POST", ["stories"]) => Create(json!),
            ("POST", ["story-links"]) => Link(json!),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private IEnumerable<JsonObject> Sorted() => _stories.Values.OrderBy(s => (int)s["id"]!);

    private JsonArray Page(IEnumerable<JsonObject> stories, int offset, int limit) =>
        new(stories.Skip(offset).Take(limit).Select(WithLinks).ToArray());

    private JsonNode WithLinks(JsonObject story)
    {
        var copy = story.DeepClone().AsObject();
        var id = (int)story["id"]!;
        copy["story_links"] = new JsonArray(Links.Where(l => (int)l["subject_id"]! == id || (int)l["object_id"]! == id)
            .Select(l => (JsonNode)l.DeepClone()).ToArray());
        return copy;
    }

    private HttpResponseMessage Create(JsonObject body)
    {
        if (Interlocked.Increment(ref _storyCreates) == FailStoryCreate)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
        var id = Interlocked.Increment(ref _nextId);
        Add(id, (string?)body["group_id"] ?? "", (long)body["workflow_state_id"]!, (int?)body["epic_id"], (string)body["name"]!);
        return Ok(new JsonObject { ["id"] = id }, HttpStatusCode.Created);
    }

    private HttpResponseMessage Link(JsonObject body)
    {
        var link = new JsonObject { ["subject_id"] = body["subject_id"]!.DeepClone(), ["verb"] = body["verb"]!.DeepClone(), ["object_id"] = body["object_id"]!.DeepClone() };
        lock (_links)
        {
            _links.Add(link);
        }
        return Ok(link.DeepClone(), HttpStatusCode.Created);
    }

    private JsonNode Update(JsonObject story, JsonObject changes)
    {
        lock (story)
        {
            foreach (var (key, value) in changes)
            {
                if (key == "labels" && DropLabelsOnPut)
                {
                    continue;
                }
                story[key] = value?.DeepClone();
            }
            return story.DeepClone();
        }
    }

    private static HttpResponseMessage Ok(JsonNode? json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json?.ToJsonString() ?? "", Encoding.UTF8, "application/json") };
}
