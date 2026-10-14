using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// One recorded <c>tools/call</c> of KanbanBoard's MCP (sc-25986), normalised: the tool, its arguments as the adapter sent them,
/// and the answer without its transport — no JSON-RPC envelope, session id or framing (JSON or an event stream), and never a
/// request header (the token). <see cref="Answer"/> of a success is the one JSON document of the result's text block (the
/// contract's shape); of a refusal (<see cref="IsError"/>) its <c>structuredContent</c> <c>{code, message}</c>; of a plain MCP
/// error (<see cref="IsError"/> with no <c>structuredContent</c>) the text itself, as a JSON string. <see cref="Note"/> says so
/// when the interaction was not answered by the board (<see cref="KanbanRecordingHandler.SyntheticBusy"/>).
/// </summary>
public sealed record KanbanInteraction(string Tool, JsonObject Arguments, bool IsError, JsonNode? Answer, string? Note = null)
{
    /// <summary>The interaction of a call answered with the <c>tools/call</c> result <paramref name="result"/>.</summary>
    public static KanbanInteraction From(string tool, JsonObject arguments, JsonObject result)
    {
        var text = string.Join("\n", (result["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "text")
            .Select(c => c["text"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""));
        var isError = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;
        var answer = isError
            ? result["structuredContent"] is JsonObject structured ? structured.DeepClone() : JsonValue.Create(text)
            : KanbanMcp.TryParse(text) ?? JsonValue.Create(text);
        return new KanbanInteraction(tool, (JsonObject)arguments.DeepClone(), isError, answer);
    }

    /// <summary>
    /// The <c>tools/call</c> result this interaction stands for, as the contract frames it: one text block holding the JSON
    /// document, and for a refusal <c>isError</c> with the same <c>{code, message}</c> as <c>structuredContent</c>.
    /// </summary>
    public JsonObject ToResult()
    {
        var plain = Answer is JsonValue v && v.TryGetValue<string>(out var said) ? said : null;
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = plain ?? Answer?.ToJsonString() ?? "" }),
        };
        if (IsError)
        {
            if (Answer is JsonObject structured)
            {
                result["structuredContent"] = structured.DeepClone();
            }
            result["isError"] = true;
        }
        return result;
    }
}

/// <summary>
/// A recorded conversation with KanbanBoard's MCP (<c>Fixtures/kanban</c>, sc-25986): <see cref="Parameters"/> carries the board
/// ids the scenario used (the throwaway project, its items), <see cref="Interactions"/> every <c>tools/call</c> in order.
/// <see cref="RecordedFrom"/> says what answered: <see cref="Hosted"/>, or — until the epic's terminal story re-records them —
/// <see cref="Provisional"/>, the in-repo contract-enforcing fake (<see cref="FakeKanbanBoard"/>).
/// </summary>
public sealed class KanbanFixture
{
    /// <summary>A fixture recorded against the in-repo fake: stands in until one <c>KANBAN_RECORD=1</c> run replaces it.</summary>
    public const string Provisional = "in-repo fake (provisional; re-record against the hosted instance)";

    /// <summary>A fixture recorded against a real KanbanBoard (<c>KANBAN_RECORD=1</c>).</summary>
    public const string Hosted = "hosted KanbanBoard";

    /// <summary>Set to <c>1</c>, every committed fixture must be <see cref="Hosted"/> (the epic's terminal story sets it).</summary>
    public const string RequireHostedVariable = "KANBAN_FIXTURES_REQUIRE_HOSTED";

    /// <summary>The fixtures, one per scenario of <c>KanbanContractTests.Scenarios</c>.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["list-ready.json", "lifecycle.json", "read-spec.json", "claims.json", "plan.json", "conflict.json", "busy.json"];

