using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>The human-only re-arm of an escalated incident (`factory recovery reassess --repo`, the dashboard's Reassess).</summary>
public class RecoveryReassessTests
{
    private const string A = "owner/alpha", B = "owner/beta";

    private static async Task<HotFileRecoveryTests.Fixture> EscalatedAsync()
    {
        var f = new HotFileRecoveryTests.Fixture();
        for (var i = 0; i < 8; i++) await f.Merge(A);
        for (var i = 0; i < 8; i++) await f.Merge(B);
        // Every answer is unusable: three semantic attempts spend the generation's budget and escalate.
        f.Assessor.Answer = new HotFileAssessment("not-this-snapshot", 0, A, "high", "inconclusive", "x");
        await using var recovery = f.Recovery;
        for (var i = 0; i < HotFileRecovery.AssessmentLimit; i++)
        {
            await recovery.AdvanceAsync(A, default);
            f.Time.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.NotNull(await recovery.HeldAsync(B, default)); // another repository's own incident
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync(i => i.Repo == A)).State);
        return f;
    }

    /// <summary>Two generations of one repository: the first resolved, then a later merge opens and resolves the second.</summary>
    private static async Task<(HotFileRecoveryTests.Fixture F, long Older, long Newer)> TwoGenerationsAsync()
    {
        var f = new HotFileRecoveryTests.Fixture();
        for (var i = 0; i < 8; i++) await f.Merge(A);
        await using (var recovery = f.Recovery) await recovery.AdvanceAsync(A, default);
        await f.Merge(A);
        await using (var recovery = f.Recovery) await recovery.AdvanceAsync(A, default);
        await using var db = f.Contexts.CreateDbContext();
        var incidents = await db.HotFileIncidents.OrderBy(i => i.Generation).ToListAsync();
        Assert.Equal(new[] { ("Resolved", 1), ("Resolved", 2) }, incidents.Select(i => (i.State, i.Generation)));
        return (f, incidents[0].Id, incidents[1].Id);
    }

    private static async Task SetAsync(HotFileRecoveryTests.Fixture f, long id, string state)
    {
        await using var db = f.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync(i => i.Id == id);
        incident.State = state; incident.Attempts = 0; incident.NextAttemptAt = null; incident.Version++;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_older_unresolved_generation_behind_a_newer_resolved_one_is_advanced_not_left_holding()
    {
        var (f, older, newer) = await TwoGenerationsAsync();
        await SetAsync(f, older, "Held");
        var calls = f.Assessor.Calls;
        await using (var recovery = f.Recovery)
        {
            Assert.NotNull(await recovery.HeldAsync(A, default));
            // Its evidence moved on (the later merge), so it is refreshed and then assessed afresh — driven, not stranded.
            for (var i = 0; i < 3 && await recovery.HeldAsync(A, default) is not null; i++)
            {
                await recovery.AdvanceAsync(A, default);
                f.Time.Advance(TimeSpan.FromMinutes(5));
            }
            Assert.Null(await recovery.HeldAsync(A, default));
        }
        Assert.True(f.Assessor.Calls > calls);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal("Resolved", (await db.HotFileIncidents.SingleAsync(i => i.Id == older)).State);
        Assert.Equal("Resolved", (await db.HotFileIncidents.SingleAsync(i => i.Id == newer)).State);
        Assert.Equal(2, await db.HotFileIncidents.CountAsync());
    }

    [Fact]
    public async Task Every_unresolved_generation_of_a_repository_is_advanced_by_one_poll_not_only_the_newest()
    {
        var (f, older, newer) = await TwoGenerationsAsync();
        await SetAsync(f, older, "Held");
        await SetAsync(f, newer, "Held");
        var calls = f.Assessor.Calls;
        await using (var recovery = f.Recovery) await recovery.AdvanceAsync(A, default);
        Assert.Equal(calls + 2, f.Assessor.Calls);
        await using var db = f.Contexts.CreateDbContext();
        Assert.All(await db.HotFileIncidents.ToListAsync(), i => Assert.Equal(1, i.Attempts));
    }

    [Fact]
    public async Task Reassess_rearms_an_older_escalated_generation_behind_a_newer_resolved_one()
    {
        var (f, older, newer) = await TwoGenerationsAsync();
        await SetAsync(f, older, "Escalated");
        var result = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync(A, "human", default);
        Assert.True(result.Ok, result.Message);
        await using var db = f.Contexts.CreateDbContext();
        var rearmed = await db.HotFileIncidents.SingleAsync(i => i.Id == older);
        // A generation is unique per repository: the re-armed incident takes the next one after every incident's.
        Assert.Equal(("Held", 3), (rearmed.State, rearmed.Generation));
        var untouched = await db.HotFileIncidents.SingleAsync(i => i.Id == newer);
        Assert.Equal(("Resolved", 2), (untouched.State, untouched.Generation));
        Assert.Contains($"incident {older} generation 1", (await db.LedgerEntries.SingleAsync(e => e.Step == RecoveryReassessment.Step)).Detail);
    }

    private static async Task<string> ControlsJsonAsync(HotFileRecoveryTests.Fixture f)
    {
        await using var db = f.Contexts.CreateDbContext();
        return JsonSerializer.Serialize(await db.Controls.AsNoTracking().OrderBy(c => c.Scope).ToListAsync());
    }

    [Fact]
    public async Task Reassess_rearms_only_the_escalated_incident_with_fresh_budgets_an_audit_row_and_no_control_change()
    {
        var f = await EscalatedAsync();
        var controls = new LedgerControls(f.Contexts, f.Time);
        await controls.SetAsync(ControlScope.Repository(RepoRef.Parse(A)), ControlState.Paused, "human", default);
        await controls.SetAsync(ControlScope.Item("sc-3"), ControlState.Stopping, "human", default);
        await controls.SetAsync(ControlScope.Usage, ControlState.Paused, UsagePause.By, default);
        await controls.FreezeAsync("hot-file", "legacy hot-file freeze", null, default);
        var before = await ControlsJsonAsync(f);
        HotFileIncident other;
        long rows;
        await using (var db = f.Contexts.CreateDbContext())
        {
            other = await db.HotFileIncidents.AsNoTracking().SingleAsync(i => i.Repo == B);
            rows = await db.LedgerEntries.CountAsync();
        }

        var result = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync("OWNER/Alpha", "michael (cli)", default);

        Assert.True(result.Ok, result.Message);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync(i => i.Repo == A);
            Assert.Equal((2, "Held", 0, 0), (incident.Generation, incident.State, incident.Attempts, incident.Refreshes));
            Assert.Null(incident.Answer);
            var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
            Assert.Equal((2, A), (snapshot.Generation, snapshot.Repo));
            Assert.Equal(8, snapshot.Merges.Count);
            Assert.StartsWith("Re-armed by michael (cli)", incident.Error);
            var audit = await db.LedgerEntries.SingleAsync(e => e.Step == RecoveryReassessment.Step);
            Assert.Contains($"incident {incident.Id} generation 1", audit.Detail);
            Assert.Contains("re-armed as generation 2 by michael (cli) at " + f.Time.GetUtcNow().ToString("u"), audit.Detail);
            Assert.Equal(f.Time.GetUtcNow(), audit.RecordedAt);
            Assert.StartsWith($"incident {incident.Id} {A} generation 1 → 2: Escalated → Held; Re-armed by michael (cli)",
                (await db.LedgerEntries.Where(e => e.Step == IncidentTransitions.Step).OrderBy(e => e.Id).LastAsync()).Detail);
            // Only the incident and its audit row changed: no control, no other repository's incident.
            Assert.Equal(other.Version, (await db.HotFileIncidents.SingleAsync(i => i.Repo == B)).Version);
            Assert.Equal(other.State, (await db.HotFileIncidents.SingleAsync(i => i.Repo == B)).State);
        }
        Assert.Equal(before, await ControlsJsonAsync(f));

        // A second re-arm is refused: the incident is no longer escalated. Nothing is written.
        var again = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync(A, "michael (cli)", default);
        Assert.False(again.Ok);
        Assert.Contains("not Escalated", again.Message);
        await using (var db = f.Contexts.CreateDbContext())
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryReassessment.Step).ToListAsync());

        // The ordinary stages then assess the new generation afresh; a safe result resolves it, and the human Pause still holds.
        f.Assessor.Answer = null;
        var calls = f.Assessor.Calls;
        await using (var recovery = f.Recovery) await recovery.AdvanceAsync(A, default);
        Assert.Equal(calls + 1, f.Assessor.Calls);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync(i => i.Repo == A);
            Assert.Equal(("Resolved", 2, 1), (incident.State, incident.Generation, incident.Attempts));
        }
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync("sc-1", null, A, default));
        Assert.Equal(before, await ControlsJsonAsync(f));
        _ = rows;
    }

    [Theory]
    [InlineData("owner/none")]
    [InlineData("not a repo")]
    public async Task Reassess_refuses_a_repository_without_an_escalated_incident_and_writes_nothing(string repo)
    {
        var f = await EscalatedAsync();
        await using var before = f.Contexts.CreateDbContext();
        var incidents = JsonSerializer.Serialize(await before.HotFileIncidents.AsNoTracking().OrderBy(i => i.Id).ToListAsync());
        var rows = await before.LedgerEntries.CountAsync();
        var held = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync(B, "human", default);
        var result = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync(repo, "human", default);
        Assert.False(held.Ok); // B's incident is Held, not Escalated
        Assert.False(result.Ok);
        Assert.Contains("nothing was written", result.Message);
        await using var after = f.Contexts.CreateDbContext();
        Assert.Equal(incidents, JsonSerializer.Serialize(await after.HotFileIncidents.AsNoTracking().OrderBy(i => i.Id).ToListAsync()));
        Assert.Equal(rows, await after.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Reassess_waits_out_another_process_driving_the_repository_and_writes_nothing()
    {
        var f = await EscalatedAsync();
        await using (await f.Locks.TryAcquireAsync(HotFileRecovery.LockKey(A), default))
        {
            var result = await new RecoveryReassessment(f.Contexts, f.Locks, f.Time).ReassessAsync(A, "human", default);
            Assert.False(result.Ok);
            Assert.Contains("another process", result.Message);
        }
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync(i => i.Repo == A)).State);
    }

    [Fact]
    public async Task Correction_attempts_are_budgeted_per_generation_so_a_rearm_allows_two_more_and_never_a_third_in_one()
    {
        await using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var incident = new HotFileIncident { Repo = A, Generation = 1, State = "Correcting", Snapshot = "{}", EvidenceHash = "h", Version = 1 };
        db.HotFileIncidents.Add(incident);
        await db.SaveChangesAsync();
        foreach (var attempt in new[] { 1, 2 })
            db.RecoveryCorrections.Add(new RecoveryCorrection { IncidentId = incident.Id, Generation = 1, Attempt = attempt, Repo = A, Base = "main",
                Snapshot = "s", MaterialSnapshot = "m", MaterialHash = "h", EvidenceHash = "h", Paths = "[]", Spec = "s", State = "Failed", Version = 1 });
        await db.SaveChangesAsync();
        var answer = new HotFileAssessment("s", 1, A, "high", "unsafe", "h");
        var policy = GatePolicy.Parse(TestPolicies.Standard());
        var spent = await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryCorrections.MaterializeAsync(db, incident, answer, policy, default, default, 3));
        Assert.Contains("exceeds two attempts", spent.Message);
        incident.Generation = 2; // a human re-arm
        var fresh = await Assert.ThrowsAnyAsync<Exception>(() => RecoveryCorrections.MaterializeAsync(db, incident, answer, policy, default, default, 3));
        Assert.DoesNotContain("exceeds two attempts", fresh.Message); // past the budget; refused later only for this fixture's empty snapshot
    }

    [Fact]
    public async Task The_cli_names_the_repository_and_requires_it()
    {
        string? asked = null;
        var cli = FactoryCli.Build((_, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException(), recoveryReassess: (repo, _) => { asked = repo; return Task.FromResult(0); });
        Assert.Equal(0, await cli.Parse("recovery reassess --repo Owner/Alpha").InvokeAsync());
        Assert.Equal("Owner/Alpha", asked);
        asked = null;
        Assert.NotEqual(0, await cli.Parse("recovery reassess").InvokeAsync());
        Assert.NotEqual(0, await cli.Parse("recovery reassess --repo nonsense").InvokeAsync());
        Assert.Null(asked);
    }

    /// <summary>No scheduler, intake, pipeline or recovery stage may re-arm: only the owner's CLI and the dashboard's form post.</summary>
    [Fact]
    public void Only_the_cli_and_the_dashboard_post_can_rearm_an_incident()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "DarkFactory.slnx"))) root = root.Parent ?? throw new InvalidOperationException("repository root not found");
        var src = Path.Combine(root.FullName, "src", "DarkFactory.Orchestrator");
        var callers = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs") || p.EndsWith(".razor")) && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p) is var text && (text.Contains("ReassessAsync") || text.Contains("RecoveryReassessment")))
            .Select(p => Path.GetRelativePath(src, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Dashboard/DashboardControls.cs", "FactoryHost.cs", "Program.cs", "Recovery/RecoveryReassessment.cs"], callers);
        // FactoryHost only registers the service for the dashboard's post; it never calls it.
        Assert.DoesNotContain("ReassessAsync", File.ReadAllText(Path.Combine(src, "FactoryHost.cs")));
    }
}
