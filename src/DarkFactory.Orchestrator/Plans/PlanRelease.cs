using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Plans;

/// <summary>A story of a plan that is not released yet because of <paramref name="By"/>, its unsatisfied blockers.</summary>
/// <param name="Outside">Those of <paramref name="By"/> that are not stories of the plan (someone related them on the board).</param>
public sealed record HeldStory(string Id, IReadOnlyList<string> By, IReadOnlyList<string> Outside);

/// <summary>What one evaluation of a waiting plan decided (<see cref="PlanRelease.Decide"/>).</summary>
/// <param name="Release">The stories to move to ready now, in plan order.</param>
/// <param name="Held">The stories that stay in the backlog, each with the blockers holding it.</param>
/// <param name="Complete">Every story of the plan has merged.</param>
public sealed record ReleaseDecision(IReadOnlyList<string> Release, IReadOnlyList<HeldStory> Held, bool Complete);

/// <summary>Where one story of a plan stands (sc-25876), from the ledger: <see cref="PlanRelease.Classify"/>.</summary>
public enum PlanStoryState
{
    /// <summary>The factory merged it (its ledger has a Merge row).</summary>
    Merged,
    /// <summary>Released by the plan, or known to the ledger and neither merged nor failed: it is being worked or about to be.</summary>
    InFlight,
    /// <summary>Not released yet: it waits on blockers none of which is failed (or on nothing: the next evaluation releases it).</summary>
    Waiting,
    /// <summary>Its work item's last state is Escalated or Cancelled and it has not merged: it satisfies no blocker until a person acts.</summary>
    Failed,
    /// <summary>Not released, and never will be while a failed story it depends on (directly or through others) stays failed.</summary>
    Blocked,
}

/// <summary>How a story of a plan failed: its item's last transition (<paramref name="As"/>: <see cref="PlanRelease.AsEscalated"/>, …).</summary>
/// <param name="Reason">That row's detail (the escalation reason, or who stopped it), as the ledger holds it.</param>
public sealed record StoryFailure(string As, string? Reason, DateTimeOffset At);

/// <summary>One story of a plan as <see cref="PlanRelease.Classify"/> found it.</summary>
/// <param name="WaitsOn">Its blockers that are not satisfied (empty for a merged, failed or in-flight story).</param>
/// <param name="HeldBy">The failed stories of the plan it is blocked behind, directly or through others (<see cref="PlanStoryState.Blocked"/>).</param>
/// <param name="ItemState">Its work item's state, null when the ledger does not know it.</param>
public sealed record PlanStoryStatus(string Id, PlanStoryState State, IReadOnlyList<string> WaitsOn, IReadOnlyList<string> HeldBy,
    StoryFailure? Failure, WorkState? ItemState);

/// <summary>Every story of a plan, classified (sc-25876).</summary>
public sealed record PlanStatus(IReadOnlyList<PlanStoryStatus> Stories)
{
    public int Count(PlanStoryState state) => Stories.Count(s => s.State == state);

    public IReadOnlyList<PlanStoryStatus> Failed => [.. Stories.Where(s => s.State == PlanStoryState.Failed)];

    public IReadOnlyList<PlanStoryStatus> Blocked => [.. Stories.Where(s => s.State == PlanStoryState.Blocked)];

    /// <summary>A failed story holds the plan: its dependents are not released until it merges.</summary>
    public bool Held => Stories.Any(s => s.State == PlanStoryState.Failed);

