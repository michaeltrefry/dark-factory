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
/// While the factory is paused (<see cref="Controls.ControlScope.Factory"/>, or the usage pause
/// <see cref="Controls.ControlScope.Usage"/>) it lists no new ready items; the runner leaves out in-flight items a
/// control pauses, and the pipeline itself refuses to claim a story in a paused epic. Each poll first reads the
/// router's usage (<paramref name="usage"/>), and a usage pause wakes the loop at its resume time, so work starts
/// again then without waiting for the next interval.
/// A failing poll or run is shown on the dashboard (<paramref name="status"/>, E10). A factory-wide failure
/// (<see cref="FactoryUnavailableException"/>, a ledger database error, or the poll itself) ends the poll and escalates
/// nothing; any other failure counts against its item, and after <see cref="IntakeOptions.MaxItemFailures"/> in a row
/// the loop gives up on the item (<see cref="IItemRunner.GiveUpAsync"/>) instead of retrying it every poll.
/// </summary>
public sealed class IntakeLoop(IWorkSource source, IItemRunner runner, IntakeOptions options, TimeProvider time, ILogger<IntakeLoop> logger,
    Controls.IControls? controls = null, Router.UsageMonitor? usage = null, IntakeStatus? status = null)
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
        IReadOnlyList<int> inFlight, ready;
        DateTimeOffset? resumeAt = null;
        try
        {
            if (usage is not null)
            {
                await usage.CheckAsync(ct);
            }
            inFlight = await runner.InFlightAsync(ct);
            var usagePause = controls is null ? null : await controls.UsagePauseAsync(ct);
            resumeAt = usagePause?.ResumeAt;
            var factoryPaused = usagePause is not null || (controls is not null
                && (await controls.GetAsync(Controls.ControlScope.Factory, ct))?.State == Controls.ControlState.Paused);
            ready = factoryPaused ? [] : await source.ListReadyAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Poll failed; retrying next interval");
            _status.FactoryFailed($"Poll failed: {ex.Message}");
            return resumeAt;
        }
        var factoryFailed = false;
        foreach (var id in inFlight.Concat(ready).Distinct())
        {
            try
            {
                var outcome = await runner.RunAsync(id, ct);
                logger.LogInformation("{Item}: {State}{Error}", id, outcome.State, outcome.Error is null ? "" : $" ({outcome.Error})");
                _status.ItemOk(id);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsFactoryWide(ex))
            {
                // Every other item would fail the same way: show it once, escalate nothing, try again next poll.
                logger.LogError(ex, "Run of {Item} failed for a factory-wide reason; ending this poll", id);
                _status.FactoryFailed($"{StoryId.Format(id)} could not run: {ex.Message}");
                factoryFailed = true;
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Run of {Item} failed", id);
                var failures = _status.ItemFailed(id, $"{ex.GetType().Name}: {ex.Message}");
                if (failures == options.MaxItemFailures)
                {
                    await GiveUpAsync(id, $"{failures} runs in a row failed before the pipeline could start; last error: {ex.GetType().Name}: {ex.Message}", ct);
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

    private async Task GiveUpAsync(int id, string reason, CancellationToken ct)
    {
        try
        {
            if (await runner.GiveUpAsync(id, reason, ct) is { } what)
            {
                logger.LogWarning("{Item}: gave up after repeated failures ({What})", id, what);
                _status.ItemGaveUp(id, what);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Shown on the dashboard still; the next streak of failures tries again.
            logger.LogError(ex, "Could not give up on {Item}", id);
        }
    }
}

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
        // The dashboard's Stop finishes a stop itself when no run holds the item.
        services.AddSingleton<Controls.IItemStops>(sp => new FactoryItemStops(options, sp.GetRequiredService<IWorkSource>(), Console.Out));
        services.AddHostedService(sp => new IntakeLoop(
            sp.GetRequiredService<IWorkSource>(), sp.GetRequiredService<IItemRunner>(), sp.GetRequiredService<IntakeOptions>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<IntakeLoop>>(), sp.GetRequiredService<Controls.IControls>(),
            sp.GetRequiredService<Router.UsageMonitor>(), sp.GetRequiredService<IntakeStatus>()));
        return services;
    }
}
