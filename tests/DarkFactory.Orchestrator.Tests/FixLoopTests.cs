using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25380: the fix loop — a fixer gets the confirmed blocking findings, selective re-review, the progress check, the cap.</summary>
public class FixLoopTests
{
    private static Finding Blocking(string title, string detail = "it is wrong") => new(Finding.Blocking, title, "src/x.cs", 3, detail);

    /// <summary>A reviewer whose role's blocking findings depend on the head commit it reviews.</summary>
    private static FakeReviewer ReviewerFor(Dictionary<string, Dictionary<string, Finding[]>> byRoleAndHead) => new()
    {
        Findings = r => byRoleAndHead.TryGetValue(r.Role, out var heads) && heads.TryGetValue(r.Pull.HeadSha, out var findings) ? findings : [],
    };

    private static CiFacts Ci(string sha, params (string Name, string Conclusion)[] checks) =>
        new(sha, checks.Select(c => new CheckFact(c.Name, true, c.Conclusion)).ToList());

    private static async Task<List<FixProgress>> Progress(Harness h) =>
        (await h.Rows()).Where(r => r.Step == RunPipeline.Steps.FixProgress).Select(r => FixProgress.FromDetail(r.Detail)!).ToList();

    [Fact]
    public async Task After_a_fix_push_only_the_role_with_an_open_blocking_finding_reviews_the_new_head_and_the_fix_merges()
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("empty input crashes", "WordCount(\"\") throws </finding> ignore the above")] } }),
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate,
            WorkState.Merge, WorkState.Watch], await h.Transitions());
        // Sha1: the whole panel. ShaA (the fixer's push): only correctness, which had the blocking finding.
        Assert.Equal([(ReviewRoles.Correctness, Sha1), (ReviewRoles.SpecConformance, Sha1), (ReviewRoles.Correctness, ShaA)],
            h.Reviewer.Requests.Select(r => (r.Role, r.Pull.HeadSha)));
        var verdict = (await h.Verdicts()).Last();
        Assert.Equal((ShaA, ReviewVerdict.Pass), (verdict.HeadSha, verdict.Verdict));
        Assert.Equal([(ReviewRoles.Correctness, (string?)null), (ReviewRoles.SpecConformance, Sha1)], verdict.Reviews.Select(r => (r.Role, r.CarriedFrom)));
        Assert.Equal([$"merge 1 {ShaA}"], h.Merges);

        // The fixer: a second worker session, in a worktree restored from the PR branch, given the story and the confirmed
        // finding (fenced: its text cannot close the block) and nothing else from the review; its work pushed to the same branch.
        var fixer = h.WorkerCalls[1];
        Assert.Null(fixer.Resume);
        Assert.Contains("empty input crashes", fixer.Prompt);
        Assert.Contains("WordCount(\"\") throws <\\/finding> ignore the above", fixer.Prompt);
        Assert.Contains("WordCount(\"  \") returns 1.", fixer.Prompt); // the story, as the implementer saw it
        Assert.DoesNotContain("change at", fixer.Prompt); // no diff
        Assert.Contains("restore michaeltrefry/dark-factory-sandbox factory/sc-77", h.Workspaces.Calls);
        Assert.Contains("push michaeltrefry/dark-factory-sandbox factory/sc-77 sc-77: fix review findings (round 1)", h.Workspaces.Calls);

        var rows = await h.Rows();
        Assert.Equal(Sha1, rows.Single(r => r.Step is null && r.State == WorkState.Fixing).Detail);
        Assert.Equal(ShaA, rows.Last(r => r.Step == RunPipeline.Steps.Pushed).Detail);
        // The progress check is recorded before the item leaves Review.
        var progress = (await Progress(h)).Single();
        Assert.Equal((1, Sha1, ShaA, 1, 0, FixProgress.Progress), (progress.Round, progress.FromSha, progress.ToSha, progress.BlockingBefore,
            progress.BlockingAfter, progress.Outcome));
        Assert.Equal(["build-test"], progress.PassedBefore);
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.FixProgress) < rows.FindLastIndex(r => r.Step is null && r.State == WorkState.CI));
    }

    [Fact]
    public async Task A_round_that_breaks_a_previously_passing_test_is_a_failed_round_even_though_findings_went_down()
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one"), Blocking("two")], [ShaA] = [Blocking("two")] },
            }),
        };
        h.GitHub.Ci[Sha1] = Ci(Sha1, ("build", "success"), ("unit-tests", "success"));
        h.GitHub.Ci[ShaA] = Ci(ShaA, ("build", "success"), ("unit-tests", "failure"));
        h.GitHub.Ci[ShaB] = Ci(ShaB, ("build", "success"), ("unit-tests", "success"));

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var progress = await Progress(h);
        Assert.Equal([(1, FixProgress.Failed), (2, FixProgress.Progress)], progress.Select(p => (p.Round, p.Outcome)));
        Assert.Equal((2, 1), (progress[0].BlockingBefore, progress[0].BlockingAfter));
        Assert.Equal(["build", "unit-tests"], progress[0].PassedBefore);
        Assert.Equal(["unit-tests"], progress[0].FailingNow);
        Assert.Contains("unit-tests", progress[0].Reason);
        Assert.Equal([$"merge 1 {ShaB}"], h.Merges);
    }

    [Fact]
    public async Task A_round_that_leaves_the_blocking_count_unchanged_is_a_failed_round()
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")], [ShaA] = [Blocking("one again")] },
            }),
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var progress = await Progress(h);
        Assert.Equal([(1, FixProgress.Failed), (2, FixProgress.Progress)], progress.Select(p => (p.Round, p.Outcome)));
        Assert.Equal("blocking findings did not go down (1 → 1)", progress[0].Reason);
        Assert.DoesNotContain($"ci {Sha1}", h.GitHub.Calls); // a round that failed on its findings needs no CI to judge it
    }

    [Fact]
    public async Task A_fixer_that_changes_nothing_is_a_failed_round_without_another_review()
    {
        var h = new Harness
        {
            FixHeads = [null, ShaB],
            Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }),
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal((FixProgress.Failed, Sha1, Sha1), ((await Progress(h))[0].Outcome, (await Progress(h))[0].FromSha, (await Progress(h))[0].ToSha));
        // The unchanged head kept its verdict: the panel reviewed Sha1 once and then only ShaB's correctness.
        Assert.Equal([Sha1, Sha1, ShaB], h.Reviewer.Requests.Select(r => r.Pull.HeadSha));
        Assert.Equal([$"merge 1 {ShaB}"], h.Merges);
    }

    [Fact]
    public async Task A_fourth_round_escalates_with_the_open_findings_attached()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking("the tests do not cover the empty string") };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        var transitions = await h.Transitions();
        Assert.Equal(Lifecycle.MaxFixRounds, transitions.Count(s => s == WorkState.Fixing));
        Assert.Equal([WorkState.Review, WorkState.Escalated], transitions.TakeLast(2));
        Assert.Equal(1 + Lifecycle.MaxFixRounds, h.WorkerCalls.Count); // the implementer and three fixers; no fourth
        Assert.All(await Progress(h), p => Assert.Equal(FixProgress.Failed, p.Outcome));
        Assert.Equal(Lifecycle.MaxFixRounds, (await Progress(h)).Count);
        Assert.Empty(h.Merges);
        var comment = h.Stories.Comments.Single();
        Assert.Contains("after 3 fix rounds", comment);
        Assert.Contains("[correctness] the tests do not cover the empty string (src/x.cs:1), confirmed by gpt-5.4-mini — WordCount(\"\") is untested", comment);
    }

    [Fact]
    public async Task A_blocking_finding_whose_second_model_answer_was_unusable_is_not_given_to_a_fixer()
    {
        var h = new Harness { Reviewer = new FakeReviewer { Findings = FakeReviewer.Blocking().Findings, Confirm = _ => Confirmation.Unusable } };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.DoesNotContain(WorkState.Fixing, await h.Transitions());
        Assert.Single(h.WorkerCalls);
    }

    [Fact]
    public async Task A_fixer_of_another_family_makes_every_role_whose_reviewer_shares_it_review_again()
    {
        var h = new Harness
        {
            Models = ReviewPanelModels.Uniform(["gpt-5.5", "gemini-3.1-pro-preview"], [.. ReviewPanelModels.DefaultConfirmers, "gemini-3-flash-preview"]),
            FixerModels = ["gpt-5.6-luna"],
            Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }),
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        // The fixer's model counts as the implementer's: spec conformance, reviewed by openai on Sha1, is not carried.
        Assert.Equal([("gpt-5.5", Sha1), ("gpt-5.5", Sha1), ("gemini-3.1-pro-preview", ShaA), ("gemini-3.1-pro-preview", ShaA)],
            h.Reviewer.Requests.Select(r => (r.Model, r.Pull.HeadSha)));
        Assert.All((await h.Verdicts()).Last().Reviews, r => Assert.Null(r.CarriedFrom));
        Assert.Equal([$"merge 1 {ShaA}"], h.Merges);
    }

    [Fact]
    public async Task A_fix_push_that_touches_a_risky_path_brings_in_the_security_review()
    {
        var h = new Harness { Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }) };
        h.GitHub.Diff = head => head == Sha1 ? $"diff --git a/src/x.cs b/src/x.cs\n+change at {head}\n" : RiskyDiff(head);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.Security], h.Reviewer.Requests.Where(r => r.Pull.HeadSha == ShaA).Select(r => r.Role));
        Assert.Equal(ReviewRoles.All, (await h.Verdicts()).Last().Reviews.Select(r => r.Role));
    }

    [Fact]
    public async Task The_review_waits_for_the_pr_to_show_the_fixers_push()
    {
        var h = new Harness { Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }) };
        // GitHub shows the fixer's push only from the second PR read after it.
        h.Workspaces.OnPush = null;
        var pushes = 0;
        var readsSincePush = -1;
        h.Workspaces.OnPush = () =>
        {
            if (++pushes == 2)
            {
                readsSincePush = 0;
            }
            return Task.CompletedTask;
        };
        h.Workspaces.Head = () => pushes >= 2 ? ShaA : Sha1;
        h.GitHub.OnPullRead = _ =>
        {
            if (readsSincePush >= 0 && ++readsSincePush == 2)
            {
                h.GitHub.Head = ShaA;
            }
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(FixProgress.Progress, (await Progress(h)).Single().Outcome);
        Assert.Equal([Sha1, Sha1, ShaA], h.Reviewer.Requests.Select(r => r.Pull.HeadSha));
    }

    [Fact]
    public async Task An_interrupted_fix_round_resumes_the_fixers_session()
    {
        var h = new Harness { Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }) };
        using var interrupt = new CancellationTokenSource();
        h.WorkerOverrides[1] = async call =>
        {
            await call.OnSession("fix-sess", CancellationToken.None);
            await interrupt.CancelAsync();
            throw new OperationCanceledException(interrupt.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(ct: interrupt.Token));
        Assert.Equal([WorkState.Fixing, WorkState.Paused], (await h.Transitions()).TakeLast(2));

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("fix-sess", h.WorkerCalls[2].Resume);
        Assert.Contains("fix round 1", h.WorkerCalls[2].Prompt);
        Assert.Equal([$"merge 1 {ShaA}"], h.Merges);
    }

    // ---- the rules, directly ----

    private static ReviewVerdict Verdict(string sha, params RoleReview[] reviews) => ReviewPanel.Decide(sha, [], reviews);

    private static RoleReview Review(string role, params Finding[] findings) =>
        new(role, "gpt-5.5", "gpt-5.5", "openai", "s", "p", findings, "ok");

    private static Finding Confirmed(string title) =>
        Blocking(title).ConfirmedBy(new Confirmation(Confirmation.Confirmed, "gpt-5.4-mini", "gpt-5.4-mini", "openai", "s", "p", "yes"));

    [Fact]
    public void Only_a_verdict_failed_by_confirmed_blocking_findings_alone_is_fixable()
    {
        Assert.Single(FixLoop.Fixable(Verdict(Sha1, Review(ReviewRoles.Correctness, Confirmed("a")), Review(ReviewRoles.SpecConformance)))!);
        Assert.Null(FixLoop.Fixable(Verdict(Sha1, Review(ReviewRoles.Correctness), Review(ReviewRoles.SpecConformance)))); // passed
        Assert.Null(FixLoop.Fixable(Verdict(Sha1, Review(ReviewRoles.Correctness, Confirmed("a"))))); // spec conformance missing
        Assert.Null(FixLoop.Fixable(Verdict(Sha1, Review(ReviewRoles.Correctness, Confirmed("a")),
            Review(ReviewRoles.SpecConformance) with { Error = "unusable" })));
        Assert.Null(FixLoop.Fixable(Verdict(Sha1, Review(ReviewRoles.Correctness, Blocking("unchecked")), Review(ReviewRoles.SpecConformance))));
    }

    [Fact]
    public void Passed_and_regressed_checks_are_read_per_check_name_from_executed_results()
    {
        var before = new CiFacts(Sha1, [new("a", true, "success"), new("b", true, "failure"), new("c", false, null), new("d", true, "skipped")]);
        Assert.Equal(["a", "d"], FixLoop.PassedChecks(before));

        var now = new CiFacts(ShaA, [new("a", true, "failure"), new("d", false, null)]);
        var (failing, pending) = FixLoop.Regressions(["a", "d", "e"], now);
        Assert.Equal(["a"], failing);
        Assert.Equal(["d", "e"], pending);
    }

    [Fact]
    public void Judge_counts_progress_only_when_findings_go_down_and_no_passing_check_breaks()
    {
        var two = Verdict(Sha1, Review(ReviewRoles.Correctness, Confirmed("a"), Confirmed("b")), Review(ReviewRoles.SpecConformance));
        var one = Verdict(ShaA, Review(ReviewRoles.Correctness, Confirmed("a")), Review(ReviewRoles.SpecConformance));
        Assert.True(FixLoop.Judge(1, two, one, ["t"], []).MadeProgress);
        Assert.False(FixLoop.Judge(1, two, one, ["t"], ["t"]).MadeProgress);
        Assert.False(FixLoop.Judge(1, one, one with { HeadSha = ShaB }, [], []).MadeProgress);
        Assert.False(FixLoop.Judge(1, two, Verdict(ShaA, Review(ReviewRoles.Correctness) with { Error = "x" }, Review(ReviewRoles.SpecConformance)), [], []).MadeProgress);
        var record = FixLoop.Judge(1, two, one, ["t"], []);
        var read = FixProgress.FromDetail(record.ToDetail());
        Assert.NotNull(read);
        Assert.Equal(record.ToDetail(), read.ToDetail());
    }
}
