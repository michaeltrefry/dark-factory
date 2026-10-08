using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The factory-wide automatic freeze (sc-25387): each trigger, seeded in an in-memory ledger, stops the next dispatch with the
/// trigger named; an unreadable freeze record counts as frozen; a human's Continue clears it and only later events freeze again.
/// </summary>
public class FreezeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkStory Story = new(77, "Whitespace counts as a word", "WordCount(\"  \") returns 1.", "bug", "https://app.shortcut.com/trefry/story/77");
    private const string Repo = "michaeltrefry/dark-factory-sandbox";

    /// <summary>The base branch's CI as the main-red trigger reads it: the tip after a merge, and that tip's checks.</summary>
    private sealed class MainCi : IGateGitHub
    {
        public string Tip { get; set; } = "tip0000000001";
        public CiFacts? Ci { get; set; }
        public Exception? Throws { get; set; }
        public List<string> Calls { get; } = [];

        public Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct)
        {
            Calls.Add($"compare {repo.FullName} {baseRef} {headSha}");
            return Throws is { } ex ? Task.FromException<BaseComparison>(ex) : Task.FromResult(new BaseComparison(Tip, 0));
        }

        public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct)
        {
            Calls.Add($"ci {sha}");
            return Task.FromResult(Ci ?? new CiFacts(sha, [new CheckFact("build-test", true, "success")]));
        }

        public Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) => throw new NotSupportedException();
        public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct) => throw new NotSupportedException();
        public Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Controls whose freeze record cannot be read (every other scope reads normally).</summary>
    private sealed class UnreadableFreeze(IControls inner) : IControls
    {
        private static Exception Broken() => new InvalidOperationException("Cannot convert 'Bogus' to ControlState");

        public Task<Control?> GetAsync(string scope, CancellationToken ct) =>
            scope == ControlScope.Freeze ? Task.FromException<Control?>(Broken()) : inner.GetAsync(scope, ct);
        public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct) => inner.EffectiveAsync(externalId, epicId, ct);
        public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<Control?> UsagePauseAsync(CancellationToken ct) => inner.UsagePauseAsync(ct);
        public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) => inner.PauseForUsageAsync(resumeAt, reason, ct);
        public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) =>
            inner.FreezeAsync(trigger, detail, readChangedAt, ct);
        public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => inner.SetAsync(scope, state, by, ct);
        public Task ClearAsync(string scope, CancellationToken ct) => inner.ClearAsync(scope, ct);
    }

    private sealed class H
    {
        private readonly DbContextOptions<LedgerDbContext> _options =
            new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private int _items;

        public H() => Db = new LedgerDbContext(_options);

        public FakeTimeProvider Time { get; } = new(T0);
        public LedgerDbContext Db { get; }
        public LedgerDbContextFactory Contexts => new(_options);
        public IControls? Controls { get; set; }
        public IControls Ledger => new LedgerControls(Contexts, Time);
        public MainCi GitHub { get; } = new();
        public FreezeOptions Options { get; set; } = new();
        public FakeWorker Worker { get; } = new(Reports(Ok));

        public FactoryFreeze Freeze => new(Contexts, Controls ?? Ledger, Options, Time, GitHub);

        public RunPipeline Pipeline() =>
            new(new FakeWorkSource(Story), new WorkLedger(Db, Time), new InProcessRunLocks(), new FakeWorkspaces(), Worker, new FakePullRequests(),
                Sandbox, TextWriter.Null, controls: Controls ?? Ledger, freeze: Freeze);

        public Task<RunOutcome> Dispatch() => Pipeline().RunAsync(77, CancellationToken.None);

        public Task<Control?> FreezeRow() => Ledger.GetAsync(ControlScope.Freeze, CancellationToken.None);

        /// <summary>Adds an item with the given rows, one second apart on the clock: (state, step, detail); step null = a transition.</summary>
        public async Task<WorkItem> Item(WorkState final, params (WorkState State, string? Step, string? Detail)[] rows)
        {
            var item = new WorkItem
            {
                Source = RunPipeline.Source, ExternalId = $"sc-{500 + ++_items}", Title = "seeded", Repo = Repo, State = final,
                CreatedAt = Time.GetUtcNow(), UpdatedAt = Time.GetUtcNow(),
            };
            Db.WorkItems.Add(item);
            await Db.SaveChangesAsync();
            foreach (var (state, step, detail) in rows)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                Db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = state, Step = step, Detail = detail, RecordedAt = Time.GetUtcNow() });
                await Db.SaveChangesAsync();
            }
            return item;
        }

        public async Task Session(WorkItem item, decimal? cost)
        {
            Time.Advance(TimeSpan.FromSeconds(1));
            Db.WorkerSessions.Add(new WorkerSession { WorkItemId = item.Id, StartedAt = Time.GetUtcNow(), EndedAt = Time.GetUtcNow(), CostUsd = cost });
            await Db.SaveChangesAsync();
        }

        public Task Escalated() => Item(WorkState.Escalated, (WorkState.Intake, null, null), (WorkState.Implement, null, null), (WorkState.Escalated, null, "boom"));

        public Task Merged(string commit, params string[] files) => Item(WorkState.Watch,
            (WorkState.MergeGate, RunPipeline.Steps.MergeFiles, new MergeFiles("main", files).ToDetail()),
            (WorkState.Merge, null, commit),
            (WorkState.Watch, null, commit));

        /// <summary>An item in Review after its implement round and two fix rounds, each round's worker session costing as given.</summary>
        public async Task Rounds(params decimal?[] costs)
        {
            var item = await Item(WorkState.Review, (WorkState.Intake, null, null), (WorkState.Implement, null, null));
            await Session(item, costs[0]);
            for (var i = 1; i < costs.Length; i++)
            {
                await Row(item, WorkState.Review);
                await Row(item, WorkState.Fixing);
                await Session(item, costs[i]);
            }
            await Row(item, WorkState.Review);
        }

        private async Task Row(WorkItem item, WorkState state)
        {
            Time.Advance(TimeSpan.FromSeconds(1));
            Db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = state, RecordedAt = Time.GetUtcNow() });
            await Db.SaveChangesAsync();
        }
    }

    /// <summary>Seeds a trigger's evidence: at its threshold (<paramref name="holds"/>) or just short of it.</summary>
    private static async Task Seed(H h, string trigger, bool holds)
    {
        switch (trigger)
        {
            case FreezeTrigger.ConsecutiveFailures:
                for (var i = 0; i < (holds ? 3 : 2); i++)
                {
                    await h.Escalated();
                }
                break;
            case FreezeTrigger.HotFile:
                for (var i = 0; i < (holds ? 3 : 2); i++)
                {
                    await h.Merged($"merge{i}aaaaaaaaaa", "src/hot.cs", $"src/own{i}.cs");
                }
                break;
            case FreezeTrigger.CostRising:
                await (holds ? h.Rounds(0.40m, 0.55m, 0.80m) : h.Rounds(0.40m, 0.30m, 0.55m));
                break;
            case FreezeTrigger.MainRed:
                await h.Merged("mergeAaaaaaaaaaa", "src/a.cs");
                h.GitHub.Ci = new CiFacts(h.GitHub.Tip, [new CheckFact("build-test", true, holds ? "failure" : "success")]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger));
        }
    }

    public static TheoryData<string> Triggers => [FreezeTrigger.ConsecutiveFailures, FreezeTrigger.HotFile, FreezeTrigger.CostRising, FreezeTrigger.MainRed];

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task Each_trigger_seeded_in_the_ledger_stops_dispatch_with_that_trigger_named(string trigger)
    {
        var h = new H();
        await Seed(h, trigger, holds: true);

        var outcome = await h.Dispatch();

        Assert.NotNull(outcome.Deferred);
        Assert.StartsWith($"factory frozen ({trigger}): ", outcome.Deferred);
        Assert.False(outcome.Succeeded);
        Assert.Empty(h.Worker.Calls);
        Assert.False(await h.Db.WorkItems.AnyAsync(i => i.ExternalId == "sc-77")); // not even taken into the ledger
        var row = await h.FreezeRow();
        Assert.Equal((ControlState.Paused, trigger, FreezeTrigger.By), (row!.State, row.Reason, row.ChangedBy));
        Assert.False(string.IsNullOrWhiteSpace(row.Detail));
        // It holds for the next dispatch too, from the record alone.
        Assert.StartsWith($"factory frozen ({trigger}): ", (await h.Dispatch()).Deferred);
        Assert.Empty(h.Worker.Calls);
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public async Task Each_trigger_short_of_its_threshold_lets_the_dispatch_run(string trigger)
    {
        var h = new H();
        await Seed(h, trigger, holds: false);

        var outcome = await h.Dispatch();

        Assert.Null(outcome.Deferred);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Single(h.Worker.Calls);
        Assert.Null(await h.FreezeRow());
    }

    [Fact]
    public async Task Rounds_whose_cost_is_not_recorded_yet_are_no_evidence_and_a_merge_ends_a_failure_streak()
    {
        var h = new H();
        await h.Rounds(0.40m, 0.55m, null);
        await h.Escalated();
        await h.Escalated();
        await h.Merged("merge1aaaaaaaaaa", "src/a.cs");
        await h.Escalated();
        h.GitHub.Ci = new CiFacts(h.GitHub.Tip, [new CheckFact("build-test", false, null)]); // main's CI still running: not red

        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_hot_file_counts_only_merges_within_the_window()
    {
        var h = new H();
        await Seed(h, FreezeTrigger.HotFile, holds: true);
        h.Time.Advance(TimeSpan.FromHours(23));
        Assert.True((await new FactoryFreeze(h.Contexts, h.Ledger, new FreezeOptions(), h.Time).CheckAsync(CancellationToken.None)).Frozen);

        var later = new H();
        await Seed(later, FreezeTrigger.HotFile, holds: true);
        later.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(FreezeStatus.Clear, await new FactoryFreeze(later.Contexts, later.Ledger, new FreezeOptions(), later.Time).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_freeze_record_counts_as_frozen()
    {
        var h = new H();
        h.Controls = new UnreadableFreeze(h.Ledger);

        var outcome = await h.Dispatch();

        Assert.StartsWith($"factory frozen ({FreezeTrigger.RecordUnreadable}): ", outcome.Deferred);
        Assert.Empty(h.Worker.Calls);
        Assert.False(await h.Db.WorkItems.AnyAsync());
    }

    [Fact]
    public async Task The_intake_loop_dispatches_nothing_while_the_freeze_record_is_unreadable_or_set()
    {
        var h = new H();
        var runner = new CountingRunner();
        var status = new IntakeStatus(h.Time);
        var unreadable = new IntakeLoop(new Ready(1), runner, new IntakeOptions(TimeSpan.FromMinutes(1)), h.Time,
            NullLogger<IntakeLoop>.Instance, new UnreadableFreeze(h.Ledger), status: status);

        await unreadable.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, runner.Runs);
        Assert.Contains("Poll failed", status.FactoryError!.Message);

        await h.Ledger.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
        var frozen = new IntakeLoop(new Ready(1), runner, new IntakeOptions(TimeSpan.FromMinutes(1)), h.Time,
            NullLogger<IntakeLoop>.Instance, h.Ledger, status: status);
        await frozen.PollOnceAsync(CancellationToken.None);
        Assert.Equal(0, runner.Runs); // no ready story is even listed

        await new ControlActions(h.Ledger, h.Contexts).ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None);
        await frozen.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task A_deferred_run_ends_the_poll_and_shows_on_the_dashboard_status()
    {
        var h = new H();
        var status = new IntakeStatus(h.Time);
        var runner = new CountingRunner { Deferred = "factory frozen (main-red): main is red" };
        var loop = new IntakeLoop(new Ready(1, 2), runner, new IntakeOptions(TimeSpan.FromMinutes(1)), h.Time, NullLogger<IntakeLoop>.Instance,
            h.Ledger, status: status);

        await loop.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.Runs); // the second item is not dispatched into the same freeze
        Assert.Equal("sc-1 deferred: factory frozen (main-red): main is red", status.DeferredRun!.Message);
        runner.Deferred = null;
        await loop.PollOnceAsync(CancellationToken.None);
        Assert.Null(status.DeferredRun);
    }

    [Fact]
    public async Task A_trigger_that_cannot_be_checked_defers_the_dispatch_without_recording_a_freeze()
    {
        var h = new H();
        await h.Merged("mergeAaaaaaaaaaa", "src/a.cs");
        h.GitHub.Throws = new HttpRequestException("GitHub is down");

        var outcome = await h.Dispatch();

        Assert.StartsWith($"factory frozen ({FreezeTrigger.CheckFailed}): ", outcome.Deferred);
        Assert.Contains("GitHub is down", outcome.Deferred);
        Assert.Empty(h.Worker.Calls);
        Assert.Null(await h.FreezeRow());

        h.GitHub.Throws = null; // readable and green again: the dispatch goes on
        Assert.True((await h.Dispatch()).Succeeded);
    }

    [Fact]
    public async Task Continue_clears_the_freeze_for_good_on_the_evidence_it_acknowledged_and_a_new_occurrence_freezes_again()
    {
        var h = new H();
        await Seed(h, FreezeTrigger.ConsecutiveFailures, holds: true);
        Assert.NotNull((await h.Dispatch()).Deferred);

        // The trigger still holds on the same evidence, but a human's Continue is not undone by it.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var cleared = await new ControlActions(h.Ledger, h.Contexts).ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None);
        Assert.True(cleared.Ok, cleared.Message);
        Assert.True((await h.Dispatch()).Succeeded);
        Assert.True((await h.Dispatch()).Succeeded); // and stays cleared, dispatch after dispatch
        Assert.Equal((ControlState.Running, "tester"), ((await h.FreezeRow())!.State, (await h.FreezeRow())!.ChangedBy));

        // A new occurrence after the Continue: the count starts again from it.
        await h.Escalated();
        await h.Escalated();
        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
        await h.Escalated();
        Assert.StartsWith($"factory frozen ({FreezeTrigger.ConsecutiveFailures}): 3 items", (await h.Dispatch()).Deferred);
        Assert.Equal(ControlState.Paused, (await h.FreezeRow())!.State);
    }

    [Fact]
    public async Task A_freeze_decided_on_a_row_a_human_has_since_continued_does_not_overwrite_the_continue()
    {
        var h = new H();
        var before = await h.Ledger.FreezeAsync(FreezeTrigger.HotFile, "hot", null, CancellationToken.None);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Ledger.SetAsync(ControlScope.Freeze, ControlState.Running, "tester", CancellationToken.None);

        var stored = await h.Ledger.FreezeAsync(FreezeTrigger.HotFile, "hot again", before.ChangedAt, CancellationToken.None);

        Assert.Equal((ControlState.Running, "tester"), (stored.State, stored.ChangedBy));
        Assert.Equal(ControlState.Running, (await h.FreezeRow())!.State);
    }

    [Fact]
    public async Task The_freeze_is_set_only_by_the_factory_a_human_can_only_continue_it()
    {
        var h = new H();
        var actions = new ControlActions(h.Ledger, h.Contexts);
        Assert.False((await actions.PauseAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);
        Assert.False((await actions.StopAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);
        Assert.Null(await h.FreezeRow());
    }

    [Fact]
    public async Task A_frozen_factory_pauses_every_item_through_its_controls()
    {
        var h = new H();
        Assert.Equal(ControlState.Running, await h.Ledger.EffectiveAsync("sc-1", 5, CancellationToken.None));
        await h.Ledger.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
        Assert.Equal(ControlState.Paused, await h.Ledger.EffectiveAsync("sc-1", 5, CancellationToken.None));
    }

    // E6: every Freeze:* value is read, and changing it changes what freezes.
    [Theory]
    [InlineData("Freeze:MaxConsecutiveFailures", "2", FreezeTrigger.ConsecutiveFailures)]
    [InlineData("Freeze:HotFileMerges", "2", FreezeTrigger.HotFile)]
    [InlineData("Freeze:CostRisingRounds", "1", FreezeTrigger.CostRising)]
    public async Task Each_threshold_comes_from_config(string key, string value, string trigger)
    {
        var h = new H();
        await Seed(h, trigger, holds: false); // short of the default threshold
        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));

        h.Options = Options((key, value)).Freeze;

        Assert.Equal(trigger, (await h.Freeze.CheckAsync(CancellationToken.None)).Trigger);
    }

    [Fact]
    public async Task The_hot_file_window_comes_from_config()
    {
        var h = new H();
        await Seed(h, FreezeTrigger.HotFile, holds: true);
        h.Time.Advance(TimeSpan.FromHours(2));
        h.Options = Options(("Freeze:HotFileWindowHours", "1")).Freeze;
        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
        h.Options = Options(("Freeze:HotFileWindowHours", "3")).Freeze;
        Assert.Equal(FreezeTrigger.HotFile, (await h.Freeze.CheckAsync(CancellationToken.None)).Trigger);
    }

    [Fact]
    public void Freeze_thresholds_default_and_out_of_range_values_are_refused()
    {
        Assert.Equal(new FreezeOptions(), Options().Freeze);
        Assert.Equal((3, 3, TimeSpan.FromHours(24), 2),
            (Options().Freeze.MaxConsecutiveFailures, Options().Freeze.HotFileMerges, Options().Freeze.HotFileWindow, Options().Freeze.CostRisingRounds));
        Assert.Throws<InvalidOperationException>(() => Options(("Freeze:MaxConsecutiveFailures", "0")).Freeze);
        Assert.Throws<InvalidOperationException>(() => Options(("Freeze:HotFileMerges", "1")).Freeze);
        Assert.Throws<InvalidOperationException>(() => Options(("Freeze:HotFileWindowHours", "0")).Freeze);
        Assert.Throws<InvalidOperationException>(() => Options(("Freeze:CostRisingRounds", "0")).Freeze);
    }

    [Fact]
    public async Task A_freeze_record_postgres_cannot_read_counts_as_frozen()
    {
        await using var temp = await TempPostgresDatabase.CreateAsync("df_freeze");
        await LedgerMigrations.MigrateAsync(temp.ConnectionString, CancellationToken.None);
        var contexts = new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(temp.ConnectionString));
        var controls = new LedgerControls(contexts, TimeProvider.System);
        await controls.FreezeAsync(FreezeTrigger.MainRed, "main is red", null, CancellationToken.None);
        await using (var conn = new NpgsqlConnection(temp.ConnectionString))
        {
            await conn.OpenAsync();
            await using var corrupt = new NpgsqlCommand("""UPDATE controls SET "State" = 'Bogus' WHERE "Scope" = 'freeze'""", conn);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var status = await new FactoryFreeze(contexts, controls, new FreezeOptions(), TimeProvider.System).CheckAsync(CancellationToken.None);

        Assert.True(status.Frozen);
        Assert.Equal(FreezeTrigger.RecordUnreadable, status.Trigger);
    }

    private static FactoryOptions Options(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build(),
            new InMemorySecrets());

    private sealed class CountingRunner : IItemRunner
    {
        public int Runs { get; private set; }
        public string? Deferred { get; set; }

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);

        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new RunOutcome(0, WorkState.Intake, null, null, Deferred) { Deferred = Deferred });
        }
    }

    /// <summary>A board listing the given ready stories; the runner under test never reads it otherwise.</summary>
    private sealed class Ready(params int[] ids) : IWorkSource
    {
        public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(ids);
        public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> InScopeAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateScopeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ReleaseAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => throw new NotSupportedException();
        public Task CommentAsync(int id, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
