using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryReassessmentTests
{
    internal sealed class Assessor(RecoveryCorrectionPipelineTests.Fixture f) : IHotFileAssessor
    {
        internal readonly List<(HotFileSnapshot Snapshot, string Session)> Calls = [];
        internal string Decision = "safe";
        internal Action? During;
        internal string Material(HotFileSnapshot s) => JsonSerializer.Serialize(RecoverySnapshot.FromDetail(f.Material)! with
        {
            Id = "corrected-" + s.Id, IncidentSnapshot = s.Id,
            BaseTips = new Dictionary<string, string> { ["main"] = new string('9', 40) },
            Evidence = RecoverySnapshot.FromDetail(f.Material)!.Evidence.Concat(
                [new RecoveryEvidence("repair:1", "recovery", "ledger", new string('9', 40), "hash", "delivered " + new string('9', 40))])
                .DistinctBy(e => e.Id).ToArray()
        });
        public Task<HotFileAssessment> AssessAsync(HotFileSnapshot s, string session, CancellationToken ct)
        {
            Calls.Add((s, session));
            var material = Material(s);
            During?.Invoke();
            return Task.FromResult(f.Recovery.Answer with { Snapshot = s.Id, EvidenceHash = s.EvidenceHash,
                Session = session, Decision = Decision, Material = material, MaterialSnapshot = "corrected-" + s.Id,
                MaterialHash = RecoverySnapshot.Hash(material),
                Findings = Decision == "unsafe" ? f.Recovery.Answer.Findings : [],
                CorrectionFindings = Decision == "unsafe" ? f.Recovery.Answer.CorrectionFindings : [] });
        }
        internal HotFileRecovery Recovery() => new(f.Recovery.Seed.Contexts, f.Recovery.Seed.Locks,
            f.Recovery.Seed.Options, f.Recovery.Seed.Time, this, capture: (s, _) => Task.FromResult(Material(s)),
            policy: (_, _, _) => Task.FromResult<string?>(TestPolicies.Standard()));
    }

    [Theory]
    [InlineData("high", true)]
    [InlineData("mid", false)]
    public async Task Post_delivery_uses_the_production_router_stream_and_fresh_high_class_context(string servedClass, bool safe)
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize(); await f.Run();
        var materials = new Assessor(f);
        HotFileSnapshot? current = null;
        Task<string> Capture(HotFileSnapshot s, CancellationToken _) { current = s; return Task.FromResult(materials.Material(s)); }
        var api = new FakeApi().On("POST /v1/messages", request =>
        {
            Assert.Equal("high", request.Headers["x-weave-model-class"]);
            Assert.NotEqual("assessment", request.Headers["X-Claude-Code-Session-Id"]);
            Assert.Contains(new string('9', 40), request.Body);
            Assert.DoesNotContain("correction-session", request.Body);
            var s = current!;
            return AssessmentReads.Respond(request, JsonSerializer.Serialize(new { version = 1, snapshot = s.Id,
                generation = s.Generation, repo = s.Repo, decision = "safe", evidenceHash = s.EvidenceHash,
                materialSnapshot = "corrected-" + s.Id, citations = new[] { "merge:1" }, findings = Array.Empty<object>() }), modelClass: servedClass);
        });
        using var http = api.Client("http://router.test/");
        var assessor = new RouterHotFileAssessor(new RouterReviewer(http, "fixture"), Capture);
        await using var recovery = new HotFileRecovery(f.Recovery.Seed.Contexts, f.Recovery.Seed.Locks,
            f.Recovery.Seed.Options, f.Recovery.Seed.Time, assessor, capture: Capture);
        await recovery.AdvanceAsync("owner/alpha", default);
        // One assessment session (its read_evidence turns included).
        Assert.Single(api.Requests.Select(r => r.Headers[DarkFactory.Orchestrator.Gate.RouterReviewer.SessionHeader]).Distinct());
        Assert.Equal(safe, await recovery.HeldAsync("owner/alpha", default) is null);
    }

    [Fact]
    public async Task A_gate_delivered_repair_stays_held_until_a_fresh_assessment_of_its_merge_is_safe()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize();
        Assert.Equal(WorkState.Watch, (await f.Run()).State);
        var assessor = new Assessor(f);
        await using var recovery = assessor.Recovery();
        Assert.NotNull(await recovery.HeldAsync("owner/alpha", default));
        await recovery.AdvanceAsync("owner/alpha", default);
        var call = Assert.Single(assessor.Calls);
        Assert.NotEqual("assessment", call.Session);
        Assert.Contains(call.Snapshot.Merges, m => m.Commit == new string('9', 40));
        Assert.Null(await recovery.HeldAsync("owner/alpha", default));
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Single(assessor.Calls);
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Single(await db.HotFileIncidents.ToListAsync());
    }

    [Fact]
    public async Task An_unsafe_reassessment_creates_only_the_second_bounded_repair_with_delivery_context()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize();
        await f.Run();
        var assessor = new Assessor(f) { Decision = "unsafe" };
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        var corrections = await db.RecoveryCorrections.OrderBy(c => c.Attempt).ToListAsync();
        Assert.True(corrections.Count == 2, JsonSerializer.Serialize(await db.HotFileIncidents.SingleAsync()));
        Assert.Equal("Merged", corrections[0].State);
        Assert.Equal(2, corrections[1].Attempt);
        Assert.Contains(new string('9', 40), corrections[1].Spec);
        Assert.Contains("prior-recovery", corrections[1].Spec);
        Assert.Equal("Correcting", (await db.HotFileIncidents.SingleAsync()).State);
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Equal(2, await db.RecoveryCorrections.CountAsync());
        // The second grant really dispatches through the same delivery handlers, not just a row that no lane can consume.
        f.Correction = corrections[1];
        var incident = await db.HotFileIncidents.SingleAsync();
        f.Recovery.Answer = HotFileAssessment.FromDetail(incident.Answer)!;
        f.Material = f.Recovery.Answer.Material!;
        f.GitHub.Merged = false;
        f.GitHub.MergedByGate = false;
        f.GitHub.PullBaseSha = new string('9', 40);
        f.GitHub.ExpectedPolicySha = f.GitHub.PullBaseSha;
        f.Tests.BaseCommit = f.GitHub.PullBaseSha;
        var secondDelivery = await f.Run();
        Assert.True(secondDelivery.State == WorkState.Watch, secondDelivery.Error);
        Assert.Equal(2, f.Prs.Opened.Count);
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var read = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Equal(2, await read.RecoveryCorrections.CountAsync());
        Assert.Equal("Escalated", (await read.HotFileIncidents.SingleAsync()).State);
        Assert.NotNull(await recovery.HeldAsync("owner/alpha", default));
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("unsafe")]
    public async Task A_moved_base_cannot_authorize_either_release_or_another_repair(string decision)
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize(); await f.Run();
        var assessor = new Assessor(f) { Decision = decision };
        assessor.During = () => f.Material += " ";
        // Change a semantic field, not serialization whitespace, during assessment.
        var changed = false;
        await using var recovery = new HotFileRecovery(f.Recovery.Seed.Contexts, f.Recovery.Seed.Locks,
            f.Recovery.Seed.Options, f.Recovery.Seed.Time, assessor, capture: (s, _) =>
            {
                var m = RecoverySnapshot.FromDetail(assessor.Material(s))!;
                if (changed) m = m with { BaseTips = new Dictionary<string, string> { ["main"] = "moved" } };
                return Task.FromResult(JsonSerializer.Serialize(m));
            }, policy: (_, _, _) => Task.FromResult<string?>(TestPolicies.Standard()));
        assessor.During = () => changed = true;
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Single(assessor.Calls);
        Assert.NotNull(await recovery.HeldAsync("owner/alpha", default));
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Single(await db.RecoveryCorrections.ToListAsync());
    }

    [Fact]
    public async Task A_failed_correction_is_observed_once_not_refreshed_again_after_reassessment_exhaustion()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize();
        f.Tests.Answer = spec => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);
        Assert.Equal(WorkState.Escalated, (await f.Run()).State);
        var assessor = new Assessor(f) { Decision = "inconclusive" };
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        f.Recovery.Seed.Time.Advance(TimeSpan.FromSeconds(30));
        await recovery.AdvanceAsync("owner/alpha", default);
        f.Recovery.Seed.Time.Advance(TimeSpan.FromSeconds(120));
        await recovery.AdvanceAsync("owner/alpha", default);
        f.Recovery.Seed.Time.Advance(TimeSpan.FromSeconds(120));
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Equal(3, assessor.Calls.Count);
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Equal(3, assessor.Calls.Count);
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync()).State);
    }

    [Fact]
    public async Task Safe_reassessment_never_clears_a_human_repository_pause()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture(); await f.Initialize(); await f.Run();
        await f.Controls.SetAsync("repo:owner/alpha", ControlState.Paused, "human", default);
        var assessor = new Assessor(f);
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Null(await recovery.HeldAsync("owner/alpha", default));
        Assert.Equal(ControlState.Paused, await f.Controls.EffectiveInRepositoryAsync("sc-77", null, "owner/alpha", default));
    }
}
