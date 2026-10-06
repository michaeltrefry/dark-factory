using Npgsql;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>Per-work-item mutual exclusion: at most one <c>factory run</c> drives an item at a time.</summary>
public interface IRunLocks
{
    /// <summary>Returns the held lock (dispose to release), or null when another run holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long workItemId, CancellationToken ct);
}

/// <summary>
/// A session-level Postgres advisory lock keyed by the work item id, held on a dedicated
/// unpooled connection for the whole run. A crashed run's connection dies with it, so the
/// lock never outlives its holder.
/// </summary>
public sealed class PostgresRunLocks(string connectionString) : IRunLocks
{
    private readonly string _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public async Task<IAsyncDisposable?> TryAcquireAsync(long workItemId, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", connection);
            command.Parameters.AddWithValue("id", workItemId);
            if (await command.ExecuteScalarAsync(ct) is true)
            {
                return new Held(connection, workItemId);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    private sealed class Held(NpgsqlConnection connection, long workItemId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", connection);
                command.Parameters.AddWithValue("id", workItemId);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                // Closing the unpooled connection ends the session, which releases the lock regardless.
                await connection.DisposeAsync();
            }
        }
    }
}
