using System.Diagnostics;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25875: the lexer-level reader that finds a Rust file's modules, functions and attributes (nothing compiled).</summary>
public class RustSourceTests
{
    private static string[] Tests(string source) => RustSource.Parse(source).TestFns().Select(t => t.Path).ToArray();

    [Fact]
    public void Test_functions_are_found_under_every_common_attribute_form_and_named_by_their_modules()
    {
        var tests = Tests("""
            pub fn words(s: &str) -> usize { s.len() }
            #[test] fn top_level() {}
            #[cfg(test)]
            mod tests {
                use super::*;
                #[test]
                fn plain() {}
                #[tokio::test]
                async fn with_tokio() {}
                #[tokio::test(flavor = "multi_thread")]
                pub(crate) async fn with_arguments() {}
                #[test]
                #[should_panic(expected = "x")]
                fn panics() { panic!("x") }
                #[rstest]
                fn with_rstest(#[case] a: u32) {}
                #[test_case(1 ; "one")]
                #[test_case(2 ; "two")]
                fn with_cases(n: u32) {}
                #[quickcheck]
                fn property(xs: Vec<u8>) -> bool { true }
                fn helper() {}
                #[derive(Debug)] struct NotATest { x: u32 }
                #[inline] fn also_not_a_test() {}
                mod deeper { #[test] fn nested() {} mod deepest { #[sqlx::test] async fn very() {} } }
            }
            """);

        Assert.Equal(
        [
            "top_level", "tests::plain", "tests::with_tokio", "tests::with_arguments", "tests::panics", "tests::with_rstest", "tests::with_cases",
            "tests::property", "tests::deeper::nested", "tests::deeper::deepest::very",
        ], tests);
    }

    [Fact]
    public void A_test_attribute_inside_a_comment_or_any_string_literal_is_not_a_test_and_hides_no_brace()
    {
        var tests = Tests(""""
            // #[test] fn in_a_line_comment() {}
            /* #[test] fn in_a_block_comment() {} /* nested { */ #[test] fn still_comment() {} */
            /// #[test] fn in_a_doc_comment() {}
            const A: &str = "#[test] fn in_a_string() {} \" { ";
            const B: &str = r#"#[test] fn in_a_raw_string() {} " { "#;
            const C: &[u8] = br##"#[test] fn in_a_raw_byte_string() {} "# { "##;
            const D: &[u8] = b"#[test] fn in_a_byte_string() { \" ";
            const E: char = '{';
            const F: char = '\'';
            const G: u8 = b'{';
            const H: char = '"';
            fn lifetimes<'a>(x: &'a str) -> &'a str { let _ = "#[test] fn inside_a_body() {}"; 'outer: loop { break 'outer; } x }
            mod r#mod { #[test] fn in_a_raw_identifier_module() {} }
            #[test] fn r#fn() { let s = "}"; let c = '}'; /* } */ }
            #[test] fn real() {}
            """");

        Assert.Equal(["mod::in_a_raw_identifier_module", "fn", "real"], tests);
    }

    [Fact]
    public void Function_and_type_bodies_are_skipped_macros_in_item_position_are_read_and_macro_templates_are_not()
    {
        var tests = Tests("""
            struct S { a: u32 }
            impl S { #[test] fn in_an_impl() {} }
            trait T { #[test] fn in_a_trait() {} }
            fn outer() { #[test] fn inside_a_function() {} }
            macro_rules! make { ($name:ident) => { #[test] fn $name() {} }; }
            extern "C" { fn foreign(); }
            pub(crate) const unsafe extern "C" fn qualified() {}
            static TABLE: [u8; 2] = [1, 2];
            mod tests {
                proptest! {
                    #![proptest_config(ProptestConfig::with_cases(8))]
                    #[test]
                    fn in_proptest(x in 0..10u32) { prop_assert!(x < 10); }
                }
                lazy_static! { static ref X: u32 = 1; }
                #[test] fn after() {}
            }
            """);

        Assert.Equal(["tests::in_proptest", "tests::after"], tests);
    }

    [Fact]
    public void Items_carry_their_attributes_spans_and_cfg_test_gate()
    {
        const string source = "use a::{b, c};\n#[cfg(test)]\nmod tests { #[test] fn t() {} }\n#[cfg(all(test, feature = \"x\"))] mod more;\n#[cfg(not(test))] mod prod;\n";
        var file = RustSource.Parse(source);

        Assert.Equal(["use", "mod", "mod", "mod"], file.Items.Select(i => i.Kind));
        Assert.Equal("#[cfg(test)]\nmod tests { #[test] fn t() {} }", file.TextOf(file.Items[1]));
        Assert.Equal([false, true, true, false], file.Items.Select(i => i.IsGated));
        Assert.True(file.Items[1].IsInlineMod);
        Assert.False(file.Items[2].IsInlineMod);
        Assert.Equal('}', source[file.Items[1].BodyClose]);
        Assert.Equal(["tests", "t"], file.ItemsAt(source.IndexOf("fn t", StringComparison.Ordinal)).Select(i => i.Name));
        Assert.Equal(source.IndexOf("mod tests", StringComparison.Ordinal), file.Position(3, 1));
        Assert.Equal(-1, file.Position(99, 1));
        Assert.True(RustSource.Parse("#![cfg(test)]\nfn helper() {}").IsGated);
    }

    [Theory]
    [InlineData("fn a() { ")]
    [InlineData("fn a() { ) }")]
    [InlineData("const A: &str = \"open;")]
    [InlineData("const A: &str = r#\"open\";")]
    [InlineData("/* open")]
    public void Source_that_cannot_be_read_throws_instead_of_holding_no_tests(string source) =>
        Assert.Throws<FormatException>(() => RustSource.Parse(source));

    [Fact]
    public void The_cases_of_a_parameterized_test_are_its_case_attributes()
    {
        var file = RustSource.Parse("#[rstest]\n#[case(1)]\n#[case(2)]\nfn a(#[case] n: u32, #[values(1, 2)] m: u32) {}\n#[test] fn b() {}");

        Assert.Equal(["case ( 1 )", "case ( 2 )", "case", "values ( 1 , 2 )"], file.Items[0].CaseRows);
        Assert.Empty(file.Items[1].CaseRows);
    }
}

/// <summary>sc-25875: which tests of a Rust pull request are new, how cargo names them, and the base run's overlay.</summary>
public class CargoIdentifyTests
{
    private static readonly RepoRef Repo = new("acme", "words");
    private const string Head = "head1";
    private const string Manifest = "[package]\nname = \"words\"\nversion = \"0.1.0\"\nedition = \"2021\"\n";
    private const string Lib = "pub fn words(s: &str) -> usize { s.split(' ').count() }\n#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn two() { assert_eq!(words(\"a b\"), 2); }\n}\n";

    internal static FakeTestRunner Runner(params (string Path, string Content)[] baseFiles)
    {
        var runner = new FakeTestRunner();
        runner.BaseFiles.Clear();
        foreach (var (path, content) in baseFiles)
        {
            runner.BaseFiles[path] = content;
        }
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal);
        runner.Answer = spec => Report(spec, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        return runner;
    }

    /// <summary>A run in which every test a test step runs has one case with <paramref name="outcome"/>.</summary>
    internal static TestRunReport Report(TestRunSpec spec, string outcome) => new(TestRunStatus.Ran,
        spec.Steps.Where(s => s.Phase == TestPhase.Test).ToDictionary(s => s.CaptureOutput!, s => (IReadOnlyList<TestCaseResult>)[new TestCaseResult(s.CaptureOutput!, outcome)]), "ran");

