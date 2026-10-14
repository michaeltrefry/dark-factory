using System.Text.Json;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Swift Testing tests in an Xcode project's macOS unit-test target (Phase 5, sc-26131), beside <see cref="SwiftNewTests"/>
/// (SwiftPM). Only in a factory with <c>Worker:Swift</c> on (<see cref="NewTestsCheck.StrategiesFor"/>, E7): with it off,
/// <see cref="SwiftNewTests"/> names an Xcode project's test file unsupported and says why.
/// <para>
/// Which files: a changed <c>.swift</c> file in no SwiftPM package (no <c>Package.swift</c> above it, else it is
/// <see cref="SwiftNewTests"/>'s). Which target compiles it is read from every <c>*.xcodeproj/project.pbxproj</c> of the
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
/// test file, but that no unit-test target compiles (not a member of its test target, or of a UI-test or other target); a
/// file the base run would apply that another target also compiles, at the head or at the base (its head production code
/// would reach the base run); a file whose membership cannot be read, or a project that cannot be read; an existing
/// parameterized test whose arguments change, and a test-looking XCTest method the gate cannot run apart (sc-26129, as for
/// SwiftPM: <see cref="SwiftXCTest"/>); a backticked name or an id with characters the xcodebuild wrapper refuses; an id declared
/// twice; a target that builds for anything but macOS in its scheme's test configuration (iOS: a later story); a test
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
/// (the build; its output is the build log: a base that does not build is not attributed to the new tests yet, so it is an
/// <c>error</c>), then per test <c>factory-xcodebuild --gate test-without-building … -only-testing:&lt;id&gt; -resultBundlePath
/// &lt;results&gt;/</c><see cref="BundleName"/> (its exit code kept) and <c>/usr/bin/xcrun xcresulttool get test-results
/// tests --path</c> that bundle (its JSON kept): the result bundle is read in the sandbox, by the user whose tests wrote it,
/// and only its JSON reaches the owner, bounded like every captured output and parsed as untrusted data
/// (<see cref="ParseResults"/>); and last <see cref="CleanDerivedData"/> again, a <see cref="TestPhase.Cleanup"/> step that
/// runs however the run ended. <see cref="GateMode"/> puts the SwiftPM checkouts inside that DerivedData, never the user's
/// shared ones every worker session can edit.
/// </para>
/// </summary>
public sealed class XcodeNewTests(string wrapperPath = SwiftToolchain.DefaultWrapperPath) : INewTestStrategy
{
    public string Name => "xcode-swift-testing";

    /// <summary>The installed xcodebuild wrapper every build and test step runs.</summary>
    public string WrapperPath => wrapperPath;

    public const string Xcodebuild = "/usr/bin/xcodebuild";

    public const string Xcrun = "/usr/bin/xcrun";

    /// <summary>This Mac: the only destination this strategy builds and tests for (iOS simulators are a later story; a device never, E3).</summary>
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

    /// <summary>Whether a SwiftPM package holds <paramref name="path"/> in <paramref name="files"/> (a <c>Package.swift</c> in a directory above it).</summary>
    internal static bool InPackage(IReadOnlySet<string> files, string path)
    {
        for (var directory = Parent(path); ; directory = Parent(directory))
        {
            if (files.Contains(directory.Length == 0 ? "Package.swift" : $"{directory}/Package.swift"))
            {
                return true;
            }
            if (directory.Length == 0)
            {
                return false;
            }
        }
    }

    /// <summary>The way a test's project is built: the container flag and path, and the scheme (<see cref="NewTestSet.Projects"/>'s value).</summary>
    private sealed record Build(string Flag, string Container, string Scheme)
    {
        public override string ToString() => $"{Flag}\n{Container}\n{Scheme}";

        public static Build From(string value) => value.Split('\n') is [var flag, var container, var scheme]
            ? new Build(flag, container, scheme)
            : throw new InvalidOperationException($"'{value}' is not an Xcode build of a new test.");
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

        public async Task<bool> InPackageAsync(string path, CancellationToken ct) => XcodeNewTests.InPackage(await FilesAsync(ct), path);