    /// <summary>
    /// Every story that has not merged is failed or blocked behind a failed one: nothing of the plan moves until a person acts
    /// (re-runs the failed story, or stops the plan).
    /// </summary>
    public bool Stuck => Held && Stories.All(s => s.State is PlanStoryState.Merged or PlanStoryState.Failed or PlanStoryState.Blocked);
}

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

    /// <summary>How a failed story's item ended (<see cref="StoryFailure.As"/>).</summary>
    public const string AsEscalated = "escalated";
    public const string AsCancelled = "cancelled";
    /// <summary>Escalated because its pull request was merged by someone else (the review or the merge queue found it merged).</summary>
    public const string AsMergedOutside = "merged outside the factory";

    /// <summary>What the pipeline's escalation reason says when it found the item's pull request merged by someone else.</summary>
    private static readonly string[] MergedOutsideMarkers = ["is merged outside the factory", "was merged outside the gate"];

    /// <summary>The most of a failed story's reason a <c>plan-held</c> row and the plan item's comment repeat (the story's own ledger has it all).</summary>
    public const int MaxFailureReason = 300;

    /// <summary>
    /// How the story failed, or null when it did not: its ledger rows (<paramref name="history"/>) have no Merge row and its last
    /// transition is Escalated or Cancelled. A story a person re-queued after an escalation is no longer failed (its last transition is
    /// Intake or later), and one that merged never is.
    /// </summary>
    public static StoryFailure? FailureOf(IReadOnlyList<LedgerEntry>? history)
    {
        if (history is null || Satisfied(history)
            || history.LastOrDefault(e => e.Step is null) is not { State: WorkState.Escalated or WorkState.Cancelled } last)
        {
            return null;
        }
        var how = last.State == WorkState.Cancelled ? AsCancelled
            : MergedOutsideMarkers.Any(m => last.Detail?.Contains(m, StringComparison.Ordinal) == true) ? AsMergedOutside
            : AsEscalated;
        return new StoryFailure(how, last.Detail, last.RecordedAt);
    }

    /// <summary>
    /// Classifies every story of a plan (sc-25876), from ledger facts alone. <paramref name="stories"/> are the plan's, in plan order;
    /// <paramref name="released"/> those a <c>plan-released</c> checkpoint names; <paramref name="blockers"/> answers what blocks a
    /// story (the plan's own order, and for a story that order no longer holds whatever else the caller knows); <paramref name="history"/>
    /// a story's ledger rows (at least its transitions), null when the ledger does not know it; <paramref name="satisfied"/> whether a
    /// blocker that is not a story of the plan is satisfied (a story of the plan is satisfied by its Merge row only).
    /// A failed story (<see cref="FailureOf"/>) satisfies nothing, so every story that depends on it, directly or through others, is
    /// <see cref="PlanStoryState.Blocked"/>; the stories that do not depend on it are classified as if it were not there.
    /// </summary>
    public static PlanStatus Classify(IReadOnlyList<string> stories, IReadOnlySet<string> released,
        Func<string, IReadOnlyList<string>> blockers, Func<string, IReadOnlyList<LedgerEntry>?> history, Func<string, bool>? satisfied = null)
    {
        var ofPlan = stories.ToHashSet(StringComparer.Ordinal);
        var rows = stories.Distinct(StringComparer.Ordinal).ToDictionary(s => s, history, StringComparer.Ordinal);
        var failures = rows.ToDictionary(r => r.Key, r => FailureOf(r.Value), StringComparer.Ordinal);
        bool Merged(string id) => Satisfied(rows[id]);
        bool Open(string blocker) => ofPlan.Contains(blocker) ? !Merged(blocker) : satisfied?.Invoke(blocker) != true;

        // The failed stories a story is blocked behind: through unmerged stories of the plan only (a merged blocker blocks nothing).
        var behind = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IReadOnlyList<string> Behind(string story, HashSet<string> path)
        {
            if (behind.TryGetValue(story, out var known))
            {
                return known;
            }
            if (!path.Add(story))
            {
                return []; // a cycle (only relations a person added on the board can make one): not followed
            }
            var by = new List<string>();
            foreach (var blocker in blockers(story).Where(b => ofPlan.Contains(b) && !Merged(b)))
            {
                by.AddRange(failures[blocker] is not null ? [blocker] : Behind(blocker, path));
            }
            path.Remove(story);
            return behind[story] = [.. by.Distinct(StringComparer.Ordinal)];
        }

        return new PlanStatus([.. stories.Select(story =>
        {
            var last = rows[story]?.LastOrDefault(e => e.Step is null);
            var state = last?.State;
            if (Merged(story))
            {
                return new PlanStoryStatus(story, PlanStoryState.Merged, [], [], null, state);
            }
            if (failures[story] is { } failure)
            {
                return new PlanStoryStatus(story, PlanStoryState.Failed, [], [], failure, state);
            }
            // A story parked on a claim refused for its blockers (moved to ready by hand) still only waits.
            var parked = state == WorkState.Paused && IsBlockedRefusal(last!.Detail);
            if ((state is not null && !parked) || (state is null && released.Contains(story)))
            {
                return new PlanStoryStatus(story, PlanStoryState.InFlight, [], [], null, state);
            }
            var open = blockers(story).Where(Open).Distinct(StringComparer.Ordinal).ToList();
            var heldBy = Behind(story, []);
            return new PlanStoryStatus(story, heldBy.Count > 0 ? PlanStoryState.Blocked : PlanStoryState.Waiting, open, heldBy, null, state);
        })]);
    }

    /// <summary>The words a <c>plan-held</c> row starts with when a failed story of the plan holds it (<see cref="HeldByFailed"/>).</summary>
    public const string FailedPrefix = "held by a failed story: ";

    /// <summary>
    /// What a failed story's hold on the plan is recorded as (<c>plan-held</c> after <see cref="FailedPrefix"/>, or <c>plan-stuck</c>),
    /// empty when no story is failed: each failed story with how and when it failed and its reason (clipped, inert), then the
    /// stories not released because of them. It changes exactly when the failed stories (or a failure itself) or what they block do,
    /// so a row is written only then.
    /// </summary>
    public static string HeldByFailed(PlanStatus status) =>
        !status.Held ? "" : string.Join("; ", status.Failed.Select(f =>
                $"{f.Id} {f.Failure!.As} at {f.Failure.At.UtcDateTime:u}: {FailureReason(f.Failure)}"))
            + (status.Blocked.Count == 0 ? "" : "; not released because of them: "
                + string.Join(", ", status.Blocked.Select(b => $"{b.Id} (behind {string.Join(", ", b.HeldBy)})")));

    /// <summary>A failure's reason as a record and a comment repeat it: clipped, one inert line in a code span (model and repo text may be in it).</summary>
    public static string FailureReason(StoryFailure failure) =>
        UntrustedText.CodeSpan(LedgerReport.Clip(string.IsNullOrWhiteSpace(failure.Reason) ? "none recorded" : failure.Reason, MaxFailureReason));

    /// <summary>What a person can do about a held or stuck plan, as its comment says it.</summary>
    public static string FailedRemedy(string plan, PlanStatus status)
    {
        var rerun = status.Failed.Where(f => f.Failure!.As != AsCancelled).Select(f => $"`factory run {f.Id}`").ToList();
        var cancelled = status.Failed.Where(f => f.Failure!.As == AsCancelled).Select(f => f.Id).ToList();
        return (rerun.Count == 0 ? "" : $"fix what failed and re-run the story ({string.Join(", ", rerun)}): once it has merged its dependents are released; or ")
            + $"stop the plan (`factory stop --item {plan}`: its unfinished stories are stopped, merged ones stay)"
            + (cancelled.Count == 0 ? "" : $". A cancelled story ({string.Join(", ", cancelled)}) cannot be re-run: what depends on it is only ever ended by stopping the plan");
    }

    /// <summary>The comment the plan item gets when a failed story starts to hold it, when which do changes, and once when it is stuck.</summary>
    public static string HeldComment(string plan, PlanStatus status)
    {
        var text = new System.Text.StringBuilder();
        text.Append(WorkSourceComments.Author).Append(' ').Append(plan).Append(status.Stuck
            ? " (plan) is stuck: every story of it that has not merged failed or is blocked behind a failed one, so nothing of it moves until a human acts.\n\n"
            : " (plan) is held by a failed story: what depends on it is not released; the stories that do not depend on it go on.\n\n");
        text.Append("Failed:\n");
        foreach (var failed in status.Failed)
        {
            text.Append($"- {failed.Id} {failed.Failure!.As} at {failed.Failure.At.UtcDateTime:u}: {FailureReason(failed.Failure)}\n");
        }
        if (status.Blocked.Count > 0)
        {
            text.Append("\nBlocked behind them (not released):\n");
            foreach (var blocked in status.Blocked)
            {
                text.Append($"- {blocked.Id} (behind {string.Join(", ", blocked.HeldBy)})\n");
            }
        }
        text.Append($"\n{Counts(status)}.\n\nRemedies: {FailedRemedy(plan, status)}.\n");
        text.Append($"{plan} stays Planned: the factory evaluates it every poll and goes on by itself once the failed story has merged.");
        return text.ToString();
    }

    /// <summary>The record (<c>plan-resumed</c>) of a plan no failed story holds any more.</summary>
    public static string Resumed(PlanStatus status) => $"no story of the plan is failed any more ({Counts(status)})";

    /// <summary>The comment the plan item gets once no failed story holds it any more.</summary>
    public static string ResumedComment(string plan, PlanStatus status) =>
        $"{WorkSourceComments.Author} {plan} (plan) is no longer held: no story of it is failed any more ({Counts(status)}). "
        + "The stories that waited behind the failed one are released as their blockers merge.";

    /// <summary>The plan's stories by state, as its records and the dashboard say it.</summary>
    public static string Counts(PlanStatus status) =>
        $"{status.Count(PlanStoryState.Merged)} of {status.Stories.Count} merged, {status.Count(PlanStoryState.InFlight)} in flight, "
        + $"{status.Count(PlanStoryState.Waiting)} waiting, {status.Count(PlanStoryState.Failed)} failed, "
        + $"{status.Count(PlanStoryState.Blocked)} blocked behind a failed story";

    /// <summary>
    /// The claim refusal of a story whose plan was stopped (sc-25876): it starts like a blocker's refusal (<see cref="BlockedPrefix"/>),
    /// so the item is parked once and nothing more is written however often the board lists it (<see cref="IsBlockedRefusal"/>).
    /// </summary>
    public static string PlanStoppedRefusal(string plan) =>
        $"{BlockedPrefix}its plan {plan}, which was stopped: a story of a stopped plan is not started (write a story of your own for the work if it is still wanted)";

    /// <summary>The words a <c>plan-held</c> row starts with when the plan could not be evaluated at all (<see cref="RunPipeline.GiveUpAsync"/>).</summary>
    public const string NotEvaluatedPrefix = "not evaluated: ";

    /// <summary>
    /// The plan's own blockers of each story (<paramref name="stories"/>: the ids <c>plan-stories</c> lists, in plan order), from the
    /// plan its <c>plan</c> checkpoint holds; null when that checkpoint does not read back as a plan of those stories (then
    /// nothing is known and the board is asked about every story).
    /// </summary>
    /// <summary>
    /// The title the plan gave each story (<paramref name="stories"/> as for <see cref="RecordedBlockers"/>), from its <c>plan</c>
    /// checkpoint; null when that does not read back as a plan of those stories. Planner-written text: render it inert.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? RecordedTitles(string? planDetail, IReadOnlyList<string> stories)
    {
        try
        {
            var plan = Plan.FromDetail(planDetail);
            return plan.Stories.Count != stories.Count ? null
                : plan.Stories.Select((s, i) => (Id: stories[i], s.Title)).ToDictionary(x => x.Id, x => x.Title, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is PlanFormatException or ArgumentException)
        {
            return null;
        }
    }

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
