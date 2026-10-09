using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>Runs one item through the pipeline, and lists items a previous process left mid-run.</summary>
public interface IItemRunner
{
    /// <summary>Items whose ledger state has a handler (still in flight), e.g. after a crash.</summary>
    Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct);

    Task<RunOutcome> RunAsync(int id, CancellationToken ct);

    /// <summary>
    /// Gives up on an item whose runs keep failing before its pipeline starts (E10): an item in a handled state is
    /// escalated, a paused one parked (so it no longer auto-resumes), each with a story comment giving the reason; an
    /// item the ledger does not know only gets the comment. Returns what was done, or null when nothing was (the item
    /// is running or already finished). The default records nothing.
    /// </summary>
    Task<string?> GiveUpAsync(int id, string reason, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// <c>factory work</c>: polls the work source at a fixed interval (the Mac exposes no webhook
/// endpoint) and runs each in-flight or ready item through the pipeline, one at a time. The
/// pipeline's Intake claims the item; its run lock stops a second process running the same item.
/// While the factory is paused (<see cref="Controls.ControlScope.Factory"/>, the usage pause
/// <see cref="Controls.ControlScope.Usage"/>, or the freeze <see cref="Controls.ControlScope.Freeze"/>) it lists no new ready items; the runner leaves out in-flight items a
/// control pauses, and the pipeline itself refuses to claim a story in a paused epic. Each poll first reads the
/// router's usage (<paramref name="usage"/>), and a usage pause wakes the loop at its resume time, so work starts
/// again then without waiting for the next interval.
/// A failing poll or run is shown on the dashboard (<paramref name="status"/>, E10). A factory-wide failure
/// (<see cref="FactoryUnavailableException"/>, a ledger database error, or the poll itself) ends the poll and escalates
/// nothing; any other failure counts against its item, and after <see cref="IntakeOptions.MaxItemFailures"/> in a row
/// the loop gives up on the item (<see cref="IItemRunner.GiveUpAsync"/>) instead of retrying it every poll.
/// <paramref name="moreLanes"/> adds work sources after the first (GitHub issues, sc-25385), polled in turn by the same loop, so
/// their runs never overlap; a source whose listing fails is skipped for that poll without holding up the others. Items are
/// keyed by their external id (<c>sc-12</c>, <c>gh-3</c>) on the dashboard. A run deferred by the freeze evaluator
/// (<see cref="RunOutcome.Deferred"/>) ends the poll and is shown on the dashboard. <paramref name="freeze"/>, when set, is the
/// freeze evaluator run at the start of each poll: frozen (or unable to check), no lane is prepared (no triage) or listed.
/// </summary>
public sealed class IntakeLoop(IWorkSource source, IItemRunner runner, IntakeOptions options, TimeProvider time, ILogger<IntakeLoop> logger,
    Controls.IControls? controls = null, Router.UsageMonitor? usage = null, IntakeStatus? status = null, IReadOnlyList<IntakeLane>? moreLanes = null,
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
    /// How many times the loop has finished a poll and armed its wake-up (the next tick, or the end of a usage pause) and is
    /// waiting on it: once it has, moving an injected clock wakes it (a test must not move the clock before this).
    /// </summary>
    internal int Waits => Volatile.Read(ref _waits);

    private void Armed() => Interlocked.Increment(ref _waits);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Polling for work every {Interval}", options.PollInterval);
        using var timer = new PeriodicTimer(options.PollInterval, time);
        Task<bool>? tick = null;
        while (true)
        {
            var resumeAt = await PollOnceAsync(stoppingToken);
            tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            // One clock reading decides the wait, and the wake-up is armed straight after it.
            var now = time.GetUtcNow();
            if (resumeAt is { } at && at - now is var wait && wait < options.PollInterval)
            {
                // Paused for usage until before the next tick: poll again as soon as it lifts.
                var lifted = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, stoppingToken);
                Armed();
                await Task.WhenAny(tick, lifted);
                stoppingToken.ThrowIfCancellationRequested();
            }
            else
            {
                Armed();
                await tick;
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

    /// <summary>One poll: resume in-flight items first, then run newly ready ones. Returns when a usage pause in effect lifts, if one is.</summary>
    public async Task<DateTimeOffset?> PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<IntakeLane> lanes = [new IntakeLane(source, runner), .. moreLanes ?? []];
        var work = new List<(IntakeLane Lane, IReadOnlyList<int> Ids)>();
        DateTimeOffset? resumeAt = null;
        bool factoryPaused;
        try
        {
            if (usage is not null)
            {
                await usage.CheckAsync(ct);
            }
            var usagePause = controls is null ? null : await controls.UsagePauseAsync(ct);
            resumeAt = usagePause?.ResumeAt;
            factoryPaused = usagePause is not null || (controls is not null
                && ((await controls.GetAsync(Controls.ControlScope.Factory, ct))?.State == Controls.ControlState.Paused
                    // Frozen (sc-25387): no new work until a human's Continue; an unreadable record fails the poll above (frozen too).
                    || (await controls.GetAsync(Controls.ControlScope.Freeze, ct)) is { State: not Controls.ControlState.Running }));
            // The freeze evaluator itself (sc-25387), before any lane is listed or prepared: a trigger that holds but is not yet
            // written, or one that cannot be checked (E2), stops triage and new work this poll as a written freeze does.
            if (!factoryPaused && freeze is not null)
            {
                if (await freeze(ct) is { Frozen: true } frozen)
                {
                    factoryPaused = true;
                    _status.Deferred("intake", frozen.Message);
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
            _status.FactoryFailed($"Poll failed: {ex.Message}");
            return resumeAt;
        }
        var factoryFailed = false;
        foreach (var lane in lanes)
        {
            try
            {
                var inFlight = await lane.Runner.InFlightAsync(ct);
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
                work.Add((lane, inFlight.Concat(ready).Distinct().ToList()));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One source failing to list (its board down) does not hold up another's items.
                logger.LogError(ex, "Poll of {Source} failed; retrying next interval", lane.Source.Naming.Source);
                _status.FactoryFailed($"Poll failed: {ex.Message}");
                factoryFailed = true;
            }
        }
        var runsFailed = false;
        foreach (var (lane, ids) in work)
        {
            foreach (var id in ids.TakeWhile(_ => !runsFailed))
            {
                var name = lane.Source.Naming.Format(id);
                try
                {
                    var outcome = await lane.Runner.RunAsync(id, ct);
                    logger.LogInformation("{Item}: {State}{Error}", name, outcome.State, outcome.Error is null ? "" : $" ({outcome.Error})");
                    _status.ItemOk(name);
                    if (outcome.Deferred is { } deferred)
                    {
                        // The factory is frozen: every other item would be deferred the same way.
                        _status.Deferred(name, deferred);
                        runsFailed = true;
                    }
                    else
                    {
                        _status.NotDeferred();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsFactoryWide(ex))
                {
                    // Every other item would fail the same way: show it once, escalate nothing, try again next poll.
                    logger.LogError(ex, "Run of {Item} failed for a factory-wide reason; ending this poll", name);
                    _status.FactoryFailed($"{name} could not run: {ex.Message}");
                    factoryFailed = runsFailed = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Run of {Item} failed", name);
                    var failures = _status.ItemFailed(name, $"{ex.GetType().Name}: {ex.Message}");
                    if (failures == options.MaxItemFailures)
                    {
                        await GiveUpAsync(lane, id, $"{failures} runs in a row failed before the pipeline could start; last error: {ex.GetType().Name}: {ex.Message}", ct);
                    }
                }
            }
        }
        if (!factoryFailed)
        {
            _status.FactoryOk();
        }
        // A run may have paused the factory for usage (its worker hit the limit) or the pause may have moved.
        try
        {
            return controls is null ? resumeAt : (await controls.UsagePauseAsync(ct))?.ResumeAt;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read the usage pause; polling again next interval");
            return null;
        }
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
public sealed record IntakeOptions(TimeSpan PollInterval, int MaxItemFailures = IntakeOptions.DefaultMaxItemFailures)
{
    public const int DefaultMaxItemFailures = 3;
}

public static class IntakeServiceCollectionExtensions
{
    /// <summary>Registers the intake loop and its Shortcut work source on any host.</summary>
    public static IServiceCollection AddIntake(this IServiceCollection services, FactoryOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new IntakeOptions(options.PollInterval, options.MaxItemFailures));
        // Each run reads the Freeze:* thresholds (FactoryRunner); a bad value fails start-up here rather than every run.
        _ = options.Freeze;
        services.TryAddSingleton(sp => new IntakeStatus(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(new Router.UsageOptions(options.UsagePollInterval));
        services.AddSingleton<Router.IUsageSource>(_ =>
            new Router.RouterClient(new HttpClient { BaseAddress = options.RouterBaseUrl }, options.RouterKey));
        services.AddSingleton(sp => new Router.UsageMonitor(sp.GetRequiredService<Router.IUsageSource>(), sp.GetRequiredService<Controls.IControls>(),
            sp.GetRequiredService<Router.UsageOptions>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<Router.UsageMonitor>>()));
        // Watches usage while a long item run holds up the intake loop, so running workers pause too.
        services.AddHostedService(sp => sp.GetRequiredService<Router.UsageMonitor>());
        services.AddSingleton<IWorkSource>(_ =>
            FactoryRunner.CreateWorkSource(options, new HttpClient { BaseAddress = Shortcut.ShortcutWorkSource.DefaultBaseAddress }));
        services.AddSingleton<IItemRunner>(sp => new FactoryItemRunner(options, sp.GetRequiredService<IWorkSource>(), Console.Out));
        // GitHub issues (sc-25385): a second source, registered by its own type so IWorkSource stays the Shortcut board.
        var issueRepos = options.WatchedIssueRepos;
        if (issueRepos.Count > 0)
        {
            services.AddSingleton(_ => FactoryRunner.CreateIssueSource(options, new HttpClient { BaseAddress = GitHub.GitHubApp.DefaultBaseAddress }));
        }
        // The dashboard's Stop finishes a stop itself when no run holds the item.
        services.AddSingleton<Controls.IItemStops>(sp => new FactoryItemStops(options, sp.GetRequiredService<IWorkSource>(), Console.Out,
            sp.GetService<Issues.GitHubIssueWorkSource>()));
        services.AddHostedService(sp =>
        {
            // One loop polls both sources in turn, so a triage and an item run never hold the single-tenant worker sandbox at once.
            var issues = sp.GetService<Issues.GitHubIssueWorkSource>();
            var status = sp.GetRequiredService<IntakeStatus>();
            IReadOnlyList<IntakeLane> lanes = issues is null
                ? []
                : [new IntakeLane(issues, new FactoryItemRunner(options, issues, Console.Out), ct => FactoryRunner.PollIssuesAsync(options, status, Console.Out, ct))];
            // One evaluator for the loop's life, so its main-red reads are reused within FactoryFreeze.MainRedCacheTtl; built on the
            // first poll, so a missing credential fails that poll (counted as frozen) rather than the host's start.
            Controls.FactoryFreeze? evaluator = null;
            return new IntakeLoop(
                sp.GetRequiredService<IWorkSource>(), sp.GetRequiredService<IItemRunner>(), sp.GetRequiredService<IntakeOptions>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<IntakeLoop>>(), sp.GetRequiredService<Controls.IControls>(),
                sp.GetRequiredService<Router.UsageMonitor>(), status, lanes,
                ct => (evaluator ??= FactoryRunner.CreateFreeze(options)).CheckAsync(ct));
        });
        return services;
    }
}
