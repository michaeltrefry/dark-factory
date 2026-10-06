using System.Collections.Concurrent;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>
/// Live worker sessions. <c>JoinSession(sessionId)</c> sends the session's stored events, then
/// its new events as they are stored, all as <see cref="EventsMethod"/>(sessionId, events[]) messages.
/// Joining the same session again on one connection does nothing. Requires the dashboard login.
/// </summary>
[Authorize]
public sealed class SessionHub(SessionBroadcaster broadcaster) : Hub
{
    public const string Path = "/hubs/sessions";
    public const string EventsMethod = "SessionEvents";

    public Task JoinSession(string sessionId) =>
        broadcaster.JoinAsync(Context.ConnectionId, sessionId, Context.Abort, Context.ConnectionAborted);

    public override Task OnDisconnectedAsync(Exception? exception) => broadcaster.LeaveAsync(Context.ConnectionId);
}

/// <summary>Limits that keep one slow viewer from holding up a session's other viewers.</summary>
public sealed record SessionHubOptions
{
    /// <summary>Events read from the ledger (and sent) per message.</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>A viewer that cannot take a live message within this is disconnected (it can rejoin).</summary>
    public TimeSpan LiveSendTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A joiner that cannot take a backlog page within this is disconnected.</summary>
    public TimeSpan BacklogSendTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Pushes stored session events to <see cref="SessionHub"/> viewers. It never sits in the
/// writer's path: <see cref="SessionEventRelay"/> calls <see cref="NotifyAsync"/> once events are
/// committed, and the broadcaster reads them back from the ledger. Per session, sending new
/// events live and sending a joiner its backlog run under one gate, against one cursor
/// (<c>LastSent</c>), so each viewer gets every event exactly once and in sequence order.
/// A session's state exists only while it has viewers: hub connections, or dashboard circuits
/// joining through <see cref="ISessionViewers"/>.
/// </summary>
public sealed class SessionBroadcaster(
    IHubContext<SessionHub> hub,
    IDbContextFactory<LedgerDbContext> contexts,
    SessionHubOptions options,
    TextWriter log) : ISessionViewers
{
    private readonly ConcurrentDictionary<long, Feed> _feeds = new();

    /// <summary>Sessions that currently have viewers.</summary>
    internal int FeedCount => _feeds.Count;

    /// <summary>A hub connection joins: pages go to it as <see cref="SessionHub.EventsMethod"/> messages.</summary>
    public Task JoinAsync(string connectionId, string claudeSessionId, Action abort, CancellationToken ct) =>
        JoinAsync(connectionId, claudeSessionId,
            (page, sendCt) => hub.Clients.Client(connectionId).SendAsync(SessionHub.EventsMethod, claudeSessionId, page, sendCt),
            abort, ct);

    public async Task JoinAsync(string viewerId, string claudeSessionId, SessionEventSink send, Action abort, CancellationToken ct)
    {
        long sessionRow;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            sessionRow = await db.WorkerSessions.Where(s => s.ClaudeSessionId == claudeSessionId).Select(s => (long?)s.Id).SingleOrDefaultAsync(ct)
                ?? throw new HubException($"Unknown session {claudeSessionId}.");
        }
        var feed = await EnterAsync(sessionRow, claudeSessionId, ct);
        try
        {
            if (feed.Viewers.ContainsKey(viewerId))
            {
                return;
            }
            if (feed.Viewers.Count == 0)
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                feed.LastSent = await db.SessionEvents.Where(e => e.WorkerSessionId == sessionRow).MaxAsync(e => (long?)e.Sequence, ct) ?? 0;
            }
            else
            {
                // Bring the current viewers up to date, so everything up to LastSent is the joiner's backlog.
                await PumpAsync(feed);
            }
            var cursor = 0L;
            while (cursor < feed.LastSent)
            {
                var page = await PageAsync(sessionRow, cursor, feed.LastSent, ct);
                if (page.Count == 0)
                {
                    break;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.BacklogSendTimeout);
                try
                {
                    await send(page, timeout.Token).WaitAsync(options.BacklogSendTimeout, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or TimeoutException)
                {
                    abort();
                    throw new HubException($"Backlog of session {claudeSessionId} not taken within {options.BacklogSendTimeout}.");
                }
                cursor = page[^1].Sequence;
            }
            feed.Viewers[viewerId] = new Viewer(send, abort);
        }
        finally
        {
            Exit(feed);
        }
    }

    public async Task LeaveAsync(string viewerId)
    {
        foreach (var feed in _feeds.Values)
        {
            await feed.Gate.WaitAsync();
            try
            {
                feed.Viewers.Remove(viewerId);
            }
            finally
            {
                Exit(feed);
            }
        }
    }