    internal static FakeTestRunner With(FakeTestRunner runner, params (string Path, string? Content)[] headFiles)
    {
        runner.HeadFiles = _ =>
        {
            var head = new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal);
            foreach (var (path, content) in headFiles)
            {
                if (content is null)
                {
                    head.Remove(path);
                }
                else
                {
                    head[path] = content;
                }
            }
            return head;
        };
        return runner;
    }

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, Head, "gate-sc-1", null, CancellationToken.None);

    private static async Task<CargoNewTestSet> Identify(FakeTestRunner runner)
    {
        var changes = await runner.ChangesAsync(Repo, FakeTestRunner.BaseSha, Head, CancellationToken.None);
        var set = await new CargoNewTests().IdentifyAsync(new TestSource(runner, Repo, FakeTestRunner.BaseSha, Head), changes, CancellationToken.None);
        return Assert.IsType<CargoNewTestSet>(set);
    }

    [Fact]
    public async Task A_new_inline_test_is_found_named_as_libtest_names_it_and_grafted_into_the_base_file()
    {
        var head = Lib.Replace("split(' ')", "split_whitespace()").Replace("}\n}\n", "}\n    #[test]\n    fn blank() { assert_eq!(words(\"  \"), 0); }\n}\n");
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", Lib)), ("src/lib.rs", head));

        var set = await Identify(runner);
        var result = await Check(runner);

        Assert.Equal(["words:lib:tests::blank"], set.Tests);
        Assert.Equal(("src/lib.rs", "tests::blank"), set.Locations["words:lib:tests::blank"]);
        Assert.Equal("Cargo.toml", set.Projects["words:lib:tests::blank"]);
        // The base file keeps its production code (split(' ')) and gets the head's test module.
        var grafted = set.BaseReplacements["src/lib.rs"];
        Assert.Contains("s.split(' ').count()", grafted);
        Assert.Contains("fn blank() { assert_eq!(words(\"  \"), 0); }", grafted);
        Assert.Equal([new ChangedFile('M', "src/lib.rs", "src/lib.rs")], set.TestFiles);
        Assert.Empty(set.WholeFiles);

        Assert.Equal(NewTestsOutcome.Pass, result.Outcome);
        Assert.Equal("rust-cargo", result.Strategy);
        var (onBase, onHead) = (runner.Runs[0], runner.Runs[1]);
        Assert.Equal((Head, "src/lib.rs"), (onBase.OverlayFrom, Assert.Single(onBase.OverlayPaths)));
        Assert.Equal(grafted, onBase.Replacements["src/lib.rs"]);
        Assert.Null(onHead.OverlayFrom);
        Assert.Empty(onHead.Replacements);
    }

    [Fact]
    public async Task Tests_are_named_per_package_and_target_in_a_workspace()
    {
        var runner = With(Runner(
                ("rust/Cargo.toml", "[workspace]\nmembers = [\"crates/*\"]\n"),
                ("rust/crates/words/Cargo.toml", Manifest), ("rust/crates/words/src/lib.rs", "pub mod count;\n"), ("rust/crates/words/src/count.rs", "pub fn c() {}\n"),
                ("rust/crates/tool/Cargo.toml", Manifest.Replace("words", "tool")), ("rust/crates/tool/src/main.rs", "mod run;\nfn main() {}\n"),
                ("rust/crates/tool/src/run.rs", "pub fn r() {}\n"), ("rust/crates/tool/src/bin/extra.rs", "fn main() {}\n")),
            ("rust/crates/words/src/count.rs", "pub fn c() {}\n#[cfg(test)]\nmod tests { #[test] fn counts() {} }\n"),
            ("rust/crates/words/src/deep/mod.rs", "#[cfg(test)] mod tests { #[test] fn in_mod_rs() {} }\n"),
            ("rust/crates/words/src/lib.rs", "pub mod count;\npub mod deep;\n"),
            ("rust/crates/words/tests/api.rs", "#[test] fn top() {}\nmod deep { #[test] fn nested() {} }\n"),
            ("rust/crates/words/tests/suite/main.rs", "mod part;\n#[test] fn in_main() {}\n"),
            ("rust/crates/words/tests/suite/part.rs", "#[test] fn in_part() {}\n"),
            ("rust/crates/tool/src/main.rs", "mod run;\nfn main() {}\n#[cfg(test)] mod tests { #[test] fn of_main() {} }\n"),
            ("rust/crates/tool/src/run.rs", "pub fn r() {}\n#[cfg(test)] mod tests { #[test] fn runs() {} }\n"),
            ("rust/crates/tool/src/bin/extra.rs", "fn main() {}\n#[test] fn of_extra() {}\n"));

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(
        [
            "tool:bins:of_extra", "tool:bins:run::tests::runs", "tool:bins:tests::of_main", "words:lib:count::tests::counts", "words:lib:deep::tests::in_mod_rs",
            "words:test/api:deep::nested", "words:test/api:top", "words:test/suite:in_main", "words:test/suite:part::in_part",
        ], set.Tests.Order(StringComparer.Ordinal));
        Assert.Equal("rust/crates/tool/Cargo.toml", set.Projects["tool:bins:tests::of_main"]);
        // Integration tests are test code as a whole: applied from the head, never rewritten.
        Assert.Equal(["rust/crates/words/tests/api.rs", "rust/crates/words/tests/suite/main.rs", "rust/crates/words/tests/suite/part.rs"], set.WholeFiles.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("rust/crates/words/tests/api.rs", set.BaseReplacements.Keys);
    }

    [Fact]
    public async Task A_rust_pull_request_that_adds_no_test_has_no_tests_and_runs_nothing()
    {
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", Lib)), ("src/lib.rs", Lib.Replace("split(' ')", "split_whitespace()")));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.NoTests, result.Outcome);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task A_changed_test_body_a_test_removed_and_added_again_and_a_test_of_a_renamed_file_are_not_new()
    {
        // The head's two (same id as the base's, another body) is not new; gone is removed; the renamed file's test is the old file's.
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "mod old;\n" + Lib), ("src/old.rs", "#[cfg(test)] mod tests { #[test] fn kept() {} #[test] fn gone() {} }\n")),
            ("src/lib.rs", "mod new;\n" + Lib.Replace("words(\"a b\"), 2", "words(\"a  b\"), 3")),
            ("src/old.rs", null), ("src/new.rs", "#[cfg(test)] mod tests { #[test] fn kept() {} }\n"));
        runner.Changes = [new ChangedFile('M', "src/lib.rs", "src/lib.rs"), new ChangedFile('R', "src/old.rs", "src/new.rs")];

        Assert.Empty((await Identify(runner)).Tests);
        Assert.Equal(NewTestsOutcome.NoTests, (await Check(runner)).Outcome);
    }

    [Fact]
    public async Task A_test_moved_unchanged_to_another_file_is_not_new_but_a_changed_one_is()
    {
        const string moved = "#[test] fn moved() { assert!(crate::a()); }";
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "mod a;\nmod b;\n"), ("src/a.rs", $"#[cfg(test)] mod tests {{ {moved} }}\n"), ("src/b.rs", "\n")),
            ("src/a.rs", "\n"), ("src/b.rs", $"#[cfg(test)] mod tests {{\n    {moved}\n    #[test] fn fresh() {{}}\n}}\n"));

        Assert.Equal(["words:lib:b::tests::fresh"], (await Identify(runner)).Tests);
    }

    [Fact]
    public async Task A_parameterized_test_is_one_test_with_every_case_and_new_cases_on_an_existing_one_cannot_be_isolated()
    {
        const string before = "#[cfg(test)] mod tests {\n    #[test_case(1)]\n    fn sizes(n: u32) {}\n}\n";
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", before)),
            ("src/lib.rs", before.Replace("#[test_case(1)]", "#[test_case(1)]\n    #[test_case(2)]").Replace("}\n}\n", "}\n    #[rstest]\n    #[case(1)]\n    fn fresh(#[case] n: u32) {}\n}\n")));

        var set = await Identify(runner);
        var result = await Check(runner);

        Assert.Equal(["words:lib:tests::fresh::*"], set.Tests);
        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("new cases on the existing parameterized test words:lib:tests::sizes::* cannot be run apart from its old cases", result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData("tests/common/mod.rs", "#[test] fn shared() {}", "tests/common/mod.rs has the new test shared, which the gate cannot run: it is in a module shared by integration tests")]
    [InlineData("benches/speed.rs", "#[test] fn in_a_bench() {}", "it is outside the package's src/ and tests/")]
    [InlineData("tests/api.rs", "#[test] fn open() {", "tests/api.rs cannot be read as Rust source (an unclosed '{' at offset 18)")]
    [InlineData("src/lib.rs", "pub fn words() {}\n#[cfg(test)] use std::fmt;\n#[cfg(test)] mod tests { #[test] fn t() {} }", "src/lib.rs:2 adds or changes a #[cfg(test)] item that is not a module")]
    [InlineData("src/lib.rs", "pub fn words() {}\nproptest! { #[test] fn p(x in 0..1u8) {} }", "src/lib.rs:2 adds or changes tests inside proptest! outside every #[cfg(test)] module")]
    public async Task Tests_the_gate_cannot_run_or_apply_to_the_base_fail_the_check_as_unsupported(string path, string content, string why)
    {
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "pub fn words() {}\n")), (path, content));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData("[[test]]\nname = \"api\"\npath = \"tests/api.rs\"\n", "Cargo.toml declares its integration tests itself ([[test]])")]
    [InlineData("autotests = false\n", "Cargo.toml sets autotests")]
    public async Task A_package_with_a_custom_target_layout_is_unsupported(string manifest, string why)
    {
        var custom = manifest.StartsWith('[') ? Manifest + manifest : Manifest.Replace("edition", manifest + "edition");
        var runner = With(Runner(("Cargo.toml", custom), ("src/lib.rs", "pub fn words() {}\n")), ("tests/api.rs", "#[test] fn t() {}"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains(why, result.Reason);
    }

    [Fact]
    public async Task A_rust_test_outside_any_cargo_package_is_unsupported_and_a_rust_file_without_tests_is_nothing()
    {
        var runner = With(Runner(("README.md", "x")), ("tools/check.rs", "#[test] fn orphan() {}"), ("tools/gen.rs", "fn main() {}"));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("tools/check.rs has the new test orphan, which the gate cannot run: it is in no cargo package", result.Reason);
        Assert.DoesNotContain("gen.rs", result.Reason);
    }

    [Fact]
    public async Task A_repository_with_dotnet_and_rust_is_judged_by_each_stack_for_its_own_tests()
    {
        var runner = new FakeTestRunner();
        runner.BaseFiles["rust/Cargo.toml"] = Manifest;
        runner.BaseFiles["rust/src/lib.rs"] = Lib;
        var rust = Lib.Replace("}\n}\n", "}\n    #[test]\n    fn blank() {}\n}\n");
        runner.Answer = spec => spec.Strategy is CargoNewTests ? Report(spec, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed)
            : FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);

        // Only .NET changes: the xUnit strategy alone, exactly as before.
        var dotnet = await Check(runner);
        Assert.Equal((NewTestsOutcome.Pass, "dotnet-xunit"), (dotnet.Outcome, dotnet.Strategy));
        Assert.All(runner.Runs, r => Assert.IsType<XunitNewTests>(r.Strategy));
        Assert.All(runner.Runs.SelectMany(r => r.Steps), s => Assert.Equal("dotnet", s.Program));

        // Both: each stack runs its own tests, and the result names both.
        runner.Runs.Clear();
        runner.HeadFiles = _ => new Dictionary<string, string>(runner.BaseFiles, StringComparer.Ordinal) { [FakeTestRunner.NewTestFile] = FakeTestRunner.NewTestSource, ["rust/src/lib.rs"] = rust };
        var both = await Check(runner);
        Assert.Equal((NewTestsOutcome.Pass, "dotnet-xunit, rust-cargo"), (both.Outcome, both.Strategy));
        Assert.Equal([FakeTestRunner.NewTest, "words:lib:tests::blank"], both.Tests.Select(t => t.Test));
        Assert.Equal(4, runner.Runs.Count);

        // A Rust test that already passes on the base rejects the whole check, named.
        runner.Runs.Clear();
        runner.Answer = spec => spec.Strategy is CargoNewTests ? Report(spec, TestCaseResult.Passed)
            : FakeTestRunner.Report(spec.Steps, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);
        var rejected = await Check(runner);
        Assert.Equal(NewTestsOutcome.Rejected, rejected.Outcome);
        Assert.EndsWith("so they check nothing: words:lib:tests::blank", rejected.Reason);
    }

    [Fact]
    public async Task Another_stacks_test_file_inside_a_cargo_package_is_still_unsupported()
    {
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "pub fn words() {}\n")),
            ("tests/api.rs", "#[test] fn t() {}\n"), ("tests/check_test.py", "def test_x(): pass\n"), ("tests/data/sample.json", "{}"));

        var set = await Identify(runner);
        var result = await Check(runner);

        // The data file is applied with the integration tests; the Python test is no cargo test and nobody runs it.
        Assert.Equal(["tests/api.rs", "tests/data/sample.json"], set.TestFiles.Select(f => f.NewPath).Order(StringComparer.Ordinal));
        Assert.Equal(NewTestsOutcome.Unsupported, result.Outcome);
        Assert.Contains("tests/check_test.py", result.Reason);
    }

    // ---- The overlay --------------------------------------------------------------------------------------------------

    private static string Graft(string? before, string head, List<string>? problems = null, params (string Name, string Text)[] declarations) =>
        CargoNewTests.Graft(before, RustSource.Parse(head), declarations, problems ?? [], "src/lib.rs");

    [Fact]
    public void The_graft_keeps_the_base_production_code_and_takes_the_heads_test_code()
    {
        const string before = """
            pub fn words(s: &str) -> usize { s.split(' ').count() }
            #[test] fn bare() { assert_eq!(words("a"), 1); }
            #[cfg(test)]
            mod tests {
                use super::*;
                #[test] fn two() {}
            }
            #[cfg(test)]
            mod dropped { #[test] fn old() {} }
            mod inner {
                pub fn base_only() {}
                #[cfg(test)] mod tests { #[test] fn inner_old() {} }
            }
            """;
        const string head = """
            pub fn words(s: &str) -> usize { s.split_whitespace().count() }
            pub fn added() {}
            #[test] fn bare() { assert_eq!(words("a b"), 2); }
            #[test] fn bare_new() { added(); }
            #[cfg(test)]
            mod tests {
                use super::*;
                fn helper() {}
                #[test] fn two() { helper(); }
                #[test] fn blank() {}
            }
            mod inner {
                pub fn head_only() {}
                #[cfg(test)] mod tests { #[test] fn inner_new() {} }
            }
            mod fresh {
                pub fn production() {}
                #[cfg(test)] mod tests { #[test] fn in_fresh() {} }
            }
            #[cfg(test)]
            mod support;
            """;

        var grafted = Graft(before, head);

        Assert.Equal("""
            pub fn words(s: &str) -> usize { s.split(' ').count() }
            #[test] fn bare() { assert_eq!(words("a b"), 2); }
            #[cfg(test)]
            mod tests {
                use super::*;
                fn helper() {}
                #[test] fn two() { helper(); }
                #[test] fn blank() {}
            }

            mod inner {
                pub fn base_only() {}
                #[cfg(test)] mod tests { #[test] fn inner_new() {} }
            }
            #[test] fn bare_new() { added(); }

            mod fresh {
            #[cfg(test)] mod tests { #[test] fn in_fresh() {} }
            }

            #[cfg(test)]
            mod support;

            """, grafted);
        // The same test code already there: nothing to write.
        Assert.Equal(before, Graft(before, before.Replace("s.split(' ')", "s.split_whitespace()")));
    }

    [Fact]
    public void A_file_the_base_lacks_is_written_with_its_test_code_alone_and_the_declarations_it_needs()
    {
        const string head = """
            use std::fmt;
            pub fn production() -> u32 { 1 }
            #[cfg(test)] use std::collections::HashMap;
            fn main() { production(); }
            mod inner { pub fn p() {} #[cfg(test)] mod tests { #[test] fn t() {} } }
            #[cfg(test)]
            mod tests { use super::*; #[test] fn uses_production() { assert_eq!(production(), 1); } }
            """;

        var only = CargoNewTests.Graft(null, RustSource.Parse(head), [("child", "pub mod child;")], [], "src/main.rs", emptyMain: true);

        Assert.Equal("""
            #[cfg(test)] use std::collections::HashMap;
            mod inner {
            #[cfg(test)] mod tests { #[test] fn t() {} }
            }
            #[cfg(test)]
            mod tests { use super::*; #[test] fn uses_production() { assert_eq!(production(), 1); } }
            pub mod child;
            fn main() {}

            """, only);
    }

    [Fact]
    public void A_declaration_is_added_to_the_base_only_when_it_has_no_module_of_that_name()
    {
        Assert.Equal("mod a;\n\nmod b;\n", Graft("mod a;\n", "mod a;\nmod b;\n", null, ("a", "mod a;"), ("b", "mod b;")));
    }

    [Fact]
    public async Task A_new_production_file_with_tests_is_applied_as_its_tests_alone_with_its_declaration_added_to_the_bases_parent()
    {
        const string trim = "pub fn trim(s: &str) -> &str { s.trim() }\n#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn trims() { assert_eq!(trim(\" a \"), \"a\"); }\n}\n";
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", Lib)), ("src/lib.rs", "pub mod text;\n" + Lib), ("src/text/mod.rs", "pub mod trim;\n"), ("src/text/trim.rs", trim));

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        Assert.Equal(["words:lib:text::trim::tests::trims"], set.Tests);
        // The test module without the function it tests, reachable from the base's library: src/lib.rs → text → trim.
        Assert.Equal("#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn trims() { assert_eq!(trim(\" a \"), \"a\"); }\n}\n", set.BaseReplacements["src/text/trim.rs"]);
        Assert.Equal("pub mod trim;\n", set.BaseReplacements["src/text/mod.rs"]);
        Assert.Equal(Lib + "\npub mod text;\n", set.BaseReplacements["src/lib.rs"]);
        Assert.Equal(["src/lib.rs", "src/text/mod.rs", "src/text/trim.rs"], set.TestFiles.Select(f => f.NewPath).Order(StringComparer.Ordinal));
        // Nothing of the base is deleted for it.
        Assert.All(set.TestFiles, f => Assert.True(f.OldPath is null || f.OldPath == f.NewPath));
    }

    [Fact]
    public async Task A_cfg_test_module_file_is_test_code_as_a_whole_and_its_declaration_is_grafted()
    {
        var runner = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "pub fn words() {}\n")),
            ("src/lib.rs", "pub fn words() {}\npub fn more() {}\n#[cfg(test)]\nmod tests;\n"), ("src/tests.rs", "use super::*;\nmod helpers;\n#[test] fn out_of_line() { words(); }\n"),
            ("src/tests/helpers.rs", "pub fn help() {}\n"));

        var set = await Identify(runner);

        Assert.Equal(["words:lib:tests::out_of_line"], set.Tests);
        Assert.Equal(["src/tests.rs", "src/tests/helpers.rs"], set.WholeFiles.Order(StringComparer.Ordinal));
        Assert.Equal("pub fn words() {}\n\n#[cfg(test)]\nmod tests;\n", Assert.Single(set.BaseReplacements).Value);
    }

    [Fact]
    public async Task Dev_dependencies_are_test_code_and_a_new_packages_manifest_is_applied_whole()
    {
        const string dev = "\n[dependencies]\nserde = \"1\"\n\n[dev-dependencies]\npretty_assertions = \"1\"\n";
        var runner = With(Runner(("Cargo.toml", Manifest + "\n[dependencies]\n"), ("src/lib.rs", "pub fn words() {}\n")),
            ("Cargo.toml", Manifest + dev), ("tests/api.rs", "#[test] fn t() {}\n"),
            ("extra/Cargo.toml", Manifest.Replace("words", "extra")), ("extra/src/lib.rs", "pub fn e() {}\n"), ("extra/tests/it.rs", "#[test] fn u() {}\n"));

        var set = await Identify(runner);

        Assert.Empty(set.Unisolable);
        // The base's dependencies, the head's dev-dependencies.
        Assert.Equal(Manifest + "\n[dependencies]\n\n[dev-dependencies]\npretty_assertions = \"1\"\n", set.BaseReplacements["Cargo.toml"]);
        Assert.DoesNotContain("serde", set.BaseReplacements["Cargo.toml"]);
        Assert.Contains(new ChangedFile('A', null, "extra/Cargo.toml"), set.TestFiles);
        Assert.DoesNotContain("extra/Cargo.toml", set.BaseReplacements.Keys);
        Assert.Equal("", set.BaseReplacements["extra/src/lib.rs"]); // the new library with no test code: empty, so the package builds

        var dotted = With(Runner(("Cargo.toml", Manifest), ("src/lib.rs", "pub fn words() {}\n")), ("Cargo.toml", Manifest + "dev-dependencies.x = \"1\"\n"), ("tests/api.rs", "#[test] fn t() {}\n"));
        Assert.Contains("Cargo.toml writes dev-dependencies as dotted keys", Assert.Single((await Identify(dotted)).Unisolable));
    }
}

