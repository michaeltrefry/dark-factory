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

        /// <summary>The PR's diff for a head commit; by default a one-line change to an unrisky file.</summary>
        public Func<string, string> Diff { get; set; } = head => $"diff --git a/src/x.cs b/src/x.cs\n+change at {head}\n";

        public RepoFiles Files { get; set; } = new(["README.md", "src/x.cs"], false);

        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
        {
            Calls.Add($"diff {baseSha}...{headSha}");
            return Task.FromResult(Diff(headSha));
        }

        public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Calls.Add($"files {sha}");
            return Task.FromResult(Files);
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
    /// The panel's calls, answered as <see cref="RouterReviewer"/> would (served by the pinned model, under the request's
    /// session and prompt): each role reports <see cref="Findings"/>, each second model answers <see cref="Confirm"/>. The
    /// first <see cref="UsageLimitedCalls"/> role reviews are refused by the router for usage.
    /// </summary>
    internal sealed class FakeReviewer : IReviewer
    {
        public List<ReviewRequest> Requests { get; } = [];
        public List<ConfirmRequest> Confirms { get; } = [];
        public int UsageLimitedCalls { get; set; }
        public Func<ReviewRequest, IReadOnlyList<Finding>> Findings { get; init; } = _ => [];
        public Func<ConfirmRequest, string> Confirm { get; init; } = _ => Confirmation.Confirmed;

        /// <summary>The correctness reviewer reports one blocking finding (which the second model confirms by default).</summary>
        public static FakeReviewer Blocking(string title = "the tests do not cover the empty string") => new()
        {
            Findings = r => r.Role == ReviewRoles.Correctness ? [new Finding(Finding.Blocking, title, "src/x.cs", 1, "WordCount(\"\") is untested")] : [],
        };

        public Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Requests.Count <= UsageLimitedCalls)
            {
                throw new RouterUsageLimitedException("The review call through the router failed: 429 All enrolled subscription accounts are currently unavailable.");
            }
            return Task.FromResult(new RoleReview(request.Role, request.Model, request.Model, ModelFamily.Of(request.Model), request.Session,
                request.Prompt.Id, Findings(request), "reviewed"));
        }

        public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Confirms.Add(request);
            return Task.FromResult(new Confirmation(Confirm(request), request.Model, request.Model, ModelFamily.Of(request.Model), request.Session,
                request.Prompt.Id, "checked against the diff"));
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
        /// <summary>When set, the panel's calls go here instead of <see cref="Reviewer"/> (e.g. a real <see cref="RouterReviewer"/>).</summary>
        public IReviewer? Panel { get; init; }
        public ReviewPanelModels Models { get; init; } = ReviewPanelModels.Default;
        public WorkLedger Ledger => new(Db, TimeProvider.System);

        public Task<RunOutcome> Run(string? implementerModel = ImplementerModel, CancellationToken ct = default) =>
            Run([implementerModel], ct);

        public Task<RunOutcome> Run(string?[] implementerModels, CancellationToken ct = default) =>
            new RunPipeline(Stories, Ledger, Locks, Workspaces, new FakeWorker(ReportsModel(implementerModels)), Prs, Sandbox, TextWriter.Null,
                    controls: Controls,
                    gate: new GateStage(GitHub, Panel ?? Reviewer, Models, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5)))
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

        // The implementer's model was recorded; the panel (correctness and spec conformance: the diff touches no risky path)
        // was pinned to another family and bound to the head commit, with the base's file list.
        Assert.Equal(ImplementerModel, rows.Single(r => r.Step == RunPipeline.Steps.ImplementerModel).Detail);
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance], h.Reviewer.Requests.Select(r => r.Role));
        Assert.All(h.Reviewer.Requests, review =>
        {
            Assert.Equal("gpt-5.5", review.Model);
            Assert.Equal(Sha1, review.Pull.HeadSha);
            Assert.Contains($"change at {Sha1}", review.Diff);
            Assert.Equal(h.GitHub.Files, review.Files);
        });
        Assert.Contains("files base0", h.GitHub.Calls);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal((Sha1, ReviewVerdict.Pass), (verdict.HeadSha, verdict.Verdict));
        Assert.Empty(verdict.RiskyPaths);
        Assert.All(verdict.Reviews, r => Assert.Equal("openai", r.Family));

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
        Assert.Equal([Sha1, Sha2], h.Reviewer.Requests.Select(r => r.Pull.HeadSha).Distinct());
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
        Assert.Equal([Sha1, Sha2], h.Reviewer.Requests.Select(r => r.Pull.HeadSha).Distinct());
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
                TextWriter.Null, gate: new GateStage(h.GitHub, h.Reviewer, ReviewPanelModels.Default, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)))
            .RunAsync(77, CancellationToken.None);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("no CI check has reported", outcome.Error);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_fail_verdict_escalates_before_ci_and_merges_nothing()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };

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

        Assert.All(h.Reviewer.Requests, r => Assert.Equal("claude-opus-5", r.Model));
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
            ReviewPanel.Decide(Sha1, [], ReviewRoles.Required(false)
                .Select(r => new RoleReview(r, "gpt-5.5", "gpt-5.5", "openai", null, null, [], "ok")).ToList()).ToDetail(), CancellationToken.None);
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
        // One named session per panel call, each recording the role's prompt file and its hash, in the ledger before the verdict.
        var named = rows.Where(r => r.Step == RunPipeline.Steps.ReviewSession).Select(r => r.Detail).ToList();
        Assert.Equal(h.Reviewer.Requests.Select(r => $"{r.Session} gpt-5.5 {Sha1} {r.Role} factory/prompts/{r.Role}.md@sha256:{r.Prompt.Sha256}"), named);
        Assert.Equal(2, h.Reviewer.Requests.Select(r => r.Session).Distinct().Count());
        Assert.True(rows.FindLastIndex(r => r.Step == RunPipeline.Steps.ReviewSession) < rows.FindIndex(r => r.Step == RunPipeline.Steps.Verdict));
        // ...and the verdict still carries each session and prompt.
        Assert.Equal(h.Reviewer.Requests.Select(r => (r.Session, r.Prompt.Id)), (await h.Verdicts()).Single().Reviews.Select(r => (r.Session!, r.Prompt!)));
    }

    [Fact]
    public async Task An_escalation_after_a_review_names_the_implementers_session_not_the_reviewers()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };

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
        Assert.Equal(3, h.Reviewer.Requests.Count); // the refused call, then the same head reviewed again by the whole panel
        Assert.All(h.Reviewer.Requests, r => Assert.Equal(Sha1, r.Pull.HeadSha));
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
        var h = new Harness { Models = ReviewPanelModels.Uniform(["gpt-5.5", "claude-opus-5", "gemini-3.1-pro-preview"]) };

        var outcome = await h.Run(["claude-sonnet-4-5", "gpt-5.6-luna"]);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.All(h.Reviewer.Requests, r => Assert.Equal("gemini-3.1-pro-preview", r.Model));
        Assert.All((await h.Verdicts()).Single().Reviews, r => Assert.Equal("google", r.Family));
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    // ---- sc-25379: the review panel ----

    private static string RiskyDiff(string head) =>
        $"diff --git a/.github/workflows/ci.yml b/.github/workflows/ci.yml\n--- a/.github/workflows/ci.yml\n+++ b/.github/workflows/ci.yml\n+  # change at {head}\n";

    [Fact]
    public async Task A_diff_touching_a_risky_path_adds_the_security_review_to_the_panel()
    {
        var h = new Harness();
        h.GitHub.Diff = RiskyDiff;

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role));
        Assert.Equal("factory/prompts/security.md", h.Reviewer.Requests.Single(r => r.Role == ReviewRoles.Security).Prompt.Path);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal([".github/workflows/ci.yml (CI and repository automation)"], verdict.RiskyPaths);
        Assert.Equal(ReviewRoles.All, verdict.Reviews.Select(r => r.Role));
    }

    [Fact]
    public async Task A_blocking_finding_the_second_model_does_not_confirm_is_downgraded_to_optional_and_does_not_block()
    {
        var h = new Harness
        {
            Reviewer = new FakeReviewer
            {
                Findings = FakeReviewer.Blocking("WordCount(\"\") still returns 1").Findings,
                Confirm = _ => Confirmation.NotConfirmed,
            },
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        // The second model is another model than the reviewer's, of no implementer family (anthropic here): the other openai one.
        var confirm = h.Reviewer.Confirms.Single();
        Assert.Equal(("gpt-5.4-mini", ReviewRoles.Correctness, "WordCount(\"\") still returns 1"), (confirm.Model, confirm.Role, confirm.Finding.Title));
        Assert.Equal("factory/prompts/confirm.md", confirm.Prompt.Path);
        var finding = (await h.Verdicts()).Single().Reviews.Single(r => r.Role == ReviewRoles.Correctness).Findings.Single();
        Assert.Equal((Finding.Optional, true, Confirmation.NotConfirmed), (finding.Severity, finding.Downgraded, finding.Confirmation!.Outcome));
        // The confirmation call's session is named before it, with the confirm prompt's hash.
        Assert.Contains((await h.Rows()).Where(r => r.Step == RunPipeline.Steps.ReviewSession).Select(r => r.Detail),
            d => d == $"{confirm.Session} gpt-5.4-mini {Sha1} confirm-correctness {ReviewPrompts.Confirm.Id}");
    }

    [Fact]
    public async Task A_confirmed_blocking_finding_fails_the_review_and_escalates_without_merging()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("blocking correctness finding by gpt-5.5, confirmed by gpt-5.4-mini", outcome.Error);
        Assert.Contains("the tests do not cover the empty string (src/x.cs:1)", outcome.Error);
        var finding = (await h.Verdicts()).Single().Reviews.Single(r => r.Role == ReviewRoles.Correctness).Findings.Single();
        Assert.Equal((Finding.Blocking, false, Confirmation.Confirmed), (finding.Severity, finding.Downgraded, finding.Confirmation!.Outcome));
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_blocking_finding_whose_second_model_answer_is_unusable_stays_blocking()
    {
        var h = new Harness { Reviewer = new FakeReviewer { Findings = FakeReviewer.Blocking().Findings, Confirm = _ => Confirmation.Unusable } };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("the second model's answer was unusable", outcome.Error);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task Optional_findings_do_not_block_and_are_never_sent_for_confirmation()
    {
        var h = new Harness { Reviewer = new FakeReviewer { Findings = _ => [new Finding(Finding.Optional, "rename the helper", null, null, "taste")] } };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(h.Reviewer.Confirms);
        Assert.Equal(2, (await h.Verdicts()).Single().Reviews.Sum(r => r.Findings.Count));
    }

    [Fact]
    public async Task No_eligible_second_model_escalates_with_the_reason()
    {
        // Implementer anthropic, reviewer gpt-5.5: the only second model configured is the reviewer's own.
        var h = new Harness { Reviewer = FakeReviewer.Blocking(), Models = ReviewPanelModels.Uniform(["gpt-5.5"], ["gpt-5.5", "claude-opus-5"]) };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("a blocking finding cannot be confirmed; set Review:Confirm:Models", outcome.Error);
        Assert.Empty(h.Reviewer.Confirms);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_role_with_no_eligible_family_escalates_with_the_reason_before_any_call()
    {
        var models = new Dictionary<string, IReadOnlyList<string>>(ReviewPanelModels.Default.Roles) { [ReviewRoles.Security] = ["claude-opus-5"] };
        var h = new Harness { Models = ReviewPanelModels.Default with { Roles = models } };
        h.GitHub.Diff = RiskyDiff;

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("set Review:Security:Models", outcome.Error);
        Assert.Empty(h.Reviewer.Requests);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task Each_role_reviews_with_its_own_configured_models()
    {
        var models = new Dictionary<string, IReadOnlyList<string>>(ReviewPanelModels.Default.Roles)
        {
            [ReviewRoles.Security] = ["gemini-3.1-pro-preview"],
            [ReviewRoles.SpecConformance] = ["claude-opus-5", "gpt-5.4-mini"],
        };
        var h = new Harness { Models = ReviewPanelModels.Default with { Roles = models } };
        h.GitHub.Diff = RiskyDiff;

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([(ReviewRoles.Correctness, "gpt-5.5"), (ReviewRoles.SpecConformance, "gpt-5.4-mini"), (ReviewRoles.Security, "gemini-3.1-pro-preview")],
            h.Reviewer.Requests.Select(r => (r.Role, r.Model)));
    }

    /// <summary>
    /// The router as the panel's prompts ask it to answer, for the seeded config-flag fixtures: the spec-conformance prompt
    /// (recognised by its checklist item) reports every configuration key the diff adds that no other added line reads; the
    /// confirm prompt confirms a finding whose key the diff indeed reads only once; the other roles find nothing. Each
    /// answer is served by the pinned model and ends with the JSON line the prompt asks for.
    /// </summary>
    private static FakeApi PromptFollowingRouter()
    {
        var addedKey = new System.Text.RegularExpressions.Regex(@"^\+.*GetValue\(""(?<key>[^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);
        return new FakeApi().On("POST /v1/messages", request =>
        {
            var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
            var system = body.GetProperty("system").GetString()!;
            var user = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var model = body.GetProperty("model").GetString()!;
            var diff = user[user.IndexOf("<diff>", StringComparison.Ordinal)..];
            var unused = addedKey.Matches(diff).Select(m => m.Groups["key"].Value)
                .Select(key => (key, Property: key.Split(':').Last()))
                .Where(k => diff.Split('\n').Count(l => l.StartsWith('+') && l.Contains(k.Property)) == 1)
                .ToList();
            string answer;
            if (system == ReviewPrompts.Confirm.Text)
            {
                var confirmed = unused.Any(k => user.Contains($"Title: config key {k.key} has no consumer"));
                answer = System.Text.Json.JsonSerializer.Serialize(new { confirmed, reason = confirmed ? "declared once, read nowhere" : "it is read" });
            }
            else if (system.Contains("Every new control/flag/config has a consumer"))
            {
                var findings = unused.Select(k => new
                {
                    severity = "blocking", title = $"config key {k.key} has no consumer", file = "src/WordCount/WordCountOptions.cs", line = 13,
                    detail = $"{k.Property} is declared but nothing reads it",
                });
                answer = System.Text.Json.JsonSerializer.Serialize(new { findings, summary = "checked the spec checklist" });
            }
            else
            {
                answer = """{"findings": [], "summary": "nothing found"}""";
            }
            return FakeApi.Json(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
            {
                model, stop_reason = "end_turn", content = new[] { new { type = "text", text = $"Reviewed.\n{answer}" } },
            }));
        });
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "review", name));

    [Fact]
    public async Task A_seeded_pr_that_adds_an_unused_config_flag_gets_a_confirmed_blocking_finding_and_is_not_merged()
    {
        // The spec-conformance prompt carries the checklist item the fake router keys on.
        Assert.Contains("Every new control/flag/config has a consumer", ReviewPrompts.For(ReviewRoles.SpecConformance).Text);
        var router = PromptFollowingRouter();
        var h = new Harness { Panel = new RouterReviewer(router.Client("http://router.test/"), "rk") };
        h.GitHub.Diff = _ => Fixture("unused-config-flag.diff");

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal(ReviewVerdict.Fail, verdict.Verdict);
        var finding = verdict.Reviews.Single(r => r.Role == ReviewRoles.SpecConformance).Findings.Single();
        Assert.Equal((Finding.Blocking, "config key WordCount:IgnoreBlankInput has no consumer", Confirmation.Confirmed, "gpt-5.4-mini"),
            (finding.Severity, finding.Title, finding.Confirmation!.Outcome, finding.Confirmation.Model));
        Assert.Contains("config key WordCount:IgnoreBlankInput has no consumer", outcome.Error);
        Assert.Empty(h.Merges);
        // Every call went through the router pinned with the role's model and that role's prompt as the system prompt.
        Assert.Equal(["gpt-5.5", "gpt-5.5", "gpt-5.4-mini"], router.Requests.Select(r => r.Headers[RouterReviewer.ForceModelHeader]));
    }

    [Fact]
    public async Task The_same_seeded_flag_with_a_consumer_passes_the_panel_and_merges()
    {
        var router = PromptFollowingRouter();
        var h = new Harness { Panel = new RouterReviewer(router.Client("http://router.test/"), "rk") };
        h.GitHub.Diff = _ => Fixture("consumed-config-flag.diff");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.All((await h.Verdicts()).Single().Reviews, r => Assert.Empty(r.Findings));
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
