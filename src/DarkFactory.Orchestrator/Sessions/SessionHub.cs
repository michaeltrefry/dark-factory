using System.Collections.Concurrent;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>
/// Live worker sessions. <c>JoinSession(sessionId)</c> sends the session's stored events, then
/// its new events as they are stored, all as <see cref="EventsMethod"/>(sessionId, events[]) messages.
/// </summary>
public sealed class SessionHub(SessionBroadcaster broadcaster) : Hub
{
    public const string Path = "/hubs/sessions";
    public const string EventsMethod = "SessionEvents";

    public Task JoinSession(string sessionId) => broadcaster.JoinAsync(Context.ConnectionId, sessionId, Context.ConnectionAborted);
}

/// <summary>
/// Pushes stored session events to <see cref="SessionHub"/> viewers. Storing a batch and sending it
/// live, and sending a joiner its backlog, run under the same per-session gate, so a joiner gets
/// every event exactly once and in sequence order.
/// </summary>
public sealed class SessionBroadcaster(IHubContext<SessionHub> hub, IDbContextFactory<LedgerDbContext> contexts) : ISessionEventPublisher
{
    private const int BacklogChunk = 500;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    public static string Group(string claudeSessionId) => $"session:{claudeSessionId}";

    public async Task CommitAndPublishAsync(long workerSessionId, string? claudeSessionId, Func<Task<IReadOnlyList<SessionEventMessage>>> commit)
    {
        var gate = Gate(workerSessionId);
        await gate.WaitAsync();
        try
        {
            var stored = await commit();
            if (claudeSessionId is not null && stored.Count > 0)
            {
                try
                {
                    await hub.Clients.Group(Group(claudeSessionId)).SendAsync(SessionHub.EventsMethod, claudeSessionId, stored);
                }
                catch (Exception)
                {
                    // Viewers are best effort: the events are stored, and a rejoin replays them.
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task JoinAsync(string connectionId, string claudeSessionId, CancellationToken ct)
    {
        long sessionRow;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            sessionRow = await db.WorkerSessions.Where(s => s.ClaudeSessionId == claudeSessionId).Select(s => (long?)s.Id).SingleOrDefaultAsync(ct)
                ?? throw new HubException($"Unknown session {claudeSessionId}.");
        }
        var gate = Gate(sessionRow);
        await gate.WaitAsync(ct);
        try
        {
            await hub.Groups.AddToGroupAsync(connectionId, Group(claudeSessionId), ct);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var backlog = await db.SessionEvents.Where(e => e.WorkerSessionId == sessionRow).OrderBy(e => e.Sequence)
                .Select(e => new SessionEventMessage(e.Sequence, e.Type, e.Subtype, e.Payload, e.ReceivedAt))
                .ToListAsync(ct);
            foreach (var chunk in backlog.Chunk(BacklogChunk))
            {
                await hub.Clients.Client(connectionId).SendAsync(SessionHub.EventsMethod, claudeSessionId, chunk, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(long workerSessionId) => _gates.GetOrAdd(workerSessionId, _ => new SemaphoreSlim(1, 1));
}