/// <summary>sc-25875: cargo's commands, and reading its results and build errors (recorded output of cargo 1.97).</summary>
public class CargoRunOutputTests
{
    private static readonly CargoNewTests Cargo = new();

    [Fact]
    public async Task The_steps_probe_fetch_build_each_target_once_and_run_each_new_test_by_its_exact_name()
    {
        string[] tests = ["words:lib:count::tests::blank", "words:test/api:deep::nested", "words:lib:tests::sizes::*", "tool:bins:tests::runs"];
        var projects = tests.ToDictionary(t => t, t => t.StartsWith("tool", StringComparison.Ordinal) ? "rust/crates/tool/Cargo.toml" : "rust/crates/words/Cargo.toml");

        var steps = await Cargo.StepsAsync(new TestSource(new FakeTestRunner(), new RepoRef("a", "b"), "base", "head"), "head", tests, projects, ".factory-test-results-x", CancellationToken.None);

        Assert.All(steps, s => Assert.Equal("cargo", s.Program));
        Assert.Equal(
        [
            "Toolchain: --version",
            "Restore: fetch --manifest-path ./rust/crates/tool/Cargo.toml",
            "Restore: fetch --manifest-path ./rust/crates/words/Cargo.toml",
            "Build: test --manifest-path ./rust/crates/tool/Cargo.toml -p tool --no-run --message-format=json --bins",
            "Build: test --manifest-path ./rust/crates/words/Cargo.toml -p words --no-run --message-format=json --lib --test api",
            "Test: test --manifest-path ./rust/crates/tool/Cargo.toml -p tool --bins --color never --no-fail-fast -- --color never --exact tests::runs",
            "Test: test --manifest-path ./rust/crates/words/Cargo.toml -p words --lib --color never --no-fail-fast -- --color never --exact count::tests::blank",
            "Test: test --manifest-path ./rust/crates/words/Cargo.toml -p words --test api --color never --no-fail-fast -- --color never --exact deep::nested",
            "Test: test --manifest-path ./rust/crates/words/Cargo.toml -p words --lib --color never --no-fail-fast -- --color never tests::sizes::",
        ], steps.Select(s => $"{s.Phase}: {string.Join(' ', s.Args)}"));
        // Every result comes from standard output, kept under the test's id; no file is read.
        Assert.Equal(["cargo-build-0", "cargo-build-1", "tool:bins:tests::runs", "words:lib:count::tests::blank", "words:test/api:deep::nested", "words:lib:tests::sizes::*"],
            steps.Select(s => s.CaptureOutput).OfType<string>());
        Assert.All(steps, s => Assert.Null(s.ResultFilePattern));
    }

