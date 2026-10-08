using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25378: after Implement, review → CI → merge gate → merge, through the pipeline with fake GitHub and reviewer.</summary>
public class GatePipelineTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string MergeCommit = "9999999999999999999999999999999999999999";
    private const string ImplementerModel = "claude-sonnet-4-5-20250929";
    private const string Policy = "version: 1\nrequire:\n  ci: green\n  review: pass\n";

    private static readonly WorkStory Story =
        new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");

    /// <summary>GitHub as the gate sees it: one PR whose head the test can move, CI per commit, the base branch's policy.</summary>
    internal sealed class FakeGateGitHub : IGateGitHub
    {
        public string Head { get; set; } = Sha1;
        public bool Open { get; set; } = true;
        public bool Merged { get; set; }
        public string? MergeCommitSha { get; set; }
        public string? PolicyText { get; set; } = Policy;
        public Dictionary<string, CiFacts> Ci { get; } = new();
        public List<string> Calls { get; } = [];
        /// <summary>Runs on each PR read, e.g. to push a new head at a given moment.</summary>
        public Action<int>? OnPullRead { get; set; }
        /// <summary>When set, the merge finds the head moved to this commit (GitHub's 409).</summary>
        public string? HeadMovesBeforeMerge { get; set; }
        private int _reads;

        public Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct)
        {
            Calls.Add($"pull {number}");
            OnPullRead?.Invoke(++_reads);
            return Task.FromResult(new PullFacts(number, PrUrl, Open && !Merged, Merged, false, Head, "main", "base0", MergeCommitSha));
        }

        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
        {
            Calls.Add($"diff {baseSha}...{headSha}");
            return Task.FromResult($"diff --git a/x b/x\n+change at {headSha}\n");
        }

        public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct)
        {
            Calls.Add($"policy {baseRef}");
            return Task.FromResult(PolicyText);
        }

        public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Calls.Add($"ci {sha}");
            return Task.FromResult(Ci.TryGetValue(sha, out var facts) ? facts : Green(sha));
        }

        /// <summary>When set, the merge call throws this; with <see cref="MergesBeforeThrowing"/>, after GitHub merged (e.g. a timeout).</summary>
        public Exception? MergeThrows { get; set; }
        public bool MergesBeforeThrowing { get; set; }
        /// <summary>Runs as the merge call starts, e.g. to press Ctrl-C while it is in flight.</summary>
        public Action? OnMergeCall { get; set; }

        public Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct)
        {
            Calls.Add($"merge {number} {headSha}");
            OnMergeCall?.Invoke();
            if (HeadMovesBeforeMerge is { } moved)
            {
                Head = moved;
                HeadMovesBeforeMerge = null;
            }
            if (headSha != Head)
            {
                return Task.FromResult(new MergeResult(false, null, true, "Head branch was modified."));
            }
            if (MergeThrows is { } failure && !MergesBeforeThrowing)
            {
                throw failure;
            }
            Merged = true;
            MergeCommitSha = MergeCommit;
            if (MergeThrows is { } late)
            {
                throw late;
            }
            return Task.FromResult(new MergeResult(true, MergeCommit, false, "Pull Request successfully merged"));
        }

        public static CiFacts Green(string sha) => new(sha, [new CheckFact("build-test", true, "success")]);
    }

    /// <summary>
    /// A reviewer that answers <c>verdict</c> under the request's router session (as <see cref="RouterReviewer"/> does);
    /// its first <see cref="UsageLimitedCalls"/> calls are refused by the router for usage.
    /// </summary>
    internal sealed class FakeReviewer(string verdict = ReviewVerdict.Pass) : IReviewer
    {
        public List<ReviewRequest> Requests { get; } = [];
        public int UsageLimitedCalls { get; set; }

        public Task<ReviewVerdict> ReviewAsync(ReviewRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Requests.Count <= UsageLimitedCalls)
            {
                throw new RouterUsageLimitedException("The reviewer call through the router failed: 429 All enrolled subscription accounts are currently unavailable.");
            }
            return Task.FromResult(new ReviewVerdict(request.Pull.HeadSha, verdict, request.Model, request.Model, ModelFamily.Of(request.Model),
                verdict == ReviewVerdict.Pass ? "looks right" : "the tests do not cover the empty string", request.Session));
        }
    }

    /// <summary>A worker that reports the models that answered it, then succeeds.</summary>
    private static Func<WorkerCall, Task<WorkerResult>> ReportsModel(params string?[] models) => async call =>
    {
        await call.OnSession(Ok.SessionId!, CancellationToken.None);
        foreach (var model in models.OfType<string>())
        {
            await call.Callbacks.OnModel!(model, CancellationToken.None);
        }
        return Ok;
    };

    private sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness() => Db = new LedgerDbContext(_options);

        public LedgerDbContext Db { get; }
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        /// <summary>The shared control table (needed for the usage pause), on the injected clock.</summary>
        public IControls Controls => new LedgerControls(new LedgerDbContextFactory(_options), Time);
        public FakeWorkSource Stories { get; } = new(Story);
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public InProcessRunLocks Locks { get; } = new();
        public FakeGateGitHub GitHub { get; } = new();
        public FakeReviewer Reviewer { get; init; } = new();
        public IReadOnlyList<string> ReviewerModels { get; init; } = GateStage.DefaultReviewerModels;
        public WorkLedger Ledger => new(Db, TimeProvider.System);

        public Task<RunOutcome> Run(string? implementerModel = ImplementerModel, CancellationToken ct = default) =>
            Run([implementerModel], ct);

        public Task<RunOutcome> Run(string?[] implementerModels, CancellationToken ct = default) =>
            new RunPipeline(Stories, Ledger, Locks, Workspaces, new FakeWorker(ReportsModel(implementerModels)), Prs, Sandbox, TextWriter.Null,
                    controls: Controls,
                    gate: new GateStage(GitHub, Reviewer, ReviewerModels, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5)))
                .RunAsync(77, ct);

        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        public async Task<List<WorkState>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => r.State).ToList();
        public async Task<List<ReviewVerdict>> Verdicts() =>
            (await Rows()).Where(r => r.Step == RunPipeline.Steps.Verdict).Select(r => ReviewVerdict.FromDetail(r.Detail)!).ToList();
        public List<string> Merges => GitHub.Calls.Where(c => c.StartsWith("merge ")).ToList();
    }

    [Fact]
    public async Task Green_ci_and_a_different_family_pass_is_merged_by_the_gate_and_the_ledger_records_the_merge_commit()
    {
        var h = new Harness();

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
            await h.Transitions());
        var rows = await h.Rows();
        Assert.Equal(MergeCommit, rows.Single(r => r.Step is null && r.State == WorkState.Merge).Detail);
        Assert.Equal((WorkState.Watch, PrUrl), (outcome.State, outcome.PullRequestUrl));

        // The implementer's model was recorded; the reviewer was pinned to another family and bound to the head commit.
        Assert.Equal(ImplementerModel, rows.Single(r => r.Step == RunPipeline.Steps.ImplementerModel).Detail);
        var review = h.Reviewer.Requests.Single();
        Assert.Equal("gpt-5.6-sol", review.Model);
        Assert.Equal(Sha1, review.Pull.HeadSha);
        Assert.Contains($"change at {Sha1}", review.Diff);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal((Sha1, ReviewVerdict.Pass, "openai"), (verdict.HeadSha, verdict.Verdict, verdict.Family));

        // The gate read its policy from the base branch, and merged exactly the reviewed, green commit, once.
        Assert.Contains("policy main", h.GitHub.Calls);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        Assert.StartsWith("Merge ", rows.Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
        Assert.Equal(Sha1, rows.Single(r => r.Step == RunPipeline.Steps.GatePassed).Detail);
        Assert.Contains("state 77 Merged", h.Stories.Writes);
        // Every checkpoint of a stage sits after its state's transition row (a step counts only once the ledger has it).
        Assert.True(rows.FindIndex(r => r.Step == RunPipeline.Steps.Verdict) > rows.FindIndex(r => r.Step is null && r.State == WorkState.Review));
    }

    [Fact]
    public async Task A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again()
    {
        var h = new Harness();
        // Reads: 1 Review (reviews Sha1), 2 CI → the head has moved to Sha2 since the verdict.
        h.GitHub.OnPullRead = read =>
        {
            if (read == 2)
            {
                h.GitHub.Head = Sha2;
            }
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.Review, WorkState.CI, WorkState.MergeGate,
            WorkState.Merge, WorkState.Watch], await h.Transitions());
        Assert.Equal([Sha1, Sha2], h.Reviewer.Requests.Select(r => r.Pull.HeadSha));
        Assert.Equal([Sha1, Sha2], (await h.Verdicts()).Select(v => v.HeadSha));
        Assert.Equal([$"merge 1 {Sha2}"], h.Merges); // never the stale commit
        Assert.DoesNotContain($"ci {Sha1}", h.GitHub.Calls); // the voided commit's CI was never consulted
    }

    [Fact]
    public async Task A_push_between_the_gate_and_the_merge_is_refused_by_github_and_reviewed_again()
    {
        var h = new Harness();
        h.GitHub.HeadMovesBeforeMerge = Sha2;

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Review, WorkState.CI,
            WorkState.MergeGate, WorkState.Merge, WorkState.Watch], await h.Transitions());
        Assert.Equal([$"merge 1 {Sha1}", $"merge 1 {Sha2}"], h.Merges);
        Assert.Equal([Sha1, Sha2], h.Reviewer.Requests.Select(r => r.Pull.HeadSha));
    }

    [Fact]
    public async Task A_head_with_no_verdict_at_the_gate_goes_back_to_review()
    {
        var h = new Harness();
        // Reads: 1 Review, 2 CI (still Sha1), 3 MergeGate → moved.
        h.GitHub.OnPullRead = read =>
        {
            if (read == 3)
            {
                h.GitHub.Head = Sha2;
            }
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Review, WorkState.CI,
            WorkState.MergeGate, WorkState.Merge, WorkState.Watch], await h.Transitions());
        Assert.Equal([$"merge 1 {Sha2}"], h.Merges);
        Assert.Contains((await h.Rows()).Where(r => r.Step == RunPipeline.Steps.GateDecision), r => r.Detail!.StartsWith("ReviewHead "));
    }

    [Theory]
    [InlineData(null, "does not exist")]
    [InlineData("version: 1\nrequire:\n  review: pass\n", "missing 'ci'")]
    [InlineData("version: 1\nrequire:\n  ci: off\n  review: pass\n", "require.ci must be 'green'")]
    [InlineData("version: [", "not valid YAML")]
    public async Task A_missing_or_invalid_policy_means_no_merge_and_an_escalation(string? policy, string reason)
    {
        var h = new Harness();
        h.GitHub.PolicyText = policy;

        var outcome = await h.Run();

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
        Assert.Contains(h.Stories.Comments, c => c.Contains("escalated") && c.Contains(reason));
    }

    [Fact]
    public async Task Failing_ci_on_the_head_escalates_without_merging()
    {
        var h = new Harness();
        h.GitHub.Ci[Sha1] = new CiFacts(Sha1, [new CheckFact("build-test", true, "failure")]);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("CI failed", outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Equal([WorkState.CI, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
    }

    [Fact]
    public async Task Ci_that_never_reports_escalates_at_the_timeout()
    {
        var h = new Harness();
        h.GitHub.Ci[Sha1] = new CiFacts(Sha1, []);

        var outcome = await new RunPipeline(h.Stories, h.Ledger, h.Locks, h.Workspaces, new FakeWorker(ReportsModel(ImplementerModel)), h.Prs, Sandbox,
                TextWriter.Null, gate: new GateStage(h.GitHub, h.Reviewer, GateStage.DefaultReviewerModels, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)))
            .RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("no CI check has reported", outcome.Error);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_fail_verdict_escalates_before_ci_and_merges_nothing()
    {
        var h = new Harness { Reviewer = new FakeReviewer(ReviewVerdict.Fail) };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("the tests do not cover the empty string", outcome.Error);
        Assert.Equal([WorkState.Review, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
        Assert.DoesNotContain(h.GitHub.Calls, c => c.StartsWith("ci ") || c.StartsWith("merge "));
    }

    [Fact]
    public async Task An_implementer_of_unknown_model_cannot_get_a_different_family_reviewer_and_escalates()
    {
        var h = new Harness();

        var outcome = await h.Run(implementerModel: null);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("implementer's model is unknown", outcome.Error);
        Assert.Empty(h.Reviewer.Requests);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task The_reviewer_family_is_the_one_the_implementer_did_not_use()
    {
        var h = new Harness();

        await h.Run(implementerModel: "gpt-5.6-sol");

        Assert.Equal("claude-opus-5-5", h.Reviewer.Requests.Single().Model);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task A_run_that_crashed_after_github_merged_records_the_merge_without_merging_again()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);
        foreach (var state in new[] { WorkState.Implement, WorkState.Review })
        {
            await ledger.RecordAsync(item, state, null, state == WorkState.Review ? PrUrl : null, CancellationToken.None);
        }
        await ledger.CheckpointAsync(item, RunPipeline.Steps.ImplementerModel, null, ImplementerModel, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Verdict, null,
            new ReviewVerdict(Sha1, ReviewVerdict.Pass, "gpt-5.6-sol", "gpt-5.6-sol", "openai", "ok").ToDetail(), CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.CI, null, Sha1, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.MergeGate, null, Sha1, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.GatePassed, null, Sha1, CancellationToken.None);
        (h.GitHub.Merged, h.GitHub.MergeCommitSha) = (true, MergeCommit);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Equal(MergeCommit, (await h.Rows()).Single(r => r.Step is null && r.State == WorkState.Merge).Detail);
        Assert.Equal(WorkState.Watch, outcome.State);
    }

    [Fact]
    public async Task A_pr_merged_outside_the_gate_escalates()
    {
        var h = new Harness();
        h.GitHub.OnPullRead = read =>
        {
            if (read == 3)
            {
                (h.GitHub.Merged, h.GitHub.MergeCommitSha) = (true, MergeCommit);
            }
        };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("merged outside the gate", outcome.Error);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task The_reviewers_router_session_is_named_before_the_call_and_never_becomes_the_items_claude_session()
    {
        var h = new Harness();

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(Ok.SessionId, outcome.SessionId); // the implementer's session, not the reviewer's
        var rows = await h.Rows();
        Assert.All(rows.Where(r => r.ClaudeSessionId is not null), r => Assert.Equal(Ok.SessionId, r.ClaudeSessionId));
        var review = h.Reviewer.Requests.Single();
        var named = rows.FindIndex(r => r.Step == RunPipeline.Steps.ReviewSession);
        Assert.Equal($"{review.Session} gpt-5.6-sol {Sha1}", rows[named].Detail);
        Assert.True(named < rows.FindIndex(r => r.Step == RunPipeline.Steps.Verdict)); // in the ledger before the verdict
        Assert.Equal(review.Session, (await h.Verdicts()).Single().Session); // and the verdict still carries it
    }

    [Fact]
    public async Task An_escalation_after_a_review_names_the_implementers_session_not_the_reviewers()
    {
        var h = new Harness { Reviewer = new FakeReviewer(ReviewVerdict.Fail) };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(Ok.SessionId, outcome.SessionId);
        Assert.Equal(Ok.SessionId, (await h.Rows()).Single(r => r.Step is null && r.State == WorkState.Escalated).ClaudeSessionId);
        Assert.Contains(h.Stories.Comments, c => c.Contains($"Claude session: {Ok.SessionId}"));
    }

    [Fact]
    public async Task A_reviewer_call_the_router_refuses_for_usage_pauses_the_factory_and_reviews_again_after_it_lifts()
    {
        var h = new Harness { Reviewer = new FakeReviewer { UsageLimitedCalls = 1 } };

        var paused = await h.Run();

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.Contains("paused for usage", paused.Error);
        var rows = await h.Rows();
        Assert.Equal((WorkState.Paused, RunPipeline.UsagePaused), rows.Where(r => r.Step is null).Select(r => (r.State, r.Detail)).Last());
        Assert.DoesNotContain(rows, r => r.Step is null && r.State == WorkState.Escalated);
        Assert.Empty(h.Stories.Comments);
        Assert.StartsWith(UsagePause.ReviewerRateLimited, rows.Last(r => r.Step == RunPipeline.Steps.UsagePause).Detail);
        Assert.Equal(UsagePause.ReviewerRateLimited, (await h.Controls.UsagePauseAsync(CancellationToken.None))!.Reason);
        Assert.Empty(h.Merges);

        h.Time.Advance(UsagePause.InitialBackoff);
        var resumed = await h.Run();

        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal(WorkState.Watch, resumed.State);
        Assert.Equal(2, h.Reviewer.Requests.Count); // the same head reviewed again
        Assert.Equal([WorkState.Review, WorkState.Paused, WorkState.Review, WorkState.CI],
            (await h.Transitions()).SkipWhile(s => s != WorkState.Review).Take(4));
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task An_interrupt_during_the_merge_after_github_merged_records_the_merge_on_resume_without_merging_again()
    {
        var h = new Harness();
        using var interrupt = new CancellationTokenSource();
        // Ctrl-C arrives while the merge call is in flight; GitHub has merged by then.
        h.GitHub.MergesBeforeThrowing = true;
        h.GitHub.MergeThrows = new OperationCanceledException(interrupt.Token);
        h.GitHub.OnMergeCall = interrupt.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(ct: interrupt.Token));
        Assert.Equal([WorkState.MergeGate, WorkState.Paused], (await h.Transitions()).TakeLast(2));
        Assert.Equal(RunPipeline.Interrupted, (await h.Rows()).Last(r => r.Step is null).Detail);
        (h.GitHub.MergeThrows, h.GitHub.OnMergeCall) = (null, null);

        var resumed = await h.Run();

        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal(WorkState.Watch, resumed.State);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges); // only the interrupted call
        Assert.Equal([WorkState.MergeGate, WorkState.Paused, WorkState.MergeGate, WorkState.Merge, WorkState.Watch], (await h.Transitions()).TakeLast(5));
        Assert.Equal(MergeCommit, (await h.Rows()).Single(r => r.Step is null && r.State == WorkState.Merge).Detail);
    }

    [Fact]
    public async Task A_merge_call_that_fails_after_github_merged_records_the_merge_instead_of_escalating()
    {
        var h = new Harness();
        h.GitHub.MergesBeforeThrowing = true;
        h.GitHub.MergeThrows = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        Assert.Equal(MergeCommit, (await h.Rows()).Single(r => r.Step is null && r.State == WorkState.Merge).Detail);
        Assert.DoesNotContain(WorkState.Escalated, await h.Transitions());
    }

    [Fact]
    public async Task A_merge_call_that_fails_without_github_merging_escalates()
    {
        var h = new Harness();
        h.GitHub.MergeThrows = new HttpRequestException("connection reset");

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("connection reset", outcome.Error);
        Assert.False(h.GitHub.Merged);
    }

    [Fact]
    public async Task An_implementer_answered_by_two_families_gets_a_reviewer_of_a_third_configured_family()
    {
        var h = new Harness { ReviewerModels = ["gpt-5.6-sol", "claude-opus-5-5", "gemini-3.1-pro-preview"] };

        var outcome = await h.Run(["claude-sonnet-4-5", "gpt-5.6-luna"]);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("gemini-3.1-pro-preview", h.Reviewer.Requests.Single().Model);
        Assert.Equal("google", (await h.Verdicts()).Single().Family);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task Without_a_gate_stage_the_pipeline_still_parks_at_review()
    {
        var h = new Harness();

        var outcome = await new RunPipeline(h.Stories, h.Ledger, h.Locks, h.Workspaces, new FakeWorker(ReportsModel(ImplementerModel)), h.Prs, Sandbox,
            TextWriter.Null).RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Review, outcome.State);
        Assert.Empty(h.GitHub.Calls);
    }
}
