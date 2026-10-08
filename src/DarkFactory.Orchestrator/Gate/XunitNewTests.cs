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
/// make the check unsupported. Runs: <c>dotnet restore</c>, <c>dotnet build --no-restore</c>, then <c>dotnet test
/// --no-build</c> on just the new tests — <c>--filter-method</c> per test and xUnit's TRX report under Microsoft.Testing.Platform
/// (<c>global.json</c> <c>test.runner</c>), else VSTest's <c>--filter FullyQualifiedName=…</c> and TRX logger — in the
/// repository root; results from the TRX files (cases joined to their test by class and method name).
/// </summary>
public sealed class XunitNewTests : INewTestStrategy
{
    public string Name => "dotnet-xunit";

    public string ResultFilePattern => "*.trx";

    public bool Understands(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex XunitReference = new(@"<PackageReference\s+Include\s*=\s*""xunit(\.[A-Za-z0-9.]+)?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Test attributes of other .NET frameworks (NUnit, MSTest): a method carrying one outside an xUnit project is not runnable here.</summary>
    private static readonly HashSet<string> OtherFrameworkAttributes = new(StringComparer.Ordinal) { "Test", "TestCase", "TestMethod", "DataTestMethod" };

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        var headProjects = await TestProjectDirectoriesAsync(source, source.HeadSha, ct);
        var baseProjects = await TestProjectDirectoriesAsync(source, source.BaseSha, ct);
        var testFiles = changes.Where(c => (c.NewPath is { } n && Under(n, headProjects)) || (c.OldPath is { } o && Under(o, baseProjects))).ToList();

        var baseTests = new Dictionary<string, TestMethod>(StringComparer.Ordinal);
        var headTests = new Dictionary<string, TestMethod>(StringComparer.Ordinal);
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
                    foreach (var method in TestMethods(await source.ReadAsync(source.HeadSha, head, ct)))
                    {
                        headTests.TryAdd(method.Id, method);
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
        return new NewTestSet(testFiles, added, unisolable);
    }

    private static bool Under(string path, IReadOnlyList<string> directories) =>
        directories.Any(d => d.Length == 0 || path.StartsWith(d + "/", StringComparison.Ordinal));

    /// <summary>The directories of the commit's projects that reference an xUnit package.</summary>
    private static async Task<IReadOnlyList<string>> TestProjectDirectoriesAsync(TestSource source, string sha, CancellationToken ct)
    {
        var directories = new List<string>();
        foreach (var project in (await source.FilesAsync(sha, ct)).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (await source.ReadAsync(sha, project, ct) is { } text && XunitReference.IsMatch(text))
            {
                var slash = project.LastIndexOf('/');
                directories.Add(slash < 0 ? "" : project[..slash]);
            }
        }
        return directories;
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
        if (source is null)
        {
            return [];
        }
        var extra = also ?? NoAttributes;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = new List<TestMethod>();
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
            methods.Add(new TestMethod($"{type}.{method.Identifier.Text}", isTheory, rows));
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

    public async Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, CancellationToken ct)
    {
        var platform = UsesTestingPlatform(await source.ReadAsync(commit, "global.json", ct));
        List<string> test = ["test", "--no-build", "--results-directory", NewTestsCheck.ResultsDirectory];
        if (platform)
        {
            test.Add("--report-xunit-trx");
            foreach (var id in tests)
            {
                test.Add("--filter-method");
                test.Add(id);
            }
        }
        else
        {
            test.AddRange(["--logger", "trx", "--filter", string.Join('|', tests.Select(id => $"FullyQualifiedName={id}"))]);
        }
        return
        [
            new TestStep(TestPhase.Restore, "dotnet", ["restore"]),
            new TestStep(TestPhase.Build, "dotnet", ["build", "--no-restore"]),
            new TestStep(TestPhase.Test, "dotnet", test),
        ];
    }

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
    /// Each test's cases from TRX files: <c>UnitTest</c> definitions name the class and method (<c>TestMethod
    /// className</c>, <c>name</c>), <c>UnitTestResult</c>s carry the case's display name and outcome, joined by test id.
    /// A file that is not readable TRX contributes nothing (the tests it held then count as not run).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TestCaseResult>> ParseResults(IEnumerable<string> resultFiles)
    {
        var results = new Dictionary<string, List<TestCaseResult>>(StringComparer.Ordinal);
        foreach (var file in resultFiles)
        {
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            var outcomes = new List<(string TestId, string Name, string Outcome)>();
            try
            {
                using var reader = XmlReader.Create(new StringReader(file),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
                string? unitTestId = null;
                while (reader.Read())
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
            }
            catch (XmlException)
            {
                continue;
            }
            foreach (var (testId, name, outcome) in outcomes)
            {
                if (definitions.TryGetValue(testId, out var test))
                {
                    (results.TryGetValue(test, out var cases) ? cases : results[test] = []).Add(new TestCaseResult(name, outcome));
                }
            }
        }
        return results.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TestCaseResult>)kv.Value, StringComparer.Ordinal);
    }

    private static string Outcome(string? trx) => trx switch
    {
        "Passed" => TestCaseResult.Passed,
        "NotExecuted" or "Inconclusive" or "Pending" => TestCaseResult.Skipped,
        _ => TestCaseResult.Failed,
    };
}
