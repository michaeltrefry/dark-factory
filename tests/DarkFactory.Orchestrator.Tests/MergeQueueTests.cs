using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25384: the per-repo merge queue, through the pipeline, against one simulated repository (GitHub, its branches and the
/// factory's git worktrees in one fake, so a merge into main is something a later head does or does not contain).
/// </summary>
public class MergeQueueTests
{
    internal const int A = 101;
    internal const int B = 102;
    internal const int C = 103;
    private static string Branch(int id) => StoryId.BranchName(id);
    private static readonly string Policy = TestPolicies.Standard();

    /// <summary>
    /// GitHub, the remote and the factory's worktrees as one repository. Commits are names; <see cref="Contains"/> says how many of
    /// main's commits (oldest first) each contains, so "the head contains the previous merge" is <c>Contains[head] == Main.Count</c>.
    /// </summary>
    internal sealed class SimRepo : IGateGitHub, IRepoWorkspace, IPullRequests
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, string> _local = new();
        private readonly Dictionary<string, int> _merging = new();
        private readonly Dictionary<int, string> _pullBranch = new();
        private readonly HashSet<int> _merged = [];
        private readonly Dictionary<int, string> _mergeCommits = new();
        private readonly HashSet<string> _updating = [];
        private int _n;

        /// <summary>Every time a branch was updated by the queue while another updated branch had not merged yet.</summary>
        public List<string> Overlaps { get; } = [];

        public List<string> Main { get; } = ["base0"];
        public Dictionary<string, string> Branches { get; } = new();
        public Dictionary<string, int> Contains { get; } = new() { ["base0"] = 1 };
        public Dictionary<string, string> Patches { get; } = new();
        public List<string> Calls { get; } = [];
        /// <summary>Each merge into main: the PR's branch, the merged head, how much of main it contained, and main's length then.</summary>
        public List<(string Branch, string Head, int Contained, int MainCount)> Merges { get; } = [];
        /// <summary>A commit's CI, when not green.</summary>
        public Func<string, CiFacts?> CiFor { get; set; } = _ => null;
        public Action<string>? OnCiRead { get; set; }
        /// <summary>Runs as a PR is opened for a branch.</summary>
        public Action<string>? OnOpen { get; set; }
        /// <summary>Branches whose merge with main conflicts (until a fix round resolves it).</summary>
        public HashSet<string> Conflicting { get; } = [];
        /// <summary>Whether merging main into a head changes the PR's own diff (main touched the same lines).</summary>
        public bool UpdateChangesDiff { get; set; }
        public Func<string, IReadOnlyList<string>> MarkersLeft { get; set; } = _ => [];

        public string Tip
        {
            get
            {
                lock (_gate)
                {
                    return Main[^1];
                }
            }
        }

        public string AdvanceMain(string commit)
        {
            lock (_gate)
            {
                Main.Add(commit);
                Contains[commit] = Main.Count;
                return commit;
            }
        }

        public bool HeadContainsMain(string head)
        {
            lock (_gate)
            {
                return Contains[head] == Main.Count;
            }
        }

        public List<string> CallsSnapshot()
        {
            lock (_gate)
            {
                return [.. Calls];
            }
        }

        private void Call(string call)
        {
            lock (_gate)
            {
                Calls.Add(call);
            }
        }

        private static string Url(int number) => $"https://github.com/michaeltrefry/dark-factory-sandbox/pull/{number}";

        private static string Patch(string branch) => TestPolicies.Diff($"docs/{branch.Replace("factory/", "")}.md", marker: branch);

        // ---- IPullRequests ----

        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_pullBranch.FirstOrDefault(p => p.Value == head) is { Value: not null } open)
                {
                    return Task.FromResult(Url(open.Key));
                }
                var number = _pullBranch.Count + 1;
                _pullBranch[number] = head;
                OnOpen?.Invoke(head);
                return Task.FromResult(Url(number));
            }
        }

        public Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct) => Task.CompletedTask;

        // ---- IGateGitHub ----

        public Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"pull {number}");
                var head = Branches[_pullBranch[number]];
                var merged = _merged.Contains(number);
                return Task.FromResult(new PullFacts(number, Url(number), !merged, merged, false, head, "main", Main[^1],
                    _mergeCommits.GetValueOrDefault(number), DiffPaths.Parse(Patches[head]).Files));
            }
        }

        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"diff {baseSha}...{headSha}");
                // Against a main commit: the PR's own diff at that head. Between two of the PR's commits: what main brought in.
                return Task.FromResult(Main.Contains(baseSha) ? Patches[headSha] : $"diff --git a/main.txt b/main.txt\n+brought in by {headSha}\n");
            }
        }

        public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct) => Task.FromResult(new RepoFiles(["README.md"], false));

        /// <summary>The base's <c>factory/gate.yaml</c>.</summary>
        public string PolicyText { get; set; } = Policy;

        public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct) => Task.FromResult<string?>(PolicyText);

        public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Call($"ci {sha}");
            OnCiRead?.Invoke(sha);
            return Task.FromResult(CiFor(sha) ?? new CiFacts(sha, [new CheckFact("build-test", true, "success")]));
        }

        public Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct) => Task.FromResult($"##[error]{check.Name} failed");

        public Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"compare {baseRef}...{headSha}");
                return Task.FromResult(new BaseComparison(Main[^1], Main.Count - Contains[headSha]));
            }
        }

        public Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"merge {number} {headSha}");
                var branch = _pullBranch[number];
                if (Branches[branch] != headSha)
                {
                    return Task.FromResult(new MergeResult(false, null, true, "Head branch was modified."));
                }
                Merges.Add((branch, headSha, Contains[headSha], Main.Count));
                _updating.Remove(branch);
                var commit = AdvanceMain($"merge-{branch}");
                _merged.Add(number);
                _mergeCommits[number] = commit;
                return Task.FromResult(new MergeResult(true, commit, false, "merged"));
            }
        }

        // ---- IRepoWorkspace ----

        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}");

        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            lock (_gate)
            {
                _local[branch] = Main[^1];
                return Task.FromResult(Ws(branch));
            }
        }

        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"restore {branch}");
                _local[branch] = Branches[branch];
                _merging.Remove(branch);
                return Task.FromResult(Ws(branch));
            }
        }

        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult(_local.ContainsKey(branch) ? Ws(branch) : null);
            }
        }

        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct)
        {
            lock (_gate)
            {
                var branch = workspace.Branch;
                var head = $"{branch}-c{++_n}";
                // A commit completing a merge of main contains what was merged; any other contains what its parent did.
                if (_merging.Remove(branch, out var merged))
                {
                    Contains[head] = merged;
                    Conflicting.Remove(branch); // the fixer resolved it
                }
                else
                {
                    Contains[head] = Contains[_local[branch]];
                }
                Patches[head] = Patch(branch);
                _local[branch] = head;
                Branches[branch] = head;
                Calls.Add($"push {branch} {head}");
                return Task.FromResult(true);
            }
        }

        public Task<string> HeadAsync(Workspace workspace, CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult(_local[workspace.Branch]);
            }
        }

        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            lock (_gate)
            {
                _local.Remove(workspace.Branch);
                return Task.CompletedTask;
            }
        }

        public Task<BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            lock (_gate)
            {
                var branch = workspace.Branch;
                Calls.Add($"merge-base {branch}");
                var local = _local[branch];
                if (Contains[local] == Main.Count)
                {
                    return Task.FromResult(new BaseMerge(Main[^1], local, [], UpToDate: true));
                }
                if (Conflicting.Contains(branch))
                {
                    _merging[branch] = Main.Count;
                    return Task.FromResult(new BaseMerge(Main[^1], local, ["src/shared.cs"]));
                }
                var head = $"{branch}-u{++_n}";
                Contains[head] = Main.Count;
                Patches[head] = UpdateChangesDiff ? Patches[local] + $"+{head} reshaped by main\n" : Patches[local];
                _local[branch] = head;
                return Task.FromResult(new BaseMerge(Main[^1], head, []));
            }
        }

        public Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add($"fast-forward {workspace.Branch} {_local[workspace.Branch]}");
                Branches[workspace.Branch] = _local[workspace.Branch];
                // A queue update while another branch's update is not merged yet: two items of the repo in flight at once.
                Overlaps.AddRange(_updating.Where(b => b != workspace.Branch).Select(b => $"{workspace.Branch} updated while {b} was in flight"));
                _updating.Add(workspace.Branch);
                return Task.CompletedTask;
            }
        }

        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(MarkersLeft(sha));
    }

    /// <summary>A reviewer every call of which passes (thread-safe: two runs may review at once), unless <see cref="Blocking"/> says otherwise.</summary>
    internal sealed class SimReviewer : IReviewer
    {
        private readonly object _gate = new();
        public List<ReviewRequest> Requests { get; } = [];
        /// <summary>Whether the review of a head reports a blocking finding (which the second model confirms).</summary>
        public Func<ReviewRequest, bool> Blocking { get; set; } = _ => false;

        public Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
        {
            lock (_gate)
            {
                Requests.Add(request);
            }
            IReadOnlyList<Finding> findings = Blocking(request) && request.Role == ReviewRoles.Correctness
                ? [new Finding(Finding.Blocking, "the merge broke it", "src/x.cs", 1, "broken")]
                : [];
            return Task.FromResult(new RoleReview(request.Role, "claude-opus-5-5", "high", request.Session, request.Prompt.Id, findings, "reviewed"));
        }

        public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct) =>
            Task.FromResult(new Confirmation(Confirmation.Confirmed, "claude-opus-5-5", "high", request.Session, request.Prompt.Id, "checked"));

        public List<ReviewRequest> Snapshot()
        {
            lock (_gate)
            {
                return [.. Requests];
            }
        }
    }

    /// <summary>A worker that succeeds at once, reporting a fresh session and the implementer's model; it records each prompt.</summary>
    internal sealed class SimWorker : IWorker
    {
        private int _n;
        public List<string> Prompts { get; } = [];

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks,
            CancellationToken ct)
        {
            var session = $"sess-{Interlocked.Increment(ref _n)}";
            lock (Prompts)
            {
                Prompts.Add(prompt);
            }
            await callbacks!.OnStarted!(WorkerPid, CancellationToken.None);
            await callbacks.OnSession!(session, CancellationToken.None);
            await callbacks.OnModel!(GatePipelineTests.ImplementerModel, CancellationToken.None);
            return new WorkerResult(session, 0, false, "success", "done", "");
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>The board, serialised: two runs may write to it at once.</summary>
    internal sealed class LockedSource(IWorkSource inner) : IWorkSource
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        private async Task<T> One<T>(Func<Task<T>> call)
        {
            await _gate.WaitAsync();
            try
            {
                return await call();
            }
            finally
            {
                _gate.Release();
            }
        }

        private Task One(Func<Task> call) => One(async () =>
        {
            await call();
            return 0;
        });

        public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => One(() => inner.ListReadyAsync(ct));
        public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => One(() => inner.ClaimAsync(id, ignoreScope, ct));
        public Task<bool> InScopeAsync(int id, CancellationToken ct) => One(() => inner.InScopeAsync(id, ct));
        public Task ValidateScopeAsync(CancellationToken ct) => One(() => inner.ValidateScopeAsync(ct));
        public Task ReleaseAsync(int id, CancellationToken ct) => One(() => inner.ReleaseAsync(id, ct));
        public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) => One(() => inner.ReadSpecAsync(id, ct));
        public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => One(() => inner.ReportStateAsync(id, state, comment, ct));
        public Task CommentAsync(int id, string text, CancellationToken ct) => One(() => inner.CommentAsync(id, text, ct));
        public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => One(() => inner.LinkAsync(id, urls, ct));
        public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
            One(() => inner.CreateChildrenAsync(parentId, children, ct));
    }

    internal sealed class Harness(DbContextOptions<LedgerDbContext>? options = null, IRunLocks? locks = null)
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            options ?? new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public SimRepo Repo { get; } = new();
        public SimReviewer Reviewer { get; } = new();
        public SimWorker Worker { get; } = new();
        public IRunLocks Locks { get; set; } = locks ?? new InProcessRunLocks();
        /// <summary>The board behind <see cref="Stories"/> (every story id is the same story; its epic can be changed between runs).</summary>
        public FakeWorkSource Board { get; } =
            new(new WorkStory(0, "Queue me", "Change one file.", "feature", "https://app.shortcut.com/trefry/story/0"));
        public IWorkSource Stories => LazyInitializer.EnsureInitialized(ref _stories, () => new LockedSource(Board));
        private IWorkSource? _stories;
        /// <summary>The factory's controls (Pause/Continue/Stop), kept in the shared ledger.</summary>
        public IControls Controls => new LedgerControls(new LedgerDbContextFactory(_options), TimeProvider.System);
        private readonly StringWriter _log = new();
        private TextWriter? _writer;
        /// <summary>One synchronised writer over <see cref="_log"/> (its methods lock the writer itself).</summary>
        public TextWriter Log => LazyInitializer.EnsureInitialized(ref _writer, () => TextWriter.Synchronized(_log));
        public TimeSpan CiPoll { get; init; } = TimeSpan.FromMilliseconds(5);

        /// <summary>A pipeline as one process would build it: its own ledger context over the shared ledger.</summary>
        public RunPipeline Pipeline() =>
            new(Stories, new WorkLedger(new LedgerDbContext(_options), TimeProvider.System), Locks, Repo, Worker, Repo, Sandbox, Log,
                controls: Controls, gate: new GateStage(Repo, Reviewer, CiPoll, TimeSpan.FromSeconds(30)));

        public Task<RunOutcome> Run(int story, CancellationToken ct = default) => Pipeline().RunAsync(story, ct);

        public async Task<List<LedgerEntry>> Rows(int story)
        {
            await using var db = new LedgerDbContext(_options);
            var item = await db.WorkItems.SingleAsync(i => i.ExternalId == StoryId.Format(story));
            return await db.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync();
        }

        public async Task<List<WorkState>> Transitions(int story) => (await Rows(story)).Where(r => r.Step is null).Select(r => r.State).ToList();

        public async Task<List<string?>> Steps(int story, string step) => (await Rows(story)).Where(r => r.Step == step).Select(r => r.Detail).ToList();

        public string Logged
        {
            get
            {
                lock (Log)
                {
                    return _log.ToString();
                }
            }
        }
    }

    private static CiFacts Pending(string sha) => new(sha, [new CheckFact("build-test", false, null)]);

    /// <summary>
    /// Runs A until it holds the queue's turn with its head updated to main and CI on that head still running, then B through
    /// its gate approval (it queues behind A). Main moves when A's PR opens, so A's head is behind when it reaches the gate.
    /// </summary>
    internal static async Task<(Task<RunOutcome> RunA, TaskCompletionSource ReleaseCi)> ABehindAWaitingForCi(Harness h, CancellationToken aToken = default,
        Action? beforeB = null)
    {
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(A))
            {
                h.Repo.AdvanceMain("m1"); // something else merged meanwhile
            }
        };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Repo.CiFor = sha => sha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal) && !release.Task.IsCompleted ? Pending(sha) : null;
        h.Repo.OnCiRead = sha =>
        {
            if (sha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal))
            {
                waiting.TrySetResult();
            }
        };
        var runA = Task.Run(() => h.Run(A, aToken));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));

        beforeB?.Invoke();
        var b = await h.Run(B);

        Assert.True(b.Succeeded, b.Error + h.Logged);
        Assert.Equal(WorkState.MergeGate, b.State); // queued behind A: the run ends, the item waits in MergeGate
        Assert.Contains($"{StoryId.Format(B)} is 2 of 2 in the merge queue", h.Logged);
        Assert.Empty(h.Repo.Merges);
        Assert.Single(await h.Steps(B, RunPipeline.Steps.Queued));
        Assert.Empty(await h.Steps(B, RunPipeline.Steps.QueueTurn));
        Assert.Empty(await h.Steps(B, RunPipeline.Steps.BaseUpdate)); // nothing of B's moved while A held the turn
        return (runA, release);
    }

    internal static async Task AssertMergedInOrderEachOnAHeadContainingMain(Harness h)
    {
        Assert.Equal([Branch(A), Branch(B)], h.Repo.Merges.Select(m => m.Branch));
        // Each merged head contained all of main as it was then — B's head includes A's merge commit.
        Assert.All(h.Repo.Merges, m => Assert.Equal(m.MainCount, m.Contained));
        var calls = h.Repo.CallsSnapshot();
        foreach (var (branch, head, _, _) in h.Repo.Merges)
        {
            // CI was read on exactly the merged head, before the merge.
            var merge = calls.FindIndex(c => c.StartsWith("merge ", StringComparison.Ordinal) && c.EndsWith($" {head}", StringComparison.Ordinal));
            Assert.InRange(calls.FindIndex(c => c == $"ci {head}"), 0, merge - 1);
            Assert.StartsWith($"{branch}-u", head);
        }
        // FIFO by gate approval: A was queued first.
        Assert.True((await h.Rows(A)).First(r => r.Step == RunPipeline.Steps.Queued).Id < (await h.Rows(B)).First(r => r.Step == RunPipeline.Steps.Queued).Id);
    }

    [Fact]
    public async Task Two_approved_items_merge_one_at_a_time_each_after_ci_on_a_head_that_contains_the_previous_merge()
    {
        var h = new Harness();
        var (runA, release) = await ABehindAWaitingForCi(h);

        release.SetResult();
        var a = await runA;
        Assert.Equal(WorkState.Watch, a.State);
        var b = await h.Run(B); // the next poll: B takes the turn

        Assert.Equal(WorkState.Watch, b.State);
        await AssertMergedInOrderEachOnAHeadContainingMain(h);
        // B was updated with A's merge, and its verdict carried to the updated head with the proof that its diff is unchanged.
        var update = BaseUpdate.FromDetail((await h.Steps(B, RunPipeline.Steps.BaseUpdate)).Single())!;
        Assert.Equal($"merge-{Branch(A)}", update.Base);
        var carried = (await h.Steps(B, RunPipeline.Steps.ReviewCarried)).Single()!;
        Assert.Contains(update.To!, carried);
        Assert.Contains("reviewed_diff_sha256", carried);
        var verdicts = (await h.Steps(B, RunPipeline.Steps.Verdict)).Select(v => ReviewVerdict.FromDetail(v)!).ToList();
        Assert.Equal(update.To, verdicts[^1].HeadSha);
        Assert.All(verdicts[^1].Reviews, r => Assert.Equal(update.From, r.CarriedFrom));
        Assert.DoesNotContain(h.Reviewer.Snapshot(), r => r.Pull.HeadSha == update.To); // no new review was spent on it
        // Every queue step is a ledger row, in order, inside MergeGate.
        var steps = (await h.Rows(B)).Where(r => r.State == WorkState.MergeGate && r.Step is not null).Select(r => r.Step).ToList();
        Assert.Equal([RunPipeline.Steps.GateDecision, RunPipeline.Steps.Queued, RunPipeline.Steps.QueueTurn, RunPipeline.Steps.BaseUpdate,
            RunPipeline.Steps.ReviewCarried, RunPipeline.Steps.Verdict, RunPipeline.Steps.GateDecision, RunPipeline.Steps.MergeFiles, RunPipeline.Steps.GatePassed], steps);
    }

    [Fact]
    public async Task A_restart_mid_queue_resumes_the_turn_from_the_ledger_without_redoing_the_update()
    {
        var h = new Harness();
        using var interrupt = new CancellationTokenSource();
        var (runA, release) = await ABehindAWaitingForCi(h, interrupt.Token);

        // The factory stops while A waits for CI in its turn (Ctrl-C / shutdown): A is paused, interrupted.
        await interrupt.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runA);
        Assert.Equal(WorkState.Paused, (await h.Transitions(A))[^1]);

        // A new process: its own locks; CI has finished meanwhile. B runs first and still waits: A keeps its turn.
        h.Locks = new InProcessRunLocks();
        release.SetResult();
        var waiting = await h.Run(B);
        Assert.Equal(WorkState.MergeGate, waiting.State);
        Assert.Contains($"waiting for {StoryId.Format(A)}, which holds the queue's turn", h.Logged);
        Assert.Empty(h.Repo.Merges);

        var a = await h.Run(A);
        Assert.Equal(WorkState.Watch, a.State);
        // The update before the interruption was not redone: one merge of main into A, the same head merged.
        Assert.Single(h.Repo.CallsSnapshot(), c => c == $"merge-base {Branch(A)}");
        Assert.Single(await h.Steps(A, RunPipeline.Steps.BaseUpdate));
        Assert.Single(await h.Steps(A, RunPipeline.Steps.QueueTurn));
        var b = await h.Run(B);
        Assert.Equal(WorkState.Watch, b.State);
        await AssertMergedInOrderEachOnAHeadContainingMain(h);
    }

    [Fact]
    public async Task A_branch_that_conflicts_with_main_goes_to_the_fix_loop_instead_of_merging()
    {
        var h = new Harness();
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        h.Repo.Conflicting.Add(Branch(B));
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(B))
            {
                h.Repo.AdvanceMain("m1"); // B's head is behind (and conflicts) when it reaches the gate
            }
        };

        var b = await h.Run(B);

        Assert.True(b.Succeeded, b.Error + h.Logged);
        Assert.Equal(WorkState.Watch, b.State);
        var transitions = await h.Transitions(B);
        Assert.Equal([WorkState.CI, WorkState.MergeGate, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge,
            WorkState.Watch], transitions.TakeLast(8));
        Assert.Equal(1, TransitionContext.From(transitions).FixRounds); // the conflict counted as a fix round
        // The conflict was recorded with its file; the conflicting head was never pushed to or merged.
        var conflict = BaseUpdate.FromDetail((await h.Steps(B, RunPipeline.Steps.MergeConflict)).Single())!;
        Assert.Equal(["src/shared.cs"], conflict.Files);
        Assert.DoesNotContain(h.Repo.CallsSnapshot(), c => c.StartsWith($"fast-forward {Branch(B)}", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Repo.Merges, m => m.Head == conflict.From);
        // The fixer got the conflicted file in a worktree where the orchestrator had merged main; its resolution was reviewed in full.
        Assert.Contains("src/shared.cs", h.Worker.Prompts[^1]);
        Assert.Contains("conflict markers", h.Worker.Prompts[^1]);
        Assert.NotNull((await h.Steps(B, RunPipeline.Steps.BaseMerged)).Single());
        var resolved = h.Repo.Merges[^1].Head;
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance], h.Reviewer.Snapshot().Where(r => r.Pull.HeadSha == resolved).Select(r => r.Role));
        Assert.Equal(h.Repo.Merges[^1].MainCount, h.Repo.Merges[^1].Contained);
    }

    [Fact]
    public async Task The_conflict_fixer_is_told_the_fix_round_cap_in_effect()
    {
        var h = new Harness();
        h.Repo.PolicyText = TestPolicies.Standard(maxFixRounds: 2);
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        h.Repo.Conflicting.Add(Branch(B));
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(B))
            {
                h.Repo.AdvanceMain("m1");
            }
        };

        Assert.Equal(WorkState.Watch, (await h.Run(B)).State);
        Assert.Contains("could not be merged (fix round 1 of 2)", h.Worker.Prompts[^1]);
    }

    [Fact]
    public async Task The_policys_max_fix_rounds_caps_conflict_fix_rounds_too()
    {
        var h = new Harness();
        h.Repo.PolicyText = TestPolicies.Standard(maxFixRounds: 1);
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        h.Repo.Conflicting.Add(Branch(B));
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(B))
            {
                h.Repo.AdvanceMain("m1");
            }
        };
        // B's first head fails review once: its one fix round is used before the conflict is found.
        var blocked = new HashSet<string>();
        h.Reviewer.Blocking = r =>
        {
            lock (blocked)
            {
                var head = r.Pull.HeadSha;
                return head.StartsWith(Branch(B), StringComparison.Ordinal) && (blocked.Contains(head) || (blocked.Count < 1 && blocked.Add(head)));
            }
        };

        var b = await h.Run(B);

        Assert.Equal(WorkState.Escalated, b.State);
        Assert.Equal(1, TransitionContext.From(await h.Transitions(B)).FixRounds);
        Assert.Contains("conflicts with main", b.Error);
        Assert.Contains("after 1 fix rounds (the cap is 1, the policy's max_fix_rounds (the factory's hard cap is 3), one count shared by review, "
            + "CI and conflict fix rounds); a fix round 2 is not allowed", b.Error);
    }

    [Fact]
    public async Task A_conflict_fix_that_leaves_conflict_markers_escalates()
    {
        var h = new Harness();
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        h.Repo.Conflicting.Add(Branch(B));
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(B))
            {
                h.Repo.AdvanceMain("m1");
            }
        };
        h.Repo.MarkersLeft = _ => ["src/shared.cs"];

        var b = await h.Run(B);

        Assert.Equal(WorkState.Escalated, b.State);
        Assert.Contains("conflict markers still in src/shared.cs", b.Error);
        Assert.Single(h.Repo.Merges); // only A's
    }

    [Fact]
    public async Task A_conflict_after_the_fix_round_cap_is_used_escalates_instead_of_a_fix_round()
    {
        var h = new Harness();
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        h.Repo.Conflicting.Add(Branch(B));
        h.Repo.OnOpen = branch =>
        {
            if (branch == Branch(B))
            {
                h.Repo.AdvanceMain("m1");
            }
        };
        // B's first heads fail review until every fix round of the cap is spent; the last fix passes review and CI.
        var blocked = new HashSet<string>();
        h.Reviewer.Blocking = r =>
        {
            lock (blocked)
            {
                var head = r.Pull.HeadSha;
                return head.StartsWith(Branch(B), StringComparison.Ordinal)
                    && (blocked.Contains(head) || (blocked.Count < Lifecycle.MaxFixRounds && blocked.Add(head)));
            }
        };

        var b = await h.Run(B);

        Assert.Equal(WorkState.Escalated, b.State);
        var transitions = await h.Transitions(B);
        Assert.DoesNotContain(transitions.Zip(transitions.Skip(1)), p => p is (WorkState.MergeGate, WorkState.Fixing));
        Assert.Equal(Lifecycle.MaxFixRounds, TransitionContext.From(transitions).FixRounds);
        Assert.Equal(Lifecycle.MaxFixRounds, transitions.Count(t => t == WorkState.Fixing));
        Assert.Contains(WorkState.MergeGate, transitions); // the conflict was found at the gate, after the cap was used in review
        var conflict = BaseUpdate.FromDetail((await h.Steps(B, RunPipeline.Steps.MergeConflict)).Single())!;
        Assert.Equal(["src/shared.cs"], conflict.Files);
        Assert.Contains($"after {Lifecycle.MaxFixRounds} fix rounds (the cap is {Lifecycle.MaxFixRounds}, one count shared by review, CI and conflict fix rounds); "
            + $"a fix round {Lifecycle.MaxFixRounds + 1} is not allowed", b.Error);
        Assert.Contains("conflicts with main", b.Error);
        Assert.Single(h.Repo.Merges); // only A's
    }

    [Fact]
    public async Task When_the_base_moves_again_while_ci_runs_the_head_is_updated_again_and_only_that_head_merges()
    {
        var h = new Harness();
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        string? firstUpdate = null;
        h.Repo.OnCiRead = sha =>
        {
            if (firstUpdate is null && sha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal))
            {
                firstUpdate = sha;
                h.Repo.AdvanceMain("m2"); // lands while CI runs on the first update
            }
        };

        var a = await h.Run(A);

        Assert.Equal(WorkState.Watch, a.State);
        var updates = (await h.Steps(A, RunPipeline.Steps.BaseUpdate)).Select(u => BaseUpdate.FromDetail(u)!).ToList();
        Assert.Equal(["m1", "m2"], updates.Select(u => u.Base));
        Assert.Equal(firstUpdate, updates[0].To);
        Assert.Equal(updates[0].To, updates[1].From); // the second update builds on the first
        var merged = Assert.Single(h.Repo.Merges);
        Assert.Equal(updates[1].To, merged.Head);
        Assert.Equal(merged.MainCount, merged.Contained);
        Assert.DoesNotContain(h.Repo.CallsSnapshot(), c => c == $"merge 1 {firstUpdate}");
        Assert.Contains($"ci {updates[1].To}", h.Repo.CallsSnapshot());
        // The second carry is from the first update's (carried) verdict.
        Assert.Equal(2, (await h.Steps(A, RunPipeline.Steps.ReviewCarried)).Count);
    }

    [Fact]
    public async Task An_update_that_changes_the_prs_diff_is_reviewed_again_not_carried()
    {
        var h = new Harness();
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        h.Repo.UpdateChangesDiff = true;

        var a = await h.Run(A);

        Assert.Equal(WorkState.Watch, a.State);
        var update = BaseUpdate.FromDetail((await h.Steps(A, RunPipeline.Steps.BaseUpdate)).Single())!;
        Assert.Empty(await h.Steps(A, RunPipeline.Steps.ReviewCarried));
        // Main's change touched every file in correctness' and spec conformance's scope: both review the updated head.
        Assert.Equal([ReviewRoles.Correctness, ReviewRoles.SpecConformance],
            h.Reviewer.Snapshot().Where(r => r.Pull.HeadSha == update.To).Select(r => r.Role));
        Assert.Equal(update.To, Assert.Single(h.Repo.Merges).Head);
    }

    [Fact]
    public async Task A_failed_review_of_the_updated_head_goes_to_the_fix_loop()
    {
        var h = new Harness();
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        h.Repo.UpdateChangesDiff = true;
        h.Reviewer.Blocking = r => r.Pull.HeadSha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal);

        var a = await h.Run(A);

        Assert.Equal(WorkState.Watch, a.State);
        Assert.Equal([WorkState.MergeGate, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate,
            WorkState.Merge, WorkState.Watch], (await h.Transitions(A)).TakeLast(8));
        var merged = Assert.Single(h.Repo.Merges);
        Assert.StartsWith($"{Branch(A)}-c", merged.Head); // the fixer's commit, on top of the update
        Assert.Equal(merged.MainCount, merged.Contained);
    }

    [Fact]
    public async Task Red_ci_on_the_updated_head_goes_to_cis_triage_and_heal()
    {
        var h = new Harness();
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        h.Repo.CiFor = sha => sha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal)
            ? new CiFacts(sha, [new CheckFact("build-test", true, "failure", 7)])
            : null;

        var a = await h.Run(A);

        Assert.Equal(WorkState.Watch, a.State);
        var transitions = await h.Transitions(A);
        Assert.Equal([WorkState.MergeGate, WorkState.CI, WorkState.CIHealing, WorkState.CI, WorkState.Review, WorkState.CI, WorkState.MergeGate,
            WorkState.Merge, WorkState.Watch], transitions.TakeLast(9));
        Assert.DoesNotContain(h.Repo.Merges, m => m.Head.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal));
    }

    private static Task Set(Harness h, string scope, ControlState state) => h.Controls.SetAsync(scope, state, "tester", CancellationToken.None);

    private static string Item(int story) => ControlScope.Item(StoryId.Format(story));

    [Fact]
    public async Task An_idle_item_whose_epic_is_paused_is_skipped_and_on_continue_merges_from_its_approval_time_place()
    {
        var h = new Harness();
        const int epic = 7;
        // A holds the turn (CI pending on its updated head); B, in epic 7, is queued behind it.
        var (runA, release) = await ABehindAWaitingForCi(h, beforeB: () => h.Board.Epic = new WorkEpic(epic, "Epic 7", null, "https://app.shortcut.com/trefry/epic/7"));
        h.Board.Epic = null;
        var c = await h.Run(C);
        Assert.Equal(WorkState.MergeGate, c.State);
        Assert.Contains($"{StoryId.Format(C)} is 3 of 3 in the merge queue", h.Logged);

        // B's epic is paused while B waits between runs: no run of B is active, so nothing is written to its ledger.
        await Set(h, ControlScope.Epic(epic), ControlState.Paused);
        release.SetResult();
        Assert.Equal(WorkState.Watch, (await runA).State);
        Assert.Equal(WorkState.MergeGate, (await h.Run(B)).State); // paused: nothing to do

        // C does not wait for B: B is held by its control and idle, so C is the head of the queue now.
        var cTurn = await h.Run(C);
        Assert.Equal(WorkState.Watch, cTurn.State);
        Assert.Equal([Branch(A), Branch(C)], h.Repo.Merges.Select(m => m.Branch));
        Assert.Empty(await h.Steps(B, RunPipeline.Steps.QueueTurn));

        // Continue: B keeps its approval-time place (its queued row is unchanged) and merges on a head containing C's merge.
        var queued = (await h.Rows(B)).Single(r => r.Step == RunPipeline.Steps.Queued).Id;
        await Set(h, ControlScope.Epic(epic), ControlState.Running);
        Assert.Equal(WorkState.Watch, (await h.Run(B)).State);
        Assert.Equal([Branch(A), Branch(C), Branch(B)], h.Repo.Merges.Select(m => m.Branch));
        Assert.All(h.Repo.Merges, m => Assert.Equal(m.MainCount, m.Contained));
        Assert.Equal(queued, (await h.Rows(B)).Single(r => r.Step == RunPipeline.Steps.Queued).Id);
    }

    [Fact]
    public async Task An_interrupted_turn_holder_paused_since_is_skipped_and_on_continue_waits_for_the_newer_turn()
    {
        var h = new Harness();
        using var interrupt = new CancellationTokenSource();
        var (runA, release) = await ABehindAWaitingForCi(h, interrupt.Token);
        await interrupt.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runA);
        h.Locks = new InProcessRunLocks(); // a new process
        await Set(h, Item(A), ControlState.Paused);

        // B takes the turn A still holds in the ledger; CI on its head stays pending, so it is mid-turn.
        release.SetResult();
        var bInCi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Repo.CiFor = sha => sha.StartsWith(Branch(B), StringComparison.Ordinal) && !bRelease.Task.IsCompleted ? Pending(sha) : null;
        h.Repo.OnCiRead = sha =>
        {
            if (sha.StartsWith(Branch(B), StringComparison.Ordinal))
            {
                bInCi.TrySetResult();
            }
        };
        var runB = Task.Run(() => h.Run(B));
        Assert.Same(bInCi.Task, await Task.WhenAny(bInCi.Task, runB).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Single(await h.Steps(B, RunPipeline.Steps.QueueTurn));

        // Continue A while B holds the newer turn: A, ahead of B by approval, does not take its old turn back over B.
        await Set(h, Item(A), ControlState.Running);
        Assert.Equal(WorkState.MergeGate, (await h.Run(A)).State);
        Assert.Contains($"waiting for {StoryId.Format(B)}, which holds the queue's turn", h.Logged);
        Assert.Empty(h.Repo.Merges);

        bRelease.SetResult();
        Assert.Equal(WorkState.Watch, (await runB).State);
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State); // B has left: A holds its own turn again
        Assert.Equal([Branch(B), Branch(A)], h.Repo.Merges.Select(m => m.Branch));
        Assert.All(h.Repo.Merges, m => Assert.Equal(m.MainCount, m.Contained));
        Assert.Single(await h.Steps(A, RunPipeline.Steps.QueueTurn));
        Assert.Empty(h.Repo.Overlaps);
    }

    [Fact]
    public async Task A_turn_holder_whose_run_is_still_active_when_paused_keeps_the_turn_until_its_run_records_the_pause()
    {
        var h = new Harness();
        var (runA, release) = await ABehindAWaitingForCi(h);
        // A's run is inside its CI wait (blocked in a CI read) when its control flips to Paused.
        var inCi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hold = new ManualResetEventSlim();
        h.Repo.OnCiRead = sha =>
        {
            if (sha.StartsWith($"{Branch(A)}-u", StringComparison.Ordinal))
            {
                inCi.TrySetResult();
                hold.Wait(TimeSpan.FromSeconds(30));
            }
        };
        await inCi.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Set(h, Item(A), ControlState.Paused);

        Assert.Equal(WorkState.MergeGate, (await h.Run(B)).State); // A's run holds its lock: B is not let past it
        Assert.Empty(await h.Steps(B, RunPipeline.Steps.QueueTurn));
        Assert.Empty(h.Repo.Merges);

        hold.Set();
        var a = await runA;
        Assert.Equal(WorkState.Paused, a.State);
        Assert.Equal(RunPipeline.UserPaused, (await h.Rows(A)).Last(r => r.Step is null).Detail);
        Assert.Equal(WorkState.Watch, (await h.Run(B)).State); // A's pause is recorded and A is idle: B takes the turn
        await Set(h, Item(A), ControlState.Running);
        release.SetResult();
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        Assert.Equal([Branch(B), Branch(A)], h.Repo.Merges.Select(m => m.Branch));
        Assert.All(h.Repo.Merges, m => Assert.Equal(m.MainCount, m.Contained));
    }

    [Fact]
    public async Task A_factory_pause_merges_nothing_and_continue_resumes_the_approval_order()
    {
        var h = new Harness();
        var (runA, release) = await ABehindAWaitingForCi(h);
        await Set(h, ControlScope.Factory, ControlState.Paused);
        Assert.Equal(WorkState.Paused, (await runA).State); // A's run records the pause, which releases its turn
        release.SetResult();
        Assert.Equal(WorkState.MergeGate, (await h.Run(B)).State);
        Assert.Empty(h.Repo.Merges);
        Assert.Empty(await h.Steps(B, RunPipeline.Steps.QueueTurn));

        await Set(h, ControlScope.Factory, ControlState.Running);
        // B runs first after Continue: A (approved first, its place kept through the pause) is ahead, so B waits.
        Assert.Equal(WorkState.MergeGate, (await h.Run(B)).State);
        Assert.Contains($"waiting for {StoryId.Format(A)}, which is ahead of it", h.Logged);
        Assert.Empty(h.Repo.Merges);
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        Assert.Equal(WorkState.Watch, (await h.Run(B)).State);
        await AssertMergedInOrderEachOnAHeadContainingMain(h);
    }

    [Fact]
    public async Task A_freeze_pauses_the_turn_holder_and_after_the_freezes_continue_the_approval_order_holds()
    {
        var h = new Harness();
        var (runA, release) = await ABehindAWaitingForCi(h);
        await h.Controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
        var a = await runA;
        Assert.Equal(WorkState.Paused, a.State);
        Assert.Equal(RunPipeline.FreezePaused, (await h.Rows(A)).Last(r => r.Step is null).Detail);
        release.SetResult();

        await Set(h, ControlScope.Freeze, ControlState.Running); // the freeze's Continue
        // B runs first after the Continue: A (approved first) keeps its place through the freeze, so B waits for it.
        Assert.Equal(WorkState.MergeGate, (await h.Run(B)).State);
        Assert.Contains($"waiting for {StoryId.Format(A)}, which is ahead of it", h.Logged);
        Assert.Empty(h.Repo.Merges);
        Assert.Equal(WorkState.Watch, (await h.Run(A)).State);
        Assert.Equal(WorkState.Watch, (await h.Run(B)).State);
        await AssertMergedInOrderEachOnAHeadContainingMain(h);
    }

    [Fact]
    public async Task An_idle_turn_holder_being_stopped_does_not_hold_up_the_queue()
    {
        var h = new Harness();
        using var interrupt = new CancellationTokenSource();
        var (runA, release) = await ABehindAWaitingForCi(h, interrupt.Token);
        await interrupt.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runA);
        h.Locks = new InProcessRunLocks();
        release.SetResult();
        await Set(h, Item(A), ControlState.Stopping); // Stop pressed; no run of A has acted on it yet

        Assert.Equal(WorkState.Watch, (await h.Run(B)).State);
        Assert.Equal([Branch(B)], h.Repo.Merges.Select(m => m.Branch));
        Assert.Equal(WorkState.Cancelled, (await h.Run(A)).State);
        Assert.Single(h.Repo.Merges);
    }

    [Fact]
    public async Task Racing_runs_never_have_two_items_of_a_repo_in_flight()
    {
        var h = new Harness();
        await RaceAsync(h);
    }

    /// <summary>
    /// A and B race through the pipeline at once (two processes), main having moved under both. Were both in flight together,
    /// the second merge's head would not contain the first merge. Leftover queued runs are run again, as later polls would.
    /// </summary>
    internal static async Task RaceAsync(Harness h)
    {
        h.Repo.OnOpen = branch => h.Repo.AdvanceMain($"m-{branch}");
        // CI on an updated head stays pending until both items' updated heads are in CI, or for a while: so were the queue to
        // let both in, both would be in flight at once; as it does not, the one in its turn goes on after the pause.
        var reading = new HashSet<string>();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Repo.OnCiRead = sha =>
        {
            if (sha.Contains("-u", StringComparison.Ordinal))
            {
                lock (reading)
                {
                    if (reading.Add(sha[..sha.IndexOf("-u", StringComparison.Ordinal)]) && reading.Count == 1)
                    {
                        _ = Task.Delay(TimeSpan.FromMilliseconds(500)).ContinueWith(_ => released.TrySetResult(), TaskScheduler.Default);
                    }
                    if (reading.Count == 2)
                    {
                        released.TrySetResult();
                    }
                }
            }
        };
        h.Repo.CiFor = sha => sha.Contains("-u", StringComparison.Ordinal) && !released.Task.IsCompleted ? Pending(sha) : null;
        var outcomes = await Task.WhenAll(Task.Run(() => h.Run(A)), Task.Run(() => h.Run(B)));
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Error + h.Logged));
        for (var poll = 0; poll < 5 && h.Repo.Merges.Count < 2; poll++)
        {
            foreach (var story in new[] { A, B })
            {
                var again = await h.Run(story);
                Assert.True(again.Succeeded, again.Error + h.Logged);
            }
        }
        Assert.Equal(2, h.Repo.Merges.Count);
        Assert.All(h.Repo.Merges, m => Assert.Equal(m.MainCount, m.Contained));
        Assert.Empty(h.Repo.Overlaps);
    }
}

