using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public class WorkLedgerTests
{
    private static async Task<(LedgerDbContext Db, WorkLedger Ledger, WorkItem Item)> NewItem(TimeProvider? time = null)
    {
        var db = TestDb.Create();
        var ledger = new WorkLedger(db, time ?? TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", "bug: url", CancellationToken.None);
        return (db, ledger, item);
    }

    [Fact]
    public async Task New_item_starts_with_an_intake_row_and_transitions_are_timestamped_rows()
    {
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-06T10:00:00Z"));
        var (db, ledger, item) = await NewItem(time);
        time.Now = time.Now.AddMinutes(5);
        await ledger.RecordAsync(item, WorkState.Implement, "sess-1", "done", CancellationToken.None);

        var rows = await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal([WorkState.Intake, WorkState.Implement], rows.Select(r => r.State));
        Assert.Equal("bug: url", rows[0].Detail);
        Assert.Equal((item.Id, "sess-1", "done"), (rows[1].WorkItemId, rows[1].ClaudeSessionId, rows[1].Detail));
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T10:05:00Z"), rows[1].RecordedAt);
        Assert.Equal(WorkState.Implement, (await db.WorkItems.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Same_story_reuses_its_work_item()
    {
        var (db, ledger, first) = await NewItem();
        var second = await ledger.GetOrCreateAsync("shortcut", "sc-1", "New", "o/r", "x", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("New", (await db.WorkItems.SingleAsync()).Title);
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Illegal_transition_is_rejected_and_writes_nothing()
    {
        var (db, ledger, item) = await NewItem();

        var ex = await Assert.ThrowsAsync<IllegalTransitionException>(
            () => ledger.RecordAsync(item, WorkState.Merge, "s", "skip ahead", CancellationToken.None));

        Assert.Equal((WorkState.Intake, WorkState.Merge), (ex.From, ex.To));
        Assert.Equal(WorkState.Intake, item.State);
        Assert.Equal(WorkState.Intake, (await db.WorkItems.AsNoTracking().SingleAsync()).State);
        Assert.Equal([WorkState.Intake], await db.LedgerEntries.AsNoTracking().Select(e => e.State).ToListAsync());
    }

    [Fact]
    public async Task Paused_item_returns_only_to_the_state_it_paused_from()
    {
        var (_, ledger, item) = await NewItem();
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Paused, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<IllegalTransitionException>(() => ledger.RecordAsync(item, WorkState.Review, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<IllegalTransitionException>(() => ledger.RecordAsync(item, WorkState.Intake, null, null, CancellationToken.None));
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        Assert.Equal(WorkState.Implement, item.State);
    }

    [Fact]
    public async Task Fix_rounds_are_counted_from_the_ledger_and_the_fourth_is_rejected()
    {
        var (_, ledger, item) = await NewItem();
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Review, null, null, CancellationToken.None);
        for (var round = 0; round < Lifecycle.MaxFixRounds; round++)
        {
            await ledger.RecordAsync(item, WorkState.Fixing, null, null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Paused, null, null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Fixing, null, null, CancellationToken.None); // back from pause
            await ledger.RecordAsync(item, WorkState.Review, null, null, CancellationToken.None);
        }

        await Assert.ThrowsAsync<IllegalTransitionException>(() => ledger.RecordAsync(item, WorkState.Fixing, null, null, CancellationToken.None));
        await ledger.RecordAsync(item, WorkState.Escalated, null, "3 fix rounds", CancellationToken.None);
    }

    [Fact]
    public async Task Checkpoint_records_a_step_without_changing_state()
    {
        var (db, ledger, item) = await NewItem();
        await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Session, "sess-9", null, CancellationToken.None);

        var last = await db.LedgerEntries.OrderBy(e => e.Id).LastAsync();
        Assert.Equal((WorkState.Implement, "session", "sess-9"), (last.State, last.Step, last.ClaudeSessionId));
        Assert.Equal(WorkState.Implement, item.State);
    }
}

public class RunPipelineTests
{
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private static readonly ShortcutStory Story =
        new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");
    private const string PrUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/pull/1";

    private sealed class FakeStories(ShortcutStory story, bool commentFails = false) : IStorySource
    {
        public List<string> Comments { get; } = [];
        public Task<ShortcutStory> GetStoryAsync(int id, CancellationToken ct) => Task.FromResult(story with { Id = id });
        public Task AddCommentAsync(int id, string text, CancellationToken ct)
        {
            if (commentFails)
            {
                throw new InvalidOperationException("Shortcut down");
            }
            Comments.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaces(bool hasChanges = true, bool worktreeExists = true) : IRepoWorkspace
    {
        public List<string> Calls { get; } = [];
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"prepare {repo} {branch}");
            return Task.FromResult(new Workspace($"/wt/{branch}", branch, "main"));
        }
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"reopen {repo} {branch}");
            return Task.FromResult(worktreeExists ? new Workspace($"/wt/{branch}", branch, "main") : null);
        }
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct)
        {
            Calls.Add($"push {repo} {workspace.Branch} {message}");
            return Task.FromResult(hasChanges);
        }
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            Calls.Add($"remove {repo} {workspace.Path}");
            return Task.CompletedTask;
        }
    }

    private sealed record WorkerCall(string Prompt, string? Resume, Func<string, CancellationToken, Task>? OnSession);

    private sealed class FakeWorker(params Func<WorkerCall, Task<WorkerResult>>[] behaviours) : IWorker
    {
        public List<WorkerCall> Calls { get; } = [];
        public Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
            Func<string, CancellationToken, Task>? onSession, CancellationToken ct)
        {
            var call = new WorkerCall(prompt, resumeSessionId, onSession);
            Calls.Add(call);
            return behaviours[Calls.Count - 1](call);
        }
    }

    private static Func<WorkerCall, Task<WorkerResult>> Reports(WorkerResult result) => async call =>
    {
        if (result.SessionId is not null)
        {
            await call.OnSession!(result.SessionId, CancellationToken.None);
        }
        return result;
    };

    private static Func<WorkerCall, Task<WorkerResult>> StartsThenThrows(string session, Exception ex) => async call =>
    {
        await call.OnSession!(session, CancellationToken.None);
        throw ex;
    };

    private sealed class FakePullRequests : IPullRequests
    {
        public List<(RepoRef Repo, string Head, string Base, string Title, string Body)> Opened { get; } = [];
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            Opened.Add((repo, head, baseBranch, title, body));
            return Task.FromResult(PrUrl);
        }
    }

    private static readonly WorkerResult Ok = new("sess-77", 0, false, "success", "done", "");

    private sealed class Harness
    {
        public LedgerDbContext Db { get; } = TestDb.Create();
        public FakeStories Stories { get; init; } = new(Story);
        public FakeWorkspaces Workspaces { get; init; } = new();
        public FakePullRequests Prs { get; } = new();
        public WorkLedger Ledger => new(Db, TimeProvider.System);

        public Task<RunOutcome> Run(FakeWorker worker, CancellationToken ct = default) =>
            new RunPipeline(Stories, Ledger, Workspaces, worker, Prs, Sandbox, TextWriter.Null).RunAsync(77, ct);

        public async Task<WorkItem> Item() => await Db.WorkItems.SingleAsync();
        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        public async Task<List<WorkState>> Transitions() => (await Rows()).Where(r => r.Step is null).Select(r => r.State).ToList();
        public async Task<List<string?>> Steps() => (await Rows()).Where(r => r.Step is not null).Select(r => r.Step).ToList();

        /// <summary>Puts the story in the ledger as an earlier, crashed run left it.</summary>
        public async Task<WorkItem> Crashed(params string[] steps)
        {
            var ledger = Ledger;
            var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
            foreach (var step in steps)
            {
                await ledger.CheckpointAsync(item, step, "sess-77", null, CancellationToken.None);
            }
            return item;
        }
    }

    [Fact]
    public async Task Successful_run_transitions_intake_implement_review_and_parks_with_the_pr_open()
    {
        var h = new Harness();
        var worker = new FakeWorker(Reports(Ok));

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded);
        Assert.Equal((WorkState.Review, PrUrl, "sess-77"), (outcome.State, outcome.PullRequestUrl, outcome.SessionId));
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], await h.Transitions());
        Assert.Equal(["session", "worker-done", "pushed"], await h.Steps());
        var rows = await h.Rows();
        Assert.Equal(("sess-77", PrUrl), (rows[^1].ClaudeSessionId, rows[^1].Detail));
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.RecordedAt <= p.Second.RecordedAt));

        Assert.Null(worker.Calls.Single().Resume);
        Assert.Equal(["prepare michaeltrefry/dark-factory-sandbox factory/sc-77",
            "push michaeltrefry/dark-factory-sandbox factory/sc-77 sc-77: Whitespace counts as a word"], h.Workspaces.Calls);
        var pr = h.Prs.Opened.Single();
        Assert.Equal(("factory/sc-77", "main"), (pr.Head, pr.Base));
        Assert.Contains("https://app.shortcut.com/trefry/story/77", pr.Body);
        Assert.Empty(h.Stories.Comments);
    }

    [Fact]
    public async Task Session_id_is_in_the_ledger_before_the_worker_finishes()
    {
        var h = new Harness();
        List<LedgerEntry>? rowsWhileRunning = null;
        var worker = new FakeWorker(async call =>
        {
            await call.OnSession!("sess-77", CancellationToken.None);
            rowsWhileRunning = await h.Rows();
            return Ok;
        });

        await h.Run(worker);

        Assert.Equal(("session", "sess-77"), (rowsWhileRunning![^1].Step, rowsWhileRunning[^1].ClaudeSessionId));
    }

    [Fact]
    public async Task Repo_line_in_story_redirects_the_run()
    {
        var h = new Harness { Stories = new(Story with { Description = "Repo: acme/widgets\nfix" }) };
        await h.Run(new FakeWorker(Reports(Ok)));
        Assert.StartsWith("prepare acme/widgets", h.Workspaces.Calls[0]);
        Assert.Equal(new RepoRef("acme", "widgets"), h.Prs.Opened.Single().Repo);
    }

    [Fact]
    public async Task Failed_worker_escalates_and_posts_one_comment_with_reason_and_last_ledger_state()
    {
        var h = new Harness();
        var outcome = await h.Run(new FakeWorker(Reports(new WorkerResult("sess-x", 1, true, "error_during_execution", "Not logged in", "boom"))));

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated], await h.Transitions());
        var escalated = (await h.Rows()).Last(r => r.Step is null);
        Assert.Equal("sess-x", escalated.ClaudeSessionId);
        Assert.Contains("Not logged in", escalated.Detail);

        var comment = Assert.Single(h.Stories.Comments);
        Assert.StartsWith("[author: dark-factory]", comment);
        Assert.Contains("exit 1", comment);
        Assert.Contains("Not logged in", comment);
        Assert.Contains("Last ledger state: Implement (after step session)", comment);
        Assert.Contains("sess-x", comment);
        Assert.Equal("escalation-comment", (await h.Rows())[^1].Step);
        Assert.Single(h.Workspaces.Calls);
        Assert.Empty(h.Prs.Opened);
        Assert.Equal(WorkState.Escalated, (await h.Item()).State);
    }

    [Fact]
    public async Task Escalation_comment_failure_is_recorded_not_swallowed()
    {
        var h = new Harness { Stories = new(Story, commentFails: true) };
        await h.Run(new FakeWorker(Reports(new WorkerResult("sess-x", 2, true, null, null, ""))));

        var last = (await h.Rows())[^1];
        Assert.Equal(("escalation-comment", WorkState.Escalated), (last.Step, last.State));
        Assert.Contains("Shortcut down", last.Detail);
    }

    [Fact]
    public async Task No_changes_escalates_and_opens_no_pr()
    {
        var h = new Harness { Workspaces = new(hasChanges: false) };
        var outcome = await h.Run(new FakeWorker(Reports(Ok)));

        Assert.False(outcome.Succeeded);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated], await h.Transitions());
        Assert.Contains("Last ledger state: Implement (after step worker-done)", Assert.Single(h.Stories.Comments));
        Assert.Empty(h.Prs.Opened);
    }

    [Fact]
    public async Task Interrupt_pauses_keeps_the_worktree_and_a_rerun_resumes_the_same_session()
    {
        var h = new Harness();
        var worker = new FakeWorker(StartsThenThrows("sess-77", new OperationCanceledException()), Reports(Ok));

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Run(worker));
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Paused], await h.Transitions());
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("remove"));

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Paused, WorkState.Implement, WorkState.Review], await h.Transitions());
        Assert.Equal("sess-77", worker.Calls[1].Resume);
        Assert.Equal(RunPipeline.BuildResumePrompt(Story), worker.Calls[1].Prompt);
        Assert.Contains("reopen michaeltrefry/dark-factory-sandbox factory/sc-77", h.Workspaces.Calls);
        Assert.Single(h.Prs.Opened);
    }

    [Fact]
    public async Task Crash_during_worker_resumes_implement_with_the_recorded_session_in_the_same_worktree()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.Session);
        var worker = new FakeWorker(Reports(Ok));

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal("sess-77", worker.Calls.Single().Resume);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("prepare"));
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], await h.Transitions());
        Assert.Single(h.Prs.Opened);
    }

    [Fact]
    public async Task Crash_after_worker_finished_does_not_rerun_the_worker()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone);
        var worker = new FakeWorker();

        await h.Run(worker);

        Assert.Empty(worker.Calls);
        Assert.Contains(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Equal(WorkState.Review, (await h.Item()).State);
    }

    [Fact]
    public async Task Crash_after_push_only_opens_the_pr()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone, RunPipeline.Steps.Pushed);
        var worker = new FakeWorker();

        await h.Run(worker);

        Assert.Empty(worker.Calls);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Single(h.Prs.Opened);
    }

    [Fact]
    public async Task Lost_worktree_on_resume_restarts_implement_with_a_fresh_session()
    {
        var h = new Harness { Workspaces = new(worktreeExists: false) };
        await h.Crashed(RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone);
        var worker = new FakeWorker(Reports(Ok with { SessionId = "sess-new" }));

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Null(worker.Calls.Single().Resume);
        Assert.Contains("worktree-lost", await h.Steps());
        Assert.Equal("sess-new", outcome.SessionId);
    }

    [Fact]
    public async Task Rerun_of_a_parked_item_redoes_nothing()
    {
        var h = new Harness();
        await h.Run(new FakeWorker(Reports(Ok)));
        var rows = (await h.Rows()).Count;
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded);
        Assert.Equal((WorkState.Review, PrUrl), (outcome.State, outcome.PullRequestUrl));
        Assert.Empty(worker.Calls);
        Assert.Single(h.Prs.Opened);
        Assert.Equal(rows, (await h.Rows()).Count);
    }

    [Fact]
    public async Task Rerun_of_an_escalated_item_starts_again_from_intake()
    {
        var h = new Harness();
        var worker = new FakeWorker(Reports(new WorkerResult("sess-x", 1, true, null, "no", "")), Reports(Ok));
        await h.Run(worker);

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated, WorkState.Intake, WorkState.Implement, WorkState.Review],
            await h.Transitions());
        Assert.Null(worker.Calls[1].Resume);
        Assert.Equal(2, h.Workspaces.Calls.Count(c => c.StartsWith("prepare")));
    }

    [Fact]
    public async Task Cancelled_item_is_left_alone()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Cancelled, null, "owner cancelled", CancellationToken.None);
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Cancelled, outcome.State);
        Assert.Empty(worker.Calls);
        Assert.Empty(h.Workspaces.Calls);
    }
}
