using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// How the step a ledger row closes ended (E7): every row carries exactly one. Stored by its snake_case name
/// (<see cref="StepOutcomes.Name"/>; the database refuses any other value and a missing one). No member is 0, so an unset value is
/// never a valid outcome.
/// </summary>
public enum StepOutcome
{
    /// <summary>The step did what it set out to do.</summary>
    Passed = 1,

    /// <summary>The step ran and its result is a failure: a failed review or fix round, red CI, a stuck worker, a check that could not run.</summary>
    Failed,

    /// <summary>A gate rule refused the change: the merge gate's decision, the new-tests check's rejection, a head without a passing verdict.</summary>
    GateRejected,

    /// <summary>The step was set aside, not judged: a pause (usage, freeze, a human's Pause, Ctrl-C), a parked item, a human's Stop.</summary>
    Deferred,

    /// <summary>A human must look: the item escalated (or a triage routed it to a human).</summary>
    Escalated,
}

/// <summary>
/// The one place a ledger row's outcome is decided (sc-25389): <see cref="WorkLedger"/> calls <see cref="Of"/> for every row it writes, so
/// no call site picks an outcome and none can write a row without one. The same rules, as SQL, backfilled the rows written before the
/// outcome column existed (<see cref="Migrations.StepOutcomes.BackfillSql"/>); a Postgres test checks the two agree on every rule.
/// </summary>
/// <remarks>
/// The rules map the typed results earlier stories record, by what the row closes:
/// <list type="bullet">
/// <item>A transition (Step null) into Escalated → escalated; into Paused → deferred (a triage's "needs a human" park → escalated);
/// into Cancelled (a human's Stop) → deferred. Out of Paused (the pause ended and the item resumes) → passed.</item>
/// <item>Into Fixing or CIHealing (a fix round starts because the review, CI or the merge queue's base update failed) → failed. A fix round
/// that ends stuck (<see cref="RunPipeline.StuckRoundDetail"/>: the fixer looped, nothing was pushed) → failed. MergeGate → CI (the queue's
/// updated head went red) → failed. Watch → Intake/Implement (a revert reopened it) → failed.</item>
/// <item>CI or MergeGate → Review: the head lacked a passing verdict bound to it (E3), so the gate sent it back → gate_rejected.</item>
/// <item>Any other transition → passed.</item>
/// <item>Checkpoints: <c>verdict</c> pass → passed, fail → failed; <c>fix-progress</c> progress → passed, failed → failed; <c>gate</c> Merge →
/// passed, ReviewHead/Blocked → gate_rejected; <c>new-tests</c> pass → passed, rejected/no-tests → gate_rejected, unsupported/error (the check
/// could not produce evidence) → failed; <c>ci-failure</c>, <c>merge-conflict</c>, <c>stuck</c>, <c>stuck-retry</c>, <c>worktree-lost</c>,
/// <c>github-refused</c> → failed; <c>escalation-comment</c> posted → passed, else failed; <c>pr-report</c> or <c>closeout</c> failed → failed; <c>approval-ignored</c> → gate_rejected;
/// <c>usage-pause</c>, <c>parked</c> → deferred; every other checkpoint (a completed sub-step) → passed.</item>
/// </list>
/// A run deferred by the freeze before it starts (<see cref="RunOutcome.Deferred"/>) writes no row, so it has no outcome to record.
/// </remarks>
public static class StepOutcomes
{
    public const string PassedName = "passed";
    public const string FailedName = "failed";
    public const string GateRejectedName = "gate_rejected";
    public const string DeferredName = "deferred";
    public const string EscalatedName = "escalated";

    public static string Name(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Passed => PassedName,
        StepOutcome.Failed => FailedName,
        StepOutcome.GateRejected => GateRejectedName,
        StepOutcome.Deferred => DeferredName,
        StepOutcome.Escalated => EscalatedName,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not a step outcome"),
    };

