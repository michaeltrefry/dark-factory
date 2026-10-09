using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// P2-AT2's invariants (sc-25378, sc-25391) over an item's ledger, after a push moved the PR head right after its first review:
/// the verdict is bound to the head it judged, so a push sends the item back to Review and only a head with a passing verdict
/// reaches the merge. Which transitions get there may vary legitimately (a fix round may run after either review), so this
/// checks the invariants, not one exact transition list.
/// </summary>
internal static class PushAfterVerdict
{
    /// <summary>
    /// What breaks the invariants in <paramref name="history"/> (empty: they hold), where <paramref name="reviewedFirst"/> is the
    /// head the first review judged, before the push.
    /// </summary>
    public static IReadOnlyList<string> Problems(List<LedgerEntry> history, string reviewedFirst)
    {
        var problems = new List<string>();
        bool IsVerdictOn(LedgerEntry e, string sha) => e.Step == RunPipeline.Steps.Verdict && ReviewVerdict.FromDetail(e.Detail)?.HeadSha == sha;

        var first = history.FindIndex(e => IsVerdictOn(e, reviewedFirst));
        if (first < 0)
        {
            problems.Add($"no verdict on the pre-push head {reviewedFirst}");
        }
        else if (!history.Skip(first + 1).Any(e => e.Step is null && e.State == WorkState.Review))
        {
            problems.Add($"the item did not go back to Review after the verdict on the pre-push head {reviewedFirst}");
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
            else if (!history.Take(index).Any(e => IsVerdictOn(e, sha) && ReviewVerdict.FromDetail(e.Detail)!.Passed))
            {
                problems.Add($"gate-passed names {sha}, which had no passing verdict before it");
            }
        }
        return problems;
    }
}
