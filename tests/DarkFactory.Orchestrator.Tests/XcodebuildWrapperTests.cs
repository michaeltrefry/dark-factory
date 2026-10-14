using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <c>scripts/factory-xcodebuild</c> (sc-26130), the only way a worker runs xcodebuild: each allowed form reaches xcodebuild with
/// exactly the arguments it should, the forced ad hoc signing and the running user's own DerivedData and SwiftPM checkouts, and each
/// refused form runs nothing. Run as the current user on a COPY of the repo's script whose one <c>xcodebuild=</c> line names a stub
/// that records its arguments and environment — never the real xcodebuild, never a sandbox user.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class XcodebuildWrapperTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("df-xcodebuild-").FullName;

    private static string RepoWrapper => Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-xcodebuild");

    private const string RealLine = "xcodebuild=/usr/bin/xcodebuild";

    private string Ran => Path.Combine(_dir, "stub-ran");

    /// <summary>The copy, its xcodebuild the stub (which writes <c>ARG</c> and <c>ENV</c> lines to <see cref="Ran"/>).</summary>
    private string Wrapper()
    {
        var stub = SandboxSupport.Executable(_dir, "stub-xcodebuild", $$"""
            #!/bin/sh
            for a in "$@"; do echo "ARG $a"; done >> '{{Ran}}'
            /usr/bin/env | sed 's/^/ENV /' >> '{{Ran}}'
            """);
        var text = File.ReadAllText(RepoWrapper);
        Assert.Equal(1, CountLines(text, RealLine));
        return SandboxSupport.Executable(_dir, "factory-xcodebuild", text.Replace("\n" + RealLine + "\n", $"\nxcodebuild={stub}\n"));
    }

    private static int CountLines(string text, string line) => text.Split('\n').Count(l => l == line);

    private async Task<(int ExitCode, string Stderr, string[] Args, Dictionary<string, string> Env)> RunAsync(
        string[] args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(Wrapper()) { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in env ?? [])
        {
            psi.Environment[key] = value;
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        await stdout;
        var lines = File.Exists(Ran) ? File.ReadAllLines(Ran) : [];
        return (p.ExitCode, await stderr,
            [.. lines.Where(l => l.StartsWith("ARG ", StringComparison.Ordinal)).Select(l => l[4..])],
            SandboxSupport.ParseEnv(string.Join('\n', lines.Where(l => l.StartsWith("ENV ", StringComparison.Ordinal)).Select(l => l[4..]))));
    }

    private static string[] Forced(string home) =>
        ["-derivedDataPath", SwiftToolchain.DerivedDataOf(home), "-clonedSourcePackagesDirPath", SwiftToolchain.SourcePackagesOf(home),
            "CODE_SIGN_IDENTITY=-", "DEVELOPMENT_TEAM=", "PROVISIONING_PROFILE_SPECIFIER=", "PROVISIONING_PROFILE="];

    [Fact]
    public void The_installed_wrapper_runs_the_systems_xcodebuild_and_the_compiled_copy_is_the_repos()
    {
        var text = File.ReadAllText(RepoWrapper);
        Assert.Equal(1, CountLines(text, RealLine));
        Assert.Null(SwiftToolchain.StaleWrapperReason(text));
        Assert.Contains("differs from the repo's scripts/factory-xcodebuild", SwiftToolchain.StaleWrapperReason(text + "# edited\n"));
        // The worker's allow rule names the installed absolute path (not the plain name its own ~/.local/bin could shadow).
        Assert.Equal("/usr/local/libexec/dark-factory/factory-xcodebuild", SwiftToolchain.DefaultWrapperPath);
    }

    public static TheoryData<string[], string[]> Allowed => new()
    {
        {
            ["build", "-scheme", "App", "-destination", "platform=iOS Simulator,name=iPhone 17"],
            ["build", "-scheme", "App", "-destination", "platform=iOS Simulator,name=iPhone 17"]
        },
        {
            ["build-for-testing", "-scheme", "Foo-Package", "-destination", "generic/platform=iOS Simulator"],
            ["build-for-testing", "-scheme", "Foo-Package", "-destination", "generic/platform=iOS Simulator"]
        },
        {
            // Every option, in any order; the wrapper puts them in its own.
            ["-quiet", "-only-testing:AppTests/FooTests/testBar", "test", "-configuration", "Debug", "-workspace", "App.xcworkspace",
                "-destination", "platform=iOS Simulator,name=iPhone 17,OS=26.0", "-skip-testing:AppTests/Slow", "-scheme", "My App",
                "-resultBundlePath", "out/run 1.xcresult", "-only-testing:AppTests/FooTests/swiftTesting()"],
            ["test", "-workspace", "App.xcworkspace", "-scheme", "My App", "-destination", "platform=iOS Simulator,name=iPhone 17,OS=26.0",
                "-configuration", "Debug", "-only-testing:AppTests/FooTests/testBar", "-skip-testing:AppTests/Slow",
                "-only-testing:AppTests/FooTests/swiftTesting()", "-resultBundlePath", "out/run 1.xcresult", "-quiet"]
        },
        {
            ["test", "-project", "sub/App.xcodeproj", "-scheme", "App", "-destination", "platform=visionOS Simulator,id=0A1B2C3D-0000-4000-8000-000000000000"],
            ["test", "-project", "sub/App.xcodeproj", "-scheme", "App", "-destination", "platform=visionOS Simulator,id=0A1B2C3D-0000-4000-8000-000000000000"]
        },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public async Task An_allowed_build_reaches_xcodebuild_with_ad_hoc_signing_and_the_users_own_derived_data_and_package_checkouts(
        string[] args, string[] expected)
    {
        var (exitCode, stderr, ran, _) = await RunAsync(args);

        Assert.True(exitCode == 0, stderr);
        Assert.Equal([.. expected, .. Forced(SandboxSupport.PasswdHome())], ran);
    }

    public static TheoryData<string[]> Containers => new() { Array.Empty<string>(), new[] { "-project", "App.xcodeproj" }, new[] { "-workspace", "a/App.xcworkspace" } };

    [Theory]
    [MemberData(nameof(Containers))]
    public async Task The_list_query_reaches_xcodebuild_with_only_the_users_own_package_checkouts(string[] container)
    {
        var (exitCode, stderr, ran, _) = await RunAsync(["-list", .. container]);

        Assert.True(exitCode == 0, stderr);
        // -derivedDataPath is not given: xcodebuild refuses it without a scheme. -list builds nothing.
        Assert.Equal(["-list", .. container, "-clonedSourcePackagesDirPath", SwiftToolchain.SourcePackagesOf(SandboxSupport.PasswdHome())], ran);
    }

    public static TheoryData<string[], string> Refused => new()
    {
        // Every action but build, build-for-testing and test.
        { ["archive", "-scheme", "App", "-destination", "generic/platform=iOS Simulator"], "the action 'archive'" },
        { ["-exportArchive", "-archivePath", "a.xcarchive", "-exportPath", "out"], "the option '-exportArchive'" },
        { ["analyze", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the action 'analyze'" },
        { ["clean", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the action 'clean'" },
        { ["installsrc"], "the action 'installsrc'" },
        { ["test-without-building", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the action 'test-without-building'" },
        { ["build", "test", "-scheme", "App", "-destination", "platform=iOS Simulator"], "a second action 'test'" },
        // Signing identities, teams, profiles, accounts.
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "CODE_SIGN_IDENTITY=Apple Development"], "the build setting override 'CODE_SIGN_IDENTITY=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "CODE_SIGN_IDENTITY=-"], "the build setting override 'CODE_SIGN_IDENTITY=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "DEVELOPMENT_TEAM=ABCDE12345"], "the build setting override 'DEVELOPMENT_TEAM=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "PROVISIONING_PROFILE_SPECIFIER=Prod"], "the build setting override 'PROVISIONING_PROFILE_SPECIFIER=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-allowProvisioningUpdates"], "the option '-allowProvisioningUpdates'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-allowProvisioningDeviceRegistration"], "the option '-allowProvisioningDeviceRegistration'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-authenticationKeyPath", "k.p8"], "the option '-authenticationKeyPath'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-exportOptionsPlist", "o.plist"], "the option '-exportOptionsPlist'" },
        // Build-setting overrides that run scripts or redirect outputs, and the settings files that would carry them.
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "OTHER_SWIFT_FLAGS=-load-plugin-executable x"], "the build setting override 'OTHER_SWIFT_FLAGS=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "SYMROOT=/tmp/out"], "the build setting override 'SYMROOT=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "CONFIGURATION_BUILD_DIR=/tmp"], "the build setting override 'CONFIGURATION_BUILD_DIR=…'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-xcconfig", "evil.xcconfig"], "the option '-xcconfig'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-derivedDataPath", "/tmp/dd"], "the option '-derivedDataPath'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-clonedSourcePackagesDirPath", "/tmp/sp"], "the option '-clonedSourcePackagesDirPath'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-skipPackagePluginValidation"], "the option '-skipPackagePluginValidation'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-skipMacroValidation"], "the option '-skipMacroValidation'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-sdk", "iphoneos"], "the option '-sdk'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-showBuildSettings"], "the option '-showBuildSettings'" },
        // Simulator only.
        { ["build", "-scheme", "App", "-destination", "platform=iOS,name=My iPhone"], "the destination 'platform=iOS,name=My iPhone'" },
        { ["build", "-scheme", "App", "-destination", "generic/platform=iOS"], "the destination 'generic/platform=iOS'" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS"], "the destination 'platform=macOS'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator,name=x;rm -rf ~"], "the destination" },
        // Paths stay in the working directory.
        { ["build", "-project", "/Users/someone/App.xcodeproj", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path '/Users/someone/App.xcodeproj'" },
        { ["build", "-project", "../App.xcodeproj", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path '../App.xcodeproj'" },
        { ["build", "-workspace", "a/../../App.xcworkspace", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -workspace path" },
        { ["build", "-project", "App.xcworkspace", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path 'App.xcworkspace'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "/tmp/r.xcresult"], "the result bundle path '/tmp/r.xcresult'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "../r.xcresult"], "the result bundle path '../r.xcresult'" },
        // Names that could be options, and what a build must have.
        { ["build", "-scheme", "-quiet", "-destination", "platform=iOS Simulator"], "the scheme name '-quiet'" },
        { ["build", "-scheme", "App", "-scheme", "Other", "-destination", "platform=iOS Simulator"], "a second -scheme" },
        { ["build", "-destination", "platform=iOS Simulator"], "a build without -scheme" },
        { ["build", "-scheme", "App"], "a build without -destination" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-only-testing:AppTests"], "-only-testing/-skip-testing outside the test action" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-only-testing:../x"], "the test identifier" },
        { ["-list", "-scheme", "App"], "-list with anything but -project or -workspace" },
        { ["-list", "build"], "a second action 'build'" },
        { ["-version"], "the option '-version'" },
        { ["-scheme"], "-scheme without a name" },
        { [""], "the action ''" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refused_form_runs_nothing_and_says_what_it_refused(string[] args, string why)
    {
        var (exitCode, stderr, ran, _) = await RunAsync(args);

        Assert.Equal(64, exitCode);
        Assert.Contains("factory-xcodebuild: refusing " + why, stderr);
        Assert.False(File.Exists(Ran), "the refused form reached xcodebuild");
        Assert.Empty(ran);
    }

    [Fact]
    public async Task No_arguments_prints_the_usage_and_runs_nothing()
    {
        var (exitCode, stderr, _, _) = await RunAsync([]);

        Assert.Equal(64, exitCode);
        Assert.Contains("factory-xcodebuild: usage", stderr);
        Assert.False(File.Exists(Ran));
    }

    [Fact]
    public async Task Xcodebuild_gets_a_fresh_environment_with_the_password_databases_home_never_the_callers_variables()
    {
        // xcodebuild reads environment variables as build settings: none of the caller's may reach it, nor a HOME that would move
        // DerivedData and the package checkouts into another user's (or another slot's) directories.
        var (exitCode, stderr, ran, env) = await RunAsync(["build", "-scheme", "App", "-destination", "platform=iOS Simulator"],
            new()
            {
                ["HOME"] = _dir,
                ["CODE_SIGN_IDENTITY"] = "Apple Distribution",
                ["DEVELOPER_DIR"] = _dir,
                ["ANTHROPIC_AUTH_TOKEN"] = "router-key",
                ["TMPDIR"] = _dir, // our own directory: kept
            });

        Assert.True(exitCode == 0, stderr);
        var home = SandboxSupport.PasswdHome();
        // (The stub's own shell adds its PWD, SHLVL and _.)
        Assert.Equal(["HOME", "PATH", "TMPDIR"], env.Keys.Except(["PWD", "OLDPWD", "SHLVL", "_"]).Order(StringComparer.Ordinal));
        Assert.Equal(home, env["HOME"]);
        Assert.Equal("/usr/bin:/bin:/usr/sbin:/sbin", env["PATH"]);
        Assert.Equal(_dir, env["TMPDIR"]);
        Assert.Contains(SwiftToolchain.DerivedDataOf(home), ran);
        Assert.DoesNotContain(ran, a => a.Contains(_dir, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tmpdir_that_is_not_the_users_own_directory_is_dropped()
    {
        var link = Path.Combine(_dir, "tmp-link");
        File.CreateSymbolicLink(link, _dir);

        var (exitCode, stderr, _, env) = await RunAsync(["build", "-scheme", "App", "-destination", "platform=iOS Simulator"],
            new() { ["TMPDIR"] = link });

        Assert.True(exitCode == 0, stderr);
        Assert.False(env.ContainsKey("TMPDIR"));
    }

    [Fact]
    public void Each_sandbox_users_caches_are_under_its_own_home()
    {
        // The launch helper gives every worker its own user's HOME (from the password database); each slot's DerivedData, SwiftPM
        // checkouts and swift build/test caches are under it, so two slots never share one.
        foreach (var home in new[] { "/Users/_factory", "/Users/_factory2" })
        {
            Assert.All([SwiftToolchain.DerivedDataOf(home), SwiftToolchain.SourcePackagesOf(home), .. SwiftToolchain.SwiftPmCachesOf(home)],
                path => Assert.StartsWith(home + "/", path));
        }
        Assert.NotEqual(SwiftToolchain.DerivedDataOf("/Users/_factory"), SwiftToolchain.DerivedDataOf("/Users/_factory2"));
        // The wrapper's text names exactly these paths.
        var text = File.ReadAllText(RepoWrapper);
        Assert.Contains("derived_data=" + SwiftToolchain.DerivedDataOf("$home") + "\n", text);
        Assert.Contains("source_packages=" + SwiftToolchain.SourcePackagesOf("$home") + "\n", text);
    }
}
