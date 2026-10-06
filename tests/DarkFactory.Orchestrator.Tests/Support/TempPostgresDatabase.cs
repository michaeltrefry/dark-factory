using Npgsql;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A throwaway database on the compose ledger Postgres (localhost:5434). Skips the test when
/// Postgres is unreachable locally; under <c>CI</c> an unreachable Postgres fails instead.
/// </summary>
public sealed class TempPostgresDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnectionStringBuilder _admin;
    private readonly string _name;

    private TempPostgresDatabase(NpgsqlConnectionStringBuilder admin, string name)
    {
        _admin = admin;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TempPostgresDatabase> CreateAsync(string prefix)
    {
        var admin = new NpgsqlConnectionStringBuilder(FactoryOptions.LoadConfiguration().GetLedgerConnectionString());
        var name = $"{prefix}_{Guid.NewGuid():N}";
        try
        {
            await using var conn = new NpgsqlConnection(admin.ConnectionString);
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await create.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.Skip($"Ledger Postgres not reachable ({ex.Message}); run `docker compose up -d`.");
        }
        return new TempPostgresDatabase(admin, name);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin.ConnectionString);
        await conn.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();
    }
}
