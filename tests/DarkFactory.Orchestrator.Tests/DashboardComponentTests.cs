using Bunit;
using Bunit.TestDoubles;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Dashboard.Components;
using DarkFactory.Orchestrator.Dashboard.Components.Pages;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.AspNetCore.Builder;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests;

internal static class TranscriptLines
{
    public const string Sid = "sess-transcript";

    public static readonly string BigOutput = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));

    public static readonly string[] All =
    [
        $$$"""{"type":"system","subtype":"init","model":"claude-opus-5","cwd":"/work/wt","session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"I'll run the tests."}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test","description":"Run tests"}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":{{{System.Text.Json.JsonSerializer.Serialize(BigOutput)}}}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"README.md"}}]},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"no such file"}],"is_error":true}]},"session_id":"{{{Sid}}}"}""",
        "Warning: not json at all",
        $$$"""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"},"session_id":"{{{Sid}}}"}""",
        $$$"""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"duration_ms":4569,"result":"All green.","session_id":"{{{Sid}}}"}""",
    ];

    public static SessionEventMessage Event(int i) =>
        new(i + 1, "x", null, All[i], DateTimeOffset.UnixEpoch);
}

public class TranscriptFormatterTests
{
    [Fact]
    public void Messages_tool_calls_commands_outputs_results_and_raw_lines_are_readable()
    {
        var formatter = new TranscriptFormatter();
        var entries = Enumerable.Range(0, TranscriptLines.All.Length).SelectMany(i => formatter.Format(TranscriptLines.Event(i))).ToList();

        Assert.Equal(
            [TranscriptKind.System, TranscriptKind.Text, TranscriptKind.Command, TranscriptKind.ToolResult, TranscriptKind.ToolCall,
             TranscriptKind.ToolResult, TranscriptKind.Raw, TranscriptKind.Raw, TranscriptKind.Result],
            entries.Select(e => e.Kind));
        Assert.Contains("model: claude-opus-5", entries[0].Body);
        Assert.Equal("I'll run the tests.", entries[1].Body);
        Assert.Equal(("Bash — Run tests", "dotnet test"), (entries[2].Title, entries[2].Body));
        Assert.Equal(("output", TranscriptLines.BigOutput), (entries[3].Title, entries[3].Body));
        Assert.Equal("Read", entries[4].Title);
        Assert.Contains("\"file_path\": \"README.md\"", entries[4].Body);
        Assert.Equal(("Read result", "no such file", true), (entries[5].Title, entries[5].Body, entries[5].IsError));
        Assert.Equal("Warning: not json at all", entries[6].Body);           // raw lines as-is
        Assert.Equal(TranscriptLines.All[7], entries[7].Body);                // unknown events as-is
        Assert.Equal("result: success", entries[8].Title);
        Assert.StartsWith("All green.", entries[8].Body);
    }

    [Fact]
    public void The_recorded_fixture_formats_without_losing_its_tool_call_or_result()
    {
        var formatter = new TranscriptFormatter();
        var entries = StreamJsonFixture.Fresh.Select((line, i) => new SessionEventMessage(i + 1, "x", null, line, DateTimeOffset.UnixEpoch))
            .SelectMany(formatter.Format).ToList();

        Assert.Contains(entries, e => e is { Kind: TranscriptKind.ToolCall, Title: "Glob" } && e.Body.Contains("\"pattern\": \"*\""));
        Assert.Contains(entries, e => e is { Kind: TranscriptKind.ToolResult, Title: "Glob result", Body: "No files found" });
        Assert.Contains(entries, e => e.Kind == TranscriptKind.Result && e.Body.StartsWith("The directory is empty"));
        Assert.DoesNotContain(entries, e => e.Kind == TranscriptKind.Thinking); // the fixture's thinking is redacted
    }
}

