using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.AcceptanceTests;

/// <summary>P2-AT2's ledger invariants (<see cref="PushAfterVerdict"/>) over hand-written ledgers (not live).</summary>
public class PushAfterVerdictTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; // the head the first review judged
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; // the test's push
    private const string C = "cccccccccccccccccccccccccccccccccccccccc"; // a fix round's push

    private static LedgerEntry To(WorkState state, string? detail = null) => new() { State = state, Detail = detail, Outcome = StepOutcome.Passed };

    private static LedgerEntry Step(WorkState state, string step, string? detail) => new() { State = state, Step = step, Detail = detail, Outcome = StepOutcome.Passed };

    private static LedgerEntry Verdict(string sha, bool pass) =>
        Step(WorkState.Review, RunPipeline.Steps.Verdict, new ReviewVerdict(sha, pass ? ReviewVerdict.Pass : ReviewVerdict.Fail, "s", [], []).ToDetail());

    private static LedgerEntry GatePassed(string sha) => Step(WorkState.MergeGate, RunPipeline.Steps.GatePassed, sha);

    /// <summary>CI's transition back to Review when it finds the head moved past the reviewed one.</summary>
    private static LedgerEntry HeadMoved(string sha) => To(WorkState.Review, $"head moved to {sha} after the review; reviewing it again");

    private static List<LedgerEntry> Merged(params LedgerEntry[] middle) =>
        [To(WorkState.Intake), To(WorkState.Implement), To(WorkState.Review), .. middle, To(WorkState.Merge, "m"), To(WorkState.Watch)];

    private static IReadOnlyList<string> Problems(List<LedgerEntry> history) => PushAfterVerdict.Problems(history, A, B);

    [Fact]
    public void A_push_after_a_passing_verdict_sent_back_from_ci_reviewed_and_merged_holds()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), Verdict(B, true), To(WorkState.CI, B),
            To(WorkState.MergeGate), GatePassed(B));

        Assert.Empty(Problems(history));
    }

    [Fact]
    public void A_fix_round_after_the_pushed_head_was_reviewed_holds_when_the_fixed_head_passed_before_the_merge()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), Verdict(B, false), To(WorkState.Fixing),
            To(WorkState.Review), Verdict(C, true), To(WorkState.CI, C), To(WorkState.MergeGate), GatePassed(C));

        Assert.Empty(Problems(history));
    }

    [Fact]
    public void A_failing_first_verdict_then_a_fix_round_is_inconclusive()
    {
        // The fixer's commit sat on top of the test's push: a Review came back, merged, but never through the head-moved path.
        var history = Merged(Verdict(A, false), To(WorkState.Fixing), To(WorkState.Review), Verdict(C, true), To(WorkState.CI, C),
            To(WorkState.MergeGate), GatePassed(C));

        Assert.Equal([$"{PushAfterVerdict.Inconclusive}: the verdict on the pre-push head {A} failed, so no push voided a passing verdict",
            $"no verdict on the pushed head {B}"], Problems(history));
    }

    [Fact]
    public void No_verdict_on_the_pre_push_head_is_inconclusive()
    {
        var history = Merged(Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"{PushAfterVerdict.Inconclusive}: no verdict on the pre-push head {A}"], Problems(history));
    }

    [Fact]
    public void A_ci_healing_round_before_the_next_review_is_inconclusive()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), To(WorkState.CIHealing), HeadMoved(B), Verdict(B, true), To(WorkState.CI, B),
            To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"{PushAfterVerdict.Inconclusive}: CIHealing ran between the passing verdict on {A} and the next Review"], Problems(history));
    }

    [Fact]
    public void Not_going_back_to_review_after_the_passing_verdict_is_a_problem()
    {
        var history = Merged(Verdict(A, true), Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"the item did not go back to Review after the passing verdict on the pre-push head {A}"], Problems(history));
    }

    [Fact]
    public void A_review_after_the_verdict_that_does_not_name_the_pushed_head_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(C), Verdict(B, true), To(WorkState.CI, B),
            To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"the Review after the passing verdict on {A} does not name the pushed head {B}: head moved to {C} after the review; reviewing it again"],
            Problems(history));
    }

    [Fact]
    public void No_verdict_on_the_pushed_head_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), Verdict(C, true), To(WorkState.CI, C),
            To(WorkState.MergeGate), GatePassed(C));

        Assert.Equal([$"no verdict on the pushed head {B}"], Problems(history));
    }

    [Fact]
    public void Passing_the_gate_on_the_voided_head_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), Verdict(B, true), To(WorkState.CI, A), To(WorkState.MergeGate),
            GatePassed(A));

        Assert.Equal([$"gate-passed names the pre-push head {A}, whose verdict the push voided"], Problems(history));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("after")]
    public void Passing_the_gate_on_a_head_without_a_passing_verdict_before_it_is_a_problem(string kind)
    {
        LedgerEntry[] reviewedB = kind == "failed" ? [Verdict(B, false)] : [];
        LedgerEntry[] afterGate = kind == "after" ? [Verdict(B, true)] : [];
        var history = Merged([Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), .. reviewedB, To(WorkState.CI, B), To(WorkState.MergeGate),
            GatePassed(B), .. afterGate]);

        Assert.Equal([$"gate-passed names {B}, which had no passing verdict before it"], Problems(history));
    }

    [Fact]
    public void Nothing_passing_the_gate_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), HeadMoved(B), Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate));

        Assert.Equal(["no gate-passed row: nothing was merged"], Problems(history));
    }
}
