using DarkFactory.Orchestrator.Controls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarkFactory.Orchestrator.Router;

/// <summary>
/// <c>factory work</c>: polls the router's subscription usage and, when every plan the factory can use is exhausted,
/// pauses the factory (<see cref="ControlScope.Usage"/>) until the earliest reset, or for a growing backoff when the
/// router knows no reset time. The pause stops dispatch and, through the controls every pipeline watches, stops
/// running workers at their next tool boundary; it lifts by itself at its resume time. Usage that is unknown (router
/// unreachable, or no credential known yet) never pauses: the worker backstop (<see cref="RunPipeline"/>) covers it.
/// </summary>
public sealed class UsageMonitor(IUsageSource usage, IControls controls, UsageOptions options, TimeProvider time, ILogger<UsageMonitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, time);
        do
        {
            await CheckAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One poll; never throws but for cancellation.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var report = await usage.GetUsageAsync(ct);
            if (report.KnownCredentials == 0 || !report.AllExhausted)
            {
                return;
            }
            var now = time.GetUtcNow();
            // A reset already past is a stale reading (nothing has been routed since): no pause, the backstop covers it.
            if (report.ResumesAt is { } at && at <= now)
            {
                return;
            }
            var pause = await controls.PauseForUsageAsync(report.ResumesAt, UsagePause.UsageExhausted, ct);
            if (pause.ChangedAt == now)
            {
                logger.LogWarning("Every plan is exhausted; the factory is paused ({Reason}) until {ResumeAt:u}", pause.Reason, pause.ResumeAt);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not read the router's subscription usage ({Error}); not pausing on it", ex.Message);
        }
    }
}

public sealed record UsageOptions(TimeSpan PollInterval);
