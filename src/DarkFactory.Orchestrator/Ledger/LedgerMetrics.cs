using System.Globalization;
using DarkFactory.Orchestrator.Gate;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// One factory metric (E5): its value, computed only from ledger rows and recorded session costs, or null — shown as N/A, never 0 — when no
/// item it covers has the data. <see cref="Sample"/>: how many items (or findings) the value is over.
/// </summary>
public sealed record Metric(string Name, double? Value, MetricUnit Unit, int Sample, string Note)
{
    public const string NotAvailable = "N/A";

    /// <summary>The value as shown: N/A when unmeasured.</summary>
    public string Display => Value is not { } v ? NotAvailable : Unit switch
    {
        MetricUnit.Usd => v.ToString("$0.00##", CultureInfo.InvariantCulture),
        MetricUnit.Percent => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
        MetricUnit.Hours => v.ToString("0.#", CultureInfo.InvariantCulture) + " h",
        _ => v.ToString("0.##", CultureInfo.InvariantCulture),
    };
}

public enum MetricUnit
{
    Usd,
    Percent,
    Count,
    Hours,
}

/// <summary>
/// Which items the metrics leave out as sandbox or demo work (sc-25389): an item whose repo is one of <see cref="SandboxRepos"/>
/// (<c>Metrics:SandboxRepos</c>, default the factory's sandbox repo), or whose title carries one of <see cref="DemoMarkers"/>
/// (<c>Metrics:DemoMarkers</c>, default <c>[demo]</c> and <c>[sandbox]</c>: a marker put on the board item's name), case-insensitively.
/// </summary>
public sealed record MetricsOptions(IReadOnlyList<string> SandboxRepos, IReadOnlyList<string> DemoMarkers)
{
    public static readonly IReadOnlyList<string> DefaultSandboxRepos = ["michaeltrefry/dark-factory-sandbox"];
    public static readonly IReadOnlyList<string> DefaultDemoMarkers = ["[demo]", "[sandbox]"];

    public static readonly MetricsOptions Default = new(DefaultSandboxRepos, DefaultDemoMarkers);