    [JsonPropertyName("recorded_from")]
    public string RecordedFrom { get; set; } = "";
    public Dictionary<string, JsonNode?> Parameters { get; set; } = new();
    public List<KanbanInteraction> Interactions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Readable fixtures: a timestamp's + and a quote are written as they are (nothing here is ever put into HTML).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The committed fixtures' folder in the source tree (where a recording is saved).</summary>
    public static string SourceDirectory => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Here()))!, "Fixtures", "kanban");

    private static string Here([CallerFilePath] string here = "") => here;

    /// <summary>The committed fixtures as the build copied them beside the tests.</summary>
    public static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "kanban");

    public static KanbanFixture Load(string name) => Parse(File.ReadAllText(Path.Combine(OutputDirectory, name)));

    public static KanbanFixture Parse(string json) => JsonSerializer.Deserialize<KanbanFixture>(json, Json)!;

    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";

    /// <summary>
    /// Replaces the fixture set in <paramref name="directory"/> by <paramref name="files"/> (file name → text), all of them or
    /// none: every file is written to a staging folder beside it and read back, and only then is the folder swapped in (the old
    /// one moved aside, put back if the swap fails, deleted once it succeeded). A failure before the swap leaves the old set as it
    /// was. The folder then holds exactly <paramref name="files"/>.
    /// </summary>
    public static void SaveAll(string directory, IReadOnlyDictionary<string, string> files)
    {
        directory = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(directory)!;
        var tag = $".{Path.GetFileName(directory)}-{Guid.NewGuid():N}";
        var (staging, replaced) = (Path.Combine(parent, tag + "-staging"), Path.Combine(parent, tag + "-replaced"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, text) in files)
            {
                File.WriteAllText(Path.Combine(staging, name), text);
            }
            if (files.FirstOrDefault(f => File.ReadAllText(Path.Combine(staging, f.Key)) != f.Value).Key is { } unreadable)
            {
                throw new IOException($"{unreadable} did not read back as written; no fixture was replaced.");
            }
            var had = Directory.Exists(directory);
            if (had)
            {
                Directory.Move(directory, replaced);
            }
            try
            {
                Directory.Move(staging, directory);
            }
            catch when (had)
            {
                Directory.Move(replaced, directory);
                throw;
            }
            if (had)
            {
                Directory.Delete(replaced, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary><paramref name="text"/> as it is written inside a fixture file's JSON string (what to search a file for).</summary>
    public static string Escaped(string text) => JsonSerializer.Serialize(text, Json)[1..^1];

    public Guid Id(string parameter) => Guid.Parse(Parameters[parameter]!.GetValue<string>());

    public IReadOnlyList<Guid> Ids(string parameter) => [.. Parameters[parameter]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>()))];

    public void Set(string parameter, Guid id) => Parameters[parameter] = id.ToString("D");

    public void Set(string parameter, IEnumerable<Guid> ids) => Parameters[parameter] = new JsonArray([.. ids.Select(i => (JsonNode)i.ToString("D"))]);

    /// <summary>The calls of <paramref name="tool"/>, in order.</summary>
    public List<KanbanInteraction> Calls(string tool) => Interactions.Where(i => i.Tool == tool).ToList();

    /// <summary>
    /// What is wrong with where the fixtures were recorded: one whose <see cref="RecordedFrom"/> is neither marker, and — when
    /// real recordings are demanded (<see cref="RequireHostedVariable"/>) — every one still <see cref="Provisional"/>.
    /// </summary>
    public static IReadOnlyList<string> RecordingProblems(IEnumerable<(string Name, KanbanFixture Fixture)> fixtures, bool requireHosted) =>
    [
        .. fixtures.SelectMany(f => f.Fixture.RecordedFrom switch
        {
            Hosted => Array.Empty<string>(),
            Provisional when !requireHosted => [],
            Provisional => [$"{f.Name} is still provisional (recorded from the in-repo fake) and {RequireHostedVariable}=1 demands real "
                + "recordings: run the recorder once with KANBAN_RECORD=1 (docs/acceptance.md, \"Kanban contract fixtures\")."],
            var other => [$"{f.Name}: recorded_from is '{other}', neither '{Hosted}' nor '{Provisional}'."],
        }),
    ];
}

/// <summary>How a replayed answer is framed: the adapter must read both (MCP Streamable HTTP allows either).</summary>
public enum KanbanFraming
{
    /// <summary>JSON and an event stream in turn.</summary>
    Alternate,
    Json,
    Sse,
}

/// <summary>The MCP transport details the record and replay handlers share.</summary>
public static class KanbanMcp
{
    public static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The JSON-RPC message with <paramref name="id"/> in an answer's <paramref name="body"/>, framed as JSON or — with
    /// <paramref name="mediaType"/> <c>text/event-stream</c> — as the data of one event of a stream; null when it holds none.
    /// </summary>
    public static JsonObject? Message(string body, string? mediaType, JsonNode? id)
    {
        IEnumerable<string> documents = [body];
        if (mediaType == "text/event-stream")
        {
            var events = new List<string>();
            var data = new StringBuilder();
            foreach (var line in body.Split('\n').Select(l => l.TrimEnd('\r')).Append(""))
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var value = line[5..];
                    data.Append(data.Length > 0 ? "\n" : "").Append(value.StartsWith(' ') ? value[1..] : value);
                }
                else if (line.Length == 0 && data.Length > 0)
                {
                    events.Add(data.ToString());
                    data.Clear();
                }
            }
            documents = events;
        }
        return documents.Select(TryParse).OfType<JsonObject>().FirstOrDefault(m => JsonNode.DeepEquals(m["id"], id));
    }

    /// <summary>The JSON-RPC response carrying <paramref name="result"/>, as JSON or as one event of a stream.</summary>
    public static HttpResponseMessage Respond(JsonNode? id, JsonObject result, bool sse, string? session = null)
    {
        var json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result.DeepClone() }.ToJsonString();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = sse
                ? new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream")
                : new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (session is not null)
        {
            response.Headers.Add(McpHttpClient.SessionHeader, session);
        }
        return response;
    }
}