/// <summary>The queue's rules over ledger rows (sc-25384).</summary>
public class MergeQueueRuleTests
{
    private static long _id;

    private static LedgerEntry Row(WorkState state, string? step = null, string? detail = null) =>
        new() { Id = Interlocked.Increment(ref _id), State = state, Step = step, Detail = detail, Outcome = StepOutcomes.Of(null, state, step, detail) };

    private static List<LedgerEntry> Queued(params LedgerEntry[] more) =>
        [Row(WorkState.Review), Row(WorkState.CI), Row(WorkState.MergeGate, null, "sha"), Row(WorkState.MergeGate, RunPipeline.Steps.Queued, "sha"), .. more];

    [Fact]
    public void The_head_of_the_queue_takes_a_free_turn_and_everyone_else_waits_fifo()
    {
        MergeQueue.Entry First(long? turn = null) => new(1, "sc-1", 10, turn);
        MergeQueue.Entry Second(long? turn = null) => new(2, "sc-2", 20, turn);

        Assert.Equal(MergeQueue.TurnKind.Take, MergeQueue.TurnOf(1, [Second(), First()]).Kind);
        var waiting = MergeQueue.TurnOf(2, [Second(), First()]);
        Assert.Equal((MergeQueue.TurnKind.Wait, "sc-1", 2, 2), (waiting.Kind, waiting.Ahead!.ExternalId, waiting.Position, waiting.Count));
        // A later item holding the turn (e.g. it took it while the head was paused) keeps it; the head waits for it.
        Assert.Equal(MergeQueue.TurnKind.Held, MergeQueue.TurnOf(2, [First(), Second(turn: 30)]).Kind);
        Assert.Equal(MergeQueue.TurnKind.Wait, MergeQueue.TurnOf(1, [First(), Second(turn: 30)]).Kind);
        Assert.Throws<InvalidOperationException>(() => MergeQueue.TurnOf(3, [First()]));
    }

