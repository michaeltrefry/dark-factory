using System.Collections;
using System.Diagnostics;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Persists only the three public GUI settings through the existing user-secrets provider, preserving every other key.</summary>
public static class GuiCollectorSettings
{
    internal static IReadOnlyDictionary<string, string> Values(string repository, IEnumerable<string> trustedRepositories, string configurationHash) =>
        new Dictionary<string, string> {
            ["Gate:Gui:CollectorRepo"] = repository,
            ["Gate:Gui:TrustedRepos"] = string.Join(',', trustedRepositories),
            ["Gate:Gui:ConfigurationSha256"] = configurationHash
        };

    public static void CheckEnvironment(string repository, IEnumerable<string> trustedRepositories, string? configurationHash = null, IDictionary? environment = null)
    {
        var values = Values(repository, trustedRepositories, configurationHash ?? "");
        foreach (DictionaryEntry entry in environment ?? Environment.GetEnvironmentVariables())
        {
            var key = (entry.Key as string)?.Replace("__", ":", StringComparison.Ordinal);
            if (key is null || !values.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            var canonical = values.Keys.Single(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (configurationHash is null && canonical == "Gate:Gui:ConfigurationSha256") continue;
            if (!string.Equals(entry.Value as string, values[canonical], StringComparison.Ordinal))
                throw new InvalidOperationException("An existing environment override for " + canonical + " differs from this deployment. Remove/update that override and resume; persisted settings would otherwise be silently ignored.");
        }
    }

    internal static ProcessStartInfo StartInfo(string key, string value)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = "/", RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "user-secrets", "set", key, value, "--id", "dark-factory-orchestrator" }) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return start;
    }

    public static async Task PersistAsync(string repository, string[] trustedRepositories, string configurationHash, CancellationToken ct,
        Func<ProcessStartInfo, CancellationToken, Task>? run = null, IDictionary? environment = null)
    {
        _ = Gate.GuiWorkflowSettings.Parse(repository, string.Join(',', trustedRepositories), configurationHash);
        CheckEnvironment(repository, trustedRepositories, configurationHash, environment);
        run ??= RunAsync;
        foreach (var value in Values(repository, trustedRepositories, configurationHash)) await run(StartInfo(value.Key, value.Value), ct);
    }

    static async Task RunAsync(ProcessStartInfo start, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot persist public GUI settings through the existing user-secrets provider.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        await stdout;
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Public GUI settings could not be persisted; retain the deployment journal and resume. Existing unrelated secrets were not replaced.");
    }
}
