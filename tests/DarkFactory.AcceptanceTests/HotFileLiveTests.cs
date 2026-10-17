using DarkFactory.Orchestrator;
using static DarkFactory.AcceptanceTests.HotFileLive;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Not live (sc-26345): the rules <see cref="HotFileRecoveryAcceptanceTests"/> decide a live run with, so a live AT8 run can
/// neither pass vacuously nor on an unresolved, wrong-class, reused-session or uncorrected recovery.
/// </summary>
public class HotFileLiveTests
{
    private static readonly RepoRef Repo = RepoRef.Parse("owner/sandbox");
    private static readonly string[] Workers = ["worker-1", "review-1"];

    private static Evidence Safe(string state = "Resolved", string served = "high", string session = "assess-1", string decision = "safe") =>
        new([new(1, "owner/sandbox", 1, state, 1, null)], [new(10, 1, decision, served, session, 0)], [], Workers);

    private static Evidence Defect(string first = "unsafe", int findings = 1, string second = "safe", long? merge = 20, bool gate = true,
        string secondSession = "assess-2", Correction[]? corrections = null) =>
        new([new(1, "owner/sandbox", 1, "Resolved", 2, null)],
            [new(10, 1, first, "high", "assess-1", findings), new(30, 1, second, "high", secondSession, 0)],
            corrections ?? [new(5, 1, 1, "Merged", merge, gate)], Workers);

    [Fact]
    public void A_resolved_safe_incident_on_high_with_a_fresh_session_and_no_correction_passes()
    {
        Assert.Empty(SafeProblems(Safe(), Repo));
        Assert.Empty(SafeProblems(Safe() with { Incidents = [new(1, "OWNER/Sandbox", 1, "Resolved", 1, null)] }, Repo));
    }

    [Theory]
    [InlineData("Escalated", "high", "assess-1", "safe", "ended Escalated")]
    [InlineData("Resolved", "mid", "assess-1", "safe", "served on mid")]
    [InlineData("Resolved", "high", "worker-1", "safe", "also a worker or review session")]
    [InlineData("Resolved", "high", "review-1", "safe", "also a worker or review session")]
    [InlineData("Resolved", "high", "assess-1", "inconclusive", "not safe")]
    public void The_safe_case_fails_on_any_unresolved_or_non_independent_assessment(string state, string served, string session,
        string decision, string expected) =>
        Assert.Contains(SafeProblems(Safe(state, served, session, decision), Repo), p => p.Contains(expected));

    [Fact]
    public void The_safe_case_fails_without_an_incident_or_with_a_correction()
    {
        Assert.Contains(SafeProblems(Safe() with { Incidents = [] }, Repo), p => p.Contains("found 0"));
        Assert.Contains(SafeProblems(Safe() with { Corrections = [new(5, 1, 1, "Pending", null, false)] }, Repo),
            p => p.Contains("correction was created"));
        Assert.Contains(SafeProblems(Safe() with { Assessments = [] }, Repo), p => p.Contains("no recovery assessment"));
    }

    [Fact]
    public void A_corrected_and_freshly_reassessed_defect_passes_including_a_second_attempt_after_a_failed_first()
    {
        Assert.Empty(DefectProblems(Defect(), Repo));
        Assert.Empty(DefectProblems(Defect(corrections: [new(5, 1, 1, "Failed", null, false), new(6, 1, 2, "Merged", 20, true)]), Repo));
        Assert.Empty(DefectProblems(Defect(corrections: [new(5, 1, 1, "Revoked", null, false), new(6, 1, 2, "Merged", 20, true)]), Repo));
    }

    [Fact]
    public void The_defect_case_needs_the_defective_story_merged_before_the_second_whatever_their_priority()
    {
        Assert.Empty(OrderProblem(10, 20));
        Assert.Contains(OrderProblem(20, 10), p => p.Contains("merged (row 10) before the defective story"));
        Assert.Contains(OrderProblem(null, 10), p => p.Contains("(d1) has no Merge row"));
        Assert.Contains(OrderProblem(10, null), p => p.Contains("(d2) has no Merge row"));
    }

    [Fact]
    public void A_safe_first_verdict_is_inconclusive_and_fails_rather_than_passing_vacuously()
    {
        Assert.Contains(DefectProblems(Defect(first: "safe", findings: 0), Repo), p => p.StartsWith("INCONCLUSIVE"));
        Assert.Contains(DefectProblems(Defect(findings: 0), Repo), p => p.StartsWith("INCONCLUSIVE"));
    }

    [Fact]
    public void The_defect_case_fails_without_one_gate_merged_correction_and_a_later_safe_reassessment()
    {
        Assert.Contains(DefectProblems(Defect(merge: null), Repo), p => p.Contains("exactly one correction merged"));
        Assert.Contains(DefectProblems(Defect(gate: false), Repo), p => p.Contains("no gate-passed"));
        Assert.Contains(DefectProblems(Defect(merge: 40), Repo), p => p.Contains("no safe assessment after"));
        Assert.Contains(DefectProblems(Defect(secondSession: "assess-1"), Repo), p => p.Contains("reused"));
        Assert.Contains(DefectProblems(Defect(corrections: [new(5, 1, 1, "Merged", 15, true), new(6, 1, 2, "Merged", 20, true)]), Repo),
            p => p.Contains("exactly one correction merged"));
        Assert.Contains(DefectProblems(Defect(corrections: [new(5, 1, 1, "Failed", null, false), new(6, 1, 2, "Failed", null, false),
            new(7, 1, 3, "Merged", 20, true)]), Repo), p => p.Contains("exceed the cap of 2"));
    }
}
