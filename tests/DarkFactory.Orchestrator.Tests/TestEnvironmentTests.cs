using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Prepare;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class TestEnvironmentTests
{
    [Theory]
    [InlineData("dotnet")]
    [InlineData("cargo")]
    [InlineData("swift")]
    public void Only_test_commands_receive_values_without_mutating_the_original_step(string program)
    {
        var values = new Dictionary<string, string> { ["REPO_MODE"] = "fixture" };
        foreach (var phase in new[] { TestPhase.Toolchain, TestPhase.Restore, TestPhase.Build, TestPhase.Test, TestPhase.Cleanup })
        {
            var original = new TestStep(phase, program, ["test", "--fixture"]);
            var actual = SandboxTestRunner.WithTestEnvironment(original, values, Path.GetTempPath());
            Assert.Equal(program, original.Program);
            if (phase != TestPhase.Test) Assert.Same(original, actual);
            else
            {
                Assert.Equal("/usr/bin/env", actual.Program);
                Assert.Equal(["REPO_MODE=fixture", program, "test", "--fixture"], actual.Args);
            }
        }
        var collector = new TestStep(TestPhase.Test, "/usr/bin/xcrun", ["xcresulttool"]);
        Assert.Same(collector, SandboxTestRunner.WithTestEnvironment(collector, values, Path.GetTempPath()));
        var xcode = new TestStep(TestPhase.Test, SwiftToolchain.DefaultWrapperPath, ["--gate", "test-without-building"]);
        Assert.Equal(["--gate", "test-without-building", "--test-env", "REPO_MODE=fixture"], SandboxTestRunner.WithTestEnvironment(xcode, values, Path.GetTempPath()).Args);
    }

    [Theory]
    [InlineData("MODE", "hello\n")]
    [InlineData("MODE", "hello\t")]
    [InlineData("MODE", "${worktree}/a//b")]
    [InlineData("MODE", "../a")]
    [InlineData("MODE", "/tmp/a")]
    [InlineData("MODE", "x${worktree}/a")]
    [InlineData("MODE", "https://example.test/a")]
    [InlineData("MODE", "${worktree}/a/../../b")]
    [InlineData("home", "x")]
    [InlineData("Repo_Secret", "x")]
    public void Unsafe_values_and_names_fail_closed(string key, string value) =>
        Assert.Contains(key, Assert.Throws<PrepareRecipeException>(() => TestEnvironment.Validate(new Dictionary<string, string> { [key] = value })).Message);

    [Fact]
    public void Bounds_unknown_keys_duplicate_keys_and_controls_are_refused()
    {
        const string recipe = "version: 1\nsteps: [{name: setup, argv: [make]}]\n";
        foreach (var yaml in new[] { "test_env: {X: a, X: b}", "test_env: {X: [a]}", "test_env: {X: a}\nunknown: true", "test_env: {X: \"a\\u0001\"}" })
            Assert.Throws<PrepareRecipeException>(() => PrepareRecipe.Parse(recipe + yaml));
        Assert.Throws<PrepareRecipeException>(() => TestEnvironment.Validate(Enumerable.Range(0, 33).ToDictionary(n => "V" + n, _ => "x")));
        Assert.Throws<PrepareRecipeException>(() => TestEnvironment.Validate(new Dictionary<string, string> { ["V"] = new('a', 1025) }));
        Assert.Throws<PrepareRecipeException>(() => TestEnvironment.Validate(Enumerable.Range(0, 9).ToDictionary(n => "V" + n, _ => new string('a', 1024))));
    }

    [Fact]
    public void Worker_prepare_does_not_apply_test_environment_or_widen_its_protocol()
    {
        const string yaml = "version: 1\nsteps: [{name: noop, argv: [/bin/echo, ready]}]\n";
        var plain = PrepareRecipe.Parse(yaml);
        var declared = PrepareRecipe.Parse(yaml + "test_env: {REPO_MODE: fixture}\n");
        Assert.Equal(PrepareSteps.WorkerPlan(plain, "/w", new Dictionary<string, string>()), PrepareSteps.WorkerPlan(declared, "/w", new Dictionary<string, string>()));
        Assert.Equal(PrepareSteps.TestSteps(plain, new Dictionary<string, string>()).Select(SandboxTestRunner.Describe), PrepareSteps.TestSteps(declared, new Dictionary<string, string>()).Select(SandboxTestRunner.Describe));
    }

    [Fact]
    public void Workspace_placeholders_are_job_local_and_links_to_owner_paths_are_refused()
    {
        var directory = Directory.CreateTempSubdirectory("df-test-env-").FullName;
        try
        {
            var values = new Dictionary<string, string> { ["REPO_HELPER"] = "${worktree}/tools/helper" };
            Assert.EndsWith("/tools/helper", TestEnvironment.Resolve(values, directory)["REPO_HELPER"]);
            Directory.CreateSymbolicLink(Path.Combine(directory, "tools"), Path.GetTempPath());
            Assert.Contains("REPO_HELPER", Assert.Throws<PrepareRecipeException>(() => TestEnvironment.Resolve(values, directory)).Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    // The same test is launched below through dotnet test in a separate testhost process.
    // Outside that explicitly selected child invocation it is inert, not an environment-dependent suite failure.
    [Fact]
    public async Task Environment_helper_child()
    {
        if (!Environment.GetCommandLineArgs().Contains("-method", StringComparer.Ordinal)) return;
        Assert.Equal("fixture", Environment.GetEnvironmentVariable("REPO_MODE"));
        var helper = Environment.GetEnvironmentVariable("REPO_HELPER");
        Assert.NotNull(helper);
        using var process = Process.Start(new ProcessStartInfo(helper) { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("repository-helper-ok", output);
    }

    [Fact]
    public async Task Real_dotnet_testhost_receives_helper_environment_and_fails_honestly_without_it()
    {
        var root = Directory.CreateTempSubdirectory("df-env-host-").FullName;
        try
        {
            SandboxSupport.Executable(root, "helper", "#!/bin/sh\n[ \"$REPO_MODE\" = fixture ] || exit 7\necho repository-helper-ok\n");
            var step = new TestStep(TestPhase.Test, "dotnet", ["exec", typeof(TestEnvironmentTests).Assembly.Location, "-method", "DarkFactory.Orchestrator.Tests.TestEnvironmentTests.Environment_helper_child"]);
            var values = new Dictionary<string, string> { ["REPO_MODE"] = "fixture", ["REPO_HELPER"] = "${worktree}/helper" };
            var yes = await Run(SandboxTestRunner.WithTestEnvironment(step, values, root), root);
            Assert.True(yes.Exit == 0, yes.Output);
            var no = await Run(step, root);
            Assert.NotEqual(0, no.Exit);
            Assert.Contains("Assert.Equal() Failure", no.Output);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("cargo")]
    [InlineData("swift")]
    public async Task Real_package_tests_receive_workspace_helper_and_fail_without_environment(string program)
    {
        try { if ((await Run(new(TestPhase.Toolchain, program, ["--version"]), Path.GetTempPath())).Exit != 0) Assert.Skip(program + " unavailable"); }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip(program + " unavailable"); }
        var root = Directory.CreateTempSubdirectory("df-env-package-").FullName;
        try
        {
            SandboxSupport.Executable(root, "helper", "#!/bin/sh\n[ \"$REPO_MODE\" = fixture ] || exit 7\necho repository-helper-ok\n");
            IReadOnlyList<string> buildArgs, testArgs;
            if (program == "cargo")
            {
                Directory.CreateDirectory(Path.Combine(root, "src"));
                File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"env_fixture\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
                File.WriteAllText(Path.Combine(root, "src/lib.rs"), "#[test] fn helper_environment() { assert_eq!(std::env::var(\"REPO_MODE\").unwrap(), \"fixture\"); let result = std::process::Command::new(std::env::var(\"REPO_HELPER\").unwrap()).output().unwrap(); assert!(result.status.success()); assert!(String::from_utf8(result.stdout).unwrap().contains(\"repository-helper-ok\")); }");
                buildArgs = ["test", "--offline", "--no-run"];
                testArgs = ["test", "--offline"];
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(root, "Tests/EnvFixtureTests"));
                File.WriteAllText(Path.Combine(root, "Package.swift"), "// swift-tools-version: 6.0\nimport PackageDescription\nlet package = Package(name: \"EnvFixture\", targets: [.testTarget(name: \"EnvFixtureTests\")])");
                File.WriteAllText(Path.Combine(root, "Tests/EnvFixtureTests/Env.swift"), "import Foundation\nimport Testing\n@Test func helperEnvironment() throws { #expect(ProcessInfo.processInfo.environment[\"REPO_MODE\"] == \"fixture\"); let p = Process(); p.executableURL = URL(fileURLWithPath: try #require(ProcessInfo.processInfo.environment[\"REPO_HELPER\"])); try p.run(); p.waitUntilExit(); #expect(p.terminationStatus == 0) }");
                buildArgs = ["build", "--build-system", "native", "--build-tests"];
                testArgs = ["test", "--build-system", "native", "--skip-build"];
            }
            var build = await Run(new(TestPhase.Build, program, buildArgs), root);
            Assert.True(build.Exit == 0, build.Output);
            var step = new TestStep(TestPhase.Test, program, testArgs);
            var values = new Dictionary<string, string> { ["REPO_MODE"] = "fixture", ["REPO_HELPER"] = "${worktree}/helper" };
            var yes = await Run(SandboxTestRunner.WithTestEnvironment(step, values, root), root);
            Assert.True(yes.Exit == 0, yes.Output);
            Assert.NotEqual(0, (await Run(step, root)).Exit);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static (string Prefix, string Destination, string[] Trusted) ExecutableFixture(string directory, string scenario)
    {
        var prefix = SandboxTestRunner.RealPath(Directory.CreateDirectory(Path.Combine(directory, "filesystem")).FullName);
        var bin = Path.Combine(prefix, "usr/local/bin");
        var destination = scenario switch
        {
            "homebrew" => Path.Combine(prefix, "opt/homebrew/Cellar/helper/1.0/bin/repo-helper"),
            "dotnet" => Path.Combine(prefix, "usr/local/share/dotnet/repo-helper"),
            "cryptex" => Path.Combine(prefix, "System/Volumes/Preboot/Cryptexes/OS/usr/bin/repo-helper"),
            _ => Path.Combine(prefix, "owner/private/repo-helper"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        SandboxSupport.ExecutableAt(destination, "#!/bin/sh\nexit 0\n");
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        if (scenario == "parent-owner") Directory.CreateSymbolicLink(bin, Path.GetDirectoryName(destination)!);
        else
        {
            Directory.CreateDirectory(bin);
            File.CreateSymbolicLink(Path.Combine(bin, "repo-helper"), destination);
        }
        return (prefix, destination, [prefix + "/usr/local/bin", prefix + "/opt/homebrew/Cellar", prefix + "/usr/local/share/dotnet", prefix + "/System/Volumes/Preboot/Cryptexes/OS/usr/bin"]);
    }

    [Theory]
    [InlineData("leaf-owner", false)]
    [InlineData("parent-owner", false)]
    [InlineData("homebrew", true)]
    [InlineData("dotnet", true)]
    [InlineData("cryptex", true)]
    public void Fixed_executables_forward_only_verified_canonical_system_or_tool_destinations(string scenario, bool allowed)
    {
        var directory = Directory.CreateTempSubdirectory("df-env-executable-").FullName;
        try
        {
            var fixture = ExecutableFixture(directory, scenario);
            var values = new Dictionary<string, string> { ["REPO_HELPER"] = "/usr/local/bin/repo-helper" };
            IReadOnlyDictionary<string, string> Resolve() => TestEnvironment.Resolve(values, directory,
                path => SandboxTestRunner.RealPath(fixture.Prefix + path), fixture.Trusted);
            if (allowed)
            {
                var resolved = Resolve()["REPO_HELPER"];
                Assert.Equal(fixture.Destination, resolved);
                using var process = Process.Start(new ProcessStartInfo(resolved))!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            else Assert.Contains("REPO_HELPER", Assert.Throws<PrepareRecipeException>(() => Resolve()).Message);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Fixed_executable_destination_policy_matches_sealed_python_on_isolated_filesystems()
    {
        var directory = Directory.CreateTempSubdirectory("df-env-canonical-parity-").FullName;
        try
        {
            var cases = new[] { "leaf-owner", "parent-owner", "homebrew", "dotnet", "cryptex" }
                .Select(scenario => (Scenario: scenario, Fixture: ExecutableFixture(Directory.CreateDirectory(Path.Combine(directory, scenario)).FullName, scenario))).ToArray();
            var script = Path.Combine(directory, "runner.py");
            await File.WriteAllTextAsync(script, RemoteIosWorkflow.Runner);
            const string python = "import json,runpy,sys,pathlib; r=runpy.run_path(sys.argv[1]); result=[]\nfor case in json.load(sys.stdin):\n try:\n  value=r['resolve_test_environment']({'REPO_HELPER':'/usr/local/bin/repo-helper'},pathlib.Path(case['prefix']),lambda p:str(pathlib.Path(case['prefix']+p).resolve(strict=True)),case['trusted'])\n  result.append(value['TEST_RUNNER_REPO_HELPER'])\n except ValueError as error: result.append(str(error))\nprint(json.dumps(result))";
            using var process = Process.Start(new ProcessStartInfo("python3", ["-I", "-c", python, script]) { WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await process.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(cases.Select(c => new { prefix = c.Fixture.Prefix, trusted = c.Fixture.Trusted })));
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stderr);
            var answers = System.Text.Json.JsonSerializer.Deserialize<string[]>(await stdout)!;
            for (var i = 0; i < cases.Length; i++)
                if (cases[i].Scenario.EndsWith("owner", StringComparison.Ordinal)) Assert.Contains("REPO_HELPER", answers[i]);
                else Assert.Equal(cases[i].Fixture.Destination, answers[i]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Csharp_and_sealed_python_validate_the_same_bounded_contract()
    {
        var cases = new List<Dictionary<string, string>>();
        foreach (var key in new[] { "MODE", "HOME", "home", "DYLD_A", "LD_A", "REPO_TOKEN", "REPO_PASSWORD", "TEST_RUNNER_MODE", "FACTORY_GUI_TEST_BINDING", "REPO_HELPER" })
            foreach (var value in new[] { "", "fixture", "${worktree}/tools/helper", "/usr/bin/true", "/Users/owner/tool", "../tool", "${artifact:sdk}/tool", "__CURRENT_VALUE__", "line\n", "${worktree}/a/../b" })
                cases.Add(new() { [key] = value });
        cases.Add(Enumerable.Range(0, 33).ToDictionary(n => "V" + n, _ => "x"));
        cases.Add(new() { ["MODE"] = new string('x', 1025) });
        cases.Add(Enumerable.Range(0, 9).ToDictionary(n => "V" + n, _ => new string('x', 1024)));
        var root = Directory.CreateTempSubdirectory("df-env-parity-").FullName;
        try
        {
            var script = Path.Combine(root, "runner.py");
            await File.WriteAllTextAsync(script, RemoteIosWorkflow.Runner);
            var python = "import json,runpy,sys; r=runpy.run_path(sys.argv[1]); result=[]\nfor value in json.load(sys.stdin):\n try: r['validate_test_environment'](value); result.append(True)\n except ValueError: result.append(False)\nprint(json.dumps(result))";
            using var process = Process.Start(new ProcessStartInfo("python3", ["-I", "-c", python, script]) { WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await process.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(cases));
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stderr);
            var expected = cases.Select(values => { try { TestEnvironment.Validate(values); return true; } catch (PrepareRecipeException) { return false; } }).ToArray();
            Assert.Equal(expected, System.Text.Json.JsonSerializer.Deserialize<bool[]>(await stdout));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task<(int Exit, string Output)> Run(TestStep step, string cwd)
    {
        var psi = new ProcessStartInfo(step.Program, step.Args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        // Test isolation: inherited repository inputs may not make the negative half pass.
        psi.Environment.Remove("REPO_MODE"); psi.Environment.Remove("REPO_HELPER");
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