    private static string Output(string lines, string summary) => $"\nrunning 1 test\n{lines}\n\n{summary}\n\n";

    private const string Ok = "test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 4 filtered out; finished in 0.01s";
    private const string Failed = "test result: FAILED. 0 passed; 1 failed; 0 ignored; 0 measured; 4 filtered out; finished in 0.02s";

    /// <summary>cargo 1.97's output for a failing assertion (the panic message and the failures list included).</summary>
    private const string Panicked = """

        running 1 test
        test tests::blank ... FAILED

        failures:

        ---- tests::blank stdout ----

        thread 'tests::blank' (112166117) panicked at crates/words/src/lib.rs:9:18:
        assertion `left == right` failed: blank
          left: 3
         right: 0
        note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace


        failures:
            tests::blank

        test result: FAILED. 0 passed; 1 failed; 0 ignored; 0 measured; 4 filtered out; finished in 0.02s

        """;

    private static TestResults Parse(string id, string output, int? exit) => Cargo.ParseResults([new ResultFile(id, output) { ExitCode = exit }]);

    [Fact]
    public void A_passing_a_failing_a_panicking_and_an_ignored_test_are_read_from_libtests_lines()
    {
        var passed = Parse("words:lib:tests::two", Output("test tests::two ... ok", Ok), 0);
        var failed = Parse("words:lib:tests::blank", Panicked, 101);
        var shouldPanic = Parse("words:lib:tests::pan", Output("test tests::pan - should panic ... ok", Ok), 0);
        var ignored = Parse("words:lib:tests::ign", Output("test tests::ign ... ignored", Ok.Replace("1 passed", "0 passed").Replace("0 ignored", "1 ignored")), 0);
        var reasoned = Parse("words:lib:tests::ign", Output("test tests::ign ... ignored, slow", Ok.Replace("1 passed", "0 passed").Replace("0 ignored", "1 ignored")), 0);

        Assert.Equal([new TestCaseResult("tests::two", "passed")], passed.Cases["words:lib:tests::two"]);
        Assert.Equal([new TestCaseResult("tests::blank", "failed")], failed.Cases["words:lib:tests::blank"]);
        Assert.Equal([new TestCaseResult("tests::pan", "passed")], shouldPanic.Cases["words:lib:tests::pan"]);
        Assert.Equal([new TestCaseResult("tests::ign", "skipped")], ignored.Cases["words:lib:tests::ign"]);
        Assert.Equal("skipped", reasoned.Cases["words:lib:tests::ign"].Single().Outcome);
        Assert.All(new[] { passed, failed, shouldPanic, ignored, reasoned }, r => Assert.Empty(r.Unreadable));
    }

