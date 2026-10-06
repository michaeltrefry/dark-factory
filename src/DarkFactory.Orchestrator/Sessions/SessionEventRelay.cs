using Microsoft.Extensions.Hosting;
using Npgsql;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>
/// The <c>factory work</c> host's link from the ledger to live viewers: LISTENs on
/// <see cref="Channel"/>, which the <c>session_events</c> insert trigger notifies with
/// <c>"&lt;worker session row&gt;:&lt;max sequence&gt;"</c> as each batch commits, from any process
/// (an in-host pipeline or a separate <c>factory run</c>). A lost connection is re-opened and the
/// viewers are caught up from the ledger, so notifications missed meanwhile cost latency, not events.
/// </summary>
public sealed class SessionEventRelay(string connectionString, SessionBroadcaster broadcaster, TextWriter log, TimeSpan? reconnectDelay = null)
    : BackgroundService
{
    public const string Channel = "session_events";

    /// <summary>The LISTEN connection's application_name (tests terminate it to force a reconnect).</summary>
    public const string ApplicationName = "dark-factory-relay";

    private readonly TimeSpan _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);
    private NpgsqlConnection? _connection;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Listening before the hub serves anyone, so no commit after a join goes unnoticed.
        _connection = await ListenAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _connection ??= await ListenAsync(stoppingToken);
                while (true)
                {
                    await _connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.WriteLine($"[relay] LISTEN {Channel} lost ({ex.Message}); reconnecting");
                await DropConnectionAsync();
                try
                {
                    await Task.Delay(_reconnectDelay, stoppingToken);
                    _connection = await ListenAsync(stoppingToken);
                    await broadcaster.CatchUpAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception retry)
                {
                    log.WriteLine($"[relay] reconnect failed: {retry.Message}");
                    await DropConnectionAsync();
                }
            }
        }
        await DropConnectionAsync();
    }

    private async Task<NpgsqlConnection> ListenAsync(CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = ApplicationName,
            Pooling = false,
            KeepAlive = 10, // notices a dead server while idle in WaitAsync
        };
        var connection = new NpgsqlConnection(cs.ConnectionString);
        connection.Notification += (_, e) => _ = RelayAsync(e.Payload);
        try
        {
            await connection.OpenAsync(ct);
            await using var listen = new NpgsqlCommand($"LISTEN {Channel}", connection);
            await listen.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Off the notification thread, so one session's slow viewer cannot delay the others' notifications.</summary>
    private async Task RelayAsync(string payload)
    {
        await Task.Yield();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var colon = payload.IndexOf(':');
                await broadcaster.NotifyAsync(long.Parse(payload[..colon]), long.Parse(payload[(colon + 1)..]));
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is not FormatException)
            {
                // e.g. a ledger blip reading the events back: the session may get no further notification.
                await Task.Delay(_reconnectDelay);
            }
            catch (Exception ex)
            {
                log.WriteLine($"[relay] could not relay '{payload}': {ex.Message}");
                return;
            }
        }
    }

    private async Task DropConnectionAsync()
    {
        if (_connection is { } c)
        {
            _connection = null;
            try
            {
                await c.DisposeAsync();
            }
            catch (Exception)
            {
                // already broken
            }
        }
    }
}
