using System.Diagnostics;
using System.Text;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-26128: a Swift file's declarations with their spans, where a base build's compiler errors are placed.</summary>
public class SwiftLayoutTests
{
    private static string[] Items(IReadOnlyList<SwiftItem> items, string text) =>
        items.Select(i => $"{i.Keyword} {i.Name}: {text[i.Start..i.End].ReplaceLineEndings("⏎")}").ToArray();

    [Fact]
    public void A_files_declarations_are_split_with_their_attributes_bodies_and_multi_line_signatures()
    {
        const string text = """
            import Testing
            @testable import Words

            // a comment
            @Test
            func blank() { #expect(count("") == 0) }
            func helper(_ s: String)
                async throws -> Int { 1 }
            let shared = [1, 2]
            #if DEBUG
            @MainActor struct Spaces: Sendable {
                @Test func one() {} @Test func two() {}
                var x: Int { get { 1 } }
            }
            #endif
            extension Words.Thing where T == Int { static func f() {}; func g() {} }
            """;

        var layout = SwiftSource.Layout(text);

        Assert.Equal(
        [
            "import Testing: import Testing",
            "import Words: @testable import Words",
            "func blank: @Test⏎func blank() { #expect(count(\"\") == 0) }",
            "func helper: func helper(_ s: String)⏎    async throws -> Int { 1 }",
            "let shared: let shared = [1, 2]",
            "#if : #if DEBUG",
            "struct Spaces: @MainActor struct Spaces: Sendable {⏎    @Test func one() {} @Test func two() {}⏎    var x: Int { get { 1 } }⏎}",
            "#endif : #endif",
            "extension Words: extension Words.Thing where T == Int { static func f() {}; func g() {} }",
        ], Items(layout.Items, text));
        var spaces = layout.Items[6];
        Assert.Equal(text.IndexOf("Sendable {", StringComparison.Ordinal) + "Sendable ".Length, spaces.BodyOpen);
        Assert.Equal(["func one: @Test func one() {}", "func two: @Test func two() {}", "var x: var x: Int { get { 1 } }"], Items(spaces.Children, text));
        Assert.Equal(["func f: static func f() {};", "func g: func g() {}"], Items(layout.Items[8].Children, text));
        Assert.True(layout.Items[5].IsDirective);
        Assert.Throws<FormatException>(() => SwiftSource.Layout("struct S {\n"));
    }

    [Fact]
    public void A_position_is_placed_in_the_innermost_declaration_and_columns_count_utf8_bytes_as_swiftcs_do()
    {
        const string text = "import Testing\nstruct S {\n    @Test func t() { let é = \"ééé\"; #expect(trim(é) == \"a\") }\n\n}\n";
        var layout = SwiftSource.Layout(text);

        // swiftc said 3:49 for `trim` (each é is two bytes).
        var position = layout.Position(3, 49);

        Assert.Equal(text.IndexOf("trim", StringComparison.Ordinal), position);
        Assert.Equal(["S", "t"], layout.ItemsAt(position).Select(i => i.Name));
        Assert.Equal(["S"], layout.ItemsAt(layout.Position(4, 1)).Select(i => i.Name)); // between members
        Assert.Empty(layout.ItemsAt(text.IndexOf("struct", StringComparison.Ordinal) - 1)); // the newline between items
        Assert.Equal(-1, layout.Position(9, 1));
        Assert.Equal(3, layout.LineOf(position));
    }
}

