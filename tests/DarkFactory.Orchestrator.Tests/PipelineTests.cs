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
    [Fact]
    public async Task Records_state_rows_with_timestamp_and_session_and_updates_item()
    {
        await using var db = TestDb.Create();
        var time = new FixedTime(DateTimeOffset.Parse("2026-10-06T10:00:00Z"));
        var ledger = new WorkLedger(db, time);

        var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", CancellationToken.None);
        time.Now = time.Now.AddMinutes(5);
        await ledger.RecordAsync(item, WorkState.Implement, "sess-1", "done", CancellationToken.None);

        var entry = await db.LedgerEntries.SingleAsync();
        Assert.Equal((item.Id, WorkState.Implement, "sess-1", "done"), (entry.WorkItemId, entry.State, entry.ClaudeSessionId, entry.Detail));
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T10:05:00Z"), entry.RecordedAt);
        Assert.Equal(WorkState.Implement, (await db.WorkItems.SingleAsync()).State);
    }

    [Fact]
    public async Task Same_story_reuses_its_work_item()
    {
        await using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);

        var first = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Old", "o/r", CancellationToken.None);
        var second = await ledger.GetOrCreateAsync("shortcut", "sc-1", "New", "o/r", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("New", (await db.WorkItems.SingleAsync()).Title);
    }
}

public class RunPipelineTests
{
    private static readonly RepoRef Sandbox = new("michaeltrefry", "dark-factory-sandbox");
    private static readonly ShortcutStory Story =
        new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");

    private sealed class FakeStories(ShortcutStory story) : IStorySource
    {
        public Task<ShortcutStory> GetStoryAsync(int id, CancellationToken ct) => Task.FromResult(story with { Id = id });
    }

    private sealed class FakeWorkspaces(bool hasChanges) : IRepoWorkspace
    {
        public List<string> Calls { get; } = [];
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct)
        {
            Calls.Add($"prepare {repo} {branch}");
            return Task.FromResult(new Workspace($"/wt/{branch}", branch, "main"));
        }
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct)
        {
            Calls.Add($"push {repo} {workspace.Branch} {message}");
            return Task.FromResult(hasChanges);
        }
    }

    private sealed class FakeWorker(WorkerResult result) : IWorker
    {
        public string? Prompt { get; private set; }
        public Task<WorkerResult> RunAsync(string workingDirectory, string prompt, CancellationToken ct)
        {
            Prompt = prompt;
            return Task.FromResult(result);
        }
    }

    private sealed class FakePullRequests : IPullRequests
    {
        public List<(RepoRef Repo, string Head, string Base, string Title, string Body)> Opened { get; } = [];
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            Opened.Add((repo, head, baseBranch, title, body));
            return Task.FromResult("https://github.com/michaeltrefry/dark-factory-sandbox/pull/1");
        }
    }

    private static readonly WorkerResult Ok = new("sess-77", 0, false, "success", "done", "");

    private static async Task<(RunOutcome Outcome, List<LedgerEntry> Rows, FakeWorkspaces Ws, FakePullRequests Prs)> Run(
        WorkerResult workerResult, bool hasChanges = true, ShortcutStory? story = null)
    {
        var db = TestDb.Create();
        var ws = new FakeWorkspaces(hasChanges);
        var prs = new FakePullRequests();
        var pipeline = new RunPipeline(new FakeStories(story ?? Story), new WorkLedger(db, TimeProvider.System), ws,
            new FakeWorker(workerResult), prs, Sandbox, TextWriter.Null);
        var outcome = await pipeline.RunAsync(77, CancellationToken.None);
        var rows = await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        return (outcome, rows, ws, prs);
    }

    [Fact]
    public async Task Successful_run_writes_intake_implement_review_and_opens_pr_linking_story()
    {
        var (outcome, rows, ws, prs) = await Run(Ok);

        Assert.True(outcome.Succeeded);
        Assert.Equal("https://github.com/michaeltrefry/dark-factory-sandbox/pull/1", outcome.PullRequestUrl);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review], rows.Select(r => r.State));
        Assert.Equal([null, "sess-77", "sess-77"], rows.Select(r => r.ClaudeSessionId));
        Assert.Equal(outcome.PullRequestUrl, rows[2].Detail);
        Assert.True(rows[0].RecordedAt <= rows[1].RecordedAt && rows[1].RecordedAt <= rows[2].RecordedAt);

        Assert.Equal(["prepare michaeltrefry/dark-factory-sandbox factory/sc-77",
            "push michaeltrefry/dark-factory-sandbox factory/sc-77 sc-77: Whitespace counts as a word"], ws.Calls);
        var pr = prs.Opened.Single();
        Assert.Equal(("factory/sc-77", "main"), (pr.Head, pr.Base));
        Assert.Contains("https://app.shortcut.com/trefry/story/77", pr.Body);
    }

    [Fact]
    public async Task Repo_line_in_story_redirects_the_run()
    {
        var (_, _, ws, prs) = await Run(Ok, story: Story with { Description = "Repo: acme/widgets\nfix" });
        Assert.StartsWith("prepare acme/widgets", ws.Calls[0]);
        Assert.Equal(new RepoRef("acme", "widgets"), prs.Opened.Single().Repo);
    }

    [Fact]
    public async Task Failed_worker_records_failed_row_with_session_and_opens_no_pr()
    {
        var (outcome, rows, ws, prs) = await Run(new WorkerResult("sess-x", 1, true, "error_during_execution", "Not logged in", "boom"));

        Assert.False(outcome.Succeeded);
        Assert.Equal([WorkState.Intake, WorkState.Failed], rows.Select(r => r.State));
        Assert.Equal("sess-x", rows[1].ClaudeSessionId);
        Assert.Contains("Not logged in", rows[1].Detail);
        Assert.Single(ws.Calls);
        Assert.Empty(prs.Opened);
    }

    [Fact]
    public async Task No_changes_means_no_pr()
    {
        var (outcome, rows, _, prs) = await Run(Ok, hasChanges: false);

        Assert.False(outcome.Succeeded);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Failed], rows.Select(r => r.State));
        Assert.Empty(prs.Opened);
    }
}
