using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DarkFactory.Orchestrator.Gate;

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
/// Unisolable (the check is <c>unsupported</c>, never a pass), so later stories have a clean seam: a parameterized test
/// (<c>@Test(arguments:)</c>, or a test function with parameters); a new XCTest method (<c>func test…()</c> in a file that
/// imports XCTest); a new test, or a changed file that looks like a test (<see cref="NewTestsCheck.LooksLikeTest"/>),
/// outside a SwiftPM test target (an Xcode project, <c>Sources/</c>, a target the manifest does not declare at its default
/// path, or a package with a version-specific manifest <c>Package@swift-*.swift</c>); a test target the base's manifest
/// does not declare; a test id declared twice (conditional compilation); a backticked test name; and a changed file that
/// may hold tests but cannot be read.
/// </para>
/// <para>
/// Runs, from the worktree root per package holding a new test: <c>swift --version</c> (a toolchain probe: failing, the
/// run is <see cref="TestRunStatus.ToolchainMissing"/>), <c>/bin/mkdir -p</c> of the run's results directory (SwiftPM
/// writes no report into a directory that does not exist), <c>swift package resolve</c> (the restore: the only step that
/// may use the network), <c>swift build --build-tests</c> (the build, its standard output kept as the build log), then
/// one <c>swift test --skip-build --disable-xctest --filter ^&lt;id&gt;(/|$) --xunit-output …</c> per new test (Swift
/// Testing matches the filter against the test's id followed by its source location), each reading only its own report
/// and keeping its exit code, which the report must agree with. Results: Swift Testing's xUnit XML
/// report (<see cref="ParseResults"/>). A base that does not build with the PR's test files is not attributed to the new
/// tests yet: it is an <c>error</c>, never a pass.
/// </para>
/// </summary>
public sealed class SwiftNewTests : INewTestStrategy
{
    public string Name => "swift-testing";

    /// <summary>The program every step but the results directory's runs, found on the test runs' PATH (the launch helper's fixed one when sandboxed).</summary>
    public const string Swift = "swift";

    /// <summary>The program that makes the results directory.</summary>
    public const string MakeDirectory = "/bin/mkdir";

    private const string Manifest = "Package.swift";

    /// <summary>The prefix of each test step's report under the results directory.</summary>
    public const string ReportPrefix = "swift-test-";

    /// <summary>
    /// The one report a test's step asks for and reads: <c>swift-test-&lt;the first 16 hex digits of the id's SHA-256&gt;.xml</c>,
    /// so <see cref="ParseResults"/> knows from a report's name which test it may speak of.
    /// </summary>
    public static string ReportName(string id) =>
        $"{ReportPrefix}{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16]}.xml";

    /// <summary>The name a test step's standard output and exit code are kept under (<see cref="TestStep.CaptureOutput"/>), beside its report.</summary>
    public static string OutputName(string id) => ReportName(id)[..^".xml".Length] + ".out";

    private static readonly Regex ReportFileName = new(@"^swift-test-[0-9a-f]{16}\.xml$", RegexOptions.CultureInvariant);

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
                return new Location(null, null, false, "it is in no SwiftPM package (no Package.swift above it; Xcode projects are not supported)");
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
    private static string Key(SwiftFunc func) => string.Join("/", func.Types.Append(func.Signature));

    // ---- Finding the new tests ---------------------------------------------------------------------------------------------

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        if (!changes.Any(c => c.Paths.Any(p => Understands(p) && !IsManifest(p))))
        {
            return NewTestSet.None;
        }
        var (head, @base) = (new Tree(source, source.HeadSha), new Tree(source, source.BaseSha));
        var problems = new List<string>();

