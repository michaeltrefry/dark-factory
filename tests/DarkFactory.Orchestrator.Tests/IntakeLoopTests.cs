using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class IntakeLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>The real pipeline over an in-memory ledger, with the Shortcut adapter on a fake board.</summary>
    private sealed class PipelineRunner(IWorkSource source, bool ignoreScope = false) : IItemRunner
    {
        public LedgerDbContext Db { get; } = TestDb.Create();
        public InProcessRunLocks Locks { get; } = new();
        public FakePullRequests Prs { get; } = new();
        /// <summary>The worker every run uses; by default a fresh one that succeeds.</summary>
        public FakeWorker? Worker { get; init; }
        private int _completed;
        public int Completed => Volatile.Read(ref _completed);
        public WorkLedger Ledger => new(Db, TimeProvider.System);

        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => RunPipeline.InFlightAsync(Ledger, ct);

        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            var outcome = await new RunPipeline(source, Ledger, Locks, new FakeWorkspaces(),
                Worker ?? new FakeWorker(Reports(Ok)), Prs, Sandbox, TextWriter.Null, ignoreScope: ignoreScope).RunAsync(id, ct);
            Interlocked.Increment(ref _completed);
            return outcome;
        }

        public async Task<WorkItem> Item() => await Db.WorkItems.AsNoTracking().SingleAsync();
        public async Task<List<LedgerEntry>> Rows() => await Db.LedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private static bool Claimed(JsonObject story) =>
        story["owner_ids"]!.AsArray().Any(o => (string?)o == FakeShortcutBoard.Me)
        && story["labels"]!.AsArray().Any(l => (string?)l!["name"] == ShortcutWorkSource.ClaimLabel);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private static List<RecordedRequest> Puts(FakeShortcutBoard board) => board.Requests.Where(r => r.Method == HttpMethod.Put).ToList();

    private static List<RecordedRequest> Comments(FakeShortcutBoard board, int id) =>
        board.Requests.Where(r => r.Method == HttpMethod.Post && r.PathAndQuery == $"/api/v3/stories/{id}/comments").ToList();

    [Fact]
    public async Task To_do_story_in_scope_is_claimed_within_one_poll_interval_and_one_outside_is_never_touched()
    {
        var board = new FakeShortcutBoard();
        board.Add(201, FakeShortcutBoard.OtherTeam); // identical story, outside the scope
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = new PipelineRunner(source);
        var time = new FakeTimeProvider();
        var loop = new IntakeLoop(source, runner, new IntakeOptions(Interval), time, NullLogger<IntakeLoop>.Instance);

        await loop.StartAsync(CancellationToken.None);
        await Eventually(() => board.Requests.Any(r => r.PathAndQuery.StartsWith("/api/v3/groups/")));

        // A ready story appears after the first poll; it is picked up on the next tick.
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        time.Advance(Interval);
        await Eventually(() => runner.Completed == 1);
        await loop.StopAsync(CancellationToken.None);

        Assert.Equal(FakeShortcutBoard.InProgress, (long)board.Story(101)["workflow_state_id"]!);

        Assert.True(Claimed(board.Story(101)));
        var item = await runner.Db.WorkItems.SingleAsync();
        Assert.Equal("sc-101", item.ExternalId);
        var rows = await runner.Db.LedgerEntries.OrderBy(e => e.Id).ToListAsync();
        Assert.Equal((WorkState.Intake, null), (rows[0].State, rows[0].Step));
        Assert.Equal((WorkState.Intake, RunPipeline.Steps.Claimed), (rows[1].State, rows[1].Step));

        // Out of scope: never read (no story or comment call names it, only the watched team is listed), never changed.
        Assert.DoesNotContain(board.Requests, r => r.PathAndQuery.Contains("/201"));
        var listings = board.Requests.Where(r => r.PathAndQuery.StartsWith("/api/v3/groups/")).ToList();
        Assert.NotEmpty(listings);
        Assert.All(listings, r => Assert.StartsWith($"/api/v3/groups/{FakeShortcutBoard.FactoryTeam}/stories?", r.PathAndQuery));
        Assert.False(Claimed(board.Story(201)));
        Assert.Equal(FakeShortcutBoard.ToDo, (long)board.Story(201)["workflow_state_id"]!);
    }

    [Fact]
    public async Task Work_host_runs_the_session_hub_relay_and_the_intake_loop()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:HostPort"] = "0",
            ["Shortcut:ApiToken"] = "tok",
            ["Router:Key"] = "rk",
        }).Build();
        await using var app = FactoryHost.BuildWork(new FactoryOptions(config, new InMemorySecrets()));

        var hosted = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ToList();
        Assert.Contains(hosted, s => s is DarkFactory.Orchestrator.Sessions.SessionEventRelay);
        Assert.Contains(hosted, s => s is IntakeLoop);
    }

    [Fact]
    public async Task Opened_pr_shows_on_the_story_as_pr_and_branch_links_while_it_sits_in_progress()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.OtherTeam, epic: 77);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope([], [77]));
        var runner = new PipelineRunner(source);

        var outcome = await runner.RunAsync(101, CancellationToken.None);

        Assert.Equal(WorkState.Review, outcome.State);
        var story = board.Story(101);
        Assert.Equal([PrUrl, "https://github.com/michaeltrefry/dark-factory-sandbox/tree/factory/sc-101"],
            story["external_links"]!.AsArray().Select(l => (string?)l));
        Assert.Equal(FakeShortcutBoard.InProgress, (long)story["workflow_state_id"]!);
        Assert.True(Claimed(story));
    }

    [Fact]
    public async Task Story_in_a_watched_epic_is_ready_even_outside_the_watched_teams()
    {
        var board = new FakeShortcutBoard();
        board.Add(301, FakeShortcutBoard.OtherTeam, epic: 77);
        board.Add(302, FakeShortcutBoard.OtherTeam, epic: 78);
        board.Add(303, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [77]));

        Assert.Equal([301], await source.ListReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Story_claimed_by_someone_else_is_not_ready()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        board.Story(101)["labels"] = new JsonArray(new JsonObject { ["name"] = ShortcutWorkSource.ClaimLabel });
        board.Story(101)["owner_ids"] = new JsonArray("00000000-0000-4000-8000-0000000000bb");
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));

        Assert.Empty(await source.ListReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Team_listing_follows_every_page()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        board.Add(102, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog);
        board.Add(103, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])) { PageSize = 2 };

        Assert.Equal([101, 103], await source.ListReadyAsync(CancellationToken.None));
        Assert.Equal(["offset=0", "offset=2"], board.Requests.Where(r => r.PathAndQuery.StartsWith("/api/v3/groups/"))
            .Select(r => r.PathAndQuery.Split('&').Last()));
    }

    [Fact]
    public async Task Listing_that_ignores_the_scope_filter_still_yields_only_in_scope_stories()
    {
        var board = new FakeShortcutBoard { IgnoreListingFilter = true };
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        board.Add(201, FakeShortcutBoard.OtherTeam); // To Do, outside the scope
        board.Add(202, FakeShortcutBoard.OtherTeam, epic: 78); // To Do, in an unwatched epic

        Assert.Equal([101], await new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])).ListReadyAsync(CancellationToken.None));
        Assert.Empty(await new ShortcutWorkSource(board.Client(), "tok", new WatchScope([], [77])).ListReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_watched_team_fails_the_poll_loudly()
    {
        var source = new ShortcutWorkSource(new FakeShortcutBoard().Client(), "tok", new WatchScope(["nope"], []));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ListReadyAsync(CancellationToken.None));
        Assert.Contains("no Shortcut team 'nope'", ex.Message);
    }

    // ---- Claim checks: a story that is no longer the factory's to take is left untouched. ----

    private static async Task AssertParkedUntouched(FakeShortcutBoard board, PipelineRunner runner, RunOutcome outcome, string reason)
    {
        Assert.Empty(Puts(board));
        Assert.Empty(Comments(board, 101));
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains(reason, outcome.Error);
        var rows = await runner.Rows();
        Assert.Equal([WorkState.Intake, WorkState.Paused], rows.Where(r => r.Step is null).Select(r => r.State));
        Assert.Contains(reason, rows.Single(r => r.Step == RunPipeline.Steps.Parked).Detail);
        // Parked, not in flight: the next poll does not run it.
        Assert.Empty(await runner.InFlightAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Story_moved_to_backlog_between_listing_and_run_is_not_claimed()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = new PipelineRunner(source);
        Assert.Equal([101], await source.ListReadyAsync(CancellationToken.None));

        board.Story(101)["workflow_state_id"] = FakeShortcutBoard.Backlog;
        var outcome = await runner.RunAsync(101, CancellationToken.None);

        await AssertParkedUntouched(board, runner, outcome, "no longer in To Do");
        Assert.Equal(FakeShortcutBoard.Backlog, (long)board.Story(101)["workflow_state_id"]!);
    }

    [Fact]
    public async Task Refused_story_is_picked_up_once_it_is_ready_again()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam, FakeShortcutBoard.Backlog);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = new PipelineRunner(source);
        await runner.RunAsync(101, CancellationToken.None);

        board.Story(101)["workflow_state_id"] = FakeShortcutBoard.ToDo;
        await Loop(source, runner).PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkState.Review, (await runner.Item()).State);
        Assert.True(Claimed(board.Story(101)));
    }

    [Fact]
    public async Task Story_labelled_by_another_owner_is_not_claimed()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        board.Story(101)["labels"] = new JsonArray(new JsonObject { ["name"] = ShortcutWorkSource.ClaimLabel });
        board.Story(101)["owner_ids"] = new JsonArray("00000000-0000-4000-8000-0000000000bb");
        var runner = new PipelineRunner(new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])));

        var outcome = await runner.RunAsync(101, CancellationToken.None);

        await AssertParkedUntouched(board, runner, outcome, "another claimant holds it");
    }

    [Fact]
    public async Task Story_without_the_watched_epic_is_not_claimed()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var runner = new PipelineRunner(new ShortcutWorkSource(board.Client(), "tok", new WatchScope([], [77])));

        var outcome = await runner.RunAsync(101, CancellationToken.None);

        await AssertParkedUntouched(board, runner, outcome, "outside the watch scope");
    }

    [Fact]
    public async Task Ignore_scope_claims_an_out_of_scope_story_but_not_one_another_claimant_holds()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.OtherTeam);
        board.Add(102, FakeShortcutBoard.OtherTeam);
        board.Story(102)["labels"] = new JsonArray(new JsonObject { ["name"] = ShortcutWorkSource.ClaimLabel });
        board.Story(102)["owner_ids"] = new JsonArray("00000000-0000-4000-8000-0000000000bb");
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));

        Assert.Equal(WorkState.Review, (await new PipelineRunner(source, ignoreScope: true).RunAsync(101, CancellationToken.None)).State);
        Assert.True(Claimed(board.Story(101)));
        Assert.Equal(WorkState.Paused, (await new PipelineRunner(source, ignoreScope: true).RunAsync(102, CancellationToken.None)).State);
        Assert.DoesNotContain(Puts(board), r => r.PathAndQuery.EndsWith("/102"));
    }

    [Fact]
    public async Task Claim_that_does_not_read_back_is_refused_and_the_story_is_not_moved()
    {
        var board = new FakeShortcutBoard { DropLabelsOnPut = true };
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var runner = new PipelineRunner(new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], [])));

        var outcome = await runner.RunAsync(101, CancellationToken.None);

        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains("did not stick", outcome.Error);
        Assert.Single(Puts(board));
        Assert.Equal(FakeShortcutBoard.ToDo, (long)board.Story(101)["workflow_state_id"]!);
    }

    // ---- Resuming: interrupted items resume; parked, user-paused and out-of-scope ones do not. ----

    /// <summary>Starts story 101's run in a poll, stops the poll mid-Implement, and returns the runner.</summary>
    private static async Task<PipelineRunner> InterruptedMidImplement(IWorkSource source)
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var runner = new PipelineRunner(source)
        {
            Worker = new FakeWorker(
                async call =>
                {
                    await call.OnSession("sess-77", CancellationToken.None);
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, stop.Token);
                    throw new InvalidOperationException("unreachable");
                },
                Reports(Ok)),
        };
        var poll = Loop(source, runner).PollOnceAsync(stop.Token);
        await started.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        Assert.Equal(WorkState.Paused, (await runner.Item()).State);
        return runner;
    }

    [Fact]
    public async Task Item_interrupted_mid_implement_resumes_on_the_next_poll_and_reaches_review()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = await InterruptedMidImplement(source);

        await Loop(source, runner).PollOnceAsync(CancellationToken.None);

        Assert.Equal(WorkState.Review, (await runner.Item()).State);
        Assert.Equal("sess-77", runner.Worker!.Calls[1].Resume);
        Assert.Equal([WorkState.Intake, WorkState.Implement, WorkState.Paused, WorkState.Implement, WorkState.Review],
            (await runner.Rows()).Where(r => r.Step is null).Select(r => r.State));
    }

    [Fact]
    public async Task Item_paused_other_than_by_an_interrupt_is_not_resumed()
    {
        var runner = new PipelineRunner(new ScriptedSource(() => []));
        var ledger = runner.Ledger;
        var interrupted = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-1", "a", "o/r", null, CancellationToken.None);
        await ledger.RecordAsync(interrupted, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(interrupted, WorkState.Paused, null, RunPipeline.Interrupted, CancellationToken.None);
        var byUser = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-2", "b", "o/r", null, CancellationToken.None);
        await ledger.RecordAsync(byUser, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(byUser, WorkState.Paused, null, "paused by a user", CancellationToken.None);
        var parked = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-3", "c", "o/r", null, CancellationToken.None);
        await ledger.RecordAsync(parked, WorkState.Implement, null, null, CancellationToken.None);
        await ledger.RecordAsync(parked, WorkState.Paused, null, RunPipeline.Interrupted, CancellationToken.None);
        await ledger.CheckpointAsync(parked, RunPipeline.Steps.Parked, null, "the story is outside the watch scope", CancellationToken.None);
        var inImplement = await ledger.GetOrCreateAsync(RunPipeline.Source, "sc-4", "d", "o/r", null, CancellationToken.None);
        await ledger.RecordAsync(inImplement, WorkState.Implement, null, null, CancellationToken.None);

        Assert.Equal([1, 4], await runner.InFlightAsync(CancellationToken.None));
    }

    [Fact]
    public async Task In_flight_item_whose_story_left_the_scope_is_parked_with_one_comment()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = await InterruptedMidImplement(source);

        board.Story(101)["group_id"] = FakeShortcutBoard.OtherTeam;
        await Loop(source, runner).PollOnceAsync(CancellationToken.None);
        await Loop(source, runner).PollOnceAsync(CancellationToken.None);

        Assert.Single(runner.Worker!.Calls);
        Assert.Single(Comments(board, 101));
        Assert.Contains("watch scope", (string?)JsonNode.Parse(Comments(board, 101)[0].Body!)!["text"]);
        Assert.Equal(WorkState.Paused, (await runner.Item()).State);
        Assert.Contains(await runner.Rows(), r => r.Step == RunPipeline.Steps.Parked && r.Detail!.Contains("watch scope"));
        Assert.Empty(await runner.InFlightAsync(CancellationToken.None));

        // Run by hand without --ignore-scope: still parked, and the story is not told again.
        var outcome = await runner.RunAsync(101, CancellationToken.None);
        Assert.Equal(WorkState.Paused, outcome.State);
        Assert.Contains("outside the watch scope", outcome.Error);
        Assert.Single(Comments(board, 101));
        Assert.Single(await runner.Rows(), r => r.Step == RunPipeline.Steps.Parked);
    }

    [Fact]
    public async Task Parked_story_moved_back_to_to_do_gets_one_comment_and_its_spec_is_not_reread()
    {
        var board = new FakeShortcutBoard();
        board.Add(101, FakeShortcutBoard.FactoryTeam, epic: 77);
        var source = new ShortcutWorkSource(board.Client(), "tok", new WatchScope(["darkfactory"], []));
        var runner = new PipelineRunner(source);
        Assert.Equal(WorkState.Review, (await runner.RunAsync(101, CancellationToken.None)).State);
        board.Story(101)["workflow_state_id"] = FakeShortcutBoard.ToDo; // a human drags it back
        var before = board.Requests.Count;

        await Loop(source, runner).PollOnceAsync(CancellationToken.None);
        await Loop(source, runner).PollOnceAsync(CancellationToken.None);

        var polled = board.Requests.Skip(before).ToList();
        Assert.Contains(polled, r => r.PathAndQuery.StartsWith("/api/v3/groups/")); // it was listed as ready both times
        Assert.DoesNotContain(polled, r => r.PathAndQuery.StartsWith("/api/v3/epics/") || r.PathAndQuery == "/api/v3/stories/101");
        Assert.Single(Comments(board, 101));
        Assert.Contains("Review", (string?)JsonNode.Parse(Comments(board, 101)[0].Body!)!["text"]);
        Assert.Equal(WorkState.Review, (await runner.Item()).State);
    }

    private sealed class ScriptedRunner(IReadOnlyList<int> inFlight, int failing = -1) : IItemRunner
    {
        public List<int> Runs { get; } = [];
        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult(inFlight);
        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Runs.Add(id);
            return id == failing
                ? throw new InvalidOperationException("ledger down")
                : Task.FromResult(new RunOutcome(1, WorkState.Review, null, null, null));
        }
    }

    private sealed class ScriptedSource(Func<IReadOnlyList<int>> ready) : IWorkSource
    {
        public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => Task.FromResult(ready());
        public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> InScopeAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ReleaseAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => throw new NotSupportedException();
        public Task CommentAsync(int id, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) => throw new NotSupportedException();
    }

    private static IntakeLoop Loop(IWorkSource source, IItemRunner runner) =>
        new(source, runner, new IntakeOptions(Interval), TimeProvider.System, NullLogger<IntakeLoop>.Instance);

    [Fact]
    public async Task Poll_resumes_in_flight_items_first_then_ready_ones_once_each_and_a_failed_run_does_not_stop_the_rest()
    {
        var runner = new ScriptedRunner([5, 3], failing: 5);
        await Loop(new ScriptedSource(() => [3, 7]), runner).PollOnceAsync(CancellationToken.None);
        Assert.Equal([5, 3, 7], runner.Runs);
    }

    [Fact]
    public async Task Failed_listing_skips_the_poll_without_throwing()
    {
        var runner = new ScriptedRunner([]);
        await Loop(new ScriptedSource(() => throw new HttpRequestException("Shortcut down")), runner).PollOnceAsync(CancellationToken.None);
        Assert.Empty(runner.Runs);
    }
}
