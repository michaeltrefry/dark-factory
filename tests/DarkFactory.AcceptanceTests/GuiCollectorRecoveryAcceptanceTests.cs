using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;

namespace DarkFactory.AcceptanceTests;

/// <summary>Actual fixed collector recovery exercise over the production Gate-App-only transport. No hand-authored result
/// or source program can satisfy this test. The journal test is crash-equivalent, explicitly not a literal SIGKILL.</summary>
public sealed class GuiCollectorRecoveryAcceptanceTests
{
    [Fact]
    public async Task The_real_collector_reconciles_an_unclean_journal_blocks_uncertain_cleanup_and_restores_fresh_reuse()
    {
        Harness.RequireOptIn();
        var settings = Harness.Options.GuiWorkflow;
        if (settings is null) Assert.Skip("Configure Gate:Gui:CollectorRepo, Gate:Gui:TrustedRepos and Gate:Gui:ConfigurationSha256 for the installed private GUI collector.");
        var appId = Harness.RequireSecret(o => o.GitHubGateAppId);
        var key = Harness.RequireSecret(o => o.GitHubGateAppPrivateKeyPem);
        using var http = OutboundHttp.GitHubApi();
        var app = new GitHubApp(http, appId, key, TimeProvider.System);
        var result = await new GuiCollectorAcceptance(http, app).RunAsync(settings!, TestContext.Current.CancellationToken);
        Assert.True(result.RunId > 0);
        Assert.Matches("^[0-9a-f]{32}$", result.Nonce);
        Assert.Matches("^[0-9a-fA-F]{40}$", result.WorkflowCommit);
        var receipt = result.Receipt;
        Assert.Equal(1, receipt.Version);
        Assert.False(receipt.LiteralCrash); // The collector deliberately leaves a valid unclean journal, not a claimed SIGKILL.
        Assert.True(receipt.ValidUncleanJournalReconciled);
        Assert.True(receipt.FailedCleanupBlockedReuse);
        Assert.False(receipt.SourceStartedWhileBlocked);
        Assert.True(receipt.RestoredOnlyOwnFixture);
        Assert.True(receipt.NormalReconciliationRestoredReuse);
        Assert.True(receipt.FreshWorkspace);
        Assert.Equal(settings!.ConfigurationSha256, receipt.ConfigurationSha256);
        Assert.Matches("^[0-9a-f]{64}$", receipt.Lease.WorkspaceSha256);
        Assert.True(double.IsFinite(receipt.Lease.StartedUtcSeconds));
        Assert.True(receipt.Lease.StartedUtcSeconds > 0);
        Assert.True(double.IsFinite(receipt.Lease.CompletedUtcSeconds));
        Assert.True(receipt.Lease.CompletedUtcSeconds >= receipt.Lease.StartedUtcSeconds);
        Assert.True(receipt.Lease.Cleaned);
        Assert.True(receipt.Lease.Reconciled);
    }
}
