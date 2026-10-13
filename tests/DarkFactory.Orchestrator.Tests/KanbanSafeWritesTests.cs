using System.Net;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Safe Kanban writes (sc-25983, E3), against the fake KanbanBoard MCP server (<see cref="FakeKanbanBoard"/>): every error code
/// of the contract as a typed refusal, <c>busy</c> retried with the same call and a bound, the read before a write is repeated
/// after a lost answer, a <c>conflict</c> on a move decided again and never written over, and claims renewed across polls — and
/// an item whose claim another caller took paused with a reason that names them.
/// </summary>
public class KanbanSafeWritesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private const string Marker = "dark-factory:test";
    private const string Pr = "https://github.com/acme/widgets/pull/1";

    /// <summary>Every write the typed caller sends, by its tool, on <paramref name="item"/> (a create: in its project).</summary>
    private static Dictionary<string, Func<KanbanBoard, Task<KanbanWorkItem>>> Writes(FakeKanbanBoard.Item item) => new()
    {
        ["claim_work_item"] = b => b.ClaimAsync(item.Id, Marker, 60, None),
        ["renew_work_item_claim"] = b => b.RenewClaimAsync(item.Id, Marker, 60, None),
        ["release_work_item_claim"] = b => b.ReleaseClaimAsync(item.Id, Marker, None),
        ["update_work_item"] = b => b.SetStatusAsync(item.Id, "InProgress", item.Version, None),
        ["add_work_item_comment"] = b => b.CommentAsync(item.Id, "hello", "key-1", None),
        ["add_work_item_link"] = b => b.LinkAsync(item.Id, "pr", Pr, None),
        ["create_work_item"] = b => b.CreateItemAsync(item.ProjectId, "A child", "Task", "what to do", null, "child-key-1", None),
    };

    private static List<string> Tools(FakeKanbanBoard board) => board.Calls.Select(c => c.Tool).ToList();

    private static JsonObject PlainError()
    {
        var result = FakeMcpServer.Text("An error occurred invoking the tool.");
        result["isError"] = true;
        return result;
    }

    // ---- Typed refusals: every code of the contract ----

    [Fact]
    public void The_callers_codes_are_the_contracts_seven_and_its_writes_cover_every_write_it_sends()
    {
        Assert.Equal(["scope_denied", "not_found", "conflict", "already_claimed", "cycle", "invalid_argument", "busy"], KanbanException.Codes);
        var h = new KanbanWorkSourceTests.Harness();
        Assert.Equal(KanbanBoard.WriteTools.Order(), Writes(h.Board.AddItem(h.Project, "x")).Keys.Order());
        Assert.Equal((4, 1, 3), (KanbanBoard.MaxBusyRetries, KanbanBoard.MaxRepeatsAfterLostAnswer, KanbanWorkSource.MaxConflictRereads));
    }

    [Theory]
    [InlineData("scope_denied")]
    [InlineData("not_found")]
    [InlineData("conflict")]
    [InlineData("already_claimed")]
    [InlineData("cycle")]
    [InlineData("invalid_argument")]
    public async Task A_refusal_with_a_code_is_thrown_with_that_code_and_the_call_is_neither_sent_again_nor_followed_by_a_read(string code)
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        foreach (var (tool, write) in Writes(item))
        {
            h.Board.Calls.Clear();
            h.Board.Instead = (t, _) => t == tool ? FakeKanbanBoard.Refusal(code, $"refused: {code}") : null;

            var ex = await Assert.ThrowsAsync<KanbanException>(() => write(h.Caller()));

            Assert.Equal((code, tool, true), (ex.Code, ex.Tool, ex.Refused));
            Assert.Contains($"refused: {code}", ex.Message);
            // A refused call changed nothing: it is the answer. Nothing is sent again, and nothing is read.
            Assert.Equal([tool], Tools(h.Board));
        }
        Assert.Empty(h.Waits);
        Assert.Equal(("Ready", 1L, 0, 0), (item.Status, item.Version, item.Comments.Count, item.Links.Count));
    }

    [Fact]
    public async Task A_read_refused_with_a_code_is_typed_the_same_way()
    {
        var h = new KanbanWorkSourceTests.Harness();

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Caller().GetItemAsync(Guid.NewGuid(), None));

        Assert.Equal((KanbanException.NotFound, "get_work_item", true), (ex.Code, ex.Tool, ex.Refused));
    }

    // ---- busy: the same call again, bounded ----

    [Fact]
    public async Task A_busy_answer_is_retried_with_the_same_call_after_a_backoff_and_the_write_happens_once()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        // The claim first, so renew and release have one to act on.
        foreach (var (tool, write) in Writes(item))
        {
            h.Board.Calls.Clear();
            h.Waits.Clear();
            h.Board.Busy[tool] = 2;

            await write(h.Caller());

            // Three sends of one call — the same arguments, so the same idempotency key and the same expected version — and no read.
            Assert.Equal([tool, tool, tool], Tools(h.Board));
            Assert.Single(h.Board.Calls.Select(c => c.Arguments.ToJsonString()).Distinct());
            Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Waits);
        }
        // Each write happened once: one move, one comment, one link, one created item; the claim was taken, renewed and released.
        Assert.Equal(("InProgress", 2L, 1, 1), (item.Status, item.Version, item.Comments.Count, item.Links.Count));
        Assert.Null(item.Claim);
        Assert.Single(h.Board.Items, i => i.Title == "A child");
    }

    [Fact]
    public async Task A_call_that_stays_busy_is_given_up_after_the_retry_bound_with_nothing_written()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        h.Board.Busy["update_work_item"] = 99;

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None));

        Assert.Equal(KanbanException.Busy, ex.Code);
        // The first send and MaxBusyRetries more, after 1 s, 2 s, 4 s and 8 s; then busy is the answer (a refusal: no read, no repeat).
        Assert.Equal(Enumerable.Repeat("update_work_item", 1 + KanbanBoard.MaxBusyRetries), Tools(h.Board));
        Assert.Equal(new[] { 1, 2, 4, 8 }.Select(s => TimeSpan.FromSeconds(s)), h.Waits);
        Assert.Equal(("Ready", 1L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_busy_work_source_write_goes_through_without_the_caller_noticing()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        h.Board.Busy["claim_work_item"] = 1;
        h.Board.Busy["update_work_item"] = 1;
        h.Board.Busy["add_work_item_comment"] = 1;

        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(id, ignoreScope: false, None));
        await h.Source().CommentAsync(id, "hello", None);

        Assert.Equal(["claim_work_item", "claim_work_item", "update_work_item", "update_work_item", "add_work_item_comment", "add_work_item_comment"],
            h.Board.Writes);
        Assert.Equal(("InProgress", 2L, "factory"), (item.Status, item.Version, item.Claim!.Value.Name));
        Assert.Single(item.Comments);
    }

    // ---- An error that is no typed refusal: read before the write is repeated ----

    [Fact]
    public async Task A_move_whose_answer_was_lost_is_read_and_not_made_a_second_time()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        h.Board.LoseAnswer = (tool, _) => tool == "update_work_item";

        var moved = await h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None);

        // The write happened and its answer did not arrive: the read shows it (the version moved, the item is in the column).
        Assert.Equal(["update_work_item", "get_work_item"], Tools(h.Board));
        Assert.Equal(("InProgress", 2L), (moved.Status, moved.Version));
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_lost_answer_as_an_http_error_or_a_broken_answer_is_read_before_the_repeat_too()
    {
        foreach (var lost in new Func<JsonObject>[]
        {
            () => throw new FakeMcpServer.HttpAnswer(new HttpResponseMessage(HttpStatusCode.BadGateway)),
            () => FakeMcpServer.Text("<html>gateway timeout</html>"),
        })
        {
            var h = new KanbanWorkSourceTests.Harness();
            var item = h.Board.AddItem(h.Project, "Count words");
            h.Board.LoseAnswer = (tool, _) => tool == "update_work_item";
            h.Board.Lost = lost;

            await h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None);

            Assert.Equal(["update_work_item", "get_work_item"], Tools(h.Board));
            Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
        }
    }

    [Fact]
    public async Task A_move_that_failed_without_writing_is_read_and_then_sent_once_more_on_the_same_version()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var failures = 1;
        h.Board.Instead = (tool, _) => tool == "update_work_item" && failures-- > 0 ? PlainError() : null;

        await h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None);

        Assert.Equal(["update_work_item", "get_work_item", "update_work_item"], Tools(h.Board));
        Assert.All(h.Board.Calls.Where(c => c.Tool == "update_work_item"), c => Assert.Equal(1, c.Arguments["expected_version"]!.GetValue<long>()));
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_write_is_repeated_once_and_the_second_error_is_thrown_as_one_that_may_have_written()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        h.Board.Instead = (tool, _) => tool == "update_work_item" ? PlainError() : null;

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None));

        Assert.Equal((null, false), (ex.Code, ex.Refused));
        Assert.Equal(["update_work_item", "get_work_item", "update_work_item"], Tools(h.Board));
    }

    [Fact]
    public async Task A_write_is_not_repeated_blind_when_the_read_fails_too()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        h.Board.Instead = (_, _) => PlainError();

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None));

        Assert.Equal(("get_work_item", (string?)null), (ex.Tool, ex.Code));
        Assert.Equal(["update_work_item", "get_work_item"], Tools(h.Board));
    }

    [Fact]
    public async Task A_move_lost_while_someone_else_moved_the_item_is_refused_as_a_conflict_and_their_move_stands()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var failures = 1;
        // The factory's move never reached the board; before its read, someone else moved the item.
        h.Board.Instead = (tool, _) => tool == "update_work_item" && failures-- > 0 ? PlainError() : null;
        h.Board.Before = (tool, _) =>
        {
            if (tool == "get_work_item")
            {
                (item.Status, item.Version) = ("Done", item.Version + 1);
            }
        };

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None));

        // The repeat still carries the version the move was decided on, so the board refuses it: nothing is written over.
        Assert.Equal(KanbanException.Conflict, ex.Code);
        Assert.Equal(("Done", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_link_whose_answer_was_lost_is_found_on_the_item_and_one_that_was_not_written_is_sent_again()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        h.Board.LoseAnswer = (tool, _) => tool == "add_work_item_link";

        var linked = await h.Caller().LinkAsync(item.Id, "pr", Pr, None);

        Assert.Equal(["add_work_item_link", "get_work_item"], Tools(h.Board));
        Assert.True(linked.HasLink("pr", Pr));
        Assert.Equal([("pr", Pr)], item.Links);

        // Not written the first time: the read shows no such link, so it is sent once more.
        h.Board.Calls.Clear();
        h.Board.LoseAnswer = null;
        var failures = 1;
        h.Board.Instead = (tool, _) => tool == "add_work_item_link" && failures-- > 0 ? PlainError() : null;
        await h.Caller().LinkAsync(item.Id, "branch", "https://github.com/acme/widgets/tree/factory/kb-1", None);

        Assert.Equal(["add_work_item_link", "get_work_item", "add_work_item_link"], Tools(h.Board));
        Assert.Equal(2, item.Links.Count);
    }

    [Fact]
    public async Task A_comment_whose_answer_was_lost_is_sent_again_with_its_key_and_the_board_adds_no_second_comment()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var lost = 1;
        h.Board.LoseAnswer = (tool, _) => tool == "add_work_item_comment" && lost-- > 0;

        var commented = await h.Caller().CommentAsync(item.Id, "hello", "key-1", None);

        // It carries an idempotency key, so no read is needed: the repeat answers the comment the key already made.
        Assert.Equal(["add_work_item_comment", "add_work_item_comment"], Tools(h.Board));
        Assert.Single(h.Board.Calls.Select(c => c.Arguments.ToJsonString()).Distinct());
        Assert.False(commented.CommentCreated);
        Assert.Equal("hello", Assert.Single(item.Comments).Body);
    }

    [Fact]
    public async Task A_create_whose_answer_was_lost_is_sent_again_with_its_key_and_returns_the_item_it_already_made()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var lost = 1;
        h.Board.LoseAnswer = (tool, _) => tool == "create_work_item" && lost-- > 0;

        var created = await h.Caller().CreateItemAsync(h.Project.Id, "A child", "Task", "what to do", null, "child-key-1", None);

        Assert.Equal(["create_work_item", "create_work_item"], Tools(h.Board));
        var item = Assert.Single(h.Board.Items);
        Assert.Equal((item.Id, false, "Backlog", "A child"), (created.Id, created.Created, created.Status, created.Title));
        // The same key again, any time later (a restart), still makes no second item; another key does.
        Assert.Equal(item.Id, (await h.Caller().CreateItemAsync(h.Project.Id, "A child", "Task", "what to do", null, "child-key-1", None)).Id);
        Assert.Single(h.Board.Items);
        Assert.True((await h.Caller().CreateItemAsync(h.Project.Id, "A child", "Task", "what to do", null, "child-key-2", None)).Created);
    }

    [Fact]
    public async Task A_claim_or_a_renewal_whose_answer_was_lost_is_read_and_sent_again_and_a_lost_release_is_not()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var lost = 1;
        h.Board.LoseAnswer = (tool, _) => tool == "claim_work_item" && lost-- > 0;

        var claimed = await h.Caller().ClaimAsync(item.Id, Marker, 60, None);

        // The same caller's claim is idempotent by the contract, and only its own answer names this caller: read, then claim again.
        Assert.Equal(["claim_work_item", "get_work_item", "claim_work_item"], Tools(h.Board));
        Assert.Equal((Marker, "factory"), (claimed.ClaimMarker, claimed.Claimant));

        h.Board.Calls.Clear();
        lost = 1;
        h.Board.LoseAnswer = (tool, _) => tool == "renew_work_item_claim" && lost-- > 0;
        h.Board.Time.Now = h.Board.Time.Now.AddSeconds(30);
        var renewed = await h.Caller().RenewClaimAsync(item.Id, Marker, 60, None);
        Assert.Equal(["renew_work_item_claim", "get_work_item", "renew_work_item_claim"], Tools(h.Board));
        Assert.Equal(h.Board.Time.Now.AddSeconds(60), renewed.ClaimExpiresAtUtc);

        // A release that happened is not sent again: the board would refuse it (conflict: no claim under the marker).
        h.Board.Calls.Clear();
        h.Board.LoseAnswer = (tool, _) => tool == "release_work_item_claim";
        var released = await h.Caller().ReleaseClaimAsync(item.Id, Marker, None);
        Assert.Equal(["release_work_item_claim", "get_work_item"], Tools(h.Board));
        Assert.Null(released.ClaimMarker);
        Assert.Null(item.Claim);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_taken_for_a_lost_answer()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        using var cancel = new CancellationTokenSource();
        h.Board.Before = (_, _) => cancel.Cancel();
        h.Board.Instead = (_, _) => PlainError();

        await Assert.ThrowsAnyAsync<Exception>(() => h.Caller().SetStatusAsync(item.Id, "InProgress", 1, cancel.Token));

        Assert.Equal(["update_work_item"], Tools(h.Board));
    }

    // ---- conflict: read again, decide again, never write over ----

    [Fact]
    public async Task A_conflict_on_the_claims_move_re_reads_the_item_and_moves_it_on_the_new_version_keeping_the_other_writers_edit()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        var edits = 1;
        // Someone edits the title between the factory's read and its move: expected_version no longer holds.
        h.Board.Before = (tool, _) =>
        {
            if (tool == "update_work_item" && edits-- > 0)
            {
                (item.Title, item.Version) = ("Count words (and lines)", item.Version + 1);
            }
        };

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Ok, result);
        var moves = h.Board.Calls.Where(c => c.Tool == "update_work_item").ToList();
        Assert.Equal([1L, 2L], moves.Select(c => c.Arguments["expected_version"]!.GetValue<long>()));
        // Only the status was ever sent: their title stands, and the item moved on the version that holds it.
        Assert.All(moves, c => Assert.Equal(["itemId", "status", "expected_version"], c.Arguments.Select(a => a.Key)));
        Assert.Equal(("Count words (and lines)", "InProgress", 3L), (item.Title, item.Status, item.Version));
        Assert.Equal(["claim_work_item", "get_work_item", "update_work_item", "get_work_item", "update_work_item"],
            Tools(h.Board).SkipWhile(t => t != "claim_work_item"));
    }

    [Fact]
    public async Task A_conflict_because_someone_else_moved_the_item_leaves_it_where_they_put_it_and_the_claim_is_refused_and_given_back()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        h.Board.Before = (tool, _) =>
        {
            if (tool == "update_work_item")
            {
                (item.Status, item.Version) = ("Backlog", item.Version + 1);
            }
        };

        var result = await h.Source().ClaimAsync(id, ignoreScope: false, None);

        Assert.Equal(ClaimResult.Refused("the item was moved to Backlog while it was being claimed"), result);
        // One move, refused; then the read, and no second move: the second writer's change is not overwritten.
        Assert.Equal(["claim_work_item", "update_work_item", "release_work_item_claim"], h.Board.Writes);
        Assert.Equal(("Backlog", 2L, (string?)null), (item.Status, item.Version, item.Claim?.Name));
    }

    [Fact]
    public async Task A_move_that_keeps_conflicting_is_given_up_after_the_re_read_bound_and_changes_nothing()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        h.Board.Before = (tool, _) =>
        {
            if (tool == "update_work_item")
            {
                item.Version++;
            }
        };

        var ex = await Assert.ThrowsAsync<KanbanException>(() => h.Source().ClaimAsync(id, ignoreScope: false, None));

        Assert.Equal((KanbanException.Conflict, "update_work_item"), (ex.Code, ex.Tool));
        Assert.Equal(1 + KanbanWorkSource.MaxConflictRereads, h.Board.Writes.Count(t => t == "update_work_item"));
        Assert.Equal("Ready", item.Status);
        // The claim is given back: left on a Ready item it would hide it from every ready listing for a day.
        Assert.Null(item.Claim);
        Assert.Equal("release_work_item_claim", h.Board.Writes[^1]);
        Assert.Equal([id], await h.Source().ListReadyAsync(None));
    }

    [Fact]
    public async Task An_error_code_the_contract_does_not_name_is_no_typed_refusal_so_the_item_is_read_before_the_repeat()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var unknown = 1;
        h.Board.Instead = (tool, _) => tool == "update_work_item" && unknown-- > 0 ? FakeKanbanBoard.Refusal("teapot", "short and stout") : null;

        var moved = await h.Caller().SetStatusAsync(item.Id, "InProgress", 1, None);

        // Only the contract's seven codes promise that nothing was written: after any other, the write may have happened.
        Assert.Equal(["update_work_item", "get_work_item", "update_work_item"], Tools(h.Board));
        Assert.Equal(("InProgress", 2L), (moved.Status, item.Version));
        Assert.False(new KanbanException("update_work_item", "teapot", "short and stout").Refused);
        Assert.False(new KanbanException("update_work_item", null, "plain").Refused);
        Assert.All(KanbanException.Codes, code => Assert.True(new KanbanException("update_work_item", code, "refused").Refused));
    }

    [Fact]
    public async Task A_reported_state_that_conflicts_with_an_edit_is_moved_on_the_new_version()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "InProgress");
        var id = await h.Key(item);
        var edits = 1;
        h.Board.Before = (tool, _) =>
        {
            if (tool == "update_work_item" && edits-- > 0)
            {
                item.Labels.Add("urgent");
                item.Version++;
            }
        };

        await h.Source().ReportStateAsync(id, BoardState.Merged, null, None);

        Assert.Equal(["update_work_item", "update_work_item"], h.Board.Writes);
        Assert.Equal(("Done", 3L, "urgent"), (item.Status, item.Version, Assert.Single(item.Labels)));
        Assert.Empty(item.Comments);
    }

    [Theory]
    [InlineData(BoardState.Merged, "Done", null)]
    [InlineData(BoardState.Stopped, "Backlog", "kb-1 was stopped by michael.")]
    public async Task A_reported_state_that_conflicts_with_someone_elses_move_leaves_their_column_and_says_so_on_the_item(
        BoardState state, string target, string? comment)
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        await h.Source().ClaimAsync(id, ignoreScope: false, None);
        h.Board.Calls.Clear();
        h.Board.Before = (tool, _) =>
        {
            if (tool == "update_work_item")
            {
                (item.Status, item.Version) = ("Blocked", item.Version + 1);
            }
        };

        await h.Source().ReportStateAsync(id, state, comment, None);

        // One move, refused (conflict); the re-read shows another column, so no second move is made.
        Assert.Equal(1, h.Board.Writes.Count(t => t == "update_work_item"));
        Assert.Equal(("Blocked", 3L), (item.Status, item.Version));
        var note = KanbanWorkSource.NotMoved(target, "Blocked");
        Assert.Equal($"The factory was moving this item to {target} when someone else moved it to Blocked; it is left in Blocked, where they put it.", note);
        Assert.Equal($"[author: dark-factory] {(comment is null ? note : $"{comment}\n\n{note}")}", Assert.Single(item.Comments).Body);
        // A stop still gives the claim back; a merge leaves it to lapse, as when the move is made.
        Assert.Equal(state == BoardState.Stopped, item.Claim is null);
    }

    [Fact]
    public async Task A_reported_state_whose_moves_answer_was_lost_is_not_moved_twice()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "InProgress");
        var id = await h.Key(item);
        h.Board.LoseAnswer = (tool, _) => tool == "update_work_item";

        await h.Source().ReportStateAsync(id, BoardState.Merged, null, None);

        Assert.Equal(["update_work_item"], h.Board.Writes);
        Assert.Equal(("Done", 2L), (item.Status, item.Version));
    }

    // ---- Claim renewal ----

    /// <summary>An item the factory claimed, as Intake leaves it: claimed and InProgress on the board, a <c>claimed</c> row in the ledger.</summary>
    private static async Task<(KanbanWorkSourceTests.Harness H, FakeKanbanBoard.Item Item, int Id)> ClaimedAsync()
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        Assert.Equal(ClaimResult.Ok, await h.Source().ClaimAsync(id, ignoreScope: false, None));
        await h.RecordClaimedAsync(id);
        h.Board.Calls.Clear();
        return (h, item, id);
    }

    [Fact]
    public async Task A_renewed_claim_is_still_held_after_its_first_lifetime_would_have_lapsed_and_nothing_else_is_written()
    {
        var (h, item, id) = await ClaimedAsync();
        var claimedAt = h.Board.Time.Now;
        Assert.Equal(claimedAt.AddSeconds(KanbanWorkSource.ClaimTtlSeconds), item.Claim!.Value.Expires);
        Assert.Equal(86400, KanbanWorkSource.ClaimTtlSeconds); // the longest the contract allows

        h.Board.Time.Now = claimedAt.AddHours(20);
        Assert.Equal(ClaimRenewal.Held, await h.Source().RenewClaimAsync(id, None));

        // One call, with the item's marker and the maximum lifetime.
        var call = Assert.Single(h.Board.Calls);
        Assert.Equal(new JsonObject { ["id"] = item.Id.ToString(), ["marker"] = KanbanWorkSource.Marker(item.Id), ["ttl_seconds"] = 86400 }.ToJsonString(),
            call.Arguments.ToJsonString());
        Assert.Equal("renew_work_item_claim", call.Tool);
        // 30 hours after the claim: its first lifetime (24 h) is over, and the factory still holds the item.
        h.Board.Time.Now = claimedAt.AddHours(30);
        var read = await h.Caller().GetItemAsync(item.Id, None);
        Assert.Equal(("factory", KanbanWorkSource.Marker(item.Id), claimedAt.AddHours(44)), (read.Claimant, read.ClaimMarker, read.ClaimExpiresAtUtc!.Value));
        Assert.Equal(("InProgress", 2L, 0), (item.Status, item.Version, item.Comments.Count));
        // Without the renewal it would have lapsed: another caller could have claimed it.
        Assert.Empty(await h.Source().ListReadyAsync(None));
    }

    [Fact]
    public async Task A_claim_that_lapsed_with_nobody_else_holding_the_item_is_taken_again()
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);

        Assert.Equal(ClaimRenewal.Held, await h.Source().RenewClaimAsync(id, None));

        // The renewal is refused (conflict: the claim lapsed); the read shows no claim; the factory claims its own item again.
        Assert.Equal(["renew_work_item_claim", "get_work_item", "claim_work_item"], Tools(h.Board));
        Assert.Equal(("factory", h.Board.Time.Now.AddHours(24)), (item.Claim!.Value.Name, item.Claim.Value.Expires));
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task An_unclaimed_item_the_ledger_never_recorded_as_claimed_is_not_claimed_by_a_renewal_and_its_claim_stays_refused()
    {
        // In flight at Intake with no `claimed` row (a run that died between the Intake row and the claim), and on the board
        // InProgress with no claim: it may be a person's work. The renewal must not take what ClaimAsync refuses.
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words", status: "InProgress");
        var id = await h.Key(item);
        await h.RecordClaimedAsync(id, claimed: false);

        Assert.Equal(ClaimRenewal.Held, await h.Source().RenewClaimAsync(id, None));

        Assert.Equal(["renew_work_item_claim", "get_work_item"], Tools(h.Board));
        Assert.Null(item.Claim);
        Assert.Equal(ClaimResult.Refused(KanbanWorkSource.LapsedRefusal), await h.Source().ClaimAsync(id, ignoreScope: false, None));
        Assert.DoesNotContain("claim_work_item", Tools(h.Board));
    }

    [Theory]
    [InlineData("Backlog")]
    [InlineData("Ready")]
    [InlineData("Done")]
    public async Task A_lapsed_claim_on_an_item_someone_moved_out_of_in_progress_is_not_taken_again(string column)
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        (item.Status, item.Version) = (column, item.Version + 1);

        Assert.Equal(ClaimRenewal.Held, await h.Source().RenewClaimAsync(id, None));

        // Theirs to place: no stray claim that would hide it from `claim_state: unclaimed` if it is made ready again.
        Assert.Equal(["renew_work_item_claim", "get_work_item"], Tools(h.Board));
        Assert.Null((await h.Caller().GetItemAsync(item.Id, None)).ClaimMarker);
        Assert.Equal((column, 3L), (item.Status, item.Version));
    }

    [Fact]
    public async Task Another_callers_name_and_marker_are_cut_and_inert_wherever_a_refusal_or_a_lost_claim_names_them()
    {
        var (h, item, id) = await ClaimedAsync();
        // A claimant's name and a marker are the other caller's text; the reason goes into a board comment and the ledger.
        var (name, marker) = ("@everyone [x](https://evil.test) `" + new string('n', 300), "run`\n# <b>@you</b> https://evil.test/" + new string('m', 300));
        item.Claim = (Guid.NewGuid(), name, marker, h.Board.Time.Now.AddHours(1));

        var lost = (await h.Source().RenewClaimAsync(id, None)).Lost!;
        var refused = (await h.Source().ClaimAsync(id, ignoreScope: false, None)).Refusal!;

        var (inertName, inertMarker) = (UntrustedText.CodeSpan(name[..200]), UntrustedText.CodeSpan(marker[..200]));
        Assert.Equal($"the item is claimed by {inertName} (marker {inertMarker})", lost);
        Assert.Equal($"the item is claimed by {inertName}", refused);
        Assert.Equal(202, inertName.Length); // cut at 200, in one code span
        foreach (var text in new[] { lost, refused })
        {
            Assert.DoesNotContain('@', text);
            Assert.DoesNotContain("://", text);
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('[', text);
            Assert.DoesNotContain('<', text);
            Assert.Equal(0, text.Count(c => c == '`') % 2);
        }
        Assert.Equal(2, refused.Count(c => c == '`'));
    }

    [Fact]
    public async Task A_claim_another_caller_took_is_lost_with_a_reason_naming_them_and_nothing_is_written()
    {
        var (h, item, id) = await ClaimedAsync();
        // The factory's claim lapsed, and someone else claimed the item.
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(item);

        var renewal = await h.Source().RenewClaimAsync(id, None);

        Assert.Equal(ClaimRenewal.LostBecause("the item is claimed by `someone` (marker `their-run`)"), renewal);
        Assert.Equal(["renew_work_item_claim", "get_work_item"], Tools(h.Board)); // the renewal was refused already_claimed
        Assert.Equal(("someone", "their-run", "InProgress", 2L), (item.Claim!.Value.Name, item.Claim.Value.Marker, item.Status, item.Version));
    }

    [Fact]
    public async Task A_lapsed_claim_another_caller_wins_the_race_for_is_lost_to_them()
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.Before = (tool, _) =>
        {
            if (tool == "claim_work_item")
            {
                h.Board.ClaimAsSomeoneElse(item);
            }
        };

        var renewal = await h.Source().RenewClaimAsync(id, None);

        Assert.Equal(ClaimRenewal.LostBecause("the item is claimed by `someone` (marker `their-run`)"), renewal);
        Assert.Equal(["renew_work_item_claim", "get_work_item", "claim_work_item", "get_work_item"], Tools(h.Board));
    }

    [Fact]
    public async Task The_factorys_marker_held_by_another_identity_is_a_lost_claim_too()
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        // Another identity (a second factory on the same board) holds the item under the very marker this factory derives.
        h.Board.ClaimAsSomeoneElse(item, KanbanWorkSource.Marker(item.Id));

        var renewal = await h.Source().RenewClaimAsync(id, None);

        // The marker alone does not make it the factory's: the board tells the identities apart and refuses the claim.
        Assert.Equal(ClaimRenewal.LostBecause($"the item is claimed by `someone` (marker `{KanbanWorkSource.Marker(item.Id)}`)"), renewal);
        Assert.Equal("someone", item.Claim!.Value.Name);
    }

    [Fact]
    public async Task An_item_gone_from_the_board_has_lost_its_claim()
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Items.Remove(item);

        Assert.Equal(ClaimRenewal.LostBecause("the item is no longer on the board"), await h.Source().RenewClaimAsync(id, None));
    }

    [Fact]
    public async Task A_renewal_the_board_refuses_for_the_calls_own_reason_is_not_renewed_and_not_a_lost_claim()
    {
        var (h, item, id) = await ClaimedAsync();
        h.Board.Instead = (tool, _) => tool == "renew_work_item_claim" ? FakeKanbanBoard.Refusal("scope_denied", "the token was revoked") : null;

        var renewal = await h.Source().RenewClaimAsync(id, None);

        // The board answered: nothing about who holds the item, so neither held nor lost — and the next item can be asked.
        Assert.Equal(ClaimRenewal.NotRenewedBecause("Kanban's renew_work_item_claim was refused (scope_denied): the token was revoked"), renewal);
        Assert.Equal((false, (string?)null), (renewal.IsHeld, renewal.Lost));
        Assert.Equal(["renew_work_item_claim"], Tools(h.Board));
        Assert.Equal("factory", item.Claim!.Value.Name);
        // A key the ledger never gave out is the item's own trouble too, and asks the board nothing.
        Assert.Equal(ClaimRenewal.NotRenewedBecause("kb-99 is no Kanban item the factory knows"), await h.Source().RenewClaimAsync(99, None));
        Assert.Equal(["renew_work_item_claim"], Tools(h.Board));
    }

    [Fact]
    public async Task A_renewal_the_board_does_not_answer_is_thrown_busy_after_its_retries_and_a_lost_answer_alike()
    {
        var (h, _, id) = await ClaimedAsync();
        // Busy past the retry bound: the board is overloaded, which is no answer about this item.
        h.Board.Busy["renew_work_item_claim"] = 100;
        var busy = await Assert.ThrowsAsync<KanbanException>(() => h.Source().RenewClaimAsync(id, None));
        Assert.Equal(KanbanException.Busy, busy.Code);

        h.Board.Busy.Clear();
        h.Board.Lost = () => throw new FakeMcpServer.HttpAnswer(new HttpResponseMessage(HttpStatusCode.BadGateway));
        h.Board.LoseAnswer = (_, _) => true;
        await Assert.ThrowsAnyAsync<Exception>(() => h.Source().RenewClaimAsync(id, None));
    }

    [Fact]
    public async Task Renewal_is_an_optional_member_and_the_other_sources_have_nothing_to_renew()
    {
        // Shortcut's and GitHub issues' sources do not implement it: the interface's default holds, asking no board anything.
        Assert.Null(typeof(Shortcut.ShortcutWorkSource).GetMethod(nameof(IWorkSource.RenewClaimAsync)));
        Assert.Null(typeof(Issues.GitHubIssueWorkSource).GetMethod(nameof(IWorkSource.RenewClaimAsync)));
        Assert.NotNull(typeof(KanbanWorkSource).GetMethod(nameof(IWorkSource.RenewClaimAsync)));
        IWorkSource other = new FakeWorkSource(new WorkStory(101, "A story", null, "feature", "https://example.test/101"));

        Assert.Equal(ClaimRenewal.Held, await other.RenewClaimAsync(101, None));
    }

    // ---- Renewal on each intake poll, and a lost claim pausing the item ----

    /// <summary>The production runner's members over the test's ledger and its controls: what <c>FactoryItemRunner</c> does.</summary>
    private sealed class Runner(KanbanWorkSourceTests.Harness h, InProcessRunLocks locks) : IItemRunner
    {
        public List<int> Runs { get; } = [];
        public List<string?> Lost { get; } = [];

        public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
        {
            await using var db = h.Db();
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, Controls(h), naming: ItemNaming.Kanban);
        }

        public async Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct)
        {
            await using var db = h.Db();
            return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), ct, Controls(h), naming: ItemNaming.Kanban);
        }

        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Runs.Add(id);
            return Task.FromResult(new RunOutcome(1, WorkState.Review, null, null, null));
        }

        public async Task<string?> ClaimLostAsync(int id, string reason, CancellationToken ct)
        {
            await using var db = h.Db();
            var what = await RunPipeline.ClaimLostAsync(h.Source(), new WorkLedger(db, TimeProvider.System), locks, id, reason, TextWriter.Null, ct, Controls(h));
            Lost.Add(what);
            return what;
        }
    }

    private static IControls Controls(KanbanWorkSourceTests.Harness h) => new LedgerControls(h.Contexts, TimeProvider.System);

    private static IntakeLoop Loop(IWorkSource source, IItemRunner runner, IntakeStatus? status = null, IReadOnlyList<IntakeLane>? more = null,
        IControls? controls = null) =>
        new(source, runner, new IntakeOptions(TimeSpan.FromSeconds(60)), TimeProvider.System, NullLogger<IntakeLoop>.Instance, controls, status: status,
            moreLanes: more);

    /// <summary>
    /// A Kanban item in flight at Implement, as Intake leaves a claimed item (a <c>claimed</c> checkpoint, then Implement): claimed
    /// and <c>InProgress</c> on the board. A state the gateless test pipeline drives, so a run of it resumes it.
    /// </summary>
    private static async Task<(KanbanWorkSourceTests.Harness H, FakeKanbanBoard.Item Item, int Id, InProcessRunLocks Locks)> ImplementingAsync()
    {
        var (h, item, id) = await ClaimedAsync();
        await using (var db = h.Db())
        {
            await new WorkLedger(db, TimeProvider.System).RecordAsync(await db.WorkItems.SingleAsync(), WorkState.Implement, null, null, None);
        }
        return (h, item, id, new InProcessRunLocks());
    }

    /// <summary>A real run of the item (<c>factory run kb-N</c>) over the test's ledger; no gate stage, so it ends at Review.</summary>
    private static async Task<RunOutcome> RunAsync(KanbanWorkSourceTests.Harness h, int id, InProcessRunLocks locks)
    {
        await using var db = h.Db();
        return await new RunPipeline(h.Source(), new WorkLedger(db, TimeProvider.System), locks, new FakeWorkspaces(), new FakeWorker(Reports(Ok)),
            new FakePullRequests(), Sandbox, TextWriter.Null).RunAsync(id, None);
    }

    /// <summary>A Kanban item the pipeline took to Review (pull request open): in flight, claimed, <c>InProgress</c>.</summary>
    private static async Task<(KanbanWorkSourceTests.Harness H, FakeKanbanBoard.Item Item, int Id, InProcessRunLocks Locks)> InFlightAsync(
        Worker.WorkerResult? result = null)
    {
        var h = new KanbanWorkSourceTests.Harness();
        var item = h.Board.AddItem(h.Project, "Count words");
        var id = await h.Key(item);
        var locks = new InProcessRunLocks();
        await using (var db = h.Db())
        {
            await new RunPipeline(h.Source(), new WorkLedger(db, TimeProvider.System), locks, new FakeWorkspaces(), new FakeWorker(Reports(result ?? Ok)),
                new FakePullRequests(), Sandbox, TextWriter.Null).RunAsync(id, None);
        }
        h.Board.Calls.Clear();
        return (h, item, id, locks);
    }

    private static async Task<List<LedgerEntry>> History(KanbanWorkSourceTests.Harness h)
    {
        await using var db = h.Db();
        return await db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    [Fact]
    public async Task Each_poll_renews_the_claim_of_an_in_flight_item_so_it_outlives_its_first_lifetime()
    {
        var (h, item, id, locks) = await InFlightAsync();
        var claimedAt = h.Board.Time.Now;
        var runner = new Runner(h, locks);

        // Three polls over 44 hours, none more than a claim's lifetime after the one before.
        foreach (var hours in new[] { 20, 40, 44 })
        {
            h.Board.Time.Now = claimedAt.AddHours(hours);
            await Loop(h.Source(), runner).PollOnceAsync(None);
        }

        Assert.Equal(["renew_work_item_claim", "renew_work_item_claim", "renew_work_item_claim"], h.Board.Writes);
        Assert.Equal(("factory", claimedAt.AddHours(68)), (item.Claim!.Value.Name, item.Claim.Value.Expires));
        Assert.Equal([id, id, id], runner.Runs); // renewed, then run as before
        Assert.Empty(runner.Lost);
    }

    [Fact]
    public async Task An_in_flight_item_another_caller_claimed_is_paused_with_a_reason_naming_them_told_once_and_not_run()
    {
        var (h, item, id, locks) = await InFlightAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(item);
        var (runner, status) = (new Runner(h, locks), new IntakeStatus(TimeProvider.System));
        const string reason = "claim lost: the item is claimed by `someone` (marker `their-run`)";

        await Loop(h.Source(), runner, status).PollOnceAsync(None);

        Assert.Empty(runner.Runs);
        Assert.Equal([$"paused ({reason})"], runner.Lost);
        var history = await History(h);
        // Paused from Review, then parked: both set aside (deferred), neither a failure nor an escalation.
        Assert.Equal(new[] { (WorkState.Paused, (string?)null, reason, StepOutcome.Deferred), (WorkState.Paused, "parked", reason, StepOutcome.Deferred) },
            history.TakeLast(2).Select(e => (e.State, e.Step, e.Detail!, e.Outcome)));
        Assert.True(RunPipeline.IsClaimLost(history[^2].Detail));
        // The item is told once, and nothing else of it is written: their claim, its column and its version stand.
        Assert.Equal(["renew_work_item_claim", "add_work_item_comment"], h.Board.Writes); // the renewal was refused: it wrote nothing
        Assert.Equal("[author: dark-factory] kb-1: the factory lost its claim on this Kanban item — the item is claimed by `someone` (marker `their-run`) — "
            + "so it paused its work on it (at Review) and writes nothing more to it. Once the item is the factory's to work again (the other "
            + "claim released or lapsed), run `factory run kb-1` to resume it where it paused, or `factory stop --item kb-1` to give it up.",
            Assert.Single(item.Comments).Body);
        Assert.Equal(("someone", "InProgress", 2L), (item.Claim!.Value.Name, item.Status, item.Version));
        // Shown on the dashboard.
        Assert.Equal((reason, $"paused ({reason})"), (status.ItemErrors["kb-1"].Message, status.ItemErrors["kb-1"].GaveUp));

        // Parked: it does not resume by itself, so later polls neither list it, renew it, run it nor comment again — and it is not
        // ready either (it is InProgress), so nothing takes it up but a person's `factory run`.
        h.Board.Calls.Clear();
        await Loop(h.Source(), runner, status).PollOnceAsync(None);
        Assert.Empty(await runner.InFlightAsync(None));
        Assert.Empty(h.Board.Writes);
        Assert.Empty(runner.Runs);
        Assert.Equal(history.Count, (await History(h)).Count);
    }

    [Fact]
    public async Task An_escalated_item_is_not_renewed_its_claim_lapses_and_its_column_keeps_it_out_of_the_ready_list()
    {
        var (h, item, _, locks) = await InFlightAsync(new Worker.WorkerResult("sess-x", 1, true, "error_during_execution", "Not logged in", "boom"));
        Assert.Equal(WorkState.Escalated, (await History(h)).Last(e => e.Step is null).State);
        var runner = new Runner(h, locks);

        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        await Loop(h.Source(), runner).PollOnceAsync(None);

        // Not in flight: no renewal. Its claim lapsed, and it is still not ready for anyone's poll, since it is InProgress.
        Assert.DoesNotContain("renew_work_item_claim", Tools(h.Board));
        Assert.Empty(h.Board.Writes);
        Assert.Null((await h.Caller().GetItemAsync(item.Id, None)).ClaimMarker);
        Assert.Equal("InProgress", item.Status);
        Assert.Empty(await h.Source().ListReadyAsync(None));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task An_item_a_control_pauses_keeps_its_claim_renewed_and_is_not_run_until_the_continue()
    {
        var (h, item, id, locks) = await InFlightAsync();
        var claimedAt = h.Board.Time.Now;
        var runner = new Runner(h, locks);
        await Controls(h).SetAsync(ControlScope.Item("kb-1"), ControlState.Paused, "michael", None);

        // Paused for 40 hours, polled meanwhile: renewed each time, never run.
        foreach (var hours in new[] { 20, 40 })
        {
            h.Board.Time.Now = claimedAt.AddHours(hours);
            await Loop(h.Source(), runner, controls: Controls(h)).PollOnceAsync(None);
        }
        Assert.Equal(["renew_work_item_claim", "renew_work_item_claim"], h.Board.Writes);
        Assert.Equal(("factory", claimedAt.AddHours(64)), (item.Claim!.Value.Name, item.Claim.Value.Expires));
        Assert.Empty(runner.Runs);
        Assert.Empty(await runner.InFlightAsync(None));
        Assert.Equal([id], await runner.RenewableAsync(None));

        await Controls(h).SetAsync(ControlScope.Item("kb-1"), ControlState.Running, "michael", None);
        await Loop(h.Source(), runner, controls: Controls(h)).PollOnceAsync(None);

        Assert.Equal([id], runner.Runs);
        Assert.Equal("factory", (await h.Caller().GetItemAsync(item.Id, None)).Claimant);
    }

    [Fact]
    public async Task A_claim_that_lapsed_while_no_poll_ran_is_taken_again_by_the_next_polls_renewal_before_the_item_is_run()
    {
        var (h, item, id, locks) = await InFlightAsync();
        var runner = new Runner(h, locks);

        // `factory work` was down for more than a day; nobody took the item meanwhile.
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        await Loop(h.Source(), runner).PollOnceAsync(None);

        Assert.Equal(["renew_work_item_claim", "claim_work_item"], h.Board.Writes);
        Assert.Equal("factory", item.Claim!.Value.Name);
        Assert.Equal([id], runner.Runs);
    }

    public static TheoryData<string> Holds => ["factory", "epic", "item", "freeze", "usage"];

    [Theory]
    [MemberData(nameof(Holds))]
    public async Task Claims_are_renewed_and_nothing_is_run_while_a_control_the_freeze_or_the_usage_pause_holds_the_item(string hold)
    {
        var (h, item, id, locks) = await InFlightAsync();
        var claimedAt = h.Board.Time.Now;
        var (runner, controls) = (new Runner(h, locks), Controls(h));
        await using (var db = h.Db())
        {
            (await db.WorkItems.SingleAsync()).EpicId = 7;
            await db.SaveChangesAsync();
        }
        switch (hold)
        {
            case "factory": await controls.SetAsync(ControlScope.Factory, ControlState.Paused, "michael", None); break;
            case "epic": await controls.SetAsync(ControlScope.Epic(7), ControlState.Paused, "michael", None); break;
            case "item": await controls.SetAsync(ControlScope.Item("kb-1"), ControlState.Paused, "michael", None); break;
            case "freeze": await controls.FreezeAsync("hot-file", "one file, three merges", null, None); break;
            default: await controls.PauseForUsageAsync(DateTimeOffset.UtcNow.AddDays(3), "all_exhausted", None); break;
        }
        // The production in-flight list leaves the item out (nothing runs it); the renewal list does not.
        Assert.Equal(ControlState.Paused, await controls.EffectiveAsync("kb-1", 7, None));
        Assert.Empty(await runner.InFlightAsync(None));

        // Held for two days — a freeze lasts until a human's Continue — and polled meanwhile.
        foreach (var hours in new[] { 20, 40, 48 })
        {
            h.Board.Time.Now = claimedAt.AddHours(hours);
            await Loop(h.Source(), runner, controls: controls).PollOnceAsync(None);
        }

        Assert.Equal(["renew_work_item_claim", "renew_work_item_claim", "renew_work_item_claim"], h.Board.Writes);
        Assert.Equal(("factory", claimedAt.AddHours(72)), (item.Claim!.Value.Name, item.Claim.Value.Expires));
        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Lost);
        Assert.Equal(("InProgress", 2L), (item.Status, item.Version));
    }

    [Fact]
    public async Task A_claim_lost_while_the_factory_is_frozen_still_pauses_the_item_and_runs_nothing()
    {
        var (h, item, _, locks) = await InFlightAsync();
        var (runner, controls) = (new Runner(h, locks), Controls(h));
        await controls.FreezeAsync("hot-file", "one file, three merges", null, None);
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(item);

        await Loop(h.Source(), runner, controls: controls).PollOnceAsync(None);

        Assert.Equal(["paused (claim lost: the item is claimed by `someone` (marker `their-run`))"], runner.Lost);
        Assert.Equal("parked", (await History(h))[^1].Step);
        Assert.Empty(runner.Runs);
        Assert.Empty(await runner.RenewableAsync(None));
    }

    /// <summary>Controls that cannot be read (the ledger's control table unreachable).</summary>
    private sealed class UnreadableControls : IControls
    {
        private static InvalidOperationException Down() => new("controls unreadable");
        public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct) => throw Down();
        public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => throw Down();
        public Task<Control?> UsagePauseAsync(CancellationToken ct) => throw Down();
        public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) => throw Down();
        public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) => throw Down();
        public Task<Control?> GetAsync(string scope, CancellationToken ct) => throw Down();
        public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => throw Down();
        public Task ClearAsync(string scope, CancellationToken ct) => throw Down();
    }

    [Fact]
    public async Task The_renewal_list_is_what_the_ledger_resumes_by_itself_whatever_the_controls_say_and_never_what_is_parked_escalated_or_being_stopped()
    {
        var (h, _, id, locks) = await InFlightAsync();
        async Task<IReadOnlyList<int>> Renewable(IControls? controls = null)
        {
            await using var db = h.Db();
            return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), None, controls ?? Controls(h), naming: ItemNaming.Kanban);
        }
        async Task Record(WorkState state, string? detail, string? step = null)
        {
            await using var db = h.Db();
            var (ledger, row) = (new WorkLedger(db, TimeProvider.System), await db.WorkItems.SingleAsync());
            await (step is null ? ledger.RecordAsync(row, state, null, detail, None) : ledger.CheckpointAsync(row, step, null, detail, None));
        }

        // In a handled state (Review): listed, also when the controls cannot be read (an unreadable control costs no claim).
        Assert.Equal([id], await Renewable());
        Assert.Equal([id], await Renewable(new UnreadableControls()));
        // Another source's list does not hold it.
        await using (var db = h.Db())
        {
            Assert.Empty(await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), None, Controls(h)));
        }
        // Being stopped: not listed (the stop gives the claim back).
        await Controls(h).SetAsync(ControlScope.Item("kb-1"), ControlState.Stopping, "michael", None);
        Assert.Empty(await Renewable());
        await Controls(h).ClearAsync(ControlScope.Item("kb-1"), None);
        // Every pause that resumes by itself: listed.
        foreach (var pause in new[] { RunPipeline.Interrupted, RunPipeline.UserPaused, RunPipeline.UsagePaused, RunPipeline.FreezePaused,
            RunPipeline.ControlsUnreadablePaused })
        {
            await Record(WorkState.Paused, pause);
            Assert.Equal([id], await Renewable());
            await Record(WorkState.Review, "unpaused");
        }
        // Parked (a give-up, a refused or a lost claim): not listed.
        await Record(WorkState.Paused, RunPipeline.Interrupted);
        await Record(WorkState.Paused, "3 runs in a row failed", RunPipeline.Steps.Parked);
        Assert.Empty(await Renewable());
        await Record(WorkState.Review, "unpaused");
        await Record(WorkState.Paused, "claim refused: the item is Backlog, not Ready");
        Assert.Empty(await Renewable());
        await Record(WorkState.Review, "unpaused");
        Assert.Equal([id], await Renewable());
        // Escalated: not listed.
        await Record(WorkState.Escalated, "boom");
        Assert.Empty(await Renewable());
        // The production runner has its own list (the interface's default is the in-flight list, which a control empties).
        Assert.Equal(typeof(FactoryItemRunner), typeof(FactoryItemRunner).GetMethod(nameof(IItemRunner.RenewableAsync))?.DeclaringType);
    }

    [Fact]
    public async Task A_run_does_not_resume_a_claim_lost_item_while_the_other_claim_stands_and_resumes_it_once_it_is_released()
    {
        var (h, item, id, locks) = await ImplementingAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(item);
        await Loop(h.Source(), new Runner(h, locks)).PollOnceAsync(None);
        var parked = await History(h);
        Assert.Equal((WorkState.Paused, "parked"), (parked[^1].State, parked[^1].Step));
        var comments = item.Comments.Count;
        h.Board.Calls.Clear();

        // `factory run kb-1` while they still hold it: nothing recorded, nothing written (the renewal is refused).
        var refused = await RunAsync(h, id, locks);

        Assert.Equal(WorkState.Paused, refused.State);
        Assert.Equal("kb-1 is still not the factory's to work (claim lost: the item is claimed by `someone` (marker `their-run`)); it stays paused. "
            + "Run `factory run kb-1` again once the other claim is released or has lapsed, or `factory stop --item kb-1` to give the item up.",
            refused.Error);
        Assert.Equal(parked.Select(e => e.Id), (await History(h)).Select(e => e.Id));
        Assert.Equal(["renew_work_item_claim"], h.Board.Writes);
        Assert.Equal(("someone", "InProgress", 2L, comments), (item.Claim!.Value.Name, item.Status, item.Version, item.Comments.Count));

        // They release it: the run takes the claim back (the ledger records the item as claimed, and it is InProgress) and
        // resumes the item where it paused.
        item.Claim = null;
        h.Board.Calls.Clear();
        var resumed = await RunAsync(h, id, locks);

        Assert.Equal((WorkState.Review, (string?)null), (resumed.State, resumed.Error));
        var after = await History(h);
        Assert.Equal((WorkState.Implement, (string?)null, "unpaused"), (after[parked.Count].State, after[parked.Count].Step, after[parked.Count].Detail));
        Assert.Equal(["renew_work_item_claim", "claim_work_item"], h.Board.Writes.Take(2));
        Assert.Equal(("factory", KanbanWorkSource.Marker(item.Id)), (item.Claim!.Value.Name, item.Claim.Value.Marker));
    }

    [Fact]
    public async Task A_run_does_not_resume_an_item_parked_for_a_lost_claim_while_it_was_already_paused()
    {
        var (h, item, id, locks) = await ImplementingAsync();
        await using (var db = h.Db())
        {
            await new WorkLedger(db, TimeProvider.System).RecordAsync(await db.WorkItems.SingleAsync(), WorkState.Paused, null, RunPipeline.Interrupted, None);
        }
        h.Board.ClaimAsSomeoneElse(item);
        Assert.NotNull(await new Runner(h, locks).ClaimLostAsync(id, "the item is claimed by someone", None));
        var parked = await History(h);
        // Its Paused row says `interrupted`; the `parked` row after it is what holds a run back while they have the item.
        Assert.Equal((RunPipeline.Interrupted, "parked"), (parked[^2].Detail, parked[^1].Step));

        var outcome = await RunAsync(h, id, locks);

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains("is still not the factory's to work", outcome.Error);
        Assert.Equal(parked.Count, (await History(h)).Count);
    }

    [Fact]
    public async Task A_run_does_not_resume_a_claim_lost_item_whose_claim_the_board_will_not_confirm()
    {
        var (h, item, id, locks) = await ImplementingAsync();
        h.Board.ClaimAsSomeoneElse(item);
        await Loop(h.Source(), new Runner(h, locks)).PollOnceAsync(None);
        var parked = (await History(h)).Count;
        item.Claim = null;
        h.Board.Instead = (tool, _) => tool == "renew_work_item_claim" ? FakeKanbanBoard.Refusal("scope_denied", "the token was revoked") : null;

        var outcome = await RunAsync(h, id, locks);

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.StartsWith("kb-1 stays paused: its claim was lost and the board would not confirm it is the factory's again (Kanban's "
            + "renew_work_item_claim was refused (scope_denied): the token was revoked).", outcome.Error);
        Assert.Equal(parked, (await History(h)).Count);
    }

    [Fact]
    public async Task An_ordinary_paused_item_is_resumed_without_asking_the_board_about_its_claim()
    {
        var (h, _, id, locks) = await ImplementingAsync();
        await using (var db = h.Db())
        {
            await new WorkLedger(db, TimeProvider.System).RecordAsync(await db.WorkItems.SingleAsync(), WorkState.Paused, null, RunPipeline.Interrupted, None);
        }

        var outcome = await RunAsync(h, id, locks);

        Assert.Equal(WorkState.Review, outcome.State);
        Assert.DoesNotContain("renew_work_item_claim", h.Board.Writes);
    }

    [Fact]
    public async Task A_lost_claims_comment_the_board_refuses_is_on_record_in_the_parked_row()
    {
        var (h, item, _, locks) = await InFlightAsync();
        h.Board.Time.Now = h.Board.Time.Now.AddHours(25);
        h.Board.ClaimAsSomeoneElse(item);
        h.Board.Instead = (tool, _) => tool == "add_work_item_comment" ? FakeKanbanBoard.Refusal("scope_denied", "the token was revoked") : null;
        var runner = new Runner(h, locks);
        const string reason = "claim lost: the item is claimed by `someone` (marker `their-run`)";
        const string notPosted = "; comment NOT posted: Kanban's add_work_item_comment was refused (scope_denied): the token was revoked";

        await Loop(h.Source(), runner).PollOnceAsync(None);

        // The item is parked and never listed again, so nothing would post the comment later: the ledger says it was not posted.
        var history = await History(h);
        Assert.Equal(new[] { (WorkState.Paused, (string?)null, reason), (WorkState.Paused, "parked", reason + notPosted) },
            history.TakeLast(2).Select(e => (e.State, e.Step, e.Detail!)));
        Assert.Equal([$"paused ({reason}){notPosted}"], runner.Lost);
        Assert.Empty(item.Comments);
        Assert.Empty(await runner.RenewableAsync(None));
    }

    [Fact]
    public async Task A_lost_claim_pauses_nothing_while_the_item_runs_is_being_stopped_or_is_unknown()
    {
        var (h, item, id, locks) = await InFlightAsync();
        h.Board.ClaimAsSomeoneElse(item);
        var runner = new Runner(h, locks);
        var before = (await History(h)).Count;

        // A run of the item is active (its run lock is held): that run goes on, and the next poll finds the claim lost again.
        long itemRow;
        await using (var db = h.Db())
        {
            itemRow = (await db.WorkItems.SingleAsync()).Id;
        }
        await using (await locks.TryAcquireAsync(itemRow, None))
        {
            Assert.Null(await runner.ClaimLostAsync(id, "the item is claimed by someone", None));
        }
        // Being stopped: the stop gives the item back anyway.
        await Controls(h).SetAsync(ControlScope.Item("kb-1"), ControlState.Stopping, "michael", None);
        Assert.Null(await runner.ClaimLostAsync(id, "the item is claimed by someone", None));
        // An item the ledger does not know.
        Assert.Null(await runner.ClaimLostAsync(id + 50, "the item is claimed by someone", None));

        Assert.Equal(before, (await History(h)).Count);
        Assert.Empty(item.Comments);
    }

    [Fact]
    public async Task A_lost_claim_on_an_item_already_paused_parks_it_where_it_is()
    {
        var (h, item, id, locks) = await InFlightAsync();
        await using (var db = h.Db())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            await ledger.RecordAsync(await db.WorkItems.SingleAsync(), WorkState.Paused, null, RunPipeline.Interrupted, None);
        }
        var runner = new Runner(h, locks);
        Assert.Equal([id], await runner.InFlightAsync(None)); // an interrupted pause resumes by itself, so it is in flight
        h.Board.ClaimAsSomeoneElse(item);

        Assert.NotNull(await runner.ClaimLostAsync(id, "the item is claimed by someone", None));

        var history = await History(h);
        Assert.Equal((WorkState.Paused, "parked", "claim lost: the item is claimed by someone"), (history[^1].State, history[^1].Step, history[^1].Detail));
        Assert.Equal(RunPipeline.Interrupted, history[^2].Detail);
        Assert.Empty(await runner.InFlightAsync(None));
        Assert.Contains("(at Review)", Assert.Single(item.Comments).Body);
    }

    private sealed class RenewingSource(ItemNaming naming, Func<int, ClaimRenewal> renew) : IWorkSource
    {
        public List<int> Renewed { get; } = [];
        public ItemNaming Naming => naming;
        public Task<ClaimRenewal> RenewClaimAsync(int id, CancellationToken ct)
        {
            Renewed.Add(id);
            return Task.FromResult(renew(id));
        }
        public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> InScopeAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateScopeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ReleaseAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => throw new NotSupportedException();
        public Task CommentAsync(int id, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ScriptedRunner(IReadOnlyList<int> inFlight, Func<int, string?>? lost = null) : IItemRunner
    {
        public List<int> Runs { get; } = [];
        public List<(int Id, string Reason)> Lost { get; } = [];
        /// <summary>The renewal list when it is not the in-flight list.</summary>
        public IReadOnlyList<int>? Renewable { get; init; }
        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult(inFlight);
        public Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct) => Task.FromResult(Renewable ?? inFlight);
        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Runs.Add(id);
            return Task.FromResult(new RunOutcome(1, WorkState.Review, null, null, null));
        }
        public Task<string?> ClaimLostAsync(int id, string reason, CancellationToken ct)
        {
            Lost.Add((id, reason));
            return Task.FromResult(lost is null ? "paused" : lost(id));
        }
    }

    [Fact]
    public async Task The_poll_renews_every_lanes_items_whatever_its_sources_and_a_board_that_does_not_answer_ends_only_its_own_lanes_renewals()
    {
        // Lane 1: item 2's claim is lost (paused: not run); item 4's is lost but its run is active (nothing paused: left to its
        // run); item 5's renewal is refused for its own reason (the board answered: the next is asked); item 3's renewal throws
        // (the board is down or hanging: each further call could wait out its timeout), so item 6 is not asked this poll — and
        // every item not paused is run all the same. Lane 2: another source, renewed and run whatever happened to lane 1.
        var first = new RenewingSource(ItemNaming.Kanban, id => id switch
        {
            2 or 4 => ClaimRenewal.LostBecause("the item is claimed by someone"),
            5 => ClaimRenewal.NotRenewedBecause("the token was revoked"),
            3 => throw new HttpRequestException("Kanban down"),
            _ => ClaimRenewal.Held,
        });
        var second = new RenewingSource(ItemNaming.Shortcut, _ => ClaimRenewal.Held);
        var (firstRunner, secondRunner) = (new ScriptedRunner([1, 2, 4, 5, 3, 6], id => id == 4 ? null : "paused"), new ScriptedRunner([7]));
        var status = new IntakeStatus(TimeProvider.System);

        await Loop(first, firstRunner, status, [new IntakeLane(second, secondRunner)]).PollOnceAsync(None);

        Assert.Equal([1, 2, 4, 5, 3], first.Renewed);
        Assert.Equal([7], second.Renewed);
        Assert.Equal([(2, "the item is claimed by someone"), (4, "the item is claimed by someone")], firstRunner.Lost);
        Assert.Equal([1, 4, 5, 3, 6], firstRunner.Runs);
        Assert.Equal([7], secondRunner.Runs);
        Assert.Equal("kb-2", Assert.Single(status.ItemErrors).Key);
        Assert.Null(status.FactoryError);
    }

    [Fact]
    public async Task The_renewal_list_runs_nothing_only_the_in_flight_list_does()
    {
        // A runner whose renewal list is longer than its in-flight list (items a control holds): renewed, not run.
        var source = new RenewingSource(ItemNaming.Kanban, _ => ClaimRenewal.Held);
        var runner = new ScriptedRunner([1]) { Renewable = [1, 2, 3] };

        await Loop(source, runner).PollOnceAsync(None);

        Assert.Equal([1, 2, 3], source.Renewed);
        Assert.Equal([1], runner.Runs);
    }

    [Fact]
    public void A_claim_lost_pause_is_a_deferred_step_and_not_one_that_resumes_by_itself()
    {
        Assert.Equal("claim lost", RunPipeline.ClaimLost);
        foreach (var from in new WorkState?[] { WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate })
        {
            Assert.Equal(StepOutcome.Deferred, StepOutcomes.Of(from, WorkState.Paused, null, "claim lost: the item is claimed by someone"));
        }
        Assert.Equal(StepOutcome.Deferred, StepOutcomes.Of(null, WorkState.Paused, RunPipeline.Steps.Parked, "claim lost: the item is claimed by someone"));
        Assert.False(RunPipeline.IsClaimLost(RunPipeline.UserPaused));
        Assert.False(RunPipeline.IsClaimLost("claim refused: the item is claimed by someone"));
    }
}
