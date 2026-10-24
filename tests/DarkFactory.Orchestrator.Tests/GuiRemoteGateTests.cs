using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using YamlDotNet.RepresentationModel;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class GuiRemoteGateTests
{
    internal static readonly GuiWorkflowSettings Settings = new(new RepoRef("acme", "collector"), [new("acme", "not-the-skeleton"), RunPipelineTests.Sandbox], new string('a', 64));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hosted_targets_and_ui_use_only_the_gui_collector_and_ordinary_per_test_judging(bool ios)
    {
        var source = RemoteIosGateTests.Sources(ios);
        if (!ios)
        {
            source.BaseFiles[XcodeFixture.Pbxproj] = source.BaseFiles[XcodeFixture.Pbxproj].Replace("SDKROOT = macosx;", "SDKROOT = macosx; TEST_HOST = App;");
        }
        var gui = new RemoteIosGateTests.Dispatch { Gui = true };
        var hosted = new RemoteIosGateTests.Dispatch();
        var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), new("acme", "not-the-skeleton"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, "gate-sc-1", null, CancellationToken.None,
            remoteIos: new RemoteIosNewTests(hosted), remoteGui: new RemoteIosNewTests(gui, gui: Settings) { PollInterval = TimeSpan.Zero });
        Assert.True(result.Passed, result.Reason);
        Assert.Empty(source.Runs);
        Assert.Empty(hosted.Requests);
        Assert.Equal(2, gui.Requests.Count);
        Assert.All(gui.Repositories, repo => Assert.Equal(Settings.Collector, repo));
        Assert.All(gui.Requests, request => Assert.Equal("acme/not-the-skeleton", request.SourceRepository));
    }

    [Fact]
    public async Task No_gui_configuration_never_falls_back_to_hosted_or_local()
    {
        var source = RemoteIosGateTests.Sources();
        var hosted = new RemoteIosGateTests.Dispatch();
        var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), new("acme", "not-the-skeleton"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, "gate-sc-1", null, CancellationToken.None, remoteIos: new RemoteIosNewTests(hosted));
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Empty(hosted.Requests);
        Assert.Empty(source.Runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_proven_non_gui_ios_runs_hosted_and_a_mixed_scheme_stays_entirely_gui(bool mixed)
    {
        var source = RemoteIosGateTests.Sources();
        source.BaseFiles[XcodeIosFixture.Pbxproj] = source.BaseFiles[XcodeIosFixture.Pbxproj]
            .Replace("TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/$(BUNDLE_EXECUTABLE_FOLDER_PATH)/Tally\";", "")
            .Replace("BUNDLE_LOADER = \"$(TEST_HOST)\";", "");
        if (!mixed) CargoIdentifyTests.With(source, (XcodeIosFixture.UnitTestFile, XcodeIosFixture.UnitTest), (XcodeIosFixture.Counter, XcodeIosFixture.Fixed));
        var hosted = new RemoteIosGateTests.Dispatch();
        var gui = new RemoteIosGateTests.Dispatch { Gui = true };
        var result = await NewTestsCheck.RunAsync(source, NewTestsCheck.StrategiesFor(true), new("acme", "not-the-skeleton"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, "gate-sc-1", null, CancellationToken.None,
            remoteIos: new RemoteIosNewTests(hosted) { PollInterval = TimeSpan.Zero }, remoteGui: new RemoteIosNewTests(gui, gui: Settings) { PollInterval = TimeSpan.Zero });
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(mixed ? 0 : 2, hosted.Requests.Count);
        Assert.Equal(mixed ? 2 : 0, gui.Requests.Count);
        Assert.Empty(source.Runs);
    }

    [Theory]
    [InlineData("gui-hash")]
    [InlineData("hash")]
    [InlineData("missing")]
    public async Task Gui_evidence_with_wrong_configuration_plan_or_missing_test_is_error(string corrupt)
    {
        var gui = new RemoteIosGateTests.Dispatch { Gui = true, Corrupt = corrupt };
        var result = await NewTestsCheck.RunAsync(RemoteIosGateTests.Sources(), NewTestsCheck.StrategiesFor(true), new("acme", "not-the-skeleton"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, "gate-sc-1", null, CancellationToken.None,
            remoteGui: new RemoteIosNewTests(gui, gui: Settings) { PollInterval = TimeSpan.Zero });
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.True(result.Reason.Length < 4000);
    }

    [Fact]
    public async Task An_unallowlisted_source_is_refused_without_dispatch_or_broader_token_authority()
    {
        var gui = new RemoteIosGateTests.Dispatch { Gui = true };
        var result = await NewTestsCheck.RunAsync(RemoteIosGateTests.Sources(), NewTestsCheck.StrategiesFor(true), new("other", "source"),
            RemoteIosGateTests.Base, RemoteIosGateTests.Head, "gate-sc-1", null, CancellationToken.None,
            remoteGui: new RemoteIosNewTests(gui, gui: Settings) { PollInterval = TimeSpan.Zero });
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Empty(gui.Requests);
    }

    [Theory]
    [InlineData("owner", "Aqua", "502", true, "502", "501", false)]
    [InlineData("lwrunner", "Background", "502", true, "502", "501", false)]
    [InlineData("lwrunner", "Aqua", "501", true, "502", "501", false)]
    [InlineData("lwrunner", "Aqua", "502", false, "502", "501", false)]
    [InlineData("lwrunner", "Aqua", "502", true, "503", "501", false)]
    [InlineData("lwrunner", "Aqua", "502", true, "502", "502", false)]
    [InlineData("lwrunner", "Aqua", "502", true, "502", "501", true)]
    public void Native_gui_test_placement_requires_actual_identity_bootstrap_and_protected_collector_binding(string user, string manager, string managerUid, bool protectedBinding, string sourceUid, string collectorUid, bool allowed)
    {
        var hash = new string('a', 64);
        Assert.Equal(allowed, GuiTestExecution.Check(user, "502", manager, managerUid, protectedBinding, sourceUid, collectorUid, hash, hash) is null);
        Assert.NotNull(GuiTestExecution.Check("lwrunner", "502", "Aqua", "502", true, "502", "501", hash, new string('b', 64)));
    }

    [Fact]
    public void Configuration_reads_nondefaults_and_refuses_partial_settings_at_startup()
    {
        var values = new Dictionary<string, string?> { ["Gate:Gui:CollectorRepo"] = "acme/collector", ["Gate:Gui:TrustedRepos"] = "acme/source", ["Gate:Gui:ConfigurationSha256"] = new string('a', 64) };
        var options = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new InMemorySecrets(), _ => null);
        options.ValidateSettings();
        Assert.Equal("acme/collector", options.GuiWorkflow!.Collector.FullName);
        Assert.Equal("acme/source", Assert.Single(options.GuiWorkflow.TrustedRepositories).FullName);
        values.Remove("Gate:Gui:ConfigurationSha256");
        var invalid = new FactoryOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new InMemorySecrets(), _ => null);
        Assert.Contains("Gate:Gui:ConfigurationSha256", Assert.Throws<InvalidOperationException>(invalid.ValidateSettings).Message);
    }

    [Fact]
    public void The_sealed_gui_workflow_selects_a_collector_not_the_execution_account_and_never_checks_out_source()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(RemoteGuiWorkflow.Text));
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var check = (YamlMappingNode)jobs.Children[new YamlScalarNode("check")];
        Assert.Contains("factory-gui-collector", check.Children[new YamlScalarNode("runs-on")].ToString());
        Assert.DoesNotContain("actions/checkout", RemoteGuiWorkflow.Text);
        Assert.DoesNotContain("secrets.", RemoteGuiWorkflow.Text);
        Assert.DoesNotContain("GH_TOKEN", RemoteGuiWorkflow.Text);
        Assert.Contains("FACTORY_GUI_TRUSTED_REPOSITORIES", RemoteGuiWorkflow.Text);
        Assert.DoesNotContain("FACTORY_RUNNER", RemoteGuiWorkflow.Text);
        Assert.Contains("Persistent lwrunner", RemoteGuiWorkflow.Runner);
    }

    [Fact]
    public async Task Actual_gui_collector_and_fixed_identity_helper_run_only_over_safe_fakes()
    {
        var directory = Directory.CreateTempSubdirectory("df-gui-contract-").FullName;
        OwnProcess? owned = null;
        try
        {
            var runner = Path.Combine(directory, "collector.py");
            var helper = Path.Combine(directory, "root-helper.py");
            await File.WriteAllTextAsync(runner, RemoteGuiWorkflow.Runner);
            await File.WriteAllTextAsync(helper, RemoteGuiWorkflow.Helper);
            using var process = Process.Start(new ProcessStartInfo("python3", ["-I", Path.Combine(AppContext.BaseDirectory, "Fixtures/ios-remote/gui-runner-tests.py"), runner, helper])
                { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true })!;
            owned = OwnProcess.Of(process);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("Ran 18 tests", output);
        }
        finally { owned?.KillIfStillRunning(); Directory.Delete(directory, true); }
    }

    [Fact]
    public void Gui_settings_are_explicit_strict_and_optional_only_as_a_whole()
    {
        Assert.Null(GuiWorkflowSettings.Parse(null, null, null));
        Assert.Throws<InvalidOperationException>(() => GuiWorkflowSettings.Parse("acme/collector", null, new string('a', 64)));
        Assert.Throws<InvalidOperationException>(() => GuiWorkflowSettings.Parse("acme/collector", "acme/source", "wrong"));
        Assert.Throws<InvalidOperationException>(() => GuiWorkflowSettings.Parse("../collector", "acme/source", new string('a', 64)));
        var settings = GuiWorkflowSettings.Parse("acme/collector", "acme/source,ACME/SOURCE", new string('a', 64))!;
        Assert.Single(settings.TrustedRepositories);
        Assert.True(settings.Allows(new("ACME", "source")));
        Assert.False(settings.Allows(new("acme", "other")));
    }
}
