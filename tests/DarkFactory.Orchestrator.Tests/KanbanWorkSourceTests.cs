using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The Kanban work source (sc-25981) against a fake KanbanBoard MCP server that behaves as the board's contract says
/// (<see cref="FakeKanbanBoard"/>): every member, every refusal, paging, and what each write sends.
/// </summary>
public class KanbanWorkSourceTests
{
    internal sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness()
        {
            Project = Board.AddProject("DF", "https://github.com/acme/widgets.git");
        }

        public FakeKanbanBoard Board { get; } = new();
        public FakeKanbanBoard.Project Project { get; }
        public IDbContextFactory<LedgerDbContext> Contexts => new LedgerDbContextFactory(_options);
        public LedgerDbContext Db() => new(_options);
        public KanbanKeys Keys => new(Contexts);

        /// <summary>A fresh source each time, as after a restart: what it knows comes from the ledger and the board.</summary>
        public KanbanWorkSource Source(string token = FakeKanbanBoard.FullToken, string[]? watch = null, int? pageSize = null)
        {
            var (board, scope, web) = (Caller(token), new KanbanScope(watch ?? ["DF"]), new Uri("https://kanban.test/"));
            // No page size given: the source's own default.
            return pageSize is { } size ? new(board, Keys, scope, web) { PageSize = size } : new(board, Keys, scope, web);
        }

        /// <summary>The backoffs the typed caller waited (it does not sleep here), in order.</summary>
        public List<TimeSpan> Waits { get; } = [];

        /// <summary>The typed caller alone, over the fake board.</summary>
        public KanbanBoard Caller(string token = FakeKanbanBoard.FullToken) => new(KanbanWrites.Client(Board.Client(), token), (wait, _) =>
        {
            Waits.Add(wait);
            return Task.CompletedTask;
        });

        public Task<int> Key(FakeKanbanBoard.Item item) => Keys.KeyAsync(item.Id, CancellationToken.None);

