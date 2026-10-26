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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_acquisition_uses_only_requested_sides_and_refuses_invalid_required_commits(bool gui)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(gui ? RemoteGuiWorkflow.Text : RemoteIosWorkflow.Text));
        var workflow = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        var sources = (YamlMappingNode)jobs.Children[new YamlScalarNode("sources")];
        var steps = (YamlSequenceNode)sources.Children[new YamlScalarNode("steps")];
        var acquisition = steps.Children.Cast<YamlMappingNode>().Single(step => step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && name.ToString().StartsWith("Acquire", StringComparison.Ordinal));
        var env = (YamlMappingNode)acquisition.Children[new YamlScalarNode("env")];
        Assert.Equal("${{ steps.plan.outputs.sides }}", env.Children[new YamlScalarNode("SIDES")].ToString());
        var shell = acquisition.Children[new YamlScalarNode("run")].ToString();
        var script = shell[(shell.IndexOf('\n') + 1)..];
        script = script[..script.LastIndexOf('\n')];
        var directory = Directory.CreateTempSubdirectory("df-source-sides-").FullName;
        try
        {
            const string harness = """
                import contextlib, io, json, os, pathlib, sys, tempfile, unittest
                from unittest.mock import patch
                script = json.loads(sys.stdin.read())
                class Response(io.BytesIO):
                    pass
                def acquire(code, sides, base='bad-unused-base', head='a'*40):
                    urls=[]
                    class Opener:
                        def open(self, request, timeout):
                            urls.append(request.full_url if hasattr(request,'full_url') else request)
                            return Response(b'archive')
                    env={'SIDES':json.dumps(sides),'BASE':base,'HEAD':head,'API':'https://api.example',
                         'REPO':'owner/source','GH_TOKEN':'fixture','SOURCE_REPOSITORY':'owner/source','TRUSTED_REPOSITORIES':'owner/source'}
                    with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ,env,clear=True), patch('urllib.request.build_opener',return_value=Opener()):
                        before=os.getcwd()
                        try:
                            os.chdir(directory)
                            exec(compile(code,'<sealed-source-acquisition>','exec'),{})
                            files=sorted(p.name for p in pathlib.Path('sources').iterdir())
                        finally: os.chdir(before)
                    return files,urls
                class Cases(unittest.TestCase):
                    def valid(self,*args,**kwargs):
                        try: return acquire(script,*args,**kwargs)
                        except Exception as error: self.fail('valid selected-side acquisition failed: '+str(error))
                    def test_head_only_ignores_invalid_base(self):
                        files,urls=self.valid(['head'])
                        self.assertEqual(files,['head.zip']);self.assertEqual(len(urls),1)
                        self.assertTrue(urls[0].endswith('/'+'a'*40))
                    def test_base_only_ignores_invalid_head(self):
                        files,urls=self.valid(['base'],base='b'*40,head='bad-unused-head')
                        self.assertEqual(files,['base.zip']);self.assertEqual(len(urls),1)
                    def test_legacy_two_sides_remain(self):
                        files,urls=self.valid(['base','head'],base='b'*40)
                        self.assertEqual(files,['base.zip','head.zip']);self.assertEqual(len(urls),2)
                    def test_required_commit_is_validated(self):
                        with self.assertRaises(ValueError): acquire(script,['head'],head='invalid')
                    def test_invalid_side_selections_are_refused(self):
                        for sides in ([],['other'],['head','head'],['base','head','base'],'head'):
                            with self.subTest(sides=sides), self.assertRaises(ValueError): acquire(script,sides,base='b'*40)
                def run():
                    return unittest.TextTestRunner(stream=io.StringIO()).run(unittest.defaultTestLoader.loadTestsFromTestCase(Cases))
                result=run()
                if not result.wasSuccessful():
                    print(result.failures,result.errors);sys.exit(1)
                original=script
                for name,old,new in [
                    ('unrequested-side-acquisition', 'for side in sides:' if 'for side in sides:' in script else 'for name in sides:',
                     "for side in ('base','head'):" if 'for side in sides:' in script else "for name in ('base','head'):"),
                    ('required-commit-validation', "if not re.fullmatch('[0-9a-f]{40}', sha):", 'if False:'),
                    ('empty-selection-accepted', 'or not sides or', 'or False or')]:
                    if old not in original: raise AssertionError('mutation anchor missing: '+name)
                    script=original.replace(old,new,1);red=run()
                    if not red.failures or red.errors: raise AssertionError('not an assertion-only RED: '+name+str(red.errors))
                    print('MUTANT RED '+name)
                    script=original
                    if not run().wasSuccessful(): raise AssertionError('restoration failed: '+name)
                print('5 source acquisition contracts passed; 3 mutants RED/restored GREEN')
                """;
            var start = new ProcessStartInfo("python3", ["-I", "-c", harness])
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(script));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("5 source acquisition contracts passed; 3 mutants RED/restored GREEN", output);
        }
        finally { Directory.Delete(directory, true); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_plan_selects_requested_sides_and_only_the_opposite_an_overlay_needs(bool gui)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(gui ? RemoteGuiWorkflow.Text : RemoteIosWorkflow.Text));
        var workflow = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];
        var steps = (YamlSequenceNode)((YamlMappingNode)jobs.Children[new YamlScalarNode("sources")]).Children[new YamlScalarNode("steps")];
        var plan = steps.Children.Cast<YamlMappingNode>().Single(step => step.Children.TryGetValue(new YamlScalarNode("name"), out var name)
            && name.ToString() == "Select requested execution side");
        var run = plan.Children[new YamlScalarNode("run")].ToString();
        var script = run[(run.IndexOf('\n') + 1)..];
        script = script[..script.LastIndexOf('\n')];
        var directory = Directory.CreateTempSubdirectory("df-source-plan-").FullName;
        try
        {
            const string harness = """
                import io, json, os, sys, tempfile, unittest
                script = json.loads(sys.stdin.read())
                def sides(execution):
                    with tempfile.TemporaryDirectory() as directory:
                        output = os.path.join(directory, 'output')
                        open(output, 'w').close()
                        env = {'EXECUTION': execution, 'GITHUB_OUTPUT': output}
                        previous = os.environ
                        os.environ = env
                        try:
                            exec(compile(script, '<sealed-source-plan>', 'exec'), {})
                        finally:
                            os.environ = previous
                        value = open(output).read()
                    return json.loads(value.split('sides=', 1)[1].strip())
                class Cases(unittest.TestCase):
                    def test_legacy_request_without_execution_keeps_both_sides(self):
                        self.assertEqual(sides(''), ['base', 'head'])
                    def test_head_only_needs_no_base_archive(self):
                        self.assertEqual(sides('{"side":"head","commit":"' + 'a'*40 + '"}'), ['head'])
                    def test_cross_revision_overlay_adds_only_the_opposite_side(self):
                        base = 'b'*40; head = 'a'*40
                        self.assertEqual(sides('{"side":"head","commit":"' + head + '","overlayFrom":"' + base + '","overlayPaths":["Sources/Shared"]}'), ['head', 'base'])
                        self.assertEqual(sides('{"side":"base","commit":"' + base + '","overlayFrom":"' + head + '","overlayPaths":["Sources/Shared"]}'), ['base', 'head'])
                    def test_same_revision_overlay_needs_no_opposite_archive(self):
                        head = 'a'*40
                        self.assertEqual(sides('{"side":"head","commit":"' + head + '","overlayFrom":"' + head + '","overlayPaths":["Sources/Shared"]}'), ['head'])
                    def test_non_overlay_ignores_overlay_from(self):
                        head = 'a'*40; base = 'b'*40
                        self.assertEqual(sides('{"side":"head","commit":"' + head + '","overlayFrom":"' + base + '","overlayPaths":[]}'), ['head'])
                    def test_invalid_side_or_malformed_execution_is_refused(self):
                        for execution in ('{"side":"other"}', 'not-json', '{"commit":"' + 'a'*40 + '"}', '[]'):
                            with self.subTest(execution=execution), self.assertRaises(Exception): sides(execution)
                def run():
                    return unittest.TextTestRunner(stream=io.StringIO()).run(unittest.defaultTestLoader.loadTestsFromTestCase(Cases))
                result=run()
                if not result.wasSuccessful():
                    print(result.failures,result.errors);sys.exit(1)
                original=script
                for name,old,new in [
                    ('overlay-opposite-always', "if parsed.get('overlayPaths') and parsed.get('overlayFrom') != parsed.get('commit'):", "if parsed.get('overlayPaths'):"),
                    ('overlay-opposite-never', "if parsed.get('overlayPaths') and parsed.get('overlayFrom') != parsed.get('commit'):", "if False:"),
                    ('legacy-sides-reduced', "sides = ['base', 'head']\n          if execution:", "sides = ['base', 'head']\n          if False:")]:
                    if old not in original: raise AssertionError('mutation anchor missing: '+name)
                    script=original.replace(old,new,1);red=run()
                    if not red.failures or red.errors: raise AssertionError('not an assertion-only RED: '+name+str(red.errors))
                    print('MUTANT RED '+name)
                    script=original
                    if not run().wasSuccessful(): raise AssertionError('restoration failed: '+name)
                print('6 source plan contracts passed; 3 mutants RED/restored GREEN')
                """;
            var start = new ProcessStartInfo("python3", ["-I", "-c", harness])
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(script));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("6 source plan contracts passed; 3 mutants RED/restored GREEN", output);
        }
        finally { Directory.Delete(directory, true); }
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
            Assert.Contains("Ran 28 tests", output);
        }
        finally { Directory.Delete(directory, true); }
    }
}
