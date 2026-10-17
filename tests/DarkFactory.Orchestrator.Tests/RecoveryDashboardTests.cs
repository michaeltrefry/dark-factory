using System.Text.Json;
using Bunit;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Dashboard.Components.Pages;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryDashboardTests : BunitContext
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot _root = new();
    private LedgerDbContextFactory _contexts;
    public RecoveryDashboardTests() => _contexts = new(new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(_database, _root).Options);

    private async Task Seed(string state = "Correcting", bool pause = false)
    {
        await using var db = _contexts.CreateDbContext();
        var trigger = new HotFileSnapshot("trigger", "owner/alpha", 2,
            [new(11, 1, new string('a', 40), "main", ["src/hot.cs"])], "hash");
        var material = new RecoverySnapshot("material", "trigger", "owner/alpha", 2,
            new Dictionary<string, string> { ["main"] = new string('b', 40) }, [], []);
        db.HotFileIncidents.Add(new() { Id = 7, Repo = "owner/alpha", Generation = 2, State = state,
            Snapshot = JsonSerializer.Serialize(trigger), EvidenceHash = "hash", Attempts = 3, Refreshes = 1,
            Answer = JsonSerializer.Serialize(new HotFileAssessment("trigger", 2, "owner/alpha", "high", state == "Resolved" ? "safe" : "unsafe", "hash")
                { Material = JsonSerializer.Serialize(material), MaterialSnapshot = "material", MaterialHash = "pinned-hash", Session = "assessment-session", Prompt = "assessment@sha256:abc" }),
            Error = state == "Escalated" ? "correction attempts exhausted" : null,
            CreatedAt = _time.GetUtcNow(), ResolvedAt = state == "Resolved" ? _time.GetUtcNow() : null });
        db.WorkItems.AddRange(new WorkItem { Id = 1, Source = "shortcut", ExternalId = "sc-1", Title = "held", Repo = "owner/alpha", State = WorkState.Paused },
            new WorkItem { Id = 2, Source = "shortcut", ExternalId = "sc-2", Title = "ordinary beta", Repo = "owner/beta", State = WorkState.Implement },
            new WorkItem { Id = 3, Source = "recovery", ExternalId = "rc-5", Title = "correction", Repo = "owner/alpha", State = WorkState.Review,
                Entries = [new() { State = WorkState.Review, Step = RunPipeline.Steps.Linked, Detail = "https://github.com/owner/alpha/pull/9 https://github.com/owner/alpha/tree/factory/rc-5", Outcome = StepOutcome.Passed }] });
        db.RecoveryCorrections.Add(new() { Id = 5, IncidentId = 7, Generation = 2, Attempt = 2, Repo = "owner/alpha", Base = "main", Snapshot = "trigger",
            MaterialSnapshot = "material", MaterialHash = "pinned-hash", EvidenceHash = "hash", Paths = "[]", Spec = "not displayed", State = "Pending" });
        db.LedgerEntries.Add(new() { WorkItemId = 1, State = WorkState.Paused, Step = RecoveryRedirect.Step,
            Detail = JsonSerializer.Serialize(new RecoveryRedirect(7, "sc-1", "owner/alpha", "main", null, "<script>not executable</script>")),
            RecordedAt = _time.GetUtcNow(), Outcome = StepOutcome.Deferred });
        if (pause) db.Controls.Add(new() { Scope = "repo:owner/alpha", State = ControlState.Paused, ChangedBy = "human", ChangedAt = _time.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Repository_continue_via_cli_clears_only_that_manual_scope_not_incident_or_other_pause()
    {
        await Seed("Escalated", pause: true);
        var controls = new LedgerControls(_contexts, _time);
        await controls.SetAsync(ControlScope.Factory, ControlState.Paused, "human", default);
        await controls.SetAsync("repo:owner/beta", ControlState.Paused, "human", default);
        var actions = new ControlActions(controls, _contexts);
        var statusCalls = 0;
        var cli = FactoryCli.Build((_, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            async (action, scope, ct) => (await actions.ContinueAsync(scope, "human (cli)", ct)).ExitCode,
            recoveryStatus: async ct => { statusCalls++; Assert.Contains("correction attempts exhausted", LedgerReport.RecoveryStatus(await new RecoveryStatusReader(_contexts, _time).ReadAsync(ct))); return 0; });
        Assert.Equal(0, await cli.Parse("recovery status").InvokeAsync());
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, await cli.Parse("continue --repo OWNER/ALPHA").InvokeAsync());
        Assert.Equal(ControlState.Running, (await controls.GetAsync("repo:owner/alpha", default))!.State);
        Assert.Equal(ControlState.Paused, (await controls.GetAsync(ControlScope.Factory, default))!.State);
        Assert.Equal(ControlState.Paused, (await controls.GetAsync("repo:owner/beta", default))!.State);
        await using var db = _contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync()).State);
    }

    [Theory]
    [InlineData("https://github.com/owner/bravo/pull/9")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com/owner/alpha/pull/9?token=secret")]
    public async Task A_correction_link_outside_its_repository_is_never_clickable(string link)
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        (await db.LedgerEntries.SingleAsync(e => e.Step == RunPipeline.Steps.Linked)).Detail = link;
        await db.SaveChangesAsync();
        var row = Page().Find(".repository-recovery");
        Assert.Empty(row.QuerySelectorAll("a.correction-pr"));
        Assert.Contains("PR unavailable", row.TextContent);
        Assert.DoesNotContain("secret", row.TextContent);
    }

    [Fact]
    public async Task Legacy_freeze_is_named_and_never_acknowledged_by_rendering()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        db.Controls.Add(new() { Scope = ControlScope.Freeze, State = ControlState.Paused, Reason = "hot-file", Detail = "red-tip old acknowledged", ChangedBy = "freeze", ChangedAt = _time.GetUtcNow() });
        await db.SaveChangesAsync();
        var cut = Page();
        Assert.Contains("predates repository incident recovery", cut.Find(".freeze").TextContent);
        Assert.Contains("explicit human Continue", cut.Find(".freeze").TextContent);
        var row = await db.Controls.AsNoTracking().SingleAsync();
        Assert.Equal(_time.GetUtcNow(), row.ChangedAt);
        Assert.Equal("red-tip old acknowledged", row.Detail);
        Assert.Equal(ControlState.Paused, row.State);
    }

    [Fact]
    public async Task Existing_reports_show_recovery_provenance_as_inert_bounded_ledger_facts()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        var rows = await db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        var report = LedgerReport.Facts(rows, []);
        Assert.Contains("Recovery ledger row", report);
        Assert.Contains("recovery-redirect", report);
        Assert.DoesNotContain("<script>", report);
    }

    [Fact]
    public async Task Concurrent_stage_change_is_reported_as_an_observation_race_not_a_safe_release()
    {
        await Seed("Resolved");
        var interceptor = new OnIncidentRead(() =>
        {
            using var db = _contexts.CreateDbContext();
            var incident = db.HotFileIncidents.Single();
            incident.State = "Escalated";
            incident.Version++;
            db.SaveChanges();
        });
        var builder = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(_database, _root).AddInterceptors(interceptor);
        var report = await new RecoveryStatusReader(new LedgerDbContextFactory(builder.Options), _time).ReadAsync(default);
        Assert.Contains("state changed during read", report.Repositories.Single().Hold);
        Assert.DoesNotContain("automatic hold resolved", report.Repositories.Single().Hold);
    }

    private sealed class OnIncidentRead(Action change) : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
    {
        private bool _changed;
        public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData data, object entity)
        {
            if (entity is HotFileIncident && !_changed) { _changed = true; change(); }
            return entity;
        }
    }

    private IRenderedComponent<Pipeline> Page()
    {
        Services.AddSingleton<IDashboardData>(new DashboardData(_contexts, _time));
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<PipelineChanges>();
        Services.AddSingleton(new IntakeStatus(_time));
        return Render<Pipeline>();
    }

    [Fact]
    public async Task Postgres_operator_queries_read_the_actual_incident_correction_and_redirect_schema()
    {
        await using var database = await Support.TempPostgresDatabase.CreateAsync("df_recovery_status");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        _contexts = new(LedgerDbContext.PostgresOptions(database.ConnectionString));
        await Seed();
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Equal("Correcting", report.Repositories.Single().Stage);
        Assert.Equal("https://github.com/owner/alpha/pull/9", report.Repositories.Single().Corrections.Single().PullRequest);
        Assert.Contains("sc-1", report.Repositories.Single().Redirects.Single());
    }

    [Fact]
    public async Task Pipeline_and_report_separate_recovery_from_normal_repository_activity_without_side_effects()
    {
        await Seed();
        await using var before = _contexts.CreateDbContext();
        var entries = await before.LedgerEntries.CountAsync();
        var cut = Page();
        var recovery = cut.Find(".repository-recovery[data-repo='owner/alpha']");
        Assert.Contains("src/hot.cs", recovery.TextContent);
        Assert.Contains("Correcting", recovery.TextContent);
        Assert.Contains("3/3", recovery.TextContent);
        Assert.Contains("2/2", recovery.TextContent);
        Assert.Equal("https://github.com/owner/alpha/pull/9", recovery.QuerySelector("a.correction-pr")!.GetAttribute("href"));
        Assert.Contains("ordinary beta", cut.Find("table.pipeline").TextContent);
        Assert.DoesNotContain("ordinary beta", recovery.TextContent);
        Assert.DoesNotContain("sc-2", recovery.TextContent);
        Assert.Contains("snapshot material", recovery.TextContent);
        Assert.Contains("not live-validated", recovery.TextContent);
        Assert.Contains("N/A", recovery.TextContent);
        Assert.Empty(cut.FindAll("script"));
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        var text = LedgerReport.RecoveryStatus(report);
        Assert.Contains("assessment-session", text);
        Assert.Contains("pinned-hash", text);
        Assert.Contains("rc-5", text);
        Assert.Contains("sc-1", text);
        Assert.Equal(entries, await before.LedgerEntries.CountAsync());
        Assert.Equal("Correcting", (await before.HotFileIncidents.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task Safe_resolution_keeps_the_human_pause_and_only_offers_its_exact_continue_scope()
    {
        await Seed("Resolved", pause: true);
        var cut = Page();
        var row = cut.Find(".repository-recovery");
        Assert.Contains("automatic hold resolved", row.TextContent);
        Assert.Contains("human Pause", row.TextContent);
        Assert.DoesNotContain("accepting work", row.TextContent);
        Assert.Contains("repo:owner/alpha", row.QuerySelector("input[name='scope']")!.GetAttribute("value"));
        Assert.Contains("continue", row.QuerySelector("input[name='action']")!.GetAttribute("value"));
        await using var db = _contexts.CreateDbContext();
        Assert.Equal(ControlState.Paused, (await db.Controls.SingleAsync()).State);
    }

    [Fact]
    public async Task Exhausted_correction_names_held_work_and_a_human_remedy_not_a_continue_of_the_incident()
    {
        await Seed("Escalated");
        await using var db = _contexts.CreateDbContext();
        var correction = await db.WorkItems.SingleAsync(w => w.ExternalId == "rc-5");
        correction.State = WorkState.Escalated;
        db.LedgerEntries.Add(new() { WorkItemId = correction.Id, State = WorkState.Escalated, Detail = "new-tests rejected: new test passes on base", Outcome = StepOutcome.Escalated });
        await db.SaveChangesAsync();
        var row = Page().Find(".repository-recovery");
        Assert.Contains("new test passes on base", row.TextContent);
        Assert.Contains("correction attempts exhausted", row.TextContent);
        Assert.Contains("sc-1", row.TextContent);
        Assert.Contains("human", row.TextContent);
        Assert.Contains("factory stop --repo owner/alpha", row.TextContent);
        Assert.Contains("does not reset", row.TextContent);
        Assert.DoesNotContain("accepting work", row.TextContent);
        Assert.Empty(row.QuerySelectorAll("input[value='freeze']"));
    }

    [Fact]
    public async Task Missing_or_mismatched_snapshot_is_explicit_never_reported_fresh()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        incident.Snapshot = "{}";
        await db.SaveChangesAsync();
        var status = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Contains("unavailable", LedgerReport.RecoveryStatus(status));
    }

    [Fact]
    public async Task An_open_idle_page_refreshes_recovery_and_manual_controls_within_fifteen_seconds()
    {
        await Seed("Resolved");
        var cut = Page();
        await using var db = _contexts.CreateDbContext();
        db.Controls.Add(new() { Scope = "repo:owner/alpha", State = ControlState.Paused, ChangedBy = "human", ChangedAt = _time.GetUtcNow() });
        await db.SaveChangesAsync();
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Contains("human Pause", cut.Find(".repository-recovery").TextContent));
    }
}
