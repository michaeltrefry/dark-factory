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
    internal const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    internal const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string ShaC = "cccccccccccccccccccccccccccccccccccccccc";
    internal const string ShaD = "dddddddddddddddddddddddddddddddddddddddd";
    private const string MergeCommit = "9999999999999999999999999999999999999999";
    internal const string ImplementerModel = "claude-sonnet-4-5-20250929";

    /// <summary>The model the fake router says served a panel call (any model: only the served class counts).</summary>
    internal const string PanelModel = "gpt-6-astra";
    private static readonly string Policy = TestPolicies.Standard();

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
        public const string BaseSha = "base0";
        /// <summary>GitHub's changed_files for the PR; by default the file count of <see cref="Diff"/> at the head.</summary>
        public int? ChangedFiles { get; set; }
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
            return Task.FromResult(new PullFacts(number, PrUrl, Open && !Merged, Merged, false, Head, "main", BaseSha, MergeCommitSha,
                ChangedFiles ?? DiffPaths.Parse(Diff(Head)).Files));
        }

        /// <summary>The PR's diff for a head commit; by default a one-line change to an unrisky file.</summary>
        public Func<string, string> Diff { get; set; } = head => $"diff --git a/src/x.cs b/src/x.cs\n+change at {head}\n";

        public RepoFiles Files { get; set; } = new(["README.md", "src/x.cs"], false);

        /// <summary>The diff between two commits neither of which is the PR's base (e.g. a CI fix's own diff); by default <see cref="Diff"/> of the head.</summary>
        public Func<string, string, string>? DiffBetween { get; set; }

        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
        {
            Calls.Add($"diff {baseSha}...{headSha}");
            return Task.FromResult(baseSha != BaseSha && DiffBetween is { } between ? between(baseSha, headSha) : Diff(headSha));
        }

        /// <summary>Each failing check's log, by check name (a default one when not set); <see cref="LogThrows"/> makes the read fail.</summary>
        public Dictionary<string, string> Logs { get; } = new();
        public Exception? LogThrows { get; set; }

        public Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct)
        {
            Calls.Add($"log {check.Name} {check.Id}");
            return LogThrows is { } failure ? Task.FromException<string>(failure)
                : Task.FromResult(Logs.TryGetValue(check.Name, out var log) ? log : $"##[error]{check.Name} failed");
        }

        public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Calls.Add($"files {sha}");
            return Task.FromResult(Files);
        }

        public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct)
        {
            // The policy is read at the base commit the diff is read against, never at the moving base branch.
            Assert.Equal(BaseSha, baseRef);
            Calls.Add($"policy {baseRef}");
            return Task.FromResult(PolicyText);
        }

        /// <summary>Runs on each CI read, with the commit, before it is answered (e.g. to let a check finish, or to crash).</summary>
        public Action<string>? OnCiRead { get; set; }

        public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Calls.Add($"ci {sha}");
            OnCiRead?.Invoke(sha);
            return Task.FromResult(Ci.TryGetValue(sha, out var facts) ? facts : Green(sha));
        }

        /// <summary>How a head compares with the base branch (sc-25384); by default it is up to date with <see cref="BaseSha"/>.</summary>
        public Func<string, BaseComparison> Compare { get; set; } = _ => new BaseComparison(BaseSha, 0);

        public Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct)
        {
            Calls.Add($"compare {baseRef}...{headSha}");
            return Task.FromResult(Compare(headSha));
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
    /// The panel's calls, answered as <see cref="RouterReviewer"/> would (served by <see cref="PanelModel"/> on the high class, under the request's
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
        /// <summary>The model the router says served a role review.</summary>
        public Func<ReviewRequest, string?> Served { get; init; } = _ => PanelModel;
        /// <summary>The model class the router says served a role review (<c>X-Weave-Model-Class</c>; null: no header).</summary>
        public Func<ReviewRequest, string?> ServedClass { get; init; } = _ => ReviewModels.Class;
        /// <summary>The model class the router says served a second opinion.</summary>
        public Func<ConfirmRequest, string?> ConfirmClass { get; init; } = _ => ReviewModels.Class;

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
            return Task.FromResult(new RoleReview(request.Role, Served(request), ServedClass(request), request.Session,
                request.Prompt.Id, Findings(request), "reviewed"));
        }

        public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
        {
            Confirms.Add(request);
            return Task.FromResult(new Confirmation(Confirm(request), PanelModel, ConfirmClass(request), request.Session,
                request.Prompt.Id, "checked against the diff"));
        }
    }

    /// <summary>A worker that reports the models that answered it, then succeeds.</summary>
    internal static Func<WorkerCall, Task<WorkerResult>> ReportsModel(params string?[] models) => async call =>
    {
        await call.OnSession(Ok.SessionId!, CancellationToken.None);
        foreach (var model in models.OfType<string>())
        {
            await call.Callbacks.OnModel!(model, CancellationToken.None);
        }
        return Ok;
    };

    internal sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private int _pushes;

        public Harness()
        {
            Db = new LedgerDbContext(_options);
            // The implementer's push opens the PR at Sha1; each later push (a fix round's) moves the PR's head to the next
            // of FixHeads (null: the fixer changed nothing, the head stays).
            Workspaces.OnPush = () =>
            {
                if (++_pushes > 1 && FixHeads[_pushes - 2] is { } head)
                {
                    GitHub.Head = head;
                }
                return Task.CompletedTask;
            };
            Workspaces.Head = () => GitHub.Head;
        }

        /// <summary>The commits fix rounds 1, 2, 3, … push (see the constructor).</summary>
        public List<string?> FixHeads { get; init; } = [ShaA, ShaB, ShaC, ShaD];
        /// <summary>The models that answer a fixer worker (by default the implementer's).</summary>
        public string?[] FixerModels { get; init; } = [ImplementerModel];
        /// <summary>Every worker session started, over all runs: the implementer's first, then each fix round's.</summary>
        public List<WorkerCall> WorkerCalls { get; } = [];
        /// <summary>When set, the worker call with this index (0 = the implementer) runs this instead.</summary>
        public Dictionary<int, Func<WorkerCall, Task<WorkerResult>>> WorkerOverrides { get; } = [];

        public LedgerDbContext Db { get; }
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));
        /// <summary>The shared control table (needed for the usage pause), on the injected clock.</summary>
        public IControls Controls => new LedgerControls(new LedgerDbContextFactory(_options), Time);
        public FakeWorkSource Stories { get; } = new(Story);
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; } = new();
        public InProcessRunLocks Locks { get; } = new();
        public FakeGateGitHub GitHub { get; } = new();
        /// <summary>The new-tests check's runs (sc-25382); by default the PR adds one test that fails on the base and passes on the head.</summary>
        public FakeTestRunner TestRunner { get; } = new();
        public FakeReviewer Reviewer { get; init; } = new();
        /// <summary>When set, the panel's calls go here instead of <see cref="Reviewer"/> (e.g. a real <see cref="RouterReviewer"/>).</summary>
        public IReviewer? Panel { get; init; }
        /// <summary>When set, the gate's waits run on <see cref="Time"/> (which only moves when the test advances it).</summary>
        public bool GateOnFakeClock { get; init; }
        /// <summary>How often a run's controls are polled while a worker or the gate's test runs execute (the pipeline's default when null).</summary>
        public TimeSpan? ControlPoll { get; init; }
        public WorkLedger Ledger => new(Db, TimeProvider.System);
        /// <summary>The ledger's contexts (for a freeze evaluator, or to seed other items beside the run's).</summary>
        public LedgerDbContextFactory Contexts => new(_options);
        /// <summary>When set, the run's freeze evaluator (sc-25387) checks these thresholds, on the system clock and <see cref="GitHub"/>.</summary>
        public FreezeOptions? Freeze { get; init; }
        /// <summary>When set, the run reads and writes its controls through this instead of <see cref="Controls"/> (e.g. reads that fail).</summary>
        public IControls? RunControls { get; set; }
        /// <summary><c>Controls:MaxReadFailures</c> for the run (the pipeline's default when null).</summary>
        public int? MaxControlReadFailures { get; init; }

        public Task<RunOutcome> Run(string? implementerModel = ImplementerModel, CancellationToken ct = default) =>
            Run([implementerModel], ct);

        public Task<RunOutcome> Run(string?[] implementerModels, CancellationToken ct = default) =>
            new RunPipeline(Stories, Ledger, Locks, Workspaces, new HarnessWorker(this, implementerModels), Prs, Sandbox, TextWriter.Null,
                    controls: RunControls ?? Controls,
                    controlPollInterval: ControlPoll,
                    gate: new GateStage(GitHub, Panel ?? Reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), GateOnFakeClock ? Time : null,
                        TestRunner),
                    freeze: Freeze is null ? null : new FactoryFreeze(Contexts, Controls, Freeze, TimeProvider.System, GitHub),
                    maxControlReadFailures: MaxControlReadFailures)
                .RunAsync(77, ct);

        /// <summary>The implementer reports <c>implementerModels</c>; every later session (a fixer) reports <see cref="FixerModels"/>.</summary>
        private sealed class HarnessWorker(Harness h, string?[] implementerModels) : IWorker
        {
            public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks,
                CancellationToken ct)
            {
                var call = new WorkerCall(prompt, resumeSessionId, callbacks!);
                var index = h.WorkerCalls.Count;
                h.WorkerCalls.Add(call);
                h.PauseFlag = false; // as ClaudeWorker deletes a stale flag when a run starts
                await callbacks!.OnStarted!(WorkerPid, CancellationToken.None);
                return await (h.WorkerOverrides.TryGetValue(index, out var behaviour) ? behaviour
                    : ReportsModel(index == 0 ? implementerModels : h.FixerModels))(call);
            }

            public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);

            public void RequestPause(string workingDirectory) => h.PauseFlag = true;

            public void CancelPause(string workingDirectory) => h.PauseFlag = false;
        }

        /// <summary>The running worker's pause flag (<see cref="IWorker.RequestPause"/>: a Pause, or the stuck detector's interrupt).</summary>
        public volatile bool PauseFlag;

        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        public async Task<List<WorkState>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => r.State).ToList();
        public async Task<List<ReviewVerdict>> Verdicts() =>
            (await Rows()).Where(r => r.Step == RunPipeline.Steps.Verdict).Select(r => ReviewVerdict.FromDetail(r.Detail)!).ToList();
        public List<string> Merges => GitHub.Calls.Where(c => c.StartsWith("merge ")).ToList();
    }

    [Fact]
    public async Task Green_ci_and_a_high_class_pass_is_merged_by_the_gate_and_the_ledger_records_the_merge_commit()
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
        // ran on the high class, bound to the head commit, with the base file list.
        Assert.Equal(ImplementerModel, rows.Single(r => r.Step == RunPipeline.Steps.ImplementerModel).Detail);
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance], h.Reviewer.Requests.Select(r => r.Role));
        Assert.All(h.Reviewer.Requests, review =>
        {
            Assert.Equal(Sha1, review.Pull.HeadSha);
            Assert.Contains($"change at {Sha1}", review.Diff);
            Assert.Equal(h.GitHub.Files, review.Files);
        });
        Assert.Contains("files base0", h.GitHub.Calls);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal((Sha1, ReviewVerdict.Pass), (verdict.HeadSha, verdict.Verdict));
        Assert.Empty(verdict.RiskyPaths);
        Assert.All(verdict.Reviews, r => Assert.Equal((PanelModel, "high"), (r.ServedModel, r.ServedClass)));

        // The gate read its policy at the base commit (the one its diff is against), and merged exactly the reviewed, green commit, once.
        Assert.Contains("policy base0", h.GitHub.Calls);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        Assert.StartsWith("Merge ", rows.Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
        Assert.Equal(Sha1, rows.Single(r => r.Step == RunPipeline.Steps.GatePassed).Detail);
        // What the merge changed is on record before the merge (the freeze's hot-file and main-red triggers read it, sc-25387).
        var mergeFiles = rows.FindIndex(r => r.Step == RunPipeline.Steps.MergeFiles);
        Assert.True(mergeFiles >= 0 && mergeFiles < rows.FindIndex(r => r.Step is null && r.State == WorkState.Merge));
        var recorded = Controls.MergeFiles.FromDetail(rows[mergeFiles].Detail);
        Assert.Equal("main", recorded!.Base);
        Assert.Equal(["src/x.cs"], recorded.Files);
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
    [InlineData("version: 1\nrequire:\n  ci: green\n  review: pass\n", "version 1, which this gate no longer accepts")]
    [InlineData("version: 2\ntiers: {}\nrisk: {}\n", "tiers is missing 'sealed'")]
    [InlineData("version: [", "not valid YAML")]
    public async Task A_missing_or_invalid_policy_means_no_merge_and_an_escalation(string? policy, string reason)
    {
        var h = new Harness();
        h.GitHub.PolicyText = policy;

        var outcome = await h.Run();

        // The review needs the policy to choose its panel, so it escalates before spending a review.
        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Empty(h.Reviewer.Requests);
        Assert.Equal([WorkState.Review, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
        Assert.Contains(h.Stories.Comments, c => c.Contains("escalated") && c.Contains(reason));
    }

    [Theory]
    [InlineData(null, "does not exist")]
    [InlineData("version: 2", "tiers")]
    public async Task A_policy_that_breaks_after_the_review_still_blocks_the_merge_and_escalates(string? policy, string reason)
    {
        var h = new Harness();
        h.GitHub.OnPullRead = read =>
        {
            if (read == 3) // the MergeGate read: the policy on the base was deleted or corrupted after the review
            {
                h.GitHub.PolicyText = policy;
            }
        };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains(reason, outcome.Error);
        Assert.Empty(h.Merges);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
    }

    // ---- sc-25381: path tiers and the risk threshold, from the diff at the head ----

    [Fact]
    public async Task A_change_touching_a_sealed_path_is_reviewed_but_escalated_by_the_gate_not_merged()
    {
        var h = new Harness();
        // The PR's own diff of a free file renames the policy away: both sides count.
        h.GitHub.Diff = head => $"diff --git a/factory/gate.yaml b/docs/old-gate.yaml\nrename from factory/gate.yaml\nrename to docs/old-gate.yaml\n"
            + TestPolicies.Diff("docs/guide.md", marker: head);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("touches sealed path(s), which always escalate: factory/gate.yaml (sealed)", outcome.Error);
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role)); // the sealed tier requires the security review
        Assert.Equal(["factory/gate.yaml (sealed)"], (await h.Verdicts()).Single().RiskyPaths);
        Assert.Empty(h.Merges);
        Assert.Equal([WorkState.MergeGate, WorkState.Escalated], (await h.Transitions()).TakeLast(2));
        Assert.StartsWith("Blocked ", (await h.Rows()).Single(r => r.Step == RunPipeline.Steps.GateDecision).Detail);
        Assert.Equal(2, h.GitHub.Calls.Count(c => c == $"diff base0...{Sha1}")); // the gate read the head's diff itself
    }

    [Fact]
    public async Task A_change_touching_a_protected_path_passes_every_check_then_escalates_instead_of_merging()
    {
        var h = new Harness();
        h.GitHub.Diff = head => TestPolicies.Diff("src/auth/Session.cs", marker: head);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("touches protected path(s), merged only after escalation: src/auth/Session.cs (protected)", outcome.Error);
        Assert.Equal(ReviewRoles.All, (await h.Verdicts()).Single().Reviews.Select(r => r.Role));
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_free_change_over_the_size_limit_merges_and_a_normal_one_escalates()
    {
        var free = new Harness();
        free.GitHub.Diff = head => TestPolicies.Diff("docs/guide.md", lines: 500, marker: head);
        Assert.True((await free.Run()).Succeeded);
        Assert.Equal([$"merge 1 {Sha1}"], free.Merges);

        var normal = new Harness();
        normal.GitHub.Diff = head => TestPolicies.Diff("src/x.cs", lines: 500, marker: head);
        var outcome = await normal.Run();
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("risk threshold: 500 changed lines exceed max_changed_lines 400", outcome.Error);
        Assert.Empty(normal.Merges);
    }

    [Fact]
    public async Task Failing_ci_on_the_head_that_is_not_the_prs_escalates_without_merging()
    {
        // sc-25383: a red check the PR could fix goes to a CI fixer (CiHealTests); one also red on the base escalates.
        var h = new Harness();
        h.GitHub.Ci[Sha1] = new CiFacts(Sha1, [new CheckFact("build-test", true, "failure")]);
        h.GitHub.Ci[FakeGateGitHub.BaseSha] = new CiFacts(FakeGateGitHub.BaseSha, [new CheckFact("build-test", true, "failure")]);

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
                TextWriter.Null, gate: new GateStage(h.GitHub, h.Reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)))
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

    [Theory]
    [InlineData("claude-sonnet-4-5-20250929")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("claude-opus-5-5,gpt-5.6-luna")]
    [InlineData(PanelModel)] // the model that wrote the code reviews it, and gives the second opinion on its own finding
    [InlineData("")] // the implementer reported no model
    public async Task Whatever_answered_the_implementer_every_panel_call_is_on_the_high_class_and_the_item_merges(string implementer)
    {
        var h = new Harness { Reviewer = new FakeReviewer { Findings = FakeReviewer.Blocking().Findings, Confirm = _ => Confirmation.NotConfirmed } };

        var models = implementer.Length == 0 ? new string?[] { null } : implementer.Split(',');
        var outcome = await h.Run(models);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance], h.Reviewer.Requests.Select(r => r.Role));
        Assert.Single(h.Reviewer.Confirms);
        // Served by one model: the review, its second opinion (which downgraded the finding) and, in one case, the implementer.
        var review = (await h.Verdicts()).Single().Reviews.Single(r => r.Role == ReviewRoles.Correctness);
        Assert.Equal((PanelModel, PanelModel), (review.ServedModel, review.Findings.Single().Confirmation!.ServedModel));
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Theory]
    [InlineData("mid", null)]
    [InlineData(null, null)] // no X-Weave-Model-Class header: fail closed (E2)
    [InlineData("high", "low")]
    [InlineData("high", null)]
    public async Task A_review_or_second_opinion_not_served_on_the_high_class_never_merges(string? reviewClass, string? confirmClass)
    {
        var h = new Harness
        {
            Reviewer = new FakeReviewer
            {
                Findings = FakeReviewer.Blocking().Findings, Confirm = _ => Confirmation.NotConfirmed,
                ServedClass = _ => reviewClass, ConfirmClass = _ => confirmClass,
            },
        };

        var outcome = await h.Run();

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains(reviewClass == "high" ? "a second opinion on a correctness finding" : "the correctness review", outcome.Error);
        // Re-reviewed once (a verdict breaking the rule may be an earlier rule's), never in a loop.
        Assert.Equal(4, h.Reviewer.Requests.Count);
    }

    /// <summary>
    /// An item an earlier factory left in <paramref name="state"/> with a passing verdict on Sha1 by the pinned
    /// <paramref name="oldReviewer"/>, recorded before sc-25626 (no served class). The PR fake errors on its 20th read, so a
    /// re-review loop escalates instead of hanging.
    /// </summary>
    private static async Task<Harness> SeededWithAnOldRuleVerdict(WorkState state, string oldReviewer, FakeReviewer? reviewer = null)
    {
        var h = new Harness { Reviewer = reviewer ?? new FakeReviewer() };
        h.GitHub.OnPullRead = read =>
        {
            if (read >= 20)
            {
                throw new InvalidOperationException("the gate kept re-reviewing the same head");
            }
        };
        var ledger = h.Ledger;
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Review, null, PrUrl, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.ImplementerModel, null, ImplementerModel, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Verdict, null,
            ReviewPanel.Decide(Sha1, [], ReviewRoles.Required(false)
                .Select(r => new RoleReview(r, oldReviewer, null, null, null, [], "ok")).ToList()).ToDetail(), CancellationToken.None);
        if (state == WorkState.CI)
        {
            await ledger.RecordAsync(item, WorkState.CI, null, Sha1, CancellationToken.None);
        }
        return h;
    }

    [Theory]
    [InlineData(WorkState.CI, "gpt-5.5")]
    [InlineData(WorkState.CI, "claude-opus-5-5")]
    [InlineData(WorkState.Review, "claude-opus-5-5")]
    public async Task A_head_passed_under_the_pinned_reviewer_rule_is_reviewed_once_on_the_high_class_and_merges(WorkState state, string oldReviewer)
    {
        var h = await SeededWithAnOldRuleVerdict(state, oldReviewer);

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance], h.Reviewer.Requests.Select(r => r.Role));
        Assert.All(h.Reviewer.Requests, r => Assert.Equal(Sha1, r.Pull.HeadSha));
        Assert.Equal([(oldReviewer, null), (PanelModel, "high")], (await h.Verdicts()).Select(v => (v.Reviews[0].ServedModel, v.Reviews[0].ServedClass)));
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        // At CI the gate sends it back to review; at Review the stale verdict is not reused.
        var expected = state == WorkState.CI
            ? new[] { WorkState.MergeGate, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch }
            : [WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch];
        Assert.Equal(expected, (await h.Transitions()).TakeLast(expected.Length));
    }

    [Fact]
    public async Task A_re_review_whose_calls_still_break_the_rule_escalates_instead_of_reviewing_again()
    {
        // The router answers without a class header: the current panel's own verdict still breaks the rule.
        var h = await SeededWithAnOldRuleVerdict(WorkState.CI, "gpt-5.5", new FakeReviewer { ServedClass = _ => null });

        var outcome = await h.Run();

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(2, h.Reviewer.Requests.Count); // one re-review, not a loop
        Assert.Contains("the router did not say which model class served it", outcome.Error);
        Assert.Empty(h.Merges);
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
                .Select(r => new RoleReview(r, "claude-opus-5-5", "high", null, null, [], "ok")).ToList()).ToDetail(), CancellationToken.None);
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
        Assert.Equal(h.Reviewer.Requests.Select(r => $"{r.Session} high {Sha1} {r.Role} factory/prompts/{r.Role}.md@sha256:{r.Prompt.Sha256}"), named);
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

    // ---- sc-25379: the review panel ----

    /// <summary>A change to a path whose tier (protected, in the test policy) requires the security review.</summary>
    internal static string RiskyDiff(string head) =>
        $"diff --git a/src/auth/Login.cs b/src/auth/Login.cs\n--- a/src/auth/Login.cs\n+++ b/src/auth/Login.cs\n@@ -1 +1 @@\n+  // change at {head}\n";

    [Fact]
    public async Task A_diff_touching_a_path_whose_tier_requires_it_adds_the_security_review_to_the_panel()
    {
        var h = new Harness();
        h.GitHub.Diff = RiskyDiff;

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State); // protected: reviewed, then escalated rather than merged
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role));
        Assert.Equal("factory/prompts/security.md", h.Reviewer.Requests.Single(r => r.Role == ReviewRoles.Security).Prompt.Path);
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal(["src/auth/Login.cs (protected)"], verdict.RiskyPaths);
        Assert.Equal(ReviewRoles.All, verdict.Reviews.Select(r => r.Role));
    }

    [Fact]
    public async Task A_normal_tier_that_requires_the_security_review_gets_it_and_merges()
    {
        var h = new Harness();
        h.GitHub.PolicyText = TestPolicies.Standard(normalChecks: "ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role));
        Assert.Equal(["src/x.cs (normal)"], (await h.Verdicts()).Single().RiskyPaths);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task A_normal_path_on_the_code_floor_gets_the_security_review_and_merges()
    {
        var h = new Harness();
        h.GitHub.Diff = head => TestPolicies.Diff("scripts/deploy.sh", marker: $"change at {head}");

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role));
        Assert.Equal(["scripts/deploy.sh (scripts)"], (await h.Verdicts()).Single().RiskyPaths);
        Assert.Contains($"policy {FakeGateGitHub.BaseSha}", h.GitHub.Calls);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
    }

    [Fact]
    public async Task A_diff_missing_files_the_pr_changed_escalates()
    {
        var h = new Harness();
        h.GitHub.ChangedFiles = 2; // the PR changed two files; the diff GitHub returned holds one

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_non_empty_diff_whose_paths_cannot_be_read_counts_as_sealed_gets_the_security_review_and_escalates()
    {
        var h = new Harness();
        h.GitHub.Diff = head => $"@@ -1 +1 @@\n-old\n+change at {head}\n"; // no diff --git, ---/+++ or rename header

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(ReviewRoles.All, h.Reviewer.Requests.Select(r => r.Role));
        var verdict = (await h.Verdicts()).Single();
        Assert.Equal(["(unparsed diff) (sealed: no file header could be read)"], verdict.RiskyPaths);
        Assert.Equal(ReviewRoles.All, verdict.Reviews.Select(r => r.Role));
        Assert.Empty(h.Merges);
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
        // The second opinion is its own call (its own session) on the finding.
        var confirm = h.Reviewer.Confirms.Single();
        Assert.Equal((ReviewRoles.Correctness, "WordCount(\"\") still returns 1"), (confirm.Role, confirm.Finding.Title));
        Assert.DoesNotContain(confirm.Session, h.Reviewer.Requests.Select(r => r.Session));
        Assert.Equal("factory/prompts/confirm.md", confirm.Prompt.Path);
        var finding = (await h.Verdicts()).Single().Reviews.Single(r => r.Role == ReviewRoles.Correctness).Findings.Single();
        Assert.Equal((Finding.Optional, true, Confirmation.NotConfirmed), (finding.Severity, finding.Downgraded, finding.Confirmation!.Outcome));
        // The confirmation call's session is named before it, with the confirm prompt's hash.
        Assert.Contains((await h.Rows()).Where(r => r.Step == RunPipeline.Steps.ReviewSession).Select(r => r.Detail),
            d => d == $"{confirm.Session} high {Sha1} confirm-correctness {ReviewPrompts.Confirm.Id}");
    }

    [Fact]
    public async Task A_confirmed_blocking_finding_fails_the_review_and_escalates_without_merging()
    {
        // The finding survives every fix round (sc-25380), so the item escalates once the rounds are used up.
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains($"[correctness] the tests do not cover the empty string (src/x.cs:1), confirmed by {PanelModel}", outcome.Error);
        Assert.Contains($"blocking correctness finding by {PanelModel}, confirmed by {PanelModel}", (await h.Verdicts()).First().Summary);
        var finding = (await h.Verdicts()).First().Reviews.Single(r => r.Role == ReviewRoles.Correctness).Findings.Single();
        Assert.Equal((Finding.Blocking, false, Confirmation.Confirmed), (finding.Severity, finding.Downgraded, finding.Confirmation!.Outcome));
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_blocking_finding_whose_second_model_answer_is_unusable_stays_blocking()
    {
        var h = new Harness { Reviewer = new FakeReviewer { Findings = FakeReviewer.Blocking().Findings, Confirm = _ => Confirmation.Unusable } };

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Contains("the second opinion was unusable", outcome.Error);
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

    /// <summary>
    /// The router as the panel's prompts ask it to answer, for the seeded config-flag fixtures: the spec-conformance prompt
    /// (recognised by its checklist item) reports every configuration key the diff adds that no other added line reads; the
    /// confirm prompt confirms a finding whose key the diff indeed reads only once; the other roles find nothing. Every
    /// answer is served by one model on the high class (so a model confirms its own finding) and ends with the JSON line the
    /// prompt asks for.
    /// </summary>
    private static FakeApi PromptFollowingRouter()
    {
        var addedKey = new System.Text.RegularExpressions.Regex(@"^\+.*GetValue\(""(?<key>[^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);
        return new FakeApi().On("POST /v1/messages", request =>
        {
            var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
            var system = body.GetProperty("system").GetString()!;
            var user = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
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
            return SseAnswers.Response(SseAnswers.Answer($"Reviewed.\n{answer}", "claude-opus-5-5"));
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

        // The fixer leaves the flag unread (the diff stays the same), so every fix round fails and the item escalates.
        Assert.Equal(WorkState.Escalated, outcome.State);
        // The escalated item's outcome still names its PR, the one an operator has to look at.
        Assert.Equal(PrUrl, outcome.PullRequestUrl);
        var verdict = (await h.Verdicts()).First();
        Assert.Equal(ReviewVerdict.Fail, verdict.Verdict);
        var finding = verdict.Reviews.Single(r => r.Role == ReviewRoles.SpecConformance).Findings.Single();
        Assert.Equal((Finding.Blocking, "config key WordCount:IgnoreBlankInput has no consumer", Confirmation.Confirmed, "claude-opus-5-5", "high"),
            (finding.Severity, finding.Title, finding.Confirmation!.Outcome, finding.Confirmation.ServedModel, finding.Confirmation.ServedClass));
        Assert.Contains("config key WordCount:IgnoreBlankInput has no consumer", outcome.Error);
        Assert.Empty(h.Merges);
        // Every call went through the router on the high class (never a pinned model), each in its own session, with that
        // role's prompt as the system prompt; after each fix push only spec conformance (the role with the blocking finding)
        // reviewed again, and its finding was confirmed.
        Assert.Equal(3 + 2 * Lifecycle.MaxFixRounds, router.Requests.Count);
        Assert.All(router.Requests, r =>
        {
            Assert.Equal("high", r.Headers["x-weave-model-class"]);
            Assert.DoesNotContain(r.Headers.Keys, k => k.Contains("force-model", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(router.Requests.Count, router.Requests.Select(r => r.Headers[RouterReviewer.SessionHeader]).Distinct().Count());
    }

    [Fact]
    public async Task A_review_call_carries_no_worker_session_id_or_transcript_content_and_runs_in_its_own_session()
    {
        // E8: each review and second opinion is its own router session that sees only the story and the PR. The implementer's
        // session (its Claude session id, its transcript) is in the ledger; none of it may reach a panel call.
        const string marker = "TRANSCRIPT-MARKER-4b9e2f7a";
        const string workerSession = "worker-claude-session-8c31d0e5";
        var api = new FakeApi().On("POST /v1/messages", request =>
        {
            var system = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement.GetProperty("system").GetString();
            var answer = system == ReviewPrompts.Confirm.Text
                ? """{"confirmed": false, "reason": "not reproducible"}"""
                : system == ReviewPrompts.For(ReviewRoles.Correctness).Text
                    ? """{"findings": [{"severity": "blocking", "title": "t", "file": "src/x.cs", "line": 1, "detail": "d"}], "summary": "s"}"""
                    : """{"findings": [], "summary": "s"}""";
            return SseAnswers.Response(SseAnswers.Answer($"Reviewed.\n{answer}", "claude-opus-5-5"));
        });
        var h = new Harness { Panel = new RouterReviewer(api.Client("http://router.test/"), "rk") };
        h.WorkerOverrides[0] = async call =>
        {
            await using (var db = h.Contexts.CreateDbContext())
            {
                var item = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-77");
                var session = new WorkerSession { WorkItemId = item.Id, ClaudeSessionId = workerSession, StartedAt = DateTimeOffset.UtcNow };
                db.WorkerSessions.Add(session);
                await db.SaveChangesAsync();
                db.SessionEvents.Add(new SessionEvent
                {
                    WorkerSessionId = session.Id, WorkItemId = item.Id, Sequence = 1, Type = "assistant", ReceivedAt = DateTimeOffset.UtcNow,
                    Payload = $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{marker}}}"}]}}""",
                });
                await db.SaveChangesAsync();
            }
            await call.OnSession(workerSession, CancellationToken.None);
            await call.Callbacks.OnModel!(PanelModel, CancellationToken.None); // the model that wrote the code may also review it
            return Ok with { SessionId = workerSession };
        };

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains(await h.Rows(), r => r.ClaudeSessionId == workerSession); // the worker's session is in the ledger
        await using (var db = h.Contexts.CreateDbContext())
        {
            Assert.Contains(marker, (await db.SessionEvents.SingleAsync()).Payload); // and so is its transcript
        }
        Assert.Equal(3, api.Requests.Count); // correctness, spec conformance, the second opinion on the correctness finding
        var workerSessions = await h.Db.WorkerSessions.Select(s => s.ClaudeSessionId).ToListAsync();
        Assert.Contains(workerSession, workerSessions);
        foreach (var request in api.Requests)
        {
            var sent = string.Join("\n", request.Headers.Select(kv => $"{kv.Key}: {kv.Value}")) + "\n" + request.PathAndQuery + "\n" + request.Body;
            Assert.DoesNotContain(marker, sent);
            Assert.DoesNotContain(workerSession, sent);
            Assert.DoesNotContain(request.Headers[RouterReviewer.SessionHeader], workerSessions);
        }
        // Each call is a fresh session of its own.
        Assert.Equal(3, api.Requests.Select(r => r.Headers[RouterReviewer.SessionHeader]).Distinct().Count());
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
