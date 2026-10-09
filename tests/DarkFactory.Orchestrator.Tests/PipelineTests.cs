using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    public async Task Same_story_reuses_its_work_item_and_refresh_updates_its_title()
    {
        var (db, ledger, first) = await NewItem();
        var second = await ledger.GetOrCreateAsync("shortcut", "sc-1", "New", "o/r", "x", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Fix", (await db.WorkItems.AsNoTracking().SingleAsync()).Title); // nothing written before the run lock
        await ledger.RefreshAsync(second, "New", "o/r", null, CancellationToken.None);
        Assert.Equal("New", (await db.WorkItems.AsNoTracking().SingleAsync()).Title);
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    private sealed class FailNextSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("database unavailable");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Failed_save_leaves_neither_the_new_state_nor_a_tracked_row_behind()
    {
        var failing = new FailNextSave();
        var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(failing).Options);
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", null, CancellationToken.None);
        var updatedAt = item.UpdatedAt;

        failing.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.RecordAsync(item, WorkState.Implement, "s", "lost", CancellationToken.None));

        Assert.Equal((WorkState.Intake, updatedAt), (item.State, item.UpdatedAt));
        Assert.DoesNotContain(db.ChangeTracker.Entries<LedgerEntry>(), e => e.State == EntityState.Added);

        // The next write commits exactly its own row.
        await ledger.RecordAsync(item, WorkState.Implement, null, "ok", CancellationToken.None);
        Assert.Equal([(WorkState.Intake, (string?)null), (WorkState.Implement, "ok")],
            (await db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync()).Select(e => (e.State, e.Detail)));
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
    internal static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private static readonly WorkStory Story =
        new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");
    internal const string PrUrl = "https://github.com/michaeltrefry/dark-factory-sandbox/pull/1";

    internal sealed class FakeWorkspaces(bool hasChanges = true, bool worktreeExists = true) : IRepoWorkspace
    {
        public List<string> Calls { get; } = [];
        /// <summary>Where worktrees live (<c>&lt;root&gt;/&lt;branch&gt;</c>); a real directory only when a test puts files there.</summary>
        public string Root { get; init; } = "/wt";
        /// <summary>Runs while a push is in progress, e.g. to set a control then.</summary>
        public Func<Task>? OnPush { get; set; }
        /// <summary>Runs on each restore of a branch's worktree (e.g. as a fix round starts), before it returns.</summary>
        public Action? OnRestore { get; set; }
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"restore {repo} {branch}");
            OnRestore?.Invoke();
            return Task.FromResult(new Workspace($"{Root}/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}"));
        }
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"prepare {repo} {branch}");
            return Task.FromResult(new Workspace($"{Root}/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}"));
        }
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"reopen {repo} {branch}");
            return Task.FromResult(worktreeExists ? new Workspace($"{Root}/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}") : null);
        }
        /// <summary>The grant of each push: the worker sessions it publishes (E4).</summary>
        public List<PushGrant> Grants { get; } = [];
        public async Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct)
        {
            Grants.Add(grant);
            Calls.Add($"push {repo} {workspace.Branch} {message}");
            if (OnPush is not null)
            {
                await OnPush();
            }
            return hasChanges;
        }
        /// <summary>The commit a worktree's HEAD is at (what the last push pushed).</summary>
        public Func<string> Head { get; set; } = () => "head";
        public Task<string> HeadAsync(Workspace workspace, CancellationToken ct) => Task.FromResult(Head());

        /// <summary>What merging the base into a worktree does (sc-25384); by default it is already up to date.</summary>
        public Func<Workspace, BaseMerge> MergeBase { get; set; } = _ => new BaseMerge("base0", "head", [], UpToDate: true);
        /// <summary>Runs on each fast-forward push of a worktree (the merge queue's base update).</summary>
        public Func<Workspace, Task>? OnFastForward { get; set; }
        /// <summary>Which of the given files still hold a conflict marker at a commit; by default none.</summary>
        public Func<string, IReadOnlyList<string>, IReadOnlyList<string>> Markers { get; set; } = (_, _) => [];

        public Task<BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            Calls.Add($"merge-base {repo} {workspace.Branch}");
            return Task.FromResult(MergeBase(workspace));
        }

        public async Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            Calls.Add($"fast-forward {repo} {workspace.Branch}");
            if (OnFastForward is not null)
            {
                await OnFastForward(workspace);
            }
        }

        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(Markers(sha, paths));
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct)
        {
            Calls.Add($"remove {repo} {workspace.Path}");
            return Task.CompletedTask;
        }
    }

    internal sealed record WorkerCall(string Prompt, string? Resume, WorkerCallbacks Callbacks)
    {
        public Task OnSession(string sid, CancellationToken ct) => Callbacks.OnSession!(sid, ct);
    }

    internal const int WorkerPid = 4321;

    internal sealed class FakeWorker(params Func<WorkerCall, Task<WorkerResult>>[] behaviours) : IWorker
    {
        public List<WorkerCall> Calls { get; } = [];
        public List<int> OrphanStops { get; } = [];
        /// <summary>Whether a stop request finds a live orphan to stop.</summary>
        public bool OrphanAlive { get; init; }
        /// <summary>Runs before each StopOrphanAsync returns, e.g. to look at the ledger then.</summary>
        public Action? OnStopOrphan { get; init; }

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
            WorkerCallbacks? callbacks, CancellationToken ct)
        {
            var call = new WorkerCall(prompt, resumeSessionId, callbacks!);
            Calls.Add(call);
            await callbacks!.OnStarted!(WorkerPid, CancellationToken.None);
            return await behaviours[Calls.Count - 1](call);
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct)
        {
            OrphanStops.Add(pid);
            OnStopOrphan?.Invoke();
            return Task.FromResult(OrphanAlive);
        }
    }

    internal static Func<WorkerCall, Task<WorkerResult>> Reports(WorkerResult result) => async call =>
    {
        if (result.SessionId is not null)
        {
            await call.OnSession(result.SessionId, CancellationToken.None);
        }
        return result;
    };

    private static Func<WorkerCall, Task<WorkerResult>> StartsThenThrows(string session, Exception ex, Action? before = null) => async call =>
    {
        await call.OnSession(session, CancellationToken.None);
        before?.Invoke();
        throw ex;
    };

    internal sealed class FakePullRequests(Exception? throws = null) : IPullRequests
    {
        public List<(RepoRef Repo, string Head, string Base, string Title, string Body)> Opened { get; } = [];
        /// <summary>Runs while a PR is being opened (after GitHub has it), e.g. to set a control then.</summary>
        public Func<Task>? OnOpen { get; set; }
        public async Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            if (throws is not null)
            {
                throw throws;
            }
            Opened.Add((repo, head, baseBranch, title, body));
            if (OnOpen is not null)
            {
                await OnOpen();
            }
            return PrUrl;
        }

        /// <summary>Heads whose open PRs were turned back into drafts.</summary>
        public List<string> Drafted { get; } = [];

        public Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct)
        {
            if (throws is not null)
            {
                return Task.FromException<IReadOnlyList<string>>(throws);
            }
            Drafted.Add(head);
            return Task.FromResult<IReadOnlyList<string>>(Opened.Any(o => o.Head == head) ? [PrUrl] : []);
        }

        /// <summary>Each rewrite of a PR's description (sc-25389), in order.</summary>
        public List<(string Url, string Body)> BodyUpdates { get; } = [];

        /// <summary>When set, rewriting a description throws this.</summary>
        public Exception? UpdateThrows { get; set; }

        public Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct)
        {
            if (UpdateThrows is { } failure)
            {
                return Task.FromException(failure);
            }
            BodyUpdates.Add((pullUrl, body));
            return Task.CompletedTask;
        }
    }

    internal static readonly WorkerResult Ok = new("sess-77", 0, false, "success", "done", "");

    private sealed class Harness
    {
        public LedgerDbContext Db { get; } = TestDb.Create();
        public FakeWorkSource Stories { get; init; } = new(Story);
        public FakeWorkspaces Workspaces { get; init; } = new();
        public FakePullRequests Prs { get; init; } = new();
        public InProcessRunLocks Locks { get; } = new();
        public WorkLedger Ledger => new(Db, TimeProvider.System);

        public Task<RunOutcome> Run(FakeWorker worker, CancellationToken ct = default) =>
            new RunPipeline(Stories, Ledger, Locks, Workspaces, worker, Prs, Sandbox, TextWriter.Null).RunAsync(77, ct);

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
                await ledger.CheckpointAsync(item, step, "sess-77", step == RunPipeline.Steps.WorkerStarted ? "999" : null, CancellationToken.None);
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
        Assert.Equal(["claimed", "worker-started", "session", "worker-done", "pushed", "linked"], await h.Steps());
        Assert.Equal(["claim 77", "state 77 Claimed", $"link 77 {PrUrl} https://github.com/michaeltrefry/dark-factory-sandbox/tree/factory/sc-77"],
            h.Stories.Writes);
        Assert.Equal(WorkerPid.ToString(), (await h.Rows()).Single(r => r.Step == "worker-started").Detail);
        var rows = await h.Rows();
        Assert.Equal(("sess-77", PrUrl), (rows[^1].ClaudeSessionId, rows[^1].Detail));
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.RecordedAt <= p.Second.RecordedAt));

        Assert.Null(worker.Calls.Single().Resume);
        Assert.Equal(["prepare michaeltrefry/dark-factory-sandbox factory/sc-77",
            "push michaeltrefry/dark-factory-sandbox factory/sc-77 sc-77: Whitespace counts as a word",
            "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77"], h.Workspaces.Calls); // throwaway once the PR is open (E5)
        var pr = h.Prs.Opened.Single();
        Assert.Equal(("factory/sc-77", "main"), (pr.Head, pr.Base));
        Assert.Contains("https://app.shortcut.com/trefry/story/77", pr.Body);
        Assert.Empty(h.Stories.Comments);
    }

    [Fact]
    public async Task Resumed_intake_does_not_claim_again()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);
        await ledger.CheckpointAsync(item, RunPipeline.Steps.Claimed, null, null, CancellationToken.None);

        await h.Run(new FakeWorker(Reports(Ok)));

        Assert.DoesNotContain(h.Stories.Writes, w => w.StartsWith("claim") || w.StartsWith("state"));
        Assert.Single(await h.Steps(), s => s == "claimed");
    }

    [Fact]
    public async Task Worker_prompt_carries_the_epic_and_its_documents()
    {
        var h = new Harness
        {
            Stories = new(Story)
            {
                Epic = new WorkEpic(5, "Phase 1", "Build the walking skeleton.", "https://app.shortcut.com/trefry/epic/5"),
                Documents = [new WorkDocument("Spec", "# Spec\nWork sources table.", "https://app.shortcut.com/trefry/write/d1")],
            },
        };
        var worker = new FakeWorker(Reports(Ok));

        await h.Run(worker);

        var prompt = worker.Calls.Single().Prompt;
        Assert.Contains("Phase 1", prompt);
        Assert.Contains("Build the walking skeleton.", prompt);
        Assert.Contains("Work sources table.", prompt);
    }

    [Fact]
    public async Task Session_id_is_in_the_ledger_before_the_worker_finishes()
    {
        var h = new Harness();
        List<LedgerEntry>? rowsWhileRunning = null;
        var worker = new FakeWorker(async call =>
        {
            await call.OnSession("sess-77", CancellationToken.None);
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
        // An escalated item restarts with a fresh worktree, so this one is removed (E5).
        Assert.Equal(["prepare michaeltrefry/dark-factory-sandbox factory/sc-77",
            "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77"], h.Workspaces.Calls);
        Assert.Empty(h.Prs.Opened);
        Assert.Equal(WorkState.Escalated, (await h.Item()).State);
    }

    [Fact]
    public async Task Escalation_comment_failure_is_recorded_and_reported_in_the_outcome()
    {
        var h = new Harness { Stories = new(Story, commentFails: true) };
        var outcome = await h.Run(new FakeWorker(Reports(new WorkerResult("sess-x", 2, true, null, null, ""))));

        var last = (await h.Rows())[^1];
        Assert.Equal(("escalation-comment", WorkState.Escalated), (last.Step, last.State));
        Assert.Contains("Shortcut down", last.Detail);
        Assert.EndsWith("escalation comment NOT posted: Shortcut down", outcome.Error);
    }

    [Fact]
    public async Task Rerun_posts_a_failed_escalation_comment_before_requeuing()
    {
        var h = new Harness { Stories = new(Story, commentFails: true) };
        var worker = new FakeWorker(Reports(new WorkerResult("sess-x", 2, true, null, "Not logged in", "")), Reports(Ok));
        await h.Run(worker);
        Assert.Empty(h.Stories.Comments);

        // Shortcut still down: the item stays Escalated and nothing runs.
        var stillDown = await h.Run(worker);
        Assert.Equal(WorkState.Escalated, stillDown.State);
        Assert.Equal("escalation comment NOT posted: Shortcut down", stillDown.Error);
        Assert.Single(worker.Calls);

        h.Stories.CommentFails = false;
        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        var comment = Assert.Single(h.Stories.Comments);
        Assert.Contains("Not logged in", comment);
        Assert.Contains("Last ledger state: Implement (after step session)", comment);
        Assert.Contains("sess-x", comment);
        var rows = await h.Rows();
        var posted = rows.FindIndex(r => r.Step == "escalation-comment" && r.Detail == "posted");
        Assert.Equal(WorkState.Escalated, rows[posted].State);
        Assert.Equal(posted + 1, rows.FindIndex(r => r.Step is null && r.State == WorkState.Intake && r.Detail!.StartsWith("re-run")));
    }

    [Fact]
    public async Task Unrequested_cancellation_such_as_an_http_timeout_escalates_instead_of_pausing()
    {
        var h = new Harness { Prs = new(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")) };

        var outcome = await h.Run(new FakeWorker(Reports(Ok)), CancellationToken.None);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Escalated], await h.Transitions());
        Assert.Contains("HttpClient.Timeout", Assert.Single(h.Stories.Comments));
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
        using var ctrlC = new CancellationTokenSource();
        var worker = new FakeWorker(StartsThenThrows("sess-77", new OperationCanceledException(ctrlC.Token), ctrlC.Cancel), Reports(Ok));

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Run(worker, ctrlC.Token));
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
    public async Task Startup_sweep_keeps_only_worktrees_a_rerun_would_resume_in()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        Assert.False(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-sc-77", CancellationToken.None)); // no item
        var item = await h.Crashed(RunPipeline.Steps.Session);
        Assert.True(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-sc-77", CancellationToken.None)); // Implement
        await ledger.RecordAsync(item, WorkState.Paused, null, "interrupted", CancellationToken.None);
        Assert.True(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-sc-77", CancellationToken.None)); // Paused
        await ledger.RecordAsync(item, WorkState.Implement, null, "unpaused", CancellationToken.None);
        await ledger.RecordAsync(item, WorkState.Escalated, null, "boom", CancellationToken.None);
        Assert.False(await RunPipeline.WorktreeIsResumableAsync(ledger, "factory-sc-77", CancellationToken.None)); // Escalated
        Assert.False(await RunPipeline.WorktreeIsResumableAsync(ledger, "not-ours", CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Timed_out_worker_escalates_and_its_worktree_is_removed_only_once_it_stopped(bool stillRunning)
    {
        var h = new Harness();
        var timeout = new TimeoutException("Worker did not finish within 00:30:00");
        var worker = new FakeWorker(StartsThenThrows("sess-77", stillRunning ? WorkerStillRunning.Mark(timeout) : timeout));

        var outcome = await h.Run(worker);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(stillRunning
                ? ["prepare michaeltrefry/dark-factory-sandbox factory/sc-77"]
                : ["prepare michaeltrefry/dark-factory-sandbox factory/sc-77", "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77"],
            h.Workspaces.Calls);
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
    public async Task Resume_stops_the_crashed_runs_worker_before_starting_the_next()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.WorkerStarted, RunPipeline.Steps.Session);
        List<LedgerEntry>? rowsAtStop = null;
        var worker = new FakeWorker(Reports(Ok)) { OrphanAlive = true, OnStopOrphan = () => rowsAtStop = h.Rows().Result };

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([999], worker.OrphanStops);
        Assert.DoesNotContain(rowsAtStop!, r => r.Step == "worker-started" && r.Detail == WorkerPid.ToString()); // stopped before the new worker
        var killed = (await h.Rows()).Single(r => r.Step == "orphan-killed");
        Assert.Equal("pid 999", killed.Detail);
        Assert.Equal(["worker-started", "session", "orphan-killed", "worker-started", "worker-done", "pushed", "linked"], await h.Steps());
    }

    [Fact]
    public async Task Resume_after_the_worker_finished_does_not_look_for_an_orphan()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.WorkerStarted, RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone);
        var worker = new FakeWorker { OrphanAlive = true };

        await h.Run(worker);

        Assert.Empty(worker.OrphanStops);
        Assert.DoesNotContain("orphan-killed", await h.Steps());
    }

    [Fact]
    public async Task Gone_orphan_is_not_recorded_as_killed()
    {
        var h = new Harness();
        await h.Crashed(RunPipeline.Steps.WorkerStarted, RunPipeline.Steps.Session);
        var worker = new FakeWorker(Reports(Ok)) { OrphanAlive = false };

        await h.Run(worker);

        Assert.Equal([999], worker.OrphanStops);
        Assert.DoesNotContain("orphan-killed", await h.Steps());
    }

    [Fact]
    public async Task Lost_worktree_after_push_is_restored_from_the_pushed_branch_and_nothing_is_redone()
    {
        var h = new Harness { Workspaces = new(worktreeExists: false) };
        await h.Crashed(RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone, RunPipeline.Steps.Pushed);
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Empty(worker.Calls);
        Assert.Equal(["reopen michaeltrefry/dark-factory-sandbox factory/sc-77", "restore michaeltrefry/dark-factory-sandbox factory/sc-77",
            "remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77"], h.Workspaces.Calls);
        Assert.Equal(["worktree-restored", "linked"], (await h.Steps())[^2..]);
        Assert.Single(h.Prs.Opened);
        Assert.Equal(WorkState.Review, (await h.Item()).State);
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
    public async Task Rerun_of_a_parked_item_redoes_nothing_and_tells_the_story_once()
    {
        var h = new Harness();
        await h.Run(new FakeWorker(Reports(Ok)));
        var rows = (await h.Rows()).Count;
        var writes = h.Stories.Writes.Count;
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);
        await h.Run(worker);

        Assert.True(outcome.Succeeded);
        Assert.Equal((WorkState.Review, PrUrl), (outcome.State, outcome.PullRequestUrl));
        Assert.Empty(worker.Calls);
        Assert.Single(h.Prs.Opened);
        Assert.Equal(writes, h.Stories.Writes.Count);
        var comment = Assert.Single(h.Stories.Comments);
        Assert.StartsWith("[author: dark-factory] sc-77 is Review in the factory ledger", comment);
        Assert.Equal(rows + 1, (await h.Rows()).Count);
        Assert.Equal(RunPipeline.Steps.HeldNotice, (await h.Rows())[^1].Step);
    }

    [Fact]
    public async Task Refused_claim_parks_the_item_without_reporting_it_in_progress()
    {
        var h = new Harness { Stories = new FakeWorkSource(Story) { RefuseClaim = "the story is no longer in To Do" } };
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Empty(h.Stories.Writes);
        Assert.Empty(h.Stories.Comments);
        Assert.Empty(worker.Calls);
        Assert.Equal([WorkState.Intake, WorkState.Paused], await h.Transitions());
        Assert.Equal(["parked"], await h.Steps());
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

    /// <summary>The worker's session starts, it uses <paramref name="tool"/> (the stream shows the tool_use), then behaves like <paramref name="then"/>.</summary>
    private static Func<WorkerCall, Task<WorkerResult>> UsesTool(string session, string tool, Func<WorkerCall, Task<WorkerResult>> then) => async call =>
    {
        await call.OnSession(session, CancellationToken.None);
        await call.Callbacks.OnUntrusted!(session, Taint.ForTool(tool)!, CancellationToken.None);
        return await then(call);
    };

    [Fact]
    public async Task An_untainted_implementer_pushes_with_a_grant_naming_its_session()
    {
        var h = new Harness();

        Assert.True((await h.Run(new FakeWorker(Reports(Ok)))).Succeeded);

        Assert.Equal(["sess-77"], Assert.Single(h.Workspaces.Grants).Sessions);
        Assert.Empty(h.Db.SessionTaints);
    }

    [Fact]
    public async Task An_implementer_that_fetched_the_web_is_tainted_and_refused_its_push_which_escalates_visibly()
    {
        var h = new Harness();

        var outcome = await h.Run(new FakeWorker(UsesTool("sess-77", "WebFetch", _ => Task.FromResult(Ok))));

        Assert.Equal(WorkState.Escalated, outcome.State);
        var taint = await h.Db.SessionTaints.SingleAsync();
        Assert.Equal(("sess-77", "web:WebFetch", (await h.Item()).Id), (taint.ClaudeSessionId, taint.Reason, taint.WorkItemId));
        // No push token: nothing pushed, no PR; the escalation says why and the worktree goes (a re-run starts a fresh session).
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Empty(h.Workspaces.Grants);
        Assert.Empty(h.Prs.Opened);
        var comment = Assert.Single(h.Stories.Comments);
        Assert.Contains("sess-77 is tainted (web:WebFetch)", comment);
        Assert.Contains("holds no push token", comment);
        Assert.Contains("remove michaeltrefry/dark-factory-sandbox /wt/factory/sc-77", h.Workspaces.Calls);
    }

    /// <summary>
    /// A taint that committed before the interrupt. The window where Ctrl-C lands after the tool_use line but before its taint
    /// commits needs the session-event store: SessionCaptureTests.A_web_fetch_whose_taint_was_cut_off_by_ctrl_c_...
    /// </summary>
    [Fact]
    public async Task A_session_whose_web_search_taint_committed_before_an_interrupt_is_refused_on_resume_and_never_pushes()
    {
        var h = new Harness();
        using var ctrlC = new CancellationTokenSource();
        var worker = new FakeWorker(
            UsesTool("sess-77", "WebSearch", _ => { ctrlC.Cancel(); throw new OperationCanceledException(ctrlC.Token); }),
            Reports(Ok));

        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Run(worker, ctrlC.Token));
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Paused], await h.Transitions());
        Assert.Equal("web:WebSearch", (await h.Db.SessionTaints.SingleAsync()).Reason);

        var outcome = await h.Run(worker);

        // The resume is refused before the worker runs again: no second worker call, no push, escalated with the reason.
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Single(worker.Calls);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Contains("sess-77 is tainted (web:WebSearch)", Assert.Single(h.Stories.Comments));
    }

    /// <summary>A harness whose worktree is a real directory, with <paramref name="settings"/> as the repo's .claude/settings.json (none when null).</summary>
    private static (Harness H, string Worktree) WithRepoSettings(string? settings)
    {
        var root = Directory.CreateTempSubdirectory("df-taint-wt-").FullName;
        var worktree = Path.Combine(root, "factory", "sc-77");
        Directory.CreateDirectory(Path.Combine(worktree, ".claude"));
        if (settings is not null)
        {
            File.WriteAllText(Path.Combine(worktree, ".claude", "settings.json"), settings);
        }
        return (new Harness { Workspaces = new FakeWorkspaces { Root = root } }, worktree);
    }

    private const string HookSettings = """{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"curl -s https://example.com"}]}]}}""";

    [Fact]
    public async Task A_new_session_in_a_repo_whose_settings_define_a_hook_is_tainted_from_its_start_and_refused_its_push()
    {
        var (h, _) = WithRepoSettings(HookSettings);
        string? atStart = null;
        var worker = new FakeWorker(async call =>
        {
            await call.OnSession("sess-77", CancellationToken.None);
            atStart = (await h.Ledger.TaintOfAsync("sess-77", CancellationToken.None))?.Reason; // before the session does anything
            return Ok;
        });

        var outcome = await h.Run(worker);

        Assert.Equal(Taint.RepoSettings, atStart);
        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(("sess-77", Taint.RepoSettings), await h.Db.SessionTaints.Select(t => ValueTuple.Create(t.ClaudeSessionId, t.Reason)).SingleAsync());
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
        Assert.Contains("sess-77 is tainted (repo-settings)", Assert.Single(h.Stories.Comments));
    }

    [Fact]
    public async Task A_session_is_not_resumed_in_a_repo_whose_settings_allow_more_than_the_worker_tools()
    {
        var (h, _) = WithRepoSettings("""{"permissions":{"allow":["Read","Bash(curl:*)"]}}""");
        await h.Crashed(RunPipeline.Steps.WorkerStarted, RunPipeline.Steps.Session);
        var worker = new FakeWorker(Reports(Ok));

        var outcome = await h.Run(worker);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(worker.Calls);
        Assert.Equal(Taint.RepoSettings, (await h.Ledger.TaintOfAsync("sess-77", CancellationToken.None))!.Reason);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
    }

    [Fact]
    public async Task A_worker_that_adds_a_hook_to_the_repo_settings_taints_its_own_session_and_is_refused_its_push()
    {
        var (h, worktree) = WithRepoSettings(null);
        var worker = new FakeWorker(async call =>
        {
            await call.OnSession("sess-77", CancellationToken.None);
            File.WriteAllText(Path.Combine(worktree, ".claude", "settings.local.json"), HookSettings);
            return Ok;
        });

        var outcome = await h.Run(worker);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Equal(Taint.RepoSettings, (await h.Db.SessionTaints.SingleAsync()).Reason);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
    }

    [Fact]
    public async Task A_crashed_run_whose_session_was_tainted_gets_no_push_token_when_resumed_after_the_worker_finished()
    {
        var h = new Harness();
        // The crashed run had recorded the session as done (worker-done) but not pushed; its session was tainted meanwhile.
        var item = await h.Crashed(RunPipeline.Steps.WorkerStarted, RunPipeline.Steps.Session, RunPipeline.Steps.WorkerDone);
        await h.Ledger.TaintSessionAsync(item, "sess-77", "web:WebFetch", CancellationToken.None);
        var worker = new FakeWorker();

        var outcome = await h.Run(worker);

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(worker.Calls);
        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("push"));
    }

    [Fact]
    public async Task A_taint_is_sticky_and_a_grant_needs_at_least_one_untainted_session()
    {
        var h = new Harness();
        var ledger = h.Ledger;
        var item = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-77", Story.Name, Sandbox.FullName, null, CancellationToken.None);

        await ledger.TaintSessionAsync(item, "s-1", Taint.IssueText, CancellationToken.None);
        await ledger.TaintSessionAsync(item, "s-1", "web:WebFetch", CancellationToken.None);

        Assert.Equal(Taint.IssueText, (await ledger.TaintOfAsync("s-1", CancellationToken.None))!.Reason); // the first reason is kept
        Assert.Null(await ledger.TaintOfAsync("s-2", CancellationToken.None));
        var refused = await Assert.ThrowsAsync<SessionTaintedException>(() => ledger.GrantPushAsync(["s-2", "s-1"], CancellationToken.None));
        Assert.Equal(("s-1", Taint.IssueText), (refused.SessionId, refused.Reason));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.GrantPushAsync([null], CancellationToken.None)); // E2
        Assert.Equal(["s-2"], (await ledger.GrantPushAsync(["s-2", null, "s-2"], CancellationToken.None)).Sessions);
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
