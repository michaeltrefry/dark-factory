using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Forwards every call of <typeparamref name="T"/> to <see cref="Target"/>, telling <see cref="On"/> its name
/// (<c>IWorker.RunAsync</c>) first: a probe on every call a pipeline makes of one of its seams.
/// </summary>
public class Probe<T> : DispatchProxy where T : class
{
    public T Target { get; set; } = null!;
    public Action<string> On { get; set; } = _ => { };

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        On($"{typeof(T).Name}.{method!.Name}");
        try
        {
            return method.Invoke(Target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

/// <summary>
/// sc-25988: the sandbox user's lock is held for a run's worker phases only. A probe on every call the pipeline makes of its worker,
/// its workspaces, the gate's test runner, the reviewers and GitHub records whether the calling flow held the lock: everything that
/// runs as the sandbox user (a session, an orphan's stop, a worktree's set-up and deletion, the test runs) must, and the waits a run
/// spends most of its time in (review, CI, the merge) must not — so two runs overlap there and never in the sandbox.
/// </summary>
public class LockScopeTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// The calls that run as the sandbox user (or delete as it): each must be made holding the lock. One exception, asserted where
    /// it happens (<see cref="RestoresOf"/>): the merge queue's base update restores its worktree without the lock when nothing is
    /// where the worktree goes — a fresh worktree runs no helper; replacing a leftover deletes it as the sandbox user, under the lock.
    /// </summary>
    private static readonly string[] SandboxCalls =
    [
        "IWorker.RunAsync", "IWorker.StopOrphanAsync", "IRepoWorkspace.PrepareAsync", "IRepoWorkspace.RestoreAsync", "IRepoWorkspace.RemoveAsync",
        "IGateTestRunner.RunAsync", "IPlanRunner.RunAsync",
    ];

    /// <summary>The calls a run waits on other systems in: none may be made holding the lock.</summary>
    private static readonly string[] WaitingCalls = ["IReviewer.ReviewAsync", "IGateGitHub.GetCiAsync", "IGateGitHub.MergeAsync"];

    /// <summary>One run's seams, each probed for the lock.</summary>
    private sealed class Probed(WorkerLock? workerLock)
    {
        public ConcurrentQueue<(string Call, bool Held)> Calls { get; } = new();
        private static readonly Lock Writing = new();
        private readonly System.Text.StringBuilder _text = new();

        /// <summary>The run's log: written by the run and its watchers, read by the test meanwhile.</summary>
        public TextWriter Log => new Lines(_text);

        private sealed class Lines(System.Text.StringBuilder text) : TextWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public override void Write(char value)
            {
                lock (Writing)
                {
                    text.Append(value);
                }
            }

            public override void Write(string? value)
            {
                lock (Writing)
                {
                    text.Append(value);
                }
            }
        }

        public T Wrap<T>(T target) where T : class
        {
            var proxy = DispatchProxy.Create<T, Probe<T>>();
            var probe = (Probe<T>)(object)proxy;
            probe.Target = target;
            probe.On = call =>
            {
                var held = workerLock?.IsHeld == true;
                Calls.Enqueue((call, held));
                OnCall?.Invoke(call, held);
            };
            return proxy;
        }

        /// <summary>Runs as each probed call is made (before it), with whether the lock was held.</summary>
        public Action<string, bool>? OnCall { get; set; }

        public List<(string Call, bool Held)> Of(params string[] calls) => Calls.Where(c => calls.Contains(c.Call)).ToList();

        public bool Logged(string text)
        {
            lock (Writing)
            {
                return _text.ToString().Contains(text, StringComparison.Ordinal);
            }
        }
    }

    private static WorkerLock NewLock() => WorkerLock.For(Directory.CreateTempSubdirectory("df-lock-scope-").FullName);

    private static RunPipeline Pipeline(Harness h, Probed probed, WorkerLock? workerLock, IWorker worker, bool gate = true,
        Func<CancellationToken, Task>? enterSandbox = null, IControls? controls = null) =>
        new(h.Stories, h.Ledger, h.Locks, probed.Wrap<IRepoWorkspace>(h.Workspaces), probed.Wrap(worker), h.Prs, Sandbox, probed.Log,
            controls: controls ?? h.Controls, controlPollInterval: Poll,
            gate: gate
                ? new GateStage(probed.Wrap<IGateGitHub>(h.GitHub), probed.Wrap(h.Panel ?? h.Reviewer), TimeSpan.FromMilliseconds(1), h.CiTimeout, null,
                    probed.Wrap<IGateTestRunner>(h.TestRunner), null, FakeCodeGraphRisk.Reader(h.CodeGraph))
                : null,
            maxControlReadFailures: 2, workerLock: workerLock, enterSandbox: enterSandbox);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        Assert.True(condition(), what);
    }

