using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

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
/// One run at a time per item: a second concurrent run exits without touching it.
/// Worktrees are throwaway (E5): removed once the PR is open or the item escalates; only a
/// paused (Ctrl-C) or crashed Implement keeps its worktree, for the re-run to resume in.
/// The board is touched only through <see cref="IWorkSource"/> (E6): Intake claims the item and
/// reports it In Progress; Implement links the PR and branch before the item reaches Review.
/// A claim the work source refuses (the story left To Do or the watch scope, or another claimant holds
/// it) parks the item in Paused with no board write; it is picked up again when it is ready again.
/// Resuming an item already in the ledger first checks the story is still in the watch scope, and parks
/// it with one comment if not. <c>ignoreScope</c> (<c>factory run --ignore-scope</c>) skips only the
/// scope checks. An item in a state nothing in this phase drives (e.g. parked at Review) is not read at
/// all: its story gets one comment saying so.
/// </summary>
public sealed class RunPipeline(
    IWorkSource source,
    WorkLedger ledger,
    IRunLocks locks,
    IRepoWorkspace workspaces,
    IWorker worker,
    IPullRequests pullRequests,
    RepoRef defaultRepo,
    TextWriter log,
    SessionRecorder? sessions = null,
    TimeSpan? failedSessionEndTimeout = null,
    bool ignoreScope = false)
{
    public const string Source = "shortcut";

    /// <summary>How long a failed run may spend recording its session's end (and router cost) before it escalates.</summary>
    public static readonly TimeSpan DefaultFailedSessionEndTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _failedSessionEndTimeout = failedSessionEndTimeout ?? DefaultFailedSessionEndTimeout;

    /// <summary>Checkpoint names: completed sub-steps inside a state.</summary>
    public static class Steps
    {
        /// <summary>The worker process exists; Detail is its pid.</summary>
        public const string WorkerStarted = "worker-started";
        public const string Session = "session";
        public const string WorkerDone = "worker-done";
        public const string Pushed = "pushed";
        public const string WorktreeLost = "worktree-lost";
        /// <summary>The worktree was gone after a push and was re-created from the pushed branch.</summary>
        public const string WorktreeRestored = "worktree-restored";
        /// <summary>A worker left running by a crashed run was stopped; Detail is its pid.</summary>
        public const string OrphanKilled = "orphan-killed";
        public const string EscalationComment = "escalation-comment";
        /// <summary>The item is claimed on its board and reported In Progress.</summary>
        public const string Claimed = "claimed";
        /// <summary>The PR and branch are linked on the item; Detail is the links.</summary>
        public const string Linked = "linked";
        /// <summary>
        /// The item was parked in Paused and waits until its story is ready again (or a human runs it);
        /// Detail is why. A parked item is never resumed automatically.
        /// </summary>
        public const string Parked = "parked";
        /// <summary>The story was told the factory holds the item in a state nothing in this phase drives.</summary>
        public const string HeldNotice = "held-notice";
    }

    /// <summary>
    /// Detail of the Paused row recorded when the run is stopped (Ctrl-C, <c>factory work</c> shutting down).
    /// Only items paused this way are resumed automatically; any other pause (a parked item, a user's
    /// explicit Pause) waits for a human.
    /// </summary>
    public const string Interrupted = "interrupted";

    private sealed record Run(WorkSpec Spec, RepoRef Repo, WorkItem Item)
    {
        public WorkStory Story => Spec.Story;

        /// <summary>The worktree this run is using, once Implement has one.</summary>
        public Workspace? Workspace { get; set; }
    }

    private Dictionary<WorkState, Func<Run, CancellationToken, Task>> Handlers => new()
    {
        [WorkState.Intake] = IntakeAsync,
        [WorkState.Implement] = ImplementAsync,
    };

    /// <summary>States this phase's handlers drive; an item in one of them is in flight.</summary>
    public static readonly IReadOnlySet<WorkState> HandledStates = new HashSet<WorkState> { WorkState.Intake, WorkState.Implement };

    /// <summary>
    /// Story ids of items a previous run left in flight, oldest first: those in a handled state, and those
    /// <see cref="Interrupted"/> (not parked, not paused by a user) while in one.
    /// </summary>
    public static async Task<IReadOnlyList<int>> InFlightAsync(WorkLedger ledger, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var item in await ledger.ItemsInAsync(Source, new HashSet<WorkState>(HandledStates) { WorkState.Paused }, ct))
        {
            if (item.State == WorkState.Paused && !ResumesAfterInterrupt(await ledger.HistoryAsync(item, ct)))
            {
                continue;
            }
            if (StoryId.TryParse(item.ExternalId, out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static bool ResumesAfterInterrupt(List<LedgerEntry> history)
    {
        var paused = history.FindLastIndex(e => e.Step is null);
        var pausedFrom = TransitionContext.From(history.Where(e => e.Step is null).Select(e => e.State).ToList()).PausedFrom;
        return history[paused] is { State: WorkState.Paused, Detail: Interrupted }
            && pausedFrom is { } from && HandledStates.Contains(from)
            && !history.Skip(paused + 1).Any(e => e.Step == Steps.Parked);
    }

    /// <summary>Whether a run can move the item: it is in a handled state, escalated (re-queue), or paused from a handled state.</summary>
    private static bool Runnable(WorkItem item, TransitionContext context) =>
        HandledStates.Contains(item.State)
        || item.State == WorkState.Escalated
        || (item.State == WorkState.Paused && context.PausedFrom is { } from && HandledStates.Contains(from));

    public async Task<RunOutcome> RunAsync(int storyId, CancellationToken ct)
    {
        // Decide from the ledger before reading anything from the board.
        if (await ledger.FindAsync(Source, StoryId.Format(storyId), ct) is { } known)
        {
            if (!Runnable(known, await ledger.ContextAsync(known, ct)))
            {
                return await HoldAsync(known, storyId, ct);
            }
            // Resuming (an escalated item re-enters Intake, whose claim checks the scope itself).
            if (known.State != WorkState.Escalated && !ignoreScope && !await source.InScopeAsync(storyId, ct))
            {
                return await ParkOutOfScopeAsync(known, storyId, ct);
            }
        }

        var spec = await source.ReadSpecAsync(storyId, ct);
        var story = spec.Story;
        var repo = RepoResolver.Resolve(story.Description, defaultRepo);
        var item = await ledger.GetOrCreateAsync(Source, StoryId.Format(storyId), story.Name, repo.FullName, IntakeDetail(story), ct);

        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; not touching it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        // Re-read under the lock: another run may have moved the item since it was loaded.
        await ledger.RefreshAsync(item, story.Name, repo.FullName, ct);
        var run = new Run(spec, repo, item);

        if (Lifecycle.IsTerminal(item.State))
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is {item.State}; nothing to do.");
            return await OutcomeAsync(item, item.State == WorkState.Cancelled ? $"{item.ExternalId} is Cancelled." : null, ct);
        }
        if (item.State == WorkState.Escalated)
        {
            // The story must carry the escalation before the item moves on (E10).
            if (!EscalationCommentPosted(await ledger.HistoryAsync(item, ct))
                && await PostEscalationCommentAsync(run, ct) is { } commentError)
            {
                return await OutcomeAsync(item, EscalationCommentNotPosted(commentError), ct);
            }
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl-C: pause where we are; the worktree and checkpoints stay so a re-run resumes.
            await ledger.RecordAsync(item, WorkState.Paused, null, Interrupted, CancellationToken.None);
            log.WriteLine($"[paused] interrupted; `factory run {item.ExternalId}` resumes");
            throw;
        }
        catch (Exception ex)
        {
            // Includes an OperationCanceledException nobody asked for, e.g. an HttpClient timeout.
            var commentError = await EscalateAsync(run, ex is WorkerFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            // An escalated item restarts from Intake with a fresh worktree, so this one is throwaway (E5) —
            // unless the worker could not be stopped; the next run's sweep removes it once it is dead.
            if (WorkerStillRunning.IsMarked(ex))
            {
                log.WriteLine($"[cleanup] worker did not stop; leaving {run.Workspace?.Path} for the next run's sweep");
            }
            else
            {
                await RemoveWorktreeAsync(run);
            }
            return await OutcomeAsync(item,
                commentError is null ? ex.Message : $"{ex.Message}; {EscalationCommentNotPosted(commentError)}", CancellationToken.None);
        }

        if (item.State == WorkState.Paused)
        {
            var reason = (await ledger.HistoryAsync(item, ct)).Last(e => e.Step == Steps.Parked).Detail;
            log.WriteLine($"[paused] {item.ExternalId} parked: {reason}");
            return await OutcomeAsync(item, $"{item.ExternalId} parked: {reason}", ct);
        }
        log.WriteLine($"[{item.State}] parked; no handler for {item.State} in this phase");
        return await OutcomeAsync(item, null, ct);
    }

    /// <summary>
    /// The item is in a state no handler drives (e.g. parked at Review, Done): read and change nothing on
    /// the board, but tell the story once per state entry why the factory is not working on it.
    /// </summary>
    private async Task<RunOutcome> HoldAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; not touching it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        await ledger.RefreshAsync(item, item.Title, item.Repo, ct);
        var history = await ledger.HistoryAsync(item, ct);
        var entered = history.FindLastIndex(e => e.Step is null);
        if (!Runnable(item, await ledger.ContextAsync(item, ct)) && !history.Skip(entered + 1).Any(e => e.Step == Steps.HeldNotice))
        {
            var id = StoryId.Format(storyId);
            try
            {
                await source.CommentAsync(storyId,
                    $"{id} is {item.State} in the factory ledger and nothing in this phase of the factory moves it on from there, "
                    + "so the factory is not working on it whatever its board state.", ct);
                await ledger.CheckpointAsync(item, Steps.HeldNotice, null, "posted", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.WriteLine($"[{item.State}] could not comment on {id}: {ex.Message}; the next run retries it");
            }
        }
        log.WriteLine($"[{item.State}] {item.ExternalId} is {item.State}; nothing to do.");
        return await OutcomeAsync(item, item.State == WorkState.Cancelled ? $"{item.ExternalId} is Cancelled." : null, ct);
    }

    /// <summary>The story left the watch scope while the item was in flight: park it, comment once, and stop.</summary>
    private async Task<RunOutcome> ParkOutOfScopeAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; not touching it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        await ledger.RefreshAsync(item, item.Title, item.Repo, ct);
        const string reason = "the story is outside the watch scope";
        var history = await ledger.HistoryAsync(item, ct);
        var alreadyParked = item.State == WorkState.Paused
            && history.Skip(history.FindLastIndex(e => e.Step is null) + 1).Any(e => e.Step == Steps.Parked);
        if (!alreadyParked)
        {
            if (item.State != WorkState.Paused)
            {
                await ledger.RecordAsync(item, WorkState.Paused, null, reason, ct);
            }
            await ledger.CheckpointAsync(item, Steps.Parked, null, reason, ct);
            var id = StoryId.Format(storyId);
            var from = (await ledger.ContextAsync(item, ct)).PausedFrom;
            try
            {
                await source.CommentAsync(storyId,
                    $"{id} left the factory's watch scope, so the factory stopped working on it (paused at {from}). "
                    + $"Move it back into scope and to To Do, or run `factory run {id} --ignore-scope`, to resume it.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.WriteLine($"[paused] could not comment on {id}: {ex.Message}");
            }
        }
        log.WriteLine($"[paused] {item.ExternalId} parked: {reason}");
        return await OutcomeAsync(item, $"{item.ExternalId} parked: {reason}", ct);
    }

    private async Task IntakeAsync(Run run, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(run.Item, ct);
        var intake = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Intake);
        if (!history.Skip(intake + 1).Any(e => e.Step == Steps.Claimed))
        {
            var claim = await source.ClaimAsync(run.Story.Id, ignoreScope, ct);
            if (!claim.Claimed)
            {
                // Not ours to take any more: write nothing to the board and wait until it is ready again.
                await ledger.RecordAsync(run.Item, WorkState.Paused, null, $"claim refused: {claim.Refusal}", ct);
                await ledger.CheckpointAsync(run.Item, Steps.Parked, null, claim.Refusal, ct);
                return;
            }
            await source.ReportStateAsync(run.Story.Id, BoardState.Claimed, null, ct);
            await ledger.CheckpointAsync(run.Item, Steps.Claimed, null, null, ct);
            log.WriteLine($"[intake] claimed {run.Item.ExternalId}");
        }
        // Phase 1 has no Plan step: the worker implements straight from the story.
        await ledger.RecordAsync(run.Item, WorkState.Implement, null, null, ct);
    }

    private async Task ImplementAsync(Run run, CancellationToken ct)
    {
        var (spec, repo, item) = run;
        var story = spec.Story;
        var branch = StoryId.BranchName(story.Id);
        var attempt = CurrentImplementAttempt(await ledger.HistoryAsync(item, ct));
        var session = attempt.LastOrDefault(e => e.Step == Steps.Session)?.ClaudeSessionId;

        // A worker a crashed run left behind must not keep editing (or resume the same session) alongside this one.
        if (OrphanedWorkerPid(attempt) is { } pid && await worker.StopOrphanAsync(pid, ct))
        {
            await ledger.CheckpointAsync(item, Steps.OrphanKilled, session, $"pid {pid}", ct);
            log.WriteLine($"[implement] stopped worker pid {pid} left running by an earlier run");
        }

        Workspace? workspace = null;
        if (attempt.Any(e => e.Step is not null))
        {
            workspace = await workspaces.ReopenAsync(repo, branch, ct);
            if (workspace is null && attempt.Any(e => e.Step == Steps.Pushed))
            {
                // The work is on origin: rebuild the worktree from it rather than redo it and force-push over it.
                workspace = await workspaces.RestoreAsync(repo, branch, ct);
                await ledger.CheckpointAsync(item, Steps.WorktreeRestored, session, $"worktree missing on resume; re-created from origin/{branch}", ct);
            }
            else if (workspace is null)
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
        run.Workspace = workspace;
        log.WriteLine($"[implement] worktree {workspace.Path} on {workspace.Branch}");

        if (!attempt.Any(e => e.Step == Steps.WorkerDone))
        {
            var resume = session;
            log.WriteLine(resume is null ? "[implement] starting worker" : $"[implement] resuming claude session {resume}");
            // Every stdout line is stored (and pushed to live viewers) as it streams (E7).
            await using var capture = sessions is null ? null : await sessions.StartAsync(item.Id, resume, ct);
            WorkerResult result;
            try
            {
                result = await worker.RunAsync(workspace.Path,
                    resume is null ? BuildPrompt(spec, repo) : BuildResumePrompt(story), resume,
                    new WorkerCallbacks(
                        OnStarted: (pid, c) => ledger.CheckpointAsync(item, Steps.WorkerStarted, session, pid.ToString(), c),
                        OnSession: async (sid, c) =>
                        {
                            if (sid != session)
                            {
                                if (capture is not null)
                                {
                                    // The session row is named before the ledger points at it, so a crash
                                    // in between cannot strand the events already stored (E7).
                                    await capture.SetClaudeSessionIdAsync(sid, c);
                                }
                                session = sid;
                                await ledger.CheckpointAsync(item, Steps.Session, sid, "claude session started", c);
                            }
                        },
                        OnLine: capture is null ? null : capture.OnLineAsync), ct);
            }
            catch (Exception ex) when (capture is not null)
            {
                // A Ctrl-C'd session resumes later, so its cost waits for the run that finishes it. A failed
                // session's cost is fetched now, bounded so a hung router or ledger cannot hold up the escalation.
                var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
                using var bounded = new CancellationTokenSource(_failedSessionEndTimeout);
                try
                {
                    await capture.CompleteAsync(null, cancelled ? "cancelled" : "error", fetchCost: !cancelled, bounded.Token).WaitAsync(bounded.Token);
                }
                catch (Exception captureError)
                {
                    log.WriteLine($"[implement] could not record the end of the worker session: {captureError.Message}");
                }
                throw;
            }
            if (capture is not null)
            {
                await capture.CompleteAsync(result.ExitCode, result.Succeeded ? "succeeded" : "failed", fetchCost: true, ct);
            }
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
        var branchUrl = BranchUrl(repo, workspace.Branch);
        if (!attempt.Any(e => e.Step == Steps.Linked && e.Detail == $"{prUrl} {branchUrl}"))
        {
            await source.LinkAsync(story.Id, [prUrl, branchUrl], ct);
            await ledger.CheckpointAsync(item, Steps.Linked, session, $"{prUrl} {branchUrl}", ct);
        }
        await ledger.RecordAsync(item, WorkState.Review, session, prUrl, ct);
        log.WriteLine($"[review] {prUrl}");
        // The work is on origin (RestoreAsync re-creates it if ever needed): the worktree is throwaway (E5).
        await RemoveWorktreeAsync(run);
    }

    private async Task RemoveWorktreeAsync(Run run)
    {
        if (run.Workspace is not { } workspace)
        {
            return;
        }
        try
        {
            await workspaces.RemoveAsync(run.Repo, workspace, CancellationToken.None);
            run.Workspace = null;
        }
        catch (Exception ex)
        {
            log.WriteLine($"[cleanup] could not remove worktree {workspace.Path}: {ex.Message}; the next run's sweep retries");
        }
    }

    /// <summary>
    /// Whether a worktree directory (<c>factory-sc-&lt;id&gt;</c>) belongs to an item a re-run would resume
    /// in it (Implement, or Paused), so the startup sweep must keep it. Everything else is an orphan.
    /// </summary>
    public static async Task<bool> WorktreeIsResumableAsync(WorkLedger ledger, string worktreeName, CancellationToken ct)
    {
        const string prefix = "factory-";
        if (!worktreeName.StartsWith(prefix, StringComparison.Ordinal) || !StoryId.TryParse(worktreeName[prefix.Length..], out var id))
        {
            return false;
        }
        return await ledger.StateOfAsync(Source, StoryId.Format(id), ct) is WorkState.Implement or WorkState.Paused;
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

    /// <summary>The pid of the attempt's last worker if no row shows it finished.</summary>
    private static int? OrphanedWorkerPid(List<LedgerEntry> attempt)
    {
        var started = attempt.FindLastIndex(e => e.Step == Steps.WorkerStarted);
        return started >= 0 && attempt.FindLastIndex(e => e.Step == Steps.WorkerDone) < started
            && int.TryParse(attempt[started].Detail, out var pid)
            ? pid
            : null;
    }

    /// <summary>Records Escalated and comments on the story. Returns why the comment failed, or null.</summary>
    private async Task<string?> EscalateAsync(Run run, string reason)
    {
        var history = await ledger.HistoryAsync(run.Item, CancellationToken.None);
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        await ledger.RecordAsync(run.Item, WorkState.Escalated, session, reason, CancellationToken.None);
        log.WriteLine($"[escalated] {reason}");
        return await PostEscalationCommentAsync(run, CancellationToken.None);
    }

    /// <summary>Whether the story got a comment for the item's latest escalation.</summary>
    private static bool EscalationCommentPosted(List<LedgerEntry> history)
    {
        var escalated = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Escalated);
        return history.Skip(escalated + 1).Any(e => e.Step == Steps.EscalationComment && e.Detail == "posted");
    }

    private static string EscalationCommentNotPosted(string reason) => $"escalation comment NOT posted: {reason}";

    /// <summary>
    /// Comments the item's latest escalation (reason, the ledger state before it, session) on the
    /// story and checkpoints the result. Returns why the comment failed, or null.
    /// </summary>
    private async Task<string?> PostEscalationCommentAsync(Run run, CancellationToken ct)
    {
        var (spec, _, item) = run;
        var story = spec.Story;
        var history = await ledger.HistoryAsync(item, ct);
        var at = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Escalated);
        var (escalated, last) = (history[at], history[at - 1]);
        var (reason, session) = (escalated.Detail, escalated.ClaudeSessionId);
        var lastState = $"{last.State}{(last.Step is null ? "" : $" (after step {last.Step})")} at {last.RecordedAt:u}";

        var comment = $"""
            [author: dark-factory] {StoryId.Format(story.Id)} escalated; a human needs to look.

            Reason: {reason}
            Last ledger state: {lastState}
            Claude session: {session ?? "none"}

            Re-run with `factory run {StoryId.Format(story.Id)}` once resolved.
            """;
        try
        {
            await source.CommentAsync(story.Id, comment, ct);
        }
        catch (Exception ex)
        {
            await ledger.CheckpointAsync(item, Steps.EscalationComment, session, $"failed: {ex.Message}", CancellationToken.None);
            log.WriteLine($"[escalated] could not comment on {StoryId.Format(story.Id)}: {ex.Message}; `factory run {StoryId.Format(story.Id)}` retries it");
            return ex.Message;
        }
        await ledger.CheckpointAsync(item, Steps.EscalationComment, session, "posted", CancellationToken.None);
        return null;
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

    private static string IntakeDetail(WorkStory story) => $"{story.StoryType}: {story.AppUrl}";

    public static string BranchUrl(RepoRef repo, string branch) => $"https://github.com/{repo}/tree/{branch}";

    public static string BuildPrompt(WorkSpec spec, RepoRef repo)
    {
        var story = spec.Story;
        var prompt = $"""
            You are a Dark Factory worker. The current directory is a git worktree of {repo}.
            Implement Shortcut story {StoryId.Format(story.Id)} ({story.StoryType}): {story.Name}

            Story description:
            {story.Description}

            Make the smallest change that satisfies the story, including a test when the
            project has tests, and make sure `dotnet build` and `dotnet test` pass.
            Do not commit, push, or open pull requests; the orchestrator does that.
            """;
        if (spec.Epic is { } epic)
        {
            prompt += $"\n\nThe story belongs to epic \"{epic.Name}\" ({epic.AppUrl}). Epic description, for context:\n{epic.Description}";
        }
        foreach (var doc in spec.Documents)
        {
            prompt += $"\n\nEpic document \"{doc.Title}\" ({doc.AppUrl}):\n{doc.Markdown}";
        }
        return prompt;
    }

    public static string BuildResumePrompt(WorkStory story) =>
        $"""
        You were interrupted while implementing Shortcut story {StoryId.Format(story.Id)}.
        Check the current state of the worktree and finish the story as originally instructed.
        """;

    public static string BuildPrBody(WorkStory story, string sessionId) =>
        $"""
        Implements Shortcut story [{StoryId.Format(story.Id)}]({story.AppUrl}): {story.Name}

        Opened by Dark Factory. Claude session: `{sessionId}`
        """;
}
