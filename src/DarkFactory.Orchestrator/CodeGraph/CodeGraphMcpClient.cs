using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.CodeGraph;

/// <summary>
/// The owner-side client of the hosted CodeGraph's MCP endpoint (<c>mcp</c> under <c>CodeGraph:BaseUrl</c>), over the generic
/// <see cref="McpHttpClient"/> (sc-25705; generalised in sc-25707): one MCP session per request, the token (<c>CodeGraph:Token</c>)
/// only in its <c>Authorization: Bearer</c> header (E5: never in a worker's env, argv, worktree or a prompt). Its
/// <see cref="Upstream"/> is CodeGraph as the upstream registry (<see cref="McpUpstreams"/>) holds it: the allowlist
/// <see cref="ReviewTools.CodeGraphTools"/>, repo-scoped.
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
/// <para>
/// Overlays (sc-25708, <see cref="ICodeGraphOverlays"/>; CodeGraph sc-25726): <see cref="RequestOverlayTool"/> and
/// <see cref="OverlayStatusTool"/> are sent by the orchestrator only (<see cref="ReviewOverlays"/>), straight through the MCP client
/// like <c>search_projects</c>: they are on no allowlist, so neither <see cref="CallAsync"/>, the upstream registry nor the loopback
/// proxy sends them. A commit-pinned read (<c>sha</c>, CodeGraph C4) that CodeGraph cannot answer for that commit carries
/// <c>structuredContent.notIndexed</c> (<see cref="NotIndexedOf"/>).
/// </para>
/// </summary>
public sealed partial class CodeGraphMcpClient : ICodeGraph, ICodeGraphOverlays
{
    public const string ProtocolVersion = McpHttpClient.ProtocolVersion;
    public const string SessionHeader = McpHttpClient.SessionHeader;
    public const int MaxAnswerBytes = McpHttpClient.MaxAnswerBytes;

    private readonly McpHttpClient _client;

    public CodeGraphMcpClient(HttpClient http, string token)
    {
        _client = new McpHttpClient(http, token, "CodeGraph", "mcp");
        Upstream = new McpUpstream(McpServers.CodeGraph, _client, ReviewTools.CodeGraphTools, repoScoped: true);
    }

    /// <summary>CodeGraph as an upstream: its allowlisted tools only, repo-scoped.</summary>
    public McpUpstream Upstream { get; }

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
    public async Task<CodeGraphAnswer> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        if (!ReviewTools.CodeGraphTools.Contains(tool))
        {
            throw new ArgumentException($"'{tool}' is not one of the reviewers' CodeGraph tools; it is never sent.", nameof(tool));
        }
        var answer = await Upstream.CallAsync(tool, arguments, ct);
        return new CodeGraphAnswer(answer.Text, answer.IsError, Commit(answer.Structured, answer.Text), NotIndexedOf(answer.Structured));
    }

    /// <summary>CodeGraph's tool that creates (or returns the live) overlay of a repository at a ref: orchestrator only.</summary>
    public const string RequestOverlayTool = "request_overlay";

    /// <summary>CodeGraph's tool that reads an overlay's status: orchestrator only.</summary>
    public const string OverlayStatusTool = "get_overlay_status";

    /// <summary>The overlay tools, which no model-facing surface may list or forward.</summary>
    public static readonly IReadOnlyList<string> OverlayTools = [RequestOverlayTool, OverlayStatusTool];

    /// <summary><c>request_overlay(repo, ref)</c>: <paramref name="project"/> is the CodeGraph repository name (<see cref="FindProjectAsync"/>).</summary>
    public async Task<OverlayAnswer> RequestOverlayAsync(string project, string gitRef, CancellationToken ct) =>
        Overlay(await _client.CallToolAsync(RequestOverlayTool, new JsonObject { ["repo"] = project, ["ref"] = gitRef }, ct));

    /// <summary><c>get_overlay_status(overlayId)</c>.</summary>
    public async Task<OverlayAnswer> OverlayStatusAsync(long overlayId, CancellationToken ct) =>
        Overlay(await _client.CallToolAsync(OverlayStatusTool, new JsonObject { ["overlayId"] = overlayId }, ct));

    /// <summary>
    /// An overlay tool's answer (CodeGraph C6, michaeltrefry/CodeGraph PR #75, <c>OverlayMcpServer</c>): from an overlay's
    /// <c>structuredContent</c> its <c>overlayId</c>, <c>status</c>, <c>headSha</c>, <c>baseSha</c>, <c>stale</c> and
    /// <c>overlayError</c> (a failed overlay's error); from an error answer's (<c>isError</c>, <c>{"error": {code, message}}</c>) the
    /// code.
    /// </summary>
    public static OverlayAnswer Overlay(McpToolResult result)
    {
        var s = result.Structured as JsonObject;
        long? id = s?["overlayId"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        var stale = s?["stale"] is JsonValue st && st.TryGetValue<bool>(out var flag) && flag;
        var code = s?["error"] is JsonObject e ? Str(e, "code") : null;
        return new OverlayAnswer(result.IsError, result.Text, id, Str(s, "status"), Str(s, "headSha"), Str(s, "baseSha"),
            Str(s, "overlayError"), stale, code);
    }

    /// <summary>
    /// The typed not-indexed answer of a commit-pinned read (CodeGraph C4: <c>structuredContent.notIndexed</c> = {code, repo, sha, status,
    /// message, overlayId, baseSha}), or null when the answer is not one.
    /// </summary>
    public static CodeGraphNotIndexed? NotIndexedOf(JsonNode? structured) =>
        structured is JsonObject obj && obj["notIndexed"] is JsonObject n
            ? new CodeGraphNotIndexed(Str(n, "code") ?? "unknown", Str(n, "status") ?? "unknown", Str(n, "message") ?? "",
                n["overlayId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null, Str(n, "baseSha"))
            : null;

    private static string? Str(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// The CodeGraph project indexing <paramref name="repo"/>, from <c>search_projects</c>: only the orchestrator calls it (it is on no
    /// allowlist, so no session or reviewer can).
    /// </summary>
    public async Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct)
    {
        var answer = await _client.CallToolAsync("search_projects", new JsonObject { ["search"] = repo.Name }, ct);
        if (answer.IsError)
        {
            throw new InvalidOperationException($"CodeGraph's search_projects answered an error: {Cut(answer.Text)}");
        }
        return ProjectFor(answer.Text, repo);
    }

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

    private static string Cut(string s) => s.Length > 500 ? s[..500] : s;
}