/// <summary>
/// The identities a recording run met (sc-25986), shared by every recording handler of the run so that a name learnt in one
/// scenario is scrubbed in all of them: the names (a <c>claimant</c>, an <c>author</c>, an <c>actor</c>'s <c>name</c>), each
/// recorded as <see cref="KanbanRecordingHandler.Recorder"/>, and the ids (<c>claimant_id</c>, <c>author_id</c>, an
/// <c>actor</c>'s <c>id</c>), each recorded as a fixed placeholder — <c>f0000000-0000-0000-0000-00000000000N</c>, N counting
/// from 1 in the order the ids were first seen, so equal ids stay equal (all the adapter does with them is compare).
/// </summary>
public sealed class KanbanIdentities
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The real names learnt so far (never written to a fixture).</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>The real ids learnt so far (never written to a fixture).</summary>
    public IReadOnlyCollection<string> Ids => _ids.Keys;

    public void Learn(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && name != KanbanRecordingHandler.Recorder)
        {
            _names.Add(name);
        }
    }

    /// <summary>The placeholder <paramref name="id"/> is recorded as: the same for the same id, whatever its case.</summary>
    public string Placeholder(string id)
    {
        if (!_ids.TryGetValue(id, out var placeholder))
        {
            _ids[id] = placeholder = $"f0000000-0000-0000-0000-{_ids.Count + 1:x12}";
        }
        return placeholder;
    }

    /// <summary>
    /// <paramref name="text"/> (a refusal's message) with every mention of a name learnt (<see cref="Mention"/>) and every id
    /// learnt replaced: the longest name first.
    /// </summary>
    public string Clean(string text)
    {
        text = _names.OrderByDescending(n => n.Length).Aggregate(text, (t, name) => Mention(name).Replace(t, KanbanRecordingHandler.Recorder));
        return _ids.Aggregate(text, (t, id) => t.Replace(id.Key, id.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where a message names the identity <paramref name="name"/>: the name standing on its own, not inside a longer word,
    /// hyphenated name or marker (sc-25992). The factory's service identity is called <c>factory</c>, and a plain replace turned
    /// a claim marker <c>dark-factory:&lt;id&gt;</c> in a refusal's message into <c>dark-fixture-recorder:&lt;id&gt;</c>, a marker
    /// no caller sent. The fixture guard uses the same rule.
    /// </summary>
    public static Regex Mention(string name) => new($@"(?<![\p{{L}}\p{{N}}_-]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}_-])", RegexOptions.CultureInvariant);
}

/// <summary>The replay met a request the recording does not have (thrown to the adapter, and kept in <see cref="KanbanReplayHandler.Mismatches"/>).</summary>
public sealed class KanbanReplayException(string message) : Exception(message);

/// <summary>
/// Replays a <see cref="KanbanFixture"/> strictly, under <see cref="McpHttpClient"/> (sc-25986): each <c>tools/call</c> must be
/// the next recorded one — the same tool name and deep-equal arguments, so a tool or an argument the recording does not have, a
/// missing one, or another value fails — and is answered with what the board answered then. The handshake is normalised and
/// checked, not recorded: every request carries a bearer token; <c>initialize</c> opens a session of a fresh id, which
/// <c>notifications/initialized</c>, the call and the closing <c>DELETE</c> must carry; nothing else (a <c>tools/list</c>) is
/// expected. Answers are framed as JSON and as an event stream (<see cref="Framing"/>). A mismatch throws and is kept in
/// <see cref="Mismatches"/> — the adapter sends a write again after an error that is no typed refusal, so the first mismatch,
/// not the adapter's last exception, is what a test reports (<see cref="AssertAllReplayed"/>).
/// </summary>
public sealed class KanbanReplayHandler(KanbanFixture fixture) : HttpMessageHandler
{
    private readonly HashSet<string> _open = [];
    private int _next;
    private int _sessions;
    private int _answers;

    public KanbanFraming Framing { get; init; } = KanbanFraming.Alternate;

    public List<string> Mismatches { get; } = [];

    /// <summary>Whether every recorded call was replayed, nothing else was asked, and every session opened was closed.</summary>
    public bool AllReplayed => _next == fixture.Interactions.Count && Mismatches.Count == 0 && _open.Count == 0;

    /// <summary>Fails naming the first mismatch, or the recorded calls the adapter never made.</summary>
    public void AssertAllReplayed()
    {
        Assert.True(Mismatches.Count == 0, Mismatches.FirstOrDefault());
        Assert.True(_next == fixture.Interactions.Count,
            $"the adapter stopped after {_next} of {fixture.Interactions.Count} recorded calls; next: {Describe(fixture.Interactions.ElementAtOrDefault(_next))}");
        Assert.True(_open.Count == 0, $"{_open.Count} MCP session(s) were never closed");
    }

    private static string Describe(KanbanInteraction? call) => call is null ? "(none)" : $"{call.Tool} {call.Arguments.ToJsonString()}";

    private KanbanReplayException Mismatch(string what)
    {
        Mismatches.Add(what);
        return new KanbanReplayException(what);
    }

    private bool Sse() => Framing == KanbanFraming.Sse || (Framing == KanbanFraming.Alternate && _answers++ % 2 == 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 })
        {
            throw Mismatch($"{request.Method} carries no bearer token");
        }
        var session = request.Headers.TryGetValues(McpHttpClient.SessionHeader, out var values) ? values.FirstOrDefault() : null;
        if (request.Method == HttpMethod.Delete)
        {
            if (session is null || !_open.Remove(session))
            {
                throw Mismatch($"DELETE closes a session that is not open ('{session}')");
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.Method != HttpMethod.Post || request.Content is null)
        {
            throw Mismatch($"unexpected {request.Method} request");
        }
        var message = KanbanMcp.TryParse(await request.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw Mismatch("a POST that is no JSON-RPC message");
        var method = message["method"]?.GetValue<string>();
        if (method == "initialize")
        {
            if (session is not null)
            {
                throw Mismatch("initialize was sent inside a session");
            }
            var opened = $"replay-session-{++_sessions}";
            _open.Add(opened);
            return KanbanMcp.Respond(message["id"], new JsonObject
            {
                ["protocolVersion"] = McpHttpClient.ProtocolVersion, ["capabilities"] = new JsonObject(),
            }, Sse(), opened);
        }
        if (session is null || !_open.Contains(session))
        {
            throw Mismatch($"{method} was sent outside an open session ('{session}')");
        }
        switch (method)
        {
            case "notifications/initialized":
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            case "tools/call":
                var tool = message["params"]?["name"]?.GetValue<string>() ?? "";
                var arguments = message["params"]?["arguments"] as JsonObject ?? new JsonObject();
                if (_next >= fixture.Interactions.Count)
                {
                    throw Mismatch($"unexpected extra call {tool} {arguments.ToJsonString()} after the {_next} recorded ones");
                }
                var expected = fixture.Interactions[_next];
                if (expected.Tool != tool || !JsonNode.DeepEquals(expected.Arguments, arguments))
                {
                    throw Mismatch($"call {_next + 1} differs from the recording.\nexpected: {Describe(expected)}\nactual:   {tool} {arguments.ToJsonString()}");
                }
                _next++;
                return KanbanMcp.Respond(message["id"], expected.ToResult(), Sse());
            default:
                throw Mismatch($"unexpected MCP method '{method}'");
        }
    }
}

/// <summary>
/// Records what the adapter asks of a KanbanBoard under <see cref="McpHttpClient"/> (sc-25986): forwards every request to
/// <see cref="DelegatingHandler.InnerHandler"/> and adds each <c>tools/call</c> to the fixture as a <see cref="KanbanInteraction"/>
/// — normalised (no envelope, session or framing; the answer read from JSON or from an event stream) and scrubbed — then hands
/// the adapter that same normalised answer, so the recording and its replay see the same board. No request header is ever read
/// into the fixture: the token is not recorded. The repository is public, so only the recording's own throwaways are kept
/// (<see cref="OwnProjects"/>, <see cref="OwnEpics"/>): a listing is cut to them (<see cref="Trim"/>), an answer about anything
/// else is refused (thrown, not recorded), an identity's name is replaced by <see cref="Recorder"/>, its id by a fixed
/// placeholder (<see cref="Identities"/>, shared by the run's handlers) and an e-mail address by a placeholder. An answer that
/// is no <c>tools/call</c> result (an HTTP error, a JSON-RPC error) is passed on unrecorded.
/// </summary>
public sealed partial class KanbanRecordingHandler(KanbanFixture fixture) : DelegatingHandler
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    /// <summary>What an identity's name (a claimant, a comment's author) is recorded as.</summary>
    public const string Recorder = "fixture-recorder";

    /// <summary>What marks an interaction the board did not answer (<see cref="SyntheticBusy"/>).</summary>
    public const string SyntheticBusyNote =
        "synthetic: a busy refusal authored from the contract's Error shape (docs/mcp-contract.md of KanbanBoard, \"Errors\") by the "
        + "recording handler, which did not forward this call — the board answers busy only after a write waited 10 s for other "
        + "writers, which no single caller can provoke on demand. The adapter's resend, next, is the board's own answer.";

    /// <summary>What an e-mail address is recorded as.</summary>
    public const string RedactedEmail = "redacted@example.com";

    /// <summary>The identities of the run: one set for all its handlers, so a name one scenario learnt is scrubbed in every other.</summary>
    public KanbanIdentities Identities { get; init; } = new();

    /// <summary>The recording's own throwaway projects: all a recorded <c>list_projects</c> or work item listing keeps.</summary>
    public HashSet<Guid> OwnProjects { get; init; } = [];

    /// <summary>The recording's own throwaway epics: all a recorded <c>list_epic_documents</c> keeps.</summary>
    public HashSet<Guid> OwnEpics { get; init; } = [];

    /// <summary>Runs before a <c>tools/call</c> is forwarded: the recorder's chance to change the board under the adapter (a real <c>conflict</c>).</summary>
    public Func<string, JsonObject, Task>? BeforeCall { get; set; }

    /// <summary>
    /// When true for a call, it is not forwarded: the adapter is answered a <c>busy</c> refusal in the contract's shape, recorded
    /// with <see cref="SyntheticBusyNote"/>. Asked once per call.
    /// </summary>
    public Func<string, JsonObject, bool>? SyntheticBusy { get; set; }

    /// <summary>The <c>busy</c> refusal of the contract for <paramref name="tool"/>, marked as not the board's own answer.</summary>
    public static KanbanInteraction BusyRefusal(string tool, JsonObject arguments) => new(tool, (JsonObject)arguments.DeepClone(), true,
        new JsonObject { ["code"] = "busy", ["message"] = $"{tool} waited 10 seconds for other writers; retry." }, SyntheticBusyNote);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var message = request.Method == HttpMethod.Post && request.Content is not null
            ? KanbanMcp.TryParse(await request.Content.ReadAsStringAsync(ct)) as JsonObject
            : null;
        if (message?["method"]?.GetValue<string>() != "tools/call")
        {
            return await base.SendAsync(request, ct);
        }
        var tool = message["params"]!["name"]!.GetValue<string>();
        var arguments = message["params"]!["arguments"] as JsonObject ?? new JsonObject();
        if (SyntheticBusy?.Invoke(tool, arguments) == true)
        {
            var busy = BusyRefusal(tool, arguments);
            fixture.Interactions.Add(busy);
            return KanbanMcp.Respond(message["id"], busy.ToResult(), sse: false);
        }
        if (BeforeCall is not null)
        {
            await BeforeCall(tool, arguments);
        }
        using var response = await base.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || KanbanMcp.Message(body, mediaType, message["id"])?["result"] is not JsonObject result)
        {
            // No tool result (an HTTP error, a JSON-RPC error): passed on as it came, and not recorded.
            return new HttpResponseMessage(response.StatusCode) { Content = new StringContent(body, Encoding.UTF8, mediaType ?? "application/json") };
        }
        var interaction = Scrub(KanbanInteraction.From(tool, arguments, result));
        fixture.Interactions.Add(interaction);
        var session = response.Headers.TryGetValues(McpHttpClient.SessionHeader, out var values) ? values.FirstOrDefault() : null;
        return KanbanMcp.Respond(message["id"], interaction.ToResult(), sse: false, session);
    }

    /// <summary>
    /// Keeps only what the factory may publish: the recording's own throwaways, no identity's name or id, no e-mail address.
    /// It works on the answer's nodes, never on its JSON text (where <c>+</c>, <c>&lt;</c> and non-ASCII are escapes a regex
    /// would cut through).
    /// </summary>
    private KanbanInteraction Scrub(KanbanInteraction call)
    {
        var answer = call.Answer?.DeepClone();
        if (!call.IsError)
        {
            answer = Trim(call.Tool, answer, OwnProjects, OwnEpics);
        }
        Anonymise(answer);
        if (call.IsError)
        {
            // A refusal's message (or a plain error's text) may name whoever holds the item.
            if (answer is JsonObject error && error["message"] is JsonValue m && m.TryGetValue<string>(out var said))
            {
                error["message"] = Identities.Clean(said);
            }
            else if (answer is JsonValue v && v.TryGetValue<string>(out var plain))
            {
                answer = JsonValue.Create(Identities.Clean(plain));
            }
        }
        return call with { Answer = WithoutEmails(answer) };
    }

    /// <summary><paramref name="node"/> with every e-mail address in a string value replaced by <see cref="RedactedEmail"/>.</summary>
    private static JsonNode? WithoutEmails(JsonNode? node)
    {
        static JsonNode? Clean(JsonNode? value) =>
            value is JsonValue v && v.TryGetValue<string>(out var text) && Email().IsMatch(text) ? JsonValue.Create(Email().Replace(text, RedactedEmail)) : null;
        switch (node)
        {
            case JsonValue:
                return Clean(node) ?? node;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Clean(array[i]) is { } cleaned)
                    {
                        array[i] = cleaned;
                    }
                    else
                    {
                        WithoutEmails(array[i]);
                    }
                }
                break;
            case JsonObject o:
                foreach (var (field, value) in o.ToList())
                {
                    if (Clean(value) is { } cleaned)
                    {
                        o[field] = cleaned;
                    }
                    else
                    {
                        WithoutEmails(value);
                    }
                }
                break;
        }
        return node;
    }

    /// <summary>
    /// Replaces every identity the contract's answers carry: a name (a work item's <c>claimant</c>, a comment's <c>author</c>, a
    /// change's <c>actor.name</c>) by <see cref="Recorder"/>, and an id (<c>claimant_id</c>, <c>author_id</c>, <c>actor.id</c>)
    /// by its placeholder (<see cref="KanbanIdentities.Placeholder"/>). The contract's shapes carry no other identity (no
    /// creator, owner, e-mail or avatar on a project, epic, document or work item).
    /// </summary>
    private void Anonymise(JsonNode? node)
    {
        void Name(JsonObject o, string field)
        {
            if (o[field] is JsonValue v && v.TryGetValue<string>(out var name))
            {
                Identities.Learn(name);
                o[field] = Recorder;
            }
        }
        void Id(JsonObject o, string field)
        {
            if (o[field] is JsonValue v && v.TryGetValue<string>(out var id))
            {
                o[field] = Identities.Placeholder(id);
            }
        }
        switch (node)
        {
            case JsonArray array:
                foreach (var entry in array)
                {
                    Anonymise(entry);
                }
                break;
            case JsonObject o:
                Name(o, "claimant");
                Name(o, "author");
                Id(o, "claimant_id");
                Id(o, "author_id");
                if (o["actor"] is JsonObject actor)
                {
                    Name(actor, "name");
                    Id(actor, "id");
                }
                foreach (var child in o.Select(p => p.Value).ToList())
                {
                    Anonymise(child);
                }
                break;
        }
    }

    /// <summary>
    /// <paramref name="answer"/> of a successful <paramref name="tool"/> call with nothing of the board's but the recording's
    /// own: <c>list_projects</c> keeps the projects in <paramref name="projects"/>, <c>list_work_items</c> (an array, or a page's
    /// <c>items</c>) the items of those projects, <c>list_epic_documents</c> the documents of the epics in
    /// <paramref name="epics"/>, each in the listing's order. Any other answer that names a project (a work item, an epic) must
    /// name one of <paramref name="projects"/>: an answer about someone else's object is not trimmed but refused.
    /// </summary>
    public static JsonNode? Trim(string tool, JsonNode? answer, IReadOnlySet<Guid> projects, IReadOnlySet<Guid> epics)
    {
        static bool In(JsonNode? entry, string field, IReadOnlySet<Guid> own) =>
            entry is JsonObject o && o[field] is JsonValue v && v.TryGetValue<string>(out var id) && Guid.TryParse(id, out var guid) && own.Contains(guid);
        static JsonArray Kept(JsonArray list, string field, IReadOnlySet<Guid> own) =>
            new([.. list.Where(e => In(e, field, own)).Select(e => e!.DeepClone())]);
        switch (tool, answer)
        {
            case ("list_changes", _):
                // The change feed names other identities' edits field by field (old and new claimants, comment authors).
                throw new InvalidOperationException("list_changes answers the board's change feed, which the recording handler does not scrub; it is not recorded.");
            case ("list_projects", JsonArray list):
                return Kept(list, "id", projects);
            case ("list_work_items", JsonArray list):
                return Kept(list, "projectId", projects);
            case ("list_work_items", JsonObject page) when page["items"] is JsonArray items:
                var trimmed = (JsonObject)page.DeepClone();
                trimmed["items"] = Kept(items, "projectId", projects);
                return trimmed;
            case ("list_epic_documents", JsonArray list):
                return Kept(list, "epicId", epics);
            case (_, JsonObject o) when o.ContainsKey("projectId") && !In(o, "projectId", projects):
                throw new InvalidOperationException($"{tool} answered an object of a project that is not the recording's own; it is not recorded.");
            case (_, JsonArray):
                throw new InvalidOperationException($"{tool} answered a listing the recording handler does not know how to trim; it is not recorded.");
            default:
                return answer;
        }
    }
}

