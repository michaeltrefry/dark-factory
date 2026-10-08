namespace DarkFactory.Orchestrator.Tests;

public class WorkerLockTests
{
    [Fact]
    public void Only_one_sandboxed_run_holds_the_work_root_at_a_time()
    {
        var root = Directory.CreateTempSubdirectory("df-lock-").FullName;

        using (WorkerLock.Acquire(root))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => WorkerLock.Acquire(root));
            Assert.Contains("only one worker may run at a time", ex.Message);
        }

        using var again = WorkerLock.Acquire(root); // released on dispose
    }
}
