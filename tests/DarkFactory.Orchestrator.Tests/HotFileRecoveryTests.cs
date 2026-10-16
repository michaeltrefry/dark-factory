using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class HotFileRecoveryTests
{
    private const string Repo = "owner/alpha";
    private sealed class Assessor : IHotFileAssessor
    {
        public int Calls;
        public TaskCompletionSource? Wait;
        public HotFileAssessment? Answer;
        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Wait is not null) await Wait.Task.WaitAsync(ct);
            return Answer ?? new(snapshot.Id, snapshot.Generation, snapshot.Repo, "high", "safe", snapshot.EvidenceHash);
        }
    }

    private sealed class Fixture
    {
        public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        public readonly LedgerDbContextFactory Contexts;
        public readonly Assessor Assessor = new();
        public readonly IRunLocks Locks;
        public Fixture(DbContextOptions<LedgerDbContext>? options = null, IRunLocks? locks = null)
        {
            Contexts = new(options ?? new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Locks = locks ?? new InProcessRunLocks();
        }
        public FreezeOptions Options = new();
        public HotFileRecovery Recovery => new(Contexts, Locks, Options, Time, Assessor);
        private int _number;
        public async Task Merge(string repo = Repo, string branch = "main", params string[] files)
        {
            await using var db = Contexts.CreateDbContext();
            var item = new WorkItem { Source = "shortcut", ExternalId = $"sc-{++_number}", Title = "merged", Repo = repo,
                State = WorkState.Watch, CreatedAt = Time.GetUtcNow(), UpdatedAt = Time.GetUtcNow() };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = WorkState.MergeGate,
                Step = RunPipeline.Steps.MergeFiles, Detail = new MergeFiles(branch, files.Length == 0 ? ["src/a.cs"] : files).ToDetail(),
                RecordedAt = Time.GetUtcNow(), Outcome = StepOutcome.Passed });
            await db.SaveChangesAsync();
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = item.Id, State = WorkState.Merge, Detail = _number.ToString("x40"),
                RecordedAt = Time.GetUtcNow(), Outcome = StepOutcome.Passed });
            await db.SaveChangesAsync();
        }
        public async Task<RunOutcome> Run(string repo, int? number = null)
        {
            await using var db = Contexts.CreateDbContext();
            var id = number ?? (repo == Repo ? 77 : 78);
            var story = new WorkStory(id, "next item", $"Repo: {repo}\nDo the work", "feature", "", ItemNaming.Shortcut);
            var pipeline = new RunPipeline(new FakeWorkSource(story), new WorkLedger(db, Time), Locks,
                new FakeWorkspaces(), new FakeWorker(RunPipelineTests.Reports(RunPipelineTests.Ok)), new FakePullRequests(),
                RepoRef.Parse(repo), TextWriter.Null, recovery: Recovery, controls: new LedgerControls(Contexts, Time),
                gate: new GateStage(new GatePipelineTests.FakeGateGitHub { Diff = _ => TestPolicies.Diff("docs/next.md") },
                    new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5)));
            return await pipeline.RunAsync(id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Postgres_restart_retains_exact_acknowledgement_and_stale_writers_cannot_resolve_a_generation()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_hotfile");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, CancellationToken.None);
        var f = new Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var i = 0; i < 8; i++) await f.Merge();
        await f.Recovery.HeldAsync(Repo, CancellationToken.None);
        await using var staleDb = f.Contexts.CreateDbContext();
        var stale = await staleDb.HotFileIncidents.SingleAsync();
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        stale.State = "Held";
        stale.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        var restarted = new HotFileRecovery(f.Contexts, new PostgresRunLocks(database.ConnectionString), f.Options, f.Time, f.Assessor);
        await restarted.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(1, f.Assessor.Calls);
        Assert.Null(await restarted.HeldAsync(Repo, CancellationToken.None));
        await f.Merge();
        await restarted.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(2, f.Assessor.Calls);
    }

    [Fact]
    public async Task Eighth_merge_holds_then_a_fresh_safe_assessment_releases_the_next_item()
    {
        var f = new Fixture();
        for (var i = 0; i < 7; i++) await f.Merge();
        Assert.Null(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        await f.Merge();
        Assert.NotNull(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Null(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        Assert.Equal(1, f.Assessor.Calls);
        Assert.Equal(WorkState.Watch, (await f.Run(Repo)).State);
    }

    [Fact]
    public async Task Resolved_exact_evidence_is_not_assessed_again_but_a_new_merge_opens_a_generation()
    {
        var f = new Fixture();
        for (var i = 0; i < 8; i++) await f.Merge();
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(1, f.Assessor.Calls);
        await f.Merge();
        Assert.NotNull(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(2, f.Assessor.Calls);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal(new[] { 1, 2 }, await db.HotFileIncidents.OrderBy(i => i.Generation).Select(i => i.Generation).ToArrayAsync());
        Assert.All(await db.HotFileIncidents.ToListAsync(), i => Assert.Equal("Resolved", i.State));
    }

    [Fact]
    public async Task Acknowledgement_does_not_cover_another_file_that_later_becomes_hot()
    {
        var f = new Fixture();
        for (var i = 0; i < 7; i++) await f.Merge(Repo, "main", "src/a.cs", "src/b.cs");
        await f.Merge(Repo, "main", "src/a.cs");
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        await f.Merge(Repo, "main", "src/b.cs");
        var incident = Assert.IsType<HotFileIncident>(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        Assert.Equal(2, incident.Generation);
        Assert.All(HotFileSnapshot.Parse(incident.Snapshot).Merges, merge => Assert.Equal(new[] { "src/b.cs" }, merge.Files));
    }

    [Fact]
    public async Task Another_repository_completes_while_the_hot_repository_waits_for_assessment()
    {
        var f = new Fixture();
        for (var i = 0; i < 8; i++) await f.Merge();
        f.Assessor.Wait = new();
        var assessment = f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Equal(1, f.Assessor.Calls);
        Assert.Equal(WorkState.Watch, (await f.Run("owner/beta")).State);
        Assert.NotNull(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        f.Assessor.Wait.SetResult();
        await assessment;
        Assert.Equal(WorkState.Watch, (await f.Run(Repo)).State);
    }

    [Theory]
    [InlineData("wrong-snapshot", 1, Repo, "high", "safe", "")]
    [InlineData("", 2, Repo, "high", "safe", "")]
    [InlineData("", 1, "owner/beta", "high", "safe", "")]
    [InlineData("", 1, Repo, "mid", "safe", "")]
    [InlineData("", 1, Repo, "high", "unsafe", "")]
    [InlineData("", 1, Repo, "high", "safe", "different-evidence")]
    public async Task Only_the_exact_current_high_class_safe_acknowledgement_releases(string id, int generation, string repo,
        string servedClass, string decision, string hash)
    {
        var f = new Fixture();
        for (var i = 0; i < 8; i++) await f.Merge();
        var incident = Assert.IsType<HotFileIncident>(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
        f.Assessor.Answer = new(id.Length == 0 ? snapshot.Id : id, generation, repo, servedClass, decision,
            hash.Length == 0 ? snapshot.EvidenceHash : hash);
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.NotNull(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
    }

    [Fact]
    public async Task Threshold_window_repository_case_base_and_distinct_normalized_paths_are_used()
    {
        var f = new Fixture { Options = new FreezeOptions { HotFileMerges = 2, HotFileWindow = TimeSpan.FromHours(1) } };
        await f.Merge(Repo, "main", "src/./a.cs", "src/a.cs");
        Assert.Null(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        await f.Merge(Repo, "factory/changeset-sc-1", "src/a.cs");
        await f.Merge(Repo, "other", "src/a.cs");
        await f.Merge("owner/beta", "main", "src/a.cs");
        Assert.Null(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        await f.Merge("OWNER/ALPHA", "main", "src/a.cs");
        Assert.NotNull(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        var expired = new Fixture { Options = f.Options };
        await expired.Merge();
        expired.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
        await expired.Merge();
        Assert.Null(await expired.Recovery.HeldAsync(Repo, CancellationToken.None));
    }

    [Fact]
    public async Task Production_evidence_and_router_assessor_resolve_eight_real_pipeline_merges()
    {
        var f = new Fixture();
        for (var i = 100; i < 108; i++) Assert.Equal(WorkState.Watch, (await f.Run(Repo, i)).State);
        var incident = Assert.IsType<HotFileIncident>(await f.Recovery.HeldAsync(Repo, CancellationToken.None));
        var snapshot = HotFileSnapshot.Parse(incident.Snapshot);
        var github = new GatePipelineTests.FakeGateGitHub { Merged = true,
            MergeCommitSha = new string('9', 40), Diff = _ => TestPolicies.Diff("docs/next.md") };
        var evidence = new HotFileEvidence(f.Contexts, github, _ => new FakeWorkSource(
            new WorkStory(1, "next item", $"Repo: {Repo}\nDo the work", "feature", "", ItemNaming.Shortcut)));
        var answer = System.Text.Json.JsonSerializer.Serialize(new { version = 1, snapshot = snapshot.Id, generation = snapshot.Generation,
            repo = snapshot.Repo, decision = "safe", evidenceHash = snapshot.EvidenceHash });
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(answer)));
        using var http = api.Client("http://router.test/");
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time,
            new RouterHotFileAssessor(new DarkFactory.Orchestrator.Gate.RouterReviewer(http, "router-test-key"), evidence.CaptureAsync));
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
        Assert.Single(api.Requests);
        await using var db = f.Contexts.CreateDbContext();
        var resolved = await db.HotFileIncidents.SingleAsync();
        Assert.Equal("Resolved", resolved.State);
        Assert.Contains("materialHash", resolved.Answer!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_router_assessor_uses_fresh_high_sessions_and_no_tools_and_checks_material_freshness()
    {
        var snapshot = new HotFileSnapshot("snapshot", Repo, 1, [], "evidence");
        var response = System.Text.Json.JsonSerializer.Serialize(new { version = 1, snapshot = "snapshot", generation = 1,
            repo = Repo, decision = "safe", evidenceHash = "evidence" });
        var api = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(response)));
        using var http = api.Client("http://router.test/");
        var captures = 0;
        var assessor = new RouterHotFileAssessor(new DarkFactory.Orchestrator.Gate.RouterReviewer(http, "router-test-key"),
            (_, _) => Task.FromResult(++captures <= 2 ? "pinned evidence" : "changed evidence"));
        var session1 = Guid.NewGuid().ToString();
        var session2 = Guid.NewGuid().ToString();
        Assert.Equal("safe", (await assessor.AssessAsync(snapshot, session1, CancellationToken.None)).Decision);
        captures = 1;
        Assert.Equal("inconclusive", (await assessor.AssessAsync(snapshot, session2, CancellationToken.None)).Decision);
        Assert.Equal(new[] { session1, session2 }, api.Requests.Select(r => r.Headers[DarkFactory.Orchestrator.Gate.RouterReviewer.SessionHeader]));
        Assert.All(api.Requests, request =>
        {
            Assert.Equal("high", request.Headers["x-weave-model-class"]);
            using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            Assert.Contains("independently assess", body.RootElement.GetProperty("system").GetString());
        });
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"command\":\"clear\"}")]
    [InlineData("{}")]
    public void Assessment_parser_refuses_duplicate_unknown_or_missing_fields(string text)
    {
        Assert.ThrowsAny<Exception>(() => RouterHotFileAssessor.Parse(new DarkFactory.Orchestrator.Gate.StreamedAnswer("model", "end_turn", text), "high"));
    }

    [Fact]
    public async Task Scheduler_returns_while_assessment_is_pending_and_does_not_duplicate_dispatch()
    {
        var f = new Fixture();
        for (var i = 0; i < 8; i++) await f.Merge();
        f.Assessor.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var recovery = f.Recovery;
        await recovery.ScheduleAsync(TextWriter.Null, CancellationToken.None);
        // The deterministic signal is the durable assessment reservation, not a sleep or a model response.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref f.Assessor.Calls) == 0) await Task.Delay(1, timeout.Token);
        await recovery.ScheduleAsync(TextWriter.Null, CancellationToken.None);
        Assert.Equal(1, f.Assessor.Calls);
        var held = await f.Run(Repo);
        Assert.Equal(WorkState.Intake, held.State);
        Assert.Contains("held by hot-file recovery", held.Error);
        Assert.Null(held.Deferred); // not a global intake deferral
        Assert.Equal(WorkState.Watch, (await f.Run("owner/beta")).State);
        f.Assessor.Wait.SetResult();
    }

    [Fact]
    public async Task Manual_and_legacy_freeze_rows_are_unchanged_by_safe_resolution()
    {
        var f = new Fixture();
        var controls = new LedgerControls(f.Contexts, f.Time);
        await controls.SetAsync(ControlScope.Factory, ControlState.Paused, "human", CancellationToken.None);
        await controls.FreezeAsync(FreezeTrigger.HotFile, "legacy", null, CancellationToken.None);
        var before = await controls.GetAsync(ControlScope.Freeze, CancellationToken.None);
        for (var i = 0; i < 8; i++) await f.Merge();
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        var after = await controls.GetAsync(ControlScope.Freeze, CancellationToken.None);
        Assert.Equal(before!.ChangedAt, after!.ChangedAt);
        Assert.Equal(before.Detail, after.Detail);
        Assert.Equal(ControlState.Paused, (await controls.GetAsync(ControlScope.Factory, CancellationToken.None))!.State);
        Assert.Equal(WorkState.Intake, (await f.Run(Repo)).State);
    }
}
