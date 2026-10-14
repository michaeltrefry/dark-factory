using System.Net;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Contract tests (sc-25986): the whole lifecycle of <see cref="KanbanWorkSource"/> replayed against recorded conversations with
/// KanbanBoard's MCP (<c>Fixtures/kanban</c>). Every <c>tools/call</c> must be the recorded one — tool name and arguments,
/// exactly (<see cref="KanbanReplayHandler"/>) — so what the adapter sends is pinned to what the board accepted. The scenarios
/// below are what the recorder runs too (<see cref="KanbanFixtureRecorder"/>; re-record with <c>KANBAN_RECORD=1</c>,
/// <see cref="KanbanFixtureRecording"/>). Until the epic's terminal story re-records them against the hosted instance the
/// committed fixtures are <see cref="KanbanFixture.Provisional"/>: recorded by the same recorder against the in-repo
/// contract-enforcing fake (<see cref="FakeKanbanBoard"/>).
/// </summary>
public class KanbanContractTests
{
    internal const string PrUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/pull/999";
    internal const string BranchUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/tree/factory/kb-999";
    private const string Prefix = KanbanFixtureGuard.Prefix;
    private static readonly Uri ReplayEndpoint = new("http://kanban.replay/mcp");
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The two items the plan scenario creates from its parent: the second is blocked by the first.</summary>
    internal static readonly IReadOnlyList<ChildItem> Children =
    [
        new($"{Prefix} child 1", "First step of the plan.", "chore", []),
        new($"{Prefix} child 2", "Second step; needs the first.", "feature", [0]),
    ];

    /// <summary>Every scenario, in the order the recorder runs them on its throwaway project (the listing first, on an otherwise empty one).</summary>
    internal static readonly IReadOnlyList<KanbanScenario> Scenarios =
    [
        new("list-ready.json", ListReady, PageSize: 2),
        new("lifecycle.json", Lifecycle),
        new("read-spec.json", ReadSpec),
        new("claims.json", Claims),
        new("plan.json", Plan),
        new("conflict.json", Conflict),
        new("busy.json", Busy),
    ];

    // ---- The scenarios: what the adapter is asked, in the recording and in every replay ----

    /// <summary>List ready: the watched project's ready, unblocked, unclaimed items, page by page, each given its key.</summary>
    private static async Task ListReady(KanbanContractSession s, KanbanFixture f)
    {
        var listed = new List<Guid>();
        foreach (var key in await s.Source.ListReadyAsync(None))
        {
            listed.Add((await s.Keys.ItemAsync(key, None))!.Value);
        }
        Assert.Equal(f.Ids("ready"), listed);
    }

