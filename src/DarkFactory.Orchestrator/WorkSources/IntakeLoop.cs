using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>Runs one item through the pipeline, and lists items a previous process left mid-run.</summary>
public interface IItemRunner
{
    /// <summary>
    /// Items whose ledger state has a handler (still in flight), e.g. after a crash; items being stopped first (a stop starts no
    /// new work, so it is finished even when the factory is frozen and the rest are deferred).
    /// </summary>
    Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct);

    /// <summary>
    /// Items whose claim the poll keeps (sc-25983, <see cref="IWorkSource.RenewClaimAsync"/>): every item the ledger would resume
    /// by itself — in a state the pipeline drives, or paused in a way that resumes by itself — whatever a Pause, the usage pause
    /// or the freeze says now (those only hold the item for a while; its claim must outlast them), except items being stopped
    /// (the stop gives the claim back). Never a list of what to run: only <see cref="InFlightAsync"/> is. The default is
    /// <see cref="InFlightAsync"/>, for a runner with no controls.
    /// </summary>
    Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct) => InFlightAsync(ct);

    Task<RunOutcome> RunAsync(int id, CancellationToken ct);

    /// <summary>
    /// Gives up on an item whose runs keep failing before its pipeline starts (E10): an item in a handled state is
    /// escalated, a paused one parked (so it no longer auto-resumes), each with a story comment giving the reason; an
    /// item the ledger does not know only gets the comment. Returns what was done, or null when nothing was (the item
    /// is running or already finished). The default records nothing.
    /// </summary>
    Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// Pauses an item whose claim the poll's renewal found held by another caller (sc-25983;
    /// <see cref="IWorkSource.RenewClaimAsync"/>): parked with <paramref name="reason"/>, which names the other claimant, and told so
    /// on its board. Returns what was done, or null when nothing was (the item is running, being stopped, or in a state a lost
    /// claim does not matter in). The default records nothing.
    /// </summary>
    Task<string?> ClaimLostAsync(int id, string reason, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// <c>factory work</c>: polls the work sources at a fixed interval (the Mac exposes no webhook
/// endpoint) and runs each in-flight or ready item through the pipeline, up to
/// <see cref="IntakeOptions.MaxConcurrentRuns"/> at once (sc-25988; 1, the default: one after another). Each item is started
/// at most once per poll, in the poll's order (each lane's in-flight items, then its ready ones). A poll does not wait for its runs
/// (sc-25992): the loop polls again on its interval while they go on, each poll renewing every lane's claims and starting work
/// only into the capacity the runs in flight leave, never a second run of an item this loop has in flight. Runs overlap
/// outside the phases that use a sandbox user, and inside them one per worker slot (<see cref="WorkerLock"/>: a run waits for its
/// slot there), and one repository's
/// merges stay one at a time (the merge queue's turn). The
/// pipeline's Intake claims the item; its run lock stops a second run — of this process or another — running the same item.
/// While the factory is paused (<see cref="Controls.ControlScope.Factory"/>, the usage pause
/// <see cref="Controls.ControlScope.Usage"/>, or the freeze <see cref="Controls.ControlScope.Freeze"/>) it lists no new ready items; the runner leaves out in-flight items a
/// control pauses (their claims are still renewed, <see cref="RenewClaimsAsync"/>), and the pipeline itself refuses to claim a story in a paused epic. Each poll first reads the
/// router's usage (<paramref name="usage"/>), and a usage pause wakes the loop at its resume time, so work starts
/// again then without waiting for the next interval.
/// A failing poll or run is shown on the dashboard (<paramref name="status"/>, E10). A factory-wide failure
/// (<see cref="FactoryUnavailableException"/>, a ledger database error, or the poll itself) ends the poll and escalates
/// nothing; any other failure counts against its item, and after <see cref="IntakeOptions.MaxItemFailures"/> in a row
/// the loop gives up on the item (<see cref="IItemRunner.GiveUpAsync"/>) instead of retrying it every poll.
/// <paramref name="lanes"/> holds one lane per work source that is watched or has its credential (sc-25982,
/// <see cref="WorkSourceRegistry.Lanes"/>: any set of them, in <see cref="WorkSourceRegistry.Kinds"/> order; an unwatched one
/// lists nothing ready and only has its in-flight items finished; none polls nothing), listed in turn by the same loop
/// before any item of the poll runs (so a lane's preparation — a triage — runs beside no item run of this process); a source
/// whose listing fails is skipped for that poll without holding up the others. Items are
/// keyed by their external id (<c>sc-12</c>, <c>gh-3</c>, <c>kb-4</c>) on the dashboard. A run deferred by the freeze evaluator
/// (<see cref="RunOutcome.Deferred"/>) is shown on the dashboard and ends the poll's new work: no ready item of any lane is
/// started after it, and no further in-flight item of its lane — but every lane's in-flight items are started up to then, and
/// those being stopped come first (<see cref="IItemRunner.InFlightAsync"/>), so a pending Stop is finished in a frozen factory
/// too. A factory-wide failure starts nothing more this poll. Runs already started when either happens run to their end: each
/// run checks the freeze, the usage pause and its controls itself, before every step.
/// What stops new work with runs in flight (sc-25991) — a poll's runs overlap, so each of these can happen while others run:
/// (1) a factory-wide control set since the poll listed its work (the usage pause, e.g. by a sibling run whose worker hit the
/// limit; a Pause of the factory; the freeze's row): the controls are read again before each ready item is started
/// (<see cref="HoldsAsync"/>) and, held, no ready item of the poll is started — shown with what lifts it
/// (<see cref="IntakeStatus.Deferrals"/>); in-flight items listed for the poll are still run, each run pausing itself at its
/// first check (or finishing its Stop); (2) a run the freeze deferred, as above — every deferred run is shown, not only the
/// last; (3) a factory-wide failure of one run: shown once per poll however many runs or listings hit it
/// (<see cref="IntakeStatus.FactoryFailedAlso"/>), it starts nothing more, and it is never a failure of its own item or of a
/// run beside it — a sibling's own failure is counted against that sibling alone, as ever.
/// <paramref name="freeze"/>, when set, is the
/// freeze evaluator run at the start of each poll: frozen (or unable to check), no lane is prepared (no triage) or listed.
/// </summary>
public sealed class IntakeLoop(IReadOnlyList<IntakeLane> lanes, IntakeOptions options, TimeProvider time, ILogger<IntakeLoop> logger,
    Controls.IControls? controls = null, Router.UsageMonitor? usage = null, IntakeStatus? status = null,
    Func<CancellationToken, Task<Controls.FreezeStatus>>? freeze = null)
    : BackgroundService
{
    private readonly IntakeStatus _status = status ?? new IntakeStatus(time);

    /// <summary>Whether <paramref name="ex"/> is the factory's failure rather than its item's.</summary>
    public static bool IsFactoryWide(Exception ex) =>
        ex is FactoryUnavailableException or MissingCredentialException or System.Data.Common.DbException
        || ex.InnerException is System.Data.Common.DbException;

    private int _waits;

    /// <summary>
    /// How many times the loop has armed its wake-up (the next tick, the end of a usage pause, or the end of a run in flight) and
    /// is waiting on it: once it has, moving an injected clock wakes it (a test must not move the clock before this).
    /// </summary>
    internal int Waits => Volatile.Read(ref _waits);

    private void Armed() => Interlocked.Increment(ref _waits);

    // Runs in flight across polls (sc-25992): an item is in at most one run of this loop at a time (by its external id), and
    // there are never more than Intake:MaxConcurrentRuns. A poll dispatches only into the capacity the runs in flight leave.
    private readonly Lock _runsGate = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _capacity = new(options.MaxConcurrentRuns, options.MaxConcurrentRuns);
    private Poll? _current;
    private TaskCompletionSource _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The items this loop has a run of in flight now (<c>sc-12</c>, <c>kb-4</c>).</summary>
    internal IReadOnlyList<string> RunsInFlight
    {
        get
        {
            lock (_runsGate)
            {
                return [.. _running.Keys];
            }
        }
    }

    private Task RunEnded()
    {
        lock (_runsGate)
        {
            return _runEnded.Task;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Polling for work every {Interval}, running up to {Runs} item(s) at once", options.PollInterval, options.MaxConcurrentRuns);
        using var timer = new PeriodicTimer(options.PollInterval, time);
        Task<bool>? tick = null;
        try
        {
            while (true)
            {
                // A poll does not wait for its runs (sc-25992): it dispatches into the free capacity, and the loop polls again on
                // its interval — renewing claims, finishing stops, starting newly ready items — while they go on.
                var ended = RunEnded();
                var resumeAt = (await StartPollAsync(stoppingToken)).ResumeAt;
                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                while (true)
                {
                    // One clock reading decides the wait, and the wake-up is armed straight after it.
                    var now = time.GetUtcNow();
                    Task? lifted = resumeAt is { } at && at - now is var wait && wait < options.PollInterval
                        // Paused for usage until before the next tick: poll again as soon as it lifts.
                        ? Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, stoppingToken)
                        : null;
                    Armed();
                    var woke = await Task.WhenAny(lifted is null ? [tick, ended] : [tick, ended, lifted]);
                    stoppingToken.ThrowIfCancellationRequested();
                    if (woke != ended)
                    {
                        break;
                    }
                    // A run ended: it may have paused the factory for usage (its worker hit the limit), so the loop wakes when
                    // that pause lifts, as it did when a poll waited for its runs.
                    ended = RunEnded();
                    resumeAt = await UsageResumeAtAsync(resumeAt, stoppingToken);
                }
                if (tick.IsCompleted)
                {
                    if (!await tick)
                    {
                        return;
                    }
                    tick = null;
                }
            }
        }
        finally
        {
            // Shutdown waits for the runs in flight: each sees the cancelled token and ends as when a poll waited for them.
            await AllRunsEndedAsync();
        }
    }

    /// <summary>Waits until this loop has no run in flight and its last poll's dispatch has ended.</summary>
    private async Task AllRunsEndedAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_runsGate)
            {
                pending = [.. _running.Values, .. _current is { } poll ? new[] { poll.Done } : []];
            }
            if (pending.All(t => t.IsCompleted))
            {
                return;
            }
            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // A run's own failure or cancellation was reported where it ran.
            }
        }
    }

    private async Task<DateTimeOffset?> UsageResumeAtAsync(DateTimeOffset? fallback, CancellationToken ct)
    {
        try
        {
            return controls is null ? fallback : (await controls.UsagePauseAsync(ct))?.ResumeAt;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read the usage pause; polling again next interval");
            return null;
        }
    }

    /// <summary>
    /// The factory-wide controls that hold new work now (sc-25991), each with what lifts it: the usage pause while it is in
    /// effect, a Pause of the factory, and the freeze's row (set until a human's Continue). Read at each poll's start and again
    /// before a ready item is dispatched — runs overlap, so one may have paused the factory since the poll listed its work. An
    /// unreadable record throws (the caller counts it as the factory's failure: frozen too, E2).
    /// </summary>
    private async Task<(IReadOnlyList<(string Scope, string Why, string Lifts)> Holds, DateTimeOffset? ResumeAt)> HoldsAsync(CancellationToken ct)
    {
        if (controls is null)
        {
            return ([], null);
        }
        var holds = new List<(string Scope, string Why, string Lifts)>();
        var usagePause = await controls.UsagePauseAsync(ct);
        if (usagePause is not null)
        {
            holds.Add((Controls.ControlScope.Usage, $"the factory is paused for usage ({usagePause.Reason ?? "no reason recorded"})",
                (usagePause.ResumeAt is { } at ? $"it lifts by itself at {at:u}, or with " : "lifted by ") + "`factory continue --usage`"));
        }
        if ((await controls.GetAsync(Controls.ControlScope.Factory, ct))?.State == Controls.ControlState.Paused)
        {
            holds.Add((Controls.ControlScope.Factory, "the factory is paused", "lifted by `factory continue --factory`"));
        }
        // Frozen (sc-25387): no new work until a human's Continue.
        if (await controls.GetAsync(Controls.ControlScope.Freeze, ct) is { State: not Controls.ControlState.Running } frozen)
        {
            holds.Add((Controls.ControlScope.Freeze, $"the factory is frozen ({frozen.Reason ?? "no trigger recorded"})",
                Controls.FreezeTrigger.Remedy(frozen.Reason ?? "")));
        }
        return (holds, usagePause?.ResumeAt);
    }

    /// <summary>
    /// One poll, waited for to its end: resume in-flight items first, then run newly ready ones, and return once every run it
    /// started has ended. Returns when a usage pause in effect lifts, if one is.
    /// </summary>
    public async Task<DateTimeOffset?> PollOnceAsync(CancellationToken ct)
    {
        var (resumeAt, done, listed) = await StartPollAsync(ct);
        await done;
        // A run may have paused the factory for usage (its worker hit the limit) or the pause may have moved.
        return listed ? await UsageResumeAtAsync(resumeAt, ct) : resumeAt;
    }

    /// <summary>
    /// What one poll decided for the items it listed and has not started yet, read and written under its gate by its dispatch and
    /// its runs. A newer poll that lists again supersedes it (<see cref="Supersede"/>): it starts nothing more, since the newer
    /// listing is fresher; its runs go on.
    /// </summary>
    private sealed class Poll(CancellationToken ct)
    {
        public readonly Lock Gate = new();
        public readonly CancellationTokenSource Dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public Queue<(IntakeLane Lane, int Id, bool Ready)> Queue = new();
        public bool FactoryFailedFlag;
        public bool RunsFailed;
        public bool DeferredRun;
        public bool NewWorkHeld;
        public readonly HashSet<IntakeLane> DeferredLanes = [];
        public Task Done = Task.CompletedTask;

        public void Supersede()
        {
            try
            {
                Dispatch.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void FactoryFailed(Poll poll, string message)
    {
        lock (poll.Gate)
        {
            if (poll.FactoryFailedFlag)
            {
                _status.FactoryFailedAlso(message);
            }
            else
            {
                _status.FactoryFailed(message);
            }
            poll.FactoryFailedFlag = true;
        }
    }

    /// <summary>
    /// One poll's listing and the start of its dispatch (sc-25992): renews every lane's claims, then — only while there is free run
    /// capacity — lists each lane's in-flight and ready items and starts them into that capacity, in the poll's order, skipping
    /// any item a run of this loop already has in flight. Returns as soon as the dispatch has started; <c>Done</c> completes
    /// once the dispatch and every run it started have ended. <c>Listed</c> is false when the poll failed before listing.
    /// </summary>
    internal async Task<(DateTimeOffset? ResumeAt, Task Done, bool Listed)> StartPollAsync(CancellationToken ct)
    {
        var poll = new Poll(ct);
        DateTimeOffset? resumeAt = null;
        bool factoryPaused;
        try
        {
            if (usage is not null)
            {
                await usage.CheckAsync(ct);
            }
            // An unreadable record fails the poll below (frozen too).
            (var holds, resumeAt) = await HoldsAsync(ct);
            _status.Holding(holds);
            factoryPaused = holds.Count > 0;
            // The freeze evaluator itself (sc-25387), before any lane is listed or prepared: a trigger that holds but is not yet
            // written, or one that cannot be checked (E2), stops triage and new work this poll as a written freeze does.
            if (!factoryPaused && freeze is not null)
            {
                if (await freeze(ct) is { Frozen: true } frozen)
                {
                    factoryPaused = true;
                    _status.Deferred("intake", frozen.Message, frozen.Remedy);
                }
                else
                {
                    _status.NotDeferred();
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Poll failed; retrying next interval");
            FactoryFailed(poll, $"Poll failed: {ex.Message}");
            return (resumeAt, Task.CompletedTask, false);
        }
        // Claims first, every lane's over its own list (IItemRunner.RenewableAsync: paused and frozen items too, and items a run
        // of this loop has in flight), before any lane is prepared or listed: a lane's preparation (a triage, which may wait for
        // a worker slot) never holds up another lane's renewals. The items to run are listed after, so one just paused for a
        // lost claim is not among them.
        var renewed = new Dictionary<IntakeLane, IReadOnlyList<int>>();
        foreach (var lane in lanes)
        {
            try
            {
                renewed[lane] = await RenewClaimsAsync(lane, await lane.Runner.RenewableAsync(ct), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One source failing does not hold up another's items.
                logger.LogError(ex, "Poll of {Source} failed; retrying next interval", lane.Source.Naming.Source);
                FactoryFailed(poll, $"Poll failed: {ex.Message}");
            }
        }
        int free;
        lock (_runsGate)
        {
            free = options.MaxConcurrentRuns - _running.Count;
        }
        if (free <= 0)
        {
            // Every run this loop may have is in flight: nothing could start, so nothing is listed or prepared, and the earlier
            // poll's dispatch keeps its place (it starts its next item when a run ends).
            logger.LogInformation("{Runs} run(s) in flight, no capacity free: claims renewed, nothing new listed this poll", options.MaxConcurrentRuns);
            if (!poll.FactoryFailedFlag)
            {
                _status.FactoryOk();
            }
            return (resumeAt, Task.CompletedTask, true);
        }
        var work = new List<(IntakeLane Lane, IReadOnlyList<int> InFlight, IReadOnlyList<int> Ready)>();
        foreach (var lane in lanes)
        {
            if (!renewed.TryGetValue(lane, out var lost))
            {
                continue;
            }
            try
            {
                IReadOnlyList<int> inFlight = (await lane.Runner.InFlightAsync(ct)).Except(lost).ToList();
                IReadOnlyList<int> ready = [];
                if (!factoryPaused)
                {
                    // E.g. GitHub issues: triage new issues and record approvals, so the items they release are listed below.
                    if (lane.Prepare is not null)
                    {
                        await lane.Prepare(ct);
                    }
                    ready = await lane.Source.ListReadyAsync(ct);
                }
                work.Add((lane, inFlight, ready.Except(inFlight).Distinct().ToList()));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One source failing to list (its board down) does not hold up another's items.
                logger.LogError(ex, "Poll of {Source} failed; retrying next interval", lane.Source.Naming.Source);
                FactoryFailed(poll, $"Poll failed: {ex.Message}");
            }
        }
        // The poll's runs, in order: each lane's in-flight items (those being stopped first), then its ready ones; an item once.
        var listed = new HashSet<string>(StringComparer.Ordinal);
        poll.Queue = new Queue<(IntakeLane Lane, int Id, bool Ready)>(work
            .SelectMany(w => w.InFlight.Select(id => (w.Lane, Id: id, Ready: false)).Concat(w.Ready.Select(id => (w.Lane, Id: id, Ready: true))))
            .Where(x => listed.Add(x.Lane.Source.Naming.Format(x.Id))));
        lock (_runsGate)
        {
            // This listing is fresher than the last poll's: that one starts nothing more (its runs go on).
            _current?.Supersede();
            _current = poll;
            poll.Done = DispatchAsync(poll, ct);
        }
        return (resumeAt, poll.Done, true);
    }

    /// <summary>
    /// Whether a ready item may still be started (sc-25991): the poll listed its work a while ago and other runs are in flight —
    /// one of them, or a person, may have paused the factory for usage, paused it or frozen it since. Then no ready item of the
    /// poll is started, and the hold is shown with what lifts it. Controls that cannot be read are the factory's failure, which
    /// starts nothing more (E2: never dispatch blind).
    /// </summary>
    private async Task<bool> MayStartNewAsync(Poll poll, string name, CancellationToken ct)
    {
        try
        {
            var (holds, _) = await HoldsAsync(ct);
            if (holds.Count == 0)
            {
                return true;
            }
            logger.LogInformation("{Item}: not started, nor any other ready item this poll: {Why}", name, string.Join("; ", holds.Select(h => h.Why)));
            _status.Holding(holds);
            lock (poll.Gate)
            {
                poll.NewWorkHeld = true;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read the controls before starting {Item}; ending this poll", name);
            lock (poll.Gate)
            {
                poll.RunsFailed = true;
            }
            FactoryFailed(poll, $"{name} not started: the controls could not be read: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// The poll's next item to start, or null when it starts nothing more: in-flight items (those being stopped first) even after
    /// another lane's deferral — a Stop starts no new work; ready ones only while nothing was deferred (the factory is frozen, so
    /// every new item would be deferred the same way) and no factory-wide control holds new work. An item a run of this loop
    /// already has in flight is passed over when it would be started (<see cref="DispatchAsync"/>).
    /// </summary>
    private (IntakeLane Lane, int Id, bool Ready)? Next(Poll poll)
    {
        lock (poll.Gate)
        {
            while (!poll.RunsFailed && poll.Queue.TryDequeue(out var next))
            {
                if (poll.DeferredLanes.Contains(next.Lane) || (next.Ready && (poll.DeferredRun || poll.NewWorkHeld)))
                {
                    continue;
                }
                return next;
            }
            return null;
        }
    }

    /// <summary>
    /// Starts the poll's items one by one as run capacity frees (<see cref="IntakeOptions.MaxConcurrentRuns"/>), until its queue
    /// is empty, something stops its new work, or a newer poll supersedes it; then waits for the runs it started.
    /// </summary>
    private async Task DispatchAsync(Poll poll, CancellationToken ct)
    {
        await Task.Yield();
        var started = new List<Task>();
        try
        {
            while (true)
            {
                try
                {
                    await _capacity.WaitAsync(poll.Dispatch.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // superseded by a newer poll
                }
                var held = true;
                try
                {
                    // A run that ended on shutdown may give its capacity back before this wait sees the cancellation.
                    if (ct.IsCancellationRequested || Next(poll) is not var (lane, id, ready))
                    {
                        break;
                    }
                    var name = lane.Source.Naming.Format(id);
                    if (ready && !await MayStartNewAsync(poll, name, ct))
                    {
                        continue;
                    }
                    lock (_runsGate)
                    {
                        // Superseded or shut down meanwhile, or the item started by another poll's dispatch: not started here.
                        if (ct.IsCancellationRequested || poll.Dispatch.IsCancellationRequested || _running.ContainsKey(name))
                        {
                            continue;
                        }
                        // Registered in flight before it starts; it leaves the in-flight set and gives the capacity back when it
                        // ends.
                        var start = new Task<Task>(async () =>
                        {
                            try
                            {
                                await RunOneAsync(poll, lane, id, name, ct);
                            }
                            finally
                            {
                                Ended(name);
                            }
                        });
                        var run = start.Unwrap();
                        _running[name] = run;
                        held = false;
                        started.Add(run);
                        start.Start(TaskScheduler.Default);
                    }
                }
                finally
                {
                    if (held)
                    {
                        _capacity.Release();
                    }
                }
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(started);
            }
            finally
            {
                lock (_runsGate)
                {
                    // The newest poll that had no factory-wide failure says the factory is fine; an older one does not.
                    if (ReferenceEquals(_current, poll) && !poll.FactoryFailedFlag)
                    {
                        _status.FactoryOk();
                    }
                }
                poll.Dispatch.Dispose();
            }
        }
    }

    private void Ended(string name)
    {
        TaskCompletionSource ended;
        lock (_runsGate)
        {
            _running.Remove(name);
            ended = _runEnded;
            _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _capacity.Release();
        ended.TrySetResult();
    }

    private async Task RunOneAsync(Poll poll, IntakeLane lane, int id, string name, CancellationToken ct)
    {
        try
        {
            var outcome = await lane.Runner.RunAsync(id, ct);
            logger.LogInformation("{Item}: {State}{Error}", name, outcome.State, outcome.Error is null ? "" : $" ({outcome.Error})");
            _status.ItemOk(name);
            lock (poll.Gate)
            {
                if (outcome.Deferred is { } deferred)
                {
                    // The factory is frozen: every other item of the lane would be deferred the same way (its stops came first).
                    // Each deferred run is shown: with runs in flight, several of one poll can be.
                    _status.Deferred(name, deferred);
                    poll.DeferredRun = true;
                    poll.DeferredLanes.Add(lane);
                }
                else if (!poll.DeferredRun)
                {
                    _status.NotDeferred();
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsFactoryWide(ex))
        {
            // Every other item would fail the same way: show it once, escalate nothing, start nothing more, try again next poll.
            // Not this item's failure, nor one of a run beside it (which ends by itself, its outcome its own).
            logger.LogError(ex, "Run of {Item} failed for a factory-wide reason; ending this poll", name);
            // Nothing more is started from here on, before anyone is told: whoever sees the error shown sees a poll that has
            // already stopped dispatching.
            lock (poll.Gate)
            {
                poll.RunsFailed = true;
            }
            FactoryFailed(poll, $"{name} could not run: {ex.Message}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Counted against this item alone, whatever runs beside it.
            logger.LogError(ex, "Run of {Item} failed", name);
            var failures = _status.ItemFailed(name, $"{ex.GetType().Name}: {ex.Message}");
            if (failures == options.MaxItemFailures)
            {
                await GiveUpAsync(lane, id, $"{failures} runs in a row failed before the pipeline could start; last error: {ex.GetType().Name}: {ex.Message}", ct);
            }
        }
    }

    /// <summary>
    /// Keeps the factory's claims (sc-25983): each poll, before anything is listed or run, every item of the lane in
    /// <paramref name="renewable"/> (<see cref="IItemRunner.RenewableAsync"/>) has its claim renewed by its source
    /// (<see cref="IWorkSource.RenewClaimAsync"/>; nothing for a source whose claims do not expire) — whatever the lane's source
    /// is, and also while the factory, the item or its epic is paused, usage is paused or the factory is frozen, so a claim
    /// outlives its first lifetime however long the item waits. Renewing runs nothing: the items to run are the runner's
    /// in-flight list, read separately. A parked or escalated item, and one being stopped, is not in the list and is not
    /// renewed. A claim lost to another caller pauses the item (<see cref="IItemRunner.ClaimLostAsync"/>) and is shown on the
    /// dashboard; the ids returned are those, left out of this poll's runs. A renewal the board refused for the item's or the
    /// call's own reason (<see cref="ClaimRenewal.NotRenewed"/>) is logged and the next item asked. A renewal that throws — the
    /// board is down or hanging, and each further call could wait out its whole timeout, inline in the poll — ends the lane's
    /// renewals for this poll: the rest are tried next poll (a claim lasts far longer than a poll, and an item's own run reports
    /// a board that stays down), and the other lanes are not held up by it.
    /// </summary>
    private async Task<IReadOnlyList<int>> RenewClaimsAsync(IntakeLane lane, IReadOnlyList<int> renewable, CancellationToken ct)
    {
        var lost = new List<int>();
        for (var i = 0; i < renewable.Count; i++)
        {
            var id = renewable[i];
            var name = lane.Source.Naming.Format(id);
            ClaimRenewal renewal;
            try
            {
                renewal = await lane.Source.RenewClaimAsync(id, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not renew the claim of {Item}; not renewing {Rest} more of {Source} this poll, trying again next poll",
                    name, renewable.Count - i - 1, lane.Source.Naming.Source);
                break;
            }
            if (renewal.NotRenewed is { } refused)
            {
                logger.LogWarning("{Item}: claim not renewed: {Reason}; trying again next poll", name, refused);
                continue;
            }
            if (renewal.Lost is not { } reason)
            {
                continue;
            }
            logger.LogWarning("{Item}: claim lost: {Reason}", name, reason);
            try
            {
                if (await lane.Runner.ClaimLostAsync(id, reason, ct) is { } what)
                {
                    _status.ItemFailed(name, $"claim lost: {reason}");
                    _status.ItemGaveUp(name, what);
                    lost.Add(id);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not pause {Item} for its lost claim; trying again next poll", name);
            }
        }
        return lost;
    }

    private async Task GiveUpAsync(IntakeLane lane, int id, string reason, CancellationToken ct)
    {
        var name = lane.Source.Naming.Format(id);
        try
        {
            if (await lane.Runner.GiveUpAsync(id, reason, ct) is { } what)
            {
                logger.LogWarning("{Item}: gave up after repeated failures ({What})", name, what);
                _status.ItemGaveUp(name, what);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Shown on the dashboard still; the next streak of failures tries again.
            logger.LogError(ex, "Could not give up on {Item}", name);
        }
    }
}

/// <summary>
/// One work source the intake loop polls, with the runner of its items. <paramref name="Prepare"/>, when set, runs before each
/// listing while the factory is not paused (GitHub issues: <see cref="Issues.IssueIntake.PollAsync"/> triages new issues
/// and records approvals, releasing the items the listing then returns).
/// </summary>
public sealed record IntakeLane(IWorkSource Source, IItemRunner Runner, Func<CancellationToken, Task>? Prepare = null);

/// <param name="MaxItemFailures"><c>Intake:MaxItemFailures</c>: runs of one item in a row that may fail before the loop gives up on it.</param>
/// <param name="MaxConcurrentRuns"><c>Intake:MaxConcurrentRuns</c> (sc-25988): how many items the loop runs at once; 1 runs them one after another.</param>
public sealed record IntakeOptions(TimeSpan PollInterval, int MaxItemFailures = IntakeOptions.DefaultMaxItemFailures,
    int MaxConcurrentRuns = IntakeOptions.DefaultMaxConcurrentRuns)
{
    public const int DefaultMaxItemFailures = 3;
    public const int DefaultMaxConcurrentRuns = 1;

    /// <summary>The most runs at once a factory may be configured for: each holds ledger connections and its own clients.</summary>
    public const int MaxConcurrentRunsLimit = 8;

    public int MaxConcurrentRuns { get; } = MaxConcurrentRuns is >= 1 and <= MaxConcurrentRunsLimit
        ? MaxConcurrentRuns
        : throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRuns), MaxConcurrentRuns, $"must be from 1 to {MaxConcurrentRunsLimit}");
}

public static class IntakeServiceCollectionExtensions
{
    /// <summary>Registers the intake loop and the registry of work sources (<see cref="WorkSourceRegistry"/>) on any host.</summary>
    public static IServiceCollection AddIntake(this IServiceCollection services, FactoryOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new IntakeOptions(options.PollInterval, options.MaxItemFailures, options.MaxConcurrentRuns));
        // Each run reads the Freeze:* thresholds (FactoryRunner); a bad value fails start-up here rather than every run.
        _ = options.Freeze;
        services.TryAddSingleton(sp => new IntakeStatus(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new Router.UsageOptions(options.UsagePollInterval));
        services.AddSingleton<Router.IUsageSource>(_ =>
            new Router.RouterClient(Gateway.OutboundHttp.RouterApi(options.RouterBaseUrl), options.RouterKey));
        services.AddSingleton(sp => new Router.UsageMonitor(sp.GetRequiredService<Router.IUsageSource>(), sp.GetRequiredService<Controls.IControls>(),
            sp.GetRequiredService<Router.UsageOptions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Router.UsageMonitor>>()));
        // Watches usage while a long item run holds up the intake loop, so running workers pause too.
        services.AddHostedService(sp => sp.GetRequiredService<Router.UsageMonitor>());
        // Every source the host reaches comes from this one registry (sc-25982): the loop's lanes, the dashboard's controls and Stop.
        services.AddSingleton(_ => new WorkSourceRegistry(options));
        // The dashboard's Stop finishes a stop itself when no run holds the item, on the item's own source.
        services.AddSingleton<Controls.IItemStops>(sp => new FactoryItemStops(options, sp.GetRequiredService<WorkSourceRegistry>(), Console.Out));
        services.AddHostedService(sp =>
        {
            // One loop lists every configured source in turn; a triage and the items' worker phases, of whatever source, share the
            // worker slots — each a single-tenant sandbox user, one worker phase at a time through its lock (WorkerLock; WorkerSlots,
            // sc-25989). A watched source without its credential
            // fails the host's start here.
            var status = sp.GetRequiredService<IntakeStatus>();
            var lanes = sp.GetRequiredService<WorkSourceRegistry>().Lanes(source => new FactoryItemRunner(options, source, Console.Out), status, Console.Out);
            // One evaluator for the loop's life, so its main-red reads are reused within FactoryFreeze.MainRedCacheTtl; built on the
            // first poll, so a missing credential fails that poll (counted as frozen) rather than the host's start.
            Controls.FactoryFreeze? evaluator = null;
            return new IntakeLoop(
                lanes, sp.GetRequiredService<IntakeOptions>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<IntakeLoop>>(), sp.GetRequiredService<Controls.IControls>(),
                sp.GetRequiredService<Router.UsageMonitor>(), status,
                ct => (evaluator ??= FactoryRunner.CreateFreeze(options)).CheckAsync(ct));
        });
        return services;
    }
}
