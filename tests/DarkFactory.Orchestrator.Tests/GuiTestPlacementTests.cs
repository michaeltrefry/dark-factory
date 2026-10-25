using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

public class GuiTestPlacementTests
{
    private static XcodeProject Project(string platform, string settings = "") => XcodeProject.Parse("Tally.xcodeproj",
        XcodeIosFixture.Read(XcodeIosFixture.Pbxproj)
            .Replace("TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/$(BUNDLE_EXECUTABLE_FOLDER_PATH)/Tally\";", "")
            .Replace("BUNDLE_LOADER = \"$(TEST_HOST)\";", "")
            .Replace("SDKROOT = iphoneos;", "SDKROOT = " + platform + "; " + settings));

    [Theory]
    [InlineData("macosx", "", "TallyTests", XcodeTestPlacement.Local)]
    [InlineData("macosx", "TEST_HOST = \"\";", "TallyTests", XcodeTestPlacement.Local)]
    [InlineData("macosx", "TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/Tally\";", "TallyTests", XcodeTestPlacement.Gui)]
    [InlineData("iphoneos", "", "TallyTests", XcodeTestPlacement.Hosted)]
    [InlineData("iphoneos", "TEST_HOST = \"\";", "TallyTests", XcodeTestPlacement.Hosted)]
    [InlineData("iphoneos", "TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/Tally\";", "TallyTests", XcodeTestPlacement.Gui)]
    [InlineData("iphoneos", "", "TallyUITests", XcodeTestPlacement.Gui)]
    [InlineData("iphoneos", "TEST_HOST = \"\";", "TallyUITests", XcodeTestPlacement.Gui)]
    [InlineData("iphoneos", "TEST_HOST = (\"a\", \"b\");", "TallyTests", XcodeTestPlacement.Unknown)]
    [InlineData("iphoneos", "\"TEST_HOST[sdk=iphonesimulator*]\" = \"a\";", "TallyTests", XcodeTestPlacement.Unknown)]
    [InlineData("iphoneos", "TEST_HOST = \"\"; \"TEST_HOST[sdk=iphonesimulator*]\" = \"a\";", "TallyTests", XcodeTestPlacement.Unknown)]
    [InlineData("iphoneos", "BUNDLE_LOADER = \"$(TEST_HOST)\";", "TallyTests", XcodeTestPlacement.Unknown)]
    public void Placement_reads_host_settings_and_ui_kind_not_only_sdk(string platform, string settings, string targetName, XcodeTestPlacement expected)
    {
        var project = Project(platform, settings);
        Assert.Equal(expected, project.TestPlacement(project.Targets.Single(t => t.Name == targetName), "Debug"));
    }

    [Fact]
    public void An_xcconfig_or_missing_configuration_cannot_prove_a_non_gui_target()
    {
        var text = XcodeIosFixture.Read(XcodeIosFixture.Pbxproj).Replace("buildSettings = {", "baseConfigurationReference = UNKNOWN; buildSettings = {");
        var project = XcodeProject.Parse("Tally.xcodeproj", text);
        var target = project.Targets.Single(t => t.Name == "TallyTests");
        Assert.Equal(XcodeTestPlacement.Unknown, project.TestPlacement(target, "Debug"));
        Assert.Equal(XcodeTestPlacement.Unknown, project.TestPlacement(target, "DoesNotExist"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_reads_host_requirements_at_both_commits_before_agreeing(bool headChangesHost)
    {
        var runner = CargoIdentifyTests.Runner(XcodeIosFixture.Files());
        var changes = XcodeIosFixture.Head.ToList();
        if (headChangesHost)
            changes.Add((XcodeIosFixture.Pbxproj, XcodeIosFixture.Read(XcodeIosFixture.Pbxproj)
                .Replace("TEST_HOST = \"$(BUILT_PRODUCTS_DIR)/Tally.app/$(BUNDLE_EXECUTABLE_FOLDER_PATH)/Tally\";", "TEST_HOST = \"\";")
                .Replace("BUNDLE_LOADER = \"$(TEST_HOST)\";", "")));
        CargoIdentifyTests.With(runner, changes.ToArray());
        var repo = new RepoRef("acme", "tally");
        var files = await runner.ChangesAsync(repo, FakeTestRunner.BaseSha, "head1", CancellationToken.None);
        var set = await new XcodeNewTests().IdentifyAsync(new TestSource(runner, repo, FakeTestRunner.BaseSha, "head1"), files, CancellationToken.None);
        Assert.Empty(set.Unisolable);
        Assert.Equal(headChangesHost ? XcodeTestPlacement.Unknown : XcodeTestPlacement.Gui, set.XcodePlacements[XcodeIosFixture.UnitTestId]);
        Assert.Equal(XcodeTestPlacement.Gui, set.XcodePlacements[XcodeIosFixture.UiTestId]);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData(XcodeTestPlacement.Local, XcodeTestPlacement.Local, XcodeTestPlacement.Local)]
    [InlineData(XcodeTestPlacement.Hosted, XcodeTestPlacement.Hosted, XcodeTestPlacement.Hosted)]
    [InlineData(XcodeTestPlacement.Gui, XcodeTestPlacement.Gui, XcodeTestPlacement.Gui)]
    [InlineData(XcodeTestPlacement.Gui, XcodeTestPlacement.Hosted, XcodeTestPlacement.Unknown)]
    [InlineData(XcodeTestPlacement.Hosted, XcodeTestPlacement.Gui, XcodeTestPlacement.Unknown)]
    [InlineData(XcodeTestPlacement.Unknown, XcodeTestPlacement.Hosted, XcodeTestPlacement.Unknown)]
    [InlineData(XcodeTestPlacement.Local, XcodeTestPlacement.Hosted, XcodeTestPlacement.Unknown)]
    public void Both_commits_must_agree_before_any_execution(XcodeTestPlacement baseline, XcodeTestPlacement head, XcodeTestPlacement expected) =>
        Assert.Equal(expected, XcodeTestPlacements.Agree(baseline, head));
}
