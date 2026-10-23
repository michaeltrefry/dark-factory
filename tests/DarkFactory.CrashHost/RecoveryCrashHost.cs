using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        if (mode.StartsWith("correction-", StringComparison.Ordinal))
            return await CorrectionAsync(connection, log, mode, clock);
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
    private static async Task<int> CorrectionAsync(string connection, string log, string mode, TimeProvider clock)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>(LedgerDbContext.PostgresOptions(connection))
            .AddInterceptors(new CrashBoundary(mode)).Options;
        var contexts = new LedgerDbContextFactory(options);
        if (mode.StartsWith("correction-materialize-", StringComparison.Ordinal))
        {
            await using var db = contexts.CreateDbContext();
            var incident = await db.HotFileIncidents.SingleAsync();
            var answer = HotFileAssessment.FromDetail(incident.Answer)!;
            await RecoveryCorrections.MaterializeAsync(db, incident, answer,
                GatePolicy.Parse(await File.ReadAllTextAsync(log + ".policy")), clock.GetUtcNow(), default);
            await db.SaveChangesAsync();
            return 0;
        }
        // Simulate the ordinary pipeline having durably recorded its merge, then replay its source report.
        var source = new RecoveryWorkSource(contexts, clock);
        await using (var db = contexts.CreateDbContext())
            await source.ReportStateAsync((await db.RecoveryCorrections.SingleAsync()).Id, BoardState.Merged, null, default);
        if (mode.StartsWith("correction-observe-", StringComparison.Ordinal)) return 0;
        var template = HotFileAssessment.FromDetail(await File.ReadAllTextAsync(log + ".answer"))!;
        string Material(HotFileSnapshot snapshot) => JsonSerializer.Serialize(RecoverySnapshot.FromDetail(template.Material)! with
        {
            Id = "corrected-" + snapshot.Id, IncidentSnapshot = snapshot.Id,
            BaseTips = new Dictionary<string, string> { ["main"] = new string('9', 40) },
            Evidence = RecoverySnapshot.FromDetail(template.Material)!.Evidence.Concat(
                [new RecoveryEvidence("repair:1", "recovery", "ledger", new string('9', 40), "hash", "delivered correction")]).ToArray()
        });
        await using var recovery = new HotFileRecovery(contexts, new PostgresRunLocks(connection), new FreezeOptions(), clock,
            new CorrectionAssessor(log, mode, template, Material), capture: (snapshot, _) => Task.FromResult(Material(snapshot)));
        await recovery.AdvanceAsync("owner/alpha", default);
        return 0;
    }

    // Test-only EF seam. SavedChanges is after the provider transaction commits; SavingChanges is before it starts.
    // Environment.Exit never runs in the test host and cannot signal any owner or worker process.
    private sealed class CrashBoundary(string mode) : SaveChangesInterceptor
    {
        private bool selected;
        private bool Matches(DbContext? db) => db is not null && (
            mode.StartsWith("correction-materialize-", StringComparison.Ordinal)
                && db.ChangeTracker.Entries<RecoveryCorrection>().Any(e => e.State == EntityState.Added)
            || mode.StartsWith("correction-observe-", StringComparison.Ordinal)
                && db.ChangeTracker.Entries<RecoveryCorrection>().Any(e => e.State == EntityState.Modified && e.Entity.State == "Merged")
            || mode.StartsWith("correction-refresh-", StringComparison.Ordinal)
                && db.ChangeTracker.Entries<LedgerEntry>().Any(e => e.State == EntityState.Added && e.Entity.Step == "recovery-reassessment")
            || mode.StartsWith("correction-ack-", StringComparison.Ordinal)
                && db.ChangeTracker.Entries<HotFileIncident>().Any(e => e.State == EntityState.Modified && e.Entity.State == "Resolved"));
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            selected = Matches(eventData.Context);
            if (selected && mode.EndsWith("-before", StringComparison.Ordinal)) Environment.Exit(73);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (selected && mode.EndsWith("-after", StringComparison.Ordinal)) Environment.Exit(73);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CorrectionAssessor(string log, string mode, HotFileAssessment template,
        Func<HotFileSnapshot, string> material) : IHotFileAssessor
    {
        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            await File.AppendAllTextAsync(log, session + "\n", ct);
            if (mode == "correction-dispatched") Environment.Exit(73);
            var captured = material(snapshot);
            return template with { Snapshot = snapshot.Id, Generation = snapshot.Generation, EvidenceHash = snapshot.EvidenceHash,
                Session = session, Decision = "safe", Material = captured, MaterialSnapshot = "corrected-" + snapshot.Id,
                MaterialHash = RecoverySnapshot.Hash(captured), Findings = [], CorrectionFindings = [], PendingDispositions = [] };
        }
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
