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
    /// Whether the factory merged an item: its ledger has a Merge row — its change went through the merge gate, into whatever base
    /// its pull request targets. <paramref name="history"/> is the item's ledger rows, null when the ledger does not know it.
    /// </summary>
    public static bool Satisfied(IReadOnlyList<LedgerEntry>? history) =>
        history?.Any(e => e.Step is null && e.State == WorkState.Merge) == true;

    /// <summary>
    /// The one place a blocker counts as satisfied. A blocker that is a story of the plan (<paramref name="ofPlan"/>) is satisfied
    /// only by its ledger Merge row: one still in flight, one that escalated or was cancelled, and one a person finished by hand on
    /// the board are all unsatisfied. A blocker outside the plan is satisfied by its Merge row or once the board shows it finished
    /// (<paramref name="finishedOnBoard"/>, <see cref="WorkSources.Blocker.Finished"/>: done, not archived): it is work the factory
    /// may never do, and a person finishing it is the natural release.
    /// </summary>
    public static bool Satisfied(IReadOnlyList<LedgerEntry>? history, bool ofPlan, bool finishedOnBoard) =>
        Satisfied(history) || (!ofPlan && finishedOnBoard);

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

    /// <summary>
    /// Why a story's claim is refused while <paramref name="unsatisfied"/> block it: it names each blocker, and for those of them
    /// that are not stories of its plan (<paramref name="outside"/>) what a person can do about it.
    /// </summary>
    public static string Refusal(IReadOnlyList<string> unsatisfied, IReadOnlyList<string>? outside = null) =>
        $"{BlockedPrefix}{string.Join(", ", unsatisfied)} (not merged): a story of a plan starts only once the factory has merged every story of the plan that blocks it"
        + (outside is { Count: > 0 } ? $"; {string.Join(", ", outside)}: {OutsideRemedy}" : "");

    /// <summary>What a blocker outside the plan is and what releases its dependent, as a held story's record and a claim refusal say it.</summary>
    public const string OutsideRemedy = "not a story of this plan, and neither merged by the factory nor finished on the board: finish it or remove the relation on the board";

    /// <summary>The claim refusal of a planner-authored story the ledger links to no plan (<see cref="RunPipeline.ClaimRefused"/> precedes it).</summary>
    public static string PlanUnfinishedRefusal(string? plan) =>
        $"{PlanUnfinishedPrefix}{plan ?? "(not named)"} has not finished creating its stories: the story starts once the plan's run has recorded them";

    private const string PlanUnfinishedPrefix = "its plan ";

    /// <summary>Whether a Paused row's detail is a claim refused because the story's plan is unfinished (<see cref="PlanUnfinishedRefusal"/>).</summary>
    public static bool IsPlanUnfinishedRefusal(string? detail) =>
        detail?.StartsWith($"{RunPipeline.ClaimRefused}: {PlanUnfinishedPrefix}", StringComparison.Ordinal) == true;

    /// <summary>Whether a Paused row's detail is a claim refused for an unsatisfied blocker (<see cref="Refusal"/>).</summary>
    public static bool IsBlockedRefusal(string? detail) =>
        detail?.StartsWith($"{RunPipeline.ClaimRefused}: {BlockedPrefix}", StringComparison.Ordinal) == true;

    /// <summary>
    /// The stories a blocker outside the plan holds, as the plan item records them (<c>plan-held</c>): empty when none is. The plan's
    /// own unmerged stories are not listed: they are the plan's normal order.
    /// </summary>
    public static string HeldByOutsiders(IEnumerable<HeldStory> held) =>
        string.Join("; ", held.Where(h => h.Outside.Count > 0)
            .Select(h => $"{h.Id} held by {string.Join(", ", h.Outside)} ({OutsideRemedy})"));

    /// <summary>The <c>plan-held</c> record of a story that cannot be released because it is archived on its board.</summary>
    public static string HeldArchived(string story) =>
        $"{story} is archived on the board, so it was not released and the plan cannot complete: unarchive it";

    /// <summary>The <c>plan-held</c> record of a story the plan released that waits in the backlog again (a person moved it back).</summary>
    public static string HeldBackInBacklog(string story) =>
        $"{story} was released and is back in the backlog: nothing picks it up there and the plan does not release it again; moving it to the board's ready state restarts it";

    /// <summary>The words a <c>plan-held</c> row starts with when the plan could not be evaluated at all (<see cref="RunPipeline.GiveUpAsync"/>).</summary>
    public const string NotEvaluatedPrefix = "not evaluated: ";

    /// <summary>
    /// The plan's own blockers of each story (<paramref name="stories"/>: the ids <c>plan-stories</c> lists, in plan order), from the
    /// plan its <c>plan</c> checkpoint holds; null when that checkpoint does not read back as a plan of those stories (then
    /// nothing is known and the board is asked about every story).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? RecordedBlockers(string? planDetail, IReadOnlyList<string> stories)
    {
        Plan plan;
        try
        {
            plan = Plan.FromDetail(planDetail);
        }
        catch (PlanFormatException)
        {
            return null;
        }
        if (plan.Stories.Count != stories.Count)
        {
            return null;
        }
        var id = plan.Stories.Select((s, i) => (s.Key, Id: stories[i])).ToDictionary(x => x.Key, x => x.Id, StringComparer.Ordinal);
        return plan.Stories.ToDictionary(s => id[s.Key],
            s => (IReadOnlyList<string>)s.BlockedBy.Where(id.ContainsKey).Select(b => id[b]).ToList(), StringComparer.Ordinal);
    }
}
