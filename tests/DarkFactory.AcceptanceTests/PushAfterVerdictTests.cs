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

    private static List<LedgerEntry> Merged(params LedgerEntry[] middle) =>
        [To(WorkState.Intake), To(WorkState.Implement), To(WorkState.Review), .. middle, To(WorkState.Merge, "m"), To(WorkState.Watch)];

    [Fact]
    public void The_plain_run_holds_the_push_back_to_review_then_the_pushed_head_reviewed_and_merged()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), To(WorkState.Review), Verdict(B, true), To(WorkState.CI, B),
            To(WorkState.MergeGate), GatePassed(B));

        Assert.Empty(PushAfterVerdict.Problems(history, A));
    }

    [Fact]
    public void A_fix_round_after_the_push_holds_when_the_fixed_head_passed_before_the_merge()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), To(WorkState.Review), Verdict(B, false), To(WorkState.Fixing),
            To(WorkState.Review), Verdict(C, true), To(WorkState.CI, C), To(WorkState.MergeGate), GatePassed(C));

        Assert.Empty(PushAfterVerdict.Problems(history, A));
    }

    [Fact]
    public void No_verdict_on_the_pre_push_head_is_a_problem()
    {
        var history = Merged(Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"no verdict on the pre-push head {A}"], PushAfterVerdict.Problems(history, A));
    }

    [Fact]
    public void Not_going_back_to_review_after_the_first_verdict_is_a_problem()
    {
        var history = Merged(Verdict(A, true), Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate), GatePassed(B));

        Assert.Equal([$"the item did not go back to Review after the verdict on the pre-push head {A}"], PushAfterVerdict.Problems(history, A));
    }

    [Fact]
    public void Passing_the_gate_on_the_voided_head_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), To(WorkState.Review), To(WorkState.CI, A), To(WorkState.MergeGate), GatePassed(A));

        Assert.Equal([$"gate-passed names the pre-push head {A}, whose verdict the push voided"], PushAfterVerdict.Problems(history, A));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("failed")]
    [InlineData("after")]
    public void Passing_the_gate_on_a_head_without_a_passing_verdict_before_it_is_a_problem(string kind)
    {
        LedgerEntry[] reviewedB = kind == "failed" ? [Verdict(B, false)] : [];
        LedgerEntry[] afterGate = kind == "after" ? [Verdict(B, true)] : [];
        var history = Merged([Verdict(A, true), To(WorkState.CI, A), To(WorkState.Review), .. reviewedB, To(WorkState.CI, B), To(WorkState.MergeGate),
            GatePassed(B), .. afterGate]);

        Assert.Equal([$"gate-passed names {B}, which had no passing verdict before it"], PushAfterVerdict.Problems(history, A));
    }

    [Fact]
    public void Nothing_passing_the_gate_is_a_problem()
    {
        var history = Merged(Verdict(A, true), To(WorkState.CI, A), To(WorkState.Review), Verdict(B, true), To(WorkState.CI, B), To(WorkState.MergeGate));

        Assert.Equal(["no gate-passed row: nothing was merged"], PushAfterVerdict.Problems(history, A));
    }
}
