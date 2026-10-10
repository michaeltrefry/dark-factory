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

    public FakeMcpServer(string path, IReadOnlyList<string> tools, Func<string, JsonObject, JsonObject> answer)
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
                                ["inputSchema"] = new JsonObject
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
}
