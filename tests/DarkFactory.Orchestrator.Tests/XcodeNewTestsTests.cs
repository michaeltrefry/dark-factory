using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using static DarkFactory.Orchestrator.Tests.GatePipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>The hand-written macOS Xcode project the sc-26131 tests use (Fixtures/xcode-macos; xcode-classic is its project with classic file references for the test target).</summary>
internal static class XcodeFixture
{
    public const string Pbxproj = "Words.xcodeproj/project.pbxproj";
    public const string Scheme = "Words.xcodeproj/xcshareddata/xcschemes/Words.xcscheme";
    public const string Library = "Words/Words.swift";
    public const string TestFile = "WordsTests/WordsTests.swift";

    public const string Fixed = "public func isBlank(_ text: String) -> Bool {\n    text.allSatisfy(\\.isWhitespace)\n}\n";

    public static string Read(string fixture, string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture, path));

    public static string SynchronizedProject => Read("xcode-macos", Pbxproj);

    public static string ClassicProject => Read("xcode-classic", Pbxproj);

    public static string BaseTests => Read("xcode-macos", TestFile);

    /// <summary>The project's files at <paramref name="prefix"/> (e.g. <c>App/</c>), with its test target synchronized or classic.</summary>
    public static (string Path, string Content)[] Files(string prefix = "", bool classic = false) =>
    [
        (prefix + Pbxproj, classic ? ClassicProject : SynchronizedProject),
        (prefix + Scheme, Read("xcode-macos", Scheme)),
        (prefix + Library, Read("xcode-macos", Library)),
        (prefix + TestFile, BaseTests),
    ];

    public static string Json(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xcresult", name));
}

/// <summary>sc-26131: the old-style property list reader, an Xcode project's membership and platform, schemes and workspaces.</summary>
public class XcodeProjectTests
{
    [Fact]
    public void The_plist_reader_reads_dictionaries_arrays_strings_escapes_and_comments_and_refuses_what_it_does_not_know()
    {
        var dict = Pbx.Parse("// !$*UTF8*$!\n{ a = \"x \\\"y\\\" \\U00e9\\n\"; /* c */ b = ( one, \"two\", ); c = { d = <group>; }; }".Replace("<group>", "\"<group>\""));

        Assert.Equal("x \"y\" é\n", dict.Str("a"));
        Assert.Equal(["one", "two"], dict.Strings("b"));
        Assert.Equal("<group>", dict.Dict("c")!.Str("d"));
        Assert.Throws<FormatException>(() => Pbx.Parse("{ a = 1; a = 2; }"));
        Assert.Throws<FormatException>(() => Pbx.Parse("{ a = \"open; }"));
        Assert.Throws<FormatException>(() => Pbx.Parse("{ a = <0fab>; }"));
        Assert.Throws<FormatException>(() => Pbx.Parse("{ a = \"\\q\"; }"));
        Assert.Throws<FormatException>(() => Pbx.Parse("{ a = 1; } extra"));
        Assert.Throws<FormatException>(() => Pbx.Parse(new string('(', 100) + new string(')', 100)));
    }

    [Fact]
    public void A_synchronized_folder_makes_every_file_under_it_a_member_of_its_target_less_its_exceptions()
    {
        var project = XcodeProject.Parse("App/Words.xcodeproj", XcodeFixture.SynchronizedProject);
        var tests = project.Targets.Single(t => t.Name == "WordsTests");
        var words = project.Targets.Single(t => t.Name == "Words");

        Assert.True(tests.IsUnitTests);
        Assert.False(words.IsUnitTests);
        Assert.Equal(XcodeMembership.Yes, project.Membership(tests, "App/WordsTests/New.swift"));
        Assert.Equal(XcodeMembership.Yes, project.Membership(tests, "App/WordsTests/Sub/Deep.swift"));
        Assert.Equal(XcodeMembership.No, project.Membership(tests, "App/Words/Words.swift"));
        Assert.Equal(XcodeMembership.Yes, project.Membership(words, "App/Words/Words.swift"));
        Assert.Equal(XcodeMembership.No, project.Membership(tests, "WordsTests/New.swift"));

        // An exception set for WordsTests takes a file out of its folder, and one in Words' folder puts a file in.
        var excepted = XcodeFixture.SynchronizedProject
            .Replace("\t\t\tpath = WordsTests;\n\t\t\tsourceTree = \"<group>\";", "\t\t\texceptions = ( E1, );\n\t\t\tpath = WordsTests;\n\t\t\tsourceTree = \"<group>\";")
            .Replace("\t\t\tpath = Words;\n\t\t\tsourceTree = \"<group>\";", "\t\t\texceptions = ( E2, );\n\t\t\tpath = Words;\n\t\t\tsourceTree = \"<group>\";")
            .Replace("/* End PBXFileSystemSynchronizedRootGroup section */",
                "E1 = { isa = PBXFileSystemSynchronizedBuildFileExceptionSet; membershipExceptions = ( Left.swift, Out/, ); target = B10000000000000000000102; };\n"
                + "E2 = { isa = PBXFileSystemSynchronizedBuildFileExceptionSet; membershipExceptions = ( Shared.swift, ); target = B10000000000000000000102; };\n"
                + "/* End PBXFileSystemSynchronizedRootGroup section */");
        var withExceptions = XcodeProject.Parse("Words.xcodeproj", excepted);
        var t = withExceptions.Targets.Single(x => x.Name == "WordsTests");

        Assert.Equal(XcodeMembership.No, withExceptions.Membership(t, "WordsTests/Left.swift"));
        Assert.Equal(XcodeMembership.No, withExceptions.Membership(t, "WordsTests/Out/In.swift"));
        Assert.Equal(XcodeMembership.Yes, withExceptions.Membership(t, "WordsTests/Kept.swift"));
        Assert.Equal(XcodeMembership.Yes, withExceptions.Membership(t, "Words/Shared.swift"));
    }