    /// <summary>
    /// One item from Ready to Done and back: key, claim and read back, an idempotent second claim, scope, renewal, status
    /// (InProgress, Done, back to Backlog on a stop), comment, links (pr and branch; one repeated), make-ready, release.
    /// </summary>
    private static async Task Lifecycle(KanbanContractSession s, KanbanFixture f)
    {
        var source = s.Source;
        var id = await source.KeyOfAsync(f.Id("item"), None);
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(id, ignoreScope: false, None)); // checked, claimed, read back, moved
        await s.RecordClaimedAsync(id);
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(id, ignoreScope: false, None)); // already ours: claimed again, no move
        Assert.True(await source.InScopeAsync(id, None));
        Assert.Equal(ClaimRenewal.Held, await source.RenewClaimAsync(id, None));
        await source.ReportStateAsync(id, BoardState.Claimed, null, None); // already InProgress: read only
        await source.CommentAsync(id, "contract test comment", None);
        await source.LinkAsync(id, [PrUrl, BranchUrl], None);
        await source.LinkAsync(id, [PrUrl], None); // already linked: changes nothing
        await source.ReportStateAsync(id, BoardState.Merged, null, None);
        await source.ReportStateAsync(id, BoardState.Stopped, "stopped by the contract test", None);
        await source.ReleaseAsync(id, None); // no claim left: read only
        Assert.Equal(ReadyResult.Moved, await source.MakeReadyAsync(id, None));
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(id, ignoreScope: false, None));
        await source.ReleaseAsync(id, None);
    }

    /// <summary>The spec of an item with its epic and the epic's documents, of one without an epic, and an epic's key read ahead (K5).</summary>
    private static async Task ReadSpec(KanbanContractSession s, KanbanFixture f)
    {
        var inEpic = await s.Keys.KeyAsync(f.Id("inEpic"), f.Id("project"), None);
        var spec = await s.Source.ReadSpecAsync(inEpic, None);
        Assert.Equal($"{Prefix} item in an epic", spec.Story.Name);
        Assert.Equal("feature", spec.Story.StoryType);
        Assert.Equal(f.Id("inEpic").ToString("D"), spec.Story.BoardId);
        // The project's repository_url, as the Repo: line every work source leads a description with.
        Assert.StartsWith("Repo: michaeltrefry/dark-factory-sandbox\n", spec.Story.Description);
        Assert.Equal(["simple"], spec.Story.Labels!);
        Assert.Equal(2, spec.Story.Estimate);
        Assert.Equal(WorkerModelClass.Low, WorkerModelClass.Coding(spec.Story));
        Assert.Equal($"{Prefix} epic", spec.Epic!.Name);
        Assert.Contains("Objective", spec.Epic.Description);
        var document = Assert.Single(spec.Documents);
        Assert.Equal($"{Prefix} spec", document.Title);
        Assert.Contains("Work sources", document.Markdown);

        var plain = await s.Keys.KeyAsync(f.Id("plain"), f.Id("project"), None);
        var alone = await s.Source.ReadSpecAsync(plain, None);
        Assert.Equal("chore", alone.Story.StoryType);
        Assert.Null(alone.Epic);
        Assert.Empty(alone.Documents);
        Assert.Empty(alone.Story.Labels!);
        Assert.Null(alone.Story.Estimate);

        Assert.Equal(spec.Epic.Id, (long)await s.Source.EpicKeyOfAsync(f.Id("epic"), None));
    }

    /// <summary>
    /// Claim renewal: a held claim is extended; one that is gone (released, as a lapse leaves it) is taken again for an item the
    /// ledger records as the factory's; one another caller took is lost, and the item's next claim refused.
    /// </summary>
    private static async Task Claims(KanbanContractSession s, KanbanFixture f)
    {
        var source = s.Source;
        var lapsed = await source.KeyOfAsync(f.Id("lapsed"), None);
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(lapsed, ignoreScope: false, None));
        await s.RecordClaimedAsync(lapsed);
        Assert.Equal(ClaimRenewal.Held, await source.RenewClaimAsync(lapsed, None));
        await s.Aside(KanbanFixtureRecorder.DropClaim);
        Assert.Equal(ClaimRenewal.Held, await source.RenewClaimAsync(lapsed, None)); // refused conflict, read, claimed again

        var lost = await source.KeyOfAsync(f.Id("lost"), None);
        Assert.Equal(ClaimResult.Ok, await source.ClaimAsync(lost, ignoreScope: false, None));
        await s.RecordClaimedAsync(lost);
        await s.Aside(KanbanFixtureRecorder.TakeClaim);
        var renewal = await source.RenewClaimAsync(lost, None); // refused already_claimed, read: someone else's
        Assert.False(renewal.IsHeld);
        Assert.Contains(KanbanFixtureRecorder.OtherMarker, renewal.Lost);
        var claim = await source.ClaimAsync(lost, ignoreScope: false, None);
        Assert.False(claim.Claimed);
        Assert.StartsWith("the item is claimed by ", claim.Refusal);
    }

    /// <summary>
    /// A plan (K4): child creation with a dependency, a retry of it that creates nothing, the blockers, make-ready (and a second
    /// one that finds the item gone from the backlog), and the return of an unstarted item.
    /// </summary>
    private static async Task Plan(KanbanContractSession s, KanbanFixture f)
    {
        var source = s.Source;
        var parent = await s.Keys.KeyAsync(f.Id("parent"), f.Id("project"), None);
        var children = await source.CreateChildrenAsync(parent, Children, None);
        Assert.Equal(children, await source.CreateChildrenAsync(parent, Children, None));
        var made = new List<Guid>();
        foreach (var child in children)
        {
            made.Add((await s.Keys.ItemAsync(child, None))!.Value);
        }
        if (f.Parameters.ContainsKey("children"))
        {
            Assert.Equal(f.Ids("children"), made);
        }
        else
        {
            // Under recording: the items the board made, for the replay to find again and the recorder to remove.
            f.Set("children", made);
        }
        Assert.Equal([new Blocker(children[0], false)], await source.BlockersAsync(children[1], None));
        Assert.Equal(ReadyResult.Moved, await source.MakeReadyAsync(children[1], None));
        Assert.False(await source.InBacklogAsync(children[1], None));
        Assert.Equal(ReadyResult.AlreadyLeft, await source.MakeReadyAsync(children[1], None));
        Assert.True(await source.ReturnUnstartedAsync(children[1], "returned by the contract test", None));
        Assert.True(await source.InBacklogAsync(children[1], None));
    }

    /// <summary>A <c>conflict</c> that is re-read (K3): someone edits the item between the read and the conditional move.</summary>
    private static async Task Conflict(KanbanContractSession s, KanbanFixture f)
    {
        var id = await s.Keys.KeyAsync(f.Id("item"), f.Id("project"), None);
        Assert.Equal(ReadyResult.Moved, await s.Source.MakeReadyAsync(id, None));
        Assert.Empty(s.Waits);
    }

    /// <summary>A <c>busy</c> that is retried (K3): the same call again, after the first backoff.</summary>
    private static async Task Busy(KanbanContractSession s, KanbanFixture f)
    {
        var id = await s.Keys.KeyAsync(f.Id("item"), f.Id("project"), None);
        Assert.Equal(ReadyResult.Moved, await s.Source.MakeReadyAsync(id, None));
        Assert.Equal([KanbanBoard.BusyBackoff(1)], s.Waits);
    }

    // ---- Replay ----

    /// <summary>
    /// Runs the fixture's scenario over a strict replay of it and fails on the first request the recording does not have, on a
    /// recorded call the adapter never made, and on whatever the scenario itself asserts.
    /// </summary>
    internal static async Task ReplayAsync(string name, KanbanFixture fixture, KanbanFraming framing = KanbanFraming.Alternate)
    {
        var scenario = Scenarios.Single(s => s.Fixture == name);
        var replay = new KanbanReplayHandler(fixture) { Framing = framing };
        var session = new KanbanContractSession(replay, ReplayEndpoint, "tok-test", fixture.Parameters["projectKey"]!.GetValue<string>(), scenario.PageSize);
        try
        {
            await scenario.Run(session, fixture);
        }
        catch (Exception) when (replay.Mismatches.Count > 0)
        {
            // The mismatch is the cause; the adapter's own exception after it is not what to report.
        }
        replay.AssertAllReplayed();
    }

    public static TheoryData<string, KanbanFraming> EveryFixtureInEveryFraming()
    {
        var data = new TheoryData<string, KanbanFraming>();
        foreach (var name in KanbanFixture.Names)
        {
            foreach (var framing in Enum.GetValues<KanbanFraming>())
            {
                data.Add(name, framing);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryFraming))]
    public async Task The_adapter_replays_every_recorded_conversation_exactly_whatever_the_framing(string name, KanbanFraming framing) =>
        await ReplayAsync(name, KanbanFixture.Load(name), framing);

    [Fact]
    public void There_is_one_fixture_per_scenario_and_no_other_file()
    {
        Assert.Equal(KanbanFixture.Names.Order(), Directory.GetFiles(KanbanFixture.OutputDirectory).Select(Path.GetFileName).Order());
        Assert.Equal(KanbanFixture.Names, Scenarios.Select(s => s.Fixture));
    }

    /// <summary>The fixtures together hold every tool the typed caller sends: no call of the adapter is left unpinned.</summary>
    [Fact]
    public void The_fixtures_cover_every_tool_the_adapter_sends()
    {
        var recorded = KanbanFixture.Names.SelectMany(n => KanbanFixture.Load(n).Interactions.Select(i => i.Tool)).ToHashSet();
        Assert.Equal(KanbanBoard.ReadTools.Concat(KanbanBoard.WriteTools).Order(), recorded.Order());
    }

    private static string? Text(JsonNode? node, string field) => node?[field]?.GetValue<string>();

    [Fact]
    public void List_ready_asks_for_the_ready_unblocked_unclaimed_items_page_by_page_and_the_board_leaves_the_others_out()
    {
        var fixture = KanbanFixture.Load("list-ready.json");
        var project = Assert.Single(Assert.Single(fixture.Calls("list_projects")).Answer!.AsArray());
        Assert.Equal(fixture.Parameters["projectKey"]!.GetValue<string>(), Text(project, "key"));
        var pages = fixture.Calls("list_work_items");
        Assert.Equal(2, pages.Count);
        // The contract's "polling for ready work", paged, comments left out, and no updated_since.
        Assert.All(pages, p => Assert.Equal((fixture.Id("project").ToString("D"), "Ready", true, "unclaimed", 2, false),
            (Text(p.Arguments, "projectId"), Text(p.Arguments, "status"), p.Arguments["unblocked_only"]!.GetValue<bool>(),
                Text(p.Arguments, "claim_state"), p.Arguments["limit"]!.GetValue<int>(), p.Arguments["include_comments"]!.GetValue<bool>())));
        Assert.All(pages, p => Assert.False(p.Arguments.ContainsKey("updated_since")));
        // The second page is asked for with the first's cursor, unchanged; the last page has none.
        Assert.False(pages[0].Arguments.ContainsKey("cursor"));
        Assert.Equal(Text(pages[0].Answer, "next_cursor"), Text(pages[1].Arguments, "cursor"));
        Assert.Null(pages[1].Answer!["next_cursor"]);
        var listed = pages.SelectMany(p => p.Answer!["items"]!.AsArray()).Select(i => Guid.Parse(Text(i, "id")!)).ToList();
        Assert.Equal(fixture.Ids("ready"), listed);
        Assert.DoesNotContain(listed, fixture.Ids("left out").Contains);
        Assert.All(pages.SelectMany(p => p.Answer!["items"]!.AsArray()), i => Assert.Empty(i!["comments"]!.AsArray()));
    }

    [Fact]
    public void The_lifecycle_claims_under_the_items_marker_moves_on_the_version_read_and_keys_its_comments()
    {
        var fixture = KanbanFixture.Load("lifecycle.json");
        var (item, calls) = (fixture.Id("item"), fixture.Interactions);
        var marker = KanbanWorkSource.Marker(item);
        // Claims: under the marker derived from the item's board id, for the longest the board allows, each read back.
        var claims = calls.Select((c, i) => (c, i)).Where(x => x.c.Tool == "claim_work_item").ToList();
        Assert.Equal(3, claims.Count);
        Assert.All(claims, x =>
        {
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = item.ToString("D"), ["marker"] = marker, ["ttl_seconds"] = KanbanWorkSource.ClaimTtlSeconds },
                x.c.Arguments), x.c.Arguments.ToJsonString());
            Assert.Equal(marker, Text(x.c.Answer, "claim_marker"));
            Assert.Equal("get_work_item", calls[x.i + 1].Tool);
            Assert.Equal(Text(x.c.Answer, "claimant_id"), Text(calls[x.i + 1].Answer, "claimant_id"));
        });
        Assert.All(fixture.Calls("renew_work_item_claim").Concat(fixture.Calls("release_work_item_claim")), c => Assert.Equal(marker, Text(c.Arguments, "marker")));
        Assert.Equal(2, fixture.Calls("release_work_item_claim").Count);
        Assert.All(fixture.Calls("release_work_item_claim"), c => Assert.Null(c.Answer!["claim_marker"]));
        // Status changes: update_work_item with the status and the version of the read just before it, nothing else.
        var moves = calls.Select((c, i) => (c, i)).Where(x => x.c.Tool == "update_work_item").ToList();
        Assert.Equal(["InProgress", "Done", "Backlog", "Ready", "InProgress"], moves.Select(m => Text(m.c.Arguments, "status")));
        Assert.All(moves, m =>
        {
            var read = calls.Take(m.i).Last(c => c.Tool == "get_work_item").Answer!;
            Assert.True(JsonNode.DeepEquals(new JsonObject
            {
                ["itemId"] = item.ToString("D"), ["status"] = Text(m.c.Arguments, "status"), ["expected_version"] = read["version"]!.GetValue<long>(),
            }, m.c.Arguments), m.c.Arguments.ToJsonString());
            Assert.Equal(read["version"]!.GetValue<long>() + 1, m.c.Answer!["version"]!.GetValue<long>());
            Assert.Equal(Text(m.c.Arguments, "status"), Text(m.c.Answer, "status"));
        });
        // Comments: attributed, each under the key of the item's board id, its version then, and the body.
        var comments = fixture.Calls("add_work_item_comment");
        Assert.Equal(["[author: dark-factory] contract test comment", "[author: dark-factory] stopped by the contract test"],
            comments.Select(c => Text(c.Arguments, "body")));
        Assert.All(comments, c =>
        {
            Assert.Equal(KanbanWorkSource.CommentKey(item, c.Answer!["version"]!.GetValue<long>(), Text(c.Arguments, "body")!), Text(c.Arguments, "idempotency_key"));
            Assert.True(c.Answer["comment_created"]!.GetValue<bool>());
            Assert.Contains(c.Answer["comments"]!.AsArray(), m => Text(m, "body") == Text(c.Arguments, "body"));
        });
        // Links: the pull request and the branch under their kinds; the repeat changes nothing.
        var links = fixture.Calls("add_work_item_link");
        Assert.Equal([("pr", PrUrl), ("branch", BranchUrl), ("pr", PrUrl)], links.Select(l => (Text(l.Arguments, "kind")!, Text(l.Arguments, "url")!)));
        Assert.Equal(2, links[^1].Answer!["links"]!.AsArray().Count);
    }

    [Fact]
    public void A_spec_reads_the_item_its_project_its_epic_and_the_epics_documents()
    {
        var fixture = KanbanFixture.Load("read-spec.json");
        Assert.Equal(["get_work_item", "list_projects", "get_epic", "list_epic_documents", "get_work_item", "list_projects", "get_epic"],
            fixture.Interactions.Select(i => i.Tool));
        var epic = fixture.Id("epic").ToString("D");
        Assert.All(fixture.Calls("get_epic").Concat(fixture.Calls("list_epic_documents")),
            c => Assert.True(JsonNode.DeepEquals(new JsonObject { ["epicId"] = epic }, c.Arguments)));
        Assert.Equal(epic, Text(fixture.Interactions[0].Answer, "epicId"));
        // An item in no epic leaves epicId out (the contract: left out, never null).
        Assert.False(fixture.Interactions[4].Answer!.AsObject().ContainsKey("epicId"));
        Assert.Equal(KanbanFixtureRecorder.Repository, Text(Assert.Single(fixture.Calls("list_projects")[0].Answer!.AsArray()), "repository_url"));
    }

    [Fact]
    public void A_renewal_the_board_refuses_is_decided_on_a_fresh_read_reclaimed_when_gone_and_lost_when_someone_elses()
    {
        var fixture = KanbanFixture.Load("claims.json");
        var renewals = fixture.Interactions.Select((c, i) => (c, i)).Where(x => x.c.Tool == "renew_work_item_claim").ToList();
        Assert.Equal([false, true, true], renewals.Select(r => r.c.IsError));
        Assert.Equal([KanbanException.Conflict, KanbanException.AlreadyClaimed], renewals.Skip(1).Select(r => Text(r.c.Answer, "code")));
        Assert.All(renewals, r => Assert.Equal(KanbanWorkSource.ClaimTtlSeconds, r.c.Arguments["ttl_seconds"]!.GetValue<int>()));
        // conflict: the claim is gone — read (no claim, still InProgress), then claimed again.
        var (gone, reread) = (renewals[1], fixture.Interactions[renewals[1].i + 1]);
        Assert.Equal(("get_work_item", "InProgress"), (reread.Tool, Text(reread.Answer, "status")));
        Assert.Null(reread.Answer!["claim_marker"]);
        Assert.Equal("claim_work_item", fixture.Interactions[gone.i + 2].Tool);
        Assert.Equal(KanbanWorkSource.Marker(fixture.Id("lapsed")), Text(fixture.Interactions[gone.i + 2].Answer, "claim_marker"));
        // already_claimed: read — another marker holds it; nothing is written after that.
        var theirs = fixture.Interactions[renewals[2].i + 1];
        Assert.Equal(("get_work_item", KanbanFixtureRecorder.OtherMarker), (theirs.Tool, Text(theirs.Answer, "claim_marker")));
        Assert.DoesNotContain(fixture.Interactions.Skip(renewals[2].i + 1), c => KanbanBoard.WriteTools.Contains(c.Tool));
    }

    [Fact]
    public void A_plans_items_are_created_once_under_their_keys_joined_by_a_dependency_released_and_returned()
    {
        var fixture = KanbanFixture.Load("plan.json");
        var (project, parent, children) = (fixture.Id("project"), fixture.Id("parent"), fixture.Ids("children"));
        var creates = fixture.Calls("create_work_item");
        Assert.Equal(4, creates.Count);
        // In the parent's project and epic, typed as planned, each under the key of the parent's board id and its own content.
        var epic = Text(fixture.Interactions[0].Answer, "epicId");
        Assert.NotNull(epic);
        for (var i = 0; i < creates.Count; i++)
        {
            var child = Children[i % 2];
            Assert.True(JsonNode.DeepEquals(new JsonObject
            {
                ["projectId"] = project.ToString("D"), ["title"] = child.Name, ["type"] = KanbanWorkSource.ItemType(child.StoryType),
                ["idempotency_key"] = KanbanWorkSource.ChildKey(parent, child), ["description"] = child.Description, ["epicId"] = epic,
            }, creates[i].Arguments), creates[i].Arguments.ToJsonString());
            Assert.Equal(children[i % 2].ToString("D"), Text(creates[i].Answer, "id"));
            Assert.True(Text(creates[i].Arguments, "idempotency_key")!.Length <= KanbanWorkSource.MaxIdempotencyKeyLength);
        }
        // The first attempt created both in the backlog; the retry created nothing and was answered the same items.
        Assert.Equal([true, true, false, false], creates.Select(c => c.Answer!["created"]!.GetValue<bool>()));
        Assert.All(creates.Take(2), c => Assert.Equal("Backlog", Text(c.Answer, "status")));
        // One dependency, written once: the retry's answer already lists the blocker.
        var dependency = Assert.Single(fixture.Calls("add_work_item_dependency"));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["id"] = children[1].ToString("D"), ["blocked_by_id"] = children[0].ToString("D") }, dependency.Arguments));
        Assert.Equal(children[0].ToString("D"), Text(Assert.Single(creates[3].Answer!["blockers"]!.AsArray()), "id"));
        // The release and the return: Backlog -> Ready -> Backlog, each on the version read, the return with its comment.
        var moves = fixture.Calls("update_work_item");
        Assert.Equal([("Ready", 1L), ("Backlog", 2L)], moves.Select(m => (Text(m.Arguments, "status")!, m.Arguments["expected_version"]!.GetValue<long>())));
        Assert.All(moves, m => Assert.Equal(children[1].ToString("D"), Text(m.Arguments, "itemId")));
        Assert.Equal("[author: dark-factory] returned by the contract test", Text(Assert.Single(fixture.Calls("add_work_item_comment")).Arguments, "body"));
        // Nothing was claimed, so nothing is released.
        Assert.Empty(fixture.Calls("release_work_item_claim"));
    }

    [Fact]
    public void A_conflict_is_read_again_and_the_move_made_on_the_new_version_keeping_the_other_callers_edit()
    {
        var fixture = KanbanFixture.Load("conflict.json");
        Assert.Equal(["get_work_item", "update_work_item", "get_work_item", "update_work_item"], fixture.Interactions.Select(i => i.Tool));
        var (read, refused, again, moved) = (fixture.Interactions[0], fixture.Interactions[1], fixture.Interactions[2], fixture.Interactions[3]);
        // The board's own refusal of a stale expected_version, in the contract's Error shape: not synthetic.
        Assert.True(refused.IsError);
        Assert.Null(refused.Note);
        Assert.Equal(KanbanException.Conflict, Text(refused.Answer, "code"));
        Assert.Equal(read.Answer!["version"]!.GetValue<long>(), refused.Arguments["expected_version"]!.GetValue<long>());
        // Someone else's edit moved the version; the same move is made on the version read again, and their title stays.
        Assert.Equal(read.Answer["version"]!.GetValue<long>() + 1, again.Answer!["version"]!.GetValue<long>());
        Assert.NotEqual(Text(read.Answer, "title"), Text(again.Answer, "title"));
        Assert.Equal(again.Answer["version"]!.GetValue<long>(), moved.Arguments["expected_version"]!.GetValue<long>());
        Assert.Equal(("Ready", Text(again.Answer, "title")), (Text(moved.Answer, "status"), Text(moved.Answer, "title")));
    }

    [Fact]
    public void A_busy_refusal_is_answered_by_the_same_call_again()
    {
        var fixture = KanbanFixture.Load("busy.json");
        Assert.Equal(["get_work_item", "update_work_item", "update_work_item"], fixture.Interactions.Select(i => i.Tool));
        var (busy, resent) = (fixture.Interactions[1], fixture.Interactions[2]);
        Assert.True(busy.IsError);
        Assert.Equal(KanbanException.Busy, Text(busy.Answer, "code"));
        // The one interaction no board answered: it says so (the board cannot be made busy on demand).
        Assert.Equal(KanbanRecordingHandler.SyntheticBusyNote, busy.Note);
        Assert.Equal([busy], fixture.Interactions.Where(i => i.Note is not null));
        // The same call, argument for argument; its answer is the board's.
        Assert.True(JsonNode.DeepEquals(busy.Arguments, resent.Arguments));
        Assert.False(resent.IsError);
        Assert.Equal("Ready", Text(resent.Answer, "status"));
    }

    // ---- Strict matching (the replay handler) ----

    private static KanbanFixture Lifecycle() => KanbanFixture.Load("lifecycle.json");

    private static async Task<string> FailsAsync(KanbanFixture fixture) =>
        (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ReplayAsync("lifecycle.json", fixture))).Message;

    private static int IndexOf(KanbanFixture fixture, string tool) => fixture.Interactions.FindIndex(i => i.Tool == tool);

    [Fact]
    public async Task A_tool_name_the_recording_does_not_have_fails_the_replay()
    {
        // The recording holds move_work_item where the adapter sends update_work_item.
        var fixture = Lifecycle();
        var at = IndexOf(fixture, "update_work_item");
        fixture.Interactions[at] = fixture.Interactions[at] with { Tool = "move_work_item" };

        var message = await FailsAsync(fixture);

        Assert.Contains($"call {at + 1} differs from the recording", message);
        Assert.Contains("expected: move_work_item", message);
        Assert.Contains("actual:   update_work_item", message);
    }

    [Theory]
    [InlineData("update_work_item", "expected_version")] // a guarded write recorded unguarded
    [InlineData("claim_work_item", "ttl_seconds")]
    [InlineData("add_work_item_comment", "idempotency_key")]
    public async Task An_argument_the_recording_does_not_have_fails_the_replay(string tool, string argument)
    {
        var fixture = Lifecycle();
        var at = IndexOf(fixture, tool);
        var recorded = (JsonObject)fixture.Interactions[at].Arguments.DeepClone();
        Assert.True(recorded.Remove(argument));
        fixture.Interactions[at] = fixture.Interactions[at] with { Arguments = recorded };

        var message = await FailsAsync(fixture);

        Assert.Contains($"call {at + 1} differs from the recording", message);
        Assert.Contains($"\"{argument}\"", message);
    }

    [Fact]
    public async Task An_argument_the_adapter_leaves_out_or_sends_with_another_value_fails_the_replay()
    {
        var missing = Lifecycle();
        var at = IndexOf(missing, "get_work_item");
        var more = (JsonObject)missing.Interactions[at].Arguments.DeepClone();
        more["include_comments"] = false;
        missing.Interactions[at] = missing.Interactions[at] with { Arguments = more };
        Assert.Contains($"call {at + 1} differs from the recording", await FailsAsync(missing));

        var other = Lifecycle();
        at = IndexOf(other, "claim_work_item");
        var changed = (JsonObject)other.Interactions[at].Arguments.DeepClone();
        changed["ttl_seconds"] = KanbanWorkSource.ClaimTtlSeconds - 1;
        other.Interactions[at] = other.Interactions[at] with { Arguments = changed };
        Assert.Contains($"call {at + 1} differs from the recording", await FailsAsync(other));
    }

    [Fact]
    public async Task A_call_beyond_the_recording_and_a_recorded_call_never_made_fail_the_replay()
    {
        var shorter = Lifecycle();
        shorter.Interactions.RemoveAt(shorter.Interactions.Count - 1);
        Assert.Contains("unexpected extra call release_work_item_claim", await FailsAsync(shorter));

        var longer = Lifecycle();
        longer.Interactions.Add(longer.Interactions[0]);
        Assert.Contains($"the adapter stopped after {longer.Interactions.Count - 1} of {longer.Interactions.Count} recorded calls", await FailsAsync(longer));
    }

    private static KanbanFixture One(string tool, JsonObject arguments, JsonNode answer, bool isError = false) =>
        new() { Interactions = [new KanbanInteraction(tool, arguments, isError, answer)] };

    private static HttpRequestMessage Post(JsonObject message, string? session = "none", string? token = "tok")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ReplayEndpoint) { Content = new StringContent(message.ToJsonString()) };
        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }
        if (session is not null)
        {
            request.Headers.Add(McpHttpClient.SessionHeader, session);
        }
        return request;
    }

    private static JsonObject Rpc(string method, JsonObject? parameters = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters ?? new JsonObject() };

    /// <summary>
    /// The handshake is normalised — not recorded, any session id — but still checked: a bearer token on every request, a
    /// session opened by initialize and carried by the call, closed afterwards, and no other MCP method.
    /// </summary>
    [Fact]
    public async Task The_replay_checks_the_handshake_it_does_not_record()
    {
        var fixture = One("get_work_item", new JsonObject { ["itemId"] = "x" }, new JsonObject { ["id"] = "x" });
        var call = Rpc("tools/call", new JsonObject { ["name"] = "get_work_item", ["arguments"] = new JsonObject { ["itemId"] = "x" } });
        async Task<string> Refused(KanbanReplayHandler handler, HttpRequestMessage request)
        {
            using var http = new HttpClient(handler, disposeHandler: false);
            return (await Assert.ThrowsAsync<KanbanReplayException>(() => http.SendAsync(request, None))).Message;
        }

        Assert.Contains("no bearer token", await Refused(new(fixture), Post(Rpc("initialize"), session: null, token: null)));
        Assert.Contains("outside an open session", await Refused(new(fixture), Post(call, session: null)));
        Assert.Contains("outside an open session", await Refused(new(fixture), Post(call, session: "made-up")));
        Assert.Contains("initialize was sent inside a session", await Refused(new(fixture), Post(Rpc("initialize"))));

        var replay = new KanbanReplayHandler(fixture);
        using var client = new HttpClient(replay, disposeHandler: false);
        var first = (await client.SendAsync(Post(Rpc("initialize"), session: null), None)).Headers.GetValues(McpHttpClient.SessionHeader).Single();
        var second = (await client.SendAsync(Post(Rpc("initialize"), session: null), None)).Headers.GetValues(McpHttpClient.SessionHeader).Single();
        Assert.NotEqual(first, second); // a fresh id per session; the adapter must send back the one it was given
        Assert.Contains("unexpected MCP method 'tools/list'", await Refused(replay, Post(Rpc("tools/list"), first)));
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, first), None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(call, first), None)).StatusCode);
        // A session left open is not a finished replay; neither is a recorded mismatch.
        Assert.False(replay.AllReplayed);
        Assert.Throws<Xunit.Sdk.TrueException>(replay.AssertAllReplayed);
    }

    [Theory]
    [InlineData(KanbanFraming.Json, "application/json")]
    [InlineData(KanbanFraming.Sse, "text/event-stream")]
    public async Task A_replayed_answer_is_framed_as_asked_and_reads_the_same_through_the_mcp_client(KanbanFraming framing, string mediaType)
    {
        var refusal = new JsonObject { ["code"] = "conflict", ["message"] = "Work item x is at version 3." };
        var fixture = new KanbanFixture
        {
            Interactions =
            [
                new("get_work_item", new JsonObject { ["itemId"] = "x" }, false, new JsonObject { ["id"] = "x", ["version"] = 3 }),
                new("update_work_item", new JsonObject { ["itemId"] = "x" }, true, refusal.DeepClone()),
                new("update_work_item", new JsonObject { ["itemId"] = "x" }, true, JsonValue.Create("An error occurred invoking the tool.")),
            ],
        };
        var replay = new KanbanReplayHandler(fixture) { Framing = framing };
        var types = new List<string?>();
        var mcp = KanbanWrites.Client(new HttpClient(new Tap(replay, r => types.Add(r.Content?.Headers.ContentType?.MediaType))) { BaseAddress = ReplayEndpoint }, "tok");

        var ok = await mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None);
        var refused = await mcp.CallToolAsync("update_work_item", new JsonObject { ["itemId"] = "x" }, None);
        var plain = await mcp.CallToolAsync("update_work_item", new JsonObject { ["itemId"] = "x" }, None);

        // A success: one text block holding the JSON document. A refusal: isError with {code, message} as structuredContent and
        // as the text. A plain MCP error: isError, its text, and no structuredContent.
        Assert.Equal((false, """{"id":"x","version":3}"""), (ok.IsError, ok.Text));
        Assert.True(refused.IsError);
        Assert.True(JsonNode.DeepEquals(refusal, refused.Structured));
        Assert.True(JsonNode.DeepEquals(refusal, JsonNode.Parse(refused.Text)));
        Assert.Equal((true, "An error occurred invoking the tool.", (JsonNode?)null), (plain.IsError, plain.Text, plain.Structured));
        replay.AssertAllReplayed();
        // Every answered request (initialize and the call, three times) came in the framing asked for.
        Assert.Equal(Enumerable.Repeat(mediaType, 6), types.Where(t => t is not null));
    }

    /// <summary>Passes requests on and shows each response to <paramref name="seen"/>.</summary>
    private sealed class Tap(HttpMessageHandler inner, Action<HttpResponseMessage> seen) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            seen(response);
            return response;
        }
    }

    // ---- Normalisation and trimming (the recording handler) ----

    /// <summary>A minimal MCP server answering every <c>tools/call</c> with <paramref name="answer"/>'s result, framed as JSON or as an event stream.</summary>
    private sealed class CannedMcp(Func<string, JsonObject, JsonObject> answer, bool sse) : HttpMessageHandler
    {
        public List<string> Forwarded { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            switch (message["method"]!.GetValue<string>())
            {
                case "initialize":
                    return KanbanMcp.Respond(message["id"], new JsonObject { ["protocolVersion"] = McpHttpClient.ProtocolVersion }, sse, $"session-{Guid.NewGuid():N}");
                case "tools/call":
                    var tool = message["params"]!["name"]!.GetValue<string>();
                    Forwarded.Add(tool);
                    if (Status != HttpStatusCode.OK)
                    {
                        return FakeApi.Json(Status, """{"error":"unavailable"}""");
                    }
                    return KanbanMcp.Respond(message["id"], answer(tool, message["params"]!["arguments"]!.AsObject()), sse);
                default:
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
        }
    }

    private static (KanbanFixture Fixture, KanbanRecordingHandler Recording, McpHttpClient Mcp) Recording(HttpMessageHandler inner, Guid? project = null, Guid? epic = null)
    {
        var fixture = new KanbanFixture();
        var recording = new KanbanRecordingHandler(fixture) { InnerHandler = inner, OwnProjects = project is { } p ? [p] : [], OwnEpics = epic is { } e ? [e] : [] };
        return (fixture, recording, KanbanWrites.Client(new HttpClient(recording) { BaseAddress = ReplayEndpoint }, "kbp_a_secret_token_value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_recording_is_the_same_whatever_the_framing_and_holds_no_envelope_session_or_token(bool sse)
    {
        var refusal = new JsonObject { ["code"] = "not_found", ["message"] = "Work item y was not found." };
        var inner = new CannedMcp((_, arguments) => arguments["itemId"]!.GetValue<string>() switch
        {
            "x" => FakeMcpServer.Text("""{ "id": "x",  "version": 3 }"""),
            "y" => FakeMcpServer.Structured(refusal.ToJsonString(), (JsonObject)refusal.DeepClone(), isError: true),
            _ => Plain(),
        }, sse);
        var (fixture, _, mcp) = Recording(inner);

        var ok = await mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None);
        var refused = await mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "y" }, None);
        var plain = await mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "z" }, None);

        // Recorded: the tool, the arguments, and the answer as one JSON document — the same from JSON and from an event stream.
        Assert.Equal("""
            {"recorded_from":"","parameters":{},"interactions":[{"tool":"get_work_item","arguments":{"itemId":"x"},"isError":false,"answer":{"id":"x","version":3}},{"tool":"get_work_item","arguments":{"itemId":"y"},"isError":true,"answer":{"code":"not_found","message":"Work item y was not found."}},{"tool":"get_work_item","arguments":{"itemId":"z"},"isError":true,"answer":"An error occurred invoking the tool."}]}
            """, JsonNode.Parse(fixture.ToJson())!.ToJsonString());
        Assert.DoesNotContain("kbp_", fixture.ToJson());
        Assert.DoesNotContain("session", fixture.ToJson());
        Assert.DoesNotContain("jsonrpc", fixture.ToJson());
        // The adapter under recording is handed the normalised answer: what a replay will hand it.
        Assert.Equal((false, """{"id":"x","version":3}"""), (ok.IsError, ok.Text));
        Assert.True(JsonNode.DeepEquals(refusal, refused.Structured));
        Assert.Equal((true, "An error occurred invoking the tool.", (JsonNode?)null), (plain.IsError, plain.Text, plain.Structured));
        // And the recording replays.
        var replay = new KanbanReplayHandler(KanbanFixture.Parse(fixture.ToJson()));
        var again = KanbanWrites.Client(new HttpClient(replay) { BaseAddress = ReplayEndpoint }, "tok");
        Assert.Equal(ok.Text, (await again.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None)).Text);

        static JsonObject Plain()
        {
            var result = FakeMcpServer.Text("An error occurred invoking the tool.");
            result["isError"] = true;
            return result;
        }
    }

    private static JsonObject Item(Guid project, string title, string? claimant = null) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["projectId"] = project.ToString(), ["title"] = title, ["claimant"] = claimant,
        ["comments"] = new JsonArray(new JsonObject { ["author"] = "Avery Example", ["body"] = "write to avery@acme.example" }),
    };

    [Fact]
    public async Task A_recorded_listing_keeps_only_the_recordings_own_projects_items_and_documents()
    {
        var (own, theirs, epic) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        JsonObject Project(Guid id, string key) => new() { ["id"] = id.ToString(), ["name"] = $"Project {key}", ["key"] = key };
        var inner = new CannedMcp((tool, arguments) => FakeMcpServer.Text((tool switch
        {
            "list_projects" => new JsonArray(Project(theirs, "SECRET"), Project(own, "DFFIX1"), Project(Guid.NewGuid(), "OTHER")),
            "list_work_items" when arguments.ContainsKey("limit") => new JsonObject
            {
                ["items"] = new JsonArray(Item(theirs, "someone's plan"), Item(own, "mine 1"), Item(theirs, "someone's bug"), Item(own, "mine 2")),
                ["next_cursor"] = "cursor-2",
            },
            "list_work_items" => new JsonArray(Item(theirs, "someone's plan"), Item(own, "mine 1")),
            _ => (JsonNode)new JsonArray(
                new JsonObject { ["id"] = "d1", ["epicId"] = Guid.NewGuid().ToString(), ["title"] = "someone's document" },
                new JsonObject { ["id"] = "d2", ["epicId"] = epic.ToString(), ["title"] = "mine" }),
        }).ToJsonString()), sse: true);
        var (fixture, _, mcp) = Recording(inner, own, epic);

        var projects = await mcp.CallToolAsync("list_projects", new JsonObject(), None);
        await mcp.CallToolAsync("list_work_items", new JsonObject { ["limit"] = 4 }, None);
        await mcp.CallToolAsync("list_work_items", new JsonObject(), None);
        await mcp.CallToolAsync("list_epic_documents", new JsonObject { ["epicId"] = epic.ToString() }, None);

        var json = fixture.ToJson();
        Assert.DoesNotContain("someone", json);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain(theirs.ToString(), json);
        Assert.Equal(["DFFIX1"], fixture.Interactions[0].Answer!.AsArray().Select(p => Text(p, "key")));
        // A page keeps its cursor and its own items, in the listing's order; the plain array likewise.
        Assert.Equal(["mine 1", "mine 2"], fixture.Interactions[1].Answer!["items"]!.AsArray().Select(i => Text(i, "title")));
        Assert.Equal("cursor-2", Text(fixture.Interactions[1].Answer, "next_cursor"));
        Assert.Equal(["mine 1"], fixture.Interactions[2].Answer!.AsArray().Select(i => Text(i, "title")));
        Assert.Equal(["mine"], fixture.Interactions[3].Answer!.AsArray().Select(d => Text(d, "title")));
        // The adapter under recording saw the trimmed board too.
        Assert.DoesNotContain("SECRET", projects.Text);
        // With nothing named as its own, a listing is recorded empty.
        Assert.Empty(KanbanRecordingHandler.Trim("list_projects", fixture.Interactions[0].Answer, new HashSet<Guid>(), new HashSet<Guid>())!.AsArray());
    }

    [Fact]
    public async Task An_answer_about_someone_elses_object_or_an_unknown_listing_is_refused_not_recorded()
    {
        var (own, theirs) = (Guid.NewGuid(), Guid.NewGuid());
        var inner = new CannedMcp((tool, _) => FakeMcpServer.Text((tool == "get_work_item" ? (JsonNode)Item(theirs, "someone's plan") : new JsonArray(Item(own, "mine"))).ToJsonString()), sse: false);
        var (fixture, _, mcp) = Recording(inner, own);

        var item = await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None));
        var listing = await Assert.ThrowsAsync<InvalidOperationException>(() => mcp.CallToolAsync("search_work_items", new JsonObject { ["query"] = "plan" }, None));

        Assert.Contains("not the recording's own", item.Message);
        Assert.Contains("does not know how to trim", listing.Message);
        Assert.Empty(fixture.Interactions);
    }

    [Fact]
    public async Task A_recording_holds_no_identitys_name_and_no_email_address()
    {
        var own = Guid.NewGuid();
        var inner = new CannedMcp((tool, _) => tool == "get_work_item"
            ? FakeMcpServer.Text(Item(own, "mine", claimant: "Avery Example").ToJsonString())
            : FakeKanbanBoard.Refusal("already_claimed", "Work item x is claimed by Avery Example."), sse: true);
        var (fixture, _, mcp) = Recording(inner, own);

        var read = await mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None);
        var refused = await mcp.CallToolAsync("claim_work_item", new JsonObject { ["id"] = "x" }, None);

        var json = fixture.ToJson();
        Assert.DoesNotContain("Avery", json);
        Assert.DoesNotContain("acme.example", json);
        Assert.Contains("redacted@example.com", json);
        Assert.Equal(KanbanRecordingHandler.Recorder, Text(fixture.Interactions[0].Answer, "claimant"));
        Assert.Equal(KanbanRecordingHandler.Recorder, Text(fixture.Interactions[0].Answer!["comments"]![0], "author"));
        Assert.Equal($"Work item x is claimed by {KanbanRecordingHandler.Recorder}.", Text(fixture.Interactions[1].Answer, "message"));
        Assert.DoesNotContain("Avery", read.Text + refused.Text);
    }

    [Fact]
    public async Task A_synthetic_busy_is_recorded_marked_and_not_forwarded_and_an_http_error_is_passed_on_unrecorded()
    {
        var own = Guid.NewGuid();
        var inner = new CannedMcp((_, _) => FakeMcpServer.Text(Item(own, "mine").ToJsonString()), sse: false);
        var (fixture, recording, mcp) = Recording(inner, own);
        var asked = 0;
        recording.SyntheticBusy = (tool, _) => tool == "update_work_item" && asked++ == 0;
        var before = new List<string>();
        recording.BeforeCall = (tool, _) =>
        {
            before.Add(tool);
            return Task.CompletedTask;
        };

        var busy = await mcp.CallToolAsync("update_work_item", new JsonObject { ["itemId"] = "x" }, None);
        var answered = await mcp.CallToolAsync("update_work_item", new JsonObject { ["itemId"] = "x" }, None);

        Assert.Equal((true, "busy"), (busy.IsError, Text(busy.Structured, "code")));
        Assert.False(answered.IsError);
        Assert.Equal(["update_work_item"], inner.Forwarded); // only the resend reached the board
        Assert.Equal(["update_work_item"], before);
        Assert.Equal([KanbanRecordingHandler.SyntheticBusyNote, null], fixture.Interactions.Select(i => i.Note));
        Assert.True(JsonNode.DeepEquals(fixture.Interactions[0].Arguments, fixture.Interactions[1].Arguments));

        inner.Status = HttpStatusCode.ServiceUnavailable;
        var error = await Assert.ThrowsAsync<McpHttpException>(() => mcp.CallToolAsync("get_work_item", new JsonObject { ["itemId"] = "x" }, None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(2, fixture.Interactions.Count);
    }

    // ---- Guards: what may not be committed ----

    private static IReadOnlyList<string> Committed() => [.. Directory.GetFiles(KanbanFixture.OutputDirectory).Order()];

    [Fact]
    public void The_committed_fixtures_hold_no_token_no_email_and_nothing_but_the_recorders_own_throwaways()
    {
        Assert.NotEmpty(Committed());
        Assert.All(Committed(), file => Assert.Empty(KanbanFixtureGuard.Problems(Path.GetFileName(file), File.ReadAllText(file))));
    }

    /// <summary>The committed lifecycle fixture with <paramref name="change"/> made to it, as its file's text.</summary>
    private static string Mutated(Action<KanbanFixture> change, string name = "lifecycle.json")
    {
        var fixture = KanbanFixture.Load(name);
        change(fixture);
        return fixture.ToJson();
    }

    private static JsonObject FirstItem(KanbanFixture fixture) => fixture.Calls("get_work_item")[0].Answer!.AsObject();

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization: x")]
    [InlineData("Bearer abcdef0123456789")]
    [InlineData("kbp_3f9a1c77d2e04b6a8c5d")]
    [InlineData("kb_full_token")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.sig")]
    public void A_fixture_holding_a_token_fails_the_guard_without_repeating_it(string token)
    {
        // Wherever it is: an answer's text, an argument, a parameter.
        foreach (var json in new[]
        {
            Mutated(f => FirstItem(f)["description"] = $"note: {token}"),
            Mutated(f => f.Interactions[0].Arguments["note"] = token),
            Mutated(f => f.Parameters["note"] = token),
        })
        {
            var problem = Assert.Single(KanbanFixtureGuard.Problems("lifecycle.json", json), p => p.Contains("token"));
            Assert.DoesNotContain(token, problem);
        }
    }

    [Fact]
    public void A_fixture_holding_an_email_address_fails_the_guard()
    {
        var problems = KanbanFixtureGuard.Problems("lifecycle.json", Mutated(f => FirstItem(f)["description"] = "ask avery@acme.example"));
        Assert.Contains(problems, p => p.Contains("e-mail address"));
        // The recorder's own placeholder is not one.
        Assert.Empty(KanbanFixtureGuard.Problems("lifecycle.json", Mutated(f => FirstItem(f)["description"] = "ask redacted@example.com")));
    }

    [Fact]
    public void A_fixture_holding_an_item_project_epic_or_listing_entry_that_is_not_the_recorders_throwaway_fails_the_guard()
    {
        static IReadOnlyList<string> Problems(Action<KanbanFixture> change, string name = "lifecycle.json") =>
            KanbanFixtureGuard.Problems(name, Mutated(change, name));
        var foreign = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["projectId"] = Guid.NewGuid().ToString(), ["title"] = "Rotate the production keys" };

        // An item that is not a throwaway: by its title, and by its project.
        Assert.Contains(Problems(f => FirstItem(f)["title"] = "Rotate the production keys"), p => p.Contains("not the recorder's own throwaway") && p.Contains("title"));
        Assert.Contains(Problems(f => FirstItem(f)["projectId"] = Guid.NewGuid().ToString()), p => p.Contains("names the project"));
        // A listing entry: another project in list_projects (its name, its key), another item in a page of list_work_items.
        Assert.Contains(Problems(f => f.Calls("list_projects")[0].Answer!.AsArray().Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "Payroll", ["key"] = "PAY" })),
            p => p.Contains("'Payroll'"));
        Assert.Contains(Problems(f => f.Calls("list_projects")[0].Answer!.AsArray()[0]!["key"] = "PAY"), p => p.Contains("project key 'PAY'"));
        var listed = Problems(f => f.Calls("list_work_items")[0].Answer!["items"]!.AsArray().Add(foreign.DeepClone()), "list-ready.json");
        Assert.Contains(listed, p => p.Contains("call 2 (list_work_items) answer") && p.Contains("Rotate the production"));
        Assert.Contains(listed, p => p.Contains("names the project"));
        // An epic and an epic document, in read-spec.json; a blocker's title, in plan.json.
        Assert.Contains(Problems(f => f.Calls("get_epic")[0].Answer!["name"] = "Q4 roadmap", "read-spec.json"), p => p.Contains("'Q4 roadmap'"));
        Assert.Contains(Problems(f => f.Calls("list_epic_documents")[0].Answer![0]!["title"] = "Board minutes", "read-spec.json"), p => p.Contains("'Board minutes'"));
        Assert.Contains(Problems(f => f.Calls("get_work_item").First(c => c.Answer!["blockers"]!.AsArray().Count > 0).Answer!["blockers"]![0]!["title"] = "Someone's blocker", "plan.json"),
            p => p.Contains("'Someone's blocker'"));
        // What the adapter was asked to create counts too.
        Assert.Contains(Problems(f => f.Calls("create_work_item")[0].Arguments["title"] = "A real story", "plan.json"), p => p.Contains("arguments") && p.Contains("'A real story'"));
        // An identity's name, a board listing, and a fixture that names no throwaway project at all.
        Assert.Contains(Problems(f => f.Calls("claim_work_item")[0].Answer!["claimant"] = "Avery Example"), p => p.Contains("identity's name in claimant"));
        Assert.Contains(Problems(f =>
        {
            var listing = f.Calls("list_projects")[0].Answer!.AsArray();
            for (var n = 0; n < KanbanFixtureGuard.MaxListed; n++)
            {
                listing.Add(new JsonObject { ["id"] = f.Id("project").ToString("D"), ["name"] = $"{Prefix} {n}", ["key"] = $"DFFIX{n}" });
            }
        }), p => p.Contains($"a listing of {KanbanFixtureGuard.MaxListed + 1} entries"));
        Assert.Contains(Problems(f => f.Parameters.Remove("project")), p => p.Contains("names no throwaway project"));
    }

    // ---- Provisional fixtures ----

    [Fact]
    public void A_provisional_fixture_fails_once_real_recordings_are_demanded()
    {
        (string, KanbanFixture)[] fixtures =
        [
            ("a.json", new() { RecordedFrom = KanbanFixture.Hosted }),
            ("b.json", new() { RecordedFrom = KanbanFixture.Provisional }),
            ("c.json", new() { RecordedFrom = "recorded live, honest" }),
        ];
        // Not demanded: the two markers pass, anything else does not.
        Assert.StartsWith("c.json: recorded_from is 'recorded live, honest'", Assert.Single(KanbanFixture.RecordingProblems(fixtures, requireHosted: false)));
        // Demanded: the provisional one fails too, saying what to run.
        var demanded = KanbanFixture.RecordingProblems(fixtures, requireHosted: true);
        Assert.Equal(2, demanded.Count);
        Assert.Contains(demanded, p => p.StartsWith("b.json is still provisional") && p.Contains("KANBAN_RECORD=1") && p.Contains(KanbanFixture.RequireHostedVariable));
        Assert.Empty(KanbanFixture.RecordingProblems(fixtures.Take(1), requireHosted: true));
    }

    /// <summary>
    /// Every committed fixture says where it was recorded, and with <c>KANBAN_FIXTURES_REQUIRE_HOSTED=1</c> — which the epic's
    /// terminal story sets — none may still be provisional: "re-record against the hosted instance" cannot be forgotten silently.
    /// </summary>
    [Fact]
    public void The_committed_fixtures_say_where_they_were_recorded_and_are_real_recordings_when_those_are_demanded()
    {
        var demanded = Environment.GetEnvironmentVariable(KanbanFixture.RequireHostedVariable) == "1";
        Assert.Empty(KanbanFixture.RecordingProblems(Committed().Select(f => (Path.GetFileName(f), KanbanFixture.Parse(File.ReadAllText(f)))), demanded));
    }
}

