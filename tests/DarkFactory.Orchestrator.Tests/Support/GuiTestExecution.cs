using System.Diagnostics;
using System.Text.Json;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>Placement, not an opt-in: native GUI fixtures run only in the fixed helper's actual lwrunner context.</summary>
internal static class GuiTestExecution
{
    internal static string? Check(string user, string uid, string manager, string managerUid,
        bool protectedBinding, string? boundSourceUid, string? collectorUid, string? expectedHash, string? boundHash)
    {
        const string placement = "Native GUI integration belongs on the verified dedicated lwrunner collector, not the owner's session";
        if (user != "lwrunner" || !int.TryParse(uid, out var actual) || actual < 500 || manager != "Aqua" || managerUid != uid)
            return placement + " (actual user/bootstrap mismatch).";
        if (!protectedBinding || boundSourceUid != uid || !int.TryParse(collectorUid, out var collector) || collector < 500 || collector == actual
            || expectedHash is null || !System.Text.RegularExpressions.Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$") || boundHash != expectedHash)
            return placement + " (no protected collector/helper binding).";
        return null;
    }

    internal static string? UnavailableReason
    {
        get
        {
            // In particular, do not even look up CoreSimulator or GUI Mach services as the owner before skipping.
            if (!OperatingSystem.IsMacOS() || Environment.UserName != "lwrunner")
                return "Native GUI integration is placed on dedicated lwrunner CI; it must not run as the owner or another account.";
            try
            {
                var path = Environment.GetEnvironmentVariable("FACTORY_GUI_TEST_BINDING");
                if (path is null || new FileInfo(path) is not { Exists: true, Length: <= 1024 } info || info.LinkTarget is not null)
                    return "Native GUI integration has no protected collector binding.";
                var metadata = Read("/usr/bin/stat", "-f", "%u %Lp", path);
                var mode = File.GetUnixFileMode(path);
                var protectedBinding = metadata.StartsWith("0 ", StringComparison.Ordinal)
                    && (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0
                    && Read("/bin/ls", "-led", path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1;
                using var binding = JsonDocument.Parse(File.ReadAllText(path));
                var root = binding.RootElement;
                return Check(Environment.UserName, Read("/usr/bin/id", "-u"), Read("/bin/launchctl", "managername"),
                    Read("/bin/launchctl", "manageruid"), protectedBinding, root.GetProperty("sourceUid").GetInt32().ToString(),
                    root.GetProperty("collectorUid").GetInt32().ToString(), Environment.GetEnvironmentVariable("FACTORY_GUI_CONFIGURATION_HASH"),
                    root.GetProperty("configurationHash").GetString());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return "Native GUI integration could not verify its dedicated lwrunner collector binding."; }
        }
    }

    private static string Read(string program, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : "";
    }
}