    [Fact]
    public void Classic_file_references_are_resolved_through_their_groups()
    {
        var project = XcodeProject.Parse("Words.xcodeproj", XcodeFixture.ClassicProject);
        var tests = project.Targets.Single(t => t.Name == "WordsTests");

        Assert.Equal(XcodeMembership.Yes, project.Membership(tests, "WordsTests/WordsTests.swift"));
        Assert.Equal(XcodeMembership.Yes, project.Membership(tests, "WordsTests/WordsTests.swift", classicOnly: true));
        Assert.Equal(XcodeMembership.No, project.Membership(tests, "WordsTests/New.swift"));
    }

    [Theory]
    [InlineData("sourceTree = \"<group>\"; };\n/* End PBXFileReference", "sourceTree = \"<absolute>\"; };\n/* End PBXFileReference", "<absolute>")]
    [InlineData("path = WordsTests.swift;", "path = \"$(SRCROOT)/WordsTests.swift\";", "build variable")]
    [InlineData("path = WordsTests.swift;", "path = ../../../Out.swift;", "leaves the repository")]
    public void A_source_file_the_gate_cannot_locate_makes_membership_unknown(string from, string to, string why)
    {
        var text = XcodeFixture.ClassicProject.Replace(from, to);
        Assert.NotEqual(XcodeFixture.ClassicProject, text);
        var project = XcodeProject.Parse("Words.xcodeproj", text);

        var membership = project.Membership(project.Targets.Single(t => t.Name == "WordsTests"), "WordsTests/WordsTests.swift");

        Assert.False(membership.Member);
        Assert.Contains(why, membership.Unknown);
    }

    [Fact]
    public void A_synchronized_folder_with_settings_or_exception_sets_the_gate_does_not_read_makes_membership_unknown()
    {
        var explicitFolders = XcodeFixture.SynchronizedProject.Replace("\t\t\tpath = WordsTests;\n", "\t\t\texplicitFolders = ( Res, );\n\t\t\tpath = WordsTests;\n");
        var phaseSet = XcodeFixture.SynchronizedProject
            .Replace("\t\t\tpath = WordsTests;\n", "\t\t\texceptions = ( E1, );\n\t\t\tpath = WordsTests;\n")
            .Replace("/* End PBXFileSystemSynchronizedRootGroup section */",
                "E1 = { isa = PBXFileSystemSynchronizedGroupBuildPhaseMembershipExceptionSet; buildPhase = B10000000000000000000602; membershipExceptions = ( A.swift, ); };\n/* End PBXFileSystemSynchronizedRootGroup section */");

        foreach (var (text, why) in new[] { (explicitFolders, "explicitFolders"), (phaseSet, "PBXFileSystemSynchronizedGroupBuildPhaseMembershipExceptionSet") })
        {
            var project = XcodeProject.Parse("Words.xcodeproj", text);
            var membership = project.Membership(project.Targets.Single(t => t.Name == "WordsTests"), "WordsTests/A.swift");
            Assert.Contains(why, membership.Unknown);
        }
    }

