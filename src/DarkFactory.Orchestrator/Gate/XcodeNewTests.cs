using System.Text.Json;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Swift Testing tests in an Xcode project's macOS unit-test target (Phase 5, sc-26131), beside <see cref="SwiftNewTests"/>
/// (SwiftPM). Only in a factory with <c>Worker:Swift</c> on (<see cref="NewTestsCheck.StrategiesFor"/>, E7): with it off,
/// <see cref="SwiftNewTests"/> names an Xcode project's test file unsupported and says why.
/// <para>
/// Which files: a changed <c>.swift</c> file that <see cref="SwiftOwnership"/> gives it (sc-26134) — one in no SwiftPM
/// package, or one of a package outside its test targets that an Xcode project's test target compiles (an Xcode project
/// beside a root <c>Package.swift</c>); a package's other files are <see cref="SwiftNewTests"/>'s, and a file in a package's
/// test target that an Xcode test target also compiles is neither's (<see cref="SwiftNewTests"/> names it unsupported). Which target compiles it is read from every <c>*.xcodeproj/project.pbxproj</c> of the
/// commit (<see cref="XcodeProject.Membership"/>: classic file references and synchronized folders, anything unread failing
/// closed). New tests: the <c>@Test</c> functions (<see cref="SwiftSource"/>) of the head versions of the files a unit-test
/// target compiles at the head whose id no base version of a changed file in that target has. The id is what xcodebuild's
/// <c>-only-testing:</c> takes and its result bundle names: <c>&lt;target&gt;/&lt;function&gt;()</c>, or
/// <c>&lt;target&gt;/&lt;Type&gt;/&lt;Nested&gt;/&lt;function&gt;()</c> in a type. XCTest's tests (sc-26129) are found over
/// every file the target compiles, at the head and the base (<see cref="SwiftXCTest.Discover"/>: a subclass runs what it
/// inherits), named <c>&lt;target&gt;/&lt;Class&gt;/testMethod()</c> as the result bundle names them; a parameterized test
/// is one test, its result bundle's test case and each of its <c>Arguments</c> cases read as its cases.
/// </para>
/// <para>
/// Unisolable (<c>unsupported</c>, never a pass and never <c>no-tests</c>): a changed file that adds tests, or looks like a
/// test file, but that no unit-test or UI-test target compiles (not a member of its test target, or only of another target); a
/// file the base run would apply that another target also compiles, at the head or at the base (its head production code
/// would reach the base run); a file whose membership cannot be read, or a project that cannot be read; an existing
/// parameterized test whose arguments change, and a test-looking XCTest method the gate cannot run apart (sc-26129, as for
/// SwiftPM: <see cref="SwiftXCTest"/>); a backticked name or an id with characters the xcodebuild wrapper refuses; an id declared
/// twice; a target the gate cannot test (<see cref="XcodeProject.TestPlatform"/>: macOS or an iOS Simulator only, and no macOS
/// UI tests); a target tested on another platform at the base; a test
/// bundle not named for its target (<see cref="XcodeProject.ProductNameProblem"/>); no shared scheme
/// (<c>xcshareddata/xcschemes</c>) whose test action — or its default test plan (<see cref="XcodeTestPlan"/>, read whole or
/// not at all) — runs the target, at the head or at the base; a new test that plan skips or does not select; a project named by more than one workspace; a base that lacks the target; and a base project whose
/// membership of the new test's file cannot be read or whose project file cannot be given it (below).
/// </para>
/// <para>
/// The base run's overlay: every changed file that a unit-test target compiles, at the head or the base; the schemes and
/// production code stay the base's, and so does the project file, with one exception. A new file in a synchronized folder
/// is compiled by the base project as it is; a new test file under classic file references is compiled only because the
/// PR's <c>project.pbxproj</c> adds it (a new helper file beside new tests likewise), and a test file the PR removes is
/// still named by the base's — so the base run's project file is the base's with exactly those test files added to (or taken out of) the test target's Sources
/// (<see cref="XcodeProject.WithClassicSources"/>, checked to change nothing else), never the PR's, which would bring its
/// production changes along.
/// </para>
/// <para>
/// Runs (<see cref="SandboxTestRunner"/>, as the sandbox user: never xcodebuild itself, always the installed wrapper
/// <see cref="SwiftToolchain.DefaultWrapperPath"/>, which forces ad hoc signing and a DerivedData of its own per worktree):
/// <c>/usr/bin/xcodebuild -version</c> and <c>/usr/bin/xcrun --find xcresulttool</c> (the toolchain), the wrapper's
/// <see cref="CleanDerivedData"/> (the worktree's path, so its DerivedData, is the same for every run of the item: nothing
/// a worker session of the same user left there is built on), then per project and scheme <c>factory-xcodebuild --gate
/// build-for-testing -project|-workspace … -scheme … -destination platform=macOS -quiet</c>
/// (the build; its standard output is the build log: a base that does not build with the PR's test files is explained from
/// swiftc's errors in it as for SwiftPM, <see cref="ExplainBuildFailure"/>, and retried up to <see cref="BaseRetries"/> times,
/// sc-26134), then per test <c>factory-xcodebuild --gate test-without-building … -only-testing:&lt;id&gt; -resultBundlePath
/// &lt;results&gt;/</c><see cref="BundleName"/> (its exit code kept) and <c>/usr/bin/xcrun xcresulttool get test-results
/// tests --path</c> that bundle (its JSON kept): the result bundle is read in the sandbox, by the user whose tests wrote it,
/// and only its JSON reaches the owner, bounded like every captured output and parsed as untrusted data
/// (<see cref="ParseResults"/>); and last <see cref="CleanDerivedData"/> again, a <see cref="TestPhase.Cleanup"/> step that
/// runs however the run ended. <see cref="GateMode"/> puts the SwiftPM checkouts inside that DerivedData, never the user's
/// shared ones every worker session can edit.
/// </para>
/// <para>
/// iOS (sc-26132): an iOS unit-test or UI-test target (<see cref="XcodeProject.TestPlatform"/>) runs on the run's own iOS
/// Simulator. Since every exit of the launch helper kills every process of its user — CoreSimulatorService and a booted
/// simulator included — everything that needs the simulator booted is ONE step, one helper run. The steps, all
/// <c>/bin/bash -c</c> <see cref="SimulatorScript"/> (the gate's own code, compiled in) but the build: <c>prepare</c> (a
/// <see cref="TestPhase.Restore"/> step: refused outside a GUI login session, naming why, and without a runtime — exit
/// <see cref="SimulatorUnavailableExit"/>, the factory's failure, not the item's; earlier runs' devices deleted once their runs
/// are over; the run's device <see cref="DeviceName"/> created FRESH from the runtime, never cloned or reused, and checked to be
/// in the user's own device set), the build for that device by name, <c>session</c> (a <see cref="TestPhase.Test"/> step: the
/// fresh device's first boot, bounded by <see cref="BootTimeoutSeconds"/>, then per test the wrapper's
/// <c>test-without-building</c> and xcresulttool into <c>xcode-test-&lt;hash&gt;.json</c>, its exit codes on its output
/// <see cref="SessionOutput"/>), and <c>remove</c> (a <see cref="TestPhase.Cleanup"/> step: the run's device deleted). The
/// build is a step of its own, outside the simulator script, so its standard output is the build log as on macOS. A base that
/// does not build is retried as a run of its own (sc-26134): a fresh results directory, so a fresh device, created by its
/// <c>prepare</c> and deleted by its <c>remove</c> — never a device of the run before — and each run bounded by its own
/// <c>Gate:TestTimeoutMinutes</c> like every run of the check. A run whose build fails stops before <c>session</c>, so it
/// never boots its device: of a base and its retries only the one that builds pays for a first boot.
/// </para>
/// </summary>
public sealed class XcodeNewTests(string wrapperPath = SwiftToolchain.DefaultWrapperPath) : INewTestStrategy
{
    public string Name => "xcode-swift-testing";

    /// <summary>The installed xcodebuild wrapper every build and test step runs.</summary>
    public string WrapperPath => wrapperPath;

    public const string Xcodebuild = "/usr/bin/xcodebuild";

    public const string Xcrun = "/usr/bin/xcrun";

    /// <summary>
    /// This Mac: a macOS test target's destination. An iOS one's (sc-26132) is the run's own simulator,
    /// <c>platform=iOS Simulator,name=</c><see cref="DeviceName"/> to build and <c>…,id=&lt;udid&gt;</c> to test; a device never (E3).
    /// </summary>
    public const string Destination = "platform=macOS";

