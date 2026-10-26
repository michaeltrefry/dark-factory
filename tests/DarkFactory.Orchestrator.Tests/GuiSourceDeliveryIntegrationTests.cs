using System.Diagnostics;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiSourceDeliveryIntegrationTests
{
    [Theory]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    [InlineData(false, "success")]
    [InlineData(true, "error")]
    public async Task Rendered_report_environment_drives_the_actual_delivery_function_for_the_configured_collector(bool uploadFailed, string expected)
    {
        const string collector = "another-owner/private-gui-control";
        var migrated = GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml",
            GuiSourceWorkflowTests.Original("inference", 120, "recursive"), collector);
        var root = Read(migrated);
        var job = Map(Map(root, "jobs"), "inference");
        var report = ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().Last();
        var values = Map(report, "env").Children.ToDictionary(p => ((YamlScalarNode)p.Key).Value!, p => ((YamlScalarNode)p.Value).Value!);
        // Do not manufacture the missing integration input: this value must actually be rendered by the C# workflow.
        values["FACTORY_GUI_SOURCE_TOKEN"] = "source-ephemeral-fixture";
        values["FACTORY_GUI_SOURCE_CONCLUSION"] = "success";
        values["FACTORY_GUI_SOURCE_TARGET_URL"] = "https://github.com/" + collector + "/actions/runs/22";
        values["FACTORY_GUI_ARTIFACT_DELIVERY_FAILED"] = uploadFailed ? "true" : "false";
        values["GITHUB_REPOSITORY"] = "michaeltrefry/LensWorks";
        values["GITHUB_REF"] = "refs/heads/main";
        values["GITHUB_EVENT_NAME"] = "pull_request_target";
        var run = Scalar(report, "run");
        var program = run[(run.IndexOf('\n') + 1)..run.LastIndexOf("\nFACTORY_GUI_REPORT", StringComparison.Ordinal)];
        program = program[..program.LastIndexOf("if __name__ == '__main__':", StringComparison.Ordinal)];
        var payload = JsonSerializer.Serialize(new { program, environment = values, expected });
        var directory = Directory.CreateTempSubdirectory("df-rendered-source-delivery-").FullName;
        OwnProcess? owned = null;
        try
        {
            var start = new ProcessStartInfo("python3", ["-I", "-c", Driver]) { WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            owned = OwnProcess.Of(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(payload);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, (await output) + (await errors));
            Assert.Equal("rendered-delivery-ok", (await output).Trim());
        }
        finally { owned?.KillIfStillRunning(); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("bash --noprofile --norc -e -o pipefail {0}")]
    public void Explicit_Bash_shell_is_not_silently_downgraded_to_implicit_nonpipefail_Bash(string shell)
    {
        var original = GuiSourceWorkflowTests.Original("inference", 120, "recursive")
            .Replace("        run: cargo test --locked", "        shell: " + shell + "\n        run: cargo test --locked", StringComparison.Ordinal);
        Assert.Contains("shell: " + shell, original);
        var root = Read(GuiSourceWorkflow.Migrate(".github/workflows/inference-adapter.yml", original));
        var step = (YamlMappingNode)((YamlSequenceNode)Map(Map(root, "jobs"), "inference").Children[new YamlScalarNode("steps")]).Children[0];
        using var plan = JsonDocument.Parse(Convert.FromBase64String(Scalar(Map(step, "env"), "FACTORY_GUI_SOURCE_PLAN")));
        var programs = plan.RootElement.GetProperty("steps");
        Assert.True(programs[1].TryGetProperty("shell", out var preservedShell));
        Assert.Equal("bash", preservedShell.GetString());
        Assert.False(programs[0].TryGetProperty("shell", out _));
    }

    const string Driver = """
        import json,sys
        payload=json.load(sys.stdin)
        namespace={'__name__':'compiled_report_contract'}
        exec(compile(payload['program'],'compiled-rendered-report','exec'),namespace)
        calls=[]
        class FakeGitHub:
            def call(self,method,path,token,body=None):
                assert method=='POST'
                assert path=='repos/michaeltrefry/LensWorks/statuses/'+'a'*40
                assert token=='source-ephemeral-fixture'
                calls.append(body)
                return {}
        event={'pull_request':{'base':{'ref':'main','repo':{'full_name':'michaeltrefry/LensWorks'}},
                              'head':{'sha':'a'*40,'repo':{'full_name':'michaeltrefry/LensWorks'}}}}
        outcome=namespace['artifact_delivery'](payload['environment'],event,FakeGitHub())
        assert outcome==0
        assert len(calls)==1 and calls[0]['state']==payload['expected']
        assert calls[0]['context']=='inference'
        assert calls[0]['target_url'].startswith('https://github.com/another-owner/private-gui-control/')
        print('rendered-delivery-ok')
        """;
    static YamlMappingNode Read(string text) { var stream = new YamlStream(); stream.Load(new StringReader(text)); return (YamlMappingNode)stream.Documents[0].RootNode; }
    static YamlMappingNode Map(YamlMappingNode map, string key) => (YamlMappingNode)map.Children[new YamlScalarNode(key)];
    static string Scalar(YamlMappingNode map, string key) => ((YamlScalarNode)map.Children[new YamlScalarNode(key)]).Value!;
}
