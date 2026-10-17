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

/// <summary>
/// Epic sc-26335's integrated, non-live acceptance paths (sc-26345): each test chains the production recovery stages that the
/// per-story tests prove one at a time. docs/acceptance.md, "Autonomous hot-file recovery", maps every AT to its evidence.
/// </summary>
public sealed class RecoveryAcceptanceTests
{
    private const string Alpha = "owner/alpha", Beta = "owner/beta";
    private static readonly string Delivered = new('9', 40);

    // AT2: unsafe assessment (production scheduler) → exactly one correction item and PR through the ordinary gate → fresh
    // reassessment safe → the overlapping paused item restarts from the corrected base, and ordinary work resumes.
    [Fact]
    public async Task AT2_an_assessed_merged_defect_is_corrected_reassessed_safe_and_affected_work_restarts()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture { Assessed = true };
        await SeedOtherControls(f.Recovery.Seed);
        var before = await OtherControls(f.Recovery.Seed);
        await RecoveryRedirectTests.SeedPending(f);
        await using (var db = f.Recovery.Seed.Contexts.CreateDbContext())
        {
            var correction = Assert.Single(await db.RecoveryCorrections.ToListAsync());
            Assert.Equal(1, correction.Attempt);
            Assert.Equal(Alpha, correction.Repo);
            Assert.Equal("main", correction.Base);
            Assert.Equal("Correcting", (await db.HotFileIncidents.SingleAsync()).State);
            Assert.Single(await db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step).ToListAsync());
            Assert.Contains(await db.LedgerEntries.ToListAsync(), e => e.Step == RecoveryRedirect.Step);
        }
        await using (var held = f.Recovery.Recovery(() => f.Material))
            Assert.Contains("held", (await f.Recovery.Seed.Run(Alpha, 90, held)).Error);
        var correcting = await AssertStatusEqualsLedgerAsync(f.Recovery.Seed);
        Assert.Contains(correcting.Single().HeldItems, h => h.StartsWith("sc-77"));
        Assert.StartsWith("automatic repository hold", correcting.Single().Hold);

        var delivery = await f.Run();
        Assert.True(delivery.State == WorkState.Watch, delivery.Error);
        Assert.Single(f.Prs.Opened);
        Assert.Contains(await f.Rows(), r => r.Step == "recovery-delivered");
        var delivered = Assert.Single(Assert.Single(await AssertStatusEqualsLedgerAsync(f.Recovery.Seed)).Corrections);
        Assert.Equal("https://github.com/owner/alpha/pull/1", delivered.PullRequest);

        var assessor = new RecoveryReassessmentTests.Assessor(f);
        await using var recovery = assessor.Recovery();
        Assert.NotNull(await recovery.HeldAsync(Alpha, default)); // a gate merge is not itself a safe verdict (E10)
        await recovery.AdvanceAsync(Alpha, default);
        var call = Assert.Single(assessor.Calls);
        Assert.DoesNotContain(call.Session, new[] { "assessment", "correction-session", "old-session" });
        Assert.Null(await recovery.HeldAsync(Alpha, default));

        var workspaces = new FakeWorkspaces();
        var worker = new FakeWorker(async c =>
        {
            Assert.Null(c.Resume);
            Assert.Contains("recovery-context", c.Prompt);
            Assert.Contains(Delivered, c.Prompt);
            Assert.DoesNotContain(workspaces.Calls, w => w.StartsWith("reopen ") || w.StartsWith("restore "));
            await c.Callbacks.OnSession!("fresh-session", default);
            return new WorkerResult("fresh-session", 0, false, "success", "done", "");
        });
        await using var db2 = f.Recovery.Seed.Contexts.CreateDbContext();
        var story = new WorkStory(77, "original request", "Repo: owner/alpha\nKeep original requirements", "feature", "", ItemNaming.Shortcut);
        var restarted = await new RunPipeline(new FakeWorkSource(story), new WorkLedger(db2, f.Recovery.Seed.Time), f.Recovery.Seed.Locks,
            workspaces, worker, new FakePullRequests(), RepoRef.Parse(Alpha), TextWriter.Null, recovery: recovery, controls: f.Controls,
            gate: new GateStage(new GatePipelineTests.FakeGateGitHub { Diff = _ => TestPolicies.Diff("docs/readme.md") },
                new GatePipelineTests.FakeReviewer(), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2))).RunAsync(77, default);
        Assert.True(restarted.State == WorkState.Watch, restarted.Error);
        Assert.Equal(WorkState.Watch, (await f.Recovery.Seed.Run(Alpha, 91, recovery)).State);
        await using var read = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Single(await read.RecoveryCorrections.ToListAsync());
        Assert.Equal("Resolved", (await read.HotFileIncidents.SingleAsync()).State);
        var resolved = Assert.Single(await AssertStatusEqualsLedgerAsync(f.Recovery.Seed));
        Assert.Empty(resolved.HeldItems);
        Assert.Contains($"session {call.Session}", resolved.Assessment);
        // AT4: the global freeze row (its acknowledgements), the usage row and unrelated manual scopes survive byte for byte.
        Assert.Equal(before, await OtherControls(f.Recovery.Seed));
    }

    private static readonly string[] OtherScopes = [ControlScope.Freeze, ControlScope.Usage, "epic:999", "repo:owner/beta"];

    private static async Task SeedOtherControls(HotFileRecoveryTests.Fixture seed)
    {
        await using var db = seed.Contexts.CreateDbContext();
        var at = seed.Time.GetUtcNow().AddHours(-5);
        db.Controls.Add(new Control { Scope = ControlScope.Freeze, State = ControlState.Running, ChangedBy = "michael", ChangedAt = at,
            Reason = "main-red", Detail = $"red-tip owner/gamma@main {new string('c', 40)}\ncheck-failed ledger: TimeoutException" });
        db.Controls.Add(new Control { Scope = ControlScope.Usage, State = ControlState.Running, ChangedBy = "michael", ChangedAt = at,
            Reason = "router", ResumeAt = at.AddMinutes(30), Backoff = TimeSpan.FromMinutes(4) });
        db.Controls.Add(new Control { Scope = "epic:999", State = ControlState.Paused, ChangedBy = "michael", ChangedAt = at });
        db.Controls.Add(new Control { Scope = "repo:owner/beta", State = ControlState.Paused, ChangedBy = "michael", ChangedAt = at });
        await db.SaveChangesAsync();
    }

    private static async Task<string> OtherControls(HotFileRecoveryTests.Fixture seed)
    {
        await using var db = seed.Contexts.CreateDbContext();
        var rows = await db.Controls.AsNoTracking().Where(c => OtherScopes.Contains(c.Scope)).OrderBy(c => c.Scope).ToListAsync();
        Assert.Equal(OtherScopes.Length, rows.Count);
        return System.Text.Json.JsonSerializer.Serialize(rows.Select(c => new { c.Scope, c.State, c.ChangedBy, c.ChangedAt, c.Reason, c.Detail, c.ResumeAt, c.Backoff }));
    }

    // AT2: the first correction is really rejected by the ordinary gate (its new test already passes on the base); the fresh
    // reassessment is unsafe, so code authorizes exactly one second repair carrying the prior attempt, which then delivers.
    [Fact]
    public async Task AT2_a_gate_rejected_first_correction_gets_one_second_attempt_that_delivers_and_resolves()
    {
        var f = new RecoveryCorrectionPipelineTests.Fixture { Assessed = true };
        await f.Initialize();
        f.Tests.Answer = run => FakeTestRunner.Report(run.Steps, TestCaseResult.Passed);
        var rejected = await f.Run();
        Assert.Equal(WorkState.Escalated, rejected.State);
        Assert.Contains("already pass on the base", rejected.Error);
        Assert.DoesNotContain(f.GitHub.Calls, c => c.StartsWith("merge "));

        var assessor = new RecoveryReassessmentTests.Assessor(f) { Decision = "unsafe" };
        await using var recovery = assessor.Recovery();
        await recovery.AdvanceAsync(Alpha, default);
        await using var db = f.Recovery.Seed.Contexts.CreateDbContext();
        var corrections = await db.RecoveryCorrections.OrderBy(c => c.Attempt).ToListAsync();
        Assert.Equal(2, corrections.Count);
        Assert.Equal("Failed", corrections[0].State);
        Assert.Equal(2, corrections[1].Attempt);
        Assert.Contains("prior-recovery", corrections[1].Spec);
        Assert.Contains("repair attempt 2", corrections[1].Spec);
        await recovery.AdvanceAsync(Alpha, default);
        Assert.Equal(2, await db.RecoveryCorrections.CountAsync()); // replay mints no third authority

        f.Correction = corrections[1];
        f.Recovery.Answer = HotFileAssessment.FromDetail((await db.HotFileIncidents.AsNoTracking().SingleAsync()).Answer)!;
        f.Material = f.Recovery.Answer.Material!;
        f.Tests.Answer = run => FakeTestRunner.Report(run.Steps, run.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        f.GitHub.Merged = false;
        f.GitHub.MergedByGate = false;
        f.GitHub.PullBaseSha = Delivered;
        f.GitHub.ExpectedPolicySha = Delivered;
        f.Tests.BaseCommit = Delivered;
        var second = await f.Run();
        Assert.True(second.State == WorkState.Watch, second.Error);
        Assert.Equal(2, f.Prs.Opened.Count);

        assessor.Decision = "safe";
        await recovery.AdvanceAsync(Alpha, default);
        Assert.Null(await recovery.HeldAsync(Alpha, default));
        await using var read = f.Recovery.Seed.Contexts.CreateDbContext();
        Assert.Equal("Resolved", (await read.HotFileIncidents.SingleAsync()).State);
        Assert.Equal(2, await read.RecoveryCorrections.CountAsync());
        var both = Assert.Single(await AssertStatusEqualsLedgerAsync(f.Recovery.Seed)).Corrections;
        Assert.Equal(new[] { 1, 2 }, both.Select(c => c.Attempt));
    }

    private sealed class PerRepositoryAssessor : IHotFileAssessor
    {
        internal readonly Dictionary<string, string> Decisions = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Sessions = [];
        internal TaskCompletionSource? Gate;
        internal int InFlight, MaxInFlight;
        public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
        {
            lock (Sessions)
            {
                Sessions.Add(session);
                MaxInFlight = Math.Max(MaxInFlight, ++InFlight);
            }
            try
            {
                if (Gate is not null) await Gate.Task.WaitAsync(ct);
                return new(snapshot.Id, snapshot.Generation, snapshot.Repo, "high", Decisions[snapshot.Repo], snapshot.EvidenceHash);
            }
            finally { lock (Sessions) InFlight--; }
        }
    }

    // AT3: two simultaneous incidents in two repositories are assessed side by side with fresh sessions and reach independent
    // outcomes: A is safe and resumes while B's inconclusive answer keeps only B held, until B's own retry is safe.
    [Fact]
    public async Task AT3_two_simultaneous_incidents_in_two_repositories_have_independent_outcomes()
    {
        var f = new HotFileRecoveryTests.Fixture();
        for (var i = 0; i < 8; i++)
        {
            await f.Merge(Alpha);
            await f.Merge(Beta);
        }
        var assessor = new PerRepositoryAssessor { Decisions = { [Alpha] = "safe", [Beta] = "inconclusive" }, Gate = new() };
        await using var recovery = new HotFileRecovery(f.Contexts, f.Locks, f.Options, f.Time, assessor);
        var a = recovery.AdvanceAsync(Alpha, default);
        var b = recovery.AdvanceAsync(Beta, default);
        await PollUntil(() => assessor.Sessions.Count == 2);
        Assert.Equal(2, assessor.MaxInFlight);
        assessor.Gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Null(await recovery.HeldAsync(Alpha, default));
        Assert.NotNull(await recovery.HeldAsync(Beta, default));
        Assert.Equal(WorkState.Watch, (await f.Run(Alpha, 77, recovery)).State);
        Assert.Contains("held", (await f.Run(Beta, 78, recovery)).Error);

        assessor.Gate = null;
        assessor.Decisions[Beta] = "safe";
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await recovery.AdvanceAsync(Beta, default);
        Assert.Null(await recovery.HeldAsync(Beta, default));
        Assert.Equal(3, assessor.Sessions.Distinct().Count());

        await using var db = f.Contexts.CreateDbContext();
        var incidents = await db.HotFileIncidents.OrderBy(i => i.Repo).ToListAsync();
        Assert.Equal(new[] { Alpha, Beta }, incidents.Select(i => i.Repo));
        Assert.All(incidents, i => Assert.Equal(1, i.Generation));
        Assert.All(incidents, i => Assert.Equal("Resolved", i.State));
        Assert.Equal(new[] { 1, 2 }, incidents.Select(i => i.Attempts));
        Assert.Equal(2, (await AssertStatusEqualsLedgerAsync(f)).Count);
    }

    // AT5: each conjunct of the structural grant (dispatch, worker entry, push, merge and the control exemption all use it) refuses
    // on its own: one fact changed from a valid grant, nothing else. Mutation-checked: dropping any one conjunct fails its row.
    [Theory]
    [InlineData("valid")]
    [InlineData("state")]
    [InlineData("repository")]
    [InlineData("cancelled-item")]
    [InlineData("incident-state")]
    [InlineData("generation")]
    [InlineData("evidence-hash")]
    [InlineData("other-incident")]
    [InlineData("stopped-ancestor")]
    public async Task AT5_each_grant_predicate_alone_refuses_the_correction(string change)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var correction = await f.Materialize();
        var externalId = ItemNaming.Recovery.Format(correction.Id);
        var repo = Alpha;
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var c = await db.RecoveryCorrections.SingleAsync();
            var incident = await db.HotFileIncidents.SingleAsync();
            switch (change)
            {
                case "state": c.State = "Revoked"; break;
                case "repository": repo = Beta; break;
                case "cancelled-item":
                    db.WorkItems.Add(new WorkItem { Source = ItemNaming.Recovery.Source, ExternalId = externalId, Title = "repair",
                        Repo = Alpha, State = WorkState.Cancelled, CreatedAt = f.Seed.Time.GetUtcNow(), UpdatedAt = f.Seed.Time.GetUtcNow() });
                    break;
                case "incident-state": incident.State = "Unsafe"; break;
                case "generation": incident.Generation++; break;
                case "evidence-hash": incident.EvidenceHash = "other"; break;
                case "other-incident":
                    db.HotFileIncidents.Add(new HotFileIncident { Repo = Alpha, Generation = incident.Generation + 1, State = "Held",
                        Snapshot = incident.Snapshot, EvidenceHash = "next", Version = 1, CreatedAt = f.Seed.Time.GetUtcNow() });
                    break;
                case "stopped-ancestor":
                    var ancestor = await db.RecoveryAncestors.Where(a => a.CorrectionId == c.Id).Select(a => a.WorkItemId).FirstAsync();
                    (await db.WorkItems.SingleAsync(w => w.Id == ancestor)).State = WorkState.Cancelled;
                    break;
            }
            await db.SaveChangesAsync();
        }
        await using var read = f.Seed.Contexts.CreateDbContext();
        var granted = await RecoveryCorrections.Grants(read, externalId, repo).CountAsync();
        Assert.Equal(change == "valid" ? 1 : 0, granted);
    }

    // AT5: the assessment-binding predicates that authorize a merged repair, each changed alone (the stored answer is rewritten to
    // match, so the JSON-equality binding does not mask the predicate under test). Some are also refused by the decision parser;
    // the mutation table in docs/acceptance.md says which.
    [Theory]
    [InlineData("valid")]
    [InlineData("class")]
    [InlineData("repository")]
    [InlineData("session")]
    [InlineData("material-hash")]
    [InlineData("decision")]
    [InlineData("generation")]
    public async Task AT5_each_assessment_binding_alone_refuses_a_correction(string change)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var answer = change switch
        {
            "class" => f.Answer with { ServedClass = "mid" },
            "repository" => f.Answer with { Repo = Beta },
            "session" => f.Answer with { Session = "another-session" },
            "material-hash" => f.Answer with { MaterialHash = "0" },
            "decision" => f.Answer with { Decision = "safe" },
            "generation" => f.Answer with { Generation = f.Answer.Generation + 1 },
            _ => f.Answer,
        };
        await using var db = f.Seed.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        incident.Answer = System.Text.Json.JsonSerializer.Serialize(answer);
        var trigger = HotFileSnapshot.Parse(incident.Snapshot);
        if (change == "valid") Assert.NotNull(RecoveryCorrections.ValidateAssessment(incident, answer, trigger));
        else Assert.Throws<InvalidOperationException>(() => RecoveryCorrections.ValidateAssessment(incident, answer, trigger));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void AT5_the_correction_envelope_is_at_most_twenty_explicit_paths(int count, bool allowed)
    {
        var paths = Enumerable.Range(0, count).Select(i => $"src/f{i:D2}.cs").ToArray();
        var policy = GatePolicy.Parse(TestPolicies.Standard());
        if (allowed) RecoveryCorrections.ValidateEnvelope(paths, policy);
        else Assert.Throws<InvalidOperationException>(() => RecoveryCorrections.ValidateEnvelope(paths, policy));
    }

    // AT1: the window is inclusive at exactly HotFileWindow and exclusive one tick past it (explicit non-default settings).
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AT1_a_merge_exactly_one_window_old_still_counts_and_one_tick_older_does_not(long extraTicks, bool held)
    {
        var f = new HotFileRecoveryTests.Fixture { Options = new FreezeOptions { HotFileMerges = 2, HotFileWindow = TimeSpan.FromHours(3) } };
        await f.Merge();
        f.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromTicks(extraTicks));
        await f.Merge();
        Assert.Equal(held, await f.Recovery.HeldAsync(Alpha, default) is not null);
        Assert.Equal(8, new FreezeOptions().HotFileMerges);
        Assert.Equal(TimeSpan.FromHours(24), new FreezeOptions().HotFileWindow);
    }

    /// <summary>
    /// AT7: the operator projection the dashboard and `factory recovery status` render (S9's <see cref="RecoveryStatusReader"/>,
    /// <see cref="LedgerReport.RecoveryStatus"/>) states exactly what the ledger holds — per repository its latest incident's state,
    /// generation, attempt counts and corrections — and the CLI text carries the same facts with no raw model fence.
    /// </summary>
    private static async Task<IReadOnlyList<RepositoryRecoveryStatus>> AssertStatusEqualsLedgerAsync(HotFileRecoveryTests.Fixture seed)
    {
        var report = await new RecoveryStatusReader(seed.Contexts, seed.Time).ReadAsync(default);
        var text = LedgerReport.RecoveryStatus(report);
        await using var db = seed.Contexts.CreateDbContext();
        var latest = (await db.HotFileIncidents.AsNoTracking().ToListAsync())
            .GroupBy(i => i.Repo.ToLowerInvariant()).Select(g => g.MaxBy(i => i.Id)!).ToList();
        Assert.Equal(latest.Select(i => i.Id).Order(), report.Repositories.Select(r => r.Incident).Order());
        foreach (var incident in latest)
        {
            var row = report.Repositories.Single(r => r.Incident == incident.Id);
            var corrections = await db.RecoveryCorrections.AsNoTracking().Where(c => c.IncidentId == incident.Id).OrderBy(c => c.Attempt).ToListAsync();
            Assert.Equal(incident.Repo, row.Repo);
            Assert.Equal(incident.Generation, row.Generation);
            Assert.Equal(incident.State, row.Stage);
            var attempts = $"assessment {incident.Attempts}/3, correction {corrections.Select(c => c.Attempt).DefaultIfEmpty().Max()}/2, refresh {incident.Refreshes}/3";
            Assert.Equal(attempts, row.Attempts);
            Assert.Equal(corrections.Select(c => (ItemNaming.Recovery.Format(c.Id), c.Attempt)), row.Corrections.Select(c => (c.Item, c.Attempt)));
            Assert.All(corrections.Zip(row.Corrections), pair => Assert.StartsWith(pair.First.State, pair.Second.State));
            Assert.All(row.Corrections, c => Assert.True(c.PullRequest is null || c.PullRequest.StartsWith($"https://github.com/{incident.Repo}/pull/")));
            Assert.Equal(incident.State == "Resolved", row.Hold.StartsWith("automatic hold resolved"));
            Assert.Contains(incident.Repo, text);
            Assert.Contains(attempts, text);
            Assert.All(row.Corrections, c => Assert.Contains(c.Item, text));
        }
        Assert.DoesNotContain("<recovery-findings>", text);
        Assert.DoesNotContain("<prior-recovery>", text);
        return report.Repositories;
    }

    private static async Task PollUntil(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!done()) await Task.Delay(10, timeout.Token);
    }
}
