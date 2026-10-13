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
}
