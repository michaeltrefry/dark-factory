using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.CodeGraph;

/// <summary>
/// The owner-side client of the hosted CodeGraph's MCP endpoint (<c>mcp</c> under <c>CodeGraph:BaseUrl</c>; MCP Streamable HTTP,
/// JSON-RPC 2.0), over the <see cref="Gateway.OutboundHttp.CodeGraphApi"/> client it is given (sc-25705). Every call is a fresh MCP
/// session: <c>initialize</c>, <c>notifications/initialized</c>, then <c>tools/call</c>, the session id (<c>Mcp-Session-Id</c>) the
/// server hands out sent back on the later requests. The token (<c>CodeGraph:Token</c>) goes only in this client's
/// <c>Authorization: Bearer</c> header (E5: never in a worker's env, argv, worktree or a prompt). An answer may come as JSON or as
/// an event stream carrying the JSON-RPC response. Bounded: the client's timeout covers each request, and at most
/// <see cref="MaxAnswerBytes"/> of an answer is read.
/// <para>
/// The commit (E3): CodeGraph indexes the default branch. The commit its index describes is read from the <c>analyze_impact</c>
/// answer (a <c>lastCommitSha</c> in its structured content, or a "last commit sha" line in its text); when the answer does not
/// carry it, from CodeGraph's <c>search_projects</c> answer for the project; failing both, it is unknown (null).
/// </para>
/// </summary>
public sealed partial class CodeGraphMcpClient(HttpClient http, string token) : ICodeGraph
{
    public const string ProtocolVersion = "2025-06-18";
    public const string SessionHeader = "Mcp-Session-Id";
    public const int MaxAnswerBytes = 1024 * 1024;

    [GeneratedRegex(@"last[\s_-]*commit[\s_-]*(?:sha)?\W{0,4}([0-9a-f]{7,40})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitInText();

    public async Task<CodeGraphAnswer> AnalyzeImpactAsync(string name, int? depth, string project, CancellationToken ct)
    {
        var arguments = new JsonObject { ["name"] = name, ["project"] = project };
        if (depth is { } d)
        {
            arguments["depth"] = d;
        }
        var session = await InitializeAsync(ct);
        var (text, isError, structured) = await CallToolAsync(session, "analyze_impact", arguments, ct);
        var commit = Commit(structured, text);
        if (commit is null)
        {
            try
            {
                var (projects, projectsError, projectsStructured) = await CallToolAsync(session, "search_projects", new JsonObject { ["search"] = project }, ct);
                commit = projectsError ? null : Commit(projectsStructured, projects);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The answer stands; only its commit stays unknown.
            }
        }
        return new CodeGraphAnswer(text, isError, commit);
    }

    /// <summary>The commit a CodeGraph answer says its index describes, or null.</summary>
    public static string? Commit(JsonNode? structured, string text)
    {
        if (structured is not null && FindCommit(structured) is { } fromStructure)
        {
            return fromStructure;
        }
        return CommitInText().Match(text) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string? FindCommit(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.Equals("lastCommitSha", StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var sha)
                        && Regex.IsMatch(sha, "^[0-9a-fA-F]{7,40}$"))
                    {
                        return sha.ToLowerInvariant();
                    }
                }
                return obj.Select(p => p.Value).OfType<JsonNode>().Select(FindCommit).FirstOrDefault(c => c is not null);
            case JsonArray array:
                return array.OfType<JsonNode>().Select(FindCommit).FirstOrDefault(c => c is not null);
            default:
                return null;
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
            throw new InvalidOperationException($"CodeGraph refused to initialize an MCP session: {Cut(response?["error"]?.ToJsonString() ?? "(no answer)")}");
        }
        await SendAsync(session, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, ct);
        return session;
    }

    private async Task<(string Text, bool IsError, JsonNode? Structured)> CallToolAsync(string? session, string tool, JsonObject arguments, CancellationToken ct)
    {
        var (response, _) = await SendAsync(session, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        }, 2, ct);
        if (response?["error"] is { } error)
        {
            return ($"CodeGraph's {tool} failed: {Cut(error.ToJsonString())}", true, null);
        }
        if (response?["result"] is not JsonObject result)
        {
            throw new InvalidOperationException($"CodeGraph's {tool} answered no result.");
        }
        var text = string.Join("\n", (result["content"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>() ?? ""));
        var isError = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;
        return (text, isError, result["structuredContent"]);
    }

    /// <summary>Posts one JSON-RPC message; for a request (<paramref name="id"/> set), returns the response with that id.</summary>
    private async Task<(JsonNode? Response, string? Session)> SendAsync(string? session, JsonObject message, int? id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
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
            throw new InvalidOperationException($"CodeGraph answered {(int)response.StatusCode}: {Cut(body)}");
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
        throw new InvalidOperationException($"CodeGraph's answer holds no JSON-RPC response with id {id}: {Cut(body)}");
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

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
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
            throw new InvalidOperationException($"CodeGraph's answer is larger than {MaxAnswerBytes} bytes.");
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string Cut(string s) => s.Length > 500 ? s[..500] : s;
}
