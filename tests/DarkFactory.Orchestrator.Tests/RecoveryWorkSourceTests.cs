using System.Text.Json;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryWorkSourceTests
{
    internal sealed class Fixture(DbContextOptions<LedgerDbContext>? options = null, IRunLocks? locks = null)
    {
        internal readonly HotFileRecoveryTests.Fixture Seed = new(options, locks);
        internal HotFileIncident Incident = null!;
        internal HotFileAssessment Answer = null!;
        internal readonly GatePolicy Policy = GatePolicy.Parse(TestPolicies.Standard());
        internal async Task Initialize(bool recordUnsafe = true)
        {
            for (var i = 0; i < 8; i++) await Seed.Merge();
            Incident = (await Seed.Recovery.HeldAsync("owner/alpha", default))!;
            var trigger = HotFileSnapshot.Parse(Incident.Snapshot);
            var material = new RecoverySnapshot("material", trigger.Id, trigger.Repo, trigger.Generation,
                new Dictionary<string, string> { ["main"] = new('a', 40) },
                [new("merge:1", "merged", "ledger", new('b', 40), "hash", "{}")], []);
            var finding = new RecoveryFinding("merged", true, "Fix the count", "Count once", ["Repeated input counts once"],
                ["src/a.cs", "tests/CountTests.cs"], ["merge:1"], []);
            Answer = new(trigger.Id, trigger.Generation, trigger.Repo, "high", "unsafe", trigger.EvidenceHash)
            {
                Material = JsonSerializer.Serialize(material), MaterialSnapshot = material.Id,
                MaterialHash = RecoverySnapshot.Hash(JsonSerializer.Serialize(material)), Session = "assessment", Prompt = "prompt",
                Citations = ["merge:1"], Findings = [finding], CorrectionFindings = [finding]
            };
            // AT2's integrated path leaves the incident Held so the production scheduler assesses and materializes it.
            if (!recordUnsafe) return;
            await using var db = Seed.Contexts.CreateDbContext();
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.Answer = JsonSerializer.Serialize(Answer);
            incident.State = "Unsafe";
            incident.Session = Answer.Session;
            incident.Version++;
            await db.SaveChangesAsync();
        }
        internal async Task<RecoveryCorrection> Materialize()
        {
            await using var db = Seed.Contexts.CreateDbContext();
            var incident = await db.HotFileIncidents.SingleAsync();
            incident.Answer = JsonSerializer.Serialize(Answer);
            var result = await RecoveryCorrections.MaterializeAsync(db, incident, Answer, Policy, Seed.Time.GetUtcNow(), default);
            await db.SaveChangesAsync();
            return result;
        }
        internal RecoveryWorkSource Source => new(Seed.Contexts, Seed.Time);
        internal HotFileRecovery Recovery(Func<string>? capture = null, string? policy = null) => new(Seed.Contexts, Seed.Locks,
            Seed.Options, Seed.Time, new FixedAssessor(this), capture: (_, _) => Task.FromResult(capture?.Invoke() ?? Answer.Material!),
            policy: (_, _, _) => Task.FromResult<string?>(policy ?? TestPolicies.Standard()));
        private sealed class FixedAssessor(Fixture f) : IHotFileAssessor
        {
            public Task<HotFileAssessment> AssessAsync(HotFileSnapshot trigger, string session, CancellationToken ct) =>
                Task.FromResult(f.Answer with { Session = session });
        }
    }

    [Fact]
    public async Task Replayed_merged_defect_has_one_immutable_spec_and_one_incident_authorization()
    {
        var f = new Fixture(); await f.Initialize();
        var first = await f.Materialize();
        var second = await f.Materialize();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Spec, second.Spec);
        await using var db = f.Seed.Contexts.CreateDbContext();
        var correction = Assert.Single(db.RecoveryCorrections);
        Assert.Equal(f.Incident.Id, correction.IncidentId);
        Assert.Equal(f.Incident.Generation, correction.Generation);
        Assert.Equal("main", correction.Base);
        Assert.Equal("owner/alpha", correction.Repo);
        Assert.Equal("Correcting", Assert.Single(db.HotFileIncidents).State);
        Assert.Equal(new[] { first.Id }, await f.Source.ListReadyAsync(default));
        Assert.True((await f.Source.ClaimAsync(first.Id, true, default)).Claimed);
        var spec = await f.Source.ReadSpecAsync(first.Id, default);
        Assert.Equal(ItemNaming.Recovery, spec.Story.Naming);
        Assert.True(spec.Story.UntrustedName);
        Assert.False(((IWorkSource)f.Source).PlansItems);
        Assert.Contains("Fix the count", spec.Story.Description);
        Assert.Equal("main", await f.Source.DeliveryBaseAsync(first.Id, default));
        Assert.Single(db.LedgerEntries.Where(e => e.Step == RecoveryCorrections.Step));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("factory/prompts/check.md")]
    [InlineData("CODEOWNERS")]
    [InlineData("infra/secret.cs")]
    [InlineData("src/*.cs")]
    public async Task Invalid_or_sealed_or_protected_envelope_writes_no_item(string path)
    {
        var f = new Fixture(); await f.Initialize();
        var finding = f.Answer.Findings[0] with { Paths = [path] };
        f.Answer = f.Answer with { Findings = [finding], CorrectionFindings = [finding] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Materialize());
        await using var db = f.Seed.Contexts.CreateDbContext();
        Assert.Empty(db.RecoveryCorrections);
    }

    [Fact]
    public async Task Pending_only_cannot_materialize_a_merged_repair()
    {
        var f = new Fixture(); await f.Initialize();
        f.Answer = f.Answer with { CorrectionFindings = [], Findings = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Materialize());
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("generation")]
    [InlineData("snapshot")]
    [InlineData("hash")]
    [InlineData("class")]
    public async Task Wrong_assessment_binding_is_not_authority(string field)
    {
        var f = new Fixture(); await f.Initialize();
        f.Answer = field switch
        {
            "repo" => f.Answer with { Repo = "owner/beta" },
            "generation" => f.Answer with { Generation = 9 },
            "snapshot" => f.Answer with { Snapshot = "another" },
            "hash" => f.Answer with { MaterialHash = "forged" },
            _ => f.Answer with { ServedClass = "low" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Materialize());
    }

    [Theory]
    [InlineData(10, 20, true)]
    [InlineData(11, 2, false)]
    [InlineData(1, 21, false)]
    public async Task Entire_repair_is_bounded_not_silently_truncated(int findings, int paths, bool allowed)
    {
        var f = new Fixture(); await f.Initialize();
        var all = Enumerable.Range(0, findings).Select(i => f.Answer.Findings[0] with
        {
            Issue = $"Defect {i}", Paths = Enumerable.Range(0, paths).Select(p => $"src/file{p}.cs").ToArray()
        }).ToArray();
        f.Answer = f.Answer with { Findings = all, CorrectionFindings = all };
        if (allowed)
        {
            var c = await f.Materialize();
            Assert.Equal(paths, JsonSerializer.Deserialize<string[]>(c.Paths)!.Length);
            Assert.Contains("Defect 9", c.Spec);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => f.Materialize());
    }

    [Fact]
    public async Task Stopped_contributing_plan_is_a_permanent_blocker_not_a_replacement_authorization()
    {
        var f = new Fixture(); await f.Initialize();
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var parent = new WorkItem { Source = "shortcut", ExternalId = "sc-99", Repo = "owner/alpha", Title = "stopped plan", State = WorkState.Cancelled };
            db.WorkItems.Add(parent); await db.SaveChangesAsync();
            (await db.WorkItems.FirstAsync(i => i.ExternalId == "sc-1")).PlanItemId = parent.Id;
            await db.SaveChangesAsync();
        }
        await using var recovery = f.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var read = f.Seed.Contexts.CreateDbContext();
        Assert.Empty(read.RecoveryCorrections);
        Assert.Equal("Escalated", Assert.Single(read.HotFileIncidents).State);
        Assert.Contains("stopped", Assert.Single(read.HotFileIncidents).Error);
        await recovery.AdvanceAsync("owner/alpha", default);
        Assert.Equal(1, Assert.Single(read.HotFileIncidents).Attempts);
    }

    [Fact]
    public async Task Stopped_correction_is_not_replaced_or_relisted()
    {
        var f = new Fixture(); await f.Initialize();
        var first = await f.Materialize();
        await f.Source.ReportStateAsync(first.Id, BoardState.Stopped, "human stop", default);
        var replay = await f.Materialize();
        Assert.Equal(first.Id, replay.Id);
        Assert.Empty(await f.Source.ListReadyAsync(default));
        Assert.False((await f.Source.ClaimAsync(first.Id, true, default)).Claimed);
        await using var db = f.Seed.Contexts.CreateDbContext();
        Assert.Equal("Escalated", Assert.Single(db.HotFileIncidents).State);
        Assert.Equal(StepOutcome.Escalated, db.LedgerEntries.Where(e => e.Step != IncidentTransitions.Step).OrderBy(e => e.Id).Last().Outcome);
    }
}

