using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class RecoveryControlTests
{
    private const string A = "owner/alpha", B = "owner/beta";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class SafeAssessor : IHotFileAssessor
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Finish.Task.WaitAsync(ct);
            return new(snapshot.Id, snapshot.Generation, snapshot.Repo, "high", "safe", snapshot.EvidenceHash);
        }
    }

    private sealed class PausableWorker : IWorker
    {
        public readonly TaskCompletionSource Working = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, string modelClass, WorkerCallbacks? callbacks, CancellationToken ct)
        {
            await callbacks!.OnStarted!(WorkerPid, ct);
            await callbacks.OnSession!("recovery-worker", ct);
            Working.TrySetResult();
            await _pause.Task.WaitAsync(ct);
            return new("recovery-worker", 0, false, "success", "", "", WorkerResult.HookStoppedReason);
        }
        public void RequestPause(string workingDirectory) => _pause.TrySetResult();
        public void CancelPause(string workingDirectory) { }
        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Fixture
    {
        public readonly LedgerDbContextFactory Contexts;
        public Fixture(DbContextOptions<LedgerDbContext>? options = null) => Contexts = new(options ??
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        public readonly InProcessRunLocks Locks = new();
        public LedgerControls Controls => new(Contexts, Time);
        public ControlActions Actions => new(Controls, Contexts);
        public async Task<WorkItem> Item(int id = 77, string repo = A, WorkState state = WorkState.Intake, long? plan = null)
        {
            await using var db = Contexts.CreateDbContext();
            var item = new WorkItem { Source = "shortcut", ExternalId = $"sc-{id}", Title = "item", Repo = repo,
                State = state, PlanItemId = plan, EpicId = 42, CreatedAt = Time.GetUtcNow(), UpdatedAt = Time.GetUtcNow() };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            return item;
        }
        public async Task Hold(string repo = A)
        {
            await using var db = Contexts.CreateDbContext();
            var snapshot = new HotFileSnapshot(Guid.NewGuid().ToString(), repo, 1, [], "evidence");
            db.HotFileIncidents.Add(new() { Repo = repo, Generation = 1, State = "Held", EvidenceHash = snapshot.EvidenceHash,
                Snapshot = JsonSerializer.Serialize(snapshot), CreatedAt = Time.GetUtcNow(), Version = 1 });
            await db.SaveChangesAsync();
        }
        public async Task<RunOutcome> Run(FakeWorkSource source, IWorker? worker = null, HotFileRecovery? recovery = null)
        {
            await using var db = Contexts.CreateDbContext();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await new RunPipeline(source, new WorkLedger(db, Time), Locks, new FakeWorkspaces(),
                worker ?? new FakeWorker(Reports(Ok)), new FakePullRequests(), RepoRef.Parse(A), TextWriter.Null,
                controls: Controls, controlPollInterval: TimeSpan.FromMilliseconds(5), recovery: recovery)
                .RunAsync(77, timeout.Token);
        }
        public FakeWorkSource Source() => new(new WorkStory(77, "item", $"Repo: {A}", "feature", "", ItemNaming.Shortcut))
            { Epic = new WorkEpic(42, "epic", "", "") };
    }

    [Fact]
    public async Task Postgres_composes_incidents_with_manual_controls_and_an_explicit_operation_repository()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery_controls");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, Ct);
        var f = new Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString));
        await f.Item(repo: "OWNER/ALPHA");
        await f.Item(78, B);
        await f.Hold();
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
        Assert.Equal(ControlState.Running, await f.Controls.EffectiveAsync("sc-78", 42, Ct));
        // A waiting plan's operation may target another repository without replacing its human/epic/item controls.
        Assert.Equal(ControlState.Running, await f.Controls.EffectiveInRepositoryAsync("sc-77", 42, B, Ct));
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveInRepositoryAsync("sc-78", 42, A, Ct));
        await f.Actions.PauseAsync("epic:42", "human", Ct);
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveInRepositoryAsync("sc-77", 42, B, Ct));
        await f.Actions.StopAsync("item:sc-77", "human", Ct);
        Assert.Equal(ControlState.Stopping, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
    }

    [Fact]
    public async Task A_repository_hold_pauses_a_live_worker_and_leaves_an_unrelated_repository_running()
    {
        var f = new Fixture();
        var worker = new PausableWorker();
        var running = f.Run(f.Source(), worker);
        await worker.Working.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Hold();
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
        await f.Item(78, B);
        Assert.Equal(ControlState.Running, await f.Controls.EffectiveAsync("sc-78", 42, Ct));
    }

    [Fact]
    public async Task A_held_queued_PR_is_not_dispatched_but_its_claim_remains_renewable()
    {
        var f = new Fixture();
        await f.Item(state: WorkState.MergeGate);
        await f.Item(78, B, WorkState.MergeGate);
        await f.Hold();
        await using var db = f.Contexts.CreateDbContext();
        var ledger = new WorkLedger(db, f.Time);
        Assert.Equal([78], await RunPipeline.InFlightAsync(ledger, ItemNaming.Shortcut, Ct, f.Controls));
        Assert.Contains(77, await RunPipeline.RenewableAsync(ledger, ItemNaming.Shortcut, Ct, f.Controls));
        var source = f.Source();
        Assert.Equal(WorkState.MergeGate, (await f.Run(source)).State);
        Assert.Empty(source.Writes);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("repo:owner/alpha")]
    [InlineData("epic:42")]
    [InlineData("item:sc-77")]
    [InlineData("item:sc-500")]
    public async Task A_safe_result_racing_a_manual_pause_never_clears_or_bypasses_any_applicable_scope(string scope)
    {
        var f = new Fixture();
        var plan = await f.Item(500);
        await f.Item(plan: plan.Id);
        await f.Hold();
        await f.Controls.SetAsync(ControlScope.Repository(RepoRef.Parse(B)), ControlState.Paused, "other human", Ct);
        var before = await f.Controls.GetAsync(ControlScope.Repository(RepoRef.Parse(B)), Ct);
        var assessor = new SafeAssessor();
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, new FreezeOptions(), f.Time, assessor);
        var source = f.Source();
        var assessing = f.Run(source, recovery: recovery);
        await assessor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await f.Actions.PauseAsync(scope, "human", Ct)).Ok);
        assessor.Finish.TrySetResult();
        Assert.Equal(WorkState.Intake, (await assessing).State);
        Assert.Empty(source.Writes); // A fresh safe result is not a control authorization.
        Assert.Null(await recovery.HeldAsync(A, Ct));
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
        var row = await f.Controls.GetAsync(scope, Ct);
        Assert.Equal((ControlState.Paused, "human"), (row!.State, row.ChangedBy));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await f.Controls.GetAsync(before!.Scope, Ct)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fresh_item_in_a_held_repository_is_not_claimed(bool automatic)
    {
        var f = new Fixture();
        if (automatic) await f.Hold();
        else await f.Actions.PauseAsync(ControlScope.Repository(RepoRef.Parse(A)), "human", Ct);
        var source = f.Source();
        Assert.Equal(WorkState.Intake, (await f.Run(source)).State);
        Assert.Empty(source.Writes);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Empty(await db.WorkItems.ToListAsync());
    }

    [Fact]
    public async Task Held_issue_intake_preparation_does_not_run_its_triage_but_another_repositories_preparation_does()
    {
        var h = new IssueIntakeTests.Harness(IssueTriageTests.Answer(type: "question"));
        var other = RepoRef.Parse(B);
        h.Watched.Add(other);
        await h.Watch();
        await using (var db = h.Db())
        {
            db.HotFileIncidents.Add(new() { Repo = IssueTriageTests.Repo.Key, Generation = 1, State = "Held", Snapshot = "{}",
                EvidenceHash = "evidence", CreatedAt = h.Time.GetUtcNow(), Version = 1 });
            await db.SaveChangesAsync();
        }
        h.Open("visitor");
        h.GitHub.Open(other, 13, "Other question", "Question", "visitor");
        await h.Poll();
        var prompt = Assert.Single(h.Triage.Prompts);
        Assert.Contains("Other question", prompt);
        Assert.DoesNotContain("WordCount counts whitespace", prompt);
    }

    [Fact]
    public async Task Manual_continue_lifts_only_its_named_scope_and_not_the_incident()
    {
        var f = new Fixture();
        await f.Item();
        await f.Hold();
        var repo = ControlScope.Repository(RepoRef.Parse("OWNER/ALPHA"));
        Assert.Equal("repo:owner/alpha", repo);
        Assert.True(ControlScope.IsValid(repo));
        Assert.False(ControlScope.IsValid("repo:OWNER/ALPHA"));
        await f.Actions.PauseAsync(repo, "human", Ct);
        await f.Actions.PauseAsync(ControlScope.Factory, "human", Ct);
        await f.Actions.ContinueAsync(repo, "human", Ct);
        Assert.Equal(ControlState.Paused, (await f.Controls.GetAsync(ControlScope.Factory, Ct))!.State);
        await f.Actions.ContinueAsync(ControlScope.Factory, "human", Ct);
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
    }

    [Fact]
    public async Task A_two_repository_plan_releases_B_while_A_is_held_even_when_A_is_its_primary_repository()
    {
        var f = new Fixture();
        var source = f.Source();
        source.PlansItems = true;
        var plan = new Plan([new("a", "A child", "A", ["Works"], "feature", [], A),
            new("b", "B child", "B", ["Works"], "feature", [], B)], [A, B], "Independent children");
        var ids = await source.CreateChildrenAsync(77, plan.Children("sc-77", RepoRef.Parse(A)), Ct);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var ledger = new WorkLedger(db, f.Time);
            var item = await ledger.GetOrCreateAsync("shortcut", "sc-77", "plan", A, null, Ct, 42);
            await ledger.RecordAsync(item, WorkState.Plan, null, null, Ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Plan, null, plan.ToDetail(), Ct);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, null, string.Join(' ', ids.Select(i => $"sc-{i}")), Ct);
            await ledger.RecordAsync(item, WorkState.Planned, null, null, Ct);
        }
        await f.Hold();
        await using (var db = f.Contexts.CreateDbContext())
            Assert.Contains(77, await RunPipeline.InFlightAsync(new WorkLedger(db, f.Time), ItemNaming.Shortcut, Ct, f.Controls));
        Assert.Equal(WorkState.Planned, (await f.Run(source)).State);
        Assert.DoesNotContain($"ready {ids[0]}", source.Writes);
        Assert.Contains($"ready {ids[1]}", source.Writes);
    }

    [Fact]
    public async Task A_repository_Stop_selects_only_its_items_case_insensitively_even_while_held()
    {
        var f = new Fixture();
        await f.Item(repo: "OWNER/ALPHA");
        await f.Item(78, B);
        await f.Hold();
        Assert.True((await f.Actions.StopAsync(ControlScope.Repository(RepoRef.Parse(A)), "human", Ct)).Ok);
        Assert.Equal(ControlState.Stopping, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
        Assert.Equal(ControlState.Running, await f.Controls.EffectiveAsync("sc-78", 42, Ct));
        Assert.Null(await f.Controls.GetAsync("item:sc-78", Ct));
    }

    [Fact]
    public async Task Stop_during_assessment_finishes_bookkeeping_and_safe_resolution_never_requeues_the_item()
    {
        var f = new Fixture();
        await f.Item();
        await f.Hold();
        var assessor = new SafeAssessor();
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, new FreezeOptions(), f.Time, assessor);
        var assessing = recovery.AdvanceAsync(A, Ct);
        await assessor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var source = f.Source();
        try
        {
            await f.Actions.StopAsync("item:sc-77", "human", Ct);
            Assert.Equal(ControlState.Stopping, await f.Controls.EffectiveAsync("sc-77", 42, Ct));
            Assert.Equal(WorkState.Cancelled, (await f.Run(source, recovery: recovery)).State);
        }
        finally
        {
            assessor.Finish.TrySetResult();
        }
        await assessing;
        var writes = source.Writes.ToArray();
        Assert.Equal(WorkState.Cancelled, (await f.Run(source, recovery: recovery)).State);
        Assert.Equal(writes, source.Writes);
        Assert.Contains("state 77 Stopped", source.Writes);
        Assert.Null(await f.Controls.GetAsync("item:sc-77", Ct));
    }
}
