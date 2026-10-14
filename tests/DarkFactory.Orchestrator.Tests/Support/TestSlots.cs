using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Worker slot pools for tests that never run a launch helper (sc-25989): each slot is a lock and a sandbox user's name, its
/// helper a path that does not exist — a pipeline under test takes the locks and records the slots, and its fake worker and
/// workspace run nothing as anyone.
/// </summary>
internal static class TestSlots
{
    /// <summary>A helper path nothing is installed at: a pool made here can hold and record, never launch.</summary>
    public const string NoHelper = "/nonexistent/df-test-helper";

    /// <summary>The one-slot pool of <paramref name="workerLock"/> (the sandbox user's lock before slots), or null without one.</summary>
    public static WorkerSlots? Of(WorkerLock? workerLock) =>
        workerLock is null ? null : WorkerSlots.Single(new WorkerSandbox("_dftest", NoHelper) { Lock = workerLock });

    /// <summary><paramref name="count"/> slots over the work root <paramref name="workRoot"/>: users <c>_dftest</c>, <c>_dftest2</c>, …, each with its own lock file there.</summary>
    public static WorkerSlots Pool(string workRoot, int count) => WorkerSlots.Create("_dftest", NoHelper, workRoot, count);
}