    public static StepOutcome Parse(string name) => name switch
    {
        PassedName => StepOutcome.Passed,
        FailedName => StepOutcome.Failed,
        GateRejectedName => StepOutcome.GateRejected,
        DeferredName => StepOutcome.Deferred,
        EscalatedName => StepOutcome.Escalated,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a step outcome"),
    };

    /// <summary>
    /// The outcome of a row moving an item from <paramref name="from"/> (null: the item is new) to <paramref name="state"/>, or a
    /// checkpoint <paramref name="step"/> inside <paramref name="state"/>, with its <paramref name="detail"/>.
    /// </summary>
    public static StepOutcome Of(WorkState? from, WorkState state, string? step, string? detail) =>
        step is null ? OfTransition(from, state, detail) : OfCheckpoint(step, detail);

    private static StepOutcome OfTransition(WorkState? from, WorkState to, string? detail) => (from, to) switch
    {
        (_, WorkState.Escalated) => StepOutcome.Escalated,
        (_, WorkState.Paused) => detail == Issues.IssueComments.RouteName(Issues.IssueRoute.NeedsHuman) ? StepOutcome.Escalated : StepOutcome.Deferred,
        (_, WorkState.Cancelled) => StepOutcome.Deferred,
        (WorkState.Paused, _) => StepOutcome.Passed,
        (_, WorkState.Fixing or WorkState.CIHealing) => StepOutcome.Failed,
        (WorkState.Fixing or WorkState.CIHealing, _) when RunPipeline.IsStuckRound(detail) => StepOutcome.Failed,
        (WorkState.MergeGate, WorkState.CI) => StepOutcome.Failed,
        (WorkState.Watch, WorkState.Intake or WorkState.Implement) => StepOutcome.Failed,
        (WorkState.CI or WorkState.MergeGate, WorkState.Review) => StepOutcome.GateRejected,
        _ => StepOutcome.Passed,
    };

    private static StepOutcome OfCheckpoint(string step, string? detail) => step switch
    {
        RunPipeline.Steps.Verdict => ReviewVerdict.FromDetail(detail) is { Passed: true } ? StepOutcome.Passed : StepOutcome.Failed,
        RunPipeline.Steps.FixProgress => FixProgress.FromDetail(detail) is { MadeProgress: true } ? StepOutcome.Passed : StepOutcome.Failed,
        RunPipeline.Steps.GateDecision => detail?.StartsWith($"{GateOutcome.Merge} ", StringComparison.Ordinal) == true
            ? StepOutcome.Passed : StepOutcome.GateRejected,
        RunPipeline.Steps.NewTests => NewTestsResult.FromDetail(detail)?.Outcome switch
        {
            NewTestsOutcome.Pass => StepOutcome.Passed,
            NewTestsOutcome.Rejected or NewTestsOutcome.NoTests => StepOutcome.GateRejected,
            _ => StepOutcome.Failed,
        },
        RunPipeline.Steps.CiFailure or RunPipeline.Steps.MergeConflict or RunPipeline.Steps.Stuck or RunPipeline.Steps.StuckRetry
            or RunPipeline.Steps.WorktreeLost or RunPipeline.Steps.ControlsUnreadable or Issues.IssueSteps.Refused => StepOutcome.Failed,
        RunPipeline.Steps.EscalationComment => detail == "posted" ? StepOutcome.Passed : StepOutcome.Failed,
        RunPipeline.Steps.PrReport or RunPipeline.Steps.Closeout =>
            detail?.StartsWith("failed", StringComparison.Ordinal) == true ? StepOutcome.Failed : StepOutcome.Passed,
        Issues.IssueSteps.ApprovalIgnored => StepOutcome.GateRejected,
        RunPipeline.Steps.UsagePause or RunPipeline.Steps.Parked => StepOutcome.Deferred,
        _ => StepOutcome.Passed,
    };
}