    /// <summary>
    /// Events up to <paramref name="maxSequence"/> of session row <paramref name="workerSessionId"/>
    /// are committed: sends its viewers whatever they have not had yet.
    /// </summary>
    public async Task NotifyAsync(long workerSessionId, long maxSequence)
    {
        if (!_feeds.TryGetValue(workerSessionId, out var feed))
        {
            return; // nobody is watching; a joiner reads the backlog from the ledger
        }
        await feed.Gate.WaitAsync();
        try
        {
            if (!feed.Closed && maxSequence > feed.LastSent)
            {
                await PumpAsync(feed);
            }
        }
        finally
        {
            Exit(feed);
        }
    }

    /// <summary>After missed notifications (e.g. the relay reconnected): catch every viewer up from the ledger.</summary>
    public async Task CatchUpAsync()
    {
        foreach (var row in _feeds.Keys)
        {
            await NotifyAsync(row, long.MaxValue);
        }
    }

    /// <summary>Under the feed's gate: sends every viewer the events after LastSent and advances it.</summary>
    private async Task PumpAsync(Feed feed)
    {
        while (true)
        {
            var page = await PageAsync(feed.SessionRow, feed.LastSent, long.MaxValue, CancellationToken.None);
            if (page.Count == 0)
            {
                return;
            }
            await Task.WhenAll(feed.Viewers.ToList().Select(v => SendLiveAsync(feed, v.Key, v.Value, page)));
            feed.LastSent = page[^1].Sequence;
            if (page.Count < options.PageSize)
            {
                return;
            }
        }
    }

    private async Task SendLiveAsync(Feed feed, string connectionId, Viewer viewer, IReadOnlyList<SessionEventMessage> page)
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.LiveSendTimeout);
            await viewer.Send(page, timeout.Token).WaitAsync(options.LiveSendTimeout);
        }
        catch (Exception ex)
        {
            // It missed events: drop it rather than let it fall silently behind; a rejoin replays them.
            log.WriteLine($"[hub] dropping viewer {connectionId} of session {feed.ClaudeSessionId}: {ex.GetType().Name}");
            lock (feed.Viewers)
            {
                feed.Viewers.Remove(connectionId);
            }
            viewer.Abort();
        }
    }

    private async Task<List<SessionEventMessage>> PageAsync(long sessionRow, long after, long upTo, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SessionEvents
            .Where(e => e.WorkerSessionId == sessionRow && e.Sequence > after && e.Sequence <= upTo)
            .OrderBy(e => e.Sequence)
            .Take(options.PageSize)
            .Select(e => new SessionEventMessage(e.Sequence, e.Type, e.Subtype, e.Payload, e.ReceivedAt))
            .ToListAsync(ct);
    }

    /// <summary>Gets the session's feed (created on first use) with its gate held.</summary>
    private async Task<Feed> EnterAsync(long sessionRow, string claudeSessionId, CancellationToken ct)
    {
        while (true)
        {
            var feed = _feeds.GetOrAdd(sessionRow, row => new Feed(row, claudeSessionId));
            await feed.Gate.WaitAsync(ct);
            if (!feed.Closed)
            {
                return feed;
            }
            feed.Gate.Release(); // removed meanwhile: take the new one
        }
    }

    /// <summary>Releases the feed's gate, removing the feed once it has no viewers.</summary>
    private void Exit(Feed feed)
    {
        if (feed.Viewers.Count == 0 && !feed.Closed)
        {
            feed.Closed = true;
            _feeds.TryRemove(new KeyValuePair<long, Feed>(feed.SessionRow, feed));
        }
        feed.Gate.Release();
    }

    private sealed record Viewer(SessionEventSink Send, Action Abort);

    private sealed class Feed(long sessionRow, string claudeSessionId)
    {
        public long SessionRow { get; } = sessionRow;
        public string ClaudeSessionId { get; } = claudeSessionId;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Every viewer has had the events up to this sequence. Guarded by <see cref="Gate"/>.</summary>
        public long LastSent { get; set; }

        /// <summary>Viewer id → its sink. Guarded by <see cref="Gate"/>.</summary>
        public Dictionary<string, Viewer> Viewers { get; } = [];

        public bool Closed { get; set; }
    }
}

/// <summary>Takes one page of a session's events, in sequence order; must finish within the hub's send timeouts.</summary>
public delegate Task SessionEventSink(IReadOnlyList<SessionEventMessage> page, CancellationToken ct);

/// <summary>
/// Watching a session: <see cref="JoinAsync"/> sends the stored events, then each new one as it is
/// stored, exactly once, to <c>send</c>; a viewer that falls behind is dropped (<c>abort</c>).
/// Throws <see cref="HubException"/> for an unknown session.
/// </summary>
public interface ISessionViewers
{
    Task JoinAsync(string viewerId, string claudeSessionId, SessionEventSink send, Action abort, CancellationToken ct);

    Task LeaveAsync(string viewerId);
}