    [Theory]
    [InlineData("SDKROOT = macosx;", true, null)]
    [InlineData("SDKROOT = iphoneos;", false, "iphoneos SDK")]
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator macosx\";", true, null)]
    [InlineData("SDKROOT = auto; SUPPORTED_PLATFORMS = \"iphoneos iphonesimulator\";", false, "iphoneos iphonesimulator only")]
    [InlineData("", false, "set neither by the target nor by the project")]
    public void Whether_a_target_builds_for_macos_is_read_from_its_sdk(string settings, bool mac, string? why)
    {
        var project = XcodeProject.Parse("Words.xcodeproj", XcodeFixture.SynchronizedProject.Replace("\t\t\t\tSDKROOT = macosx;\n", "\t\t\t\t" + settings + "\n"));

        var (isMac, reason) = project.BuildsForMac(project.Targets.Single(t => t.Name == "WordsTests"), "Debug");

        Assert.Equal(mac, isMac);
        if (why is not null)
        {
            Assert.Contains(why, reason);
        }
    }

    [Fact]
    public void A_project_the_gate_cannot_read_throws_never_reads_as_empty()
    {
        Assert.Throws<FormatException>(() => XcodeProject.Parse("W.xcodeproj", "{ objects = { }; }"));
        Assert.Throws<FormatException>(() => XcodeProject.Parse("W.xcodeproj", XcodeFixture.SynchronizedProject.Replace("projectDirPath = \"\";", "projectDirPath = Elsewhere;")));
    }

    [Fact]
    public void A_scheme_names_its_test_targets_and_configuration_and_a_test_plan_is_named_as_unread()
    {
        var scheme = XcodeScheme.Parse("Words", XcodeFixture.Read("xcode-macos", XcodeFixture.Scheme));

        Assert.Equal(("Debug", null), (scheme.TestConfiguration, scheme.Problem));
        Assert.Equal([new XcodeTestable("B10000000000000000000102", "container:Words.xcodeproj", false)], scheme.Testables);
        Assert.Equal("App/Words.xcodeproj", XcodeScheme.ContainerPath("container:Words.xcodeproj", "App"));
        Assert.Contains("test plan", XcodeScheme.Parse("P", "<Scheme><TestAction><TestPlans><TestPlanReference reference=\"container:P.xctestplan\"/></TestPlans></TestAction></Scheme>").Problem);
        Assert.Throws<FormatException>(() => XcodeScheme.Parse("X", "<!DOCTYPE x [<!ENTITY e \"y\">]><Scheme/>"));
    }

    [Fact]
    public void A_workspace_names_its_projects_through_its_groups()
    {
        const string contents = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Workspace version = "1.0">
               <FileRef location = "group:Words.xcodeproj"></FileRef>
               <Group location = "group:Sub" name = "Sub"><FileRef location = "group:Other.xcodeproj"></FileRef></Group>
               <FileRef location = "container:Pods/Pods.xcodeproj"></FileRef>
               <FileRef location = "absolute:/elsewhere/X.xcodeproj"></FileRef>
            </Workspace>
            """;

        Assert.Equal(["App/Words.xcodeproj", "App/Sub/Other.xcodeproj", "App/Pods/Pods.xcodeproj"], XcodeWorkspace.Projects(contents, "App"));
    }
}

/// <summary>sc-26131: which Swift Testing tests an Xcode project's change adds, and what the gate cannot run (unsupported, never no-tests).</summary>
public class XcodeIdentifyTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string Head = "head1";

    internal static FakeTestRunner Runner(string prefix = "", bool classic = false, params (string Path, string? Content)[] head)
    {
        var runner = CargoIdentifyTests.Runner(XcodeFixture.Files(prefix, classic));
        runner.Answer = spec => FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        return CargoIdentifyTests.With(runner, head);
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner, bool swift = true) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(swift), Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static async Task<NewTestSet> Identify(FakeTestRunner runner)
    {
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, Head, CancellationToken.None);
        return await new XcodeNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head), changes, CancellationToken.None);
    }

    private const string NewFile = "WordsTests/BlankTests.swift";
    private const string NewTests = "import Testing\n@testable import Words\n\n@Test func blank() { #expect(isBlank(\"  \")) }\nstruct Spaces { struct Only { @Test func empty() { #expect(isBlank(\"\")) } } }\n";

    [Fact]
    public async Task A_new_test_file_in_a_synchronized_test_folder_holds_new_tests_named_as_xcodebuild_names_them()
    {
        var runner = Runner("App/", head: [("App/" + XcodeFixture.Library, XcodeFixture.Fixed), ("App/" + NewFile, NewTests)]);

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/Spaces/Only/empty()", "WordsTests/blank()"], set.Tests);
        Assert.Equal("-project\nApp/Words.xcodeproj\nWords", set.Projects["WordsTests/blank()"]);
        // Only the test file is applied to the base: the project, scheme and production code stay the base's.
        Assert.Equal([new ChangedFile('A', null, "App/" + NewFile)], set.TestFiles);
    }

    [Fact]
    public async Task A_new_test_in_an_existing_test_file_is_new_and_the_files_old_tests_are_not()
    {
        var runner = Runner(head: [(XcodeFixture.TestFile, XcodeFixture.BaseTests.Replace("struct WordsTests {", "struct WordsTests {\n    @Test func spaces() { #expect(isBlank(\" \")) }"))]);

        var result = await Check(runner);

        Assert.True(result.Passed, result.Reason);
        Assert.Equal(["WordsTests/WordsTests/spaces()"], result.Tests.Select(t => t.Test));
        Assert.Equal("xcode-swift-testing", result.Strategy);
    }

    [Fact]
    public async Task With_classic_file_references_a_new_test_in_an_existing_member_file_runs()
    {
        var runner = Runner(classic: true, head: [(XcodeFixture.TestFile, XcodeFixture.BaseTests + "\n@Test func blank() { #expect(isBlank(\"\")) }\n")]);

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/blank()"], set.Tests);
    }

    [Fact]
    public async Task A_new_test_file_that_is_not_a_member_of_its_test_target_is_unsupported_never_no_tests()
    {
        // Classic file references: the PR adds the file but not to the target's Compile Sources.
        var runner = Runner(classic: true, head: [(NewFile, NewTests)]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains($"{NewFile} adds test(s) blank(), Spaces/Only/empty() the gate cannot run: it is not a member of any test target", result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task A_new_test_file_excluded_from_its_synchronized_folder_is_unsupported_never_no_tests()
    {
        var excepted = XcodeFixture.SynchronizedProject
            .Replace("\t\t\tpath = WordsTests;\n", "\t\t\texceptions = ( E1, );\n\t\t\tpath = WordsTests;\n")
            .Replace("/* End PBXFileSystemSynchronizedRootGroup section */",
                "E1 = { isa = PBXFileSystemSynchronizedBuildFileExceptionSet; membershipExceptions = ( BlankTests.swift, ); target = B10000000000000000000102; };\n/* End PBXFileSystemSynchronizedRootGroup section */");
        var runner = Runner(head: [(XcodeFixture.Pbxproj, excepted), (NewFile, NewTests)]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains($"{NewFile} adds test(s)", result.Reason);
        Assert.Contains("not a member of any test target", result.Reason);
    }

    /// <summary>The classic project as the PR changes it: <see cref="NewFile"/> added to the test target, and a production file added to Words.</summary>
    internal static string ClassicWithNewFile => XcodeFixture.ClassicProject
        .Replace("/* End PBXBuildFile section */", "C1 /* BlankTests.swift in Sources */ = {isa = PBXBuildFile; fileRef = C2 /* BlankTests.swift */; };\n"
            + "C3 = {isa = PBXBuildFile; fileRef = C4; };\n/* End PBXBuildFile section */")
        .Replace("/* End PBXFileReference section */", "C2 /* BlankTests.swift */ = {isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = BlankTests.swift; sourceTree = \"<group>\"; };\n"
            + "C4 = {isa = PBXFileReference; path = Words/Extra.swift; sourceTree = SOURCE_ROOT; };\n/* End PBXFileReference section */")
        .Replace("\t\t\t\tB10000000000000000000203 /* WordsTests.swift */,\n\t\t\t);", "\t\t\t\tB10000000000000000000203 /* WordsTests.swift */,\n\t\t\t\tC2 /* BlankTests.swift */,\n\t\t\t);")
        .Replace("\t\t\t\tB10000000000000000000902 /* WordsTests.swift in Sources */,\n", "\t\t\t\tB10000000000000000000902 /* WordsTests.swift in Sources */,\n\t\t\t\tC1 /* BlankTests.swift in Sources */,\n")
        .Replace("\t\tB10000000000000000000601 /* Sources */ = {\n\t\t\tisa = PBXSourcesBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n\t\t\tfiles = (\n",
            "\t\tB10000000000000000000601 /* Sources */ = {\n\t\t\tisa = PBXSourcesBuildPhase;\n\t\t\tbuildActionMask = 2147483647;\n\t\t\tfiles = (\n\t\t\t\tC3,\n");

    [Fact]
    public async Task A_new_file_under_classic_references_runs_on_the_base_with_only_its_test_file_added_to_the_base_project()
    {
        Assert.NotEqual(XcodeFixture.ClassicProject, ClassicWithNewFile);
        var runner = Runner(classic: true, head: [(XcodeFixture.Pbxproj, ClassicWithNewFile), (NewFile, NewTests)]);

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["WordsTests/Spaces/Only/empty()", "WordsTests/blank()"], set.Tests);
        Assert.Contains(new ChangedFile('M', XcodeFixture.Pbxproj, XcodeFixture.Pbxproj), set.TestFiles);
        var grafted = XcodeProject.Parse("Words.xcodeproj", set.BaseReplacements[XcodeFixture.Pbxproj]);
        var tests = grafted.Targets.Single(t => t.Name == "WordsTests");
        var words = grafted.Targets.Single(t => t.Name == "Words");
        // The test file is compiled into the test target; the PR's production file is not in the base's project.
        Assert.True(grafted.Membership(tests, NewFile).Member);
        Assert.True(grafted.Membership(tests, XcodeFixture.TestFile).Member);
        Assert.Equal(XcodeProject.Parse("Words.xcodeproj", XcodeFixture.ClassicProject).Targets.Single(t => t.Name == "Words").Object.Strings("buildPhases"),
            words.Object.Strings("buildPhases"));
        Assert.DoesNotContain("Extra.swift", set.BaseReplacements[XcodeFixture.Pbxproj]);
        Assert.DoesNotContain("C3", set.BaseReplacements[XcodeFixture.Pbxproj]);

        var result = await Check(runner);
        Assert.True(result.Passed, result.Reason);
        Assert.Equal(set.BaseReplacements[XcodeFixture.Pbxproj], runner.Runs[0].Replacements[XcodeFixture.Pbxproj]);
        Assert.Empty(runner.Runs[1].Replacements);
    }

    [Fact]
    public async Task A_test_file_removed_from_a_classic_target_is_taken_out_of_the_base_project_too()
    {
        var removed = XcodeFixture.ClassicProject
            .Replace("\t\t\t\tB10000000000000000000902 /* WordsTests.swift in Sources */,\n", "\t\t\t\tC1 /* BlankTests.swift in Sources */,\n")
            .Replace("/* End PBXBuildFile section */", "C1 /* BlankTests.swift in Sources */ = {isa = PBXBuildFile; fileRef = C2; };\n/* End PBXBuildFile section */")
            .Replace("/* End PBXFileReference section */", "C2 = {isa = PBXFileReference; path = BlankTests.swift; sourceTree = \"<group>\"; };\n/* End PBXFileReference section */")
            .Replace("\t\t\t\tB10000000000000000000203 /* WordsTests.swift */,\n", "\t\t\t\tC2,\n");
        var runner = Runner(classic: true, head: [(XcodeFixture.Pbxproj, removed), (XcodeFixture.TestFile, null), (NewFile, NewTests)]);

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        var grafted = XcodeProject.Parse("Words.xcodeproj", set.BaseReplacements[XcodeFixture.Pbxproj]);
        var tests = grafted.Targets.Single(t => t.Name == "WordsTests");
        Assert.True(grafted.Membership(tests, NewFile).Member);
        Assert.False(grafted.Membership(tests, XcodeFixture.TestFile).Member);
        Assert.Contains(new ChangedFile('D', XcodeFixture.TestFile, null), set.TestFiles);
    }

    [Fact]
    public void A_project_file_edit_that_would_change_anything_else_is_refused()
    {
        var project = XcodeProject.Parse("Words.xcodeproj", XcodeFixture.ClassicProject);
        var tests = project.Targets.Single(t => t.Name == "WordsTests");

        // Two Sources phases: where the file goes cannot be told.
        var two = XcodeProject.Parse("Words.xcodeproj", XcodeFixture.ClassicProject.Replace("\t\t\t\tB10000000000000000000602 /* Sources */,\n",
            "\t\t\t\tB10000000000000000000602 /* Sources */,\n\t\t\t\tB10000000000000000000601 /* Sources */,\n"));
        Assert.Contains("exactly one Sources build phase", two.WithClassicSources(XcodeFixture.ClassicProject, two.Targets.Single(t => t.Name == "WordsTests"), [NewFile], []).Problem);
        // A file outside the project's directory, and one the target does not compile.
        Assert.Contains("outside the project's directory", XcodeProject.Parse("App/Words.xcodeproj", XcodeFixture.ClassicProject)
            .WithClassicSources(XcodeFixture.ClassicProject, tests, ["Other/X.swift"], []).Problem);
        Assert.Contains("no build file", project.WithClassicSources(XcodeFixture.ClassicProject, tests, [], ["WordsTests/None.swift"]).Problem);
        // A list on one line is edited too, and the result checked.
        var oneLine = XcodeFixture.ClassicProject.Replace("\t\t\tfiles = (\n\t\t\t\tB10000000000000000000902 /* WordsTests.swift in Sources */,\n\t\t\t);",
            "\t\t\tfiles = (B10000000000000000000902 /* WordsTests.swift in Sources */,);");
        Assert.NotEqual(XcodeFixture.ClassicProject, oneLine);
        var inline = XcodeProject.Parse("Words.xcodeproj", oneLine);
        var (text, problem) = inline.WithClassicSources(oneLine, inline.Targets.Single(t => t.Name == "WordsTests"), [NewFile], [XcodeFixture.TestFile]);
        Assert.Null(problem);
        var edited = XcodeProject.Parse("Words.xcodeproj", text!);
        Assert.Equal((true, false), (edited.Membership(edited.Targets.Single(t => t.Name == "WordsTests"), NewFile).Member,
            edited.Membership(edited.Targets.Single(t => t.Name == "WordsTests"), XcodeFixture.TestFile).Member));
        // Two definitions of the phase where Xcode writes one: the edit's place cannot be told, so it is refused.
        var twice = XcodeFixture.ClassicProject.Replace("/* End PBXSourcesBuildPhase section */",
            "/* End PBXSourcesBuildPhase section */\n\t\tB10000000000000000000602 /* Sources */ = {isa = PBXSourcesBuildPhase; files = ( ); };");
        Assert.Throws<FormatException>(() => XcodeProject.Parse("Words.xcodeproj", twice)); // a key twice is no project
    }

    [Fact]
    public async Task With_worker_swift_off_an_xcode_projects_test_file_is_unsupported_and_says_why()
    {
        var runner = Runner(head: [(XcodeFixture.Library, XcodeFixture.Fixed), (NewFile, NewTests)]);

        var result = await Check(runner, swift: false);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains($"{NewFile} adds test(s) blank(), Spaces/Only/empty() the gate cannot run: it is in no SwiftPM package (no Package.swift above it), and the gate runs an Xcode project's tests only in a factory with Worker:Swift on",
            result.Reason);
        Assert.Same(NewTestsCheck.Strategies, NewTestsCheck.StrategiesFor(false));
    }

    [Theory]
    [InlineData("SDKROOT = macosx;", "SDKROOT = iphoneos;", "is not a macOS target in its scheme's Debug configuration (it builds for the iphoneos SDK): iOS and other destinations are not run by the gate yet")]
    [InlineData("productType = \"com.apple.product-type.bundle.unit-test\";", "productType = \"com.apple.product-type.bundle.ui-testing\";", "it is compiled only by WordsTests of Words.xcodeproj (a UI-test target, which the gate does not run), not by a unit-test target")]
    public async Task An_ios_or_ui_test_target_is_unsupported(string from, string to, string why)
    {
        var project = XcodeFixture.SynchronizedProject.Replace(from, to);
        var runner = CargoIdentifyTests.Runner([(XcodeFixture.Pbxproj, project), .. XcodeFixture.Files().Skip(1)]);
        runner = CargoIdentifyTests.With(runner, (NewFile, NewTests));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
    }

    [Fact]
    public async Task A_test_target_no_shared_scheme_tests_is_unsupported()
    {
        var runner = Runner(head: [(XcodeFixture.Scheme, null), (NewFile, NewTests)]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("no shared scheme of Words.xcodeproj tests the target WordsTests (share one in Xcode", result.Reason);
    }

    [Theory]
    [InlineData("@Test(arguments: [1, 2]) func many(n: Int) {}", "WordsTests/many(n:) in WordsTests/BlankTests.swift is a parameterized test")]
    [InlineData("final class Legacy: XCTestCase { func testOld() {} }", "WordsTests/Legacy/testOld() in WordsTests/BlankTests.swift is a new XCTest test method")]
    [InlineData("@Test func `with spaces`() {}", "has a backticked name")]
    public async Task Tests_the_gate_cannot_run_apart_yet_are_unsupported(string declaration, string why)
    {
        var runner = Runner(head: [(NewFile, "import Testing\nimport XCTest\n" + declaration + "\n")]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
    }

    [Fact]
    public async Task A_project_that_cannot_be_read_makes_its_test_files_unsupported()
    {
        var runner = Runner(head: [(XcodeFixture.Pbxproj, "{ broken"), (NewFile, NewTests)]);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("which target compiles WordsTests/BlankTests.swift cannot be told: Words.xcodeproj/project.pbxproj cannot be read", result.Reason);
    }

    [Fact]
    public async Task A_project_in_a_workspace_is_built_through_the_workspace()
    {
        var runner = Runner("App/", head: [("App/" + NewFile, NewTests)]);
        runner.BaseFiles["Words.xcworkspace/contents.xcworkspacedata"] = "<Workspace version=\"1.0\"><FileRef location=\"group:App/Words.xcodeproj\"></FileRef></Workspace>";
        runner.BaseFiles["App/Words.xcodeproj/project.xcworkspace/contents.xcworkspacedata"] = "<Workspace version=\"1.0\"><FileRef location=\"self:\"></FileRef></Workspace>";

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal("-workspace\nWords.xcworkspace\nWords", set.Projects["WordsTests/blank()"]);
    }

    [Fact]
    public async Task A_swiftpm_package_beside_an_xcode_project_is_left_to_the_swiftpm_strategy()
    {
        var runner = Runner(head: [(NewFile, NewTests),
            ("Package.swift", SwiftIdentifyTests.Manifest), ("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary),
            (SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + "@Test func blank() { #expect(count(\"\") == 0) }\n")]);
        runner.BaseFiles["Package.swift"] = SwiftIdentifyTests.Manifest;
        runner.BaseFiles["Sources/Words/Words.swift"] = SwiftIdentifyTests.BaseLibrary;
        runner.BaseFiles[SwiftIdentifyTests.TestFile] = SwiftIdentifyTests.BaseTests;

        var result = await Check(runner);

        // Package.swift at the root holds every file below it, the Xcode project's too: SwiftPM's rule (nearest manifest) decides.
        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("WordsTests/BlankTests.swift adds test(s) blank(), Spaces/Only/empty() the gate cannot run: it is outside the package's Tests/<target>/ directories", result.Reason);
    }
}

/// <summary>sc-26131: an Xcode run's steps, xcresulttool's JSON, and xcodebuild's build errors.</summary>
public class XcodeRunTests
{
    private static readonly XcodeNewTests Strategy = new();

    [Fact]
    public async Task The_steps_build_once_per_scheme_through_the_wrapper_and_run_each_test_alone_then_read_its_bundle()
    {
        var projects = new Dictionary<string, string>
        {
            ["WordsTests/blank()"] = "-project\nApp/Words.xcodeproj\nWords",
            ["WordsTests/Spaces/empty()"] = "-project\nApp/Words.xcodeproj\nWords",
        };

        var steps = await Strategy.StepsAsync(null!, "c", ["WordsTests/blank()", "WordsTests/Spaces/empty()"], projects, ".factory-test-results-x", CancellationToken.None);

        var wrapper = SwiftToolchain.DefaultWrapperPath;
        var bundle = $".factory-test-results-x/{XcodeNewTests.BundleName("WordsTests/blank()")}";
        Assert.Equal(
        [
            "Toolchain /usr/bin/xcodebuild -version",
            "Toolchain /usr/bin/xcrun --find xcresulttool",
            $"Build {wrapper} build-for-testing -project App/Words.xcodeproj -scheme Words -destination platform=macOS -quiet",
            $"Test {wrapper} test-without-building -project App/Words.xcodeproj -scheme Words -destination platform=macOS -only-testing:WordsTests/blank() -resultBundlePath {bundle} -quiet",
            $"Test /usr/bin/xcrun xcresulttool get test-results tests --path {bundle}",
            $"Test {wrapper} test-without-building -project App/Words.xcodeproj -scheme Words -destination platform=macOS -only-testing:WordsTests/Spaces/empty() -resultBundlePath .factory-test-results-x/{XcodeNewTests.BundleName("WordsTests/Spaces/empty()")} -quiet",
            $"Test /usr/bin/xcrun xcresulttool get test-results tests --path .factory-test-results-x/{XcodeNewTests.BundleName("WordsTests/Spaces/empty()")}",
        ], steps.Select(s => $"{s.Phase} {s.Program} {string.Join(' ', s.Args)}"));
        Assert.Equal(["xcode-build-0", XcodeNewTests.OutputName("WordsTests/blank()"), XcodeNewTests.JsonName("WordsTests/blank()")],
            steps.Skip(2).Take(3).Select(s => s.CaptureOutput));
        Assert.All(steps, s => Assert.Null(s.ResultFilePattern));
        Assert.Equal(["WordsTests/blank()", "WordsTests/Spaces/empty()"], FakeTestRunner.Filtered(steps));
    }

    private static TestResults Parse(string id, int testExit, string json, int jsonExit = 0) => Strategy.ParseResults(
    [
        new ResultFile(XcodeNewTests.OutputName(id), "") { ExitCode = testExit },
        new ResultFile(XcodeNewTests.JsonName(id), json) { ExitCode = jsonExit },
    ]);

    [Theory]
    // Xcode 26.5's xcresulttool, recorded from the fixture project (Fixtures/xcresult; the device id scrubbed).
    [InlineData("WordsTests/Outer/Inner/deep()", 65, "failed-nested.json", TestCaseResult.Failed)]
    [InlineData("WordsTests/WordsTests/notBlank()", 0, "passed.json", TestCaseResult.Passed)]
    [InlineData("WordsTests/off()", 0, "skipped.json", TestCaseResult.Skipped)]
    public void A_bundles_test_case_is_read_with_its_result(string id, int exit, string fixture, string outcome)
    {
        var results = Parse(id, exit, XcodeFixture.Json(fixture));

        Assert.Empty(results.Unreadable);
        Assert.Equal([new TestCaseResult(id, outcome)], results.Cases[id]);
    }

    [Fact]
    public void A_bundle_with_no_test_case_gives_no_case_so_the_test_counts_as_not_run()
    {
        var results = Parse("WordsTests/nothere()", 0, XcodeFixture.Json("no-test.json"));

        Assert.Empty(results.Unreadable);
        Assert.Empty(results.Cases);
    }

    [Theory]
    [InlineData("WordsTests/WordsTests/notBlank()", 65, "passed.json", 0, "xcodebuild exited 65, which does not agree with its 0 failing case(s)")]
    [InlineData("WordsTests/Outer/Inner/deep()", 0, "failed-nested.json", 0, "xcodebuild exited 0, which does not agree with its 1 failing case(s)")]
    [InlineData("WordsTests/other()", 0, "passed.json", 0, "it holds WordsTests/WordsTests/notBlank(), which is not the test its step ran")]
    [InlineData("WordsTests/WordsTests/notBlank()", 0, "passed.json", 1, "xcresulttool exited 1")]
    public void A_bundle_that_does_not_agree_with_its_step_is_unreadable(string id, int exit, string fixture, int jsonExit, string why)
    {
        var results = Parse(id, exit, XcodeFixture.Json(fixture), jsonExit);

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
    }

    [Theory]
    [InlineData("not json", "it is not JSON")]
    [InlineData("{}", "no testNodes")]
    [InlineData("""{"testNodes":[{"nodeType":"Test Case","nodeIdentifier":"x()","result":"Passed"}]}""", "outside every test bundle")]
    [InlineData("""{"testNodes":[{"nodeType":"Unit test bundle","name":"WordsTests","children":[{"nodeType":"Test Case","nodeIdentifier":"x()","result":"Mixed"}]}]}""", "has the result 'Mixed'")]
    public void Json_that_is_not_xcresulttools_test_results_is_unreadable(string json, string why)
    {
        var results = Parse("WordsTests/x()", 0, json);

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
    }

    [Fact]
    public void A_test_step_without_its_bundles_json_and_an_output_no_step_kept_are_unreadable()
    {
        var results = Strategy.ParseResults(
        [
            new ResultFile(XcodeNewTests.OutputName("WordsTests/a()"), "") { ExitCode = 70 },
            new ResultFile("xcode-test-zz.json", "{}") { ExitCode = 0 },
        ]);

        Assert.Empty(results.Cases);
        Assert.Equal(2, results.Unreadable.Count);
        Assert.Contains(results.Unreadable, u => u.Contains("its test step (exit 70) left no result bundle read"));
        Assert.Contains(results.Unreadable, u => u.Contains("it is not the output of a test step"));
    }

    [Fact]
    public void Build_errors_are_read_from_xcodebuilds_output()
    {
        const string log = """
            Failed frontend command:
            /usr/bin/swift-frontend -frontend -c -primary-file /work/wt/WordsTests/Broken.swift
            /work/wt/WordsTests/Broken.swift:2:31: error: cannot find 'nope' in scope
            @Test func broken() { #expect(nope("")) }
            """;

        Assert.Equal([new BuildError("WordsTests/Broken.swift", 2, 31, "swiftc", "cannot find 'nope' in scope")], Strategy.ParseBuildErrors(log, ["/work/wt"]));
    }

    [Fact]
    public async Task A_base_that_does_not_build_with_the_prs_test_files_is_an_error_never_a_pass()
    {
        var runner = XcodeIdentifyTests.Runner(head: [("WordsTests/BlankTests.swift", "import Testing\n@Test func trims() { #expect(trim(\" a \") == \"a\") }\n")]);
        runner.Answer = spec => spec.OverlayFrom is null ? FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed)
            : FakeTestRunner.NotBuilt(new BuildError("WordsTests/BlankTests.swift", 2, 30, "swiftc", "cannot find 'trim' in scope"));

        var result = await NewTestsCheck.RunAsync(runner, NewTestsCheck.StrategiesFor(true), new RepoRef("acme", "words"), FakeTestRunner.BaseSha, "head1", "gate-sc-1", null,
            CancellationToken.None);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("an Xcode project's base that does not build with the PR's test files is not attributed to the new tests yet", result.Reason);
        Assert.Equal(2, runner.Runs.Count); // no retry
    }
}

/// <summary>sc-26131: an Xcode project's change through the pipeline, with Worker:Swift on.</summary>
public class XcodePipelineTests
{
    [Fact]
    public async Task An_xcode_change_with_a_new_macos_test_failing_on_the_base_and_passing_on_the_head_merges()
    {
        var h = new Harness { Swift = true };
        var runner = h.TestRunner;
        runner.BaseFiles.Clear();
        foreach (var (path, content) in XcodeFixture.Files())
        {
            runner.BaseFiles[path] = content;
        }
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal)
        {
            [XcodeFixture.Library] = XcodeFixture.Fixed,
            ["WordsTests/BlankTests.swift"] = "import Testing\n@testable import Words\n@Test func blank() { #expect(isBlank(\"\")) }\n",
        };
        h.GitHub.Diff = head => $"diff --git a/{XcodeFixture.Library} b/{XcodeFixture.Library}\n+change at {head}\n"
            + $"diff --git a/WordsTests/BlankTests.swift b/WordsTests/BlankTests.swift\n+test at {head}\n";

        var outcome = await h.Run();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([$"merge 1 {Sha1}"], h.Merges);
        var result = NewTestsResult.FromDetail((await h.Rows()).Single(r => r.Step == RunPipeline.Steps.NewTests).Detail)!;
        Assert.Equal((NewTestsOutcome.Pass, "xcode-swift-testing"), (result.Outcome, result.Strategy));
        Assert.Equal([("WordsTests/blank()", TestCaseResult.Failed, TestCaseResult.Passed)], result.Tests.Select(t => (t.Test, t.Base, t.Head)));
        var baseRun = h.TestRunner.Runs[0];
        Assert.Equal(["WordsTests/BlankTests.swift"], baseRun.OverlayPaths);
        Assert.Contains(baseRun.Steps, s => s.Program == SwiftToolchain.DefaultWrapperPath && s.Args.Contains("-only-testing:WordsTests/blank()"));
    }

    [Fact]
    [FailsGateCheck(GateChecks.NewTestsFailOnBase)]
    public async Task With_worker_swift_off_an_xcode_change_is_not_merged()
    {
        var h = new Harness();
        var runner = h.TestRunner;
        runner.BaseFiles.Clear();
        foreach (var (path, content) in XcodeFixture.Files())
        {
            runner.BaseFiles[path] = content;
        }
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal)
        {
            [XcodeFixture.Library] = XcodeFixture.Fixed,
            ["WordsTests/BlankTests.swift"] = "import Testing\n@testable import Words\n@Test func blank() { #expect(isBlank(\"\")) }\n",
        };
        h.GitHub.Diff = head => $"diff --git a/{XcodeFixture.Library} b/{XcodeFixture.Library}\n+change at {head}\n";

        var outcome = await h.Run();

        Assert.Equal(WorkState.Escalated, outcome.State);
        Assert.Empty(h.Merges);
        Assert.Contains("new-tests-fail-on-base (unsupported)", outcome.Error);
        Assert.Contains("only in a factory with Worker:Swift on", outcome.Error);
    }
}

/// <summary>
/// sc-26131: the real toolchain — the repo's xcodebuild wrapper over the real xcodebuild, as the OWNER (never a sandbox user),
/// building the fixture macOS project and running one new Swift Testing test alone on the base and the head, judged from the
/// real result bundle through xcresulttool. Skipped where Xcode is not installed. The DerivedData the wrapper made in the owner's
/// home for the two worktrees is deleted afterwards.
/// </summary>
public class XcodeRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-xcoderun-").FullName;
    private readonly string _remote;
    private readonly string _seed;
    private readonly List<string> _derivedData = [];
    private readonly List<string> _madeHere = [];

    public XcodeRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var directory in new[] { SwiftToolchain.DerivedDataRootOf(home), SwiftToolchain.SourcePackagesOf(home), Path.GetDirectoryName(SwiftToolchain.SourcePackagesOf(home))! })
        {
            if (!Directory.Exists(directory))
            {
                _madeHere.Add(directory);
            }
        }
    }

    public void Dispose()
    {
        foreach (var directory in _derivedData.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
        // What the wrapper made in the owner's home and did not exist before, if nothing else is in it.
        foreach (var directory in _madeHere.Where(d => Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()))
        {
            Directory.Delete(directory);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static readonly Lazy<bool> HasXcode = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(XcodeNewTests.Xcodebuild, ["-version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0 && OperatingSystem.IsMacOS();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_seed, path))!);
        File.WriteAllText(Path.Combine(_seed, path), content);
    }

    private string CommitAndPush(string message, string branch)
    {
        TestGit.Run(_seed, "add", "-A");
        TestGit.Run(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        TestGit.Run(_seed, "push", "-q", _remote, branch);
        return TestGit.Run(_seed, "rev-parse", "HEAD").Trim();
    }

    /// <summary><paramref name="directory"/> as <c>pwd -P</c> prints it (the temp directory is under <c>/var</c>, a link to <c>/private/var</c>).</summary>
    private static string Physical(string directory)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/pwd", ["-P"]) { WorkingDirectory = directory, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return output;
    }

    private async Task<NewTestsResult> Check(string baseSha, string headSha)
    {
        // The repo's wrapper, as the owner (unsandboxed: slots null), over the real /usr/bin/xcodebuild.
        var wrapper = Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild");
        var result = await NewTestsCheck.RunAsync(
            new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null, TimeSpan.FromMinutes(15)),
            NewTestsCheck.StrategiesFor(swift: true, wrapper), Repo, baseSha, headSha, "gate-sc-5", null, TestContext.Current.CancellationToken);
        var worktrees = Physical(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _derivedData.AddRange(new[] { "gate-sc-5-base", "gate-sc-5-head" }.Select(n => SwiftToolchain.DerivedDataOf(home, $"{worktrees}/{n}")));
        return result;
    }

    [Fact]
    public async Task With_xcode_a_new_macos_test_is_run_alone_on_the_base_and_the_head_and_judged_from_its_result_bundle()
    {
        Assert.SkipUnless(HasXcode.Value, "Xcode is not installed");
        foreach (var (path, content) in XcodeFixture.Files())
        {
            Write(path, content);
        }
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        Write(XcodeFixture.Library, XcodeFixture.Fixed);
        // A new file in the synchronized test folder (no project change), a free test and one nested in types.
        Write("WordsTests/BlankTests.swift",
            "import Testing\n@testable import Words\n\n@Test func blank() { #expect(isBlank(\"  \")) }\nstruct Spaces { struct Only { @Test func empty() { #expect(isBlank(\"\")) } } }\n");
        var passing = CommitAndPush("fix", "factory/sc-5");

        var pass = await Check(baseSha, passing);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal(("ran", "ran", "xcode-swift-testing"), (pass.BaseRun, pass.HeadRun, pass.Strategy));
        Assert.Equal([("WordsTests/Spaces/Only/empty()", "failed", "passed"), ("WordsTests/blank()", "failed", "passed")],
            pass.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        // Each ran alone: one case each, on both commits.
        Assert.All(pass.Tests, t => Assert.Equal((1, 1), (t.BaseCases.Count, t.HeadCases.Count)));

        Write(XcodeFixture.TestFile, XcodeFixture.BaseTests.Replace("struct WordsTests {", "struct WordsTests {\n    @Test func one() { #expect(!isBlank(\"x\")) }"));
        var vacuous = CommitAndPush("a test that checks nothing", "factory/sc-5");

        var rejected = await Check(baseSha, vacuous);

        Assert.True(rejected.Outcome == NewTestsOutcome.Rejected, $"{rejected.Outcome}: {rejected.Reason}");
        Assert.EndsWith("so they check nothing: WordsTests/WordsTests/one()", rejected.Reason);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    [Fact]
    public async Task With_xcode_a_new_test_file_under_classic_references_is_built_on_the_base_with_only_its_file_added_to_the_base_project()
    {
        Assert.SkipUnless(HasXcode.Value, "Xcode is not installed");
        foreach (var (path, content) in XcodeFixture.Files(classic: true))
        {
            Write(path, content);
        }
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        // The PR's project file adds the test file to WordsTests and a new production file to Words; the base run takes only the former.
        Write(XcodeFixture.Pbxproj, XcodeIdentifyTests.ClassicWithNewFile);
        Write("Words/Extra.swift", "public func extra() -> Int { 1 }\n");
        Write(XcodeFixture.Library, XcodeFixture.Fixed);
        Write("WordsTests/BlankTests.swift", "import Testing\n@testable import Words\n\n@Test func blank() { #expect(isBlank(\"  \") && extra() == 1) }\n");
        var head = CommitAndPush("fix", "factory/sc-5");

        var result = await Check(baseSha, head);

        // On the base the test file is compiled (no Extra.swift there: extra() does not exist, so it does not build — an error, not
        // yet attributed) — so the test is written against the base's API only, below.
        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("cannot find 'extra' in scope", result.Reason);

        Write("WordsTests/BlankTests.swift", "import Testing\n@testable import Words\n\n@Test func blank() { #expect(isBlank(\"  \")) }\n");
        var plain = CommitAndPush("only the base's API", "factory/sc-5");

        var pass = await Check(baseSha, plain);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal([("WordsTests/blank()", "failed", "passed")], pass.Tests.Select(t => (t.Test, t.Base, t.Head)));
    }
}
