using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>Runs one item through the pipeline, and lists items a previous process left mid-run.</summary>
public interface IItemRunner
{
    /// <summary>Items whose ledger state has a handler (still in flight), e.g. after a crash.</summary>
    Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct);

    Task<RunOutcome> RunAsync(int id, CancellationToken ct);
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
/// </summary>
public sealed class IntakeLoop(IWorkSource source, IItemRunner runner, IntakeOptions options, TimeProvider time, ILogger<IntakeLoop> logger,
    Controls.IControls? controls = null, Router.UsageMonitor? usage = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Polling for work every {Interval}", options.PollInterval);
        using var timer = new PeriodicTimer(options.PollInterval, time);
        Task<bool>? tick = null;
        while (true)
        {
            var resumeAt = await PollOnceAsync(stoppingToken);
            tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            if (resumeAt is { } at && at - time.GetUtcNow() is var wait && wait < options.PollInterval)
            {
                // Paused for usage until before the next tick: poll again as soon as it lifts.
                await Task.WhenAny(tick, Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, stoppingToken));
                stoppingToken.ThrowIfCancellationRequested();
            }
            else
            {
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
            return resumeAt;
        }
        foreach (var id in inFlight.Concat(ready).Distinct())
        {
            try
            {
                var outcome = await runner.RunAsync(id, ct);
                logger.LogInformation("{Item}: {State}{Error}", id, outcome.State, outcome.Error is null ? "" : $" ({outcome.Error})");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Run of {Item} failed", id);
            }
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
}

public sealed record IntakeOptions(TimeSpan PollInterval);

public static class IntakeServiceCollectionExtensions
{
    /// <summary>Registers the intake loop and its Shortcut work source on any host.</summary>
    public static IServiceCollection AddIntake(this IServiceCollection services, FactoryOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new IntakeOptions(options.PollInterval));
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
            sp.GetRequiredService<Router.UsageMonitor>()));
        return services;
    }
}