/// <summary>sc-26128: a Swift base that does not build with the PR's test files — not-built, error and retries.</summary>
public class SwiftBaseBuildTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string TestFile = SwiftIdentifyTests.TestFile;
    private const string Imports = "import Testing\n@testable import Words\n\n";

    private static FakeTestRunner Runner(string tests) =>
        SwiftIdentifyTests.Runner(("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary), (TestFile, tests));

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, "head1", "gate-sc-1", null, CancellationToken.None);

    private static string Stage(TestRunSpec spec) => spec.Name["gate-sc-1-".Length..];

    private static TestRunReport Passing(TestRunSpec spec) => FakeTestRunner.Report(spec.Steps, TestCaseResult.Passed);

    private static TestRunReport Failing(TestRunSpec spec) => FakeTestRunner.Report(spec.Steps, TestCaseResult.Failed);

    /// <summary>swiftc's error at <paramref name="text"/> in the test file as the run built it (the head's, or the retry's replacement).</summary>
    private static BuildError At(TestRunSpec spec, string tests, string text, string message = "cannot find 'trim' in scope")
    {
        var content = spec.Replacements.TryGetValue(TestFile, out var replaced) ? replaced : tests;
        var offset = content.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"{text} is not in the run's {TestFile}");
        var lineStart = content.LastIndexOf('\n', Math.Max(offset - 1, 0)) is var nl and >= 0 && offset > 0 ? nl + 1 : 0;
        return new BuildError(TestFile, content.AsSpan(0, offset).Count('\n') + 1, Encoding.UTF8.GetByteCount(content.AsSpan(lineStart, offset - lineStart)) + 1, "swiftc", message);
    }

    [Fact]
    public async Task A_new_test_calling_code_the_base_lacks_is_not_built_and_the_others_get_their_real_results_from_a_retry_without_it()
    {
        const string tests = Imports + "@Test func two() { #expect(count(\"a b\") == 2) }\n@Test func blank() { #expect(count(\"\") == 0) }\n"
            + "struct Trimming {\n    @Test func trims() { #expect(trim(\" a \") == \"a\") }\n}\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt(At(spec, tests, "trim(\" a")),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("WordsTests.Trimming/trims()", "not-built"), ("WordsTests.blank()", "failed")], result.Tests.Select(t => (t.Test, t.Base)));
        Assert.Contains("(1 of them do not compile there: WordsTests.Trimming/trims())", result.Reason);
        var retry = runner.Runs.Single(r => Stage(r) == "base-retry");
        Assert.Equal(["WordsTests.blank()"], FakeTestRunner.Filtered(retry.Steps));
        Assert.Equal(Imports + "@Test func two() { #expect(count(\"a b\") == 2) }\n@Test func blank() { #expect(count(\"\") == 0) }\nstruct Trimming {\n    \n}\n",
            retry.Replacements[TestFile]);
        Assert.Equal([$"{TestFile}:7 func trims"], result.BaseRemoved);
    }

    [Fact]
    public async Task A_new_test_in_a_type_whose_header_the_base_cannot_build_is_not_built()
    {
        const string tests = Imports + "@Test func blank() { #expect(count(\"\") == 0) }\nextension Trimmer {\n    @Test func trims() {}\n}\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt(At(spec, tests, "Trimmer", "cannot find type 'Trimmer' in scope")),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal("not-built", result.Tests.Single(t => t.Test == "WordsTests.Trimmer/trims()").Base);
        Assert.Equal([$"{TestFile}:5 extension Trimmer"], result.BaseRemoved);
    }

    public static TheoryData<string, BuildError[]> ErrorsOutsideTheTestCode => new()
    {
        { "the base's production code", [new BuildError("Sources/Words/Words.swift", 1, 31, "swiftc", "cannot find 'nope' in scope")] },
        { "the package manifest", [new BuildError("Package.swift", 10, 19, "swiftc", "cannot convert value of type 'String' to specified type 'Int'")] },
        { "a file SwiftPM generated, outside the worktree", [new BuildError(null, 1, 1, "swiftc", "cannot find 'x' in scope")] },
        { "a dependency's missing product", [new BuildError(null, 0, 0, "swiftc", "product 'Collections' required by package 'words' target 'WordsTests' not found")] },
        { "no error named at all", [] },
        { "a test file the PR did not change", [new BuildError("Tests/WordsTests/Old.swift", 2, 5, "swiftc", "cannot find 'x' in scope")] },
    };

    [Theory]
    [MemberData(nameof(ErrorsOutsideTheTestCode))]
    public async Task An_error_outside_the_prs_test_code_makes_the_check_an_error_with_no_retry(string where, BuildError[] errors)
    {
        const string tests = Imports + "@Test func blank() { #expect(count(\"\") == 0) }\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) == "head" ? Passing(spec) : FakeTestRunner.NotBuilt(errors);

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Error, $"{where}: {result.Outcome}: {result.Reason}");
        Assert.Contains("does not build with the PR's test files for a reason that says nothing about the new tests", result.Reason);
        Assert.DoesNotContain(runner.Runs, r => Stage(r).StartsWith("base-retry", StringComparison.Ordinal));
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task An_error_in_production_code_beside_one_in_a_new_test_is_still_an_error()
    {
        const string tests = Imports + "@Test func trims() { #expect(trim(\" a \") == \"a\") }\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) == "head" ? Passing(spec)
            : FakeTestRunner.NotBuilt(At(spec, tests, "trim("), new BuildError("Sources/Words/Words.swift", 1, 1, "swiftc", "expected declaration"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("Sources/Words/Words.swift", result.Reason);
    }

    [Fact]
    public async Task A_helper_that_does_not_compile_is_left_out_of_the_retry_and_a_test_that_needs_it_is_named_by_the_next_stage()
    {
        const string tests = Imports + "func trimmed() -> String { trim(\" a \") }\n@Test func trims() { #expect(trimmed() == \"a\") }\n"
            + "@Test func one() { #expect(count(\"a\") == 1) }\n";
        var runner = Runner(tests);
        // swiftc in stages: the helper first; without it, the test that called it.
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt(At(spec, tests, "trim(\"")),
            "base-retry" => FakeTestRunner.NotBuilt(At(spec, tests, "trimmed()", "cannot find 'trimmed' in scope")),
            _ => Passing(spec),
        };

        var result = await Check(runner);

        // The helper was test code the base cannot build, not a new test: trims is not-built only once its own function is.
        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal([("WordsTests.one()", "passed"), ("WordsTests.trims()", "not-built")], result.Tests.Select(t => (t.Test, t.Base)));
        Assert.EndsWith("so they check nothing: WordsTests.one()", result.Reason);
        Assert.DoesNotContain("func trimmed", runner.Runs.Single(r => Stage(r) == "base-retry").Replacements[TestFile]);
        Assert.Contains("func trims", runner.Runs.Single(r => Stage(r) == "base-retry").Replacements[TestFile]);
        var last = runner.Runs.Single(r => Stage(r) == "base-retry-2").Replacements[TestFile];
        Assert.DoesNotContain("func trims", last);
        Assert.Contains("@Test func one()", last);
        Assert.Equal(["WordsTests.one()"], FakeTestRunner.Filtered(runner.Runs.Single(r => Stage(r) == "base-retry-2").Steps));
    }

    [Fact]
    public async Task An_import_of_a_module_the_base_lacks_is_left_out_alone_and_the_tests_that_used_it_are_named_by_the_next_stage()
    {
        const string tests = "import Testing\nimport Collections\n@testable import Words\n\n@Test func deque() { #expect(Deque([1]).count == 1) }\n"
            + "@Test func blank() { #expect(count(\"\") == 0) }\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt(At(spec, tests, "Collections", "no such module 'Collections'")),
            "base-retry" => FakeTestRunner.NotBuilt(At(spec, tests, "Deque(", "cannot find 'Deque' in scope")),
            _ => Failing(spec),
        };

        var result = await Check(runner);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        Assert.Equal([("WordsTests.blank()", "failed"), ("WordsTests.deque()", "not-built")], result.Tests.Select(t => (t.Test, t.Base)));
        var retry = runner.Runs.Single(r => Stage(r) == "base-retry").Replacements[TestFile];
        Assert.Equal("import Testing\n\n@testable import Words\n\n@Test func deque() { #expect(Deque([1]).count == 1) }\n@Test func blank() { #expect(count(\"\") == 0) }\n", retry);
        Assert.Contains($"{TestFile}:2 import Collections", result.BaseRemoved);
    }

    [Fact]
    public async Task A_test_the_last_retry_still_cannot_build_is_unproven()
    {
        // Each stage uncovers another helper the base cannot build; after the third retry the base still does not build.
        const string tests = Imports + "func a() -> Int { x }\nfunc b() -> Int { y }\nfunc c() -> Int { z }\nfunc d() -> Int { w }\n"
            + "@Test func blank() { #expect(count(\"\") == 0) }\n";
        var runner = Runner(tests);
        runner.Answer = spec => Stage(spec) switch
        {
            "head" => Passing(spec),
            "base" => FakeTestRunner.NotBuilt(At(spec, tests, "{ x }")),
            "base-retry" => FakeTestRunner.NotBuilt(At(spec, tests, "{ y }")),
            "base-retry-2" => FakeTestRunner.NotBuilt(At(spec, tests, "{ z }")),
            "base-retry-3" => FakeTestRunner.NotBuilt(At(spec, tests, "{ w }")),
            _ => Passing(spec),
        };

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.Equal("unproven", result.Tests.Single().Base);
        Assert.Contains("new test(s) could not be shown to fail on the base", result.Reason);
        Assert.Equal(4, runner.Runs.Count(r => Stage(r).StartsWith("base", StringComparison.Ordinal))); // the base and three retries
    }

    [Fact]
    public async Task An_error_between_a_types_members_or_on_a_conditional_compilation_line_is_unremovable_so_the_tests_stay_unproven()
    {
        const string tests = Imports + "#if canImport(Collections)\n#endif\nstruct S {\n    @Test func blank() { #expect(count(\"\") == 0) }\n    // between\n}\n";
        foreach (var at in new[] { "#if canImport", "// between" })
        {
            var runner = Runner(tests);
            runner.Answer = spec => Stage(spec) == "head" ? Passing(spec) : FakeTestRunner.NotBuilt(At(spec, tests, at, "unexpected"));

            var result = await Check(runner);

            Assert.True(result.Outcome == NewTestsOutcome.Rejected, $"{at}: {result.Outcome}: {result.Reason}");
            Assert.Equal("unproven", result.Tests.Single().Base);
            Assert.DoesNotContain(runner.Runs, r => Stage(r).StartsWith("base-retry", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Swiftpms_echo_of_a_failed_compiler_job_is_no_error_of_its_own_but_a_crashed_one_is()
    {
        const string log = """
            [7/9] Emitting module Words
            error: emit-module command failed with exit code 1 (use -v to see invocation)
            /work/wt/Tests/WordsTests/WordsTests.swift:2:8: error: no such module 'Missing'
            error: compile command failed with exit code 1 (use -v to see invocation)
            error: compile command failed due to signal 6 (use -v to see invocation)
            """;

        Assert.Equal(
        [
            new BuildError("Tests/WordsTests/WordsTests.swift", 2, 8, "swiftc", "no such module 'Missing'"),
            new BuildError(null, 0, 0, "swiftc", "compile command failed due to signal 6 (use -v to see invocation)"),
        ], new SwiftNewTests().ParseBuildErrors(log, ["/work/wt"]));
    }
}

/// <summary>sc-26128: the real toolchain — a base that does not build, through swift build and swift test (skipped where swift is not installed).</summary>
public class SwiftBaseBuildRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-swiftbase-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public SwiftBaseBuildRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Lazy<bool> HasSwift = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(SwiftNewTests.Swift, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_seed, path))!);
        File.WriteAllText(Path.Combine(_seed, path), content);
    }

    private string CommitAndPush(string message, string branch)
    {
        TestGit.Run(_seed, "add", "-A");
        TestGit.Run(_seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        TestGit.Run(_seed, "push", "-q", _remote, branch);
        return TestGit.Run(_seed, "rev-parse", "HEAD").Trim();
    }

    private Task<NewTestsResult> Check(string baseSha, string headSha) =>
        NewTestsCheck.RunAsync(new SandboxTestRunner(new GitWorkspace(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null)), slots: null,
            TimeSpan.FromMinutes(10)), NewTestsCheck.Strategies, Repo, baseSha, headSha, "gate-sc-6", null, TestContext.Current.CancellationToken);

    private const string Trim = "public func trim(_ s: String) -> String { s.filter { $0 != \" \" } }\n";

    [Fact]
    public async Task With_swift_a_new_test_calling_a_function_the_base_lacks_is_not_built_and_passes_and_a_broken_base_is_an_error()
    {
        Assert.SkipUnless(HasSwift.Value, "swift is not installed");
        Write("Package.swift", SwiftIdentifyTests.Manifest);
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.BaseLibrary);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests);
        Write(".gitignore", ".build/\n");
        var baseSha = CommitAndPush("base", "main");

        // The head adds trim and a test of it (the base has no trim: it cannot build there), and a test of count's fix.
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-6");
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary + Trim);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests
            + "@Test func trims() { #expect(trim(\" a \") == \"a\") }\n@Test func blank() { #expect(count(\"\") == 0) }\n");
        var head = CommitAndPush("trim", "factory/sc-6");

        var pass = await Check(baseSha, head);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal([("WordsTests.blank()", "failed", "passed"), ("WordsTests.trims()", "not-built", "passed")],
            pass.Tests.Select(t => (t.Test, t.Base, t.Head)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.Equal(("build-failed", "ran"), (pass.BaseRun, pass.BaseRetry));
        Assert.Equal([$"{SwiftIdentifyTests.TestFile}:5 func trims"], pass.BaseRemoved);

        // In stages: the head adds a module the base's manifest lacks. The base build names only its import (and SwiftPM's
        // echo of the failed job, which is no error of its own); the retry without it names the test that used it.
        Write("Package.swift", SwiftIdentifyTests.Manifest.Replace(".target(name: \"Words\"),", ".target(name: \"Words\"),\n        .target(name: \"Extra\"),")
            .Replace("dependencies: [\"Words\"]", "dependencies: [\"Words\", \"Extra\"]"));
        Write("Sources/Extra/Extra.swift", "public func extra() -> Int { 1 }\n");
        Write(SwiftIdentifyTests.TestFile, "import Testing\nimport Extra\n@testable import Words\n\n@Test func two() { #expect(count(\"a b\") == 2) }\n"
            + "@Test func usesExtra() { #expect(extra() == 1) }\n@Test func blank() { #expect(count(\"\") == 0) }\n");
        var staged = CommitAndPush("extra", "factory/sc-6");

        var inStages = await Check(baseSha, staged);

        Assert.True(inStages.Outcome == NewTestsOutcome.Pass, $"{inStages.Outcome}: {inStages.Reason}");
        Assert.Equal([("WordsTests.blank()", "failed"), ("WordsTests.usesExtra()", "not-built")],
            inStages.Tests.Select(t => (t.Test, t.Base)).OrderBy(t => t.Test, StringComparer.Ordinal));
        Assert.Equal([$"{SwiftIdentifyTests.TestFile}:2 import Extra", $"{SwiftIdentifyTests.TestFile}:6 func usesExtra"], inStages.BaseRemoved);

        // A base whose own production code does not build is no evidence about the new tests: an error, never a pass.
        TestGit.Run(_seed, "checkout", "-q", "main");
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.BaseLibrary + "public func broken() -> Int { nope }\n");
        var brokenBase = CommitAndPush("a broken base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-7");
        Write("Sources/Words/Words.swift", SwiftIdentifyTests.FixedLibrary);
        Write(SwiftIdentifyTests.TestFile, SwiftIdentifyTests.BaseTests + "@Test func blank() { #expect(count(\"\") == 0) }\n");
        var fixedHead = CommitAndPush("fix", "factory/sc-7");

        var error = await Check(brokenBase, fixedHead);

        Assert.True(error.Outcome == NewTestsOutcome.Error, $"{error.Outcome}: {error.Reason}");
        Assert.Contains("Sources/Words/Words.swift", error.Reason);
        Assert.Contains("cannot find 'nope' in scope", error.Reason);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }
}
