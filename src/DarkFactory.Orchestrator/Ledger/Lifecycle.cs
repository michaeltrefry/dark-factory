namespace DarkFactory.Orchestrator.Ledger;

/// <summary>The spec's full work-item lifecycle. Stored by name, so members may be added but not renamed.</summary>
public enum WorkState
{
    Intake,
    Plan,
    /// <summary>
    /// A plan item whose stories are on the board (sc-25870): nothing implements it. Each poll releases the stories whose blockers
    /// have merged. Once every story has merged into the plan's changeset branch the item goes to <see cref="Review"/> with the
    /// changeset pull request as its own (sc-25872) and from there through the gate like any item; a plan with no changeset, or
    /// an empty one, is closed out and goes to <see cref="Done"/> (sc-25871). A plan of several repositories (sc-25873) does so
    /// once per repository, in the plan's merge order, and comes back here from <see cref="Merge"/> after each but the last.
    /// </summary>
    Planned,
    Implement,
    Review,
    Fixing,
    CI,
    CIHealing,
    MergeGate,
    Merge,
    Watch,
    Done,
    Escalated,
    Paused,
    Cancelled,
}

/// <summary>Facts from an item's ledger history that some transitions depend on.</summary>
public sealed record TransitionContext(WorkState? PausedFrom = null, int FixRounds = 0)
{
    /// <summary>Derives the context from an item's transition rows, oldest first.</summary>
    public static TransitionContext From(IReadOnlyList<WorkState> transitions)
    {
        WorkState? pausedFrom = transitions.Count >= 2 && transitions[^1] == WorkState.Paused ? transitions[^2] : null;
        // A fix round is a Review → Fixing step (review findings), a CI → CIHealing step (red CI, sc-25383) or a MergeGate →
        // Fixing step (the merge queue's update to the base conflicted, sc-25384) since the last Implement: every kind shares one
        // count and one cap. Returning from Paused is not a new round. A plan item has no Implement: its changeset pull request's
        // rounds are counted since it last waited on its stories (Planned, sc-25872).
        var lastImplement = transitions.ToList().FindLastIndex(StartsAttempt);
        var fixRounds = Enumerable.Range(lastImplement + 1, transitions.Count - lastImplement - 1)
            .Count(i => i > 0 && IsFixRound(transitions[i - 1], transitions[i]));
        return new TransitionContext(pausedFrom, fixRounds);
    }

    /// <summary>
    /// Whether entering <paramref name="state"/> starts an item's attempt at a change afresh, so the fix rounds before it no longer
    /// count: Implement (a fresh branch from the base), or Planned (a plan item, whose changeset pull request has no Implement).
    /// </summary>
    public static bool StartsAttempt(WorkState state) => state is WorkState.Implement or WorkState.Planned;

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> starts a fix round (counted against <see cref="Lifecycle.MaxFixRounds"/>).</summary>
    public static bool IsFixRound(WorkState from, WorkState to) =>
        (from is WorkState.Review or WorkState.MergeGate && to == WorkState.Fixing) || (from == WorkState.CI && to == WorkState.CIHealing);
}

public sealed class IllegalTransitionException(WorkState from, WorkState to, string reason)
    : InvalidOperationException($"Illegal transition {from} → {to}: {reason}")
{
    public WorkState From { get; } = from;
    public WorkState To { get; } = to;
}

/// <summary>
/// The transition table (E2: code decides every transition). Later phases register
/// handlers for states; they do not add rows here.
/// </summary>
public static class Lifecycle
{
    public const int MaxFixRounds = 3;

    private static readonly Dictionary<WorkState, WorkState[]> Forward = new()
    {
        [WorkState.Intake] = [WorkState.Plan, WorkState.Implement],
        // A plan item's stories were created: it waits on them (sc-25870) and never goes on to Implement itself.
        [WorkState.Plan] = [WorkState.Implement, WorkState.Planned],
        // Every story of the plan merged. With a changeset branch (sc-25872) the plan item takes the changeset's one pull request
        // to the default branch through Review → CI → MergeGate → Merge like any item's; without one, or with an empty one, the
        // plan item was reported and closed out on its board (sc-25871) and is finished.
        [WorkState.Planned] = [WorkState.Done, WorkState.Review],
        [WorkState.Implement] = [WorkState.Review],
        [WorkState.Review] = [WorkState.Fixing, WorkState.CI],
        [WorkState.Fixing] = [WorkState.Review],
        // A push after the review verdict voids it (E3): the new head goes back to Review.
        [WorkState.CI] = [WorkState.CIHealing, WorkState.MergeGate, WorkState.Review],
        [WorkState.CIHealing] = [WorkState.CI],
        // The merge queue (sc-25384): a conflict with the base goes to the fixer; the updated head's red CI goes to CI's triage.
        [WorkState.MergeGate] = [WorkState.Merge, WorkState.Review, WorkState.Fixing, WorkState.CI],
        // A plan of several repositories (sc-25873) merges one changeset per repository, one at a time: after each but the last
        // the plan item waits again (Planned) — on the next repository's stories, then on that changeset's own pull request.
        [WorkState.Merge] = [WorkState.Watch, WorkState.Planned],
        // A reverted change reopens: back to Intake, or straight to Implement.
        [WorkState.Watch] = [WorkState.Done, WorkState.Intake, WorkState.Implement],
        // A human re-queues an escalated item.
        [WorkState.Escalated] = [WorkState.Intake],
    };

    public static bool IsTerminal(WorkState state) => state is WorkState.Done or WorkState.Cancelled;

    /// <summary>Returns null when <paramref name="from"/> → <paramref name="to"/> is legal, else why not.</summary>
    public static string? Check(WorkState from, WorkState to, TransitionContext context)
    {
        if (IsTerminal(from))
        {
            return $"{from} is terminal";
        }
        if (to == WorkState.Cancelled)
        {
            return null;
        }
        if (to == WorkState.Paused)
        {
            return from == WorkState.Paused ? "already paused" : null;
        }
        if (from == WorkState.Paused)
        {
            return to == context.PausedFrom ? null : $"a paused item returns only to the state it paused from ({context.PausedFrom?.ToString() ?? "unknown"})";
        }
        if (to == WorkState.Escalated)
        {
            return from == WorkState.Escalated ? "already escalated" : null;
        }
        if (TransitionContext.IsFixRound(from, to) && context.FixRounds >= MaxFixRounds)
        {
            return $"{MaxFixRounds} fix rounds used; escalate instead";
        }
        return Forward.TryGetValue(from, out var next) && next.Contains(to) ? null : "not in the transition table";
    }

    public static void Ensure(WorkState from, WorkState to, TransitionContext context)
    {
        if (Check(from, to, context) is { } reason)
        {
            throw new IllegalTransitionException(from, to, reason);
        }
    }
}
