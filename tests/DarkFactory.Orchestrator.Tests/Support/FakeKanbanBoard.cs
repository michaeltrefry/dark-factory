using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// An in-memory KanbanBoard behind a fake MCP server (<see cref="FakeMcpServer"/> at <c>/mcp</c>), behaving as KanbanBoard's
/// contract says (<c>docs/mcp-contract.md</c> there) for the tools the factory's adapter sends: exact tool and argument names (an
/// argument the tool does not have, or a required one missing, is <c>invalid_argument</c>), two tokens (<see cref="FullToken"/> and
/// <see cref="ReadToken"/>: a write tool with the read token is <c>scope_denied</c> before anything else, an unknown token HTTP
/// 401), identities by token, expiring claims (claim, renew, release), <c>version</c>/<c>expected_version</c>, comment and
/// create idempotency keys, idempotent links, cursor paging, and answers in the contract's shapes (a field without a value left
/// out or null as the contract says). Every call is recorded with the token it carried. A test can make it answer <c>busy</c>
/// (<see cref="Busy"/>) or lose an answer after the write happened (<see cref="LoseAnswer"/>), as the contract's "Errors" allow.
/// </summary>
public sealed class FakeKanbanBoard
{
    public const string FullToken = "kb_full_token";
    public const string ReadToken = "kb_read_token";
    /// <summary>A second full-scope caller (another identity).</summary>
    public const string OtherToken = "kb_other_token";

    public const string Url = "http://kanban.test/mcp";

    /// <summary>The 23 write tools of the contract.</summary>
    public static readonly IReadOnlyList<string> WriteTools =
    [
        "add_issue_comment", "add_work_item_comment", "add_work_item_dependency", "add_work_item_link", "claim_work_item", "create_epic",
        "create_epic_document", "create_epic_work_item", "create_project", "create_work_item", "delete_epic", "delete_project",
        "delete_work_item", "import_epic_plan", "move_work_item", "release_work_item_claim", "remove_work_item_dependency",
        "remove_work_item_link", "renew_work_item_claim", "update_epic", "update_epic_document", "update_project", "update_work_item",
    ];

    /// <summary>Each tool this fake answers: its arguments, the required ones first (before <c>|</c>).</summary>
    private static readonly Dictionary<string, (string[] Required, string[] Optional)> Arguments = new()
    {
        ["list_projects"] = ([], ["includeArchived"]),
        ["get_work_item"] = (["itemId"], []),
        ["list_work_items"] = ([], ["projectId", "epicId", "type", "status", "includeArchivedEpics", "unblocked_only", "label", "updated_since",
            "claim_state", "limit", "cursor", "include_comments"]),
        ["get_epic"] = (["epicId"], []),
        ["list_epic_documents"] = (["epicId"], []),
        ["claim_work_item"] = (["id", "marker", "ttl_seconds"], []),
        ["renew_work_item_claim"] = (["id", "marker", "ttl_seconds"], []),
        ["release_work_item_claim"] = (["id", "marker"], []),
        ["create_work_item"] = (["projectId", "title", "type"], ["status", "priority", "epicId", "description", "estimate", "labels", "label_list",
            "idempotency_key"]),
        ["update_work_item"] = (["itemId"], ["title", "type", "status", "priority", "epicId", "description", "estimate", "labels", "label_list",
            "expected_version", "clear"]),
        ["add_work_item_comment"] = (["itemId", "body"], ["author", "idempotency_key"]),
        ["add_work_item_link"] = (["id", "kind", "url"], []),
    };

    private static readonly Dictionary<string, (Guid Id, string Name)> Identities = new()
    {
        [FullToken] = (Guid.Parse("f0000000-0000-0000-0000-000000000001"), "factory"),
        [ReadToken] = (Guid.Parse("f0000000-0000-0000-0000-000000000002"), "factory-read"),
        [OtherToken] = (Guid.Parse("f0000000-0000-0000-0000-000000000003"), "someone"),
    };

