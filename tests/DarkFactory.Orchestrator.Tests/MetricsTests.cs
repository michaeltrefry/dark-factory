using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25389: the metrics come from the ledger only, say N/A when nothing is measured, and leave sandbox and demo items out.</summary>
public class MetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MetricsOptions Options = new(["acme/sandbox"], ["[demo]"]);
    private static long _ids;

    private static Metric Get(IReadOnlyList<Metric> metrics, string name) => metrics.Single(m => m.Name == name);

    /// <summary>An item whose ledger holds <paramref name="rows"/> (transitions, or checkpoints as "step:detail"), an hour apart.</summary>
    private static MetricItem Item(string repo, string title, IReadOnlyList<decimal?> costs, params object[] rows)
    {
        var item = new WorkItem { Id = ++_ids, Source = "shortcut", ExternalId = $"sc-{_ids}", Title = title, Repo = repo, CreatedAt = T0 };
        var history = new List<LedgerEntry>();
        WorkState? from = null;
        var state = WorkState.Intake;
        foreach (var (row, i) in rows.Select((r, i) => (r, i)))
        {
            var (step, detail) = row is string s ? (s[..s.IndexOf(':')], s[(s.IndexOf(':') + 1)..]) : ((string?)null, (string?)null);
            if (row is WorkState next)
            {
                state = next;
            }
            history.Add(new LedgerEntry
            {
                Id = ++_ids, WorkItemId = item.Id, State = state, Step = step, Detail = detail, RecordedAt = T0.AddHours(i),
                Outcome = StepOutcomes.Of(from, state, step, detail),
            });
            from = step is null ? state : from;
        }
        return new MetricItem(item, history, costs);
    }

    private static string Verdict(params (string Outcome, bool Downgraded)[] confirmations) => new ReviewVerdict("sha", ReviewVerdict.Fail, "s", [],
    [
        new RoleReview(ReviewRoles.Correctness, "m", "m", null, null,
            confirmations.Select(c => new Finding(c.Downgraded ? Finding.Optional : Finding.Blocking, "t", null, null, "d",
                new Confirmation(c.Outcome, "c", "c", null, null, "r"), c.Downgraded)).ToList(), "s"),
    ]).ToDetail();

    private static readonly object[] MergedAfterOneFixRound =
    [
        WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate,
        WorkState.Merge, WorkState.Watch,
    ];

    [Fact]
    public void Every_metric_over_no_items_is_not_available()
    {
        var metrics = LedgerMetrics.Compute([], Options);

        Assert.Equal(6, metrics.Count);
        Assert.All(metrics, m =>
        {
            Assert.Null(m.Value);
            Assert.Equal("N/A", m.Display);
            Assert.Equal(0, m.Sample);
        });
    }

    [Fact]
    public void Sandbox_and_demo_items_are_left_out_so_their_data_alone_measures_nothing()
    {
        var sandbox = Item("ACME/Sandbox", "Real work", [1.50m], [.. MergedAfterOneFixRound]);
        var demo = Item("acme/widgets", "[Demo] show the gate", [2m], [.. MergedAfterOneFixRound]);
        var escalatedDemo = Item("acme/widgets", "a [demo] escalation", [], WorkState.Intake, WorkState.Implement, WorkState.Escalated);

        var metrics = LedgerMetrics.Compute([sandbox, demo, escalatedDemo], Options);

        Assert.All(metrics, m => Assert.Equal("N/A", m.Display));
    }

    [Fact]
    public void The_metrics_count_only_real_items_and_what_their_ledger_measured()
    {
        var merged = Item("acme/widgets", "Fix words", [1.00m, 0.50m], [.. MergedAfterOneFixRound,
            $"{RunPipeline.Steps.Verdict}:{Verdict((Confirmation.Confirmed, false), (Confirmation.NotConfirmed, true), (Confirmation.Unusable, false))}"]);
        var unmeasured = Item("acme/widgets", "Merged, cost unknown", [0.25m, null], WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI,
            WorkState.MergeGate, WorkState.Merge);
        var escalated = Item("acme/widgets", "Escalated", [3m], WorkState.Intake, WorkState.Implement, WorkState.Escalated);
        var sandbox = Item("acme/sandbox", "Sandbox", [100m], [.. MergedAfterOneFixRound, WorkState.Escalated,
            $"{RunPipeline.Steps.Verdict}:{Verdict((Confirmation.NotConfirmed, true))}"]);

        var metrics = LedgerMetrics.Compute([merged, unmeasured, escalated, sandbox], Options);

        // Cost: only the merged item whose every session is priced ($1.50); the other merged one is unmeasured, not $0.
        var cost = Get(metrics, LedgerMetrics.CostPerMergedPr);
        Assert.Equal((1.5, 1, "$1.50"), (cost.Value, cost.Sample, cost.Display));
        Assert.Null(Get(metrics, LedgerMetrics.RevertRate14Days).Value);
        var escalation = Get(metrics, LedgerMetrics.EscalationRate);
        Assert.Equal((1d / 3, 3), (escalation.Value, escalation.Sample));
        var rounds = Get(metrics, LedgerMetrics.FixRoundsPerPr);
        Assert.Equal((0.5, 2), (rounds.Value, rounds.Sample));
        // One confirmed and one not-confirmed blocking finding were judged; the unusable answer measures nothing.
        var precision = Get(metrics, LedgerMetrics.ReviewerPrecision);
        Assert.Equal((0.5, 2, "50%"), (precision.Value, precision.Sample, precision.Display));
        var time = Get(metrics, LedgerMetrics.IntakeToMerge);
        Assert.Equal((6d, 2), (time.Value, time.Sample)); // merges 7 h and 5 h after intake
    }

    [Fact]
    public async Task The_metrics_read_the_ledger_and_the_recorded_session_costs()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var db = new LedgerDbContext(options))
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Fix", "acme/widgets", null, CancellationToken.None);
            foreach (var state in new[] { WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge })
            {
                await ledger.RecordAsync(item, state, null, null, CancellationToken.None);
            }
            db.WorkerSessions.Add(new WorkerSession { WorkItemId = item.Id, Attempt = 1, CostUsd = 0.75m });
            var sandbox = await ledger.GetOrCreateAsync("shortcut", "sc-2", "Fix", "acme/sandbox", null, CancellationToken.None);
            db.WorkerSessions.Add(new WorkerSession { WorkItemId = sandbox.Id, Attempt = 1, CostUsd = 9m });
            await db.SaveChangesAsync();
        }

        var metrics = await new LedgerMetrics(new LedgerDbContextFactory(options), Options).ComputeAsync(CancellationToken.None);

        Assert.Equal(("$0.75", 1), (Get(metrics, LedgerMetrics.CostPerMergedPr).Display, Get(metrics, LedgerMetrics.CostPerMergedPr).Sample));
        Assert.Equal("0%", Get(metrics, LedgerMetrics.EscalationRate).Display);
        Assert.Equal("N/A", Get(metrics, LedgerMetrics.ReviewerPrecision).Display);
    }
}
