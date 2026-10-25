using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// What <see cref="SwiftNewTests.IdentifyAsync"/> found, with each new test's place (sc-26128): its file and its key in
/// that file (its types and signature, <c>Outer/Inner/f()</c>), by which a base build's errors are placed in it.
/// </summary>
public sealed record SwiftNewTestSet(IReadOnlyList<ChangedFile> TestFiles, IReadOnlyList<string> Tests, IReadOnlyList<string> Unisolable)
    : NewTestSet(TestFiles, Tests, Unisolable)
{
    public IReadOnlyDictionary<string, (string Path, string Key)> Locations { get; init; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    /// <summary>
    /// Each applied <c>.swift</c> test file as the head has it (the base run applies it so): what a retry's file, with
    /// what did not compile left out, is compared with to tell which types the overlay itself changed.
    /// </summary>
    public IReadOnlyDictionary<string, string> Originals { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Swift/SwiftPM with Swift Testing (Phase 5, sc-26127), beside <see cref="XunitNewTests"/> and <see cref="CargoNewTests"/>.
/// Nothing is compiled to find tests: <see cref="SwiftSource"/> reads each changed <c>.swift</c> file's functions from its
/// tokens.
/// <para>
/// New tests: the functions with <c>@Test</c> in the head versions of the changed files of a SwiftPM test target — a file
/// under <c>Tests/&lt;target&gt;/</c> of the nearest directory holding a <c>Package.swift</c> that declares
/// <c>testTarget(name: "&lt;target&gt;")</c> with no <c>path:</c> — whose id no base version of a changed file has. The id
/// is Swift Testing's own: <c>&lt;target&gt;.&lt;function&gt;()</c> for a free function, and
/// <c>&lt;target&gt;.&lt;Type&gt;/&lt;Nested&gt;/&lt;function&gt;()</c> for one in a type (what <c>swift test list</c>
/// prints; the test target's name is its module's).
/// </para>
/// <para>
/// The base run's overlay: every changed file under a package's <c>Tests/</c> comes from the head; the package's manifest
/// and its production code stay the base's.
/// </para>
/// <para>
/// XCTest and parameterized tests (sc-26129). A new parameterized test (<c>@Test(arguments:)</c>) is one test whose id is
/// its signature (<c>Target.blanks(text:)</c>): Swift Testing reports all its argument cases as one test case, failed when
/// any case failed. An existing parameterized test whose <c>arguments:</c> the PR changes (<see cref="SwiftFunc.TestArguments"/>)
/// is unsupported: its new cases cannot be run apart from its old ones; so is one whose arguments are unchanged text but
/// name a declaration the pull request changes in its test target, or cannot be resolved to names while the target's
/// declarations change (<see cref="SwiftSource.ArgumentsReachChange"/>). XCTest's tests of a test target are found over
/// every file of the target at the head and the base (<see cref="SwiftXCTest.Discover"/>, when a changed file there declares
/// a class or a test-looking method), so a method a class inherits is a test of that class too; a new one is
/// <c>Target.Class/testMethod</c> (as <c>swift test list</c> names it) and is run with XCTest alone
/// (<c>--disable-swift-testing --filter</c>), its result read from XCTest's own console lines (<see cref="XCTestCases"/>):
/// SwiftPM writes an xUnit report for XCTest only with <c>--parallel</c>, where a skipped test reads as passed.
/// </para>
/// <para>
/// Unisolable (the check is <c>unsupported</c>, never a pass): an existing parameterized test whose arguments change; a
/// test-looking XCTest method the gate cannot run apart (<see cref="SwiftXCTest"/>: an unknown base class, an extension,
/// <c>#if</c>, <c>@objc</c>, private, …); a new test, or a changed file that looks like a test (<see cref="NewTestsCheck.LooksLikeTest"/>),
/// outside a SwiftPM test target (an Xcode project, <c>Sources/</c>, a target the manifest does not declare at its default
/// path, or a package with a version-specific manifest <c>Package@swift-*.swift</c>; beside an Xcode project which files are
/// this strategy's is <see cref="SwiftOwnership"/>'s rule, sc-26134: a file an Xcode test target compiles outside the package's
/// test targets is <see cref="XcodeNewTests"/>'s, and one in both is unsupported here); a test target the base's manifest
/// does not declare; a test id declared twice (conditional compilation); a backticked test name; and a changed file that
/// may hold tests but cannot be read.
/// </para>
/// <para>
/// Runs, from the worktree root per package holding a new test: <c>swift --version</c> (a toolchain probe: failing, the
/// run is <see cref="TestRunStatus.ToolchainMissing"/>), <c>/bin/mkdir -p</c> of the run's results directory (SwiftPM
/// writes no report into a directory that does not exist), <c>swift package resolve</c> (the restore: the only step that
/// may use the network), <c>swift build --build-system native --build-tests</c> (the build, its standard output kept as
/// the build log; <see cref="BuildSystem"/>), then one <c>swift test --build-system native --skip-build --disable-xctest --filter ^&lt;id&gt;(/|$) --xunit-output …</c> per new test (Swift
/// Testing matches the filter against the test's id followed by its source location), each reading only its own report
/// and keeping its exit code, which the report must agree with. Results: Swift Testing's xUnit XML
/// report (<see cref="ParseResults"/>), with its event stream (<c>--event-stream-output-path</c>, <see cref="EventsName"/>): a case that passed with a known issue (<c>withKnownIssue</c>) is a plain pass in the report and is read as skipped. A base that does not build with the PR's test files is explained from swiftc's
/// errors (<see cref="ExplainBuildFailure"/>, sc-26128) and retried up to <see cref="BaseRetries"/> times.
/// </para>
/// </summary>
/// <param name="xcode">
/// Whether <see cref="XcodeNewTests"/> runs beside it (a factory with <c>Worker:Swift</c> on, sc-26131): a file in no SwiftPM
/// package is then left to it; otherwise such a file that adds tests, or looks like a test, is unsupported here, saying that
/// an Xcode project's tests are run only with <c>Worker:Swift</c> on.
/// </param>
public sealed class SwiftNewTests(bool xcode = false) : INewTestStrategy
{
    public string Name => "swift-testing";

    /// <summary>swiftc reports in stages (a module that does not resolve hides the errors of the code using it), as rustc does.</summary>
    public int BaseRetries => 3;

    /// <summary>The program every step but the results directory's runs, found on the test runs' PATH (the launch helper's fixed one when sandboxed).</summary>
    public const string Swift = "swift";

    /// <summary>The program that makes the results directory.</summary>
    public const string MakeDirectory = "/bin/mkdir";

    private const string Manifest = "Package.swift";

    /// <summary>The prefix of each test step's report under the results directory.</summary>
    public const string ReportPrefix = "swift-test-";

    /// <summary>
    /// The one report a test's step asks for and reads: <c>swift-test-&lt;the first 16 hex digits of the id's SHA-256&gt;.xml</c>,
    /// with the package fingerprint suffix for a non-root package, so <see cref="ParseResults"/> knows from a report's name
    /// which package and test it may speak of.
    /// </summary>
    public static string ReportName(string id) =>
        $"{ReportPrefix}{Hash(id)}{PackageSuffix(id)}.xml";

    /// <summary>The name a test step's standard output and exit code are kept under (<see cref="TestStep.CaptureOutput"/>), beside its report.</summary>
    public static string OutputName(string id) => ReportName(id)[..^".xml".Length] + ".out";

    /// <summary>
    /// The Swift Testing event stream a test's step writes beside its report (<c>swift-test-&lt;hash&gt;.events.jsonl</c>,
    /// sc-26134): Swift Testing's JSON event stream, schema version <see cref="EventStreamVersion"/>, read for the known
    /// issues its xUnit report does not show (<see cref="ParseResults"/>).
    /// </summary>
    public static string EventsName(string id) => ReportName(id)[..^".xml".Length] + EventsSuffix;

    private const string EventsSuffix = ".events.jsonl";

    /// <summary>
    /// What a Swift Testing step's results are read from (<see cref="TestStep.ResultFilePattern"/>): its report and its event
    /// stream, both named by the test's hash (<c>swift-test-&lt;hash&gt;.*</c>), so no other test's file matches.
    /// </summary>
    public static string ResultPattern(string id) => ReportName(id)[..^".xml".Length] + ".*";

    /// <summary>
    /// The version of Swift Testing's JSON event stream asked for (<c>--event-stream-version 0</c>): the schema swift-testing
    /// documents as stable (its Documentation/ABI/JSON.md), whose <c>issueRecorded</c> events carry <c>issue.isKnown</c>.
    /// </summary>
    public const string EventStreamVersion = "0";

    private static string Hash(string id) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];

    /// <summary>Discovery/result identity includes the package manifest's SHA-256; root-package keys retain their historic
    /// spelling. The fixed-size fingerprint also keeps report filenames below NAME_MAX even for deeply nested packages.
    /// Runtime filters and the test runner's own names never contain this prefix; Projects retains the manifest path.</summary>
    public static string TestKey(string manifest, string runtimeId) => manifest == Manifest ? runtimeId
        : $"swiftpm:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest)))}:{runtimeId}";

    public static string RuntimeId(string key) => key.StartsWith("swiftpm:", StringComparison.Ordinal)
        ? key[(key.IndexOf(':', "swiftpm:".Length) + 1)..] : key;

    private static string PackageHex(string key) => key.StartsWith("swiftpm:", StringComparison.Ordinal)
        ? key["swiftpm:".Length..key.IndexOf(':', "swiftpm:".Length)] : "";

    private static string PackageSuffix(string key) => PackageHex(key) is { Length: > 0 } hex ? "-p" + hex : "";

    private static string ResultKey(string runtimeId, Match filename) => filename.Groups["package"].Success
        ? $"swiftpm:{filename.Groups["package"].Value}:{runtimeId}" : runtimeId;

    /// <summary>
    /// The name an XCTest test's step output and exit code are kept under (sc-26129): <c>swift-xctest-&lt;16 hex of the id's
    /// SHA-256&gt;-&lt;the target's name in hex&gt;[-p&lt;package fingerprint&gt;].out</c> — the target, because XCTest on Linux names a test by its class
    /// and method alone.
    /// </summary>
    public static string XCTestOutputName(string id) =>
        $"swift-xctest-{Hash(id)}-{Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(RuntimeId(id)[..RuntimeId(id).IndexOf('.')]))}{PackageSuffix(id)}.out";

    private static readonly Regex XCTestOutputFileName = new(@"^swift-xctest-(?<hash>[0-9a-f]{16})-(?<target>(?:[0-9a-f]{2})+)(?:-p(?<package>(?:[0-9a-f]{2})+))?\.out$", RegexOptions.CultureInvariant);

    private static readonly Regex ReportFileName =new(@"^swift-test-[0-9a-f]{16}(?:-p(?<package>(?:[0-9a-f]{2})+))?\.xml$", RegexOptions.CultureInvariant);

    private static readonly Regex EventsFileName = new(@"^swift-test-[0-9a-f]{16}(?:-p(?<package>(?:[0-9a-f]{2})+))?\.events\.jsonl$", RegexOptions.CultureInvariant);

    public bool Understands(string path) => path.EndsWith(".swift", StringComparison.Ordinal);

    /// <summary>Nothing: the build's standard output is kept (<see cref="TestStep.CaptureOutput"/>), no log file is read.</summary>
    public string BuildLogPattern => "swift-build-*.none";

    private static bool IsManifest(string path) =>
        Regex.IsMatch(path[(path.LastIndexOf('/') + 1)..], @"^Package(@swift-[^/]*)?\.swift$", RegexOptions.CultureInvariant);

    private sealed record Package(string Directory, string ManifestPath);

    /// <summary>Where a file is: its package, and its test target when it is in one; else why it is not (<see cref="Problem"/>).</summary>
    private sealed record Location(Package? Package, string? Target, bool InTests, string? Problem);

    /// <summary>One commit's files, read once each.</summary>
    private sealed class Tree(TestSource source, string sha)
    {
        private HashSet<string>? _files;
        private readonly Dictionary<string, string?> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SwiftFile?> _parsed = new(StringComparer.Ordinal);

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

        private SwiftOwnership? _owners;

        /// <summary>Which strategy judges each file of this commit (sc-26134: a package beside an Xcode project).</summary>
        public SwiftOwnership Owners => _owners ??= new SwiftOwnership(async ct => await FilesAsync(ct), ReadAsync);

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

        /// <summary>The package <paramref name="path"/> belongs to: the nearest directory above it holding a <c>Package.swift</c>.</summary>
        public async Task<Package?> PackageAsync(string path, CancellationToken ct)
        {
            var files = await FilesAsync(ct);
            for (var directory = Parent(path); ; directory = Parent(directory))
            {
                if (files.Contains(Join(directory, Manifest)))
                {
                    return new Package(directory, Join(directory, Manifest));
                }
                if (directory.Length == 0)
                {
                    return null;
                }
            }
        }

        public async Task<Location> LocateAsync(string path, CancellationToken ct)
        {
            if (await PackageAsync(path, ct) is not { } package)
            {
                return new Location(null, null, false, "it is in no SwiftPM package (no Package.swift above it), and the gate runs an Xcode project's tests only in a factory with Worker:Swift on");
            }
            var parts = Relative(package.Directory, path).Split('/');
            return parts switch
            {
                ["Tests", var target, _, ..] => new Location(package, target, true, null),
                ["Tests", ..] => new Location(package, null, true, "it is directly in Tests/, outside every test target's directory"),
                _ => new Location(package, null, false, "it is outside the package's Tests/<target>/ directories, where SwiftPM's test targets are"),
            };
        }
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    private static string Join(string directory, string relative) => directory.Length == 0 ? relative : $"{directory}/{relative}";

    private static string Relative(string directory, string path) => directory.Length == 0 ? path : path[(directory.Length + 1)..];

    /// <summary>A test's id as Swift Testing names it: <c>Target.f()</c>, or <c>Target.Outer/Inner/f()</c> in a type.</summary>
    public static string Id(string target, IReadOnlyList<string> types, string signature) =>
        $"{target}.{(types.Count == 0 ? "" : string.Join("/", types) + "/")}{signature}";

    private static string Id(string target, SwiftFunc func) => Id(target, func.Types, func.Signature);

    /// <summary>A function's place in its file, independent of the module (for a file in no test target).</summary>
    internal static string Key(SwiftFunc func) => string.Join("/", func.Types.Append(func.Signature));

    // ---- Finding the new tests ---------------------------------------------------------------------------------------------

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        if (!changes.Any(c => c.Paths.Any(p => Understands(p) && !IsManifest(p))))
        {
            return NewTestSet.None;
        }
        var (head, @base) = (new Tree(source, source.HeadSha), new Tree(source, source.BaseSha));
        var problems = new List<string>();

        // The base's Swift Testing tests of the changed files, by id; and the test targets whose XCTest tests may have changed
        // (a changed file there declares a class or a test-looking method, at the head or the base).
        var baseTests = new Dictionary<string, SwiftFunc>(StringComparer.Ordinal);
        var xctestTargets = new SortedDictionary<string, Location>(StringComparer.Ordinal);
        bool MayChangeXCTests(SwiftFile file) => file.Types.Any(t => t.Keyword is "class" or "extension") || file.Funcs.Any(SwiftXCTest.IsCandidate);
        foreach (var old in changes.Select(c => c.OldPath).OfType<string>().Where(p => Understands(p) && !IsManifest(p)))
        {
            if (await @base.ParseAsync(old, ct) is { } file && await @base.LocateAsync(old, ct) is { Target: { } target } place)
            {
                foreach (var test in file.Tests)
                {
                    baseTests.TryAdd(TestKey(place.Package!.ManifestPath, Id(target, test)), test);
                }
                if (MayChangeXCTests(file))
                {
                    xctestTargets[$"{place.Package!.Directory}\n{target}"] = place;
                }
            }
        }

        var news = new SortedDictionary<string, Location>(StringComparer.Ordinal);
        var locations = new Dictionary<string, (string Path, string Key)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new SortedSet<string>(StringComparer.Ordinal);
        var testFiles = new List<ChangedFile>();
        var targetChanges = new SortedDictionary<string, (Location Place, List<string> Head, List<string> Base)>(StringComparer.Ordinal);
        var argumentsChanged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            // Every changed file under a package's Tests/ is the PR's test code: the base run applies it.
            var inTests = false;
            foreach (var (tree, at) in new[] { (head, change.NewPath), (@base, change.OldPath) })
            {
                var located = at is null ? null : await tree.LocateAsync(at, ct);
                inTests |= located is { InTests: true };
                if (located is { Target: { } changedTarget } && Understands(at!) && !IsManifest(at!))
                {
                    var entry = targetChanges.TryGetValue($"{located.Package!.Directory}\n{changedTarget}", out var known) ? known
                        : targetChanges[$"{located.Package!.Directory}\n{changedTarget}"] = (located, [], []);
                    (ReferenceEquals(tree, head) ? entry.Head : entry.Base).Add(at!);
                }
            }
            if (inTests)
            {
                testFiles.Add(change);
            }
            if (change.NewPath is not { } path || !Understands(path) || IsManifest(path))
            {
                continue;
            }
            var place = await head.LocateAsync(path, ct);
            if (xcode && place.Package is null)
            {
                continue; // in no SwiftPM package: an Xcode project's file, XcodeNewTests's (sc-26131)
            }
            if (place.Package is not null && await head.Owners.OfAsync(path, ct) is var (owner, both) && owner != SwiftOwner.SwiftPM)
            {
                // A package beside an Xcode project (sc-26134): a file an Xcode test target compiles outside the package's test
                // targets is XcodeNewTests's; one in both, or with Worker:Swift off, has tests the gate cannot run here.
                if (owner == SwiftOwner.Xcode && xcode)
                {
                    continue;
                }
                place = place with
                {
                    Target = null,
                    Problem = both ?? "an Xcode project's test target compiles it, and the gate runs an Xcode project's tests only in a factory with Worker:Swift on",
                };
            }
            if (await head.ParseAsync(path, ct) is not { } file)
            {
                var text = await head.ReadAsync(path, ct) ?? "";
                if (head.Unreadable.TryGetValue(path, out var why)
                    && (place.Target is not null || NewTestsCheck.LooksLikeTest(path) || text.Contains("@Test", StringComparison.Ordinal) || text.Contains("XCTest", StringComparison.Ordinal)))
                {
                    problems.Add($"{path} cannot be read as Swift source ({why}), so the tests it may add cannot be found");
                }
                continue;
            }
            if (place.Target is not { } target)
            {
                var tests = file.Tests.Concat(file.XCTestMethods).ToList();
                // Outside a test target: no test of it can be run apart, so a new one (or a file that looks like a test) is unisolable.
                var before = change.OldPath is { } old && await @base.ParseAsync(old, ct) is { } was
                    ? was.Tests.Concat(was.XCTestMethods).Select(Key).ToHashSet(StringComparer.Ordinal) : [];
                var added = tests.Where(t => !before.Contains(Key(t))).Select(Key).ToList();
                if (added.Count > 0 || (NewTestsCheck.LooksLikeTest(path) && !inTests))
                {
                    problems.Add(added.Count > 0
                        ? $"{path} adds test(s) {string.Join(", ", added)} the gate cannot run: {place.Problem}"
                        : $"{path} looks like a test file the gate cannot run: {place.Problem}");
                }
                continue;
            }
            if (MayChangeXCTests(file))
            {
                xctestTargets[$"{place.Package!.Directory}\n{target}"] = place;
            }
            foreach (var test in file.Tests)
            {
                var id = TestKey(place.Package!.ManifestPath, Id(target, test));
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
                    problems.Add($"{id} in {path} has a backticked name, which the gate cannot name to Swift Testing's filter");
                }
                else
                {
                    news[id] = place;
                    locations[id] = (path, Key(test));
                }
            }
        }
        // XCTest's tests of each test target a changed file may change them in: every file of the target read at the head and
        // the base, since a method a class inherits is a test of that class too (sc-26129).
        foreach (var place in xctestTargets.Values)
        {
            var (package, target) = (place.Package!, place.Target!);
            var (headFiles, headUnreadable) = await ModuleAsync(head, package.Directory, target, ct);
            var (baseFiles, _) = await ModuleAsync(@base, package.Directory, target, ct);
            var (now, before) = (SwiftXCTest.Discover(headFiles), SwiftXCTest.Discover(baseFiles));
            // A head problem is new unless its place was a test or a problem at the base: one that was only a test-looking
            // method of a class that was no test class there is judged now.
            var known = before.Tests.Select(t => t.Key).Concat(before.Problems.Select(p => p.Key)).ToHashSet(StringComparer.Ordinal);
            var baseIdsOfTarget = before.Tests.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var moved = SwiftXCTest.MovedUnchanged(before, now);
            var added = now.Tests.Where(t => !baseIdsOfTarget.Contains(t.Key) && !moved.Contains(t.Key)).ToList();
            var unjudged = now.Problems.Where(p => !known.Contains(p.Key)).ToList();
            problems.AddRange(unjudged.Select(p => $"{target}.{p.Key} in {p.Path} is a new XCTest test method the gate cannot run apart: {p.Why}"));
            if ((added.Count > 0 || unjudged.Count > 0) && headUnreadable.Count > 0)
            {
                problems.Add($"the test target {target}'s {string.Join(", ", headUnreadable)} cannot be read as Swift source, so which XCTest classes inherit its new tests cannot be told");
            }
            foreach (var test in added)
            {
                var id = TestKey(package.ManifestPath, XCTestId(target, test.Class, test.Method.Name));
                if (!seen.Add(id))
                {
                    twice.Add(id);
                }
                news[id] = place;
                locations[id] = (test.Path, Key(test.Method));
            }
        }
        // An existing parameterized test whose arguments: are the same text may still get new cases: when they name a
        // declaration of its test target that the pull request changes (a fixture's new case, say). Failing closed (sc-26129).
        foreach (var (place, headPaths, basePaths) in targetChanges.Values)
        {
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
            foreach (var (path, file) in (await ModuleAsync(head, place.Package!.Directory, place.Target!, ct)).Files)
            {
                foreach (var test in file.Tests.Where(t => t.TestArguments is not null))
                {
                    var id = TestKey(place.Package!.ManifestPath, Id(place.Target!, test));
                    var existing = !news.ContainsKey(id) && !argumentsChanged.Contains(id) && (!headPaths.Contains(path) || baseTests.ContainsKey(id));
                    if (existing && SwiftSource.ArgumentsReachChange(test, changed) is { } why)
                    {
                        problems.Add($"{id} in {path} is an existing parameterized test whose cases the pull request may change: {why}; its new cases cannot be run apart from its old ones");
                    }
                }
            }
        }
        problems.AddRange(twice.Select(id => $"{id} is declared more than once at the head (conditional compilation?), so which one runs cannot be told"));

        // Each new test's target must be declared, at its default path, by the package's manifest at the head and at the base.
        foreach (var (package, target) in news.Values.Select(l => (l.Package!, l.Target!)).Distinct())
        {
            var directory = package.Directory.Length == 0 ? "" : package.Directory + "/";
            if ((await head.FilesAsync(ct)).Any(f => f.StartsWith(directory, StringComparison.Ordinal) && !f[directory.Length..].Contains('/') && IsManifest(f) && f != package.ManifestPath))
            {
                problems.Add($"{package.ManifestPath}'s package has a version-specific manifest (Package@swift-*.swift), so which manifest declares its test targets depends on the toolchain");
                continue;
            }
            foreach (var (tree, label) in new[] { (head, "the head"), (@base, "the base") })
            {
                var declared = await TargetAsync(tree, package.ManifestPath, target, ct);
                if (declared is not null)
                {
                    problems.Add(label == "the base" && declared == Undeclared
                        ? $"the test target {target} is not declared in the base's {package.ManifestPath} (a new test target), so its new tests cannot be run on the base yet"
                        : $"{package.ManifestPath} at {label} {declared} {target}");
                }
            }
        }

        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in testFiles.Where(f => !f.Deleted).Select(f => f.NewPath!).Where(p => Understands(p) && !IsManifest(p)).Distinct(StringComparer.Ordinal))
        {
            if (await head.ReadAsync(path, ct) is { } text)
            {
                originals[path] = text;
            }
        }
        return new SwiftNewTestSet(testFiles, news.Keys.ToList(), problems)
        {
            Originals = originals,
            Projects = news.ToDictionary(kv => kv.Key, kv => kv.Value.Package!.ManifestPath, StringComparer.Ordinal),
            Locations = news.Keys.ToDictionary(id => id, id => locations[id], StringComparer.Ordinal),
        };
    }

    /// <summary>Every <c>.swift</c> file of a test target (<c>Tests/&lt;target&gt;/</c> of the package), parsed; and those that cannot be.</summary>
    private static async Task<(List<(string Path, SwiftFile File)> Files, List<string> Unreadable)> ModuleAsync(Tree tree, string packageDirectory, string target, CancellationToken ct)
    {
        var prefix = Join(packageDirectory, $"Tests/{target}/");
        var (files, unreadable) = (new List<(string, SwiftFile)>(), new List<string>());
        foreach (var path in (await tree.FilesAsync(ct)).Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.EndsWith(".swift", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if ((await tree.Owners.OfAsync(path, ct)).Owner != SwiftOwner.SwiftPM)
            {
                continue; // an Xcode test target compiles it too (sc-26134): its tests are no SwiftPM target's alone
            }
            if (await tree.ParseAsync(path, ct) is { } file)
            {
                files.Add((path, file));
            }
            else
            {
                unreadable.Add(path);
            }
        }
        return (files, unreadable);
    }

    /// <summary>An XCTest test's id as SwiftPM names it (<c>swift test list</c>): <c>Target.Class/testMethod</c>, no parentheses (sc-26129).</summary>
    public static string XCTestId(string target, string type, string method) => $"{target}.{type}/{method}";

    /// <summary>Whether an id is an XCTest test's (<see cref="XCTestId"/>): a Swift Testing id ends in its parameter list's <c>)</c>.</summary>
    public static bool IsXCTestId(string id) => !id.EndsWith(')');

    private const string Undeclared ="does not declare (as testTarget(name: \"…\") with a plain string) the test target";

    /// <summary>Null when the manifest declares the target at its default path; else what is wrong.</summary>
    private static async Task<string?> TargetAsync(Tree tree, string manifest, string target, CancellationToken ct)
    {
        if (await tree.ReadAsync(manifest, ct) is not { } text)
        {
            return Undeclared;
        }
        IReadOnlyList<(string Name, bool CustomPath)> targets;
        try
        {
            targets = SwiftSource.TestTargets(text);
        }
        catch (FormatException e)
        {
            return $"cannot be read ({e.Message}), so it is not known to declare the test target";
        }
        return targets.FirstOrDefault(t => t.Name == target) is not { Name: not null } declaredTarget ? Undeclared
            : declaredTarget.CustomPath ? "sets a path: for the test target" : null;
    }

    // ---- Running them ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Swift Testing's <c>--filter</c> for exactly one test: the id, its regex characters escaped, at the start of the test's
    /// id and followed by the source location Swift Testing appends (<c>/File.swift:3:2</c>) or by nothing.
    /// </summary>
    public static string Filter(string id) => "^" + Regex.Replace(id, @"[^A-Za-z0-9_\u0080-￿]", @"\$0") + "(/|$)";

    /// <summary>
    /// The build system every build and test step names (<c>--build-system native</c>), never the toolchain's default.
    /// <see cref="ParseBuildErrors"/> reads the native build system's output: swiftc's diagnostics on either captured build stream, plain
    /// text. SwiftPM 6.4 made <c>swiftbuild</c> the default, which writes them to standard error, in colour, after its own
    /// <c>error: SwiftCompile … failed …</c> echo — read as it is, every base that does not build would be an
    /// <c>error</c> ("names no compiler error"). Named on both steps so <c>swift test --skip-build</c> finds the build's
    /// products. A toolchain that no longer knows the flag fails the build with an error naming no file: unattributable,
    /// an <c>error</c>, never a pass.
    /// </summary>
    public const string BuildSystem = "native";

    /// <summary>
    /// <c>swift --version</c>; <c>/bin/mkdir -p</c> of the results directory; per package <c>swift package resolve</c> (the
    /// only step that may use the network) and <c>swift build --build-system native --build-tests</c> (output kept as the build log); then per new
    /// test one <c>swift test --skip-build --disable-xctest --filter … --xunit-output &lt;results&gt;/</c><see cref="ReportName"/>,
    /// reading that report alone and keeping the step's exit code (<see cref="OutputName"/>).
    /// Run from the worktree root with <c>--package-path</c>.
    /// </summary>
    public Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct)
    {
        string PackagePath(string id)
        {
            var manifest = projects.TryGetValue(id, out var m) ? m : throw new InvalidOperationException($"The new test {id} has no package manifest.");
            return manifest == Manifest ? "." : $"./{Parent(manifest)}";
        }
        var byPackage = tests.GroupBy(PackagePath, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var steps = new List<TestStep>
        {
            new(TestPhase.Toolchain, Swift, ["--version"]),
            new(TestPhase.Restore, MakeDirectory, ["-p", resultsDirectory]),
        };
        steps.AddRange(byPackage.Select(g => new TestStep(TestPhase.Restore, Swift, ["package", "--package-path", g.Key, "resolve"])));
        steps.AddRange(byPackage.Select((g, n) => new TestStep(TestPhase.Build, Swift, ["build", "--package-path", g.Key, "--build-system", BuildSystem, "--build-tests"]) { CaptureOutput = $"swift-build-{n}", CaptureBuildStderr = true }));
        foreach (var id in byPackage.SelectMany(g => g.Select(id => (Id: id, Package: g.Key))))
        {
            if (IsXCTestId(RuntimeId(id.Id)))
            {
                // An XCTest test (sc-26129): XCTest alone, its own console output kept with the exit code (SwiftPM writes an
                // xUnit report for XCTest only with --parallel, where a skipped test reads as passed).
                steps.Add(new TestStep(TestPhase.Test, Swift,
                [
                    "test", "--package-path", id.Package, "--build-system", BuildSystem, "--skip-build", "--disable-swift-testing", "--filter", Filter(RuntimeId(id.Id)),
                ]) { CaptureOutput = XCTestOutputName(id.Id) });
                continue;
            }
            // Its own report only (a pattern naming exactly it), and its exit code, which the report must agree with.
            steps.Add(new TestStep(TestPhase.Test, Swift,
            [
                "test", "--package-path", id.Package, "--build-system", BuildSystem, "--skip-build", "--disable-xctest", "--filter", Filter(RuntimeId(id.Id)),
                "--xunit-output", $"{resultsDirectory}/{ReportName(id.Id)}",
                "--event-stream-output-path", $"{resultsDirectory}/{EventsName(id.Id)}", "--event-stream-version", EventStreamVersion,
            ]) { ResultFilePattern = ResultPattern(id.Id), CaptureOutput = OutputName(id.Id) });
        }
        return Task.FromResult<IReadOnlyList<TestStep>>(steps);
    }

    /// <summary>
    /// Each test's cases from Swift Testing's xUnit XML reports (<c>testsuites</c> of <c>testsuite</c>s of
    /// <c>testcase</c>s): a case's id is its <c>classname</c> (the target, then its types, dot-separated) and <c>name</c>
    /// (the function's signature), as <see cref="Id(string, IReadOnlyList{string}, string)"/> builds it; a
    /// <c>failure</c> or <c>error</c> child fails it, a <c>skipped</c> one skips it. A report is read only when it is
    /// well-formed (no DTD), Swift Testing's shape, and each suite's counts agree with its cases (the cases are its
    /// <c>tests</c>, with or without its <c>skipped</c>; failed ones its <c>failures</c> plus <c>errors</c>; skipped ones
    /// its <c>skipped</c>); anything else is unreadable and named, never a result. A report is also unreadable unless it
    /// is one a step asked for (<see cref="ReportName"/>, at the results directory's top), every case in it is the test
    /// that step ran (its name is that test's), and the step's exit code is known and agrees with it (0 exactly when no
    /// case failed): a test that writes another test's report, or a report of its own, is never read as a result. A
    /// step whose report is missing is unreadable too, and so is a report without its event stream (<see cref="EventsName"/>, read by <see cref="KnownIssue"/>): a passing case of a run that recorded a known issue is read as skipped (Swift Testing writes it as a plain pass; also when the step's output mentions a known issue).
    /// </summary>
    public TestResults ParseResults(IEnumerable<ResultFile> resultFiles)
    {
        var results = new Dictionary<string, List<TestCaseResult>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var all = resultFiles.ToList();
        foreach (var output in all.Where(f => f.ExitCode is not null && XCTestOutputFileName.IsMatch(f.Name)))
        {
            try
            {
                var match = XCTestOutputFileName.Match(output.Name);
                var target = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(match.Groups["target"].Value));
                foreach (var result in XCTestCases(output.Content, target, match.Groups["hash"].Value, output.ExitCode!.Value, match))
                {
                    (results.TryGetValue(result.Name, out var list) ? list : results[result.Name] = []).Add(result);
                }
            }
            catch (InvalidDataException e)
            {
                unreadable.Add($"{output.Name}: {e.Message}");
            }
        }
        all = all.Where(f => f.ExitCode is null || !XCTestOutputFileName.IsMatch(f.Name)).ToList();
        var exits = all.Where(f => f.ExitCode is not null).ToDictionary(f => f.Name, f => f.ExitCode!.Value, StringComparer.Ordinal);
        var outputs = all.Where(f => f.ExitCode is not null).ToDictionary(f => f.Name, f => f.Content, StringComparer.Ordinal);
        var streams = all.Where(f => f.ExitCode is null && EventsFileName.IsMatch(f.Name)).ToDictionary(f => f.Name, f => f.Content, StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, content) in all.Where(f => f.ExitCode is null && !EventsFileName.IsMatch(f.Name)))
        {
            var cases = new List<(string, TestCaseResult)>();
            try
            {
                if (!ReportFileName.IsMatch(name))
                {
                    throw new InvalidDataException("it is not a report a test step asked for");
                }
                var output = name[..^".xml".Length] + ".out";
                reported.Add(output);
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new InvalidDataException("it is empty (the test runner could not write it)");
                }
                using var reader = XmlReader.Create(new StringReader(content),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
                var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("it has no root element");
                var suites = root.Name.LocalName switch
                {
                    "testsuites" => root.Elements().Where(e => e.Name.LocalName == "testsuite").ToList(),
                    "testsuite" => [root],
                    var other => throw new InvalidDataException($"it is not an xUnit XML report of Swift Testing (root element <{other}>)"),
                };
                foreach (var suite in suites)
                {
                    var suiteCases = suite.Descendants().Where(e => e.Name.LocalName == "testcase").ToList();
                    int Count(string attribute) => int.TryParse(suite.Attribute(attribute)?.Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidDataException($"a testsuite has no count of its {attribute}");
                    var (tests, failures, errors, skipped) = (Count("tests"), Count("failures"), Count("errors"), Count("skipped"));
                    var outcomes = suiteCases.Select(Outcome).ToList();
                    // Swift Testing counts issues in failures (one <failure> per issue: a test with two failed #expect has
                    // two), not failed cases (sc-26129); a failed case has at least one.
                    var issues = suiteCases.Sum(c => c.Elements().Count(e => e.Name.LocalName is "failure" or "error"));
                    if ((suiteCases.Count != tests && suiteCases.Count != tests + skipped) || issues != failures + errors
                        || outcomes.Count(o => o == TestCaseResult.Skipped) != skipped)
                    {
                        throw new InvalidDataException("its testcase elements do not add up to their suite's counts (a test wrote to it, or it is not Swift Testing's report)");
                    }
                    foreach (var (element, outcome) in suiteCases.Zip(outcomes))
                    {
                        if (element.Attribute("classname")?.Value is not { Length: > 0 } classname || element.Attribute("name")?.Value is not { Length: > 0 } function)
                        {
                            throw new InvalidDataException("a testcase names no classname and name");
                        }
                        var types = classname.Split('.');
                        var id = ResultKey(Id(types[0], types[1..], function), ReportFileName.Match(name));
                        if (ReportName(id) != name)
                        {
                            throw new InvalidDataException($"it holds {id}, which is not the test its step ran");
                        }
                        cases.Add((id, new TestCaseResult(id, outcome)));
                    }
                }
                if (!exits.TryGetValue(output, out var exit))
                {
                    throw new InvalidDataException("its step's exit code is not known");
                }
                var failed = cases.Count(c => c.Item2.Outcome == TestCaseResult.Failed);
                if ((exit == 0) != (failed == 0))
                {
                    throw new InvalidDataException($"swift test exited {exit}, which does not agree with its {failed} failing case(s)");
                }
                // A known issue (withKnownIssue) leaves a plain passing testcase in the report (Swift 6.3.2): the case passed
                // only because its failure was expected, so it is read as skipped — no pass on the head, no failure on the base.
                var events = name[..^".xml".Length] + EventsSuffix;
                if (!streams.TryGetValue(events, out var stream))
                {
                    throw new InvalidDataException($"its step wrote no Swift Testing event stream ({events}), so whether a case passed only with a known issue cannot be told");
                }
                if (KnownIssue(stream) || outputs[output].Contains("known issue", StringComparison.OrdinalIgnoreCase))
                {
                    cases = cases.Select(c => c.Item2.Outcome == TestCaseResult.Passed ? (c.Item1, new TestCaseResult(c.Item1, TestCaseResult.Skipped)) : c).ToList();
                }
            }
            catch (Exception e) when (e is XmlException or InvalidDataException)
            {
                unreadable.Add($"{name}: {(e is XmlException ? $"it is not well-formed XML ({e.Message})" : e.Message)}");
                continue;
            }
            foreach (var (test, result) in cases)
            {
                (results.TryGetValue(test, out var list) ? list : results[test] = []).Add(result);
            }
        }
        unreadable.AddRange(exits.Where(e => !reported.Contains(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key[..^".out".Length]}.xml: the test step (exit {e.Value}) wrote no report"));
        return new TestResults(results.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TestCaseResult>)kv.Value, StringComparer.Ordinal), unreadable);
    }

    private static readonly Regex XCTestStarted = new(@"^Test Case '(?<name>[^']+)' started(?: at .+|\.)$", RegexOptions.CultureInvariant);

    private static readonly Regex XCTestEnded = new(@"^Test Case '(?<name>[^']+)' (?<verdict>passed|failed|skipped) \(\d+(?:\.\d+)? seconds\)\.?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A test suite's summary, the same on Apple platforms and in swift-corelibs-xctest (Linux): <c>Executed 1 test, with
    /// [1 test skipped and ]0 failures (0 unexpected) in 0.001 (0.001) seconds</c>. Its failures count the issues recorded
    /// (two failed assertions in one test are 2), not the failed tests.
    /// </summary>
    private static readonly Regex XCTestSummary = new(
        @"^\s*Executed (?<tests>\d{1,9}) tests?, with (?:(?<skipped>\d{1,9}) tests? skipped and )?(?<failures>\d{1,9}) failures? \(\d{1,9} unexpected\) in \d+(?:\.\d+)? \(\d+(?:\.\d+)?\) seconds$",
        RegexOptions.CultureInvariant);

    /// <summary>XCTest on Apple platforms: <c>-[Target.Class testMethod]</c>.</summary>
    private static readonly Regex XCTestObjCName = new(@"^-\[(?<type>[^ \[\]']+) (?<method>[^ \[\]']+)\]$", RegexOptions.CultureInvariant);

    /// <summary>swift-corelibs-xctest (Linux): <c>Class.testMethod</c>, the class unqualified.</summary>
    private static readonly Regex XCTestPlainName = new(@"^(?<type>[^ .\[\]']+)\.(?<method>[^ .\[\]']+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// An XCTest test's result from XCTest's own console output (sc-26129), which SwiftPM prints on standard output: each
    /// <c>Test Case '…' started</c> line and its <c>Test Case '…' passed|failed|skipped (N seconds)</c> line. Read only in
    /// that shape: every ended test was started first, ended once, and is the test the step ran (its id's hash is the
    /// output's), at least one suite summary (<see cref="XCTestSummary"/>) follows the last ended line and every summary
    /// after it counts exactly the cases read — as many tests, as many skipped, and failures (issues) exactly when a case
    /// failed, at least one per failed case — and the step's exit code agrees (0 exactly when no case failed); otherwise
    /// the output is unreadable, never a result. A test process that crashed or was killed (a trap, a fatal error: a case
    /// started and never ended, and no summary after it) is therefore unreadable, so the check is an <c>error</c>, never a
    /// failure on the base. A summary of <c>Executed 0 tests</c> and no case line is no case (the filter matched nothing:
    /// not run). An output that mentions an expected failure (Apple's <c>XCTExpectFailure</c>) has its passes read as
    /// skipped: no pass on the head, no failure on the base. As with cargo's libtest lines, a test that prints a complete
    /// fake report of itself and picks its exit code is read as what it printed (accepted: reviewed code lying about itself).
    /// </summary>
    internal static List<TestCaseResult> XCTestCases(string output, string target, string hash, int exit, Match? filename = null)
    {
        var started = new HashSet<string>(StringComparer.Ordinal);
        var ended = new HashSet<string>(StringComparer.Ordinal);
        var cases = new List<TestCaseResult>();
        var (skipped, failedCases) = (0, 0);
        var summaries = new List<(int Tests, int Skipped, int Failures)>();
        var expectedFailure = output.Contains("expected failure", StringComparison.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (XCTestStarted.Match(line) is { Success: true } start)
            {
                started.Add(start.Groups["name"].Value);
                continue;
            }
            if (XCTestSummary.Match(line) is { Success: true } summary)
            {
                static int Count(Group g) => g.Success ? int.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                summaries.Add((Count(summary.Groups["tests"]), Count(summary.Groups["skipped"]), Count(summary.Groups["failures"])));
                continue;
            }
            if (XCTestEnded.Match(line) is not { Success: true } end)
            {
                continue;
            }
            summaries.Clear(); // only the summaries after the last ended line count
            var name = end.Groups["name"].Value;
            var id = XCTestObjCName.Match(name) is { Success: true } objc ? $"{objc.Groups["type"].Value}/{objc.Groups["method"].Value}"
                : XCTestPlainName.Match(name) is { Success: true } plain ? XCTestId(target, plain.Groups["type"].Value, plain.Groups["method"].Value)
                : throw new InvalidDataException($"its test case '{name}' is not named as XCTest names one");
            if (filename is not null) id = ResultKey(id, filename);
            if (Hash(id) != hash)
            {
                throw new InvalidDataException($"it reports {id}, which is not the test its step ran");
            }
            if (!started.Contains(name) || !ended.Add(name))
            {
                throw new InvalidDataException($"its test case '{name}' ends {(started.Contains(name) ? "twice" : "without having started")} (a test wrote to it)");
            }
            var verdict = end.Groups["verdict"].Value;
            skipped += verdict == "skipped" ? 1 : 0;
            failedCases += verdict == "failed" ? 1 : 0;
            cases.Add(new TestCaseResult(id, verdict switch
            {
                "failed" => TestCaseResult.Failed,
                "passed" when !expectedFailure => TestCaseResult.Passed,
                _ => TestCaseResult.Skipped,
            }));
        }
        if (started.FirstOrDefault(s => !ended.Contains(s)) is { } unfinished)
        {
            throw new InvalidDataException($"its test case '{unfinished}' started and never ended (the test process crashed or was killed)");
        }
        if (summaries.Count == 0)
        {
            throw new InvalidDataException("no XCTest summary (Executed N tests, …) follows its last test case");
        }
        foreach (var wrong in summaries.Where(s => s.Tests != cases.Count || s.Skipped != skipped || (s.Failures == 0) != (failedCases == 0) || s.Failures < failedCases))
        {
            throw new InvalidDataException($"its summary (Executed {wrong.Tests} test(s), {wrong.Skipped} skipped, {wrong.Failures} failure(s)) does not agree with its {cases.Count} case(s), {skipped} skipped and {failedCases} failed");
        }
        var failed = cases.Count(c => c.Outcome == TestCaseResult.Failed);
        if ((exit == 0) != (failed == 0))
        {
            throw new InvalidDataException($"swift test exited {exit}, which does not agree with its {failed} failing case(s)");
        }
        return cases;
    }

    /// <summary>
    /// Whether a Swift Testing event stream (JSON Lines, schema <see cref="EventStreamVersion"/>) records a known issue: an
    /// <c>event</c> record of kind <c>issueRecorded</c> whose <c>issue.isKnown</c> is true (sc-26134). Read only when every
    /// line is a JSON object of that schema version and a <c>runEnded</c> event closes the run; otherwise unreadable, never
    /// "no known issue".
    /// </summary>
    internal static bool KnownIssue(string stream)
    {
        var (known, ended) = (false, false);
        foreach (var line in stream.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            try
            {
                using var record = System.Text.Json.JsonDocument.Parse(line);
                var root = record.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !root.TryGetProperty("version", out var version) || version.ValueKind != System.Text.Json.JsonValueKind.Number || version.GetRawText() != EventStreamVersion
                    || !root.TryGetProperty("kind", out var kind) || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    throw new InvalidDataException($"its Swift Testing event stream has a record that is not of schema version {EventStreamVersion}");
                }
                if (kind.ValueEquals("event") && payload.TryGetProperty("kind", out var what))
                {
                    ended |= what.ValueEquals("runEnded");
                    known |= what.ValueEquals("issueRecorded") && payload.TryGetProperty("issue", out var issue) && issue.ValueKind == System.Text.Json.JsonValueKind.Object
                        && issue.TryGetProperty("isKnown", out var isKnown) && isKnown.ValueKind == System.Text.Json.JsonValueKind.True;
                }
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new InvalidDataException($"its Swift Testing event stream has a line that is not JSON ({e.Message})");
            }
        }
        return ended ? known : throw new InvalidDataException("its Swift Testing event stream has no runEnded event (the run did not finish writing it)");
    }

    private static string Outcome(XElement testcase)
    {
        var children = testcase.Elements().Select(e => e.Name.LocalName).ToList();
        return children.Any(c => c is "failure" or "error") ? TestCaseResult.Failed
            : children.Contains("skipped") ? TestCaseResult.Skipped
            : TestCaseResult.Passed;
    }

    /// <summary>A located compiler error: <c>/abs/File.swift:3:28: error: message</c> (or <c>fatal error:</c>, clang's).</summary>
    private static readonly Regex LocatedError = new(@"^(?<file>/.+?):(?<line>\d{1,9}):(?<col>\d{1,9}): (?:fatal )?error: (?<message>.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Any other line that reports an error: at its start (<c>error: …</c>, <c>fatal error: …</c>) or after a <c>: </c>
    /// (<c>&lt;unknown&gt;:0: error: …</c>, <c>clang: error: …</c>, a relative <c>Tests/X.swift:1:2: error: …</c>). Such
    /// a line names no file in the worktree that an error can be placed in, so it is an error with no file. The excerpt
    /// lines swiftc prints under an error (<c>|  `- error: …</c>) do not match: there <c>error:</c> follows <c>- </c>.
    /// </summary>
    private static readonly Regex OtherError = new(@"(?:^|: )(?:fatal )?error: (?<message>.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// SwiftPM's echo of a compiler job that reported errors and exited 1 (<c>error: emit-module command failed with exit
    /// code 1 (use -v to see invocation)</c>, Swift 6.3.2): it says no more than the located errors beside it. A job that
    /// failed otherwise (a signal, another exit code) is still an error of its own.
    /// </summary>
    private static readonly Regex FailedJob = new(@"^error: [a-z-]+ command failed with exit code 1 \(use -v to see invocation\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A located error that ends in a rule tag (<c>… (line_length)</c>): what a build-tool plugin such as SwiftLint prints
    /// in swiftc's format. It is not the compiler's, so it says nothing about whether a new test builds on the base: it
    /// keeps its file but takes the code <see cref="PluginCode"/>, which is unattributable.
    /// </summary>
    private static readonly Regex RuleTag = new(@" \([a-z][a-z0-9_]*\)$", RegexOptions.CultureInvariant);

    /// <summary>The code of an error a build-tool plugin reported (<see cref="RuleTag"/>).</summary>
    public const string PluginCode = "plugin";

    /// <summary>
    /// The errors in <c>swift build</c>'s output: each line <c>/abs/path:line:col: error: message</c> (the file made
    /// relative to one of <paramref name="roots"/>, the worktree, also when the compiler prints the root's resolved path —
    /// <c>/private/var/…</c> for <c>/var/…</c>; outside it, no path; code <c>swiftc</c>, or <see cref="PluginCode"/> when
    /// it ends in a rule tag) and every other line reporting an error (<see cref="OtherError"/>: no file) but SwiftPM's echo
    /// of a failed compiler job (<see cref="FailedJob"/>). The source excerpt the compiler prints under an error is not
    /// read again. Why this text and not another format: SwiftPM's <c>swift build</c> has no machine-readable diagnostics
    /// on a stable toolchain (swiftc's <c>-serialize-diagnostics</c> is clang's binary bitstream, and would add a flag to
    /// every build), while the <c>file:line:col: error:</c> header line is the same in both of swiftc's diagnostic styles
    /// (<c>swift</c>, the default since 5.9, and <c>llvm</c>); a build that fails without one names no error, which is
    /// unattributable.
    /// </summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots)
    {
        var errors = new List<BuildError>();
        foreach (var line in log.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (LocatedError.Match(line) is { Success: true } located)
            {
                var message = located.Groups["message"].Value;
                errors.Add(new BuildError(RelativeTo(located.Groups["file"].Value, roots), int.Parse(located.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(located.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture), RuleTag.IsMatch(message) ? PluginCode : "swiftc", message));
            }
            else if (OtherError.Match(line) is { Success: true } other && !FailedJob.IsMatch(line))
            {
                errors.Add(new BuildError(null, 0, 0, "swiftc", line.StartsWith("error: ", StringComparison.Ordinal) ? other.Groups["message"].Value : line.Trim()));
            }
        }
        return errors.Distinct().ToList();
    }

    private static string? RelativeTo(string file, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(file);
        var files = new[] { full, Resolved(full) }.Distinct(StringComparer.Ordinal).ToList();
        foreach (var root in roots)
        {
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            foreach (var prefix in new[] { rootFull, Resolved(rootFull) }.Distinct(StringComparer.Ordinal).Select(r => r + Path.DirectorySeparatorChar))
            {
                if (files.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal)) is { } match)
                {
                    return match[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
                }
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link among its existing directories resolved (<c>/var</c> is
    /// <c>/private/var</c> on macOS, as swiftc prints it); the part that does not exist is kept as it is.
    /// </summary>
    internal static string Resolved(string path) => Resolved(path, 0);

    private static string Resolved(string path, int links)
    {
        var parts = Path.TrimEndingDirectorySeparator(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetPathRoot(path) ?? "/";
        for (var n = 0; n < parts.Length; n++)
        {
            var next = Path.Combine(current, parts[n]);
            var rest = parts[(n + 1)..];
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists || links >= 40)
                {
                    return Path.Combine([next, .. rest]);
                }
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target may itself lie under a link (`/var/folders/…` is `/private/var/folders/…`).
                    return Resolved(Path.Combine([target.FullName, .. rest]), links + 1);
                }
                current = next;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Path.Combine([next, .. rest]);
            }
        }
        return current;
    }

    /// <summary>
    /// A base that does not build with the PR's test files (sc-26128), as <see cref="CargoNewTests"/> explains rustc's
    /// errors. Each error is placed, by <see cref="SwiftSource.Layout"/> on the applied file as it was built, in the
    /// innermost declaration at its position (its unit). Every file the overlay applies is the PR's test code as a whole
    /// (a changed file under a package's <c>Tests/</c>, from the head), so an error says something about the new tests
    /// only there: an error in any other file (the base's production code, a manifest, an unchanged test file, a file
    /// SwiftPM generated), in no file, or a build that names no error, is unattributable. A new test whose own function is
    /// a unit, or whose type's header (<c>extension Missing</c>, <c>struct S: NewProtocol</c>) is, does not build on the
    /// base. The retry's files leave every outermost unit out: a helper, another declaration or an <c>import</c> (a module
    /// the base lacks) that did not compile, so the tests that do not need it get their real results, and a test that does
    /// is named by the next stage. An error outside every declaration, between a type's members, or on a
    /// conditional-compilation line (nothing a test lacks on the base shows there) is unremovable, and so is one in the
    /// header of a type an earlier retry took something out of (<see cref="SwiftNewTestSet.Originals"/>: the overlay's own
    /// doing). An ambiguity or redeclaration (a clash with a declaration the base has) and a build-tool plugin's error are
    /// unattributable wherever they are.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var swift = set as SwiftNewTestSet;
        var unattributable = new List<string>();
        var unremovable = new List<string>();
        var files = new SortedDictionary<string, (SwiftLayout Layout, HashSet<SwiftItem> Units)>(StringComparer.Ordinal);
        if (errors.Count == 0)
        {
            unattributable.Add("the build failed without naming a compiler error (see the build's output: a manifest, dependency or linker error, or no swiftc)");
        }
        foreach (var error in errors)
        {
            // An ambiguity or a redeclaration is a clash with something the base HAS (its production code declares the name
            // too): it says nothing about what a new test lacks there. A plugin's error is not the compiler's.
            if (swift is null || error.Path is not { } path || !Understands(path) || IsManifest(path) || !applied.TryGetValue(path, out var text)
                || error.Code != "swiftc" || BaseHasIt.IsMatch(error.Message))
            {
                unattributable.Add(error.ToString());
                continue;
            }
            if (!files.TryGetValue(path, out var file))
            {
                try
                {
                    files[path] = file = (SwiftSource.Layout(text), new HashSet<SwiftItem>(ReferenceEqualityComparer.Instance));
                }
                catch (FormatException)
                {
                    unattributable.Add(error.ToString());
                    continue;
                }
            }
            var position = file.Layout.Position(error.Line, error.Column);
            var chain = position < 0 ? [] : file.Layout.ItemsAt(position);
            var last = chain.Count > 0 ? chain[^1] : null;
            if (position < 0)
            {
                unattributable.Add(error.ToString());
            }
            else if (last is null || last.IsDirective || (last.BodyOpen >= 0 && position >= last.BodyOpen))
            {
                unremovable.Add(error.ToString());
            }
            else if (last.IsType && (!swift.Originals.TryGetValue(path, out var original) || ChangedTypes(original, file.Layout).Contains(last.Name)))
            {
                // A header error (`does not conform to protocol`) in a type an earlier stage took members out of: the
                // overlay's own doing, not something the base lacks. Nor can a header be judged in a file whose head
                // version is not known.
                unremovable.Add($"{error} (in the header of {last.Keyword} {last.Name}, which an earlier retry took something out of: the overlay's own doing)");
            }
            else
            {
                file.Units.Add(last);
            }
        }

        var notBuilt = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, (layout, units)) in files.Where(f => f.Value.Units.Count > 0))
        {
            SwiftFile parsed;
            try
            {
                parsed = SwiftSource.Parse(layout.Text);
            }
            catch (FormatException e)
            {
                // Laid out but its tests cannot be found again: nothing is said about them.
                unattributable.Add($"{path} cannot be read as Swift source ({e.Message}), so its errors cannot be placed in its tests");
                continue;
            }
            // Every function, not only @Test ones: an XCTest test's location is its method (sc-26129).
            foreach (var func in parsed.Funcs)
            {
                if (layout.ItemsAt(func.Start).Any(units.Contains))
                {
                    notBuilt.UnionWith(tests.Where(id => swift!.Locations.TryGetValue(id, out var at) && at == (path, Key(func))));
                }
            }
            var outermost = units.Where(u => !layout.ItemsAt(u.Start).TakeWhile(i => !ReferenceEquals(i, u)).Any(units.Contains)).OrderByDescending(u => u.Start).ToList();
            var rewritten = new System.Text.StringBuilder(layout.Text);
            foreach (var unit in outermost)
            {
                rewritten.Remove(unit.Start, unit.End - unit.Start);
            }
            removed.AddRange(outermost.AsEnumerable().Reverse().Select(u => $"{path}:{layout.LineOf(u.Start)} {Describe(layout, u)}"));
            replacements[path] = rewritten.ToString();
        }
        return new BuildFailure(tests.Where(notBuilt.Contains).ToList(), unattributable, unremovable, removed, replacements);
    }

    /// <summary>An error message that names a clash with a declaration the base has (<c>ambiguous use of</c>, <c>invalid redeclaration of</c>).</summary>
    private static readonly Regex BaseHasIt = new(@"\bambiguous\b|^invalid redeclaration\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The names of the types (and extensions) whose body differs between the head's version of a file and the applied
    /// one, or that the applied one no longer has: the types an earlier retry took something out of. Matched by the chain
    /// of names from the top level; an original that cannot be laid out counts every type of the applied file as changed.
    /// </summary>
    private static HashSet<string> ChangedTypes(string original, SwiftLayout applied)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (original == applied.Text)
        {
            return changed;
        }
        static IEnumerable<(string Chain, SwiftItem Item, string Body)> Types(SwiftLayout layout, IReadOnlyList<SwiftItem> items, string chain) =>
            items.Where(i => i.IsType && i.BodyOpen >= 0).SelectMany(i => Types(layout, i.Children, $"{chain}/{i.Keyword} {i.Name}")
                .Prepend(($"{chain}/{i.Keyword} {i.Name}", i, layout.Text[i.BodyOpen..i.End])));
        SwiftLayout head;
        try
        {
            head = SwiftSource.Layout(original);
        }
        catch (FormatException)
        {
            changed.UnionWith(Types(applied, applied.Items, "").Select(t => t.Item.Name));
            return changed;
        }
        var now = Types(applied, applied.Items, "").ToLookup(t => t.Chain, t => t.Body, StringComparer.Ordinal);
        foreach (var (chain, item, body) in Types(head, head.Items, ""))
        {
            if (!now[chain].Contains(body, StringComparer.Ordinal))
            {
                changed.Add(item.Name);
            }
        }
        return changed;
    }

    private static string Describe(SwiftLayout layout, SwiftItem item) =>
        item.Keyword.Length > 0 && item.Name.Length > 0 ? $"{item.Keyword} {item.Name}"
        : Regex.Replace(layout.Text[item.Start..item.End], @"\s+", " ") is var text && text.Length > 60 ? text[..60] + "…" : text;
}
