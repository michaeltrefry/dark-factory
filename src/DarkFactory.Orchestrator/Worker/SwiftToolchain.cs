using System.Security.Cryptography;
using System.Text;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// The Swift/Xcode toolchain every sandbox user (worker slot) needs when the factory works Swift repositories (Phase 5, sc-26130;
/// <c>Worker:Swift</c>, <see cref="FactoryOptions.WorkerSwift"/>): Swift, Xcode's <c>xcodebuild</c> with its first launch done and its
/// license accepted, and the factory's xcodebuild wrapper (<c>scripts/factory-xcodebuild</c>, installed root-owned at
/// <see cref="WrapperPath"/> by <c>setup-worker-user.sh</c>) — the only xcodebuild command a worker is allowed
/// (<see cref="ClaudeWorker.XcodebuildTools"/>; build code it runs is not bound by it: CLAUDE.md's residuals). A sandbox carries one only when <c>Worker:Swift</c> is on
/// (<see cref="WorkerSandbox.Swift"/>), so a .NET/Rust-only factory is never refused for a missing Xcode (E7).
/// Each sandbox user builds into its own caches, all under its own home (the launch helper sets HOME from the password database,
/// never the caller's): xcodebuild's DerivedData (one per working directory, <see cref="DerivedDataOf"/>) and SwiftPM checkouts
/// (<see cref="SourcePackagesOf"/>) are the wrapper's per-user paths, and SwiftPM run directly (<c>swift build</c>/<c>swift test</c>,
/// where a worker is allowed them) keeps its caches and configuration under <c>~/Library/Caches/org.swift.swiftpm</c> and
/// <c>~/Library/org.swift.swiftpm</c> of that same home (<see cref="SwiftPmCachesOf"/>), so no two slots share a build or package cache.
/// </summary>
/// <param name="SwiftPath">Swift as the sandbox user runs it (the system shim, which resolves Xcode's).</param>
/// <param name="XcodebuildPath">xcodebuild as the probe runs it (the system shim; the wrapper runs the same path).</param>
/// <param name="WrapperPath">The installed wrapper: what the worker's allow rule names.</param>
/// <param name="WrapperSource">The wrapper as <c>scripts/factory-xcodebuild</c> has it, which the installed one must equal; null is the copy compiled into this assembly.</param>
/// <param name="LocalIosSimulator">Explicit local simulator execution only. Production dispatches iOS gate tests remotely and leaves this false.</param>
public sealed record SwiftToolchain(
    string SwiftPath = SwiftToolchain.DefaultSwiftPath,
    string XcodebuildPath = SwiftToolchain.DefaultXcodebuildPath,
    string WrapperPath = SwiftToolchain.DefaultWrapperPath,
    string? WrapperSource = null,
    string LaunchctlPath = SwiftToolchain.DefaultLaunchctlPath,
    string XcrunPath = SwiftToolchain.DefaultXcrunPath,
    bool LocalIosSimulator = false)
{
    /// <summary>xcrun, through which the probe lists the user's simulator runtimes and device types (<c>xcrun simctl list … -j</c>; sc-26134).</summary>
    public const string DefaultXcrunPath = "/usr/bin/xcrun";

    /// <summary>launchctl, whose <c>managername</c> says which launchd session a process is in (sc-26132: a simulator needs <c>Aqua</c>).</summary>
    public const string DefaultLaunchctlPath = "/bin/launchctl";

    public const string DefaultSwiftPath = "/usr/bin/swift";
    public const string DefaultXcodebuildPath = "/usr/bin/xcodebuild";

    /// <summary>Where <c>setup-worker-user.sh</c> installs the wrapper, beside the launch helpers (one copy for every slot: it pins nothing per user).</summary>
    public const string DefaultWrapperPath = "/usr/local/libexec/dark-factory/factory-xcodebuild";

    /// <summary>
    /// The wrapper's DerivedData for the user whose home is <paramref name="home"/> working in <paramref name="workingDirectory"/> (its
    /// physical path, as <c>pwd -P</c> prints it): <c>~/Library/Developer/Xcode/DerivedData/dark-factory/&lt;key&gt;</c>, the key the
    /// first 16 hex characters of the SHA-256 of that path. One per worktree, so two items, or a gate's base and head runs, never share
    /// build products; the same worktree's <c>build-for-testing</c> and <c>test-without-building</c> share one.
    /// </summary>
    public static string DerivedDataOf(string home, string workingDirectory) =>
        $"{DerivedDataRootOf(home)}/{Sha256(workingDirectory)[..16]}";

    /// <summary>The directory under which the wrapper keeps every working directory's DerivedData, for the user whose home is <paramref name="home"/>.</summary>
    public static string DerivedDataRootOf(string home) => $"{home}/Library/Developer/Xcode/DerivedData/dark-factory";

    /// <summary>
    /// The stamp of the wrapper's xcodebuild run in <paramref name="workingDirectory"/> (its physical path) for the user whose home
    /// is <paramref name="home"/> (sc-26134): made before xcodebuild starts, removed once what the run made writable for others in
    /// the working directory has been closed again; one a killed run left is kept by the next run, whose clean-up then covers both.
    /// </summary>
    public static string BuildStampOf(string home, string workingDirectory) =>
        $"{home}/Library/Caches/dark-factory/xcodebuild-runs/{Sha256(workingDirectory)[..16]}";

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
    /// (<c>xcodebuild -checkFirstLaunchStatus</c>), its license is accepted (<c>xcodebuild -license check</c>), the local SDKs
    /// are installed (<c>xcodebuild -showsdks</c>, <see cref="RequiredSdks"/>), and the wrapper runs as that user.
    /// Production dispatches iOS gate tests remotely, so it needs neither an iOS Simulator SDK nor an Aqua session or local
    /// CoreSimulator. An explicit <see cref="LocalIosSimulator"/> toolchain additionally requires all three: local execution
    /// must still fail closed when its simulator prerequisites are absent. The probe boots no simulator.
    /// </summary>
    public async Task EnsureUsableAsync(
        string user, string reinstall, Func<string, string[], Task<(int ExitCode, string Stdout, string Stderr)>> run)
    {
        await Expect(SwiftPath, ["--version"], "Swift is not usable; install Xcode (or its Command Line Tools) and select it with `sudo xcode-select -s /Applications/Xcode.app`");
        await Expect(XcodebuildPath, ["-version"], "Xcode's xcodebuild is not usable; install Xcode and select it with `sudo xcode-select -s /Applications/Xcode.app`");
        await Expect(XcodebuildPath, ["-checkFirstLaunchStatus"], "Xcode's first launch has not been completed; run `sudo xcodebuild -runFirstLaunch`");
        await Expect(XcodebuildPath, ["-license", "check"], "Xcode's license has not been accepted; run `sudo xcodebuild -license accept`");
        var sdks = await Expect(XcodebuildPath, ["-showsdks"], "Xcode's SDKs cannot be listed; reinstall Xcode");
        if ((LocalIosSimulator ? RequiredSdks.Concat(LocalSimulatorSdks) : RequiredSdks).Where(s => !sdks.Contains("-sdk " + s.Sdk, StringComparison.Ordinal)).ToList() is { Count: > 0 } missing)
        {
            throw new InvalidOperationException(
                $"Swift toolchain (Worker:Swift) of {user}: `{XcodebuildPath} -showsdks` as {user} lists no "
                + string.Join(" and no ", missing.Select(s => $"{s.Platform} SDK (-sdk {s.Sdk}…)"))
                + ": install " + string.Join(" and ", missing.Select(s => s.Fix))
                + $", then re-run `{reinstall} --swift` to check it.");
        }
        if (LocalIosSimulator)
        {
            var manager = await run(LaunchctlPath, ["managername"]);
            if (manager.ExitCode != 0 || manager.Stdout.Trim() != "Aqua")
            {
                throw new InvalidOperationException(
                    $"Swift toolchain (Worker:Swift) of {user}: `{LaunchctlPath} managername` as {user} answers "
                    + $"'{Clip(manager.ExitCode == 0 ? manager.Stdout : $"exit {manager.ExitCode}: {manager.Stderr}")}', not 'Aqua', so the gate's iOS "
                    + "simulator checks cannot run as it: this explicit local simulator execution needs a GUI session. "
                    + "Production workers are headless and must not borrow the owner's session; use the sealed remote iOS executor.");
            }
            foreach (var (what, args) in new[] { ("runtimes", new[] { "simctl", "list", "runtimes", "-j" }), ("devicetypes", new[] { "simctl", "list", "devicetypes", "-j" }) })
            {
                var listed = await run(XcrunPath, args);
                if (SimulatorProblem(what, listed.ExitCode, listed.Stdout, listed.Stderr) is { } problem)
                {
                    throw new InvalidOperationException(
                        $"Swift toolchain (Worker:Swift) of {user}: `{XcrunPath} {string.Join(' ', args)}` as {user} {problem}, so the gate's iOS simulator "
                        + $"checks cannot run as it: install an iOS simulator runtime (`sudo xcodebuild -downloadPlatform iOS`), then re-run `{reinstall} --swift` to check it.");
                }
            }
        }
        var wrapper = await run(WrapperPath, []);
        if (wrapper.ExitCode != 64 || !wrapper.Stderr.Contains("factory-xcodebuild: usage", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Swift toolchain (Worker:Swift) of {user}: the xcodebuild wrapper {WrapperPath} does not run as {user} "
                + $"(exit {wrapper.ExitCode}: {Clip(wrapper.Stderr)}); re-run `{reinstall} --swift`.");
        }

        async Task<string> Expect(string program, string[] args, string fix)
        {
            var result = await run(program, args);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Swift toolchain (Worker:Swift) of {user}: `{program} {string.Join(' ', args)}` as {user} exited {result.ExitCode} "
                    + $"({Clip(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)}): {fix}, then re-run `{reinstall} --swift` to check it.");
            }
            return result.Stdout;
        }
    }

    /// <summary>
    /// The SDKs every Swift factory needs locally, each as <c>xcodebuild -showsdks</c>
    /// names it after <c>-sdk</c> (a version follows), and how to install it. The same list as <c>setup-worker-user.sh</c>'s.
    /// </summary>
    public static readonly IReadOnlyList<(string Platform, string Sdk, string Fix)> RequiredSdks =
    [
        ("macOS", "macosx", "Xcode (its macOS SDK ships with it)"),
    ];

    /// <summary>Additional SDKs required only for explicitly local iOS simulator execution, never for remote dispatch.</summary>
    public static readonly IReadOnlyList<(string Platform, string Sdk, string Fix)> LocalSimulatorSdks =
    [
        ("iOS Simulator", "iphonesimulator", "the iOS platform (`sudo xcodebuild -downloadPlatform iOS`)"),
    ];

    /// <summary>
    /// Why an <c>xcrun simctl list runtimes|devicetypes -j</c> answer (<paramref name="what"/>) shows no simulator the gate can
    /// make, or null: runtimes must include one whose <c>platform</c> is <c>iOS</c> and <c>isAvailable</c> is true; device types
    /// one whose <c>productFamily</c> is <c>iPhone</c>. A failed command or JSON that is not that shape is a problem too.
    /// </summary>
    public static string? SimulatorProblem(string what, int exitCode, string stdout, string stderr)
    {
        if (exitCode != 0)
        {
            return $"exited {exitCode} ({Clip(stderr.Length > 0 ? stderr : stdout)})";
        }
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !json.RootElement.TryGetProperty(what, out var list)
                || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return $"answers no {what} list";
            }
            static bool Is(System.Text.Json.JsonElement e, string name, string value) =>
                e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() == value;
            return what == "runtimes"
                ? list.EnumerateArray().Any(r => Is(r, "platform", "iOS") && r.TryGetProperty("isAvailable", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True)
                    ? null : "lists no available iOS simulator runtime"
                : list.EnumerateArray().Any(d => Is(d, "productFamily", "iPhone")) ? null : "lists no iPhone simulator device type";
        }
        catch (System.Text.Json.JsonException)
        {
            return $"answers no JSON {what} list";
        }
    }

    private static string Clip(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
    }
}
