using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25871 (E7): which stories of a waiting plan are released, decided by code from ledger and board facts.</summary>
public class PlanReleaseDecisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static LedgerEntry Row(WorkState state, string? step = null) =>
        new() { State = state, Step = step, RecordedAt = T0, Outcome = StepOutcome.Passed };

    private static ReleaseDecision Decide(string[] stories, Dictionary<string, string[]> blockers, string[] merged, params string[] released) =>
        PlanRelease.Decide(stories, released.ToHashSet(),
            blockers.ToDictionary(b => b.Key, b => (IReadOnlyList<string>)b.Value), merged.Contains);

    [Fact]
    public void Only_a_recorded_merge_satisfies_a_blocker()
    {
        Assert.False(PlanRelease.Satisfied(null)); // the ledger has never seen it (e.g. a person finished it by hand)
        Assert.False(PlanRelease.Satisfied([Row(WorkState.Intake), Row(WorkState.Implement), Row(WorkState.Review)]));
        Assert.False(PlanRelease.Satisfied([Row(WorkState.Intake), Row(WorkState.Implement), Row(WorkState.Escalated)]));
        Assert.False(PlanRelease.Satisfied([Row(WorkState.Intake), Row(WorkState.Cancelled)]));
        // A checkpoint written in Merge is not the transition.
        Assert.False(PlanRelease.Satisfied([Row(WorkState.MergeGate), Row(WorkState.MergeGate, RunPipeline.Steps.GatePassed)]));
        Assert.True(PlanRelease.Satisfied([Row(WorkState.MergeGate), Row(WorkState.Merge), Row(WorkState.Watch)]));
    }

    [Fact]
    public void A_chain_releases_one_story_at_a_time()
    {
        string[] stories = ["a", "b", "c"];
        var blockers = new Dictionary<string, string[]> { ["a"] = [], ["b"] = ["a"], ["c"] = ["b"] };

        var first = Decide(stories, blockers, []);
        Assert.Equal(["a"], first.Release);
        Assert.Equal([("b", "a"), ("c", "b")], first.Held.Select(h => (h.Id, string.Join(",", h.By))));
        Assert.All(first.Held, h => Assert.Empty(h.Outside));
        Assert.False(first.Complete);

        // Released but not merged: nothing new, and it is not released again.
        Assert.Empty(Decide(stories, blockers, [], "a").Release);
        Assert.Equal(["b"], Decide(stories, blockers, ["a"], "a").Release);
        Assert.Equal(["c"], Decide(stories, blockers, ["a", "b"], "a", "b").Release);
        var done = Decide(stories, blockers, ["a", "b", "c"], "a", "b", "c");
        Assert.Equal((0, 0, true), (done.Release.Count, done.Held.Count, done.Complete));
    }

    [Fact]
    public void A_diamond_releases_both_arms_together_and_its_join_only_after_both_merged()
    {
        string[] stories = ["a", "b", "c", "d"];
        var blockers = new Dictionary<string, string[]> { ["b"] = ["a"], ["c"] = ["a"], ["d"] = ["b", "c"] };

        Assert.Equal(["a"], Decide(stories, blockers, []).Release);
        Assert.Equal(["b", "c"], Decide(stories, blockers, ["a"], "a").Release);
        var oneArm = Decide(stories, blockers, ["a", "b"], "a", "b", "c");
        Assert.Empty(oneArm.Release);
        Assert.Equal(["c"], Assert.Single(oneArm.Held).By);
        Assert.Equal(["d"], Decide(stories, blockers, ["a", "b", "c"], "a", "b", "c").Release);
    }

    [Fact]
    public void A_blocker_outside_the_plan_holds_a_story_like_one_inside_it_and_is_named()
    {
        string[] stories = ["a", "b"];
        var blockers = new Dictionary<string, string[]> { ["b"] = ["a", "sc-777"] };

        var held = Assert.Single(Decide(stories, blockers, ["a"], "a").Held);
        Assert.Equal(("b", "sc-777", "sc-777"), (held.Id, string.Join(",", held.By), string.Join(",", held.Outside)));
        Assert.Equal("b held by sc-777 (not a story of this plan, not merged by the factory)", PlanRelease.HeldByOutsiders([held]));
        // Only the outsiders are the plan item's to report; the plan's own order is not.
        Assert.Equal("", PlanRelease.HeldByOutsiders(Decide(stories, new() { ["b"] = ["a"] }, []).Held));

        Assert.Equal(["b"], Decide(stories, blockers, ["a", "sc-777"], "a").Release);
    }

    [Fact]
    public void A_blocker_that_escalated_never_releases_its_dependents_and_the_plan_is_not_complete()
    {
        // "a" was released and then escalated: it has no Merge row, so it satisfies nothing, however long the plan waits.
        var decision = Decide(["a", "b", "c"], new() { ["b"] = ["a"], ["c"] = [] }, [], "a");

        Assert.Equal(["c"], decision.Release);
        Assert.Equal("b", Assert.Single(decision.Held).Id);
        Assert.False(decision.Complete);
    }

    [Fact]
    public void A_merged_story_is_never_released_and_an_empty_plan_is_not_complete()
    {
        // Run by hand and merged without ever being released by the plan.
        Assert.Empty(Decide(["a"], new(), ["a"]).Release);
        Assert.True(Decide(["a"], new(), ["a"]).Complete);
        Assert.False(Decide([], new(), []).Complete);
    }

    [Fact]
    public void The_refusal_names_each_blocker_and_is_told_from_any_other_claim_refusal()
    {
        var refusal = PlanRelease.Refusal(["sc-12", "sc-13"]);

        Assert.StartsWith("blocked by sc-12, sc-13 (not merged)", refusal);
        Assert.True(PlanRelease.IsBlockedRefusal($"{RunPipeline.ClaimRefused}: {refusal}"));
        Assert.True(RunPipeline.IsClaimRefused($"{RunPipeline.ClaimRefused}: {refusal}"));
        Assert.False(PlanRelease.IsBlockedRefusal($"{RunPipeline.ClaimRefused}: the story is no longer in To Do"));
        Assert.False(PlanRelease.IsBlockedRefusal(null));
        Assert.Equal(["sc-12", "sc-13"], PlanRelease.StoriesOf(" sc-12  sc-13 "));
        Assert.Empty(PlanRelease.StoriesOf(null));
    }
}

