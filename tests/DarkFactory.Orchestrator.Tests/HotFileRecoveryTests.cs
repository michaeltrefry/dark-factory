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
    internal sealed class Assessor : IHotFileAssessor
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

    internal sealed class Fixture
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
        public async Task<RunOutcome> Run(string repo, int? number = null, HotFileRecovery? recovery = null, bool background = false)
        {
            await using var db = Contexts.CreateDbContext();
            var id = number ?? (repo == Repo ? 77 : 78);
            var story = new WorkStory(id, "next item", $"Repo: {repo}\nDo the work", "feature", "", ItemNaming.Shortcut);
            var pipeline = new RunPipeline(new FakeWorkSource(story), new WorkLedger(db, Time), Locks,
                new FakeWorkspaces(), new FakeWorker(RunPipelineTests.Reports(RunPipelineTests.Ok)), new FakePullRequests(),
                RepoRef.Parse(repo), TextWriter.Null, recovery: recovery ?? Recovery, backgroundRecovery: background, controls: new LedgerControls(Contexts, Time),
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

    private sealed class QueryCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Reads); return ValueTask.FromResult(result); }
    }

    /// <summary>A poll reads a fixed number of queries however many repositories the ledger has ever seen (E12).</summary>
    [Fact]
    public async Task Postgres_a_poll_looks_only_at_repositories_with_an_unresolved_incident_or_a_merge_in_the_window()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_hotfile_poll");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, CancellationToken.None);
        var counter = new QueryCounter();
        var f = new Fixture(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(counter).Options,
            new PostgresRunLocks(database.ConnectionString));
        await using var recovery = f.Recovery;
        async Task<int> PollReads()
        {
            counter.Reads = 0;
            await recovery.ScheduleAsync(TextWriter.Null, CancellationToken.None);
            return counter.Reads;
        }
        var empty = await PollReads();
        // Twenty repositories whose merges are all older than the window: none can be held or become hot.
        for (var n = 0; n < 20; n++) await f.Merge($"owner/idle{n}");
        f.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(empty, await PollReads());
        Assert.InRange(empty, 1, 6);

        await f.Merge("owner/fresh");
        await using (var db = f.Contexts.CreateDbContext())
        {
            // A plan item's changeset pass into another repository names it in its merge-files row.
            var plan = await db.WorkItems.FirstAsync(i => i.Repo == "owner/fresh");
            var files = await db.LedgerEntries.FirstAsync(e => e.WorkItemId == plan.Id && e.Step == RunPipeline.Steps.MergeFiles);
            files.Detail = new MergeFiles("main", ["src/a.cs"], "Owner/Pass").ToDetail();
            db.HotFileIncidents.Add(new HotFileIncident { Repo = "owner/held", Generation = 1, State = "Escalated", Snapshot = "{}", EvidenceHash = "x" });
            await db.SaveChangesAsync();
        }
        await using (var db = f.Contexts.CreateDbContext())
            Assert.Equal(["owner/fresh", "owner/held", "owner/pass"], await recovery.CandidateReposAsync(db, CancellationToken.None));
    }

    /// <summary>E3: each incident state change is a durable ledger row saved with it; a stale writer's change leaves none.</summary>
    [Fact]
    public async Task Postgres_every_incident_state_change_is_an_append_only_ledger_row_in_its_own_transaction()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_hotfile_states");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, CancellationToken.None);
        var f = new Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var i = 0; i < 8; i++) await f.Merge();
        await f.Recovery.HeldAsync(Repo, CancellationToken.None);
        await using var staleDb = f.Contexts.CreateDbContext();
        var stale = await staleDb.HotFileIncidents.SingleAsync();
        await f.Recovery.AdvanceAsync(Repo, CancellationToken.None);
        stale.State = "Escalated";
        stale.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        await using var db = f.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        var rows = await db.LedgerEntries.Where(e => e.Step == IncidentTransitions.Step).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal([$"incident new {Repo} generation 1: (new) → Held",
            $"incident {incident.Id} {Repo} generation 1: Held → Assessing",
            $"incident {incident.Id} {Repo} generation 1: Assessing → Resolved"], rows.Select(r => r.Detail));
        // Anchored on the first contributing merge's item, typed like any hold (a resolution passes).
        var first = HotFileSnapshot.Parse(incident.Snapshot).Merges[0].Item;
        Assert.All(rows, r => Assert.Equal(first, r.WorkItemId));
        Assert.Equal([StepOutcome.Deferred, StepOutcome.Deferred, StepOutcome.Passed], rows.Select(r => r.Outcome));
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
        var captured = RecoverySnapshot.FromDetail(await evidence.CaptureAsync(snapshot, CancellationToken.None))!;
        var answer = System.Text.Json.JsonSerializer.Serialize(new { version = 1, snapshot = snapshot.Id, generation = snapshot.Generation,
            repo = snapshot.Repo, decision = "safe", evidenceHash = snapshot.EvidenceHash, materialSnapshot = captured.Id,
            citations = new[] { captured.Evidence[0].Id }, findings = Array.Empty<object>() });
        var api = new FakeApi().On("POST /v1/messages", r => AssessmentReads.Respond(r, answer));
        using var http = api.Client("http://router.test/");
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time,
            new RouterHotFileAssessor(new DarkFactory.Orchestrator.Gate.RouterReviewer(http, "router-test-key"), evidence.CaptureAsync));
        await recovery.AdvanceAsync(Repo, CancellationToken.None);
        Assert.Null(await recovery.HeldAsync(Repo, CancellationToken.None));
        // One assessment session (its read_evidence turns included).
        Assert.Single(api.Requests.Select(r => r.Headers[DarkFactory.Orchestrator.Gate.RouterReviewer.SessionHeader]).Distinct());
        await using var db = f.Contexts.CreateDbContext();
        var resolved = await db.HotFileIncidents.SingleAsync();
        Assert.Equal("Resolved", resolved.State);
        Assert.Contains("materialHash", resolved.Answer!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_router_assessor_uses_fresh_high_sessions_and_checks_material_freshness()
    {
        var snapshot = RecoveryAssessmentTests.Trigger;
        var material = System.Text.Json.JsonSerializer.Serialize(RecoveryAssessmentTests.Snapshot);
        var response = RecoveryAssessmentTests.Answer();
        var api = new FakeApi().On("POST /v1/messages", r => AssessmentReads.Respond(r, response));
        using var http = api.Client("http://router.test/");
        var captures = 0;
        var assessor = new RouterHotFileAssessor(new DarkFactory.Orchestrator.Gate.RouterReviewer(http, "router-test-key"),
            (_, _) => Task.FromResult(++captures <= 2 ? material : material + " "));
        var session1 = Guid.NewGuid().ToString();
        var session2 = Guid.NewGuid().ToString();
        Assert.Equal("safe", (await assessor.AssessAsync(snapshot, session1, CancellationToken.None)).Decision);
        captures = 1;
        Assert.Equal("inconclusive", (await assessor.AssessAsync(snapshot, session2, CancellationToken.None)).Decision);
        Assert.Equal(new[] { session1, session2 }, api.Requests.Select(r => r.Headers[DarkFactory.Orchestrator.Gate.RouterReviewer.SessionHeader]).Distinct());
        Assert.All(api.Requests, request =>
        {
            Assert.Equal("high", request.Headers["x-weave-model-class"]);
            using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
            Assert.Equal("read_evidence", Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
            Assert.Contains("independently assess", body.RootElement.GetProperty("system").GetString());
        });
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"command\":\"clear\"}")]
    [InlineData("{}")]
    public void Assessment_parser_refuses_duplicate_unknown_or_missing_fields(string text)
    {
        Assert.ThrowsAny<Exception>(() => RecoveryDecision.Parse(text, RecoveryAssessmentTests.Trigger, RecoveryAssessmentTests.Snapshot));
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

    private sealed class AdmissionRunner(Fixture fixture, HotFileRecovery recovery) : IItemRunner
    {
        public readonly List<RunOutcome> Outcomes = [];
        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
        public async Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            var result = await fixture.Run(id == 77 ? Repo : "owner/beta", id, recovery, background: true);
            Outcomes.Add(result);
            return result;
        }
    }

    [Fact]
    public async Task Intake_admission_schedules_the_first_assessment_without_holding_the_only_run_slot()
    {
        var f = new Fixture();
        for (var i = 0; i < 8; i++) await f.Merge();
        f.Assessor.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var recovery = f.Recovery;
        var runner = new AdmissionRunner(f, recovery);
        using var loop = new IntakeLoop([new(IntakeDispatchTests.ListingSource.Listing(77, 78), runner)],
            new IntakeOptions(TimeSpan.FromMinutes(1), MaxConcurrentRuns: 1), f.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IntakeLoop>.Instance);
        try
        {
            // No poll scheduler has run: alpha's admission must discover and schedule the incident itself.
            await loop.PollOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Volatile.Read(ref f.Assessor.Calls) == 0) await Task.Delay(1, timeout.Token);
            Assert.Equal((2, WorkState.Intake, false, WorkState.Watch, 1, false),
                (runner.Outcomes.Count, runner.Outcomes[0].State, runner.Outcomes[0].Deferred is not null,
                 runner.Outcomes[1].State, f.Assessor.Calls, f.Assessor.Wait.Task.IsCompleted));
        }
        finally { f.Assessor.Wait.TrySetResult(); }
    }

    [Theory]
    [InlineData("stable", "safe")]
    [InlineData("moved", "inconclusive")]
    [InlineData("missing-link", "incomplete")]
    [InlineData("wrong-repo", "incomplete")]
    [InlineData("missing-head", "incomplete")]
    public async Task Pending_PR_heads_are_required_and_a_push_without_ledger_changes_invalidates_safe(string scenario, string expected)
    {
        var f = new Fixture();
        await using (var db = f.Contexts.CreateDbContext())
        {
            var ledger = new WorkLedger(db, f.Time);
            var item = await ledger.GetOrCreateAsync("shortcut", "sc-77", "pending", Repo, "pending", CancellationToken.None);
            item.State = WorkState.Review;
            await db.SaveChangesAsync();
            if (scenario != "missing-link")
                await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null,
                    $"https://github.com/{(scenario == "wrong-repo" ? "owner/beta" : Repo)}/pull/1", CancellationToken.None);
        }
        var github = new GatePipelineTests.FakeGateGitHub
        {
            Head = scenario == "missing-head" ? "" : new string('a', 40)
        };
        var evidence = new HotFileEvidence(f.Contexts, github, _ => new FakeWorkSource(
            new WorkStory(77, "pending", $"Repo: {Repo}", "feature", "", ItemNaming.Shortcut)));
        var snapshot = new HotFileSnapshot("snapshot", Repo, 1, [], "evidence");
        var response = "";
        var api = new FakeApi().On("POST /v1/messages", r =>
        {
            // Only GitHub changes, not the ledger or the board specification.
            if (scenario == "moved") github.Head = new string('b', 40);
            return AssessmentReads.Respond(r, response);
        });
        using var http = api.Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new DarkFactory.Orchestrator.Gate.RouterReviewer(http, "test-key"), async (trigger, ct) =>
        {
            var material = await evidence.CaptureAsync(trigger, ct);
            var captured = RecoverySnapshot.FromDetail(material)!;
            response = System.Text.Json.JsonSerializer.Serialize(new { version = 1, snapshot = trigger.Id, generation = trigger.Generation,
                repo = Repo, decision = "safe", evidenceHash = trigger.EvidenceHash, materialSnapshot = captured.Id,
                citations = new[] { captured.Evidence[0].Id }, findings = Array.Empty<object>() });
            return material;
        });
        string actual;
        try { actual = (await assessor.AssessAsync(snapshot, Guid.NewGuid().ToString(), CancellationToken.None)).Decision; }
        catch (InvalidOperationException) { actual = "incomplete"; }
        Assert.Equal(expected, actual);
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
