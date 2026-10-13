using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Kanban;

/// <summary>
/// KanbanBoard refused a call, or answered something that is no answer (sc-25981). <see cref="Code"/> is the contract's error code
/// (<c>docs/mcp-contract.md</c> of KanbanBoard: <c>scope_denied</c>, <c>not_found</c>, <c>conflict</c>, <c>already_claimed</c>,
/// <c>cycle</c>, <c>invalid_argument</c>, <c>busy</c>) — a refused call changed nothing — or null for a plain MCP error, after
/// which the call may have written.
/// </summary>
public sealed class KanbanException(string tool, string? code, string message)
    : InvalidOperationException($"Kanban's {tool} {(code is null ? "failed" : $"was refused ({code})")}: {message}")
{
    public const string ScopeDenied = "scope_denied";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string AlreadyClaimed = "already_claimed";

    public string Tool { get; } = tool;

    public string? Code { get; } = code;
}

/// <summary>A KanbanBoard project (the contract's Project shape; only the fields the factory reads).</summary>
public sealed record KanbanProject(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("repository_url")] string? RepositoryUrl = null);

/// <summary>An entry of a work item's <c>blockers</c>: a blocker counts as finished only in <c>Done</c>.</summary>
public sealed record KanbanDependency(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status);

/// <summary>An external link of a work item.</summary>
public sealed record KanbanLink(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("url")] string Url);

/// <summary>
/// A KanbanBoard work item (the contract's Work item shape; only the fields the factory reads). The four claim fields are null
/// when the item is unclaimed or its claim has expired.
/// </summary>
public sealed record KanbanWorkItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("epicId")] Guid? EpicId = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("estimate")] int? Estimate = null,
    [property: JsonPropertyName("claimant")] string? Claimant = null,
    [property: JsonPropertyName("claimant_id")] Guid? ClaimantId = null,
    [property: JsonPropertyName("claim_marker")] string? ClaimMarker = null,
    [property: JsonPropertyName("blockers")] List<KanbanDependency>? Blockers = null,
    [property: JsonPropertyName("links")] List<KanbanLink>? Links = null,
    [property: JsonPropertyName("label_list")] List<string>? LabelList = null)
{
    /// <summary>The blockers that still block: every one not in <see cref="KanbanStatus.Done"/> (a <c>Closed</c> blocker still blocks).</summary>
    public IReadOnlyList<KanbanDependency> OpenBlockers => (Blockers ?? []).Where(b => b.Status != KanbanStatus.Done).ToList();
}

/// <summary>One page of <c>list_work_items</c>; <see cref="NextCursor"/> is left out on the last.</summary>
public sealed record KanbanPage(
    [property: JsonPropertyName("items")] List<KanbanWorkItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor = null);