/// <summary>
/// The recorder (sc-25986). <c>KANBAN_RECORD=1</c>: records the contract fixtures against a real KanbanBoard — the hosted one, or
/// <c>KANBAN_RECORD_URL</c> — with the full-scope token in <c>KANBAN_RECORD_TOKEN</c>, which is read from the environment of
/// whoever runs it and from nowhere else (no keychain, no configuration file), is never written to a fixture and never printed.
/// <c>KANBAN_RECORD=fake</c>: the same recorder against the in-repo fake, which is how the provisional fixtures were made.
/// Either way the fixtures are saved only after the board was cleaned up and checked (<see cref="KanbanFixtureRecorder"/>).
/// </summary>
public class KanbanFixtureRecording
{
    public const string TokenVariable = "KANBAN_RECORD_TOKEN";
    public const string UrlVariable = "KANBAN_RECORD_URL";

    [Fact]
    public async Task Record()
    {
        var ct = TestContext.Current.CancellationToken;
        KanbanFixtureRecorder recorder;
        switch (Environment.GetEnvironmentVariable("KANBAN_RECORD"))
        {
            case null or "":
                Assert.Skip("Set KANBAN_RECORD=1 (and KANBAN_RECORD_TOKEN) to re-record the Kanban contract fixtures against a real KanbanBoard, "
                    + "or KANBAN_RECORD=fake to re-record the provisional ones against the in-repo fake.");
                return;
            case "fake":
                recorder = new(new FakeKanbanBoard().Server.Api, new Uri(FakeKanbanBoard.Url), FakeKanbanBoard.FullToken, KanbanFixture.Provisional,
                    KanbanContractTests.Scenarios);
                break;
            case "1":
                var token = Environment.GetEnvironmentVariable(TokenVariable);
                Assert.False(string.IsNullOrWhiteSpace(token), $"KANBAN_RECORD=1 needs a full-scope KanbanBoard token in {TokenVariable} (the environment only).");
                var url = Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } given ? new Uri(given) : OutboundHttp.KanbanDefaultMcpUrl;
                recorder = new(new HttpClientHandler(), url, token!, KanbanFixture.Hosted, KanbanContractTests.Scenarios);
                break;
            case var other:
                Assert.Fail($"KANBAN_RECORD is '{other}': 1 records against a real KanbanBoard, fake against the in-repo fake.");
                return;
        }
        var fixtures = await recorder.RecordAsync(ct);
        foreach (var (name, fixture) in fixtures)
        {
            // Nothing is saved that the guards would refuse.
            Assert.Empty(KanbanFixtureGuard.Problems(name, fixture.ToJson()));
        }
        foreach (var (name, fixture) in fixtures)
        {
            fixture.Save(KanbanFixture.SourceDirectory, name);
        }
    }

    /// <summary>A board as the hosted one is found: someone else's project with work in it, which the recorder must neither record nor touch.</summary>
    private static (FakeKanbanBoard Board, FakeKanbanBoard.Project Theirs, FakeKanbanBoard.Item TheirItem) SharedBoard()
    {
        var board = new FakeKanbanBoard();
        var theirs = board.AddProject("PAYROLL", "https://github.com/acme/payroll.git");
        theirs.Name = "Payroll";
        var item = board.AddItem(theirs, "Rotate the production keys", description: "ask avery@acme.example");
        board.ClaimAsSomeoneElse(item);
        board.AddEpic(theirs, "Q4 roadmap").Documents.Add((Guid.NewGuid(), "Board minutes", "private"));
        return (board, theirs, item);
    }

    private static KanbanFixtureRecorder Recorder(FakeKanbanBoard board) =>
        new(board.Server.Api, new Uri(FakeKanbanBoard.Url), FakeKanbanBoard.FullToken, KanbanFixture.Provisional, KanbanContractTests.Scenarios);

    /// <summary>
    /// The recorder end to end against the in-repo contract-enforcing fake: it records every scenario, its fixtures pass the
    /// guards and replay strictly after a round trip through their files' text, and the board is left as it was found — nothing
    /// of the recorder's on it, nothing of anyone else's recorded or written.
    /// </summary>
    [Fact]
    public async Task The_recorder_records_the_whole_lifecycle_on_a_throwaway_project_and_leaves_nothing_but_what_it_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, theirs, theirItem) = SharedBoard();
        var recorder = Recorder(board);

        var fixtures = await recorder.RecordAsync(ct);

        Assert.Equal(KanbanFixture.Names, fixtures.Keys);
        foreach (var (name, fixture) in fixtures)
        {
            var json = fixture.ToJson();
            Assert.Equal(KanbanFixture.Provisional, fixture.RecordedFrom);
            Assert.Empty(KanbanFixtureGuard.Problems(name, json));
            Assert.DoesNotContain(FakeKanbanBoard.FullToken, json);
            Assert.DoesNotContain("PAYROLL", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(theirs.Id.ToString(), json);
            Assert.DoesNotContain("Rotate", json);
            foreach (var framing in Enum.GetValues<KanbanFraming>())
            {
                await KanbanContractTests.ReplayAsync(name, KanbanFixture.Parse(json), framing);
            }
        }
        // One project, created under an idempotency key with the throwaway name and key, and deleted at the end.
        var created = Assert.Single(board.Calls, c => c.Tool == "create_project");
        Assert.StartsWith(KanbanFixtureGuard.Prefix, created.Arguments["name"]!.GetValue<string>());
        Assert.Matches($"^{KanbanFixtureGuard.KeyPrefix}[0-9A-F]{{8}}$", created.Arguments["key"]!.GetValue<string>());
        Assert.StartsWith("dark-factory:fixture:", created.Arguments["idempotency_key"]!.GetValue<string>());
        Assert.Equal(recorder.Project.ToString("D"), Assert.Single(board.Calls, c => c.Tool == "delete_project").Arguments["projectId"]!.GetValue<string>());
        // Left: exactly what was there before, unwritten.
        Assert.Equal([theirs], board.Projects);
        Assert.Equal([theirItem], board.Items);
        Assert.Single(board.Epics);
        Assert.Equal((1L, "Ready", "their-run"), (theirItem.Version, theirItem.Status, theirItem.Claim!.Value.Marker));
        Assert.DoesNotContain(board.Calls, c => FakeKanbanBoard.WriteTools.Contains(c.Tool) && c.Arguments.ToJsonString().Contains(theirs.Id.ToString()));
        Assert.DoesNotContain(board.Calls, c => c.Arguments.ToJsonString().Contains(theirItem.Id.ToString()));
        Assert.NotEmpty(recorder.Items);
        Assert.Empty(await recorder.LeftBehindAsync(ct));
        // Only the recorder's token was ever sent.
        Assert.All(board.Calls, c => Assert.Equal(FakeKanbanBoard.FullToken, c.Token));
    }

    [Fact]
    public async Task A_scenario_that_fails_still_removes_the_throwaway_project_and_saves_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, theirs, theirItem) = SharedBoard();
        // The board loses every comment's answer for good: the lifecycle scenario fails part-way.
        board.Instead = (tool, _) => tool == "add_work_item_comment" ? board.Lost() : null;

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder(board).RecordAsync(ct));

        Assert.StartsWith("The Kanban fixture recording failed (its throwaway project was removed)", failed.Message);
        Assert.DoesNotContain(FakeKanbanBoard.FullToken, failed.ToString());
        Assert.Single(board.Calls, c => c.Tool == "delete_project");
        Assert.Equal([theirs], board.Projects);
        Assert.Equal([theirItem], board.Items);
    }

    [Fact]
    public async Task A_project_the_board_will_not_delete_is_archived_and_one_it_will_neither_delete_nor_archive_fails_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        // Delete refused: archived instead — an archived throwaway is all that is left, which is allowed.
        var (board, theirs, _) = SharedBoard();
        board.Instead = (tool, _) => tool == "delete_project" ? board.Lost() : null;
        var recorder = Recorder(board);
        await recorder.RecordAsync(ct);
        var left = Assert.Single(board.Projects, p => p != theirs);
        Assert.True(left.Archived);
        Assert.False(theirs.Archived);
        Assert.Empty(await recorder.LeftBehindAsync(ct));

        // The next run sweeps it (and nothing else) before it starts.
        board.Instead = null;
        await Recorder(board).RecordAsync(ct);
        Assert.Equal([theirs], board.Projects);

        // Neither: the run fails naming the project, and the check says so afterwards.
        board.Instead = (tool, _) => tool is "delete_project" or "update_project" ? board.Lost() : null;
        var stuck = Recorder(board);
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => stuck.RecordAsync(ct));
        Assert.Contains("left something on the board", failed.Message);
        Assert.Contains(stuck.Project.ToString("D"), failed.Message);
        board.Instead = null;
        Assert.NotEmpty(await stuck.LeftBehindAsync(ct));
    }

    /// <summary>The sweep takes only what carries BOTH throwaway prefixes: a project that merely looks alike is left alone.</summary>
    [Fact]
    public async Task The_sweep_deletes_only_projects_with_the_throwaway_name_and_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = new FakeKanbanBoard();
        var stale = board.AddProject($"{KanbanFixtureGuard.KeyPrefix}0BADF00D");
        stale.Name = $"{KanbanFixtureGuard.Prefix} contract recording";
        stale.Archived = true;
        board.AddItem(stale, $"{KanbanFixtureGuard.Prefix} left by a run that died");
        var nameOnly = board.AddProject("REAL");
        nameOnly.Name = $"{KanbanFixtureGuard.Prefix} a person's notes";
        var keyOnly = board.AddProject($"{KanbanFixtureGuard.KeyPrefix}ES");
        keyOnly.Name = "Dffixes";

        await Recorder(board).SweepAsync(ct);

        Assert.Equal([nameOnly, keyOnly], board.Projects);
        Assert.Empty(board.Items);
    }
}
