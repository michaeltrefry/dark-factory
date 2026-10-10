using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>A tool as an MCP server's <c>tools/list</c> describes it: its name, description and input schema.</summary>
public sealed record McpToolInfo(string Name, string? Description, JsonObject InputSchema);

/// <summary>
/// One <c>tools/call</c> answer: its text content (the text blocks joined), whether the server said it is an error, its
/// <c>structuredContent</c>, and the whole <c>result</c> object as the server sent it.
/// </summary>
public sealed record McpToolResult(string Text, bool IsError, JsonNode? Structured, JsonObject Result);

/// <summary>
/// An owner-side client of one upstream MCP server (MCP Streamable HTTP, JSON-RPC 2.0; sc-25705, generalised in sc-25707) over the
/// <see cref="Gateway.OutboundHttp"/> client it is given, posting to <paramref name="endpoint"/> (relative to the client's base
/// address; empty: the base address itself). Every request is a fresh MCP session: <c>initialize</c>,
/// <c>notifications/initialized</c>, then the request, the session id (<c>Mcp-Session-Id</c>) the server hands out sent back on the
/// later requests, and the session ended afterwards with a best-effort <c>DELETE</c>. The token goes only in this client's
/// <c>Authorization: Bearer</c> header (E5: never in a worker's env, argv, worktree, prompt or transcript). An answer may come as
/// JSON or as an event stream carrying the JSON-RPC response. Bounded: the client's timeout covers each request, and at most
/// <see cref="MaxAnswerBytes"/> of an answer is read. This client sends whatever tool it is asked to: the allowlist is
/// <see cref="McpUpstream"/>'s (and <see cref="CodeGraph.CodeGraphMcpClient"/>'s), which is all the rest of the factory sees.
/// </summary>
public sealed class McpHttpClient(HttpClient http, string token, string service, string endpoint)
{
    public const string ProtocolVersion = "2025-06-18";
    public const string SessionHeader = "Mcp-Session-Id";
    public const int MaxAnswerBytes = 1024 * 1024;

    /// <summary>How long the best-effort session close may take.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The service's name, as errors name it (CodeGraph, Kanban).</summary>
    public string Service => service;