/// <summary>sc-25871: the Shortcut adapter's blocker read and release, against fake HTTP and against the recorded story shape.</summary>
public class ShortcutReleaseTests
{
    private static (FakeShortcutBoard Board, ShortcutWorkSource Source) Board()
    {
        var board = new FakeShortcutBoard();
        return (board, new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])));
    }

    private static async Task Link(FakeShortcutBoard board, int subject, string verb, int obj)
    {
        using var response = await board.Client().PostAsJsonAsync("story-links", new { subject_id = subject, verb, object_id = obj });
        response.EnsureSuccessStatusCode();
    }

    private static List<RecordedRequest> Writes(FakeShortcutBoard board) => board.Requests.Where(r => r.Method != HttpMethod.Get).ToList();

    [Fact]
    public async Task Blockers_are_the_subjects_of_the_blocks_relations_whose_object_the_story_is()
    {
        var (board, source) = Board();
        foreach (var id in new[] { 10, 11, 12, 13 })
        {
            board.Add(id, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog);
        }
        await Link(board, 11, "blocks", 12);
        await Link(board, 10, "blocks", 12);
        await Link(board, 10, "blocks", 12); // the same relation twice
        await Link(board, 12, "blocks", 13); // 12 blocks another: not its blocker
        await Link(board, 13, "relates to", 12);
        await Link(board, 13, "duplicates", 12);
        board.Requests.Clear();

        Assert.Equal([10, 11], await source.BlockersAsync(12, CancellationToken.None));
        Assert.Equal([12], await source.BlockersAsync(13, CancellationToken.None));
        Assert.Empty(await source.BlockersAsync(10, CancellationToken.None));
        // Reads only: one GET per story.
        Assert.Equal(["/api/v3/stories/12", "/api/v3/stories/13", "/api/v3/stories/10"], board.Requests.Select(r => r.PathAndQuery));
        Assert.All(board.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Release_moves_a_backlog_story_to_to_do_once_and_never_moves_a_story_back()
    {
        var (board, source) = Board();
        board.Add(20, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog);

        Assert.True(await source.MakeReadyAsync(20, CancellationToken.None));

        Assert.Equal(FakeShortcutBoard.ToDo, (long)board.Story(20)["workflow_state_id"]!);
        var put = Assert.Single(Writes(board));
        Assert.Equal((HttpMethod.Put, "/api/v3/stories/20"), (put.Method, put.PathAndQuery));
        // Only the state changes, to the id the API gives for the name "To Do".
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(put.Body!), new JsonObject { ["workflow_state_id"] = FakeShortcutBoard.ToDo }), put.Body);
        Assert.Contains(20, await source.ListReadyAsync(CancellationToken.None));

        // Again, and from every state past the backlog: nothing is written.
        foreach (var state in new[] { FakeShortcutBoard.ToDo, FakeShortcutBoard.InProgress, FakeShortcutBoard.Done })
        {
            board.Story(20)["workflow_state_id"] = state;
            Assert.False(await source.MakeReadyAsync(20, CancellationToken.None));
            Assert.Equal(state, (long)board.Story(20)["workflow_state_id"]!);
        }
        board.Story(20)["workflow_state_id"] = FakeShortcutBoard.Backlog;
        board.Story(20)["archived"] = true;
        Assert.False(await source.MakeReadyAsync(20, CancellationToken.None));
        Assert.Single(Writes(board));
    }

    [Fact]
    public async Task Release_does_not_look_at_the_watch_scope_so_a_story_outside_it_is_ready_but_not_listed()
    {
        var (board, source) = Board();
        board.Add(30, FakeShortcutBoard.OtherTeam, FakeShortcutBoard.Backlog);

        Assert.True(await source.MakeReadyAsync(30, CancellationToken.None));

        Assert.Equal(FakeShortcutBoard.ToDo, (long)board.Story(30)["workflow_state_id"]!);
        Assert.False(await source.InScopeAsync(30, CancellationToken.None));
        Assert.DoesNotContain(30, await source.ListReadyAsync(CancellationToken.None));
    }

    /// <summary>Serves recorded responses by path; anything else is a 404. Records what was sent.</summary>
    private sealed class Recorded(Dictionary<string, JsonNode> responses) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Sent.Add((request.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return responses.TryGetValue(path, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// The recorded API's own shape (Fixtures/shortcut/list-ready.json holds two stories joined by a real <c>blocks</c> relation, and
    /// the workspace's workflow): <c>story_links</c> entries carry <c>verb</c>, <c>subject_id</c> (the blocker) and <c>object_id</c>,
    /// and a relation is listed on both of its stories. <c>GET stories/{id}</c> itself and the release's <c>PUT</c> have no recording
    /// with a relation or a Backlog → To Do move yet: the stories here are the listing's.
    /// </summary>
    [Fact]
    public async Task The_recorded_story_shape_reads_its_blocker_and_is_released_to_the_recorded_to_do_state()
    {
        var fixture = ShortcutFixture.Load("list-ready.json");
        var linked = fixture.Interactions.Where(i => i.Path.EndsWith("/stories", StringComparison.Ordinal)).SelectMany(i => i.Response!.AsArray())
            .Where(s => s!["story_links"]!.AsArray().Any(l => (string?)l!["verb"] == "blocks"))
            .GroupBy(s => s!["id"]!.GetValue<int>()).Select(g => g.First()!.DeepClone().AsObject()).ToList();
        var relation = linked[0]["story_links"]!.AsArray().First(l => (string?)l!["verb"] == "blocks")!;
        var (blocker, blocked) = (relation["subject_id"]!.GetValue<int>(), relation["object_id"]!.GetValue<int>());
        Assert.Equal(new[] { blocker, blocked }.Order(), linked.Select(s => s["id"]!.GetValue<int>()).Order());
        var workflows = fixture.Interactions.First(i => i.Path == "/api/v3/workflows").Response!;
        long State(string name) => workflows.AsArray().SelectMany(w => w!["states"]!.AsArray()).First(s => (string?)s!["name"] == name)!["id"]!.GetValue<long>();
        var responses = linked.ToDictionary(s => $"/api/v3/stories/{s["id"]}", s => (JsonNode)s);
        responses["/api/v3/workflows"] = workflows.DeepClone();
        var handler = new Recorded(responses);
        var source = new ShortcutWorkSource(new HttpClient(handler) { BaseAddress = ShortcutWorkSource.DefaultBaseAddress }, "tok", WatchScope.None);

        Assert.Equal([blocker], await source.BlockersAsync(blocked, CancellationToken.None));
        Assert.Empty(await source.BlockersAsync(blocker, CancellationToken.None));

        // Both recorded stories wait in the Backlog; the fixture's own stories were archived after recording, which release leaves alone.
        var story = responses[$"/api/v3/stories/{blocker}"].AsObject();
        Assert.Equal(State("Backlog"), story["workflow_state_id"]!.GetValue<long>());
        Assert.True(story["archived"]!.GetValue<bool>());
        Assert.False(await source.MakeReadyAsync(blocker, CancellationToken.None));
        story["archived"] = false;
        // The PUT answers the story; the fake serves the same one back.
        Assert.True(await source.MakeReadyAsync(blocker, CancellationToken.None));
        var put = Assert.Single(handler.Sent, s => s.Method == HttpMethod.Put);
        Assert.Equal($"/api/v3/stories/{blocker}", put.Path);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(put.Body!), new JsonObject { ["workflow_state_id"] = State("To Do") }), put.Body);
    }
}

