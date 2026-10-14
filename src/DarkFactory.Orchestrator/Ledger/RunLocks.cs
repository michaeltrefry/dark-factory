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

    /// <summary>
    /// The work items whose run lock some session holds now — a run of any process on this ledger (sc-25991: the dashboard's
    /// "is this item's run alive", which the rows a dead run left cannot say). Read from <c>pg_locks</c>, so nothing is taken:
    /// asking never makes a run find its item locked. A single-key advisory lock shows there as <c>objsubid</c> 1 with the key's
    /// halves in <c>classid</c> and <c>objid</c>; only this database's, and only item ids (the merge queue's per-repository
    /// keys are negative, the migration's lock is a two-key one). A clean-up that holds an item's lock for a moment to delete
    /// its leftovers (<see cref="LiveRuns"/>) shows as a run for that moment.
    /// </summary>
    public async Task<IReadOnlySet<long>> HeldAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT ((l.classid::bigint << 32) | l.objid::bigint)
            FROM pg_locks l JOIN pg_database d ON d.oid = l.database
            WHERE l.locktype = 'advisory' AND l.objsubid = 1 AND l.granted AND d.datname = current_database()
            """, connection);
        var held = new HashSet<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetInt64(0) is > 0 and var id)
            {
                held.Add(id);
            }
        }
        return held;
    }

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
