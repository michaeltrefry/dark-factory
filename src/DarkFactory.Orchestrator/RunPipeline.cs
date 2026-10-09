using DarkFactory.Orchestrator.Controls;
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

    /// <summary>
    /// Set when the run was not dispatched because the factory is frozen (<see cref="FactoryFreeze"/>): the typed outcome
    /// <c>deferred</c>, with why. Nothing about the item changed.
    /// </summary>
    public string? Deferred { get; init; }
}

/// <summary>A worker session that ended without success.</summary>
public sealed class WorkerFailedException(string message) : Exception(message);

/// <summary>
/// A worker session the stuck detector (<see cref="Sessions.StuckDetector"/>, sc-25388) found looping and interrupted at a tool
/// boundary: its round failed. <see cref="Reason"/> names the repetition (never transcript content).
/// </summary>
public sealed class WorkerStuckException(string reason, string? session)
    : Exception($"Worker session {session ?? "(unnamed)"} was stuck in a loop and interrupted: {reason}")
{
    public string Reason { get; } = reason;
    public string? Session { get; } = session;
}

/// <summary>
/// <c>factory run</c>: drives one Shortcut story through the <see cref="Lifecycle"/> from
/// its last ledger state. Each registered handler does one state's work and makes one
/// transition (E2); every transition and completed sub-step is a committed ledger row
/// before the next starts (E3), so a re-run after a crash resumes where the ledger says
/// and never redoes a recorded step. States without a handler park the item (with a
/// <see cref="GateStage"/> the run goes on through review and the merge gate and parks at Watch
/// once merged; without one it parks at Review with the PR open). A failure escalates with a story comment (E10).
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
/// Controls (<see cref="IControls"/>) are checked before every step and watched while a worker runs: Pause
/// records Paused (<see cref="UserPaused"/>) once the worker has stopped at a tool boundary, keeping its session
/// and worktree; Stop kills the worker and cancels the item (<see cref="ItemStopper"/>); a paused factory or epic
/// claims nothing new.
/// </summary>
public sealed partial class RunPipeline(
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
    bool ignoreScope = false,
    IControls? controls = null,
    TimeSpan? pauseGrace = null,
    TimeSpan? controlPollInterval = null,
    GateStage? gate = null,
    FactoryFreeze? freeze = null,
    StuckDetection? stuck = null)
{
    /// <summary>When a running worker counts as stuck (<c>Worker:StuckRepeats</c>, <c>Worker:StuckSimilarity</c>; sc-25388).</summary>
    private readonly StuckDetection _stuck = (stuck ?? StuckDetection.Default).Validate();

    /// <summary>
    /// Implementer sessions in a row that may be found stuck before the item escalates (sc-25388): the first stuck session's
    /// attempt fails and one fresh session (in a fresh worktree) retries Implement; a second escalates.
    /// </summary>
    public const int MaxStuckImplementSessions = 2;

    /// <summary>The Shortcut source's ledger name (<see cref="ItemNaming.Shortcut"/>); a pipeline names items by its source's <see cref="Naming"/>.</summary>
    public const string Source = "shortcut";

    /// <summary>How this pipeline's source names its items (ledger source, external id, branch).</summary>
    private ItemNaming Naming => source.Naming;

    /// <summary>The longest a single worker tool call may legitimately run: Claude Code's maximum Bash timeout (10 minutes).</summary>
    public static readonly TimeSpan LongestToolCall = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a paused worker gets to reach its next tool boundary before it is stopped anyway: longer than the
    /// longest tool call, so a pause never kills a tool call (e.g. a long <c>dotnet test</c>) mid-way.
    /// </summary>
    public static readonly TimeSpan DefaultPauseGrace = LongestToolCall + TimeSpan.FromMinutes(1);

    /// <summary>How often a running worker's run re-reads its controls.</summary>
    public static readonly TimeSpan DefaultControlPollInterval = TimeSpan.FromSeconds(1);

    private readonly IControls _controls = controls ?? NoControls.Instance;
    private readonly TimeSpan _pauseGrace = pauseGrace ?? DefaultPauseGrace;
    private readonly TimeSpan _controlPoll = controlPollInterval ?? DefaultControlPollInterval;

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
        /// <summary>Stopping: the item's open PRs are drafts again; Detail is their URLs (or <c>none</c>).</summary>
        public const string PrsDrafted = "prs-drafted";
        /// <summary>Stopping: the story is back in the Backlog with its claim released and a comment; Detail is who stopped it.</summary>
        public const string StopReported = "stop-reported";
        /// <summary>The item was paused by the factory's usage pause; Detail is its reason and resume time.</summary>
        public const string UsagePause = "usage-pause";
        /// <summary>A model answered the implementer's session (first time seen for the item); Detail is the model id.</summary>
        public const string ImplementerModel = "implementer-model";
        /// <summary>
        /// Review: a review panel call is about to be made; Detail is "&lt;router session&gt; &lt;model&gt; &lt;head sha&gt;
        /// &lt;role&gt; &lt;prompt path&gt;@sha256:&lt;prompt hash&gt;" (role <c>confirm-&lt;role&gt;</c> for a second model's
        /// check of a blocking finding), so the call's cost is readable even when it never returns (E9) and the prompt it
        /// used is on record.
        /// </summary>
        public const string ReviewSession = "review-session";
        /// <summary>Review: a reviewer's verdict on one head commit; Detail is the <see cref="Gate.ReviewVerdict"/> JSON.</summary>
        public const string Verdict = "verdict";
        /// <summary>
        /// Review after a fix round: the round's progress check; Detail is the <see cref="Gate.FixProgress"/> JSON (outcome
        /// <c>progress</c> or <c>failed</c>). In a fix round (Fixing) the <see cref="Pushed"/> checkpoint's Detail is the
        /// commit the fixer's work was pushed as.
        /// </summary>
        public const string FixProgress = "fix-progress";
        /// <summary>
        /// CI: the PR's head commit finished CI red; Detail is the <see cref="Gate.CiTriage"/> JSON — the failing checks a CI
        /// fixer may work on and every failure that is not the PR's (names and conclusions only, never log text). In a CI fix
        /// round (CIHealing) the <see cref="Pushed"/> checkpoint's Detail is the commit the fixer's work was pushed as.
        /// </summary>
        public const string CiFailure = "ci-failure";
        /// <summary>MergeGate: one evaluation of the gate; Detail is its decision and reasons.</summary>
        public const string GateDecision = "gate";
        /// <summary>
        /// MergeGate: the <c>new-tests-fail-on-base</c> check's result for one base/head pair (sc-25382); Detail is the
        /// <see cref="Gate.NewTestsResult"/> JSON — outcome (<c>pass</c>, <c>rejected</c>, <c>no-tests</c>, <c>unsupported</c>,
        /// <c>error</c>; anything but pass fails the check), why, and each new test's cases on the base and the head.
        /// </summary>
        public const string NewTests = "new-tests";
        /// <summary>MergeGate: every rule held for this head commit (Detail) and the gate is merging exactly it.</summary>
        public const string GatePassed = "gate-passed";
        /// <summary>Merge: the board shows the item merged.</summary>
        public const string MergedReported = "merged-reported";
        /// <summary>
        /// MergeGate: the gate approved the head (Detail) and the item joined its repo's merge queue (sc-25384); the row's order
        /// among the repo's queued items is the queue's (FIFO by gate approval, <see cref="Gate.MergeQueue"/>).
        /// </summary>
        public const string Queued = "queued";
        /// <summary>MergeGate: the item took its repo's one merge-queue turn (Detail: its head); it holds it until it leaves MergeGate.</summary>
        public const string QueueTurn = "queue-turn";
        /// <summary>
        /// MergeGate: the head was behind the base and the base was merged into it (Detail: the <see cref="Gate.BaseUpdate"/> JSON:
        /// the old head, the base commit, the merge commit), recorded before the merge commit is pushed.
        /// </summary>
        public const string BaseUpdate = "base-update";
        /// <summary>MergeGate: the head does not merge with the base (Detail: the <see cref="Gate.BaseUpdate"/> JSON with the conflicted files).</summary>
        public const string MergeConflict = "merge-conflict";
        /// <summary>
        /// MergeGate: the old head's verdict was carried to the updated head because the PR's diff is unchanged by the update
        /// (Detail: the <see cref="Gate.ReviewCarry"/> proof), before the carried verdict is recorded.
        /// </summary>
        public const string ReviewCarried = "review-carried";
        /// <summary>
        /// MergeGate: the gated head is about to be merged; Detail is the <see cref="MergeFiles"/> JSON (the PR's base branch and
        /// the files its diff changes), which the freeze's hot-file and main-red triggers read (<see cref="FactoryFreeze"/>).
        /// </summary>
        public const string MergeFiles = "merge-files";
        /// <summary>Fixing (a conflict fix round): the base was merged into the fixer's worktree (Detail: the <see cref="Gate.BaseUpdate"/> JSON with the conflicted files).</summary>
        public const string BaseMerged = "base-merged";
        /// <summary>
        /// The stuck detector (<see cref="StuckDetector"/>, sc-25388) found the running worker session looping, as it found it and
        /// before the worker is interrupted; Detail is why (tool names, never transcript content). The session is never resumed:
        /// its round failed.
        /// </summary>
        public const string Stuck = "stuck";
        /// <summary>
        /// Implement: the stuck implementer's worktree was removed and a fresh session in a fresh worktree retries (Detail: which
        /// session, why, and the count of <see cref="MaxStuckImplementSessions"/>); a new worker attempt starts after it.
        /// </summary>
        public const string StuckRetry = "stuck-retry";
        /// <summary>Merge: the closeout comment (the ledger's facts, <see cref="LedgerReport.MergedCloseout"/>) is on the board item.</summary>
        public const string Closeout = "closeout";
        /// <summary>
        /// Merge: the PR's description was rewritten from the ledger (<see cref="LedgerReport.PullRequestBody"/>); Detail is <c>merge</c>, or
        /// <c>failed: &lt;why&gt;</c> (the description is a report only: a failure is recorded, not escalated).
        /// </summary>
        public const string PrReport = "pr-report";
    }

    /// <summary>
    /// Detail of the Paused row recorded when a Pause control stopped the run. Such an item resumes (its
    /// worker with <c>claude --resume</c>) once no control pauses it any more, i.e. after Continue.
    /// </summary>
    public const string UserPaused = "user-paused";

    /// <summary>
    /// Detail of the Paused row recorded when the factory's usage pause (<see cref="ControlScope.Usage"/>) stopped the run:
    /// every plan was exhausted, or the worker failed with the router's exhaustion or a rate limit. Such an item resumes
    /// (its worker with <c>claude --resume</c>) once the pause lifts at its resume time; it is never escalated for it.
    /// </summary>
    public const string UsagePaused = "usage-paused";

    /// <summary>
    /// Detail of the Paused row recorded when the factory-wide freeze (<see cref="ControlScope.Freeze"/>) stopped the run at a
    /// step or tool boundary. Such an item resumes (its worker with <c>claude --resume</c>) once a human's Continue clears the freeze.
    /// </summary>
    public const string FreezePaused = "freeze-paused";

    /// <summary>A Pause or Stop control reached a run; the worker (if any) has ended.</summary>
    private sealed class ControlRequestedException(ControlState state, bool workerStillRunning = false)
        : Exception($"control: {state}")
    {
        public ControlState State { get; } = state;
        public bool WorkerStillRunning { get; } = workerStillRunning;
        /// <summary>Set when the freeze evaluator (not a control row) paused the run: why it is frozen.</summary>
        public FreezeStatus? Frozen { get; init; }
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

    private Dictionary<WorkState, Func<Run, CancellationToken, Task>> Handlers
    {
        get
        {
            var handlers = new Dictionary<WorkState, Func<Run, CancellationToken, Task>>
            {
                [WorkState.Intake] = IntakeAsync,
                [WorkState.Implement] = ImplementAsync,
            };
            if (gate is not null)
            {
                handlers[WorkState.Review] = ReviewAsync;
                handlers[WorkState.Fixing] = FixAsync;
                handlers[WorkState.CI] = CiAsync;
                handlers[WorkState.CIHealing] = CiFixAsync;
                handlers[WorkState.MergeGate] = MergeGateAsync;
                handlers[WorkState.Merge] = MergeAsync;
            }
            return handlers;
        }
    }

    /// <summary>States the implement stage drives (all a pipeline without a <see cref="GateStage"/> drives).</summary>
    public static readonly IReadOnlySet<WorkState> ImplementStates = new HashSet<WorkState> { WorkState.Intake, WorkState.Implement };

    /// <summary>States the factory's handlers drive (the production pipeline has a <see cref="GateStage"/>); an item in one is in flight.</summary>
    public static readonly IReadOnlySet<WorkState> HandledStates = new HashSet<WorkState>
    {
        WorkState.Intake, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.CI, WorkState.CIHealing, WorkState.MergeGate,
        WorkState.Merge,
    };

    /// <summary>The states this pipeline drives.</summary>
    private IReadOnlySet<WorkState> Handled => gate is null ? ImplementStates : HandledStates;

    /// <summary>
    /// Story ids of items a run should pick up, oldest first: items being stopped (whatever their state), then,
    /// unless a control pauses them, those in a handled state and those <see cref="Interrupted"/> or
    /// <see cref="UserPaused"/> (not parked) while in one.
    /// </summary>
    /// <param name="handled">The states the runner's pipeline drives; default <see cref="HandledStates"/> (production).</param>
    /// <param name="naming">The source whose items to list; default Shortcut stories.</param>
    public static async Task<IReadOnlyList<int>> InFlightAsync(WorkLedger ledger, CancellationToken ct, IControls? controls = null,
        IReadOnlySet<WorkState>? handled = null, ItemNaming? naming = null)
    {
        controls ??= NoControls.Instance;
        handled ??= HandledStates;
        naming ??= ItemNaming.Shortcut;
        var stopping = (await controls.ListAsync(ct)).Where(c => c.State == ControlState.Stopping).Select(c => c.Scope).ToHashSet();
        var ids = new List<int>();
        var active = await ledger.ActiveItemsAsync(naming.Source, ct);
        // A merged item whose closeout failed is listed until it is posted or its attempts are used up (a run retries it from Watch).
        var closeouts = handled.Contains(WorkState.Merge)
            ? await ledger.CloseoutRowsAsync(active.Where(i => i.State == WorkState.Watch).Select(i => i.Id).ToList(), ct)
            : Array.Empty<LedgerEntry>().ToLookup(e => e.WorkItemId);
        foreach (var item in active)
        {
            if (!stopping.Contains(ControlScope.Item(item.ExternalId)))
            {
                var resumable = handled.Contains(item.State)
                    || CloseoutPending(item.State, closeouts[item.Id])
                    || (item.State == WorkState.Paused && ResumesAutomatically(await ledger.HistoryAsync(item, ct), handled));
                if (!resumable || await controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) != ControlState.Running)
                {
                    continue;
                }
            }
            if (naming.TryParse(item.ExternalId, out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static bool ResumesAutomatically(List<LedgerEntry> history, IReadOnlySet<WorkState> handled)
    {
        var paused = history.FindLastIndex(e => e.Step is null);
        var pausedFrom = TransitionContext.From(history.Where(e => e.Step is null).Select(e => e.State).ToList()).PausedFrom;
        return history[paused] is { State: WorkState.Paused, Detail: Interrupted or UserPaused or UsagePaused or FreezePaused }
            && pausedFrom is { } from && handled.Contains(from)
            && !history.Skip(paused + 1).Any(e => e.Step == Steps.Parked);
    }

    /// <summary>Whether a run can move the item: it is in a handled state, escalated (re-queue), or paused from a handled state.</summary>
    private bool Runnable(WorkItem item, TransitionContext context) =>
        Handled.Contains(item.State)
        || item.State == WorkState.Escalated
        || (item.State == WorkState.Paused && context.PausedFrom is { } from && Handled.Contains(from));

    public async Task<RunOutcome> RunAsync(int storyId, CancellationToken ct)
    {
        // Decide from the ledger (and the controls) before reading anything from the board.
        var known = await ledger.FindAsync(Naming.Source, Naming.Format(storyId), ct);
        var knownControl = known is null ? ControlState.Running : await _controls.EffectiveAsync(known.ExternalId, known.EpicId, ct);
        if (known is not null && knownControl == ControlState.Stopping)
        {
            // A stop is finished even while the factory is frozen: it starts no new work.
            return await StopIdleAsync(known, storyId, ct);
        }
        // The freeze evaluator runs before every dispatch (sc-25387): a frozen factory defers the run, changing nothing.
        if (freeze is not null && await freeze.CheckAsync(ct) is { Frozen: true } frozen)
        {
            log.WriteLine($"[deferred] {Naming.Format(storyId)}: {frozen.Message}");
            return new RunOutcome(known?.Id ?? 0, known?.State ?? WorkState.Intake, null, null, $"{Naming.Format(storyId)} deferred: {frozen.Message}")
            {
                Deferred = frozen.Message,
            };
        }
        if (known is not null)
        {
            if (knownControl == ControlState.Paused)
            {
                log.WriteLine($"[{known.State}] {known.ExternalId} is paused by a control; nothing to do.");
                return await OutcomeAsync(known, PausedMessage(known.ExternalId), ct);
            }
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
        if (known is null && await _controls.EffectiveAsync(Naming.Format(storyId), spec.Epic?.Id, ct) is not ControlState.Running and var control)
        {
            // A paused factory or epic claims nothing new: the story stays as it is on the board.
            log.WriteLine($"[intake] {Naming.Format(storyId)} is {control} by a control; not claiming it.");
            return new RunOutcome(0, WorkState.Intake, null, null, $"{Naming.Format(storyId)} is {control} by a control; not claimed.");
        }
        var repo = RepoResolver.Resolve(story.Description, defaultRepo);
        var item = await ledger.GetOrCreateAsync(Naming.Source, Naming.Format(storyId), story.Name, repo.FullName, IntakeDetail(story), ct, spec.Epic?.Id);

        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; not touching it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        // Re-read under the lock: another run may have moved the item since it was loaded.
        await ledger.RefreshAsync(item, story.Name, repo.FullName, spec.Epic?.Id, ct);
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
                // Pause/Stop: no new step starts once a control says so.
                await ThrowIfControlledAsync(item, ct);
                // Nor once the factory is frozen (sc-25387): the triggers are evaluated before every step, so this run's own
                // fix rounds, escalations and merges (and main turning red while it waits) stop it. A merged item's bookkeeping
                // (Merge → Watch) starts no new work and is not held.
                if (item.State != WorkState.Merge)
                {
                    await ThrowIfFrozenAsync(item, ct);
                }
                var before = item.State;
                await handler(run, ct);
                if (item.State == before)
                {
                    throw new InvalidOperationException($"The {before} handler finished without a transition.");
                }
                log.WriteLine($"[{item.State}]");
            }
            // A Stop that arrived during the last step (e.g. while the PR was being opened) is finished now, by this
            // run: the item is not left at Review with a ready PR and a Stopping control nobody acts on. A Pause there
            // has nothing left to pause.
            if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) == ControlState.Stopping)
            {
                throw new ControlRequestedException(ControlState.Stopping);
            }
        }
        catch (MergeQueueWaitException wait)
        {
            // Queued behind another item of the repo (sc-25384): the item stays in MergeGate, and a later run (the next poll)
            // takes its turn once the queue reaches it. Nothing failed.
            log.WriteLine($"[queue] {wait.Message}");
            return await OutcomeAsync(item, null, CancellationToken.None);
        }
        catch (ControlRequestedException request) when (request.State == ControlState.Paused)
        {
            // The worktree, checkpoints and Claude session stay: Continue (or the usage pause lifting) resumes the same session.
            // A user's Pause on the factory, the epic or the item outranks the usage pause: Continue, not the reset, resumes it.
            var userPaused = await UserPausedAsync(item);
            if (!userPaused && await _controls.UsagePauseAsync(CancellationToken.None) is { } usage)
            {
                var why = $"{usage.Reason}; resumes at {usage.ResumeAt:u}";
                await ledger.RecordAsync(item, WorkState.Paused, null, UsagePaused, CancellationToken.None);
                await ledger.CheckpointAsync(item, Steps.UsagePause, null, why, CancellationToken.None);
                log.WriteLine($"[paused] {item.ExternalId} paused for usage ({why}); its worktree and session are kept");
                return await OutcomeAsync(item, $"{item.ExternalId} is paused for usage ({why}).", CancellationToken.None);
            }
            var frozenBy = request.Frozen?.Trigger;
            if (!userPaused && frozenBy is null
                && await _controls.GetAsync(ControlScope.Freeze, CancellationToken.None) is { State: not ControlState.Running } frozenRow)
            {
                frozenBy = frozenRow.Reason;
            }
            if (!userPaused && frozenBy is not null)
            {
                await ledger.RecordAsync(item, WorkState.Paused, null, FreezePaused, CancellationToken.None);
                log.WriteLine($"[paused] {item.ExternalId} paused by the factory freeze ({frozenBy}); its worktree and session are kept for Continue");
                return await OutcomeAsync(item, $"{item.ExternalId} is paused by the factory freeze ({frozenBy}); `factory continue --freeze` clears it.",
                    CancellationToken.None);
            }
            await ledger.RecordAsync(item, WorkState.Paused, null, UserPaused, CancellationToken.None);
            log.WriteLine($"[paused] {item.ExternalId} paused by a control; its worktree and session are kept for Continue");
            return await OutcomeAsync(item, PausedMessage(item.ExternalId), CancellationToken.None);
        }
        catch (ControlRequestedException request)
        {
            return await FinishStopAsync(item, storyId, repo, run.Workspace, request.WorkerStillRunning);
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
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
        var history = await ledger.HistoryAsync(item, ct);
        if (gate is not null && CloseoutPending(item.State, history))
        {
            // The merge's closeout failed: one more attempt, recorded like the first (bounded by MaxCloseoutAttempts).
            var error = await PostCloseoutAsync(item, storyId, ct);
            return await OutcomeAsync(item, error is null ? null : $"{item.ExternalId}: closeout NOT posted: {error}", ct);
        }
        var entered = history.FindLastIndex(e => e.Step is null);
        if (!Runnable(item, await ledger.ContextAsync(item, ct)) && !history.Skip(entered + 1).Any(e => e.Step == Steps.HeldNotice))
        {
            var id = Naming.Format(storyId);
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
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
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
            var id = Naming.Format(storyId);
            var from = (await ledger.ContextAsync(item, ct)).PausedFrom;
            try
            {
                await source.CommentAsync(storyId,
                    $"{id} left the factory's watch scope, so the factory stopped working on it (paused at {from}). "
                    + $"{source.ScopeReturnHint}, or run `factory run {id} --ignore-scope`, to resume it.", ct);
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
                // A source that lists a refused item again by itself bounds the retries: the last refusal escalates (E10).
                if (source.MaxClaimRefusals is { } max && ClaimRefusalsInARow(history) + 1 >= max)
                {
                    throw new InvalidOperationException($"the claim was refused {max} times in a row; last refusal: {claim.Refusal}");
                }
                // Not ours to take any more: write nothing to the board and wait until it is ready again.
                await ledger.RecordAsync(run.Item, WorkState.Paused, null, $"{ClaimRefused}: {claim.Refusal}", ct);
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

    /// <summary>The Paused row's detail prefix when the work source refused the claim.</summary>
    public const string ClaimRefused = "claim refused";

    public static bool IsClaimRefused(string? detail) => detail?.StartsWith(ClaimRefused, StringComparison.Ordinal) == true;

    /// <summary>
    /// The claim refusals (Paused, <see cref="ClaimRefused"/>) since the item last did anything else: counted back over its transitions,
    /// passing the Intake rows a re-run records between them, up to the first other transition.
    /// </summary>
    public static int ClaimRefusalsInARow(IReadOnlyList<LedgerEntry> history)
    {
        var count = 0;
        foreach (var row in history.Where(e => e.Step is null).Reverse())
        {
            if (row.State == WorkState.Paused && IsClaimRefused(row.Detail))
            {
                count++;
            }
            else if (row.State != WorkState.Intake)
            {
                break;
            }
        }
        return count;
    }

    private async Task ImplementAsync(Run run, CancellationToken ct)
    {
        var (spec, repo, item) = run;
        var story = spec.Story;
        var branch = story.Kind.BranchName(story.Id);
        var fullHistory = await ledger.HistoryAsync(item, ct);
        var attempt = CurrentWorkerAttempt(fullHistory);
        var stuckHint = StuckRetryHint(fullHistory);
        var session = attempt.LastOrDefault(e => e.Step == Steps.Session)?.ClaudeSessionId;
        // Every model that answers the implementer is recorded once (implementer-model), for the record of what wrote the code.
        var models = ImplementerModels(fullHistory).ToHashSet(StringComparer.Ordinal);

        // A worker a crashed run left behind must not keep editing (or resume the same session) alongside this one.
        if (OrphanedWorkerPid(attempt) is { } pid && await worker.StopOrphanAsync(pid, ct))
        {
            await ledger.CheckpointAsync(item, Steps.OrphanKilled, session, $"pid {pid}", ct);
            log.WriteLine($"[implement] stopped worker pid {pid} left running by an earlier run");
        }

        // A session found looping is never resumed (a run that stopped after the detection left only its checkpoint).
        if (StuckSession(attempt) is { } found)
        {
            run.Workspace = await workspaces.ReopenAsync(repo, branch, ct);
            await StuckImplementAsync(run, found, ct);
            return;
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
            try
            {
                session = await RunWorkerSessionAsync(run, workspace, session, resume => resume is null ? BuildPrompt(spec, repo) + stuckHint : BuildResumePrompt(story),
                    models, [SpecInput(story)], "implement", ct);
            }
            catch (WorkerStuckException stuckSession) when (!WorkerStillRunning.IsMarked(stuckSession))
            {
                await StuckImplementAsync(run, stuckSession, ct);
                return;
            }
        }
        await ThrowIfControlledAsync(item, ct);

        if (!attempt.Any(e => e.Step == Steps.Pushed))
        {
            var grant = await GrantPushAsync(item, session, ct);
            if (!await workspaces.CommitAndPushAsync(repo, workspace, $"{story.Ref}: {story.PublicName}", grant, ct))
            {
                throw new InvalidOperationException("Worker finished without changing the repository; nothing to review.");
            }
            await ledger.CheckpointAsync(item, Steps.Pushed, session, workspace.Branch, ct);
        }

        // A Stop or Pause that arrived during the push: no ready PR is opened for a stopped item.
        await ThrowIfControlledAsync(item, ct);
        // Returns the branch's already-open PR instead of opening a second one.
        var prUrl = await pullRequests.OpenAsync(repo, workspace.Branch, workspace.BaseBranch,
            $"{story.Ref}: {story.PublicName}", await PullRequestBodyAsync(run, ct), ct);
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

    /// <summary>
    /// The attempt's worker session the stuck detector stopped (its <see cref="Steps.Stuck"/> checkpoint), unless the worker then
    /// finished on its own (<see cref="Steps.WorkerDone"/> after it: no tool boundary came, and its work is judged like any other).
    /// </summary>
    private static WorkerStuckException? StuckSession(List<LedgerEntry> attempt)
    {
        var found = attempt.FindLastIndex(e => e.Step == Steps.Stuck);
        return found >= 0 && !attempt.Skip(found + 1).Any(e => e.Step == Steps.WorkerDone)
            ? new WorkerStuckException(attempt[found].Detail ?? "stuck", attempt[found].ClaudeSessionId)
            : null;
    }

    /// <summary>
    /// For a fresh implementer that retries after a stuck one (a <see cref="Steps.StuckRetry"/> since Implement began): one line
    /// naming the tool calls the stuck session kept repeating — tool names only, from the detector's reason, never transcript
    /// content (E4). Empty otherwise.
    /// </summary>
    private static string StuckRetryHint(List<LedgerEntry> history)
    {
        var entered = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Implement && e.Detail != "unpaused");
        var retry = history.FindLastIndex(e => e.Step == Steps.StuckRetry);
        if (retry <= entered)
        {
            return "";
        }
        var tools = StuckDetector.RepeatedTools(history.Take(retry).LastOrDefault(e => e.Step == Steps.Stuck)?.Detail);
        return "\n\nAn earlier session on this story was stopped because it was stuck in a loop, repeating the same "
            + (tools.Count > 0 ? $"tool calls ({string.Join(", ", tools)})" : "turns")
            + " with the same results; its edits were discarded. Take a different approach rather than repeating them.";
    }

    /// <summary>
    /// Implement's failed round (sc-25388): the implementer was found looping and interrupted. Implement has no fix rounds, so its
    /// round is the session: the first stuck session since the item entered Implement is retried once by a fresh session in a fresh
    /// worktree (the looping session's edits are discarded, never pushed; <see cref="Steps.StuckRetry"/> starts a new attempt), and
    /// the <see cref="MaxStuckImplementSessions"/>th escalates. A worktree that cannot be removed escalates too (the retry must not
    /// build on the looping session's edits).
    /// </summary>
    private async Task StuckImplementAsync(Run run, WorkerStuckException stuckSession, CancellationToken ct)
    {
        var item = run.Item;
        var history = await ledger.HistoryAsync(item, ct);
        var entered = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Implement && e.Detail != "unpaused");
        var count = history.Skip(entered + 1).Count(e => e.Step == Steps.Stuck);
        if (count >= MaxStuckImplementSessions)
        {
            throw new WorkerFailedException(
                $"The implementer was stuck in a loop in {count} sessions in a row (the cap is {MaxStuckImplementSessions}), so Implement is not "
                + $"retried again. Last: session {stuckSession.Session ?? "(unnamed)"}: {stuckSession.Reason}");
        }
        if (run.Workspace is { } workspace && !await RemoveWorktreeAsync(run.Repo, workspace))
        {
            throw new WorkerFailedException(
                $"The implementer session {stuckSession.Session ?? "(unnamed)"} was stuck in a loop ({stuckSession.Reason}), and its worktree could "
                + "not be removed, so no fresh session can start clean.");
        }
        run.Workspace = null;
        await ledger.CheckpointAsync(item, Steps.StuckRetry, stuckSession.Session,
            $"session {stuckSession.Session ?? "(unnamed)"} stuck ({stuckSession.Reason}); a fresh session retries Implement "
            + $"(stuck session {count} of {MaxStuckImplementSessions})", ct);
        log.WriteLine($"[implement] the worker was stuck in a loop; retrying with a fresh session (stuck session {count} of {MaxStuckImplementSessions})");
        await ThrowIfControlledAsync(item, ct);
        await ImplementAsync(run, ct);
    }

    /// <summary>
    /// Runs one worker session in <paramref name="workspace"/> (the implementer's, or a fix round's), continuing
    /// <paramref name="session"/> when set: every stdout line is stored as it streams (E7), the pid, the session id and each
    /// model that answers are checkpointed as they appear (models into <paramref name="models"/>, each recorded once), and
    /// Pause/Stop are watched. A paused or usage-limited session
    /// throws <see cref="ControlRequestedException"/> (resumed later); a failed one throws <see cref="WorkerFailedException"/>.
    /// On success it checkpoints <see cref="Steps.WorkerDone"/> and returns the session id.
    /// </summary>
    private async Task<string?> RunWorkerSessionAsync(Run run, Workspace workspace, string? session, Func<string?, string> prompt,
        HashSet<string> models, IReadOnlyCollection<WorkerInput> inputs, string label, CancellationToken ct)
    {
        var item = run.Item;
        var resume = session;
        // The target repo's own Claude settings may feed the session content its stream would not show (hooks, MCP, extra allow rules).
        var taints = inputs.Select(Taint.Of).Append(Taint.OfRepoSettings(workspace.Path)).OfType<string>().ToList();
        // Every worker this runs exists to push its work: a tainted session would only be refused its push token after spending
        // the model's time, so it is not resumed at all (E4). Its stored stream is replayed first: a run stopped between storing
        // a web tool's line and committing its taint left the use only there.
        if (resume is not null)
        {
            await ledger.ReplayTaintsAsync(item, resume, ct);
            foreach (var reason in taints)
            {
                await ledger.TaintSessionAsync(item, resume, reason, ct);
            }
            if (await ledger.TaintOfAsync(resume, ct) is { } taint)
            {
                throw new SessionTaintedException(resume, taint.Reason);
            }
        }
        // No worker session starts into a frozen factory (implement, a fix round, a CI fix).
        await ThrowIfFrozenAsync(item, ct);
        log.WriteLine(resume is null ? $"[{label}] starting worker" : $"[{label}] resuming claude session {resume}");
        // Every stdout line is stored (and pushed to live viewers) as it streams (E7).
        await using var capture = sessions is null ? null : await sessions.StartAsync(item.Id, resume, ct);
        // Pause asks the worker to stop at its next tool boundary (and stops it if it doesn't); Stop stops it now.
        await using var watch = new ControlWatch(_controls, worker, log, _controlPoll, _pauseGrace, item.ExternalId, item.EpicId, workspace.Path, ct);
        // A looping session is interrupted at its next tool boundary like a Pause, but never resumed (sc-25388).
        var detector = new StuckDetector(_stuck);
        string? stuckReason = null;
        WorkerResult result;
        try
        {
            result = await worker.RunAsync(workspace.Path, prompt(resume), resume,
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
                            // Tainted by what it was handed before the ledger points at the session (E4).
                            foreach (var reason in taints)
                            {
                                await ledger.TaintSessionAsync(item, sid, reason, c);
                            }
                            await ledger.CheckpointAsync(item, Steps.Session, sid, "claude session started", c);
                        }
                    },
                    OnLine: async (line, c) =>
                    {
                        if (capture is not null)
                        {
                            await capture.OnLineAsync(line, c);
                        }
                        if (detector.Accept(line) is { } reason)
                        {
                            // On the ledger before the interrupt (and on a token nothing cancels): a run that stops now must not
                            // resume the looping session.
                            stuckReason = reason;
                            await ledger.CheckpointAsync(item, Steps.Stuck, session, reason, CancellationToken.None);
                            log.WriteLine($"[{label}] session {session ?? "(unnamed)"} is stuck: {reason}; interrupting it at its next tool call");
                            watch.InterruptStuck();
                        }
                    },
                    OnModel: async (model, c) =>
                    {
                        if (models.Add(model))
                        {
                            await ledger.CheckpointAsync(item, Steps.ImplementerModel, session, model, c);
                        }
                    },
                    OnUntrusted: async (sid, reason, c) =>
                    {
                        await ledger.TaintSessionAsync(item, sid, reason, c);
                        log.WriteLine($"[{label}] session {sid} used {reason}: tainted, it gets no push token");
                    }), watch.Token);
        }
        catch (Exception ex) when (watch.Requested is { } requested && !ct.IsCancellationRequested)
        {
            // The control cut the worker off (Stop, or a pause it did not honour in time).
            await CompleteControlledSessionAsync(capture, requested);
            throw new ControlRequestedException(requested, WorkerStillRunning.IsMarked(ex));
        }
        catch (Exception ex) when (stuckReason is not null && watch.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The stuck worker did not reach a tool boundary within the grace and was stopped: still a failed round.
            await CompleteStuckSessionAsync(capture);
            var failed = new WorkerStuckException(stuckReason, session);
            throw WorkerStillRunning.IsMarked(ex) ? WorkerStillRunning.Mark(failed) : failed;
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
                log.WriteLine($"[{label}] could not record the end of the worker session: {captureError.Message}");
            }
            throw;
        }
        if (stuckReason is not null && watch.Requested != ControlState.Stopping && result.HookStopped)
        {
            // Stopped at a tool boundary for looping (even if a Pause also asked): the round failed; the session is not resumed.
            // A worker that finished on its own after the detection (no tool boundary came) is done, like one after a pause.
            await CompleteStuckSessionAsync(capture);
            throw new WorkerStuckException(stuckReason, result.SessionId ?? session);
        }
        if (watch.Requested == ControlState.Stopping || (watch.PauseRequested && result.HookStopped))
        {
            // Stopped; or the worker stopped at a tool boundary (the pause hook ends the session as a success,
            // so its result says nothing about the story being done): it resumes on Continue. A worker that
            // finished on its own after a pause request is done: WorkerDone is recorded below and the pause
            // takes effect before the push (E3: Continue does not run a finished worker again).
            var requested = watch.Requested == ControlState.Stopping ? ControlState.Stopping : ControlState.Paused;
            await CompleteControlledSessionAsync(capture, requested);
            throw new ControlRequestedException(requested);
        }
        // The plans ran out, not the work: pause the factory (backing off) and resume this session afterwards, rather
        // than escalate the item. Without a control table nothing could hold the pause, so the failure escalates.
        if (result.UsageLimited && await _controls.PauseForUsageAsync(null, UsagePause.WorkerRateLimited, ct) is { State: ControlState.Paused } pause)
        {
            log.WriteLine($"[{label}] worker hit a router exhaustion or rate-limit error; factory paused for usage until {pause.ResumeAt:u}");
            await CompleteControlledSessionAsync(capture, ControlState.Paused);
            throw new ControlRequestedException(ControlState.Paused);
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
        // The worker can write the repo's settings itself (a hook it added may have run in-session): checked again before it is done.
        if (session is not null && Taint.OfRepoSettings(workspace.Path) is { } changed)
        {
            await ledger.TaintSessionAsync(item, session, changed, ct);
        }
        await ledger.CheckpointAsync(item, Steps.WorkerDone, session, $"worker exit {result.ExitCode}", ct);
        return session;
    }

    /// <summary>What the worker handed <paramref name="story"/> reads: the owner's story, or an issue item's approved triage (never the issue's text).</summary>
    private static WorkerInput SpecInput(WorkStory story) =>
        story.Kind == ItemNaming.GitHubIssue ? WorkerInput.TriageSummary : WorkerInput.Story;

    /// <summary>
    /// The push grant for the current worker attempt (E4): every session the attempt recorded (read fresh, so a session id that
    /// changed on resume is included) plus <paramref name="session"/>, none of them tainted.
    /// </summary>
    private async Task<PushGrant> GrantPushAsync(WorkItem item, string? session, CancellationToken ct)
    {
        var sessions = CurrentWorkerAttempt(await ledger.HistoryAsync(item, ct))
            .Where(e => e.Step == Steps.Session).Select(e => e.ClaudeSessionId).Append(session);
        return await ledger.GrantPushAsync(sessions, ct);
    }

    private async Task RemoveWorktreeAsync(Run run)
    {
        if (run.Workspace is not null && await RemoveWorktreeAsync(run.Repo, run.Workspace))
        {
            run.Workspace = null;
        }
    }

    private async Task<bool> RemoveWorktreeAsync(RepoRef repo, Workspace workspace)
    {
        try
        {
            await workspaces.RemoveAsync(repo, workspace, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            log.WriteLine($"[cleanup] could not remove worktree {workspace.Path}: {ex.Message}; the next run's sweep retries");
            return false;
        }
    }

    private static string PausedMessage(string externalId) => $"{externalId} is paused by a control; `factory continue` resumes it.";

    private async Task ThrowIfControlledAsync(WorkItem item, CancellationToken ct)
    {
        if (await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) is not ControlState.Running and var state)
        {
            throw new ControlRequestedException(state);
        }
    }

    /// <summary>
    /// Runs the freeze evaluator (sc-25387) and, when the factory is frozen, pauses the run like a Pause control (Paused
    /// <see cref="FreezePaused"/>, worktree and session kept; it resumes after the Continue). Called before every step, before
    /// every worker session starts, before a merge-queue base-update push and (reading GitHub fresh, <paramref name="fresh"/>)
    /// right before a merge. Its cost is bounded: the ledger triggers are a few queries; main-red's GitHub reads are reused for
    /// <see cref="FactoryFreeze.MainRedCacheTtl"/> except right before a merge.
    /// </summary>
    private async Task ThrowIfFrozenAsync(WorkItem item, CancellationToken ct, bool fresh = false)
    {
        if (freeze is not null && await freeze.CheckAsync(ct, fresh) is { Frozen: true } frozen)
        {
            log.WriteLine($"[frozen] {item.ExternalId}: {frozen.Message}");
            throw new ControlRequestedException(ControlState.Paused) { Frozen = frozen };
        }
    }

    /// <summary>A paused session resumes later, so its cost waits for the run that finishes it; a stopped one's is fetched now (bounded).</summary>
    private async Task CompleteControlledSessionAsync(SessionRecorder.SessionCapture? capture, ControlState requested)
    {
        if (capture is null)
        {
            return;
        }
        var stopped = requested == ControlState.Stopping;
        using var bounded = new CancellationTokenSource(_failedSessionEndTimeout);
        try
        {
            await capture.CompleteAsync(null, stopped ? "stopped" : "paused", fetchCost: stopped, bounded.Token).WaitAsync(bounded.Token);
        }
        catch (Exception captureError)
        {
            log.WriteLine($"[implement] could not record the end of the worker session: {captureError.Message}");
        }
    }

    /// <summary>A stuck session is never resumed, so it ends now: exit status <c>stuck</c> and its cost fetched (bounded).</summary>
    private async Task CompleteStuckSessionAsync(SessionRecorder.SessionCapture? capture)
    {
        if (capture is null)
        {
            return;
        }
        using var bounded = new CancellationTokenSource(_failedSessionEndTimeout);
        try
        {
            await capture.CompleteAsync(null, "stuck", fetchCost: true, bounded.Token).WaitAsync(bounded.Token);
        }
        catch (Exception captureError)
        {
            log.WriteLine($"[stuck] could not record the end of the worker session: {captureError.Message}");
        }
    }

    /// <summary>Whether a user's Pause holds the item: on the factory, its epic or the item itself.</summary>
    private async Task<bool> UserPausedAsync(WorkItem item)
    {
        string[] scopes = item.EpicId is { } epic
            ? [ControlScope.Factory, ControlScope.Epic(epic), ControlScope.Item(item.ExternalId)]
            : [ControlScope.Factory, ControlScope.Item(item.ExternalId)];
        foreach (var scope in scopes)
        {
            if ((await _controls.GetAsync(scope, CancellationToken.None))?.State == ControlState.Paused)
            {
                return true;
            }
        }
        return false;
    }

    private ItemStopper Stopper => new(source, ledger, locks, pullRequests, _controls, log);

    /// <summary>
    /// Stops an item no run is driving (its Stopping control was set while it was idle, or an earlier stop did
    /// not finish): stops a worker a crashed run left behind, then cancels the item and removes its worktree.
    /// </summary>
    private async Task<RunOutcome> StopIdleAsync(WorkItem item, int storyId, CancellationToken ct)
    {
        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; its run stops it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        await ledger.RefreshAsync(item, item.Title, item.Repo, item.EpicId, ct);
        var repo = RepoRef.Parse(item.Repo);
        Workspace? workspace = null;
        if (!Lifecycle.IsTerminal(item.State))
        {
            var attempt = CurrentWorkerAttempt(await ledger.HistoryAsync(item, ct));
            if (OrphanedWorkerPid(attempt) is { } pid && await worker.StopOrphanAsync(pid, ct))
            {
                await ledger.CheckpointAsync(item, Steps.OrphanKilled, null, $"pid {pid}", ct);
            }
            workspace = await workspaces.ReopenAsync(repo, Naming.BranchName(storyId), ct);
        }
        return await FinishStopAsync(item, storyId, repo, workspace, workerStillRunning: false);
    }

    /// <summary>
    /// Cancels the item (its worker has ended): PRs back to draft, story to the Backlog with a comment, Cancelled,
    /// then its worktree removed (E5) unless the worker may still be running in it. A stop that fails keeps the
    /// item's Stopping control and says so; the next run or poll finishes it.
    /// </summary>
    private async Task<RunOutcome> FinishStopAsync(WorkItem item, int storyId, RepoRef repo, Workspace? workspace, bool workerStillRunning)
    {
        string? error = null;
        try
        {
            log.WriteLine($"[stopping] {await Stopper.StopLockedAsync(item, storyId, CancellationToken.None)}");
        }
        catch (Exception ex)
        {
            error = $"stop of {item.ExternalId} not finished ({ex.Message}); it stays Stopping and the next poll or `factory stop` retries it";
            log.WriteLine($"[stopping] {error}");
        }
        if (workspace is not null && !workerStillRunning)
        {
            await RemoveWorktreeAsync(repo, workspace);
        }
        else if (workspace is not null)
        {
            log.WriteLine($"[cleanup] worker did not stop; leaving {workspace.Path} for the next run's sweep");
        }
        return await OutcomeAsync(item, error, CancellationToken.None);
    }

    /// <summary>
    /// Watches a running worker's controls (polling, so it sees writes from any process). Pause: asks the worker
    /// to stop at its next tool boundary (<see cref="IWorker.RequestPause"/>) and cancels the run if it has not
    /// ended within the pause grace, since the worker could tamper with anything it can write. Continue before then
    /// withdraws the request (<see cref="IWorker.CancelPause"/>) and the grace. Stop: cancels at once.
    /// </summary>
    private sealed class ControlWatch : IAsyncDisposable
    {
        private readonly IControls _controls;
        private readonly IWorker _workerProcess;
        private readonly TextWriter _log;
        private readonly TimeSpan _poll;
        private readonly TimeSpan _grace;
        private readonly CancellationTokenSource _worker;
        private readonly CancellationTokenSource _done = new();
        private readonly Task _loop;
        private readonly string _workingDirectory;
        private int _requested = -1;
        private int _pauseRequested;
        private int _stuck;

        public ControlWatch(IControls controls, IWorker worker, TextWriter log, TimeSpan poll, TimeSpan grace, string externalId, long? epicId, string workingDirectory, CancellationToken ct)
        {
            (_controls, _workerProcess, _log, _poll, _grace, _workingDirectory) = (controls, worker, log, poll, grace, workingDirectory);
            _worker = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = Task.Run(() => WatchAsync(externalId, epicId, workingDirectory));
        }

        /// <summary>Cancelled by Ctrl-C, by Stop, or when a paused worker overruns its grace.</summary>
        public CancellationToken Token => _worker.Token;

        /// <summary>The control that reached the run, if any.</summary>
        public ControlState? Requested => Volatile.Read(ref _requested) is var r and >= 0 ? (ControlState)r : null;

        /// <summary>Whether this run ever asked its worker to pause (even if Continue withdrew it since).</summary>
        public bool PauseRequested => Volatile.Read(ref _pauseRequested) == 1;

        /// <summary>Whether the stuck detector interrupted the worker (<see cref="InterruptStuck"/>).</summary>
        public bool Stuck => Volatile.Read(ref _stuck) == 1;

        /// <summary>
        /// The stuck detector found the worker looping (sc-25388): asks it to stop at its next tool boundary now, through the same
        /// mechanism as a Pause (<see cref="IWorker.RequestPause"/>), and the watch stops it if it has not within the pause grace.
        /// A Continue does not withdraw it. Once only.
        /// </summary>
        public void InterruptStuck()
        {
            if (Interlocked.Exchange(ref _stuck, 1) == 1)
            {
                return;
            }
            try
            {
                _workerProcess.RequestPause(_workingDirectory);
            }
            catch (Exception ex)
            {
                _log.WriteLine($"[stuck] could not ask the worker to stop ({ex.Message}); stopping it at the deadline");
            }
        }

        private async Task WatchAsync(string externalId, long? epicId, string workingDirectory)
        {
            var log = _log;
            DateTimeOffset? deadline = null;
            var pausedByControl = false;
            while (true)
            {
                try
                {
                    await Task.Delay(_poll, _done.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                ControlState state;
                try
                {
                    state = await _controls.EffectiveAsync(externalId, epicId, _done.Token);
                }
                catch (OperationCanceledException) when (_done.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[control] could not read the controls of {externalId}: {ex.Message}; retrying");
                    continue;
                }
                if (state == ControlState.Stopping)
                {
                    Volatile.Write(ref _requested, (int)ControlState.Stopping);
                    log.WriteLine($"[control] {externalId} is being stopped; stopping its worker");
                    await _worker.CancelAsync();
                    return;
                }
                var stuck = Stuck;
                if (stuck && deadline is null)
                {
                    // The stuck interrupt (InterruptStuck) asked the worker to stop at its next tool call: the pause grace applies too.
                    deadline = DateTimeOffset.UtcNow + _grace;
                }
                if (state == ControlState.Running && pausedByControl)
                {
                    // Continue before the worker reached a tool boundary: its next tool call proceeds, no grace kill — unless it is
                    // stuck, whose interrupt a Continue does not withdraw.
                    pausedByControl = false;
                    Volatile.Write(ref _requested, -1);
                    if (!stuck)
                    {
                        deadline = null;
                        log.WriteLine($"[control] {externalId} continued; its worker goes on");
                        try
                        {
                            _workerProcess.CancelPause(workingDirectory);
                        }
                        catch (Exception ex)
                        {
                            log.WriteLine($"[control] could not withdraw the worker's pause ({ex.Message}); it stops at its next tool call");
                        }
                    }
                }
                if (state == ControlState.Paused && !pausedByControl)
                {
                    pausedByControl = true;
                    Volatile.Write(ref _requested, (int)ControlState.Paused);
                    Volatile.Write(ref _pauseRequested, 1);
                    deadline ??= DateTimeOffset.UtcNow + _grace;
                    log.WriteLine($"[control] {externalId} paused; its worker stops at its next tool call");
                    try
                    {
                        _workerProcess.RequestPause(workingDirectory);
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"[control] could not ask the worker to pause ({ex.Message}); stopping it at the deadline");
                    }
                }
                if (deadline is { } d && DateTimeOffset.UtcNow >= d)
                {
                    log.WriteLine($"[control] {externalId}'s worker did not stop at a tool boundary within {_grace}; stopping it (session and worktree kept)");
                    await _worker.CancelAsync();
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _done.CancelAsync();
            await _loop;
            _done.Dispose();
            _worker.Dispose();
        }
    }

    /// <summary>
    /// Whether a worktree directory (<c>factory-sc-&lt;id&gt;</c>, <c>factory-gh-&lt;id&gt;</c>) belongs to an item a re-run would resume
    /// in it (Implement, a fix round in Fixing or CIHealing, or Paused), so the startup sweep must keep it. Everything else is an orphan
    /// (a triage worktree, <c>factory-triage-gh-&lt;id&gt;</c>, never is).
    /// </summary>
    public static async Task<bool> WorktreeIsResumableAsync(WorkLedger ledger, string worktreeName, CancellationToken ct)
    {
        const string prefix = "factory-";
        if (!worktreeName.StartsWith(prefix, StringComparison.Ordinal) || ItemNaming.ParseAny(worktreeName[prefix.Length..]) is not { } item)
        {
            return false;
        }
        return await ledger.StateOfAsync(item.Naming.Source, item.ToString(), ct) is WorkState.Implement or WorkState.Fixing or WorkState.CIHealing
            or WorkState.Paused;
    }

    /// <summary>
    /// Rows of the current worker attempt: from the last entry into Implement, Fixing or CIHealing (a fix round; a return
    /// from Paused continues the attempt) or the last lost-worktree restart.
    /// </summary>
    private static List<LedgerEntry> CurrentWorkerAttempt(List<LedgerEntry> history)
    {
        var start = 0;
        WorkState? previous = null;
        for (var i = 0; i < history.Count; i++)
        {
            var e = history[i];
            if (e.Step is null)
            {
                if (e.State is WorkState.Implement or WorkState.Fixing or WorkState.CIHealing && previous != WorkState.Paused)
                {
                    start = i;
                }
                previous = e.State;
            }
            else if (e.Step == Steps.WorktreeLost)
            {
                start = i;
            }
            else if (e.Step == Steps.StuckRetry)
            {
                // The stuck session's worktree is gone: the retry starts with nothing of the attempt before it.
                start = i + 1;
            }
        }
        return history.Skip(start).ToList();
    }

    /// <summary>
    /// Whether the item's last worker was started by a run that recorded nothing after it (no WorkerDone, no
    /// transition such as Paused or Escalated, no orphan kill): that run crashed and the worker may still be running.
    /// </summary>
    internal static bool CrashedWorkerMayBeRunning(List<LedgerEntry> history)
    {
        var attempt = CurrentWorkerAttempt(history);
        var started = attempt.FindLastIndex(e => e.Step == Steps.WorkerStarted);
        return OrphanedWorkerPid(attempt) is not null
            && !attempt.Skip(started + 1).Any(e => e.Step is null || e.Step == Steps.OrphanKilled);
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

    /// <summary>
    /// <c>factory work</c> gives up on an item whose runs keep failing before the pipeline starts (E10; see
    /// <see cref="IItemRunner.GiveUpAsync"/>): under the item's run lock, an item in a handled state is escalated with
    /// the usual escalation comment; a paused one (which may not escalate) is parked with a comment, so it no longer
    /// resumes by itself; an item the ledger does not know gets only the comment. Returns what was done, or null.
    /// </summary>
    public static async Task<string?> GiveUpAsync(IWorkSource source, WorkLedger ledger, IRunLocks locks, int storyId, string reason,
        TextWriter log, CancellationToken ct)
    {
        var id = source.Naming.Format(storyId);
        var known = await ledger.FindAsync(source.Naming.Source, id, ct);
        if (known is null)
        {
            await source.CommentAsync(storyId,
                $"{id}: the factory could not start work on this {source.Naming.Noun}; a human needs to look. Reason: {reason}", ct);
            log.WriteLine($"[intake] {id}: {reason}; commented");
            return "commented";
        }
        await using var runLock = await locks.TryAcquireAsync(known.Id, ct);
        if (runLock is null)
        {
            return null;
        }
        await ledger.RefreshAsync(known, known.Title, known.Repo, known.EpicId, ct);
        if (HandledStates.Contains(known.State))
        {
            var history = await ledger.HistoryAsync(known, ct);
            var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
            await ledger.RecordAsync(known, WorkState.Escalated, session, reason, ct);
            log.WriteLine($"[escalated] {id}: {reason}");
            var commentError = await PostEscalationCommentAsync(source, ledger, log, known, storyId, ct);
            return commentError is null ? "escalated" : $"escalated; {EscalationCommentNotPosted(commentError)}";
        }
        if (known.State == WorkState.Paused)
        {
            await ledger.CheckpointAsync(known, Steps.Parked, null, reason, ct);
            log.WriteLine($"[paused] {id} parked: {reason}");
            try
            {
                await source.CommentAsync(storyId,
                    $"{id} is paused and the factory could not resume it, so it parked it; a human needs to look. Reason: {reason}\n\n"
                    + $"Re-run with `factory run {id}` once resolved.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.WriteLine($"[paused] could not comment on {id}: {ex.Message}");
                return $"parked; comment NOT posted: {ex.Message}";
            }
            return "parked";
        }
        return null;
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
    private Task<string?> PostEscalationCommentAsync(Run run, CancellationToken ct) =>
        PostEscalationCommentAsync(source, ledger, log, run.Item, run.Story.Id, ct);

    private static async Task<string?> PostEscalationCommentAsync(IWorkSource source, WorkLedger ledger, TextWriter log, WorkItem item, int storyId,
        CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(item, ct);
        var at = history.FindLastIndex(e => e.Step is null && e.State == WorkState.Escalated);
        var (escalated, last) = (history[at], history[at - 1]);
        var (reason, session) = (escalated.Detail, escalated.ClaudeSessionId);
        var lastState = $"{last.State}{(last.Step is null ? "" : $" (after step {last.Step})")} at {last.RecordedAt:u}";

        // The closeout on escalation (sc-25389): the reason (factory text that can quote model findings, so fenced as data) and the
        // ledger's facts, never a worker's summary.
        var comment = $"""
            [author: dark-factory] {source.Naming.Format(storyId)} escalated; a human needs to look.

            Reason:
            {UntrustedText.Fenced(LedgerReport.Clip(reason ?? "none recorded", LedgerReport.MaxReasonLength))}
            Last ledger state: {lastState}
            Claude session: {session ?? "none"}

            Re-run with `factory run {source.Naming.Format(storyId)}` once resolved.

            """ + "\n" + LedgerReport.Facts(history, await ledger.SessionCostsAsync(item, ct)).TrimEnd();
        try
        {
            await source.CommentAsync(storyId, comment, ct);
        }
        catch (Exception ex)
        {
            await ledger.CheckpointAsync(item, Steps.EscalationComment, session, $"failed: {ex.Message}", CancellationToken.None);
            log.WriteLine($"[escalated] could not comment on {source.Naming.Format(storyId)}: {ex.Message}; `factory run {source.Naming.Format(storyId)}` retries it");
            return ex.Message;
        }
        await ledger.CheckpointAsync(item, Steps.EscalationComment, session, "posted", CancellationToken.None);
        return null;
    }

    private async Task<RunOutcome> OutcomeAsync(WorkItem item, string? error, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(item, ct);
        var session = history.LastOrDefault(e => e.ClaudeSessionId is not null)?.ClaudeSessionId;
        var pr = item.State is WorkState.Review or WorkState.Fixing or WorkState.CI or WorkState.CIHealing or WorkState.MergeGate or WorkState.Merge or WorkState.Watch
            ? LinkedPullRequestUrl(history)
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
            Implement {story.Kind.Noun} {story.Ref} ({story.StoryType}): {PromptFence.Spec(story)}

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
        You were interrupted while implementing {story.Kind.Noun} {story.Ref}.
        Check the current state of the worktree and finish the story as originally instructed.
        """;

    /// <summary>
    /// The PR's description, rendered from the item's ledger rows and recorded session costs (<see cref="LedgerReport.PullRequestBody"/>): it
    /// links back to the item and, for an item that closes an issue (<see cref="WorkStory.Closes"/>), carries GitHub's closing keyword.
    /// </summary>
    private async Task<string> PullRequestBodyAsync(Run run, CancellationToken ct) =>
        LedgerReport.PullRequestBody(run.Story, await ledger.HistoryAsync(run.Item, ct), await ledger.SessionCostsAsync(run.Item, ct));
}
