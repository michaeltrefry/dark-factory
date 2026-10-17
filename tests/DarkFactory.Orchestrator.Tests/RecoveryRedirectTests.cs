using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryRedirectTests
{
    internal static async Task SeedPending(RecoveryCorrectionPipelineTests.Fixture f, WorkState state = WorkState.Implement)
    {
        f.BeforeMaterialize = async material =>
        {
            await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
            var ledger = new WorkLedger(db, f.Recovery.Seed.Time);
            var item = await ledger.GetOrCreateAsync(ItemNaming.Shortcut.Source, "sc-77", "original request", "owner/alpha", "requirements", default);
            await ledger.RecordAsync(item, WorkState.Implement, null, "started", default);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Session, "old-session", "old assumptions", default);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, "old-session", "pid 12345", default);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerDone, "old-session", "done", default);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Pushed, "old-session", "factory/sc-77", default);
            if (state != WorkState.Implement)
            {
                await ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, "old-session", "https://github.com/owner/alpha/pull/77", default);
                await ledger.RecordAsync(item, WorkState.Review, "old-session", "old PR", default);
                if (state == WorkState.MergeGate)
                {
                    await ledger.RecordAsync(item, WorkState.CI, null, "old verdict", default);
                    await ledger.RecordAsync(item, WorkState.MergeGate, null, "old CI", default);
                }
            }
            await ledger.CheckpointAsync(item, RunPipeline.Steps.Verdict, null,
                ReviewPanel.Decide(GatePipelineTests.Sha1, [], ReviewRoles.Required(false).Select(role =>
                    new RoleReview(role, GatePipelineTests.PanelModel, "high", "old-review", "old-prompt", [], "old safe verdict")).ToArray()).ToDetail(), default);
            await ledger.CheckpointAsync(item, RunPipeline.Steps.GatePassed, null, GatePipelineTests.Sha1, default);
            await ledger.RecordAsync(item, WorkState.Paused, "old-session", RunPipeline.UserPaused, default);
            return material with { Evidence = [.. material.Evidence,
                new("pending:77", "pending", "ledger", "old-head", "hash", JsonSerializer.Serialize(new { item.ExternalId }))] };
        };
        await f.Initialize();
    }

    [Theory]
    [InlineData(WorkState.Implement)]
    [InlineData(WorkState.Review)]
    [InlineData(WorkState.MergeGate)]
    public async Task Overlapping_paused_work_rebuilds_from_the_corrected_base_without_resuming_its_old_conversation(WorkState from)
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await SeedPending(f, from);
        await using (var db = f.Recovery.Seed.Contexts.CreateDbContext())
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Step == RecoveryRedirect.Step);
        await f.Run();
        var assessor = new RecoveryReassessmentTests.Assessor(f);
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        var workspaces = new FakeWorkspaces();
        var reviewer = new GatePipelineTests.FakeReviewer();
        var calls = 0;
        var worker = new FakeWorker(async call =>
        {
            calls++;
            Assert.Null(call.Resume);
            Assert.Contains("Keep original requirements", call.Prompt);
            Assert.Contains("recovery-context", call.Prompt);
            Assert.Contains(new string('9', 40), call.Prompt);
            Assert.Contains("prepare owner/alpha factory/sc-77", workspaces.Calls);
            Assert.DoesNotContain(workspaces.Calls, c => c.StartsWith("reopen ") || c.StartsWith("restore "));
            await call.Callbacks.OnSession!("fresh-session", default);
            return new WorkerResult("fresh-session", 0, false, "success", "done", "");
        });
        await using var db2 = f.Recovery.Seed.Contexts.CreateDbContext();
        var story = new WorkStory(77, "original request", "Repo: owner/alpha\nKeep original requirements", "feature", "", ItemNaming.Shortcut);
        var github = new GatePipelineTests.FakeGateGitHub { Diff = _ => TestPolicies.Diff("docs/readme.md"),
            OnPullRead = reads => Assert.True(reads < 25, "Redirected work must progress, not loop on a stale head verdict.") };
        var result = await new RunPipeline(new FakeWorkSource(story), new WorkLedger(db2, f.Recovery.Seed.Time), f.Recovery.Seed.Locks,
            workspaces, worker, new FakePullRequests(), RepoRef.Parse("owner/alpha"), TextWriter.Null, recovery: recovery,
            controls: f.Controls, gate: new GateStage(github,
                reviewer, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2)))
            .RunAsync(77, default);
        Assert.Equal(1, calls);
        Assert.Equal(2, reviewer.Requests.Count); // The same head's pre-redirect verdict is not reused.
        Assert.Equal(WorkState.Watch, result.State);
        Assert.Contains(await db2.LedgerEntries.ToListAsync(), e => e.Step == RecoveryRedirect.Started && e.Detail!.Contains(new string('9', 40)));
    }

    [Fact]
    public async Task Changed_pending_requirements_after_safe_reassessment_rehold_instead_of_resuming_stale_work()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await SeedPending(f); await f.Run();
        var assessor = new RecoveryReassessmentTests.Assessor(f);
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        var before = RecoverySnapshot.FromDetail(f.Material)!;
        f.Material = JsonSerializer.Serialize(before with { Evidence = [..before.Evidence,
            new("spec:77", "spec", "board", null, "hash", "changed original requirements")] });
        Assert.Equal(WorkState.Paused, (await f.Recovery.Seed.Run("owner/alpha", 77, recovery)).State);
        Assert.NotNull(await recovery.HeldAsync("owner/alpha", default));
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => e.Step == RecoveryRedirect.Started);
    }

    [Theory]
    [InlineData(ControlState.Paused)]
    [InlineData(ControlState.Stopping)]
    public async Task Safe_reassessment_cannot_restart_a_manually_paused_or_stopped_item(ControlState control)
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await SeedPending(f); await f.Run();
        var assessor = new RecoveryReassessmentTests.Assessor(f);
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        await f.Controls.SetAsync("item:sc-77", control, "human", default);
        var result = await f.Recovery.Seed.Run("owner/alpha", 77, recovery);
        Assert.Equal(control == ControlState.Stopping ? WorkState.Cancelled : WorkState.Paused, result.State);
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.DoesNotContain(await db.LedgerEntries.ToListAsync(), e => e.Step == RecoveryRedirect.Started);
    }
}
