using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class RecoveryCorrectionPipelineTests
{
    internal sealed class Fixture(DbContextOptions<LedgerDbContext>? options = null, IRunLocks? locks = null)
    {
        internal readonly RecoveryWorkSourceTests.Fixture Recovery = new(options, locks);
        internal IPullRequests? PrClient;
        internal readonly FakeWorkspaces Workspaces = new();
        internal readonly FakePullRequests Prs = new();
        internal readonly GatePipelineTests.FakeGateGitHub GitHub = new();
        internal IGateGitHub? GateClient;
        internal GatePipelineTests.FakeReviewer Reviewer = new();
        internal readonly FakeTestRunner Tests = new();
        internal RecoveryCorrection Correction = null!;
        internal Func<Task>? AfterWorker;
        internal Func<RecoverySnapshot, Task<RecoverySnapshot>>? BeforeMaterialize;
        internal bool Tainted;
        internal string Material = "";
        internal string[] Paths = ["src/a.cs", FakeTestRunner.NewTestFile];
        /// <summary>When set, the correction is materialized by the production scheduler's unsafe assessment, not directly.</summary>
        internal bool Assessed;
        internal async Task Initialize()
        {
            await Recovery.Initialize(recordUnsafe: !Assessed);
            var material = RecoverySnapshot.FromDetail(Recovery.Answer.Material)! with
            { BaseTips = new Dictionary<string, string> { ["main"] = GatePipelineTests.FakeGateGitHub.BaseSha } };
            if (BeforeMaterialize is not null) material = await BeforeMaterialize(material);
            var finding = Recovery.Answer.CorrectionFindings.Single() with { Paths = Paths };
            Material = JsonSerializer.Serialize(material);
            Recovery.Answer = Recovery.Answer with { Material = Material, MaterialHash = RecoverySnapshot.Hash(Material),
                Findings = [finding], CorrectionFindings = [finding] };
            if (Assessed)
            {
                await using var recovery = Recovery.Recovery(() => Material);
                await recovery.AdvanceAsync("owner/alpha", default);
                await using var db = Recovery.Seed.Contexts.CreateDbContext();
                Correction = await db.RecoveryCorrections.SingleAsync();
            }
            else Correction = await Recovery.Materialize();
            Workspaces.CorrectionPaths = () => Paths;
            Workspaces.Head = () => GitHub.Head;
            GitHub.Diff = _ => string.Concat(Paths.Select(p => TestPolicies.Diff(p)));
        }
        internal IControls Controls => new LedgerControls(Recovery.Seed.Contexts, Recovery.Seed.Time);
        internal IControls? ControlOverride;
        /// <summary>Every full evidence capture the pipeline's recovery made, labelled with the item's last ledger row then.</summary>
        internal readonly List<string> Captures = [];
        private string Capture()
        {
            using var db = Recovery.Seed.Contexts.CreateDbContext();
            var item = db.WorkItems.AsNoTracking().SingleOrDefault(i => i.ExternalId == $"rc-{Correction.Id}");
            var last = item is null ? null : db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id)
                .OrderByDescending(e => e.Id).FirstOrDefault();
            Captures.Add(item is null ? "new" : $"{item.State}/{last?.Step ?? "-"}");
            return Material;
        }
        internal async Task<RunOutcome> Run(CancellationToken ct = default)
        {
            await using var db = Recovery.Seed.Contexts.CreateDbContext();
            var worker = new FakeWorker(async call =>
            {
                await call.Callbacks.OnSession!("correction-session", default);
                await call.Callbacks.OnModel!(GatePipelineTests.ImplementerModel, default);
                if (Tainted) await call.Callbacks.OnUntrusted!("correction-session", "web:WebFetch", default);
                if (AfterWorker is not null) await AfterWorker();
                return new WorkerResult("correction-session", 0, false, "success", "done", "");
            });
            return await new RunPipeline(Recovery.Source, new WorkLedger(db, Recovery.Seed.Time), Recovery.Seed.Locks,
                Workspaces, worker, PrClient ?? Prs, RepoRef.Parse("owner/alpha"), TextWriter.Null, controls: ControlOverride ?? Controls,
                recovery: Recovery.Recovery(Capture),
                gate: new GateStage(GateClient ?? GitHub, Reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), Tests: Tests,
                    Risk: FakeCodeGraphRisk.Reader(new FakeCodeGraphRisk())))
                .RunAsync(Correction.Id, ct);
        }
        internal async Task<List<LedgerEntry>> Rows()
        {
            await using var db = Recovery.Seed.Contexts.CreateDbContext();
            return await db.LedgerEntries.Where(e => db.WorkItems.Any(i => i.Id == e.WorkItemId && i.ExternalId == $"rc-{Correction.Id}"))
                .OrderBy(e => e.Id).ToListAsync();
        }
    }

    // Real GitHubGate transport for every PR read and merge; unrelated policy/CI fixtures stay deterministic.
    private sealed class TokenBoundaryGate : HttpMessageHandler, IGateGitHub
    {
        private readonly GatePipelineTests.FakeGateGitHub inner;
        private readonly HttpClient client;
        private readonly GitHubGate transport;
        internal readonly TaskCompletionSource TokenRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseToken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Requests = [];
        internal TokenBoundaryGate(GatePipelineTests.FakeGateGitHub inner)
        {
            this.inner = inner;
            client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
            using var key = RSA.Create(2048);
            transport = new GitHubGate(client, new GitHubApp(client, "4242", key.ExportRSAPrivateKeyPem(), TimeProvider.System));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (path.EndsWith("/installation")) return FakeApi.Json(HttpStatusCode.OK, "{\"id\":555}");
            if (path.EndsWith("/access_tokens"))
            {
                var write = JsonDocument.Parse(body).RootElement.GetProperty("permissions").GetProperty("contents").GetString() == "write";
                Requests.Add(write ? "write-token requested" : "read-token");
                if (write)
                {
                    TokenRequested.TrySetResult();
                    await ReleaseToken.Task.WaitAsync(ct);
                    Requests.Add("write-token returned");
                }
                return FakeApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new
                    { token = write ? "write-token" : "read-token", expires_at = DateTimeOffset.UtcNow.AddMinutes(59) }));
            }
            if (request.Method == HttpMethod.Get && path == "/repos/owner/alpha/pulls/1")
            {
                Assert.Equal("read-token", request.Headers.Authorization!.Parameter);
                Requests.Add("GET pull");
                return FakeApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    number = 1, html_url = "https://github.com/owner/alpha/pull/1", state = inner.Merged ? "closed" : "open",
                    merged = inner.Merged, draft = false, changed_files = 2,
                    head = new { sha = inner.Head }, @base = new { @ref = inner.PullBaseRef, sha = inner.PullBaseSha }
                }));
            }
            if (request.Method == HttpMethod.Put && path == "/repos/owner/alpha/pulls/1/merge")
            {
                Requests.Add("PUT merge");
                Assert.Equal("write-token", request.Headers.Authorization!.Parameter);
                inner.Merged = true;
                return FakeApi.Json(HttpStatusCode.OK, "{\"merged\":true,\"sha\":\"merged\",\"message\":\"merged\"}");
            }
            throw new InvalidOperationException($"Unexpected transport request: {request.Method} {path}");
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
        public Task<PullFacts> GetPullAsync(RepoRef repo, int number, CancellationToken ct) => transport.GetPullAsync(repo, number, ct);
        public Task<MergeResult> MergeAsync(RepoRef repo, int number, string headSha, CancellationToken ct, Func<CancellationToken, Task>? authorize = null) => transport.MergeAsync(repo, number, headSha, ct, authorize);
        public Task<string> GetDiffAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct) => inner.GetDiffAsync(repo, baseSha, headSha, ct);
        public Task<RepoFiles> GetFilesAsync(RepoRef repo, string sha, CancellationToken ct) => inner.GetFilesAsync(repo, sha, ct);
        public Task<string?> GetPolicyAsync(RepoRef repo, string baseRef, CancellationToken ct) => inner.GetPolicyAsync(repo, baseRef, ct);
        public Task<CiFacts> GetCiAsync(RepoRef repo, string sha, CancellationToken ct) => inner.GetCiAsync(repo, sha, ct);
        public Task<RepoWorkflows> GetWorkflowsAsync(RepoRef repo, CancellationToken ct) => inner.GetWorkflowsAsync(repo, ct);
        public Task<string> GetCheckLogAsync(RepoRef repo, CheckFact check, CancellationToken ct) => inner.GetCheckLogAsync(repo, check, ct);
        public Task<BaseComparison> CompareAsync(RepoRef repo, string baseRef, string headSha, CancellationToken ct) => inner.CompareAsync(repo, baseRef, headSha, ct);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("grant")]
    [InlineData("repo-pause")]
    [InlineData("item-pause")]
    [InlineData("item-stop")]
    [InlineData("pull-base-sha")]
    [InlineData("pull-head")]
    [InlineData("pull-retarget")]
    public async Task Correction_reauthorizes_after_the_merge_token_returns_before_any_PUT(string change)
    {
        var f = new Fixture(); await f.Initialize();
        using var transport = new TokenBoundaryGate(f.GitHub);
        f.GateClient = transport;
        var run = f.Run();
        try
        {
            await transport.TokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(run.IsCompleted);
            Assert.DoesNotContain("PUT merge", transport.Requests);
            if (change == "grant")
            {
                await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
                (await db.RecoveryCorrections.SingleAsync()).State = "Revoked";
                await db.SaveChangesAsync();
            }
            if (change is "repo-pause" or "item-pause" or "item-stop")
                await f.Controls.SetAsync(change == "repo-pause" ? "repo:owner/alpha" : $"item:rc-{f.Correction.Id}",
                    change == "item-stop" ? ControlState.Stopping : ControlState.Paused, "human", default);
            if (change == "pull-base-sha") f.GitHub.PullBaseSha = "moved";
            if (change == "pull-head") f.GitHub.Head = GatePipelineTests.ShaA;
            if (change == "pull-retarget") f.GitHub.PullBaseRef = "other";
        }
        finally { transport.ReleaseToken.TrySetResult(); }
        var result = await run;
        if (change == "unchanged")
        {
            Assert.Equal(WorkState.Watch, result.State);
            Assert.Single(transport.Requests, r => r == "PUT merge");
            Assert.Contains("GET pull", transport.Requests.Skip(transport.Requests.IndexOf("write-token returned") + 1)
                .TakeWhile(r => r != "PUT merge"));
        }
        else
        {
            Assert.DoesNotContain("PUT merge", transport.Requests);
            Assert.False(f.GitHub.Merged);
            Assert.DoesNotContain(await f.Rows(), r => r.Step == "recovery-delivered");
        }
    }

    [Fact]
    public async Task Correction_uses_normal_panel_tests_queue_and_merge_while_ordinary_work_stays_held_and_restart_does_not_redeliver()
    {
        var f = new Fixture(); await f.Initialize();
        f.AfterWorker = async () => Assert.Contains("held", (await f.Recovery.Seed.Run("owner/alpha", recovery: f.Recovery.Recovery())).Error);
        var ordinary = await f.Recovery.Seed.Run("owner/alpha", recovery: f.Recovery.Recovery());
        Assert.Contains("held", ordinary.Error);
        var result = await f.Run();
        Assert.Equal(WorkState.Watch, result.State);
        Assert.Single(f.Prs.Opened);
        Assert.Equal(new[] { ReviewRoles.Correctness, ReviewRoles.SpecConformance }, f.Reviewer.Requests.Select(r => r.Role));
        Assert.Equal(2, f.Tests.Runs.Count);
        var rows = await f.Rows();
        Assert.Contains(rows, r => r.Step == RunPipeline.Steps.NewTests && r.Outcome == StepOutcome.Passed);
        Assert.Contains(rows, r => r.Step == RunPipeline.Steps.QueueTurn);
        Assert.Contains(rows, r => r.Step == "recovery-delivered" && r.Outcome == StepOutcome.Passed && r.Detail!.Contains("999999"));
        Assert.Single(f.GitHub.Calls, c => c.StartsWith("merge "));
        await using (var db = f.Recovery.Seed.Contexts.CreateDbContext())
        {
            Assert.Equal("Merged", (await db.RecoveryCorrections.SingleAsync()).State);
            Assert.NotEqual("Resolved", (await db.HotFileIncidents.SingleAsync()).State);
        }
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        Assert.Single(f.GitHub.Calls, c => c.StartsWith("merge "));
        Assert.Single(await f.Rows(), r => r.Step == "recovery-delivered");
        Assert.Contains("held", (await f.Recovery.Seed.Run("owner/alpha", recovery: f.Recovery.Recovery())).Error);
    }

    /// <summary>Counts every effective-control read, forwarding all to the ledger's controls.</summary>
    internal sealed class CountingControls(IControls inner) : IControls
    {
        internal int Checks;
        public Task<ControlState> EffectiveAsync(string externalId, long? epicId, CancellationToken ct)
        { Interlocked.Increment(ref Checks); return inner.EffectiveAsync(externalId, epicId, ct); }
        public Task<ControlState> EffectiveWithoutRepositoryAsync(string externalId, long? epicId, CancellationToken ct) =>
            inner.EffectiveWithoutRepositoryAsync(externalId, epicId, ct);
        public Task<ControlState> EffectiveInRepositoryAsync(string externalId, long? epicId, string repository, CancellationToken ct) =>
            inner.EffectiveInRepositoryAsync(externalId, epicId, repository, ct);
        public Task<IReadOnlyList<Control>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<Control?> UsagePauseAsync(CancellationToken ct) => inner.UsagePauseAsync(ct);
        public Task<Control> PauseForUsageAsync(DateTimeOffset? resumeAt, string reason, CancellationToken ct) => inner.PauseForUsageAsync(resumeAt, reason, ct);
        public Task<Control> FreezeAsync(string trigger, string detail, DateTimeOffset? readChangedAt, CancellationToken ct) => inner.FreezeAsync(trigger, detail, readChangedAt, ct);
        public Task<Control?> GetAsync(string scope, CancellationToken ct) => inner.GetAsync(scope, ct);
        public Task SetAsync(string scope, ControlState state, string by, CancellationToken ct) => inner.SetAsync(scope, state, by, ct);
        public Task ClearAsync(string scope, CancellationToken ct) => inner.ClearAsync(scope, ct);
    }

    [Fact]
    public async Task A_correction_captures_evidence_once_at_each_authority_boundary_and_never_in_its_in_loop_checks()
    {
        var f = new Fixture(); await f.Initialize();
        var counting = new CountingControls(f.Controls);
        f.ControlOverride = counting;
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        // Dispatch (before the item has a run), worker entry (Implement, before its session), push (Implement, the
        // worker done) and merge (MergeGate, after the write token): one capture each, and no other.
        Assert.Equal(["new", "Implement/-", "Implement/" + RunPipeline.Steps.WorkerDone, "MergeGate/" + RunPipeline.Steps.GatePassed], f.Captures);
        // Every step, slot/CI/overlay wait and gate read checked the composed controls structurally, capturing nothing.
        Assert.True(counting.Checks >= 10, $"only {counting.Checks} in-loop control checks ran");
    }

    [Fact]
    public async Task Delivered_bookkeeping_resumes_after_a_human_continue_without_fresh_execution_authority()
    {
        var f = new Fixture(); await f.Initialize();
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        await using (var db = f.Recovery.Seed.Contexts.CreateDbContext())
        {
            var item = await db.WorkItems.SingleAsync(i => i.ExternalId == $"rc-{f.Correction.Id}");
            // Recreate the valid crash boundary after reporting Merged but before the final Watch transition.
            db.LedgerEntries.Remove(await db.LedgerEntries.SingleAsync(e => e.WorkItemId == item.Id && e.Step == null && e.State == WorkState.Watch));
            item.State = WorkState.Merge; item.Version++;
            await db.SaveChangesAsync();
            await new WorkLedger(db, f.Recovery.Seed.Time).RecordAsync(item, WorkState.Paused, null, RunPipeline.RecoveryHeld, default);
        }
        await f.Controls.SetAsync("repo:owner/alpha", ControlState.Paused, "human", default);
        f.Material += " ";
        Assert.Equal(WorkState.Paused, (await f.Run()).State);
        await f.Controls.SetAsync("repo:owner/alpha", ControlState.Running, "human", default);
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        Assert.Single(f.GitHub.Calls, c => c.StartsWith("merge "));
        Assert.Single(await f.Rows(), r => r.Step == "recovery-delivered");
    }

    [Fact]
    public async Task Restart_observes_a_completed_authorized_merge_even_when_that_merge_moved_the_base()
    {
        var f = new Fixture(); await f.Initialize();
        using var interrupted = new CancellationTokenSource();
        f.GitHub.OnMergeCall = interrupted.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(interrupted.Token));
        Assert.True(f.GitHub.Merged);
        f.Material += " "; // A recapture is now different: completed work needs observation, not a fresh execution grant.
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        Assert.Single(f.Prs.Opened);
        Assert.Single(f.GitHub.Calls, c => c.StartsWith("merge "));
        Assert.Single(await f.Rows(), r => r.Step == "recovery-delivered");
    }

    [Theory]
    [InlineData("wrong-base")]
    [InlineData("external")]
    [InlineData("external-after-authorization")]
    public async Task An_external_or_wrong_base_merge_is_not_autonomous_delivery(string kind)
    {
        var f = new Fixture(); await f.Initialize();
        if (kind != "external")
        {
            using var interrupted = new CancellationTokenSource();
            f.GitHub.OnMergeCall = interrupted.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(interrupted.Token));
            if (kind == "wrong-base") f.GitHub.PullBaseRef = "other";
            else f.GitHub.MergedByGate = false;
        }
        else f.Prs.OnOpen = () =>
        {
            f.GitHub.Merged = true; f.GitHub.MergeCommitSha = "external";
            return Task.CompletedTask;
        };
        Assert.Equal(WorkState.Escalated, (await f.Run()).State);
        Assert.DoesNotContain(await f.Rows(), r => r.Step == "recovery-delivered");
    }

    [Fact]
    public async Task Head_change_before_review_invalidates_worker_delivery()
    {
        var f = new Fixture(); await f.Initialize();
        f.Prs.OnOpen = () => { f.GitHub.Head = GatePipelineTests.ShaA; return Task.CompletedTask; };
        Assert.Equal(WorkState.Escalated, (await f.Run()).State);
        Assert.Empty(f.Reviewer.Requests);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
    }

    [Theory]
    [InlineData("pull-base")]
    [InlineData("pull-base-sha")]
    [InlineData("pull-head")]
    public async Task Final_delivery_revalidates_the_actual_pull_binding(string change)
    {
        var f = new Fixture(); await f.Initialize();
        var changed = false;
        f.GitHub.Diff = _ =>
        {
            if (!changed && f.GitHub.Calls.Any(c => c.StartsWith("compare ")))
            {
                changed = true;
                if (change == "pull-base") f.GitHub.PullBaseRef = "other";
                if (change == "pull-base-sha") f.GitHub.PullBaseSha = "moved";
                if (change == "pull-head") f.GitHub.Head = GatePipelineTests.ShaA;
            }
            return string.Concat(f.Paths.Select(p => TestPolicies.Diff(p)));
        };
        await f.Run();
        Assert.True(changed);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
    }

    [Fact]
    public async Task Another_incident_cannot_borrow_this_corrections_authorization()
    {
        var f = new Fixture(); await f.Initialize();
        await using (var db = f.Recovery.Seed.Contexts.CreateDbContext())
        {
            db.HotFileIncidents.Add(new HotFileIncident { Repo = "owner/alpha", Generation = 2, State = "Held",
                Snapshot = JsonSerializer.Serialize(new HotFileSnapshot("other", "owner/alpha", 2, [], "other")), EvidenceHash = "other" });
            await db.SaveChangesAsync();
        }
        Assert.False(await f.Recovery.Recovery().AuthorizeCorrectionAsync($"rc-{f.Correction.Id}", "owner/alpha", default));
        Assert.NotNull((await f.Run()).Error);
        Assert.Empty(f.Prs.Opened);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("sha")]
    [InlineData("paths")]
    public async Task Boundary_grant_independently_refuses_wrong_base_tip_and_diff(string mismatch)
    {
        var f = new Fixture(); await f.Initialize();
        Assert.False(await f.Recovery.Recovery().AuthorizeCorrectionAsync($"rc-{f.Correction.Id}", "owner/alpha", default,
            mismatch == "name" ? "other" : "main", mismatch == "sha" ? "moved" : GatePipelineTests.FakeGateGitHub.BaseSha,
            mismatch == "paths" ? ["src/not-authorized.cs"] : f.Paths));
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Equal("Revoked", (await db.RecoveryCorrections.SingleAsync()).State);
        Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Step == RecoveryCorrections.RefusalStep && e.Outcome == StepOutcome.GateRejected);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("envelope")]
    public async Task Stale_or_outside_envelope_work_is_not_committed_or_pushed(string change)
    {
        var f = new Fixture(); await f.Initialize();
        f.AfterWorker = () =>
        {
            if (change == "evidence") f.Material += " "; else f.Paths = ["src/not-authorized.cs"];
            return Task.CompletedTask;
        };
        await f.Run();
        Assert.DoesNotContain(f.Workspaces.Calls, c => c.StartsWith("push "));
        Assert.Empty(f.Prs.Opened);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("paths")]
    [InlineData("grant")]
    [InlineData("human-pause")]
    [InlineData("human-stop")]
    public async Task A_change_during_final_merge_reads_prevents_delivery(string change)
    {
        var f = new Fixture(); await f.Initialize();
        // Change the answer only after the queue's base comparison: this is the final merge's diff read, not the gate's earlier reads.
        var changed = false;
        f.GitHub.Diff = _ =>
        {
            if (!changed && f.GitHub.Calls.Any(c => c.StartsWith("compare ")))
            {
                changed = true;
                if (change == "base") f.Material = JsonSerializer.Serialize(RecoverySnapshot.FromDetail(f.Material)! with
                    { BaseTips = new Dictionary<string, string> { ["main"] = "moved" } });
                if (change == "paths") f.Paths = ["src/not-authorized.cs"];
                if (change == "grant")
                {
                    using var db = f.Recovery.Seed.Contexts.CreateDbContext();
                    db.RecoveryCorrections.Single().State = "Revoked"; db.SaveChanges();
                }
                if (change.StartsWith("human-")) f.Controls.SetAsync(
                    change == "human-stop" ? $"item:rc-{f.Correction.Id}" : "repo:owner/alpha",
                    change == "human-stop" ? ControlState.Stopping : ControlState.Paused, "human", default).GetAwaiter().GetResult();
            }
            return string.Concat(f.Paths.Select(p => TestPolicies.Diff(p)));
        };
        await f.Run();
        Assert.True(changed, "Must reach the final merge reads, not stop at an earlier unrelated gate.");
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
        Assert.DoesNotContain(await f.Rows(), r => r.Step == "recovery-delivered");
    }

    [Theory]
    [InlineData("review-class")]
    [InlineData("ci")]
    public async Task Correction_has_no_exemption_from_high_class_review_or_CI(string failure)
    {
        var f = new Fixture(); await f.Initialize();
        if (failure == "review-class") f.Reviewer = new() { ServedClass = _ => "mid" };
        else
        {
            f.GitHub.Ci[GatePipelineTests.Sha1] = new(GatePipelineTests.Sha1, [new("build-test", true, "failure")]);
            f.GitHub.Ci[GatePipelineTests.FakeGateGitHub.BaseSha] = new(GatePipelineTests.FakeGateGitHub.BaseSha, [new("build-test", true, "failure")]);
        }
        Assert.Equal(WorkState.Escalated, (await f.Run()).State);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Equal("Failed", (await db.RecoveryCorrections.SingleAsync()).State);
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_vacuous_test_and_taint_rejections_are_not_exempted(bool tainted)
    {
        var f = new Fixture(); await f.Initialize();
        f.Tainted = tainted;
        if (!tainted) f.Tests.Answer = run => FakeTestRunner.Report(run.Steps, TestCaseResult.Passed);
        var result = await f.Run();
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Contains(tainted ? "tainted" : "already pass on the base", result.Error);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));
        if (tainted) Assert.Empty(f.Workspaces.Grants);
        else Assert.Contains(await f.Rows(), r => r.Step == RunPipeline.Steps.GateDecision && r.Outcome == StepOutcome.GateRejected);
        Assert.Contains(await f.Rows(), r => r.Step == RecoveryCorrections.StateStep && r.Outcome == StepOutcome.Failed);
        var opens = f.Prs.Opened.Count;
        await f.Run();
        Assert.Equal(opens, f.Prs.Opened.Count);
    }
}