/// <summary>bUnit tests of the pipeline and session pages against fakes.</summary>
public class DashboardComponentTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeData _data = new();
    private readonly PipelineChanges _changes = new();
    private readonly FakeViewers _viewers = new();

    public DashboardComponentTests()
    {
        Services.AddSingleton<IDashboardData>(_data);
        Services.AddSingleton(_changes);
        Services.AddSingleton<ISessionViewers>(_viewers);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton(_intake);
    }

    private readonly IntakeStatus _intake = new(new FakeTimeProvider(Now));

    [Fact]
    public void Intake_errors_show_as_a_factory_banner_and_per_item_lines_as_they_happen()
    {
        _data.Rows = [];
        var cut = Render<Pipeline>();
        Assert.Empty(cut.FindAll(".factory-error"));
        Assert.Empty(cut.FindAll(".item-errors"));

        _intake.FactoryFailed("sc-5 could not run: the worker run lock: Another factory run is using /w");
        _intake.ItemFailed("sc-101", "InvalidOperationException: Shortcut GET stories/101 failed: 404 Not Found");
        _intake.ItemFailed("sc-101", "InvalidOperationException: Shortcut GET stories/101 failed: 404 Not Found");
        _intake.ItemGaveUp("sc-101", "escalated");

        cut.WaitForAssertion(() => Assert.Contains("Another factory run", cut.Find(".factory-error").TextContent));
        var line = cut.Find(".item-errors li[data-story='sc-101']").TextContent;
        Assert.Contains("sc-101", line);
        Assert.Contains("failed 2", line);
        Assert.Contains("404 Not Found", line);
        Assert.Contains("escalated", line);

        _intake.FactoryOk();
        _intake.ItemOk("sc-101");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".factory-error")));
        Assert.Empty(cut.FindAll(".item-errors"));
    }

    [Fact]
    public void The_pipeline_shows_the_metrics_with_unmeasured_ones_as_not_available()
    {
        _data.Rows = [];
        _data.Metrics = LedgerMetrics.Compute([], MetricsOptions.Default);

        var cut = Render<Pipeline>();

        var rows = cut.FindAll("table.metrics tbody tr");
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal("N/A", r.QuerySelector(".value")!.TextContent));
        Assert.Contains("14-day revert rate", cut.Find("tr[data-metric='14-day revert rate']").TextContent);
        Assert.Contains("sandbox and demo items are left out", cut.Markup);
    }

    [Fact]
    public void A_metrics_failure_marks_only_the_metrics_stale_and_the_items_still_show()
    {
        _data.Rows = [Row(1, WorkState.Review)];
        _data.MetricsFails = new InvalidOperationException("metrics query timed out");

        var cut = Render<Pipeline>();

        Assert.Single(cut.FindAll("tr[data-item='1']"));
        Assert.Empty(cut.FindAll("p.error:not(.metrics-error)"));
        Assert.Contains("Could not compute the metrics", cut.Find(".metrics-error").TextContent);
    }

    [Fact]
    public void A_failed_closeout_shows_on_its_item_with_whether_it_is_retried()
    {
        _data.Rows =
        [
            Row(1, WorkState.Watch) with { FailedCloseout = new CloseoutStatus(1, false, "failed: Shortcut down") },
            Row(2, WorkState.Watch) with { FailedCloseout = new CloseoutStatus(RunPipeline.MaxCloseoutAttempts, false, "failed: 413 too large") },
            Row(3, WorkState.Watch),
        ];

        var cut = Render<Pipeline>();

        var retrying = cut.Find("tr[data-item='1'] .closeout-failed").TextContent;
        Assert.Contains("failed: Shortcut down", retrying);
        Assert.Contains("retried on the next poll", retrying);
        var given = cut.Find("tr[data-item='2'] .closeout-failed").TextContent;
        Assert.Contains("failed: 413 too large", given);
        Assert.Contains("post it by hand", given);
        Assert.Empty(cut.FindAll("tr[data-item='3'] .closeout-failed"));
    }

    private static PipelineRow Row(long id, WorkState state, string? pr = null, decimal? cost = null, params SessionLink[] sessions) =>
        new(id, $"sc-{id}", $"Story {id}", "acme/widgets", state, Now.AddMinutes(-65), Now, pr, cost, sessions);

    [Fact]
    public void Pipeline_lists_a_kanban_item_with_its_id_a_link_to_it_its_epic_and_controls_on_the_kanban_scopes()
    {
        const string board = "https://kanban.test/?project=7f0c5a52-3b0e-4b6f-9a51-0d8a8a1f2c11";
        _data.Rows =
        [
            // Kanban item 4 in the Kanban epic with ledger key 5, and a Shortcut story in Shortcut epic 5: the same number.
            new(1, "kb-4", "Count words", "acme/widgets", WorkState.Implement, Now.AddMinutes(-5), Now, null, null, [], EpicId: 5,
                Links: new BoardLink($"{board}&epic=e1&item=i1", $"{board}&epic=e1")),
            Row(77, WorkState.Implement) with { EpicId = 5 },
            // A Kanban item in no epic, and one whose project the ledger has not recorded yet (no link to build).
            new(3, "kb-6", "Split words", "acme/widgets", WorkState.Review, Now, Now, null, null, [], Links: new BoardLink($"{board}&item=i3", null)),
            new(4, "kb-7", "Old", "acme/widgets", WorkState.Review, Now, Now, null, null, [], EpicId: 5, Links: new BoardLink(null, null)),
        ];
        _data.Controls = [new Control { Scope = "epic:kb-5", State = ControlState.Paused, ChangedBy = "michael" }];

        var cut = Render<Pipeline>();

        var row = cut.Find("tr[data-item='1']");
        var id = row.QuerySelector("a.id")!;
        Assert.Equal(("kb-4", $"{board}&epic=e1&item=i1", "noopener noreferrer"), (id.TextContent, id.GetAttribute("href"), id.GetAttribute("rel")));
        Assert.Contains("Count words", row.TextContent);
        var epic = row.QuerySelector(".epic")!;
        Assert.Equal(("kb-5", "kb-5", $"{board}&epic=e1"),
            (epic.GetAttribute("data-epic"), epic.QuerySelector("a")!.TextContent, epic.QuerySelector("a")!.GetAttribute("href")));
        // Its own controls post the item's scope.
        Assert.Equal(["item:kb-4", "item:kb-4"], row.QuerySelectorAll("td.control form").Select(f => f.GetAttribute("data-scope")));
        Assert.Equal(["pause", "stop"], row.QuerySelectorAll("td.control form").Select(f => f.GetAttribute("data-action")));

        // Two epic control rows, one per source: the Kanban epic's posts `epic:kb-5` (paused: Continue), the Shortcut epic's `epic:5`.
        Assert.Equal(["factory", "repo:acme/widgets", "epic:5", "epic:kb-5"], cut.FindAll("div.controls").Select(d => d.GetAttribute("data-scope")));
        var kanban = cut.Find("div.controls[data-scope='epic:kb-5']");
        Assert.Contains("Epic kb-5: paused", kanban.TextContent);
        Assert.Equal([("continue", "epic:kb-5"), ("stop", "epic:kb-5")],
            kanban.QuerySelectorAll("form").Select(f => (f.GetAttribute("data-action"), f.GetAttribute("data-scope"))));
        Assert.Equal(["epic:kb-5", "epic:kb-5"], kanban.QuerySelectorAll("input[name='scope']").Select(i => i.GetAttribute("value")));
        var shortcut = cut.Find("div.controls[data-scope='epic:5']");
        Assert.Contains("Epic 5: running", shortcut.TextContent);
        Assert.Equal([("pause", "epic:5"), ("stop", "epic:5")],
            shortcut.QuerySelectorAll("form").Select(f => (f.GetAttribute("data-action"), f.GetAttribute("data-scope"))));

        // A Shortcut story's row is what it was: its id as text, no link and no epic beside it.
        var story = cut.Find("tr[data-item='77']");
        Assert.Equal("sc-77", story.QuerySelector("span.id")!.TextContent);
        Assert.Empty(story.QuerySelectorAll("a.id, .epic"));
        // No epic: none shown. No project in the ledger yet: the id as text, the epic without a link.
        Assert.NotNull(cut.Find("tr[data-item='3'] a.id"));
        Assert.Empty(cut.FindAll("tr[data-item='3'] .epic"));
        Assert.Equal("kb-7", cut.Find("tr[data-item='4'] span.id").TextContent);
        Assert.Equal("epic kb-5", System.Text.RegularExpressions.Regex.Replace(cut.Find("tr[data-item='4'] .epic").TextContent.Trim(), @"\s+", " "));
        Assert.Empty(cut.FindAll("tr[data-item='4'] .epic a"));
    }

    [Fact]
    public void Pipeline_lists_each_active_item_with_state_repo_pr_elapsed_cost_and_session_links()
    {
        _data.Rows = [Row(1, WorkState.Review, "https://github.com/acme/widgets/pull/9", 1.5m,
            new SessionLink("s-1", 1, Now, Now, "succeeded", 1.5m), new SessionLink("s-2", 2, Now, null, null, null))];

        var cut = Render<Pipeline>();

        var cells = cut.FindAll("tr[data-item='1'] td").Select(td => td.TextContent.Trim()).ToList();
        // s-2 is running and its cost not measured yet: the sum says it covers one of the two sessions (E5).
        // The Run cell (sc-25991) is empty for a row with no run view; the others are where and what they were.
        Assert.Equal(["sc-1 Story 1", "Review", "—", "acme/widgets", "PR", "1h 05m", "$1.50 of 1/2 measured"], cells.Take(7));
        Assert.Equal(["Item", "State", "Run", "Repo", "PR", "Elapsed", "Cost", "Sessions", "Control"], cut.FindAll("table.pipeline th").Select(th => th.TextContent));
        Assert.Equal("https://github.com/acme/widgets/pull/9", cut.Find("td.pr a").GetAttribute("href"));
        Assert.Equal(["sessions/s-1", "sessions/s-2"], cut.FindAll("td.sessions a").Select(a => a.GetAttribute("href")));
        Assert.Equal("#2 (live)", cut.FindAll("td.sessions a")[1].TextContent);
    }

    private static string Words(AngleSharp.Dom.IElement element) => string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Two_running_items_each_show_their_phase_and_their_slot_or_that_they_wait_for_one()
    {
        // sc-25991: two runs in flight at once (Intake:MaxConcurrentRuns 2) — one in a worker phase in slot 2, one waiting for a
        // free slot; a third item in the merge queue with no run now; a fourth whose run died holding its slot.
        var live = new SessionLink("s-1", 1, Now, null, null, null);
        _data.Rows =
        [
            Row(1, WorkState.Implement, null, null, live) with { Run = new RunView(true, "implementing", new SlotUse(SlotUseKind.Holding, 2, "_factory2", "implement"), 2, "_factory2") },
            Row(2, WorkState.Implement) with { Run = new RunView(true, "implementing", new SlotUse(SlotUseKind.Waiting, null, null, "implement")) },
            Row(3, WorkState.MergeGate) with { Run = new RunView(false, "merge queue: waiting for its turn", new SlotUse(SlotUseKind.None, 1, "_factory", "run the new tests"), 1, "_factory") },
            Row(4, WorkState.Fixing) with { Run = new RunView(false, "fix round", new SlotUse(SlotUseKind.Holding, 1, "_factory", "run the review fix"), 1, "_factory") },
            Row(5, WorkState.Review) with { Run = new RunView(null, "review", SlotUse.None) },
        ];

        var cut = Render<Pipeline>();

        Assert.Equal("running: implementing · holds slot 2 (_factory2) to implement", Words(cut.Find("tr[data-item='1'] td.run")));
        Assert.Equal(("true", "held"), (cut.Find("tr[data-item='1'] td.run").GetAttribute("data-live"), cut.Find("tr[data-item='1'] .slot").GetAttribute("data-slot")));
        Assert.Equal("running: implementing · waiting for a slot to implement", Words(cut.Find("tr[data-item='2'] td.run")));
        Assert.Equal(("true", "waiting"), (cut.Find("tr[data-item='2'] td.run").GetAttribute("data-live"), cut.Find("tr[data-item='2'] .slot").GetAttribute("data-slot")));
        // Both at once: two rows say "running".
        Assert.Equal(2, cut.FindAll("td.run[data-live='true']").Count);
        Assert.Equal("no run now: merge queue: waiting for its turn · slot 1 (_factory), not held", Words(cut.Find("tr[data-item='3'] td.run")));
        Assert.Equal("idle", cut.Find("tr[data-item='3'] .slot").GetAttribute("data-slot"));
        // A dead run's last word is not shown as a hold.
        var stale = cut.Find("tr[data-item='4'] .slot");
        Assert.Equal("stale", stale.GetAttribute("data-slot"));
        Assert.Contains("error", stale.ClassList);
        Assert.StartsWith("no run now: the last one ended holding slot 1 (_factory) without recording it", Words(stale));
        // Liveness not known (a reader with no lock reader): the phase alone, nothing claimed either way.
        Assert.Equal("review", Words(cut.Find("tr[data-item='5'] td.run")));
        Assert.Null(cut.Find("tr[data-item='5'] td.run").GetAttribute("data-live"));
    }

    [Fact]
    public void Each_deferral_is_listed_with_what_lifts_it_and_one_deferred_run_reads_as_before()
    {
        _data.Rows = [];
        var cut = Render<Pipeline>();
        Assert.Empty(cut.FindAll(".deferred, .deferrals"));

        // One run deferred: the line it always was.
        _intake.Deferred("sc-2", "factory frozen (main-red): main is red; `factory continue --freeze` clears it");
        cut.WaitForAssertion(() => Assert.Equal(
            $"Deferred (last at {Now:u}): sc-2 deferred: factory frozen (main-red): main is red; `factory continue --freeze` clears it", Words(cut.Find("p.deferred"))));
        Assert.Empty(cut.FindAll(".deferrals"));

        // A second run of the poll deferred, and a factory-wide control the loop found in effect: each on its own line.
        _intake.Deferred("sc-3", "factory frozen (main-red): main is red; `factory continue --freeze` clears it");
        _intake.Holding([(ControlScope.Usage, "the factory is paused for usage (worker-rate-limited)", "it lifts by itself at 2026-10-06 13:00:00Z, or with `factory continue --usage`")]);
        cut.WaitForAssertion(() => Assert.Equal([ControlScope.Usage, "sc-2", "sc-3"], cut.FindAll(".deferrals li").Select(li => li.GetAttribute("data-deferral"))));
        Assert.Empty(cut.FindAll("p.deferred"));
        Assert.Equal($"Deferred (last at {Now:u}): new work held: the factory is paused for usage (worker-rate-limited); it lifts by itself at 2026-10-06 13:00:00Z, or with `factory continue --usage`",
            Words(cut.Find(".deferrals li.hold")));
        Assert.All(cut.FindAll(".deferrals li.deferred"), li => Assert.Contains("factory continue --freeze", li.TextContent));

        _intake.NotDeferred();
        _intake.Holding([]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".deferred, .deferrals")));
    }

    /// <summary>The plan row's note about its hold comment, as the page renders it (whitespace collapsed).</summary>
    private List<string> RenderPlanNotice(PlanView plan)
    {
        _data.Rows = [Row(500, WorkState.Planned) with { Plan = plan }];
        using var cut = Render<Pipeline>();
        return [.. cut.FindAll("tr[data-item='500'] .plan-notice").Select(n => string.Join(' ', n.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))];
    }

    [Fact]
    public void A_plan_row_shows_its_counts_and_hold_with_its_stories_beneath_it_each_with_its_state_and_what_it_waits_on()
    {
        // sc-25876: sc-501 in flight, sc-502 escalated, sc-503 (not released: no work item) blocked behind it, sc-504 merged.
        var status = new PlanStatus([
            new PlanStoryStatus("sc-501", PlanStoryState.InFlight, [], [], null, WorkState.Review),
            new PlanStoryStatus("sc-502", PlanStoryState.Failed, [], [], new StoryFailure(PlanRelease.AsEscalated, "CI is red", Now), WorkState.Escalated),
            new PlanStoryStatus("sc-503", PlanStoryState.Blocked, ["sc-502"], ["sc-502"], null, null),
            new PlanStoryStatus("sc-504", PlanStoryState.Merged, [], [], null, WorkState.Done),
        ]);
        string[] titles = ["Story 501", "Story 502", "Third <story>", "Story 504"];
        var plan = new PlanView([.. status.Stories.Select((s, i) => new PlanStoryView(s.Id, titles[i], s.State, s.ItemState, PlanStoryView.WaitsOf(s, status)))],
            status, PlanRelease.HeldByFailed(status));
        Assert.Empty(RenderPlanNotice(plan)); // its hold comment was posted (or nothing is held): nothing said
        Assert.Equal("hold comment not posted (attempt 1 of 3; tried again on the next poll)", Assert.Single(RenderPlanNotice(plan with { FailedNotices = 1 })));
        Assert.Equal("hold comment not posted (attempt 3 of 3; not tried again: this page and the ledger have it)",
            Assert.Single(RenderPlanNotice(plan with { FailedNotices = RunPipeline.MaxPlanNoticeAttempts })));
        _data.Rows =
        [
            Row(500, WorkState.Planned) with { Plan = plan },
            Row(501, WorkState.Review, "https://github.com/acme/widgets/pull/9") with { PlanItemId = 500 },
            Row(502, WorkState.Escalated) with { PlanItemId = 500 },
            Row(7, WorkState.Implement),
        ];

        var cut = Render<Pipeline>();

        // The plan row, then its stories in plan order (with or without a work item of their own), then the item that is no plan's.
        var rows = cut.FindAll("table.pipeline tbody tr");
        Assert.Equal(["500", "501", "502", null, null, "7"], rows.Select(r => r.GetAttribute("data-item")));
        Assert.Equal([null, "sc-501", "sc-502", "sc-503", "sc-504", null], rows.Select(r => r.GetAttribute("data-story")));
        Assert.All(rows.Skip(1).Take(4), r => Assert.Equal(("plan-story", "500"), (r.ClassName, r.GetAttribute("data-plan"))));
        Assert.Single(cut.FindAll("tr[data-item='501']")); // a story is not listed a second time as a row of its own
        var planRow = cut.Find("tr[data-item='500']");
        Assert.Equal("1 of 4 merged · 1 in flight · 1 waiting · 1 failed", planRow.QuerySelector(".plan-counts")!.TextContent);
        Assert.Equal("held", planRow.QuerySelector(".plan-flag")!.TextContent);
        Assert.Contains("sc-502 escalated at 2026-10-06 12:00:00Z: `CI is red`; not released because of them: sc-503 (behind sc-502)",
            planRow.QuerySelector(".plan-hold")!.TextContent);
        // Each story: its state and what it waits on; a story with a work item keeps its pull request and its own controls.
        Assert.Equal("", cut.Find("tr[data-story='sc-501'] td.state").TextContent.Trim().Replace("Review", ""));
        Assert.Equal("failed: escalated", cut.Find("tr[data-story='sc-502'] .waits").TextContent);
        Assert.NotNull(cut.Find("tr[data-story='sc-501'] td.pr a"));
        var unreleased = cut.Find("tr[data-story='sc-503']");
        Assert.Contains("sc-503 Third <story>", unreleased.TextContent);
        Assert.DoesNotContain("<story>", unreleased.InnerHtml); // rendered as text, never as markup
        Assert.StartsWith("not released", unreleased.QuerySelector("td.state")!.TextContent.Trim());
        Assert.Equal("held by sc-502 (escalated)", unreleased.QuerySelector(".waits")!.TextContent);
        Assert.StartsWith("Done", cut.Find("tr[data-story='sc-504'] td.state").TextContent.Trim());
        // The control form on the plan row posts the plan's own item scope: the control reaches its stories from there.
        Assert.Equal(["item:sc-500", "item:sc-500"], planRow.QuerySelectorAll("td.control form").Select(f => f.GetAttribute("data-scope")));
        Assert.Equal(["pause", "stop"], planRow.QuerySelectorAll("td.control form").Select(f => f.GetAttribute("data-action")));
        Assert.Equal("item:sc-501", cut.Find("tr[data-story='sc-501'] td.control form").GetAttribute("data-scope"));

        // Stuck, and being stopped: said on the plan row, and a stopping story offers no control.
        var stuck = new PlanStatus([status.Stories[1], status.Stories[2], status.Stories[3]]);
        _data.Rows =
        [
            Row(500, WorkState.Planned) with { Control = ControlState.Stopping, Plan = new PlanView(
                [.. stuck.Stories.Select(s => new PlanStoryView(s.Id, s.Id, s.State, s.ItemState, PlanStoryView.WaitsOf(s, stuck)))], stuck, PlanRelease.HeldByFailed(stuck)) },
            Row(502, WorkState.Escalated) with { PlanItemId = 500, Control = ControlState.Stopping },
        ];
        _changes.Notify(500);
        cut.WaitForAssertion(() => Assert.Equal(["stopping", "stuck"], cut.FindAll("tr[data-item='500'] .plan-flag").Select(f => f.TextContent)));
        Assert.Empty(cut.FindAll("tr[data-item='502'] td.control form"));
    }

    [Fact]
    public void A_finished_plan_row_groups_its_listed_stories_says_it_is_finished_and_offers_no_control()
    {
        // sc-25877: the plan closed (Done); its merged stories are still in Watch and are listed beneath it, not on their own.
        var status = new PlanStatus([
            new PlanStoryStatus("sc-501", PlanStoryState.Merged, [], [], null, WorkState.Watch),
            new PlanStoryStatus("sc-502", PlanStoryState.Merged, [], [], null, WorkState.Watch, MergedOutside: true),
        ]);
        var plan = new PlanView([.. status.Stories.Select(s => new PlanStoryView(s.Id, s.Id, s.State, s.ItemState, PlanStoryView.WaitsOf(s, status)))], status, null);
        _data.Rows =
        [
            Row(500, WorkState.Done) with { Plan = plan },
            Row(501, WorkState.Watch) with { PlanItemId = 500 },
            Row(502, WorkState.Escalated) with { PlanItemId = 500 },
        ];

        var cut = Render<Pipeline>();

        var rows = cut.FindAll("table.pipeline tbody tr");
        Assert.Equal(["500", "501", "502"], rows.Select(r => r.GetAttribute("data-item")));
        Assert.Equal([null, "500", "500"], rows.Select(r => r.GetAttribute("data-plan")));
        var planRow = cut.Find("tr[data-item='500']");
        Assert.StartsWith("Done", planRow.QuerySelector("td.state")!.TextContent.Trim());
        Assert.Equal("2 of 2 merged · 0 in flight · 0 waiting · 0 failed", planRow.QuerySelector(".plan-counts")!.TextContent);
        Assert.Empty(planRow.QuerySelectorAll("td.control form")); // nothing of a finished item can be paused or stopped
        Assert.NotEmpty(cut.FindAll("tr[data-item='501'] td.control form")); // its story keeps its own
        // A story counted as merged though the factory's gate did not merge it says so.
        Assert.Equal("merged outside the factory into the changeset branch; reviewed as part of the changeset pull request",
            cut.Find("tr[data-story='sc-502'] .waits").TextContent);
    }

    [Fact]
    public void A_running_session_silent_for_the_quiet_threshold_is_marked_quiet_and_unmarked_once_it_speaks_again()
    {
        var time = (FakeTimeProvider)Services.GetRequiredService<TimeProvider>();
        var live = new SessionLink("s-2", 2, Now.AddMinutes(-30), null, null, null, LastEventAt: Now.AddMinutes(-9));
        _data.Rows = [Row(1, WorkState.Implement, sessions: live)];

        var cut = Render<Pipeline>();
        Assert.Equal("#2 (live)", cut.Find("td.sessions a").TextContent);

        // Two quiet minutes later (the page re-reads a running session's last event on its tick; stream events are not item changes).
        time.Advance(TimeSpan.FromMinutes(2));
        cut.WaitForAssertion(() => Assert.Equal("#2 (live, quiet)", cut.Find("td.sessions a").TextContent));
        Assert.Equal("quiet", cut.Find("td.sessions a").GetAttribute("class"));
        Assert.Equal("no event for 11m 00s", cut.Find("td.sessions a").GetAttribute("title"));

        // It speaks again: the next tick's read shows it.
        _data.Rows = [Row(1, WorkState.Implement, sessions: live with { LastEventAt = time.GetUtcNow() })];
        time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal("#2 (live)", cut.Find("td.sessions a").TextContent));

        // A finished session is never quiet.
        _data.Rows = [Row(1, WorkState.Review, sessions: live with { EndedAt = Now, ExitStatus = "stuck", LastEventAt = null })];
        _changes.Notify(1);
        cut.WaitForAssertion(() => Assert.Equal("#2", cut.Find("td.sessions a").TextContent));
    }

    [Fact]
    public void Worker_QuietMinutes_sets_when_a_session_counts_as_quiet()
    {
        _data.Quiet = TimeSpan.FromMinutes(20);
        _data.Rows = [Row(1, WorkState.Implement, sessions: new SessionLink("s-2", 2, Now.AddMinutes(-30), null, null, null, LastEventAt: Now.AddMinutes(-15)))];

        Assert.Equal("#2 (live)", Render<Pipeline>().Find("td.sessions a").TextContent);

        _data.Quiet = TimeSpan.FromMinutes(10);
        Assert.Equal("#2 (live, quiet)", Render<Pipeline>().Find("td.sessions a").TextContent);
    }

    [Fact]
    public void The_session_page_marks_a_silent_running_session_quiet_until_an_event_arrives()
    {
        var time = (FakeTimeProvider)Services.GetRequiredService<TimeProvider>();
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets",
            new SessionLink(TranscriptLines.Sid, 1, Now.AddMinutes(-40), null, null, null, LastEventAt: Now.AddMinutes(-12)));
        _viewers.Backlog = [TranscriptLines.Event(0)]; // stored long ago (ReceivedAt is the epoch)

        var cut = Render<Session>(p => p.Add(x => x.SessionId, TranscriptLines.Sid));
        cut.WaitForAssertion(() => Assert.Equal("quiet: no event for 12m 00s", cut.Find(".quiet").TextContent));
        Assert.Contains("running (live)", cut.Find(".status").TextContent);
        Assert.EndsWith("cost N/A", cut.Find(".meta").TextContent.Trim()); // not measured yet: N/A, never a dash or $0 (E5)

        _viewers.Send([new SessionEventMessage(2, "assistant", "text", TranscriptLines.All[1], time.GetUtcNow())]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".quiet")));

        // Silent again past the threshold: the page's tick marks it without any new read.
        time.Advance(TimeSpan.FromMinutes(10));
        cut.WaitForAssertion(() => Assert.Equal("quiet: no event for 10m 00s", cut.Find(".quiet").TextContent));
    }

    [Fact]
    public void A_zero_cost_shows_as_zero_dollars_an_unmeasured_one_as_n_a_and_a_partial_sum_says_what_it_covers()
    {
        // A session served on the router's local model costs $0, which is known spend, not missing spend (E5).
        _data.Rows =
        [
            Row(1, WorkState.Review, null, 0m, new SessionLink("s-1", 1, Now, Now, "succeeded", 0m)),
            Row(2, WorkState.Review, null, null, new SessionLink("s-2", 1, Now, Now, "failed", null)),
            Row(3, WorkState.Review, null, 0.4m, new SessionLink("s-3", 1, Now, Now, "succeeded", 0.4m), new SessionLink("s-4", 2, Now, Now, "failed", null),
                new SessionLink("s-5", 3, Now, Now, "succeeded", null)),
        ];

        var cut = Render<Pipeline>();

        Assert.Equal("$0.00", cut.Find("tr[data-item='1'] td.cost").TextContent);
        Assert.Equal("N/A", cut.Find("tr[data-item='2'] td.cost").TextContent);
        Assert.Equal("$0.40 of 1/3 measured", cut.Find("tr[data-item='3'] td.cost").TextContent);
    }

    [Fact]
    public void The_usage_pause_shows_as_a_banner_with_its_reason_and_resume_time_until_it_lifts()
    {
        var time = (FakeTimeProvider)Services.GetRequiredService<TimeProvider>();
        _data.Rows = [Row(1, WorkState.Paused) with { Control = ControlState.Paused }];
        _data.Controls = [new Control
        {
            Scope = ControlScope.Usage, State = ControlState.Paused, ChangedBy = "usage", ChangedAt = Now,
            Reason = "usage-exhausted", ResumeAt = Now.AddMinutes(30),
        }];

        var cut = Render<Pipeline>();

        var banner = cut.Find($".usage-pause[data-scope='{ControlScope.Usage}']");
        Assert.Equal("usage-exhausted", banner.QuerySelector(".usage-reason")!.TextContent);
        Assert.Equal("2026-10-06 12:30:00Z", banner.QuerySelector(".usage-resume")!.TextContent);
        Assert.Equal(ControlScope.Usage, banner.QuerySelector("form[data-action='continue']")!.GetAttribute("data-scope"));
        Assert.Equal("paused", cut.Find("tr[data-item='1'] .control-state").TextContent);

        time.Advance(TimeSpan.FromMinutes(30));
        _changes.Notify(null);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".usage-pause")));
    }

    [Fact]
    public void The_freeze_shows_as_a_banner_with_its_trigger_and_reason_and_a_continue_until_a_human_clears_it()
    {
        _data.Rows = [Row(1, WorkState.Paused) with { Control = ControlState.Paused }];
        _data.Controls = [new Control
        {
            Scope = ControlScope.Freeze, State = ControlState.Paused, ChangedBy = FreezeTrigger.By, ChangedAt = Now,
            Reason = FreezeTrigger.MainRed, Detail = "main of michaeltrefry/dark-factory-sandbox is red",
        }];
        _intake.Deferred("sc-2", "factory frozen (main-red): main of michaeltrefry/dark-factory-sandbox is red");

        var cut = Render<Pipeline>();

        var banner = cut.Find($".freeze[data-scope='{ControlScope.Freeze}']");
        Assert.Equal(FreezeTrigger.MainRed, banner.QuerySelector(".freeze-trigger")!.TextContent);
        Assert.Equal("main of michaeltrefry/dark-factory-sandbox is red", banner.QuerySelector(".freeze-detail")!.TextContent);
        Assert.Equal(ControlScope.Freeze, banner.QuerySelector("form[data-action='continue']")!.GetAttribute("data-scope"));
        Assert.Contains("sc-2 deferred: factory frozen (main-red)", cut.Find(".deferred").TextContent);

        _data.Controls = [new Control { Scope = ControlScope.Freeze, State = ControlState.Running, ChangedBy = "dashboard", ChangedAt = Now }];
        _changes.Notify(null);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".freeze")));
    }

    [Fact]
    public void A_pipeline_row_changes_state_when_its_ledger_changes_without_a_reload()
    {
        _data.Rows = [Row(1, WorkState.Intake), Row(2, WorkState.Implement)];
        var cut = Render<Pipeline>();
        Assert.Equal("Intake", cut.Find("tr[data-item='1'] td.state").TextContent);

        _data.Rows = [Row(1, WorkState.Implement), Row(2, WorkState.Implement)];
        _changes.Notify(1);

        cut.WaitForAssertion(() => Assert.Equal("Implement", cut.Find("tr[data-item='1'] td.state").TextContent));

        _data.Rows = [Row(2, WorkState.Implement)]; // item 1 is Done: it leaves the view
        _changes.Notify(null);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr")));
    }

    [Fact]
    public async Task Pipeline_unsubscribes_when_closed()
    {
        _data.Rows = [];
        var cut = Render<Pipeline>();
        Assert.Contains("No active items.", cut.Markup);

        await DisposeComponentsAsync();
        var reads = _data.Reads;
        _changes.Notify(null);

        Assert.Equal(reads, _data.Reads);
    }

    [Fact]
    public void Session_view_renders_the_transcript_readably_with_large_outputs_collapsed()
    {
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, Now, "succeeded", 0.25m));
        _viewers.Backlog = Enumerable.Range(0, TranscriptLines.All.Length).Select(TranscriptLines.Event).ToList();

        var cut = Render<Session>(p => p.Add(x => x.SessionId, TranscriptLines.Sid));

        cut.WaitForAssertion(() => Assert.Equal(9, cut.FindAll("li.entry").Count));
        Assert.Contains("finished: succeeded", cut.Find(".status").TextContent);
        Assert.Contains("$0.25", cut.Find(".meta").TextContent);
        var command = cut.Find("li.entry.Command");
        Assert.Equal("Bash — Run tests", command.QuerySelector(".title")!.TextContent);
        Assert.Equal("dotnet test", command.QuerySelector("pre")!.TextContent);
        var output = cut.FindAll("li.entry.ToolResult")[0];
        Assert.NotNull(output.QuerySelector("details"));          // 40 lines: collapsed
        Assert.Contains("line 1", output.QuerySelector("summary")!.TextContent);
        Assert.Equal(TranscriptLines.BigOutput, output.QuerySelector("details pre")!.TextContent);
        Assert.Contains("is-error", cut.FindAll("li.entry.ToolResult")[1].ClassName);
        Assert.Null(cut.FindAll("li.entry.ToolResult")[1].QuerySelector("details")); // short: open
        Assert.Contains(cut.FindAll("li.entry.Raw"), li => li.QuerySelector("pre")!.TextContent == "Warning: not json at all");
    }

    [Fact]
    public async Task Session_view_appends_live_events_and_leaves_the_feed_when_closed()
    {
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, null, null, null));
        _viewers.Backlog = [TranscriptLines.Event(0), TranscriptLines.Event(1)];

        var cut = Render<Session>(p => p.Add(x => x.SessionId, TranscriptLines.Sid));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li.entry").Count));
        Assert.Contains("running (live)", cut.Find(".status").TextContent);

        _viewers.Send([TranscriptLines.Event(2)]);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("li.entry").Count));
        Assert.Equal("dotnet test", cut.Find("li.entry.Command pre").TextContent);

        // The session ends: the header follows the item's change notification.
        _data.Header = _data.Header with { Session = _data.Header.Session with { EndedAt = Now, ExitStatus = "succeeded" } };
        _changes.Notify(1);
        cut.WaitForAssertion(() => Assert.Contains("finished: succeeded", cut.Find(".status").TextContent));

        await DisposeComponentsAsync();
        Assert.Equal([TranscriptLines.Sid], _viewers.Left);
    }

    [Fact]
    public void An_unknown_session_says_so_and_joins_nothing()
    {
        var cut = Render<Session>(p => p.Add(x => x.SessionId, "nope"));

        Assert.Contains("Unknown session.", cut.Markup);
        Assert.Empty(_viewers.Joined);
    }

    [Fact]
    public void An_open_page_whose_login_ends_is_torn_down_and_sent_to_the_login_page()
    {
        // The real circuit provider, rechecking a real login every 50 ms against a swappable stored hash.
        var secrets = DashboardLogin.Secrets();
        var logins = new DashboardLogins(new PasswordHashSource(() => secrets.Get(SecretAccounts.DashboardPasswordHash)!, TimeProvider.System, TimeSpan.Zero), TimeProvider.System);
        var provider = new DashboardAuthStateProvider(NullLoggerFactory.Instance, logins, new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromMilliseconds(50) });
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "dashboard"), .. DashboardLogins.Issue(secrets.Values[SecretAccounts.DashboardPasswordHash])], "Cookies")))));
        Services.AddSingleton<AuthenticationStateProvider>(provider);
        Services.AddAuthorizationCore();
        Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService, Microsoft.AspNetCore.Authorization.DefaultAuthorizationService>(); // over bUnit's placeholder
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton<AntiforgeryStateProvider>(new NoAntiforgery());
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("sessions/" + TranscriptLines.Sid);
        _data.Header = new SessionHeader(1, "sc-1", "Story 1", "acme/widgets", new SessionLink(TranscriptLines.Sid, 1, Now, null, null, null));

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => Assert.Equal([TranscriptLines.Sid], _viewers.Joined));
        Thread.Sleep(300); // several revalidations while the login holds
        Assert.Empty(_viewers.Left);
        Assert.Contains("Story 1", cut.Markup);

        secrets.Values[SecretAccounts.DashboardPasswordHash] = DashboardAuth.HashPassword("a brand new password");

        cut.WaitForAssertion(() => Assert.Equal([TranscriptLines.Sid], _viewers.Left), TimeSpan.FromSeconds(10)); // the page left its feed
        // The page leaves its feed as it is torn down; the re-render without it can land a moment later.
        cut.WaitForAssertion(() => Assert.DoesNotContain("Story 1", cut.Markup), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.NotEmpty(navigation.History), TimeSpan.FromSeconds(10));
        var redirect = navigation.History.First();
        Assert.Equal($"/login?returnUrl={Uri.EscapeDataString("/sessions/" + TranscriptLines.Sid)}", redirect.Uri);
        Assert.True(redirect.Options.ForceLoad);
    }

    private sealed class NoAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class FakeData : IDashboardData
    {
        public IReadOnlyList<PipelineRow> Rows { get; set; } = [];
        public SessionHeader? Header { get; set; }
        public int Reads;
        public IReadOnlyList<Control> Controls { get; set; } = [];
        public TimeSpan Quiet { get; set; } = DashboardData.DefaultQuietThreshold;

        public TimeSpan QuietThreshold => Quiet;

        public Task<IReadOnlyList<Control>> ControlsAsync(CancellationToken ct) => Task.FromResult(Controls);

        public Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Rows);
        }

        public Task<SessionHeader?> SessionAsync(string claudeSessionId, CancellationToken ct) =>
            Task.FromResult(Header?.Session.ClaudeSessionId == claudeSessionId ? Header : null);

        public IReadOnlyList<Metric> Metrics { get; set; } = [];

        public Exception? MetricsFails { get; set; }

        public Task<IReadOnlyList<Metric>> MetricsAsync(CancellationToken ct) =>
            MetricsFails is { } fails ? Task.FromException<IReadOnlyList<Metric>>(fails) : Task.FromResult(Metrics);
    }

    private sealed class FakeViewers : ISessionViewers
    {
        private SessionEventSink? _sink;
        public List<SessionEventMessage> Backlog { get; set; } = [];
        public List<string> Joined { get; } = [];
        public List<string> Left { get; } = [];
        private readonly Dictionary<string, string> _sessions = [];

        public async Task JoinAsync(string viewerId, string claudeSessionId, SessionEventSink send, Action abort, CancellationToken ct)
        {
            Joined.Add(claudeSessionId);
            _sessions[viewerId] = claudeSessionId;
            await send(Backlog, ct);
            _sink = send;
        }

        public void Send(IReadOnlyList<SessionEventMessage> page) => _sink!(page, CancellationToken.None).GetAwaiter().GetResult();

        public Task LeaveAsync(string viewerId)
        {
            Left.Add(_sessions[viewerId]);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The pipeline and session pages on the real host's services against the compose Postgres: changes
/// written by another process (a separate ledger context and recorder) reach the rendered page.
/// </summary>
public sealed class DashboardLiveTests : BunitContext, IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;
    private WebApplication? _app;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_dashlive");
        _cs = _db.ConnectionString;
        await LedgerMigrations.MigrateAsync(_cs, CancellationToken.None);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = _cs,
            ["Factory:HostPort"] = "0",
            ["Router:CostSettleSeconds"] = "0",
        }).Build();
        _app = FactoryHost.Build(new FactoryOptions(config, DashboardLogin.Secrets()), s => s.AddSingleton<ISessionCostSource>(new FixedCost()));
        await _app.StartAsync();
        Services.AddSingleton(_app.Services.GetRequiredService<IDashboardData>());
        Services.AddSingleton(_app.Services.GetRequiredService<PipelineChanges>());
        Services.AddSingleton(_app.Services.GetRequiredService<ISessionViewers>());
        Services.AddSingleton(_app.Services.GetRequiredService<IntakeStatus>());
        Services.AddSingleton(TimeProvider.System);
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private LedgerDbContext Context() => new(LedgerDbContext.PostgresOptions(_cs));

    private SessionRecorder Recorder() =>
        new(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), new FixedCost(), TimeProvider.System, TextWriter.Null, costRetryDelays: [TimeSpan.Zero],
            costSettleDelay: TimeSpan.Zero);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_pipeline_row_follows_ledger_writes_from_another_process_without_a_reload()
    {
        await using var db = Context();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("shortcut", "sc-41", "Live row", "acme/widgets", null, CancellationToken.None);

        var cut = Render<Pipeline>();
        cut.WaitForAssertion(() => Assert.Equal("Intake", cut.Find($"tr[data-item='{item.Id}'] td.state").TextContent), Wait);

        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal("Implement", cut.Find($"tr[data-item='{item.Id}'] td.state").TextContent), Wait);

        await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null,
            "https://github.com/acme/widgets/pull/3 https://github.com/acme/widgets/tree/factory/sc-41", CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Equal("https://github.com/acme/widgets/pull/3", cut.Find($"tr[data-item='{item.Id}'] td.pr a").GetAttribute("href")), Wait);

        // A session's cost lands on the session row (no ledger entry): the row's cost follows too.
        await using (var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None))
        {
            await capture.SetClaudeSessionIdAsync("sess-live-cost", CancellationToken.None);
            await capture.CompleteAsync(0, "succeeded", fetchCost: true, CancellationToken.None);
        }
        cut.WaitForAssertion(() => Assert.Equal("$0.4296", cut.Find($"tr[data-item='{item.Id}'] td.cost").TextContent), Wait);

        await ledger.RecordAsync(item, WorkState.Cancelled, null, null, CancellationToken.None);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll($"tr[data-item='{item.Id}']")), Wait);
    }

    [Fact]
    public async Task Opening_a_finished_session_replays_its_full_transcript_from_the_ledger()
    {
        var item = await ItemAsync();
        await using (var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None))
        {
            foreach (var line in StreamJsonFixture.Fresh)
            {
                await capture.OnLineAsync(line, CancellationToken.None);
            }
            await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);
        }

        var cut = Render<Session>(p => p.Add(x => x.SessionId, StreamJsonFixture.SessionId));

        var expected = ExpectedEntries(StreamJsonFixture.Fresh);
        cut.WaitForAssertion(() => Assert.Equal(expected, cut.FindAll("li.entry").Select(li => int.Parse(li.GetAttribute("data-seq")!))), Wait);
        Assert.Contains("finished: succeeded", cut.Find(".status").TextContent);
        Assert.Contains(cut.FindAll("li.entry.ToolCall"), li => li.TextContent.Contains("Glob"));
    }

    [Fact]
    public async Task Opening_a_running_session_shows_its_backlog_then_streams_new_events_once_each()
    {
        var item = await ItemAsync();
        await using var capture = await Recorder().StartAsync(item.Id, null, CancellationToken.None);
        foreach (var line in StreamJsonFixture.Fresh.Take(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await WaitStoredAsync(4);

        var cut = Render<Session>(p => p.Add(x => x.SessionId, StreamJsonFixture.SessionId));
        cut.WaitForAssertion(() => Assert.Equal(ExpectedEntries(StreamJsonFixture.Fresh.Take(4)), Sequences(cut)), Wait);
        Assert.Contains("running (live)", cut.Find(".status").TextContent);

        foreach (var line in StreamJsonFixture.Fresh.Skip(4))
        {
            await capture.OnLineAsync(line, CancellationToken.None);
        }
        await capture.CompleteAsync(0, "succeeded", fetchCost: false, CancellationToken.None);

        cut.WaitForAssertion(() => Assert.Equal(ExpectedEntries(StreamJsonFixture.Fresh), Sequences(cut)), Wait);
        cut.WaitForAssertion(() => Assert.Contains("finished: succeeded", cut.Find(".status").TextContent), Wait);
    }

    private static List<int> Sequences(IRenderedComponent<Session> cut) =>
        cut.FindAll("li.entry").Select(li => int.Parse(li.GetAttribute("data-seq")!)).ToList();

    /// <summary>The entry sequence numbers the transcript should show for these lines, in order (once each).</summary>
    private static List<int> ExpectedEntries(IEnumerable<string> lines)
    {
        var formatter = new TranscriptFormatter();
        return lines.Select((line, i) => new SessionEventMessage(i + 1, "x", null, line, DateTimeOffset.UnixEpoch))
            .SelectMany(formatter.Format).Select(e => (int)e.Sequence).ToList();
    }

    private async Task<WorkItem> ItemAsync()
    {
        await using var db = Context();
        return await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-42", "t", "acme/widgets", null, CancellationToken.None);
    }

    private async Task WaitStoredAsync(int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            await using var db = Context();
            if (db.SessionEvents.Count() >= count)
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "events were not stored");
            await Task.Delay(20);
        }
    }

    private sealed class FixedCost : ISessionCostSource
    {
        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionCost?>(new SessionCost(sessionId, 3, 429606));
    }
}
