using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;

/// <summary>Crash only this child process at a durable recovery boundary. No worker user, signals or real service.</summary>
internal static class RecoveryCrashHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var (connection, log, mode) = (args[0], args[1], args[2]);
        var clock = new Clock(DateTimeOffset.Parse(args[3]));
        await LedgerMigrations.MigrateAsync(connection, default);
        await using var recovery = new HotFileRecovery(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(connection)),
            new PostgresRunLocks(connection), new FreezeOptions(), clock, new Assessor(log, mode));
        if (mode == "detected")
        {
            await recovery.HeldAsync("owner/alpha", default);
            Environment.Exit(73);
        }
        await recovery.AdvanceAsync("owner/alpha", default);
        if (mode == "accepted") Environment.Exit(73);
        return 0;
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Assessor(string log, string mode) : IHotFileAssessor
    {
        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            await File.AppendAllTextAsync(log, session + "\n", ct);
            if (mode == "dispatched") Environment.Exit(73); // reservation saved; no finally/acceptance runs
            return new(snapshot.Id, snapshot.Generation, snapshot.Repo, "high", "safe", snapshot.EvidenceHash);
        }
    }
}