        /// <summary>
        /// The ledger's record that this factory claimed the item: its work item with a <c>claimed</c> checkpoint, as Intake leaves
        /// it — or, not <paramref name="claimed"/>, a work item with another checkpoint only (an item parked on a refused claim).
        /// </summary>
        public async Task RecordClaimedAsync(int id, bool claimed = true)
        {
            await using var db = Db();
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync(ItemNaming.Kanban.Source, ItemNaming.Kanban.Format(id), "Count words", "acme/widgets", null,
                CancellationToken.None);
            await ledger.CheckpointAsync(item, claimed ? RunPipeline.Steps.Claimed : RunPipeline.Steps.Parked, null, null, CancellationToken.None);
        }
    }

    private static readonly CancellationToken None = CancellationToken.None;

    // ---- ListReadyAsync ----

    [Fact]
    public async Task Ready_items_are_the_watched_projects_ready_unblocked_unclaimed_ones_read_page_by_page_without_comments()
    {
        var h = new Harness();
        var other = h.Board.AddProject("OTHER");
        var ready = Enumerable.Range(1, 5).Select(n => h.Board.AddItem(h.Project, $"Ready {n}")).ToList();
        h.Board.AddItem(h.Project, "Backlog", status: "Backlog");
        h.Board.AddItem(h.Project, "In progress", status: "InProgress");
        var someones = h.Board.AddItem(h.Project, "Someone's");
        var blocker = h.Board.AddItem(h.Project, "Blocker", status: "InProgress");
        h.Board.AddItem(h.Project, "Blocked").Blockers.Add(blocker);
        h.Board.AddItem(other, "Not watched");
        // A claim that has expired holds nothing: the item is ready again.
        var lapsed = h.Board.AddItem(h.Project, "Lapsed claim");
        h.Board.ClaimAsSomeoneElse(lapsed);
        h.Board.Time.Now = h.Board.Time.Now.AddHours(2);
        h.Board.ClaimAsSomeoneElse(someones);

        var listed = await h.Source(pageSize: 2).ListReadyAsync(None);

        // Each item got its key when it was first listed, in the board's order (oldest first).
        var expected = new List<int>();
        foreach (var item in ready.Append(lapsed))
        {
            expected.Add(await h.Key(item));
        }
        Assert.Equal(expected, listed);
        Assert.Equal([1, 2, 3, 4, 5, 6], listed);
        var pages = h.Board.Calls.Where(c => c.Tool == "list_work_items").ToList();
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { null, "items:2", "items:4" }, pages.Select(p => p.Arguments["cursor"]?.GetValue<string>()));
        // The contract's "polling for ready work", paged, comments left out, and no updated_since.
        Assert.All(pages, p =>
        {
            Assert.Equal(h.Project.Id.ToString(), p.Arguments["projectId"]!.GetValue<string>());
            Assert.Equal(("Ready", true, "unclaimed", 2, false), (p.Arguments["status"]!.GetValue<string>(), p.Arguments["unblocked_only"]!.GetValue<bool>(),
                p.Arguments["claim_state"]!.GetValue<string>(), p.Arguments["limit"]!.GetValue<int>(), p.Arguments["include_comments"]!.GetValue<bool>()));
            Assert.Null(p.Arguments["updated_since"]);
        });
        Assert.Empty(h.Board.Writes);
        // The same items have the same keys on the next listing, from a new source.
        h.Board.Calls.Clear();
        Assert.Equal(listed, await h.Source().ListReadyAsync(None));
        // The default page is the contract's default of 50, not the board's maximum of 200: an MCP answer over 1 MiB is refused.
        Assert.Equal([50], h.Board.Calls.Where(c => c.Tool == "list_work_items").Select(c => c.Arguments["limit"]!.GetValue<int>()));
        Assert.Equal((50, 50, 200), (KanbanWorkSource.DefaultPageSize, KanbanWorkSource.MaxPages, KanbanBoard.MaxPageSize));
    }

    [Fact]
    public async Task An_empty_watch_scope_lists_nothing_and_asks_the_board_nothing()
    {
        var h = new Harness();
        h.Board.AddItem(h.Project, "Ready");

        Assert.Empty(await h.Source(watch: []).ListReadyAsync(None));
        Assert.False(await h.Source(watch: []).InScopeAsync(1, None));
        await h.Source(watch: []).ValidateScopeAsync(None);

        Assert.Empty(h.Board.Calls);
    }

    [Fact]
    public async Task What_the_board_lists_is_checked_again_and_a_listing_that_never_ends_is_refused()
    {
        var h = new Harness();
        var mine = h.Board.AddItem(h.Project, "Ready");
        var claimed = h.Board.AddItem(h.Project, "Claimed after all");
        h.Board.AddItem(h.Project, "Moved after all", status: "InProgress");
        h.Board.ClaimAsSomeoneElse(claimed);
        // A board that lists everything, whatever was asked.
        h.Board.Before = (tool, args) =>
        {
            if (tool == "list_work_items")
            {
                args.Remove("status");
                args.Remove("claim_state");
            }
        };

        Assert.Equal([await h.Key(mine)], await h.Source().ListReadyAsync(None));

        h.Board.Before = null;
        h.Board.Instead = (tool, _) => tool == "list_work_items"
            ? FakeMcpServer.Text(new JsonObject { ["items"] = new JsonArray(), ["next_cursor"] = "again" }.ToJsonString())
            : null;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Source().ListReadyAsync(None));
        Assert.Contains($"more than {KanbanWorkSource.MaxPages} pages", ex.Message);
    }

    [Fact]
    public async Task A_watched_project_the_board_does_not_have_fails_the_listing_and_the_start_up_check_naming_it()
    {
        var h = new Harness();

        await h.Source().ValidateScopeAsync(None);
        var listing = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Source(watch: ["DF", "GONE"]).ListReadyAsync(None));
        var check = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Source(watch: ["GONE"]).ValidateScopeAsync(None));
        // An archived project is not listed by the board, so it is not watched either.
        h.Project.Archived = true;
        var archived = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Source().ValidateScopeAsync(None));

        Assert.All(new[] { listing, check }, e => Assert.Equal("Kanban:Watch:Projects: KanbanBoard has no project with the key 'GONE' (or it is archived).", e.Message));
        Assert.Contains("'DF'", archived.Message);
        // A token the board does not know fails the check too (HTTP 401): the board is not reachable with it.
        await Assert.ThrowsAsync<Mcp.McpHttpException>(() => h.Source(token: "kb_unknown").ValidateScopeAsync(None));
    }

    // ---- ClaimAsync ----

    [Fact]
    public async Task Claiming_a_ready_item_claims_it_under_a_marker_from_its_board_id_reads_the_claim_back_and_moves_it_to_in_progress()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);

        var claim = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Ok, claim);
        Assert.Equal(["get_work_item", "list_projects", "claim_work_item", "get_work_item", "update_work_item"], h.Board.Calls.Select(c => c.Tool));
        var marker = $"dark-factory:{item.Id}";
        Assert.Equal(marker, KanbanWorkSource.Marker(item.Id));
        // Exactly the contract's argument names.
        Assert.Equal(new JsonObject { ["id"] = item.Id.ToString(), ["marker"] = marker, ["ttl_seconds"] = 86400 }.ToJsonString(),
            h.Board.Calls[2].Arguments.ToJsonString());
        Assert.Equal(new JsonObject { ["itemId"] = item.Id.ToString(), ["status"] = "InProgress", ["expected_version"] = 1 }.ToJsonString(),
            h.Board.Calls[4].Arguments.ToJsonString());
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
        Assert.Equal(("factory", marker), (item.Claim!.Value.Name, item.Claim.Value.Marker));
        Assert.Equal(h.Board.Time.Now.AddDays(1), item.Claim.Value.Expires);

        // Idempotent: claiming it again (a run that died before its checkpoint) keeps the claim and moves nothing.
        h.Board.Calls.Clear();
        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(id, ignoreScope: false, None));
        Assert.Equal(["claim_work_item"], h.Board.Writes);
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_run_that_died_between_the_claim_and_the_move_finishes_the_claim()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        await new KanbanBoard(KanbanWrites.Client(h.Board.Client(), FakeKanbanBoard.FullToken))
            .ClaimAsync(item.Id, KanbanWorkSource.Marker(item.Id), 60, None);

        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(id, ignoreScope: false, None));

        Assert.Equal("InProgress", item.Status);
    }

    [Fact]
    public async Task A_lapsed_claim_is_taken_again_only_on_an_item_the_ledger_records_as_claimed_by_this_factory()
    {
        var h = new Harness();
        var (ours, unrecorded, theirs) = (h.Board.AddItem(h.Project, "Ours"), h.Board.AddItem(h.Project, "Unrecorded"), h.Board.AddItem(h.Project, "Theirs"));
        var (o, u, t) = (await h.Key(ours), await h.Key(unrecorded), await h.Key(theirs));
        foreach (var id in new[] { o, u, t })
        {
            Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(id, ignoreScope: false, None));
        }
        // Intake checkpointed the claim of two of them; the run of the third died before its checkpoint.
        await h.RecordClaimedAsync(o);
        await h.RecordClaimedAsync(t);
        // A fourth the ledger knows, but not as claimed (only a `claimed` checkpoint counts): someone moved it to InProgress.
        var parked = h.Board.AddItem(h.Project, "Parked", status: "InProgress");
        var p = await h.Key(parked);
        await h.RecordClaimedAsync(p, claimed: false);
        // More than a day later every claim has lapsed (nothing renews one), and someone else has claimed the third item.
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(theirs);
        var versions = (ours.Version, unrecorded.Version, theirs.Version);
        h.Board.Calls.Clear();

        // In progress, no claim, and the ledger does not say it is the factory's: refused with what to do, nothing written.
        Assert.Equal(ClaimResult.Refused("the item is InProgress with no claim, and the ledger records no claim of it by this factory (a claim lasts 24 hours and is renewed only while the item is in flight, so the factory's claim may have lapsed): move it back to Ready to hand it to the factory again"),
            await h.Source().ClaimAsync(u, ignoreScope: false, None));
        Assert.Equal(ClaimResult.Refused(KanbanWorkSource.LapsedRefusal), await h.Source().ClaimAsync(p, ignoreScope: false, None));
        Assert.Null(parked.Claim);
        // Claimed under another marker: always refused, whatever the ledger says.
        Assert.Equal(ClaimResult.Refused("the item is claimed by `someone`"), await h.Source().ClaimAsync(t, ignoreScope: false, None));
        Assert.Empty(h.Board.Writes);
        Assert.Equal("their-run", theirs.Claim!.Value.Marker);
        Assert.Equal(h.Board.Time.Now.AddHours(-1), unrecorded.Claim!.Value.Expires); // the lapsed claim, untouched

        // In progress, no claim, and the ledger records the factory's claim: claimed again, read back, and not moved.
        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(o, ignoreScope: false, None));
        Assert.Equal(["claim_work_item"], h.Board.Writes);
        Assert.Equal("get_work_item", h.Board.Calls[^1].Tool); // the claim read back
        Assert.Equal(("factory", KanbanWorkSource.Marker(ours.Id), h.Board.Time.Now.AddDays(1)),
            (ours.Claim!.Value.Name, ours.Claim.Value.Marker, ours.Claim.Value.Expires));
        Assert.Equal(("InProgress", "InProgress", "InProgress"), (ours.Status, unrecorded.Status, theirs.Status));
        Assert.Equal(versions, (ours.Version, unrecorded.Version, theirs.Version));
        Assert.Equal("the item is InProgress with no claim, and the ledger records no claim of it by this factory (a claim lasts 24 hours and is renewed only while the item is in flight, so the factory's claim may have lapsed): move it back to Ready to hand it to the factory again", KanbanWorkSource.LapsedRefusal);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "claimed", "the item is claimed by `someone`" },
        { "backlog", "the item is Backlog, not Ready" },
        // In progress with no claim, and nothing in the ledger says the factory ever claimed it.
        { "in-progress", "the item is InProgress with no claim, and the ledger records no claim of it by this factory (a claim lasts 24 hours and is renewed only while the item is in flight, so the factory's claim may have lapsed): move it back to Ready to hand it to the factory again" },
        { "done", "the item is Done, not Ready" },
        { "blocked", "the item is blocked by \"Schema first\" (InProgress)" },
        { "closed-blocker", "the item is blocked by \"Schema first\" (Closed)" },
        { "unwatched", "the item's project is outside the watched projects (Kanban:Watch:Projects)" },
        { "deleted", "the item is no longer on the board" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task An_item_that_is_not_the_factorys_to_take_is_refused_with_the_reason_and_nothing_is_written(string what, string reason)
    {
        var h = new Harness();
        var item = h.Board.AddItem(what == "unwatched" ? h.Board.AddProject("OTHER") : h.Project, "Count words");
        var id = await h.Key(item);
        switch (what)
        {
            case "claimed": h.Board.ClaimAsSomeoneElse(item); break;
            case "backlog": item.Status = "Backlog"; break;
            case "in-progress": item.Status = "InProgress"; break;
            case "done": item.Status = "Done"; break;
            case "blocked": item.Blockers.Add(h.Board.AddItem(h.Project, "Schema first", status: "InProgress")); break;
            // A Closed blocker still blocks: only Done finishes one.
            case "closed-blocker": item.Blockers.Add(h.Board.AddItem(h.Project, "Schema first", status: "Closed")); break;
            case "deleted": h.Board.Items.Remove(item); break;
        }
        var (status, version, claim) = (item.Status, item.Version, item.Claim);

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Refused(reason), result);
        Assert.Empty(h.Board.Writes);
        Assert.Equal((status, version, claim), (item.Status, item.Version, item.Claim));
    }

    [Fact]
    public async Task A_key_the_ledger_never_gave_out_is_refused_without_asking_the_board()
    {
        var h = new Harness();

        Assert.Equal(ClaimResult.Refused("kb-9 is no Kanban item the factory knows"), await h.Source().ClaimAsync(9, ignoreScope: false, None));
        Assert.Empty(h.Board.Calls);
    }

    [Fact]
    public async Task Ignoring_the_scope_claims_an_item_of_an_unwatched_project_and_the_other_checks_still_apply()
    {
        var h = new Harness();
        var other = h.Board.AddProject("OTHER");
        var item = h.Board.AddItem(other, "Elsewhere");
        var held = h.Board.AddItem(other, "Held");
        h.Board.ClaimAsSomeoneElse(held);

        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(await h.Key(item), ignoreScope: true, None));
        Assert.Equal(ClaimResult.Refused("the item is claimed by `someone`"), await h.Source().ClaimAsync(await h.Key(held), ignoreScope: true, None));
        Assert.Equal("InProgress", item.Status);
    }

    [Fact]
    public async Task Another_caller_winning_the_race_for_the_claim_is_a_refusal_and_the_item_stays_theirs()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        // Read as unclaimed; claimed by someone else before the factory's claim arrives.
        h.Board.Before = (tool, _) =>
        {
            if (tool == "claim_work_item")
            {
                h.Board.ClaimAsSomeoneElse(item);
            }
        };

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        // The refusal names the claimant: the item is read once more for that, and nothing else is written.
        Assert.Equal(ClaimResult.Refused("the item is claimed by `someone`"), result);
        Assert.Equal(["claim_work_item"], h.Board.Writes); // the one call the board refused: a refused call changes nothing
        Assert.Equal(("Ready", 1L, "someone"), (item.Status, item.Version, item.Claim!.Value.Name));
    }

    [Fact]
    public async Task The_same_marker_held_by_another_identity_is_another_callers_claim()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "InProgress");
        var id = await h.Key(item);
        h.Board.ClaimAsSomeoneElse(item, KanbanWorkSource.Marker(item.Id));

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Refused("the item is claimed by `someone`"), result);
        Assert.Equal("someone", item.Claim!.Value.Name);
    }

    [Fact]
    public async Task A_claim_that_does_not_read_back_is_refused_and_the_item_is_not_moved()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        // The claim is accepted, and gone when the item is read again.
        h.Board.Before = (tool, _) =>
        {
            if (tool == "get_work_item" && h.Board.Writes.Contains("claim_work_item"))
            {
                item.Claim = null;
            }
        };

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Refused("the claim did not stick (read back claimant none, marker none)"), result);
        Assert.Equal(["claim_work_item"], h.Board.Writes);
        Assert.Equal(("Ready", 1L), (item.Status, item.Version));
    }

    [Fact]
    public async Task An_item_moved_away_while_it_was_being_claimed_is_refused_and_the_claim_is_given_back()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        h.Board.Before = (tool, _) =>
        {
            if (tool == "claim_work_item")
            {
                (item.Status, item.Version) = ("Done", item.Version + 1);
            }
        };

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Refused("the item was moved to Done while it was being claimed"), result);
        Assert.Equal(["claim_work_item", "release_work_item_claim"], h.Board.Writes);
        Assert.Equal(("Done", (string?)null), (item.Status, item.Claim?.Name));
    }

    [Fact]
    public async Task A_read_scope_token_cannot_make_any_of_the_adapters_writes()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        var source = h.Source(token: FakeKanbanBoard.ReadToken);

        var refused = new[]
        {
            await Assert.ThrowsAsync<KanbanException>(() => source.ClaimAsync(id, ignoreScope: false, None)),
            await Assert.ThrowsAsync<KanbanException>(() => source.ReportStateAsync(id, BoardState.Claimed, null, None)),
            await Assert.ThrowsAsync<KanbanException>(() => source.CommentAsync(id, "hello", None)),
            await Assert.ThrowsAsync<KanbanException>(() => source.LinkAsync(id, ["https://github.com/acme/widgets/pull/1"], None)),
        };

        Assert.All(refused, e => Assert.Equal(KanbanException.ScopeDenied, e.Code));
        Assert.Equal(["claim_work_item", "update_work_item", "add_work_item_comment", "add_work_item_link"], h.Board.Writes);
        Assert.Equal(("Ready", 1L, 0, 0), (item.Status, item.Version, item.Comments.Count, item.Links.Count));
        Assert.Null(item.Claim);
        // It can read: the scope check and the spec need no write.
        Assert.True(await source.InScopeAsync(id, None));
    }

    // ---- InScopeAsync, ReleaseAsync ----

    [Fact]
    public async Task An_item_is_in_scope_while_it_is_on_the_board_in_a_watched_project()
    {
        var h = new Harness();
        var watched = h.Board.AddItem(h.Project, "Watched");
        var elsewhere = h.Board.AddItem(h.Board.AddProject("OTHER"), "Elsewhere");
        var gone = h.Board.AddItem(h.Project, "Gone");
        var (w, e, g) = (await h.Key(watched), await h.Key(elsewhere), await h.Key(gone));
        h.Board.Items.Remove(gone);

        Assert.True(await h.Source().InScopeAsync(w, None));
        Assert.False(await h.Source().InScopeAsync(e, None));
        Assert.False(await h.Source().InScopeAsync(g, None));
        Assert.False(await h.Source().InScopeAsync(99, None));
        // Project keys are matched whatever their case; the setting stores them upper-cased.
        Assert.True(await h.Source(watch: ["other"]).InScopeAsync(e, None));
        Assert.Empty(h.Board.Writes);
    }

    [Fact]
    public async Task Release_gives_back_the_factorys_claim_and_leaves_anyone_elses()
    {
        var h = new Harness();
        var mine = h.Board.AddItem(h.Project, "Mine");
        var theirs = h.Board.AddItem(h.Project, "Theirs");
        var (m, t) = (await h.Key(mine), await h.Key(theirs));
        await h.Source().ClaimAsync(m, ignoreScope: false, None);
        h.Board.ClaimAsSomeoneElse(theirs);
        h.Board.Calls.Clear();

        await h.Source().ReleaseAsync(m, None);
        await h.Source().ReleaseAsync(m, None); // idempotent: nothing left to release
        await h.Source().ReleaseAsync(t, None);

        Assert.Equal(["release_work_item_claim"], h.Board.Writes);
        Assert.Equal(new JsonObject { ["id"] = mine.Id.ToString(), ["marker"] = KanbanWorkSource.Marker(mine.Id) }.ToJsonString(),
            h.Board.Calls.Single(c => c.Tool == "release_work_item_claim").Arguments.ToJsonString());
        Assert.Null(mine.Claim);
        Assert.Equal("someone", theirs.Claim!.Value.Name);
    }

    // ---- ReadSpecAsync ----

    [Fact]
    public async Task The_spec_is_the_item_its_epic_and_the_epics_documents_with_links_built_on_the_web_url()
    {
        var h = new Harness();
        var epic = h.Board.AddEpic(h.Project, "Word counting", "Counts words.");
        var doc = Guid.NewGuid();
        epic.Documents.Add((doc, "Requirements", "E1: whitespace is no word."));
        var item = h.Board.AddItem(h.Project, "Count words", description: "WordCount(\"  \") returns 1.", epic: epic);
        item.Estimate = 2;
        item.Labels.AddRange(["simple", "parser"]);
        item.Type = "Issue";
        var id = await h.Key(item);

        var spec = await h.Source().ReadSpecAsync(id, None);

        var board = $"https://kanban.test/?project={h.Project.Id}&epic={epic.Id}";
        Assert.Equal(new WorkStory(id, "Count words", "Repo: acme/widgets\n\nWordCount(\"  \") returns 1.", "bug", $"{board}&item={item.Id}", ItemNaming.Kanban,
            Labels: spec.Story.Labels, Estimate: 2, BoardId: item.Id.ToString("D")), spec.Story);
        Assert.Equal(["simple", "parser"], spec.Story.Labels);
        Assert.Equal($"kb-{id}", spec.Story.Ref);
        // Written on the owner's board: trusted like a Shortcut story (E5), so its name and text are not fenced or made inert.
        Assert.False(spec.Story.UntrustedName);
        Assert.Equal("Count words", spec.Story.PublicName);
        // A Kanban epic has no number an epic-scope control addresses.
        Assert.Equal(new WorkEpic(null, "Word counting", "Counts words.", board), spec.Epic);
        Assert.Equal([new WorkDocument("Requirements", "E1: whitespace is no word.", $"{board}&document={doc}")], spec.Documents);
        Assert.Empty(h.Board.Writes);
        Assert.Equal(["get_work_item", "list_projects", "get_epic", "list_epic_documents"], h.Board.Calls.Select(c => c.Tool));
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "acme/widgets")]
    [InlineData("https://github.com/acme/widgets", "acme/widgets")]
    [InlineData("ssh://git@github.com/Acme/Widgets.git", "Acme/Widgets")]
    [InlineData("git@github.com:acme/widgets.git", "acme/widgets")]
    public async Task An_items_repository_is_its_projects_repository_url_in_its_https_and_ssh_forms(string url, string repo)
    {
        var h = new Harness();
        h.Project.RepositoryUrl = url;
        var item = h.Board.AddItem(h.Project, "No description");
        var described = h.Board.AddItem(h.Project, "Described", description: "Do the thing.");
        var fallback = new RepoRef("michaeltrefry", "dark-factory-sandbox");

        var bare = (await h.Source().ReadSpecAsync(await h.Key(item), None)).Story;
        var withText = (await h.Source().ReadSpecAsync(await h.Key(described), None)).Story;

        // The pipeline resolves every item's repository from its description's Repo: line.
        Assert.Equal($"Repo: {repo}", bare.Description);
        Assert.Equal($"Repo: {repo}\n\nDo the thing.", withText.Description);
        Assert.Equal(RepoRef.Parse(repo), RepoResolver.Resolve(bare.Description, fallback));
        Assert.Equal(RepoRef.Parse(repo), RepoResolver.Resolve(withText.Description, fallback));
    }

    [Fact]
    public async Task A_repo_line_in_the_item_overrides_its_projects_repository()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Elsewhere", description: "Fix the gadget.\n\nRepo: acme/gadgets");
        // With its own Repo: line an item needs nothing of its project: not a repository_url the factory can read, nor the project itself.
        h.Project.RepositoryUrl = "https://gitlab.example/acme/widgets.git";

        var story = (await h.Source().ReadSpecAsync(await h.Key(item), None)).Story;

        Assert.Equal("Fix the gadget.\n\nRepo: acme/gadgets", story.Description);
        Assert.Equal(new RepoRef("acme", "gadgets"), RepoResolver.Resolve(story.Description, new RepoRef("o", "default")));
        Assert.DoesNotContain("list_projects", h.Board.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task A_project_without_a_repository_leaves_the_item_to_the_default_repository()
    {
        var h = new Harness();
        h.Project.RepositoryUrl = null;
        var item = h.Board.AddItem(h.Project, "Anywhere", description: "Do the thing.");

        var story = (await h.Source().ReadSpecAsync(await h.Key(item), None)).Story;

        Assert.Equal("Do the thing.", story.Description);
        Assert.Equal(new RepoRef("o", "default"), RepoResolver.Resolve(story.Description, new RepoRef("o", "default")));
    }

    [Theory]
    [InlineData("https://gitlab.example/acme/widgets.git")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/widgets/tree/main")]
    [InlineData("https://github.com.evil.example/acme/widgets.git")]
    [InlineData("git@github.com:acme/../widgets.git")]
    public async Task A_repository_url_that_names_no_github_repository_is_refused_rather_than_worked_somewhere_else(string url)
    {
        var h = new Harness();
        h.Project.RepositoryUrl = url;
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);

        var ex = await Assert.ThrowsAsync<KanbanItemException>(() => h.Source().ReadSpecAsync(id, None));

        Assert.Equal($"kb-{id}: the repository_url of its Kanban project DF names no github.com repository ('{url}'); set it to one, or add a "
            + "`Repo: owner/name` line to the item.", ex.Message);
    }

    [Fact]
    public async Task An_item_of_an_archived_project_has_no_known_repository_and_an_unknown_key_no_spec()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        h.Project.Archived = true;

        var archived = await Assert.ThrowsAsync<KanbanItemException>(() => h.Source().ReadSpecAsync(id, None));
        var unknown = await Assert.ThrowsAsync<KanbanItemException>(() => h.Source().ReadSpecAsync(42, None));

        Assert.Contains("its Kanban project is archived or gone", archived.Message);
        // The remedy is in the message: `factory run` prints it (KanbanPipelineTests).
        Assert.Equal("kb-42 is no Kanban item the factory knows: the ledger never gave out that key. Run `factory kanban key <item GUID>` first; it prints the item's kb-<n>.", unknown.Message);
    }

    [Theory]
    [InlineData("Story", "feature")]
    [InlineData("Issue", "bug")]
    [InlineData("Task", "chore")]
    public async Task The_boards_item_types_are_the_other_sources_story_types(string type, string storyType)
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        item.Type = type;

        var spec = await h.Source().ReadSpecAsync(await h.Key(item), None);

        Assert.Equal(storyType, spec.Story.StoryType);
        Assert.Null(spec.Epic);
        Assert.Empty(spec.Documents);
        Assert.Null(spec.Story.Estimate);
    }

    // ---- ReportStateAsync ----

    [Fact]
    public async Task Reported_states_move_the_item_on_its_version_and_write_nothing_when_it_is_already_there()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        await h.Source().ClaimAsync(id, ignoreScope: false, None);
        h.Board.Calls.Clear();

        // The claim already moved it: reporting Claimed writes nothing.
        await h.Source().ReportStateAsync(id, BoardState.Claimed, null, None);
        Assert.Empty(h.Board.Writes);

        await h.Source().ReportStateAsync(id, BoardState.Merged, null, None);
        await h.Source().ReportStateAsync(id, BoardState.Merged, null, None);

        Assert.Equal(["update_work_item"], h.Board.Writes);
        Assert.Equal(new JsonObject { ["itemId"] = item.Id.ToString(), ["status"] = "Done", ["expected_version"] = 2 }.ToJsonString(),
            h.Board.Calls.Single(c => c.Tool == "update_work_item").Arguments.ToJsonString());
        Assert.Equal(("Done", 3L), (item.Status, item.Version));
    }

    [Fact]
    public async Task Stopping_moves_the_item_to_the_backlog_releases_the_claim_and_comments_once_however_often_it_is_reported()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        await h.Source().ClaimAsync(id, ignoreScope: false, None);
        h.Board.Calls.Clear();

        await h.Source().ReportStateAsync(id, BoardState.Stopped, "kb-1 was stopped by michael.", None);

        Assert.Equal(["update_work_item", "release_work_item_claim", "add_work_item_comment"], h.Board.Writes);
        Assert.Equal(("Backlog", (string?)null), (item.Status, item.Claim?.Name));
        Assert.Equal("[author: dark-factory] kb-1 was stopped by michael.", Assert.Single(item.Comments).Body);

        // A stop that is reported again (the run died before its checkpoint) moves nothing and adds no second comment.
        h.Board.Calls.Clear();
        await h.Source().ReportStateAsync(id, BoardState.Stopped, "kb-1 was stopped by michael.", None);
        Assert.Equal(["add_work_item_comment"], h.Board.Writes); // the same idempotency key: the board adds nothing
        Assert.Single(item.Comments);
        Assert.Equal(3L, item.Version);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Source().ReportStateAsync(id, BoardState.Stopped, " ", None));
    }

    [Fact]
    public async Task Stopping_an_item_someone_else_claimed_since_leaves_their_claim()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "InProgress");
        var id = await h.Key(item);
        h.Board.ClaimAsSomeoneElse(item);

        await h.Source().ReportStateAsync(id, BoardState.Stopped, "stopped", None);

        Assert.Equal(["update_work_item", "add_work_item_comment"], h.Board.Writes);
        Assert.Equal(("Backlog", "someone"), (item.Status, item.Claim!.Value.Name));
    }

    // ---- CommentAsync, LinkAsync ----

    [Fact]
    public async Task A_comment_is_attributed_and_carries_an_idempotency_key_from_the_items_board_id_version_and_text()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var other = h.Board.AddItem(h.Project, "Another");
        var (id, otherId) = (await h.Key(item), await h.Key(other));

        await h.Source().CommentAsync(id, "kb-1 escalated; a human needs to look.", None);
        await h.Source().CommentAsync(id, "kb-1 escalated; a human needs to look.", None); // a retry of the same comment
        await h.Source().CommentAsync(id, "Something else.", None);
        await h.Source().CommentAsync(otherId, "kb-1 escalated; a human needs to look.", None);
        // Intended, and a limit: the item's version moves only on an update or a move, so the same words said again later at the
        // same version — not a retry — carry the same key and are dropped by the board too (the call still succeeds).
        await h.Source().CommentAsync(id, "kb-1 escalated; a human needs to look.", None);
        Assert.Equal((1L, 2), (item.Version, item.Comments.Count));
        Assert.Equal(3, h.Board.Calls.Count(c => c.Tool == "add_work_item_comment"
            && c.Arguments["idempotency_key"]!.GetValue<string>() == KanbanWorkSource.CommentKey(item.Id, 1, "[author: dark-factory] kb-1 escalated; a human needs to look.")));

        Assert.Equal(["[author: dark-factory] kb-1 escalated; a human needs to look.", "[author: dark-factory] Something else."],
            item.Comments.Select(c => c.Body));
        Assert.Single(other.Comments);
        var sent = h.Board.Calls.Where(c => c.Tool == "add_work_item_comment").ToList();
        var keys = sent.Select(c => c.Arguments["idempotency_key"]!.GetValue<string>()).ToList();
        Assert.Equal(["itemId", "body", "idempotency_key"], sent[0].Arguments.Select(a => a.Key));
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(3, keys.Distinct().Count());
        // The contract's key: 1 to 128 printable ASCII characters without spaces. Nothing of the ledger's key (kb-1) is in it.
        Assert.All(keys, k => Assert.Matches("^dark-factory:comment:[0-9a-f]{64}$", k));
        Assert.Equal(keys[0], KanbanWorkSource.CommentKey(item.Id, 1, "[author: dark-factory] kb-1 escalated; a human needs to look."));

        // The same words after the item was moved are a new comment.
        await h.Source().ReportStateAsync(id, BoardState.Merged, null, None);
        await h.Source().CommentAsync(id, "Something else.", None);
        Assert.Equal(3, item.Comments.Count);
    }

    [Fact]
    public async Task Links_are_attached_as_a_pull_request_and_a_branch_once_each_however_often_they_are_added()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        string[] urls = ["https://github.com/acme/widgets/pull/12", "https://github.com/acme/widgets/tree/factory/kb-1"];

        await h.Source().LinkAsync(id, urls, None);
        await h.Source().LinkAsync(id, urls, None);

        Assert.Equal(new[] { ("pr", urls[0]), ("branch", urls[1]) }, item.Links);
        Assert.Equal(new JsonObject { ["id"] = item.Id.ToString(), ["kind"] = "pr", ["url"] = urls[0] }.ToJsonString(),
            h.Board.Calls.First(c => c.Tool == "add_work_item_link").Arguments.ToJsonString());
        Assert.Equal(1L, item.Version); // a link is no update: the version the claim's move depends on is untouched
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets/pull/12", "pr")]
    [InlineData("https://github.com/acme/widgets/pull/12/files", "pr")]
    [InlineData("https://github.com/acme/widgets/tree/factory/kb-1", "branch")]
    [InlineData("https://github.com/acme/widgets", "other")]
    [InlineData("https://github.com/acme/widgets/pull/new", "other")]
    [InlineData("https://github.com/acme/widgets/issues/3", "other")]
    [InlineData("not a url", "other")]
    public void A_links_kind_comes_from_its_url(string url, string kind) => Assert.Equal(kind, KanbanWorkSource.LinkKind(url));

    // ---- keys ----

    [Fact]
    public async Task An_item_gets_a_key_only_once_it_is_read_from_the_board_and_keeps_it()
    {
        var h = new Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "Backlog");

        var key = await h.Source().KeyOfAsync(item.Id, None);
        var again = await h.Source().KeyOfAsync(item.Id, None);
        var unknown = await Assert.ThrowsAsync<KanbanException>(() => h.Source().KeyOfAsync(Guid.NewGuid(), None));

        Assert.Equal((1, 1), (key, again));
        Assert.Equal(KanbanException.NotFound, unknown.Code);
        await using var db = h.Db();
        Assert.Equal(item.Id, (await db.KanbanItems.SingleAsync()).ItemId);
        Assert.Empty(h.Board.Writes);
    }

    [Fact]
    public async Task An_id_the_board_does_not_know_gets_no_key_and_nothing_is_written_to_the_board()
    {
        var h = new Harness();
        h.Board.AddItem(h.Project, "Count words");

        var unknown = await Assert.ThrowsAsync<KanbanException>(() => h.Source().KeyOfAsync(Guid.NewGuid(), None));

        Assert.Equal((KanbanException.NotFound, "get_work_item"), (unknown.Code, unknown.Tool));
        Assert.Equal(["get_work_item"], h.Board.Calls.Select(c => c.Tool));
        Assert.Empty(h.Board.Writes);
        await using var db = h.Db();
        Assert.Empty(await db.KanbanItems.ToListAsync()); // no key row: the ledger never heard of the id
    }

    /// <summary>A ledger whose every save fails, for a reason that is no lost race.</summary>
    private sealed class FailingSaves : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("the ledger's disk is full");
    }

    [Fact]
    public async Task A_key_that_could_not_be_saved_for_another_reason_than_a_lost_race_fails_with_that_reason()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new FailingSaves()).Options;
        var keys = new KanbanKeys(new LedgerDbContextFactory(options));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => keys.KeyAsync(Guid.NewGuid(), None));

        Assert.Equal("the ledger's disk is full", ex.Message); // not "Sequence contains no elements"
        await using var db = new LedgerDbContext(options);
        Assert.Empty(await db.KanbanItems.ToListAsync());
    }

    [Fact]
    public void Factory_kanban_key_reports_a_board_http_or_ledger_failure_by_its_message_and_leaves_a_requested_cancel_alone()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Exception[] reported =
        [
            new KanbanException("get_work_item", KanbanException.NotFound, "no such item"), new InvalidOperationException("Kanban:WebUrl ..."),
            new HttpRequestException("connection refused"), new TaskCanceledException("the request timed out"),
            new Npgsql.NpgsqlException("the ledger is down"), new DbUpdateException("could not save"),
        ];

        Assert.All(reported, ex => Assert.True(FactoryRunner.IsKanbanKeyFailure(ex, None), ex.GetType().Name));
        Assert.IsAssignableFrom<System.Data.Common.DbException>(reported[4]);
        // Ctrl-C is no failure to report, and neither is anything unexpected (a bug keeps its stack trace).
        Assert.False(FactoryRunner.IsKanbanKeyFailure(new TaskCanceledException(), cancelled.Token));
        Assert.False(FactoryRunner.IsKanbanKeyFailure(new OperationCanceledException(), cancelled.Token));
        Assert.False(FactoryRunner.IsKanbanKeyFailure(new OperationCanceledException(), None));
        Assert.False(FactoryRunner.IsKanbanKeyFailure(new NullReferenceException(), None));
    }

    [Fact]
    public void The_kanban_source_names_its_items_kb_is_trusted_and_plans()
    {
        IWorkSource source = new Harness().Source();

        Assert.Equal(ItemNaming.Kanban, source.Naming);
        Assert.Equal(("kanban", "kb", "Kanban item"), (source.Naming.Source, source.Naming.Prefix, source.Naming.Noun));
        // The owner's board (E5): its items are trusted, so a `plan` label on one is the owner's and makes it a plan item (sc-25984).
        Assert.True(source.Naming.Trusted);
        Assert.True(source.PlansItems);
        Assert.Equal(("Closed", "move it back to Backlog"), source.Shelved);
        Assert.Null(source.MaxClaimRefusals); // a refused item is listed again only once the board shows it ready again
        Assert.Equal("Add its project back to Kanban:Watch:Projects and move it to Ready", source.ScopeReturnHint);
    }

    [Fact]
    public void A_kanban_item_is_named_kb_everywhere_a_name_is_shared()
    {
        Assert.Equal(new ItemRef(ItemNaming.Kanban, 3), ItemNaming.ParseAny("kb-3"));
        Assert.Equal(new ItemRef(ItemNaming.Kanban, 3), ItemNaming.ParseAny("KB-3"));
        Assert.Equal(new ItemRef(ItemNaming.Shortcut, 3), ItemNaming.ParseAny("3")); // a bare number is still a Shortcut story
        Assert.False(ItemNaming.Kanban.TryParse("3", out _));
        Assert.False(ItemNaming.Kanban.TryParse("kb-0", out _));
        Assert.Equal("factory/kb-3", ItemNaming.Kanban.BranchName(3));
        Assert.Equal(new ItemRef(ItemNaming.Kanban, 3), ItemNaming.OfWorktree("factory-kb-3"));
        Assert.Equal(new ItemRef(ItemNaming.Kanban, 3), Controls.ControlScope.ItemOf("item:kb-3"));
        Assert.Equal("item:kb-3", Controls.ControlScope.Item("kb-3"));
        Assert.Contains(ItemNaming.Kanban, ItemNaming.All);
    }
}
