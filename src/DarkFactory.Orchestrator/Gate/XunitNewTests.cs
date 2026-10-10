using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// .NET/xUnit (sc-25382). Test files: every changed file under the directory of a <c>*.csproj</c> that references an
/// <c>xunit</c> package (at the head for new paths, at the base for old ones). New tests: the <c>[Fact]</c>/<c>[Theory]</c>
/// methods (any attribute named <c>…Fact</c>/<c>…Theory</c>) in the head versions of those files whose id —
/// <c>Namespace.Class.Method</c>, nested classes joined with <c>+</c>, as xUnit names them — no base version of a changed
/// test file has. Parsed with Roslyn's syntax tree (no build, nothing executed). New data rows on an existing theory are
/// unisolable (its old rows would pass on the base) and test methods outside an xUnit project are another stack's: both
/// make the check unsupported. Runs, per test project holding a new test (no solution file involved, so a new test
/// project runs on the base too): <c>dotnet restore</c>, <c>dotnet build --no-restore</c> (errors also to an errors-only
/// file log), then <c>dotnet test --no-build</c> on just its new tests — <c>--filter-method</c> per test and xUnit's own
/// XML report under Microsoft.Testing.Platform (<c>global.json</c> <c>test.runner</c>), else VSTest's <c>--filter
/// FullyQualifiedName=…</c> and TRX logger; results from those reports (cases joined to their test by class and method
/// name, never by display name). A failed base build is explained from the error log (<see cref="ExplainBuildFailure"/>).
/// </summary>
public sealed class XunitNewTests : INewTestStrategy
{
    public string Name => "dotnet-xunit";

    /// <summary>The name of xUnit's XML report under Microsoft.Testing.Platform (one per project, in its own directory).</summary>
    public const string XunitReportFileName = "results.xunit.xml";

    /// <summary>The reports read: xUnit's XML report under Microsoft.Testing.Platform, the TRX logger's under VSTest (never both).</summary>
    private const string XunitReportPattern = "*.xunit.xml", TrxPattern = "*.trx";

