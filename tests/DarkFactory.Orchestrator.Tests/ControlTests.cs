using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Pause, Continue and Stop at item, epic and factory scope, through the real pipeline over an in-memory ledger
/// with a worker that works in tool calls (the end-to-end version with a real process is <see cref="ControlProcessTests"/>).
/// </summary>
public class ControlTests
{
    private const string Session = "sess-ctl";
    private static readonly WorkStory Story = new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");

    /// <summary>
    /// Works in tool calls until its run is cancelled or, honouring a pause, until the call after the pause request
    /// (as the PreToolUse hook does: the session then ends as a success). A resumed session finishes at once.
    /// </summary>
    private sealed class ToolWorker(bool honoursPause = true) : IWorker
    {
        private int _pause;
        private int _tools;
        public List<string?> Resumes { get; } = [];
        public TaskCompletionSource Working { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Tools => Volatile.Read(ref _tools);
        public bool Cancelled { get; private set; }

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, WorkerCallbacks? callbacks, CancellationToken ct)
        {
            Resumes.Add(resumeSessionId);
            await callbacks!.OnStarted!(WorkerPid, ct);
            await callbacks.OnSession!(Session, ct);
            if (resumeSessionId is not null)
            {
                return new WorkerResult(Session, 0, false, "success", "done", "");
            }
            Working.TrySetResult();
            try
            {
                while (true)
                {
                    if (honoursPause && Volatile.Read(ref _pause) == 1)
                    {
                        return new WorkerResult(Session, 0, false, "success", "", ""); // hook_stopped
                    }
                    await Task.Delay(5, ct); // one tool call
                    Interlocked.Increment(ref _tools);
                }
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);

        public void RequestPause(string workingDirectory) => Volatile.Write(ref _pause, 1);
    }

    private sealed class Harness
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public Harness() => Db = new LedgerDbContext(_options);

        public LedgerDbContext Db { get; }
        public LedgerDbContextFactory Contexts => new(_options);
        public IControls Controls => new LedgerControls(Contexts, TimeProvider.System);
        public FakeWorkSource Stories { get; init; } = new(Story);
        public FakeWorkspaces Workspaces { get; } = new();
        public FakePullRequests Prs { get; init; } = new();
        public InProcessRunLocks Locks { get; } = new();
        public WorkLedger Ledger => new(Db, TimeProvider.System);
        public TimeSpan PauseGrace { get; init; } = TimeSpan.FromSeconds(30);

        public RunPipeline Pipeline(IWorker worker) =>
            new(Stories, Ledger, Locks, Workspaces, worker, Prs, RunPipelineTests.Sandbox, TextWriter.Null,
                controls: Controls, pauseGrace: PauseGrace, controlPollInterval: TimeSpan.FromMilliseconds(10));

        public Task<RunOutcome> Run(IWorker worker, int story = 77) => Pipeline(worker).RunAsync(story, CancellationToken.None);

        public ControlActions Actions(bool stops = true) =>
            new(Controls, Contexts, stops ? new Stops(this) : null);

        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        public async Task<List<(WorkState, string?)>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => (r.State, r.Detail)).ToList();
        public async Task<WorkItem> Item() => await Db.WorkItems.AsNoTracking().SingleAsync();

        public Task<IReadOnlyList<int>> InFlight() => RunPipeline.InFlightAsync(Ledger, CancellationToken.None, Controls);