    /// <summary>Whether <paramref name="item"/> is sandbox or demo work, which no metric counts.</summary>
    public bool Excludes(WorkItem item) =>
        SandboxRepos.Contains(item.Repo, StringComparer.OrdinalIgnoreCase)
        || DemoMarkers.Any(m => item.Title.Contains(m, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One item as the metrics see it: the item, every ledger row (oldest first) and each worker session's recorded cost.</summary>
public sealed record MetricItem(WorkItem Item, IReadOnlyList<LedgerEntry> History, IReadOnlyList<decimal?> SessionCosts);

/// <summary>
/// The factory's metrics over its ledger (E5, sc-25389): cost per merged PR, 14-day revert rate, escalation rate, fix rounds per merged PR,
/// reviewer precision and intake-to-merge time. Each comes only from executed results the ledger recorded, leaves out sandbox and demo items
/// (<see cref="MetricsOptions"/>), and is N/A when no item it covers has the data.
/// </summary>
public sealed class LedgerMetrics(IDbContextFactory<LedgerDbContext> contexts, MetricsOptions options)
{
    public const string CostPerMergedPr = "Cost per merged PR";
    public const string RevertRate14Days = "14-day revert rate";
    public const string EscalationRate = "Escalation rate";
    public const string FixRoundsPerPr = "Fix rounds per merged PR";
    public const string ReviewerPrecision = "Reviewer precision";
    public const string IntakeToMerge = "Intake-to-merge time";

    /// <summary>Reads every item, its rows and its session costs, and computes the metrics.</summary>
    public async Task<IReadOnlyList<Metric>> ComputeAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var items = await db.WorkItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync(ct);
        var rows = (await db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct)).ToLookup(e => e.WorkItemId);
        var costs = (await db.WorkerSessions.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.WorkItemId, s.CostUsd }).ToListAsync(ct))
            .ToLookup(s => s.WorkItemId, s => s.CostUsd);
        return Compute(items.Select(i => new MetricItem(i, rows[i.Id].ToList(), costs[i.Id].ToList())), options);
    }

    public static IReadOnlyList<Metric> Compute(IEnumerable<MetricItem> all, MetricsOptions options)
    {
        var items = all.Where(i => !options.Excludes(i.Item)).ToList();
        var merged = items.Select(i => (i, Merge: i.History.FirstOrDefault(e => e.Step is null && e.State == WorkState.Merge))).Where(x => x.Merge is not null).ToList();
        var started = items.Where(i => i.History.Any(e => e.Step is null && e.State == WorkState.Implement)).ToList();

        // A merged item's cost is measured only when every one of its worker sessions has a recorded cost.
        var costed = merged.Where(m => m.i.SessionCosts.Count > 0 && m.i.SessionCosts.All(c => c is not null))
            .Select(m => (double)m.i.SessionCosts.Sum(c => c!.Value)).ToList();
        var escalated = started.Count(i => i.History.Any(e => e.Outcome == StepOutcome.Escalated));
        var (confirmed, judged) = Precision(items);
        return
        [
            new(CostPerMergedPr, Mean(costed), MetricUnit.Usd, costed.Count,
                $"worker session cost (router, recorded) of {costed.Count} of {merged.Count} merged item(s) whose every session cost is recorded; review calls not included"),
            new(RevertRate14Days, null, MetricUnit.Percent, 0,
                "not measured: the factory records no reverts yet (nothing drives Watch), so there is no revert data"),
            new(EscalationRate, started.Count == 0 ? null : (double)escalated / started.Count, MetricUnit.Percent, started.Count,
                $"{escalated} of {started.Count} item(s) that reached Implement escalated at least once"),
            new(FixRoundsPerPr, Mean(merged.Select(m => (double)FixRounds(m.i.History)).ToList()), MetricUnit.Count, merged.Count,
                $"fix rounds (review, CI and conflict) over {merged.Count} merged item(s)"),
            new(ReviewerPrecision, judged == 0 ? null : (double)confirmed / judged, MetricUnit.Percent, judged,
                $"{confirmed} of {judged} blocking finding(s) a second model confirmed (not-confirmed ones were downgraded; unusable or unchecked answers not counted; carried reviews counted once)"),
            new(IntakeToMerge, Mean(merged.Select(m => (m.Merge!.RecordedAt - m.i.Item.CreatedAt).TotalHours).ToList()), MetricUnit.Hours, merged.Count,
                $"mean from intake to the merge row over {merged.Count} merged item(s)"),
        ];
    }

    private static double? Mean(IReadOnlyList<double> values) => values.Count == 0 ? null : values.Average();

    /// <summary>Every fix round the item's ledger records (across re-implementations).</summary>
    private static int FixRounds(IReadOnlyList<LedgerEntry> history)
    {
        var transitions = history.Where(e => e.Step is null).Select(e => e.State).ToList();
        return Enumerable.Range(1, Math.Max(transitions.Count - 1, 0)).Count(i => TransitionContext.IsFixRound(transitions[i - 1], transitions[i]));
    }

    /// <summary>
    /// Blocking findings a second model confirmed, over those it answered clearly (confirmed or not): every fresh review of every verdict
    /// row (a review carried from an earlier head is the same review, counted once).
    /// </summary>
    private static (int Confirmed, int Judged) Precision(IEnumerable<MetricItem> items)
    {
        var findings = items.SelectMany(i => i.History).Where(e => e.Step == RunPipeline.Steps.Verdict)
            .Select(e => ReviewVerdict.FromDetail(e.Detail)).OfType<ReviewVerdict>()
            .SelectMany(v => v.Reviews).Where(r => r.CarriedFrom is null)
            .SelectMany(r => r.Findings).Where(f => f.IsBlocking || f.Downgraded)
            .Select(f => f.Confirmation?.Outcome)
            .Where(o => o is Confirmation.Confirmed or Confirmation.NotConfirmed)
            .ToList();
        return (findings.Count(o => o == Confirmation.Confirmed), findings.Count);
    }
}
