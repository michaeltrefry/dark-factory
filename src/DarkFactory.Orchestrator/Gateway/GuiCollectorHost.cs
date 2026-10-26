using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Read-only host metadata probes. They never boot a simulator, change a license, install a tool or execute downloaded bytes.</summary>
public static class GuiCollectorHost
{
    public static JsonObject DefaultConfiguration(string? collectorOwner = null) => new() {
        ["collectorRepository"] = Gate.RemoteGuiSourceWorkflow.Repository,
        ["developer"] = "/Applications/Xcode.app/Contents/Developer", ["xcode"] = "26.5", ["build"] = "17F42",
        ["runtime"] = "iOS-26-5", ["deviceType"] = "iPhone-17",
        ["trustedRepositories"] = new JsonArray("michaeltrefry/LensWorks", (collectorOwner ?? "michaeltrefry") + "/dark-factory-s6-fixtures")
    };

    public static async Task VerifyAsync(JsonObject configuration, CancellationToken ct,
        Func<string, string[], CancellationToken, Task<string>>? probe = null)
    {
        if (probe is null && (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64))
            throw new InvalidOperationException("This collector deployment pins the osx-arm64 runner and Apple-silicon Xcode host.");
        probe ??= ProbeAsync;
        var selected = (await probe("/usr/bin/xcode-select", ["-p"], ct)).Trim();
        if (selected != configuration["developer"]?.GetValue<string>())
            throw new InvalidOperationException("Selected Xcode developer directory differs from the pinned collector configuration; select the intended installed Xcode and prepare again.");
        var version = (await probe("/usr/bin/xcodebuild", ["-version"], ct)).Replace("\r", "", StringComparison.Ordinal);
        var lines = version.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!lines.Contains("Xcode " + configuration["xcode"]?.GetValue<string>())
            || !lines.Contains("Build version " + configuration["build"]?.GetValue<string>()))
            throw new InvalidOperationException("Installed Xcode version/build differs from the pinned collector configuration.");
        await probe("/usr/bin/xcodebuild", ["-checkFirstLaunchStatus"], ct);
        await probe("/usr/bin/xcodebuild", ["-license", "check"], ct);
        var runtimes = JsonNode.Parse(await probe("/usr/bin/xcrun", ["simctl", "list", "runtimes", "-j"], ct))?["runtimes"]?.AsArray();
        var runtime = configuration["runtime"]?.GetValue<string>();
        var runtimeId = runtime is null ? null : FullIdentifier(runtime, "com.apple.CoreSimulator.SimRuntime.");
        if (runtimeId is null || runtimes is null || !runtimes.Any(r => r?["isAvailable"]?.GetValue<bool>() == true
            && r?["identifier"]?.GetValue<string>() == runtimeId))
            throw new InvalidOperationException("Pinned available iOS simulator runtime is not installed; install it and prepare again.");
        var devices = JsonNode.Parse(await probe("/usr/bin/xcrun", ["simctl", "list", "devicetypes", "-j"], ct))?["devicetypes"]?.AsArray();
        var device = configuration["deviceType"]?.GetValue<string>();
        var deviceId = device is null ? null : FullIdentifier(device, "com.apple.CoreSimulator.SimDeviceType.");
        if (deviceId is null || devices is null || !devices.Any(d => d?["identifier"]?.GetValue<string>() == deviceId))
            throw new InvalidOperationException("Pinned iPhone simulator type is not installed; install it and prepare again.");
    }

    static string FullIdentifier(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) ? value : prefix + value;

    static async Task<string> ProbeAsync(string program, string[] arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = "/", RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        start.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the read-only collector host probe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        var value = await output;
        await errors;
        if (process.ExitCode != 0 || value.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("Read-only Xcode/runtime metadata probe failed; no host setting was changed.");
        return value;
    }
}
