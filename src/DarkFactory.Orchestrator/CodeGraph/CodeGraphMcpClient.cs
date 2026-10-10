using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.CodeGraph;

/// <summary>
/// The owner-side client of the hosted CodeGraph's MCP endpoint (<c>mcp</c> under <c>CodeGraph:BaseUrl</c>; MCP Streamable HTTP,
/// JSON-RPC 2.0), over the <see cref="Gateway.OutboundHttp.CodeGraphApi"/> client it is given (sc-25705). Every call is a fresh MCP
/// session: <c>initialize</c>, <c>notifications/initialized</c>, then <c>tools/call</c>, the session id (<c>Mcp-Session-Id</c>) the
/// server hands out sent back on the later requests, and the session ended afterwards with a best-effort <c>DELETE</c>. The token
/// (<c>CodeGraph:Token</c>) goes only in this client's <c>Authorization: Bearer</c> header (E5: never in a worker's env, argv,
/// worktree or a prompt). An answer may come as JSON or as an event stream carrying the JSON-RPC response. Bounded: the client's
/// timeout covers each request, and at most <see cref="MaxAnswerBytes"/> of an answer is read.
/// <para>
/// The commit (E3): CodeGraph indexes the default branch, and by its contract (CodeGraph sc-25702) every answer names the commit
/// its index describes: <c>structuredContent.commitSha</c>, or a first text line <c>Commit: &lt;40-hex sha&gt;</c>
/// (<see cref="Commit"/>). An answer that carries neither has an unknown commit (null); nothing else is searched for one.
/// </para>
/// <para>
/// The project (<see cref="FindProjectAsync"/>): the CodeGraph project whose listed GitHub repository URL is exactly the
/// repository's (<c>https://github.com/&lt;owner&gt;/&lt;name&gt;</c>, with or without the git suffix, case-insensitive), read from
/// <c>search_projects</c>' entries (a <c>- **Name** …</c> line, then a <c>Repo: &lt;url&gt;</c> line).
/// </para>
/// </summary>
public sealed partial class CodeGraphMcpClient(HttpClient http, string token) : ICodeGraph
{
    public const string ProtocolVersion = "2025-06-18";
    public const string SessionHeader = "Mcp-Session-Id";
    public const int MaxAnswerBytes = 1024 * 1024;

    /// <summary>How long the best-effort session close may take.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"\ACommit: ([0-9a-fA-F]{40})[ \t]*(?:\r?\n|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex CommitLine();

    [GeneratedRegex(@"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullSha();

    [GeneratedRegex(@"\A\s*-\s+\*\*(?<name>[^*\r\n]+)\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectEntry();

    [GeneratedRegex(@"\A\s*Repo:\s*(?<url>\S+)\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepoLine();

    /// <summary>
    /// Calls one of the reviewers' CodeGraph tools. A tool not on <see cref="ReviewTools.CodeGraphTools"/> is refused here too, before
    /// anything is sent (E2: no model-running CodeGraph tool is reachable through this client).
    /// </summary>
    public Task<CodeGraphAnswer> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        if (!ReviewTools.CodeGraphTools.Contains(tool))
        {
            throw new ArgumentException($"'{tool}' is not one of the reviewers' CodeGraph tools; it is never sent.", nameof(tool));
        }
        return InSessionAsync(async session =>
        {
            var (text, isError, structured) = await CallToolAsync(session, tool, arguments, ct);
            return new CodeGraphAnswer(text, isError, Commit(structured, text));
        }, ct);
    }

    public Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct) =>
        InSessionAsync(async session =>
        {
            var (text, isError, _) = await CallToolAsync(session, "search_projects", new JsonObject { ["search"] = repo.Name }, ct);
            if (isError)
            {
                throw new InvalidOperationException($"CodeGraph's search_projects answered an error: {Cut(text)}");
            }
            return ProjectFor(text, repo);
        }, ct);

    /// <summary>
    /// The commit a CodeGraph answer says its index describes (CodeGraph's contract, sc-25702): <c>structuredContent.commitSha</c>,
    /// else a first text line <c>Commit: &lt;sha&gt;</c>; a full 40-hex sha only, lowercased; otherwise null (never guessed).
    /// </summary>
    public static string? Commit(JsonNode? structured, string text)
    {
        if (structured is JsonObject obj && obj["commitSha"] is JsonValue v && v.TryGetValue<string>(out var sha) && FullSha().IsMatch(sha))
        {
            return sha.ToLowerInvariant();
        }
        return CommitLine().Match(text) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// The name of the entry of a <c>search_projects</c> listing whose own <c>Repo:</c> URL is exactly <paramref name="repo"/>'s GitHub
    /// URL (with or without the git suffix or a trailing slash, case-insensitive), or null when no entry's is.
    /// </summary>
    public static string? ProjectFor(string listing, RepoRef repo)
    {
        var wanted = RepoUrl(GitRemoteReads.GitHubRemote(repo));
        string? name = null;
        foreach (var line in listing.Split('\n'))
        {
            if (ProjectEntry().Match(line) is { Success: true } entry)
            {
                name = entry.Groups["name"].Value.Trim();
            }
            else if (name is not null && RepoLine().Match(line) is { Success: true } url
                && string.Equals(RepoUrl(url.Groups["url"].Value), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return null;
    }

    private static string RepoUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        return u.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase) ? u[..^GitSuffix.Length] : u;
    }

    private const string GitSuffix = ".git";

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
            using var request = new HttpRequestMessage(HttpMethod.Delete, "mcp");
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
            throw new InvalidOperationException($"CodeGraph refused to initialize an MCP session: {Cut(response?["error"]?.ToJsonString() ?? "(no answer)")}");
        }
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