public sealed record KanbanEpic(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record KanbanDocument(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string? Body = null);

/// <summary>The work item statuses (the board's columns), written and read as these strings exactly.</summary>
public static class KanbanStatus
{
    public const string Backlog = "Backlog";
    public const string Ready = "Ready";
    public const string InProgress = "InProgress";
    public const string Done = "Done";
}

/// <summary>
/// The orchestrator's typed caller of KanbanBoard's MCP (sc-25981), over the one client that holds its full-scope token
/// (<see cref="Gateway.KanbanWrites.Client"/>; E2: that client is no MCP upstream, so no model session reaches it). It sends exactly
/// the tools and argument names of KanbanBoard's contract (<c>docs/mcp-contract.md</c> there; names are compared exactly and an
/// argument the tool does not have is refused) and nothing else of the board: the tools in <see cref="ReadTools"/> and
/// <see cref="WriteTools"/>. Every write is conditional or idempotent by the contract (E3): the status change carries
/// <c>expected_version</c>, the comment an <c>idempotency_key</c>, and claim, release and link are idempotent by themselves. A
/// refused call throws <see cref="KanbanException"/> with the contract's code; nothing is retried here.
/// </summary>
public sealed class KanbanBoard(McpHttpClient mcp)
{
    /// <summary>The read tools this caller sends.</summary>
    public static readonly IReadOnlyList<string> ReadTools = ["list_projects", "get_work_item", "list_work_items", "get_epic", "list_epic_documents"];

    /// <summary>The write tools this caller sends: none of them is on any MCP allowlist (<see cref="McpServers.KanbanTools"/>).</summary>
    public static readonly IReadOnlyList<string> WriteTools =
        ["claim_work_item", "release_work_item_claim", "update_work_item", "add_work_item_comment", "add_work_item_link"];

    /// <summary>The largest page <c>list_work_items</c> takes.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Every project that is not archived.</summary>
    public Task<List<KanbanProject>> ListProjectsAsync(CancellationToken ct) =>
        CallAsync<List<KanbanProject>>("list_projects", new JsonObject(), ct);

    /// <summary>The item with its blockers and links; <see cref="KanbanException.NotFound"/> for an id the board does not know.</summary>
    public Task<KanbanWorkItem> GetItemAsync(Guid id, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("get_work_item", new JsonObject { ["itemId"] = id.ToString() }, ct);

    /// <summary>
    /// One page of the project's items that are ready to take, oldest first: in <see cref="KanbanStatus.Ready"/>, every blocker
    /// <c>Done</c>, and with no live claim (the contract's "Polling for ready work"; no <c>updated_since</c>, since neither a
    /// finished blocker nor an expired claim stamps the item). Comments are left out.
    /// </summary>
    public Task<KanbanPage> ListReadyAsync(Guid projectId, int limit, string? cursor, CancellationToken ct)
    {
        var arguments = new JsonObject
        {
            ["projectId"] = projectId.ToString(),
            ["status"] = KanbanStatus.Ready,
            ["unblocked_only"] = true,
            ["claim_state"] = "unclaimed",
            ["limit"] = limit,
            ["include_comments"] = false,
        };
        if (cursor is not null)
        {
            arguments["cursor"] = cursor;
        }
        return CallAsync<KanbanPage>("list_work_items", arguments, ct);
    }

    public Task<KanbanEpic> GetEpicAsync(Guid epicId, CancellationToken ct) =>
        CallAsync<KanbanEpic>("get_epic", new JsonObject { ["epicId"] = epicId.ToString() }, ct);

    /// <summary>The epic's documents, each with its whole body.</summary>
    public Task<List<KanbanDocument>> ListEpicDocumentsAsync(Guid epicId, CancellationToken ct) =>
        CallAsync<List<KanbanDocument>>("list_epic_documents", new JsonObject { ["epicId"] = epicId.ToString() }, ct);

    /// <summary>
    /// Claims the item for the token's identity under <paramref name="marker"/> until <paramref name="ttlSeconds"/> from now; the
    /// same identity and marker again resets the expiry. <see cref="KanbanException.AlreadyClaimed"/> when another identity or
    /// marker holds a live claim.
    /// </summary>
    public Task<KanbanWorkItem> ClaimAsync(Guid id, string marker, int ttlSeconds, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("claim_work_item",
            new JsonObject { ["id"] = id.ToString(), ["marker"] = marker, ["ttl_seconds"] = ttlSeconds }, ct);

    /// <summary>
    /// Releases the caller's claim under <paramref name="marker"/>: <see cref="KanbanException.Conflict"/> when the caller holds
    /// none, <see cref="KanbanException.AlreadyClaimed"/> when someone else holds the item.
    /// </summary>
    public Task<KanbanWorkItem> ReleaseClaimAsync(Guid id, string marker, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("release_work_item_claim", new JsonObject { ["id"] = id.ToString(), ["marker"] = marker }, ct);

    /// <summary>
    /// Puts the item at the end of the <paramref name="status"/> column and changes nothing else, only while it still has
    /// <paramref name="expectedVersion"/> (<see cref="KanbanException.Conflict"/> otherwise, nothing written).
    /// </summary>
    public Task<KanbanWorkItem> SetStatusAsync(Guid id, string status, long expectedVersion, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("update_work_item",
            new JsonObject { ["itemId"] = id.ToString(), ["status"] = status, ["expected_version"] = expectedVersion }, ct);

    /// <summary>
    /// Comments on the item as the token's identity. A repeat of <paramref name="idempotencyKey"/> by the same identity on the same
    /// item adds nothing.
    /// </summary>
    public Task<KanbanWorkItem> CommentAsync(Guid id, string body, string idempotencyKey, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("add_work_item_comment",
            new JsonObject { ["itemId"] = id.ToString(), ["body"] = body, ["idempotency_key"] = idempotencyKey }, ct);

    /// <summary>Attaches <paramref name="url"/> under <paramref name="kind"/> (<c>pr</c>, <c>branch</c> or <c>other</c>); a kind and URL the item already carries change nothing.</summary>
    public Task<KanbanWorkItem> LinkAsync(Guid id, string kind, string url, CancellationToken ct) =>
        CallAsync<KanbanWorkItem>("add_work_item_link", new JsonObject { ["id"] = id.ToString(), ["kind"] = kind, ["url"] = url }, ct);

    /// <summary>
    /// One call: a success's single text block is one JSON document of the shape the contract names; a refusal is
    /// <c>isError</c> with <c>structuredContent</c> <c>{code, message}</c> (the same JSON in its text block).
    /// </summary>
    private async Task<T> CallAsync<T>(string tool, JsonObject arguments, CancellationToken ct)
    {
        var result = await mcp.CallToolAsync(tool, arguments, ct);
        if (result.IsError)
        {
            var error = result.Structured as JsonObject ?? TryParse(result.Text) as JsonObject;
            var code = error?["code"] is JsonValue c && c.TryGetValue<string>(out var known) ? known : null;
            var message = error?["message"] is JsonValue m && m.TryGetValue<string>(out var said) ? said : result.Text;
            throw new KanbanException(tool, code, Cut(message));
        }
        try
        {
            return JsonSerializer.Deserialize<T>(result.Text) ?? throw new JsonException("the answer is null");
        }
        catch (JsonException ex)
        {
            throw new KanbanException(tool, null, $"its answer is not the JSON the contract names ({ex.Message}): {Cut(result.Text)}");
        }
    }

    private static JsonNode? TryParse(string text)
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

    private static string Cut(string s) => s.Length > 500 ? s[..500] : s;
}
