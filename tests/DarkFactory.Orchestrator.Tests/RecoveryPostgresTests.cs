using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.WorkSources;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryPostgresTests
{
    private sealed class LostAnswerPr : HttpMessageHandler, IPullRequests
    {
        internal int Created;
        internal int Calls;
        private string? url;
        private readonly HttpClient http;
        private readonly GitHubPullRequests transport;
        internal LostAnswerPr()
        {
            http = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
            using var key = RSA.Create(2048);
            transport = new(http, new GitHubApp(http, "42", key.ExportRSAPrivateKeyPem(), TimeProvider.System));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/installation")) return Task.FromResult(FakeApi.Json(HttpStatusCode.OK, "{\"id\":555}"));
            if (path.EndsWith("/access_tokens")) return Task.FromResult(FakeApi.Json(HttpStatusCode.Created,
                JsonSerializer.Serialize(new { token = "fixture", expires_at = DateTimeOffset.UtcNow.AddMinutes(59) })));
            if (request.Method == HttpMethod.Patch) return Task.FromResult(FakeApi.Json(HttpStatusCode.OK, "{}"));
            if (request.Method == HttpMethod.Post && path == "/repos/owner/alpha/pulls")
            {
                Calls++;
                if (url is null)
                {
                    Created++; url = "https://github.com/owner/alpha/pull/1";
                    throw new HttpRequestException("POST accepted, answer lost");
                }
                return Task.FromResult(FakeApi.Json(HttpStatusCode.UnprocessableEntity, "{\"message\":\"PR exists\"}"));
            }
            if (request.Method == HttpMethod.Get && path == "/repos/owner/alpha/pulls")
                return Task.FromResult(FakeApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new[] { new { html_url = url } })));
            throw new InvalidOperationException("Unexpected PR transport call " + request.Method + " " + path);
        }
        protected override void Dispose(bool disposing) { if (disposing) http.Dispose(); base.Dispose(disposing); }
        public Task<string> OpenAsync(RepoRef repo, string head, string baseBranch, string title, string body, CancellationToken ct) => transport.OpenAsync(repo, head, baseBranch, title, body, ct);
        public Task<IReadOnlyList<string>> ConvertOpenToDraftAsync(RepoRef repo, string head, CancellationToken ct) => transport.ConvertOpenToDraftAsync(repo, head, ct);
        public Task UpdateBodyAsync(RepoRef repo, string pullUrl, string body, CancellationToken ct) => transport.UpdateBodyAsync(repo, pullUrl, body, ct);
    }

    [Fact]
    public async Task Racing_Postgres_drivers_recover_one_PR_after_a_lost_creation_answer_and_accept_one_reassessment_result()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new RecoveryCorrectionPipelineTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        await f.Initialize();
        using var prs = new LostAnswerPr(); f.PrClient = prs;
        var lost = await f.Run();
        Assert.Equal(WorkState.Implement, lost.State);
        Assert.Contains("uncertain", lost.Error);
        Assert.Equal(1, prs.Created);
        var resumed = await Task.WhenAll(f.Run(), f.Run());
        Assert.Contains(resumed, o => o.State == WorkState.Watch);
        Assert.Equal(1, prs.Created);
        var rows = await f.Rows();
        Assert.Single(rows.Where(e => e.Step == RunPipeline.Steps.WorkerDone));
        Assert.Single(rows.Where(e => e.Step == RunPipeline.Steps.Linked));
        Assert.Single(rows.Where(e => e.Step is null && e.State == WorkState.Merge));
        Assert.Contains(rows, e => e.Step == "recovery-delivered");
        Assert.Contains(rows, e => e.Step == RunPipeline.Steps.NewTests);
        Assert.Contains(rows, e => e.Step == RunPipeline.Steps.GatePassed);
        var materials = new RecoveryReassessmentTests.Assessor(f);
        var api = new FakeApi().On("POST /v1/messages", request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var text = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            using var input = JsonDocument.Parse(text[text.IndexOf('{')..(text.LastIndexOf('}') + 1)]);
            var root = input.RootElement;
            return SseAnswers.Response(SseAnswers.Answer(JsonSerializer.Serialize(new {
                version = 1, snapshot = root.GetProperty("Id").GetString(), generation = root.GetProperty("Generation").GetInt32(),
                repo = root.GetProperty("Repo").GetString(), decision = "safe", evidenceHash = root.GetProperty("EvidenceHash").GetString(),
                materialSnapshot = root.GetProperty("material").GetProperty("Id").GetString(), citations = new[] { "merge:1" }, findings = Array.Empty<object>() })));
        });
        using var http = api.Client("http://router.test/");
        Task<string> Capture(HotFileSnapshot s, CancellationToken _) => Task.FromResult(materials.Material(s));
        HotFileRecovery Driver() => new(f.Recovery.Seed.Contexts, new PostgresRunLocks(database.ConnectionString), f.Recovery.Seed.Options,
            f.Recovery.Seed.Time, new RouterHotFileAssessor(new RouterReviewer(http, "fixture"), Capture), capture: Capture);
        await using var a = Driver(); await using var b = Driver();
        await Task.WhenAll(a.AdvanceAsync("owner/alpha", default), b.AdvanceAsync("OWNER/ALPHA", default));
        Assert.Single(api.Requests);
        await using var read = f.Recovery.Seed.Contexts.CreateDbContext();
        var accepted = Assert.Single(await read.RecoveryAssessmentAttempts.ToListAsync());
        Assert.Equal("Accepted", accepted.State);
        Assert.NotNull(accepted.Result);
        var resolved = await read.HotFileIncidents.SingleAsync();
        Assert.True(resolved.State == "Resolved", resolved.Error + " | " + accepted.Result);
        Assert.Null(await b.HeldAsync("owner/alpha", default));
        Assert.Single(await read.RecoveryCorrections.ToListAsync());
    }

    [Theory]
    [InlineData("assessment")]
    [InlineData("refresh")]
    [InlineData("correction")]
    public async Task Spent_budgets_survive_restarts_without_dispatch_or_replacement(string budget)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new RecoveryWorkSourceTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        await f.Initialize();
        if (budget == "correction")
        {
            await f.Materialize();
            await using var failed = f.Seed.Contexts.CreateDbContext();
            var c = await failed.RecoveryCorrections.SingleAsync(); c.State = "Failed";
            c.Attempt = 2; // persisted second repair reservation, failure observed before source closeout
            var item = await new WorkLedger(failed, f.Seed.Time).GetOrCreateAsync("recovery", $"rc-{c.Id}", "repair", "owner/alpha", null, default);
            item.State = WorkState.Escalated;
            await failed.SaveChangesAsync();
        }
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.State = budget == "refresh" ? "Refreshing" : budget == "correction" ? "CorrectionFailed" : "Held";
            incident.Attempts = budget == "assessment" ? 3 : 0;
            incident.Refreshes = budget == "refresh" ? 3 : 0;
            incident.Version++; await db.SaveChangesAsync();
        }
        for (var n = 0; n < 4; n++)
        {
            await using var restarted = f.Recovery();
            await restarted.AdvanceAsync("owner/alpha", default);
            Assert.Equal("Escalated", (await restarted.HeldAsync("owner/alpha", default))!.State);
            f.Seed.Time.Advance(TimeSpan.FromDays(2));
        }
        await using var read = f.Seed.Contexts.CreateDbContext();
        Assert.Empty(await read.RecoveryAssessmentAttempts.ToListAsync());
        Assert.Equal(budget == "correction" ? 1 : 0, await read.RecoveryCorrections.CountAsync());
    }

    [Theory]
    [InlineData("inconclusive")]
    [InlineData("unreachable")]
    [InlineData("wrong-class")]
    public async Task Unusable_assessments_retry_after_30_then_120_seconds_and_escalate_on_the_third(string result)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        var assessor = new FailingAssessor(result);
        HotFileRecovery Driver() => new(f.Contexts, new PostgresRunLocks(database.ConnectionString), f.Options, f.Time, assessor);
        await using (var first = Driver()) await first.AdvanceAsync("owner/alpha", default);
        await using (var early = Driver()) await early.AdvanceAsync("owner/alpha", default);
        Assert.Equal(1, assessor.Calls);
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await using (var second = Driver()) await second.AdvanceAsync("owner/alpha", default);
        f.Time.Advance(TimeSpan.FromSeconds(119));
        await using (var early = Driver()) await early.AdvanceAsync("owner/alpha", default);
        Assert.Equal(2, assessor.Calls);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await using (var third = Driver()) await third.AdvanceAsync("owner/alpha", default);
        await using var final = Driver();
        Assert.Equal("Escalated", (await final.HeldAsync("owner/alpha", default))!.State);
        await final.AdvanceAsync("owner/alpha", default);
        Assert.Equal(3, assessor.Calls);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal(3, await db.RecoveryAssessmentAttempts.CountAsync());
    }

    private sealed class DelegateAssessor(Func<HotFileSnapshot, string, CancellationToken, Task<HotFileAssessment>> run) : IHotFileAssessor
    {
        public Task<HotFileAssessment> AssessAsync(HotFileSnapshot s, string session, CancellationToken ct) => run(s, session, ct);
    }

    [Fact]
    public async Task Repeated_stale_evidence_consumes_only_three_lifetime_refreshes_across_new_driver_instances()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        var snapshots = new HashSet<string>();
        var assessor = new DelegateAssessor((s, _, _) =>
        {
            Assert.True(snapshots.Add(s.Id));
            return Task.FromResult(new HotFileAssessment(s.Id, s.Generation, s.Repo, "high", "inconclusive", s.EvidenceHash) { EvidenceChanged = true });
        });
        for (var n = 0; n < 7; n++)
        {
            await using var driver = new HotFileRecovery(f.Contexts, new PostgresRunLocks(database.ConnectionString), f.Options, f.Time, assessor);
            await driver.AdvanceAsync("owner/alpha", default);
        }
        Assert.Equal(4, snapshots.Count);
        await using var db = f.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        Assert.Equal(3, incident.Refreshes);
        Assert.Equal("Escalated", incident.State);
        Assert.Contains("refresh budget exhausted", incident.Error);
        Assert.Equal(4, await db.RecoveryAssessmentAttempts.CountAsync());
    }

    [Fact]
    public async Task A_stale_result_CAS_cannot_accept_after_the_current_incident_was_changed()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        var assessor = new DelegateAssessor(async (s, _, _) =>
        {
            await using var update = f.Contexts.CreateDbContext();
            var successor = await update.HotFileIncidents.SingleAsync();
            successor.Version++; successor.State = "Escalated"; successor.Error = "new authority";
            await update.SaveChangesAsync();
            return new(s.Id, s.Generation, s.Repo, "high", "safe", s.EvidenceHash);
        });
        await using var driver = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => driver.AdvanceAsync("owner/alpha", default));
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Equal("Reserved", (await db.RecoveryAssessmentAttempts.SingleAsync()).State);
        Assert.Empty(await db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step).ToListAsync());
    }

    [Theory]
    [InlineData("usage")]
    [InlineData("shutdown")]
    public async Task Usage_and_orderly_shutdown_do_not_spend_semantic_attempts(string cause)
    {
        var f = new HotFileRecoveryTests.Fixture();
        for (var n = 0; n < 8; n++) await f.Merge();
        using var stop = new CancellationTokenSource();
        var assessor = new DelegateAssessor((_, _, ct) =>
        {
            if (cause == "usage") throw new RouterUsageLimitedException("tier unavailable");
            stop.Cancel(); ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        });
        await using var driver = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        if (cause == "shutdown") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.AdvanceAsync("owner/alpha", stop.Token));
        else await driver.AdvanceAsync("owner/alpha", stop.Token);
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal(0, (await db.HotFileIncidents.SingleAsync()).Attempts);
        Assert.Equal("Held", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Equal(cause == "usage" ? "UsageWait" : "Cancelled", (await db.RecoveryAssessmentAttempts.SingleAsync()).State);
    }

    private sealed class FailingAssessor(string result) : IHotFileAssessor
    {
        internal int Calls;
        public Task<HotFileAssessment> AssessAsync(HotFileSnapshot s, string session, CancellationToken ct)
        {
            Calls++;
            if (result == "unreachable") throw new HttpRequestException("router unreachable");
            if (result == "usage") throw new RouterUsageLimitedException("tier unavailable");
            return Task.FromResult(new HotFileAssessment(s.Id, s.Generation, s.Repo, result == "wrong-class" ? "mid" : "high", result == "wrong-class" ? "safe" : "inconclusive", s.EvidenceHash));
        }
    }

    [Theory]
    [InlineData("detected")]
    [InlineData("dispatched")]
    [InlineData("accepted")]
    public async Task A_real_process_crash_at_a_stage_checkpoint_resumes_without_replaying_a_reserved_session(string boundary)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery_crash");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        var temp = Directory.CreateTempSubdirectory("df-recovery-crash-");
        try
        {
        var log = Path.Combine(temp.FullName, "sessions");
        async Task Run(string mode, int exit)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DarkFactory.CrashHost.dll"), "recovery", database.ConnectionString, log, mode, f.Time.GetUtcNow().ToString("O") })
                start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var own = OwnProcess.Of(child);
            try
            {
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(child.ExitCode == exit, await stdout + await stderr);
            }
            finally { own.KillIfStillRunning(); }
        }
        await Run(boundary, 73);
        await Run("resume", 0); // a dispatched session's unexpired lease cannot be stolen
        if (boundary == "dispatched")
        {
            Assert.Single(File.ReadAllLines(log));
            f.Time.Advance(HotFileRecovery.StageTimeout + TimeSpan.FromSeconds(1));
            await Run("resume", 0);
        }
        await Run("resume", 0);
        var sessions = File.ReadAllLines(log);
        Assert.Equal(boundary == "dispatched" ? 2 : 1, sessions.Length);
        Assert.Equal(sessions.Length, sessions.Distinct().Count());
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal("Resolved", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Single(await db.RecoveryAssessmentAttempts.Where(a => a.State == "Accepted").ToListAsync());
        Assert.Equal(boundary == "dispatched" ? 1 : 0, await db.RecoveryAssessmentAttempts.CountAsync(a => a.State == "Uncertain"));
        }
        finally { temp.Delete(recursive: true); }
    }

    [Fact]
    public async Task A_dispatched_assessment_reserves_its_retry_time_before_the_answer_and_restart_spends_it()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        f.Assessor.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var driver = f.Recovery;
        var first = driver.AdvanceAsync("owner/alpha", default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (f.Assessor.Calls == 0) await Task.Delay(10, timeout.Token);
            await using var db = f.Contexts.CreateDbContext();
            var incident = await db.HotFileIncidents.SingleAsync();
            Assert.Equal("Assessing", incident.State);
            Assert.Equal(1, incident.Attempts);
            Assert.Equal(f.Time.GetUtcNow() + TimeSpan.FromSeconds(30), incident.NextAttemptAt);
            // Independent connection cannot dispatch while the durable session claim is held.
            await using var other = f.Recovery;
            await other.AdvanceAsync("owner/alpha", default);
            Assert.Equal(1, f.Assessor.Calls);
        }
        finally { f.Assessor.Wait.TrySetResult(); await first; }
    }

    [Fact]
    public async Task A_crashed_reserved_third_assessment_escalates_without_another_dispatch()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        await f.Recovery.HeldAsync("owner/alpha", default);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.State = "Assessing"; incident.Attempts = 3; incident.Session = "dispatched-before-crash"; incident.Version++;
            await db.SaveChangesAsync();
        }
        for (var n = 0; n < 4; n++)
        {
            await using var restarted = f.Recovery;
            await restarted.AdvanceAsync("owner/alpha", default);
            Assert.Equal("Escalated", (await restarted.HeldAsync("owner/alpha", default))!.State);
        }
        Assert.Equal(0, f.Assessor.Calls);
    }

    [Theory]
    [InlineData("hot-file")]
    [InlineData("main-red")]
    [InlineData("check-failed")]
    public async Task Upgrade_and_safe_resolution_preserve_the_entire_legacy_global_freeze(string reason)
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_recovery");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new HotFileRecoveryTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        for (var n = 0; n < 8; n++) await f.Merge();
        var controls = new LedgerControls(f.Contexts, f.Time);
        await controls.SetAsync(ControlScope.Freeze, ControlState.Paused, "freeze", default);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var row = await db.Controls.SingleAsync();
            row.Reason = reason; row.Detail = "red-tip owner/alpha@main abc\ncheck-failed ledger: DbException";
            await db.SaveChangesAsync();
        }
        string before;
        await using (var db = f.Contexts.CreateDbContext()) before = JsonSerializer.Serialize(await db.Controls.SingleAsync());
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        await using var restarted = f.Recovery;
        await restarted.AdvanceAsync("owner/alpha", default);
        Assert.Null(await restarted.HeldAsync("owner/alpha", default));
        await using var read = f.Contexts.CreateDbContext();
        Assert.Equal(before, JsonSerializer.Serialize(await read.Controls.SingleAsync()));
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync("sc-77", null, "owner/alpha", default));
    }
}
