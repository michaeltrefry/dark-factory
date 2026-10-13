using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Contract tests: every <see cref="IWorkSource"/> operation of <see cref="ShortcutWorkSource"/>
/// replayed against recorded Shortcut API fixtures (Fixtures/shortcut). Requests must match the
/// recording exactly. Re-record with <c>SHORTCUT_RECORD=1</c> (see <see cref="ShortcutFixtureRecorder"/>).
/// </summary>
public class ShortcutContractTests
{
    internal static readonly WatchScope Scope = new(["darkfactory"], [25171]);
    internal const string PrUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/pull/999";
    internal const string BranchUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/tree/factory/sc-999";

    internal static async Task<IReadOnlyList<int>> WriteLifecycle(ShortcutWorkSource source, int parent)
    {
        var ct = CancellationToken.None;
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(parent, ignoreScope: false, ct)); // checked, written, read back
        await source.ReleaseAsync(parent, ct);
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(parent, ignoreScope: false, ct));
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(parent, ignoreScope: false, ct)); // already claimed: read only
        Assert.True(await source.InScopeAsync(parent, ct));
        await source.ReportStateAsync(parent, BoardState.Claimed, null, ct);
        await source.CommentAsync(parent, "contract test comment", ct);
        await source.LinkAsync(parent, [PrUrl, BranchUrl], ct);
        await source.LinkAsync(parent, [PrUrl], ct); // already linked: read only
        var children = await source.CreateChildrenAsync(parent, Children, ct);
        await source.ReportStateAsync(parent, BoardState.Merged, null, ct);
        await source.ReportStateAsync(parent, BoardState.Stopped, "stopped by the contract test", ct);
        return children;
    }

    /// <summary>The two stories <see cref="WriteLifecycle"/> creates from its parent: the second is blocked by the first.</summary>
    internal static readonly IReadOnlyList<ChildItem> Children =
    [
        new("[dark-factory fixture] child 1", "First step of the plan.", "chore", []),
        new("[dark-factory fixture] child 2", "Second step; needs the first.", "chore", [0]),
    ];

    /// <summary>
    /// What a plan's retry and release ask of the board (sc-25877), on the <paramref name="children"/> <see cref="WriteLifecycle"/>
    /// left in the Backlog: a second <see cref="ShortcutWorkSource.CreateChildrenAsync"/> finds both by their external id in the
    /// listing and creates nothing; the second child's blockers are read from its <c>story_links</c>; it is moved Backlog → To Do
    /// by a <c>PUT</c> of <c>workflow_state_id</c> alone, and is then no longer in the Backlog.
    /// </summary>
    internal static async Task ReleaseLifecycle(ShortcutWorkSource source, int parent, IReadOnlyList<int> children)
    {
        var ct = CancellationToken.None;
        Assert.Equal(children, await source.CreateChildrenAsync(parent, Children, ct));
        Assert.Equal([new Blocker(children[0], false)], await source.BlockersAsync(children[1], ct));
        Assert.Equal(ReadyResult.Moved, await source.MakeReadyAsync(children[1], ct));
        Assert.False(await source.InBacklogAsync(children[1], ct));
        Assert.Equal(ReadyResult.AlreadyLeft, await source.MakeReadyAsync(children[1], ct));
    }

    private static (ShortcutWorkSource Source, ReplayHandler Replay) Replay(ShortcutFixture fixture, WatchScope? scope = null)
    {
        var replay = new ReplayHandler(fixture);
        var http = new HttpClient(replay) { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        return (new ShortcutWorkSource(http, "tok-test", scope ?? Scope), replay);
    }

    private static long StateId(ShortcutFixture fixture, string name) =>
        fixture.Interactions.First(i => i.Path == "/api/v3/workflows").Response!.AsArray()
            .SelectMany(w => w!["states"]!.AsArray()).First(s => (string?)s!["name"] == name)!["id"]!.GetValue<long>();

    private static List<Interaction> Calls(ShortcutFixture fixture, string method, string path) =>
        fixture.Interactions.Where(i => i.Method == method && i.Path == path).ToList();

    [Fact]
    public async Task List_ready_returns_to_do_stories_in_the_watched_team_or_epic()
    {
        var fixture = ShortcutFixture.Load("list-ready.json");
        var (source, replay) = Replay(fixture);

        var ready = await source.ListReadyAsync(CancellationToken.None);

        Assert.True(replay.AllReplayed);
        Assert.Equal(fixture.Parameters["expected"]!.AsArray().Select(n => n!.GetValue<int>()), ready);
        Assert.NotEmpty(ready);
        // Only the watched team and epic are listed; the ready ones are the listed To Do stories, unarchived,
        // not held by another claimant.
        var team = fixture.Interactions.Single(i => i.Path == "/api/v3/groups").Response!.AsArray().Single()!["id"]!.GetValue<string>();
        var listings = fixture.Interactions.Where(i => i.Path.EndsWith("/stories")).ToList();
        Assert.Equal([$"/api/v3/groups/{team}/stories", "/api/v3/epics/25171/stories"], listings.Select(l => l.Path));
        var toDo = StateId(fixture, "To Do");
        var listed = listings.SelectMany(l => l.Response!.AsArray()).ToList();
        var me = fixture.Interactions.Single(i => i.Path == "/api/v3/member").Response!["id"]!.GetValue<string>();
        bool HeldByAnother(JsonNode s) =>
            s["labels"]!.AsArray().Any(l => (string?)l!["name"] == ShortcutWorkSource.ClaimLabel)
            && !s["owner_ids"]!.AsArray().Any(o => (string?)o == me);
        Assert.Equal(listed.Where(s => s!["workflow_state_id"]!.GetValue<long>() == toDo && !s["archived"]!.GetValue<bool>() && !HeldByAnother(s))
                .Select(s => s!["id"]!.GetValue<int>()).Distinct().Order(), ready);
        Assert.Contains(listed, s => s!["workflow_state_id"]!.GetValue<long>() != toDo); // the listing is not pre-filtered
    }

    [Fact]
    public async Task Empty_scope_lists_nothing_and_calls_nothing()
    {
        var (source, replay) = Replay(new ShortcutFixture(), WatchScope.None);
        Assert.Empty(await source.ListReadyAsync(CancellationToken.None));
        Assert.True(replay.AllReplayed);
    }

    [Fact]
    public async Task Read_spec_without_an_epic_is_the_story_alone()
    {
        var fixture = ShortcutFixture.Load("read-spec.json");
        var (source, replay) = Replay(fixture);

        var spec = await source.ReadSpecAsync(25185, CancellationToken.None);

        Assert.True(replay.AllReplayed);
        Assert.Equal(25185, spec.Story.Id);
        Assert.Equal("bug", spec.Story.StoryType);
        Assert.StartsWith("https://app.shortcut.com/trefry/story/25185", spec.Story.AppUrl);
        Assert.Null(spec.Epic);
        Assert.Empty(spec.Documents);
        // The recorded story has no labels and no estimate: complex, the mid class (E8).
        Assert.Empty(spec.Story.Labels!);
        Assert.Null(spec.Story.Estimate);
        Assert.Equal(WorkerModelClass.Mid, WorkerModelClass.Coding(spec.Story));
    }

    [Theory]
    [InlineData(2, null, 2, "low")]
    [InlineData(3, null, 3, "mid")]
    [InlineData(null, "simple", null, "low")]
    public async Task Read_spec_carries_the_storys_labels_and_estimate(int? estimate, string? label, int? expected, string modelClass)
    {
        // The recorded story with its estimate and labels set as the v3 schema has them (Story.estimate: integer or null;
        // labels: LabelSlim objects); the request is unchanged, so replay stays strict.
        var fixture = ShortcutFixture.Load("read-spec.json");
        var call = fixture.Interactions.Single();
        var story = call.Response!.DeepClone();
        story["estimate"] = estimate;
        story["labels"] = label is null ? new JsonArray() : new JsonArray(JsonNode.Parse($$"""{"entity_type":"label","id":1,"name":"{{label}}"}"""));
        fixture.Interactions[0] = call with { Response = story };
        var (source, replay) = Replay(fixture);

        var spec = await source.ReadSpecAsync(25185, CancellationToken.None);

        Assert.True(replay.AllReplayed);
        Assert.Equal(expected, spec.Story.Estimate);
        Assert.Equal(label is null ? [] : [label], spec.Story.Labels!);
        Assert.Equal(modelClass, WorkerModelClass.Coding(spec.Story));
    }

    [Fact]
    public async Task Read_spec_includes_the_epic_and_its_documents()
    {
        var fixture = ShortcutFixture.Load("read-spec-epic.json");
        var (source, replay) = Replay(fixture);

        var spec = await source.ReadSpecAsync(25185, CancellationToken.None);

        Assert.True(replay.AllReplayed);
        Assert.Equal(25171, spec.Epic!.Id);
        Assert.Contains("Objective", spec.Epic.Description);
        var doc = Assert.Single(spec.Documents);
        Assert.Equal("Dark Factory Spec", doc.Title);
        Assert.Contains("Work sources", doc.Markdown);
    }

    [Fact]
    public async Task Write_operations_claim_report_comment_link_create_children_and_stop()
    {
        var fixture = ShortcutFixture.Load("write-lifecycle.json");
        var (source, replay) = Replay(fixture);
        var parent = fixture.Int("parent");
        var me = fixture.Interactions.First(i => i.Path == "/api/v3/member").Response!["id"]!.GetValue<string>();
        var story = $"/api/v3/stories/{parent}";

        var children = await WriteLifecycle(source, parent);

        Assert.True(replay.AllReplayed);
        // Each claim that writes reads the story back afterwards.
        var calls = fixture.Interactions;
        var claimPuts = calls.Select((c, i) => (c, i)).Where(x => x.c.Method == "PUT" && x.c.Request!["labels"] is { } l
            && l.AsArray().Any(n => (string?)n!["name"] == ShortcutWorkSource.ClaimLabel)).ToList();
        Assert.Equal(2, claimPuts.Count);
        Assert.All(claimPuts, x => Assert.Equal(("GET", story), (calls[x.i + 1].Method, calls[x.i + 1].Path)));
        var puts = Calls(fixture, "PUT", story).Select(p => p.Request!).ToList();
        // Claim: owner = factory user plus the factory-claimed label.
        Assert.Contains(me, puts[0]["owner_ids"]!.AsArray().Select(o => (string?)o));
        Assert.Contains(ShortcutWorkSource.ClaimLabel, puts[0]["labels"]!.AsArray().Select(l => (string?)l!["name"]));
        // Release removes both.
        Assert.DoesNotContain(me, puts[1]["owner_ids"]!.AsArray().Select(o => (string?)o));
        Assert.Empty(puts[1]["labels"]!.AsArray());
        // Report state: workflow state ids resolved by name.
        var states = puts.Where(p => p["workflow_state_id"] is not null).Select(p => p["workflow_state_id"]!.GetValue<long>()).ToList();
        Assert.Equal([StateId(fixture, "In Progress"), StateId(fixture, "Done"), StateId(fixture, "Backlog")], states);
        var stop = puts.Last();
        Assert.DoesNotContain(me, stop["owner_ids"]!.AsArray().Select(o => (string?)o));
        Assert.DoesNotContain(ShortcutWorkSource.ClaimLabel, stop["labels"]!.AsArray().Select(l => (string?)l!["name"]));
        // Comments are attributed.
        var comments = Calls(fixture, "POST", $"{story}/comments").Select(c => c.Request!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["[author: dark-factory] contract test comment", "[author: dark-factory] stopped by the contract test"], comments);
        // Links: PR + branch as external links, shown on the story afterwards.
        Assert.Equal([PrUrl, BranchUrl], puts.Single(p => p["external_links"] is not null)["external_links"]!.AsArray().Select(l => (string?)l));
        Assert.Contains(PrUrl, Calls(fixture, "PUT", story).Single(p => p.Request!["external_links"] is not null).Response!["external_links"]!.AsArray().Select(l => (string?)l));
        // Two child stories joined by a blocker relation: child 1 blocks child 2. The parent's team is looked up
        // first for children an earlier attempt created; none exist, so both are created.
        Assert.Equal(fixture.Parameters["children"]!.AsArray().Select(c => c!.GetValue<int>()), children);
        var siblings = Assert.Single(fixture.Interactions, i => i.Method == "GET" && i.Path.StartsWith("/api/v3/groups/"));
        // The listing holds an archived story with the first child's name, which is never reused: the child is created anew.
        Assert.Contains(siblings.Response!.AsArray(), s => s!["archived"]!.GetValue<bool>() && (string?)s["name"] == Children[0].Name);
        Assert.DoesNotContain(siblings.Response!.AsArray(), s => !s!["archived"]!.GetValue<bool>() && ((string?)s["name"])!.Contains("child"));
        var posts = Calls(fixture, "POST", "/api/v3/stories");
        Assert.Equal(2, posts.Count);
        // Each is created with its idempotency key as the external id, which the API accepted and returned unchanged (sc-25877).
        Assert.Equal(Children.Select(c => c.Key($"sc-{parent}")), posts.Select(p => p.Request!["external_id"]!.GetValue<string>()));
        Assert.All(posts, p => Assert.Equal(p.Request!["external_id"]!.GetValue<string>(), p.Response!["external_id"]!.GetValue<string>()));
        Assert.All(posts, p => Assert.True(p.Request!["external_id"]!.GetValue<string>().Length >= 92));
        var link = Assert.Single(Calls(fixture, "POST", "/api/v3/story-links"));
        Assert.Equal((children[0], "blocks", children[1]),
            (link.Response!["subject_id"]!.GetValue<int>(), link.Response["verb"]!.GetValue<string>(), link.Response["object_id"]!.GetValue<int>()));
    }

    /// <summary>
    /// A plan's retry and release against the recorded API (sc-25877, <c>release.json</c>, recorded right after
    /// <c>write-lifecycle.json</c> on the same throwaway stories): the listing the retry reads carries each child's external id,
    /// so both are found and none is created; a story read back carries its <c>story_links</c>; the release is one <c>PUT</c>
    /// holding only <c>workflow_state_id</c>.
    /// </summary>
    [Fact]
    public async Task A_retry_finds_its_children_by_external_id_and_a_release_reads_blockers_and_moves_backlog_to_to_do()
    {
        var fixture = ShortcutFixture.Load("release.json");
        var (source, replay) = Replay(fixture);
        var parent = fixture.Int("parent");
        var children = fixture.Parameters["children"]!.AsArray().Select(c => c!.GetValue<int>()).ToList();

        await ReleaseLifecycle(source, parent, children);

        Assert.True(replay.AllReplayed);
        // The retry created nothing and linked nothing: it found its children in the listing, by the key the API returned there.
        Assert.DoesNotContain(fixture.Interactions, i => i.Method == "POST");
        var listing = Assert.Single(fixture.Interactions, i => i.Method == "GET" && i.Path.StartsWith("/api/v3/groups/")).Response!.AsArray();
        for (var i = 0; i < children.Count; i++)
        {
            var listed = Assert.Single(listing, s => s!["id"]!.GetValue<int>() == children[i]);
            Assert.Equal(Children[i].Key($"sc-{parent}"), listed!["external_id"]!.GetValue<string>());
        }
        // A story read back carries its relations: child 1 blocks child 2.
        var blocked = fixture.Interactions.First(i => i.Method == "GET" && i.Path == $"/api/v3/stories/{children[1]}").Response!;
        Assert.Contains(blocked["story_links"]!.AsArray(), l => (string?)l!["verb"] == "blocks"
            && l["subject_id"]!.GetValue<int>() == children[0] && l["object_id"]!.GetValue<int>() == children[1]);
        // The release: one PUT, nothing in it but the To Do state, answered with the story in that state.
        var put = Assert.Single(fixture.Interactions, i => i.Method == "PUT");
        Assert.Equal($"/api/v3/stories/{children[1]}", put.Path);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["workflow_state_id"] = StateId(fixture, "To Do") }, put.Request), put.Request!.ToJsonString());
        Assert.Equal(StateId(fixture, "To Do"), put.Response!["workflow_state_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task Stopping_without_a_comment_is_refused_before_any_call()
    {
        var (source, replay) = Replay(new ShortcutFixture());
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReportStateAsync(1, BoardState.Stopped, " ", CancellationToken.None));
        Assert.True(replay.AllReplayed);
    }

    [Fact]
    public async Task Children_may_only_be_blocked_by_earlier_children()
    {
        var (source, _) = Replay(new ShortcutFixture());
        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateChildrenAsync(1,
            [new("a", "", "chore", [1]), new("b", "", "chore", [])], CancellationToken.None));
    }

    [Fact]
    public async Task Api_errors_name_the_call_and_status()
    {
        var api = new FakeApi();
        var source = new ShortcutWorkSource(api.Client(ShortcutWorkSource.DefaultBaseAddress.ToString()), "tok", Scope);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadSpecAsync(1, CancellationToken.None));
        Assert.StartsWith("Shortcut GET stories/1 failed: 404 Not Found", ex.Message);
    }

    [Fact]
    public void Fixtures_carry_no_token_or_email()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcut")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("sct_", text);
            Assert.DoesNotContain("Shortcut-Token", text);
            Assert.DoesNotMatch(@"[A-Za-z0-9._%+-]+@(?!example\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}", text);
        }
    }

    /// <summary>
    /// The repository is public: no fixture holds a board listing. Every recorded list response (a team's or an epic's stories
    /// above all) holds at most <see cref="RecordingHandler.MaxListedStories"/> entries, and a story listing's entries are the
    /// recorder's own throwaway stories with only the fields the adapter reads — the recorder drops the rest (2026-10-13: a
    /// re-recording had put the team's 155 stories in three fixtures).
    /// </summary>
    [Fact]
    public void Fixtures_hold_no_board_listing_only_the_recordings_own_stories()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "shortcut")))
        {
            var fixture = ShortcutFixture.Load(Path.GetFileName(file));
            foreach (var call in fixture.Interactions.Where(i => i.Response is JsonArray))
            {
                var list = call.Response!.AsArray();
                Assert.True(list.Count <= RecordingHandler.MaxListedStories,
                    $"{Path.GetFileName(file)}: {call.Method} {call.Path} holds {list.Count} entries (at most {RecordingHandler.MaxListedStories}): trim the recording");
                if (RecordingHandler.IsStoryListing(call.Path))
                {
                    Assert.All(list, s => Assert.All(s!.AsObject(), field => Assert.Contains(field.Key, RecordingHandler.ListedStoryFields)));
                    Assert.All(list, s => Assert.StartsWith("[dark-factory fixture]", (string?)s!["name"]));
                }
            }
        }
    }

    [Fact]
    public void A_recorded_listing_keeps_only_the_recordings_own_stories_and_the_fields_the_adapter_reads()
    {
        JsonObject Story(int id, string name) => new()
        {
            ["id"] = id, ["name"] = name, ["archived"] = false, ["workflow_state_id"] = 7, ["external_id"] = null,
            ["story_links"] = new JsonArray(), ["labels"] = new JsonArray(), ["owner_ids"] = new JsonArray("someone"),
            ["requested_by_id"] = "someone", ["follower_ids"] = new JsonArray("someone"), ["position"] = 123, ["stats"] = new JsonObject(),
        };
        var listing = new JsonArray([.. Enumerable.Range(1, 150).Select(i => (JsonNode)Story(i, $"someone else's story {i}"))]);
        listing.Insert(40, Story(1001, "[dark-factory fixture] parent"));
        listing.Add(Story(1002, "[dark-factory fixture] child 1"));

        var trimmed = RecordingHandler.TrimListing(listing, new HashSet<int> { 1002, 1001, 5000 });

        Assert.Equal([1001, 1002], trimmed.Select(s => s!["id"]!.GetValue<int>()));
        Assert.DoesNotContain("someone", trimmed[0]!["name"]!.GetValue<string>() + trimmed[1]!["name"]!.GetValue<string>());
        Assert.All(trimmed, s => Assert.Equal(new[] { "id", "name", "archived", "workflow_state_id", "owner_ids", "labels", "story_links", "external_id" }.Order(),
            s!.AsObject().Select(f => f.Key).Order()));
        Assert.Empty(RecordingHandler.TrimListing(listing, new HashSet<int>()));
        // Which responses are listings: a team's and an epic's stories; one story, or a story's comments, is not.
        Assert.True(RecordingHandler.IsStoryListing("/api/v3/groups/6ac526c4/stories"));
        Assert.True(RecordingHandler.IsStoryListing("/api/v3/epics/25171/stories"));
        Assert.False(RecordingHandler.IsStoryListing("/api/v3/stories/12"));
        Assert.False(RecordingHandler.IsStoryListing("/api/v3/stories"));
        Assert.False(RecordingHandler.IsStoryListing("/api/v3/stories/12/comments"));
    }

    private sealed class Canned(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    /// <summary>
    /// The recording handler trims a listing as it records it, and hands the adapter under recording the trimmed one: the
    /// recording and its replay see the same board. With no story of its own named, a listing is recorded empty.
    /// </summary>
    [Fact]
    public async Task The_recording_handler_stores_and_serves_a_listing_trimmed_to_the_recordings_own_stories()
    {
        const string board = """[{"id":1,"name":"someone else's","position":9},{"id":2,"name":"[dark-factory fixture] parent","position":8}]""";
        var ct = TestContext.Current.CancellationToken;
        foreach (var (path, keep, expected) in new (string, HashSet<int>, string)[]
        {
            ("groups/g/stories", [2], """[{"id":2,"name":"[dark-factory fixture] parent"}]"""),
            ("epics/7/stories", [2], """[{"id":2,"name":"[dark-factory fixture] parent"}]"""),
            ("groups/g/stories", [], "[]"),
        })
        {
            var fixture = new ShortcutFixture();
            using var http = new HttpClient(new RecordingHandler(fixture) { KeepStories = keep, InnerHandler = new Canned(board) })
            {
                BaseAddress = ShortcutWorkSource.DefaultBaseAddress,
            };

            var served = JsonNode.Parse(await http.GetStringAsync(path, ct))!.ToJsonString();

            Assert.Equal(expected, served);
            Assert.Equal(expected, Assert.Single(fixture.Interactions).Response!.ToJsonString());
        }
    }
}