    [Fact]
    public void When_two_items_hold_a_turn_the_newer_turn_is_the_repos_and_the_older_holder_waits()
    {
        // The head held the turn (row 15), was paused idle and left out; the second took the turn (row 30) meanwhile.
        MergeQueue.Entry first = new(1, "sc-1", 10, 15);
        MergeQueue.Entry second = new(2, "sc-2", 20, 30);

        var head = MergeQueue.TurnOf(1, [first, second]);
        Assert.Equal((MergeQueue.TurnKind.Wait, "sc-2"), (head.Kind, head.Ahead!.ExternalId));
        Assert.Equal(MergeQueue.TurnKind.Held, MergeQueue.TurnOf(2, [first, second]).Kind);
        // Once the second has left the queue, the head holds its own turn again.
        Assert.Equal(MergeQueue.TurnKind.Held, MergeQueue.TurnOf(1, [first]).Kind);
    }

    [Fact]
    public void The_turn_row_is_the_queue_turn_checkpoint_that_holds_the_turn()
    {
        var turn = Row(WorkState.MergeGate, RunPipeline.Steps.QueueTurn);
        Assert.Equal(turn.Id, MergeQueue.TurnRowOf(Queued(turn, Row(WorkState.Paused, null, RunPipeline.Interrupted)), RunPipeline.Steps.QueueTurn));
        Assert.Null(MergeQueue.TurnRowOf(Queued(), RunPipeline.Steps.QueueTurn));
    }

