using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoverySnapshotTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "check-runs")]
    [InlineData(false, false, "status")]
    [InlineData(false, false, "check-suites")]
    public async Task Paged_CI_is_fetched_whole_or_a_missing_page_refuses_capture(bool missingPage, bool missingTotal, string? overlap)
    {
        var api = new FakeApi()
            .On("GET /repos/owner/alpha/installation", System.Net.HttpStatusCode.OK, "{\"id\":555}")
            .On("POST /app/installations/555/access_tokens", _ => FakeApi.Json(System.Net.HttpStatusCode.Created,
                $$"""{"token":"fixture","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}"""));
        foreach (var kind in new[] { "check-runs", "status", "check-suites" })
        {
            var field = kind == "check-runs" ? "check_runs" : kind == "status" ? "statuses" : "check_suites";
            var entry = kind == "check-runs" ? "{\"id\":1,\"name\":\"first\",\"status\":\"completed\",\"conclusion\":\"success\"}"
                : kind == "status" ? "{\"id\":1,\"context\":\"first\",\"state\":\"success\"}"
                : "{\"id\":1,\"status\":\"completed\",\"conclusion\":\"success\",\"latest_check_runs_count\":1,\"app\":{\"slug\":\"actions\"}}";
            api.On($"GET /repos/owner/alpha/commits/head/{kind}", request =>
            {
                var second = request.PathAndQuery.Contains("page=2", StringComparison.Ordinal);
                return FakeApi.Json(second && missingPage ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK,
                    missingTotal ? $"{{\"{field}\":[]}}" : second && missingPage ? "{}" : $"{{\"total_count\":2,\"{field}\":[{(second ? entry.Replace("first", "second").Replace("\"id\":1", kind == overlap ? "\"id\":1" : "\"id\":2") : entry)}]}}");
            });
        }
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var http = api.Client("https://api.github.com/");
        var gate = new DarkFactory.Orchestrator.GitHub.GitHubGate(http,
            new DarkFactory.Orchestrator.GitHub.GitHubApp(http, "4242", key.ExportRSAPrivateKeyPem(), TimeProvider.System));
        if (missingPage || missingTotal || overlap is not null) await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.GetRecoveryCiAsync(RepoRef.Parse(Repo), "head", CancellationToken.None));
        else
        {
            var facts = await gate.GetRecoveryCiAsync(RepoRef.Parse(Repo), "head", CancellationToken.None);
            Assert.True(facts.Complete);
            Assert.Equal(4, facts.Checks.Count);
            Assert.Contains(facts.Checks, c => c.Name == "second");
            Assert.Equal(2, facts.Suites!.Count);
            Assert.Equal(3, api.Requests.Count(r => r.PathAndQuery.EndsWith("page=2", StringComparison.Ordinal)));
        }
    }

    private const string Repo = "owner/alpha";
    private sealed class Fixture
    {
        public readonly LedgerDbContextFactory Contexts = new(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly GatePipelineTests.FakeGateGitHub GitHub = new() { Merged = true, MergeCommitSha = new string('9', 40) };
        public FakeWorkSource Source = new(new WorkStory(1, "requirements", "original spec", "chore", "board/story", ItemNaming.Shortcut))
        { Epic = new(9, "epic", "epic requirements", "board/epic"), Documents = [new("requirements", "document body", "board/doc")] };
        public HotFileSnapshot Trigger = null!;
        public HotFileEvidence Evidence => new(Contexts, GitHub, _ => Source);
        public async Task Seed()
        {
            await using var db = Contexts.CreateDbContext();
            var plan = Item("sc-10", WorkState.Planned);
            var merged = Item("sc-1", WorkState.Watch);
            var pending = Item("sc-2", WorkState.Review);
            db.AddRange(plan, merged, pending);
            await db.SaveChangesAsync();
            merged.PlanItemId = pending.PlanItemId = plan.Id;
            Add(plan, "plan", "recorded plan requirements");
            Add(merged, RunPipeline.Steps.Linked, $"https://github.com/{Repo}/pull/1");
            Add(merged, RunPipeline.Steps.Verdict, "executed verdict");
            Add(merged, RunPipeline.Steps.NewTests, "executed new tests");
            Add(merged, RunPipeline.Steps.GatePassed, GitHub.Head);
            Add(pending, RunPipeline.Steps.Linked, $"https://github.com/{Repo}/pull/2");
            var row = new LedgerEntry { WorkItemId = merged.Id, State = WorkState.Merge, Detail = GitHub.MergeCommitSha,
                Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow };
            db.Add(row);
            await db.SaveChangesAsync();
            Trigger = new("trigger", Repo, 1, [new(row.Id, merged.Id, row.Detail!, "main", ["src/x.cs"])], "merge-hash");
            void Add(WorkItem item, string step, string detail) => db.Add(new LedgerEntry { WorkItemId = item.Id,
                State = item.State, Step = step, Detail = detail, Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
        }
        private static WorkItem Item(string id, WorkState state) => new() { Source = "shortcut", ExternalId = id,
            Repo = Repo, Title = id, State = state, EpicId = 9, Version = 1 };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialized_plan_coordinator_keeps_production_evidence_authority(bool completed)
    {
        var f = new Fixture(); await f.Seed();
        await using (var db = f.Contexts.CreateDbContext())
        {
            var coordinator = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-1");
            coordinator.PlanItemId = null;
            coordinator.State = completed ? WorkState.Done : WorkState.Watch;
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10")).State = WorkState.Done;
            (await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-2")).PlanItemId = coordinator.Id;
            db.Add(new LedgerEntry { WorkItemId = coordinator.Id, State = coordinator.State, Step = "plan",
                Detail = "coordinator requirements", Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var material = await f.Evidence.CaptureAsync(f.Trigger, default);
        var snapshot = RecoverySnapshot.FromDetail(material)!;
        Assert.True(snapshot.Complete);
        Assert.Contains(snapshot.Evidence, e => e.Category == "plan" && e.Content.Contains("coordinator requirements"));
        var citation = snapshot.Evidence.First(e => e.Category == "merged").Id;
        var finding = new RecoveryFinding("merged", true, "Fix defect", "Count once", ["Counts once"],
            ["src/a.cs", "tests/CountTests.cs"], [citation], []);
        var answer = new HotFileAssessment(f.Trigger.Id, 1, Repo, "high", "unsafe", f.Trigger.EvidenceHash)
        {
            Material = material, MaterialSnapshot = snapshot.Id, MaterialHash = RecoverySnapshot.Hash(material), Session = "fresh-assessment",
            Citations = [citation], Findings = [finding], CorrectionFindings = [finding]
        };
        RecoveryCorrection correction;
        await using (var db = f.Contexts.CreateDbContext())
        {
            var incident = new HotFileIncident { Repo = Repo, Generation = 1, State = "Unsafe", Snapshot = JsonSerializer.Serialize(f.Trigger),
                EvidenceHash = f.Trigger.EvidenceHash, Answer = JsonSerializer.Serialize(answer), Session = answer.Session };
            db.Add(incident); await db.SaveChangesAsync();
            correction = await RecoveryCorrections.MaterializeAsync(db, incident, answer, GatePolicy.Parse(TestPolicies.Standard()), DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
        }
        Assert.Equal(material, await f.Evidence.CaptureAsync(f.Trigger, default));
        await using var recovery = new HotFileRecovery(f.Contexts, new InProcessRunLocks(), new(), TimeProvider.System,
            new HotFileRecoveryTests.Assessor(), capture: f.Evidence.CaptureAsync,
            policy: (_, _, _) => Task.FromResult<string?>(TestPolicies.Standard()));
        Assert.True(await recovery.AuthorizeCorrectionAsync($"rc-{correction.Id}", Repo, default));
        // A genuine requirement change remains semantic evidence and revokes the same grant.
        await using (var db = f.Contexts.CreateDbContext())
        {
            (await db.LedgerEntries.SingleAsync(e => e.WorkItemId == f.Trigger.Merges[0].Item && e.Step == "plan")).Detail = "changed requirements";
            await db.SaveChangesAsync();
        }
        Assert.False(await recovery.AuthorizeCorrectionAsync($"rc-{correction.Id}", Repo, default));
        await using var read = f.Contexts.CreateDbContext();
        Assert.Equal("Revoked", Assert.Single(read.RecoveryCorrections).State);
    }

    [Fact]
    public async Task Snapshot_pins_requirements_plan_diffs_executed_results_heads_and_hashes_and_is_durable_before_review()
    {
        var f = new Fixture(); await f.Seed();
        var material = await f.Evidence.CaptureAsync(f.Trigger, CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(material)!;
        Assert.True(snapshot.Complete);
        Assert.Equal(GatePipelineTests.FakeGateGitHub.BaseSha, Assert.Single(snapshot.Evidence, e => e.Category == "merged").BaseCommit);
        Assert.Equal(GatePipelineTests.FakeGateGitHub.BaseSha, snapshot.BaseTips["main"]);
        foreach (var category in new[] { "spec", "plan", "merged-diff", "executed-results", "ci", "pending" })
            Assert.Contains(snapshot.Evidence, e => e.Category == category);
        Assert.Contains("epic requirements", material);
        Assert.Contains("document body", material);
        Assert.Contains("recorded plan requirements", material);
        Assert.Contains("executed new tests", material);
        Assert.All(snapshot.Evidence, e =>
        {
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(e.Content))), e.Hash);
            Assert.NotEmpty(e.Provenance);
        });
        Assert.Contains(snapshot.Evidence, e => e.Category == "pending" && e.Content.Contains(f.GitHub.Head));
        Assert.Contains(f.GitHub.Calls, c => c == $"diff {GatePipelineTests.FakeGateGitHub.BaseSha}...{f.GitHub.Head}");
        await using var db = f.Contexts.CreateDbContext();
        Assert.Equal(material, (await db.LedgerEntries.SingleAsync(r => r.Step == RecoverySnapshot.Step)).Detail);
        Assert.Equal(material, await f.Evidence.CaptureAsync(f.Trigger, CancellationToken.None));
        Assert.Single(await db.LedgerEntries.Where(r => r.Step == RecoverySnapshot.Step).ToListAsync());
    }

    [Theory]
    [InlineData("ci-page", "ci")]
    [InlineData("spec-page", "spec")]
    [InlineData("timeout", "spec")]
    [InlineData("diff", "merged-diff")]
    [InlineData("bound", "context-bytes")]
    public async Task Missing_paged_inputs_or_context_bounds_are_explicit_and_never_complete(string missing, string category)
    {
        var f = new Fixture(); await f.Seed();
        if (missing == "ci-page") f.GitHub.Ci[f.GitHub.Head] = new(f.GitHub.Head, [], Complete: false);
        if (missing == "spec-page") f.Source.ReadSpecFails = new InvalidOperationException("page 2 unavailable");
        if (missing == "timeout") f.Source.ReadSpecFails = new TaskCanceledException("HTTP spec read timed out");
        if (missing == "diff") f.GitHub.ChangedFiles = 2;
        if (missing == "bound") f.Source.Epic = new(9, "epic", new string('x', RecoverySnapshot.MaxBytes), "board/epic");
        var snapshot = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        Assert.False(snapshot.Complete);
        Assert.Contains(snapshot.Missing, m => m.Category == category);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Evidence.CaptureAsync(f.Trigger, CancellationToken.None));
        Assert.Contains(category, error.Message);
        await using var db = f.Contexts.CreateDbContext();
        var row = await db.LedgerEntries.SingleAsync(e => e.Step == RecoverySnapshot.Step);
        Assert.Equal(StepOutcome.Failed, row.Outcome);
        Assert.Equal(row.Outcome, StepOutcomes.Of(null, row.State, row.Step, row.Detail));
    }

    [Theory]
    [InlineData("spec", "safe")]
    [InlineData("tip", "safe")]
    [InlineData("head", "safe")]
    [InlineData("version", "safe")]
    [InlineData("unlinked", "safe")]
    [InlineData("spec", "unsafe")]
    [InlineData("tip", "unsafe")]
    [InlineData("head", "unsafe")]
    [InlineData("version", "unsafe")]
    [InlineData("unlinked", "unsafe")]
    [InlineData("unclaimed", "safe")]
    [InlineData("unclaimed", "unsafe")]
    [InlineData("changeset", "safe")]
    [InlineData("changeset", "unsafe")]
    public async Task Changed_spec_tip_head_or_version_invalidates_snapshot_before_safe_result_acceptance(string change, string decision)
    {
        var f = new Fixture(); await f.Seed();
        if (change is "unclaimed" or "changeset") await RecordedChildren(f, true);
        if (change == "changeset")
        {
            await using var db = f.Contexts.CreateDbContext();
            var parent = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10");
            var changeset = new DarkFactory.Orchestrator.Plans.Changeset(Repo, "factory/changeset-sc-10", "main", "original");
            foreach (var (step, detail) in new[] { (RunPipeline.Steps.Changeset, changeset.ToDetail()),
                (RunPipeline.Steps.ChangesetPushed, DarkFactory.Orchestrator.Plans.Changeset.PushedDetail(changeset.Branch, "adopted", true)) })
                db.Add(new LedgerEntry { WorkItemId = parent.Id, State = parent.State, Step = step, Detail = detail,
                    Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        if (change == "unlinked")
        {
            await using var db = f.Contexts.CreateDbContext();
            var pending = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-2");
            pending.State = WorkState.Implement;
            var row = await db.LedgerEntries.SingleAsync(e => e.WorkItemId == pending.Id && e.Step == RunPipeline.Steps.Linked);
            row.Step = RunPipeline.Steps.Pushed;
            row.Detail = "factory/sc-2";
            await db.SaveChangesAsync();
            f.GitHub.Compare = head => new(head == "factory/sc-2" ? "pending-before" : GatePipelineTests.FakeGateGitHub.BaseSha, 0);
        }
        var response = JsonSerializer.Serialize(new { version = 1, snapshot = "trigger", generation = 1, repo = Repo,
            decision, evidenceHash = "merge-hash" });
        var api = new FakeApi().On("POST /v1/messages", _ =>
        {
            if (change == "unclaimed") f.Source.SpecFor = id => new(new WorkStory(id, "requirements",
                id == 3 ? "changed unclaimed child spec" : "original spec", "chore", "board/story", ItemNaming.Shortcut), f.Source.Epic, f.Source.Documents);
            if (change == "changeset") f.GitHub.Compare = head => new(head == "factory/changeset-sc-10" ? "admin-push" : GatePipelineTests.FakeGateGitHub.BaseSha, 0);
            if (change == "spec") f.Source.SpecFor = id => new(new WorkStory(id, "requirements",
                id == 2 ? "changed pending-only spec" : "original spec", "chore", "board/story", ItemNaming.Shortcut), f.Source.Epic, f.Source.Documents);
            if (change == "unlinked") f.GitHub.Compare = head => new(head == "factory/sc-2" ? "pending-after" : GatePipelineTests.FakeGateGitHub.BaseSha, 0);
            if (change == "tip") f.GitHub.Compare = _ => new("moved-base", 0);
            if (change == "head") f.GitHub.Head = "moved-head";
            if (change == "version")
            {
                using var db = f.Contexts.CreateDbContext();
                db.WorkItems.Single(i => i.ExternalId == "sc-2").Version++;
                db.SaveChanges();
            }
            return SseAnswers.Response(SseAnswers.Answer(response));
        });
        using var http = api.Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test-key"), f.Evidence.CaptureAsync);
        var result = await assessor.AssessAsync(f.Trigger, Guid.NewGuid().ToString(), CancellationToken.None);
        Assert.Equal("inconclusive", result.Decision);
        await using var saved = f.Contexts.CreateDbContext();
        Assert.Contains(await saved.LedgerEntries.Where(e => e.Step == RecoverySnapshot.Step).ToListAsync(), e => e.Detail == result.Material);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unclaimed_plan_children_and_cross_repository_parents_are_current_evidence(bool crossRepo)
    {
        var f = new Fixture(); await f.Seed();
        await RecordedChildren(f, crossRepo);
        var before = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        Assert.True(before.Complete);
        Assert.Contains(before.Evidence, e => e.Id == "pending:sc-3");
        Assert.DoesNotContain(before.Evidence, e => e.Id == "pending:sc-4");
        f.Source.SpecFor = id => new(new WorkStory(id, "requirements", id == 3 ? "edited unclaimed child" : "original spec",
            "chore", "board/story", ItemNaming.Shortcut), f.Source.Epic, f.Source.Documents);
        var after = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        Assert.True(after.Complete);
        Assert.NotEqual(before.Id, after.Id);
        Assert.Contains(after.Evidence, e => e.Id == "spec:sc-3" && e.Content.Contains("edited unclaimed child"));
        Assert.Empty(f.Source.Writes);
    }

    private static async Task RecordedChildren(Fixture f, bool crossRepo)
    {
        await using var db = f.Contexts.CreateDbContext();
        var parent = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10");
        if (crossRepo) parent.Repo = "owner/beta";
        var row = await db.LedgerEntries.SingleAsync(e => e.WorkItemId == parent.Id && e.Step == "plan");
        row.Detail = """
            {"stories":[
              {"key":"a","title":"alpha child","description":"alpha","acceptance":["works"],"type":"chore","repo":"owner/alpha","blocked_by":[]},
              {"key":"b","title":"beta child","description":"beta","acceptance":["works"],"type":"chore","repo":"owner/beta","blocked_by":[]}],
              "merge_order":["owner/alpha","owner/beta"],"interactions":"separate components"}
            """;
        db.Add(new LedgerEntry { WorkItemId = parent.Id, State = parent.State, Step = RunPipeline.Steps.PlanStories,
            Detail = "sc-3 sc-4", Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Retained_coding_is_incomplete_even_paused_or_with_an_old_published_attempt(bool oldPublication, bool started)
    {
        var f = new Fixture(); await f.Seed();
        await using (var db = f.Contexts.CreateDbContext())
        {
            var item = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-2");
            if (!oldPublication) db.RemoveRange(await db.LedgerEntries.Where(e => e.WorkItemId == item.Id).ToListAsync());
            else
            {
                Row(WorkState.Implement, RunPipeline.Steps.Pushed, "old-push");
                Row(WorkState.Escalated);
                Row(WorkState.Intake);
            }
            Row(WorkState.Implement);
            if (started) Row(WorkState.Implement, RunPipeline.Steps.WorkerStarted, "123");
            if (!oldPublication) Row(WorkState.Paused);
            item.State = oldPublication ? WorkState.Implement : WorkState.Paused;
            await db.SaveChangesAsync();
            void Row(WorkState state, string? step = null, string? detail = null) => db.Add(new LedgerEntry {
                WorkItemId = item.Id, State = state, Step = step, Detail = detail, Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
        }
        var snapshot = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        if (started)
        {
            Assert.False(snapshot.Complete);
            Assert.Contains(snapshot.Missing, m => m.Category == "pending" && m.Reason.Contains("Unpublished coding work"));
        }
        else
        {
            Assert.True(snapshot.Complete);
            var pending = Assert.Single(snapshot.Evidence, e => e.Category == "pending" && e.Content.Contains("sc-2"));
            using var json = JsonDocument.Parse(pending.Content);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("head").ValueKind);
            Assert.Equal("not-pushed", json.RootElement.GetProperty("headState").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changeset_remote_tip_is_captured_before_its_PR_even_for_an_adopted_cross_repo_plan(bool crossRepo)
    {
        var f = new Fixture(); await f.Seed();
        await RecordedChildren(f, crossRepo);
        await using (var db = f.Contexts.CreateDbContext())
        {
            var parent = await db.WorkItems.SingleAsync(i => i.ExternalId == "sc-10");
            var changeset = new DarkFactory.Orchestrator.Plans.Changeset(Repo, "factory/changeset-sc-10", "main", "original");
            foreach (var (step, detail) in new[] { (RunPipeline.Steps.Changeset, changeset.ToDetail()),
                (RunPipeline.Steps.ChangesetPushed, DarkFactory.Orchestrator.Plans.Changeset.PushedDetail(changeset.Branch, "adopted", true)) })
                db.Add(new LedgerEntry { WorkItemId = parent.Id, State = parent.State, Step = step, Detail = detail,
                    Outcome = StepOutcome.Passed, RecordedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        f.GitHub.Compare = head => new(head == "factory/changeset-sc-10" ? "actual-tip" : GatePipelineTests.FakeGateGitHub.BaseSha, 0);
        var before = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        Assert.True(before.Complete);
        Assert.Contains(before.Evidence, e => e.Category == "changeset-head" && e.Commit == "actual-tip");
        f.GitHub.Compare = head => new(head == "factory/changeset-sc-10" ? "admin-push" : GatePipelineTests.FakeGateGitHub.BaseSha, 0);
        var after = await f.Evidence.SnapshotAsync(f.Trigger, CancellationToken.None);
        Assert.True(after.Complete);
        Assert.NotEqual(before.Id, after.Id);
        Assert.Contains(after.Evidence, e => e.Category == "changeset-head" && e.Commit == "admin-push");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquisition_deadline_persists_incomplete_but_caller_shutdown_remains_cancellation(bool shutdown)
    {
        var f = new Fixture(); await f.Seed();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new WaitingSource(f.Source, entered);
        var time = new FakeTimeProvider();
        var evidence = new HotFileEvidence(f.Contexts, f.GitHub, _ => source, TimeSpan.FromMilliseconds(25), time);
        using var http = new FakeApi().Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test-key"), evidence.CaptureAsync);
        var capture = assessor.AssessAsync(f.Trigger, "deadline-session", caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!shutdown) time.Advance(TimeSpan.FromMilliseconds(25));
        if (shutdown)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => capture);
            Assert.Contains("acquisition-deadline", error.Message);
        }
        await using var db = f.Contexts.CreateDbContext();
        var saved = await db.LedgerEntries.Where(e => e.Step == RecoverySnapshot.Step).ToListAsync();
        if (shutdown) Assert.Empty(saved);
        else
        {
            var snapshot = RecoverySnapshot.FromDetail(Assert.Single(saved).Detail)!;
            Assert.False(snapshot.Complete);
            Assert.Contains(snapshot.Missing, m => m.Category == "acquisition-deadline");
            Assert.Equal(StepOutcome.Failed, saved[0].Outcome);
        }
    }

    private sealed class WaitingSource(FakeWorkSource inner, TaskCompletionSource entered) : IWorkSource
    {
        public ItemNaming Naming => inner.Naming;
        public async Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await inner.ReadSpecAsync(id, ct);
        }
        public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => inner.ListReadyAsync(ct);
        public Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct) => inner.ClaimAsync(id, ignoreScope, ct);
        public Task<bool> InScopeAsync(int id, CancellationToken ct) => inner.InScopeAsync(id, ct);
        public Task ValidateScopeAsync(CancellationToken ct) => inner.ValidateScopeAsync(ct);
        public Task ReleaseAsync(int id, CancellationToken ct) => inner.ReleaseAsync(id, ct);
        public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct) => inner.ReportStateAsync(id, state, comment, ct);
        public Task CommentAsync(int id, string text, CancellationToken ct) => inner.CommentAsync(id, text, ct);
        public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => inner.LinkAsync(id, urls, ct);
        public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct) => inner.CreateChildrenAsync(parentId, children, ct);
    }

    [Fact]
    public async Task Assessor_passes_only_shutdown_token_to_the_acquisition_deadline_owner()
    {
        var f = new Fixture(); await f.Seed();
        using var shutdown = new CancellationTokenSource();
        var captures = 0;
        var captured = await f.Evidence.SnapshotAsync(f.Trigger, shutdown.Token);
        var response = JsonSerializer.Serialize(new { version = 1, snapshot = f.Trigger.Id, generation = f.Trigger.Generation, repo = Repo,
            decision = "safe", evidenceHash = f.Trigger.EvidenceHash, materialSnapshot = captured.Id,
            citations = new[] { captured.Evidence[0].Id }, findings = Array.Empty<object>() });
        using var http = new FakeApi().On("POST /v1/messages", _ => SseAnswers.Response(SseAnswers.Answer(response)))
            .Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "test-key"), (_, token) =>
        {
            Assert.Equal(shutdown.Token, token);
            captures++;
            return Task.FromResult(JsonSerializer.Serialize(captured));
        });
        var answer = await assessor.AssessAsync(f.Trigger, "single-deadline", shutdown.Token);
        Assert.Equal("safe", answer.Decision);
        Assert.Equal(2, captures);
    }

}