        /// <summary>
        /// Every <c>.swift</c> file <paramref name="target"/> compiles, parsed (sc-26129); and those whose membership cannot be
        /// told or that cannot be read.
        /// </summary>
        public async Task<(List<(string Path, SwiftFile File)> Files, List<string> Unknown)> ModuleAsync(XcodeProject project, XcodeTarget target, CancellationToken ct)
        {
            var (files, unknown) = (new List<(string, SwiftFile)>(), new List<string>());
            var all = await FilesAsync(ct);
            foreach (var path in all.Where(f => f.EndsWith(".swift", StringComparison.Ordinal) && !IsManifest(f) && !XcodeNewTests.InPackage(all, f)).Order(StringComparer.Ordinal))
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

    /// <summary>A test function's place in its target: its types and signature (the id without the target).</summary>
    private static string Key(SwiftFunc func) => string.Join("/", func.Types.Append(func.Signature));

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
                    else if (membership.Member && target.IsUnitTests)
                    {
                        tests.Add((project, target));
                    }
                    else if (membership.Member)
                    {
                        others.Add($"{target.Name} of {projectPath}{(target.ProductType == XcodeTarget.UITestBundle ? " (a UI-test target, which the gate does not run)" : "")}");
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new SortedSet<string>(StringComparer.Ordinal);
        var testFiles = new List<ChangedFile>();
        var removedClassic = new List<(string Project, string Target, string Path)>();
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
                    var where = others.Count > 0 ? $"it is compiled only by {string.Join(", ", others)}, not by a unit-test target"
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
                || project.Targets.FirstOrDefault(t => t.Name == targetName && t.IsUnitTests) is not { } target)
            {
                continue; // the target is gone at the head: no test of it runs
            }
            var (headFiles, headUnknown) = await head.ModuleAsync(project, target, ct);
            var baseProject = (await @base.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == projectPath).Project;
            var baseTarget = baseProject?.Targets.FirstOrDefault(t => t.Name == targetName && t.IsUnitTests);
            var baseFiles = baseProject is not null && baseTarget is not null ? (await @base.ModuleAsync(baseProject, baseTarget, ct)).Files : [];
            var (now, before) = (SwiftXCTest.Discover(headFiles), SwiftXCTest.Discover(baseFiles));
            var known = before.Tests.Select(t => t.Key).Concat(before.Candidates).Concat(before.Problems.Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
            var baseIdsOfTarget = before.Tests.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var added = now.Tests.Where(t => !baseIdsOfTarget.Contains(t.Key)).ToList();
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
            if (project.BuildsForMac(target, configuration!) is { Mac: false } headPlatform)
            {
                groupProblems.Add($"{label} is not a macOS target in its scheme's {configuration} configuration ({headPlatform.Why}): iOS and other destinations are not run by the gate yet");
            }
            if (project.ProductNameProblem(target, configuration!) is { } headName)
            {
                groupProblems.Add($"{label}: {headName}");
            }
            groupProblems.AddRange(PlanExcludes(headPlan, "the head", group.Select(n => n.Key), target.Name));
            var baseProject = (await @base.ProjectsAsync(ct)).FirstOrDefault(p => p.Path == project.Path);
            var baseTarget = baseProject.Project?.Targets.FirstOrDefault(t => t.Name == target.Name && t.IsUnitTests);
            if (baseProject.Project is null || baseTarget is null)
            {
                groupProblems.Add(baseProject.Problem is { } why ? $"the base's {why}" : $"the base has no {label} (a new test target), so its new tests cannot be run on the base");
            }
            else
            {
                var (baseBuild, baseConfiguration, baseProblem, basePlan) = await @base.BuildOfAsync(baseProject.Project, baseTarget, ct);
                if (baseBuild is null || baseBuild.ToString() != build.ToString())
                {
                    groupProblems.Add(baseBuild is null ? $"at the base {baseProblem}" : $"the base tests {label} with the scheme {baseBuild.Scheme} of {baseBuild.Container}, the head with {build.Scheme} of {build.Container}");
                }
                else if (baseProject.Project.BuildsForMac(baseTarget, baseConfiguration!) is { Mac: false } basePlatform)
                {
                    groupProblems.Add($"{label} is not a macOS target at the base ({basePlatform.Why})");
                }
                else if (baseProject.Project.ProductNameProblem(baseTarget, baseConfiguration!) is { } baseName)
                {
                    groupProblems.Add($"{label} at the base: {baseName}");
                }
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
                (text, problem) = project.Targets.Where(t => t.Name == targetName && t.IsUnitTests).ToList() is [var target]
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

        return new NewTestSet(testFiles.Distinct().ToList(), news.Keys.ToList(), problems) { Projects = projects, BaseReplacements = replacements };

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
        for (var n = 0; n < groups.Count; n++)
        {
            var b = Build.From(groups[n].Key);
            steps.Add(new TestStep(TestPhase.Build, wrapperPath, [GateMode, "build-for-testing", b.Flag, b.Container, "-scheme", b.Scheme, "-destination", Destination, "-quiet"])
                { CaptureOutput = $"xcode-build-{n}" });
        }
        foreach (var group in groups)
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
        // And again once the bundles are read, however the run ended (SandboxTestRunner runs it after a failed build or a timeout too).
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
        foreach (var file in resultFiles)
        {
            if (KeptName.Match(file.Name) is not { Success: true } match || file.ExitCode is null)
            {
                unreadable.Add($"{file.Name}: it is not the output of a test step");
                continue;
            }
            var hash = match.Groups["hash"].Value;
            var pair = kept.GetValueOrDefault(hash);
            kept[hash] = match.Groups["kind"].Value == "out" ? pair with { Out = file } : pair with { Json = file };
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
                    throw new InvalidDataException($"xcresulttool exited {json.ExitCode}: {json.Content.Trim().Split('\n')[0]}");
                }
                var cases = Cases(json.Content);
                if (cases.FirstOrDefault(c => Hash(c.Name) != hash) is { } stranger)
                {
                    throw new InvalidDataException($"it holds {stranger.Name}, which is not the test its step ran");
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

    /// <summary>The test cases of one xcresulttool <c>get test-results tests</c> JSON document; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    internal static List<TestCaseResult> Cases(string json)
    {
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
        return cases;
    }

    private static readonly SwiftNewTests SwiftErrors = new();

    /// <summary>The compiler's errors in the build's output, read as <see cref="SwiftNewTests.ParseBuildErrors"/> reads swift build's (<c>/abs/File.swift:3:5: error: …</c>).</summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots) => SwiftErrors.ParseBuildErrors(log, roots);

    /// <summary>
    /// Not yet (a later story, as for SwiftPM): a base that does not build with the PR's test files says nothing here about
    /// which new test cannot build there, so every error is unattributable and the check is an <c>error</c>, never a pass.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var note = "an Xcode project's base that does not build with the PR's test files is not attributed to the new tests yet";
        IReadOnlyList<string> unattributable = errors.Count == 0 ? [$"the build failed and named no error ({note})"]
            : [$"{note}:", .. errors.Select(e => e.ToString())];
        return new BuildFailure([], unattributable, [], [], new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