    [Fact]
    public void An_interruption_keeps_the_turn_and_any_other_pause_or_leaving_mergegate_releases_it()
    {
        var turn = Row(WorkState.MergeGate, RunPipeline.Steps.QueueTurn);
        Assert.True(MergeQueue.InTurn(Queued(turn), RunPipeline.Steps.QueueTurn));

        var interrupted = Queued(turn, Row(WorkState.Paused, null, RunPipeline.Interrupted));
        Assert.True(MergeQueue.Member(interrupted));
        Assert.True(MergeQueue.InTurn(interrupted, RunPipeline.Steps.QueueTurn));
        Assert.True(MergeQueue.InTurn([.. interrupted, Row(WorkState.MergeGate, null, "unpaused")], RunPipeline.Steps.QueueTurn));

        // A user's Pause or the usage pause keeps the item's place (Continue resumes it) but releases its turn.
        var userPaused = Queued(turn, Row(WorkState.Paused, null, RunPipeline.UserPaused));
        Assert.True(MergeQueue.Member(userPaused));
        Assert.False(MergeQueue.InTurn(userPaused, RunPipeline.Steps.QueueTurn));
        var usagePaused = Queued(turn, Row(WorkState.Paused, null, RunPipeline.UsagePaused));
        Assert.True(MergeQueue.Member(usagePaused));
        Assert.False(MergeQueue.InTurn(usagePaused, RunPipeline.Steps.QueueTurn));
        // So do the freeze (its Continue resumes it) and controls that could not be read (they resume once readable).
        foreach (var detail in new[] { RunPipeline.FreezePaused, RunPipeline.ControlsUnreadablePaused })
        {
            var held = Queued(turn, Row(WorkState.Paused, null, detail));
            Assert.True(MergeQueue.Member(held), detail);
            Assert.False(MergeQueue.InTurn(held, RunPipeline.Steps.QueueTurn), detail);
        }
        // Paused any other way (e.g. a refused claim) needs a human: out of the queue.
        Assert.False(MergeQueue.Member(Queued(turn, Row(WorkState.Paused, null, "claim refused: owner changed"))));
        Assert.False(MergeQueue.Member([.. userPaused, Row(WorkState.Paused, RunPipeline.Steps.Parked, "out of scope")]));
        var resumed = new List<LedgerEntry>([.. userPaused, Row(WorkState.MergeGate, null, "unpaused")]);
        Assert.True(MergeQueue.Member(resumed));
        Assert.False(MergeQueue.InTurn(resumed, RunPipeline.Steps.QueueTurn)); // it must take the turn again
        Assert.NotNull(MergeQueue.QueuedRow(resumed, RunPipeline.Steps.Queued)); // but keeps its place

        // Interrupted, then parked (its story left the scope): it never resumes by itself, so it must not hold up the repo.
        Assert.False(MergeQueue.Member([.. interrupted, Row(WorkState.Paused, RunPipeline.Steps.Parked, "out of scope")]));

        var left = Queued(turn, Row(WorkState.Review));
        Assert.False(MergeQueue.Member(left));
        Assert.Null(MergeQueue.QueuedRow([.. left, Row(WorkState.CI), Row(WorkState.MergeGate, null, "sha2")], RunPipeline.Steps.Queued));
    }

