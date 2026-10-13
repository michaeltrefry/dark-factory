using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The orchestrator's own MCP client of KanbanBoard (sc-25981, E2): the only client that holds the full-scope token
/// (<c>Kanban:WriteToken</c>), built here and nowhere else, over an <see cref="OutboundHttp.KanbanApi"/> client. It is handed to the
/// Kanban work source's typed caller (<c>Kanban.KanbanBoard</c>) and to nothing else: it is no <see cref="McpUpstream"/>, so it is in
/// no upstream registry (<see cref="McpUpstreams"/>), profile (<see cref="McpProfile"/>), allowlist or proxy grant, and no model
/// session — reviewer, triage or planner — can list or call a tool through it. Model sessions keep the separate read token
/// (<c>Kanban:Token</c>), whose upstream's allowlist (<see cref="McpServers.KanbanTools"/>) holds read tools only.
/// </summary>
public static class KanbanWrites
{
    /// <summary>The service's name, as errors name it.</summary>
    public const string Service = "Kanban";

    /// <summary>The client; <c>Kanban:McpUrl</c> is the endpoint itself, so requests go to <paramref name="kanbanHttp"/>'s base address.</summary>
    public static McpHttpClient Client(HttpClient kanbanHttp, string writeToken) => new(kanbanHttp, writeToken, Service, "");
}