    public sealed class Project
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Key { get; init; }
        public string? RepositoryUrl { get; set; }
        public bool Archived { get; set; }
    }

    public sealed class Epic
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Guid ProjectId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; set; }
        public List<(Guid Id, string Title, string Body)> Documents { get; } = [];
    }

    public sealed record Comment(Guid Id, string Author, Guid AuthorId, string Body, string? Key);

    public sealed class Item
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required Guid ProjectId { get; init; }
        public Guid? EpicId { get; set; }
        public required string Title { get; set; }
        public string? Description { get; set; }
        public string Type { get; set; } = "Story";
        public string Status { get; set; } = "Ready";
        public int? Estimate { get; set; }
        public List<string> Labels { get; } = [];
        public long Version { get; set; } = 1;
        public (Guid Identity, string Name, string Marker, DateTimeOffset Expires)? Claim { get; set; }
        public List<Item> Blockers { get; } = [];
        public List<(string Kind, string Url)> Links { get; } = [];
        public List<Comment> Comments { get; } = [];
        /// <summary>The identity and idempotency key that created it through <c>create_work_item</c>.</summary>
        public (Guid Identity, string Key)? CreatedWith { get; set; }
    }

    public sealed record Call(string Tool, JsonObject Arguments, string Token);

    public FakeKanbanBoard()
    {
        // tools/list names its read tools and every write tool, as the real server does.
        Server = new FakeMcpServer("/mcp", [.. Arguments.Keys.Concat(WriteTools).Distinct()], Answer);
    }

    public FakeMcpServer Server { get; }
    public FixedTime Time { get; } = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    public List<Project> Projects { get; } = [];
    public List<Epic> Epics { get; } = [];
    public List<Item> Items { get; } = [];
    public List<Call> Calls { get; } = [];

    /// <summary>The write tools called so far, in order.</summary>
    public List<string> Writes => Calls.Where(c => WriteTools.Contains(c.Tool)).Select(c => c.Tool).ToList();

    /// <summary>Runs before a call is answered (after it is recorded): a test's chance to change the board under the caller.</summary>
    public Action<string, JsonObject>? Before { get; set; }

    /// <summary>When it answers a result for a call, that is the call's answer and the board does nothing.</summary>
    public Func<string, JsonObject, JsonObject?>? Instead { get; set; }

    /// <summary>
    /// When true for a call, the board does the call and then loses its answer: the caller gets what <see cref="Lost"/> makes
    /// (default: a plain MCP error — <c>isError</c> with no <c>structuredContent</c>, the contract's "anything else that goes
    /// wrong"). Asked once per call, after <see cref="Before"/>.
    /// </summary>
    public Func<string, JsonObject, bool>? LoseAnswer { get; set; }

    /// <summary>What a lost answer looks like to the caller; throw <see cref="FakeMcpServer.HttpAnswer"/> for an HTTP error.</summary>
    public Func<JsonObject> Lost { get; set; } = () =>
    {
        var result = FakeMcpServer.Text("An error occurred invoking the tool.");
        result["isError"] = true;
        return result;
    };

    /// <summary>How many of the next calls of each tool named here are refused <c>busy</c> (changing nothing) before the tool answers.</summary>
    public Dictionary<string, int> Busy { get; } = [];

    public HttpClient Client() => Server.Client(Url);

    public Project AddProject(string key, string? repositoryUrl = null)
    {
        var project = new Project { Key = key, RepositoryUrl = repositoryUrl };
        Projects.Add(project);
        return project;
    }

    public Epic AddEpic(Project project, string name, string? description = null)
    {
        var epic = new Epic { ProjectId = project.Id, Name = name, Description = description };
        Epics.Add(epic);
        return epic;
    }

    public Item AddItem(Project project, string title, string status = "Ready", string? description = null, Epic? epic = null)
    {
        var item = new Item { ProjectId = project.Id, Title = title, Status = status, Description = description, EpicId = epic?.Id };
        Items.Add(item);
        return item;
    }

    /// <summary>Claims <paramref name="item"/> as another caller, for an hour.</summary>
    public void ClaimAsSomeoneElse(Item item, string marker = "their-run")
    {
        var (id, name) = Identities[OtherToken];
        item.Claim = (id, name, marker, Time.Now.AddHours(1));
    }

    public static JsonObject Refusal(string code, string message)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        return FakeMcpServer.Structured(error.ToJsonString(), error, isError: true);
    }

    private static JsonObject Ok(JsonNode shape) => FakeMcpServer.Text(shape.ToJsonString());

    private JsonObject Answer(string tool, JsonObject arguments)
    {
        var header = Server.Api.Requests[^1].Headers.GetValueOrDefault("Authorization") ?? "";
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        if (!Identities.TryGetValue(token, out var identity))
        {
            throw new FakeMcpServer.HttpAnswer(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        }
        Calls.Add(new Call(tool, (JsonObject)arguments.DeepClone(), token));
        // The scope first, then the arguments, then the call itself.
        if (WriteTools.Contains(tool) && token == ReadToken)
        {
            return Refusal("scope_denied", $"The token's scope is read; {tool} needs full.");
        }
        if (!Arguments.TryGetValue(tool, out var known))
        {
            throw new InvalidOperationException($"The fake KanbanBoard does not answer {tool}.");
        }
        if (arguments.Select(a => a.Key).FirstOrDefault(a => !known.Required.Contains(a) && !known.Optional.Contains(a)) is { } unknown)
        {
            return Refusal("invalid_argument", $"{tool} has no argument '{unknown}'.");
        }
        if (known.Required.FirstOrDefault(r => arguments[r] is null) is { } missing)
        {
            return Refusal("invalid_argument", $"{tool} needs '{missing}'.");
        }
        Before?.Invoke(tool, arguments);
        if (Instead?.Invoke(tool, arguments) is { } instead)
        {
            return instead;
        }
        if (Busy.GetValueOrDefault(tool) is > 0 and var left)
        {
            Busy[tool] = left - 1;
            return Refusal("busy", $"{tool} waited 10 seconds for other writers; retry.");
        }
        var answer = Apply(tool, arguments, identity);
        return LoseAnswer?.Invoke(tool, arguments) == true ? Lost() : answer;
    }

    private JsonObject Apply(string tool, JsonObject arguments, (Guid Id, string Name) identity)
    {
        switch (tool)
        {
            case "list_projects":
                return Ok(new JsonArray(Projects.Where(p => !p.Archived).Select(p => (JsonNode)ProjectShape(p)).ToArray()));
            case "list_work_items":
                return ListItems(arguments);
            case "get_epic":
                return Epics.FirstOrDefault(e => e.Id == Id(arguments, "epicId")) is { } epic
                    ? Ok(EpicShape(epic))
                    : Refusal("not_found", $"Epic {arguments["epicId"]} was not found.");
            case "list_epic_documents":
                return Epics.FirstOrDefault(e => e.Id == Id(arguments, "epicId")) is { } owner
                    ? Ok(new JsonArray(owner.Documents.Select(d => (JsonNode)new JsonObject
                    {
                        ["id"] = d.Id.ToString(), ["epicId"] = owner.Id.ToString(), ["title"] = d.Title, ["body"] = d.Body,
                        ["createdAtUtc"] = Stamp(Time.Now), ["updatedAtUtc"] = Stamp(Time.Now),
                    }).ToArray()))
                    : Refusal("not_found", $"Epic {arguments["epicId"]} was not found.");
            case "create_work_item":
            {
                if (Projects.FirstOrDefault(p => p.Id == Id(arguments, "projectId")) is not { } project)
                {
                    return Refusal("not_found", $"Project {arguments["projectId"]} was not found.");
                }
                var key = arguments["idempotency_key"]?.GetValue<string>();
                if (key is not null && (key.Length is 0 or > 128 || key.Any(ch => ch is <= ' ' or > '~')))
                {
                    return Refusal("invalid_argument", "idempotency_key is 1 to 128 printable ASCII characters without spaces.");
                }
                // A repeat of the key by the same identity in the project: nothing created, the item as it is now.
                var earlier = key is null ? null : Items.FirstOrDefault(i => i.ProjectId == project.Id && i.CreatedWith == (identity.Id, key));
                var made = earlier ?? new Item
                {
                    ProjectId = project.Id, Title = arguments["title"]!.GetValue<string>(), Type = arguments["type"]!.GetValue<string>(),
                    Status = arguments["status"]?.GetValue<string>() ?? "Backlog", Description = arguments["description"]?.GetValue<string>(),
                    EpicId = arguments["epicId"] is null ? null : Id(arguments, "epicId"), CreatedWith = key is null ? null : (identity.Id, key),
                };
                if (earlier is null)
                {
                    Items.Add(made);
                }
                var created = ItemShape(made);
                created["created"] = earlier is null;
                return Ok(created);
            }
        }
        var idArgument = tool is "claim_work_item" or "renew_work_item_claim" or "release_work_item_claim" or "add_work_item_link" ? "id" : "itemId";
        if (Items.FirstOrDefault(i => i.Id == Id(arguments, idArgument)) is not { } item)
        {
            return Refusal("not_found", $"Work item {arguments[idArgument]} was not found.");
        }
        var live = item.Claim is { } c && c.Expires > Time.Now ? item.Claim : null;
        switch (tool)
        {
            case "get_work_item":
                return Ok(ItemShape(item));
            case "claim_work_item":
            {
                var (marker, ttl) = (arguments["marker"]!.GetValue<string>(), arguments["ttl_seconds"]!.GetValue<int>());
                if (ttl is < 1 or > 86400 || marker.Length > 200)
                {
                    return Refusal("invalid_argument", "ttl_seconds is 1 to 86400 and marker at most 200 characters.");
                }
                if (live is { } held && (held.Identity != identity.Id || held.Marker != marker))
                {
                    return Refusal("already_claimed", $"Work item {item.Id} is claimed by {held.Name}.");
                }
                item.Claim = (identity.Id, identity.Name, marker, Time.Now.AddSeconds(ttl));
                return Ok(ItemShape(item));
            }
            case "renew_work_item_claim":
            {
                var (marker, ttl) = (arguments["marker"]!.GetValue<string>(), arguments["ttl_seconds"]!.GetValue<int>());
                if (ttl is < 1 or > 86400)
                {
                    return Refusal("invalid_argument", "ttl_seconds is 1 to 86400.");
                }
                if (live is { } held && held.Identity == identity.Id && held.Marker == marker)
                {
                    item.Claim = (identity.Id, identity.Name, marker, Time.Now.AddSeconds(ttl));
                    return Ok(ItemShape(item));
                }
                return live is not null
                    ? Refusal("already_claimed", $"Work item {item.Id} is claimed by someone else.")
                    : Refusal("conflict", $"The caller holds no live claim on work item {item.Id} under this marker.");
            }
            case "release_work_item_claim":
            {
                var marker = arguments["marker"]!.GetValue<string>();
                if (item.Claim is { } stored && stored.Identity == identity.Id && stored.Marker == marker)
                {
                    item.Claim = null;
                    return Ok(ItemShape(item));
                }
                return live is not null
                    ? Refusal("already_claimed", $"Work item {item.Id} is claimed by someone else.")
                    : Refusal("conflict", $"The caller holds no claim on work item {item.Id} under this marker.");
            }
            case "update_work_item":
            {
                if (arguments["expected_version"] is { } expected && expected.GetValue<long>() != item.Version)
                {
                    return Refusal("conflict", $"Work item {item.Id} is at version {item.Version}.");
                }
                if (arguments.Count(a => a.Key is not ("itemId" or "expected_version")) == 0)
                {
                    return Refusal("invalid_argument", "update_work_item needs a field to change.");
                }
                if (arguments["status"] is { } status)
                {
                    item.Status = status.GetValue<string>();
                }
                item.Version++;
                return Ok(ItemShape(item));
            }
            case "add_work_item_comment":
            {
                var key = arguments["idempotency_key"]?.GetValue<string>();
                if (key is not null && (key.Length is 0 or > 128 || key.Any(ch => ch is <= ' ' or > '~')))
                {
                    return Refusal("invalid_argument", "idempotency_key is 1 to 128 printable ASCII characters without spaces.");
                }
                var earlier = key is null ? null : item.Comments.FirstOrDefault(m => m.Key == key && m.AuthorId == identity.Id);
                var comment = earlier ?? new Comment(Guid.NewGuid(), identity.Name, identity.Id, arguments["body"]!.GetValue<string>(), key);
                if (earlier is null)
                {
                    item.Comments.Add(comment);
                }
                var shape = ItemShape(item);
                shape["comment_id"] = comment.Id.ToString();
                shape["comment_created"] = earlier is null;
                return Ok(shape);
            }
            case "add_work_item_link":
            {
                var (kind, url) = (arguments["kind"]!.GetValue<string>(), arguments["url"]!.GetValue<string>());
                if (kind is not ("pr" or "branch" or "other") || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    return Refusal("invalid_argument", "kind is pr, branch or other, and url an absolute http or https URL.");
                }
                if (!item.Links.Contains((kind, url)))
                {
                    item.Links.Add((kind, url));
                }
                return Ok(ItemShape(item));
            }
            default:
                throw new InvalidOperationException($"The fake KanbanBoard does not answer {tool}.");
        }
    }

    private JsonObject ListItems(JsonObject arguments)
    {
        var matching = Items.Where(i => i.Status != "Closed" || arguments["status"]?.GetValue<string>() == "Closed").ToList();
        if (arguments["projectId"] is not null)
        {
            matching = matching.Where(i => i.ProjectId == Id(arguments, "projectId")).ToList();
        }
        if (arguments["status"] is { } status)
        {
            matching = matching.Where(i => i.Status == status.GetValue<string>()).ToList();
        }
        if (arguments["unblocked_only"]?.GetValue<bool>() == true)
        {
            matching = matching.Where(i => i.Blockers.All(b => b.Status == "Done")).ToList();
        }
        if (arguments["claim_state"]?.GetValue<string>() is { } claimState)
        {
            if (claimState is not ("unclaimed" or "claimed" or "expired"))
            {
                return Refusal("invalid_argument", "claim_state is unclaimed, claimed or expired.");
            }
            bool Live(Item i) => i.Claim is { } c && c.Expires > Time.Now;
            matching = matching.Where(i => claimState switch
            {
                "unclaimed" => !Live(i),
                "claimed" => Live(i),
                _ => i.Claim is not null && !Live(i),
            }).ToList();
        }
        var comments = arguments["include_comments"]?.GetValue<bool>() != false;
        if (arguments["limit"] is null && arguments["cursor"] is null)
        {
            return Ok(new JsonArray(matching.Select(i => (JsonNode)ItemShape(i, comments)).ToArray()));
        }
        var limit = arguments["limit"]?.GetValue<int>() ?? 50;
        if (limit is < 1 or > 200)
        {
            return Refusal("invalid_argument", "limit is 1 to 200.");
        }
        var from = 0;
        if (arguments["cursor"] is { } cursor && (!cursor.GetValue<string>().StartsWith("items:", StringComparison.Ordinal)
            || !int.TryParse(cursor.GetValue<string>()["items:".Length..], out from)))
        {
            return Refusal("invalid_argument", "The cursor is malformed.");
        }
        var page = new JsonObject { ["items"] = new JsonArray(matching.Skip(from).Take(limit).Select(i => (JsonNode)ItemShape(i, comments)).ToArray()) };
        if (from + limit < matching.Count)
        {
            page["next_cursor"] = $"items:{from + limit}";
        }
        return Ok(page);
    }

    private static Guid Id(JsonObject arguments, string name) =>
        Guid.TryParse(arguments[name]?.GetValue<string>(), out var id) ? id : Guid.Empty;

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", System.Globalization.CultureInfo.InvariantCulture);

    private JsonObject ProjectShape(Project p) => new()
    {
        ["id"] = p.Id.ToString(), ["name"] = $"Project {p.Key}", ["key"] = p.Key, ["isArchived"] = p.Archived,
        ["createdAtUtc"] = Stamp(Time.Now), ["totalItems"] = Items.Count(i => i.ProjectId == p.Id),
        ["openItems"] = Items.Count(i => i.ProjectId == p.Id && i.Status is not ("Done" or "Closed")),
        ["repository_url"] = p.RepositoryUrl, ["default_branch"] = null,
    };

    private JsonObject EpicShape(Epic e)
    {
        var shape = new JsonObject { ["id"] = e.Id.ToString(), ["projectId"] = e.ProjectId.ToString(), ["name"] = e.Name };
        if (e.Description is not null)
        {
            shape["description"] = e.Description;
        }
        shape["isArchived"] = false;
        shape["createdAtUtc"] = Stamp(Time.Now);
        shape["updatedAtUtc"] = Stamp(Time.Now);
        return shape;
    }

    /// <summary>The contract's Work item shape, its fields in the server's order.</summary>
    private JsonObject ItemShape(Item i, bool comments = true)
    {
        var live = i.Claim is { } c && c.Expires > Time.Now ? i.Claim : null;
        var shape = new JsonObject { ["id"] = i.Id.ToString(), ["projectId"] = i.ProjectId.ToString() };
        if (i.EpicId is { } epicId)
        {
            shape["epicId"] = epicId.ToString();
            shape["epic"] = new JsonObject { ["id"] = epicId.ToString(), ["name"] = Epics.First(e => e.Id == epicId).Name };
        }
        shape["title"] = i.Title;
        if (i.Description is not null)
        {
            shape["description"] = i.Description;
        }
        shape["type"] = i.Type;
        shape["status"] = i.Status;
        shape["priority"] = "Medium";
        shape["order"] = Items.IndexOf(i);
        shape["estimate"] = i.Estimate;
        if (i.Labels.Count > 0)
        {
            shape["labels"] = string.Join(",", i.Labels);
        }
        shape["comments"] = new JsonArray(comments
            ? i.Comments.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id.ToString(), ["workItemId"] = i.Id.ToString(), ["author"] = m.Author, ["body"] = m.Body,
                ["createdAtUtc"] = Stamp(Time.Now), ["author_id"] = m.AuthorId.ToString(),
            }).ToArray()
            : Array.Empty<JsonNode>());
        shape["createdAtUtc"] = Stamp(Time.Now);
        shape["updatedAtUtc"] = Stamp(Time.Now);
        shape["claimant"] = live?.Name;
        shape["claimant_id"] = live?.Identity.ToString();
        shape["claim_marker"] = live?.Marker;
        shape["claim_expires_at_utc"] = live is { } l ? Stamp(l.Expires) : null;
        shape["blockers"] = new JsonArray(i.Blockers.Select(b => (JsonNode)new JsonObject { ["id"] = b.Id.ToString(), ["title"] = b.Title, ["status"] = b.Status }).ToArray());
        shape["dependents"] = new JsonArray(Items.Where(d => d.Blockers.Contains(i))
            .Select(d => (JsonNode)new JsonObject { ["id"] = d.Id.ToString(), ["title"] = d.Title, ["status"] = d.Status }).ToArray());
        shape["links"] = new JsonArray(i.Links.Select(k => (JsonNode)new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["kind"] = k.Kind, ["url"] = k.Url, ["created_at_utc"] = Stamp(Time.Now),
        }).ToArray());
        shape["version"] = i.Version;
        shape["label_list"] = new JsonArray(i.Labels.Select(l => (JsonNode)l).ToArray());
        return shape;
    }
}
