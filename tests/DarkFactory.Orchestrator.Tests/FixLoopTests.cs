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
        // The merge record names the carried review and the commit it was made on.
        var gate = (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail!;
        Assert.Contains($"{ReviewRoles.SpecConformance}: gpt-5.5 (openai), carried from {Sha1[..12]}", gate);
        Assert.DoesNotContain($"{ReviewRoles.Correctness}: gpt-5.5 (openai), carried", gate);

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
        var (failing, missing, pending) = FixLoop.Regressions(["a", "d", "e"], now);
        Assert.Equal(["a"], failing);
        Assert.Equal(["e"], missing);
        Assert.Equal(["d"], pending);

        // No verdict: still running, cancelled, stale, or no conclusion — and no run of the name failed.
        var unknown = new CiFacts(Sha1, [new("a", true, "success"), new("b", true, "failure"), new("c", false, null), new("d", true, "cancelled"),
            new("e", true, "stale"), new("f", true, null), new("g", true, "cancelled"), new("g", true, "failure")]);
        Assert.Equal(["c", "d", "e", "f"], FixLoop.UnknownChecks(unknown));

        Assert.True(FixLoop.Settled(new CiFacts(Sha1, [new("a", true, "failure")])));
        Assert.False(FixLoop.Settled(new CiFacts(Sha1, [new("a", true, "failure"), new("b", false, null)])));
        Assert.False(FixLoop.Settled(new CiFacts(Sha1, [])));
        Assert.False(FixLoop.Settled(new CiFacts(Sha1, [new("a", true, "success")], Complete: false)));
        Assert.False(FixLoop.Settled(new CiFacts(Sha1, [new("a", true, "success")], Suites: [new(Orchestrator.Gate.Ci.ActionsApp, false, null, 0)])));
    }

    [Fact]
    public async Task The_progress_check_waits_for_the_fixed_heads_ci_before_reading_what_passed_there()
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one"), Blocking("two")], [ShaA] = [Blocking("two")] },
            }),
        };
        // The fixed head failed review before its CI was awaited: unit-tests is still running on the first read.
        h.GitHub.Ci[Sha1] = new CiFacts(Sha1, [new("build", true, "success"), new("unit-tests", false, null)]);
        var sha1Reads = 0;
        h.GitHub.OnCiRead = sha =>
        {
            if (sha == Sha1 && ++sha1Reads == 2)
            {
                h.GitHub.Ci[Sha1] = Ci(Sha1, ("build", "success"), ("unit-tests", "success"));
            }
        };
        h.GitHub.Ci[ShaA] = Ci(ShaA, ("build", "success"), ("unit-tests", "failure"));
        h.GitHub.Ci[ShaB] = Ci(ShaB, ("build", "success"), ("unit-tests", "success"));

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var progress = await Progress(h);
        Assert.Equal(["build", "unit-tests"], progress[0].PassedBefore);
        Assert.Empty(progress[0].UnknownBefore);
        Assert.Equal(FixProgress.Failed, progress[0].Outcome);
        Assert.Equal(["unit-tests"], progress[0].FailingNow);
    }

    [Theory]
    [InlineData("failure", FixProgress.Failed)]
    [InlineData("success", FixProgress.Progress)]
    public async Task A_check_cancelled_on_the_fixed_head_is_compared_on_the_new_head_like_one_that_passed(string now, string outcome)
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one"), Blocking("two")], [ShaA] = [Blocking("two")] },
            }),
        };
        // The fixer's push cancelled unit-tests on the fixed head (a concurrency group): it never reached a verdict there.
        h.GitHub.Ci[Sha1] = Ci(Sha1, ("build", "success"), ("unit-tests", "cancelled"));
        h.GitHub.Ci[ShaA] = Ci(ShaA, ("build", "success"), ("unit-tests", now));
        h.GitHub.Ci[ShaB] = Ci(ShaB, ("build", "success"), ("unit-tests", "success"));

        var run = await h.Run();

        Assert.True(run.Succeeded, run.Error);
        var round = (await Progress(h))[0];
        Assert.Equal(outcome, round.Outcome);
        Assert.Equal(["build"], round.PassedBefore);
        Assert.Equal(["unit-tests"], round.UnknownBefore);
        Assert.Contains("unit-tests", round.Reason);
        Assert.Contains($"no verdict on {Sha1[..12]}", round.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ci_that_could_not_be_read_in_full_makes_a_failed_round(bool onTheFixedHead)
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one"), Blocking("two")], [ShaA] = [Blocking("two")] },
            }),
        };
        var unread = onTheFixedHead ? Sha1 : ShaA;
        h.GitHub.Ci[Sha1] = Ci(Sha1, ("build", "success"));
        h.GitHub.Ci[ShaA] = Ci(ShaA, ("build", "success"));
        h.GitHub.Ci[unread] = h.GitHub.Ci[unread] with { Complete = false };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var round = (await Progress(h))[0];
        Assert.Equal(FixProgress.Failed, round.Outcome);
        Assert.StartsWith($"CI could not be read in full on {unread[..12]}", round.Reason);
    }

    [Fact]
    public async Task A_check_that_passed_before_and_has_no_run_on_the_finished_new_head_is_a_failed_round()
    {
        var h = new Harness
        {
            Reviewer = ReviewerFor(new()
            {
                [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one"), Blocking("two")], [ShaA] = [Blocking("two")] },
            }),
        };
        h.GitHub.Ci[Sha1] = Ci(Sha1, ("build", "success"), ("unit-tests", "success"));
        h.GitHub.Ci[ShaA] = Ci(ShaA, ("build", "success")); // the fix removed (or renamed) the unit-tests job
        h.GitHub.Ci[ShaB] = Ci(ShaB, ("build", "success"));

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        var round = (await Progress(h))[0];
        Assert.Equal(FixProgress.Failed, round.Outcome);
        Assert.Equal(["unit-tests"], round.FailingNow);
        Assert.Contains($"missing on {ShaA[..12]}: unit-tests", round.Reason);
    }

    [Fact]
    public async Task A_head_that_is_not_the_fixers_push_escalates_without_being_judged_as_the_round()
    {
        var h = new Harness
        {
            FixHeads = [ShaC],
            Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }),
        };
        // The fixer pushed ShaA, but by the time the review reads the PR someone else has pushed ShaC.
        h.Workspaces.Head = () => h.GitHub.Head == ShaC ? ShaA : h.GitHub.Head;

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains($"head is {ShaC}", outcome.Error);
        Assert.DoesNotContain(h.Reviewer.Requests, r => r.Pull.HeadSha == ShaC);
        Assert.Empty(await Progress(h));
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_check_still_unfinished_on_the_new_head_at_the_ci_timeout_escalates_without_a_progress_record()
    {
        var h = new Harness
        {
            GateOnFakeClock = true,
            Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }),
        };
        h.GitHub.Ci[ShaA] = new CiFacts(ShaA, [new("build-test", false, null)]);
        h.GitHub.OnCiRead = sha =>
        {
            if (sha == ShaA)
            {
                h.Time.Advance(TimeSpan.FromMinutes(10)); // past the 5 s CI timeout
            }
        };

        var outcome = await h.Run().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("fix round 1's progress cannot be judged", outcome.Error);
        Assert.Contains("build-test", outcome.Error);
        Assert.Empty(await Progress(h));
        Assert.Contains("fix round 1's progress cannot be judged", h.Stories.Comments.Single());
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_crash_after_the_verdict_but_before_the_progress_check_does_not_review_again_and_records_one_progress_check()
    {
        var h = new Harness { Reviewer = ReviewerFor(new() { [ReviewRoles.Correctness] = new() { [Sha1] = [Blocking("one")] } }) };
        using var crash = new CancellationTokenSource();
        h.GitHub.OnCiRead = sha =>
        {
            if (sha == Sha1) // the progress check's first read, after the verdict on ShaA was checkpointed
            {
                crash.Cancel();
                throw new OperationCanceledException(crash.Token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(ct: crash.Token));
        Assert.Contains(await h.Verdicts(), v => v.HeadSha == ShaA);
        Assert.Empty(await Progress(h));
        h.GitHub.OnCiRead = null;

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single(h.Reviewer.Requests, r => r.Pull.HeadSha == ShaA);
        var progress = (await Progress(h)).Single(); // the judged record, not a placeholder written before the CI reads
        Assert.Equal(FixProgress.Progress, progress.Outcome);
        Assert.Equal(["build-test"], progress.PassedBefore);
        Assert.Equal([$"merge 1 {ShaA}"], h.Merges);
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
