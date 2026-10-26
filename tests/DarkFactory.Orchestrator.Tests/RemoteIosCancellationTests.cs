using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RemoteIosCancellationTests
{
    private static readonly RepoRef Repo = new("acme", "ios");
    private static readonly IosNewTestsRequest Request = new(RemoteIosGateTests.Base, RemoteIosGateTests.Head, ["TallyTests/TallyTests/testNew"]);

    [Fact]
    public async Task Pause_before_correlation_is_visible_cancels_with_independent_token_and_confirms_terminal()
    {
        using var pause = new CancellationTokenSource();
        var github = new Workflow { Interrupt = pause, HiddenReads = 2 };
        try { await Remote(github).RunAsync(Repo, Request, pause.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal((1, true, true), (github.Cancels, github.CleanupTokenIndependent, github.TerminalObserved));
    }

    [Fact]
    public async Task Pause_preserves_caller_cancellation_after_remote_cleanup()
    {
        using var pause = new CancellationTokenSource();
        var github = new Workflow { Interrupt = pause };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Remote(github).RunAsync(Repo, Request, pause.Token));
    }

    [Fact]
    public async Task Deadline_cancels_the_remote_run_before_returning_timeout_evidence()
    {
        var github = new Workflow();
        var result = await Remote(github, TimeSpan.FromMilliseconds(200)).RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal(("error", 1, true), (result.Outcome, github.Cancels, github.TerminalObserved));
    }

    [Fact]
    public async Task An_uncertain_invisible_dispatch_blocks_a_replacement_in_a_new_process_instance()
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-journal-").FullName;
        try
        {
            using var stop = new CancellationTokenSource();
            var github = new Workflow { Interrupt = stop, HiddenReads = int.MaxValue };
            try { await Remote(github, directory: directory).RunAsync(Repo, Request, stop.Token); }
            catch (OperationCanceledException) { }
            github.Interrupt = null;
            var result = await Remote(github, directory: directory).RunAsync(Repo, Request, CancellationToken.None);
            Assert.Equal((1, true), (github.Dispatches, result.Reason.Contains("remote-cancel-uncertain", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Polling_uses_the_deadline_not_the_old_180_poll_limit()
    {
        var github = new Workflow { CompleteAfterReads = 182 };
        var result = await Remote(github, poll: TimeSpan.Zero).RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal("pass", result.Outcome);
        Assert.Equal(182, github.Reads);
    }

    [Fact]
    public async Task The_hosted_budget_excludes_time_spent_reading_the_trusted_workflow()
    {
        var github = new Workflow { WorkflowDelay = TimeSpan.FromMilliseconds(40), CompleteAfterReads = 1 };
        await Remote(github, TimeSpan.FromSeconds(1)).RunAsync(Repo, Request, CancellationToken.None);
        Assert.InRange(github.Budget, 0.01, 0.98);
    }

    [Fact]
    public async Task A_correlation_with_wrong_provenance_is_never_cancelled_and_remains_a_barrier()
    {
        var github = new Workflow { WrongProvenance = true };
        var remote = Remote(github);
        await remote.RunAsync(Repo, Request, CancellationToken.None);
        await remote.RunAsync(Repo, Request, CancellationToken.None);
        Assert.Equal((0, 1), (github.Cancels, github.Dispatches));
    }

    private static RemoteIosNewTests Remote(Workflow github, TimeSpan? timeout = null, string? directory = null, TimeSpan? poll = null) => new(github, directory)
    {
        PollInterval = poll ?? TimeSpan.FromMilliseconds(1), Timeout = timeout ?? TimeSpan.FromSeconds(3), CleanupTimeout = TimeSpan.FromMilliseconds(500)
    };

    private sealed class Workflow : IIosWorkflowGitHub
    {
        public CancellationTokenSource? Interrupt;
        public int HiddenReads, CompleteAfterReads = int.MaxValue, Dispatches, Cancels;
        public bool CleanupTokenIndependent, TerminalObserved, WrongProvenance;
        public double Budget;
        public TimeSpan WorkflowDelay;
        private string nonce = "";
        private int reads;
        public int Reads => reads;
        private bool cancelled;
        public async Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct)
        {
            await Task.Delay(WorkflowDelay, ct);
            return new("main", RemoteIosGateTests.Workflow, RemoteIosWorkflow.Text);
        }
        public Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct)
        {
            nonce = correlation; Budget = request.ExecutionBudgetSeconds; Dispatches++;
            Interrupt?.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            reads++;
            if (reads <= HiddenReads) return Task.FromResult<IReadOnlyList<IosWorkflowRun>>([]);
            var completed = cancelled || reads >= CompleteAfterReads;
            if (cancelled) TerminalObserved = true;
            return Task.FromResult<IReadOnlyList<IosWorkflowRun>>([new(42, "factory-ios-" + nonce,
                WrongProvenance ? RemoteIosGateTests.Head : RemoteIosGateTests.Workflow, "workflow_dispatch", RemoteIosWorkflow.Path, 1,
                completed ? "completed" : "in_progress", cancelled ? "cancelled" : completed ? "success" : null)]);
        }
        public Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct)
        {
            CleanupTokenIndependent = !ct.IsCancellationRequested && ct.CanBeCanceled;
            Cancels++; cancelled = true;
            return Task.CompletedTask;
        }
        public Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct) => Task.FromResult(
            RemoteIosNewTestsTests.WithEnvironment(System.Text.Json.JsonSerializer.Serialize(new {
                version = 1, nonce = correlation, @base = Request.Base, head = Request.Head,
                tests = new[] { new { test = Request.Tests[0], @base = "failed", head = "passed" } }
            })));
    }
}
