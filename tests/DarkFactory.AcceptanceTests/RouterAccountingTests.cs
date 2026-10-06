namespace DarkFactory.AcceptanceTests;

/// <summary>
/// AT7, live and standalone: every worker session a ledger recorded since a given time is accounted for by the router
/// (<c>GET /v1/sessions/{id}/cost</c>: at least one request, non-zero cost). AT2 and AT5 run the same check on their own
/// throwaway ledgers; this one checks a ledger the owner names, e.g. the real one after a manual run. Read-only.
/// Runbook (including the router-log check for calls without a session): docs/acceptance.md.
/// </summary>
public class RouterAccountingTests
{
    [Fact]
    public async Task Router_reports_requests_and_cost_for_every_worker_session_since_the_given_time()
    {
        Harness.RequireOptIn();
        Harness.RequireSecret(o => o.RouterKey);
        await Harness.RequireRouterAsync();
        var since = Harness.RequireEnv("FACTORY_E2E_SINCE", "an ISO 8601 time; worker sessions started since then are checked");
        Assert.True(DateTimeOffset.TryParse(since, out var from), $"FACTORY_E2E_SINCE='{since}' is not a time");
        var ledger = Environment.GetEnvironmentVariable("FACTORY_E2E_LEDGER") ?? Harness.Options.LedgerConnectionString;

        var checkedSessions = await E2e.AssertRouterAccountsForSessionsAsync(ledger, from, TestContext.Current.CancellationToken);
        Console.WriteLine($"router accounts for {checkedSessions.Count} session(s): {string.Join(", ", checkedSessions)}");
    }
}