    /// <summary>A reviewer whose correctness review of the first head finds one confirmed blocking finding: one fix round, then a pass.</summary>
    private static FakeReviewer OneFixRound() => new()
    {
        Findings = r => r.Role == ReviewRoles.Correctness && r.Pull.HeadSha == Sha1
            ? [new Finding(Finding.Blocking, "the empty string is untested", "src/x.cs", 1, "WordCount(\"\") is untested")]
            : [],
    };

    [Fact]
    public async Task Every_call_that_runs_as_the_sandbox_user_holds_the_lock_and_no_review_ci_or_merge_call_does()
    {
        var workerLock = NewLock();
        var h = new Harness { Reviewer = OneFixRound() };
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel), ReportsModel(ImplementerModel));
        var entered = new List<bool>();

        var outcome = await Pipeline(h, probed, workerLock, worker, enterSandbox: _ =>
        {
            entered.Add(workerLock.IsHeld);
            return Task.CompletedTask;
        }).RunAsync(77, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(
            [WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch],
            await h.Transitions());
        // The implementer and the fixer, each worktree's set-up and removal, and the gate's test runs: all under the lock.
        var sandbox = probed.Of(SandboxCalls);
        Assert.Equal(2, sandbox.Count(c => c.Call == "IWorker.RunAsync"));
        Assert.Contains(sandbox, c => c.Call == "IRepoWorkspace.PrepareAsync");
        Assert.Contains(sandbox, c => c.Call == "IRepoWorkspace.RestoreAsync");
        Assert.Equal(2, sandbox.Count(c => c.Call == "IRepoWorkspace.RemoveAsync"));
        Assert.Contains(sandbox, c => c.Call == "IGateTestRunner.RunAsync");
        Assert.All(sandbox, c => Assert.True(c.Held, $"{c.Call} ran without the sandbox user's lock"));
        // The reviews, the CI reads and the merge: none holds it, so another run's worker runs meanwhile.
        var waiting = probed.Of(WaitingCalls);
        Assert.All(WaitingCalls, call => Assert.Contains(waiting, c => c.Call == call));
        Assert.All(waiting, c => Assert.False(c.Held, $"{c.Call} held the sandbox user's lock"));
        // The sandbox set-up every run shares: once per run, in its first worker phase, under the lock.
        Assert.Equal([true], entered);
        Assert.False(workerLock.IsHeld);
        using var free = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!); // released when the run ended
    }

    /// <summary>How many flows are inside the sandbox at once, over every worker that reports to it.</summary>
    private sealed class SandboxMeter
    {
        private int _inside;
        private int _most;
        public int Most => Volatile.Read(ref _most);
        public int Sessions;

        public async Task<WorkerResult> InsideAsync(Func<Task<WorkerResult>> session)
        {
            var now = Interlocked.Increment(ref _inside);
            int seen;
            while ((seen = Volatile.Read(ref _most)) < now && Interlocked.CompareExchange(ref _most, now, seen) != seen)
            {
            }
            Interlocked.Increment(ref Sessions);
            try
            {
                return await session();
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }

    [Fact]
    public async Task While_one_item_waits_on_its_review_another_items_implementer_runs_and_the_two_never_share_the_sandbox_user()
    {
        // Two runs as the intake loop dispatches them with Intake:MaxConcurrentRuns 2: each its own ledger item, one sandbox user.
        var workerLock = NewLock();
        var meter = new SandboxMeter();
        var (aSession, aSessionEnds, aInReview, aReviewEnds) =
            (new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource());
        var a = new Harness { Panel = new GatedReviewer(aInReview, aReviewEnds.Task) };
        var b = new Harness();
        var (probedA, probedB) = (new Probed(workerLock), new Probed(workerLock));
        var workerA = new FakeWorker(call => meter.InsideAsync(async () =>
        {
            aSession.SetResult();
            await aSessionEnds.Task;
            return await ReportsModel(ImplementerModel)(call);
        }));
        bool? aWaitedOnReview = null;
        var workerB = new FakeWorker(call => meter.InsideAsync(async () =>
        {
            // B's session holds the sandbox user while A, past its own session, reaches its review and waits there: A needs no
            // lock for that (it would never get there otherwise).
            await aInReview.Task.WaitAsync(TimeSpan.FromSeconds(30));
            aWaitedOnReview = !aReviewEnds.Task.IsCompleted;
            return await ReportsModel(ImplementerModel)(call);
        }));

        // A's implementer holds the sandbox user: B's run starts, and waits at its Implement step — it does not fail.
        var runA = Task.Run(() => Pipeline(a, probedA, workerLock, workerA, controls: a.Controls).RunAsync(77, CancellationToken.None));
        await aSession.Task;
        var runB = Task.Run(() => Pipeline(b, probedB, workerLock, workerB, controls: b.Controls).RunAsync(77, CancellationToken.None));
        await Eventually(() => probedB.Logged("waiting for the sandbox user to implement"), "B waits for the sandbox user");
        Assert.Empty(workerB.Calls);

        // A's session ends and A goes on to its review, without the lock: B's implementer runs while A waits there, and B's
        // whole run (its own review, CI, test runs and merge) finishes before A's review does.
        aSessionEnds.SetResult();
        await aInReview.Task;
        var outcomeB = await runB;
        Assert.True(outcomeB.Succeeded, outcomeB.Error);
        Assert.Equal(WorkState.Watch, outcomeB.State);
        Assert.True(aWaitedOnReview, "B's implementer did not run while A waited on its review");
        Assert.False(runA.IsCompleted);

        aReviewEnds.SetResult();
        var outcomeA = await runA;
        Assert.True(outcomeA.Succeeded, outcomeA.Error);
        // Never two in the sandbox at once, and every sandbox call of both runs held the lock.
        Assert.Equal((2, 1), (meter.Sessions, meter.Most));
        Assert.All(probedA.Of(SandboxCalls).Concat(probedB.Of(SandboxCalls)), c => Assert.True(c.Held, $"{c.Call} ran without the sandbox user's lock"));
    }

    /// <summary>The panel, whose first call says it started and waits to be let go.</summary>
    private sealed class GatedReviewer(TaskCompletionSource started, Task release) : IReviewer
    {
        private readonly FakeReviewer _inner = new();

        public async Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
        {
            started.TrySetResult();
            await release.WaitAsync(ct);
            return await _inner.ReviewAsync(request, ct);
        }

        public Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => _inner.ConfirmAsync(request, ct);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("freeze")]
    public async Task A_run_waiting_for_the_sandbox_user_honours_pause_stop_and_the_freeze_and_starts_no_worker(string control)
    {
        var workerLock = NewLock();
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));
        // Another process's worker phase (a `factory run` beside `factory work`) holds the sandbox user throughout.
        var other = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!);

        var run = Task.Run(() => Pipeline(h, probed, workerLock, worker).RunAsync(77, CancellationToken.None));
        await Eventually(() => probed.Logged("waiting for the sandbox user to implement"), "the run waits for the sandbox user");
        Assert.False(run.IsCompleted);
        switch (control)
        {
            case "pause":
                await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Paused, "tester", CancellationToken.None);
                break;
            case "stop":
                await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);
                break;
            default:
                await h.Controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
                break;
        }
        var outcome = await run;

        Assert.Empty(worker.Calls);
        Assert.Empty(probed.Of(SandboxCalls));
        var last = (await h.Rows()).Last(r => r.Step is null);
        Assert.Equal(control == "stop" ? WorkState.Cancelled : WorkState.Paused, outcome.State);
        Assert.Equal(control switch { "pause" => RunPipeline.UserPaused, "freeze" => RunPipeline.FreezePaused, _ => last.Detail }, last.Detail);
        if (control == "pause")
        {
            // Continue, and the sandbox user free again: the run that resumes it implements it.
            other.Dispose();
            await h.Controls.ClearAsync(ControlScope.Item("sc-77"), CancellationToken.None);
            var resumed = await Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None);
            Assert.Equal((WorkState.Review, (string?)null), (resumed.State, resumed.Error));
            Assert.Single(worker.Calls);
        }
        other.Dispose();
    }

    [Fact]
    public async Task Controls_that_cannot_be_read_while_a_run_waits_for_the_sandbox_user_pause_it_rather_than_let_it_wait_blind()
    {
        var workerLock = NewLock();
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));
        using var other = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!);
        var controls = new FlakyControls(h.Controls);

        var run = Task.Run(() => Pipeline(h, probed, workerLock, worker, controls: controls).RunAsync(77, CancellationToken.None));
        await Eventually(() => probed.Logged("waiting for the sandbox user to implement"), "the run waits for the sandbox user");
        // From here every read fails: more than Controls:MaxReadFailures (2 here) in a row.
        controls.Broken = true;
        var outcome = await run;

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Equal(RunPipeline.ControlsUnreadablePaused, (await h.Rows()).Last(r => r.Step is null).Detail);
        Assert.Contains(await h.Rows(), r => r.Step == RunPipeline.Steps.ControlsUnreadable);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task A_sandbox_that_is_not_ready_is_the_factorys_failure_found_under_the_lock_and_the_item_is_left_where_it_is()
    {
        var workerLock = NewLock();
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));
        var held = new List<bool>();

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() => Pipeline(h, probed, workerLock, worker, gate: false, enterSandbox: _ =>
        {
            held.Add(workerLock.IsHeld);
            throw new FactoryUnavailableException("the worker sandbox: Stale helper", new InvalidOperationException("Stale helper"));
        }).RunAsync(77, CancellationToken.None));

        // Nothing is recorded against the item (E10), no session started, and the lock is free again.
        Assert.True(IntakeLoop.IsFactoryWide(ex));
        Assert.Equal([true], held);
        Assert.Equal([WorkState.Intake, WorkState.Implement], await h.Transitions());
        Assert.Empty(worker.Calls);
        Assert.False(workerLock.IsHeld);
        // The next run tries the set-up again, and goes on once it passes.
        var outcome = await Pipeline(h, probed, workerLock, worker, gate: false, enterSandbox: _ =>
        {
            held.Add(workerLock.IsHeld);
            return Task.CompletedTask;
        }).RunAsync(77, CancellationToken.None);
        Assert.Equal((WorkState.Review, (string?)null), (outcome.State, outcome.Error));
        Assert.Equal([true, true], held);
    }

    [Fact]
    public async Task A_stop_waits_for_the_sandbox_user_to_stop_a_leftover_worker_but_leaves_a_worktree_it_cannot_delete_now_to_the_sweep()
    {
        var workerLock = NewLock();
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel)) { OrphanAlive = true };
        // An item at Review, with a worker a crashed run left on record, then stopped while no run drives it.
        Assert.Equal(WorkState.Review, (await Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None)).State);
        var item = (await h.Ledger.FindAsync(ItemNaming.Shortcut.Source, "sc-77", CancellationToken.None))!;
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, null, WorkerPid.ToString(), CancellationToken.None);
        await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);
        var removedBefore = probed.Of("IRepoWorkspace.RemoveAsync").Count;
        var other = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!); // another run's worker has the sandbox user

        // The orphan's stop is a kill of every process of the sandbox user: it waits for the other run's phase to end.
        var run = Task.Run(() => Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None));
        await Eventually(() => probed.Logged("waiting for the sandbox user to stop sc-77's leftover worker"), "the stop waits for the sandbox user");
        Assert.Empty(worker.OrphanStops);
        other.Dispose();
        var outcome = await run;

        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Equal([WorkerPid], worker.OrphanStops);
        Assert.All(probed.Of("IWorker.StopOrphanAsync", "IRepoWorkspace.RemoveAsync"), c => Assert.True(c.Held, $"{c.Call} ran without the sandbox user's lock"));
        Assert.Equal(removedBefore + 1, probed.Of("IRepoWorkspace.RemoveAsync").Count); // free by then: its worktree is removed
    }

    [Fact]
    public async Task Clean_up_outside_a_worker_phase_never_waits_for_another_runs_worker_and_never_deletes_without_the_lock()
    {
        var workerLock = NewLock();
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));
        Assert.Equal(WorkState.Review, (await Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None)).State);
        await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);
        var removedBefore = probed.Of("IRepoWorkspace.RemoveAsync").Count;
        using var other = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!); // held for the whole stop

        // No leftover worker to stop: the stop is finished at once, and its worktree (deleted as the sandbox user) is left.
        var outcome = await Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None);

        Assert.Equal((WorkState.Cancelled, (string?)null), (outcome.State, outcome.Error));
        Assert.Equal(removedBefore, probed.Of("IRepoWorkspace.RemoveAsync").Count);
        Assert.True(probed.Logged("the sandbox user is busy (another run's worker phase); leaving /wt/factory/sc-77 for the next worker phase's sweep"));
    }

    [Fact]
    public async Task A_worker_that_would_not_stop_keeps_the_sandbox_user_until_its_helper_is_gone_and_no_other_runs_session_starts_before()
    {
        var workerLock = NewLock();
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (a, b) = (new Harness(), new Harness());
        var (probedA, probedB) = (new Probed(workerLock), new Probed(workerLock));
        var kept = new ConcurrentQueue<string>();
        var workerA = new FakeWorker(async call =>
        {
            await call.OnSession("sess-a", CancellationToken.None);
            // As the real worker ends such a session (WorkerSandbox.EndAsync): its helper did not exit, so the lock is kept for it.
            Assert.True(workerLock.KeepUntil(gone.Task, "a launch helper (sudo pid 4242)", kept.Enqueue));
            throw WorkerStillRunning.Mark(new TimeoutException("Worker did not finish within 00:30:00"));
        });
        var workerB = new FakeWorker(ReportsModel(ImplementerModel));

        // A's session ends with its worker still running: the item escalates, and nothing deletes the worktree under it.
        var outcomeA = await Pipeline(a, probedA, workerLock, workerA, gate: false).RunAsync(77, CancellationToken.None);
        Assert.Equal(WorkState.Escalated, outcomeA.State);
        Assert.Empty(probedA.Of("IRepoWorkspace.RemoveAsync"));
        Assert.False(workerLock.IsHeld);
        Assert.Contains("a launch helper (sudo pid 4242) is still alive", Assert.Single(kept));

        // A's run is over, but the helper's exit is still to kill every process of the sandbox user: B waits at its worker phase
        // (this is not B's first phase in general: no sweep of leftovers would save its worker).
        var runB = Task.Run(() => Pipeline(b, probedB, workerLock, workerB, gate: false).RunAsync(77, CancellationToken.None));
        await Eventually(() => probedB.Logged("waiting for the sandbox user to implement"), "B waits for the sandbox user");
        Assert.Empty(workerB.Calls);
        Assert.Empty(probedB.Of(SandboxCalls));
        Assert.False(runB.IsCompleted);

        gone.SetResult(); // the helper is dead
        var outcomeB = await runB;

        Assert.Equal((WorkState.Review, (string?)null), (outcomeB.State, outcomeB.Error));
        Assert.Single(workerB.Calls);
        Assert.True(probedB.Logged("the sandbox user is free; going on to implement"));
        Assert.All(probedB.Of(SandboxCalls), c => Assert.True(c.Held, $"{c.Call} ran without the sandbox user's lock"));
    }

    [Fact]
    public async Task A_lock_that_cannot_be_opened_is_the_factorys_failure_and_the_claimed_item_is_left_where_it_is()
    {
        // The work root is not a directory (not set up): the lock file can never be made.
        var notADirectory = Path.Combine(Directory.CreateTempSubdirectory("df-lock-scope-").FullName, "work-root");
        File.WriteAllText(notADirectory, "x");
        var workerLock = WorkerLock.For(notADirectory);
        var h = new Harness();
        var probed = new Probed(workerLock);
        var worker = new FakeWorker(ReportsModel(ImplementerModel));

        var ex = await Assert.ThrowsAsync<FactoryUnavailableException>(() => Pipeline(h, probed, workerLock, worker, gate: false).RunAsync(77, CancellationToken.None));

        // Not the item's failure (E10): claimed and at Implement, it is not escalated and no session started.
        Assert.StartsWith($"the sandbox user's lock: {workerLock.Path} cannot be opened (", ex.Message);
        Assert.True(IntakeLoop.IsFactoryWide(ex));
        Assert.Equal([WorkState.Intake, WorkState.Implement], await h.Transitions());
        Assert.Empty(worker.Calls);
        Assert.Empty(probed.Of(SandboxCalls));
    }

    // ---- The merge queue's base update, a conflict fix round and a CI fix round (one simulated repository, MergeQueueTests) ----

    private const int Queued = MergeQueueTests.A;
    private static readonly string QueuedBranch = StoryId.BranchName(Queued);
    private static readonly string QueuedId = StoryId.Format(Queued);

    private static RunPipeline QueuePipeline(MergeQueueTests.Harness h, Probed probed, WorkerLock workerLock) =>
        new(h.Stories, new WorkLedger(h.Db(), TimeProvider.System), h.Locks, probed.Wrap<IRepoWorkspace>(h.Repo), probed.Wrap<IWorker>(h.Worker), h.Repo, Sandbox,
            probed.Log, controls: h.Controls, controlPollInterval: Poll,
            gate: new GateStage(probed.Wrap<IGateGitHub>(h.Repo), probed.Wrap<IReviewer>(h.Reviewer), h.CiPoll, TimeSpan.FromSeconds(30), Risk: FakeCodeGraphRisk.Reader()),
            maxControlReadFailures: 2, workerLock: workerLock);

    /// <summary>
    /// Every <c>RestoreAsync</c> of a run, in order: whether the lock was held, and whether something was where the worktree goes
    /// (a leftover, whose replacement deletes it as the sandbox user). Unheld is allowed only with no leftover.
    /// </summary>
    private static ConcurrentQueue<(bool Held, bool Leftover)> RestoresOf(MergeQueueTests.Harness h, Probed probed)
    {
        var restores = new ConcurrentQueue<(bool Held, bool Leftover)>();
        probed.OnCall = (call, held) =>
        {
            if (call == "IRepoWorkspace.RestoreAsync")
            {
                restores.Enqueue((held, h.Repo.ReopenAsync(Sandbox, QueuedBranch, CancellationToken.None).GetAwaiter().GetResult() is not null));
            }
        };
        return restores;
    }

    private static void AssertEverySandboxCallButRestoreHeld(Probed probed) =>
        Assert.All(probed.Of(SandboxCalls).Where(c => c.Call != "IRepoWorkspace.RestoreAsync"), c => Assert.True(c.Held, $"{c.Call} ran without the sandbox user's lock"));

    [Fact]
    public async Task A_base_update_restores_a_fresh_worktree_without_the_lock_and_removes_it_under_the_lock()
    {
        var workerLock = NewLock();
        var h = new MergeQueueTests.Harness();
        var probed = new Probed(workerLock);
        var restores = RestoresOf(h, probed);
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1"); // the head is behind its base when it reaches the queue

        var outcome = await QueuePipeline(h, probed, workerLock).RunAsync(Queued, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Single(await h.Steps(Queued, RunPipeline.Steps.BaseUpdate));
        // The one restore is the base update's: nothing was where its worktree goes, so no helper runs and no lock is needed —
        // other runs' workers go on meanwhile. Everything else of the sandbox user's held the lock: the implementer and its
        // worktree's set-up and removal, and the base-update worktree's removal.
        Assert.Equal([(false, false)], restores);
        Assert.Equal([("IRepoWorkspace.HasLeftoverAsync", false), ("IRepoWorkspace.RestoreAsync", false)],
            probed.Of("IRepoWorkspace.HasLeftoverAsync", "IRepoWorkspace.RestoreAsync"));
        Assert.Equal(2, probed.Of("IRepoWorkspace.RemoveAsync").Count);
        Assert.Single(probed.Of("IWorker.RunAsync"));
        AssertEverySandboxCallButRestoreHeld(probed);
        Assert.All(probed.Of(WaitingCalls), c => Assert.False(c.Held, $"{c.Call} held the sandbox user's lock"));
        Assert.False(probed.Logged("[lock]")); // never waited
    }

    /// <summary>
    /// The base moves twice (once as the PR opens, again while CI runs on the first update), and another run's worker phase holds
    /// the sandbox user from the first head's CI on: the first update's worktree cannot be removed and is left behind.
    /// <paramref name="release"/>: whether that other phase ends as CI on the first update is read (before the second update).
    /// </summary>
    private static Func<IDisposable?> TwoUpdatesWithALeftover(MergeQueueTests.Harness h, WorkerLock workerLock, bool release)
    {
        IDisposable? other = null;
        var moved = false;
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        h.Repo.OnCiRead = sha =>
        {
            var updated = sha.StartsWith($"{QueuedBranch}-u", StringComparison.Ordinal);
            if (!updated && other is null)
            {
                other = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!);
            }
            if (updated && !moved)
            {
                moved = true;
                h.Repo.AdvanceMain("m2");
                if (release)
                {
                    other!.Dispose();
                }
            }
        };
        return () => other;
    }

    [Fact]
    public async Task A_base_update_over_a_leftover_worktree_replaces_it_under_the_lock()
    {
        var workerLock = NewLock();
        var h = new MergeQueueTests.Harness();
        var probed = new Probed(workerLock);
        var restores = RestoresOf(h, probed);
        TwoUpdatesWithALeftover(h, workerLock, release: true);

        var outcome = await QueuePipeline(h, probed, workerLock).RunAsync(Queued, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Equal(2, (await h.Steps(Queued, RunPipeline.Steps.BaseUpdate)).Count);
        // The first update's worktree stayed (the sandbox user was busy): the second update replaces it — a deletion as the
        // sandbox user — holding the lock; the first, with nothing there, needed none.
        Assert.True(probed.Logged($"the sandbox user is busy (another run's worker phase); leaving /wt/{QueuedBranch} for the next worker phase's sweep"));
        Assert.Equal([(false, false), (true, true)], restores);
        Assert.Equal(2, probed.Of("IRepoWorkspace.RemoveAsync").Count); // the implementer's, and the second update's
        AssertEverySandboxCallButRestoreHeld(probed);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("freeze")]
    public async Task A_base_update_waiting_to_replace_a_leftover_worktree_logs_the_wait_and_honours_pause_stop_and_the_freeze(string control)
    {
        var workerLock = NewLock();
        var h = new MergeQueueTests.Harness();
        var probed = new Probed(workerLock);
        var restores = RestoresOf(h, probed);
        var other = TwoUpdatesWithALeftover(h, workerLock, release: false); // the other run's session goes on and on

        var run = Task.Run(() => QueuePipeline(h, probed, workerLock).RunAsync(Queued, CancellationToken.None));
        try
        {
            // The run holds its item and the repository's queue turn: its wait is said, and it reads its controls meanwhile.
            await Eventually(() => probed.Logged($"[lock] {QueuedId}: waiting for the sandbox user to replace its leftover worktree"), "the base update waits for the sandbox user");
            Assert.False(run.IsCompleted);
            var (restored, removed) = (restores.Count, probed.Of("IRepoWorkspace.RemoveAsync").Count);
            switch (control)
            {
                case "pause":
                    await h.Controls.SetAsync(ControlScope.Item(QueuedId), ControlState.Paused, "tester", CancellationToken.None);
                    break;
                case "stop":
                    await h.Controls.SetAsync(ControlScope.Item(QueuedId), ControlState.Stopping, "tester", CancellationToken.None);
                    break;
                default:
                    await h.Controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
                    break;
            }
            var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); // a blind wait would never end

            Assert.Equal(control == "stop" ? WorkState.Cancelled : WorkState.Paused, outcome.State);
            var last = (await h.Rows(Queued)).Last(r => r.Step is null);
            Assert.Equal(control switch { "pause" => RunPipeline.UserPaused, "freeze" => RunPipeline.FreezePaused, _ => last.Detail }, last.Detail);
            // Nothing ran as the sandbox user while the other run's session had it: no restore, no deletion.
            Assert.Equal((1, restored, removed), (restored, restores.Count, probed.Of("IRepoWorkspace.RemoveAsync").Count));
            Assert.Single(h.Repo.CallsSnapshot(), c => c == $"restore {QueuedBranch}");
            Assert.Empty(h.Repo.Merges);
        }
        finally
        {
            other()?.Dispose();
        }
    }

    [Fact]
    public async Task A_conflict_fix_round_holds_the_lock_from_its_worktrees_restore_to_its_removal()
    {
        var workerLock = NewLock();
        var h = new MergeQueueTests.Harness();
        var probed = new Probed(workerLock);
        var restores = RestoresOf(h, probed);
        h.Repo.Conflicting.Add(QueuedBranch);
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1"); // behind, and conflicting, when it reaches the queue

        var outcome = await QueuePipeline(h, probed, workerLock).RunAsync(Queued, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Contains(WorkState.Fixing, await h.Transitions(Queued));
        Assert.Single(await h.Steps(Queued, RunPipeline.Steps.MergeConflict));
        // The base update that found the conflict: a fresh worktree, no lock. The fix round's restore: under the lock, like its
        // session — a worker phase, whether or not anything was there.
        Assert.Equal([(false, false), (true, false)], restores);
        Assert.Equal([true, true], probed.Of("IWorker.RunAsync").Select(c => c.Held)); // the implementer and the conflict fixer
        Assert.Equal(3, probed.Of("IRepoWorkspace.RemoveAsync").Count);
        AssertEverySandboxCallButRestoreHeld(probed);
        Assert.All(probed.Of(WaitingCalls), c => Assert.False(c.Held, $"{c.Call} held the sandbox user's lock"));
    }

    [Fact]
    public async Task A_ci_fix_round_holds_the_lock_from_its_worktrees_restore_to_its_removal()
    {
        var workerLock = NewLock();
        var h = new MergeQueueTests.Harness();
        var probed = new Probed(workerLock);
        var restores = RestoresOf(h, probed);
        h.Repo.OnOpen = _ => h.Repo.AdvanceMain("m1");
        // CI is red on the updated head (and green on its base): the PR's own failure, healed by a CI fix round.
        h.Repo.CiFor = sha => sha.StartsWith($"{QueuedBranch}-u", StringComparison.Ordinal) ? new CiFacts(sha, [new CheckFact("build-test", true, "failure", 7)]) : null;

        var outcome = await QueuePipeline(h, probed, workerLock).RunAsync(Queued, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Contains(WorkState.CIHealing, await h.Transitions(Queued));
        Assert.Equal([(false, false), (true, false)], restores); // the base update's, then the CI fix round's
        Assert.Equal([true, true], probed.Of("IWorker.RunAsync").Select(c => c.Held)); // the implementer and the CI fixer
        Assert.Equal(3, probed.Of("IRepoWorkspace.RemoveAsync").Count);
        AssertEverySandboxCallButRestoreHeld(probed);
        // No review and no merge holds it. Of the CI reads exactly one does: the fixer's prompt reads the failing checks once
        // when its session starts, inside its phase (a single read, not CI's polling, which is all outside).
        Assert.All(probed.Of("IReviewer.ReviewAsync", "IGateGitHub.MergeAsync"), c => Assert.False(c.Held, $"{c.Call} held the sandbox user's lock"));
        Assert.Single(probed.Of("IGateGitHub.GetCiAsync"), c => c.Held);
        Assert.True(probed.Of("IGateGitHub.GetCiAsync").Count(c => !c.Held) >= 3);
    }

    /// <summary>
    /// The one way production code runs the launch helper is <see cref="WorkerSandbox.Start"/>, which refuses without the lock
    /// — provided the sandbox has one. So: production builds a sandbox in one place only, with the work root's lock, and nothing
    /// but the sandbox, the worker and the gate's test runner starts a process through it.
    /// </summary>
    [Fact]
    public void Production_builds_the_sandbox_in_one_place_with_its_lock_and_starts_the_helper_only_through_it()
    {
        var src = Path.Combine(SandboxSupport.RepoRoot, "src", "DarkFactory.Orchestrator");
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}") && !f.Contains($"{separator}obj{separator}"))
            .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllLines);
        IEnumerable<string> Lines(string pattern) => files
            .SelectMany(f => f.Value.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, pattern) && !l.TrimStart().StartsWith("//")).Select(l => $"{f.Key}: {l.Trim()}"));

        var built = Assert.Single(Lines(@"new (Worker\.)?WorkerSandbox\("));
        Assert.StartsWith("FactoryOptions.cs: ", built);
        Assert.EndsWith("{ Lock = WorkerLock.For(WorkRoot) }", built);
        Assert.Equal(["Gate/SandboxTestRunner.cs", "Worker/ClaudeWorker.cs"],
            Lines(@"\bsandbox\.Start\(").Select(l => l.Split(": ")[0].Replace(separator, '/')).Order());
        // Whoever starts a helper process ends it with EndAsync: waited for, and the lock kept for it while it is alive.
        Assert.Equal(["Gate/SandboxTestRunner.cs", "Worker/ClaudeWorker.cs"],
            Lines(@"\bsandbox\.EndAsync\(").Select(l => l.Split(": ")[0].Replace(separator, '/')).Order());
        Assert.StartsWith("Worker" + separator + "WorkerSandbox.cs: ", Assert.Single(Lines(@"await EndAsync\(process\)")));
        // `factory run` and `factory work` both check the installed helper at start-up (read, not run), before anything is claimed.
        Assert.Equal(2, Lines(@"FactoryRunner\.CheckHelperAsync\(options\.Worker" + "Sandbox").Count(l => l.StartsWith("Program.cs: ", StringComparison.Ordinal)));
        // Whoever else names the launch helper only compares or reports it; sudo is started in one method.
        Assert.Equal(["Worker/WorkerSandbox.cs"], Lines(@"new ProcessStartInfo\(SudoPath\)").Select(l => l.Split(": ")[0].Replace(separator, '/')));
        // And the lock the sandbox requires there is real: nothing starts without it.
        var sandbox = new FactoryOptions(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new InMemorySecrets());
        Assert.Contains("Lock?.Require(", string.Join('\n', files[Path.Combine("Worker", "WorkerSandbox.cs")]));
        Assert.Equal(Path.Combine(sandbox.WorkRoot, WorkerLock.FileName), WorkerLock.For(sandbox.WorkRoot).Path);
    }

    [Fact]
    public async Task Unsandboxed_there_is_no_lock_and_a_run_goes_straight_through_its_worker_phases()
    {
        var h = new Harness { Reviewer = OneFixRound() };
        var probed = new Probed(null);
        var entered = 0;

        var outcome = await Pipeline(h, probed, null, new FakeWorker(ReportsModel(ImplementerModel), ReportsModel(ImplementerModel)),
            enterSandbox: _ => { entered++; return Task.CompletedTask; }).RunAsync(77, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(1, entered); // the worktree sweep still runs once, in the first worker phase
        Assert.False(probed.Logged("[lock]"));
    }
}