/// <summary>
/// What may not be in a committed Kanban fixture (sc-25986; the repository is public): a token — an <c>Authorization</c>
/// header's name, a bearer value, a string shaped like a KanbanBoard personal access token (<c>kbp_…</c>), a JSON web token or
/// the fake board's own tokens —, an e-mail address, an identity's name (in a <c>claimant</c>, an <c>author</c>, an
/// <c>actor</c>, or — a name the set or the recording run knows — in a refusal's message), an identity's id that is not the
/// recorder's placeholder (<c>claimant_id</c>, <c>author_id</c>, <c>actor.id</c>), and anything of the board that is not the recorder's
/// own throwaway: a work item, epic, document or project of another project than the fixture's (<c>parameters.project</c>), or
/// one whose title or name does not start with <see cref="Prefix"/> (a project's key with <see cref="KeyPrefix"/>), and a
/// listing longer than <see cref="MaxListed"/>.
/// </summary>
public static partial class KanbanFixtureGuard
{
    /// <summary>What every throwaway's title or name starts with.</summary>
    public const string Prefix = "[dark-factory fixture]";

    /// <summary>What a throwaway project's key starts with.</summary>
    public const string KeyPrefix = "DFFIX";

    /// <summary>The most entries any recorded array may hold.</summary>
    public const int MaxListed = 20;