/// <summary>
/// Re-records the contract fixtures against the live Shortcut API. Reads touch team DarkFactory,
/// epic 25171 and story 25185; writes go only to throwaway "[dark-factory fixture]" stories in team
/// DarkFactory with no epic, archived afterwards. Run with SHORTCUT_RECORD=1 and the token in
/// SHORTCUT_API_TOKEN or the keychain. The repository is public: a recorded story listing keeps only the recording's own
/// throwaway stories (<see cref="RecordingHandler.KeepStories"/>, <see cref="RecordingHandler.TrimListing"/>) — the parent, an
/// archived namesake of the first child, and the children — never another story of the board.
/// </summary>
public class ShortcutFixtureRecorder
{
    private const string Team = "darkfactory";

    private static string Token()
    {
        if (Environment.GetEnvironmentVariable("SHORTCUT_RECORD") != "1")
        {
            Assert.Skip("Set SHORTCUT_RECORD=1 to re-record the Shortcut contract fixtures against the live API.");
        }
        return new FactoryOptions(FactoryOptions.LoadConfiguration(), new MacKeychain()).ShortcutApiToken;
    }

    private static ShortcutWorkSource Recording(ShortcutFixture fixture, string token, HashSet<int> keep) =>
        new(new HttpClient(new RecordingHandler(fixture) { KeepTeam = Team, KeepStories = keep }) { BaseAddress = ShortcutWorkSource.DefaultBaseAddress },
            token, ShortcutContractTests.Scope);

