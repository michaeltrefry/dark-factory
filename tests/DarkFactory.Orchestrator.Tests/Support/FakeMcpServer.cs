using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A fake upstream MCP server (Streamable HTTP) at <paramref name="path"/>: initialize hands out a session, notifications are accepted,
/// <c>tools/list</c> lists <paramref name="tools"/> (each taking a <c>project</c> and a <c>name</c>), <c>tools/call</c> is answered by
/// <paramref name="answer"/> (as an event stream), and DELETE ends a session. Every request is recorded.
/// </summary>
public sealed class FakeMcpServer
{
    public FakeApi Api { get; } = new();

    public FakeMcpServer(string path, IReadOnlyList<string> tools, Func<string, JsonObject, JsonObject> answer,
        Func<string, JsonNode?>? schema = null)
    {
        Api.On($"DELETE {path}", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On($"POST {path}", r =>
            {
                var message = JsonNode.Parse(r.Body!)!;
                var id = message["id"]?.DeepClone();
                JsonObject result;
                switch (message["method"]!.GetValue<string>())
                {
                    case "initialize":
                        var init = FakeApi.Json(HttpStatusCode.OK, new JsonObject
                        {
                            ["jsonrpc"] = "2.0", ["id"] = id,
                            ["result"] = new JsonObject { ["protocolVersion"] = McpHttpClient.ProtocolVersion, ["capabilities"] = new JsonObject() },
                        }.ToJsonString());
                        init.Headers.Add(McpHttpClient.SessionHeader, "upstream-session");
                        return init;
                    case "tools/list":
                        result = new JsonObject
                        {
                            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                            {
                                ["name"] = t,
                                ["description"] = $"upstream {t}",
                                ["inputSchema"] = schema?.Invoke(t)?.DeepClone() ?? new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["project"] = new JsonObject { ["type"] = "string" },
                                        ["name"] = new JsonObject { ["type"] = "string" },
                                    },
                                },
                            }).ToArray()),
                        };
                        break;
                    case "tools/call":
                        result = answer(message["params"]!["name"]!.GetValue<string>(), message["params"]!["arguments"]!.AsObject());
                        break;
                    default:
                        return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                var json = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result.DeepClone() }.ToJsonString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream"),
                };
            });
    }

    /// <summary>The <c>tools/call</c> requests the server got: tool name and arguments.</summary>
    public List<(string Tool, JsonObject Arguments)> Calls =>
        Api.Requests.Where(r => r.Method == HttpMethod.Post)
            .Select(r => JsonNode.Parse(r.Body!)!)
            .Where(m => m["method"]!.GetValue<string>() == "tools/call")
            .Select(m => (m["params"]!["name"]!.GetValue<string>(), m["params"]!["arguments"]!.AsObject()))
            .ToList();

    public HttpClient Client(string baseAddress) => Api.Client(baseAddress);

    /// <summary>A <c>tools/call</c> result with one text block.</summary>
    public static JsonObject Text(string text) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    /// <summary>A <c>tools/call</c> result with one text block and <paramref name="structured"/> as its <c>structuredContent</c>.</summary>
    public static JsonObject Structured(string text, JsonObject structured, bool isError = false)
    {
        var result = Text(text);
        result["structuredContent"] = structured;
        if (isError)
        {
            result["isError"] = true;
        }
        return result;
    }

    // ---- CodeGraph's answers, in the shapes of its contract (sc-25708) ----

    /// <summary>
    /// A fake hosted CodeGraph at <c>/mcp</c>: <c>search_projects</c> answers <paramref name="listing"/>, every other tool
    /// <paramref name="answer"/> (overlay tools included).
    /// </summary>
    public static FakeMcpServer CodeGraph(string listing, Func<string, JsonObject, JsonObject> answer) =>
        new("/mcp", [], (tool, args) => tool == "search_projects" ? Text(listing) : answer(tool, args));

    /// <summary>
    /// A read answered for a commit (CodeGraph C4, michaeltrefry/CodeGraph PR #71, <c>CommitAnswer.Create</c>): the first text line
    /// <c>Commit: &lt;sha&gt;</c> and <c>structuredContent.commitSha</c>, as in
    /// <c>Result($"{commit.HeaderLine}\n\n{text}", structured, isError: false)</c> with <c>["commitSha"] = commit.CommitSha</c> and
    /// <c>["source"] = scope.Overlay is not null ? "overlay" : "default-branch"</c>.
    /// </summary>
    public static JsonObject CommitAnswer(string sha, string text, bool overlay = false) =>
        Structured($"Commit: {sha}\n\n{text}", new JsonObject { ["commitSha"] = sha, ["source"] = overlay ? "overlay" : "default-branch" });

    /// <summary>
    /// A commit-pinned read CodeGraph cannot answer for <paramref name="sha"/> (C4, PR #71, <c>CommitAnswer.NotIndexed</c>):
    /// <c>$"Commit: {sha}\n\n" + $"Not indexed ({code}, status: {status}): {message}"</c>, <c>["commitSha"] = sha</c>,
    /// <c>["notIndexed"]</c> = <c>CommitNotIndexedResponse(Code, Repo, Sha, Status, Message, OverlayId, BaseSha)</c> in camelCase, and
    /// <c>isError: notIndexed.Code != NotIndexedCode</c> (so <c>commit_not_indexed</c> is not an error). It names the SHA asked
    /// about, yet carries no data.
    /// </summary>
    public static JsonObject NotIndexed(string repo, string sha, string status, long? overlayId = null, string? baseSha = null,
        string code = "commit_not_indexed", string message = "No ready overlay for this commit.") =>
        Structured($"Commit: {sha}\n\nNot indexed ({code}, status: {status}): {message}", new JsonObject
        {
            ["commitSha"] = sha,
            ["notIndexed"] = new JsonObject
            {
                ["code"] = code, ["repo"] = repo, ["sha"] = sha, ["status"] = status, ["message"] = message,
                ["overlayId"] = overlayId, ["baseSha"] = baseSha,
            },
        }, isError: code != "commit_not_indexed");

    /// <summary>
    /// An overlay tool's answer (CodeGraph C6, sc-25726: <c>request_overlay</c> / <c>get_overlay_status</c> answer in
    /// <c>structuredContent</c> <c>overlayId</c>, <c>status</c> (queued | indexing | ready | failed | expired), <c>headSha</c>,
    /// <c>baseSha</c>; field names as C3's <c>BranchOverlayResponse</c>, PR #67, serialized camelCase).
    /// </summary>
    public static JsonObject Overlay(long overlayId, string status, string? headSha, string? baseSha, string? error = null) =>
        Structured($"Overlay {overlayId}: {status}", new JsonObject
        {
            ["overlayId"] = overlayId, ["status"] = status, ["headSha"] = headSha, ["baseSha"] = baseSha, ["error"] = error,
        });
}
