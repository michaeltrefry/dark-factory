namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25988: the sandbox user's lock is held per phase, waited for, and re-entrant in the flow that holds it.</summary>
public class WorkerLockTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(5);

    private static WorkerLock NewLock() => WorkerLock.For(Directory.CreateTempSubdirectory("df-lock-").FullName);

    [Fact]
    public void The_lock_of_a_work_root_is_its_run_lock_file_and_two_of_one_root_are_one_lock()
    {
        var root = Directory.CreateTempSubdirectory("df-lock-").FullName;

        Assert.Equal(Path.Combine(root, ".factory-run.lock"), WorkerLock.For(root).Path);
        Assert.Equal(WorkerLock.For(root), WorkerLock.For(root + Path.DirectorySeparatorChar + "."));
    }

    [Fact]
    public async Task A_second_phase_waits_for_the_first_to_end_instead_of_failing()
    {
        var workerLock = NewLock();
        var release = new TaskCompletionSource();
        var order = new List<string>();
        var entered = new TaskCompletionSource();
        var first = workerLock.HoldAsync(async () =>
        {
            order.Add("first in");
            entered.SetResult();
            await release.Task;
            order.Add("first out");
        }, CancellationToken.None);
        await entered.Task;
        var waits = 0;

        var second = workerLock.HoldAsync(() =>
        {
            order.Add("second in");
            return Task.CompletedTask;
        }, CancellationToken.None, _ => { Interlocked.Increment(ref waits); return Task.CompletedTask; }, Retry);
        while (Volatile.Read(ref waits) < 3)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first in", "first out", "second in"], order);
    }

    [Fact]
    public async Task The_flow_that_holds_it_takes_it_again_at_once_and_no_other_flow_holds_it()
    {
        var workerLock = NewLock();
        Assert.False(workerLock.IsHeld);
        Task<bool>? outlives = null;
        var ended = new TaskCompletionSource();

        await workerLock.HoldAsync(async () =>
        {
            Assert.True(workerLock.IsHeld);
            workerLock.Require("a probe");
            // Re-entrant: the same flow, a callee of it, and another instance of the same path (a seat inside the run).
            await workerLock.HoldAsync(async () =>
            {
                await Task.Yield();
                Assert.True(new WorkerLock(workerLock.Path).IsHeld);
                Assert.True(await workerLock.TryHoldAsync(() => Task.CompletedTask));
            }, CancellationToken.None);
            // The inner hold's end is not the outer's.
            Assert.True(workerLock.IsHeld);
            // Another lock is not held by holding this one.
            Assert.False(NewLock().IsHeld);
            // A task started inside the hold and still running after it no longer holds it.
            outlives = Task.Run(async () =>
            {
                await ended.Task;
                return workerLock.IsHeld;
            });
        }, CancellationToken.None);

        Assert.False(workerLock.IsHeld);
        ended.SetResult();
        Assert.False(await outlives!);
        var refusal = Assert.Throws<InvalidOperationException>(() => workerLock.Require("a run of /bin/rm as _factory"));
        Assert.Contains("Refusing a run of /bin/rm as _factory: the worker lock", refusal.Message);
        // Released: the next phase takes it without a wait.
        await workerLock.HoldAsync(() => Task.CompletedTask, CancellationToken.None, _ => throw new InvalidOperationException("waited"));
    }

    [Fact]
    public async Task A_wait_ends_when_the_waiter_gives_up_or_is_cancelled_and_the_phase_never_runs()
    {
        var workerLock = NewLock();
        using var held = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!); // another process's worker phase
        var ran = false;

        var gaveUp = await Assert.ThrowsAsync<TimeoutException>(() => workerLock.HoldAsync(() => { ran = true; return Task.CompletedTask; },
            CancellationToken.None, _ => throw new TimeoutException("paused"), Retry));
        Assert.Equal("paused", gaveUp.Message);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workerLock.HoldAsync(() => { ran = true; return Task.CompletedTask; },
            cancel.Token, retry: Retry));

        Assert.False(await workerLock.TryHoldAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
        Assert.False(workerLock.IsHeld);
    }

    [Fact]
    public async Task A_phase_that_throws_releases_the_lock()
    {
        var workerLock = NewLock();

        await Assert.ThrowsAsync<InvalidOperationException>(() => workerLock.HoldAsync(() => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.True(await workerLock.TryHoldAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task A_whole_life_hold_keeps_every_phase_out_and_fails_at_once_when_one_is_running()
    {
        var root = Directory.CreateTempSubdirectory("df-lock-").FullName;
        var workerLock = WorkerLock.For(root);

        using (WorkerLock.Acquire(root))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root));
            Assert.Contains("is locked", ex.Message);
            Assert.False(await workerLock.TryHoldAsync(() => Task.CompletedTask));
        }

        await workerLock.HoldAsync(() =>
        {
            Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root));
            return Task.CompletedTask;
        }, CancellationToken.None);
        using var again = WorkerLock.Acquire(root); // released when the phase ended
    }

    /// <summary>A list a background report appends to while the test reads it.</summary>
    private sealed class Reports
    {
        private readonly List<string> _lines = [];
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task First => _first.Task;
        public Task Second => _second.Task;

        public void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
                (_lines.Count == 1 ? _first : _second).TrySetResult();
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }
    }

    [Fact]
    public async Task A_lock_kept_for_a_helper_that_outlived_its_phase_is_released_only_when_that_helper_is_gone_and_says_so_meanwhile()
    {
        var root = Directory.CreateTempSubdirectory("df-lock-").FullName;
        var workerLock = WorkerLock.For(root);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new Reports();
        var kept = false;

        // The phase gives up on a helper it started (it would not stop) and ends; the helper's exit is still to come.
        await workerLock.HoldAsync(() =>
        {
            kept = workerLock.KeepUntil(gone.Task, "a launch helper (sudo pid 4242)", reports.Add, TimeSpan.FromMilliseconds(1));
            return Task.CompletedTask;
        }, CancellationToken.None);

        // The flow no longer holds it, and nobody else gets it: not this process, not another, not a waiter.
        Assert.True(kept);
        Assert.False(workerLock.IsHeld);
        Assert.False(await workerLock.TryHoldAsync(() => Task.CompletedTask));
        Assert.Contains("is locked", Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root)).Message);
        var waits = 0;
        var next = workerLock.HoldAsync(() => Task.CompletedTask, CancellationToken.None,
            _ => { Interlocked.Increment(ref waits); return Task.CompletedTask; }, Retry, _ => { });
        // Never silent: said when the phase ended and again each report interval, naming the helper.
        await reports.Second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.All(reports.Lines.Take(2), line =>
        {
            Assert.StartsWith($"[lock] the sandbox user's lock {workerLock.Path} stays held", line);
            Assert.Contains("a launch helper (sudo pid 4242) is still alive", line);
        });
        while (Volatile.Read(ref waits) < 3)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.False(next.IsCompleted);

        gone.SetResult();

        await next.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nothing_is_kept_for_a_helper_already_gone_or_by_a_flow_that_holds_no_lock()
    {
        var workerLock = NewLock();

        var reported = 0;

        Assert.False(workerLock.KeepUntil(new TaskCompletionSource().Task, "nothing", _ => Interlocked.Increment(ref reported)));
        await workerLock.HoldAsync(() =>
        {
            Assert.True(workerLock.KeepUntil(Task.CompletedTask, "a helper that exited", _ => Interlocked.Increment(ref reported)));
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(await workerLock.TryHoldAsync(() => Task.CompletedTask)); // released with the phase
        Assert.Equal(0, Volatile.Read(ref reported));
    }

    [Fact]
    public async Task A_wait_for_the_lock_is_reported_with_why_the_last_try_failed_and_still_ends_when_cancelled()
    {
        var workerLock = NewLock();
        using var held = WorkerLock.Acquire(Path.GetDirectoryName(workerLock.Path)!);
        var reports = new Reports();
        using var cancel = new CancellationTokenSource();

        var waiting = workerLock.HoldAsync(() => Task.CompletedTask, cancel.Token, retry: Retry, report: reports.Add, reportEvery: TimeSpan.Zero);
        await reports.Second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.All(reports.Lines.Take(2), line =>
        {
            Assert.Contains($"for the sandbox user's lock {workerLock.Path} and still waiting", line);
            Assert.Contains(workerLock.Path, line[line.IndexOf("): ", StringComparison.Ordinal)..]); // the last try's own error
        });
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task A_lock_file_that_cannot_be_opened_is_the_factorys_failure_not_a_wait_and_not_an_items()
    {
        // The "work root" is a file: its lock file can never be made (as with a work root that was not set up).
        var notADirectory = Path.Combine(Directory.CreateTempSubdirectory("df-lock-").FullName, "work-root");
        File.WriteAllText(notADirectory, "x");
        var workerLock = WorkerLock.For(notADirectory);
        var ran = false;

        var waited = await Assert.ThrowsAsync<WorkSources.FactoryUnavailableException>(() => workerLock.HoldAsync(() => { ran = true; return Task.CompletedTask; },
            CancellationToken.None, _ => throw new InvalidOperationException("waited on a lock that can never be had")));
        var tried = await Assert.ThrowsAsync<WorkSources.FactoryUnavailableException>(() => workerLock.TryHoldAsync(() => { ran = true; return Task.CompletedTask; }));

        Assert.StartsWith($"the sandbox user's lock: {workerLock.Path} cannot be opened (", waited.Message);
        Assert.Equal(waited.Message, tried.Message);
        Assert.True(WorkSources.IntakeLoop.IsFactoryWide(waited));
        Assert.False(ran);
    }
}
