using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Kanban;

/// <summary>
/// KanbanBoard refused a call, or answered something that is no answer (sc-25981, sc-25983). <see cref="Code"/> is the contract's
/// error code (<c>docs/mcp-contract.md</c> of KanbanBoard, <see cref="Codes"/>) — a typed refusal: the call changed nothing — or
/// null for a plain MCP error (no <c>{code, message}</c>), after which the call may have written.
/// </summary>
public sealed class KanbanException(string tool, string? code, string message)
    : InvalidOperationException($"Kanban's {tool} {(code is null ? "failed" : $"was refused ({code})")}: {message}")
{
    /// <summary>A write with a <c>read</c> token, or a token revoked or expired. Never sent again.</summary>
    public const string ScopeDenied = "scope_denied";
    /// <summary>The id that addresses the call is not known. Never sent again.</summary>
    public const string NotFound = "not_found";
    /// <summary>
    /// The object is no longer in the state the call depends on (a stale <c>expected_version</c>, a claim no longer held): never
    /// sent again as it is — the caller reads again and decides again.
    /// </summary>
    public const string Conflict = "conflict";
    /// <summary>A live claim of another identity or another marker holds the item.</summary>
    public const string AlreadyClaimed = "already_claimed";
    /// <summary>A dependency would make an item block itself. Never sent again.</summary>
    public const string Cycle = "cycle";
    /// <summary>An argument is missing, malformed or not the tool's. Never sent again.</summary>
    public const string InvalidArgument = "invalid_argument";
    /// <summary>
    /// The server could not take the call in time because of other callers: the one refusal that is sent again, the same call
    /// (<see cref="KanbanBoard.MaxBusyRetries"/>).
    /// </summary>
    public const string Busy = "busy";

    /// <summary>Every error code of the contract.</summary>
    public static readonly IReadOnlyList<string> Codes = [ScopeDenied, NotFound, Conflict, AlreadyClaimed, Cycle, InvalidArgument, Busy];

    public string Tool { get; } = tool;

    public string? Code { get; } = code;

    /// <summary>Whether the board refused the call with a code, so it changed nothing. False for an error after which it may have written.</summary>
    public bool Refused => Code is not null;
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
/// when the item is unclaimed or its claim has expired. <see cref="CommentCreated"/> is there only in the answer of a comment
/// (false: its idempotency key was a repeat and nothing was added), <see cref="Created"/> only in that of a create (likewise).
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
    [property: JsonPropertyName("claim_expires_at_utc")] DateTimeOffset? ClaimExpiresAtUtc = null,
    [property: JsonPropertyName("blockers")] List<KanbanDependency>? Blockers = null,
    [property: JsonPropertyName("links")] List<KanbanLink>? Links = null,
    [property: JsonPropertyName("label_list")] List<string>? LabelList = null,
    [property: JsonPropertyName("comment_created")] bool? CommentCreated = null,
    [property: JsonPropertyName("created")] bool? Created = null)
{
    /// <summary>The blockers that still block: every one not in <see cref="KanbanStatus.Done"/> (a <c>Closed</c> blocker still blocks).</summary>
    public IReadOnlyList<KanbanDependency> OpenBlockers => (Blockers ?? []).Where(b => b.Status != KanbanStatus.Done).ToList();

    /// <summary>Whether the item carries the external link <paramref name="url"/> under <paramref name="kind"/> (the board compares URLs exactly).</summary>
    public bool HasLink(string kind, string url) => (Links ?? []).Any(l => l.Kind == kind && l.Url == url);
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
/// <see cref="WriteTools"/>.
/// Safe writes (sc-25983, E3), decided here so every caller has them. A refusal with a code (<c>{code, message}</c>) throws
/// <see cref="KanbanException"/> with that code; it changed nothing. <c>busy</c> — and no other code — is sent again: the same
/// call with the same arguments (so the same idempotency key, the same expected version), at most <see cref="MaxBusyRetries"/>
/// times, each after <see cref="BusyBackoff"/>; then <c>busy</c> is thrown. An error that is no typed refusal — a plain MCP
/// error, a lost answer, a timeout, an HTTP error — may have written, so a write that carries no idempotency key READS the item
/// before it is repeated (<see cref="WriteAsync"/>): when the read shows the write landed, the item read is the answer and
/// nothing is sent again; otherwise the write is sent once more (<see cref="MaxRepeatsAfterLostAnswer"/>), and a second such
/// error is thrown. A write that carries an idempotency key (a comment, a create) is sent again with the same key and no read:
/// the board answers the object the key already made. Every status change carries <c>expected_version</c>, so a repeat never
/// overwrites what someone else wrote in between: the board answers <c>conflict</c>, which is the caller's to handle (read again,
/// decide again; <see cref="KanbanWorkSource"/>).
/// </summary>
/// <param name="delay">How a backoff waits; default <see cref="Task.Delay(TimeSpan, CancellationToken)"/> (a test passes its own).</param>
public sealed class KanbanBoard(McpHttpClient mcp, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>The read tools this caller sends.</summary>
    public static readonly IReadOnlyList<string> ReadTools = ["list_projects", "get_work_item", "list_work_items", "get_epic", "list_epic_documents"];

    /// <summary>The write tools this caller sends: none of them is on any MCP allowlist (<see cref="McpServers.KanbanTools"/>).</summary>
    public static readonly IReadOnlyList<string> WriteTools =
    [
        "claim_work_item", "renew_work_item_claim", "release_work_item_claim", "update_work_item", "add_work_item_comment",
        "add_work_item_link", "create_work_item",
    ];

    /// <summary>The largest page <c>list_work_items</c> takes.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// How often a call answered <c>busy</c> is sent again before <c>busy</c> is thrown: 4, so at most 5 sends. The board answers
    /// <c>busy</c> only after it waited 10 s itself, so the bound is about a minute for one call.
    /// </summary>
    public const int MaxBusyRetries = 4;

    /// <summary>The wait before the <paramref name="retry"/>-th resend (1-based) of a <c>busy</c> call: 1 s, 2 s, 4 s, 8 s.</summary>
    public static TimeSpan BusyBackoff(int retry) => TimeSpan.FromSeconds(1 << Math.Clamp(retry - 1, 0, 3));

    /// <summary>How often a write is sent again after an error that is no typed refusal: once.</summary>
    public const int MaxRepeatsAfterLostAnswer = 1;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

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
    /// marker holds a live claim. After a lost answer the item is read and the claim is sent again whatever the read shows
    /// (<see cref="Again"/>): the same caller's claim is idempotent by the contract, and only the claim's own answer tells this
    /// caller's claim from another identity's under the same marker.
    /// </summary>
    public Task<KanbanWorkItem> ClaimAsync(Guid id, string marker, int ttlSeconds, CancellationToken ct) =>
        WriteAsync("claim_work_item", new JsonObject { ["id"] = id.ToString(), ["marker"] = marker, ["ttl_seconds"] = ttlSeconds }, id, Again, ct);

    /// <summary>
    /// Extends the live claim the caller holds under <paramref name="marker"/> to <paramref name="ttlSeconds"/> from now:
    /// <see cref="KanbanException.Conflict"/> when that claim has lapsed or was released, <see cref="KanbanException.AlreadyClaimed"/>
    /// when someone else now holds the item. After a lost answer the item is read and the claim renewed again, as
    /// <see cref="ClaimAsync"/> does.
    /// </summary>
    public Task<KanbanWorkItem> RenewClaimAsync(Guid id, string marker, int ttlSeconds, CancellationToken ct) =>
        WriteAsync("renew_work_item_claim", new JsonObject { ["id"] = id.ToString(), ["marker"] = marker, ["ttl_seconds"] = ttlSeconds }, id, Again, ct);

    /// <summary>
    /// Releases the caller's claim under <paramref name="marker"/>: <see cref="KanbanException.Conflict"/> when the caller holds
    /// none, <see cref="KanbanException.AlreadyClaimed"/> when someone else holds the item. After a lost answer, an item that no
    /// longer carries the marker is released: it is not sent again (the board would refuse a release that happened, <c>conflict</c>).
    /// </summary>
    public Task<KanbanWorkItem> ReleaseClaimAsync(Guid id, string marker, CancellationToken ct) =>
        WriteAsync("release_work_item_claim", new JsonObject { ["id"] = id.ToString(), ["marker"] = marker }, id,
            now => now.ClaimMarker != marker, ct);

    /// <summary>
    /// Puts the item at the end of the <paramref name="status"/> column and changes nothing else, only while it still has
    /// <paramref name="expectedVersion"/> (<see cref="KanbanException.Conflict"/> otherwise, nothing written). Sent as
    /// <c>update_work_item(itemId, status, expected_version)</c>, not <c>move_work_item</c>: a status change has no position to
    /// ask for, and <c>update_work_item</c> puts the item at the end of its new column by itself, where <c>move_work_item</c>
    /// takes an <c>order</c> (left out it is 0, the top of the column, ahead of everything waiting there) and renumbers the
    /// column's other items. Both check <c>expected_version</c> the same way. After a lost answer, an item whose version moved
    /// and that is in <paramref name="status"/> is where the call meant it; any other is sent again on the same expected version,
    /// which the board refuses <c>conflict</c> if anyone wrote in between.
    /// </summary>
    public Task<KanbanWorkItem> SetStatusAsync(Guid id, string status, long expectedVersion, CancellationToken ct) =>
        WriteAsync("update_work_item",
            new JsonObject { ["itemId"] = id.ToString(), ["status"] = status, ["expected_version"] = expectedVersion }, id,
            now => now.Version != expectedVersion && now.Status == status, ct);

    /// <summary>
    /// Comments on the item as the token's identity. A repeat of <paramref name="idempotencyKey"/> by the same identity on the same
    /// item adds nothing (<see cref="KanbanWorkItem.CommentCreated"/> false), so after a lost answer it is sent again as it is.
    /// </summary>
    public Task<KanbanWorkItem> CommentAsync(Guid id, string body, string idempotencyKey, CancellationToken ct) =>
        WriteAsync("add_work_item_comment",
            new JsonObject { ["itemId"] = id.ToString(), ["body"] = body, ["idempotency_key"] = idempotencyKey }, null, null, ct);

    /// <summary>
    /// Attaches <paramref name="url"/> under <paramref name="kind"/> (<c>pr</c>, <c>branch</c> or <c>other</c>); a kind and URL the
    /// item already carries change nothing. After a lost answer, an item that carries the link is not sent it again.
    /// </summary>
    public Task<KanbanWorkItem> LinkAsync(Guid id, string kind, string url, CancellationToken ct) =>
        WriteAsync("add_work_item_link", new JsonObject { ["id"] = id.ToString(), ["kind"] = kind, ["url"] = url }, id,
            now => now.HasLink(kind, url), ct);

    /// <summary>
    /// Creates a work item in the project's backlog (in <paramref name="epicId"/>'s when given) — or, when the same identity
    /// already used <paramref name="idempotencyKey"/> for an item of the project, creates nothing and answers the item that key
    /// made, as it is now (<see cref="KanbanWorkItem.Created"/> false). The key is not optional here: a create is only ever sent
    /// with one, so after a lost answer it is sent again as it is and makes no second item.
    /// </summary>
    public Task<KanbanWorkItem> CreateItemAsync(Guid projectId, string title, string type, string? description, Guid? epicId,
        string idempotencyKey, CancellationToken ct)
    {
        var arguments = new JsonObject
        {
            ["projectId"] = projectId.ToString(), ["title"] = title, ["type"] = type, ["idempotency_key"] = idempotencyKey,
        };
        if (description is not null)
        {
            arguments["description"] = description;
        }
        if (epicId is { } epic)
        {
            arguments["epicId"] = epic.ToString();
        }
        return WriteAsync("create_work_item", arguments, null, null, ct);
    }

    /// <summary>For a write its caller may always send again: what a read shows never stands in for the write's own answer.</summary>
    private static bool Again(KanbanWorkItem _) => false;

    /// <summary>
    /// One write under the read-before-repeat rule. A typed refusal is thrown as it is (it changed nothing). After any other error
    /// the write may have happened. With <paramref name="landed"/> (a write without an idempotency key) the item
    /// <paramref name="itemId"/> is read first — a read that fails is thrown, so the write is never repeated blind — and when
    /// <paramref name="landed"/> holds for the item read, that item is the answer. Otherwise, and for a write that carries an
    /// idempotency key (<paramref name="landed"/> null), the same call is sent again, at most
    /// <see cref="MaxRepeatsAfterLostAnswer"/> times.
    /// </summary>
    private async Task<KanbanWorkItem> WriteAsync(string tool, JsonObject arguments, Guid? itemId, Func<KanbanWorkItem, bool>? landed,
        CancellationToken ct)
    {
        for (var repeat = 0; ; repeat++)
        {
            try
            {
                return await CallAsync<KanbanWorkItem>(tool, arguments, ct);
            }
            catch (Exception ex) when (repeat < MaxRepeatsAfterLostAnswer && MayHaveWritten(ex, ct))
            {
                if (landed is not null && itemId is { } id && await GetItemAsync(id, ct) is var now && landed(now))
                {
                    return now;
                }
            }
        }
    }

    /// <summary>
    /// Whether a failed call may have written: anything but a typed refusal (which changed nothing) and the caller's own
    /// cancellation — a plain MCP error, an HTTP error, a timeout, an answer that cannot be read.
    /// </summary>
    private static bool MayHaveWritten(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is not KanbanException { Refused: true };

    /// <summary>
    /// One call: a success's single text block is one JSON document of the shape the contract names; a refusal is
    /// <c>isError</c> with <c>structuredContent</c> <c>{code, message}</c> (the same JSON in its text block). A <c>busy</c>
    /// refusal changed nothing and is sent again as it is (<see cref="MaxBusyRetries"/>, <see cref="BusyBackoff"/>).
    /// </summary>
    private async Task<T> CallAsync<T>(string tool, JsonObject arguments, CancellationToken ct)
    {
        for (var retry = 0; ; retry++)
        {
            var result = await mcp.CallToolAsync(tool, arguments, ct);
            if (result.IsError)
            {
                var error = result.Structured as JsonObject ?? TryParse(result.Text) as JsonObject;
                var code = error?["code"] is JsonValue c && c.TryGetValue<string>(out var known) ? known : null;
                if (code == KanbanException.Busy && retry < MaxBusyRetries)
                {
                    await _delay(BusyBackoff(retry + 1), ct);
                    continue;
                }
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
