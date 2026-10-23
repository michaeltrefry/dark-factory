using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosDispatchBoundaryTests
{
    private static readonly RepoRef Repo = new("acme", "ios");
    private static readonly IosNewTestsRequest Request = new(RemoteIosGateTests.Base, RemoteIosGateTests.Head, ["TallyTests/TallyTests/testNew"]);

    [Fact]
    public async Task Definitely_unsent_dispatch_clears_durable_state_without_remote_reconciliation()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-unsent-").FullName;
        try
        {
            var github = new Dispatch();
            await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            Assert.Equal((2, 0), (github.Attempts, github.RunReads));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task A_POST_that_may_have_started_retains_the_durable_no_overlap_barrier()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-post-").FullName;
        try
        {
            var github = new Dispatch { MayHaveSent = true };
            await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            Assert.Equal(1, github.Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Definitely_unsent_cancellation_clears_the_journal_and_preserves_caller_cancellation()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-unsent-stop-").FullName;
        try
        {
            using var stop = new CancellationTokenSource();
            var github = new Dispatch { Stop = stop };
            var error = await Record.ExceptionAsync(() => Remote(github, directory).RunAsync(Repo, Request, stop.Token));
            Assert.Equal((true, 0), (error is OperationCanceledException, Directory.GetFiles(directory, "*.json").Length));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Failure_to_clear_a_definitely_unsent_journal_remains_fail_closed_and_code_written()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-unsent-clear-").FullName;
        try
        {
            var github = new Dispatch { BlockClearDirectory = directory };
            var first = await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            var second = await Remote(github, directory).RunAsync(Repo, Request, CancellationToken.None);
            Assert.Equal((1, true, true), (github.Attempts,
                first.Reason.StartsWith("remote-journal-cleanup-failed:", StringComparison.Ordinal),
                second.Reason.StartsWith("remote-journal-invalid:", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static RemoteIosNewTests Remote(Dispatch github, string directory) => new(github, directory)
    {
        Timeout = TimeSpan.FromSeconds(5), CleanupTimeout = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(1)
    };

    private sealed class Dispatch : IIosWorkflowGitHub
    {
        public int Attempts, RunReads;
        public bool MayHaveSent;
        public CancellationTokenSource? Stop;
        public string? BlockClearDirectory;
        public Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct) =>
            Task.FromResult<IosSealedWorkflow?>(new("main", RemoteIosGateTests.Workflow, RemoteIosWorkflow.Text));
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            Attempts++;
            if (BlockClearDirectory is not null)
            {
                var journal = Directory.GetFiles(BlockClearDirectory, "*.json").Single();
                var contents = File.ReadAllText(journal);
                File.Delete(journal);
                Directory.CreateDirectory(journal);
                File.WriteAllText(Path.Combine(journal, "reservation"), contents);
            }
            Stop?.Cancel();
            if (MayHaveSent) throw new HttpRequestException("arbitrary lost POST response");
            throw new IosWorkflowNotSentException("The gate App could not authorize dispatch; no workflow was sent.", Stop is not null);
        }
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
        {
            RunReads++;
            return Task.FromResult<IReadOnlyList<IosWorkflowRun>>([]);
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct) => Task.CompletedTask;
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct) => throw new InvalidOperationException();
    }
}
