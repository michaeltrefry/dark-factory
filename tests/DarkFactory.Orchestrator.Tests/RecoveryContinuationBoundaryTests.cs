using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryContinuationBoundaryTests
{
    private const string Repo = "owner/alpha";
    private static readonly string Delivered = new('9', 40);

    internal static async Task<RecoverySnapshotTests.Fixture> SeedSafe(bool two = false, WorkState coding = WorkState.Implement, bool unclaimed = false)
    {
        var f = new RecoverySnapshotTests.Fixture(); await f.Seed();
        await using (var db = f.Contexts.CreateDbContext())
        {
            var pending = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-2");
            db.RemoveRange(await db.LedgerEntries.Where(e => e.WorkItemId == pending.Id).ToListAsync());
            pending.Title = "requirements";
            pending.State = WorkState.Paused;
            Add(pending);
            if (two)
            {
                var second = new WorkItem { Source = "shortcut", ExternalId = "sc-3", Title = "requirements", Repo = Repo,
                    State = WorkState.Paused, EpicId = 9, Version = 1 };
                db.Add(second); await db.SaveChangesAsync(); Add(second);
            }
            if (unclaimed)
            {
                f.Source.PlansItems = true;
                var parent = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10");
                var plan = await db.LedgerEntries.SingleAsync(e => e.WorkItemId == parent.Id && e.Step == "plan");
                plan.Detail = """
                    {"stories":[{"key":"child","title":"queued child","description":"original spec","acceptance":["works"],"type":"chore","blocked_by":[]}]}
                    """;
                db.Add(new LedgerEntry { WorkItemId = parent.Id, State = WorkState.Planned, Step = RunPipeline.Steps.PlanStories,
                    Detail = "sc-3", Outcome = StepOutcome.Passed });
            }
            db.Add(new HotFileIncident { Repo = Repo, Generation = 1, State = "Unsafe", Snapshot = JsonSerializer.Serialize(f.Trigger),
                EvidenceHash = f.Trigger.EvidenceHash });
            await db.SaveChangesAsync();
            void Add(WorkItem item)
            {
                db.Add(new LedgerEntry { WorkItemId = item.Id, State = coding, Outcome = StepOutcome.Passed });
                db.Add(new LedgerEntry { WorkItemId = item.Id, State = coding, Step = RunPipeline.Steps.WorkerStarted,
                    Detail = "123", ClaudeSessionId = "old-session", Outcome = StepOutcome.Passed });
                db.Add(new LedgerEntry { WorkItemId = item.Id, State = WorkState.Paused, Detail = RunPipeline.RecoveryHeld,
                    Outcome = StepOutcome.Deferred });
            }
        }
        var material = await f.Evidence.CaptureAsync(f.Trigger, default);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync();
            var answer = new HotFileAssessment(f.Trigger.Id, 1, Repo, "high", "unsafe", f.Trigger.EvidenceHash) { Material = material };
            await HotFileRecovery.RecordRedirectsAsync(db, incident, answer, DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
            foreach (var row in await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Step).ToListAsync())
                row.Detail = JsonSerializer.Serialize(RecoveryRedirect.Parse(row.Detail)! with { Commit = Delivered });
            await db.SaveChangesAsync();
        }
        material = await f.Evidence.CaptureAsync(f.Trigger, default);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.State = "Resolved";
            incident.Answer = JsonSerializer.Serialize(new HotFileAssessment(f.Trigger.Id, 1, Repo, "high", "safe", f.Trigger.EvidenceHash) { Material = material });
            await db.SaveChangesAsync();
        }
        return f;
    }

    internal static HotFileRecovery Recovery(RecoverySnapshotTests.Fixture f, IRunLocks? locks = null) =>
        new(f.Contexts, locks ?? new InProcessRunLocks(), new FreezeOptions(), TimeProvider.System,
            new HotFileRecoveryTests.Assessor(), capture: f.Evidence.CaptureAsync);

    internal static async Task<RunOutcome> Run(RecoverySnapshotTests.Fixture f, HotFileRecovery recovery, int id,
        DarkFactory.Orchestrator.Git.IRepoWorkspace workspaces, IWorker worker, WorkerSlots? slots = null, TextWriter? log = null, GateStage? gate = null)
    {
        await using var db = f.Contexts.CreateDbContext();
        return await new RunPipeline(f.Source, new WorkLedger(db, TimeProvider.System), new InProcessRunLocks(),
            workspaces, worker, new FakePullRequests(), RepoRef.Parse(Repo), log ?? TextWriter.Null,
            controls: new LedgerControls(f.Contexts, TimeProvider.System), recovery: recovery, slots: slots, gate: gate,
            controlPollInterval: TimeSpan.FromMilliseconds(5)).RunAsync(id, default);
    }

    [Theory]
    [InlineData(WorkState.Implement)]
    [InlineData(WorkState.Fixing)]
    public async Task Real_capture_unpublished_paused_coding_gets_a_discard_restart_disposition(WorkState coding)
    {
        var f = await SeedSafe(coding: coding);
        await using var recovery = Recovery(f);
        Assert.NotNull(await recovery.RedirectAsync("sc-2", default));
        var calls = 0;
        var worker = new FakeWorker(async call =>
        {
            calls++; Assert.Null(call.Resume);
            Assert.Contains("original spec", call.Prompt);
            Assert.Contains(Delivered, call.Prompt);
            await call.Callbacks.OnSession!("fresh-2", default);
            return new WorkerResult("fresh-2", 0, false, "success", "done", "");
        });
        var spaces = new FakeWorkspaces();
        var gate = coding == WorkState.Fixing ? new GateStage(new GatePipelineTests.FakeGateGitHub { Diff = _ => TestPolicies.Diff("docs/readme.md") },
            new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)) : null;
        var result = await Run(f, recovery, 2, spaces, worker, gate: gate);
        Assert.Equal(gate is null ? WorkState.Review : WorkState.Watch, result.State);
        Assert.Equal(1, calls);
        Assert.Contains($"corrected {Repo} {Delivered}", spaces.Calls);
        Assert.DoesNotContain(spaces.Calls, c => c.StartsWith("reopen ") || c.StartsWith("restore "));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_capture_consumption_is_per_item_and_external_requirements_still_revoke_it(bool changeSpec)
    {
        var f = await SeedSafe(two: true);
        await using var recovery = Recovery(f);
        var calls = 0;
        Func<WorkerCall, Task<WorkerResult>> behavior = async call =>
        {
            calls++; Assert.Null(call.Resume); Assert.Contains(Delivered, call.Prompt);
            await call.Callbacks.OnSession!("fresh-" + calls, default);
            return new WorkerResult("fresh-" + calls, 0, false, "success", "done", "");
        };
        var worker = new FakeWorker(behavior, behavior);
        var spaces = new FakeWorkspaces { Head = () => f.GitHub.Head };
        Assert.Equal(WorkState.Review, (await Run(f, recovery, 2, spaces, worker)).State);
        if (changeSpec)
        {
            var original = await f.Source.ReadSpecAsync(3, default);
            f.Source.SpecFor = id => original with { Story = original.Story with { Id = id, Description = id == 3 ? "external requirements changed" : original.Story.Description } };
        }
        // A new service instance has no in-memory consumption cache.
        await using var restarted = Recovery(f);
        var result = await Run(f, restarted, 3, spaces, worker);
        Assert.Equal(changeSpec ? WorkState.Paused : WorkState.Review, result.State);
        Assert.Equal(changeSpec ? 1 : 2, calls);
        Assert.Equal(changeSpec, await restarted.HeldAsync(Repo, default) is not null);
    }

    [Fact]
    public async Task Real_capture_unclaimed_plan_child_retains_durable_redirect_and_claims_before_restart()
    {
        var f = await SeedSafe(unclaimed: true);
        await using (var db = f.Contexts.CreateDbContext())
        {
            Assert.False(await db.WorkItems.AnyAsync(i => i.ExternalId == "sc-3"));
            Assert.Contains(await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Step).ToListAsync(),
                e => RecoveryRedirect.Parse(e.Detail)?.Item == "sc-3");
        }
        await using var recovery = Recovery(f);
        var spaces = new FakeWorkspaces();
        var worker = new FakeWorker(async call =>
        {
            Assert.Null(call.Resume); Assert.Contains("original spec", call.Prompt); Assert.Contains(Delivered, call.Prompt);
            await call.Callbacks.OnSession!("fresh-child", default); return Ok with { SessionId = "fresh-child" };
        });
        var result = await Run(f, recovery, 3, spaces, worker);
        Assert.Equal(WorkState.Review, result.State);
        Assert.Single(worker.Calls);
        await using var read = f.Contexts.CreateDbContext();
        var item = await read.WorkItems.SingleAsync(i => i.ExternalId == "sc-3");
        var rows = await read.LedgerEntries.Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync();
        Assert.True(rows.Single(e => e.Step == RunPipeline.Steps.Claimed).Id < rows.Single(e => e.Step == RecoveryRedirect.Started).Id);
        Assert.Contains($"corrected {Repo} {Delivered}", spaces.Calls);
    }

    private sealed class ContendedLocks : IRunLocks
    {
        private readonly InProcessRunLocks inner = new();
        public bool Refuse;
        public Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken ct) =>
            Refuse ? Task.FromResult<IAsyncDisposable?>(null) : inner.TryAcquireAsync(key, ct);
    }

    [Fact]
    public async Task Required_redirect_lock_contention_never_falls_through_to_the_old_attempt()
    {
        var f = await SeedSafe();
        var locks = new ContendedLocks { Refuse = true };
        await using var recovery = Recovery(f, locks);
        await Assert.ThrowsAsync<RecoveryContinuationDeferredException>(() => recovery.RedirectAsync("sc-2", default));
        var spaces = new FakeWorkspaces();
        var calls = 0;
        var result = await Run(f, recovery, 2, spaces, new FakeWorker(_ => { calls++; return Task.FromResult(Ok); }));
        Assert.Equal(WorkState.Paused, result.State);
        Assert.Equal(0, calls); Assert.Empty(spaces.Calls);
        locks.Refuse = false;
        Assert.NotNull(await recovery.RedirectAsync("sc-2", default));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("spec")]
    [InlineData("tip")]
    public async Task Slot_acquisition_revalidates_semantic_evidence_without_rejecting_its_own_bookkeeping(string change)
    {
        var f = await SeedSafe();
        await using var recovery = Recovery(f);
        var root = Directory.CreateTempSubdirectory("df-redirect-slot-").FullName;
        var slots = TestSlots.Pool(root, 1);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Task.Run(() => slots.HoldAsync<int>(null, async _ => { held.SetResult(); await release.Task; return 0; }, default));
        await held.Task;
        var spaces = new FakeWorkspaces(); var calls = 0;
        var run = Run(f, recovery, 2, spaces, new FakeWorker(async call =>
        {
            calls++; await call.Callbacks.OnSession!("fresh", default); return Ok with { SessionId = "fresh" };
        }), slots, new WaitLog(waited));
        try
        {
            await waited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (change == "spec")
            {
                var spec = await f.Source.ReadSpecAsync(2, default);
                f.Source.SpecFor = id => spec with { Story = spec.Story with { Id = id, Description = "changed during slot wait" } };
            }
            if (change == "tip") f.GitHub.Compare = _ => new("new-default-tip", 0);
        }
        finally { release.TrySetResult(); await blocker; }
        var result = await run;
        Assert.Equal(change == "none" ? WorkState.Review : WorkState.Implement, result.State);
        Assert.Equal(change == "none" ? 1 : 0, calls);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal(change == "none", await db.LedgerEntries.AnyAsync(e => e.Step == RecoveryRedirect.Started));
    }

    /// <summary>A recovery whose capture is counted: a disposition already acted on must need none.</summary>
    private static HotFileRecovery Counting(RecoverySnapshotTests.Fixture f, Action counted) =>
        new(f.Contexts, new InProcessRunLocks(), new FreezeOptions(), TimeProvider.System, new HotFileRecoveryTests.Assessor(),
            capture: (trigger, ct) => { counted(); return f.Evidence.CaptureAsync(trigger, ct); });

    private static async Task<HotFileIncident> IncidentAsync(RecoverySnapshotTests.Fixture f, long? id = null)
    {
        await using var db = f.Contexts.CreateDbContext();
        return await db.HotFileIncidents.AsNoTracking().OrderBy(i => i.Id).FirstAsync(i => id == null || i.Id == id);
    }

    [Fact]
    public async Task Real_capture_a_restarted_item_is_never_revalidated_when_the_base_moves_and_another_pending_item_advances()
    {
        var f = await SeedSafe(two: true);
        var captures = 0;
        await using var recovery = Counting(f, () => captures++);
        var calls = 0;
        Func<WorkerCall, Task<WorkerResult>> fresh = async call =>
        {
            calls++; await call.Callbacks.OnSession!("fresh-" + calls, default);
            return calls == 1 ? Ok with { SessionId = "fresh-1", ExitCode = 1, IsError = true, ResultText = "boom" } : Ok with { SessionId = "fresh-" + calls };
        };
        var spaces = new FakeWorkspaces { Head = () => f.GitHub.Head };
        // sc-2 restarts from its disposition (consumed), then its fresh attempt escalates.
        Assert.Equal(WorkState.Escalated, (await Run(f, recovery, 2, spaces, new FakeWorker(fresh))).State);
        await using (var db = f.Contexts.CreateDbContext())
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Started).ToListAsync());
        // Between the restart and sc-2's next dispatch: the delivery base moves and the other pending item advances.
        f.GitHub.Compare = _ => new("new-default-tip", 0);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var other = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-3");
            db.Add(new LedgerEntry { WorkItemId = other.Id, State = WorkState.Paused, Detail = RunPipeline.UserPaused, Outcome = StepOutcome.Deferred });
            other.Version++;
            await db.SaveChangesAsync();
        }
        var resolved = await IncidentAsync(f);
        // The evidence really did change: the safe answer's material is no longer what a capture sees.
        Assert.NotEqual(HotFileAssessment.FromDetail(resolved.Answer)!.Material, await f.Evidence.CaptureAsync(f.Trigger, default));
        captures = 0;

        Assert.NotNull(await recovery.RedirectAsync("sc-2", default));
        var result = await Run(f, recovery, 2, spaces, new FakeWorker(fresh));

        Assert.Equal(WorkState.Review, result.State);
        Assert.Equal(0, captures);
        var after = await IncidentAsync(f);
        Assert.Equal(("Resolved", 0, resolved.Version), (after.State, after.Refreshes, after.Version));
        Assert.Null(await recovery.HeldAsync(Repo, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_capture_a_newer_resolved_incident_never_reopens_the_older_one(bool consumed)
    {
        var f = await SeedSafe();
        var captures = 0;
        await using var recovery = Counting(f, () => captures++);
        var spaces = new FakeWorkspaces { Head = () => f.GitHub.Head };
        var calls = 0;
        Func<WorkerCall, Task<WorkerResult>> fresh = async call =>
        {
            calls++; await call.Callbacks.OnSession!("fresh-" + calls, default);
            return calls == 1 && consumed ? Ok with { SessionId = "fresh-1", ExitCode = 1, IsError = true, ResultText = "boom" } : Ok with { SessionId = "fresh-" + calls };
        };
        if (consumed) Assert.Equal(WorkState.Escalated, (await Run(f, recovery, 2, spaces, new FakeWorker(fresh))).State);
        long older;
        await using (var db = f.Contexts.CreateDbContext())
        {
            older = (await db.HotFileIncidents.SingleAsync()).Id;
            // A later generation of the repository was detected and resolved safe while sc-2 was in flight.
            var trigger = f.Trigger with { Id = "newer", Generation = 2 };
            db.Add(new HotFileIncident { Repo = Repo, Generation = 2, State = "Resolved", Snapshot = JsonSerializer.Serialize(trigger),
                EvidenceHash = trigger.EvidenceHash, ResolvedAt = DateTimeOffset.UtcNow, Version = 1 });
            await db.SaveChangesAsync();
        }
        f.GitHub.Compare = _ => new("new-default-tip", 0);
        var before = await IncidentAsync(f, older);
        captures = 0;

        Assert.NotNull(await recovery.RedirectAsync("sc-2", default));
        var result = await Run(f, recovery, 2, spaces, new FakeWorker(fresh));

        Assert.Equal(WorkState.Review, result.State);
        Assert.Equal(0, captures);
        var after = await IncidentAsync(f, older);
        Assert.Equal(("Resolved", before.Refreshes, before.Version), (after.State, after.Refreshes, after.Version));
        Assert.Null(await recovery.HeldAsync(Repo, default));
        await using (var db = f.Contexts.CreateDbContext())
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryRedirect.Started).ToListAsync());
    }

    [Fact]
    public async Task A_newer_unresolved_incident_defers_an_older_disposition_without_touching_either()
    {
        var f = await SeedSafe();
        var captures = 0;
        await using var recovery = Counting(f, () => captures++);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var trigger = f.Trigger with { Id = "newer", Generation = 2 };
            db.Add(new HotFileIncident { Repo = Repo, Generation = 2, State = "Held", Snapshot = JsonSerializer.Serialize(trigger),
                EvidenceHash = trigger.EvidenceHash, Version = 1 });
            await db.SaveChangesAsync();
        }
        var before = await IncidentAsync(f);
        await Assert.ThrowsAsync<RecoveryContinuationDeferredException>(() => recovery.RedirectAsync("sc-2", default));
        Assert.Equal(0, captures);
        var after = await IncidentAsync(f);
        Assert.Equal((before.State, before.Version), (after.State, after.Version));
    }

    [Theory]
    [InlineData(WorkState.Watch)]
    [InlineData(WorkState.Done)]
    [InlineData(WorkState.Merge)]
    public async Task A_disposition_the_item_outgrew_without_restarting_is_void_and_captures_nothing(WorkState state)
    {
        var f = await SeedSafe();
        var captures = 0;
        await using var recovery = Counting(f, () => captures++);
        await using (var db = f.Contexts.CreateDbContext())
        {
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-2")).State = state;
            await db.SaveChangesAsync();
        }
        f.GitHub.Compare = _ => new("new-default-tip", 0);
        var before = await IncidentAsync(f);
        Assert.Null(await recovery.RedirectAsync("sc-2", default));
        Assert.Equal(0, captures);
        Assert.Equal(before.Version, (await IncidentAsync(f)).Version);
    }

    [Fact]
    public async Task Redirect_recording_resolves_the_correction_once_asynchronously_not_per_merge_per_pending_item()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_redirect_queries");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var reads = new QueryReads();
        var options = new DbContextOptionsBuilder<LedgerDbContext>(LedgerDbContext.PostgresOptions(database.ConnectionString))
            .AddInterceptors(reads).Options;
        var f = new RecoverySnapshotTests.Fixture(options); await f.Seed();
        await using var db = f.Contexts.CreateDbContext();
        var trigger = f.Trigger with { Merges = Enumerable.Repeat(f.Trigger.Merges[0], 50).ToArray() };
        var incident = new HotFileIncident { Repo = Repo, Generation = 1, State = "Unsafe", Snapshot = JsonSerializer.Serialize(trigger), EvidenceHash = trigger.EvidenceHash };
        db.Add(incident); await db.SaveChangesAsync();
        var material = await f.Evidence.CaptureAsync(f.Trigger, default);
        reads.Clear();
        await HotFileRecovery.RecordRedirectsAsync(db, incident, new(trigger.Id, 1, Repo, "high", "unsafe", trigger.EvidenceHash) { Material = material }, DateTimeOffset.UtcNow, default);
        Assert.Empty(reads.Sync);
        Assert.True(reads.Async.Count <= 6, $"Expected bounded async queries, got {reads.Async.Count}");
        Assert.Single(reads.Async, q => q.StartsWith("SELECT w.\"Id\"\n") && q.Contains("Source"));
        Assert.Single(db.ChangeTracker.Entries<LedgerEntry>(), e => e.State == EntityState.Added && e.Entity.Step == RecoveryRedirect.Step);
    }

    [Fact]
    public async Task Latest_dispositions_are_scoped_and_reduced_in_SQL_before_history_is_materialized()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_redirect_scope");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var reads = new QueryReads();
        var options = new DbContextOptionsBuilder<LedgerDbContext>(LedgerDbContext.PostgresOptions(database.ConnectionString))
            .AddInterceptors(reads).Options;
        var f = new RecoverySnapshotTests.Fixture(options); await f.Seed();
        await using var db = f.Contexts.CreateDbContext();
        var incident = new HotFileIncident { Repo = Repo, Generation = 1, State = "Held", Snapshot = JsonSerializer.Serialize(f.Trigger), EvidenceHash = f.Trigger.EvidenceHash };
        db.Add(incident); await db.SaveChangesAsync();
        for (var n = 0; n < 30; n++)
        {
            Add(new(incident.Id + 100 + n, "sc-" + (200 + n), "another/repo", "main", "unrelated", "other generation"));
            Add(new(incident.Id, "sc-2", Repo, "main", "old-" + n, "superseded"));
        }
        Add(new(incident.Id, "sc-2", Repo, "main", Delivered, "latest"));
        await db.SaveChangesAsync();
        reads.Clear();
        var dispositions = await RecoveryRedirect.LatestAsync(db, incident, default);
        Assert.Equal(Delivered, Assert.Single(dispositions).Commit);
        var sql = Assert.Single(reads.Async);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("LIMIT", sql);
        // Exercise the production capture caller as well as the SQL helper.
        var snapshot = await f.Evidence.SnapshotAsync(f.Trigger, default);
        Assert.True(snapshot.Complete);
        Assert.Equal(Delivered, Assert.Single(snapshot.Evidence, e => e.Category == "disposition").Commit);
        void Add(RecoveryRedirect redirect) => db.Add(new LedgerEntry { WorkItemId = f.Trigger.Merges[0].Item,
            State = WorkState.Watch, Step = RecoveryRedirect.Step, Detail = JsonSerializer.Serialize(redirect), Outcome = StepOutcome.Deferred });
    }

    private sealed class QueryReads : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        internal readonly List<string> Sync = [];
        internal readonly List<string> Async = [];
        internal void Clear() { Sync.Clear(); Async.Clear(); }
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        { Sync.Add(command.CommandText); return result; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        { Async.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class WaitLog(TaskCompletionSource waited) : StringWriter
    {
        public override void WriteLine(string? value) { if (value?.Contains("waiting for") == true) waited.TrySetResult(); }
    }
}
