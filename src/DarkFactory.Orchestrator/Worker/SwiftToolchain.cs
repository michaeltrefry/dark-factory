using System.Security.Cryptography;
using System.Text;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// The Swift/Xcode toolchain every sandbox user (worker slot) needs when the factory works Swift repositories (Phase 5, sc-26130;
/// <c>Worker:Swift</c>, <see cref="FactoryOptions.WorkerSwift"/>): Swift, Xcode's <c>xcodebuild</c> with its first launch done and its
/// license accepted, and the factory's xcodebuild wrapper (<c>scripts/factory-xcodebuild</c>, installed root-owned at
/// <see cref="WrapperPath"/> by <c>setup-worker-user.sh</c>) — the only way a worker runs xcodebuild
/// (<see cref="ClaudeWorker.XcodebuildTools"/>). A sandbox carries one only when <c>Worker:Swift</c> is on
/// (<see cref="WorkerSandbox.Swift"/>), so a .NET/Rust-only factory is never refused for a missing Xcode (E7).
/// Each sandbox user builds into its own caches, all under its own home (the launch helper sets HOME from the password database,
/// never the caller's): xcodebuild's DerivedData and SwiftPM checkouts are the wrapper's fixed per-user paths
/// (<see cref="DerivedDataOf"/>, <see cref="SourcePackagesOf"/>), and <c>swift build</c>/<c>swift test</c> keep SwiftPM's caches and
/// configuration under <c>~/Library/Caches/org.swift.swiftpm</c> and <c>~/Library/org.swift.swiftpm</c> of that same home, with the
/// user's own TMPDIR (<see cref="SwiftPmCachesOf"/>), so no two slots share a build or package cache.
/// </summary>
/// <param name="SwiftPath">Swift as the sandbox user runs it (the system shim, which resolves Xcode's).</param>
/// <param name="XcodebuildPath">xcodebuild as the probe runs it (the system shim; the wrapper runs the same path).</param>
/// <param name="WrapperPath">The installed wrapper: what the worker's allow rule names.</param>
/// <param name="WrapperSource">The wrapper as <c>scripts/factory-xcodebuild</c> has it, which the installed one must equal; null is the copy compiled into this assembly.</param>
public sealed record SwiftToolchain(
    string SwiftPath = SwiftToolchain.DefaultSwiftPath,
    string XcodebuildPath = SwiftToolchain.DefaultXcodebuildPath,
    string WrapperPath = SwiftToolchain.DefaultWrapperPath,
    string? WrapperSource = null)
{
    public const string DefaultSwiftPath = "/usr/bin/swift";
    public const string DefaultXcodebuildPath = "/usr/bin/xcodebuild";

    /// <summary>Where <c>setup-worker-user.sh</c> installs the wrapper, beside the launch helpers (one copy for every slot: it pins nothing per user).</summary>
    public const string DefaultWrapperPath = "/usr/local/libexec/dark-factory/factory-xcodebuild";

    /// <summary>The wrapper's DerivedData for the user whose home is <paramref name="home"/>.</summary>
    public static string DerivedDataOf(string home) => $"{home}/Library/Developer/Xcode/DerivedData/dark-factory";

    /// <summary>The wrapper's SwiftPM checkouts (<c>-clonedSourcePackagesDirPath</c>) for the user whose home is <paramref name="home"/>.</summary>
    public static string SourcePackagesOf(string home) => $"{home}/Library/Caches/dark-factory/SourcePackages";

    /// <summary>Where <c>swift build</c>/<c>swift test</c> keep SwiftPM's cache and configuration for the user whose home is <paramref name="home"/> (SwiftPM's own, per home).</summary>
    public static IReadOnlyList<string> SwiftPmCachesOf(string home) =>
        [$"{home}/Library/Caches/org.swift.swiftpm", $"{home}/Library/org.swift.swiftpm"];

    /// <summary><c>scripts/factory-xcodebuild</c> as built into this assembly (never written out or run: only compared).</summary>
    private static readonly Lazy<string> CompiledWrapper = new(() =>
    {
        using var stream = typeof(SwiftToolchain).Assembly.GetManifestResourceStream("factory-xcodebuild")
            ?? throw new InvalidOperationException("The xcodebuild wrapper is not compiled into the orchestrator.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Why <paramref name="installed"/> (the installed wrapper's text) is not the current <c>scripts/factory-xcodebuild</c>, or null.
    /// Setup installs it unedited, so its SHA-256 must be <paramref name="source"/>'s (null: the compiled-in wrapper).
    /// </summary>
    public static string? StaleWrapperReason(string installed, string? source = null)
    {
        var (have, want) = (Sha256(installed), Sha256(source ?? CompiledWrapper.Value));
        return have == want ? null : $"its text differs from the repo's scripts/factory-xcodebuild (sha256 {have[..12]}…, expected {want[..12]}…)";
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The part of the check that runs nothing (start-up, and every run's start): the installed wrapper must be readable and the
    /// current one. <paramref name="reinstall"/> is the setup command to name.
    /// </summary>
    public async Task EnsureWrapperCurrentAsync(string reinstall, CancellationToken ct)
    {
        string installed;
        try
        {
            installed = await File.ReadAllTextAsync(WrapperPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Swift toolchain (Worker:Swift): cannot read the xcodebuild wrapper {WrapperPath} ({ex.Message}); run `{reinstall} --swift` to install it.", ex);
        }
        if (StaleWrapperReason(installed, WrapperSource) is { } stale)
        {
            throw new InvalidOperationException(
                $"Swift toolchain (Worker:Swift): the installed xcodebuild wrapper {WrapperPath} is stale ({stale}); re-run `{reinstall} --swift` to install it.");
        }
    }

    /// <summary>
    /// The probe of one sandbox user (<paramref name="user"/>), each command run as that user through <paramref name="run"/> (its
    /// launch helper, under its slot's lock): Swift answers, xcodebuild answers, Xcode's first launch is done
    /// (<c>xcodebuild -checkFirstLaunchStatus</c>), its license is accepted (<c>xcodebuild -license check</c>), and the wrapper runs
    /// as that user (with no arguments it prints its usage and exits 64, running nothing). The first that fails throws, naming the
    /// user, the command and the fix.
    /// </summary>
    public async Task EnsureUsableAsync(
        string user, string reinstall, Func<string, string[], Task<(int ExitCode, string Stdout, string Stderr)>> run)
    {
        await Expect(SwiftPath, ["--version"], "Swift is not usable; install Xcode (or its Command Line Tools) and select it with `sudo xcode-select -s /Applications/Xcode.app`");
        await Expect(XcodebuildPath, ["-version"], "Xcode's xcodebuild is not usable; install Xcode and select it with `sudo xcode-select -s /Applications/Xcode.app`");
        await Expect(XcodebuildPath, ["-checkFirstLaunchStatus"], "Xcode's first launch has not been completed; run `sudo xcodebuild -runFirstLaunch`");
        await Expect(XcodebuildPath, ["-license", "check"], "Xcode's license has not been accepted; run `sudo xcodebuild -license accept`");
        var wrapper = await run(WrapperPath, []);
        if (wrapper.ExitCode != 64 || !wrapper.Stderr.Contains("factory-xcodebuild: usage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Swift toolchain (Worker:Swift) of {user}: the xcodebuild wrapper {WrapperPath} does not run as {user} "
                + $"(exit {wrapper.ExitCode}: {Clip(wrapper.Stderr)}); re-run `{reinstall} --swift`.");
        }

        async Task Expect(string program, string[] args, string fix)
        {
            var result = await run(program, args);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Swift toolchain (Worker:Swift) of {user}: `{program} {string.Join(' ', args)}` as {user} exited {result.ExitCode} "
                    + $"({Clip(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)}): {fix}, then re-run `{reinstall} --swift` to check it.");
            }
        }
    }

    private static string Clip(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
    }
}
