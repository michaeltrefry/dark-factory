using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>A tool off an upstream's allowlist was asked for: it is never sent (E2).</summary>
public sealed class ToolNotAllowedException(string upstream, string tool)
    : InvalidOperationException($"'{tool}' is not one of the factory's {upstream} tools; it is never sent.")
{
    public string Tool { get; } = tool;
}

/// <summary>
/// One upstream MCP server the factory reads (sc-25707): its name (the MCP server name sessions see, <c>mcp__&lt;name&gt;__&lt;tool&gt;</c>),
/// its read-only allowlist (one per upstream, in code; E2), whether its queries are pinned to the item's repository
/// (<see cref="RepoScoped"/>), and its owner-side client. <see cref="ListToolsAsync"/> shows only allowlisted tools and
/// <see cref="CallAsync"/> refuses any other before anything is sent. Shared by the loopback proxy (<see cref="McpProxy"/>) and the
/// reviewers' tool loop (<see cref="Gate.ReviewTools"/>).
/// </summary>
public sealed class McpUpstream(string name, McpHttpClient client, IReadOnlyList<string> allowlist, bool repoScoped)
{
    public string Name { get; } = name;

    /// <summary>The service's display name (CodeGraph, Kanban).</summary>
    public string Service => client.Service;

    /// <summary>The only tools of this upstream anything in the factory lists or calls.</summary>
    public IReadOnlyList<string> Allowlist { get; } = allowlist;

    /// <summary>
    /// Whether every query is pinned to the item's repository: CodeGraph (its project is the repository's, resolved by exact repo URL
    /// match); Kanban has no repository mapping, so it is not.
    /// </summary>
    public bool RepoScoped { get; } = repoScoped;

    public bool Allows(string tool) => Allowlist.Contains(tool, StringComparer.Ordinal);

    /// <summary>The upstream's tools that are on the allowlist (an allowlisted tool the upstream lacks is simply not listed).</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
        (await client.ListToolsAsync(ct)).Where(t => Allows(t.Name)).GroupBy(t => t.Name).Select(g => g.First()).ToList();

    /// <summary>Calls an allowlisted tool; any other is refused (<see cref="ToolNotAllowedException"/>) before anything is sent.</summary>
    public Task<McpToolResult> CallAsync(string tool, JsonObject arguments, CancellationToken ct) =>
        Allows(tool) ? client.CallToolAsync(tool, arguments, ct) : throw new ToolNotAllowedException(Service, tool);
}

/// <summary>
/// The upstream MCP servers this factory is configured with (sc-25707), each optional: CodeGraph (<c>CodeGraph:Token</c>) and Kanban
/// (<c>Kanban:Token</c>). No token, no upstream (not an error). The one registry the loopback proxy and the reviewers' tool loop share.
/// </summary>
public sealed record McpUpstreams(CodeGraphMcpClient? CodeGraph, McpUpstream? Kanban)
{
    public static readonly McpUpstreams None = new(null, null);

    /// <summary>Every configured upstream.</summary>
    public IReadOnlyList<McpUpstream> All => new[] { CodeGraph?.Upstream, Kanban }.OfType<McpUpstream>().ToList();

    public McpUpstream? Named(string name) => All.FirstOrDefault(u => u.Name == name);
}

/// <summary>The MCP server names sessions see, and the per-upstream allowlists not kept elsewhere.</summary>
public static class McpServers
{
    /// <summary>CodeGraph's name (its allowlist is <see cref="Gate.ReviewTools.CodeGraphTools"/>, repo-scoped).</summary>
    public const string CodeGraph = "codegraph";

    /// <summary>Kanban's name (its allowlist is <see cref="KanbanTools"/>; not repo-scoped: Kanban has no repository mapping).</summary>
    public const string Kanban = "kanban";

    /// <summary>
    /// KanbanBoard's read tools (it runs no models). Its write tools (Create*, Update*, Delete*, Move*, Add*Comment) are never offered or
    /// forwarded. <c>GetWorkItem</c> and <c>SearchWorkItems</c> come with KanbanBoard sc-25711; an upstream without them does not list them.
    /// </summary>
    public static readonly IReadOnlyList<string> KanbanTools =
    [
        "ListProjects", "GetBoard", "ListEpics", "GetEpic", "ListEpicDocuments", "GetEpicDocument", "ListWorkItems",
        "ListEpicWorkItems", "GetIssues", "GetWorkItem", "SearchWorkItems",
    ];

    /// <summary>Claude Code's name for an MCP tool: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public static string ToolRule(string server, string tool) => $"mcp__{server}__{tool}";

    /// <summary>
    /// Every allow rule a session may be given for MCP: each allowlisted tool of each upstream, by its full Claude Code name. Nothing
    /// else (a server-wide <c>mcp__codegraph</c> rule would allow whatever the server adds).
    /// </summary>
    public static readonly IReadOnlyList<string> AllToolRules =
    [
        .. Gate.ReviewTools.CodeGraphTools.Select(t => ToolRule(CodeGraph, t)),
        .. KanbanTools.Select(t => ToolRule(Kanban, t)),
    ];

    /// <summary>The allow rules for <paramref name="upstreams"/>' allowlisted tools.</summary>
    public static IReadOnlyList<string> ToolRules(McpUpstreams upstreams) =>
        upstreams.All.SelectMany(u => u.Allowlist.Select(t => ToolRule(u.Name, t))).ToList();
}
