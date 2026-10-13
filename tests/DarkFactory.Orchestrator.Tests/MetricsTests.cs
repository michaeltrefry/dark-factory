using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

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
        new RoleReview(ReviewRoles.Correctness, "m", "high", null, null,
            confirmations.Select(c => new Finding(c.Downgraded ? Finding.Optional : Finding.Blocking, "t", null, null, "d",
                new Confirmation(c.Outcome, "c", "high", null, null, "r"), c.Downgraded)).ToList(), "s"),
    ]).ToDetail();

    private static readonly object[] MergedAfterOneFixRound =
    [
        WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate,
        WorkState.Merge, WorkState.Watch,
    ];

    // ---- a plan item of several repositories (sc-25873): one Merge row per changeset pull request ----

    private static string[] ChangesetIn(string repo) =>
    [
        $"{RunPipeline.Steps.Changeset}:{new Plans.Changeset(repo, "factory/changeset-sc-9", "main", "b0").ToDetail()}",
        $"{RunPipeline.Steps.ChangesetPushed}:{Plans.Changeset.PushedDetail("factory/changeset-sc-9", "b0", false)}",
    ];

    private static object[] Pass(string repo) =>
    [
        WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge,
        $"{RunPipeline.Steps.ChangesetDelivered}:{new Plans.ChangesetDelivery(repo, Plans.ChangesetDelivery.Merged).ToDetail()}",
    ];

    private static object[] PlanOf(params string[] repos) => [WorkState.Intake, WorkState.Plan, .. repos.SelectMany(ChangesetIn), WorkState.Planned];

    [Fact]
    public void A_plan_of_several_repositories_is_merged_only_at_its_last_changesets_merge()
    {
        var half = Item("acme/up", "Share the format", [], [.. PlanOf("acme/up", "acme/down"), .. Pass("acme/up"), WorkState.Planned]);

        var metrics = LedgerMetrics.Compute([half], Options);

        // One repository's changeset is on its default branch; the plan is not merged, and may never be.
        Assert.Equal(0, Get(metrics, LedgerMetrics.FixRoundsPerPr).Sample);
        Assert.Equal("N/A", Get(metrics, LedgerMetrics.IntakeToMerge).Display);

        object[] rows = [.. PlanOf("acme/up", "acme/down"), .. Pass("acme/up"), WorkState.Planned, .. Pass("acme/down"), WorkState.Watch];
        var whole = Item("acme/up", "Share the format", [], rows);

        metrics = LedgerMetrics.Compute([whole], Options);

        Assert.Equal(1, Get(metrics, LedgerMetrics.FixRoundsPerPr).Sample);
        // Measured to the second repository's Merge row, not the first's.
        var last = Array.FindLastIndex(rows, r => r is WorkState.Merge);
        Assert.NotEqual(last, Array.FindIndex(rows, r => r is WorkState.Merge));
        Assert.Equal((double)last, Get(metrics, LedgerMetrics.IntakeToMerge).Value);
    }

    [Fact]
    public void A_plan_with_a_changeset_in_a_sandbox_repository_is_left_out_whichever_repository_the_item_names()
    {
        // The plan item's own repository is not the sandbox; one of the repositories it changes is.
        var plan = Item("acme/widgets", "Share the format", [],
            [.. PlanOf("acme/widgets", "ACME/Sandbox"), .. Pass("acme/widgets"), WorkState.Planned, .. Pass("ACME/Sandbox"), WorkState.Watch]);

        var metrics = LedgerMetrics.Compute([plan], Options);

        Assert.All(metrics, m => Assert.Equal("N/A", m.Display));
        // The same plan in no sandbox repository is measured.
        var real = Item("acme/widgets", "Share the format", [],
            [.. PlanOf("acme/widgets", "acme/other"), .. Pass("acme/widgets"), WorkState.Planned, .. Pass("acme/other"), WorkState.Watch]);
        Assert.Equal(1, Get(LedgerMetrics.Compute([real], Options), LedgerMetrics.FixRoundsPerPr).Sample);
    }

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

    [Fact]
    public async Task The_dashboard_computes_the_metrics_at_most_once_per_ttl()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var time = new FakeTimeProvider(T0);
        var data = new DashboardData(new LedgerDbContextFactory(options), time, metrics: Options);
        async Task StartItemAsync(string id)
        {
            await using var db = new LedgerDbContext(options);
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync("shortcut", id, "Fix", "acme/widgets", null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        }
        int Started(IReadOnlyList<Metric> metrics) => Get(metrics, LedgerMetrics.EscalationRate).Sample;

        await StartItemAsync("sc-1");
        Assert.Equal(1, Started(await data.MetricsAsync(CancellationToken.None)));
        await StartItemAsync("sc-2");

        // Within the TTL the ledger change is not recomputed; once it has passed it is.
        time.Advance(DashboardData.DefaultMetricsTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Started(await data.MetricsAsync(CancellationToken.None)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Started(await data.MetricsAsync(CancellationToken.None)));
    }
}
