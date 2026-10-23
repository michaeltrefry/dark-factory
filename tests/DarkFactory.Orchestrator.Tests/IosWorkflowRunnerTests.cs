using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Tests;

public sealed class IosWorkflowRunnerTests
{
    [Fact]
    public void The_rendered_workflow_is_valid_and_has_no_repo_controlled_dependency_or_credential_in_the_execution_job()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(RemoteIosWorkflow.Text));
        var workflow = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        Assert.Equal(2, jobs.Children.Count);
        var check = (YamlMappingNode)jobs.Children[new YamlScalarNode("check")];
        Assert.Equal("macos-15", check.Children[new YamlScalarNode("runs-on")].ToString());
        Assert.Empty(((YamlMappingNode)check.Children[new YamlScalarNode("permissions")]).Children);
        var steps = (YamlSequenceNode)check.Children[new YamlScalarNode("steps")];
        var run = (YamlMappingNode)steps.Children[1];
        Assert.Equal("/usr/bin/python3 -I {0}", run.Children[new YamlScalarNode("shell")].ToString());
        var script = run.Children[new YamlScalarNode("run")].ToString();
        Assert.StartsWith("# Compiled into the sealed workflow.", script);
        Assert.Contains("def main():", script);
        Assert.DoesNotContain("FACTORY_RUNNER", script);
        Assert.DoesNotContain("secrets.", RemoteIosWorkflow.Text);
        Assert.DoesNotContain("actions/checkout", RemoteIosWorkflow.Text);
        Assert.DoesNotContain("${{", script);
        Assert.Contains("env=env", script);
        foreach (YamlMappingNode step in steps)
            if (step.Children.TryGetValue(new YamlScalarNode("uses"), out var action))
                Assert.Matches("^actions/(download|upload)-artifact@[0-9a-f]{40}$", action.ToString());
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("timeout")]
    public async Task The_command_drain_is_bounded_and_waits_for_its_whole_owned_tree(string mode)
    {
        var directory = Directory.CreateTempSubdirectory("df-ios-command-").FullName;
        var owned = new List<OwnProcess>();
        try
        {
            var helper = Path.Combine(directory, "runner.py");
            await File.WriteAllTextAsync(helper, RemoteIosWorkflow.Runner);
            var script = Path.Combine(directory, "source.sh");
            // Both records are captured by their unreaped parent, never inferred
            // from an arbitrary pid. Only OwnProcess signals this test's tree.
            await File.WriteAllTextAsync(script, $"/bin/sleep 600 &\nchild=$!\necho {OwnProcess.ShellRecord("$$")} > '{directory}/owned'\necho {OwnProcess.ShellRecord("$child")} >> '{directory}/owned'\n"
                + (mode == "overflow" ? "while :; do printf '0123456789012345678901234567890123456789'; done\n" : "wait\n"));
            using var process = Process.Start(new ProcessStartInfo("python3", ["-I", Path.Combine(AppContext.BaseDirectory, "Fixtures/ios-remote/runner-tests.py"), helper,
                "--process", directory, script, mode]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory })!;
            var host = OwnProcess.Of(process);
            owned.Add(host);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            for (var i = 0; i < 200 && !File.Exists(Path.Combine(directory, "stop")) && !process.HasExited; i++) await Task.Delay(25);
            Assert.True(File.Exists(Path.Combine(directory, "stop")), "the drain never reached its bounded stop callback");
            owned.AddRange(File.ReadAllLines(Path.Combine(directory, "owned")).Select(OwnProcess.Parse));
            owned[1].KillGroupIfStillLeading();
            // Let Python reap its own immediate child before judging liveness;
            // a zombie is not a surviving executable descendant.
            await File.WriteAllTextAsync(Path.Combine(directory, "stopped"), "signals sent");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await OwnProcess.WaitUntilDeadAsync(owned.Skip(1).ToArray());
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0 && output.Contains(mode == "overflow" ? "oversized command output" : "command timed out", StringComparison.Ordinal), output);
        }
        finally
        {
            var records = Path.Combine(directory, "owned");
            if (File.Exists(records))
            {
                var tree = File.ReadAllLines(records).Select(OwnProcess.Parse).ToArray();
                if (tree.Length > 0) tree[0].KillGroupIfStillLeading();
                foreach (var child in tree) child.KillIfStillRunning();
            }
            foreach (var process in owned) process.KillIfStillRunning();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task The_actual_runner_executes_base_overlay_and_head_and_rejects_unprovable_results()
    {
        var directory = Path.Combine(Path.GetTempPath(), "factory-ios-runner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var helper = Path.Combine(directory, "runner.py");
            await File.WriteAllTextAsync(helper, RemoteIosWorkflow.Runner);
            var start = new ProcessStartInfo("python3", ["-I", Path.Combine(AppContext.BaseDirectory, "Fixtures/ios-remote/runner-tests.py"), helper])
                { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("Ran 25 tests", output);
        }
        finally { Directory.Delete(directory, true); }
    }
}