        // The base's tests (both kinds) of the changed files, by id.
        var baseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in changes.Select(c => c.OldPath).OfType<string>().Where(p => Understands(p) && !IsManifest(p)))
        {
            if (await @base.ParseAsync(old, ct) is { } file && await @base.LocateAsync(old, ct) is { Target: { } target })
            {
                baseIds.UnionWith(file.Tests.Concat(file.XCTestMethods).Select(f => Id(target, f)));
            }
        }

        var news = new SortedDictionary<string, Location>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new SortedSet<string>(StringComparer.Ordinal);
        var testFiles = new List<ChangedFile>();
        foreach (var change in changes)
        {
            // Every changed file under a package's Tests/ is the PR's test code: the base run applies it.
            var inTests = false;
            foreach (var (tree, at) in new[] { (head, change.NewPath), (@base, change.OldPath) })
            {
                inTests |= at is not null && (await tree.LocateAsync(at, ct)).InTests;
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
            var tests = file.Tests.Concat(file.XCTestMethods).ToList();
            if (place.Target is not { } target)
            {
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
            foreach (var test in tests)
            {
                var id = Id(target, test);
                if (!seen.Add(id))
                {
                    twice.Add(id);
                }
                if (baseIds.Contains(id))
                {
                    continue;
                }
                if (!test.IsTest)
                {
                    problems.Add($"{id} in {path} is a new XCTest test method: the gate runs Swift Testing's @Test tests only so far");
                }
                else if (test.IsParameterized)
                {
                    problems.Add($"{id} in {path} is a parameterized test (@Test(arguments:), or a test function with parameters): the gate cannot run it apart yet");
                }
                else if (test.Name.Contains('`'))
                {
                    problems.Add($"{id} in {path} has a backticked name, which the gate cannot name to Swift Testing's filter");
                }
                else
                {
                    news[id] = place;
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

        return new NewTestSet(testFiles, news.Keys.ToList(), problems)
        {
            Projects = news.ToDictionary(kv => kv.Key, kv => kv.Value.Package!.ManifestPath, StringComparer.Ordinal),
        };
    }

    private const string Undeclared = "does not declare (as testTarget(name: \"…\") with a plain string) the test target";

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
    /// <c>swift --version</c>; <c>/bin/mkdir -p</c> of the results directory; per package <c>swift package resolve</c> (the
    /// only step that may use the network) and <c>swift build --build-tests</c> (output kept as the build log); then per new
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
        steps.AddRange(byPackage.Select((g, n) => new TestStep(TestPhase.Build, Swift, ["build", "--package-path", g.Key, "--build-tests"]) { CaptureOutput = $"swift-build-{n}" }));
        foreach (var id in byPackage.SelectMany(g => g.Select(id => (Id: id, Package: g.Key))))
        {
            // Its own report only (a pattern naming exactly it), and its exit code, which the report must agree with.
            steps.Add(new TestStep(TestPhase.Test, Swift,
            [
                "test", "--package-path", id.Package, "--skip-build", "--disable-xctest", "--filter", Filter(id.Id),
                "--xunit-output", $"{resultsDirectory}/{ReportName(id.Id)}",
            ]) { ResultFilePattern = ReportName(id.Id), CaptureOutput = OutputName(id.Id) });
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
    /// step whose report is missing is unreadable too.
    /// </summary>
    public TestResults ParseResults(IEnumerable<ResultFile> resultFiles)
    {
        var results = new Dictionary<string, List<TestCaseResult>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var all = resultFiles.ToList();
        var exits = all.Where(f => f.ExitCode is not null).ToDictionary(f => f.Name, f => f.ExitCode!.Value, StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, content) in all.Where(f => f.ExitCode is null))
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
                    if ((suiteCases.Count != tests && suiteCases.Count != tests + skipped) || outcomes.Count(o => o == TestCaseResult.Failed) != failures + errors
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
                        var id = Id(types[0], types[1..], function);
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

    private static string Outcome(XElement testcase)
    {
        var children = testcase.Elements().Select(e => e.Name.LocalName).ToList();
        return children.Any(c => c is "failure" or "error") ? TestCaseResult.Failed
            : children.Contains("skipped") ? TestCaseResult.Skipped
            : TestCaseResult.Passed;
    }

    /// <summary>A located compiler error: <c>/abs/File.swift:3:28: error: message</c>.</summary>
    private static readonly Regex LocatedError = new(@"^(?<file>/.+?):(?<line>\d{1,9}):(?<col>\d{1,9}): error: (?<message>.*)$", RegexOptions.CultureInvariant);

    private static readonly Regex UnlocatedError = new(@"^error: (?<message>.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The errors in <c>swift build</c>'s output: each line <c>/abs/path:line:col: error: message</c> (the file made
    /// relative to one of <paramref name="roots"/>, the worktree; outside it, no path) and each <c>error: message</c> line
    /// (no file); code <c>swiftc</c>. The source excerpt the compiler prints under an error is not read again.
    /// </summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots)
    {
        var errors = new List<BuildError>();
        foreach (var line in log.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (LocatedError.Match(line) is { Success: true } located)
            {
                errors.Add(new BuildError(RelativeTo(located.Groups["file"].Value, roots), int.Parse(located.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(located.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture), "swiftc", located.Groups["message"].Value));
            }
            else if (UnlocatedError.Match(line) is { Success: true } bare)
            {
                errors.Add(new BuildError(null, 0, 0, "swiftc", bare.Groups["message"].Value));
            }
        }
        return errors.Distinct().ToList();
    }

    private static string? RelativeTo(string file, IReadOnlyList<string> roots)
    {
        var full = Path.GetFullPath(file);
        foreach (var root in roots)
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.Ordinal))
            {
                return full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        return null;
    }

    /// <summary>
    /// Not yet (a later story): a base that does not build with the PR's test files says nothing here about which new test
    /// cannot build there, so every error is unattributable and the check is an <c>error</c>, never a pass.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var note = "a Swift base that does not build with the PR's test files is not attributed to the new tests yet";
        IReadOnlyList<string> unattributable = errors.Count == 0 ? [$"the build failed and named no error ({note})"]
            : [$"{note}:", .. errors.Select(e => e.ToString())];
        return new BuildFailure([], unattributable, [], [], new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