    [Fact]
    public void A_run_that_ran_no_test_gives_no_case_so_the_test_is_not_run_and_never_passed()
    {
        const string none = "\nrunning 0 tests\n\ntest result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 5 filtered out; finished in 0.00s\n\n";

        var results = Parse("words:lib:tests::nothere", none, 0);

        Assert.Empty(results.Cases);
        Assert.Empty(results.Unreadable);
        // Judged: no result on a base that ran is an error, and on the head the test does not pass.
        var ran = new TestRunReport(TestRunStatus.Ran, results.Cases, "log");
        var failing = new TestRunReport(TestRunStatus.Ran, Parse("words:lib:tests::nothere", Panicked.Replace("tests::blank", "tests::nothere"), 101).Cases, "log");
        Assert.Equal(NewTestsOutcome.Error, NewTestsCheck.Judge("base0", "head1", "rust-cargo", ["words:lib:tests::nothere"], ran, ran with { Results = Parse("words:lib:tests::nothere", Output("test tests::nothere ... ok", Ok), 0).Cases }).Outcome);
        Assert.Equal(NewTestsOutcome.Rejected, NewTestsCheck.Judge("base0", "head1", "rust-cargo", ["words:lib:tests::nothere"], failing, ran).Outcome);
    }

    [Fact]
    public void A_parameterized_test_has_every_case_under_its_name_and_only_those()
    {
        const string output = "\nrunning 3 tests\ntest tests::sizes::one ... ok\ntest tests::sizes::two ... FAILED\ntest other::tests::sizes::x ... ok\n\n"
            + "test result: FAILED. 2 passed; 1 failed; 0 ignored; 0 measured; 4 filtered out; finished in 0.02s\n";

        var results = Parse("words:lib:tests::sizes::*", output, 101);

        Assert.Equal([new TestCaseResult("tests::sizes::one", "passed"), new TestCaseResult("tests::sizes::two", "failed")], results.Cases["words:lib:tests::sizes::*"]);
    }

    [Theory]
    // A passing test that prints a failure line (and even a summary) of its own: cargo's exit code says nothing failed.
    [InlineData("test tests::two ... FAILED\ntest tests::two ... ok", Ok, 0, "its test lines do not add up to its summaries")]
    [InlineData("test tests::two ... FAILED\n" + Failed + "\ntest tests::two ... ok", Ok, 0, "it reports one test with two different results")]
    [InlineData("test tests::two ... FAILED", Failed, 0, "it reports a failed test but cargo exited with code 0")]
    // cargo failed, but no test is reported failed (it did not build, or the harness died).
    [InlineData("test tests::two ... ok", Ok, 101, "cargo exited with code 101 but no test is reported as failed")]
    [InlineData("error: could not compile `words` (lib test) due to 1 previous error", "", 101, "cargo test printed no test result summary (exit code 101)")]
    [InlineData("test tests::two ... ok", "", 0, "cargo test printed no test result summary (exit code 0)")]
    [InlineData("{ \"type\": \"test\", \"event\": \"ok\", \"name\": \"tests::two\" }", "", 0, "cargo test printed no test result summary")]
    public void An_output_that_cannot_be_trusted_is_an_unreadable_report_never_a_result(string lines, string summary, int exit, string why)
    {
        var results = Parse("words:lib:tests::two", Output(lines, summary), exit);

        Assert.Empty(results.Cases);
        Assert.Contains(why, Assert.Single(results.Unreadable));
        Assert.StartsWith("words:lib:tests::two: ", results.Unreadable[0]);
        // And the check is an error, on either commit.
        var unreadable = new TestRunReport(TestRunStatus.Ran, results.Cases, "log") { UnreadableReports = results.Unreadable };
        var good = new TestRunReport(TestRunStatus.Ran, Parse("words:lib:tests::two", Panicked.Replace("tests::blank", "tests::two"), 101).Cases, "log");
        Assert.Equal(NewTestsOutcome.Error, NewTestsCheck.Judge("base0", "head1", "rust-cargo", ["words:lib:tests::two"], unreadable, good).Outcome);
        Assert.Equal(NewTestsOutcome.Error, NewTestsCheck.Judge("base0", "head1", "rust-cargo", ["words:lib:tests::two"], good, unreadable).Outcome);
    }

    [Fact]
    public void An_output_without_an_exit_code_or_under_a_name_that_is_no_test_id_is_unreadable()
    {
        Assert.Contains("cargo's exit code is unknown", Assert.Single(Parse("words:lib:tests::two", Output("test tests::two ... ok", Ok), null).Unreadable));
        Assert.Contains("is not the output of a cargo test run", Assert.Single(Parse("results.xunit.xml", Output("test tests::two ... ok", Ok), 0).Unreadable));
    }

    /// <summary>One line of <c>cargo test --no-run --message-format=json</c> (cargo 1.97; the code's long explanation cut).</summary>
    private static string Message(string file, int line, int column, string? code, string message, string manifest = "/work/wt/rust/crates/words/Cargo.toml") =>
        """{"reason":"compiler-message","package_id":"path+file:///work/wt/rust/crates/words#0.1.0","manifest_path":"MANIFEST","target":{"kind":["lib"],"crate_types":["lib"],"name":"words","src_path":"/work/wt/rust/crates/words/src/lib.rs","edition":"2021","doc":true,"doctest":true,"test":true},"message":{"rendered":"error","$message_type":"diagnostic","children":[],"level":"error","message":"MESSAGE","spans":[{"byte_end":389,"byte_start":385,"column_end":99,"column_start":COLUMN,"expansion":null,"file_name":"FILE","is_primary":true,"label":"not found in this scope","line_end":LINE,"line_start":LINE,"suggested_replacement":null,"suggestion_applicability":null,"text":[]}],"code":CODE}}"""
            .Replace("MANIFEST", manifest).Replace("MESSAGE", message).Replace("COLUMN", column.ToString()).Replace("FILE", file).Replace("LINE", line.ToString())
            .Replace("CODE", code is null ? "null" : "{\"code\":\"" + code + "\",\"explanation\":\"…\"}");

    private const string Aborting = """{"reason":"compiler-message","package_id":"p","manifest_path":"/work/wt/rust/crates/words/Cargo.toml","target":{},"message":{"rendered":"error: aborting due to 1 previous error\n\n","$message_type":"diagnostic","children":[],"level":"error","message":"aborting due to 1 previous error","spans":[],"code":null}}""";

    [Fact]
    public void Build_errors_are_read_from_cargos_json_messages_with_files_placed_from_the_workspace_root()
    {
        var log = string.Join('\n',
            """{"reason":"compiler-artifact","package_id":"p","target":{},"filenames":[]}""",
            Message("crates/words/src/lib.rs", 17, 31, "E0425", "cannot find function `nope` in this scope"),
            Message("crates/words/src/lib.rs", 16, 5, null, "expected item after attributes"),
            Message("src/lib.rs", 3, 1, "E0432", "unresolved import", "/real/wt/solo/Cargo.toml"),
            Message("/home/u/.cargo/registry/src/x/lib.rs", 1, 1, "E0001", "in a dependency"),
            """{"reason":"compiler-message","package_id":"p","manifest_path":"/work/wt/rust/crates/words/Cargo.toml","target":{},"message":{"level":"warning","message":"unused","spans":[],"code":null}}""",
            Aborting,
            "   Compiling words v0.1.0 (not JSON)",
            """{"reason":"build-finished","success":false}""");

        var errors = Cargo.ParseBuildErrors(log, ["/work/wt", "/real/wt"]);

        Assert.Equal(
        [
            new BuildError("rust/crates/words/src/lib.rs", 17, 31, "E0425", "cannot find function `nope` in this scope"),
            new BuildError("rust/crates/words/src/lib.rs", 16, 5, "rustc", "expected item after attributes"),
            new BuildError("solo/src/lib.rs", 3, 1, "E0432", "unresolved import"),
            new BuildError(null, 0, 0, "E0001", "in a dependency"),
        ], errors);
    }

