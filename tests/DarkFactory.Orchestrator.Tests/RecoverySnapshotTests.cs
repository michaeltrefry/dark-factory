using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoverySnapshotTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Paged_CI_is_fetched_whole_or_a_missing_page_refuses_capture(bool missingPage, bool missingTotal)
    {
        var api = new FakeApi()
            .On("GET /repos/owner/alpha/installation", System.Net.HttpStatusCode.OK, "{\"id\":555}")
            .On("POST /app/installations/555/access_tokens", _ => FakeApi.Json(System.Net.HttpStatusCode.Created,
                $$"""{"token":"fixture","expires_at":"{{DateTimeOffset.UtcNow.AddMinutes(59):O}}"}"""));
        foreach (var kind in new[] { "check-runs", "status", "check-suites" })
        {
            var field = kind == "check-runs" ? "check_runs" : kind == "status" ? "statuses" : "check_suites";
            var entry = kind == "check-runs" ? "{\"id\":1,\"name\":\"first\",\"status\":\"completed\",\"conclusion\":\"success\"}"
                : kind == "status" ? "{\"context\":\"first\",\"state\":\"success\"}"
                : "{\"status\":\"completed\",\"conclusion\":\"success\",\"latest_check_runs_count\":1,\"app\":{\"slug\":\"actions\"}}";
            api.On($"GET /repos/owner/alpha/commits/head/{kind}", request =>
            {
                var second = request.PathAndQuery.Contains("page=2", StringComparison.Ordinal);
                return FakeApi.Json(second && missingPage ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK,
                    missingTotal ? $"{{\"{field}\":[]}}" : second && missingPage ? "{}" : $"{{\"total_count\":2,\"{field}\":[{(second ? entry.Replace("first", "second").Replace("\"id\":1", "\"id\":2") : entry)}]}}");
            });
        }
        using var key = System.Security.Cryptography.RSA.Create(2048);
        using var http = api.Client("https://api.github.com/");
        var gate = new DarkFactory.Orchestrator.GitHub.GitHubGate(http,
            new DarkFactory.Orchestrator.GitHub.GitHubApp(http, "4242", key.ExportRSAPrivateKeyPem(), TimeProvider.System));
        if (missingPage || missingTotal) await Assert.ThrowsAsync<InvalidOperationException>(
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

    [Fact]
    public async Task Snapshot_pins_requirements_plan_diffs_executed_results_heads_and_hashes_and_is_durable_before_review()
    {
        var f = new Fixture(); await f.Seed();
        var material = await f.Evidence.CaptureAsync(f.Trigger, CancellationToken.None);
        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(material)!;
        Assert.True(snapshot.Complete);
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
    public async Task Changed_spec_tip_head_or_version_invalidates_snapshot_before_safe_result_acceptance(string change, string decision)
    {
        var f = new Fixture(); await f.Seed();
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
}
