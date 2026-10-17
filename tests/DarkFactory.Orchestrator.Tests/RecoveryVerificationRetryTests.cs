using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Recovery;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests;

public sealed class RecoveryVerificationRetryTests
{
    private sealed class Assessor(RecoveryWorkSourceTests.Fixture f) : IHotFileAssessor
    {
        public Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct) =>
            Task.FromResult(f.Answer with { Session = session });
    }

    [Theory]
    [InlineData("second-capture-deadline")]
    [InlineData("second-capture-timeout")]
    [InlineData("policy-transport")]
    [InlineData("policy-gateway")]
    [InlineData("policy-deadline")]
    public async Task Transient_correction_verification_retries_with_durable_remaining_assessment_budget(string failure)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        var captures = 0;
        var fail = true;
        Task<string> Capture(HotFileSnapshot s, CancellationToken ct)
        {
            ++captures;
            if (captures == 2 && fail && failure == "second-capture-timeout") throw new TaskCanceledException("verification unavailable");
            if (captures == 2 && fail && failure == "second-capture-deadline")
            {
                f.Seed.Time.Advance(HotFileRecovery.StageTimeout);
                return Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<string>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
            }
            return Task.FromResult(f.Answer.Material!);
        }
        Task<string?> Policy(RepoRef repo, string sha, CancellationToken ct)
        {
            if (fail)
            {
                if (failure == "policy-transport") throw new HttpRequestException("temporarily unavailable");
                if (failure == "policy-gateway") throw new GitHubGatewayException("gateway unavailable", System.Net.HttpStatusCode.ServiceUnavailable);
                if (failure == "policy-deadline")
                {
                    // Verification shares the original stage deadline, not another full stage.
                    f.Seed.Time.Advance(HotFileRecovery.StageTimeout);
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith<string?>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default);
                }
            }
            return Task.FromResult<string?>(TestPolicies.Standard());
        }
        HotFileRecovery Driver() => new(f.Seed.Contexts, f.Seed.Locks, f.Seed.Options, f.Seed.Time,
            new Assessor(f), capture: Capture, policy: Policy);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var first = Driver())
        {
            var error = await Record.ExceptionAsync(() => first.AdvanceAsync("owner/alpha", shutdown.Token).WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.Null(error); // the stage's virtual deadline, not the host's safety cancellation, must end verification
        }
        await using (var read = f.Seed.Contexts.CreateDbContext())
        {
            var incident = await read.HotFileIncidents.SingleAsync();
            Assert.Equal("Held", incident.State);
            Assert.Equal(1, incident.Attempts);
            Assert.Equal(f.Seed.Time.GetUtcNow().AddSeconds(30), incident.NextAttemptAt);
            Assert.NotNull(incident.Answer);
            Assert.Single(await read.RecoveryAssessmentAttempts.ToListAsync());
            Assert.Empty(await read.RecoveryCorrections.ToListAsync());
            Assert.Empty(await read.LedgerEntries.Where(e => e.Step == RecoveryCorrections.RefusalStep).ToListAsync());
        }
        fail = false;
        f.Seed.Time.Advance(TimeSpan.FromSeconds(30));
        await using (var restarted = Driver()) await restarted.AdvanceAsync("owner/alpha", default);
        await using var db = f.Seed.Contexts.CreateDbContext();
        Assert.Equal("Correcting", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Equal(2, (await db.HotFileIncidents.SingleAsync()).Attempts);
        Assert.Equal(2, await db.RecoveryAssessmentAttempts.CountAsync());
        Assert.Equal(1, (await db.RecoveryCorrections.SingleAsync()).Attempt);
    }

    [Fact]
    public async Task Shutdown_during_second_verification_does_not_spend_an_assessment_or_refuse_a_correction()
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        using var stop = new CancellationTokenSource();
        var captures = 0;
        Task<string> Capture(HotFileSnapshot s, CancellationToken ct)
        {
            if (++captures == 2) { stop.Cancel(); throw new OperationCanceledException(ct); }
            return Task.FromResult(f.Answer.Material!);
        }
        await using var recovery = new HotFileRecovery(f.Seed.Contexts, f.Seed.Locks, f.Seed.Options, f.Seed.Time,
            new Assessor(f), capture: Capture, policy: (_, _, _) => Task.FromResult<string?>(TestPolicies.Standard()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.AdvanceAsync("owner/alpha", stop.Token));
        await using var db = f.Seed.Contexts.CreateDbContext();
        var incident = await db.HotFileIncidents.SingleAsync();
        Assert.Equal("Held", incident.State);
        Assert.Equal(0, incident.Attempts);
        Assert.Null(incident.NextAttemptAt);
        Assert.Equal("Cancelled", (await db.RecoveryAssessmentAttempts.SingleAsync()).State);
        Assert.Empty(await db.RecoveryCorrections.ToListAsync());
        Assert.Empty(await db.LedgerEntries.Where(e => e.Step == RecoveryCorrections.RefusalStep).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitive_invalid_envelope_or_policy_still_escalates_without_a_correction(bool invalidPolicy)
    {
        var f = new RecoveryWorkSourceTests.Fixture(); await f.Initialize();
        if (!invalidPolicy)
        {
            var finding = f.Answer.Findings.Single() with { Paths = ["CODEOWNERS"] };
            f.Answer = f.Answer with { Findings = [finding], CorrectionFindings = [finding] };
        }
        await using var recovery = new HotFileRecovery(f.Seed.Contexts, f.Seed.Locks, f.Seed.Options, f.Seed.Time,
            new Assessor(f), capture: (_, _) => Task.FromResult(f.Answer.Material!),
            policy: (_, _, _) => Task.FromResult<string?>(invalidPolicy ? "version: invalid" : TestPolicies.Standard()));
        await recovery.AdvanceAsync("owner/alpha", default);
        await using var db = f.Seed.Contexts.CreateDbContext();
        Assert.Equal("Escalated", (await db.HotFileIncidents.SingleAsync()).State);
        Assert.Empty(await db.RecoveryCorrections.ToListAsync());
        Assert.Single(await db.LedgerEntries.Where(e => e.Step == RecoveryCorrections.RefusalStep).ToListAsync());
    }
}
