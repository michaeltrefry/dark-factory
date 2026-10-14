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
/// from the recording. Afterwards — also when a scenario fails or the caller cancels — the project is deleted
/// (<c>delete_project</c>, which takes its epics, documents and items), by the id the board gave THIS run for it: the only hard
/// delete the recorder ever sends. A delete the board refuses falls back to archiving it (<c>update_project is_archived</c>).
/// Then the board is read again (<see cref="LeftBehindAsync"/>) and anything of the recorder's that is neither deleted nor
/// archived fails the run. The clean-up runs on a token of its own (<see cref="CleanupTimeout"/>), so a cancelled or timed-out
/// run still cleans up, and a clean-up that itself fails is reported as a leftover naming the project.
/// Before it starts it looks for what an earlier run that died left (<see cref="SweepAsync"/>) and never deletes it: a project
/// that is exactly a recording's (<see cref="NotARecordingAsync"/>) is archived — reversible — and named in <see cref="Notes"/>
/// for the owner to delete; one that only looks like it (both prefixes, but not exactly) is left untouched and fails the run.
/// The contract gives a project no creator or owner, so "made by this token" cannot be part of that test.
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
    private string? _key;
    private Guid _project;
    private Guid _epic;
    private int _created;

    /// <summary>How long the clean-up may take, on a token of its own: it does not end with the caller's.</summary>
    public static readonly TimeSpan CleanupTimeout = 2 * OutboundHttp.KanbanTimeout;

    /// <summary>The project this run created (empty until it has), and every work item made in it.</summary>
    public Guid Project => _project;
    public IReadOnlyList<Guid> Items => _items;

    /// <summary>The key of this run's project: the throwaway prefix and 8 hex digits of the run's id.</summary>
    public string ProjectKey => _key ??= KanbanFixtureGuard.KeyPrefix + _run[..8].ToUpperInvariant();

    /// <summary>
    /// The identities this run met: one set for every scenario's recording handler, seeded from the recorder's own first claim
    /// (before any scenario runs), so a refusal that names the token's identity is scrubbed in whichever scenario it comes.
    /// </summary>
    public KanbanIdentities Identities { get; } = new();

    /// <summary>What the sweep found of earlier runs, for whoever runs the recorder to read (<see cref="SweepAsync"/>).</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>
    /// Records every scenario and returns the fixtures by file name. The board is cleaned up and checked before it returns or
    /// throws: a run that left something behind throws naming it, whatever else happened.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, KanbanFixture>> RecordAsync(CancellationToken ct)
    {
        Notes = await SweepAsync(ct);
        var key = ProjectKey;
        var fixtures = new Dictionary<string, KanbanFixture>();
        Exception? failed = null;
        try
        {
            var project = await RawAsync("create_project", new JsonObject
            {
                ["name"] = KanbanFixtureGuard.ProjectName,
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
        // On a token of its own: the caller's may be why the scenarios stopped (Ctrl-C, a test timeout), and the board is
        // cleaned up all the same.
        IReadOnlyList<string> left;
        using (var own = new CancellationTokenSource(CleanupTimeout))
        {
            try
            {
                left = await RemoveAsync(own.Token);
            }
            catch (Exception ex)
            {
                var which = _project == Guid.Empty ? $"a project with the key {key}, if the board created it," : $"project {_project:D} ({key})";
                left = [$"{which} may still be on the board, unarchived: the clean-up itself failed ({ex.GetType().Name}: {ex.Message})"];
            }
        }
        if (left.Count > 0)
        {
            var message = $"The Kanban fixture recorder left something on the board: {string.Join("; ", left)}. Remove it by hand "
                + $"(this run's project is '{KanbanFixtureGuard.ProjectName}' with the key {key}"
                + (_project == Guid.Empty ? "" : $" and the id {_project:D}") + "; the next run archives it if it is still there).";
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
        var recording = new KanbanRecordingHandler(fixture)
        {
            InnerHandler = transport, OwnProjects = [_project], OwnEpics = [_epic], Identities = Identities,
        };
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
        if (Identities.Names.Count == 0)
        {
            // The first scenario's set-up claims an item as the recorder; a reordering that loses that must not go unnoticed.
            throw new InvalidOperationException("The recorder has not learnt its own identity's name (no claim_work_item of its own before "
                + $"{scenario.Fixture}); a refusal naming it would be recorded as it is.");
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
        string? refused = null;
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
                    // Not a leftover yet: an answer may be lost after the write happened (the delete did delete, and the
                    // archive then finds no project). The board, read again below, says whether the project is still there.
                    refused = $"it could be neither deleted ({ex.Message}) nor archived ({archive.Message})";
                }
            }
        }
        return await LeftBehindAsync(ct, refused);
    }

    /// <summary>
    /// What of the recorder's is still on the board and is neither deleted nor archived: an unarchived project that is this
    /// run's (by the id the board gave it) or carries both throwaway prefixes, and a work item of this
    /// run that can still be read anywhere but in this run's archived project. Empty: nothing but archived or deleted
    /// throwaways is left. <paramref name="refused"/>: why this run's project could not be removed, said with it if it is
    /// still listed.
    /// </summary>
    public async Task<IReadOnlyList<string>> LeftBehindAsync(CancellationToken ct, string? refused = null)
    {
        var left = new List<string>();
        var projects = (await RawAsync("list_projects", new JsonObject { ["includeArchived"] = true }, ct))!.AsArray().OfType<JsonObject>().ToList();
        // A project the board made for this run without this run learning its id (the answer was lost) is not Mine, but it
        // carries this run's name and key, so it looks like a throwaway and is reported below all the same.
        bool Mine(JsonObject p) => _project != Guid.Empty && Id(p) == _project;
        var archived = projects.Where(p => Mine(p) && Archived(p)).Select(Id).ToHashSet();
        left.AddRange(projects.Where(p => (Mine(p) || LooksThrowaway(p)) && !Archived(p))
            .Select(p => $"project {Id(p):D} ({Text(p, "key")}) is neither deleted nor archived" + (Mine(p) && refused is not null ? $": {refused}" : "")));
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
    /// What an earlier run that died left, which this run did not create and so never deletes. Every project that looks like a
    /// throwaway (<see cref="LooksThrowaway"/>: both prefixes) is examined (<see cref="NotARecordingAsync"/>). One that is
    /// exactly a recording's is archived if it is not yet (<c>update_project is_archived</c>: reversible, and the only write the
    /// sweep makes) and named — key and id — in the notes returned, for the owner to delete by hand. One that is not is left
    /// untouched, and the run fails naming it before anything is written: nothing is archived or created then. No project
    /// without both prefixes is read into or written.
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepAsync(CancellationToken ct)
    {
        var projects = (await RawAsync("list_projects", new JsonObject { ["includeArchived"] = true }, ct))!.AsArray().OfType<JsonObject>();
        var (leftovers, strangers) = (new List<JsonObject>(), new List<string>());
        foreach (var candidate in projects.Where(LooksThrowaway).ToList())
        {
            if (await NotARecordingAsync(candidate, ct) is { } why)
            {
                strangers.Add($"project {Id(candidate):D} ({Text(candidate, "key")}): {why}");
            }
            else
            {
                leftovers.Add(candidate);
            }
        }
        if (strangers.Count > 0)
        {
            throw new InvalidOperationException("The Kanban fixture recorder did not start, and wrote nothing: the board holds what looks like a "
                + $"throwaway of its own (a name starting '{KanbanFixtureGuard.Prefix}' and a key starting '{KanbanFixtureGuard.KeyPrefix}') but is "
                + $"not exactly a recording's leftover, so it was left untouched — {string.Join("; ", strangers)}. Rename it, or remove it by hand.");
        }
        var notes = new List<string>();
        foreach (var leftover in leftovers)
        {
            var was = Archived(leftover);
            if (!was)
            {
                await RawAsync("update_project", new JsonObject { ["id"] = Id(leftover).ToString("D"), ["is_archived"] = true }, ct);
            }
            notes.Add($"An earlier recording's leftover project {Text(leftover, "key")} (id {Id(leftover):D}) "
                + (was ? "is still on the board, archived" : "was archived (not deleted)") + ": delete it by hand when you are sure it is not needed.");
        }
        return notes;
    }

    /// <summary>
    /// Why <paramref name="project"/> is not exactly what a recording run leaves — null when it is: its name is
    /// <see cref="KanbanFixtureGuard.ProjectName"/>, its key the throwaway prefix and 8 upper-case hex digits, and everything in
    /// it — every work item (closed ones and those of archived epics included), every epic and every epic document — is titled
    /// with the throwaway prefix. (A project carries no creator in the contract, so who made it cannot be asked.) Nothing of
    /// what it holds is repeated in the answer, only counted: the answer goes into an exception's message.
    /// </summary>
    public async Task<string?> NotARecordingAsync(JsonObject project, CancellationToken ct)
    {
        if (Text(project, "name") != KanbanFixtureGuard.ProjectName)
        {
            return $"its name is not exactly '{KanbanFixtureGuard.ProjectName}'";
        }
        if (Text(project, "key") is not { } key || !KanbanFixtureGuard.IsRecorderKey(key))
        {
            return $"its key is not '{KanbanFixtureGuard.KeyPrefix}' and 8 upper-case hex digits";
        }
        static int Foreign(JsonNode? list, string field) =>
            list!.AsArray().Count(e => Text(e, field)?.StartsWith(KanbanFixtureGuard.Prefix, StringComparison.Ordinal) != true);
        var id = Id(project).ToString("D");
        var items = Foreign(await RawAsync("list_work_items", new JsonObject { ["projectId"] = id, ["includeArchivedEpics"] = true }, ct), "title")
            + Foreign(await RawAsync("list_work_items", new JsonObject { ["projectId"] = id, ["includeArchivedEpics"] = true, ["status"] = KanbanStatus.Closed }, ct), "title");
        if (items > 0)
        {
            return $"it holds {items} work item(s) not titled '{KanbanFixtureGuard.Prefix} …'";
        }
        var epics = (await RawAsync("list_epics", new JsonObject { ["projectId"] = id, ["includeArchived"] = true }, ct))!.AsArray();
        if (Foreign(epics, "name") is > 0 and var named)
        {
            return $"it holds {named} epic(s) not named '{KanbanFixtureGuard.Prefix} …'";
        }
        foreach (var epic in epics)
        {
            if (Foreign(await RawAsync("list_epic_documents", new JsonObject { ["epicId"] = Id(epic).ToString("D") }, ct), "title") is > 0 and var documents)
            {
                return $"it holds {documents} epic document(s) not titled '{KanbanFixtureGuard.Prefix} …'";
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="project"/> carries both throwaway prefixes: worth a look, and never enough to write to it.</summary>
    private static bool LooksThrowaway(JsonObject project) =>
        Text(project, "name")?.StartsWith(KanbanFixtureGuard.Prefix, StringComparison.Ordinal) == true
        && Text(project, "key")?.StartsWith(KanbanFixtureGuard.KeyPrefix, StringComparison.Ordinal) == true;

    private static bool Archived(JsonObject project) => project["isArchived"] is JsonValue v && v.TryGetValue<bool>(out var archived) && archived;

    private static string? Text(JsonNode? shape, string field) =>
        shape is JsonObject o && o[field] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

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

    /// <summary>
    /// Claims <paramref name="item"/> under the other marker. The answer names the token's own identity (one token is one
    /// identity): its name and id are learnt here, from the recorder's first claim on, so every handler scrubs them.
    /// </summary>
    private async Task ClaimAsOtherAsync(Guid item, CancellationToken ct)
    {
        var claimed = await RawAsync("claim_work_item", new JsonObject { ["id"] = item.ToString("D"), ["marker"] = OtherMarker, ["ttl_seconds"] = 3600 }, ct);
        Identities.Learn(Text(claimed, "claimant"));
        if (Text(claimed, "claimant_id") is { } id)
        {
            Identities.Placeholder(id);
        }
    }

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
