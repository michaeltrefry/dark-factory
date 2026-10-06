using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
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

    [Fact]
    public async Task Second_concurrent_run_of_an_item_exits_already_running_and_the_worker_runs_once()
    {
        var worker = new BlockingWorker();
        var prs = new CountingPullRequests();
        await using var dbA = Context();
        await using var dbB = Context();
        RunPipeline Pipeline(LedgerDbContext db) => new(new FakeWorkSource(new WorkStory(1, "Fix", "Fix it.", "bug", "https://app.shortcut.com/t/story/1")), new WorkLedger(db, TimeProvider.System),
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

    private sealed class Workspaces : IRepoWorkspace
    {
        private static Workspace Ws(string branch) => new($"/wt/{branch}", branch, "main", $"/clone/.git/worktrees/{branch}");
        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult(Ws(branch));
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult(Ws(branch));
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct) => Task.FromResult<Workspace?>(Ws(branch));
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, CancellationToken ct) => Task.FromResult(true);
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
    }

    /// <summary>Announces its session, then works until released.</summary>
    private sealed class BlockingWorker : IWorker
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerResult> RunAsync(string workingDirectory, string prompt, string? resumeSessionId,
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