    /// <summary>
    /// Read-only: lists the ready stories of team DarkFactory and epic 25171, of which the recording keeps the recorder's own
    /// (<paramref name="keep"/>): the To Do parent, which is ready, and the archived namesake, which is not.
    /// </summary>
    private static async Task RecordListReady(string token, HashSet<int> keep)
    {
        var ready = new ShortcutFixture { Source = "recorded live; the listings keep only the recorder's own throwaway stories" };
        var ids = await Recording(ready, token, keep).ListReadyAsync(CancellationToken.None);
        ready.Parameters["expected"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray());
        ready.Save("list-ready.json");
    }

    [Fact]
    public async Task Record()
    {
        var token = Token();
        var ct = CancellationToken.None;

        // The recording's own stories: all a recorded listing keeps.
        var keep = new HashSet<int>();
        ShortcutWorkSource Recording(ShortcutFixture fixture) => ShortcutFixtureRecorder.Recording(fixture, token, keep);

        var spec = new ShortcutFixture { Source = "recorded live" };
        await Recording(spec).ReadSpecAsync(25185, ct);
        spec.Save("read-spec.json");

        // No story in reach has an epic with documents: record the epic live and hand-author the
        // story's epic_id and the documents from the v3 schema (DocSlim, Doc).
        var epic = new ShortcutFixture();
        var epicHttp = new HttpClient(new RecordingHandler(epic)) { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        await Raw(epicHttp, token, HttpMethod.Get, "epics/25171", null);
        var docId = "6a0d4f00-0000-4000-8000-000000000001";
        var storyCall = spec.Interactions.Single();
        var story = storyCall.Response!.DeepClone();
        story["epic_id"] = 25171;
        var specEpic = new ShortcutFixture
        {
            Source = "story and epic recorded live; story.epic_id and the documents hand-authored from the v3 API schema",
            Interactions =
            [
                storyCall with { Response = story },
                epic.Interactions.Single(),
                new("GET", "/api/v3/epics/25171/documents", null, 200,
                    JsonNode.Parse($$"""[{"id":"{{docId}}","title":"Dark Factory Spec","app_url":"https://app.shortcut.com/trefry/write/{{docId}}"}]""")),
                new("GET", $"/api/v3/documents/{docId}", null, 200, JsonNode.Parse($$"""
                    {"id":"{{docId}}","title":"Dark Factory Spec","archived":false,
                     "content_markdown":"# Dark Factory Spec\n\n## Work sources\n\nShortcut and KanbanBoard plug in through one adapter interface.",
                     "content_html":"<h1>Dark Factory Spec</h1>","app_url":"https://app.shortcut.com/trefry/write/{{docId}}",
                     "created_at":"2026-10-06T12:00:00Z","updated_at":"2026-10-06T12:00:00Z"}
                    """)),
            ],
        };
        specEpic.Save("read-spec-epic.json");

        // Writes: a throwaway parent in To Do, team DarkFactory, no epic.
        var raw = new HttpClient { BaseAddress = ShortcutWorkSource.DefaultBaseAddress };
        var group = (await Raw(raw, token, HttpMethod.Get, "groups", null))!.AsArray().Single(g => (string?)g!["mention_name"] == Team)!["id"]!.GetValue<string>();
        var states = (await Raw(raw, token, HttpMethod.Get, "workflows", null))!.AsArray().SelectMany(w => w!["states"]!.AsArray()).ToList();
        long State(string name) => states.First(s => (string?)s!["name"] == name)!["id"]!.GetValue<long>();
        var toDo = State("To Do");
        var parent = (await Raw(raw, token, HttpMethod.Post, "stories", new
        {
            name = "[dark-factory fixture] parent",
            description = "Throwaway story for the dark-factory Shortcut contract fixtures; archived after recording.",
            story_type = "chore",
            group_id = group,
            workflow_state_id = toDo,
        }))!["id"]!.GetValue<int>();
        var created = new List<int> { parent };
        keep.Add(parent);
        try
        {
            // An archived story with the first child's name, in the Backlog: the listing's one story that is not ready, and the
            // namesake a retry must never reuse (the board's real ones — earlier recordings' children — are not recorded).
            var namesake = (await Raw(raw, token, HttpMethod.Post, "stories", new
            {
                name = ShortcutContractTests.Children[0].Name,
                description = "Throwaway archived namesake for the dark-factory Shortcut contract fixtures.",
                story_type = "chore",
                group_id = group,
                workflow_state_id = State("Backlog"),
            }))!["id"]!.GetValue<int>();
            created.Add(namesake);
            keep.Add(namesake);
            await Raw(raw, token, HttpMethod.Put, $"stories/{namesake}", new { archived = true });

            // Listed while the throwaway parent waits in To Do, so the recording always holds a ready story (on 2026-10-13 the
            // team and the epic had none, and the replay's "the listing is not empty" failed on the fresh recording).
            await RecordListReady(token, keep);

            var writes = new ShortcutFixture { Source = "recorded live on throwaway stories, archived afterwards" };
            writes.Parameters["parent"] = parent;
            var children = await ShortcutContractTests.WriteLifecycle(Recording(writes), parent);
            created.AddRange(children);
            keep.UnionWith(children);
            writes.Parameters["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray());

            // Read back: the second child is blocked by the first.
            var child2 = await Raw(raw, token, HttpMethod.Get, $"stories/{children[1]}", null);
            Assert.Contains(child2!["story_links"]!.AsArray(), l => (string?)l!["verb"] == "blocks" && l["subject_id"]!.GetValue<int>() == children[0]);
            writes.Save("write-lifecycle.json");

            // The retry and the release of those children (sc-25877), with a source of its own (no cached read).
            var release = new ShortcutFixture { Source = "recorded live on the throwaway stories of write-lifecycle.json, archived afterwards" };
            release.Parameters["parent"] = parent;
            release.Parameters["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray());
            await ShortcutContractTests.ReleaseLifecycle(Recording(release), parent, children);
            release.Save("release.json");
        }
        finally
        {
            foreach (var id in created)
            {
                await Raw(raw, token, HttpMethod.Put, $"stories/{id}", new { archived = true });
            }
        }
    }

    private static async Task<JsonNode?> Raw(HttpClient http, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Shortcut-Token", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text);
    }
}