/// <summary>sc-25871: a waiting plan item releases its stories in dependency order, refuses a blocked claim, and closes itself out.</summary>
public class PlanReleasePipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const int PlanId = 500;

    private static string Chain => PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story", "a"),
        PlanAnswers.Story("c", "Third story", "b"));

    private sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness(WatchScope? scope = null)
        {
            Db = new LedgerDbContext(_options);
            Board.Add(PlanId, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.InProgress, epic: 42, name: "Add CSV export");
            Board.Story(PlanId)["labels"] = new JsonArray(new JsonObject { ["name"] = "plan" });
            Source = new ShortcutWorkSource(Board.Client(), "tok", scope ?? new WatchScope(["darkfactory"], []));
        }

        public FakeTimeProvider Time { get; } = new(T0);
        public LedgerDbContext Db { get; }
        public FakeShortcutBoard Board { get; } = new();
        public ShortcutWorkSource Source { get; }
        public LedgerDbContextFactory Contexts => new(_options);
        public IControls Controls => new LedgerControls(Contexts, Time);
        public WorkLedger Ledger => new(Db, TimeProvider.System);
        public InProcessRunLocks Locks { get; } = new();
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public FakeWorker Implementer { get; } = new(Reports(Ok), Reports(Ok with { SessionId = "sess-78" }));
        public StringWriter Log { get; } = new();

        public Task<RunOutcome> Run(int id = PlanId, bool ignoreScope = false) =>
            new RunPipeline(Source, Ledger, Locks, Workspaces, Implementer, Prs, Sandbox, Log, controls: Controls,
                controlPollInterval: TimeSpan.FromMilliseconds(10), ignoreScope: ignoreScope).RunAsync(id, CancellationToken.None);

        /// <summary>
        /// The plan item as its Plan step leaves it (sc-25870): the plan's stories on the board in the Backlog with their blockers, the
        /// ledger at Planned with <c>plan-stories</c> recorded. Returns the stories' ids in plan order.
        /// </summary>
        public async Task<int[]> Planned(string answer)
        {
            var plan = PlanParser.Parse(answer);
            var ids = await Source.CreateChildrenAsync(PlanId, plan.Children($"sc-{PlanId}", Sandbox), CancellationToken.None);
            var ledger = Ledger;
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, $"sc-{PlanId}", "Add CSV export", Sandbox.FullName, null, CancellationToken.None, 42);
            await ledger.RecordAsync(item, WorkState.Plan, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Plan, "plan-sess", plan.ToDetail(), CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanCreating, "plan-sess", null, CancellationToken.None);
            var stories = string.Join(' ', ids.Select(i => $"sc-{i}"));
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, "plan-sess", stories, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Planned, "plan-sess", $"{ids.Count} stories: {stories}", CancellationToken.None);
            Board.Requests.Clear();
            return [.. ids];
        }

        public static string Pr(int id) => $"https://github.com/michaeltrefry/dark-factory-sandbox/pull/{id}";

        /// <summary>The story's own ledger as its run through the gate leaves it: every transition up to <paramref name="last"/>.</summary>
        public async Task Worked(int id, WorkState last = WorkState.Watch, string? title = null)
        {
            var ledger = Ledger;
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, $"sc-{id}", title ?? $"Story {id}", Sandbox.FullName, null, CancellationToken.None, 42);
            WorkState[] path = [WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch];
            foreach (var state in path.Skip(Array.IndexOf(path, item.State) + 1))
            {
                if (item.State == last)
                {
                    break;
                }
                if (state == WorkState.Review)
                {
                    await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null, $"{Pr(id)} https://github.com/michaeltrefry/dark-factory-sandbox/tree/factory/sc-{id}",
                        CancellationToken.None);
                }
                await ledger.RecordAsync(item, state, null, state == WorkState.Merge ? $"c0ffee{id}" : null, CancellationToken.None);
            }
        }

        public Task Merged(int id, string? title = null) => Worked(id, WorkState.Watch, title);

        public long State(int id) => (long)Board.Story(id)["workflow_state_id"]!;

        public List<RecordedRequest> Writes(int? story = null) => Board.Requests
            .Where(r => r.Method != HttpMethod.Get && (story is null || r.PathAndQuery.StartsWith($"/api/v3/stories/{story}", StringComparison.Ordinal))).ToList();

        public async Task<List<LedgerEntry>> Rows(int id = PlanId)
        {
            var item = await Db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.ExternalId == $"sc-{id}");
            return item is null ? [] : await Db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync();
        }

        public async Task<List<string?>> Released() =>
            (await Rows()).Where(r => r.Step == RunPipeline.Steps.PlanReleased).Select(r => r.Detail).ToList();

        public async Task<IReadOnlyList<int>> InFlight()
        {
            await using var db = new LedgerDbContext(_options);
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), CancellationToken.None, Controls);
        }

        public async Task Link(int subject, int obj)
        {
            using var response = await Board.Client().PostAsJsonAsync("story-links", new { subject_id = subject, verb = "blocks", object_id = obj });
            response.EnsureSuccessStatusCode();
            Board.Requests.Clear();
        }
    }

    [Fact]
    public async Task A_chain_is_released_one_story_at_a_time_each_on_the_run_after_its_blockers_merge_row()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        var (a, b, c) = (ids[0], ids[1], ids[2]);

        // The poll lists the waiting plan item; its run releases only the story nothing blocks.
        Assert.Equal([PlanId], await h.InFlight());
        var first = await h.Run();

        Assert.Equal((WorkState.Planned, (string?)null), (first.State, first.Error));
        Assert.Equal((FakeShortcutBoard.ToDo, FakeShortcutBoard.Backlog, FakeShortcutBoard.Backlog), (h.State(a), h.State(b), h.State(c)));
        Assert.Equal([$"sc-{a}"], await h.Released());
        // It is ready like any story: the listing finds it, the others not.
        Assert.Equal([a], await h.Source.ListReadyAsync(CancellationToken.None));
        Assert.Equal(StepOutcome.Passed, (await h.Rows()).Last().Outcome);

        // Released is not merged: while A is only in flight nothing else is released, and nothing at all is written.
        await h.Worked(a, WorkState.MergeGate);
        var rows = (await h.Rows()).Count;
        h.Board.Requests.Clear();
        Assert.True((await h.Run()).Succeeded);
        Assert.Empty(h.Writes());
        Assert.Equal(rows, (await h.Rows()).Count);
        Assert.Equal((FakeShortcutBoard.Backlog, FakeShortcutBoard.Backlog), (h.State(b), h.State(c)));

        // A's Merge row: the next run releases B, and only B.
        await h.Merged(a);
        Assert.True((await h.Run()).Succeeded);
        Assert.Equal((FakeShortcutBoard.ToDo, FakeShortcutBoard.Backlog), (h.State(b), h.State(c)));
        Assert.Equal([$"sc-{a}", $"sc-{b}"], await h.Released());
        Assert.Single(h.Writes()); // B's move; A is not touched again

        await h.Merged(b);
        Assert.True((await h.Run()).Succeeded);
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(c));
        Assert.Equal([$"sc-{a}", $"sc-{b}", $"sc-{c}"], await h.Released());
        // The plan item stays where it is until every story has merged; nothing implements it.
        Assert.Equal([WorkState.Intake, WorkState.Plan, WorkState.Planned], (await h.Rows()).Where(r => r.Step is null).Select(r => r.State));
        Assert.Empty(h.Implementer.Calls);
        Assert.Empty(h.Writes(PlanId));
    }

    [Fact]
    public async Task A_blocker_outside_the_plan_holds_the_story_and_the_plan_item_says_which_once()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story")));
        var (a, b) = (ids[0], ids[1]);
        h.Board.Add(777, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Done, name: "Someone else's story, finished by hand");
        await h.Link(777, b);

        await h.Run();

        // The outside blocker is done on the board, but the factory never merged it: it holds B, and the plan item names it.
        Assert.Equal((FakeShortcutBoard.ToDo, FakeShortcutBoard.Backlog), (h.State(a), h.State(b)));
        var held = Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanHeld);
        Assert.Equal(($"sc-{b} held by sc-777 (not a story of this plan, not merged by the factory)", StepOutcome.Deferred), (held.Detail, held.Outcome));

        // Said once, not every poll.
        await h.Run();
        await h.Run();
        Assert.Single(await h.Rows(), r => r.Step == RunPipeline.Steps.PlanHeld);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(b));

        // The factory merges it (or someone removes the relation): B is released.
        await h.Merged(777);
        await h.Run();
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(b));
        Assert.Equal([$"sc-{a}", $"sc-{b}"], await h.Released());
    }

    [Theory]
    [InlineData(WorkState.Escalated)]
    [InlineData(WorkState.Cancelled)]
    public async Task A_story_that_escalated_or_was_cancelled_never_releases_its_dependents_and_the_plan_stays_planned(WorkState ended)
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Run();
        await h.Worked(ids[0], WorkState.Review);
        var item = await h.Ledger.FindAsync(RunPipeline.Source, $"sc-{ids[0]}", CancellationToken.None);
        await h.Ledger.RecordAsync(item!, ended, null, "gave up", CancellationToken.None);

        for (var poll = 0; poll < 3; poll++)
        {
            var outcome = await h.Run();
            Assert.Equal((WorkState.Planned, (string?)null), (outcome.State, outcome.Error));
        }

        Assert.Equal((FakeShortcutBoard.Backlog, FakeShortcutBoard.Backlog), (h.State(ids[1]), h.State(ids[2])));
        Assert.Equal([$"sc-{ids[0]}"], await h.Released());
        Assert.Equal([PlanId], (await h.InFlight()).Where(i => i == PlanId));
    }

    [Fact]
    public async Task A_plan_story_moved_to_ready_by_hand_while_its_blocker_is_unmerged_is_refused_naming_the_blocker_and_recovers_when_it_merges()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        var (a, b) = (ids[0], ids[1]);
        await h.Run();
        // Someone drags B to To Do: the listing now returns it, and the intake loop runs it.
        h.Board.Story(b)["workflow_state_id"] = FakeShortcutBoard.ToDo;
        Assert.Contains(b, await h.Source.ListReadyAsync(CancellationToken.None));
        h.Board.Requests.Clear();

        var refused = await h.Run(b);

        // Not started: parked like any refused claim, with no board write, the refusal naming the blocker.
        Assert.Equal(WorkState.Paused, refused.State);
        Assert.Contains($"sc-{b} parked: blocked by sc-{a} (not merged)", refused.Error);
        var rows = await h.Rows(b);
        Assert.Equal([WorkState.Intake, WorkState.Paused], rows.Where(r => r.Step is null).Select(r => r.State));
        Assert.StartsWith($"{RunPipeline.ClaimRefused}: blocked by sc-{a} (not merged)", rows.Last(r => r.Step is null).Detail);
        var parked = Assert.Single(rows, r => r.Step is not null);
        Assert.Equal((RunPipeline.Steps.Parked, StepOutcome.Deferred), (parked.Step, parked.Outcome));
        Assert.StartsWith($"blocked by sc-{a}", parked.Detail);
        Assert.Empty(h.Writes());
        Assert.Empty(h.Implementer.Calls);
        // The story records its plan, from the ledger's own record of what the plan created.
        var plan = await h.Db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == $"sc-{PlanId}");
        Assert.Equal(plan.Id, (await h.Db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == $"sc-{b}")).PlanItemId);
        Assert.Null(plan.PlanItemId);

        // The board keeps listing it: every poll's run says the same and writes nothing, to the board or the ledger.
        await h.Worked(a, WorkState.MergeGate);
        for (var poll = 0; poll < 3; poll++)
        {
            var again = await h.Run(b);
            Assert.Equal(WorkState.Paused, again.State);
            Assert.Contains($"blocked by sc-{a} (not merged)", again.Error);
        }
        Assert.Equal(rows.Count, (await h.Rows(b)).Count);
        Assert.Empty(h.Writes());
        // Nor does the plan release it early, or move it back.
        await h.Run();
        Assert.Equal([$"sc-{a}"], await h.Released());
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(b));

        // A merges: the plan records B's release without touching the story someone already moved, and B's next run starts it.
        await h.Merged(a);
        await h.Run();
        Assert.Equal([$"sc-{a}", $"sc-{b} (it had already left the backlog; left where it is)"], await h.Released());
        Assert.Empty(h.Writes(b));

        var started = await h.Run(b);

        Assert.Null(started.Error);
        Assert.Equal([WorkState.Intake, WorkState.Paused, WorkState.Intake, WorkState.Implement, WorkState.Review],
            (await h.Rows(b)).Where(r => r.Step is null).Select(r => r.State));
        Assert.Contains(RunPipeline.Steps.Claimed, (await h.Rows(b)).Select(r => r.Step));
        Assert.Equal(FakeShortcutBoard.InProgress, h.State(b));
        Assert.Single(h.Implementer.Calls);
    }

    [Fact]
    public async Task A_plan_story_with_no_unmerged_blocker_is_claimed_and_a_hand_written_story_is_not_held_to_its_blockers()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Run();

        // A, released by the plan, starts like any ready story.
        Assert.Null((await h.Run(ids[0])).Error);
        Assert.Equal(FakeShortcutBoard.InProgress, h.State(ids[0]));

        // A story a person wrote, blocked on the board by a story the factory never merged: the relation is the owner's own
        // business, as before this story, and the factory starts it.
        h.Board.Add(600, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.ToDo, name: "Hand-written");
        h.Board.Add(601, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog, name: "Its blocker");
        await h.Link(601, 600);
        Assert.Null((await h.Run(600)).Error);
        Assert.Equal(FakeShortcutBoard.InProgress, h.State(600));
        Assert.Null((await h.Db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == "sc-600")).PlanItemId);
    }

    [Fact]
    public async Task When_the_last_story_merges_the_plan_item_is_reported_merged_once_with_a_closeout_listing_each_story_and_its_pull_request()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Run();
        await h.Merged(ids[0], "First story");
        await h.Run();
        await h.Merged(ids[1], "Second @story [x](http://evil.example) <b>");
        await h.Run();
        // The last story is still in the gate: the plan is not complete.
        await h.Worked(ids[2], WorkState.MergeGate, "Third story");
        Assert.Equal(WorkState.Planned, (await h.Run()).State);
        Assert.Empty(h.Writes(PlanId));

        await h.Merged(ids[2]);
        var closed = await h.Run();

        Assert.Equal((WorkState.Done, (string?)null), (closed.State, closed.Error));
        Assert.Equal(FakeShortcutBoard.Done, h.State(PlanId));
        var writes = h.Writes(PlanId);
        Assert.Equal([(HttpMethod.Put, $"/api/v3/stories/{PlanId}"), (HttpMethod.Post, $"/api/v3/stories/{PlanId}/comments")],
            writes.Select(w => (w.Method, w.PathAndQuery)));
        var closeout = (string)JsonNode.Parse(writes[1].Body!)!["text"]!;
        Assert.StartsWith($"[author: dark-factory] sc-{PlanId}'s plan is complete: 3 of its 3 stories merged", closeout);
        Assert.Contains("**From the factory ledger**", closeout);
        // Each story, its outcome and state from its own ledger rows, its merge commit and its pull request.
        Assert.Contains($"- sc-{ids[0]} `First story`: **passed** Watch, merged as `c0ffee{ids[0]}`, {Harness.Pr(ids[0])}\n", closeout);
        Assert.Contains($"- sc-{ids[2]} `Third story`: **passed** Watch, merged as `c0ffee{ids[2]}`, {Harness.Pr(ids[2])}", closeout);
        // A planner's title is inert: no mention, link or HTML renders.
        Assert.DoesNotContain("@story", closeout);
        Assert.DoesNotContain("](http://", closeout);
        Assert.DoesNotContain("<b>", closeout);

        // Each step a row before the next, and the terminal state.
        var rows = await h.Rows();
        var tail = rows.Skip(rows.FindLastIndex(r => r.Step == RunPipeline.Steps.PlanReleased) + 1).ToList();
        Assert.Equal([(RunPipeline.Steps.MergedReported, WorkState.Planned), (RunPipeline.Steps.Closeout, WorkState.Planned), (null, WorkState.Done)],
            tail.Select(r => (r.Step, r.State)));
        Assert.Equal(RunPipeline.Posted, tail[1].Detail);
        Assert.Equal("3 stories merged", tail[2].Detail);
        Assert.All(tail, r => Assert.Equal(StepOutcome.Passed, r.Outcome));
        Assert.True(Lifecycle.IsTerminal(closed.State));

        // Once: nothing lists the finished plan, and another run reports nothing again.
        Assert.Empty(await h.InFlight());
        h.Board.Requests.Clear();
        Assert.Equal(WorkState.Done, (await h.Run()).State);
        Assert.Empty(h.Writes());
        Assert.Equal(rows.Count, (await h.Rows()).Count);
    }

    [Fact]
    public async Task A_run_resumed_after_the_board_was_told_but_before_the_closeout_reports_the_plan_merged_only_once()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "Only story")));
        await h.Merged(ids[0]);
        // The closeout cannot be posted (Shortcut answers 500 to the comment): the merge report is recorded, the closeout's failure too.
        h.Board.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null;

        var failed = await h.Run();

        Assert.Equal(WorkState.Planned, failed.State);
        Assert.Contains("closeout NOT posted", failed.Error);
        var attempt = (await h.Rows()).Last();
        Assert.Equal((RunPipeline.Steps.Closeout, StepOutcome.Failed), (attempt.Step, attempt.Outcome));
        Assert.StartsWith("failed: ", attempt.Detail);
        Assert.Equal([PlanId], await h.InFlight()); // the next poll tries again
        // The dashboard shows the failed closeout and that it is being retried, as for a merged item's.
        var shown = Assert.Single(await new Dashboard.DashboardData(h.Contexts, h.Time).ActiveItemsAsync(CancellationToken.None), r => r.ExternalId == $"sc-{PlanId}");
        Assert.Equal((1, true), (shown.FailedCloseout!.Attempts, shown.CloseoutRetrying));

        h.Board.Intercept = null;
        var posted = await h.Run();

        Assert.Equal((WorkState.Done, (string?)null), (posted.State, posted.Error));
        var rows = await h.Rows();
        Assert.Single(rows, r => r.Step == RunPipeline.Steps.MergedReported);
        Assert.Equal(["failed", RunPipeline.Posted], rows.Where(r => r.Step == RunPipeline.Steps.Closeout).Select(r => r.Detail!.Split(':')[0]));
        // One state change on the board, and one comment that was accepted.
        Assert.Single(h.Writes(PlanId), w => w.Method == HttpMethod.Put);
        Assert.Equal(2, h.Writes(PlanId).Count(w => w.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task A_closeout_the_board_keeps_refusing_is_tried_a_bounded_number_of_times_and_the_plan_still_finishes_saying_so()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "Only story")));
        await h.Merged(ids[0]);
        h.Board.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : null;

        for (var attempt = 1; attempt < RunPipeline.MaxCloseoutAttempts; attempt++)
        {
            Assert.Equal(WorkState.Planned, (await h.Run()).State);
        }
        var last = await h.Run();

        Assert.Equal(WorkState.Done, last.State);
        var rows = await h.Rows();
        Assert.Equal(RunPipeline.MaxCloseoutAttempts, rows.Count(r => r.Step == RunPipeline.Steps.Closeout));
        Assert.Equal($"1 stories merged; closeout NOT posted after {RunPipeline.MaxCloseoutAttempts} attempts: post it by hand", rows.Last().Detail);
        await h.Run();
        Assert.Equal(RunPipeline.MaxCloseoutAttempts, h.Writes(PlanId).Count(w => w.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task A_run_that_died_between_the_release_and_its_row_releases_nothing_twice()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        // The earlier run moved A on the board and died before recording it; A's own run has claimed it since.
        Assert.True(await h.Source.MakeReadyAsync(ids[0], CancellationToken.None));
        h.Board.Story(ids[0])["workflow_state_id"] = FakeShortcutBoard.InProgress;
        h.Board.Requests.Clear();

        await h.Run();

        // The row is written now; the story is not written again, let alone moved back to To Do.
        Assert.Equal([$"sc-{ids[0]} (it had already left the backlog; left where it is)"], await h.Released());
        Assert.Empty(h.Writes());
        Assert.Equal(FakeShortcutBoard.InProgress, h.State(ids[0]));
        await h.Run();
        Assert.Single(await h.Released());
    }

    [Fact]
    public async Task A_board_that_fails_mid_release_records_nothing_for_the_story_and_the_next_run_releases_it()
    {
        var h = new Harness();
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "First story"), PlanAnswers.Story("b", "Second story")));
        h.Board.Intercept = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.EndsWith($"/stories/{ids[1]}", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : null;

        // The board's failure is thrown to the caller (the intake loop shows it and polls again): the plan item is not escalated.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());

        Assert.Equal([$"sc-{ids[0]}"], await h.Released());
        Assert.Equal(WorkState.Planned, (await h.Db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == $"sc-{PlanId}")).State);
        Assert.Null(await RunPipeline.GiveUpAsync(h.Source, h.Ledger, h.Locks, PlanId, "3 runs failed", TextWriter.Null, CancellationToken.None));

        h.Board.Intercept = null;
        await h.Run();
        Assert.Equal([$"sc-{ids[0]}", $"sc-{ids[1]}"], await h.Released());
        Assert.Equal(1, h.Board.Requests.Count(r => r.Method == HttpMethod.Put && r.PathAndQuery.EndsWith($"/stories/{ids[0]}", StringComparison.Ordinal)));
    }

    public static TheoryData<string> PausingScopes => [ControlScope.Factory, ControlScope.Epic(42), ControlScope.Item($"sc-{PlanId}"), ControlScope.Freeze];

    [Theory]
    [MemberData(nameof(PausingScopes))]
    public async Task A_paused_factory_epic_or_plan_item_and_a_frozen_factory_release_nothing_until_continue(string scope)
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        await h.Controls.SetAsync(scope, ControlState.Paused, "test", CancellationToken.None);

        Assert.Empty(await h.InFlight());
        var paused = await h.Run();

        Assert.NotNull(paused.Error);
        Assert.Equal(FakeShortcutBoard.Backlog, h.State(ids[0]));
        Assert.Empty(await h.Released());
        Assert.Empty(h.Writes());

        await h.Controls.ClearAsync(scope, CancellationToken.None);
        await h.Run();
        Assert.Equal(FakeShortcutBoard.ToDo, h.State(ids[0]));
    }

    [Fact]
    public async Task A_plan_story_outside_the_watch_scope_is_released_and_recorded_as_such_and_runs_only_with_ignore_scope()
    {
        // The plan item was run with --ignore-scope: its stories were created in its team, which nothing watches.
        var h = new Harness(new WatchScope([], [99]));
        var ids = await h.Planned(PlanAnswers.Answer(PlanAnswers.Story("a", "Only story")));

        await h.Run();

        Assert.Equal(FakeShortcutBoard.ToDo, h.State(ids[0]));
        var released = Assert.Single(await h.Released());
        Assert.StartsWith($"sc-{ids[0]} (outside the watch scope: ", released);
        Assert.Contains($"`factory run sc-{ids[0]} --ignore-scope`", released);
        Assert.Contains("outside the watch scope", h.Log.ToString());
        // Nothing lists it, and a run without the flag refuses the claim; with it the story is worked.
        Assert.Empty(await h.Source.ListReadyAsync(CancellationToken.None));
        Assert.Contains("outside the watch scope", (await h.Run(ids[0])).Error);
        Assert.Null((await h.Run(ids[0], ignoreScope: true)).Error);
        Assert.Equal(FakeShortcutBoard.InProgress, h.State(ids[0]));
    }

    [Fact]
    public async Task The_plan_of_a_story_is_found_by_its_exact_id_in_the_newest_plan_stories_row_of_its_source()
    {
        var h = new Harness();
        var ids = await h.Planned(Chain);
        var plan = await h.Db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == $"sc-{PlanId}");

        Assert.Equal(plan.Id, await h.Ledger.PlanOfAsync(RunPipeline.Source, $"sc-{ids[1]}", CancellationToken.None));
        // A prefix of a listed id is not that id, and another source's item with the same id is not the plan's story.
        Assert.Null(await h.Ledger.PlanOfAsync(RunPipeline.Source, $"sc-{ids[1]}"[..^1], CancellationToken.None));
        Assert.Null(await h.Ledger.PlanOfAsync(RunPipeline.Source, $"sc-{PlanId}", CancellationToken.None));
        Assert.Null(await h.Ledger.PlanOfAsync(ItemNaming.GitHubIssue.Source, $"sc-{ids[1]}", CancellationToken.None));

        var known = await h.Ledger.ItemsByIdAsync(RunPipeline.Source, [$"sc-{PlanId}", "sc-1", $"sc-{PlanId}"], CancellationToken.None);
        Assert.Equal([$"sc-{PlanId}"], known.Keys);
        Assert.Equal(RunPipeline.Steps.PlanStories, known[$"sc-{PlanId}"].History.Last(r => r.Step is not null).Step);
    }
}

