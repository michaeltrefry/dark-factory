using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>
/// Brings the ledger schema up to date. Every process that uses the ledger (<c>factory run</c>,
/// <c>factory work</c>) migrates first; EF Core does not serialise concurrent <c>MigrateAsync</c>
/// calls on Postgres (two racing migrators both apply the same migration and one fails), so this
/// holds a session advisory lock while migrating. Nothing reads the ledger before this returns.
/// </summary>
public static class LedgerMigrations
{
    // Two-int advisory key: a different key space from the run locks' single bigint item ids.
    private const int LockClass = 0x4446_4C47; // "DFLG"
    private const int LockObject = 1;

    public static async Task MigrateAsync(string connectionString, CancellationToken ct)
    {
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(ct);
        await using (var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({LockClass}, {LockObject})", lockConnection))
        {
            await acquire.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(connectionString));
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockClass}, {LockObject})", lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
