using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
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
        Assert.Contains("Checks run:\n- none recorded", opened);
        Assert.Contains("Review verdicts:\n- none recorded", opened);
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
            Assert.Equal(checks.Count, Section(report, "Checks run:").Count);
            Assert.Equal(verdicts.Count, Section(report, "Review verdicts:").Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
            Assert.Contains(Section(report, "Checks run:"), l => l.StartsWith("- **passed** merge gate: `Merge aaaaaaaaaaaa:", StringComparison.Ordinal));
            Assert.Contains(Section(report, "Checks run:"), l => l.StartsWith("- **passed** new-tests-fail-on-base on `base0`...`aaaaaaaaaaaa`: `pass`", StringComparison.Ordinal));
            Assert.Equal(
                ["- **failed** `fail` on `111111111111`", "- **passed** `pass` on `aaaaaaaaaaaa`"],
                Section(report, "Review verdicts:").Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[..l.IndexOf(" by ", StringComparison.Ordinal)]));
            Assert.Contains("  - blocking correctness finding, confirmed by `claude-opus-5`: `whitespace-only input (src/x.cs:1)`", report);
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
        Assert.Equal(verdicts, Section(comment, "Review verdicts:").Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
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
        { null, WorkState.Implement, RunPipeline.Steps.Session, null, StepOutcome.Passed },
    };

    [Theory]
    [MemberData(nameof(Mapping))]
    public void Outcomes_map_the_typed_results_of_each_step(WorkState? from, WorkState state, string? step, string? detail, StepOutcome expected) =>
        Assert.Equal(expected, StepOutcomes.Of(from, state, step, detail));

    // ---- The migration (Postgres) ----

    private const string BeforeOutcomes = "20261008234036_FreezeDetail";

    [Fact]
    public async Task The_migration_backfills_every_existing_row_with_the_outcome_the_writer_gives_it()
    {
        // A corpus of real rows (from pipeline runs) and hand-written ones covering every rule, written before the column existed.
        var corpus = new List<List<(WorkState State, string? Step, string? Detail)>>();
        foreach (var h in new[] { await FixedAndMergedAsync(), await EscalatedAsync() })
        {
            corpus.Add((await h.Rows()).Select(r => (r.State, r.Step, r.Detail)).ToList());
        }
        var failing = new ReviewVerdict(Sha1, ReviewVerdict.Fail, "a reviewer wrote \"verdict\":\"pass\" here", [], []).ToDetail();
        var stalled = new FixProgress(1, Sha1, Sha1, 1, 1, [], [], ["\"outcome\":\"progress\",\"reason\":"], FixProgress.Failed, "x").ToDetail();
        corpus.Add(
        [
            (WorkState.Intake, null, "bug: url"), (WorkState.Implement, null, null), (WorkState.Implement, RunPipeline.Steps.WorktreeLost, "gone"),
            (WorkState.Implement, RunPipeline.Steps.Stuck, "loop"), (WorkState.Implement, RunPipeline.Steps.StuckRetry, "retry"),
            (WorkState.Paused, null, RunPipeline.UsagePaused), (WorkState.Paused, RunPipeline.Steps.UsagePause, "exhausted"),
            (WorkState.Implement, null, "unpaused"), (WorkState.Review, null, "https://github.com/o/r/pull/1"),
            (WorkState.Review, RunPipeline.Steps.Verdict, failing), (WorkState.Fixing, null, Sha1),
            (WorkState.Review, null, "fix round 1 stuck: loop; nothing pushed"), (WorkState.Review, RunPipeline.Steps.FixProgress, stalled),
            (WorkState.CI, null, Sha1), (WorkState.CI, RunPipeline.Steps.CiFailure, "{}"), (WorkState.CIHealing, null, Sha1),
            (WorkState.CI, null, RunPipeline.StuckRoundDetail("ci fix round 2", "loop")), (WorkState.Review, null, "head moved"),
            (WorkState.CI, null, Sha1), (WorkState.MergeGate, null, Sha1), (WorkState.MergeGate, RunPipeline.Steps.GateDecision, "ReviewHead 1: none"),
            (WorkState.MergeGate, RunPipeline.Steps.GateDecision, "Blocked 1: sealed"),
            (WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Rejected, "x").ToDetail()),
            (WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.NoTests, "x").ToDetail()),
            (WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Unsupported, "x").ToDetail()),
            (WorkState.MergeGate, RunPipeline.Steps.NewTests, NewTestsResult.Without("b", "h", NewTestsOutcome.Error, "x").ToDetail()),
            (WorkState.MergeGate, RunPipeline.Steps.MergeConflict, "{}"), (WorkState.CI, null, Sha1), (WorkState.MergeGate, null, Sha1),
            (WorkState.Review, null, "no verdict"), (WorkState.CI, null, Sha1), (WorkState.MergeGate, null, Sha1), (WorkState.Fixing, null, Sha1),
            (WorkState.Review, null, "conflict fix round 3 pushed abc"), (WorkState.CI, null, Sha1), (WorkState.MergeGate, null, Sha1),
            (WorkState.Merge, null, "commit"), (WorkState.Watch, null, "commit"), (WorkState.Intake, null, "reverted"),
            (WorkState.Escalated, null, "boom"), (WorkState.Escalated, RunPipeline.Steps.EscalationComment, "failed: 500"), (WorkState.Cancelled, null, "stopped"),
        ]);
        corpus.Add(
        [
            (WorkState.Intake, null, "issue"), (WorkState.Paused, null, "needs a human"), (WorkState.Paused, Issues.IssueSteps.Triaged, "{}"),
            (WorkState.Paused, Issues.IssueSteps.ApprovalIgnored, "{}"), (WorkState.Paused, Issues.IssueSteps.Refused, "422"),
            (WorkState.Paused, RunPipeline.Steps.Parked, "awaiting approval: x"),
        ]);
        var expected = corpus.SelectMany(rows =>
        {
            WorkState? from = null;
            return rows.Select(r =>
            {
                var outcome = StepOutcomes.Of(from, r.State, r.Step, r.Detail);
                from = r.Step is null ? r.State : from;
                return StepOutcomes.Name(outcome);
            }).ToList();
        }).ToList();
        Assert.Equal(Enum.GetValues<StepOutcome>().Select(StepOutcomes.Name).Order(), expected.Distinct().Order()); // every outcome is exercised

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
                foreach (var (state, step, detail) in rows)
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

    private static async Task<Harness> EscalatedAsync()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };
        Assert.Equal(WorkState.Escalated, (await h.Run()).State);
        return h;
    }
}