    [GeneratedRegex(@"authorization|bearer\s+[A-Za-z0-9._~+/=-]{6,}|\bkbp_[A-Za-z0-9_-]{6,}|\bkb_[a-z]+_token\b|\beyJ[A-Za-z0-9_-]{10,}\.", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@(?!example\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex("^f0000000-0000-0000-0000-[0-9a-f]{12}$")]
    private static partial Regex PlaceholderId();

    [GeneratedRegex("^DFFIX[0-9A-F]{8}$")]
    private static partial Regex RecorderKey();

    /// <summary>The one name the recorder gives its throwaway project.</summary>
    public const string ProjectName = Prefix + " contract recording";

    /// <summary>Whether <paramref name="id"/> is what an identity's id is recorded as (<see cref="KanbanIdentities.Placeholder"/>).</summary>
    public static bool IsPlaceholderId(string id) => PlaceholderId().IsMatch(id);

    /// <summary>Whether <paramref name="key"/> is a key the recorder gives its project: <see cref="KeyPrefix"/> and 8 upper-case hex digits.</summary>
    public static bool IsRecorderKey(string key) => RecorderKey().IsMatch(key);

    /// <summary>
    /// Every problem of a fixture set (file name and text of each): <see cref="Problems"/> of each fixture, with the names that
    /// no refusal's message may hold — the real names and ids a recording run learnt (<paramref name="known"/>; none for the
    /// committed set, whose recording is long over) and every <c>claimant</c>/<c>author</c> other than
    /// <paramref name="allowed"/> that any fixture of the set holds.
    /// </summary>
    public static IReadOnlyList<string> SetProblems(IReadOnlyCollection<(string Name, string Json)> set, KanbanIdentities? known = null,
        string allowed = KanbanRecordingHandler.Recorder)
    {
        var names = new HashSet<string>(known?.Names ?? [], StringComparer.Ordinal);
        foreach (var (_, json) in set)
        {
            Names(KanbanMcp.TryParse(json), names);
        }
        names.Remove(allowed);
        var ids = known?.Ids ?? [];
        return
        [
            .. set.SelectMany(f => Problems(f.Name, f.Json, names, allowed)
                .Concat(ids.Any(id => f.Json.Contains(id, StringComparison.OrdinalIgnoreCase)) ? [$"{f.Name} holds the id of an identity the recording met"] : [])),
        ];

        static void Names(JsonNode? node, HashSet<string> into)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var entry in array)
                    {
                        Names(entry, into);
                    }
                    break;
                case JsonObject o:
                    foreach (var (field, value) in o)
                    {
                        if (field is "claimant" or "author" && value is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0)
                        {
                            into.Add(name);
                        }
                        Names(value, into);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Every problem of the fixture <paramref name="name"/> whose file holds <paramref name="json"/>; empty when it may be
    /// committed. <paramref name="identities"/>: names no refusal's message (and no plain error's text) may hold;
    /// <paramref name="allowed"/>: the one name a <c>claimant</c> or <c>author</c> may be.
    /// </summary>
    public static IReadOnlyList<string> Problems(string name, string json, IReadOnlyCollection<string>? identities = null,
        string allowed = KanbanRecordingHandler.Recorder)
    {
        identities ??= [];
        var problems = new List<string>();
        if (Token().Match(json) is { Success: true } token)
        {
            // Where, never what: a token is not repeated into a test's output.
            problems.Add($"{name} holds what looks like a token or its header (at character {token.Index})");
        }
        if (Email().IsMatch(json))
        {
            problems.Add($"{name} holds an e-mail address");
        }
        KanbanFixture fixture;
        try
        {
            fixture = KanbanFixture.Parse(json);
        }
        catch (JsonException ex)
        {
            return [.. problems, $"{name} is not a Kanban fixture: {ex.Message}"];
        }
        if (fixture.Parameters.GetValueOrDefault("project") is not JsonValue p || !p.TryGetValue<string>(out var id) || !Guid.TryParse(id, out var project))
        {
            return [.. problems, $"{name} names no throwaway project (parameters.project)"];
        }
        for (var i = 0; i < fixture.Interactions.Count; i++)
        {
            var call = fixture.Interactions[i];
            var where = $"{name}, call {i + 1} ({call.Tool})";
            Walk(call.Arguments, $"{where} arguments", project, allowed, problems);
            Walk(call.Answer, $"{where} answer", project, allowed, problems);
            // What a refusal says, for people: its message, or a plain error's text. It may name whoever holds the item.
            var said = !call.IsError ? null
                : call.Answer is JsonObject error ? error["message"] is JsonValue m && m.TryGetValue<string>(out var message) ? message : null
                : call.Answer is JsonValue v && v.TryGetValue<string>(out var plain) ? plain : null;
            if (said is not null && identities.FirstOrDefault(n => n != allowed && KanbanIdentities.Mention(n).IsMatch(said)) is { } named)
            {
                problems.Add($"{where} answer's message names an identity ('{Cut(named)}')");
            }
        }
        return problems;
    }

    private static void Walk(JsonNode? node, string where, Guid project, string allowed, List<string> problems)
    {
        switch (node)
        {
            case JsonArray array:
                if (array.Count > MaxListed)
                {
                    problems.Add($"{where} holds a listing of {array.Count} entries (at most {MaxListed}): trim the recording");
                }
                foreach (var entry in array)
                {
                    Walk(entry, where, project, allowed, problems);
                }
                break;
            case JsonObject o:
                foreach (var (field, value) in o)
                {
                    if (field == "actor" && value is JsonObject actor)
                    {
                        if (actor["name"] is JsonValue an && (!an.TryGetValue<string>(out var actorName) || actorName != allowed))
                        {
                            problems.Add($"{where} holds an identity's name in actor.name");
                        }
                        if (actor["id"] is JsonValue ai && (!ai.TryGetValue<string>(out var actorId) || !IsPlaceholderId(actorId)))
                        {
                            problems.Add($"{where} holds an identity's id in actor.id, not the recorder's placeholder");
                        }
                        continue;
                    }
                    if (value is JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        switch (field)
                        {
                            case "projectId" or "project_id" when !Guid.TryParse(text, out var named) || named != project:
                                problems.Add($"{where} names the project {Cut(text)}, not the recorder's own throwaway {project:D}");
                                break;
                            case "title" or "name" when !text.StartsWith(Prefix, StringComparison.Ordinal):
                                problems.Add($"{where} holds '{Cut(text)}', which is not the recorder's own throwaway (no '{Prefix}' {field})");
                                break;
                            case "key" when !text.StartsWith(KeyPrefix, StringComparison.Ordinal):
                                problems.Add($"{where} holds the project key '{Cut(text)}', not a throwaway's ('{KeyPrefix}…')");
                                break;
                            case "claimant" or "author" when text != allowed:
                                problems.Add($"{where} holds an identity's name in {field} ('{Cut(text)}')");
                                break;
                            case "claimant_id" or "author_id" when !IsPlaceholderId(text):
                                problems.Add($"{where} holds an identity's id in {field}, not the recorder's placeholder (f0000000-0000-0000-0000-…)");
                                break;
                        }
                    }
                    else if (field is "claimant_id" or "author_id" or "claimant" or "author" && value is not null)
                    {
                        problems.Add($"{where} holds {field} that is neither null nor a string");
                    }
                    Walk(value, where, project, allowed, problems);
                }
                break;
        }
    }

    private static string Cut(string s) => s.Length > 24 ? s[..24] : s;
}