        private sealed class Stops(Harness h) : IItemStops
        {
            public Task<string> StopAsync(int storyId, CancellationToken ct) =>
                new ItemStopper(h.Stories, new WorkLedger(new LedgerDbContext(h._options), TimeProvider.System), h.Locks, h.Prs, h.Controls, TextWriter.Null)
                    .StopAsync(storyId, ct);
        }
    }

    private static async Task<RunOutcome> PauseWhileWorking(Harness h, ToolWorker worker, string scope)
    {
        var run = h.Run(worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Actions().PauseAsync(scope, "tester", CancellationToken.None);
        return await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("item:sc-77")]
    [InlineData("epic:5")]
    [InlineData("factory")]
    public async Task Pause_during_implement_stops_the_worker_at_its_next_tool_call_and_continue_resumes_the_same_session(string scope)
    {
        var h = new Harness { Stories = new(Story) { Epic = new WorkEpic(5, "Phase 1", null, "https://app.shortcut.com/trefry/epic/5") } };
        var worker = new ToolWorker();

        var paused = await PauseWhileWorking(h, worker, scope);

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.False(worker.Cancelled); // it stopped on its own, at a tool boundary
        Assert.Equal((WorkState.Paused, RunPipeline.UserPaused), (await h.Transitions())[^1]);
        var steps = (await h.Rows()).Where(r => r.Step is not null).Select(r => r.Step).ToList();
        Assert.DoesNotContain(RunPipeline.Steps.WorkerDone, steps); // the story is not done: nothing is pushed
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("remove") || c.StartsWith("push")); // worktree kept
        Assert.Empty(await h.InFlight()); // a user's pause is not auto-resumed
        Assert.Equal(5, (await h.Item()).EpicId);

        // `factory run` while paused changes nothing.
        var held = await h.Run(worker);
        Assert.Contains("paused by a control", held.Error);
        Assert.Single(worker.Resumes);

        await h.Actions().ContinueAsync(scope, "tester", CancellationToken.None);
        Assert.Equal([77], await h.InFlight());
        var resumed = await h.Run(worker);

        Assert.True(resumed.Succeeded, resumed.Error);
        Assert.Equal([null, Session], worker.Resumes); // the same Claude session, resumed
        Assert.Equal(
            [WorkState.Intake, WorkState.Implement, WorkState.Paused, WorkState.Implement, WorkState.Review],
            (await h.Transitions()).Select(t => t.Item1));
        Assert.Single(h.Prs.Opened);
    }

    [Fact]
    public async Task A_worker_that_ignores_the_pause_is_stopped_after_the_grace_and_keeps_its_session_and_worktree()
    {
        var h = new Harness { PauseGrace = TimeSpan.FromMilliseconds(200) };
        var worker = new ToolWorker(honoursPause: false);

        var paused = await PauseWhileWorking(h, worker, ControlScope.Item("sc-77"));

        Assert.Equal(WorkState.Paused, paused.State);
        Assert.True(worker.Cancelled);
        Assert.Equal((WorkState.Paused, RunPipeline.UserPaused), (await h.Transitions())[^1]);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("remove"));

        await h.Actions().ContinueAsync(ControlScope.Item("sc-77"), "tester", CancellationToken.None);
        Assert.True((await h.Run(worker)).Succeeded);
        Assert.Equal([null, Session], worker.Resumes);
    }

    [Fact]
    public async Task Stop_during_implement_kills_the_worker_cancels_the_item_and_tells_the_story()
    {
        var h = new Harness();
        var worker = new ToolWorker();
        var run = h.Run(worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await h.Actions().StopAsync(ControlScope.Item("sc-77"), "tester", CancellationToken.None);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("is running; its run stops it", result);
        Assert.True(worker.Cancelled); // killed, not asked
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Equal((WorkState.Cancelled, "stopped by tester"), (await h.Transitions())[^1]);
        Assert.Equal(["factory/sc-77"], h.Prs.Drafted);
        Assert.Contains("state 77 Stopped", h.Stories.Writes);
        Assert.Contains("remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77", h.Workspaces.Calls);
        Assert.Null(await h.Controls.GetAsync(ControlScope.Item("sc-77"), CancellationToken.None));
        Assert.Empty(await h.InFlight());
    }

    [Fact]
    public async Task Stop_of_an_idle_item_drafts_its_pr_moves_the_story_to_the_backlog_and_cancels_it()
    {
        var h = new Harness();
        Assert.True((await h.Run(new FakeWorker(Reports(Ok)))).Succeeded); // parked at Review with its PR open
        var workspaceCalls = h.Workspaces.Calls.Count;

        var result = await h.Actions().StopAsync(ControlScope.Item("sc-77"), "tester", CancellationToken.None);

        Assert.Contains("sc-77 stopped", result);
        Assert.Equal(WorkState.Cancelled, (await h.Item()).State);
        var rows = await h.Rows();
        Assert.Equal(PrUrl, rows.Single(r => r.Step == RunPipeline.Steps.PrsDrafted).Detail);
        Assert.Equal(["factory/sc-77"], h.Prs.Drafted);
        Assert.Equal("state 77 Stopped", h.Stories.Writes[^1]); // Backlog, claim released, comment
        Assert.Equal(workspaceCalls, h.Workspaces.Calls.Count); // the branch is not touched
        var comment = Assert.Single(h.Stories.Comments);
        Assert.StartsWith("[author: dark-factory] sc-77 was stopped by tester", comment);
        Assert.Contains($"Its pull request ({PrUrl}) is a draft again. Branch factory/sc-77 is kept; nothing was merged or deleted.", comment);
        Assert.Null(await h.Controls.GetAsync(ControlScope.Item("sc-77"), CancellationToken.None));
    }

    [Fact]
    public async Task A_stop_that_fails_stays_stopping_and_the_next_poll_finishes_it_without_repeating_steps()
    {
        var failing = new FakePullRequests(new HttpRequestException("GitHub down"));
        var h = new Harness { Prs = failing };
        var item = await h.Ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, RunPipelineTests.Sandbox.FullName, null, CancellationToken.None);
        await h.Ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);

        var result = await h.Actions().StopAsync(ControlScope.Item("sc-77"), "tester", CancellationToken.None);

        Assert.Contains("stop NOT finished (GitHub down)", result);
        Assert.Equal(WorkState.Implement, (await h.Item()).State);
        Assert.Equal(ControlState.Stopping, (await h.Controls.GetAsync(ControlScope.Item("sc-77"), CancellationToken.None))!.State);
        Assert.Equal([77], await h.InFlight()); // the intake loop retries it

        // GitHub is back: the next poll's run of the item finishes the stop instead of running it.
        var outcome = await new RunPipeline(h.Stories, h.Ledger, h.Locks, h.Workspaces, new ToolWorker(), new FakePullRequests(), RunPipelineTests.Sandbox,
            TextWriter.Null, controls: h.Controls).RunAsync(77, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Single(h.Stories.Writes, w => w == "state 77 Stopped");
        Assert.Contains("reopen michaeltrefry/dark-factory-sandbox factory/sc-77", h.Workspaces.Calls);
        Assert.Contains("remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77", h.Workspaces.Calls);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("prepare") || c.StartsWith("push"));
    }

    [Fact]
    public async Task Stopping_an_epic_or_the_factory_stops_each_of_its_active_items()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        foreach (var (id, epic) in new[] { (1, 5L), (2, 5L), (3, 6L) })
        {
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, $"sc-{id}", "s", RunPipelineTests.Sandbox.FullName, null, CancellationToken.None, epic);
            await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        }

        await h.Actions().StopAsync(ControlScope.Epic(5), "tester", CancellationToken.None);
        var states = await h.Db.WorkItems.AsNoTracking().OrderBy(i => i.Id).Select(i => i.State).ToListAsync();
        Assert.Equal([WorkState.Cancelled, WorkState.Cancelled, WorkState.Implement], states);

        await h.Actions().StopAsync(ControlScope.Factory, "tester", CancellationToken.None);
        Assert.All(await h.Db.WorkItems.AsNoTracking().ToListAsync(), i => Assert.Equal(WorkState.Cancelled, i.State));
        Assert.Equal(["state 1 Stopped", "state 2 Stopped", "state 3 Stopped"], h.Stories.Writes);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("epic:5")]
    public async Task A_paused_factory_or_epic_claims_no_new_story(string scope)
    {
        var h = new Harness { Stories = new(Story) { Epic = new WorkEpic(5, "Phase 1", null, "https://app.shortcut.com/trefry/epic/5") } };
        await h.Actions().PauseAsync(scope, "tester", CancellationToken.None);
        var worker = new FakeWorker(Reports(Ok));

        var outcome = await h.Run(worker);

        Assert.Contains("not claimed", outcome.Error);
        Assert.Empty(h.Stories.Writes);
        Assert.Empty(await h.Db.WorkItems.ToListAsync());
        Assert.Empty(worker.Calls);

        await h.Actions().ContinueAsync(scope, "tester", CancellationToken.None);
        Assert.True((await h.Run(worker)).Succeeded);
        Assert.Equal("claim 77", h.Stories.Writes[0]);
    }

    [Fact]
    public async Task While_the_factory_is_paused_a_new_to_do_story_in_scope_stays_unclaimed()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var h = new Harness();
        var runner = new Runner(h, source);
        var loop = new IntakeLoop(source, runner, new IntakeOptions(TimeSpan.FromMinutes(1)), TimeProvider.System, NullLogger<IntakeLoop>.Instance, h.Controls);
        await h.Actions().PauseAsync(ControlScope.Factory, "tester", CancellationToken.None);

        await loop.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, runner.Runs);
        Assert.Equal(FakeShortcutBoard.ToDo, (long)board.Story(101)["workflow_state_id"]!);
        Assert.Empty(((JsonArray)board.Story(101)["owner_ids"]!));
        Assert.DoesNotContain(board.Requests, r => r.Method == HttpMethod.Put);

        await h.Actions().ContinueAsync(ControlScope.Factory, "tester", CancellationToken.None);
        await loop.PollOnceAsync(CancellationToken.None);

        Assert.Equal(FakeShortcutBoard.InProgress, (long)board.Story(101)["workflow_state_id"]!);
    }

    private sealed class Runner(Harness h, IWorkSource source) : IItemRunner
    {
        public int Runs;

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => h.InFlight();

        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return new RunPipeline(source, h.Ledger, h.Locks, h.Workspaces, new FakeWorker(Reports(Ok)), h.Prs, RunPipelineTests.Sandbox, TextWriter.Null,
                controls: h.Controls).RunAsync(id, ct);
        }
    }

    [Fact]
    public async Task Continue_does_not_undo_a_stop_in_progress()
    {
        var h = new Harness();
        await h.Controls.SetAsync(ControlScope.Item("sc-77"), ControlState.Stopping, "tester", CancellationToken.None);

        Assert.Contains("cannot be continued", await h.Actions().ContinueAsync(ControlScope.Item("sc-77"), "tester", CancellationToken.None));
        Assert.Equal(ControlState.Stopping, (await h.Controls.GetAsync(ControlScope.Item("sc-77"), CancellationToken.None))!.State);
    }

    [Theory]
    [InlineData("factory", true)]
    [InlineData("epic:5", true)]
    [InlineData("item:sc-7", true)]
    [InlineData("item:7", true)]
    [InlineData("epic:0", false)]
    [InlineData("epic:x", false)]
    [InlineData("item:nope", false)]
    [InlineData("everything", false)]
    public void Scopes_are_validated(string scope, bool valid) => Assert.Equal(valid, ControlScope.IsValid(scope));
}
