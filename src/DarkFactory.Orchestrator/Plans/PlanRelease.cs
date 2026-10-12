using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.Orchestrator.Plans;

/// <summary>A story of a plan that is not released yet because of <paramref name="By"/>, its unsatisfied blockers.</summary>
/// <param name="Outside">Those of <paramref name="By"/> that are not stories of the plan (someone related them on the board).</param>
public sealed record HeldStory(string Id, IReadOnlyList<string> By, IReadOnlyList<string> Outside);

/// <summary>What one evaluation of a waiting plan decided (<see cref="PlanRelease.Decide"/>).</summary>
/// <param name="Release">The stories to move to ready now, in plan order.</param>
/// <param name="Held">The stories that stay in the backlog, each with the blockers holding it.</param>
/// <param name="Complete">Every story of the plan has merged.</param>
public sealed record ReleaseDecision(IReadOnlyList<string> Release, IReadOnlyList<HeldStory> Held, bool Complete);

/// <summary>
/// Dependency order for a plan's stories (sc-25871, E7), decided by code from ledger and board facts: which stories of a waiting plan
/// are released, which are held and by what, and whether the plan is complete. Pure: the pipeline reads the facts
/// (<see cref="RunPipeline"/>'s Planned evaluation and its claim check) and acts on the answer.
/// </summary>
public static class PlanRelease
{
    /// <summary>The words a blocked story's claim refusal starts with (after <see cref="RunPipeline.ClaimRefused"/>).</summary>
    public const string BlockedPrefix = "blocked by ";

    /// <summary>The story ids a <c>plan-stories</c> (or <c>plan-released</c>) checkpoint lists, in its order.</summary>
    public static IReadOnlyList<string> StoriesOf(string? detail) =>
        (detail ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The one place a blocker counts as satisfied: the factory's ledger has its Merge row — its change went through the merge gate,
    /// into whatever base its pull request targets. A blocker the ledger has never seen, one still in flight, one that escalated or
    /// was cancelled, and one a person finished by hand on the board are all unsatisfied: nothing but a recorded merge releases a
    /// dependent. <paramref name="history"/> is the blocker's ledger rows, null when the ledger does not know it.
    /// </summary>
    public static bool Satisfied(IReadOnlyList<LedgerEntry>? history) =>
        history?.Any(e => e.Step is null && e.State == WorkState.Merge) == true;

    /// <summary>
    /// Decides a waiting plan. <paramref name="stories"/> are the plan's, in plan order; <paramref name="released"/> those a
    /// <c>plan-released</c> checkpoint already names (never released twice); <paramref name="blockers"/> what the board says blocks
    /// each story that is neither merged nor released (a story missing from it has none); <paramref name="satisfied"/> answers
    /// <see cref="Satisfied"/> for any id, a story of the plan or not. A blocker outside the plan holds a story exactly like one
    /// inside it.
    /// </summary>
    public static ReleaseDecision Decide(IReadOnlyList<string> stories, IReadOnlySet<string> released,
        IReadOnlyDictionary<string, IReadOnlyList<string>> blockers, Func<string, bool> satisfied)
    {
        var release = new List<string>();
        var held = new List<HeldStory>();
        foreach (var story in stories.Where(s => !satisfied(s) && !released.Contains(s)))
        {
            var open = blockers.TryGetValue(story, out var by) ? by.Where(b => !satisfied(b)).Distinct(StringComparer.Ordinal).ToList() : [];
            if (open.Count == 0)
            {
                release.Add(story);
            }
            else
            {
                held.Add(new HeldStory(story, open, open.Where(b => !stories.Contains(b, StringComparer.Ordinal)).ToList()));
            }
        }
        return new ReleaseDecision(release, held, stories.Count > 0 && stories.All(satisfied));
    }

    /// <summary>Why a story's claim is refused while <paramref name="unsatisfied"/> block it: it names each blocker.</summary>
    public static string Refusal(IReadOnlyList<string> unsatisfied) =>
        $"{BlockedPrefix}{string.Join(", ", unsatisfied)} (not merged): a story of a plan starts only once the factory has merged every story that blocks it";

    /// <summary>Whether a Paused row's detail is a claim refused for an unsatisfied blocker (<see cref="Refusal"/>).</summary>
    public static bool IsBlockedRefusal(string? detail) =>
        detail?.StartsWith($"{RunPipeline.ClaimRefused}: {BlockedPrefix}", StringComparison.Ordinal) == true;

    /// <summary>
    /// The stories a blocker outside the plan holds, as the plan item records them (<c>plan-held</c>): empty when none is. The plan's
    /// own unmerged stories are not listed: they are the plan's normal order.
    /// </summary>
    public static string HeldByOutsiders(IEnumerable<HeldStory> held) =>
        string.Join("; ", held.Where(h => h.Outside.Count > 0)
            .Select(h => $"{h.Id} held by {string.Join(", ", h.Outside)} (not a story of this plan, not merged by the factory)"));
}
