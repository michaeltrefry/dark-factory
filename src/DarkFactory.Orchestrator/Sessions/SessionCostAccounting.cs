using DarkFactory.Orchestrator.Router;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>Shared retry-and-settle policy. A measured zero is recorded; absent telemetry remains unknown.</summary>
internal static class SessionCostAccounting
{
    internal static async Task CollectAsync(ISessionCostSource costs, string session, IReadOnlyList<TimeSpan> retries,
        TimeSpan settle, Func<TimeSpan, CancellationToken, Task> delay, TextWriter log,
        Func<SessionCost, Task> recorded, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await ReadAsync() is { } first)
            {
                // Store before waiting, preserving the first measurement if settling is interrupted.
                await recorded(first);
                await delay(settle, ct);
                if (await ReadAsync() is { } latest) await recorded(latest);
                return;
            }
            if (attempt >= retries.Count)
            {
                log.WriteLine($"[session] router has recorded no request for session {session}; cost left unset");
                return;
            }
            await delay(retries[attempt], ct);
        }

        async Task<SessionCost?> ReadAsync()
        {
            try { return await costs.GetSessionCostAsync(session, ct) is { RequestCount: > 0 } cost ? cost : null; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.WriteLine($"[session] router cost for session {session}: {ex.Message}");
                return null;
            }
        }
    }
}