    public bool Understands(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex XunitReference = new(@"<PackageReference\s+Include\s*=\s*""xunit(\.[A-Za-z0-9.]+)?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Test attributes of other .NET frameworks (NUnit, MSTest): a method carrying one outside an xUnit project is not runnable here.</summary>
    private static readonly HashSet<string> OtherFrameworkAttributes = new(StringComparer.Ordinal) { "Test", "TestCase", "TestMethod", "DataTestMethod" };

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        var headProjectFiles = await TestProjectsAsync(source, source.HeadSha, ct);
        var headProjects = headProjectFiles.Select(p => p.Directory).ToList();
        var baseProjects = (await TestProjectsAsync(source, source.BaseSha, ct)).Select(p => p.Directory).ToList();
        var testFiles = changes.Where(c => (c.NewPath is { } n && Under(n, headProjects)) || (c.OldPath is { } o && Under(o, baseProjects))).ToList();

        var baseTests = new Dictionary<string, TestMethod>(StringComparer.Ordinal);
        var headTests = new Dictionary<string, TestMethod>(StringComparer.Ordinal);
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        var unisolable = new List<string>();
        foreach (var change in changes)
        {
            if (testFiles.Contains(change))
            {
                foreach (var path in change.Paths.Where(Understands))
                {
                    foreach (var method in TestMethods(await source.ReadAsync(source.BaseSha, path, ct)))
                    {
                        baseTests.TryAdd(method.Id, method);
                    }
                }
                if (change.NewPath is { } head && Understands(head))
                {
                    // The test's project: the deepest test project directory holding the file.
                    var project = headProjectFiles.Where(p => Under(head, [p.Directory])).OrderByDescending(p => p.Directory.Length).Select(p => p.Project).FirstOrDefault();
                    foreach (var method in TestMethods(await source.ReadAsync(source.HeadSha, head, ct)))
                    {
                        if (project is null)
                        {
                            unisolable.Add($"{head} has test method {method.Id} outside an xUnit test project");
                        }
                        else if (headTests.TryAdd(method.Id, method))
                        {
                            projects[method.Id] = project;
                        }
                    }
                }
            }
            else if (change.NewPath is { } path && Understands(path)
                && TestMethods(await source.ReadAsync(source.HeadSha, path, ct), OtherFrameworkAttributes).FirstOrDefault() is { } stray)
            {
                unisolable.Add($"{path} has test method {stray.Id} outside an xUnit test project");
            }
        }

        var added = headTests.Keys.Where(id => !baseTests.ContainsKey(id)).ToList();
        foreach (var (id, head) in headTests)
        {
            if (head.IsTheory && baseTests.TryGetValue(id, out var before) && head.DataRows.Except(before.DataRows).Any())
            {
                unisolable.Add($"new data rows on the existing theory {id} cannot be run apart from its old rows (add them as a new test method)");
            }
        }
        return new NewTestSet(testFiles, added, unisolable) { Projects = added.ToDictionary(id => id, id => projects[id], StringComparer.Ordinal) };
    }

    private static bool Under(string path, IReadOnlyList<string> directories) =>
        directories.Any(d => d.Length == 0 || path.StartsWith(d + "/", StringComparison.Ordinal));

    /// <summary>The commit's projects that reference an xUnit package, with their directories.</summary>
    private static async Task<IReadOnlyList<(string Project, string Directory)>> TestProjectsAsync(TestSource source, string sha, CancellationToken ct)
    {
        var projects = new List<(string, string)>();
        foreach (var project in (await source.FilesAsync(sha, ct)).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (await source.ReadAsync(sha, project, ct) is { } text && XunitReference.IsMatch(text))
            {
                var slash = project.LastIndexOf('/');
                projects.Add((project, slash < 0 ? "" : project[..slash]));
            }
        }
        return projects;
    }

    /// <summary>A test method: its xUnit id, whether it is a theory, and its data attributes (whitespace-normalized source).</summary>
    public sealed record TestMethod(string Id, bool IsTheory, IReadOnlyList<string> DataRows);

    private static readonly HashSet<string> NoAttributes = [];

    /// <summary>
    /// The test methods in C# source: methods with an attribute named <c>Fact</c>/<c>Theory</c> (or ending so, e.g.
    /// <c>SkippableFact</c>; an <c>Attribute</c> suffix and namespace qualification ignored), or with one of <paramref name="also"/>.
    /// </summary>
    public static IReadOnlyList<TestMethod> TestMethods(string? source, IReadOnlySet<string>? also = null)
    {
        return source is null ? [] : TestMethodNodes(CSharpSyntaxTree.ParseText(source).GetRoot(), also).Select(m => m.Method).ToList();
    }

    private static List<(TestMethod Method, MethodDeclarationSyntax Syntax)> TestMethodNodes(SyntaxNode root, IReadOnlySet<string>? also = null)
    {
        var extra = also ?? NoAttributes;
        var methods = new List<(TestMethod, MethodDeclarationSyntax)>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(l => l.Attributes).Select(a => (Name: AttributeName(a), Syntax: a)).ToList();
            var isFact = attributes.Any(a => a.Name.EndsWith("Fact", StringComparison.Ordinal));
            var isTheory = attributes.Any(a => a.Name.EndsWith("Theory", StringComparison.Ordinal));
            if (!isFact && !isTheory && !attributes.Any(a => extra.Contains(a.Name)))
            {
                continue;
            }
            if (TypeName(method) is not { } type)
            {
                continue;
            }
            var rows = attributes.Where(a => a.Name.EndsWith("Data", StringComparison.Ordinal))
                .Select(a => Regex.Replace(a.Syntax.ToString(), @"\s+", "")).ToList();
            methods.Add((new TestMethod($"{type}.{method.Identifier.Text}", isTheory, rows), method));
        }
        return methods;
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length ? name[..^"Attribute".Length] : name;
    }

