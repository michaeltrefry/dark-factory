using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// P2-AT2's invariants (sc-25378, sc-25391) over an item's ledger, after the test pushed a commit right after the first review:
/// the verdict is bound to the head it judged, so the push sends the item from CI (or the merge gate) back to Review for the
/// pushed head, and only a head with a passing verdict reaches the merge. A fix round after the pushed head's review is
/// legitimate, so this checks invariants, not one exact transition list. When the ledger does not show the push-after-verdict
/// path at all (the first verdict failed, so a fix round rather than the push sent the item back), the problem is reported as
/// <see cref="Inconclusive"/>: the run proved nothing about it.
/// </summary>
internal static class PushAfterVerdict
{
    public const string Inconclusive = "INCONCLUSIVE";

    /// <summary>
    /// What breaks the invariants in <paramref name="history"/> (empty: they hold), where <paramref name="reviewedFirst"/> is the
    /// head the first review judged and <paramref name="pushed"/> the commit the test pushed onto it.
    /// </summary>
    public static IReadOnlyList<string> Problems(List<LedgerEntry> history, string reviewedFirst, string pushed)
    {
        var problems = new List<string>();
        ReviewVerdict? VerdictOf(LedgerEntry e) => e.Step == RunPipeline.Steps.Verdict ? ReviewVerdict.FromDetail(e.Detail) : null;

        var first = history.FindIndex(e => VerdictOf(e)?.HeadSha == reviewedFirst);
        if (first < 0)
        {
            problems.Add($"{Inconclusive}: no verdict on the pre-push head {reviewedFirst}");
        }
        else if (!VerdictOf(history[first])!.Passed)
        {
            problems.Add($"{Inconclusive}: the verdict on the pre-push head {reviewedFirst} failed, so no push voided a passing verdict");
        }
        else
        {
            // The push must be what sent the item back: the next transition to Review comes straight from CI or the merge gate
            // (no fix or CI-healing round between) and names the pushed head.
            var back = history.FindIndex(first + 1, e => e.Step is null && e.State == WorkState.Review);
            var between = history.Skip(first + 1).Take((back < 0 ? history.Count : back) - first - 1)
                .Where(e => e.Step is null && e.State is not (WorkState.CI or WorkState.MergeGate)).Select(e => e.State).ToList();
            if (back < 0)
            {
                problems.Add($"the item did not go back to Review after the passing verdict on the pre-push head {reviewedFirst}");
            }
            else if (between.Count > 0)
            {
                problems.Add($"{Inconclusive}: {string.Join(", ", between)} ran between the passing verdict on {reviewedFirst} and the next Review");
            }
            else if (history[back].Detail?.StartsWith($"head moved to {pushed}", StringComparison.Ordinal) != true)
            {
                problems.Add($"the Review after the passing verdict on {reviewedFirst} does not name the pushed head {pushed}: {history[back].Detail}");
            }
        }
        if (!history.Any(e => VerdictOf(e)?.HeadSha == pushed))
        {
            problems.Add($"no verdict on the pushed head {pushed}");
        }

        var passed = history.Select((e, i) => (Entry: e, Index: i)).Where(x => x.Entry.Step == RunPipeline.Steps.GatePassed).ToList();
        if (passed.Count == 0)
        {
            problems.Add("no gate-passed row: nothing was merged");
        }
        foreach (var (entry, index) in passed)
        {
            var sha = entry.Detail ?? "";
            if (sha == reviewedFirst)
            {
                problems.Add($"gate-passed names the pre-push head {sha}, whose verdict the push voided");
            }
            else if (!history.Take(index).Any(e => VerdictOf(e) is { Passed: true } v && v.HeadSha == sha))
            {
                problems.Add($"gate-passed names {sha}, which had no passing verdict before it");
            }
        }
        return problems;
    }
}