    // ---- A base that does not build ---------------------------------------------------------------------------------------

    private const string Manifest = "[package]\nname = \"words\"\nversion = \"0.1.0\"\n";
    private const string BaseLib = "pub fn words(s: &str) -> usize { s.split(' ').count() }\n";

    /// <summary>The head: a new function, and a test module with a helper and three tests (lines 3 to 10).</summary>
    private const string HeadLib = """
        pub fn words(s: &str) -> usize { s.split_whitespace().count() }
        pub fn trim(s: &str) -> &str { s.trim() }
        #[cfg(test)]
        mod tests {
            use super::*;
            fn helper() -> &'static str { trim(" a ") }
            #[test] fn trims() { assert_eq!(trim(" a "), "a"); }
            #[test] fn helped() { assert_eq!(helper(), "a"); }
            #[test] fn blank() { assert_eq!(words("  "), 0); }
        }

        """;

    private static readonly RepoRef Repo = new("acme", "words");

    private static FakeTestRunner Words() =>
        CargoIdentifyTests.With(CargoIdentifyTests.Runner(("Cargo.toml", Manifest), ("src/lib.rs", BaseLib), ("src/other.rs", "\n")), ("src/lib.rs", HeadLib));

    private static Task<NewTestsResult> Check(FakeTestRunner runner) =>
        NewTestsCheck.RunAsync(runner, NewTestsCheck.Strategies, Repo, FakeTestRunner.BaseSha, "head1", "gate-sc-1", null, CancellationToken.None);

    /// <summary>The line (1-based) of the grafted base file that holds <paramref name="text"/>.</summary>
    private static int LineOf(TestRunSpec spec, string text)
    {
        var file = spec.Replacements["src/lib.rs"];
        return file[..file.IndexOf(text, StringComparison.Ordinal)].Count(c => c == '\n') + 1;
    }

    private static BuildError Error(TestRunSpec spec, string text, string path = "src/lib.rs") => new(path, path == "src/lib.rs" ? LineOf(spec, text) : 1, 12, "E0425", "cannot find function");

    [Fact]
    public async Task A_new_test_whose_own_code_does_not_compile_on_the_base_fails_there_and_the_others_get_real_results_from_a_retry()
    {
        var runner = Words();
        runner.Answer = spec => spec.Name.EndsWith("-base", StringComparison.Ordinal)
            ? FakeTestRunner.NotBuilt(Error(spec, "#[test] fn trims"), Error(spec, "fn helper"))
            : CargoIdentifyTests.Report(spec, spec.OverlayFrom is null ? TestCaseResult.Passed : TestCaseResult.Failed);

        var result = await Check(runner);

        // trims calls the function the PR adds: it cannot pass on the base. The helper is left out of the retry, in which
        // helped (which calls it) does not compile either, and blank gets a real result.
        Assert.True(result.Outcome == NewTestsOutcome.Pass, result.Reason);
        Assert.Equal(("build-failed", "ran", "ran"), (result.BaseRun, result.BaseRetry, result.HeadRun));
        Assert.Equal(["src/lib.rs:6 fn helper", "src/lib.rs:7 fn trims"], result.BaseRemoved);
        var retry = runner.Runs.Single(r => r.Name == "gate-sc-1-base-retry");
        Assert.DoesNotContain("fn trims", retry.Replacements["src/lib.rs"]);
        Assert.DoesNotContain("fn helper", retry.Replacements["src/lib.rs"]);
        Assert.Contains("s.split(' ').count()", retry.Replacements["src/lib.rs"]); // still the base's production code
        Assert.Contains("#[test] fn blank()", retry.Replacements["src/lib.rs"]);
        Assert.Equal(["words:lib:tests::helped", "words:lib:tests::blank"], retry.Steps.Where(s => s.Phase == TestPhase.Test).Select(s => s.CaptureOutput));
        Assert.Equal([("words:lib:tests::trims", "not-built"), ("words:lib:tests::helped", "failed"), ("words:lib:tests::blank", "failed")], result.Tests.Select(t => (t.Test, t.Base)));
    }

    [Fact]
    public async Task A_test_that_passes_on_the_base_cannot_hide_behind_another_tests_compile_error()
    {
        var runner = Words();
        runner.Answer = spec => spec.Name.EndsWith("-base", StringComparison.Ordinal) ? FakeTestRunner.NotBuilt(Error(spec, "#[test] fn trims"))
            : spec.Name.EndsWith("-base-retry", StringComparison.Ordinal) ? CargoIdentifyTests.Report(spec, TestCaseResult.Passed)
            : CargoIdentifyTests.Report(spec, TestCaseResult.Passed);

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Rejected, result.Outcome);
        Assert.EndsWith("so they check nothing: words:lib:tests::helped, words:lib:tests::blank", result.Reason);
    }

    [Theory]
    [InlineData("src/lib.rs", "pub fn words")] // the base's own production code
    [InlineData("src/other.rs", "")] // a file the run did not apply (e.g. another module's test that the base cannot build)
    [InlineData(null, "")] // no file: a manifest, dependency or linker error
    public async Task A_base_that_fails_to_build_for_a_reason_outside_the_prs_test_code_is_an_error(string? path, string at)
    {
        var runner = Words();
        runner.Answer = spec => spec.OverlayFrom is null ? CargoIdentifyTests.Report(spec, TestCaseResult.Passed)
            : FakeTestRunner.NotBuilt(path is null ? new BuildError(null, 0, 0, "rustc", "linking with `cc` failed") : Error(spec, at, path));

        var result = await Check(runner);

        Assert.Equal(NewTestsOutcome.Error, result.Outcome);
        Assert.Contains("for a reason that says nothing about the new tests", result.Reason);
        Assert.DoesNotContain(runner.Runs, r => r.Name.EndsWith("-retry", StringComparison.Ordinal));
        Assert.All(result.Tests, t => Assert.NotEqual("not-built", t.Base));
    }

    [Fact]
    public async Task A_base_build_that_names_no_compiler_error_or_a_missing_toolchain_is_an_error_never_a_pass()
    {
        var runner = Words();
        runner.Answer = spec => spec.OverlayFrom is null ? CargoIdentifyTests.Report(spec, TestCaseResult.Passed) : FakeTestRunner.NotBuilt();
        var unnamed = await Check(runner);
        Assert.Equal(NewTestsOutcome.Error, unnamed.Outcome);
        Assert.Contains("the build failed without naming a compiler error", unnamed.Reason);

        runner.Answer = _ => TestRunReport.Failed(TestRunStatus.ToolchainMissing, "cargo is not available to the gate's test runs as _factory (`cargo --version` exited 127)");
        var missing = await Check(runner);
        Assert.Equal(NewTestsOutcome.Error, missing.Outcome);
        Assert.Contains("(toolchain-missing): cargo is not available to the gate's test runs as _factory", missing.Reason);
        Assert.Equal("toolchain-missing", missing.HeadRun);
    }

    [Fact]
    public async Task An_error_between_a_test_modules_items_leaves_the_tests_unproven_and_one_in_a_whole_test_file_is_attributed()
    {
        var runner = Words();
        runner.Answer = spec => spec.OverlayFrom is null ? CargoIdentifyTests.Report(spec, TestCaseResult.Passed)
            : FakeTestRunner.NotBuilt(new BuildError("src/lib.rs", LineOf(spec, "mod tests {"), 11, "rustc", "in the module's body"));
        var unproven = await Check(runner);
        Assert.Equal(NewTestsOutcome.Rejected, unproven.Outcome);
        Assert.Contains("could not be shown to fail on the base", unproven.Reason);

        var integration = CargoIdentifyTests.With(CargoIdentifyTests.Runner(("Cargo.toml", Manifest), ("src/lib.rs", BaseLib)),
            ("tests/api.rs", "use words::trim;\n#[test] fn trims() { assert_eq!(trim(\" a \"), \"a\"); }\n"));
        integration.Answer = spec => spec.Name.EndsWith("-base", StringComparison.Ordinal) ? FakeTestRunner.NotBuilt(new BuildError("tests/api.rs", 1, 5, "E0432", "unresolved import"))
            : spec.Name.EndsWith("-base-retry", StringComparison.Ordinal) ? FakeTestRunner.NotBuilt(new BuildError("tests/api.rs", 2, 34, "E0425", "cannot find function `trim`"))
            : CargoIdentifyTests.Report(spec, TestCaseResult.Passed);
        var attributed = await Check(integration);
        Assert.True(attributed.Outcome == NewTestsOutcome.Pass, attributed.Reason);
        Assert.Equal(["tests/api.rs:1 use words::trim;"], attributed.BaseRemoved);
        Assert.Equal("not-built", attributed.Tests.Single().Base);
    }
}

