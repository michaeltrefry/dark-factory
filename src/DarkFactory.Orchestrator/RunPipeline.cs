using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator;

/// <summary>
/// What a run left: the item's state, its last Claude session, its pull request (the ledger's <c>linked</c> PR, in any state,
/// escalated included; null when it never got one) and why the run did not succeed.
/// </summary>
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
/// reports it In Progress; Implement links the PR and branch before the item reaches Review. An item labelled <c>plan</c> on a
/// source that plans (<see cref="IWorkSource.PlansItems"/>) goes to Plan instead (sc-25870, <see cref="PlanAsync"/>): a read-only
/// planner session proposes stories, the code checks the plan and creates them, and the item waits on them in
/// <see cref="WorkState.Planned"/>, never implemented itself: each run of it releases the stories whose blockers have merged and,
/// once every story has merged, closes the plan out (sc-25871, <see cref="PlannedAsync"/>).
/// A claim the work source refuses (the story left To Do or the watch scope, or another claimant holds
/// it) parks the item in Paused with no board write; it is picked up again when it is ready again. So does the claim of a plan's
/// story while a story that blocks it has not merged (<see cref="BlockedAsync"/>): it starts once they have.
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
    StuckDetection? stuck = null,
    int? maxControlReadFailures = null,
    Plans.IPlanRunner? planner = null,
    IReadOnlyList<RepoRef>? configuredRepos = null,
    Worker.WorkerSlots? slots = null,
    Func<CancellationToken, Task>? enterSandbox = null)
{
    /// <summary>
    /// The worker slots (sc-25989, E6/E7; null unsandboxed: workers then run as the owner and share no user): one sandbox user
    /// each, with its own lock (sc-25988). A run holds a slot only for its worker phases (<see cref="InWorkerPhaseAsync"/>) — a
    /// worker session with its worktree's set-up, push and removal (implement, a review, CI or conflict fix round, a planner's
    /// seat), the gate's sandboxed test runs, the stop of a worker a crashed run left — and waits on reviews, CI, CodeGraph and the
    /// merge queue without one, so other runs' workers run meanwhile; with several slots, also during its worker phases.
    /// </summary>
    private readonly Worker.WorkerSlots? _slots = slots;

    /// <summary>Whether this run has done its sandbox set-up (<c>enterSandbox</c>): once, in its first worker phase.</summary>
    private bool _sandboxEntered;

    /// <summary>The slot this run has read or recorded for an item (by item id), so a later phase of the run reads no history for it.</summary>
    private readonly Dictionary<long, int> _slotOf = [];

    /// <summary>
    /// The slot the ledger has for an item with <paramref name="history"/> (sc-25989, E7), or null when it has had no worker
    /// phase: its last <see cref="Steps.Slot"/> row; or slot 1 for an item with a <see cref="Steps.WorkerStarted"/> row and no
    /// slot row — a worker ran for it before the factory had slots, or while it had one (a one-slot factory records none), and
    /// that worker was the first sandbox user's.
    /// </summary>
    internal static int? SlotOf(IReadOnlyList<LedgerEntry> history) =>
        history.LastOrDefault(e => e.Step == Steps.Slot)?.Detail?.Split(' ')[0] is { } text && int.TryParse(text, out var slot) && slot >= 1
            ? slot
            : history.Any(e => e.Step == Steps.WorkerStarted) ? 1 : null;

    /// <summary>
    /// Runs <paramref name="phase"/> as a worker phase: holding a worker slot's lock, waited for (never failed on) while another
    /// run — of this process or another, e.g. a <c>factory run</c> beside <c>factory work</c> — has it. Which slot (sc-25989, E7):
    /// the one the ledger recorded for the item (<see cref="SlotOf"/>; <paramref name="slotOf"/> names the item when
    /// <paramref name="item"/> is null) — only that one, however long it is busy, since the item's session lives in that sandbox
    /// user's home, its worktrees are shared with that user, and a worker a crashed run left is that user's process; an item
    /// with none yet takes the first free slot, the one with the fewest unfinished items first, and that slot is recorded
    /// (<see cref="Steps.Slot"/>) under its lock before anything is created in it. A recorded slot that is no longer configured
    /// (<c>Worker:Slots</c> lowered) is the item's failure, saying so. A one-slot factory records nothing: every item is slot 1's.
    /// While the run waits it
    /// reads <paramref name="item"/>'s controls every control poll (null: a stop, which no control holds up): a Pause — the usage
    /// pause and the freeze's row included — or a Stop ends the wait as it ends a step, and controls that cannot be read more than
    /// <c>Controls:MaxReadFailures</c> times in a row count as a Pause (E2). After a wait the controls and the freeze evaluator are
    /// asked once more before the phase starts. The run's first worker phase starts with the sandbox set-up every run shares
    /// (<c>enterSandbox</c>: the readiness probe of the held slot and the sweep of leftover worktrees, both through the launch
    /// helper and so under the lock; a failure there is the factory's, <see cref="FactoryUnavailableException"/>). A phase inside
    /// a phase runs at once, in the same slot.
    /// The wait is logged when it starts and again every <see cref="WorkerLock.ReportEvery"/> (with why the lock could not be
    /// had), and a lock file that cannot be opened at all is the factory's failure too (<see cref="WorkerLock.HoldAsync{T}"/>:
    /// the item, already claimed, is left where it is, E10).
    /// </summary>
    private async Task<T> InWorkerPhaseAsync<T>(WorkItem? item, string what, Func<Task<T>> phase, CancellationToken ct, WorkItem? slotOf = null)
    {
        async Task<T> EnteredAsync()
        {
            if (!_sandboxEntered && enterSandbox is not null)
            {
                await enterSandbox(ct);
            }
            _sandboxEntered = true;
            return await phase();
        }
        if (_slots is null)
        {
            return await EnteredAsync();
        }
        var owner = item ?? slotOf;
        var recorded = owner is null ? null : await RecordedSlotAsync(owner, ct);
        async Task<T> InSlotAsync(Worker.WorkerSlot slot)
        {
            if (owner is not null && recorded is null)
            {
                // Under the slot's lock, before the set-up or the phase makes anything in it: the record is what resumes it there.
                await RecordSlotAsync(owner, slot, ct);
            }
            return await EnteredAsync();
        }
        if (_slots.Held is { } held)
        {
            return recorded is { } other && other != held.Number
                ? throw new InvalidOperationException(
                    $"Refusing to {what} in worker {held}: {owner!.ExternalId} is worker slot {other}'s, and a phase is one sandbox user's.")
                : await InSlotAsync(held);
        }
        // An item with no slot yet goes where the fewest unfinished items are, so the slots fill evenly rather than in number order.
        var order = recorded is null && _slots.Count > 1 ? await ledger.SlotsByLoadAsync(_slots.Count, ct) : null;
        var waitsFor = recorded is { } own && _slots.Count > 1
            ? $"worker {_slots.Slot(own)} (another run's worker phase holds {_slots.Slot(own).Lock?.Path})"
            : _slots.Count > 1 ? $"a free worker slot (other runs' worker phases hold all {_slots.Count})"
            : $"the sandbox user (another run's worker phase holds {_slots.All[0].Lock?.Path})";
        var (waited, unreadable) = (false, 0);
        return await _slots.HoldAsync(recorded, async slot =>
        {
            if (waited && item is not null)
            {
                log.WriteLine(_slots.Count > 1
                    ? $"[lock] {item.ExternalId}: worker {slot} is free; going on to {what}"
                    : $"[lock] {item.ExternalId}: the sandbox user is free; going on to {what}");
                await ThrowIfControlledAsync(item, ct);
                await ThrowIfFrozenAsync(item, ct);
            }
            return await InSlotAsync(slot);
        }, ct, async c =>
        {
            if (!waited)
            {
                waited = true;
                log.WriteLine(_slots.Count > 1
                    ? $"[lock] {item?.ExternalId ?? "a stop"}: waiting for {waitsFor} to {what}"
                    : $"[lock] {item?.ExternalId ?? "a stop"}: waiting for the sandbox user to {what} (another run's worker phase holds {_slots.All[0].Lock?.Path})");
            }
            if (item is null)
            {
                return;
            }
            try
            {
                await ThrowIfControlledAsync(item, c);
                unreadable = 0;
            }
            catch (Exception ex) when (ex is not ControlRequestedException and not OperationCanceledException)
            {
                if (++unreadable > _maxControlReadFailures)
                {
                    throw new ControlRequestedException(ControlState.Paused) { ControlsUnreadable = $"{ex.GetType().Name}: {ex.Message}" };
                }
            }
        }, _controlPoll, report: log.WriteLine, order: order);
    }

    /// <summary>
    /// The slot the ledger has for <paramref name="item"/> (<see cref="SlotOf"/>; read once per run), or null when it has none.
    /// </summary>
    /// <exception cref="Worker.SlotUnavailableException">It is not one of the configured slots (<c>Worker:Slots</c> was lowered).</exception>
    private async Task<int?> RecordedSlotAsync(WorkItem item, CancellationToken ct)
    {
        if (!_slotOf.TryGetValue(item.Id, out var slot))
        {
            if (SlotOf(await ledger.HistoryAsync(item, ct)) is not { } recorded)
            {
                return null;
            }
            _slotOf[item.Id] = slot = recorded;
        }
        return slot <= _slots!.Count
            ? slot
            : throw new Worker.SlotUnavailableException(
                $"{item.ExternalId} was started in worker slot {slot} ({Worker.WorkerSlots.UserOf(_slots.All[0].User, slot)}), but Worker:Slots is "
                + $"{_slots.Count}: its session and worktree are that sandbox user's, and nothing of it is run as another. Set Worker:Slots to at "
                + $"least {slot} again and re-run it, or stop it.");
    }

    /// <summary>Records <paramref name="slot"/> as <paramref name="item"/>'s (<see cref="Steps.Slot"/>); a one-slot factory writes no row.</summary>
    private async Task RecordSlotAsync(WorkItem item, Worker.WorkerSlot slot, CancellationToken ct)
    {
        if (_slots!.Count > 1)
        {
            await ledger.CheckpointAsync(item, Steps.Slot, null, $"{slot.Number} {slot.User}", ct);
            log.WriteLine($"[slot] {item.ExternalId}: worker {slot}");
        }
        _slotOf[item.Id] = slot.Number;
    }

    /// <summary>
    /// Runs <paramref name="work"/> — owner-side work that makes a worktree of <paramref name="item"/> and runs no launch helper,
    /// so no worker phase (the merge queue's base update) — with the worktree shared with the item's slot's user
    /// (<see cref="Worker.WorkerSlots.AssignedAsync{T}"/>): its recorded slot, or for an item that has none yet (a plan item that
    /// never needed a planner in this factory) the one with the fewest unfinished items, recorded now. Nothing is waited for: the
    /// share is the owner's own <c>chmod</c>. With one slot, or inside a worker phase, <paramref name="work"/> just runs.
    /// </summary>
    private async Task<T> InItemSlotAsync<T>(WorkItem item, Func<Task<T>> work, CancellationToken ct)
    {
        if (_slots is null || _slots.Count == 1 || _slots.Held is not null)
        {
            return await work();
        }
        if (await RecordedSlotAsync(item, ct) is not { } slot)
        {
            slot = (await ledger.SlotsByLoadAsync(_slots.Count, ct))[0];
            await RecordSlotAsync(item, _slots.Slot(slot), ct);
        }
        return await _slots.AssignedAsync(slot, work);
    }

    private Task InWorkerPhaseAsync(WorkItem? item, string what, Func<Task> phase, CancellationToken ct) =>
        InWorkerPhaseAsync(item, what, async () => { await phase(); return true; }, ct, null);

    /// <summary>
    /// The factory's configured repositories (<c>FactoryOptions.ConfiguredRepos</c>; the default repository alone when not given): the
    /// only ones a plan item may name, whether it names one or several (sc-25874, sc-25877, <see cref="PlanRepositories"/>).
    /// </summary>
    private readonly IReadOnlyList<RepoRef> _configuredRepos = configuredRepos ?? [defaultRepo];

    /// <summary>
    /// Reads of an item's controls in a row that may fail while a worker or the gate's test runs go on (<c>Controls:MaxReadFailures</c>);
    /// the next failure counts as a Pause (E2: a check that cannot run counts as failed), recorded as <see cref="ControlsUnreadablePaused"/>.
    /// </summary>
    public const int DefaultMaxControlReadFailures = 10;

    private readonly int _maxControlReadFailures = maxControlReadFailures is { } max
        ? max >= 1 ? max : throw new ArgumentOutOfRangeException(nameof(maxControlReadFailures), max, "must be at least 1")
        : DefaultMaxControlReadFailures;

    /// <summary>When a running worker counts as stuck (<c>Worker:StuckRepeats</c>, <c>Worker:StuckSimilarity</c>; sc-25388).</summary>
    private readonly StuckDetection _stuck = (stuck ?? StuckDetection.Default).Validate();

    /// <summary>
    /// Implementer sessions in a row that may be found stuck before the item escalates (sc-25388): the first stuck session's
    /// attempt fails and one fresh session (in a fresh worktree) retries Implement; a second escalates.
    /// </summary>
    public const int MaxStuckImplementSessions = 2;

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
        /// <summary>
        /// The worker slot the item is in (sc-25989, E7), written under that slot's lock at the item's first worker phase, before
        /// its first worktree or session exists; Detail is the slot's number, then its sandbox user (<c>2 _factory2</c>). Every
        /// later worker phase of the item — a resume, a fix round, the gate's test runs, the stop of a leftover worker — waits
        /// for that slot (<see cref="SlotOf"/>). A one-slot factory writes none.
        /// </summary>
        public const string Slot = "slot";
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
        /// <summary>
        /// The item's controls could not be read <c>Controls:MaxReadFailures</c> times in a row while its worker (or the gate's test
        /// runs) went on, so it was paused; Detail is the last read's error. Recorded before the Paused row.
        /// </summary>
        public const string ControlsUnreadable = "controls-unreadable";
        /// <summary>A model answered the implementer's session (first time seen for the item); Detail is the model id.</summary>
        public const string ImplementerModel = "implementer-model";
        /// <summary>
        /// A worker session (implement, a fix round, a CI fix, a triage) ran on a router model class (E8); Detail is the class
        /// (<c>high</c>, <c>mid</c> or <c>low</c>, <see cref="WorkerModelClass"/>). Recorded each time a run starts or resumes
        /// the session, as soon as its id streams.
        /// </summary>
        public const string ModelClass = "worker-model-class";
        /// <summary>
        /// Review: a review panel call is about to be made; Detail is "&lt;router session&gt; &lt;model class&gt; &lt;head sha&gt;
        /// &lt;role&gt; &lt;prompt path&gt;@sha256:&lt;prompt hash&gt;" (role <c>confirm-&lt;role&gt;</c> for a second opinion's
        /// check of a blocking finding), so the call's cost is readable even when it never returns (E9) and the prompt it
        /// used is on record.
        /// </summary>
        public const string ReviewSession = "review-session";
        /// <summary>
        /// Review: what became of CodeGraph's overlay of the head a review waited for (sc-25708); Detail is the
        /// <see cref="Gate.CodeGraphOverlay"/> JSON (outcome, head, overlay, base, reason). A <c>ready</c> one for the same head is
        /// reused when the review is resumed (crash, Ctrl-C, a pause) instead of waiting again; any other outcome is retried.
        /// </summary>
        public const string CodeGraphOverlay = "codegraph-overlay";
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
        /// The fix-round cap in effect changed (<see cref="FixCapOf"/>): Detail starts with the cap — the lower of the base's
        /// <c>risk.max_fix_rounds</c> and <see cref="Lifecycle.MaxFixRounds"/> — then says where it came from. Recorded, before the
        /// fix-round decision it is read for, only when it differs from the one in effect (none recorded: the hard cap).
        /// </summary>
        public const string FixCap = "fix-cap";
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
        /// <summary>
        /// Plan: the planner's answer passed every rule (<see cref="Plans.PlanParser"/>); Detail is the plan
        /// (<see cref="Plans.Plan.ToDetail"/>), recorded before any story is created (sc-25870). Whether a later run creates this
        /// plan's stories or plans afresh: <see cref="ReusesRecordedPlan"/>.
        /// </summary>
        public const string Plan = "plan";
        /// <summary>
        /// Plan: written right before the work source is first asked to create the newest plan's stories. From this row on a story
        /// of that plan may be on the board, so no later run replaces the plan.
        /// </summary>
        public const string PlanCreating = "plan-creating";
        /// <summary>Plan: the planner's answer broke a rule (Detail: which); no story is created and the item escalates.</summary>
        public const string PlanRejected = "plan-rejected";
        /// <summary>
        /// Plan: what the check of a repository's CI workflow triggers found (sc-25873, <see cref="Gate.CiTriggers"/>), when it is not
        /// "a workflow runs" — Detail is <see cref="Gate.CiTriggerCheck.ToDetail"/>, starting with the repository: <c>blocked: …</c>
        /// (no workflow can run for the plan's story pull requests, or for its changeset pull request: no story is created and the item escalates) or
        /// <c>warning: …</c> (it could not be told; planning goes on). A warning is written when it changes, not every run.
        /// </summary>
        public const string CiTriggers = "ci-triggers";
        /// <summary>Plan: the plan's stories are on the board; Detail is their ids, in the plan's order (e.g. <c>sc-12 sc-13</c>).</summary>
        public const string PlanStories = "plan-stories";
        /// <summary>
        /// Planned: one story of the plan was moved to its board's ready state because every story that blocks it has merged
        /// (sc-25871); Detail starts with its id (e.g. <c>sc-13</c>), then anything worth saying about it. A story named by one is
        /// never released again.
        /// </summary>
        public const string PlanReleased = "plan-released";
        /// <summary>
        /// Planned: stories of the plan are held by blockers that are not stories of the plan and that the factory has not merged
        /// (Detail: which story by which, <see cref="Plans.PlanRelease.HeldByOutsiders"/>); recorded when that changes, not every poll.
        /// Also (sc-25876), after <see cref="Plans.PlanRelease.FailedPrefix"/>: a failed story of the plan (Escalated, Cancelled,
        /// merged outside the factory) holds what depends on it — which story, how and when it failed, its reason, what it blocks.
        /// </summary>
        public const string PlanHeld = "plan-held";
        /// <summary>
        /// Plan: the plan's changeset branch in one repository is about to be created (sc-25872); Detail is the
        /// <see cref="Plans.Changeset"/> JSON — the repository, the branch (<c>factory/changeset-&lt;plan item&gt;</c>), the default
        /// branch and its head the branch starts from — recorded before anything is pushed.
        /// </summary>
        public const string Changeset = "changeset";
        /// <summary>
        /// Plan: the changeset branch is on origin (Detail: <see cref="Plans.Changeset.PushedDetail"/>: the branch, its head, and
        /// whether origin already had it). From this row on the plan's stories in that repository branch from it and merge into it.
        /// </summary>
        public const string ChangesetPushed = "changeset-pushed";
        /// <summary>
        /// Planned: every story merged but the factory has no changeset to merge — the changeset branch does not differ from the
        /// default branch (Detail: both), or the plan's changeset pull request was merged outside the factory
        /// (<see cref="Plans.Changeset.MergedOutside"/>) — so no changeset pull request is opened and the plan is closed out saying so.
        /// </summary>
        public const string ChangesetEmpty = "changeset-empty";
        /// <summary>
        /// A plan of several repositories (sc-25873): one repository's changeset is on its default branch, or needs no merge
        /// (Detail: <see cref="Plans.ChangesetDelivery"/> JSON — the repository, how — merged by the gate with its pull request and
        /// merge commit, empty, or merged outside the factory). From this row on the stories blocked by that changeset may start
        /// and the next repository's changeset pull request may be opened. A plan with one changeset records none.
        /// </summary>
        public const string ChangesetDelivered = "changeset-delivered";
        /// <summary>
        /// A plan of several repositories that escalates or is stopped while some of its changesets are on their default branch and
        /// others are not (sc-25873): which repositories are delivered and which are not (Detail:
        /// <see cref="Plans.Changeset.DeliveryOf"/>), recorded before the Escalated row or the stop's report. Nothing is rolled back.
        /// </summary>
        public const string PlanDelivery = "plan-delivery";
        /// <summary>
        /// A plan's story whose pull request someone merged outside the factory INTO ITS PLAN'S CHANGESET BRANCH (sc-25877; Detail:
        /// <c>&lt;pull request&gt; into &lt;changeset branch&gt; at &lt;head&gt;</c>), written by the story's own run when the gate
        /// client's read of the pull request shows it merged with that base — before the Escalated row that still follows (the
        /// factory's gate did not merge it). From this row on the story counts as merged for its plan
        /// (<see cref="Plans.PlanRelease.Satisfied(IReadOnlyList{LedgerEntry}?)"/>): its work is on the changeset branch, which is
        /// reviewed and gated as a whole before it reaches a default branch. A pull request merged into any other base writes none.
        /// </summary>
        public const string MergedIntoChangeset = "merged-into-changeset";
        /// <summary>
        /// Planned: every story of the plan that has not merged failed or is blocked behind a failed one (sc-25876): nothing of it
        /// moves until a person acts. Detail: the failed stories and what they block (<see cref="Plans.PlanRelease.HeldByFailed"/>).
        /// A failed story that still leaves other stories going is a <see cref="PlanHeld"/> row instead
        /// (after <see cref="Plans.PlanRelease.FailedPrefix"/>). Either is written when it changes, not every poll.
        /// </summary>
        public const string PlanStuck = "plan-stuck";
        /// <summary>Planned: no story of the plan is failed any more (a person re-ran the failed one); the hold a failed story put on it is lifted.</summary>
        public const string PlanResumed = "plan-resumed";
        /// <summary>
        /// Planned (sc-25984): the plan item's claim, which a <see cref="PlanHeld"/> row <c>claim lost: …</c> recorded as someone
        /// else's (<see cref="ClaimLostAsync"/>), is the factory's again; the plan is evaluated again from this row on.
        /// </summary>
        public const string PlanClaimRegained = "plan-claim-regained";
        /// <summary>
        /// Planned: the plan item's comment for the latest <see cref="PlanHeld"/> (failed story), <see cref="PlanStuck"/> or
        /// <see cref="PlanResumed"/> row; Detail is <c>posted</c> or <c>failed: …</c> (tried again by the next evaluations, up to
        /// <see cref="MaxPlanNoticeAttempts"/>).
        /// </summary>
        public const string PlanNotice = "plan-notice";
    }

    /// <summary>How often the plan item's comment for one hold (or its lifting) is tried before it is left to the ledger and the dashboard.</summary>
    public const int MaxPlanNoticeAttempts = MaxCloseoutAttempts;

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

    /// <summary>
    /// Detail of the Paused row recorded when the item's controls could not be read <c>Controls:MaxReadFailures</c> times in a row
    /// while its worker (or the gate's test runs) went on: counted as a Pause rather than run blind (E2). Such an item resumes
    /// (its worker with <c>claude --resume</c>) once its controls can be read and none pauses it.
    /// </summary>
    public const string ControlsUnreadablePaused = "controls-unreadable";

    /// <summary>A Pause or Stop control reached a run; the worker (if any) has ended.</summary>
    private sealed class ControlRequestedException(ControlState state, bool workerStillRunning = false)
        : Exception($"control: {state}")
    {
        public ControlState State { get; } = state;
        public bool WorkerStillRunning { get; } = workerStillRunning;
        /// <summary>Set when the freeze evaluator (not a control row) paused the run: why it is frozen.</summary>
        public FreezeStatus? Frozen { get; init; }
        /// <summary>Set when the controls could not be read (<see cref="ControlsUnreadablePaused"/>): the last read's error.</summary>
        public string? ControlsUnreadable { get; init; }
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

        /// <summary>
        /// The repository the item's story names (sc-25873). <see cref="Repo"/> for every item but a plan item taking a changeset
        /// pull request through the gate in another repository: the item is that repository's only for that pass, and this
        /// one's again once the pass has ended (<see cref="MergeAsync"/>).
        /// </summary>
        public required RepoRef Primary { get; init; }

        /// <summary>
        /// The item's branch, whose pull request is the item's: its own <c>factory/&lt;id&gt;</c> branch, or — a plan item taking its
        /// changeset to the default branch (sc-25872) — the plan's changeset branch.
        /// </summary>
        public required string Branch { get; init; }

        /// <summary>
        /// The branch the item's branch starts from and its pull request targets: the changeset branch of the item's plan
        /// (sc-25872), or null for the repository's default branch (an item of no plan, and a plan item's own changeset).
        /// </summary>
        public string? BaseBranch { get; init; }

        /// <summary>A plan item's changeset (the item's branch is then the changeset branch); null for every other item.</summary>
        public Plans.Changeset? Changeset { get; init; }

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
                [WorkState.Plan] = PlanAsync,
                // A worker phase from the orphan's stop to the worktree's removal (sc-25988): nothing else may run as the sandbox user
                // between the session and its push.
                [WorkState.Implement] = (run, ct) => InWorkerPhaseAsync(run.Item, "implement", () => ImplementAsync(run, ct), ct),
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
    public static readonly IReadOnlySet<WorkState> ImplementStates = new HashSet<WorkState> { WorkState.Intake, WorkState.Plan, WorkState.Implement };

    /// <summary>States the factory's handlers drive (the production pipeline has a <see cref="GateStage"/>); an item in one is in flight.</summary>
    public static readonly IReadOnlySet<WorkState> HandledStates = new HashSet<WorkState>
    {
        WorkState.Intake, WorkState.Plan, WorkState.Implement, WorkState.Review, WorkState.Fixing, WorkState.CI, WorkState.CIHealing,
        WorkState.MergeGate, WorkState.Merge,
    };

    /// <summary>The states this pipeline drives.</summary>
    private IReadOnlySet<WorkState> Handled => gate is null ? ImplementStates : HandledStates;

    /// <summary>
    /// Story ids of items a run should pick up: first the items being stopped (whatever their state; a stop is finished even in a
    /// frozen factory, before anything is deferred), then, oldest first and unless a control pauses them, those in a handled
    /// state and those paused in one in a way that resumes by itself (<see cref="ResumesAutomatically"/>, not parked), and plan items
    /// waiting on their stories (<see cref="WorkState.Planned"/>, sc-25871: each poll releases what is unblocked; no worker runs).
    /// </summary>
    /// <param name="naming">The source whose items to list: each source's items are listed, and run, by its own intake lane (sc-25982).</param>
    /// <param name="handled">The states the runner's pipeline drives; default <see cref="HandledStates"/> (production).</param>
    public static async Task<IReadOnlyList<int>> InFlightAsync(WorkLedger ledger, ItemNaming naming, CancellationToken ct, IControls? controls = null,
        IReadOnlySet<WorkState>? handled = null)
    {
        controls ??= NoControls.Instance;
        handled ??= HandledStates;
        var stopping = (await controls.ListAsync(ct)).Where(c => c.State == ControlState.Stopping).Select(c => c.Scope).ToHashSet();
        var stops = new List<int>();
        var ids = new List<int>();
        var active = await ledger.ActiveItemsAsync(naming.Source, ct);
        // A merged item whose closeout failed is listed until it is posted or its attempts are used up (a run retries it from Watch);
        // a finished plan whose closeout failed likewise (it waits in Planned).
        var closeouts = await ledger.CloseoutRowsAsync(active.Where(i => i.State == WorkState.Planned
            || (i.State == WorkState.Watch && handled.Contains(WorkState.Merge))).Select(i => i.Id).ToList(), ct);
        var byId = active.ToDictionary(i => i.Id);
        foreach (var item in active)
        {
            // Its own Stop, or its plan's (sc-25876: a story of a plan being stopped, unless it already merged — the controls say which).
            var beingStopped = stopping.Contains(ControlScope.Item(item.ExternalId))
                || (item.PlanItemId is { } plan && byId.TryGetValue(plan, out var planItem) && stopping.Contains(ControlScope.Item(planItem.ExternalId))
                    && await controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) == ControlState.Stopping);
            if (!beingStopped)
            {
                var resumable = handled.Contains(item.State)
                    || (item.State == WorkState.Planned && !PlanCloseoutAbandoned(item.State, closeouts[item.Id]))
                    || CloseoutPending(item.State, closeouts[item.Id])
                    || (item.State == WorkState.Paused && ResumesAutomatically(await ledger.HistoryAsync(item, ct), handled));
                if (!resumable || await controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) != ControlState.Running)
                {
                    continue;
                }
            }
            if (naming.TryParse(item.ExternalId, out var id))
            {
                (beingStopped ? stops : ids).Add(id);
            }
        }
        return [.. stops, .. ids];
    }

    /// <summary>
    /// Story ids of the items whose claim an intake poll keeps (sc-25983; <see cref="IItemRunner.RenewableAsync"/>,
    /// <see cref="IWorkSource.RenewClaimAsync"/>), oldest first: every item the ledger would resume by itself — in a handled state,
    /// or paused from one in a way that resumes by itself (<see cref="ResumesAutomatically"/>: <c>interrupted</c>,
    /// <c>user-paused</c>, <c>usage-paused</c>, <c>freeze-paused</c>, <c>controls-unreadable</c>) — WHATEVER the factory, epic,
    /// item, usage and freeze controls say now. Unlike <see cref="InFlightAsync"/>, which leaves out what a control pauses: a
    /// pause only holds an item for a while (a freeze until a human's Continue, however long that takes), and a claim that lapsed
    /// meanwhile would leave the board showing nobody on an item the factory resumes. Also listed (sc-25984): a plan item waiting
    /// on its stories (<see cref="WorkState.Planned"/>) — it has no handler, but the factory drives it for as long as its stories
    /// take, often more than a claim's lifetime, and then takes its changeset pull request through the gate; a plan item whose
    /// claim is lost stays listed and is not paused — it has no state to pause from — but is not evaluated while the claim is
    /// someone else's (<see cref="ClaimLostAsync"/>: one <c>plan-held</c> row <c>claim lost: …</c>; nothing of it is released and
    /// no changeset pull request opened until a renewal holds again). Not listed, so not
    /// renewed: an item being stopped (its own Stop or its plan's; the stop gives the claim back), a parked one (a refused or a
    /// lost claim, a give-up), an escalated one, a merged or finished one. This is never a list of what to run.
    /// The controls are read only to leave out an item being stopped: when they cannot be read the item is listed (a claim renewed
    /// on an item whose stop then releases it costs nothing; an unreadable control must not cost every item its claim), so this
    /// throws only when the ledger itself cannot be read.
    /// </summary>
    /// <param name="naming">The source whose items to list: each lane renews its own source's claims (sc-25982; no default source).</param>
    /// <param name="handled">The states the runner's pipeline drives; default <see cref="HandledStates"/> (production).</param>
    public static async Task<IReadOnlyList<int>> RenewableAsync(WorkLedger ledger, ItemNaming naming, CancellationToken ct, IControls? controls = null,
        IReadOnlySet<WorkState>? handled = null)
    {
        controls ??= NoControls.Instance;
        handled ??= HandledStates;
        var ids = new List<int>();
        foreach (var item in await ledger.ActiveItemsAsync(naming.Source, ct))
        {
            var resumes = handled.Contains(item.State) || item.State == WorkState.Planned
                || (item.State == WorkState.Paused && ResumesAutomatically(await ledger.HistoryAsync(item, ct), handled));
            if (!resumes || !naming.TryParse(item.ExternalId, out var id))
            {
                continue;
            }
            try
            {
                if (await controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) == ControlState.Stopping)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Unreadable controls: renewed all the same (see above).
            }
            ids.Add(id);
        }
        return ids;
    }

    private static bool ResumesAutomatically(List<LedgerEntry> history, IReadOnlySet<WorkState> handled)
    {
        var paused = history.FindLastIndex(e => e.Step is null);
        var pausedFrom = TransitionContext.From(history.Where(e => e.Step is null).Select(e => e.State).ToList()).PausedFrom;
        return history[paused] is { State: WorkState.Paused, Detail: Interrupted or UserPaused or UsagePaused or FreezePaused or ControlsUnreadablePaused }
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
        if (freeze is not null && await CheckFreezeAsync(ct) is { Frozen: true } frozen)
        {
            log.WriteLine($"[deferred] {Naming.Format(storyId)}: {frozen.Message}; {frozen.Remedy}");
            return new RunOutcome(known?.Id ?? 0, known?.State ?? WorkState.Intake, null, null,
                $"{Naming.Format(storyId)} deferred: {frozen.Message}; {frozen.Remedy}")
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
            // A plan story parked because a blocker had not merged (sc-25871) and still listed ready: nothing is written until
            // the blocker has merged, however often the board lists it.
            if (known.State == WorkState.Paused && await StillBlockedAsync(known, storyId, ct) is { } blocked)
            {
                log.WriteLine($"[paused] {known.ExternalId} parked: {blocked}");
                return await OutcomeAsync(known, $"{known.ExternalId} parked: {blocked}", ct);
            }
            // Resuming (an escalated item re-enters Intake, whose claim checks the scope itself).
            if (known.State != WorkState.Escalated && !ignoreScope && !await source.InScopeAsync(storyId, ct))
            {
                return await ParkOutOfScopeAsync(known, storyId, ct);
            }
        }

        var spec = await source.ReadSpecAsync(storyId, ct);
        var story = spec.Story;
        var control = known is not null ? ControlState.Running : await _controls.EffectiveAsync(Naming.Format(storyId), spec.Epic?.Id, ct);
        if (known is null && control == ControlState.Running)
        {
            control = await PlanControlAsync(Naming.Format(storyId), ct);
        }
        if (control != ControlState.Running)
        {
            // A paused factory or epic claims nothing new, nor does a paused or stopping plan for its stories (sc-25876): the story
            // stays as it is on the board.
            log.WriteLine($"[intake] {Naming.Format(storyId)} is {control} by a control; not claiming it.");
            return new RunOutcome(0, WorkState.Intake, null, null, $"{Naming.Format(storyId)} is {control} by a control; not claimed.");
        }
        // A story parked because its plan never recorded it (sc-25871) and still listed ready: nothing is written while that holds.
        if (known is { State: WorkState.Paused } && await StillWithoutPlanAsync(known, story, ct) is { } unfinished)
        {
            log.WriteLine($"[paused] {known.ExternalId} parked: {unfinished}");
            return await OutcomeAsync(known, $"{known.ExternalId} parked: {unfinished}", ct);
        }
        // The repository the item's story names: every item's own, and a plan item's primary one (sc-25873).
        var primary = RepoResolver.Resolve(story.Description, defaultRepo);
        var item = await ledger.GetOrCreateAsync(Naming.Source, Naming.Format(storyId), story.Name, primary.FullName, IntakeDetail(story), ct, spec.Epic?.Id);

        await using var runLock = await locks.TryAcquireAsync(item.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{item.State}] {item.ExternalId} is already running; not touching it.");
            return new RunOutcome(item.Id, item.State, null, null, $"{item.ExternalId} is already running.");
        }
        // A plan item taking one of its changesets through the gate (sc-25873) works in that changeset's repository until it has
        // merged: the pull request, the gate policy, CI and the merge queue are that repository's. Any other item, and a plan item
        // in any other state, works in the repository its story names. Decided from the rows read under the run lock, never from
        // what was loaded before it: another process may have finished one repository's pull request and opened the next one's
        // meanwhile, and this run would take that one through the gate as the earlier repository's.
        var repo = Plans.Changeset.PassRepo(await ledger.HistoryAsync(item, ct)) ?? primary;
        // Re-read under the lock: another run may have moved the item since it was loaded.
        await ledger.RefreshAsync(item, story.Name, repo.FullName, spec.Epic?.Id, ct);
        // A story a plan created records its plan (sc-25871), from the ledger's own record of what the plan created. Looked up
        // until the item is first claimed: a plan's story is linked before its claim (WithoutPlanAsync refuses one that is not),
        // so an item that was ever claimed without a link is no plan's story and is not looked up on every later run.
        if (item.PlanItemId is null && source.PlansItems && !(await ledger.HistoryAsync(item, ct)).Any(e => e.Step == Steps.Claimed)
            && await ledger.PlanOfAsync(Naming.Source, item.ExternalId, ct) is { } planItem)
        {
            await ledger.LinkPlanAsync(item, planItem, ct);
        }
        // Where the item's work goes (sc-25872), from the ledger: a plan's story branches from its plan's changeset branch and
        // merges into it; a plan item whose stories have all merged works on the changeset branch itself, against the default branch.
        var changeset = Plans.Changeset.Of(await ledger.HistoryAsync(item, ct), repo);
        var ofPlan = item.PlanItemId is { } plan ? await ledger.ChangesetOfAsync(plan, repo, ct) : (null, []);
        var run = new Run(spec, repo, item)
        {
            Primary = primary,
            Branch = changeset?.Branch ?? story.Kind.BranchName(story.Id),
            BaseBranch = ofPlan.Changeset?.Branch,
            Changeset = changeset,
        };
        // Fail closed: a plan with a changeset has none on origin for this repository (the story's `Repo:` line names another one),
        // so the story would merge into that repository's default branch, count as merged and be left out of the changeset pull
        // request. Only a plan recorded before changesets (no `changeset` row at all) has its stories target the default branch.
        var noChangeset = ofPlan is { Changeset: null, Recorded: [.., var recorded] }
            ? $"{item.ExternalId} is a story of the plan whose changeset branch is {recorded.Branch} in "
                + $"{string.Join(", ", ofPlan.Recorded.Select(c => c.Repo).Distinct(StringComparer.OrdinalIgnoreCase))}, and that plan has no changeset "
                + $"branch in {repo}: the story cannot merge into its plan's changeset. Its `Repo:` line must name the plan's repository."
            // Nor into another repository's changeset of its plan (sc-25873): the plan counts the story for the repository it
            // recorded it in, and that repository's changeset would go to its default branch without the story's work.
            : item.PlanItemId is { } owner && await RecordedRepoAsync(owner, item.ExternalId, ct) is { } planned
                && !string.Equals(planned, repo.FullName, StringComparison.OrdinalIgnoreCase)
            ? $"{item.ExternalId} is a story of a plan that recorded it in {planned}, but its `Repo:` line now names {repo}: there it "
                + $"would merge into another repository's changeset of the plan, while the plan still waits for it in {planned}. "
                + $"Its `Repo:` line must name {planned}."
            : null;

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
            // A plan's story whose pull request someone merged into its plan's changeset branch (sc-25877) counts as merged for
            // its plan: its work is on that branch already. A re-run would implement it a second time, so there is none.
            if (item.PlanItemId is not null && Plans.PlanRelease.MergedOutsideChangeset(await ledger.HistoryAsync(item, ct)))
            {
                var done = $"{item.ExternalId} was {Plans.PlanRelease.MergedOutsideIntoChangeset}: for its plan it counts as merged, "
                    + $"and a re-run does nothing. {char.ToUpperInvariant(Plans.PlanRelease.BoardStoryStillClaimed[0])}{Plans.PlanRelease.BoardStoryStillClaimed[1..]}.";
                log.WriteLine($"[{item.State}] {done}");
                return await OutcomeAsync(item, done, ct);
            }
            await ledger.RecordAsync(item, WorkState.Intake, null, $"re-run after escalation; {IntakeDetail(story)}", ct);
        }
        else if (item.State == WorkState.Paused)
        {
            var pausedFrom = (await ledger.ContextAsync(item, ct)).PausedFrom
                ?? throw new InvalidOperationException($"{item.ExternalId} is paused but its ledger has no state to return to.");
            // Paused because another caller took its claim (sc-25983): it is resumed only once the item is the factory's again.
            // The source is asked before anything is recorded — it takes a lapsed or released claim back — and while someone
            // else still holds the item (or the board will not say) the item stays as it is: no row, nothing written to the board.
            if (ParkedForLostClaim(await ledger.HistoryAsync(item, ct)))
            {
                var renewal = await source.RenewClaimAsync(storyId, ct);
                if (!renewal.IsHeld)
                {
                    var why = renewal.Lost is { } lost
                        ? $"{item.ExternalId} is still not the factory's to work ({ClaimLost}: {lost}); it stays paused"
                        : $"{item.ExternalId} stays paused: its claim was lost and the board would not confirm it is the factory's again ({renewal.NotRenewed})";
                    log.WriteLine($"[paused] {why}");
                    return await OutcomeAsync(item, $"{why}. Run `factory run {item.ExternalId}` again once the other claim is released or "
                        + $"has lapsed, or `factory stop --item {item.ExternalId}` to give the item up.", ct);
                }
            }
            await ledger.RecordAsync(item, pausedFrom, null, "unpaused", ct);
        }
        log.WriteLine($"[{item.State}] {item.ExternalId} \"{story.Name}\" -> {repo}");

        try
        {
            if (noChangeset is not null)
            {
                throw new WorkerFailedException(noChangeset);
            }
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
            if (request.ControlsUnreadable is { } unreadable)
            {
                // Recorded without reading the controls again: they were unreadable, and the item resumes once they are not.
                await ledger.CheckpointAsync(item, Steps.ControlsUnreadable, null, unreadable, CancellationToken.None);
                await ledger.RecordAsync(item, WorkState.Paused, null, ControlsUnreadablePaused, CancellationToken.None);
                log.WriteLine($"[paused] {item.ExternalId}: its controls could not be read ({unreadable}); paused, worktree and session kept");
                return await OutcomeAsync(item, $"{item.ExternalId} is paused: its controls could not be read more than {_maxControlReadFailures} "
                    + $"times in a row ({unreadable}); it resumes on its own once they can be read and nothing pauses it.", CancellationToken.None);
            }
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
                log.WriteLine($"[paused] {item.ExternalId} paused by the factory freeze ({frozenBy}); its worktree and session are kept");
                return await OutcomeAsync(item, $"{item.ExternalId} is paused by the factory freeze ({frozenBy}); {FreezeTrigger.Remedy(frozenBy)}.",
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
        // The factory's own failure (the planner's seat: its sweep, the MCP proxy, the session file's ACL) is not the item's: it
        // reaches the caller, which treats it as factory-wide (IntakeLoop.IsFactoryWide). The item stays where it is, nothing is
        // recorded against it, and the next run takes the step again; whatever threw it has already cleaned up after itself.
        catch (Exception ex) when (ex is not FactoryUnavailableException)
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
        log.WriteLine(item.State == WorkState.Planned
            ? $"[{item.State}] {item.ExternalId} waits on its plan's stories; the next run or poll releases the unblocked ones"
            : $"[{item.State}] parked; no handler for {item.State} in this phase");
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
        if (item.State == WorkState.Planned)
        {
            // A plan item waiting on its stories (sc-25871): release what is unblocked, or close the plan out.
            var evaluated = await PlannedAsync(item, storyId, history, ct);
            // A Stop that landed while the plan was evaluated (sc-25876): nothing watches a control during the evaluation, so this
            // run finishes the stop now, under the run lock it holds — the plan item is not left Stopping with nobody acting on it.
            if (!Lifecycle.IsTerminal(item.State) && await _controls.EffectiveAsync(item.ExternalId, item.EpicId, ct) == ControlState.Stopping)
            {
                return await FinishStopAsync(item, storyId, RepoRef.Parse(item.Repo), null, workerStillRunning: false);
            }
            return evaluated;
        }
        var entered = history.FindLastIndex(e => e.Step is null);
        // A finished plan item (sc-25871) was told so by its closeout: no notice on top of it.
        var finishedPlan = item.State == WorkState.Done && history.Any(e => e.Step == Steps.PlanStories);
        if (!finishedPlan && !Runnable(item, await ledger.ContextAsync(item, ct))
            && !history.Skip(entered + 1).Any(e => e.Step == Steps.HeldNotice))
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
            // A story of a plan is not started before its blockers have merged (sc-25871, E7), whoever moved it to ready: the
            // claim is refused before the board is asked for it, the refusal naming the blockers.
            // Nor is a story that says a plan created it but that the ledger links to no plan: its blockers cannot be checked.
            if ((await WithoutPlanAsync(run.Item, run.Story, ct) ?? await BlockedAsync(run.Item, run.Story.Id, ct)) is { } blocked)
            {
                await ledger.RecordAsync(run.Item, WorkState.Paused, null, $"{ClaimRefused}: {blocked}", ct);
                await ledger.CheckpointAsync(run.Item, Steps.Parked, null, blocked, ct);
                return;
            }
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
        // A plan item (sc-25870) is planned into stories, never implemented itself; any other item is implemented straight from its story.
        if (source.PlansItems && Plans.PlanLabel.IsOn(run.Story))
        {
            await ledger.RecordAsync(run.Item, WorkState.Plan, null, $"label {Plans.PlanLabel.Name}", ct);
            return;
        }
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
        var (branch, baseBranch) = (run.Branch, run.BaseBranch);
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
            run.Workspace = await workspaces.ReopenAsync(repo, branch, ct, baseBranch);
            await StuckImplementAsync(run, found, ct);
            return;
        }

        Workspace? workspace = null;
        if (attempt.Any(e => e.Step is not null))
        {
            workspace = await workspaces.ReopenAsync(repo, branch, ct, baseBranch);
            if (workspace is null && attempt.Any(e => e.Step == Steps.Pushed))
            {
                // The work is on origin: rebuild the worktree from it rather than redo it and force-push over it.
                workspace = await workspaces.RestoreAsync(repo, branch, ct, baseBranch);
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
            // A plan's story starts from its plan's changeset branch (sc-25872), so it sees the stories that blocked it.
            workspace = await workspaces.PrepareAsync(repo, branch, ct, baseBranch);
        }
        run.Workspace = workspace;
        log.WriteLine($"[implement] worktree {workspace.Path} on {workspace.Branch}{(baseBranch is null ? "" : $" from {baseBranch}")}");

        if (!attempt.Any(e => e.Step == Steps.WorkerDone))
        {
            try
            {
                session = await RunWorkerSessionAsync(run, workspace, session, resume => resume is null ? BuildPrompt(spec, repo) + stuckHint : BuildResumePrompt(story),
                    models, [SpecInput(story)], "implement", WorkerModelClass.Coding(story), ct);
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
    /// Pause/Stop are watched. The session runs on <paramref name="modelClass"/> (E8; checkpointed as <see cref="Steps.ModelClass"/>
    /// once its id streams). A paused, usage-limited or class-refused (<see cref="WorkerResult.ModelClassUnavailable"/>) session
    /// throws <see cref="ControlRequestedException"/> (resumed later); a failed one throws <see cref="WorkerFailedException"/>.
    /// On success it checkpoints <see cref="Steps.WorkerDone"/> and returns the session id.
    /// </summary>
    private async Task<string?> RunWorkerSessionAsync(Run run, Workspace workspace, string? session, Func<string?, string> prompt,
        HashSet<string> models, IReadOnlyCollection<WorkerInput> inputs, string label, string modelClass, CancellationToken ct) =>
        (await RunSessionAsync(run, worker, workspace.Path, null, implementing: true, session, prompt, models,
            inputs.Select(Taint.Of).OfType<string>().ToList(), label, modelClass, ct)).Session;

    /// <summary>
    /// <see cref="RunWorkerSessionAsync"/> for any session of the item: <paramref name="runner"/> in <paramref name="path"/>, tainted from
    /// its start by <paramref name="inputTaints"/>. An <paramref name="implementing"/> session (the implementer, a fixer) loads the
    /// worktree's Claude settings — which taint and are guarded (<see cref="RepoSettingsGuard"/>) —, has its models recorded and is
    /// watched by the stuck detector; a planner session (sc-25870: read-only, no settings file loaded, no round to fail, reaching the
    /// loopback MCP proxy through <paramref name="mcpConfig"/>) is not. Returns the session id and the worker's result.
    /// </summary>
    private async Task<(string? Session, WorkerResult Result)> RunSessionAsync(Run run, IWorker runner, string path, string? mcpConfig, bool implementing,
        string? session, Func<string?, string> prompt, HashSet<string> models, IReadOnlyList<string> inputTaints, string label, string modelClass,
        CancellationToken ct)
    {
        var item = run.Item;
        var resume = session;
        // The target repo's own Claude settings may feed the session content its stream would not show (hooks, MCP, extra allow rules).
        var taints = implementing ? inputTaints.Append(Taint.OfRepoSettings(path)).OfType<string>().ToList() : inputTaints;
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
        // The worktree's Claude settings must not replace the router variables or pin a model (E8): the session does not start.
        if (implementing)
        {
            ThrowIfUnsafeRepoSettings(path, starting: true);
        }
        // No worker session starts into a frozen factory (implement, a fix round, a CI fix).
        await ThrowIfFrozenAsync(item, ct);
        log.WriteLine(resume is null ? $"[{label}] starting worker" : $"[{label}] resuming claude session {resume}");
        // Every stdout line is stored (and pushed to live viewers) as it streams (E7).
        await using var capture = sessions is null ? null : await sessions.StartAsync(item.Id, resume, ct);
        // Pause asks the worker to stop at its next tool boundary (and stops it if it doesn't); Stop stops it now.
        await using var watch = new ControlWatch(_controls, runner, log, _controlPoll, _pauseGrace, item.ExternalId, item.EpicId, path,
            _maxControlReadFailures, ct);
        // A looping session is interrupted at its next tool boundary like a Pause, but never resumed (sc-25388).
        var detector = new StuckDetector(_stuck);
        string? stuckReason = null;
        var classRecorded = false;
        log.WriteLine($"[{label}] model class {modelClass}");
        WorkerResult result;
        try
        {
            result = await runner.RunAsync(path, prompt(resume), resume, modelClass,
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
                        if (!classRecorded)
                        {
                            // The class this run of the session sends on every call (E8), on record once per run, new or resumed.
                            classRecorded = true;
                            await ledger.CheckpointAsync(item, Steps.ModelClass, sid, modelClass, c);
                        }
                    },
                    OnLine: async (line, c) =>
                    {
                        if (capture is not null)
                        {
                            await capture.OnLineAsync(line, c);
                        }
                        if (implementing && detector.Accept(line) is { } reason)
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
                        if (implementing && models.Add(model))
                        {
                            await ledger.CheckpointAsync(item, Steps.ImplementerModel, session, model, c);
                        }
                    },
                    OnUntrusted: async (sid, reason, c) =>
                    {
                        await ledger.TaintSessionAsync(item, sid, reason, c);
                        log.WriteLine($"[{label}] session {sid} used {reason}: tainted, it gets no push token");
                    }), mcpConfig, watch.Token);
        }
        catch (Exception ex) when (watch.Requested is { } requested && !ct.IsCancellationRequested)
        {
            // The control cut the worker off (Stop, or a pause it did not honour in time).
            await CompleteControlledSessionAsync(capture, requested);
            throw new ControlRequestedException(requested, WorkerStillRunning.IsMarked(ex))
            {
                ControlsUnreadable = requested == ControlState.Paused ? watch.ControlsUnreadable : null,
            };
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
            throw new ControlRequestedException(requested)
            {
                ControlsUnreadable = requested == ControlState.Paused ? watch.ControlsUnreadable : null,
            };
        }
        // The plans ran out, not the work: pause the factory (backing off) and resume this session afterwards, rather
        // than escalate the item. Without a control table nothing could hold the pause, so the failure escalates. The router having no
        // servable model in the session's class (E8: it never falls back to another class) is treated the same way.
        if (UsagePauseReason(result) is { } usageReason
            && await _controls.PauseForUsageAsync(null, usageReason, ct) is { State: ControlState.Paused } pause)
        {
            log.WriteLine(usageReason == UsagePause.WorkerModelClassUnavailable
                ? $"[{label}] the router had no servable model in class {modelClass} (model_class_unavailable); factory paused for usage until {pause.ResumeAt:u}"
                : $"[{label}] worker hit a router exhaustion or rate-limit error; factory paused for usage until {pause.ResumeAt:u}");
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
        if (implementing && session is not null && Taint.OfRepoSettings(path) is { } changed)
        {
            await ledger.TaintSessionAsync(item, session, changed, ct);
        }
        // Nor may the worker leave such settings for a later session (or push them): its round fails instead.
        if (implementing)
        {
            ThrowIfUnsafeRepoSettings(path, starting: false);
        }
        await ledger.CheckpointAsync(item, Steps.WorkerDone, session, $"worker exit {result.ExitCode}", ct);
        return (session, result);
    }

    /// <summary>Fails the worker round (escalates) when the worktree's Claude settings are not safe (<see cref="RepoSettingsGuard"/>).</summary>
    private static void ThrowIfUnsafeRepoSettings(string worktree, bool starting)
    {
        if (RepoSettingsGuard.Refusal(worktree) is { } refusal)
        {
            throw new WorkerFailedException(starting
                ? $"The worker session was not started: the worktree's Claude settings are refused ({refusal}). Only keys that cannot "
                    + "change the model, endpoint, headers or credentials are allowed in the branch's .claude settings."
                : $"The worker left Claude settings the factory refuses ({refusal}), so its work is not pushed.");
        }
    }

    /// <summary>
    /// Why a failed worker session pauses the factory for usage instead of escalating, or null when it does not: the router had no
    /// servable model in its class (<see cref="UsagePause.WorkerModelClassUnavailable"/>), or the plans refused it
    /// (<see cref="UsagePause.WorkerRateLimited"/>).
    /// </summary>
    internal static string? UsagePauseReason(WorkerResult result) =>
        result.ModelClassUnavailable ? UsagePause.WorkerModelClassUnavailable
        : result.UsageLimited ? UsagePause.WorkerRateLimited
        : null;

    /// <summary>
    /// What the worker handed <paramref name="story"/> reads: the owner's story (an item of a trusted source,
    /// <see cref="ItemNaming.Trusted"/>), or an issue item's approved triage (never the issue's text).
    /// </summary>
    private static WorkerInput SpecInput(WorkStory story) =>
        story.Kind.Trusted ? WorkerInput.Story : WorkerInput.TriageSummary;

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

    /// <summary>
    /// Removes a worktree. Sandboxed, the worker user deletes it first, through the launch helper, so it happens under the sandbox
    /// user's lock (sc-25988): inside a worker phase at once; outside one (an escalation's or a stop's clean-up, the merge queue's
    /// base-update worktree) only if the lock is free now — a run does not hold its item, or its repository's queue turn, for
    /// another run's whole worker session to throw a directory away. Left behind, it is deleted by the sweep the next worker phase
    /// of any run starts with, or replaced by this item's next worktree. False when it is still there.
    /// </summary>
    private async Task<bool> RemoveWorktreeAsync(RepoRef repo, Workspace workspace)
    {
        try
        {
            if (_slots is null)
            {
                await workspaces.RemoveAsync(repo, workspace, CancellationToken.None);
                return true;
            }
            // The slot whose user the worktree was shared with (sc-25989): the item's own, which a worker phase holds already.
            if (await _slots.TryHoldForAsync(workspace.Path, _ => workspaces.RemoveAsync(repo, workspace, CancellationToken.None), CancellationToken.None))
            {
                return true;
            }
            log.WriteLine($"[cleanup] the sandbox user is busy (another run's worker phase); leaving {workspace.Path} for the next worker phase's sweep");
            return false;
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
        if (freeze is not null && await CheckFreezeAsync(ct, fresh) is { Frozen: true } frozen)
        {
            log.WriteLine($"[frozen] {item.ExternalId}: {frozen.Message}");
            throw new ControlRequestedException(ControlState.Paused) { Frozen = frozen };
        }
    }

    /// <summary>The freeze evaluator's answer, its notes (what it passed over, e.g. a base branch GitHub no longer has) logged.</summary>
    private async Task<FreezeStatus> CheckFreezeAsync(CancellationToken ct, bool fresh = false)
    {
        var status = await freeze!.CheckAsync(ct, fresh);
        foreach (var note in status.Notes)
        {
            log.WriteLine($"[freeze] {note}");
        }
        return status;
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

    /// <summary>Whether a user's Pause holds the item: on the factory, its epic, the item itself or the plan it is a story of (sc-25876).</summary>
    private async Task<bool> UserPausedAsync(WorkItem item)
    {
        List<string> scopes = ItemNaming.EpicOf(item.ExternalId, item.EpicId) is { } epic
            ? [ControlScope.Factory, ControlScope.Epic(epic), ControlScope.Item(item.ExternalId)]
            : [ControlScope.Factory, ControlScope.Item(item.ExternalId)];
        if (item.PlanItemId is { } plan && await ledger.ItemAsync(plan, CancellationToken.None) is { } planItem)
        {
            scopes.Add(ControlScope.Item(planItem.ExternalId));
        }
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
            var history = await ledger.HistoryAsync(item, ct);
            var attempt = CurrentWorkerAttempt(history);
            // Sandboxed, the stop of a leftover worker is a run of the launch helper (it kills every process of the sandbox user):
            // a worker phase, waited for, so it never lands under another run's worker.
            if (OrphanedWorkerPid(attempt) is { } pid
                && await InWorkerPhaseAsync(null, $"stop {item.ExternalId}'s leftover worker", () => worker.StopOrphanAsync(pid, ct), ct, slotOf: item))
            {
                await ledger.CheckpointAsync(item, Steps.OrphanKilled, null, $"pid {pid}", ct);
            }
            // A plan item's worktree is its changeset branch's (sc-25872); only the worktree is removed, the branch stays.
            workspace = await workspaces.ReopenAsync(repo, Plans.Changeset.Of(history, repo)?.Branch ?? Naming.BranchName(storyId), ct);
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
    /// withdraws the request (<see cref="IWorker.CancelPause"/>) and the grace. Stop: cancels at once. Controls that cannot be
    /// read more than <c>maxReadFailures</c> times in a row count as a Pause (<see cref="ControlsUnreadable"/>); a read that then
    /// shows nothing pausing the item withdraws it like a Continue.
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
        private readonly int _maxReadFailures;
        private int _requested = -1;
        private int _pauseRequested;
        private int _stuck;
        private string? _unreadable;

        public ControlWatch(IControls controls, IWorker worker, TextWriter log, TimeSpan poll, TimeSpan grace, string externalId, long? epicId, string workingDirectory,
            int maxReadFailures, CancellationToken ct)
        {
            (_controls, _workerProcess, _log, _poll, _grace, _workingDirectory, _maxReadFailures) =
                (controls, worker, log, poll, grace, workingDirectory, maxReadFailures);
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

        /// <summary>While the controls are unreadable past the limit (counted as a Pause): the last read's error.</summary>
        public string? ControlsUnreadable => Volatile.Read(ref _unreadable);

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
            var readFailures = 0;
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
                var unreadableNow = false;
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
                    if (++readFailures <= _maxReadFailures)
                    {
                        log.WriteLine($"[control] could not read the controls of {externalId} ({readFailures} in a row): {ex.Message}; retrying");
                        continue;
                    }
                    // Unreadable for too long: a Pause, not a worker running blind (E2). The run records it once the worker stops.
                    // The reads that paused it are recorded (later failures while the worker stops change nothing).
                    Interlocked.CompareExchange(ref _unreadable, $"{readFailures} reads in a row failed; the last: {ex.GetType().Name}: {ex.Message}", null);
                    state = ControlState.Paused;
                    unreadableNow = true;
                }
                if (!unreadableNow)
                {
                    // Read: whatever the controls say now decides (a Continue-like withdrawal when nothing pauses the item).
                    readFailures = 0;
                    Volatile.Write(ref _unreadable, null);
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
                    log.WriteLine(ControlsUnreadable is { } why
                        ? $"[control] the controls of {externalId} could not be read {readFailures} times in a row ({why}); counted as a pause: its worker stops at its next tool call"
                        : $"[control] {externalId} paused; its worker stops at its next tool call");
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
    /// Whether a worktree directory (<c>factory-sc-&lt;id&gt;</c>, <c>factory-gh-&lt;id&gt;</c>, or a plan item's changeset worktree
    /// <c>factory-changeset-sc-&lt;id&gt;</c>, sc-25872) belongs to an item a re-run would resume
    /// in it (Implement, a fix round in Fixing or CIHealing, or Paused), so the startup sweep must keep it. Everything else is an orphan
    /// (a triage worktree, <c>factory-triage-gh-&lt;id&gt;</c>, never is).
    /// </summary>
    public static async Task<bool> WorktreeIsResumableAsync(WorkLedger ledger, string worktreeName, CancellationToken ct)
    {
        if (ItemNaming.OfWorktree(worktreeName) is not { } item)
        {
            return false;
        }
        return await ledger.StateOfAsync(item.Naming.Source, item.ToString(), ct) is WorkState.Implement or WorkState.Fixing or WorkState.CIHealing
            or WorkState.Paused;
    }

    /// <summary>
    /// Rows of the current worker attempt: from the last entry into Implement, Fixing or CIHealing (a fix round), or Plan (a plan
    /// item's planner; a return from Paused continues the attempt) or the last lost-worktree restart.
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
                if (e.State is WorkState.Implement or WorkState.Fixing or WorkState.CIHealing or WorkState.Plan && previous != WorkState.Paused)
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
    /// resumes by itself; a plan item waiting on its stories (Planned, sc-25871) gets one comment and one <c>plan-held</c> row per
    /// reason and stays Planned; an item the ledger does not know gets only the comment. Returns what was done, or null.
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
        if (known.State == WorkState.Planned)
        {
            // A plan waiting on its stories that cannot be evaluated (sc-25871: a story of it deleted on the board, a board read
            // that keeps failing): it may not escalate (it stays Planned and goes on by itself once the cause is gone), so it is
            // told once per reason — one comment, one row — and not again while that row is still its last.
            var detail = Plans.PlanRelease.NotEvaluatedPrefix + reason;
            if ((await ledger.HistoryAsync(known, ct)).LastOrDefault() is { Step: Steps.PlanHeld } last && last.Detail == detail)
            {
                log.WriteLine($"[{known.State}] {id} still cannot be evaluated: {reason}; already commented");
                return "held; already commented for this reason";
            }
            log.WriteLine($"[{known.State}] {id} cannot be evaluated: {reason}");
            try
            {
                await source.CommentAsync(storyId,
                    $"{id} is a plan waiting on its stories and the factory could not evaluate it, so none of its stories is released "
                    + "until this is resolved; a human needs to look. Reason:\n"
                    + UntrustedText.Fenced(LedgerReport.Clip(reason, LedgerReport.MaxReasonLength))
                    + $"\n{id} stays Planned: the factory evaluates it again every poll and goes on by itself once the cause is gone.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.WriteLine($"[{known.State}] could not comment on {id}: {ex.Message}");
                await ledger.CheckpointAsync(known, Steps.PlanHeld, null, $"{detail}; comment NOT posted: {ex.Message}", ct);
                return $"held; comment NOT posted: {ex.Message}";
            }
            await ledger.CheckpointAsync(known, Steps.PlanHeld, null, detail, ct);
            return "commented";
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

    /// <summary>The Paused row's detail prefix when the factory lost its claim on an item it was working (sc-25983).</summary>
    public const string ClaimLost = "claim lost";

    public static bool IsClaimLost(string? detail) => detail?.StartsWith(ClaimLost, StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether the item is paused because its claim was lost (<see cref="ClaimLostAsync"/>): its last transition is Paused and
    /// that row, or a <c>parked</c> row after it (an item already paused when the claim was lost), says so.
    /// </summary>
    private static bool ParkedForLostClaim(List<LedgerEntry> history)
    {
        var paused = history.FindLastIndex(e => e.Step is null);
        return paused >= 0 && history[paused].State == WorkState.Paused
            && history.Skip(paused).Any(e => (e.Step is null || e.Step == Steps.Parked) && IsClaimLost(e.Detail));
    }

    /// <summary>
    /// How a waiting plan item's claim is recorded as lost (sc-25984): the Detail of its <c>plan-held</c> row <c>claim lost: …</c>
    /// when no <c>plan-claim-regained</c> row follows it, else null. Told apart from the other <c>plan-held</c> rows by its prefix
    /// (<see cref="ClaimLost"/>; the others: a blocker outside the plan, <c>not evaluated: </c>, <c>held by a failed story: </c>).
    /// </summary>
    internal static string? PlanClaimLost(IReadOnlyList<LedgerEntry> history) =>
        history.LastOrDefault(e => e.Step == Steps.PlanClaimRegained || (e.Step == Steps.PlanHeld && IsClaimLost(e.Detail)))
            is { Step: Steps.PlanHeld } lost ? lost.Detail : null;

    /// <summary>
    /// The intake poll's claim renewal found an item held by another caller (sc-25983; see
    /// <see cref="IItemRunner.ClaimLostAsync"/>, <see cref="IWorkSource.RenewClaimAsync"/>): under the item's run lock the item is
    /// paused — Paused <c>claim lost: &lt;reason&gt;</c> from a handled state, then <c>parked</c> with the reason, which names
    /// the other claimant; an item already paused (one that resumes by itself) is parked where it is — and the item is told once,
    /// with what resumes it. The comment is posted before the <c>parked</c> row, so a comment the board would not take is on
    /// record (<c>…; comment NOT posted: &lt;error&gt;</c>): the item is never listed again, so nothing would post it later.
    /// Deliberately parked, so it is not one of the pauses that resume by themselves
    /// (<see cref="ResumesAutomatically"/>): someone else is working the item, the factory writes nothing more to it, no poll lists
    /// it (so its claim is not renewed either), and only a person's <c>factory run</c> takes it up again, from where it paused —
    /// and only once the item is the factory's again: that run asks the source first (<see cref="IWorkSource.RenewClaimAsync"/>,
    /// which takes a released or lapsed claim back) and, while someone else still holds the item, records nothing and writes
    /// nothing to the board (<see cref="ParkedForLostClaim"/>).
    /// Typed outcome: deferred (set aside, not judged), like every pause. Returns what was done, or null when nothing was: the
    /// ledger does not know the item, it is being stopped (the stop gives the item back anyway), its run is active (that run goes
    /// on — the board's claims are advisory — and the next poll finds the claim lost again), or it is in a state a lost claim does
    /// not matter in (a merged item).
    /// A plan item waiting on its stories (Planned, sc-25984) has no state to pause from and resumes by itself, so it is neither
    /// paused nor parked nor commented on (the item is someone else's): it gets one <c>plan-held</c> row <c>claim lost:
    /// &lt;reason&gt;</c> — not again while that is how its claim is recorded (<see cref="PlanClaimLost"/>) — and what was done is
    /// returned, so the poll leaves it out of its runs and shows it. While the claim is recorded lost no evaluation releases a
    /// story, opens or links a changeset pull request (<see cref="PlannedAsync"/> asks the source first, also on a
    /// <c>factory run</c> by hand); the item stays listed for renewal, and the first evaluation after a renewal that holds
    /// records <c>plan-claim-regained</c> and goes on.
    /// </summary>
    public static async Task<string?> ClaimLostAsync(IWorkSource source, WorkLedger ledger, IRunLocks locks, int storyId, string reason,
        TextWriter log, CancellationToken ct, IControls? controls = null)
    {
        var id = source.Naming.Format(storyId);
        if (await ledger.FindAsync(source.Naming.Source, id, ct) is not { } known)
        {
            return null;
        }
        if (await (controls ?? NoControls.Instance).EffectiveAsync(known.ExternalId, known.EpicId, ct) == ControlState.Stopping)
        {
            return null;
        }
        await using var runLock = await locks.TryAcquireAsync(known.Id, ct);
        if (runLock is null)
        {
            log.WriteLine($"[{known.State}] {id} lost its claim ({reason}) while a run of it is active; not touching it.");
            return null;
        }
        await ledger.RefreshAsync(known, known.Title, known.Repo, known.EpicId, ct);
        if (known.State == WorkState.Planned)
        {
            var detail = $"{ClaimLost}: {reason}";
            if (PlanClaimLost(await ledger.HistoryAsync(known, ct)) != detail)
            {
                await ledger.CheckpointAsync(known, Steps.PlanHeld, null, detail, ct);
            }
            log.WriteLine($"[{known.State}] {id} held: {detail}; not evaluated until its claim is the factory's again");
            return $"held ({detail}); not evaluated, nothing of the plan released, until its claim is the factory's again";
        }
        var from = known.State == WorkState.Paused ? (await ledger.ContextAsync(known, ct)).PausedFrom : known.State;
        if (from is not { } pausedAt || !HandledStates.Contains(pausedAt))
        {
            return null;
        }
        if (known.State != WorkState.Paused)
        {
            await ledger.RecordAsync(known, WorkState.Paused, null, $"{ClaimLost}: {reason}", ct);
        }
        string? notPosted = null;
        try
        {
            await source.CommentAsync(storyId,
                $"{id}: the factory lost its claim on this {source.Naming.Noun} — {reason} — so it paused its work on it (at {pausedAt}) "
                + "and writes nothing more to it. Once the item is the factory's to work again (the other claim released or lapsed), "
                + $"run `factory run {id}` to resume it where it paused, or `factory stop --item {id}` to give it up.", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.WriteLine($"[paused] could not comment on {id}: {ex.Message}");
            notPosted = $"; comment NOT posted: {ex.Message}";
        }
        await ledger.CheckpointAsync(known, Steps.Parked, null, $"{ClaimLost}: {reason}{notPosted}", ct);
        log.WriteLine($"[paused] {id} parked: {ClaimLost}: {reason}{notPosted}");
        return $"paused ({ClaimLost}: {reason}){notPosted}";
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
        // A plan of several repositories (sc-25873) may be partly delivered: the record first, then the comment says the same.
        var changesets = Plans.Changeset.States(history, RepoRef.Parse(item.Repo), item.State);
        var delivery = "";
        if (changesets.Count > 1)
        {
            var record = Plans.Changeset.DeliveryOf(changesets);
            if (!history.Skip(at + 1).Any(e => e.Step == Steps.PlanDelivery))
            {
                await ledger.CheckpointAsync(item, Steps.PlanDelivery, session, record, CancellationToken.None);
            }
            delivery = $"\n{Plans.Changeset.DeliveryNote(source.Naming.Format(storyId), changesets)}\n";
        }

        // The closeout on escalation (sc-25389): the reason (factory text that can quote model findings, so fenced as data) and the
        // ledger's facts, never a worker's summary.
        var comment = $"""
            [author: dark-factory] {source.Naming.Format(storyId)} escalated; a human needs to look.

            Reason:
            {UntrustedText.Fenced(LedgerReport.Clip(reason ?? "none recorded", LedgerReport.MaxReasonLength))}
            Last ledger state: {lastState}
            Claude session: {session ?? "none"}

            Re-run with `factory run {source.Naming.Format(storyId)}` once resolved.
            {delivery}
            """ + "\n" + LedgerReport.Facts(Plans.Changeset.CurrentPass(history), await ledger.SessionCostsAsync(item, ct)).TrimEnd();
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
        // The item's PR whenever it has one, whatever its state: an escalated, paused or stopped item's PR is the one an
        // operator has to look at.
        return new RunOutcome(item.Id, item.State, session, LinkedPullRequestUrl(history), error);
    }

    private static string IntakeDetail(WorkStory story) => $"{story.StoryType}: {story.AppUrl}";

    public static string BranchUrl(RepoRef repo, string branch) => $"https://github.com/{repo}/tree/{branch}";

    /// <summary>
    /// What every implementing prompt (implement, review fix, CI fix, conflict fix) asks before the worker stops, naming the
    /// build-and-test commands the worker is allowed (<see cref="ClaudeWorker.AllowedTools"/>: .NET's, and cargo's since sc-25877).
    /// </summary>
    internal const string BuildsAndPasses =
        "the build and the tests pass (`dotnet build` and `dotnet test`; in a Rust package `cargo build` and `cargo test`)";

    public static string BuildPrompt(WorkSpec spec, RepoRef repo)
    {
        var story = spec.Story;
        var prompt = $"""
            You are a Dark Factory worker. The current directory is a git worktree of {repo}.
            Implement {story.Kind.Noun} {story.Ref} ({story.StoryType}): {PromptFence.Spec(story)}

            Make the smallest change that satisfies the story, including a test when the
            project has tests, and make sure {BuildsAndPasses}.
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
    private async Task<string> PullRequestBodyAsync(Run run, CancellationToken ct)
    {
        var history = await ledger.HistoryAsync(run.Item, ct);
        var costs = await ledger.SessionCostsAsync(run.Item, ct);
        if (run.Changeset is not { } changeset)
        {
            return LedgerReport.PullRequestBody(run.Story, history, costs, run.BaseBranch);
        }
        // One of several changesets (sc-25873): this repository's stories, this pull request's rows, and where every changeset stands.
        var states = Plans.Changeset.States(history, run.Repo, run.Item.State);
        var stories = await PlanStoryReportsAsync(history, ct);
        if (states.Count > 1 && Plans.PlanRelease.RecordedRepos(history.LastOrDefault(e => e.Step == Steps.Plan)?.Detail,
                [.. stories.Select(s => s.ExternalId)], run.Repo) is { } repos)
        {
            var mine = repos.StoriesIn(changeset.Repo);
            stories = [.. stories.Where(s => mine.Contains(s.ExternalId))];
        }
        return LedgerReport.ChangesetPullRequestBody(run.Item.ExternalId, run.Story.AppUrl, changeset, stories, Plans.Changeset.CurrentPass(history), costs,
            states.Count > 1 ? states : null);
    }
}