    [Fact]
    public void The_queue_lock_key_is_stable_per_repo_and_never_a_work_item_id()
    {
        var key = MergeQueue.LockKey(new RepoRef("michaeltrefry", "dark-factory-sandbox"));
        Assert.True(key < 0);
        Assert.Equal(key, MergeQueue.LockKey(new RepoRef("MichaelTrefry", "Dark-Factory-Sandbox")));
        Assert.NotEqual(key, MergeQueue.LockKey(new RepoRef("michaeltrefry", "dark-factory")));
    }
}

/// <summary>
/// The merge queue on the real ledger (sc-25384): the queue's order and turn are Postgres rows, its lock a Postgres advisory
/// lock, and each run its own connection, as separate processes would be. Needs the compose Postgres on localhost:5434.
/// </summary>
public sealed class MergeQueuePostgresTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_queue");
        _cs = _db.ConnectionString;
        await LedgerMigrations.MigrateAsync(_cs, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private MergeQueueTests.Harness Harness() => new(LedgerDbContext.PostgresOptions(_cs), new PostgresRunLocks(_cs));

    [Fact]
    public async Task Two_approved_items_merge_in_approval_order_through_the_postgres_ledger_and_a_restart()
    {
        var h = Harness();
        using var interrupt = new CancellationTokenSource();
        var (runA, release) = await MergeQueueTests.ABehindAWaitingForCi(h, interrupt.Token);
        await interrupt.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runA);

        // Restart: fresh locks (the old process's sessions are gone), B first — A still holds the turn in the ledger.
        h.Locks = new PostgresRunLocks(_cs);
        release.SetResult();
        Assert.Equal(WorkState.MergeGate, (await h.Run(MergeQueueTests.B)).State);
        Assert.Empty(h.Repo.Merges);
        Assert.Equal(WorkState.Watch, (await h.Run(MergeQueueTests.A)).State);
        Assert.Equal(WorkState.Watch, (await h.Run(MergeQueueTests.B)).State);
        await MergeQueueTests.AssertMergedInOrderEachOnAHeadContainingMain(h);
    }

    [Fact]
    public async Task Racing_runs_on_postgres_never_have_two_items_of_a_repo_in_flight() => await MergeQueueTests.RaceAsync(Harness());
}
