using System.Diagnostics;
using System.Text.Json;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>The real installer is imported only by Python contracts whose host adapter is entirely fake.</summary>
public class GuiCollectorSetupTests
{
    [Theory]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [InlineData("test_setup_gui_collector.py", "GUI_SETUP_CONTRACT_RESULT ", 46, false)]
    [InlineData("test_gui_source_job.py", "GUI_SOURCE_CONTRACT_RESULT ", 13, false)]
    [InlineData("test_gui_source_job.py", "GUI_SOURCE_MUTATION_RESULT ", 16, true)]
    [InlineData("test_gui_source_dispatch.py", "GUI_SOURCE_DISPATCH_CONTRACT_RESULT ", 101, false)]
    [InlineData("test_gui_source_dispatch.py", "GUI_SOURCE_DISPATCH_MUTATION_RESULT ", 76, true)]
    [InlineData("test_gui_source_publisher.py", "GUI_SOURCE_PUBLISHER_CONTRACT_RESULT ", 60, false)]
    [InlineData("test_gui_source_publisher.py", "GUI_SOURCE_PUBLISHER_MUTATION_RESULT ", 16, true)]
    public async Task Mandatory_gui_collector_contracts_execute_without_skips(string scriptName, string marker, int minimum, bool mutations)
    {
        var directory = Directory.CreateTempSubdirectory("df-gui-setup-wrapper-").FullName;
        OwnProcess? owned = null;
        try
        {
            var script = Path.Combine(SandboxSupport.RepoRoot, "tests", scriptName);
            using var process = Process.Start(new ProcessStartInfo("python3", mutations ? ["-I", script, "--mutations"] : ["-I", script])
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            owned = OwnProcess.Of(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(scriptName == "test_gui_source_job.py" && mutations ? 10 : 3));
            var output = await stdout;
            var errors = await stderr;
            Assert.True(process.ExitCode == 0, output + errors);
            var receipts = output.Split('\n').Where(line => line.StartsWith(marker, StringComparison.Ordinal)).ToArray();
            Assert.Single(receipts);
            using var receipt = JsonDocument.Parse(receipts[0][marker.Length..]);
            var result = receipt.RootElement;
            Assert.True(result.GetProperty("executed").GetInt32() >= minimum, output + errors);
            Assert.Equal(0, result.GetProperty("skipped").GetInt32());
            Assert.Equal(0, result.GetProperty("failures").GetInt32());
            Assert.Equal(0, result.GetProperty("errors").GetInt32());
        }
        finally
        {
            owned?.KillIfStillRunning();
            Directory.Delete(directory, true);
        }
    }
}
