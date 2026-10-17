using System.Text.Json;
using Bunit;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Dashboard.Components.Pages;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.Orchestrator.Tests;

public sealed partial class RecoveryDashboardTests
{
    [Fact]
    public async Task Rejected_recorded_findings_never_claim_validation()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        var answer = HotFileAssessment.FromDetail(incident.Answer)!;
        incident.Answer = JsonSerializer.Serialize(answer with { ServedClass = "low", Problem = "wrong class",
            Findings = [new RecoveryFinding("merged", true, "broken", "expected", [], [], [], [])] });
        await db.SaveChangesAsync();
        var text = LedgerReport.RecoveryStatus(await new RecoveryStatusReader(_contexts, _time).ReadAsync(default));
        Assert.DoesNotContain("validated findings", text);
        Assert.Contains("recorded", text);
        Assert.Contains("wrong class", text);
    }

    [Fact]
    public async Task Safe_discard_redirect_uses_resolution_and_durable_restart_not_a_missing_commit()
    {
        await Seed("Resolved");
        await using var db = _contexts.CreateDbContext();
        var redirect = await db.LedgerEntries.SingleAsync(e => e.Step == RecoveryRedirect.Step);
        db.LedgerEntries.Add(new() { WorkItemId = 1, State = WorkState.Implement, Step = RecoveryRedirect.Started,
            Detail = redirect.Detail, Outcome = StepOutcome.Passed });
        await db.SaveChangesAsync();
        var row = (await new RecoveryStatusReader(_contexts, _time).ReadAsync(default)).Repositories.Single();
        Assert.Contains("restarted", row.Redirects.Single());
        Assert.DoesNotContain("waiting for delivery", row.Redirects.Single());
    }

    [Fact]
    public async Task A_new_successor_incident_invalidates_a_resolved_observation()
    {
        await Seed("Resolved");
        var interceptor = new OnIncidentRead(() =>
        {
            using var db = _contexts.CreateDbContext();
            db.HotFileIncidents.Add(new() { Id = 8, Repo = "owner/alpha", Generation = 3, State = "Detected",
                Snapshot = "{}", EvidenceHash = "next", CreatedAt = _time.GetUtcNow() });
            db.SaveChanges();
        });
        var options = new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(_database, _root).AddInterceptors(interceptor);
        var row = (await new RecoveryStatusReader(new LedgerDbContextFactory(options.Options), _time).ReadAsync(default)).Repositories.Single();
        Assert.Contains("state changed during read", row.Hold);
        Assert.DoesNotContain("automatic hold resolved", row.Hold);
    }

    [Fact]
    public async Task Idle_page_retries_an_initial_failure_without_a_notification()
    {
        var data = new RecoveryRetryData();
        Services.AddSingleton<IDashboardData>(data);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<PipelineChanges>();
        Services.AddSingleton(new IntakeStatus(_time));
        var cut = Render<Pipeline>();
        Assert.Equal(1, data.Reads);
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal(2, data.Reads));
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal(3, data.Reads));
    }

    [Fact]
    public void An_idle_tick_polls_only_the_recovery_panel_and_a_live_run_reloads_the_page()
    {
        var data = new CountingData();
        Services.AddSingleton<IDashboardData>(data);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<PipelineChanges>();
        Services.AddSingleton(new IntakeStatus(_time));
        var cut = Render<Pipeline>();
        Assert.Equal((1, 1), (data.Items, data.Reads));
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal(2, data.Reads));
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal(3, data.Reads));
        Assert.Equal(1, data.Items); // idle: the rows and metrics are not reloaded every tick
        data.Live = true;
        Services.GetRequiredService<PipelineChanges>().Notify(1);
        cut.WaitForAssertion(() => Assert.Equal(2, data.Items));
        _time.Advance(TimeSpan.FromSeconds(15));
        cut.WaitForAssertion(() => Assert.Equal(3, data.Items)); // a live run is asked about on every tick
    }

    private sealed class CountingData : IDashboardData
    {
        public int Items, Reads;
        public bool Live;
        public Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct)
        {
            Items++;
            return Task.FromResult<IReadOnlyList<PipelineRow>>(Live
                ? [new PipelineRow(1, "sc-1", "live", "owner/alpha", WorkState.Implement, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [],
                    Run: new RunView(true, "implementing", SlotUse.None))]
                : []);
        }
        public Task<SessionHeader?> SessionAsync(string id, CancellationToken ct) => Task.FromResult<SessionHeader?>(null);
        public Task<RecoveryStatusReport> RecoveryAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new RecoveryStatusReport(DateTimeOffset.UtcNow, []));
        }
    }

    private sealed class RecoveryRetryData : IDashboardData
    {
        public int Reads;
        public Task<IReadOnlyList<PipelineRow>> ActiveItemsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PipelineRow>>([]);
        public Task<SessionHeader?> SessionAsync(string id, CancellationToken ct) => Task.FromResult<SessionHeader?>(null);
        public Task<RecoveryStatusReport> RecoveryAsync(CancellationToken ct)
        {
            if (++Reads == 1) throw new InvalidOperationException("ledger temporarily unavailable");
            return Task.FromResult(new RecoveryStatusReport(DateTimeOffset.UtcNow, []));
        }
    }

    [Fact]
    public async Task Unstarted_plan_children_are_named_in_their_own_repository_only()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        db.WorkItems.Add(new() { Id = 20, Source = "shortcut", ExternalId = "sc-20", Repo = "owner/beta", Title = "multi", State = WorkState.Planned });
        var plan = new Plan([new("a", "alpha child", "a", ["works"], "chore", [], "owner/alpha"),
            new("b", "beta child", "b", ["works"], "chore", [], "owner/beta")], ["owner/alpha", "owner/beta"], "none");
        db.LedgerEntries.AddRange(new LedgerEntry { WorkItemId = 20, State = WorkState.Planned, Step = RunPipeline.Steps.PlanStories,
            Detail = "sc-21 sc-22", Outcome = StepOutcome.Passed }, new LedgerEntry { WorkItemId = 20, State = WorkState.Plan, Step = RunPipeline.Steps.Plan,
            Detail = plan.ToDetail(), Outcome = StepOutcome.Passed });
        await db.SaveChangesAsync();
        var row = (await new RecoveryStatusReader(_contexts, _time).ReadAsync(default)).Repositories.Single();
        Assert.Contains(row.HeldItems, x => x.Contains("sc-21"));
        Assert.DoesNotContain(row.HeldItems, x => x.Contains("sc-22"));
    }

    [Fact]
    public async Task Resolved_history_does_not_call_running_work_held_but_keeps_ancestor_pauses()
    {
        await Seed("Resolved");
        await using var db = _contexts.CreateDbContext();
        (await db.WorkItems.SingleAsync(w => w.Id == 1)).State = WorkState.Implement;
        await db.SaveChangesAsync();
        var reader = new RecoveryStatusReader(_contexts, _time);
        Assert.Empty((await reader.ReadAsync(default)).Repositories.Single().HeldItems);
        db.Controls.Add(new() { Scope = ControlScope.Item("sc-1"), State = ControlState.Paused, ChangedBy = "human" });
        await db.SaveChangesAsync();
        Assert.Contains((await reader.ReadAsync(default)).Repositories.Single().HeldItems, x => x.Contains("sc-1"));
    }

    [Fact]
    public async Task Active_incidents_are_prioritized_over_a_full_page_of_resolved_history()
    {
        await Seed("Resolved");
        await using var db = _contexts.CreateDbContext();
        for (var n = 0; n < 101; n++) db.HotFileIncidents.Add(new() { Repo = $"aaa/repo{n:D3}", Generation = 1,
            State = "Resolved", Snapshot = "{}", EvidenceHash = "x" });
        db.HotFileIncidents.Add(new() { Repo = "zzz/active", Generation = 1, State = "Escalated", Snapshot = "{}", EvidenceHash = "x" });
        await db.SaveChangesAsync();
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Contains(report.Repositories, x => x.Repo == "zzz/active");
        Assert.Contains("page", report.Note);
    }

    [Fact]
    public async Task Every_repository_remains_accessible_through_dashboard_and_cli_pages()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        for (var n = 0; n < 105; n++) db.HotFileIncidents.Add(new() { Id = 100 + n, Repo = $"owner/paged{n:D3}", Generation = 1,
            State = "Escalated", Snapshot = "{}", EvidenceHash = "x" });
        await db.SaveChangesAsync();
        var reader = new RecoveryStatusReader(_contexts, _time);
        var first = await reader.ReadPageAsync(0, default);
        var second = await reader.ReadPageAsync(1, default);
        Assert.Equal(100, first.Repositories.Count); Assert.True(first.HasMore);
        Assert.Equal(6, second.Repositories.Count); Assert.False(second.HasMore);
        Assert.Equal(106, first.Repositories.Concat(second.Repositories).Select(r => r.Incident).Distinct().Count());
        var cli = FactoryCli.Build((_, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException(), recoveryPageStatus: async (page, ct) =>
            {
                Assert.Equal(1, page);
                Assert.Equal(second.Repositories.Select(r => r.Incident), (await reader.ReadPageAsync(page, ct)).Repositories.Select(r => r.Incident));
                return 0;
            });
        Assert.Equal(0, await cli.Parse("recovery status --page 1").InvokeAsync());
        Assert.NotEqual(0, await cli.Parse("recovery status --page -1").InvokeAsync());
        var cut = Page();
        cut.Find("button.recovery-next").Click();
        cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll(".repository-recovery").Count));
        Assert.Empty(cut.FindAll("button.recovery-next"));
        cut.Find("button.recovery-previous").Click();
        cut.WaitForAssertion(() => Assert.Equal(100, cut.FindAll(".repository-recovery").Count));
    }

    [Fact]
    public async Task Safe_but_plan_paused_children_remain_held_and_dispositions_survive_finished_work()
    {
        await Seed("Resolved");
        await using var db = _contexts.CreateDbContext();
        (await db.WorkItems.SingleAsync(w => w.Id == 1)).State = WorkState.Watch;
        db.WorkItems.Add(new() { Id = 20, Source = "shortcut", ExternalId = "sc-20", Repo = "owner/beta", EpicId = 40,
            Title = "parent", State = WorkState.Planned });
        var plan = new Plan([new("a", "child", "work", ["works"], "chore", [], "owner/alpha")], ["owner/alpha"]);
        db.LedgerEntries.AddRange(new LedgerEntry { WorkItemId = 20, State = WorkState.Planned, Step = RunPipeline.Steps.PlanStories,
            Detail = "sc-21", Outcome = StepOutcome.Passed }, new LedgerEntry { WorkItemId = 20, State = WorkState.Planned, Step = RunPipeline.Steps.Plan,
            Detail = plan.ToDetail(), Outcome = StepOutcome.Passed });
        db.Controls.Add(new() { Scope = ControlScope.Item("sc-20"), State = ControlState.Paused, ChangedBy = "human" });
        await db.SaveChangesAsync();
        var row = (await new RecoveryStatusReader(_contexts, _time).ReadAsync(default)).Repositories.Single();
        Assert.Contains(row.HeldItems, x => x.Contains("sc-21"));
        Assert.Contains(row.Controls, x => x.Contains("item:sc-20") && x.Contains("human Pause"));
        Assert.Contains("controls still hold", row.Hold);
        Assert.Contains(row.Redirects, x => x.Contains("sc-1"));
        db.WorkItems.Add(new() { Id = 21, Source = "shortcut", ExternalId = "sc-21", Repo = "owner/alpha", Title = "listed child", State = WorkState.Intake });
        await db.SaveChangesAsync();
        Assert.Contains((await new RecoveryStatusReader(_contexts, _time).ReadAsync(default)).Repositories.Single().HeldItems, x => x.Contains("sc-21"));
        var pause = await db.Controls.SingleAsync(); pause.State = ControlState.Running;
        await db.SaveChangesAsync();
        Assert.Empty((await new RecoveryStatusReader(_contexts, _time).ReadAsync(default)).Repositories.Single().HeldItems);
    }

    [Fact]
    public async Task Unstarted_plan_membership_overflow_is_explicit_not_silent()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        for (var n = 0; n < 6; n++)
        {
            var parent = 100 + n;
            db.WorkItems.Add(new() { Id = parent, Source = "shortcut", ExternalId = $"sc-{parent}", Repo = "owner/beta",
                Title = "multi", State = WorkState.Planned });
            var plan = new Plan(Enumerable.Range(0, 20).Select(i => new PlanStory($"k{i}", $"child {i}", "work", ["works"],
                "chore", [], "owner/alpha")).ToArray(), ["owner/alpha"]);
            db.LedgerEntries.AddRange(new LedgerEntry { WorkItemId = parent, State = WorkState.Planned, Step = RunPipeline.Steps.PlanStories,
                Detail = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"sc-{1000 + n * 20 + i}")), Outcome = StepOutcome.Passed },
                new LedgerEntry { WorkItemId = parent, State = WorkState.Planned, Step = RunPipeline.Steps.Plan,
                Detail = plan.ToDetail(), Outcome = StepOutcome.Passed });
        }
        await db.SaveChangesAsync();
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Equal(101, report.Repositories.Single().HeldItems.Count);
        Assert.Contains("21 held items omitted", LedgerReport.RecoveryStatus(report));
        Assert.Contains("21 held items omitted", Page().Find(".repository-recovery").TextContent);
    }

    [Fact]
    public async Task Inflight_assessment_keeps_reserved_session_and_prompt_without_an_answer()
    {
        await Seed("Assessing");
        await using var db = _contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        incident.Answer = null; incident.Session = "durable-session"; incident.Prompt = "durable-prompt";
        db.RecoveryAssessmentAttempts.Add(new() { IncidentId = 7, Snapshot = "trigger", Session = "reserved-session", Number = 3,
            State = "Dispatched", LeaseUntil = _time.GetUtcNow() });
        await db.SaveChangesAsync();
        var text = LedgerReport.RecoveryStatus(await new RecoveryStatusReader(_contexts, _time).ReadAsync(default));
        Assert.Contains("durable-session", text); Assert.Contains("durable-prompt", text);
        Assert.Contains("reserved-session", text); Assert.Contains("cost N/A", text);
        Assert.Contains("result unavailable", text);
    }

    [Fact]
    public async Task Status_reads_batch_dependents_instead_of_reading_each_repository()
    {
        await using var database = await Support.TempPostgresDatabase.CreateAsync("df_status_queries");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var interceptor = new QueryCounter();
        _contexts = new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptor).Options);
        await Seed();
        interceptor.Reads = 0;
        await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        var singleRepositoryReads = interceptor.Reads;
        await using var db = _contexts.CreateDbContext();
        for (var n = 0; n < 8; n++) db.HotFileIncidents.Add(new() { Id = 100 + n, Repo = $"owner/batch{n}", Generation = 1,
            State = "Detected", Snapshot = "{}", EvidenceHash = "x" });
        await db.SaveChangesAsync();
        interceptor.Reads = 0;
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Equal(9, report.Repositories.Count);
        Assert.InRange(interceptor.Reads, 1, 20);
        Assert.Equal(singleRepositoryReads, interceptor.Reads);
    }

    private sealed class QueryCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        { Reads++; return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task Hot_file_overflow_is_explicit_in_the_same_dashboard_and_cli_projection()
    {
        await Seed();
        await using var db = _contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        var trigger = HotFileSnapshot.Parse(incident.Snapshot);
        incident.Snapshot = JsonSerializer.Serialize(trigger with { Merges = [trigger.Merges[0] with {
            Files = Enumerable.Range(0, 130).Select(n => $"src/hot{n}.cs").ToArray() }] });
        await db.SaveChangesAsync();
        var report = await new RecoveryStatusReader(_contexts, _time).ReadAsync(default);
        Assert.Contains("29 hot file/base pairs omitted", LedgerReport.RecoveryStatus(report));
        Assert.Contains("29 hot file/base pairs omitted", Page().Find(".repository-recovery").TextContent);
    }
}
