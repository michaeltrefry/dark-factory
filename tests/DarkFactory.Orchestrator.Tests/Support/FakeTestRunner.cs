using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// The gate's test-run seam faked (sc-25382): commits are in-memory file maps, the PR's changes are the difference between
/// the base's and the head's map (or <see cref="Changes"/>), and each run answers from <see cref="Answer"/>; nothing is
/// executed. By default every commit but the base adds <see cref="NewTestFile"/> with one new fact, which fails on the
/// base and passes on the head.
/// </summary>
internal sealed class FakeTestRunner : IGateTestRunner
{
    public const string BaseSha = "base0";
    public const string Project = "tests/Widgets.Tests/Widgets.Tests.csproj";
    public const string NewTestFile = "tests/Widgets.Tests/WordCountTests.cs";
    public const string NewTest = "Widgets.Tests.WordCountTests.Whitespace_is_not_a_word";

    public const string XunitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="xunit.v3" Version="4.0.1" />
          </ItemGroup>
        </Project>
        """;

    public const string NewTestSource = """
        namespace Widgets.Tests;

        public class WordCountTests
        {
            [Fact]
            public void Whitespace_is_not_a_word() => Assert.Equal(0, TextTools.WordCount("  "));
        }
        """;

    public Dictionary<string, string> BaseFiles { get; } = new(StringComparer.Ordinal)
    {
        [Project] = XunitProject,
        ["src/Widgets/TextTools.cs"] = "namespace Widgets; public static class TextTools { }",
        ["global.json"] = """{ "test": { "runner": "Microsoft.Testing.Platform" } }""",
    };

    /// <summary>The head's files given its commit; by default the base's plus <see cref="NewTestFile"/>.</summary>
    public Func<string, Dictionary<string, string>> HeadFiles { get; set; }

    /// <summary>When set, the PR's changed files instead of the difference between the maps (e.g. to show a rename).</summary>
    public IReadOnlyList<ChangedFile>? Changes { get; set; }

    /// <summary>What a run reports; by default each test fails on the base and passes on the head.</summary>
    public Func<TestRunSpec, TestRunReport> Answer { get; set; }

    /// <summary>When set, a run throws this (e.g. the sandbox is unavailable).</summary>
    public Exception? RunThrows { get; set; }

    /// <summary>When set, a run is this (e.g. one that waits on its cancellation token) instead of <see cref="Answer"/>.</summary>
    public Func<TestRunSpec, CancellationToken, Task<TestRunReport>>? Running { get; set; }

    public List<TestRunSpec> Runs { get; } = [];

    public FakeTestRunner()
    {
        HeadFiles = _ => new Dictionary<string, string>(BaseFiles, StringComparer.Ordinal) { [NewTestFile] = NewTestSource };
        Answer = spec => Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
    }

    /// <summary>A run in which every test the spec's test step names has one case with <paramref name="outcome"/>.</summary>
    public static TestRunReport Report(IReadOnlyList<TestStep> steps, string outcome) => new(TestRunStatus.Ran,
        Filtered(steps).ToDictionary(t => t, t => (IReadOnlyList<TestCaseResult>)[new TestCaseResult(t, outcome)]), "ran");

    /// <summary>The test ids a run's test steps filter on (either runner's form).</summary>
    public static IReadOnlyList<string> Filtered(IReadOnlyList<TestStep> steps) =>
        steps.Where(s => s.Phase == TestPhase.Test).SelectMany(s => Filtered(s.Args)).ToList();

    private static IEnumerable<string> Filtered(IReadOnlyList<string> args)
    {
        var methods = args.Select((a, i) => (a, i)).Where(x => x.a == "--filter-method").Select(x => args[x.i + 1]).ToList();
        if (methods.Count > 0)
        {
            return methods;
        }
        var filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault() ?? "";
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(f => f["FullyQualifiedName=".Length..]).ToList();
    }

    /// <summary>A base run that did not build, with these errors.</summary>
    public static TestRunReport NotBuilt(params BuildError[] errors) => TestRunReport.Failed(TestRunStatus.BuildFailed, "build failed") with { BuildErrors = errors };

    /// <summary>A compiler error at <paramref name="line"/> of <paramref name="path"/> (column 5).</summary>
    public static BuildError CompilerError(string path, int line, string code = "CS0117") => new(path, line, 5, code, "does not compile");

    private Dictionary<string, string> FilesAt(string sha) => sha == BaseSha ? BaseFiles : HeadFiles(sha);

    public Task<IReadOnlyList<ChangedFile>> ChangesAsync(RepoRef repo, string baseSha, string headSha, CancellationToken ct)
    {
        if (Changes is { } changes)
        {
            return Task.FromResult(changes);
        }
        var (before, after) = (FilesAt(baseSha), FilesAt(headSha));
        var list = new List<ChangedFile>();
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var (had, has) = (before.TryGetValue(path, out var a), after.TryGetValue(path, out var b));
            if (!had)
            {
                list.Add(new ChangedFile('A', null, path));
            }
            else if (!has)
            {
                list.Add(new ChangedFile('D', path, null));
            }
            else if (a != b)
            {
                list.Add(new ChangedFile('M', path, path));
            }
        }
        return Task.FromResult<IReadOnlyList<ChangedFile>>(list);
    }

    public Task<IReadOnlyList<string>> FilesAsync(RepoRef repo, string sha, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(FilesAt(sha).Keys.Order(StringComparer.Ordinal).ToList());

    public Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct) =>
        Task.FromResult(FilesAt(sha).GetValueOrDefault(path));

    public Task<TestRunReport> RunAsync(RepoRef repo, TestRunSpec spec, CancellationToken ct)
    {
        Runs.Add(spec);
        return RunThrows is { } failure ? Task.FromException<TestRunReport>(failure)
            : Running is { } running ? running(spec, ct)
            : Task.FromResult(Answer(spec));
    }
}
