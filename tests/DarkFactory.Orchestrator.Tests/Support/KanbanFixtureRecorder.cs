using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// The Kanban adapter as a contract scenario drives it (sc-25986), recording or replaying: a <see cref="KanbanWorkSource"/> over
/// <paramref name="handler"/> with a fresh in-memory ledger — so the <c>kb-&lt;n&gt;</c> keys a scenario is given are the same in
/// the recording and in every replay — watching the one project <paramref name="projectKey"/>, and a typed caller that does not
/// sleep (<see cref="Waits"/>).
/// </summary>
public sealed class KanbanContractSession(HttpMessageHandler handler, Uri endpoint, string token, string projectKey, int? pageSize = null)
{
    private readonly DbContextOptions<LedgerDbContext> _options =
        new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private KanbanWorkSource? _source;

    public KanbanKeys Keys => new(new LedgerDbContextFactory(_options));

    /// <summary>The backoffs the typed caller waited (it does not sleep here), in order.</summary>
    public List<TimeSpan> Waits { get; } = [];

    /// <summary>
    /// What happens on the board between two of the adapter's calls that the adapter does not do itself (another caller taking
    /// a claim): the recorder does it for real; a replay does nothing, since the recorded answers already show it.
    /// </summary>
    public Func<string, Task> Aside { get; set; } = _ => Task.CompletedTask;

    public KanbanWorkSource Source => _source ??= Build();

    private KanbanWorkSource Build()
    {
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = endpoint, Timeout = OutboundHttp.KanbanTimeout };
        var board = new KanbanBoard(KanbanWrites.Client(http, token), (wait, _) =>
        {
            Waits.Add(wait);
            return Task.CompletedTask;
        });
        var (keys, scope, web) = (Keys, new KanbanScope([projectKey]), new Uri("https://kanban.test/"));
        return pageSize is { } size ? new(board, keys, scope, web) { PageSize = size } : new(board, keys, scope, web);
    }

    /// <summary>The ledger's record that this factory claimed the item: its work item with a <c>claimed</c> checkpoint, as Intake leaves it.</summary>
    public async Task RecordClaimedAsync(int id)
    {
        await using var db = new LedgerDbContext(_options);
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync(ItemNaming.Kanban.Source, ItemNaming.Kanban.Format(id), "contract fixture", "acme/widgets", null,
            CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Claimed, null, null, CancellationToken.None);
    }
}

/// <summary>One contract scenario: the fixture it records and replays, and what it asks of the adapter (asserting as it goes).</summary>
public sealed record KanbanScenario(string Fixture, Func<KanbanContractSession, KanbanFixture, Task> Run, int? PageSize = null);