    /// <summary>The containing type as xUnit names it: <c>Namespace.Outer+Inner</c> (a generic type with its arity, <c>Name`1</c>).</summary>
    private static string? TypeName(MethodDeclarationSyntax method)
    {
        var types = method.Ancestors().OfType<TypeDeclarationSyntax>().ToList();
        if (types.Count == 0)
        {
            return null;
        }
        var nested = string.Join('+', types.AsEnumerable().Reverse()
            .Select(t => t.Identifier.Text + (t.TypeParameterList is { Parameters.Count: > 0 } p ? $"`{p.Parameters.Count}" : "")));
        var namespaces = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).ToList();
        return namespaces.Count == 0 ? nested : $"{string.Join('.', namespaces)}.{nested}";
    }

    public string BuildLogPattern => "build-errors-*.log";

    /// <summary>
    /// Per project holding a new test (so a new test project needs no solution entry, and no other project is built):
    /// <c>dotnet restore</c>, <c>dotnet build --no-restore</c> with an errors-only file log
    /// (<c>build-errors-&lt;n&gt;.log</c>), then <c>dotnet test --no-build</c> on just its new tests, results in
    /// <c>&lt;n&gt;/</c> of <paramref name="resultsDirectory"/>.
    /// </summary>
    public async Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct)
    {
        var platform = UsesTestingPlatform(await source.ReadAsync(commit, "global.json", ct));
        var byProject = tests.GroupBy(id => projects.TryGetValue(id, out var p) ? p
                : throw new InvalidOperationException($"The new test {id} has no project."), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var steps = new List<TestStep>();
        steps.AddRange(byProject.Select(g => new TestStep(TestPhase.Restore, "dotnet", ["restore", ProjectArgument(g.Key)])));
        steps.AddRange(byProject.Select((g, n) => new TestStep(TestPhase.Build, "dotnet",
            ["build", "--no-restore", ProjectArgument(g.Key), $"-flp:errorsonly;logfile={resultsDirectory}/build-errors-{n}.log"])));
        foreach (var (group, n) in byProject.Select((g, n) => (g, n)))
        {
            List<string> Test(string? report)
            {
                List<string> test = report is not null ? ["test", "--project", ProjectArgument(group.Key)] : ["test", ProjectArgument(group.Key)];
                test.AddRange(["--no-build", "--results-directory", $"{resultsDirectory}/{n}"]);
                if (report is not null)
                {
                    test.AddRange([report, $"{report}-filename", XunitReportFileName]);
                    foreach (var id in group)
                    {
                        test.Add("--filter-method");
                        test.Add(id);
                    }
                }
                else
                {
                    test.AddRange(["--logger", "trx", "--filter", string.Join('|', group.Select(id => $"FullyQualifiedName={id}"))]);
                }
                return test;
            }
            // Under the testing platform, xUnit's own XML report, never its TRX: since xunit.v3 4.0 the TRX (and JUnit,
            // NUnit, HTML) writer puts a failure message's text into XML as it is, so an unpaired UTF-16 surrogate (e.g. a
            // string reverse that splits a pair) aborts it and leaves an empty file; the XML report escapes such characters
            // (\xNNNN). Its option is --report-xunit-xml since 4.0 and --report-xunit before, so an older xunit.v3, which
            // rejects the new name before running anything, gets the old one.
            steps.Add(platform
                ? new TestStep(TestPhase.Test, "dotnet", Test("--report-xunit-xml"))
                {
                    Fallback = new TestStep(TestPhase.Test, "dotnet", Test("--report-xunit")), ResultFilePattern = XunitReportPattern,
                }
                : new TestStep(TestPhase.Test, "dotnet", Test(null)) { ResultFilePattern = TrxPattern });
        }
        return steps;
    }

    /// <summary>A project path as a command argument: relative to the worktree root, never read as an option.</summary>
    private static string ProjectArgument(string project) => $"./{project}";

    /// <summary>Whether <c>global.json</c> opts <c>dotnet test</c> into Microsoft.Testing.Platform (<c>"test": {"runner": …}</c>).</summary>
    public static bool UsesTestingPlatform(string? globalJson)
    {
        if (globalJson is null)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(globalJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Each test's cases from the run's reports (each format only from its own file name: <c>*.trx</c> TRX, else xUnit XML): xUnit's XML report (<c>&lt;assemblies&gt;</c>: each <c>test</c> names its
    /// class and method by <c>type</c> and <c>method</c>, which come from the test's reflected method, not its display name,
    /// and its <c>result</c>) or TRX (<c>UnitTest</c> definitions name the class and method, <c>TestMethod className</c>
    /// and <c>name</c>; <c>UnitTestResult</c>s carry the case's display name and outcome, joined by test id). A file that
    /// cannot be read — empty (a report writer that failed), not well-formed XML, not its name's format, or an xUnit result it
    /// does not know — contributes nothing and is named in <see cref="TestResults.Unreadable"/>.
    /// </summary>
    public TestResults ParseResults(IEnumerable<ResultFile> resultFiles)
    {
        var results = new Dictionary<string, List<TestCaseResult>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var (name, file) in resultFiles)
        {
            List<(string Test, TestCaseResult Case)> cases;
            try
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    throw new InvalidDataException("it is empty (the test runner could not write it)");
                }
                using var reader = XmlReader.Create(new StringReader(file),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
                var trx = name.EndsWith(".trx", StringComparison.OrdinalIgnoreCase);
                cases = reader.MoveToContent() == XmlNodeType.Element ? (reader.LocalName, trx) switch
                {
                    ("assemblies", false) => ReadXunitXml(reader),
                    ("TestRun", true) => ReadTrx(reader),
                    (var root, _) => throw new InvalidDataException($"it is not {(trx ? "a TRX" : "an xUnit XML")} report (root element <{root}>)"),
                } : throw new InvalidDataException("it has no root element");
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
        return new TestResults(results.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TestCaseResult>)kv.Value, StringComparer.Ordinal), unreadable);
    }

    /// <summary>xUnit's XML report (v2+ format): every <c>test</c> element, by its <c>type</c> and <c>method</c>.</summary>
    private static List<(string, TestCaseResult)> ReadXunitXml(XmlReader reader)
    {
        var cases = new List<(string, TestCaseResult)>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "test")
            {
                continue;
            }
            if (reader.GetAttribute("type") is not { Length: > 0 } type || reader.GetAttribute("method") is not { Length: > 0 } method)
            {
                throw new InvalidDataException("a test result names no type and method");
            }
            var outcome = reader.GetAttribute("result") switch
            {
                "Pass" => TestCaseResult.Passed,
                "Fail" => TestCaseResult.Failed,
                "Skip" or "NotRun" => TestCaseResult.Skipped,
                var other => throw new InvalidDataException($"test {type}.{method} has the unknown result '{other}'"),
            };
            cases.Add(($"{type}.{method}", new TestCaseResult(reader.GetAttribute("name") ?? "", outcome)));
        }
        return cases;
    }

    /// <summary>A TRX file: <c>UnitTestResult</c>s joined to their <c>UnitTest</c> definition's class and method by test id.</summary>
    private static List<(string, TestCaseResult)> ReadTrx(XmlReader reader)
    {
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        var outcomes = new List<(string TestId, string Name, string Outcome)>();
        string? unitTestId = null;
        do
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            switch (reader.LocalName)
            {
                case "UnitTestResult" when reader.GetAttribute("testId") is { } id:
                    outcomes.Add((id, reader.GetAttribute("testName") ?? "", Outcome(reader.GetAttribute("outcome"))));
                    break;
                case "UnitTest":
                    unitTestId = reader.GetAttribute("id");
                    break;
                case "TestMethod" when unitTestId is not null && reader.GetAttribute("className") is { } cls && reader.GetAttribute("name") is { } method:
                    definitions[unitTestId] = $"{cls}.{method}";
                    break;
            }
        }
        while (reader.Read());
        return outcomes.Where(o => definitions.ContainsKey(o.TestId)).Select(o => (definitions[o.TestId], new TestCaseResult(o.Name, o.Outcome))).ToList();
    }

    private static string Outcome(string? trx) => trx switch
    {
        "Passed" => TestCaseResult.Passed,
        "NotExecuted" or "Inconclusive" or "Pending" => TestCaseResult.Skipped,
        _ => TestCaseResult.Failed,
    };

    /// <summary>An MSBuild error line: <c>[n&gt;]file(line,col[,endLine,endCol]): error CODE: message [project]</c>.</summary>
    private static readonly Regex LocatedError = new(
        @"^\s*(?:\d+>)?(?<file>\S.*?)\((?<line>\d{1,9}),(?<col>\d{1,9})(?:,\d{1,9},\d{1,9})?\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex UnlocatedError = new(@"error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The errors of an errors-only MSBuild file log (<c>-flp:errorsonly</c>): one per non-empty line. A line that does not
    /// name a file inside one of <paramref name="roots"/> becomes an error with no path (it then attributes nothing).
    /// </summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots)
    {
        var errors = new List<BuildError>();
        foreach (var line in log.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0))
        {
            if (LocatedError.Match(line) is { Success: true } located)
            {
                errors.Add(new BuildError(Relative(located.Groups["file"].Value, roots), int.Parse(located.Groups["line"].Value),
                    int.Parse(located.Groups["col"].Value), located.Groups["code"].Value, located.Groups["message"].Value));
            }
            else
            {
                var bare = UnlocatedError.Match(line);
                errors.Add(new BuildError(null, 0, 0, bare.Success ? bare.Groups["code"].Value : "", bare.Success ? bare.Groups["message"].Value : line.Trim()));
            }
        }
        return errors.Distinct().ToList();
    }

    /// <summary><paramref name="file"/> relative to the first root it is under ('/'-separated), else null.</summary>
    private static string? Relative(string file, IReadOnlyList<string> roots)
    {
        if (!Path.IsPathRooted(file))
        {
            return null;
        }
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

    private static readonly Regex CompilerCode = new(@"^CS\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Each compiler (<c>CS</c>) error in an applied <c>.cs</c> file is placed, by Roslyn syntax tree of that file as it was
    /// built, in its innermost member declaration or <c>using</c> directive (its unit). A new test whose own method, or a
    /// type that contains it, is a unit does not build on the base. The retry's files leave every outermost unit out. An
    /// error outside any unit (e.g. an assembly attribute) is unremovable; any other error (another code, another file, no
    /// file, a position outside the file) is unattributable; a build that names no error at all is too.
    /// </summary>
    public BuildFailure ExplainBuildFailure(IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var unattributable = new List<string>();
        var unremovable = new List<string>();
        var files = new SortedDictionary<string, (SyntaxNode Root, HashSet<SyntaxNode> Units)>(StringComparer.Ordinal);
        if (errors.Count == 0)
        {
            unattributable.Add("the build failed without naming an error");
        }
        foreach (var error in errors)
        {
            if (error.Path is not { } path || !CompilerCode.IsMatch(error.Code) || !Understands(path) || !applied.TryGetValue(path, out var text))
            {
                unattributable.Add(error.ToString());
                continue;
            }
            if (!files.TryGetValue(path, out var file))
            {
                files[path] = file = (CSharpSyntaxTree.ParseText(text).GetRoot(), new HashSet<SyntaxNode>());
            }
            var lines = file.Root.SyntaxTree.GetText().Lines;
            if (error.Line < 1 || error.Line > lines.Count)
            {
                unattributable.Add(error.ToString());
                continue;
            }
            var line = lines[error.Line - 1];
            var position = Math.Min(line.Start + Math.Max(error.Column - 1, 0), Math.Max(line.End - 1, line.Start));
            var unit = file.Root.FindToken(position).Parent?.AncestorsAndSelf()
                .FirstOrDefault(n => n is UsingDirectiveSyntax || n is MemberDeclarationSyntax and not BaseNamespaceDeclarationSyntax);
            if (unit is null)
            {
                unremovable.Add(error.ToString());
                continue;
            }
            file.Units.Add(unit);
        }

        var wanted = tests.ToHashSet(StringComparer.Ordinal);
        var notBuilt = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, (root, units)) in files.Where(f => f.Value.Units.Count > 0))
        {
            foreach (var (method, syntax) in TestMethodNodes(root))
            {
                if (wanted.Contains(method.Id) && syntax.AncestorsAndSelf().Any(units.Contains))
                {
                    notBuilt.Add(method.Id);
                }
            }
            var outermost = units.Where(u => !u.Ancestors().Any(units.Contains)).OrderBy(u => u.SpanStart).ToList();
            removed.AddRange(outermost.Select(u => $"{path}:{u.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {Describe(u)}"));
            replacements[path] = root.RemoveNodes(outermost, SyntaxRemoveOptions.KeepDirectives)!.ToFullString();
        }
        return new BuildFailure(tests.Where(notBuilt.Contains).ToList(), unattributable, unremovable, removed, replacements);
    }

    private static string Describe(SyntaxNode node) => node switch
    {
        UsingDirectiveSyntax u => u.ToString().Trim(),
        TypeDeclarationSyntax t => $"{t.Keyword.Text} {t.Identifier.Text}",
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        MethodDeclarationSyntax m => $"{m.Identifier.Text}()",
        ConstructorDeclarationSyntax c => $"{c.Identifier.Text}()",
        PropertyDeclarationSyntax p => p.Identifier.Text,
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        _ => node.Kind().ToString(),
    };
}
