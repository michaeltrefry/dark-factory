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
/// While the factory is paused (<see cref="Controls.ControlScope.Factory"/>) it lists no new ready items; the
/// runner leaves out in-flight items a control pauses, and the pipeline itself refuses to claim a story in a
/// paused epic.
/// </summary>
public sealed class IntakeLoop(IWorkSource source, IItemRunner runner, IntakeOptions options, TimeProvider time, ILogger<IntakeLoop> logger,
    Controls.IControls? controls = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Polling for work every {Interval}", options.PollInterval);
        using var timer = new PeriodicTimer(options.PollInterval, time);
        do
        {
            await PollOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One poll: resume in-flight items first, then run newly ready ones.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<int> inFlight, ready;
        try
        {
            inFlight = await runner.InFlightAsync(ct);
            var factoryPaused = controls is not null
                && (await controls.GetAsync(Controls.ControlScope.Factory, ct))?.State == Controls.ControlState.Paused;
            ready = factoryPaused ? [] : await source.ListReadyAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Poll failed; retrying next interval");
            return;
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
        services.AddSingleton<IWorkSource>(_ =>
            FactoryRunner.CreateWorkSource(options, new HttpClient { BaseAddress = Shortcut.ShortcutWorkSource.DefaultBaseAddress }));
        services.AddSingleton<IItemRunner>(sp => new FactoryItemRunner(options, sp.GetRequiredService<IWorkSource>(), Console.Out));
        // The dashboard's Stop finishes a stop itself when no run holds the item.
        services.AddSingleton<Controls.IItemStops>(sp => new FactoryItemStops(options, sp.GetRequiredService<IWorkSource>(), Console.Out));
        services.AddHostedService(sp => new IntakeLoop(
            sp.GetRequiredService<IWorkSource>(), sp.GetRequiredService<IItemRunner>(), sp.GetRequiredService<IntakeOptions>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<IntakeLoop>>(), sp.GetRequiredService<Controls.IControls>()));
        return services;
    }
}