    /// <summary>The wrapper's action that deletes the working directory's DerivedData (and the gate's package checkouts in it).</summary>
    public const string CleanDerivedData = "clean-derived-data";

    /// <summary>The wrapper's first argument for the gate's builds and tests: package checkouts inside the run's own DerivedData.</summary>
    public const string GateMode = "--gate";

    private const string Prefix = "xcode-test-";

    private static string Hash(string id) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];

    /// <summary>The result bundle a test's step writes, under the results directory: <c>xcode-test-&lt;16 hex of the id's SHA-256&gt;.xcresult</c>.</summary>
    public static string BundleName(string id) => $"{Prefix}{Hash(id)}.xcresult";

    /// <summary>The name the test step's output and exit code are kept under.</summary>
    public static string OutputName(string id) => $"{Prefix}{Hash(id)}.out";

    /// <summary>The name xcresulttool's JSON of that bundle is kept under.</summary>
    public static string JsonName(string id) => $"{Prefix}{Hash(id)}.json";

    // ---- iOS Simulator (sc-26132) ----------------------------------------------------------------------------------------

    /// <summary>The shell the simulator steps run under: <c>/bin/bash -c</c> <see cref="SimulatorScript"/>.</summary>
    public const string Bash = "/bin/bash";

    /// <summary>
    /// How long the run's fresh simulator may take to boot: its first boot migrates its data, 2–4.5 minutes as a sandbox user
    /// (sc-26126: 123, 137, 178 and 272 s seen), so about 2.6 times the slowest seen. Each run (the base's, a retry's, the head's) boots its
    /// own device within its own <c>Gate:TestTimeoutMinutes</c> (default 20 minutes), which leaves 8 minutes beside it for the
    /// build and the tests.
    /// </summary>
    public const int BootTimeoutSeconds = 720;

    /// <summary>
    /// The exit of <c>prepare</c> when this machine cannot run a simulator for the run's user at all (outside a GUI login
    /// session, no CoreSimulator service, no iOS runtime or iPhone type): the factory's failure, never the item's
    /// (<see cref="TestStep.FactoryWideExit"/>).
    /// </summary>
    public const int SimulatorUnavailableExit = 69;

    /// <summary>The name the iOS simulator session's output and exit code are kept under.</summary>
    public const string SessionOutput = "xcode-ios-session.out";

    /// <summary>
    /// <c>scripts/factory-ios-simulator</c> as the repository has it, compiled in: the gate's own code (never installed, never a
    /// worker's), run as <c>/bin/bash -c &lt;text&gt; factory-ios-simulator &lt;command&gt; …</c> by the run's user.
    /// </summary>
    public static string SimulatorScript { get; } = ReadScript();

    private static string ReadScript()
    {
        using var stream = typeof(XcodeNewTests).Assembly.GetManifestResourceStream("factory-ios-simulator")
            ?? throw new InvalidOperationException("The factory-ios-simulator resource is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The run's own simulator: <c>dark-factory-gate-&lt;16 hex of the SHA-256 of the run's random results directory&gt;</c>.</summary>
    public static string DeviceName(string resultsDirectory) => $"dark-factory-gate-{Hash(resultsDirectory)}";

    private static TestStep Simulator(TestPhase phase, IReadOnlyList<string> args) => new(phase, Bash, ["-c", SimulatorScript, "factory-ios-simulator", .. args]);

    private static readonly Regex SessionResult = new(@"^factory-ios-simulator: result (?<hash>[0-9a-f]{16}) test-exit (?<test>\d{1,3}) xcresulttool-exit (?<json>\d{1,3})$", RegexOptions.CultureInvariant);

    private static readonly Regex KeptName = new(@"^xcode-test-(?<hash>[0-9a-f]{16})\.(?<kind>out|json)$", RegexOptions.CultureInvariant);

    // The wrapper's own patterns (scripts/factory-xcodebuild): what it refuses, the gate cannot ask of it.
    private static readonly Regex WrapperContainer = new(@"^[A-Za-z0-9_.][A-Za-z0-9 ._/+-]{0,255}$", RegexOptions.CultureInvariant);
    private static readonly Regex WrapperScheme = new(@"^[A-Za-z0-9_][A-Za-z0-9 ._+-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex WrapperTestId = new(@"^[A-Za-z0-9_][A-Za-z0-9_./():-]{0,255}$", RegexOptions.CultureInvariant);

    public bool Understands(string path) => path.EndsWith(".swift", StringComparison.Ordinal);

    /// <summary>Nothing: the build's output is kept (<see cref="TestStep.CaptureOutput"/>), no log file is read.</summary>
    public string BuildLogPattern => "xcode-build-*.none";

    internal static bool IsManifest(string path) =>
        Regex.IsMatch(path[(path.LastIndexOf('/') + 1)..], @"^Package(@swift-[^/]*)?\.swift$", RegexOptions.CultureInvariant);

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    /// <summary>The way a test's project is built: the container flag and path, and the scheme (<see cref="NewTestSet.Projects"/>'s value).</summary>
    internal sealed record Build(string Flag, string Container, string Scheme, string Platform = XcodeProject.MacOS)
    {
        public override string ToString() => Platform == XcodeProject.MacOS ? $"{Flag}\n{Container}\n{Scheme}" : $"{Flag}\n{Container}\n{Scheme}\n{Platform}";

        public static Build From(string value) => value.Split('\n') switch
        {
            [var flag, var container, var scheme] => new Build(flag, container, scheme),
            [var flag, var container, var scheme, XcodeProject.IOS] => new Build(flag, container, scheme, XcodeProject.IOS),
            _ => throw new InvalidOperationException($"'{value}' is not an Xcode build of a new test."),
        };
    }

    /// <summary>How a target is built and tested: its build, its scheme's test configuration and, when the scheme runs a test plan, the plan's path and its entry for the target; or why it cannot be.</summary>
    private sealed record BuildOf(Build? Build, string? Configuration, string? Problem, (string Path, XcodePlanTarget Target)? Plan = null);

    /// <summary>One commit's files, projects, schemes and workspaces, each read once.</summary>
    private sealed class Tree(TestSource source, string sha)
    {
        private HashSet<string>? _files;
        private readonly Dictionary<string, string?> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SwiftFile?> _parsed = new(StringComparer.Ordinal);
        private List<(string Path, XcodeProject? Project, string? Problem)>? _projects;

        public Dictionary<string, string> Unreadable { get; } = new(StringComparer.Ordinal);

        public async Task<HashSet<string>> FilesAsync(CancellationToken ct) => _files ??= (await source.FilesAsync(sha, ct)).ToHashSet(StringComparer.Ordinal);

        public async Task<string?> ReadAsync(string path, CancellationToken ct)
        {
            if (!_texts.TryGetValue(path, out var text))
            {
                _texts[path] = text = (await FilesAsync(ct)).Contains(path) ? await source.ReadAsync(sha, path, ct) : null;
            }
            return text;
        }

        public async Task<SwiftFile?> ParseAsync(string path, CancellationToken ct)
        {
            if (!_parsed.TryGetValue(path, out var file))
            {
                file = null;
                if (await ReadAsync(path, ct) is { } text)
                {
                    try
                    {
                        file = SwiftSource.Parse(text);
                    }
                    catch (FormatException e)
                    {
                        Unreadable[path] = e.Message;
                    }
                }
                _parsed[path] = file;
            }
            return file;
        }

        private SwiftOwnership? _owners;

        /// <summary>
        /// Whether <paramref name="path"/> is not this strategy's to judge (<see cref="SwiftOwnership"/>, sc-26134): a SwiftPM
        /// package's file that no Xcode test target compiles, or one in a package's test target that one also compiles
        /// (<see cref="SwiftNewTests"/> names that unsupported).
        /// </summary>
        public async Task<bool> InPackageAsync(string path, CancellationToken ct) =>
            (await (_owners ??= new SwiftOwnership(async c => await FilesAsync(c), ReadAsync)).OfAsync(path, ct)).Owner != SwiftOwner.Xcode;

        /// <summary>
        /// Every <c>.swift</c> file <paramref name="target"/> compiles, parsed (sc-26129); and those whose membership cannot be
        /// told or that cannot be read.
        /// </summary>
        public async Task<(List<(string Path, SwiftFile File)> Files, List<string> Unknown)> ModuleAsync(XcodeProject project, XcodeTarget target, CancellationToken ct)
        {
            var (files, unknown) = (new List<(string, SwiftFile)>(), new List<string>());
            var all = await FilesAsync(ct);
            foreach (var path in all.Where(f => f.EndsWith(".swift", StringComparison.Ordinal) && !IsManifest(f)).Order(StringComparer.Ordinal))
            {
                if (await InPackageAsync(path, ct))
                {
                    continue;
                }
                XcodeMembership membership;
                try
                {
                    membership = project.Membership(target, path);
                }
                catch (FormatException e)
                {
                    membership = XcodeMembership.NotKnown(e.Message);
                }
                if (membership.Unknown is not null)
                {
                    unknown.Add(path);
                }
                else if (membership.Member)
                {
                    if (await ParseAsync(path, ct) is { } file)
                    {
                        files.Add((path, file));
                    }
                    else
                    {
                        unknown.Add(path);
                    }
                }
            }
            return (files, unknown);
        }

        /// <summary>Every Xcode project of the commit (not one inside another bundle), read; or why it cannot be.</summary>
        public async Task<List<(string Path, XcodeProject? Project, string? Problem)>> ProjectsAsync(CancellationToken ct)
        {
            if (_projects is null)
            {
                _projects = [];
                foreach (var pbxproj in (await FilesAsync(ct)).Where(f => f.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
                {
                    var path = pbxproj[..^"/project.pbxproj".Length];
                    try
                    {
                        _projects.Add((path, XcodeProject.Parse(path, await ReadAsync(pbxproj, ct) ?? throw new FormatException("it cannot be read")), null));
                    }
                    catch (FormatException e)
                    {
                        _projects.Add((path, null, $"{pbxproj} cannot be read ({e.Message})"));
                    }
                }
            }
            return _projects;
        }

        /// <summary>The workspaces (not a project's own <c>project.xcworkspace</c>) that name <paramref name="project"/>; or why one cannot be read.</summary>
        public async Task<(List<string> Workspaces, string? Problem)> WorkspacesOfAsync(string project, CancellationToken ct)
        {
            var found = new List<string>();
            foreach (var contents in (await FilesAsync(ct)).Where(f => f.EndsWith(".xcworkspace/contents.xcworkspacedata", StringComparison.Ordinal)
                && !f.Contains(".xcodeproj/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var workspace = contents[..^"/contents.xcworkspacedata".Length];
                try
                {
                    if (XcodeWorkspace.Projects(await ReadAsync(contents, ct) ?? "", Parent(workspace)).Contains(project))
                    {
                        found.Add(workspace);
                    }
                }
                catch (FormatException e)
                {
                    return ([], $"{contents} cannot be read ({e.Message})");
                }
            }
            return (found, null);
        }

        /// <summary>
        /// The build of <paramref name="target"/> of <paramref name="project"/>: the first shared scheme (by name; the
        /// workspace's when a workspace names the project, then the project's) whose test action runs it; or why there is none.
        /// </summary>
        public async Task<BuildOf> BuildOfAsync(XcodeProject project, XcodeTarget target, CancellationToken ct)
        {
            var (workspaces, problem) = await WorkspacesOfAsync(project.Path, ct);
            if (problem is not null)
            {
                return new BuildOf(null, null, problem);
            }
            if (workspaces.Count > 1)
            {
                return new BuildOf(null, null, $"{project.Path} is in more than one workspace ({string.Join(", ", workspaces)}), so which one builds it cannot be told");
            }
            var (flag, container) = workspaces.Count == 1 ? ("-workspace", workspaces[0]) : ("-project", project.Path);
            var directories = workspaces.Select(w => (Dir: w, Base: Parent(w))).Append((Dir: project.Path, Base: project.Directory));
            var files = await FilesAsync(ct);
            var problems = new List<string>();
            foreach (var (dir, baseDirectory) in directories)
            {
                var prefix = $"{dir}/xcshareddata/xcschemes/";
                foreach (var file in files.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.EndsWith(".xcscheme", StringComparison.Ordinal)
                    && !f[prefix.Length..].Contains('/')).Order(StringComparer.Ordinal))
                {
                    var name = file[prefix.Length..^".xcscheme".Length];
                    XcodeScheme scheme;
                    try
                    {
                        scheme = XcodeScheme.Parse(name, await ReadAsync(file, ct) ?? "");
                    }
                    catch (FormatException e)
                    {
                        problems.Add($"the scheme {file} cannot be read ({e.Message})");
                        continue;
                    }
                    if (scheme.TestPlan is { } reference)
                    {
                        // The scheme's default test plan: its targets (containers relative to the same directory as the scheme's).
                        var planPath = XcodeScheme.ContainerPath(reference, baseDirectory);
                        IReadOnlyList<XcodePlanTarget> planTargets;
                        try
                        {
                            planTargets = planPath is not null && planPath.EndsWith(".xctestplan", StringComparison.Ordinal)
                                ? XcodeTestPlan.Parse(await ReadAsync(planPath, ct) ?? throw new FormatException("it is not in the commit"))
                                : throw new FormatException($"{reference} names no test plan in the repository");
                        }
                        catch (FormatException e)
                        {
                            problems.Add($"the scheme {name}'s test plan {planPath ?? reference} cannot be read ({e.Message})");
                            continue;
                        }
                        if (planTargets.FirstOrDefault(t => t.Identifier == target.Id && XcodeScheme.ContainerPath(t.Container, baseDirectory) == project.Path) is { } planned)
                        {
                            return new BuildOf(new Build(flag, container, name), scheme.TestConfiguration, null, (planPath!, planned));
                        }
                        continue;
                    }
                    var testable = scheme.Testables.FirstOrDefault(t => t.BlueprintId == target.Id && XcodeScheme.ContainerPath(t.Container, baseDirectory) == project.Path);
                    if (testable is null)
                    {
                        if (scheme.Problem is not null)
                        {
                            problems.Add($"the scheme {name}: {scheme.Problem}");
                        }
                        continue;
                    }
                    if (scheme.Problem is not null || testable.Filtered)
                    {
                        problems.Add($"the scheme {name} {scheme.Problem ?? "selects or skips some of the target's tests"}");
                        continue;
                    }
                    return new BuildOf(new Build(flag, container, name), scheme.TestConfiguration, null);
                }
            }
            return new BuildOf(null, null, $"no shared scheme of {container} tests the target {target.Name}"
                + (problems.Count == 0 ? " (share one in Xcode: Manage Schemes → Shared, so it is in xcshareddata/xcschemes)" : $" the gate can use: {string.Join("; ", problems)}"));
        }
    }

    /// <summary>
    /// A test function's place in its target: its types and signature (the id without the target) — <see cref="SwiftNewTests.Key"/>,
    /// so a base build's errors are placed in it as for SwiftPM (sc-26134).
    /// </summary>
    private static string Key(SwiftFunc func) => SwiftNewTests.Key(func);

    /// <summary>A test's id as xcodebuild names it: <c>Target/Type/Nested/f()</c>.</summary>
    public static string Id(string target, IReadOnlyList<string> types, string signature) => string.Join("/", new[] { target }.Concat(types).Append(signature));

    // ---- Finding the new tests ---------------------------------------------------------------------------------------------

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        var (head, @base) = (new Tree(source, source.HeadSha), new Tree(source, source.BaseSha));
        bool Swift(string? p) => p is not null && Understands(p) && !IsManifest(p);
        var candidates = new List<ChangedFile>();
        foreach (var change in changes)
        {
            if ((Swift(change.NewPath) && !await head.InPackageAsync(change.NewPath!, ct)) || (Swift(change.OldPath) && !await @base.InPackageAsync(change.OldPath!, ct)))
            {
                candidates.Add(change);
            }
        }
        if (candidates.Count == 0)
        {
            return NewTestSet.None;
        }
        var problems = new List<string>();

        // Which unit-test targets compile a path, in a commit's projects; or why that cannot be told.
        async Task<(List<(XcodeProject Project, XcodeTarget Target)> Tests, List<string> Others, List<string> Unknown)> TargetsAsync(Tree tree, string path)
        {
            var (tests, others, unknown) = (new List<(XcodeProject, XcodeTarget)>(), new List<string>(), new List<string>());
            foreach (var (projectPath, project, problem) in await tree.ProjectsAsync(ct))
            {
                if (project is null)
                {
                    unknown.Add(problem!);
                    continue;
                }
                foreach (var target in project.Targets)
                {
                    XcodeMembership membership;
                    try
                    {
                        membership = project.Membership(target, path);
                    }
                    catch (FormatException e)
                    {
                        membership = XcodeMembership.NotKnown(e.Message);
                    }
                    if (membership.Unknown is { } why)
                    {
                        unknown.Add($"{projectPath}: {why}");
                    }
                    else if (membership.Member && target.IsTestBundle)
                    {
                        tests.Add((project, target));
                    }
                    else if (membership.Member)
                    {
                        others.Add($"{target.Name} of {projectPath}");
                    }
                }
            }
            return (tests, others, unknown);
        }

        // The base's Swift Testing tests of the changed files, by target; and the unit-test targets whose XCTest tests may have
        // changed (a changed file they compile declares a class or a test-looking method, at the head or the base).
        var baseTests = new Dictionary<string, SwiftFunc>(StringComparer.Ordinal);
        var xctestTargets = new SortedSet<string>(StringComparer.Ordinal);
        static bool MayChangeXCTests(SwiftFile file) => file.Types.Any(t => t.Keyword is "class" or "extension") || file.Funcs.Any(SwiftXCTest.IsCandidate);
        foreach (var old in candidates.Select(c => c.OldPath).OfType<string>().Where(p => Swift(p)))
        {
            if (await @base.ParseAsync(old, ct) is { } file)
            {
                foreach (var (project, target) in (await TargetsAsync(@base, old)).Tests)
                {
                    foreach (var test in file.Tests)
                    {
                        baseTests.TryAdd($"{target.Name}/{Key(test)}", test);
                    }
                    if (MayChangeXCTests(file))
                    {
                        xctestTargets.Add($"{project.Path}\n{target.Name}");
                    }
                }
            }
        }

        var news = new SortedDictionary<string, (XcodeProject Project, XcodeTarget Target, string File)>(StringComparer.Ordinal);
        // Each new test's file and place in it (its function, or for XCTest the method its class runs), where a base build's errors are placed.
        var locations = new Dictionary<string, (string Path, string Key)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new SortedSet<string>(StringComparer.Ordinal);
        var testFiles = new List<ChangedFile>();
        var removedClassic = new List<(string Project, string Target, string Path)>();
        var argumentsChanged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in candidates)
        {
            // A file the base compiled into a unit-test target that the PR deletes or renames: the base run removes it.
            var inBaseTests = false;
            if (Swift(change.OldPath) && !await @base.InPackageAsync(change.OldPath!, ct))
            {
                foreach (var (project, target) in (await TargetsAsync(@base, change.OldPath!)).Tests)
                {
                    inBaseTests = true;
                    if (change.OldPath != change.NewPath && project.Membership(target, change.OldPath!, classicOnly: true).Member)
                    {
                        removedClassic.Add((project.Path, target.Name, change.OldPath!));
                    }
                }
            }
            if (change.NewPath is not { } path || !Swift(path) || await head.InPackageAsync(path, ct))
            {
                if (inBaseTests)
                {
                    testFiles.Add(change);
                }
                continue;
            }
            var (tests, others, unknown) = await TargetsAsync(head, path);
            var file = await head.ParseAsync(path, ct);
            if (file is null)
            {
                var text = await head.ReadAsync(path, ct) ?? "";
                if (head.Unreadable.TryGetValue(path, out var why)
                    && (tests.Count > 0 || NewTestsCheck.LooksLikeTest(path) || text.Contains("@Test", StringComparison.Ordinal) || text.Contains("XCTest", StringComparison.Ordinal)))
                {
                    problems.Add($"{path} cannot be read as Swift source ({why}), so the tests it may add cannot be found");
                }
                if (tests.Count > 0 || inBaseTests)
                {
                    testFiles.Add(change);
                }
                continue;
            }
            var functions = file.Tests.Concat(file.XCTestMethods).ToList();
            if (unknown.Count > 0)
            {
                if (functions.Count > 0 || NewTestsCheck.LooksLikeTest(path))
                {
                    problems.Add($"which target compiles {path} cannot be told: {string.Join("; ", unknown.Distinct().Take(5))}");
                }
                continue;
            }
            if (tests.Count == 0)
            {
                var before = change.OldPath is { } old && await @base.ParseAsync(old, ct) is { } was
                    ? was.Tests.Concat(was.XCTestMethods).Select(Key).ToHashSet(StringComparer.Ordinal) : [];
                var added = functions.Where(t => !before.Contains(Key(t))).Select(Key).ToList();
                if (added.Count > 0 || NewTestsCheck.LooksLikeTest(path))
                {
                    var where = others.Count > 0 ? $"it is compiled only by {string.Join(", ", others)}, not by a unit-test or UI-test target"
                        : (await head.ProjectsAsync(ct)).Count == 0 ? "it is in no SwiftPM package and there is no Xcode project"
                        : "it is not a member of any test target (add it to its test target: put it in the target's synchronized folder, or add it to the target's Compile Sources)";
                    problems.Add(added.Count > 0 ? $"{path} adds test(s) {string.Join(", ", added)} the gate cannot run: {where}"
                        : $"{path} looks like a test file the gate cannot run: {where}");
                }
                continue;
            }
            testFiles.Add(change);
            foreach (var (project, target) in tests)
            {
                if (MayChangeXCTests(file))
                {
                    xctestTargets.Add($"{project.Path}\n{target.Name}");
                }
                foreach (var test in file.Tests)
                {
                    var id = $"{target.Name}/{Key(test)}";
                    if (!seen.Add(id))
                    {
                        twice.Add(id);
                    }
                    if (baseTests.TryGetValue(id, out var was))
                    {
                        // An existing test: a parameterized one whose arguments changed has new cases that cannot be run apart from its old ones.
                        if ((test.IsParameterized || was.IsParameterized) && test.TestArguments != was.TestArguments)
                        {
                            argumentsChanged.Add(id);
                            problems.Add($"{id} in {path} is an existing parameterized test whose arguments: the pull request changes: its new cases cannot be run apart from its old ones");
                        }
                        continue;
                    }
                    if (test.Name.Contains('`'))
                    {
                        problems.Add($"{id} in {path} has a backticked name, which the gate cannot name to xcodebuild's -only-testing");
                    }
                    else if (!WrapperTestId.IsMatch(id))
                    {
                        problems.Add($"{id} in {path} has characters the xcodebuild wrapper refuses in -only-testing");
                    }
                    else
                    {
                        news[id] = (project, target, path);
                        locations[id] = (path, Key(test));
                    }
                }
            }
        }

        // XCTest's tests of each unit-test target a changed file may change them in: every file the target compiles, read at
        // the head and the base, since a method a class inherits is a test of that class too (sc-26129).
        foreach (var key in xctestTargets)
        {
            var (projectPath, targetName) = (key[..key.IndexOf('\n')], key[(key.IndexOf('\n') + 1)..]);
            if ((await head.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == projectPath).Project is not { } project
                || project.Targets.FirstOrDefault(t => t.Name == targetName && t.IsTestBundle) is not { } target)
            {
                continue; // the target is gone at the head: no test of it runs
            }
            var (headFiles, headUnknown) = await head.ModuleAsync(project, target, ct);
            var baseProject = (await @base.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == projectPath).Project;
            var baseTarget = baseProject?.Targets.FirstOrDefault(t => t.Name == targetName && t.IsTestBundle);
            var baseFiles = baseProject is not null && baseTarget is not null ? (await @base.ModuleAsync(baseProject, baseTarget, ct)).Files : [];
            var (now, before) = (SwiftXCTest.Discover(headFiles), SwiftXCTest.Discover(baseFiles));
            // A head problem is new unless its place was a test or a problem at the base: one that was only a test-looking
            // method of a class that was no test class there is judged now.
            var known = before.Tests.Select(t => t.Key).Concat(before.Problems.Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
            var baseIdsOfTarget = before.Tests.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var moved = SwiftXCTest.MovedUnchanged(before, now);
            var added = now.Tests.Where(t => !baseIdsOfTarget.Contains(t.Key) && !moved.Contains(t.Key)).ToList();
            var unjudged = now.Problems.Where(p => !known.Contains(p.Key)).ToList();
            problems.AddRange(unjudged.Select(p => $"{targetName}/{p.Key} in {p.Path} is a new XCTest test method the gate cannot run apart: {p.Why}"));
            if ((added.Count > 0 || unjudged.Count > 0) && headUnknown.Count > 0)
            {
                problems.Add($"which files the test target {targetName} compiles, or what they hold, cannot be told ({string.Join(", ", headUnknown.Take(5))}), so which XCTest classes inherit its new tests cannot be told");
            }
            foreach (var test in added)
            {
                var id = $"{targetName}/{test.Key}";
                if (!seen.Add(id))
                {
                    twice.Add(id);
                }
                if (!WrapperTestId.IsMatch(id))
                {
                    problems.Add($"{id} in {test.Path} has characters the xcodebuild wrapper refuses in -only-testing");
                    continue;
                }
                news[id] = (project, target, test.Path);
                locations[id] = (test.Path, Key(test.Method));
            }
        }
        // An existing parameterized test whose arguments: are the same text may still get new cases: when they name a
        // declaration of its unit-test target that the pull request changes (a fixture's new case, say). Failing closed (sc-26129).
        var targetChanges = new SortedDictionary<string, (List<string> Head, List<string> Base)>(StringComparer.Ordinal);
        foreach (var change in candidates)
        {
            foreach (var (tree, at) in new[] { (head, change.NewPath), (@base, change.OldPath) })
            {
                if (!Swift(at) || await tree.InPackageAsync(at!, ct))
                {
                    continue;
                }
                foreach (var (project, target) in (await TargetsAsync(tree, at!)).Tests)
                {
                    var key = $"{project.Path}\n{target.Name}";
                    var entry = targetChanges.TryGetValue(key, out var known) ? known : targetChanges[key] = ([], []);
                    (ReferenceEquals(tree, head) ? entry.Head : entry.Base).Add(at!);
                }
            }
        }
        foreach (var (key, (headPaths, basePaths)) in targetChanges)
        {
            var (projectPath, targetName) = (key[..key.IndexOf('\n')], key[(key.IndexOf('\n') + 1)..]);
            if ((await head.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == projectPath).Project is not { } project
                || project.Targets.FirstOrDefault(t => t.Name == targetName && t.IsTestBundle) is not { } target)
            {
                continue; // the target is gone at the head: no test of it runs
            }
            var (baseTexts, headTexts) = (new List<string?>(), new List<string?>());
            foreach (var p in basePaths.Distinct())
            {
                baseTexts.Add(await @base.ReadAsync(p, ct));
            }
            foreach (var p in headPaths.Distinct())
            {
                headTexts.Add(await head.ReadAsync(p, ct));
            }
            var changed = SwiftSource.ChangedDeclarations(baseTexts, headTexts);
            if (changed is { Count: 0 })
            {
                continue;
            }
            foreach (var (path, file) in (await head.ModuleAsync(project, target, ct)).Files)
            {
                foreach (var test in file.Tests.Where(t => t.TestArguments is not null))
                {
                    var id = $"{targetName}/{Key(test)}";
                    var existing = !news.ContainsKey(id) && !argumentsChanged.Contains(id) && (!headPaths.Contains(path) || baseTests.ContainsKey(id));
                    if (existing && SwiftSource.ArgumentsReachChange(test, changed) is { } why)
                    {
                        problems.Add($"{id} in {path} is an existing parameterized test whose cases the pull request may change: {why}; its new cases cannot be run apart from its old ones");
                    }
                }
            }
        }
        problems.AddRange(twice.Select(id => $"{id} is declared more than once at the head (conditional compilation?), so which one runs cannot be told"));

        // A file applied to the base run must be test code alone: one any other target compiles — at the head, or at the base
        // where the overlay writes or removes it (an app file the PR moves into the test target, say) — would carry the head's
        // production code into the base run, or take the base's own out of it.
        foreach (var change in testFiles.Distinct())
        {
            var where = new List<(Tree Tree, string Path, string Commit)>();
            if (change.NewPath is { } now && Swift(now))
            {
                where.Add((head, now, "the head"));
                where.Add((@base, now, "the base"));
            }
            if (change.OldPath is { } old && Swift(old) && old != change.NewPath)
            {
                where.Add((@base, old, "the base"));
            }
            foreach (var (tree, path, commit) in where)
            {
                if (await tree.InPackageAsync(path, ct))
                {
                    continue;
                }
                var (_, others, unknown) = await TargetsAsync(tree, path);
                if (others.Count > 0)
                {
                    problems.Add($"{path} is compiled by both a test target and {string.Join(", ", others)} at {commit}: the gate cannot apply its test code alone");
                }
                else if (unknown.Count > 0)
                {
                    problems.Add($"which targets compile {path} at {commit} cannot be told: {string.Join("; ", unknown.Distinct().Take(5))}");
                }
            }
        }

        // Each target with new tests: its scheme and platform at the head and the base, and the base compiling the new files.
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var placements = new Dictionary<string, XcodeTestPlacement>(StringComparer.Ordinal);
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        var edits = new Dictionary<(string Project, string Target), (List<string> Add, List<string> Remove)>();
        foreach (var group in news.GroupBy(n => (n.Value.Project.Path, n.Value.Target.Name)))
        {
            var (project, target, _) = group.First().Value;
            var label = $"the test target {target.Name} of {project.Path}";
            var (build, configuration, problem, headPlan) = await head.BuildOfAsync(project, target, ct);
            if (build is null)
            {
                problems.Add($"{label} cannot be built and tested at the head: {problem}");
                continue;
            }
            var groupProblems = new List<string>();
            if (!WrapperContainer.IsMatch(build.Container) || build.Container.Split('/').Any(s => s is "." or ".."))
            {
                groupProblems.Add($"the path {build.Container} has characters the xcodebuild wrapper refuses");
            }
            if (!WrapperScheme.IsMatch(build.Scheme))
            {
                groupProblems.Add($"the scheme name {build.Scheme} has characters the xcodebuild wrapper refuses");
            }
            var (platform, platformWhy, platformNote) = project.TestPlatform(target, configuration!);
            if (platform is null)
            {
                groupProblems.Add($"{label} cannot be tested by the gate in its scheme's {configuration} configuration: {platformWhy}");
            }
            else
            {
                build = build with { Platform = platform };
                if (platformNote is not null)
                {
                    notes.Add(platformNote);
                }
            }
            if (project.ProductNameProblem(target, configuration!) is { } headName)
            {
                groupProblems.Add($"{label}: {headName}");
            }
            groupProblems.AddRange(PlanExcludes(headPlan, "the head", group.Select(n => n.Key), target.Name));
            var baseProject = (await @base.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == project.Path);
            var baseTarget = baseProject.Project?.Targets.FirstOrDefault(t => t.Name == target.Name && t.IsTestBundle);
            if (baseProject.Project is null || baseTarget is null)
            {
                groupProblems.Add(baseProject.Problem is { } why ? $"the base's {why}" : $"the base has no {label} (a new test target), so its new tests cannot be run on the base");
            }
            else
            {
                var (baseBuild, baseConfiguration, baseProblem, basePlan) = await @base.BuildOfAsync(baseProject.Project, baseTarget, ct);
                if (baseBuild is null || baseBuild with { Platform = build.Platform } != build)
                {
                    groupProblems.Add(baseBuild is null ? $"at the base {baseProblem}" : $"the base tests {label} with the scheme {baseBuild.Scheme} of {baseBuild.Container}, the head with {build.Scheme} of {build.Container}");
                }
                else if (platform is not null && baseProject.Project.TestPlatform(baseTarget, baseConfiguration!) is var (basePlatform, baseWhy, _) && basePlatform != platform)
                {
                    groupProblems.Add(basePlatform is null ? $"{label} cannot be tested by the gate at the base: {baseWhy}"
                        : $"{label} is tested on {platform} at the head but on {basePlatform} at the base, so the two runs would not run the same tests");
                }
                else if (baseProject.Project.ProductNameProblem(baseTarget, baseConfiguration!) is { } baseName)
                {
                    groupProblems.Add($"{label} at the base: {baseName}");
                }
                foreach (var test in group)
                    placements[test.Key] = XcodeTestPlacements.Agree(
                        baseConfiguration is null ? XcodeTestPlacement.Unknown : baseProject.Project.TestPlacement(baseTarget, baseConfiguration),
                        project.TestPlacement(target, configuration!));
                if (baseBuild is not null)
                {
                    groupProblems.AddRange(PlanExcludes(basePlan, "the base", group.Select(n => n.Key), target.Name));
                }
                // Every applied file the head's target compiles — a new helper beside the new tests too — must be compiled on the base.
                var applied = testFiles.Select(c => c.NewPath).OfType<string>().Where(p => Swift(p)).Distinct(StringComparer.Ordinal).ToList();
                var compiled = new List<string>();
                foreach (var path in applied)
                {
                    if (await head.InPackageAsync(path, ct))
                    {
                        continue;
                    }
                    XcodeMembership atHead;
                    try
                    {
                        atHead = project.Membership(target, path);
                    }
                    catch (FormatException e)
                    {
                        atHead = XcodeMembership.NotKnown(e.Message);
                    }
                    if (atHead.Unknown is { } headWhy)
                    {
                        groupProblems.Add($"whether the head's {project.Path} compiles {path} into {target.Name} cannot be told: {headWhy}");
                    }
                    else if (atHead.Member)
                    {
                        compiled.Add(path);
                    }
                }
                foreach (var file in compiled)
                {
                    var membership = baseProject.Project.Membership(baseTarget, file);
                    if (membership.Unknown is { } unknownWhy)
                    {
                        groupProblems.Add($"whether the base's {project.Path} compiles {file} into {target.Name} cannot be told: {unknownWhy}");
                    }
                    else if (!membership.Member)
                    {
                        // A new file the PR's project file adds (classic file references): the base run's project gets it added.
                        Edits(project.Path, target.Name).Add.Add(file);
                    }
                }
            }
            if (groupProblems.Count > 0)
            {
                problems.AddRange(groupProblems);
                continue;
            }
            foreach (var (id, _) in group)
            {
                projects[id] = build.ToString();
            }
        }

        // The base run's project files: the base's, with the classic Sources of its test targets changed as the PR changes
        // them for its test files — new files compiled, removed ones no longer — and nothing else of the PR's project file.
        foreach (var (projectPath, targetName, path) in removedClassic.Where(r => news.Values.Any(n => n.Project.Path == r.Project)))
        {
            Edits(projectPath, targetName).Remove.Add(path);
        }
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var byProject in edits.OrderBy(e => e.Key.Project, StringComparer.Ordinal).ThenBy(e => e.Key.Target, StringComparer.Ordinal).GroupBy(e => e.Key.Project))
        {
            var pbxproj = $"{byProject.Key}/project.pbxproj";
            var text = await @base.ReadAsync(pbxproj, ct);
            string? problem = text is null ? $"the base's {pbxproj} cannot be read" : null;
            foreach (var ((_, targetName), (add, remove)) in byProject)
            {
                if (problem is not null || text is null)
                {
                    break;
                }
                var project = XcodeProject.Parse(byProject.Key, text);
                (text, problem) = project.Targets.Where(t => t.Name == targetName && t.IsTestBundle).ToList() is [var target]
                    ? project.WithClassicSources(text, target, add, remove)
                    : (null, $"the base does not have exactly one unit-test target {targetName}");
                if (problem is not null)
                {
                    problem = $"the base's {byProject.Key} cannot be given the PR's test files for {targetName} ({string.Join(", ", add.Concat(remove))}; classic file references): {problem}";
                }
            }
            if (problem is not null || text is null)
            {
                problems.Add(problem!);
                continue;
            }
            replacements[pbxproj] = text;
            testFiles.Add(new ChangedFile('M', pbxproj, pbxproj));
        }

        // Each applied test file as the head has it (the base run applies it so): a retry's file, with what did not compile
        // left out, is compared with it to tell which types the overlay itself changed (SwiftNewTests.ExplainBuildFailure).
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in testFiles.Where(f => !f.Deleted).Select(f => f.NewPath!).Where(p => Swift(p)).Distinct(StringComparer.Ordinal))
        {
            if (await head.ReadAsync(path, ct) is { } text)
            {
                originals[path] = text;
            }
        }
        return new SwiftNewTestSet(testFiles.Distinct().ToList(), news.Keys.ToList(), problems)
        {
            Projects = projects,
            XcodePlacements = placements,
            BaseReplacements = replacements,
            Notes = [.. notes],
            Originals = originals,
            Locations = news.Keys.ToDictionary(id => id, id => locations[id], StringComparer.Ordinal),
        };

        (List<string> Add, List<string> Remove) Edits(string project, string target) =>
            edits.TryGetValue((project, target), out var e) ? e : edits[(project, target)] = ([], []);

        // The new tests a scheme's test plan would not run (xcodebuild's -only-testing runs nothing the plan skips or does not select).
        static IEnumerable<string> PlanExcludes((string Path, XcodePlanTarget Target)? plan, string commit, IEnumerable<string> ids, string targetName) =>
            plan is not { } p ? []
            : ids.Select(id => (id, why: p.Target.Excludes(id[(targetName.Length + 1)..])))
                .Where(x => x.why is not null)
                .Select(x => $"{x.id}: the test plan {p.Path} at {commit} {x.why}, so the gate cannot run it");
    }

    // ---- Running them ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>/usr/bin/xcodebuild -version</c> and <c>/usr/bin/xcrun --find xcresulttool</c> (the toolchain); per project and
    /// scheme the wrapper's <c>build-for-testing</c> (output kept as the build log); then per new test the wrapper's
    /// <c>test-without-building -only-testing:&lt;id&gt; -resultBundlePath</c> (exit code kept) and <c>xcrun xcresulttool get
    /// test-results tests</c> of its bundle (JSON kept). Every path is relative to the worktree root, where each step runs.
    /// </summary>
    public Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct)
    {
        Build BuildOf(string id) => projects.TryGetValue(id, out var b) ? Build.From(b) : throw new InvalidOperationException($"The new test {id} has no Xcode build.");
        var groups = tests.GroupBy(id => BuildOf(id).ToString(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var steps = new List<TestStep>
        {
            new(TestPhase.Toolchain, Xcodebuild, ["-version"]),
            new(TestPhase.Toolchain, Xcrun, ["--find", "xcresulttool"]),
            // The worktree's path is the same for every run of the item, so its DerivedData (keyed on it) is deleted first:
            // nothing a worker session of the same user left there is built on or run. Failing, the run is an error.
            new(TestPhase.Restore, wrapperPath, [CleanDerivedData]),
        };
        // iOS (sc-26132): the run's own simulator, created fresh for it (never cloned or reused), named from the results directory
        // (random per run) so the build can name it, and removed again however the run ends.
        var ios = groups.Where(g => Build.From(g.Key).Platform == XcodeProject.IOS).ToList();
        var device = DeviceName(resultsDirectory);
        var bootSeconds = BootTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ios.Count > 0)
        {
            steps.Add(Simulator(TestPhase.Restore, ["prepare", device, resultsDirectory]) with { FactoryWideExit = SimulatorUnavailableExit });
        }
        for (var n = 0; n < groups.Count; n++)
        {
            var b = Build.From(groups[n].Key);
            var destination = b.Platform == XcodeProject.IOS ? $"platform=iOS Simulator,name={device}" : Destination;
            steps.Add(new TestStep(TestPhase.Build, wrapperPath, [GateMode, "build-for-testing", b.Flag, b.Container, "-scheme", b.Scheme, "-destination", destination])
                { CaptureOutput = $"xcode-build-{n}", CaptureBuildStderr = true });
        }
        foreach (var group in groups.Where(g => Build.From(g.Key).Platform == XcodeProject.MacOS))
        {
            var b = Build.From(group.Key);
            foreach (var id in group)
            {
                var bundle = $"{resultsDirectory}/{BundleName(id)}";
                steps.Add(new TestStep(TestPhase.Test, wrapperPath,
                [
                    GateMode, "test-without-building", b.Flag, b.Container, "-scheme", b.Scheme, "-destination", Destination, $"-only-testing:{id}", "-resultBundlePath", bundle, "-quiet",
                ]) { CaptureOutput = OutputName(id) });
                steps.Add(new TestStep(TestPhase.Test, Xcrun, ["xcresulttool", "get", "test-results", "tests", "--path", bundle]) { CaptureOutput = JsonName(id) });
            }
        }
        if (ios.Count > 0)
        {
            // Everything that needs the booted simulator in ONE helper run (its exit kills the simulator): boot, each test
            // alone, xcresulttool of each bundle, shutdown. Its output says each test's exit codes; the JSONs are files.
            var records = ios.SelectMany(g => g.SelectMany(id => Build.From(g.Key) is var b ? new[] { b.Flag, b.Container, b.Scheme, id, Hash(id) } : []));
            steps.Add(Simulator(TestPhase.Test, ["session", device, resultsDirectory, wrapperPath, bootSeconds, .. records])
                with { CaptureOutput = SessionOutput, ResultFilePattern = $"{Prefix}*.json" });
        }
        // Last, however the run ended (SandboxTestRunner runs cleanup after a failed build or a timeout too): the run's device,
        // then the DerivedData again once the bundles are read.
        if (ios.Count > 0)
        {
            steps.Add(Simulator(TestPhase.Cleanup, ["remove", device]));
        }
        steps.Add(new TestStep(TestPhase.Cleanup, wrapperPath, [CleanDerivedData]));
        return Task.FromResult<IReadOnlyList<TestStep>>(steps);
    }

    /// <summary>
    /// Each test's cases from xcresulttool's JSON of its result bundle (Xcode 26's <c>get test-results tests</c>: a tree of
    /// <c>testNodes</c> — test plan, then the <c>Unit test bundle</c> named for the target, then <c>Test Suite</c>s, then
    /// <c>Test Case</c>s with a <c>nodeIdentifier</c> such as <c>Outer/Inner/deep()</c> and a <c>result</c> of
    /// <c>Passed</c>, <c>Failed</c>, <c>Skipped</c> or <c>Expected Failure</c> (a known issue: read as skipped, so neither a pass on the head nor a failure on the base)). A case's id is its bundle's name, a
    /// <c>/</c>, and its node identifier. The JSON is untrusted (the tests wrote the bundle): it is read only when
    /// xcresulttool exited 0, it is the JSON of exactly one test step (<see cref="JsonName"/>), every case in it is that
    /// step's test, every result is one of those, and the test step's exit code is known and agrees with it (0 exactly when
    /// no case failed); anything else is unreadable and named, never a result.
    /// </summary>
    public TestResults ParseResults(IEnumerable<ResultFile> resultFiles)
    {
        var results = new Dictionary<string, List<TestCaseResult>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var kept = new Dictionary<string, (ResultFile? Out, ResultFile? Json)>(StringComparer.Ordinal);
        var files = resultFiles.ToList();
        // The iOS simulator session (sc-26132): its lines give each test's two exit codes; the JSONs it wrote are files.
        var session = new Dictionary<string, (int Test, int Json)>(StringComparer.Ordinal);
        foreach (var output in files.Where(f => f.Name == SessionOutput && f.ExitCode is not null))
        {
            foreach (var line in output.Content.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (SessionResult.Match(line) is { Success: true } m && !session.TryAdd(m.Groups["hash"].Value, (int.Parse(m.Groups["test"].Value), int.Parse(m.Groups["json"].Value))))
                {
                    unreadable.Add($"{SessionOutput}: it reports xcode-test-{m.Groups["hash"].Value} twice");
                }
            }
            if (output.ExitCode != 0)
            {
                var why = output.Content.Split('\n').Where(l => l.StartsWith("factory-ios-simulator: ", StringComparison.Ordinal) && !SessionResult.IsMatch(l)).TakeLast(3).ToList();
                unreadable.Add($"{SessionOutput}: the iOS simulator session failed (exit {output.ExitCode}){(why.Count == 0 ? "" : ": " + string.Join(" ", why))}");
            }
        }
        foreach (var hash in session.Keys)
        {
            kept[hash] = (new ResultFile($"{Prefix}{hash}.out", "") { ExitCode = session[hash].Test }, null);
        }
        foreach (var file in files.Where(f => f.Name != SessionOutput))
        {
            var match = KeptName.Match(file.Name);
            var fromSession = match.Success && file.ExitCode is null && match.Groups["kind"].Value == "json" && session.ContainsKey(match.Groups["hash"].Value);
            if (!match.Success || (file.ExitCode is null && !fromSession))
            {
                unreadable.Add($"{file.Name}: it is not the output of a test step");
                continue;
            }
            var hash = match.Groups["hash"].Value;
            var current = fromSession ? file with { ExitCode = session[hash].Json } : file;
            var pair = kept.GetValueOrDefault(hash);
            if ((match.Groups["kind"].Value == "out" ? pair.Out : pair.Json) is not null)
            {
                unreadable.Add($"{file.Name}: a second report of the same test");
                continue;
            }
            kept[hash] = match.Groups["kind"].Value == "out" ? pair with { Out = current } : pair with { Json = current };
        }
        foreach (var (hash, (output, json)) in kept.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var name = $"{Prefix}{hash}.json";
            try
            {
                if (json is null)
                {
                    throw new InvalidDataException($"its test step (exit {output!.ExitCode}) left no result bundle read");
                }
                if (output is null)
                {
                    throw new InvalidDataException("its test step's exit code is not known");
                }
                if (json.ExitCode != 0)
                {
                    throw new InvalidDataException($"xcresulttool exited {json.ExitCode}: {InertCodeSpan(json.Content.Trim().Split('\n')[0], MaxHostMessageChars)}");
                }
                var cases = Cases(json.Content, out var hostFailures);
                if (hostFailures.Count > 0)
                {
                    // The test host never ran the test (LensWorks PR #27): no evidence either way, but say why.
                    throw new InvalidDataException($"the test host of its step's test failed before the test ran: {string.Join("; ", hostFailures)}");
                }
                if (cases.FirstOrDefault(c => Hash(c.Name) != hash) is { } stranger)
                {
                    throw new InvalidDataException($"it holds {InertCodeSpan(stranger.Name, MaxHostNameChars)}, which is not the test its step ran");
                }
                var failed = cases.Count(c => c.Outcome == TestCaseResult.Failed);
                if ((output.ExitCode == 0) != (failed == 0))
                {
                    throw new InvalidDataException($"xcodebuild exited {output.ExitCode}, which does not agree with its {failed} failing case(s)");
                }
                foreach (var result in cases)
                {
                    (results.TryGetValue(result.Name, out var list) ? list : results[result.Name] = []).Add(result);
                }
            }
            catch (InvalidDataException e)
            {
                unreadable.Add($"{name}: {e.Message}");
            }
        }
        return new TestResults(results.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TestCaseResult>)kv.Value, StringComparer.Ordinal), unreadable);
    }

    /// <summary>At most this many characters of each failure message of a test host's error are reported (<see cref="HostFailure"/>).</summary>
    public const int MaxHostMessageChars = 300;

    /// <summary>At most this many failure messages of one test host's error are reported.</summary>
    public const int MaxHostMessages = 3;

    /// <summary>At most this many host errors of one xcresult report are named.</summary>
    public const int MaxHostFailures = 3;

    /// <summary>At most this many characters of a host or test-case name from an xcresult report are named.</summary>
    public const int MaxHostNameChars = 200;

    /// <summary>
    /// The pseudo test case xcresulttool records when a test target's host app (its <c>TEST_HOST</c>) fails to launch or
    /// crashes: a <c>Test Case</c> whose node identifier is <c>&lt;Host&gt; encountered an error</c> — no <c>/</c> and no
    /// <c>(</c>, which every real test's identifier has — with <c>Failure Message</c> children saying why.
    /// </summary>
    private static bool IsHostError(string identifier) =>
        identifier.EndsWith(" encountered an error", StringComparison.Ordinal) && !identifier.Contains('/') && !identifier.Contains('(');

    /// <summary>
    /// A test host's error in words for the <c>new-tests</c> row and the reports: the node's name and its first
    /// <see cref="MaxHostMessages"/> failure messages, each made inert (<see cref="UntrustedText.Inert"/> in a code span: the
    /// tests wrote them) and cut at <see cref="MaxHostMessageChars"/> with a marker.
    /// </summary>
    private static string HostFailure(string name, IReadOnlyList<string> messages)
    {
        var said = messages.Count == 0
            ? "xcresult gives no failure message"
            : string.Join(", ", messages.Take(MaxHostMessages).Select(message => InertCodeSpan(message, MaxHostMessageChars)))
                + (messages.Count > MaxHostMessages ? $" and {messages.Count - MaxHostMessages} more" : "");
        return $"{InertCodeSpan(name, MaxHostNameChars)}: {said}";
    }

    /// <summary>Makes untrusted one-line result text inert, bounded, and safe to render in a Markdown code span.</summary>
    private static string InertCodeSpan(string text, int maxChars)
    {
        var line = UntrustedText.Inert(text);
        if (line.Length <= maxChars)
        {
            return $"`{line}`";
        }
        var keep = char.IsHighSurrogate(line[maxChars - 1]) && char.IsLowSurrogate(line[maxChars]) ? maxChars - 1 : maxChars;
        return $"`{line[..keep]}` [cut: {line.Length} characters, the first {keep} shown]";
    }

    /// <summary>The test cases of one xcresulttool <c>get test-results tests</c> JSON document; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    internal static List<TestCaseResult> Cases(string json) => Cases(json, out _);

    /// <summary>
    /// As <see cref="Cases(string)"/>, with each test host error node (<see cref="IsHostError"/>) set apart in words
    /// (<see cref="HostFailure"/>), never read as a case.
    /// </summary>
    internal static List<TestCaseResult> Cases(string json, out List<string> hostFailures)
    {
        var hosts = new List<string>();
        var hostFailureCount = 0;
        hostFailures = hosts;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"it is not JSON ({e.Message})");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("testNodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("it is not xcresulttool's test results (no testNodes)");
        }
        var cases = new List<TestCaseResult>();
        void Walk(JsonElement list, string? bundle)
        {
            foreach (var node in list.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("nodeType", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("a test node has no nodeType");
                }
                var type = typeElement.GetString();
                var children = node.TryGetProperty("children", out var c) ? c : default;
                if (children.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array))
                {
                    throw new InvalidDataException("a test node's children are not a list");
                }
                switch (type)
                {
                    case "Test Case":
                    {
                        if (bundle is null)
                        {
                            throw new InvalidDataException("a test case is outside every test bundle");
                        }
                        var identifier = node.TryGetProperty("nodeIdentifier", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        var result = node.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                        if (string.IsNullOrEmpty(identifier))
                        {
                            throw new InvalidDataException("a test case has no nodeIdentifier");
                        }
                        if (IsHostError(identifier))
                        {
                            var messages = children.ValueKind != JsonValueKind.Array ? [] : children.EnumerateArray()
                                .Where(m => m.ValueKind == JsonValueKind.Object
                                    && m.TryGetProperty("nodeType", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "Failure Message"
                                    && m.TryGetProperty("name", out var mn) && mn.ValueKind == JsonValueKind.String)
                                .Select(m => m.GetProperty("name").GetString()!)
                                .ToList();
                            hostFailureCount++;
                            if (hosts.Count < MaxHostFailures)
                            {
                                hosts.Add(HostFailure($"{bundle}/{identifier}", messages));
                            }
                            break;
                        }
                        string Outcome(string? value) => value switch
                        {
                            "Passed" => TestCaseResult.Passed,
                            "Failed" => TestCaseResult.Failed,
                            // A known issue (withKnownIssue) recorded: the test checked nothing that passed, so it is no pass on
                            // the head, and no failure on the base either.
                            "Skipped" or "Expected Failure" => TestCaseResult.Skipped,
                            _ => throw new InvalidDataException($"the test case {bundle}/{identifier} has the result '{value}', which the gate does not read"),
                        };
                        cases.Add(new TestCaseResult($"{bundle}/{identifier}", Outcome(result)));
                        // Its children are its messages and, for a parameterized test (sc-26129), one "Arguments" node per
                        // argument case: each is a case of the same test, so it passes only when the test and every case did.
                        if (children.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var argument in children.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object
                                && a.TryGetProperty("nodeType", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "Arguments"))
                            {
                                var value = argument.TryGetProperty("result", out var ar) && ar.ValueKind == JsonValueKind.String ? ar.GetString() : null;
                                cases.Add(new TestCaseResult($"{bundle}/{identifier}", Outcome(value)));
                            }
                        }
                        break;
                    }
                    case "Unit test bundle" or "UI test bundle":
                        if (bundle is not null)
                        {
                            throw new InvalidDataException("a test bundle is inside another");
                        }
                        if (!node.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(n.GetString()))
                        {
                            throw new InvalidDataException("a test bundle has no name");
                        }
                        if (children.ValueKind == JsonValueKind.Array)
                        {
                            Walk(children, n.GetString());
                        }
                        break;
                    default:
                        if (children.ValueKind == JsonValueKind.Array)
                        {
                            Walk(children, bundle);
                        }
                        break;
                }
            }
        }
        Walk(nodes, null);
        if (hostFailureCount > MaxHostFailures)
        {
            hostFailures = [.. hosts, $"and {hostFailureCount - MaxHostFailures} more"];
        }
        return cases;
    }

    private static readonly SwiftNewTests SwiftErrors = new();

    /// <summary>
    /// As for SwiftPM (sc-26134): swiftc reports in stages — a type or import that does not resolve hides the errors of the
    /// code using it, and an error in a declaration's signature (the module's interface, built first) hides the errors in
    /// function bodies — so a base that does not build is retried up to three times, each without what did not compile.
    /// </summary>
    public int BaseRetries => 3;

    /// <summary>
    /// A build command xcodebuild reports as failed in its own words, with no <c>error:</c> (Xcode 26.5 with <c>-quiet</c>):
    /// <c>Command PhaseScriptExecution failed with a nonzero exit code</c> — a run-script phase, which may be a test target's
    /// own. It names no file in the applied test code, so it is unattributable.
    /// </summary>
    private static readonly Regex FailedCommand = new(@"^Command [A-Za-z][A-Za-z0-9]* failed\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The errors in the build's bounded standard output and standard error (<c>factory-xcodebuild --gate build-for-testing …</c>, kept by
    /// <see cref="TestStep.CaptureOutput"/>, the same for macOS and an iOS Simulator destination), read as
    /// <see cref="SwiftNewTests.ParseBuildErrors"/> reads <c>swift build</c>'s: swiftc's <c>/abs/File.swift:3:5: error: …</c>
    /// (Xcode prints the path as it standardizes it, <c>/tmp/…</c> or <c>/var/…</c> for a worktree under <c>/private</c>: the
    /// file is resolved before it is made relative), and every other line that reports an error with no file — the linker's
    /// <c>…/App.xcodeproj: AppTests: clang: error: linker command failed …</c>, <c>error: Build input file cannot be found: …</c>,
    /// a signing error — plus xcodebuild's <see cref="FailedCommand"/> lines. What Xcode 26.5 prints and does not: the
    /// <c>Failed frontend command:</c> line and the compiler invocation after it (they say no more than the located errors),
    /// notes and warnings are not errors. Both streams are captured independently and parsed together, without an arrival-order
    /// assumption. Unlocated failures (including <c>Command Ld failed</c>) remain unattributable even beside located compiler
    /// errors: coexistence does not prove the linker failure secondary. Oversized or missing capture is never partial proof.
    /// </summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots) =>
        SwiftErrors.ParseBuildErrors(log, roots)
            .Concat(log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => FailedCommand.IsMatch(l)).Select(l => new BuildError(null, 0, 0, "xcodebuild", l)))
            .Distinct().ToList();

    /// <summary>
    /// A base that does not build with the PR's test files (sc-26134), by exactly the rules of
    /// <see cref="SwiftNewTests.ExplainBuildFailure"/> (the same code, over this strategy's <see cref="SwiftNewTestSet"/>):
    /// each swiftc error is placed by <see cref="SwiftSource.Layout"/> in the innermost declaration at its position, in the
    /// files the overlay applied only — the head's test files of the unit-test or UI-test target, as the base run built them
    /// (a classic-reference project's test file included: the base's project file is given it, <see cref="XcodeProject.WithClassicSources"/>).
    /// A new test whose own function (an XCTest test's: the method its class runs) or whose type's header is such a declaration
    /// is <c>not-built</c>; every other outermost declaration that did not compile is left out of the next retry
    /// (<see cref="BaseRetries"/>), so the other tests get their real results; an error between members, outside every
    /// declaration or on a conditional-compilation line, and one in the header of a type an earlier retry changed, is
    /// unremovable (those tests stay <c>unproven</c>). Unattributable, so the check is an <c>error</c>: an error in any file the
    /// overlay did not apply (the base's production code, an unchanged test file, a file Xcode generated, a macro expansion
    /// outside the worktree), the project file, packages, Info.plist, a script phase, the linker or signing, an error in no
    /// file, an ambiguity or redeclaration (a clash with what the base has), a build-tool plugin's error, and a build that
    /// names no error.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests) =>
        SwiftErrors.ExplainBuildFailure(set, applied, errors, tests);
}
