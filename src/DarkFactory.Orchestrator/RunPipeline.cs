using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator;

public sealed record RunOutcome(long WorkItemId, WorkState State, string? SessionId, string? PullRequestUrl, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>A worker session that ended without success.</summary>
public sealed class WorkerFailedException(string message) : Exception(message);

/// <summary>
/// <c>factory run</c>: drives one Shortcut story through the <see cref="Lifecycle"/> from
/// its last ledger state. Each registered handler does one state's work and makes one
/// transition (E2); every transition and completed sub-step is a committed ledger row
/// before the next starts (E3), so a re-run after a crash resumes where the ledger says
/// and never redoes a recorded step. States without a handler park the item (phase 1
/// parks at Review with the PR open). A failure escalates with a story comment (E10).
/// </summary>
public sealed class RunPipeline(
    IStorySource stories,
    WorkLedger ledger,
    IRepoWorkspace workspaces,
    IWorker worker,
    IPullRequests pullRequests,
    RepoRef defaultRepo,
    TextWriter log)
{
    public const string Source = "shortcut";

    /// <summary>Checkpoint names: completed sub-steps inside a state.</summary>
    public static class Steps
    {
        public const string Session = "session";
        public const string WorkerDone = "worker-done";
        public const string Pushed = "pushed";
        public const string WorktreeLost = "worktree-lost";
        public const string EscalationComment = "escalation-comment";
    }

    private sealed record Run(ShortcutStory Story, RepoRef Repo, WorkItem Item);

    private Dictionary<WorkState, Func<Run, CancellationToken, Task>> Handlers => new()
    {
        [WorkState.Intake] = IntakeAsync,
        [WorkState.Implement] = ImplementAsync,
    };

    public async Task<RunOutcome> RunAsync(int storyId, CancellationToken ct)
    {
        var story = await stories.GetStoryAsync(storyId, ct);
        var repo = RepoResolver.Resolve(story.Description, defaultRepo);
        var item = await ledger.GetOrCreateAsync(Source, StoryId.Format(storyId), story.Name, repo.FullName, IntakeDetail(story), ct);
        var run = new Run(story, repo, item);

        if (Lifecycle.IsTerminal(item.State))
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is {item.State}; nothing to do.");
            return await OutcomeAsync(item, item.State == WorkState.Cancelled ? $"{item.ExternalId} is Cancelled." : null, ct);
        }
        if (item.State == WorkState.Escalated)
        {
            await ledger.RecordAsync(item, WorkState.Intake, null, $"re-run after escalation; {IntakeDetail(story)}", ct);
        }
        else if (item.State == WorkState.Paused)
        {
            var pausedFrom = (await ledger.ContextAsync(item, ct)).PausedFrom
                ?? throw new InvalidOperationException($"{item.ExternalId} is paused but its ledger has no state to return to.");
            await ledger.RecordAsync(item, pausedFrom, null, "unpaused", ct);
        }
        log.WriteLine($"[{item.State}] {item.ExternalId} \"{story.Name}\" -> {repo}");

        try
        {
            while (Handlers.TryGetValue(item.State, out var handler))
            {
                var before = item.State;
                await handler(run, ct);
                if (item.State == before)
                {
                    throw new InvalidOperationException($"The {before} handler finished without a transition.");
                }
                log.WriteLine($"[{item.State}]");
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: pause where we are; the worktree and checkpoints stay so a re-run resumes.
            await ledger.RecordAsync(item, WorkState.Paused, null, "interrupted", CancellationToken.None);
            log.WriteLine($"[paused] interrupted; `factory run {item.ExternalId}` resumes");
            throw;
        }
        catch (Exception ex)
        {
            await EscalateAsync(run, ex is WorkerFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            return await OutcomeAsync(item, ex.Message, CancellationToken.None);
        }

        log.WriteLine($"[{item.State}] parked; no handler for {item.State} in this phase");
        return await OutcomeAsync(item, null, ct);
    }

    private async Task IntakeAsync(Run run, CancellationToken ct) =>
        // Phase 1 has no Plan step: the worker implements straight from the story.
        await ledger.RecordAsync(run.Item, WorkState.Implement, null, null, ct);

    private async Task ImplementAsync(Run run, CancellationToken ct)
    {
        var (story, repo, item) = run;
        var branch = StoryId.BranchName(story.Id);
        var attempt = CurrentImplementAttempt(await ledger.HistoryAsync(item, ct));
        var session = attempt.LastOrDefault(e => e.Step == Steps.Session)?.ClaudeSessionId;

        Workspace? workspace = null;
        if (attempt.Any(e => e.Step is not null))
        {
            workspace = await workspaces.ReopenAsync(repo, branch, ct);
            if (workspace is null)
            {
                await ledger.CheckpointAsync(item, Steps.WorktreeLost, session, "worktree missing on resume; starting Implement over", ct);
                attempt = [];
                session = null;
            }
        }
        if (workspace is null)
        {
            workspace = await workspaces.PrepareAsync(repo, branch, ct);
        }
        log.WriteLine($"[implement] worktree {workspace.Path} on {workspace.Branch}");

        if (!attempt.Any(e => e.Step == Steps.WorkerDone))
        {
            var resume = session;
            log.WriteLine(resume is null ? "[implement] starting worker" : $"[implement] resuming claude session {resume}");
            var result = await worker.RunAsync(workspace.Path,
                resume is null ? BuildPrompt(story, repo) : BuildResumePrompt(story), resume,
                async (sid, c) =>
                {
                    if (sid != session)
                    {
                        session = sid;
                        await ledger.CheckpointAsync(item, Steps.Session, sid, "claude session started", c);
                    }
                }, ct);
            session = result.SessionId ?? session;
            if (!result.Succeeded)
            {
                throw new WorkerFailedException(
                    $"Worker failed (exit {result.ExitCode}, result {result.ResultSubtype ?? "none"}): {result.ResultText} {result.StderrTail}".Trim());
            }
            await ledger.CheckpointAsync(item, Steps.WorkerDone, session, $"worker exit {result.ExitCode}", ct);
        }

        if (!attempt.Any(e => e.Step == Steps.Pushed))
        {
            if (!await workspaces.CommitAndPushAsync(repo, workspace, $"{StoryId.Format(story.Id)}: {story.Name}", ct))
            {
                throw new InvalidOperationException("Worker finished without changing the repository; nothing to review.");
            }
            await ledger.CheckpointAsync(item, Steps.Pushed, session, workspace.Branch, ct);
        }

        // Returns the branch's already-open PR instead of opening a second one.
        var prUrl = await pullRequests.OpenAsync(repo, workspace.Branch, workspace.BaseBranch,
            $"{StoryId.Format(story.Id)}: {story.Name}", BuildPrBody(story, session!), ct);
        await ledger.RecordAsync(item, WorkState.Review, session, prUrl, ct);
        log.WriteLine($"[review] {prUrl}");
    }

    /// <summary>
    /// Rows of the current Implement attempt: from the last entry into Implement (a return
    /// from Paused continues the attempt) or the last lost-worktree restart.
    /// </summary>
    private static List<LedgerEntry> CurrentImplementAttempt(List<LedgerEntry> history)
    {
        var start = 0;
        WorkState? previous = null;
        for (var i = 0; i < history.Count; i++)
        {
            var e = history[i];
            if (e.Step is null)
            {
                if (e.State == WorkState.Implement && previous != WorkState.Paused)
                {
                    start = i;
                }
                previous = e.State;
            }
            else if (e.Step == Steps.WorktreeLost)
            {
                start = i;
            }
        }
        return history.Skip(start).ToList();
    }

    private async Task EscalateAsync(Run run, string reason)
    {
        var (story, _, item) = run;
        var history = await ledger.HistoryAsync(item, CancellationToken.None);
        var last = history[^1];
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        var lastState = $"{last.State}{(last.Step is null ? "" : $" (after step {last.Step})")} at {last.RecordedAt:u}";

        await ledger.RecordAsync(item, WorkState.Escalated, session, reason, CancellationToken.None);
        log.WriteLine($"[escalated] {reason}");

        var comment = $"""
            [author: dark-factory] {StoryId.Format(story.Id)} escalated; a human needs to look.

            Reason: {reason}
            Last ledger state: {lastState}
            Claude session: {session ?? "none"}

            Re-run with `factory run {StoryId.Format(story.Id)}` once resolved.
            """;
        try
        {
            await stories.AddCommentAsync(story.Id, comment, CancellationToken.None);
            await ledger.CheckpointAsync(item, Steps.EscalationComment, session, "posted", CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ledger.CheckpointAsync(item, Steps.EscalationComment, session, $"failed: {ex.Message}", CancellationToken.None);
            log.WriteLine($"[escalated] could not comment on {StoryId.Format(story.Id)}: {ex.Message}");
        }
    }

    private async Task<RunOutcome> OutcomeAsync(WorkItem item, string? error, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(item, ct);
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        var pr = item.State == WorkState.Review
            ? history.Last(e => e.Step is null && e.State == WorkState.Review).Detail
            : null;
        return new RunOutcome(item.Id, item.State, session, pr, error);
    }

    private static string IntakeDetail(ShortcutStory story) => $"{story.StoryType}: {story.AppUrl}";

    public static string BuildPrompt(ShortcutStory story, RepoRef repo) =>
        $"""
        You are a Dark Factory worker. The current directory is a git worktree of {repo}.
        Implement Shortcut story {StoryId.Format(story.Id)} ({story.StoryType}): {story.Name}

        Story description:
        {story.Description}

        Make the smallest change that satisfies the story, including a test when the
        project has tests, and make sure `dotnet build` and `dotnet test` pass.
        Do not commit, push, or open pull requests; the orchestrator does that.
        """;

    public static string BuildResumePrompt(ShortcutStory story) =>
        $"""
        You were interrupted while implementing Shortcut story {StoryId.Format(story.Id)}.
        Check the current state of the worktree and finish the story as originally instructed.
        """;

    public static string BuildPrBody(ShortcutStory story, string sessionId) =>
        $"""
        Implements Shortcut story [{StoryId.Format(story.Id)}]({story.AppUrl}): {story.Name}

        Opened by Dark Factory. Claude session: `{sessionId}`
        """;
}
