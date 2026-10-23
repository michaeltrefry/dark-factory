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
            Assert.Contains("Ran 11 tests", output);
        }
        finally { Directory.Delete(directory, true); }
    }
}