/// <summary>
/// Records the Kanban contract fixtures (sc-25986) by running every scenario of <c>KanbanContractTests.Scenarios</c> — the
/// adapter's whole lifecycle — against the KanbanBoard MCP at <paramref name="endpoint"/>, reached through
/// <paramref name="transport"/> with a full-scope <paramref name="token"/>. The token is sent as each request's bearer header
/// and is nowhere else: not in a fixture (<see cref="KanbanRecordingHandler"/> records no header), not in a message this class
/// throws, not in a log.
/// What it writes on the board is its own and thrown away: ONE project, created under an idempotency key, named
/// <c>[dark-factory fixture] …</c> with a key <c>DFFIX&lt;8 hex&gt;</c> (<see cref="KanbanFixtureGuard"/>), holding one epic, one
/// epic document and the scenarios' work items; nothing outside that project is written, and reads of anything else are cut
/// from the recording. Afterwards — also when a scenario fails — the project is deleted (<c>delete_project</c>, which takes its
/// epics, documents and items); a delete the board refuses falls back to archiving it (<c>update_project is_archived</c>). Then
/// the board is read again (<see cref="LeftBehindAsync"/>) and anything of the recorder's that is neither deleted nor archived
/// fails the run. Before it starts it removes what an earlier run that died left (<see cref="SweepAsync"/>: projects with both
/// the throwaway name and key prefix, never anything else).
/// The two refusals the story names: a <c>conflict</c> is real — the recorder edits the item, as another caller, between the
/// adapter's read and its conditional write, so the board refuses the stale <c>expected_version</c>; a <c>busy</c> cannot be
/// provoked on demand (the board answers it only after a write waited 10 s for other writers), so that one interaction is
/// authored from the contract's Error shape and marked (<see cref="KanbanRecordingHandler.SyntheticBusyNote"/>), and the
/// adapter's resend after it is the board's own answer.
/// </summary>
public sealed class KanbanFixtureRecorder(HttpMessageHandler transport, Uri endpoint, string token, string recordedFrom,
    IReadOnlyList<KanbanScenario> scenarios)
{
    /// <summary>The repository the throwaway project names: the factory's sandbox (the item's <c>Repo:</c> line in a spec).</summary>
    public const string Repository = "https://github.com/michaeltrefry/dark-factory-sandbox.git";

    /// <summary>The marker under which the recorder, as "another caller", takes a claim away from the factory's.</summary>
    public const string OtherMarker = "dark-factory-fixture:another-run";

    public const string DropClaim = "drop-claim";
    public const string TakeClaim = "take-claim";

    private readonly McpHttpClient _raw = KanbanWrites.Client(
        new HttpClient(transport, disposeHandler: false) { BaseAddress = endpoint, Timeout = OutboundHttp.KanbanTimeout }, token);
    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly List<Guid> _items = [];
    private Guid _project;
    private Guid _epic;
    private int _created;

    /// <summary>The project this run created (empty until it has), and every work item made in it.</summary>
    public Guid Project => _project;
    public IReadOnlyList<Guid> Items => _items;

    /// <summary>
    /// Records every scenario and returns the fixtures by file name. The board is cleaned up and checked before it returns or
    /// throws: a run that left something behind throws naming it, whatever else happened.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, KanbanFixture>> RecordAsync(CancellationToken ct)
    {
        await SweepAsync(ct);
        var key = KanbanFixtureGuard.KeyPrefix + _run[..8].ToUpperInvariant();
        var fixtures = new Dictionary<string, KanbanFixture>();
        Exception? failed = null;
        try
        {
            var project = await RawAsync("create_project", new JsonObject
            {
                ["name"] = $"{KanbanFixtureGuard.Prefix} contract recording",
                ["key"] = key,
                ["description"] = "Throwaway project of the dark-factory Kanban contract fixtures; deleted after recording.",
                ["repository_url"] = Repository,
                ["idempotency_key"] = $"dark-factory:fixture:{_run}",
            }, ct);
            _project = Id(project);
            var epic = await RawAsync("create_epic", new JsonObject
            {
                ["projectId"] = _project.ToString("D"),
                ["name"] = $"{KanbanFixtureGuard.Prefix} epic",
                ["description"] = "Objective: record what the dark-factory adapter asks of KanbanBoard.",
                ["idempotency_key"] = $"dark-factory:fixture:{_run}:epic",
            }, ct);
            _epic = Id(epic);
            await RawAsync("create_epic_document", new JsonObject
            {
                ["epicId"] = _epic.ToString("D"),
                ["title"] = $"{KanbanFixtureGuard.Prefix} spec",
                ["body"] = "# Spec\n\n## Work sources\n\nShortcut and KanbanBoard plug in through one adapter interface.",
                ["idempotency_key"] = $"dark-factory:fixture:{_run}:document",
            }, ct);
            foreach (var scenario in scenarios)
            {
                fixtures[scenario.Fixture] = await RecordAsync(scenario, key, ct);
            }
        }
        catch (Exception ex)
        {
            failed = ex;
        }
        var left = await RemoveAsync(ct);
        if (left.Count > 0)
        {
            var message = $"The Kanban fixture recorder left something on the board: {string.Join("; ", left)}. Remove it by hand "
                + $"(projects named '{KanbanFixtureGuard.Prefix} …' with a key '{KanbanFixtureGuard.KeyPrefix}…').";
            throw failed is null ? new InvalidOperationException(message) : new AggregateException(message, failed);
        }
        if (failed is not null)
        {
            throw new InvalidOperationException($"The Kanban fixture recording failed (its throwaway project was removed): {failed.Message}", failed);
        }
        return fixtures;
    }

    /// <summary>One scenario: its items made on the board, then the adapter run through a recording handler.</summary>
    private async Task<KanbanFixture> RecordAsync(KanbanScenario scenario, string projectKey, CancellationToken ct)
    {
        var fixture = new KanbanFixture { RecordedFrom = recordedFrom };
        fixture.Set("project", _project);
        fixture.Parameters["projectKey"] = projectKey;
        var recording = new KanbanRecordingHandler(fixture) { InnerHandler = transport, OwnProjects = [_project], OwnEpics = [_epic] };
        var session = new KanbanContractSession(recording, endpoint, token, projectKey, scenario.PageSize);
        switch (scenario.Fixture)
        {
            case "list-ready.json":
            {
                // Three ready items (two pages of two), and three the board's own ready filter leaves out: one in the backlog,
                // one blocked by it, one another caller holds.
                var ready = new List<Guid>();
                for (var n = 1; n <= 3; n++)
                {
                    ready.Add(await ItemAsync($"ready {n}", KanbanStatus.Ready, ct));
                }
                var backlog = await ItemAsync("in the backlog", KanbanStatus.Backlog, ct);
                var blocked = await ItemAsync("blocked", KanbanStatus.Ready, ct);
                await RawAsync("add_work_item_dependency", new JsonObject { ["id"] = blocked.ToString("D"), ["blocked_by_id"] = backlog.ToString("D") }, ct);
                var held = await ItemAsync("held by another caller", KanbanStatus.Ready, ct);
                await ClaimAsOtherAsync(held, ct);
                fixture.Set("ready", ready);
                fixture.Set("left out", [backlog, blocked, held]);
                break;
            }
            case "lifecycle.json":
                fixture.Set("item", await ItemAsync("lifecycle", KanbanStatus.Ready, ct));
                break;
            case "read-spec.json":
                fixture.Set("epic", _epic);
                fixture.Set("inEpic", await ItemAsync("item in an epic", KanbanStatus.Ready, ct, epic: true, type: "Story", label: "simple", estimate: 2));
                fixture.Set("plain", await ItemAsync("item without an epic", KanbanStatus.Ready, ct));
                break;
            case "claims.json":
            {
                var (lapsed, lost) = (await ItemAsync("claim that lapses", KanbanStatus.Ready, ct), await ItemAsync("claim that is lost", KanbanStatus.Ready, ct));
                fixture.Set("lapsed", lapsed);
                fixture.Set("lost", lost);
                session.Aside = async step =>
                {
                    switch (step)
                    {
                        case DropClaim:
                            // What a lapse leaves (no claim) without waiting a claim's lifetime out: the factory's claim released.
                            await ReleaseAsync(lapsed, KanbanWorkSource.Marker(lapsed), ct);
                            break;
                        case TakeClaim:
                            // One token is one identity: "someone else" is another marker, which the board holds the item for alike.
                            await ReleaseAsync(lost, KanbanWorkSource.Marker(lost), ct);
                            await ClaimAsOtherAsync(lost, ct);
                            break;
                        default:
                            throw new InvalidOperationException($"The recorder knows no step '{step}'.");
                    }
                };
                break;
            }
            case "plan.json":
                fixture.Set("parent", await ItemAsync("plan item", KanbanStatus.Ready, ct, epic: true, type: "Story", label: "plan"));
                break;
            case "conflict.json":
            {
                var item = await ItemAsync("edited under the factory", KanbanStatus.Backlog, ct);
                fixture.Set("item", item);
                // Someone else edits the item between the adapter's read and its conditional move: a real stale expected_version.
                var edited = false;
                recording.BeforeCall = async (tool, _) =>
                {
                    if (tool == "update_work_item" && !edited)
                    {
                        edited = true;
                        await RawAsync("update_work_item", new JsonObject
                        {
                            ["itemId"] = item.ToString("D"), ["title"] = $"{KanbanFixtureGuard.Prefix} edited by someone else",
                        }, ct);
                    }
                };
                break;
            }
            case "busy.json":
            {
                fixture.Set("item", await ItemAsync("moved after a busy answer", KanbanStatus.Backlog, ct));
                var refused = false;
                recording.SyntheticBusy = (tool, _) => tool == "update_work_item" && !refused && (refused = true);
                break;
            }
            default:
                throw new InvalidOperationException($"The recorder does not know how to set the board up for {scenario.Fixture}.");
        }
        await scenario.Run(session, fixture);
        if (fixture.Parameters.ContainsKey("children"))
        {
            // The items the adapter itself created (a plan's): the recorder's to remove like its own.
            _items.AddRange(fixture.Ids("children"));
        }
        return fixture;
    }

    /// <summary>
    /// Deletes the project of this run (archives it when the delete is refused) and reads the board again; returns what is
    /// still there and should not be (<see cref="LeftBehindAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<string>> RemoveAsync(CancellationToken ct)
    {
        if (_project != Guid.Empty)
        {
            try
            {
                await RawAsync("delete_project", new JsonObject { ["projectId"] = _project.ToString("D") }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await RawAsync("update_project", new JsonObject { ["id"] = _project.ToString("D"), ["is_archived"] = true }, ct);
                }
                catch (Exception archive) when (archive is not OperationCanceledException)
                {
                    return [$"project {_project:D} could be neither deleted ({ex.Message}) nor archived ({archive.Message})"];
                }
            }
        }
        return await LeftBehindAsync(ct);
    }

    /// <summary>
    /// What of the recorder's is still on the board and is neither deleted nor archived: an unarchived project that is this
    /// run's or carries both throwaway prefixes, and a work item of this run that can still be read outside an archived
    /// throwaway project. Empty: nothing but archived or deleted throwaways is left.
    /// </summary>
    public async Task<IReadOnlyList<string>> LeftBehindAsync(CancellationToken ct)
    {
        var left = new List<string>();
        var projects = (await RawAsync("list_projects", new JsonObject { ["includeArchived"] = true }, ct))!.AsArray().OfType<JsonObject>().ToList();
        var archived = projects.Where(p => IsThrowaway(p) && p["isArchived"]?.GetValue<bool>() == true).Select(Id).ToHashSet();
        left.AddRange(projects.Where(p => (IsThrowaway(p) || Id(p) == _project) && !archived.Contains(Id(p)))
            .Select(p => $"project {Id(p):D} ({p["key"]}) is neither deleted nor archived"));
        foreach (var item in _items)
        {
            var read = await _raw.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = item.ToString("D") }, ct);
            if (read.IsError)
            {
                if (Code(read) != KanbanException.NotFound)
                {
                    left.Add($"work item {item:D} could not be checked ({Code(read) ?? "an error"})");
                }
            }
            else if (KanbanMcp.TryParse(read.Text) is not JsonObject found || !archived.Contains(Guid.Parse(found["projectId"]!.GetValue<string>())))
            {
                left.Add($"work item {item:D} is still on the board");
            }
        }
        return left;
    }

    /// <summary>
    /// Deletes every project an earlier run left: those — archived ones included — whose name starts with the throwaway prefix
    /// AND whose key starts with the throwaway key prefix. No other project is touched.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var projects = (await RawAsync("list_projects", new JsonObject { ["includeArchived"] = true }, ct))!.AsArray().OfType<JsonObject>();
        foreach (var stale in projects.Where(IsThrowaway).ToList())
        {
            await RawAsync("delete_project", new JsonObject { ["projectId"] = Id(stale).ToString("D") }, ct);
        }
    }

    private static bool IsThrowaway(JsonObject project) =>
        project["name"]?.GetValue<string>().StartsWith(KanbanFixtureGuard.Prefix, StringComparison.Ordinal) == true
        && project["key"]?.GetValue<string>().StartsWith(KanbanFixtureGuard.KeyPrefix, StringComparison.Ordinal) == true;

    private static Guid Id(JsonNode? shape) => Guid.Parse(shape!["id"]!.GetValue<string>());

    private static string? Code(McpToolResult result) =>
        (result.Structured as JsonObject ?? KanbanMcp.TryParse(result.Text) as JsonObject)?["code"] is JsonValue c && c.TryGetValue<string>(out var code) ? code : null;

    /// <summary>A throwaway work item in this run's project, under an idempotency key of its own.</summary>
    private async Task<Guid> ItemAsync(string title, string status, CancellationToken ct, bool epic = false, string type = "Task",
        string? label = null, int? estimate = null)
    {
        var arguments = new JsonObject
        {
            ["projectId"] = _project.ToString("D"),
            ["title"] = $"{KanbanFixtureGuard.Prefix} {title}",
            ["type"] = type,
            ["status"] = status,
            ["description"] = "Throwaway item of the dark-factory Kanban contract fixtures.",
            ["idempotency_key"] = $"dark-factory:fixture:{_run}:item:{++_created}",
        };
        if (epic)
        {
            arguments["epicId"] = _epic.ToString("D");
        }
        if (label is not null)
        {
            arguments["label_list"] = new JsonArray(label);
        }
        if (estimate is { } points)
        {
            arguments["estimate"] = points;
        }
        var id = Id(await RawAsync("create_work_item", arguments, ct));
        _items.Add(id);
        return id;
    }

    private Task ClaimAsOtherAsync(Guid item, CancellationToken ct) =>
        RawAsync("claim_work_item", new JsonObject { ["id"] = item.ToString("D"), ["marker"] = OtherMarker, ["ttl_seconds"] = 3600 }, ct);

    private Task ReleaseAsync(Guid item, string marker, CancellationToken ct) =>
        RawAsync("release_work_item_claim", new JsonObject { ["id"] = item.ToString("D"), ["marker"] = marker }, ct);

    /// <summary>One call of the recorder's own (not recorded); a refusal throws naming the tool and what the board said, never the token.</summary>
    private async Task<JsonNode?> RawAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var result = await _raw.CallToolAsync(tool, arguments, ct);
        if (result.IsError)
        {
            throw new InvalidOperationException($"The recorder's own {tool} was refused: {(result.Text.Length > 300 ? result.Text[..300] : result.Text)}");
        }
        return KanbanMcp.TryParse(result.Text) ?? throw new InvalidOperationException($"The recorder's own {tool} answered no JSON.");
    }
}