/// <summary>sc-25875: the runner's part — output kept in memory with its exit code, and a missing toolchain.</summary>
public class CargoRunnerTests : IDisposable
{
    private static readonly RepoRef Repo = new("acme", "words");
    private readonly string _root = Directory.CreateTempSubdirectory("df-cargorun-").FullName;
    private readonly string _remote;
    private readonly string _seed;

    public CargoRunnerTests()
    {
        _remote = Path.Combine(_root, "remote.git");
        _seed = Path.Combine(_root, "seed");
        TestGit.Run(_root, "init", "--bare", "-b", "main", _remote);
        TestGit.Run(_root, "init", "-b", "main", _seed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    private GitWorkspace Workspace() => new(Path.Combine(_root, "work"), _ => _remote, (_, _) => Task.FromResult<string?>(null));

    private async Task<(SandboxTestRunner Runner, string Sha)> ShRunner(long maxBytes = SandboxTestRunner.MaxResultFileBytes)
    {
        Write("a.txt", "a");
        var sha = CommitAndPush("base", "main");
        var git = Workspace();
        await git.ChangedFilesAsync(Repo, sha, sha, CancellationToken.None);
        return (new SandboxTestRunner(git, sandbox: null, TimeSpan.FromMinutes(1), maxBytes), sha);
    }

    private static TestStep Sh(TestPhase phase, string script, string? capture = null) => new(phase, "/bin/sh", ["-c", script]) { CaptureOutput = capture };

    private static TestRunSpec Spec(string sha, params TestStep[] steps) => new("gate-sc-1-head", sha, null, [], [], steps, new CargoNewTests(), NewTestsCheck.NewResultsDirectory());

    [Fact]
    public async Task A_steps_standard_output_is_its_result_with_its_exit_code_and_standard_error_is_not()
    {
        var (runner, sha) = await ShRunner();
        const string ok = "printf 'running 1 test\\ntest tests::two ... ok\\n\\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\\n'";
        const string failed = "printf 'test tests::blank ... FAILED\\n\\ntest result: FAILED. 0 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out\\n'; echo 'test tests::blank ... ok' >&2; exit 101";

        var report = await runner.RunAsync(Repo, Spec(sha, Sh(TestPhase.Toolchain, "exit 0"), Sh(TestPhase.Test, ok, "words:lib:tests::two"), Sh(TestPhase.Test, failed, "words:lib:tests::blank"),
            Sh(TestPhase.Test, ok + "; exit 3", "words:lib:tests::odd")), CancellationToken.None);

        Assert.Equal(TestRunStatus.Ran, report.Status);
        Assert.Equal("passed", report.Results["words:lib:tests::two"].Single().Outcome);
        Assert.Equal("failed", report.Results["words:lib:tests::blank"].Single().Outcome);
        Assert.Equal(["words:lib:tests::odd: cargo exited with code 3 but no test is reported as failed"], report.UnreadableReports);
    }

    [Fact]
    public async Task An_output_larger_than_the_limit_is_an_unreadable_report_and_a_failed_builds_output_is_its_error_log()
    {
        var (runner, sha) = await ShRunner(maxBytes: 64);
        var large = await runner.RunAsync(Repo, Spec(sha, Sh(TestPhase.Test, "i=0; while [ $i -lt 20 ]; do echo 'test tests::two ... ok'; i=$((i+1)); done", "words:lib:tests::two")), CancellationToken.None);
        Assert.Equal(["words:lib:tests::two: it is larger than 64 bytes, so it was not read"], large.UnreadableReports);

        var (plain, _) = (new SandboxTestRunner(Workspace(), sandbox: null, TimeSpan.FromMinutes(1)), sha);
        const string json = """{"reason":"compiler-message","manifest_path":"/elsewhere/Cargo.toml","message":{"level":"error","message":"boom","spans":[],"code":null}}""";
        var built = await plain.RunAsync(Repo, Spec(sha, Sh(TestPhase.Build, $"echo '{json}'; exit 101", "cargo-build-0"), Sh(TestPhase.Test, "echo never", "words:lib:tests::two")), CancellationToken.None);
        Assert.Equal(TestRunStatus.BuildFailed, built.Status);
        Assert.Equal([new BuildError(null, 0, 0, "rustc", "boom")], built.BuildErrors);
        Assert.DoesNotContain("never", built.Log);
    }

    [Fact]
    public async Task A_toolchain_that_is_not_installed_ends_the_run_as_missing_and_names_the_program()
    {
        var (runner, sha) = await ShRunner();

        var absent = await runner.RunAsync(Repo, Spec(sha, new TestStep(TestPhase.Toolchain, "df-no-such-cargo", ["--version"]), Sh(TestPhase.Test, "echo never", "words:lib:tests::two")), CancellationToken.None);
        var failing = await runner.RunAsync(Repo, Spec(sha, Sh(TestPhase.Toolchain, "exit 127"), Sh(TestPhase.Test, "echo never", "words:lib:tests::two")), CancellationToken.None);

        Assert.Equal(TestRunStatus.ToolchainMissing, absent.Status);
        Assert.Contains("df-no-such-cargo is not available to the gate's test runs (`df-no-such-cargo --version` exited 127)", absent.Log);
        Assert.Equal(TestRunStatus.ToolchainMissing, failing.Status);
        Assert.Empty(absent.Results);
        Assert.DoesNotContain("never", absent.Log + failing.Log);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    // ---- Real cargo (skipped where it is not installed) -------------------------------------------------------------------

    private static readonly Lazy<bool> HasCargo = new(() =>
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(CargoNewTests.Cargo, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private const string Manifest = "[package]\nname = \"words\"\nversion = \"0.1.0\"\nedition = \"2021\"\n";

    /// <summary>The base: <c>words</c> splits on every space (so blanks count as words), with one inline test.</summary>
    private string Base(string extra = "")
    {
        Write("Cargo.toml", Manifest);
        Write("src/lib.rs", extra + "pub fn words(s: &str) -> usize { s.split(' ').count() }\n#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn two() { assert_eq!(words(\"a b\"), 2); }\n}\n");
        var sha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        return sha;
    }

    private Task<NewTestsResult> Check(string baseSha, string headSha) =>
        NewTestsCheck.RunAsync(new SandboxTestRunner(Workspace(), sandbox: null, TimeSpan.FromMinutes(10)), NewTestsCheck.Strategies, Repo, baseSha, headSha, "gate-sc-5", null,
            TestContext.Current.CancellationToken);

    private const string FixedLib = "pub fn words(s: &str) -> usize { s.split_whitespace().count() }\n";

    [Fact]
    public async Task With_cargo_a_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named()
    {
        Assert.SkipUnless(HasCargo.Value, "cargo is not installed");
        var baseSha = Base();
        Write("src/lib.rs", FixedLib + "#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn two() { assert_eq!(words(\"a b\"), 2); }\n    #[test]\n    fn blank_has_no_words() { assert_eq!(words(\"  \"), 0); }\n}\n");
        Write("tests/api.rs", "#[test]\nfn spaces_between_words_do_not_count() { assert_eq!(words::words(\"a  b\"), 2); }\n");
        var passing = CommitAndPush("fix", "factory/sc-5");

        var pass = await Check(baseSha, passing);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal(("ran", "ran", "rust-cargo"), (pass.BaseRun, pass.HeadRun, pass.Strategy));
        Assert.Equal([("words:lib:tests::blank_has_no_words", "failed", "passed"), ("words:test/api:spaces_between_words_do_not_count", "failed", "passed")],
            pass.Tests.Select(t => (t.Test, t.Base, t.Head)).Order());

        Write("tests/api.rs", "#[test]\nfn spaces_between_words_do_not_count() { assert_eq!(words::words(\"a  b\"), 2); }\n#[test]\nfn one_word() { assert_eq!(words::words(\"a\"), 1); }\n");
        var vacuous = CommitAndPush("a test that checks nothing", "factory/sc-5");

        var rejected = await Check(baseSha, vacuous);

        Assert.True(rejected.Outcome == NewTestsOutcome.Rejected, $"{rejected.Outcome}: {rejected.Reason}");
        Assert.EndsWith("so they check nothing: words:test/api:one_word", rejected.Reason);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work", "worktrees", Repo.Owner, Repo.Name)));
    }

    [Fact]
    public async Task With_cargo_a_new_inline_test_calling_code_the_pull_request_adds_does_not_build_on_the_base_and_hides_no_test_that_passes_there()
    {
        Assert.SkipUnless(HasCargo.Value, "cargo is not installed");
        var baseSha = Base();
        const string tests = "#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn two() { assert_eq!(words(\"a b\"), 2); }\n    #[test]\n    fn trims() { assert_eq!(trim(\" a \"), \"a\"); }\n";
        Write("src/lib.rs", FixedLib + "pub fn trim(s: &str) -> &str { s.trim() }\n" + tests + "}\n");
        // A new module too: its file does not exist on the base, where its test is compiled without the function it tests.
        Write("src/lib.rs", "pub mod upper;\n" + File.ReadAllText(Path.Combine(_seed, "src/lib.rs")));
        Write("src/upper.rs", "pub fn upper(s: &str) -> String { s.to_uppercase() }\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn shouts() { assert_eq!(super::upper(\"a\"), \"A\"); }\n}\n");
        var headSha = CommitAndPush("trim and upper", "factory/sc-5");

        var pass = await Check(baseSha, headSha);

        Assert.True(pass.Outcome == NewTestsOutcome.Pass, $"{pass.Outcome}: {pass.Reason}");
        Assert.Equal("build-failed", pass.BaseRun);
        Assert.Equal([("words:lib:tests::trims", "not-built", "passed"), ("words:lib:upper::tests::shouts", "not-built", "passed")], pass.Tests.Select(t => (t.Test, t.Base, t.Head)).Order());

        Write("src/lib.rs", "pub mod upper;\n" + FixedLib + "pub fn trim(s: &str) -> &str { s.trim() }\n" + tests + "    #[test]\n    fn one_word() { assert_eq!(words(\"a\"), 1); }\n}\n");
        var vacuous = CommitAndPush("a test that checks nothing", "factory/sc-5");

        var rejected = await Check(baseSha, vacuous);

        Assert.True(rejected.Outcome == NewTestsOutcome.Rejected, $"{rejected.Outcome}: {rejected.Reason}");
        Assert.Equal(("build-failed", "ran"), (rejected.BaseRun, rejected.BaseRetry));
        Assert.EndsWith("so they check nothing: words:lib:tests::one_word", rejected.Reason);
        Assert.Equal("passed", rejected.Tests.Single(t => t.Test == "words:lib:tests::one_word").Base);
    }

    [Fact]
    public async Task With_cargo_tests_of_a_library_and_a_binary_in_a_workspace_below_the_repository_root_are_run_and_their_build_errors_placed()
    {
        Assert.SkipUnless(HasCargo.Value, "cargo is not installed");
        Write("rust/Cargo.toml", "[workspace]\nmembers = [\"crates/*\"]\nresolver = \"2\"\n");
        Write("rust/crates/words/Cargo.toml", Manifest);
        Write("rust/crates/words/src/lib.rs", "pub fn words(s: &str) -> usize { s.split(' ').count() }\n");
        Write("rust/crates/tool/Cargo.toml", Manifest.Replace("words", "tool"));
        Write("rust/crates/tool/src/main.rs", "fn main() { println!(\"{}\", double(1)); }\nfn double(x: u32) -> u32 { x + 1 }\n");
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        Write("rust/crates/words/src/lib.rs", FixedLib + "pub fn trim(s: &str) -> &str { s.trim() }\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn trims() { assert_eq!(super::trim(\" a \"), \"a\"); }\n}\n");
        Write("rust/crates/tool/src/main.rs", "fn main() { println!(\"{}\", double(1)); }\nfn double(x: u32) -> u32 { x * 2 }\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn doubles() { assert_eq!(super::double(2), 4); }\n}\n");
        var headSha = CommitAndPush("fix", "factory/sc-5");

        var result = await Check(baseSha, headSha);

        Assert.True(result.Outcome == NewTestsOutcome.Pass, $"{result.Outcome}: {result.Reason}");
        // The library's test calls a function the base lacks (rustc names the file from the workspace root, rust/, and
        // the error is placed in rust/crates/words/src/lib.rs); the binary's test gets its real failure from the retry.
        Assert.Equal([("tool:bins:tests::doubles", "failed", "passed"), ("words:lib:tests::trims", "not-built", "passed")], result.Tests.Select(t => (t.Test, t.Base, t.Head)).Order());
    }

    /// <summary>The seed of the live acceptance test P4-AT4 (<c>GateNegativeTests</c>), run here without the factory around it.</summary>
    [Fact]
    public async Task With_cargo_a_new_package_is_applied_to_the_base_with_its_test_code_alone()
    {
        Assert.SkipUnless(HasCargo.Value, "cargo is not installed");
        Write("README.md", "a repository with no Rust yet\n");
        var baseSha = CommitAndPush("base", "main");
        TestGit.Run(_seed, "checkout", "-q", "-b", "factory/sc-5");
        Write("e2e-rust/Cargo.toml", "[package]\nname = \"df-e2e-rust\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        Write("e2e-rust/src/lib.rs", "pub fn double(x: u32) -> u32 { x * 2 }\n\n#[cfg(test)]\nmod tests {\n    #[test]\n    fn doubles() { assert_eq!(super::double(2), 4); }\n\n"
            + "    #[test]\n    fn already_passes_on_the_base() { assert_eq!(1 + 1, 2); }\n}\n");
        var headSha = CommitAndPush("a new crate", "factory/sc-5");

        var result = await Check(baseSha, headSha);

        Assert.True(result.Outcome == NewTestsOutcome.Rejected, $"{result.Outcome}: {result.Reason}");
        Assert.Equal(("build-failed", "ran", "ran"), (result.BaseRun, result.BaseRetry, result.HeadRun));
        Assert.EndsWith("so they check nothing: df-e2e-rust:lib:tests::already_passes_on_the_base", result.Reason);
        Assert.Equal([("df-e2e-rust:lib:tests::already_passes_on_the_base", "passed", "passed"), ("df-e2e-rust:lib:tests::doubles", "not-built", "passed")],
            result.Tests.Select(t => (t.Test, t.Base, t.Head)).Order());
    }

    [Fact]
    public async Task With_cargo_a_base_whose_other_tests_do_not_build_is_an_error_not_evidence()
    {
        Assert.SkipUnless(HasCargo.Value, "cargo is not installed");
        // The base's library builds, but a test of another module (which the PR does not touch) calls a function that is not there yet.
        Write("src/other.rs", "#[cfg(test)]\nmod tests {\n    #[test]\n    fn waits() { crate::later(); }\n}\n");
        var baseSha = Base("mod other;\n");
        Write("src/lib.rs", "mod other;\npub fn later() {}\n" + FixedLib + "#[cfg(test)]\nmod tests {\n    use super::*;\n    #[test]\n    fn blank_has_no_words() { assert_eq!(words(\"  \"), 0); }\n}\n");
        var headSha = CommitAndPush("fix", "factory/sc-5");

        var result = await Check(baseSha, headSha);

        Assert.True(result.Outcome == NewTestsOutcome.Error, $"{result.Outcome}: {result.Reason}");
        Assert.Contains("for a reason that says nothing about the new tests", result.Reason);
        Assert.Contains("src/other.rs(4,", result.Reason);
    }
}