    /// <summary>Calls <paramref name="tool"/> with <paramref name="arguments"/> in a fresh session.</summary>
    public Task<McpToolResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct) =>
        InSessionAsync(session => CallInSessionAsync(session, tool, arguments, ct), ct);

    /// <summary>The server's tools (every page of <c>tools/list</c>, at most <see cref="MaxListPages"/>), in a fresh session.</summary>
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
        InSessionAsync<IReadOnlyList<McpToolInfo>>(async session =>
        {
            var tools = new List<McpToolInfo>();
            string? cursor = null;
            for (var page = 0; page < MaxListPages; page++)
            {
                var parameters = new JsonObject();
                if (cursor is not null)
                {
                    parameters["cursor"] = cursor;
                }
                var (response, _) = await SendAsync(session, new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list", ["params"] = parameters,
                }, 2, ct);
                if (response?["result"] is not JsonObject result)
                {
                    throw new InvalidOperationException($"{service}'s tools/list failed: {Cut(response?["error"]?.ToJsonString() ?? "(no result)")}");
                }
                foreach (var tool in (result["tools"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (tool["name"] is JsonValue n && n.TryGetValue<string>(out var name))
                    {
                        var description = tool["description"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null;
                        var schema = tool["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
                        tools.Add(new McpToolInfo(name, description, (JsonObject)schema.DeepClone()));
                    }
                }
                cursor = result["nextCursor"] is JsonValue c && c.TryGetValue<string>(out var next) && next.Length > 0 ? next : null;
                if (cursor is null)
                {
                    break;
                }
            }
            return tools;
        }, ct);

    /// <summary>The most <c>tools/list</c> pages read.</summary>
    public const int MaxListPages = 10;

    /// <summary>Runs <paramref name="work"/> in a fresh MCP session, ended afterwards whatever happens.</summary>
    private async Task<T> InSessionAsync<T>(Func<string?, Task<T>> work, CancellationToken ct)
    {
        var session = await InitializeAsync(ct);
        try
        {
            await SendAsync(session, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, ct);
            return await work(session);
        }
        finally
        {
            await CloseAsync(session);
        }
    }

    /// <summary>Ends the MCP session (<c>DELETE</c> with its id). Best effort: a server that refuses or is gone changes nothing.</summary>
    private async Task CloseAsync(string? session)
    {
        if (session is null)
        {
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add(SessionHeader, session);
            request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
            using var response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception)
        {
            // Best effort: the server expires the session anyway.
        }
    }

    private async Task<string?> InitializeAsync(CancellationToken ct)
    {
        var (response, session) = await SendAsync(null, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "dark-factory", ["version"] = "1" },
            },
        }, 1, ct);
        if (response?["result"] is null)
        {
            await CloseAsync(session);
            throw new InvalidOperationException($"{service} refused to initialize an MCP session: {Cut(response?["error"]?.ToJsonString() ?? "(no answer)")}");
        }
        return session;
    }

    private async Task<McpToolResult> CallInSessionAsync(string? session, string tool, JsonObject arguments, CancellationToken ct)
    {
        var (response, _) = await SendAsync(session, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() },
        }, 2, ct);
        if (response?["error"] is { } error)
        {
            var text = $"{service}'s {tool} failed: {Cut(error.ToJsonString())}";
            return new McpToolResult(text, true, null, ErrorResult(text));
        }
        if (response?["result"] is not JsonObject result)
        {
            throw new InvalidOperationException($"{service}'s {tool} answered no result.");
        }
        var joined = string.Join("\n", (result["content"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => c["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "text")
            .Select(c => c["text"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : ""));
        var isError = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;
        return new McpToolResult(joined, isError, result["structuredContent"], (JsonObject)result.DeepClone());
    }

    /// <summary>A <c>tools/call</c> result holding one text block and <c>isError: true</c>.</summary>
    public static JsonObject ErrorResult(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true,
    };

    /// <summary>Posts one JSON-RPC message; for a request (<paramref name="id"/> set), returns the response with that id.</summary>
    private async Task<(JsonNode? Response, string? Session)> SendAsync(string? session, JsonObject message, int? id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session is not null)
        {
            request.Headers.Add(SessionHeader, session);
            request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await ReadBoundedAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpHttpException(service, (int)response.StatusCode, ErrorCode(body), Cut(body));
        }
        var newSession = response.Headers.TryGetValues(SessionHeader, out var values) ? values.FirstOrDefault() : session;
        if (id is null)
        {
            return (null, newSession);
        }
        var messages = response.Content.Headers.ContentType?.MediaType == "text/event-stream" ? EventData(body) : [body];
        foreach (var data in messages)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }
            if (node is JsonObject obj && obj["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
            {
                return (obj, newSession);
            }
        }
        throw new InvalidOperationException($"{service}'s answer holds no JSON-RPC response with id {id}: {Cut(body)}");
    }

    /// <summary>
    /// The error code of a refusal's JSON body, or null: a string <c>error</c> (CodeGraph's middleware refusals,
    /// <c>WriteAsJsonAsync(new { error = code, message })</c>, e.g. <c>tool_not_entitled</c> with HTTP 403) or an object's <c>code</c>.
    /// </summary>
    private static string? ErrorCode(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject o
                ? o["error"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out var code) => code,
                    JsonObject e when e["code"] is JsonValue c && c.TryGetValue<string>(out var code) => code,
                    _ => null,
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The data of each event of a server-sent-event body.</summary>
    private static IEnumerable<string> EventData(string body)
    {
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
                yield return data.ToString();
                data.Clear();
            }
        }
    }

    private async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxAnswerBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }
        if (total > MaxAnswerBytes)
        {
            throw new InvalidOperationException($"{service}'s answer is larger than {MaxAnswerBytes} bytes.");
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string Cut(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>
/// An MCP server answered a request with a non-success HTTP status (sc-25708): the status and, when its JSON body names one, the
/// error code (e.g. CodeGraph's <c>tool_not_entitled</c> 403 for a token not entitled to the tool it called).
/// </summary>
public sealed class McpHttpException(string service, int statusCode, string? code, string body)
    : InvalidOperationException($"{service} answered {statusCode}: {body}")
{
    /// <summary>CodeGraph's code for a token not entitled to the tool it called (its <c>McpToolEntitlementMiddleware</c>).</summary>
    public const string NotEntitled = "tool_not_entitled";

    public int StatusCode { get; } = statusCode;

    public string? Code { get; } = code;
}
