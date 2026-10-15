using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// Ledger behaviour that only a real Postgres shows: advisory run locks, optimistic
/// concurrency inside a transaction, and migrations. Needs the compose Postgres on localhost:5434.
/// </summary>
public sealed class LedgerPostgresTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;
    private string _cs = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_ledger");
        _cs = _db.ConnectionString;
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private LedgerDbContext Context() => new(LedgerDbContext.PostgresOptions(_cs));

    /// <summary>
    /// `factory work` and `factory run` (or two runs) may start together on an unmigrated ledger. EF Core alone
    /// lets both apply the same migration (42701 "already exists"); <see cref="LedgerMigrations"/> serialises them.
    /// </summary>
    [Fact]
    public async Task Processes_migrating_a_fresh_ledger_at_once_all_succeed()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var fresh = await TempPostgresDatabase.CreateAsync("df_migrate");
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => LedgerMigrations.MigrateAsync(fresh.ConnectionString, CancellationToken.None))));

            await using var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(fresh.ConnectionString));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
    }

    /// <summary>A session's taint (E4) is sticky in the database itself: racing writers both succeed, and no row can be changed, removed or truncated.</summary>
    [Fact]
    public async Task A_session_taint_cannot_be_updated_or_deleted_and_racing_taints_both_succeed()
    {
        await using (var a = Context())
        await using (var b = Context())
        {
            var item = await new WorkLedger(a, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-5", "t", "o/r", null, CancellationToken.None);
            await Task.WhenAll(
                new WorkLedger(a, TimeProvider.System).TaintSessionAsync(item, "sess-t", Taint.IssueText, CancellationToken.None),
                new WorkLedger(b, TimeProvider.System).TaintSessionAsync(null, "sess-t", "web:WebFetch", CancellationToken.None));
        }

        await using var db = Context();
        Assert.Single(await db.SessionTaints.ToListAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""UPDATE session_taints SET "Reason" = 'cleared'"""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM session_taints"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE session_taints"));
        Assert.Single(await db.SessionTaints.ToListAsync());
        await Assert.ThrowsAsync<SessionTaintedException>(() => new WorkLedger(db, TimeProvider.System).GrantPushAsync(["sess-t"], CancellationToken.None));
    }

    [Fact]
    public async Task Second_concurrent_run_of_an_item_exits_already_running_and_the_worker_runs_once()
    {
        var worker = new BlockingWorker();
        var prs = new CountingPullRequests();
        await using var dbA = Context();
        await using var dbB = Context();
        RunPipeline Pipeline(LedgerDbContext db) => new(new FakeWorkSource(new WorkStory(1, "Fix", "Fix it.", "bug", "https://app.shortcut.com/t/story/1", ItemNaming.Shortcut)), new WorkLedger(db, TimeProvider.System),
            new PostgresRunLocks(_cs), new Workspaces(), worker, prs, new RepoRef("acme", "widgets"), TextWriter.Null);

        var first = Task.Run(() => Pipeline(dbA).RunAsync(77, CancellationToken.None));
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await Pipeline(dbB).RunAsync(77, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(second.Succeeded);
        Assert.Equal("sc-77 is already running.", second.Error);
        worker.Release.SetResult();
        var outcome = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, prs.Opened);
        await using var check = Context();
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Review],
            await check.LedgerEntries.Where(e => e.Step == null).OrderBy(e => e.Id).Select(e => e.State).ToListAsync());

        // The lock is released with the run: the next run gets in and, re-reading the item under the
        // lock (dbB loaded it while the first run was still in Implement), finds it parked.
        var third = await Pipeline(dbB).RunAsync(77, CancellationToken.None);
        Assert.True(third.Succeeded, third.Error);
        Assert.Equal(WorkState.Review, third.State);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, prs.Opened);
    }

    [Fact]
    public async Task The_run_locks_held_are_read_without_taking_any_whoever_holds_them_and_a_dead_runs_is_gone()
    {
        // sc-25991: the dashboard asks which items' runs are alive — in its own process or another (a `factory run` beside
        // `factory work`: its own PostgresRunLocks, its own connections).
        await using var db = Context();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var one = await ledger.GetOrCreateAsync("shortcut", "sc-1", "One", "o/r", null, CancellationToken.None);
        var two = await ledger.GetOrCreateAsync("shortcut", "sc-2", "Two", "o/r", null, CancellationToken.None);
        var reader = new PostgresRunLocks(_cs);
        Assert.Empty(await reader.HeldAsync(CancellationToken.None));

        var running = await new PostgresRunLocks(_cs).TryAcquireAsync(one.Id, CancellationToken.None);
        // The merge queue's per-repository lock is no item's (a negative key), and the migration's is a two-key one.
        await using var queue = await new PostgresRunLocks(_cs).TryAcquireAsync(Gate.MergeQueue.LockKey(new RepoRef("o", "r")), CancellationToken.None);
        Assert.NotNull(queue);
        Assert.Equal([one.Id], await reader.HeldAsync(CancellationToken.None));
        // Asking took nothing: another run of sc-2 gets its lock, and sc-1's holder still has its own.
        await using (var other = await new PostgresRunLocks(_cs).TryAcquireAsync(two.Id, CancellationToken.None))
        {
            Assert.NotNull(other);
            Assert.Null(await new PostgresRunLocks(_cs).TryAcquireAsync(one.Id, CancellationToken.None));
            Assert.Equal([one.Id, two.Id], (await reader.HeldAsync(CancellationToken.None)).Order());
        }

        // The run ends (or its process dies: the session ends either way), and the item is no longer alive.
        await running!.DisposeAsync();
        Assert.Empty(await reader.HeldAsync(CancellationToken.None));

        // The dashboard's data over this ledger: the rows' word "holds" counts only under a live run.
        await ledger.RecordAsync(one, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.CheckpointAsync(one, RunPipeline.Steps.SlotHeld, null, SlotUse.Detail(1, "_factory", "implement"), CancellationToken.None);
        var data = new Dashboard.DashboardData(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), TimeProvider.System,
            liveRuns: reader.HeldAsync);
        var dead = (await data.ActiveItemsAsync(CancellationToken.None)).Single(r => r.ExternalId == "sc-1").Run!;
        Assert.Equal((false, false, true), (dead.Live, dead.Holds, dead.Stale));
        await using var again = await new PostgresRunLocks(_cs).TryAcquireAsync(one.Id, CancellationToken.None);
        var alive = (await data.ActiveItemsAsync(CancellationToken.None)).Single(r => r.ExternalId == "sc-1").Run!;
        Assert.Equal((true, true, "holds slot 1 (_factory) to implement"), (alive.Live, alive.Holds, alive.SlotText));
    }

    [Fact]
    public async Task The_dashboard_reads_a_merged_items_merge_time_from_its_merge_row_and_none_before_it_is_in_watch()
    {
        // sc-26134: a merged item's elapsed time stops at its merge and it offers no controls, decided from the ledger's rows.
        await using var db = Context();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "One", "o/r", null, CancellationToken.None);
        var data = new Dashboard.DashboardData(new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(_cs)), TimeProvider.System);
        foreach (var state in new[] { WorkState.Implement, WorkState.Review, WorkState.CI, WorkState.MergeGate, WorkState.Merge })
        {
            await ledger.RecordAsync(item, state, null, null, CancellationToken.None);
        }
        Assert.Null((await data.ActiveItemsAsync(CancellationToken.None)).Single(r => r.ExternalId == "sc-1").MergedAt);

        await ledger.RecordAsync(item, WorkState.Watch, null, null, CancellationToken.None);

        await using var check = Context();
        var merge = await check.LedgerEntries.SingleAsync(e => e.WorkItemId == item.Id && e.Step == null && e.State == WorkState.Merge);
        var row = (await data.ActiveItemsAsync(CancellationToken.None)).Single(r => r.ExternalId == "sc-1");
        Assert.Equal(merge.RecordedAt, row.MergedAt);
        Assert.True(row.Merged);
    }

    [Fact]
    public async Task A_sweep_leaves_what_another_processes_live_run_owns_and_deletes_it_once_that_run_is_gone()
    {
        // sc-25987: the live run is another process's — its own PostgresRunLocks, its own connection — and the sweeper's context
        // has never loaded the item.
        await using var dbA = Context();
        var item = await new WorkLedger(dbA, TimeProvider.System).GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", null, CancellationToken.None);
        var directory = Directory.CreateTempSubdirectory("df-mcp-live-").FullName;
        var file = Path.Combine(directory, $"sc-1.{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{}");
        await using var dbB = Context();
        var sweeper = new LiveRuns(new WorkLedger(dbB, TimeProvider.System), new PostgresRunLocks(_cs));
        var sc1 = WorkSources.ItemNaming.ParseAny("sc-1");

        await using (var running = await new PostgresRunLocks(_cs).TryAcquireAsync(item.Id, CancellationToken.None))
        {
            Assert.NotNull(running);
            Assert.Null(await sweeper.TryClaimAsync(sc1, CancellationToken.None));
            await Mcp.McpProxySessions.SweepAsync(directory, sweeper, CancellationToken.None);
            Assert.True(File.Exists(file));
        }

        // The run is gone: its leftovers are claimed under the item's run lock (no run of it starts meanwhile), then the lock is given back.
        await using (var claim = await sweeper.TryClaimAsync(sc1, CancellationToken.None))
        {
            Assert.True(claim!.UnderRunLock);
            Assert.Null(await new PostgresRunLocks(_cs).TryAcquireAsync(item.Id, CancellationToken.None));
        }
        await Mcp.McpProxySessions.SweepAsync(directory, sweeper, CancellationToken.None);
        Assert.False(File.Exists(file));
        await using var free = await new PostgresRunLocks(_cs).TryAcquireAsync(item.Id, CancellationToken.None);
        Assert.NotNull(free);
    }

    [Fact]
    public async Task Stale_writer_cannot_overwrite_another_runs_state_and_writes_nothing()
    {
        await using var dbA = Context();
        await using var dbB = Context();
        var a = new WorkLedger(dbA, TimeProvider.System);
        var b = new WorkLedger(dbB, TimeProvider.System);
        var itemA = await a.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", null, CancellationToken.None);
        var itemB = await b.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", null, CancellationToken.None);

        await a.RecordAsync(itemA, WorkState.Implement, null, null, CancellationToken.None);

        // B still believes the item is in Intake; its write must fail, not clobber A's Implement.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => b.RecordAsync(itemB, WorkState.Cancelled, null, "stale", CancellationToken.None));
        Assert.Equal(WorkState.Intake, itemB.State);
        await using var check = Context();
        Assert.Equal(WorkState.Implement, (await check.WorkItems.SingleAsync()).State);
        Assert.Equal([WorkState.Intake, WorkState.Implement], await check.LedgerEntries.OrderBy(e => e.Id).Select(e => e.State).ToListAsync());
    }

    [Fact]
    public async Task Rolling_back_the_lifecycle_migration_drops_checkpoint_rows_and_maps_states_to_failed()
    {
        await using (var db = Context())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var item = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Fix", "o/r", null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Implement, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Session, "s", null, CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerDone, "s", null, CancellationToken.None);
            await ledger.RecordAsync(item, WorkState.Escalated, "s", "boom", CancellationToken.None);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.EscalationComment, "s", "posted", CancellationToken.None);

            await db.GetService<IMigrator>().MigrateAsync("20261006173537_InitialLedger");
        }

        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await using var query = new NpgsqlCommand("""SELECT "State" FROM ledger_entries ORDER BY "Id" """, conn);
        var states = new List<string>();
        await using (var reader = await query.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                states.Add(reader.GetString(0));
            }
        }
        Assert.Equal(["Intake", "Implement", "Failed"], states);
    }

    /// <summary>
    /// sc-25871: the <c>PlanItems</c> migration. A story's plan link is a nullable reference to the plan's own work item; rows that
    /// existed before it have none; and the plan of a story is found from the plan item's <c>plan-stories</c> row in real SQL.
    /// </summary>
    [Fact]
    public async Task The_plan_items_migration_adds_a_nullable_plan_link_that_must_name_a_work_item_and_rolls_back()
    {
        long planId;
        await using (var db = Context())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            var plan = await ledger.GetOrCreateAsync("shortcut", "sc-1", "Plan", "o/r", null, CancellationToken.None);
            await ledger.RecordAsync(plan, WorkState.Plan, null, null, CancellationToken.None);
            await ledger.CheckpointAsync(plan, RunPipeline.Steps.PlanStories, null, "sc-20 sc-21", CancellationToken.None);
            await ledger.RecordAsync(plan, WorkState.Planned, null, "2 stories: sc-20 sc-21", CancellationToken.None);
            await ledger.GetOrCreateAsync("shortcut", "sc-20", "Story", "o/r", null, CancellationToken.None);
            planId = plan.Id;

            // Down drops the link and keeps the rows; up again gives every existing row no plan.
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261009011317_StepOutcomes");
            await migrator.MigrateAsync();
        }
        await using (var db = Context())
        {
            var ledger = new WorkLedger(db, TimeProvider.System);
            Assert.All(await db.WorkItems.AsNoTracking().ToListAsync(), i => Assert.Null(i.PlanItemId));
            Assert.Equal(planId, await ledger.PlanOfAsync("shortcut", "sc-20", CancellationToken.None));
            Assert.Equal(planId, await ledger.PlanOfAsync("shortcut", "sc-21", CancellationToken.None));
            Assert.Null(await ledger.PlanOfAsync("shortcut", "sc-2", CancellationToken.None)); // a prefix of a listed id
            Assert.Null(await ledger.PlanOfAsync("github", "sc-20", CancellationToken.None));

            var story = (await ledger.FindAsync("shortcut", "sc-20", CancellationToken.None))!;
            await ledger.LinkPlanAsync(story, planId, CancellationToken.None);
            var version = story.Version;
            await ledger.LinkPlanAsync(story, planId, CancellationToken.None); // already linked: nothing written
            Assert.Equal(version, story.Version);

            // The link must name a work item: one that does not exist is refused and nothing is left changed in memory.
            await Assert.ThrowsAsync<DbUpdateException>(() => ledger.LinkPlanAsync(story, planId + 1000, CancellationToken.None));
            Assert.Equal((planId, version), (story.PlanItemId, story.Version));
        }
        await using (var db = Context())
        {
            Assert.Equal(planId, (await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == "sc-20")).PlanItemId);
            var known = await new WorkLedger(db, TimeProvider.System).ItemsByIdAsync("shortcut", ["sc-1", "sc-20", "sc-99"], CancellationToken.None);
            Assert.Equal(["sc-1", "sc-20"], known.Keys.Order());
            Assert.Equal([null, RunPipeline.Steps.PlanStories, null], known["sc-1"].History.Skip(1).Select(r => r.Step));
        }
    }

    private sealed class Workspaces : IRepoWorkspace
    {
        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}");
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult(Ws(branch));
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult(Ws(branch));
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct, string? baseBranch = null) => Task.FromResult<Workspace?>(Ws(branch));
        public Task<BranchStart> BranchStartAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CreateBranchAsync(RepoRef repo, string branch, string sha, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct) => Task.FromResult(true);
        public Task<string> HeadAsync(Workspace workspace, CancellationToken ct) => Task.FromResult("head");
        public Task<Gate.BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingPullRequests : IPullRequests
    {
        public int Opened;
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct)
        {
            Interlocked.Increment(ref Opened);
            return Task.FromResult("https://github.com/acme/widgets/pull/1");
        }

        public Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Announces its session, then works until released.</summary>
    private sealed class BlockingWorker : IWorker
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId, string modelClass,
            WorkerCallbacks? callbacks, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await callbacks!.OnStarted!(1, ct);
            await callbacks.OnSession!("sess-1", ct);
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return new WorkerResult("sess-1", 0, false, "success", "done", "");
        }

        public Task<bool> StopOrphanAsync(int pid, CancellationToken ct) => Task.FromResult(false);
    }
}
