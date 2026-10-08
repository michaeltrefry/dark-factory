namespace DarkFactory.Orchestrator.Ledger;

/// <summary>The spec's full work-item lifecycle. Stored by name, so members may be added but not renamed.</summary>
public enum WorkState
{
    Intake,
    Plan,
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
        // A fix round is a Review → Fixing step since the last Implement; returning from Paused is not a new round.
        var lastImplement = transitions.ToList().FindLastIndex(s => s == WorkState.Implement);
        var fixRounds = Enumerable.Range(lastImplement + 1, transitions.Count - lastImplement - 1)
            .Count(i => i > 0 && transitions[i] == WorkState.Fixing && transitions[i - 1] == WorkState.Review);
        return new TransitionContext(pausedFrom, fixRounds);
    }
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
        [WorkState.Plan] = [WorkState.Implement],
        [WorkState.Implement] = [WorkState.Review],
        [WorkState.Review] = [WorkState.Fixing, WorkState.CI],
        [WorkState.Fixing] = [WorkState.Review],
        // A push after the review verdict voids it (E3): the new head goes back to Review.
        [WorkState.CI] = [WorkState.CIHealing, WorkState.MergeGate, WorkState.Review],
        [WorkState.CIHealing] = [WorkState.CI],
        [WorkState.MergeGate] = [WorkState.Merge, WorkState.Review],
        [WorkState.Merge] = [WorkState.Watch],
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
        if (from == WorkState.Review && to == WorkState.Fixing && context.FixRounds >= MaxFixRounds)
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
