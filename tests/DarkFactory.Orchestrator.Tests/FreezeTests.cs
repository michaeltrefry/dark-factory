using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
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
        public Task<WorkItem> Item(WorkState final, params (WorkState State, string? Step, string? Detail)[] rows) => ItemIn(Repo, final, rows);

        public async Task<WorkItem> ItemIn(string repo, WorkState final, params (WorkState State, string? Step, string? Detail)[] rows)
        {
            var item = new WorkItem
            {
                Source = RunPipeline.Source, ExternalId = $"sc-{500 + ++_items}", Title = "seeded", Repo = repo, State = final,
                CreatedAt = Time.GetUtcNow(), UpdatedAt = Time.GetUtcNow(),
            };
            Db.WorkItems.Add(item);
            await Db.SaveChangesAsync();
            foreach (var (state, step, detail) in rows)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                Db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = state, Step = step, Detail = detail, RecordedAt = Time.GetUtcNow(), Outcome = StepOutcomes.Of(null, state, step, detail) });
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

        public Task Merged(string commit, params string[] files) => MergedIn(Repo, commit, files);

        public Task MergedIn(string repo, string commit, params string[] files) => ItemIn(repo, WorkState.Watch,
            (WorkState.MergeGate, RunPipeline.Steps.MergeFiles, new MergeFiles("main", files).ToDetail()),
            (WorkState.Merge, null, commit),
            (WorkState.Watch, null, commit));

        /// <summary>An item in Review after its implement round and two fix rounds, each round's worker session costing as given.</summary>
        public Task Rounds(params decimal?[] costs) => RoundsThen(null, costs);

        /// <summary><see cref="Rounds"/>, then (when set) the item merged: Review → CI → MergeGate → Merge → Watch.</summary>
        public async Task RoundsThen(string? mergedAs, params decimal?[] costs)
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
            if (mergedAs is not null)
            {
                foreach (var state in new[] { WorkState.CI, WorkState.MergeGate, WorkState.Merge, WorkState.Watch })
                {
                    await Row(item, state);
                }
                item.State = WorkState.Watch;
                await Db.SaveChangesAsync();
            }
        }

        private async Task Row(WorkItem item, WorkState state)
        {
            Time.Advance(TimeSpan.FromSeconds(1));
            Db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = state, RecordedAt = Time.GetUtcNow(), Outcome = StepOutcomes.Of(null, state, null, null) });
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

    [Fact]
    public async Task One_item_escalated_again_and_again_is_not_a_streak_of_items()
    {
        var h = new H();
        await h.Item(WorkState.Escalated,
            (WorkState.Intake, null, null), (WorkState.Implement, null, null), (WorkState.Escalated, null, "boom 1"),
            (WorkState.Intake, null, null), (WorkState.Implement, null, null), (WorkState.Escalated, null, "boom 2"),
            (WorkState.Intake, null, null), (WorkState.Implement, null, null), (WorkState.Escalated, null, "boom 3"));

        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task One_file_path_merged_in_two_repos_is_counted_per_repo()
    {
        var h = new H();
        await h.MergedIn(Repo, "merge1aaaaaaaaaa", "src/hot.cs");
        await h.MergedIn(Repo, "merge2aaaaaaaaaa", "src/hot.cs");
        await h.MergedIn("michaeltrefry/other-repo", "merge3aaaaaaaaaa", "src/hot.cs");

        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_merged_item_whose_rounds_cost_more_each_time_does_not_freeze_no_round_can_follow()
    {
        var h = new H();
        await h.RoundsThen("merged", 0.40m, 0.55m, 0.80m);

        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Main_red_is_a_state_a_continue_acknowledges_only_the_red_tip_it_saw()
    {
        var h = new H();
        await Seed(h, FreezeTrigger.MainRed, holds: true);
        Assert.Equal(FreezeTrigger.MainRed, (await h.Freeze.CheckAsync(CancellationToken.None)).Trigger);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await new ControlActions(h.Ledger, h.Contexts).ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);

        // Main is still red at the tip the Continue saw: cleared.
        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));

        // A human commit moves main, still red, with no factory merge since the Continue: frozen again.
        h.GitHub.Tip = "tip0000000002";
        h.GitHub.Ci = new CiFacts(h.GitHub.Tip, [new CheckFact("build-test", true, "failure")]);
        var again = await h.Freeze.CheckAsync(CancellationToken.None);
        Assert.Equal(FreezeTrigger.MainRed, again.Trigger);
        Assert.Contains("red at tip00000000", again.Detail);
        Assert.Equal((ControlState.Paused, FreezeTrigger.MainRed), ((await h.FreezeRow())!.State, (await h.FreezeRow())!.Reason));
    }

    [Fact]
    public async Task A_red_tip_a_continue_acknowledged_stays_acknowledged_through_a_later_freeze_of_another_trigger()
    {
        var h = new H();
        await Seed(h, FreezeTrigger.MainRed, holds: true);
        var actions = new ControlActions(h.Ledger, h.Contexts);
        Assert.Equal(FreezeTrigger.MainRed, (await h.Freeze.CheckAsync(CancellationToken.None)).Trigger);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await actions.ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);

        await Seed(h, FreezeTrigger.ConsecutiveFailures, holds: true);
        Assert.Equal(FreezeTrigger.ConsecutiveFailures, (await h.Freeze.CheckAsync(CancellationToken.None)).Trigger);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await actions.ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);

        // Main is still red at the same tip the first Continue acknowledged.
        Assert.Equal(FreezeStatus.Clear, await h.Freeze.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Continue_on_a_factory_that_is_not_frozen_is_refused_and_keeps_the_evidence_count()
    {
        var h = new H();
        var actions = new ControlActions(h.Ledger, h.Contexts);

        var none = await actions.ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None);
        Assert.False(none.Ok);
        Assert.Equal("the factory is not frozen", none.Message);
        Assert.Null(await h.FreezeRow());

        await h.Ledger.FreezeAsync(FreezeTrigger.HotFile, "hot", null, CancellationToken.None);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True((await actions.ContinueAsync(ControlScope.Freeze, "tester", CancellationToken.None)).Ok);
        var continued = (await h.FreezeRow())!.ChangedAt;
        h.Time.Advance(TimeSpan.FromMinutes(1));

        var running = await actions.ContinueAsync(ControlScope.Freeze, "other", CancellationToken.None);
        Assert.False(running.Ok);
        Assert.Equal((continued, "tester"), ((await h.FreezeRow())!.ChangedAt, (await h.FreezeRow())!.ChangedBy));
    }

    public static TheoryData<string> Unwritten => ["trigger-holds", "github-down"];

    [Theory]
    [MemberData(nameof(Unwritten))]
    public async Task The_intake_loop_runs_no_triage_while_a_trigger_holds_or_cannot_be_checked_before_any_freeze_is_written(string why)
    {
        var h = new H();
        if (why == "trigger-holds")
        {
            await Seed(h, FreezeTrigger.ConsecutiveFailures, holds: true);
        }
        else
        {
            await h.Merged("mergeAaaaaaaaaaa", "src/a.cs");
            h.GitHub.Throws = new HttpRequestException("GitHub is down");
        }
        Assert.Null(await h.FreezeRow());
        var runner = new CountingRunner();
        var status = new IntakeStatus(h.Time);
        var prepares = 0;
        var issues = new IntakeLane(new Ready(2), runner, _ => { prepares++; return Task.CompletedTask; });
        var loop = new IntakeLoop(new Ready(1), runner, new IntakeOptions(TimeSpan.FromMinutes(1)), h.Time, NullLogger<IntakeLoop>.Instance,
            h.Ledger, status: status, moreLanes: [issues], freeze: ct => h.Freeze.CheckAsync(ct));

        await loop.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, prepares);
        Assert.Equal(0, runner.Runs);
        Assert.StartsWith("intake deferred: factory frozen (", status.DeferredRun!.Message);
        Assert.Contains(why == "trigger-holds" ? FreezeTrigger.ConsecutiveFailures : FreezeTrigger.CheckFailed, status.DeferredRun.Message);
    }

    /// <summary>Seeds <paramref name="count"/> other items, each escalated once (the consecutive-failures trigger's evidence).</summary>
    private static void SeedEscalations(LedgerDbContextFactory contexts, int count)
    {
        using var db = contexts.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var item = new WorkItem
            {
                Source = RunPipeline.Source, ExternalId = $"sc-{900 + i}", Title = "seeded", Repo = Repo, State = WorkState.Escalated,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkItems.Add(item);
            db.SaveChanges();
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = WorkState.Escalated, Detail = "boom", RecordedAt = DateTimeOffset.UtcNow, Outcome = StepOutcome.Escalated });
            db.SaveChanges();
        }
    }

    /// <summary>A worker session that records its router cost the way the session recorder would, inside the round that started it.</summary>
    private static Func<WorkerCall, Task<WorkerResult>> Costs(GatePipelineTests.Harness h, decimal cost) => async call =>
    {
        await Task.Delay(5);
        await using (var db = h.Contexts.CreateDbContext())
        {
            var item = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-77");
            db.WorkerSessions.Add(new WorkerSession { WorkItemId = item.Id, StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, CostUsd = cost });
            await db.SaveChangesAsync();
        }
        await Task.Delay(5);
        return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
    };

    private static Finding[] BlockingFindings(int count) =>
        Enumerable.Range(1, count).Select(i => new Finding(Finding.Blocking, $"finding {i}", "src/x.cs", i, "wrong")).ToArray();

    private static async Task<(WorkState State, string? Detail)> LastTransition(GatePipelineTests.Harness h) =>
        (await h.Rows()).Where(r => r.Step is null).Select(r => (r.State, r.Detail)).Last();

    [Fact]
    public async Task Costs_rising_through_this_runs_own_fix_rounds_pause_it_before_fix_round_3_in_the_same_run()
    {
        var findings = new Dictionary<string, int> { [GatePipelineTests.Sha1] = 4, [GatePipelineTests.ShaA] = 3, [GatePipelineTests.ShaB] = 2, [GatePipelineTests.ShaC] = 1 };
        var h = new GatePipelineTests.Harness
        {
            Freeze = new FreezeOptions(),
            Reviewer = new GatePipelineTests.FakeReviewer
            {
                Findings = r => r.Role == ReviewRoles.Correctness && findings.TryGetValue(r.Pull.HeadSha, out var n) ? BlockingFindings(n) : [],
            },
        };
        h.WorkerOverrides[0] = Costs(h, 0.40m);
        h.WorkerOverrides[1] = Costs(h, 0.55m);
        h.WorkerOverrides[2] = Costs(h, 0.80m);
        h.WorkerOverrides[3] = Costs(h, 1.20m);

        var outcome = await h.Run();

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains($"factory freeze ({FreezeTrigger.CostRising})", outcome.Error);
        Assert.Equal(3, h.WorkerCalls.Count); // implement, fix 1, fix 2: fix round 3 never started
        Assert.Empty(h.Merges);
        var transitions = (await h.Rows()).Where(r => r.Step is null).ToList();
        // Held before the step after fix round 2 (the review of its push), not merely at the next worker's start.
        Assert.Equal([WorkState.Fixing, WorkState.Review, WorkState.Paused], transitions.TakeLast(3).Select(t => t.State));
        Assert.Equal(RunPipeline.FreezePaused, transitions[^1].Detail);
        var row = await h.Controls.GetAsync(ControlScope.Freeze, CancellationToken.None);
        Assert.Equal((ControlState.Paused, FreezeTrigger.CostRising), (row!.State, row.Reason));
    }

    [Fact]
    public async Task Main_turning_red_while_a_run_waits_for_ci_stops_it_before_the_merge()
    {
        var h = new GatePipelineTests.Harness { Freeze = new FreezeOptions() };
        await SeedEarlierMerge(h);
        // Main is green when the item is dispatched; its CI fails while the item is being worked on.
        h.WorkerOverrides[0] = async call =>
        {
            h.GitHub.Ci[GatePipelineTests.FakeGateGitHub.BaseSha] =
                new CiFacts(GatePipelineTests.FakeGateGitHub.BaseSha, [new CheckFact("build-test", true, "failure")]);
            return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
        };

        var outcome = await h.Run();

        Assert.Empty(h.Merges);
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains($"factory freeze ({FreezeTrigger.MainRed})", outcome.Error);
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await LastTransition(h));
        var row = await h.Controls.GetAsync(ControlScope.Freeze, CancellationToken.None);
        Assert.Equal((ControlState.Paused, FreezeTrigger.MainRed), (row!.State, row.Reason));
    }

    [Fact]
    public async Task A_trigger_that_cannot_be_checked_mid_run_pauses_it_as_frozen_without_writing_a_freeze()
    {
        var h = new GatePipelineTests.Harness { Freeze = new FreezeOptions() };
        await SeedEarlierMerge(h);
        var down = false;
        h.GitHub.Compare = head => down && head == "mergeAaaaaaaaaaa"
            ? throw new HttpRequestException("GitHub is down")
            : new BaseComparison(GatePipelineTests.FakeGateGitHub.BaseSha, 0);
        h.WorkerOverrides[0] = async call =>
        {
            down = true;
            return await GatePipelineTests.ReportsModel(GatePipelineTests.ImplementerModel)(call);
        };

        var outcome = await h.Run();

        Assert.Empty(h.Merges);
        Assert.Contains($"factory freeze ({FreezeTrigger.CheckFailed})", outcome.Error);
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await LastTransition(h)); // resumes on its own once checkable
        Assert.Null(await h.Controls.GetAsync(ControlScope.Freeze, CancellationToken.None));
    }

    /// <summary>An earlier factory merge into main (sc-600), so main's head is checked by the main-red trigger.</summary>
    private static async Task SeedEarlierMerge(GatePipelineTests.Harness h)
    {
        await using (var db = h.Contexts.CreateDbContext())
        {
            var earlier = new WorkItem
            {
                Source = RunPipeline.Source, ExternalId = "sc-600", Title = "earlier", Repo = Repo, State = WorkState.Watch,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkItems.Add(earlier);
            await db.SaveChangesAsync();
            db.LedgerEntries.AddRange(
                new LedgerEntry { WorkItemId = earlier.Id, State = WorkState.MergeGate, Step = RunPipeline.Steps.MergeFiles,
                    Detail = new MergeFiles("main", ["src/a.cs"]).ToDetail(), RecordedAt = DateTimeOffset.UtcNow, Outcome = StepOutcome.Passed },
                new LedgerEntry { WorkItemId = earlier.Id, State = WorkState.Merge, Detail = "mergeAaaaaaaaaaa", RecordedAt = DateTimeOffset.UtcNow, Outcome = StepOutcome.Passed },
                new LedgerEntry { WorkItemId = earlier.Id, State = WorkState.Watch, Detail = "mergeAaaaaaaaaaa", RecordedAt = DateTimeOffset.UtcNow, Outcome = StepOutcome.Passed });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task A_trigger_that_starts_holding_as_a_fix_round_prepares_stops_it_before_its_worker_session_starts()
    {
        var h = new GatePipelineTests.Harness { Freeze = new FreezeOptions(), Reviewer = GatePipelineTests.FakeReviewer.Blocking() };
        h.Workspaces.OnRestore = () => SeedEscalations(h.Contexts, 3); // the fixer's worktree is restored after its step began

        var outcome = await h.Run();

        Assert.Single(h.WorkerCalls); // the implementer only: no fixer session started
        Assert.Contains($"factory freeze ({FreezeTrigger.ConsecutiveFailures})", outcome.Error);
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await LastTransition(h));
    }

    [Fact]
    public async Task A_trigger_that_starts_holding_during_a_base_update_stops_the_push()
    {
        var h = new GatePipelineTests.Harness { Freeze = new FreezeOptions() };
        h.GitHub.Compare = head => head == GatePipelineTests.Sha1 ? new BaseComparison("base1", 1) : new BaseComparison("base1", 0);
        var seeded = false;
        h.Workspaces.MergeBase = _ =>
        {
            if (!seeded)
            {
                SeedEscalations(h.Contexts, 3);
                seeded = true;
            }
            return new BaseMerge("base1", GatePipelineTests.ShaA, []);
        };
        // Were it pushed, the PR's head would move to the update (so a run that wrongly pushes still ends).
        h.Workspaces.OnFastForward = _ =>
        {
            h.GitHub.Head = GatePipelineTests.ShaA;
            return Task.CompletedTask;
        };

        var outcome = await h.Run();

        Assert.DoesNotContain(h.Workspaces.Calls, c => c.StartsWith("fast-forward", StringComparison.Ordinal));
        Assert.Empty(h.Merges);
        Assert.Contains($"factory freeze ({FreezeTrigger.ConsecutiveFailures})", outcome.Error);
        Assert.Equal((WorkState.Paused, RunPipeline.FreezePaused), await LastTransition(h));
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