/// <summary>sc-25871 (E10): the plan closeout is rendered from the stories' ledger rows, bounded, the planner's titles inert.</summary>
public class PlanCloseoutReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static LedgerEntry Row(WorkState state, string? step = null, string? detail = null, StepOutcome outcome = StepOutcome.Passed) =>
        new() { State = state, Step = step, Detail = detail, RecordedAt = T0, Outcome = outcome };

    [Fact]
    public void Each_story_is_one_line_from_its_own_rows_and_what_the_ledger_lacks_is_said_not_invented()
    {
        var merged = new PlanStoryReport("sc-12", "Add `export` @all", [
            Row(WorkState.Implement), Row(WorkState.Implement, RunPipeline.Steps.Linked, "https://github.com/acme/widgets/pull/7 https://github.com/acme/widgets/tree/factory/sc-12"),
            Row(WorkState.Merge, detail: "abc123"), Row(WorkState.Watch, detail: "abc123")]);
        var escalated = new PlanStoryReport("sc-13", "Second", [Row(WorkState.Implement), Row(WorkState.Escalated, detail: "boom", outcome: StepOutcome.Escalated)]);
        var odd = new PlanStoryReport("sc-14", "Third", [Row(WorkState.Implement, RunPipeline.Steps.Linked, "javascript:alert(1) x"), Row(WorkState.Merge, detail: "def456")]);
        var unknown = new PlanStoryReport("sc-15", "Fourth", []);

        var text = LedgerReport.PlanCloseout("sc-9", [merged, escalated, odd, unknown]);

        Assert.StartsWith("[author: dark-factory] sc-9's plan is complete: 2 of its 4 stories merged through the factory's merge gate.", text);
        Assert.Contains("- sc-12 `Add 'export' ＠all`: **passed** Watch, merged as `abc123`, https://github.com/acme/widgets/pull/7\n", text);
        Assert.Contains("- sc-13 `Second`: **escalated** Escalated, not merged, no pull request recorded\n", text);
        // A recorded link that is not an https URL is shown as data, never as a link.
        Assert.Contains("- sc-14 `Third`: **passed** Merge, merged as `def456`, `javascript:alert(1)`\n", text);
        Assert.EndsWith("- sc-15 `Fourth`: no ledger row, not merged, no pull request recorded", text);
    }

    [Fact]
    public void A_plan_of_any_size_stays_under_the_boards_comment_limit()
    {
        var stories = Enumerable.Range(0, 400).Select(i => new PlanStoryReport($"sc-{i}", new string('t', 5000),
            [Row(WorkState.Implement, RunPipeline.Steps.Linked, "https://github.com/acme/widgets/pull/" + new string('9', 5000)), Row(WorkState.Merge, detail: new string('c', 5000))])).ToList();

        var text = LedgerReport.PlanCloseout("sc-9", stories);

        Assert.True(text.Length <= LedgerReport.MaxFactsLength, text.Length.ToString());
        Assert.Contains($"- … and {400 - LedgerReport.MaxPlanStoriesShown} more (the ledger has them)", text);
        Assert.Equal(LedgerReport.MaxPlanStoriesShown, text.Split('\n').Count(l => l.StartsWith("- sc-", StringComparison.Ordinal)));
    }
}
