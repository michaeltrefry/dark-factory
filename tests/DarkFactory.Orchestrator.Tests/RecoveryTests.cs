using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

public class RecoveryTests
{
    private static async Task<Harness> Escalated()
    {
        var h = new Harness { Reviewer = FakeReviewer.Blocking() };
        Assert.Equal(WorkState.Escalated, (await h.Run()).State);
        Assert.Equal(3, (await h.Ledger.ContextAsync(await h.Db.WorkItems.SingleAsync(), default)).FixRounds);
        h.Reviewer = new FakeReviewer();
        h.GitHub.Head = ShaD;
        return h;
    }

    [Fact]
    public async Task Owner_adopts_repaired_head_without_worker_or_push_and_the_full_normal_gate_merges_it()
    {
        var h = await Escalated();
        var workers = h.WorkerCalls.Count;
        var pushes = h.Workspaces.Calls.Count(c => c.StartsWith("push"));
        var tests = h.TestRunner.Runs.Count;
        var outcome = await h.Pipeline().RecoverAsync(77, ShaD, "owner (cli)", default, "main");
        Assert.Null(outcome.Error);
        Assert.Equal(WorkState.Watch, outcome.State);
        Assert.Equal(ShaD, h.GitHub.Head);
        Assert.Equal(workers, h.WorkerCalls.Count);
        Assert.Equal(pushes, h.Workspaces.Calls.Count(c => c.StartsWith("push")));
        Assert.True(h.TestRunner.Runs.Count > tests);
        Assert.Contains(h.Reviewer.Requests, r => r.Role == ReviewRoles.Correctness);
        Assert.Contains(h.Reviewer.Requests, r => r.Role == ReviewRoles.SpecConformance);
        var rows = await h.Rows();
        var adopted = Assert.Single(rows.Where(r => r.Step is null && r.State == WorkState.Recovering));
        var audit = RunPipeline.Recovery.Of(rows)!;
        Assert.Equal("owner (cli)", audit.Owner);
        Assert.Equal(ShaD, audit.Head);
        Assert.Equal("factory/sc-77", audit.Branch);
        Assert.Equal("main", audit.Base);
        Assert.Equal(0, (await h.Ledger.ContextAsync(await h.Db.WorkItems.SingleAsync(), default)).FixRounds);
        Assert.Contains(rows, r => r.Id > adopted.Id && r.Step == RunPipeline.Steps.Verdict);
        Assert.Contains(rows, r => r.Id > adopted.Id && r.Step == RunPipeline.Steps.NewTests);
        Assert.Contains(rows, r => r.Id > adopted.Id && r.Step == RunPipeline.Steps.GatePassed && r.Detail == ShaD);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("verdict")]
    [InlineData("push")]
    [InlineData("closed")]
    [InlineData("merged")]
    [InlineData("draft")]
    [InlineData("branch")]
    [InlineData("fork")]
    [InlineData("base")]
    [InlineData("head")]
    [InlineData("unchanged")]
    [InlineData("taint")]
    [InlineData("link")]
    [InlineData("provenance")]
    [InlineData("worker")]
    [InlineData("repo")]
    [InlineData("state")]
    [InlineData("scope")]
    public async Task Unsafe_admission_refuses_without_changing_item_or_history(string problem)
    {
        var h = await Escalated();
        var item = await h.Db.WorkItems.SingleAsync();
        var head = ShaD;
        switch (problem)
        {
            case "claim":
            case "verdict":
            case "push":
                var step = problem == "claim" ? RunPipeline.Steps.Claimed : problem == "verdict" ? RunPipeline.Steps.Verdict : RunPipeline.Steps.Pushed;
                h.Db.LedgerEntries.RemoveRange(await h.Db.LedgerEntries.Where(r => r.Step == step).ToListAsync());
                await h.Db.SaveChangesAsync();
                break;
            case "closed": h.GitHub.Open = false; break;
            case "merged": h.GitHub.Merged = true; break;
            case "draft": h.GitHub.Draft = true; break;
            case "branch": h.GitHub.HeadRef = "factory/sc-78"; break;
            case "fork": h.GitHub.HeadRepo = "outsider/fork"; break;
            case "base": h.GitHub.BaseRef = "other"; break;
            case "head": h.GitHub.Head = ShaA; break;
            case "unchanged": head = ShaC; h.GitHub.Head = ShaC; break;
            case "taint":
                await h.Ledger.TaintSessionAsync(item, (await h.Rows()).Last(r => r.Step == RunPipeline.Steps.Pushed).ClaudeSessionId!, "web:WebFetch", default);
                break;
            case "link":
                h.Db.LedgerEntries.RemoveRange(await h.Db.LedgerEntries.Where(r => r.Step == RunPipeline.Steps.Linked).ToListAsync());
                await h.Db.SaveChangesAsync();
                break;
            case "provenance":
                foreach (var row in await h.Db.LedgerEntries.Where(r => r.Step == RunPipeline.Steps.Pushed).ToListAsync()) row.ClaudeSessionId = null;
                await h.Db.SaveChangesAsync();
                break;
            case "worker": await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.WorkerStarted, "unfinished", "123", default); break;
            case "repo": item.Repo = "owner/other"; await h.Db.SaveChangesAsync(); break;
            case "state": await h.Ledger.RecordAsync(item, WorkState.Intake, null, "ordinary rerun", default); break;
            case "scope": h.Stories.InScope = false; break;
        }
        var before = await h.Rows();
        var state = item.State;
        var updated = item.UpdatedAt;
        var workers = h.WorkerCalls.Count;
        var result = await h.Pipeline().RecoverAsync(77, head, "owner", default, "main");
        Assert.StartsWith("Recovery refused:", result.Error);
        Assert.Equal(before.Select(r => r.Id), (await h.Rows()).Select(r => r.Id));
        Assert.Equal(state, item.State);
        Assert.Equal(updated, item.UpdatedAt);
        Assert.Equal(workers, h.WorkerCalls.Count);
        Assert.Empty(h.Merges);
    }

    [Theory]
    [InlineData(ControlState.Paused, ControlScope.Factory)]
    [InlineData(ControlState.Stopping, "item:sc-77")]
    [InlineData(ControlState.Paused, "item:sc-77")]
    public async Task Held_control_refuses_even_stop_without_finishing_it(ControlState state, string scope)
    {
        var h = await Escalated();
        await h.Controls.SetAsync(scope, state, "owner", default);
        var before = (await h.Rows()).Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Contains("control", result.Error);
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_and_freeze_hold_recovery_admission(bool frozen)
    {
        var h = await Escalated();
        if (frozen) await h.Controls.FreezeAsync("test", "owner freeze", null, default);
        else await h.Controls.PauseForUsageAsync(DateTimeOffset.UtcNow.AddHours(1), "exhausted", default);
        var before = (await h.Rows()).Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Contains("control", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pause_or_stop_after_adoption_is_honored_by_normal_dispatch(bool stop)
    {
        var h = await Escalated();
        var reads = 0;
        h.GitHub.OnPullRead = _ =>
        {
            if (++reads == 3) h.Controls.SetAsync("item:sc-77", stop ? ControlState.Stopping : ControlState.Paused, "owner", default).GetAwaiter().GetResult();
        };
        var workers = h.WorkerCalls.Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(stop ? WorkState.Cancelled : WorkState.Paused, result.State);
        Assert.Empty(h.Reviewer.Requests);
        Assert.Empty(h.Merges);
        Assert.Equal(workers, h.WorkerCalls.Count);
        if (!stop)
        {
            await h.Controls.SetAsync("item:sc-77", ControlState.Running, "owner", default);
            h.GitHub.OnPullRead = null;
            Assert.Equal(WorkState.Watch, (await h.Run()).State);
            Assert.Equal(workers, h.WorkerCalls.Count);
        }
    }

    [Fact]
    public async Task A_control_that_arrives_during_admission_leaves_the_item_escalated()
    {
        var h = await Escalated();
        var before = (await h.Rows()).Count;
        h.GitHub.OnPullRead = _ => h.Controls.SetAsync("item:sc-77", ControlState.Stopping, "owner", default).GetAwaiter().GetResult();
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Contains("control", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Fact]
    public async Task A_new_recovery_attempt_has_its_own_fix_rounds_and_push_grants()
    {
        var h = await Escalated();
        h.Reviewer = new FakeReviewer { Findings = r => r.Pull.HeadSha == ShaD ? FakeReviewer.Blocking().Findings(r) : [] };
        h.Workspaces.OnPush = () => { h.GitHub.Head = ShaA; return Task.CompletedTask; };
        var workers = h.WorkerCalls.Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(WorkState.Watch, result.State);
        Assert.Equal(workers + 1, h.WorkerCalls.Count);
        Assert.Equal(1, (await h.Ledger.ContextAsync(await h.Db.WorkItems.SingleAsync(), default)).FixRounds);
        Assert.Contains(h.Workspaces.Grants.Last().Sessions, id => id is not null);
    }

    [Fact]
    public async Task Concurrent_recovery_uses_existing_item_run_lock()
    {
        var h = await Escalated();
        var item = await h.Db.WorkItems.SingleAsync();
        await using var held = await h.Locks.TryAcquireAsync(item.Id, default);
        var before = (await h.Rows()).Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Contains("already running", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Fact]
    public async Task Head_race_during_admission_refuses_without_a_recovery_row()
    {
        var h = await Escalated();
        var before = (await h.Rows()).Count;
        var reads = 0;
        h.GitHub.OnPullRead = _ => { if (++reads == 2) h.GitHub.Head = ShaA; };
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.StartsWith("Recovery refused:", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Fact]
    public async Task Head_race_after_durable_adoption_never_reviews_or_merges_the_wrong_head()
    {
        var h = await Escalated();
        var reads = 0;
        h.GitHub.OnPullRead = _ => { if (++reads == 3) h.GitHub.Head = ShaA; };
        var workers = h.WorkerCalls.Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Empty(h.Reviewer.Requests);
        Assert.Empty(h.Merges);
        Assert.Equal(workers, h.WorkerCalls.Count);
    }

    [Fact]
    public async Task Durable_boundary_resumes_after_interruption_with_no_old_verdict_or_gate_approval()
    {
        var h = await Escalated();
        var item = await h.Db.WorkItems.SingleAsync();
        // Old evidence for even the adopted SHA must not carry across the boundary.
        var old = (await h.Rows()).Last(r => r.Step == RunPipeline.Steps.Verdict);
        var verdict = ReviewVerdict.FromDetail(old.Detail)!;
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.Verdict, null, (verdict with { HeadSha = ShaD }).ToDetail(), default);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.GatePassed, null, ShaD, default);
        var audit = new RunPipeline.Recovery("owner", Sandbox.FullName, "factory/sc-77", "main", PrUrl, ShaC, ShaD);
        await h.Ledger.RecordAsync(item, WorkState.Recovering, null, audit.ToDetail(), default);
        Assert.Null(RunPipeline.GatePassedAt(await h.Rows(), new PullFacts(1, PrUrl, false, true, false, ShaD, "main", "base", "merged")));
        Assert.Empty(RunPipeline.Verdicts(await h.Rows()));
        var workers = h.WorkerCalls.Count;
        var result = await h.Run();
        Assert.Equal(WorkState.Watch, result.State);
        Assert.NotEmpty(h.Reviewer.Requests);
        Assert.Equal(workers, h.WorkerCalls.Count);
    }

    [Fact]
    public async Task A_crash_after_fixer_publication_before_pushed_checkpoint_recognizes_only_the_exact_intended_commit()
    {
        var h = await Escalated();
        h.Reviewer = new FakeReviewer { Findings = r => r.Pull.HeadSha == ShaD ? FakeReviewer.Blocking().Findings(r) : [] };
        h.Workspaces.OnPush = () => { h.GitHub.Head = ShaA; return Task.CompletedTask; };
        h.Workspaces.AfterPush = () => throw new FactoryUnavailableException("simulated crash after remote publication", new IOException("test"));
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main"));
        var rows = await h.Rows();
        var intent = rows.Last(r => r.Step == RunPipeline.Steps.RecoveryPushIntent);
        Assert.Equal(ShaA, BaseUpdate.FromDetail(intent.Detail)!.To);
        Assert.DoesNotContain(rows, r => r.Id > intent.Id && r.Step == RunPipeline.Steps.Pushed);
        var workers = h.WorkerCalls.Count;
        var pushes = h.Workspaces.Grants.Count;
        h.Workspaces.AfterPush = null;
        var result = await h.Run();
        Assert.Null(result.Error);
        Assert.Equal(WorkState.Watch, result.State);
        Assert.Equal(workers, h.WorkerCalls.Count);
        Assert.Equal(pushes, h.Workspaces.Grants.Count);
        Assert.Contains(await h.Rows(), r => r.Id > intent.Id && r.Step == RunPipeline.Steps.Pushed && r.Detail == ShaA);
    }

    [Fact]
    public async Task An_external_repair_during_a_recovered_fixer_is_never_force_overwritten()
    {
        var h = await Escalated();
        h.Reviewer = FakeReviewer.Blocking();
        var workers = h.WorkerCalls.Count;
        h.WorkerOverrides[workers] = async call =>
        {
            h.GitHub.Head = ShaA;
            return await ReportsModel(ImplementerModel)(call);
        };
        var before = (await h.Rows()).Last().Id;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Equal(WorkState.Escalated, result.State);
        Assert.Equal(ShaA, h.GitHub.Head);
        Assert.Contains("lease", result.Error);
        Assert.DoesNotContain(await h.Rows(), r => r.Id > before && r.Step == RunPipeline.Steps.Pushed);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public async Task A_pending_base_update_after_a_crash_resumes_from_the_still_unpushed_old_head()
    {
        var h = await Escalated();
        h.TestRunner.RunThrows = new FactoryUnavailableException("simulated process loss before the gate check", new IOException("test"));
        await Assert.ThrowsAsync<FactoryUnavailableException>(() => h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main"));
        var item = await h.Db.WorkItems.SingleAsync();
        Assert.Equal(WorkState.MergeGate, item.State);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.BaseUpdate, null,
            new BaseUpdate(BaseUpdate.Kinds.Update, ShaD, FakeGateGitHub.BaseSha, ShaA).ToDetail(), default);
        await h.Ledger.RecordAsync(item, WorkState.Paused, null, RunPipeline.Interrupted, default);
        h.TestRunner.RunThrows = null;
        h.GitHub.Compare = head => new BaseComparison(FakeGateGitHub.BaseSha, head == ShaD ? 1 : 0);
        h.Workspaces.MergeBase = _ => new BaseMerge(FakeGateGitHub.BaseSha, ShaA, []);
        h.Workspaces.OnFastForward = _ => { h.GitHub.Head = ShaA; return Task.CompletedTask; };
        var workers = h.WorkerCalls.Count;
        var result = await h.Run();
        Assert.Null(result.Error);
        Assert.Equal(WorkState.Watch, result.State);
        Assert.Equal(ShaA, h.GitHub.Head);
        Assert.Equal(workers, h.WorkerCalls.Count);
        Assert.Contains(await h.Rows(), r => r.Step == RunPipeline.Steps.RecoveryBasePushed && r.Detail == ShaA);
    }

    [Theory]
    [InlineData(null, "main", "main", false)]
    [InlineData("other", "main", "main", false)]
    [InlineData("release", "release", "main", false)]
    [InlineData("release", "release", "release", true)]
    public async Task Legacy_base_is_an_explicit_assertion_checked_against_the_actual_default(string? asserted, string actualBase, string actualDefault, bool accepted)
    {
        var h = await Escalated();
        h.GitHub.BaseRef = actualBase;
        h.GitHub.DefaultBranch = actualDefault;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, asserted);
        Assert.Equal(accepted ? WorkState.Watch : WorkState.Escalated, result.State);
        if (accepted) Assert.Contains("owner assertion", RunPipeline.Recovery.Of(await h.Rows())!.BaseEvidence);
        else Assert.StartsWith("Recovery refused:", result.Error);
    }

    [Fact]
    public async Task Live_ownership_refusal_is_read_only_and_is_checked_again_at_the_boundary()
    {
        var h = await Escalated();
        var before = (await h.Rows()).Count;
        h.GitHub.OnPullRead = _ => h.Stories.RecoveryClaimRefusal = "another claimant holds it";
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Contains("another claimant", result.Error);
        Assert.Equal(before, (await h.Rows()).Count);
    }

    [Fact]
    public async Task A_recovery_that_fails_before_its_first_verdict_can_be_recovered_again_without_reimplementation()
    {
        var h = await Escalated();
        var reads = 0;
        h.GitHub.OnPullRead = _ => { if (++reads == 3) h.GitHub.Head = ShaA; };
        Assert.Equal(WorkState.Escalated, (await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main")).State);
        h.GitHub.OnPullRead = null;
        h.GitHub.Head = ShaD;
        var workers = h.WorkerCalls.Count;
        Assert.Equal(WorkState.Watch, (await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main")).State);
        Assert.Equal(workers, h.WorkerCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pushed_plan_changeset_needs_no_implementer_provenance_and_keeps_its_pass_repository(bool anotherRepo)
    {
        var h = await Escalated();
        var item = await h.Db.WorkItems.SingleAsync();
        var repo = anotherRepo ? new RepoRef("other", "pass") : Sandbox;
        var branch = "factory/changeset-sc-77";
        var url = $"https://github.com/{repo}/pull/99";
        var changeset = new Orchestrator.Plans.Changeset(repo.FullName, branch, "main", "base0");
        h.Db.LedgerEntries.RemoveRange(await h.Db.LedgerEntries.Where(r => r.Step == RunPipeline.Steps.Pushed).ToListAsync());
        await h.Db.SaveChangesAsync();
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.Changeset, null, changeset.ToDetail(), default);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.ChangesetPushed, null, Orchestrator.Plans.Changeset.PushedDetail(branch, "base0", false), default);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.PlanStories, null, "sc-12", default);
        await h.Ledger.CheckpointAsync(item, RunPipeline.Steps.Linked, null, $"{url} https://github.com/{repo}/tree/{branch}", default);
        item.Repo = repo.FullName;
        await h.Db.SaveChangesAsync();
        h.GitHub.HeadRef = branch;
        h.GitHub.HeadRepo = repo.FullName;
        h.GitHub.PullUrl = url;
        var workers = h.WorkerCalls.Count;
        var result = await h.Pipeline().RecoverAsync(77, ShaD, "owner", default, "main");
        Assert.Null(result.Error);
        Assert.Equal(WorkState.Watch, result.State);
        Assert.All(h.Reviewer.Requests, request => Assert.Equal(repo.FullName, request.Repo));
        Assert.Equal(workers, h.WorkerCalls.Count);
        Assert.Contains("ledger changeset", RunPipeline.Recovery.Of(await h.Rows())!.BaseEvidence);
        Assert.Equal(repo.FullName, RunPipeline.Recovery.Of(await h.Rows())!.Repo);
    }

    [Fact]
    public async Task Ordinary_rerun_still_reimplements_after_escalation()
    {
        var h = await Escalated();
        h.Workspaces.OnPush = () => Task.CompletedTask;
        var workers = h.WorkerCalls.Count;
        await h.Run();
        Assert.True(h.WorkerCalls.Count > workers);
        Assert.DoesNotContain(await h.Rows(), r => r.State == WorkState.Recovering);
    }
}
