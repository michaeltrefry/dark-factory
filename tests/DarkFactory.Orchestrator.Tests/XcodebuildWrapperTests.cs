using System.Diagnostics;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// <c>scripts/factory-xcodebuild</c> (sc-26130), the only xcodebuild command a worker is allowed: each allowed form reaches xcodebuild
/// with exactly the arguments it should, the forced ad hoc signing, the running user's own DerivedData (one per working directory) and
/// SwiftPM checkouts and the forced macro/plugin validation skips, and each
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
        string[] args, Dictionary<string, string>? env = null, string? workingDirectory = null)
    {
        if (File.Exists(Ran))
        {
            File.Delete(Ran);
        }
        var psi = new ProcessStartInfo(Wrapper()) { WorkingDirectory = workingDirectory ?? _dir, RedirectStandardOutput = true, RedirectStandardError = true };
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

    private static string[] Forced(string home, string workingDirectory) =>
        ["-derivedDataPath", SwiftToolchain.DerivedDataOf(home, Physical(workingDirectory)), "-clonedSourcePackagesDirPath", SwiftToolchain.SourcePackagesOf(home),
            "-skipMacroValidation", "-skipPackagePluginValidation",
            "CODE_SIGN_IDENTITY=-", "DEVELOPMENT_TEAM=", "PROVISIONING_PROFILE_SPECIFIER=", "PROVISIONING_PROFILE="];

    /// <summary><paramref name="directory"/> as <c>pwd -P</c> prints it (the temp directory is under <c>/var</c>, a link to <c>/private/var</c>).</summary>
    private static string Physical(string directory)
    {
        var psi = new ProcessStartInfo("/bin/pwd", ["-P"]) { WorkingDirectory = directory, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().TrimEnd('\n');
        p.WaitForExit();
        return output;
    }

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
        // This Mac (a macOS app, e.g. LensWorks).
        { ["build", "-scheme", "LensWorks", "-destination", "platform=macOS"], ["build", "-scheme", "LensWorks", "-destination", "platform=macOS"] },
        { ["test", "-scheme", "App", "-destination", "platform=macOS,arch=arm64"], ["test", "-scheme", "App", "-destination", "platform=macOS,arch=arm64"] },
        {
            ["build-for-testing", "-scheme", "App", "-destination", "generic/platform=macOS,arch=x86_64,variant=Mac Catalyst"],
            ["build-for-testing", "-scheme", "App", "-destination", "generic/platform=macOS,arch=x86_64,variant=Mac Catalyst"]
        },
        { ["build", "-scheme", "App", "-destination", "platform=macOS,variant=macOS"], ["build", "-scheme", "App", "-destination", "platform=macOS,variant=macOS"] },
        // The gate's base/head runs: test-without-building, serial tests, a result bundle in its hidden results directory, Swift Testing ids.
        {
            ["test-without-building", "-scheme", "App", "-destination", "platform=macOS", "-parallel-testing-enabled", "NO",
                "-only-testing:AppTests/Suite/f(x:)", "-only-testing:AppTests/Suite/g(_:)",
                "-resultBundlePath", ".factory-test-results-0a1b2c3d4e5f/r.xcresult"],
            ["test-without-building", "-scheme", "App", "-destination", "platform=macOS",
                "-only-testing:AppTests/Suite/f(x:)", "-only-testing:AppTests/Suite/g(_:)", "-parallel-testing-enabled", "NO",
                "-resultBundlePath", ".factory-test-results-0a1b2c3d4e5f/r.xcresult"]
        },
        {
            ["test", "-scheme", "App", "-destination", "platform=iOS Simulator,name=iPhone 17", "-parallel-testing-enabled", "NO"],
            ["test", "-scheme", "App", "-destination", "platform=iOS Simulator,name=iPhone 17", "-parallel-testing-enabled", "NO"]
        },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public async Task An_allowed_build_reaches_xcodebuild_with_ad_hoc_signing_and_the_users_own_derived_data_and_package_checkouts(
        string[] args, string[] expected)
    {
        var (exitCode, stderr, ran, _) = await RunAsync(args);

        Assert.True(exitCode == 0, stderr);
        Assert.Equal([.. expected, .. Forced(SandboxSupport.PasswdHome(), _dir)], ran);
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
        { ["build-for-testing", "test-without-building", "-scheme", "App", "-destination", "platform=iOS Simulator"], "a second action 'test-without-building'" },
        { ["test-without-building", "-scheme", "App", "-destination", "platform=macOS", "-xctestrun", "a.xctestrun"], "the option '-xctestrun'" },
        { ["test", "-scheme", "App", "-destination", "platform=macOS", "-testPlan", "All"], "the option '-testPlan'" },
        { ["test", "-scheme", "App", "-destination", "platform=macOS", "-parallel-testing-enabled", "YES"], "-parallel-testing-enabled 'YES' (only NO)" },
        { ["test", "-scheme", "App", "-destination", "platform=macOS", "-parallel-testing-enabled", "no"], "-parallel-testing-enabled 'no' (only NO)" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS", "-parallel-testing-enabled", "NO"], "-parallel-testing-enabled outside the test actions" },
        { ["test", "-scheme", "App", "-destination", "platform=macOS", "-parallel-testing-enabled", "NO", "-parallel-testing-enabled", "NO"], "a second -parallel-testing-enabled" },
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
        { ["build", "-scheme", "App", "-destination", "platform=visionOS"], "the destination 'platform=visionOS'" },
        { ["build", "-scheme", "App", "-destination", "generic/platform=visionOS"], "the destination 'generic/platform=visionOS'" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS,id=00008110-000A1B2C3D4E5F6A"], "the destination 'platform=iOS,id=00008110-000A1B2C3D4E5F6A'" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS,name=Someone's Mac"], "the destination 'platform=macOS,name=Someone's Mac'" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS,arch=ppc"], "the destination 'platform=macOS,arch=ppc'" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS,variant=DriverKit"], "the destination 'platform=macOS,variant=DriverKit'" },
        { ["build", "-scheme", "App", "-destination", "platform=macOS,arch=arm64;rm -rf ~"], "the destination" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator,name=x;rm -rf ~"], "the destination" },
        // Paths stay in the working directory.
        { ["build", "-project", "/Users/someone/App.xcodeproj", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path '/Users/someone/App.xcodeproj'" },
        { ["build", "-project", "../App.xcodeproj", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path '../App.xcodeproj'" },
        { ["build", "-workspace", "a/../../App.xcworkspace", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -workspace path" },
        { ["build", "-project", "App.xcworkspace", "-scheme", "App", "-destination", "platform=iOS Simulator"], "the -project path 'App.xcworkspace'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "/tmp/r.xcresult"], "the result bundle path '/tmp/r.xcresult'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "../r.xcresult"], "the result bundle path '../r.xcresult'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "./r.xcresult"], "the result bundle path './r.xcresult'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "a/./r.xcresult"], "the result bundle path 'a/./r.xcresult'" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-resultBundlePath", "..r/../../r.xcresult"], "the result bundle path '..r/../../r.xcresult'" },
        // Names that could be options, and what a build must have.
        { ["build", "-scheme", "-quiet", "-destination", "platform=iOS Simulator"], "the scheme name '-quiet'" },
        { ["build", "-scheme", "App", "-scheme", "Other", "-destination", "platform=iOS Simulator"], "a second -scheme" },
        { ["build", "-destination", "platform=iOS Simulator"], "a build without -scheme" },
        { ["build", "-scheme", "App"], "a build without -destination" },
        { ["build", "-scheme", "App", "-destination", "platform=iOS Simulator", "-only-testing:AppTests"], "-only-testing/-skip-testing outside the test actions" },
        { ["test", "-scheme", "App", "-destination", "platform=iOS Simulator", "-only-testing::x"], "the test identifier" },
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
    public async Task A_path_that_resolves_outside_the_working_directory_through_a_symbolic_link_is_refused()
    {
        var outside = Directory.CreateTempSubdirectory("df-xcodebuild-outside-").FullName;
        Directory.CreateDirectory(Path.Combine(outside, "Real.xcodeproj"));
        File.CreateSymbolicLink(Path.Combine(_dir, "out"), outside); // a parent directory that is a link out
        File.CreateSymbolicLink(Path.Combine(_dir, "Linked.xcodeproj"), Path.Combine(outside, "Real.xcodeproj"));
        File.CreateSymbolicLink(Path.Combine(_dir, "dangling.xcresult"), Path.Combine(outside, "none"));
        Directory.CreateDirectory(Path.Combine(_dir, "results"));
        File.CreateSymbolicLink(Path.Combine(_dir, "in"), Path.Combine(_dir, "results")); // a link that stays inside

        foreach (var (args, why) in new (string[], string)[]
        {
            (["test", "-scheme", "App", "-destination", "platform=macOS", "-resultBundlePath", "out/r.xcresult"], "the result bundle path 'out/r.xcresult'"),
            (["test", "-scheme", "App", "-destination", "platform=macOS", "-resultBundlePath", "out/not/yet/r.xcresult"], "the result bundle path 'out/not/yet/r.xcresult'"),
            (["test", "-scheme", "App", "-destination", "platform=macOS", "-resultBundlePath", "dangling.xcresult"], "the result bundle path 'dangling.xcresult'"),
            (["build", "-project", "Linked.xcodeproj", "-scheme", "App", "-destination", "platform=macOS"], "the -project path 'Linked.xcodeproj'"),
            (["build", "-project", "out/Real.xcodeproj", "-scheme", "App", "-destination", "platform=macOS"], "the -project path 'out/Real.xcodeproj'"),
        })
        {
            var (exitCode, stderr, ran, _) = await RunAsync(args);
            Assert.Equal(64, exitCode);
            Assert.Contains("factory-xcodebuild: refusing " + why, stderr);
            Assert.Empty(ran);
        }

        // A link that resolves inside the working directory, and a path whose directories do not exist yet, are fine.
        foreach (var bundle in new[] { "in/r.xcresult", "new/dir/r.xcresult" })
        {
            var (exitCode, stderr, ran, _) = await RunAsync(["test", "-scheme", "App", "-destination", "platform=macOS", "-resultBundlePath", bundle]);
            Assert.True(exitCode == 0, stderr);
            Assert.Contains(bundle, ran);
        }
        Directory.Delete(outside, recursive: true);
    }

    [Fact]
    public async Task Each_working_directory_builds_into_its_own_derived_data()
    {
        // Two worktrees (two items, or a gate's base and head runs) never share build products.
        var other = Directory.CreateTempSubdirectory("df-xcodebuild-other-").FullName;
        string[] build = ["build", "-scheme", "App", "-destination", "platform=macOS"];

        var (firstExit, firstErr, first, _) = await RunAsync(build);
        var (secondExit, secondErr, second, _) = await RunAsync(build, workingDirectory: other);

        Assert.True(firstExit == 0, firstErr);
        Assert.True(secondExit == 0, secondErr);
        var home = SandboxSupport.PasswdHome();
        var (one, two) = (DerivedData(first), DerivedData(second));
        Assert.NotEqual(one, two);
        Assert.Equal(SwiftToolchain.DerivedDataOf(home, Physical(_dir)), one);
        Assert.Equal(SwiftToolchain.DerivedDataOf(home, Physical(other)), two);
        Assert.All([one, two], d => Assert.StartsWith(SwiftToolchain.DerivedDataRootOf(home) + "/", d));
        Assert.Matches("/[0-9a-f]{16}$", one);
        // The same working directory always gets the same one (build-for-testing, then test-without-building).
        Assert.Equal(one, DerivedData((await RunAsync(["test-without-building", "-scheme", "App", "-destination", "platform=macOS"])).Args));
        Directory.Delete(other, recursive: true);

        static string DerivedData(string[] ran) => ran[Array.IndexOf(ran, "-derivedDataPath") + 1];
    }

    [Fact]
    public async Task A_gate_run_resolves_its_packages_inside_its_own_derived_data_and_a_workers_run_into_the_shared_checkouts()
    {
        // sc-26131: the shared checkouts are every worker session's to edit; the gate's run uses checkouts its own clean deletes.
        var home = SandboxSupport.PasswdHome();
        var derived = SwiftToolchain.DerivedDataOf(home, Physical(_dir));
        foreach (var action in new[] { "build-for-testing", "test-without-building" })
        {
            var (exitCode, stderr, ran, _) = await RunAsync(["--gate", action, "-scheme", "App", "-destination", "platform=macOS"]);
            Assert.True(exitCode == 0, stderr);
            Assert.Equal(derived + "/SourcePackages", ran[Array.IndexOf(ran, "-clonedSourcePackagesDirPath") + 1]);
            Assert.Equal(derived, ran[Array.IndexOf(ran, "-derivedDataPath") + 1]);
            Assert.Equal(action, ran[0]);
        }
        var (workerExit, workerErr, worker, _) = await RunAsync(["build-for-testing", "-scheme", "App", "-destination", "platform=macOS"]);
        Assert.True(workerExit == 0, workerErr);
        Assert.Equal(SwiftToolchain.SourcePackagesOf(home), worker[Array.IndexOf(worker, "-clonedSourcePackagesDirPath") + 1]);
    }

    public static TheoryData<string[], string> RefusedGateForms => new()
    {
        { ["--gate", "build", "-scheme", "App", "-destination", "platform=macOS"], "--gate with the action 'build'" },
        { ["--gate", "test", "-scheme", "App", "-destination", "platform=macOS"], "--gate with the action 'test'" },
        { ["--gate", "-list"], "--gate with -list" },
        { ["build", "--gate", "-scheme", "App", "-destination", "platform=macOS"], "the option '--gate'" },
        { ["clean-derived-data", "-quiet"], "clean-derived-data with arguments" },
        { ["--gate", "clean-derived-data"], "--gate before clean-derived-data" },
        { ["build", "clean-derived-data"], "the action 'clean-derived-data'" },
    };

    [Theory]
    [MemberData(nameof(RefusedGateForms))]
    public async Task A_refused_gate_or_clean_form_runs_nothing(string[] args, string why)
    {
        var (exitCode, stderr, ran, _) = await RunAsync(args);

        Assert.Equal(64, exitCode);
        Assert.Contains("factory-xcodebuild: refusing " + why, stderr);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task Clean_derived_data_deletes_this_working_directorys_derived_data_and_nothing_else()
    {
        var home = SandboxSupport.PasswdHome();
        var derived = SwiftToolchain.DerivedDataOf(home, Physical(_dir));
        var other = Directory.CreateTempSubdirectory("df-xcodebuild-keep-").FullName;
        var otherDerived = SwiftToolchain.DerivedDataOf(home, Physical(other));
        try
        {
            // What a worker session of the same user could have planted: products, a build database, package checkouts.
            Directory.CreateDirectory(Path.Combine(derived, "Build", "Products", "Debug"));
            File.WriteAllText(Path.Combine(derived, "Build", "Products", "Debug", "planted"), "x");
            Directory.CreateDirectory(Path.Combine(derived, "SourcePackages", "checkouts", "dep"));
            Directory.CreateDirectory(otherDerived);

            var (exitCode, stderr, ran, _) = await RunAsync(["clean-derived-data"]);

            Assert.True(exitCode == 0, stderr);
            Assert.Empty(ran); // xcodebuild is not run
            Assert.False(Directory.Exists(derived) || File.Exists(derived));
            Assert.True(Directory.Exists(otherDerived)); // another working directory's is not touched
            // Nothing there is a success too.
            Assert.Equal(0, (await RunAsync(["clean-derived-data"])).ExitCode);

            // A symbolic link at the path is removed, never followed.
            var target = Directory.CreateTempSubdirectory("df-xcodebuild-target-").FullName;
            File.WriteAllText(Path.Combine(target, "kept"), "x");
            File.CreateSymbolicLink(derived, target);
            var (linkExit, linkErr, _, _) = await RunAsync(["clean-derived-data"]);
            Assert.True(linkExit == 0, linkErr);
            Assert.False(File.Exists(derived) || Directory.Exists(derived) || new FileInfo(derived).LinkTarget is not null);
            Assert.True(File.Exists(Path.Combine(target, "kept")));
            Directory.Delete(target, recursive: true);
        }
        finally
        {
            foreach (var d in new[] { derived, otherDerived })
            {
                if (new FileInfo(d).LinkTarget is not null)
                {
                    File.Delete(d);
                }
                else if (Directory.Exists(d))
                {
                    Directory.Delete(d, recursive: true);
                }
            }
            Directory.Delete(other, recursive: true);
        }
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
        Assert.Contains(SwiftToolchain.DerivedDataOf(home, Physical(_dir)), ran);
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
            Assert.All([SwiftToolchain.DerivedDataOf(home, "/opt/w/a"), SwiftToolchain.SourcePackagesOf(home), .. SwiftToolchain.SwiftPmCachesOf(home)],
                path => Assert.StartsWith(home + "/", path));
        }
        Assert.NotEqual(SwiftToolchain.DerivedDataOf("/Users/_factory", "/opt/w/a"), SwiftToolchain.DerivedDataOf("/Users/_factory2", "/opt/w/a"));
        Assert.NotEqual(SwiftToolchain.DerivedDataOf("/Users/_factory", "/opt/w/a"), SwiftToolchain.DerivedDataOf("/Users/_factory", "/opt/w/b"));
        // The wrapper's text names exactly these paths.
        var text = File.ReadAllText(RepoWrapper);
        Assert.Contains("derived_data=" + SwiftToolchain.DerivedDataRootOf("$home") + "/$work_key\n", text);
        Assert.Contains("source_packages=" + SwiftToolchain.SourcePackagesOf("$home") + "\n", text);
    }
}