public sealed class RecoveryGrantTests
{
    [Fact]
    public async Task Production_recovery_materializes_unsafe_once_and_preserves_the_assessment_attempt()
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        await using var recovery = f.Recovery();
        await recovery.AdvanceAsync("owner/alpha", default);
        f.Seed.Time.Advance(TimeSpan.FromMinutes(10));
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var db = f.Seed.Contexts.CreateDbContext();
        var correction = Assert.Single(db.RecoveryCorrections);
        Assert.Equal(1, Assert.Single(db.HotFileIncidents).Attempts);
        Assert.Single(db.LedgerEntries.Where(e => e.Step == HotFileAssessment.Step));
        Assert.True(await recovery.AuthorizeCorrectionAsync($"rc-{correction.Id}", "owner/alpha", default));
        Assert.False(await recovery.AuthorizeCorrectionAsync($"rc-{correction.Id}", "owner/beta", default));
        Assert.False(await recovery.AuthorizeCorrectionAsync("sc-1", "owner/alpha", default));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("protected")]
    public async Task Fresh_evidence_and_current_policy_revoke_stale_authority(string change)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var correction = await f.Materialize();
        await using var recovery = f.Recovery(() => change == "protected" ? f.Answer.Material!
            : JsonSerializer.Serialize(RecoverySnapshot.FromDetail(f.Answer.Material)! with { BaseTips = new Dictionary<string, string> { ["main"] = new('c', 40) } }),
            change == "protected" ? TestPolicies.Standard().Replace("infra/", "infra/, src/") : null);
        Assert.False(await recovery.AuthorizeCorrectionAsync($"rc-{correction.Id}", "owner/alpha", default));
        await using var db = f.Seed.Contexts.CreateDbContext();
        Assert.Equal("Revoked", Assert.Single(db.RecoveryCorrections).State);
        Assert.Equal("Refreshing", Assert.Single(db.HotFileIncidents).State);
        Assert.Empty(await f.Source.ListReadyAsync(default));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("unreadable")]
    [InlineData("incomplete")]
    [InlineData("policy")]
    public async Task Unverifiable_authorization_defers_without_spending_the_existing_identity(string failure)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var c = await f.Materialize();
        var unavailable = true;
        await using var recovery = new HotFileRecovery(f.Seed.Contexts, f.Seed.Locks, f.Seed.Options, f.Seed.Time,
            f.Seed.Assessor, capture: (_, _) => failure == "capture" && unavailable
                ? throw new IOException("temporary evidence outage")
                : Task.FromResult(failure == "unreadable" && unavailable ? "unreadable" : failure == "incomplete" && unavailable
                    ? JsonSerializer.Serialize(RecoverySnapshot.FromDetail(f.Answer.Material)! with { Missing = [new("ci", "temporarily unavailable")] })
                    : f.Answer.Material!),
            policy: (_, _, _) => failure == "policy" && unavailable
                ? throw new IOException("temporary policy outage") : Task.FromResult<string?>(TestPolicies.Standard()));
        Assert.False(await recovery.AuthorizeCorrectionAsync($"rc-{c.Id}", "owner/alpha", default));
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            Assert.Equal("Pending", Assert.Single(db.RecoveryCorrections).State);
            Assert.Equal("Correcting", Assert.Single(db.HotFileIncidents).State);
        }
        unavailable = false;
        Assert.True(await recovery.AuthorizeCorrectionAsync($"rc-{c.Id}", "owner/alpha", default));
        Assert.Equal(c.Id, (await f.Materialize()).Id);
        Assert.Equal(new[] { c.Id }, await f.Source.ListReadyAsync(default));
    }

    [Theory]
    [InlineData(ControlState.Paused)]
    [InlineData(ControlState.Stopping)]
    public async Task Human_control_during_FINAL_worker_authorization_blocks_coding_and_finishes_Stop(ControlState state)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var c = await f.Materialize();
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        var worker = new RunPipelineTests.FakeWorker();
        var entered = false;
        await using var recovery = f.Recovery(() =>
        {
            // This run's preparation precedes its final session-entry capture. Ambient caller names say nothing
            // about this item's phase: a parallel test's continuation may still have RunSessionAsync on its stack.
            if (!entered && workspaces.Calls.Any(call => call.StartsWith("prepare ", StringComparison.Ordinal)))
            {
                entered = true;
                Assert.Empty(worker.Calls);
                using var read = f.Seed.Contexts.CreateDbContext();
                Assert.Equal(WorkState.Implement, read.WorkItems.Single(i => i.ExternalId == $"rc-{c.Id}").State);
                controls.SetAsync($"item:rc-{c.Id}", state, "human", default).GetAwaiter().GetResult();
            }
            return f.Answer.Material!;
        });
        await using var db = f.Seed.Contexts.CreateDbContext();
        // An unrelated caller with this name must not make dispatch look like final session entry.
        var outcome = await RunSessionAsync(() => new RunPipeline(f.Source, new WorkLedger(db, f.Seed.Time), f.Seed.Locks,
            workspaces, worker, new RunPipelineTests.FakePullRequests(),
            RepoRef.Parse("owner/alpha"), TextWriter.Null, controls: controls, recovery: recovery).RunAsync(c.Id, default));
        Assert.True(entered);
        Assert.Empty(worker.Calls);
        Assert.Equal(state == ControlState.Stopping ? WorkState.Cancelled : WorkState.Paused, outcome.State);
        if (state == ControlState.Stopping)
        {
            await using var read = f.Seed.Contexts.CreateDbContext();
            Assert.Equal("Stopped", Assert.Single(read.RecoveryCorrections).State);
            Assert.Null(await controls.GetAsync($"item:rc-{c.Id}", default));
        }
        else Assert.Equal("human", (await controls.GetAsync($"item:rc-{c.Id}", default))!.ChangedBy);
    }

    // Keep the ambient frame visible: the old fixture searched every caller, including unrelated test-runner continuations.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task<RunOutcome> RunSessionAsync(Func<Task<RunOutcome>> run) => run();

    [Theory]
    [InlineData("correction")]
    [InlineData("factory")]
    [InlineData("repo")]
    [InlineData("plan")]
    [InlineData("epic")]
    public async Task Public_Stop_settles_preintake_correction_and_never_replaces_it(string scope)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var plan = new WorkItem { Source = "shortcut", ExternalId = "sc-99", Title = "completed plan",
                Repo = "owner/alpha", State = WorkState.Done, EpicId = 42 };
            db.Add(plan); await db.SaveChangesAsync();
            (await db.WorkItems.FirstAsync(i => i.ExternalId == "sc-1")).PlanItemId = plan.Id;
            await db.SaveChangesAsync();
        }
        var c = await f.Materialize();
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        var actions = new ControlActions(controls, f.Seed.Contexts, new PublicStops(f, controls),
            naming => naming == ItemNaming.Recovery ? f.Source : new FakeWorkSource(
                new WorkStory(99, "plan", "requirements", "chore", "", ItemNaming.Shortcut)), f.Seed.Locks);
        var key = scope switch { "correction" => $"item:rc-{c.Id}", "repo" => "repo:owner/alpha", "plan" => "item:sc-99",
            "epic" => "epic:42", _ => "factory" };
        Assert.True((await actions.StopAsync(key, "human", default)).Ok);
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            Assert.Equal("Stopped", Assert.Single(db.RecoveryCorrections).State);
            Assert.Equal("Escalated", Assert.Single(db.HotFileIncidents).State);
        }
        Assert.Empty(await f.Source.ListReadyAsync(default));
        Assert.False((await f.Source.ClaimAsync(c.Id, true, default)).Claimed);
        Assert.Equal(c.Id, (await f.Materialize()).Id);
        await using var recovery = f.Recovery();
        Assert.False(await recovery.AuthorizeCorrectionAsync($"rc-{c.Id}", "owner/alpha", default));
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var final = f.Seed.Contexts.CreateDbContext();
        Assert.Equal("Stopped", Assert.Single(final.RecoveryCorrections).State);
    }

    private sealed class PublicStops(RecoveryWorkSourceTests.Fixture f, IControls controls) : IItemStops
    {
        public async Task<ControlResult> StopAsync(ItemRef item, CancellationToken ct)
        {
            await using var db = f.Seed.Contexts.CreateDbContext();
            IWorkSource source = item.Naming == ItemNaming.Recovery ? f.Source : new FakeWorkSource(
                new WorkStory(item.Id, "contributor", "requirements", "chore", "", item.Naming));
            return await new ItemStopper(source, new WorkLedger(db, f.Seed.Time), f.Seed.Locks,
                new RunPipelineTests.FakePullRequests(), controls, TextWriter.Null).StopAsync(item.Id, ct);
        }
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("escape")]
    [InlineData("sealed")]
    [InlineData("plan-pause")]
    [InlineData("fresh", 1)]
    public async Task Pipeline_refuses_wrong_or_held_corrections_before_any_coding(string kind, int expectedCalls = 0)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var parent = new WorkItem { Source = "shortcut", ExternalId = "sc-99", Title = "parent", Repo = "owner/alpha", State = WorkState.Planned };
            db.WorkItems.Add(parent); await db.SaveChangesAsync();
            (await db.WorkItems.FirstAsync(i => i.ExternalId == "sc-1")).PlanItemId = parent.Id;
            await db.SaveChangesAsync();
        }
        var correction = await f.Materialize();
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var c = await db.RecoveryCorrections.SingleAsync();
            if (kind == "repo") c.Spec = c.Spec.Replace("Repo: owner/alpha", "Repo: owner/beta");
            if (kind == "escape") c.Paths = "[\"../escape.cs\"]";
            if (kind == "sealed") c.Paths = "[\"factory/gate.yaml\"]";
            await db.SaveChangesAsync();
        }
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        if (kind == "plan-pause") await controls.SetAsync("item:sc-99", ControlState.Paused, "human", default);
        await using var recovery = f.Recovery();
        var worker = new RunPipelineTests.FakeWorker(RunPipelineTests.Reports(new("repair-session", 1, true, "error", "fixture ends after worker entry", "")));
        var workspaces = new RunPipelineTests.FakeWorkspaces();
        await using var ledger = f.Seed.Contexts.CreateDbContext();
        var outcome = await new RunPipeline(f.Source, new WorkLedger(ledger, f.Seed.Time), f.Seed.Locks,
            workspaces, worker, new RunPipelineTests.FakePullRequests(), RepoRef.Parse("owner/alpha"), TextWriter.Null,
            controls: controls, recovery: recovery, ignoreScope: true).RunAsync(correction.Id, default);
        Assert.Equal(expectedCalls, worker.Calls.Count);
        if (expectedCalls == 0)
        {
            Assert.Empty(workspaces.Calls);
            if (kind != "plan-pause")
                Assert.Equal(StepOutcome.GateRejected, Assert.Single(ledger.LedgerEntries.Where(e => e.Step == RecoveryCorrections.RefusalStep)).Outcome);
        }
        else
        {
            Assert.Equal(WorkState.Escalated, outcome.State);
            Assert.Contains("<triage>", worker.Calls[0].Prompt);
        }
        if (kind == "plan-pause") Assert.Equal(ControlState.Paused, (await controls.GetAsync("item:sc-99", default))!.State);
    }

    [Fact]
    public async Task Human_pause_arriving_during_fresh_authorization_still_prevents_worker_dispatch()
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var c = await f.Materialize();
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        var reads = 0;
        await using var recovery = f.Recovery(() =>
        {
            if (Interlocked.Increment(ref reads) == 1)
                controls.SetAsync("repo:owner/alpha", ControlState.Paused, "human", default).GetAwaiter().GetResult();
            return f.Answer.Material!;
        });
        var worker = new RunPipelineTests.FakeWorker();
        await using var db = f.Seed.Contexts.CreateDbContext();
        await new RunPipeline(f.Source, new WorkLedger(db, f.Seed.Time), f.Seed.Locks, new RunPipelineTests.FakeWorkspaces(),
            worker, new RunPipelineTests.FakePullRequests(), RepoRef.Parse("owner/alpha"), TextWriter.Null,
            controls: controls, recovery: recovery).RunAsync(c.Id, default);
        Assert.True(reads > 0);
        Assert.Empty(worker.Calls);
        var pause = await controls.GetAsync("repo:owner/alpha", default);
        Assert.Equal(ControlState.Paused, pause!.State);
        Assert.Equal("human", pause.ChangedBy);
        Assert.Empty(db.WorkItems.Where(i => i.Source == ItemNaming.Recovery.Source));
    }

    [Fact]
    public async Task Registry_and_controls_resolve_rc_without_any_board_credential_or_http_client()
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var correction = await f.Materialize();
        var options = new FactoryOptions(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new InMemorySecrets(), environment: _ => null);
        using var registry = new WorkSourceRegistry(options, _ => throw new InvalidOperationException("No HTTP for recovery"), f.Seed.Contexts);
        var source = Assert.IsType<RecoveryWorkSource>(registry.Of(ItemNaming.Recovery));
        Assert.Same(source, registry.Find(ItemNaming.Recovery));
        Assert.Equal(ItemNaming.Recovery, ItemNaming.ParseAny($"rc-{correction.Id}")!.Naming);
        Assert.True(ControlScope.IsValid($"item:rc-{correction.Id}"));
        Assert.Empty(registry.Configured);
        Assert.Contains(registry.Lanes(_ => new EmptyRunner(), new IntakeStatus(f.Seed.Time), TextWriter.Null), l => l.Source == source);
        Assert.True((await source.ReadSpecAsync(correction.Id, default)).Story.UntrustedName);
    }
    private sealed class EmptyRunner : IItemRunner
    {
        public Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<RunOutcome> RunAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Postgres_retains_grant_and_composed_controls_and_refuses_duplicate_attempts_and_stale_writers()
    {
        await using var database = await TempPostgresDatabase.CreateAsync("df_corrections");
        await LedgerMigrations.MigrateAsync(database.ConnectionString, default);
        var f = new RecoveryWorkSourceTests.Fixture(LedgerDbContext.PostgresOptions(database.ConnectionString), new PostgresRunLocks(database.ConnectionString));
        await f.Initialize();
        var first = await f.Materialize();
        Assert.Equal(first.Id, (await f.Materialize()).Id);
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        Assert.Equal(ControlState.Running, await controls.EffectiveInRepositoryAsync($"rc-{first.Id}", null, "owner/alpha", default));
        await controls.SetAsync("item:sc-1", ControlState.Paused, "human", default);
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync($"rc-{first.Id}", null, "owner/alpha", default));
        await using var stale = f.Seed.Contexts.CreateDbContext();
        var old = await stale.RecoveryCorrections.SingleAsync();
        await f.Source.ReportStateAsync(first.Id, BoardState.Stopped, "human", default);
        old.State = "Pending"; old.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var duplicate = f.Seed.Contexts.CreateDbContext();
        var copy = JsonSerializer.Deserialize<RecoveryCorrection>(JsonSerializer.Serialize(first))!;
        copy.Id = 0; duplicate.RecoveryCorrections.Add(copy);
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Empty(await f.Source.ListReadyAsync(default));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("plan")]
    [InlineData("epic")]
    [InlineData("repo")]
    [InlineData("factory")]
    [InlineData("correction")]
    [InlineData("usage")]
    [InlineData("freeze")]
    public async Task Own_incident_exception_does_not_bypass_any_manual_or_other_control(string scope)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            var parent = new WorkItem { ExternalId = "sc-99", Source = "shortcut", Title = "plan", Repo = "owner/alpha", State = WorkState.Planned };
            db.WorkItems.Add(parent); await db.SaveChangesAsync();
            var contributor = await db.WorkItems.FirstAsync(i => i.ExternalId == "sc-1");
            contributor.PlanItemId = parent.Id; contributor.EpicId = 42;
            await db.SaveChangesAsync();
        }
        var correction = await f.Materialize();
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        Assert.Equal(ControlState.Running, await controls.EffectiveInRepositoryAsync($"rc-{correction.Id}", null, "owner/alpha", default));
        var key = scope switch { "item" => "item:sc-1", "plan" => "item:sc-99", "epic" => "epic:42", "repo" => "repo:owner/alpha", "correction" => $"item:rc-{correction.Id}", _ => scope };
        await controls.SetAsync(key, ControlState.Paused, "human", default);
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync($"rc-{correction.Id}", null, "owner/alpha", default));
        Assert.Empty(await f.Source.ListReadyAsync(default));
        Assert.Equal(ControlState.Paused, (await controls.GetAsync(key, default))!.State);
    }

    [Fact]
    public async Task Ordinary_work_and_another_incident_get_no_exception()
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var correction = await f.Materialize();
        var controls = new LedgerControls(f.Seed.Contexts, f.Seed.Time);
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync("sc-77", null, "owner/alpha", default));
        await using (var db = f.Seed.Contexts.CreateDbContext())
        {
            db.HotFileIncidents.Add(new HotFileIncident { Repo = "owner/alpha", Generation = 2, State = "Held", Snapshot = "other", EvidenceHash = "other" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(ControlState.Paused, await controls.EffectiveInRepositoryAsync($"rc-{correction.Id}", null, "owner/alpha", default));
        Assert.Empty(await f.Source.ListReadyAsync(default));
    }
}
