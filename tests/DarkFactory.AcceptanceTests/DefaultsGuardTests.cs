using DarkFactory.Orchestrator;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Not live (sc-25992): the P4b-AT7 guard (<see cref="E2e.RequireDefaultsWhenAsked"/>). A run with
/// <c>FACTORY_E2E_P4B_DEFAULTS=1</c> re-runs one live test per earlier phase "with default settings"; the guard makes such a
/// run fail before it starts unless the settings are Phase 4b's defaults, so it can never pass with Kanban or slots configured.
/// </summary>
public class DefaultsGuardTests
{
    private static (IConfiguration Config, FactoryOptions Options) Settings(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (config, new FactoryOptions(config, new NoSecrets(), _ => null));
    }

    private static readonly Func<string, string?> AskedFor = name => name == E2e.DefaultsVariable ? "1" : null;

    [Fact]
    public void Defaults_pass_and_phase_3_kanban_reads_stay_allowed()
    {
        var (config, options) = Settings(new() { ["Kanban:Token"] = "kb_read", ["Kanban:McpUrl"] = "http://127.0.0.1:5006/mcp" });
        Assert.Empty(E2e.DefaultsProblems(config, options));
        E2e.RequireDefaultsWhenAsked(config, options, AskedFor);
    }

    [Theory]
    [InlineData("Kanban:Watch:Projects", "DF", "Kanban:Watch:Projects is set")]
    [InlineData("Kanban:WebUrl", "https://kanban.example/", "Kanban:WebUrl is set")]
    [InlineData("Kanban:WriteToken", "kb_full", "a Kanban write token is configured")]
    [InlineData("Worker:Slots", "2", "Worker:Slots is 2, not 1")]
    [InlineData("Intake:MaxConcurrentRuns", "2", "Intake:MaxConcurrentRuns is 2, not 1")]
    public void A_phase_4b_setting_fails_an_at7_run_before_it_starts_and_nothing_else(string key, string value, string problem)
    {
        var (config, options) = Settings(new() { [key] = value });
        Assert.Contains(E2e.DefaultsProblems(config, options), p => p.StartsWith(problem, StringComparison.Ordinal));
        var failed = Assert.ThrowsAny<Exception>(() => E2e.RequireDefaultsWhenAsked(config, options, AskedFor));
        Assert.Contains(problem, failed.Message);
        // Not an AT7 run: the same settings are a Phase 4b test's own, and nothing is refused.
        E2e.RequireDefaultsWhenAsked(config, options, _ => null);
    }
}
