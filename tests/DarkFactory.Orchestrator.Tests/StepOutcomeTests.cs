using System.Text.Json;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25389: every ledger row carries a typed outcome, and what the factory reports is rendered from the ledger.</summary>
public class StepOutcomeTests
{
    /// <summary>A run with one review fix round, then CI, the gate and the merge.</summary>
    private static async Task<Harness> FixedAndMergedAsync()
    {
        var h = new Harness
        {
            Reviewer = new FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness && r.Pull.HeadSha == Sha1
                    ? [new Finding(Finding.Blocking, "whitespace-only input", "src/x.cs", 1, "WordCount(\"  \") returns 1")]
                    : [],
            },
        };
        var outcome = await h.Run();
        Assert.True(outcome.Succeeded, outcome.Error);
        return h;
    }

    [Fact]
    public async Task Every_row_of_a_full_run_carries_its_typed_outcome()
    {
        var h = await FixedAndMergedAsync();
        var rows = await h.Rows();

        Assert.All(rows, r => Assert.True(Enum.IsDefined(r.Outcome), $"row {r.Id} ({r.State} {r.Step}) has no outcome"));
        var transitions = rows.Where(r => r.Step is null).Select(r => (r.State, r.Outcome)).ToList();
        Assert.Equal(
        [
            (WorkState.Intake, StepOutcome.Passed), (WorkState.Implement, StepOutcome.Passed), (WorkState.Review, StepOutcome.Passed),
            (WorkState.Fixing, StepOutcome.Failed), // the review failed: a fix round starts
            (WorkState.Review, StepOutcome.Passed), (WorkState.CI, StepOutcome.Passed), (WorkState.MergeGate, StepOutcome.Passed),
            (WorkState.Merge, StepOutcome.Passed), (WorkState.Watch, StepOutcome.Passed),
        ], transitions);
        Assert.Equal([StepOutcome.Failed, StepOutcome.Passed], rows.Where(r => r.Step == RunPipeline.Steps.Verdict).Select(r => r.Outcome));
        Assert.Equal(StepOutcome.Passed, rows.Single(r => r.Step == RunPipeline.Steps.FixProgress).Outcome);
        Assert.Equal(StepOutcome.Passed, rows.Single(r => r.Step == RunPipeline.Steps.GateDecision).Outcome);
        Assert.Equal(StepOutcome.Passed, rows.Single(r => r.Step == RunPipeline.Steps.NewTests).Outcome);
        // The one writer decided each from the row itself.
        WorkState? from = null;
        foreach (var row in rows)
        {
            Assert.Equal(StepOutcomes.Of(from, row.State, row.Step, row.Detail), row.Outcome);
            from = row.Step is null ? row.State : from;
        }
    }

    [Fact]
    public async Task An_escalated_run_records_failed_rounds_and_an_escalated_step()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        var rows = await h.Rows();
        Assert.All(rows, r => Assert.True(Enum.IsDefined(r.Outcome)));
        Assert.Equal(StepOutcome.Escalated, rows.Single(r => r.Step is null && r.State == WorkState.Escalated).Outcome);
        Assert.All(rows.Where(r => r.Step is null && r.State == WorkState.Fixing), r => Assert.Equal(StepOutcome.Failed, r.Outcome));
        Assert.All(rows.Where(r => r.Step == RunPipeline.Steps.FixProgress), r => Assert.Equal(StepOutcome.Failed, r.Outcome));
        Assert.Equal(StepOutcome.Passed, rows.Single(r => r.Step == RunPipeline.Steps.EscalationComment).Outcome);
    }

    [Fact]
    public async Task The_pr_body_and_the_closeout_list_exactly_the_checks_and_verdicts_in_the_ledger()
    {
        var h = await FixedAndMergedAsync();
        var rows = await h.Rows();
        var checks = rows.Where(r => r.Step is { } s && LedgerReport.CheckSteps.Contains(s)).ToList();
        var verdicts = rows.Where(r => r.Step == RunPipeline.Steps.Verdict).ToList();
        Assert.Equal(2, checks.Count); // the gate's decision and the new-tests run
        Assert.Equal(2, verdicts.Count);

        // At PR open nothing has been checked or reviewed yet, and the body says so (no worker summary in it).
        var opened = h.Prs.Opened.Single().Body;
        Assert.Contains("https://app.shortcut.com/trefry/story/77", opened);
        Assert.Contains("Checks run (0):\n- none recorded", opened);
        Assert.Contains("Review verdicts (0):\n- none recorded", opened);
        Assert.Contains($"Worker sessions: `{RunPipelineTests.Ok.SessionId}`", opened);
        Assert.Contains("Worker cost: N/A", opened);
        Assert.DoesNotContain("done", opened); // the worker's result text

        // At merge the description is rewritten from the ledger, once, and the board gets the same facts as its closeout.
        var (url, merged) = Assert.Single(h.Prs.BodyUpdates);
        Assert.Equal(RunPipelineTests.PrUrl, url);
        var closeout = Assert.Single(h.Stories.Comments);
        Assert.StartsWith("[author: dark-factory] sc-77 merged as `9999999999999999999999999999999999999999`", closeout);
        foreach (var report in new[] { merged, closeout })
        {
            Assert.Equal(checks.Count, Section(report, "Checks run (2: 2 passed):").Count);
            Assert.Equal(verdicts.Count, Section(report, "Review verdicts (2: 1 passed, 1 failed):").Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
            Assert.Contains(Section(report, "Checks run (2: 2 passed):"), l => l.StartsWith("- **passed** merge gate: `Merge aaaaaaaaaaaa:", StringComparison.Ordinal));
            Assert.Contains(Section(report, "Checks run (2: 2 passed):"), l => l.StartsWith("- **passed** new-tests-fail-on-base on `base0`...`aaaaaaaaaaaa`: `pass`", StringComparison.Ordinal));
            Assert.Equal(
                ["- **failed** `fail` on `111111111111`", "- **passed** `pass` on `aaaaaaaaaaaa`"],
                Section(report, "Review verdicts (2: 1 passed, 1 failed):").Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(" by ", StringComparison.Ordinal)]));
            Assert.Contains("  - blocking correctness finding, confirmed by `gpt-6-astra`: `whitespace-only input (src/x.cs:1)`", report);
            Assert.Contains("Fix rounds: 1 of 3", report);
            Assert.Contains("- round 1 (review findings): **passed**, `fix round 1 pushed aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`; progress check **passed**", report);
            Assert.Contains("Worker cost: N/A", report);
        }
        Assert.Contains("State: Merge (passed", merged);
        Assert.Equal([RunPipeline.Steps.MergedReported, RunPipeline.Steps.Closeout, RunPipeline.Steps.PrReport],
            rows.Where(r => r.Step is RunPipeline.Steps.MergedReported or RunPipeline.Steps.Closeout or RunPipeline.Steps.PrReport).Select(r => r.Step));
    }

    [Fact]
    public async Task A_description_that_cannot_be_rewritten_is_recorded_as_failed_and_the_merge_stands()
    {
        var h = new Harness();
        h.Prs.UpdateThrows = new HttpRequestException("502 Bad Gateway");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        var report = (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.PrReport);
        Assert.Equal(("failed: 502 Bad Gateway", StepOutcome.Failed), (report.Detail, report.Outcome));
    }

    [Fact]
    public async Task A_closeout_that_cannot_be_posted_is_recorded_shown_and_retried_from_watch_a_bounded_number_of_times()
    {
        var h = new Harness();
        h.Stories.CommentFails = true;

        var outcome = await h.Run();

        // The merge stands: the item is in Watch (not escalated), the description was still rewritten, the closeout's failure recorded.
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.DoesNotContain(WorkState.Escalated, await h.Transitions());
        Assert.Single(h.Prs.BodyUpdates);
        var first = (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.Closeout);
        Assert.Equal((WorkState.Merge, "failed: Shortcut down", StepOutcome.Failed), (first.State, first.Detail, first.Outcome));

        // The dashboard shows it, and the poll lists the item so its next run tries again (each attempt recorded).
        var data = new DashboardData(h.Contexts, h.Time);
        var row = Assert.Single(await data.ActiveItemsAsync(CancellationToken.None));
        Assert.Equal((new CloseoutStatus(1, false, "failed: Shortcut down"), true), (row.FailedCloseout, row.CloseoutRetrying));
        Assert.Equal([77], await RunPipeline.InFlightAsync(h.Ledger, CancellationToken.None, h.Controls));
        for (var attempt = 2; attempt <= RunPipeline.MaxCloseoutAttempts; attempt++)
        {
            var retry = await h.Run();
            Assert.Equal((WorkState.Watch, "sc-77: closeout NOT posted: Shortcut down"), (retry.State, retry.Error));
        }
        var attempts = (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.Closeout).ToList();
        Assert.Equal(RunPipeline.MaxCloseoutAttempts, attempts.Count);
        Assert.All(attempts.Skip(1), a => Assert.Equal((WorkState.Watch, StepOutcome.Failed), (a.State, a.Outcome)));

        // Bounded: once the attempts are used up the poll leaves it, and the dashboard says a human must post it.
        Assert.Empty(await RunPipeline.InFlightAsync(h.Ledger, CancellationToken.None, h.Controls));
        row = Assert.Single(await data.ActiveItemsAsync(CancellationToken.None));
        Assert.Equal((RunPipeline.MaxCloseoutAttempts, false), (row.FailedCloseout!.Attempts, row.CloseoutRetrying));
        await h.Run();
        Assert.Equal(RunPipeline.MaxCloseoutAttempts, (await h.Rows()).Count(r => r.Step == RunPipeline.Steps.Closeout));
        Assert.Empty(h.Stories.Comments);
    }

    [Fact]
    public async Task A_retried_closeout_that_posts_ends_the_retries()
    {
        var h = new Harness();
        h.Stories.CommentFails = true;
        await h.Run();
        h.Stories.CommentFails = false;

        var retry = await h.Run();

        Assert.Equal((WorkState.Watch, (string?)null), (retry.State, retry.Error));
        var closeout = Assert.Single(h.Stories.Comments);
        Assert.StartsWith("[author: dark-factory] sc-77 merged as", closeout);
        Assert.Equal([StepOutcome.Failed, StepOutcome.Passed], (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.Closeout).Select(r => r.Outcome));
        Assert.DoesNotContain(await h.Rows(), r => r.Step == RunPipeline.Steps.HeldNotice);
        Assert.Empty(await RunPipeline.InFlightAsync(h.Ledger, CancellationToken.None, h.Controls));
        Assert.Null(Assert.Single(await new DashboardData(h.Contexts, h.Time).ActiveItemsAsync(CancellationToken.None)).FailedCloseout);
    }

    /// <summary>A blocking finding with a 400-character title (the most a report quotes of one).</summary>
    private static Finding LongFinding(int n) => new(Finding.Blocking, $"finding {n:000} " + new string('x', 400), "src/x.cs", n, "detail",
        new Confirmation(Confirmation.Confirmed, "m", "high", null, null, "r"));

    [Fact]
    public void Reports_over_a_long_history_stay_under_the_github_limit_and_keep_the_checks_summary_and_the_latest_verdict()
    {
        const int GitHubLimit = 65_536;
        var history = new List<LedgerEntry>();
        WorkState? from = null;
        void Add(WorkState state, string? step, string? detail)
        {
            history.Add(new LedgerEntry
            {
                Id = history.Count + 1, WorkItemId = 1, State = state, Step = step, Detail = detail, RecordedAt = DateTimeOffset.UnixEpoch,
                Outcome = StepOutcomes.Of(from, state, step, detail),
            });
            from = step is null ? state : from;
        }
        Add(WorkState.Intake, null, "bug: url");
        Add(WorkState.Implement, null, null);
        Add(WorkState.Review, null, RunPipelineTests.PrUrl);
        const int verdicts = 20;
        static string Sha(int v) => $"{v:d2}" + new string('a', 38);
        for (var v = 1; v <= verdicts; v++)
        {
            var findings = Enumerable.Range(1, 200).Select(LongFinding).ToList();
            Add(WorkState.Review, RunPipeline.Steps.Verdict, new ReviewVerdict(Sha(v), ReviewVerdict.Fail, "s", [],
                [new RoleReview(ReviewRoles.Correctness, "m", "high", null, null, findings, "s")]).ToDetail());
            var names = Enumerable.Range(1, 40).Select(i => $"check {i} " + new string('y', 400)).ToList();
            Add(WorkState.CI, RunPipeline.Steps.CiFailure, JsonSerializer.Serialize(new CiTriage(Sha(v), "base0", names, names)));
        }
        Add(WorkState.MergeGate, null, "sha");
        Add(WorkState.MergeGate, RunPipeline.Steps.GateDecision, "Merge 1111: ok");
        Add(WorkState.Merge, null, "commit");

        var reports = new[]
        {
            LedgerReport.PullRequestBody(new WorkStory(77, "story", "d", "bug", "https://app.shortcut.com/trefry/story/77"), history, [1m]),
            LedgerReport.MergedCloseout("sc-77", history, [1m]),
        };

        foreach (var report in reports)
        {
            Assert.True(report.Length < GitHubLimit, $"{report.Length} characters");
            Assert.Contains($"\nChecks run ({verdicts + 1}: 1 passed, {verdicts} failed):\n", report);
            Assert.Contains($"\nReview verdicts ({verdicts}: {verdicts} failed):\n", report);
            Assert.Contains("earlier check(s) not shown (the ledger has them)", report);
            Assert.Contains("earlier verdict(s) not shown (the ledger has them)", report);
            // The latest check and the latest verdict are there in full: its first findings, then a count of the rest.
            Assert.Contains("- **passed** merge gate: `Merge 1111: ok`", report);
            Assert.Contains($"- **failed** `fail` on `{Sha(verdicts)[..12]}`", report);
            var latest = report[report.LastIndexOf($"- **failed** `fail` on `{Sha(verdicts)[..12]}`", StringComparison.Ordinal)..];
            Assert.Equal(LedgerReport.MaxBlockingFindingsShown, latest.Split('\n').Count(l => l.StartsWith("  - blocking correctness finding", StringComparison.Ordinal)));
            Assert.Contains($"  - … and {200 - LedgerReport.MaxBlockingFindingsShown} more blocking finding(s) (the ledger has them)", latest);
            Assert.Contains("Worker cost: $1", report);
        }
    }

    [Fact]
    public async Task An_escalation_comment_over_a_flood_of_findings_stays_under_the_github_limit()
    {
        var h = new Harness
        {
            Reviewer = new FakeReviewer { Findings = r => r.Role == ReviewRoles.Correctness ? Enumerable.Range(1, 200).Select(LongFinding).ToList() : [] },
        };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        var comment = Assert.Single(h.Stories.Comments);
        Assert.True(comment.Length < 65_536, $"{comment.Length} characters");
        Assert.Contains("more characters; the ledger has them)", comment); // the reason (every open finding) is cut short
        Assert.Contains($"  - … and {200 - LedgerReport.MaxBlockingFindingsShown} more blocking finding(s) (the ledger has them)", comment);
        Assert.Equal("posted", (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.EscalationComment).Detail);
    }

    [Fact]
    public void A_merge_conflict_is_reported_as_a_failed_check()
    {
        var detail = new BaseUpdate(BaseUpdate.Kinds.Conflict, "head1", "base1", Files: ["src/@x.cs"]).ToDetail();
        var history = new List<LedgerEntry>
        {
            new() { Id = 1, State = WorkState.MergeGate, RecordedAt = DateTimeOffset.UnixEpoch, Outcome = StepOutcome.Passed },
            new()
            {
                Id = 2, State = WorkState.MergeGate, Step = RunPipeline.Steps.MergeConflict, Detail = detail, RecordedAt = DateTimeOffset.UnixEpoch,
                Outcome = StepOutcomes.Of(WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.MergeConflict, detail),
            },
        };

        var facts = LedgerReport.Facts(history, []);

        Assert.Contains($"Checks run (1: 1 failed):\n- **failed** merge with the base: conflict {UntrustedText.CodeSpan(detail)}\n", facts);
    }

    [Fact]
    public async Task The_escalation_closeout_carries_the_ledger_facts_with_model_text_made_inert()
    {
        const string title = "@owner see [this](https://evil.example/x) `now`";
        var h = new Harness
        {
            Reviewer = new FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness ? [new Finding(Finding.Blocking, title, "src/x.cs", 1, "detail")] : [],
            },
        };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        var comment = h.Stories.Comments.Single();
        var verdicts = (await h.Rows()).Count(r => r.Step == RunPipeline.Steps.Verdict);
        Assert.Equal(verdicts, Section(comment, $"Review verdicts ({verdicts}: {verdicts} failed):").Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Contains("Fix rounds: 3 of 3", comment);
        // The reason quotes the finding, fenced as data; the facts show it inert in a code span.
        var reason = comment[comment.IndexOf("Reason:\n~~~~text\n", StringComparison.Ordinal)..comment.IndexOf("Last ledger state:", StringComparison.Ordinal)];
        Assert.Contains(title, reason);
        var facts = comment[comment.IndexOf("**From the factory ledger**", StringComparison.Ordinal)..];
        Assert.DoesNotContain("@", facts);
        Assert.DoesNotContain("](", facts);
        Assert.DoesNotContain("https://", facts);
        Assert.Contains("State: Escalated (escalated", facts);
    }

    /// <summary>The bullet lines under <paramref name="heading"/> up to the next blank line.</summary>
    private static List<string> Section(string text, string heading)
    {
        var lines = text.Split('\n');
        var at = Array.IndexOf(lines, heading);
        Assert.True(at >= 0, $"no '{heading}' in:\n{text}");
        return lines.Skip(at + 1).TakeWhile(l => l.Length > 0).Where(l => !l.EndsWith("none recorded", StringComparison.Ordinal)).ToList();
    }

    public static TheoryData<WorkState?, WorkState, string?, string?, StepOutcome> Mapping => new()
    {
        { null, WorkState.Intake, null, "story: url", StepOutcome.Passed },
        { WorkState.Implement, WorkState.Escalated, null, "boom", StepOutcome.Escalated },
        { WorkState.Implement, WorkState.Paused, null, RunPipeline.UsagePaused, StepOutcome.Deferred },
        { WorkState.Implement, WorkState.Paused, null, RunPipeline.FreezePaused, StepOutcome.Deferred },
        { WorkState.Implement, WorkState.Paused, null, RunPipeline.UserPaused, StepOutcome.Deferred },
        { WorkState.Intake, WorkState.Paused, null, "needs a human", StepOutcome.Escalated },
        { WorkState.Paused, WorkState.Fixing, null, "unpaused", StepOutcome.Passed },
        { WorkState.Review, WorkState.Cancelled, null, "stopped by x", StepOutcome.Deferred },
        { WorkState.Review, WorkState.Fixing, null, "sha", StepOutcome.Failed },
        { WorkState.CI, WorkState.CIHealing, null, "sha", StepOutcome.Failed },
        { WorkState.MergeGate, WorkState.Fixing, null, "sha", StepOutcome.Failed },
        { WorkState.Fixing, WorkState.Review, null, "fix round 1 pushed sha", StepOutcome.Passed },
        { WorkState.Fixing, WorkState.Review, null, RunPipeline.StuckRoundDetail("fix round 1", "looping"), StepOutcome.Failed },
        { WorkState.CIHealing, WorkState.CI, null, RunPipeline.StuckRoundDetail("ci fix round 1", "looping"), StepOutcome.Failed },
        { WorkState.MergeGate, WorkState.CI, null, "sha", StepOutcome.Failed },
        { WorkState.CI, WorkState.Review, null, "head moved", StepOutcome.GateRejected },
        { WorkState.MergeGate, WorkState.Review, null, "no verdict", StepOutcome.GateRejected },
        { WorkState.Watch, WorkState.Intake, null, "reverted", StepOutcome.Failed },
        { WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.GateDecision, "Merge 1111: ok", StepOutcome.Passed },
        { WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.GateDecision, "Blocked 1111: sealed", StepOutcome.GateRejected },
        { WorkState.MergeGate, WorkState.MergeGate, RunPipeline.Steps.GateDecision, "ReviewHead 1111: no verdict", StepOutcome.GateRejected },
        { null, WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Pass, "ok").ToDetail(), StepOutcome.Passed },
        { null, WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Rejected, "x").ToDetail(), StepOutcome.GateRejected },
        { null, WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.NoTests, "x").ToDetail(), StepOutcome.GateRejected },
        { null, WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Unsupported, "x").ToDetail(), StepOutcome.Failed },
        { null, WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Error, "x").ToDetail(), StepOutcome.Failed },
        { null, WorkState.CI, RunPipeline.Steps.CiFailure, "{}", StepOutcome.Failed },
        { null, WorkState.Implement, RunPipeline.Steps.Stuck, "looping", StepOutcome.Failed },
        { null, WorkState.Escalated, RunPipeline.Steps.EscalationComment, "posted", StepOutcome.Passed },
        { null, WorkState.Escalated, RunPipeline.Steps.EscalationComment, "failed: 500", StepOutcome.Failed },
        { null, WorkState.Paused, RunPipeline.Steps.UsagePause, "exhausted", StepOutcome.Deferred },
        { null, WorkState.Paused, RunPipeline.Steps.Parked, "claim refused", StepOutcome.Deferred },
        { null, WorkState.Paused, Issues.IssueSteps.ApprovalIgnored, "{}", StepOutcome.GateRejected },
        { null, WorkState.Paused, Issues.IssueSteps.RoutedToHuman, "no apparent fix", StepOutcome.Escalated },
        { null, WorkState.Implement, RunPipeline.Steps.Session, null, StepOutcome.Passed },
        { null, WorkState.MergeGate, RunPipeline.Steps.MergeConflict, "{}", StepOutcome.Failed },
        { null, WorkState.Merge, RunPipeline.Steps.Closeout, RunPipeline.Posted, StepOutcome.Passed },
        { null, WorkState.Merge, RunPipeline.Steps.Closeout, "failed: Shortcut down", StepOutcome.Failed },
        { null, WorkState.Watch, RunPipeline.Steps.Closeout, "failed: Shortcut down", StepOutcome.Failed },
        { null, WorkState.Merge, RunPipeline.Steps.PrReport, "failed: 502", StepOutcome.Failed },
        { null, WorkState.Merge, RunPipeline.Steps.PrReport, "merge", StepOutcome.Passed },
    };

    [Theory]
    [MemberData(nameof(Mapping))]
    public void Outcomes_map_the_typed_results_of_each_step(WorkState? from, WorkState state, string? step, string? detail, StepOutcome expected) =>
        Assert.Equal(expected, StepOutcomes.Of(from, state, step, detail));

    // ---- The migration (Postgres) ----

    private const string BeforeOutcomes = "20261008234036_FreezeDetail";

    // Details as the writers stored them before the outcome column existed (frozen literals: the backfill is a shipped migration).
    private const string VerdictPass = """{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","verdict":"pass","summary":"ok","risky":[],"reviews":[]}""";
    private const string VerdictFail = """{"sha":"1111111111111111111111111111111111111111","verdict":"fail","summary":"a reviewer wrote \"verdict\":\"pass\" here","risky":[],"reviews":[]}""";
    private const string ProgressMade = """{"round":1,"from":"111","to":"aaa","blocking_before":1,"blocking_after":0,"passed_before":[],"unknown_before":[],"failing_now":[],"outcome":"progress","reason":"fixed"}""";
    private const string ProgressStalled = """{"round":1,"from":"111","to":"111","blocking_before":1,"blocking_after":1,"passed_before":[],"unknown_before":[],"failing_now":["\"outcome\":\"progress\",\"reason\":"],"outcome":"failed","reason":"x"}""";

    private static string NewTests(string outcome) =>
        $$"""{"base":"b","head":"h","outcome":"{{outcome}}","reason":"x","strategy":null,"base_run":null,"head_run":null,"tests":[]}""";

    /// <summary>
    /// The rows the backfill test writes before the outcome column exists, one list per item (oldest first), each with the outcome the
    /// backfill must give it — a frozen corpus covering every rule, written as literals so a later change to the live rules
    /// (<see cref="StepOutcomes.Of"/>, checked by <see cref="Outcomes_map_the_typed_results_of_each_step"/>) never bears on the migration.
    /// </summary>
    private static readonly (WorkState State, string? Step, string? Detail, string Expected)[][] BackfillCorpus =
    [
        // A run merged after one review fix round.
        [
            (WorkState.Intake, null, "bug: url", "passed"), (WorkState.Intake, "claimed", null, "passed"),
            (WorkState.Implement, null, null, "passed"), (WorkState.Implement, "worker-started", "123", "passed"),
            (WorkState.Implement, "session", null, "passed"), (WorkState.Implement, "pushed", "111", "passed"),
            (WorkState.Review, null, "https://github.com/o/r/pull/1", "passed"), (WorkState.Review, "verdict", VerdictFail, "failed"),
            (WorkState.Fixing, null, "111", "failed"), (WorkState.Fixing, "pushed", "aaa", "passed"),
            (WorkState.Review, null, "fix round 1 pushed aaa", "passed"), (WorkState.Review, "fix-progress", ProgressMade, "passed"),
            (WorkState.Review, "verdict", VerdictPass, "passed"), (WorkState.CI, null, "aaa", "passed"), (WorkState.MergeGate, null, "aaa", "passed"),
            (WorkState.MergeGate, "new-tests", NewTests("pass"), "passed"), (WorkState.MergeGate, "gate", "Merge aaa: ok", "passed"),
            (WorkState.MergeGate, "gate-passed", "aaa", "passed"), (WorkState.Merge, null, "commit", "passed"),
            (WorkState.Merge, "merged-reported", "commit", "passed"), (WorkState.Merge, "pr-report", "merge", "passed"),
            (WorkState.Watch, null, "commit", "passed"),
        ],
        // A run escalated after a stalled fix round.
        [
            (WorkState.Intake, null, "bug: url", "passed"), (WorkState.Implement, null, null, "passed"),
            (WorkState.Review, null, "https://github.com/o/r/pull/2", "passed"), (WorkState.Review, "verdict", VerdictFail, "failed"),
            (WorkState.Fixing, null, "111", "failed"), (WorkState.Review, null, "fix round 1 pushed 111", "passed"),
            (WorkState.Review, "fix-progress", ProgressStalled, "failed"), (WorkState.Escalated, null, "boom", "escalated"),
            (WorkState.Escalated, "escalation-comment", "posted", "passed"),
        ],
        // Every other rule.
        [
            (WorkState.Intake, null, "bug: url", "passed"), (WorkState.Implement, null, null, "passed"),
            (WorkState.Implement, "worktree-lost", "gone", "failed"), (WorkState.Implement, "stuck", "loop", "failed"),
            (WorkState.Implement, "stuck-retry", "retry", "failed"), (WorkState.Paused, null, "usage-paused", "deferred"),
            (WorkState.Paused, "usage-pause", "exhausted", "deferred"), (WorkState.Implement, null, "unpaused", "passed"),
            (WorkState.Review, null, "https://github.com/o/r/pull/1", "passed"), (WorkState.Fixing, null, "111", "failed"),
            (WorkState.Review, null, "fix round 1 stuck: loop; nothing pushed", "failed"), (WorkState.CI, null, "111", "passed"),
            (WorkState.CI, "ci-failure", "{}", "failed"), (WorkState.CIHealing, null, "111", "failed"),
            (WorkState.CI, null, "ci fix round 2 stuck: loop; nothing pushed", "failed"), (WorkState.Review, null, "head moved", "gate_rejected"),
            (WorkState.CI, null, "111", "passed"), (WorkState.MergeGate, null, "111", "passed"),
            (WorkState.MergeGate, "gate", "ReviewHead 1: none", "gate_rejected"), (WorkState.MergeGate, "gate", "Blocked 1: sealed", "gate_rejected"),
            (WorkState.MergeGate, "new-tests", NewTests("rejected"), "gate_rejected"), (WorkState.MergeGate, "new-tests", NewTests("no-tests"), "gate_rejected"),
            (WorkState.MergeGate, "new-tests", NewTests("unsupported"), "failed"), (WorkState.MergeGate, "new-tests", NewTests("error"), "failed"),
            (WorkState.MergeGate, "merge-conflict", "{}", "failed"), (WorkState.CI, null, "111", "failed"), (WorkState.MergeGate, null, "111", "passed"),
            (WorkState.Review, null, "no verdict", "gate_rejected"), (WorkState.CI, null, "111", "passed"), (WorkState.MergeGate, null, "111", "passed"),
            (WorkState.Fixing, null, "111", "failed"), (WorkState.Review, null, "conflict fix round 3 pushed abc", "passed"),
            (WorkState.CI, null, "111", "passed"), (WorkState.MergeGate, null, "111", "passed"), (WorkState.Merge, null, "commit", "passed"),
            (WorkState.Watch, null, "commit", "passed"), (WorkState.Intake, null, "reverted", "failed"), (WorkState.Escalated, null, "boom", "escalated"),
            (WorkState.Escalated, "escalation-comment", "failed: 500", "failed"), (WorkState.Cancelled, null, "stopped", "deferred"),
        ],
        // A GitHub issue's triage.
        [
            (WorkState.Intake, null, "issue", "passed"), (WorkState.Paused, null, "needs a human", "escalated"),
            (WorkState.Paused, "triaged", "{}", "passed"), (WorkState.Paused, "approval-ignored", "{}", "gate_rejected"),
            (WorkState.Paused, "github-refused", "422", "failed"), (WorkState.Paused, "parked", "awaiting approval: x", "deferred"),
        ],
    ];

    [Fact]
    public async Task The_migration_backfills_every_existing_row_with_its_frozen_outcome()
    {
        var corpus = BackfillCorpus;
        var expected = corpus.SelectMany(rows => rows.Select(r => r.Expected)).ToList();
        Assert.Equal(["deferred", "escalated", "failed", "gate_rejected", "passed"], expected.Distinct().Order()); // every outcome is exercised

        await using var pg = await TempPostgresDatabase.CreateAsync("df_outcomes");
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(pg.ConnectionString)))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeOutcomes);
        }
        await using (var conn = new NpgsqlConnection(pg.ConnectionString))
        {
            await conn.OpenAsync();
            var n = 0;
            foreach (var rows in corpus)
            {
                await using var item = new NpgsqlCommand("""
                    INSERT INTO work_items ("Source", "ExternalId", "Title", "Repo", "State", "CreatedAt", "UpdatedAt", "Version")
                    VALUES ('shortcut', @id, 't', 'o/r', 'Intake', now(), now(), 0) RETURNING "Id"
                    """, conn);
                item.Parameters.AddWithValue("id", $"sc-{++n}");
                var itemId = (long)(await item.ExecuteScalarAsync())!;
                foreach (var (state, step, detail, _) in rows)
                {
                    await using var row = new NpgsqlCommand("""
                        INSERT INTO ledger_entries ("WorkItemId", "State", "Step", "RecordedAt", "Detail") VALUES (@item, @state, @step, now(), @detail)
                        """, conn);
                    row.Parameters.AddWithValue("item", itemId);
                    row.Parameters.AddWithValue("state", state.ToString());
                    row.Parameters.AddWithValue("step", (object?)step ?? DBNull.Value);
                    row.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
                    await row.ExecuteNonQueryAsync();
                }
            }
        }

        await LedgerMigrations.MigrateAsync(pg.ConnectionString, CancellationToken.None);

        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(pg.ConnectionString)))
        {
            var backfilled = await db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Outcome).ToListAsync();
            Assert.Equal(expected, backfilled.Select(StepOutcomes.Name));
        }
    }

    [Fact]
    public async Task Postgres_refuses_a_row_without_an_outcome_or_with_an_unknown_one()
    {
        await using var pg = await TempPostgresDatabase.CreateAsync("df_outcomes");
        await LedgerMigrations.MigrateAsync(pg.ConnectionString, CancellationToken.None);
        long itemId;
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(pg.ConnectionString)))
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "t", "o/r", "bug: url", CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Escalated, null, "boom", CancellationToken.None);
            itemId = item.Id;
        }
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(pg.ConnectionString)))
        {
            Assert.Equal([StepOutcome.Passed, StepOutcome.Escalated], await db.LedgerEntries.OrderBy(e => e.Id).Select(e => e.Outcome).ToListAsync());
        }
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        foreach (var outcome in new[] { "NULL", "'bogus'", "''" })
        {
            await using var insert = new NpgsqlCommand(
                $"""INSERT INTO ledger_entries ("WorkItemId", "State", "RecordedAt", "Outcome") VALUES ({itemId}, 'Intake', now(), {outcome})""", conn);
            await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        }
    }
}
