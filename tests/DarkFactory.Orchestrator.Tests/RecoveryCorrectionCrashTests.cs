using System.Diagnostics;
using System.Text.Json;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryCorrectionCrashTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task A_child_crash_on_either_side_of_materialization_commits_one_correction_and_preserves_budgets(string side)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_correction_crash");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new RecoveryWorkSourceTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        await f.Initialize();
        await SetBudgets(f);
        var temp = Directory.CreateTempSubdirectory("df-correction-crash-");
        try
        {
            var log = Path.Combine(temp.FullName, "sessions");
            await File.WriteAllTextAsync(log + ".policy", TestPolicies.Standard());
            await Child(database.ConnectionString, log, "correction-materialize-" + side, f.Seed.Time.GetUtcNow(), 73);
            await using (var crashed = f.Seed.Contexts.CreateDbContext())
            {
                Assert.Equal(side == "before" ? 0 : 1, await crashed.RecoveryCorrections.CountAsync());
                var incident = await crashed.HotFileIncidents.SingleAsync();
                Assert.Equal(side == "before" ? "Unsafe" : "Correcting", incident.State);
                Assert.Equal(2, incident.Attempts);
                Assert.Equal(1, incident.Refreshes);
            }
            for (var n = 0; n < 3; n++)
                await Child(database.ConnectionString, log, "correction-materialize-resume", f.Seed.Time.GetUtcNow(), 0);
            await using var db = f.Seed.Contexts.CreateDbContext();
            var correction = Assert.Single(await db.RecoveryCorrections.ToListAsync());
            Assert.Equal(1, correction.Attempt);
            Assert.Equal(f.Incident.Id, correction.IncidentId);
            Assert.Equal(f.Answer.MaterialHash, correction.MaterialHash);
            Assert.Equal("Pending", correction.State);
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryCorrections.Step).ToListAsync());
            Assert.Equal(new[] { correction.Id }, await f.Source.ListReadyAsync(default));
            Assert.Empty(await db.RecoveryAssessmentAttempts.ToListAsync());
            Assert.Empty(await db.WorkItems.Where(w => w.Source == ItemNaming.Recovery.Source).ToListAsync());
            Assert.Equal(2, (await db.HotFileIncidents.SingleAsync()).Attempts);
            Assert.Equal(1, (await db.HotFileIncidents.SingleAsync()).Refreshes);
        }
        finally { temp.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("observe-before")]
    [InlineData("observe-after")]
    [InlineData("refresh-before")]
    [InlineData("refresh-after")]
    [InlineData("dispatched")]
    [InlineData("ack-before")]
    [InlineData("ack-after")]
    public async Task A_child_crash_during_delivery_observation_or_reassessment_keeps_one_identity_and_one_safe_acknowledgment(string boundary)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_correction_crash");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new RecoveryWorkSourceTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        await f.Initialize();
        var correction = await f.Materialize();
        Assert.True((await f.Source.ClaimAsync(correction.Id, true, default)).Claimed);
        await SetBudgets(f);
        // The real delivery pipeline's already committed facts, before its idempotent source report.
        // No git, worker, external board or network is involved in this child fixture.
        long itemId;
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var item = new WorkItem { Source = ItemNaming.Recovery.Source, ExternalId = $"rc-{correction.Id}",
                Title = "repair", Repo = "owner/alpha", State = WorkState.Watch,
                CreatedAt = f.Seed.Time.GetUtcNow(), UpdatedAt = f.Seed.Time.GetUtcNow(), Version = 1 };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            itemId = item.Id;
            void Row(WorkState state, string? step, string detail) => db.LedgerEntries.Add(new()
            { WorkItemId = item.Id, State = state, Step = step, Detail = detail,
                RecordedAt = f.Seed.Time.GetUtcNow(), Outcome = StepOutcome.Passed });
            Row(WorkState.Review, RunPipeline.Steps.Linked, "https://github.com/owner/alpha/pull/1");
            Row(WorkState.MergeGate, RunPipeline.Steps.GatePassed, new string('8', 40));
            Row(WorkState.MergeGate, RunPipeline.Steps.MergeFiles, new MergeFiles("main", ["src/a.cs", "tests/CountTests.cs"]).ToDetail());
            await db.SaveChangesAsync();
            Row(WorkState.Merge, null, new string('9', 40));
            await db.SaveChangesAsync();
            Row(WorkState.Watch, "recovery-delivered", "delivered correction");
            await db.SaveChangesAsync();
        }
        var temp = Directory.CreateTempSubdirectory("df-correction-crash-");
        try
        {
            var log = Path.Combine(temp.FullName, "sessions");
            await File.WriteAllTextAsync(log + ".answer", JsonSerializer.Serialize(f.Answer));
            await Child(database.ConnectionString, log, "correction-" + boundary, f.Seed.Time.GetUtcNow(), 73);
            var uncertain = boundary is "dispatched" or "ack-before";
            await using (var crashed = f.Seed.Contexts.CreateDbContext())
            {
                var incident = await crashed.HotFileIncidents.SingleAsync();
                var observed = boundary is not ("observe-before" or "observe-after" or "refresh-before");
                Assert.Equal(observed ? 2 : 1, incident.Refreshes);
                Assert.Equal(boundary == "refresh-after" ? 0 : observed ? 1 : 2, incident.Attempts);
                Assert.Equal(boundary == "observe-before" ? "Claimed" : "Merged", (await crashed.RecoveryCorrections.SingleAsync()).State);
                Assert.Equal(observed ? 1 : 0, await crashed.LedgerEntries.CountAsync(e => e.Step == "recovery-reassessment"));
                Assert.Equal(boundary == "ack-after", incident.ResolvedAt is not null);
                Assert.Equal(uncertain ? 1 : 0, await crashed.RecoveryAssessmentAttempts.CountAsync(a => a.State == "Reserved"));
            }
            // An unexpired dispatched call must not be stolen by a fresh process.
            await Child(database.ConnectionString, log, "correction-resume", f.Seed.Time.GetUtcNow(), 0);
            if (uncertain)
            {
                Assert.Single(File.ReadAllLines(log));
                f.Seed.Time.Advance(HotFileRecovery.StageTimeout + TimeSpan.FromSeconds(1));
                await Child(database.ConnectionString, log, "correction-resume", f.Seed.Time.GetUtcNow(), 0);
            }
            for (var n = 0; n < 3; n++)
                await Child(database.ConnectionString, log, "correction-resume", f.Seed.Time.GetUtcNow(), 0);
            await using var db = f.Seed.Contexts.CreateDbContext();
            var resolved = await db.HotFileIncidents.SingleAsync();
            Assert.Equal("Resolved", resolved.State);
            Assert.NotNull(resolved.ResolvedAt);
            Assert.Equal(2, resolved.Refreshes); // delivery consumes exactly one of the lifetime refreshes
            Assert.Equal(uncertain ? 2 : 1, resolved.Attempts); // crash spends its reserved assessment
            Assert.Equal(f.Incident.Id, resolved.Id);
            Assert.Equal(f.Incident.Generation, resolved.Generation);
            Assert.Equal(correction.Id, Assert.Single(await db.RecoveryCorrections.ToListAsync()).Id);
            Assert.Equal(1, (await db.RecoveryCorrections.SingleAsync()).Attempt);
            Assert.Equal(itemId, Assert.Single(await db.WorkItems.Where(w => w.Source == ItemNaming.Recovery.Source).ToListAsync()).Id);
            var rows = await db.LedgerEntries.Where(e => e.WorkItemId == itemId).ToListAsync();
            Assert.Equal("https://github.com/owner/alpha/pull/1", Assert.Single(rows, e => e.Step == RunPipeline.Steps.Linked).Detail);
            Assert.Single(rows, e => e.Step is null && e.State == WorkState.Merge);
            Assert.Single(rows, e => e.Step == "recovery-delivered");
            Assert.Single(rows, e => e.Step == "recovery-reassessment");
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryCorrections.StateStep && e.Detail == "Merged").ToListAsync());
            var accepted = Assert.Single(await db.RecoveryAssessmentAttempts.Where(a => a.State == "Accepted").ToListAsync());
            Assert.Equal("safe", HotFileAssessment.FromDetail(accepted.Result)!.Decision);
            Assert.Equal(uncertain ? 1 : 0, await db.RecoveryAssessmentAttempts.CountAsync(a => a.State == "Uncertain"));
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step).ToListAsync());
            var sessions = File.ReadAllLines(log);
            Assert.Equal(uncertain ? 2 : 1, sessions.Length);
            Assert.Equal(sessions.Length, sessions.Distinct().Count());
            await using var restarted = f.Seed.Recovery;
            Assert.Null(await restarted.HeldAsync("owner/alpha", default)); // acknowledgment prevents a duplicate incident
        }
        finally { temp.Delete(recursive: true); }
    }

    private static async Task SetBudgets(RecoveryWorkSourceTests.Fixture f)
    {
        await using var db = f.Seed.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        incident.Attempts = 2; incident.Refreshes = 1; incident.Version++;
        await db.SaveChangesAsync();
    }

    private static async Task Child(string connection, string log, string mode, DateTimeOffset now, int exit)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DarkFactory.CrashHost.dll"), "recovery", connection, log, mode, now.ToString("O") })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var own = OwnProcess.Of(child);
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.ExitCode == exit, await stdout + await stderr);
        }
        finally { own.KillIfStillRunning(); }
    }
}
