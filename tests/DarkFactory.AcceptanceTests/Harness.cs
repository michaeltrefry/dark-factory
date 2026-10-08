using System.Diagnostics;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Gates for the live acceptance tests. They spend real model tokens and touch real
/// GitHub/Shortcut state, so they only run with FACTORY_E2E=1 and skip with the exact
/// missing prerequisite otherwise.
/// </summary>
internal static class Harness
{
    public static readonly FactoryOptions Options = new(FactoryOptions.LoadConfiguration(), new MacKeychain());

    public static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable("FACTORY_E2E") != "1")
        {
            Assert.Skip("Live acceptance test: set FACTORY_E2E=1 to run (spends router tokens).");
        }
    }

    public static string RequireSecret(Func<FactoryOptions, string> secret)
    {
        try
        {
            return secret(Options);
        }
        catch (MissingCredentialException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    /// <summary>Skips naming the missing owner step unless the review panel's models are configured (<c>Review:Models</c>: no default).</summary>
    public static Orchestrator.Gate.ReviewPanelModels RequireReviewPanel(FactoryOptions? options = null)
    {
        try
        {
            return (options ?? Options).ReviewPanel;
        }
        catch (ReviewConfigurationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    public static string RequireEnv(string name, string purpose)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Skip($"Set {name}: {purpose}.");
        }
        return value!;
    }

    public static async Task RequireRouterAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            await http.GetAsync(Options.RouterBaseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"Router not reachable at {Options.RouterBaseUrl}: {ex.Message}");
        }
    }

    public static async Task<LedgerDbContext> RequireLedgerAsync()
    {
        var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(Options.LedgerConnectionString));
        if (!await db.Database.CanConnectAsync())
        {
            await db.DisposeAsync();
            Assert.Skip("Ledger Postgres not reachable; run `docker compose up -d`.");
        }
        return db;
    }

    public static void RequireClaude()
    {
        if (Options.WorkerSandbox is { } sandbox)
        {
            try
            {
                sandbox.EnsureReadyAsync(Options.WorkerAuth, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                Assert.Skip($"{ex.Message} Or set Worker__RunAs=none to run workers as the owner.");
            }
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Options.ClaudePath, "--version") { RedirectStandardOutput = true })!;
            p.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip($"Claude Code CLI '{Options.ClaudePath}' not found; set Worker__ClaudePath.");
        }
    }

    /// <summary>
    /// The router commits telemetry asynchronously, so poll briefly until it has recorded a request for the session
    /// (<c>request_count &gt; 0</c>). The cost may then be 0: turns served on the router's local model are priced at $0.
    /// </summary>
    public static async Task<SessionCost?> WaitForCostAsync(string sessionId, string routerKey, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = Options.RouterBaseUrl };
        var router = new RouterClient(http, routerKey);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var cost = await router.GetSessionCostAsync(sessionId, ct);
            if (cost is { RequestCount: > 0 })
            {
                return cost;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return await router.GetSessionCostAsync(sessionId, ct);
    }
}
