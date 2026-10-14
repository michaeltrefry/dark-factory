using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests;

// TEMPORARY diagnostic (removed before review): prints swift build's stdout/stderr on Linux.
public class SwiftLinuxProbeTests
{
    private static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(SwiftNewTests.Swift, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return $"== {string.Join(' ', args)} exit {p.ExitCode}\n-- STDOUT --\n{o.Result}\n-- STDERR --\n{e.Result}\n";
    }

    private static string Package(string lib, string tests)
    {
        var dir = Directory.CreateTempSubdirectory("df-probe-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "Sources/Words"));
        Directory.CreateDirectory(Path.Combine(dir, "Tests/WordsTests"));
        File.WriteAllText(Path.Combine(dir, "Package.swift"), SwiftIdentifyTests.Manifest);
        File.WriteAllText(Path.Combine(dir, "Sources/Words/Words.swift"), lib);
        File.WriteAllText(Path.Combine(dir, SwiftIdentifyTests.TestFile), tests);
        return dir;
    }

    [Fact]
    public void Probe()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "linux probe");
        var text = Run(Path.GetTempPath(), "--version");
        var broken = Package(SwiftIdentifyTests.BaseLibrary + "public func broken() -> Int { nope }\n", SwiftIdentifyTests.BaseTests);
        text += Run(broken, "build", "--package-path", broken, "--build-tests");
        var clash = Package(SwiftIdentifyTests.BaseLibrary + "public func make() -> String { \"\" }\n",
            SwiftIdentifyTests.BaseTests + "func make() -> Int { 1 }\n@Test func made() { let x = make(); #expect(x == 1) }\n");
        text += Run(clash, "build", "--package-path", clash, "--build-tests");
        Assert.Fail(text);
    }
}
