using System.Text.Json;
using DarkFactory.Orchestrator.Sessions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>Recorded-shape stream-json transcripts for the stuck detector (sc-25388).</summary>
internal static class StuckFixtures
{
    /// <summary>
    /// A Read, then the same failing Edit six times (its wording differing only in whitespace and a counter; each with a thinking
    /// line of its own message id, as real streams send it), then a final turn. As in real streams, the router's banner is on the
    /// first message only and its feedback footer on the last.
    /// </summary>
    public static string[] Loop => Lines("stream-json-stuck-loop.jsonl");

    /// <summary>A Read, then six cycles of a different Edit followed by the same <c>dotnet test</c> command, whose output improves each time.</summary>
    public static string[] EditTestCycle => Lines("stream-json-edit-test-cycle.jsonl");

    /// <summary>A Read, a slow failing test run, an Edit, a slow passing test run, a final turn (replayed with a long silence).</summary>
    public static string[] SilenceThenProgress => Lines("stream-json-silence-progress.jsonl");

    public const string LoopSession = "5d1c0a7e-1111-4a51-9c11-000000000001";
    public const string CycleSession = "5d1c0a7e-2222-4a51-9c11-000000000002";
    public const string SilenceSession = "5d1c0a7e-3333-4a51-9c11-000000000003";

    private static string[] Lines(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The index of each line that is a tool result.</summary>
    public static List<int> ResultLines(string[] lines) =>
        lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("\"tool_result\"")).Select(x => x.i).ToList();

    /// <summary>The index of each line that is an assistant tool call.</summary>
    public static List<int> ToolUseLines(string[] lines) =>
        lines.Select((l, i) => (l, i)).Where(x => x.l.Contains("\"type\": \"tool_use\"")).Select(x => x.i).ToList();
}

public class StuckDetectorTests
{
    /// <summary>The index of the line on which the detector trips, or null.</summary>
    private static (int? Line, string? Reason) Replay(string[] lines, StuckDetection? options = null)
    {
        var detector = new StuckDetector(options ?? StuckDetection.Default);
        for (var i = 0; i < lines.Length; i++)
        {
            if (detector.Accept(lines[i]) is { } reason)
            {
                return (i, reason);
            }
        }
        return (null, null);
    }

    [Fact]
    public void A_transcript_that_loops_trips_on_the_result_of_its_fifth_repetition_and_not_before()
    {
        var lines = StuckFixtures.Loop;

        var (line, reason) = Replay(lines);

        // Result lines: the Read's, then one per failing Edit. The fifth Edit's result completes the fifth repetition.
        Assert.Equal(StuckFixtures.ResultLines(lines)[5], line);
        Assert.Contains("the last 5 turns were near-identical", reason);
        Assert.Contains("repeating tool calls: Edit", reason);
        Assert.DoesNotContain("Words.cs", reason); // the reason names tools, never transcript content
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    public void Worker_StuckRepeats_sets_how_many_repetitions_trip(int repeats, int trippingEdit)
    {
        var lines = StuckFixtures.Loop;

        var (line, reason) = Replay(lines, new StuckDetection(Repeats: repeats));

        Assert.Equal(StuckFixtures.ResultLines(lines)[trippingEdit], line);
        Assert.Contains($"the last {repeats} turns", reason);
    }

    [Fact]
    public void Fewer_repetitions_than_Worker_StuckRepeats_never_trip()
    {
        Assert.Null(Replay(StuckFixtures.Loop, new StuckDetection(Repeats: 7)).Line); // the fixture repeats six times
    }

    [Fact]
    public void Running_the_same_test_command_after_each_different_edit_is_not_a_loop()
    {
        var lines = StuckFixtures.EditTestCycle;
        Assert.Equal(6, lines.Count(l => l.Contains("\"command\": \"dotnet test\""))); // the same command six times

        Assert.Null(Replay(lines).Line);
        Assert.Null(Replay(lines, new StuckDetection(Repeats: 3)).Line);
    }

    [Fact]
    public void A_transcript_with_a_long_silence_and_then_progress_is_not_a_loop()
    {
        // The detector reads only what the session did, never the clock: the silence between lines cannot trip it.
        Assert.Null(Replay(StuckFixtures.SilenceThenProgress).Line);
        Assert.Null(Replay(StuckFixtures.SilenceThenProgress, new StuckDetection(Repeats: 2)).Line);
    }

    [Fact]
    public void A_repeating_cycle_of_two_turns_trips_once_it_has_repeated_five_times()
    {
        var lines = Transcript(Enumerable.Range(0, 6).SelectMany(_ => new[]
        {
            Turn("Read", """{"file_path":"src/Words.cs"}""", "1\tpublic static class Words"),
            Turn("Edit", """{"file_path":"src/Words.cs","old_string":"Split(' ')","new_string":"Split()"}""", "String to replace not found in file.", error: true),
        }));

        var (line, reason) = Replay(lines);

        Assert.Equal(StuckFixtures.ResultLines(lines)[9], line); // the 10th turn completes the 5th cycle
        Assert.Contains("the last 5 cycles of 2 turns", reason);
        Assert.Contains("repeating tool calls: Read, Edit", reason);
    }

    [Fact]
    public void The_same_tool_call_whose_result_changes_is_not_a_loop()
    {
        // Nothing changed in between, but each run fails a different test (flaky, or the worker is reading the failures one by one).
        string[] failing = ["Counts_tabs", "Counts_newlines", "Counts_empty", "Ignores_leading_space", "Ignores_trailing_space", "Handles_unicode_spaces"];
        var lines = Transcript(failing.Select(name =>
            Turn("Bash", """{"command":"dotnet test"}""", $"Failed WordsTests.{name}\nFailed!  - Failed: 1, Passed: 5")));

        Assert.Null(Replay(lines).Line);
        // The same command with the same result, though, is a loop.
        var same = Transcript(failing.Select(_ => Turn("Bash", """{"command":"dotnet test"}""", "Failed WordsTests.Counts_tabs\nFailed!  - Failed: 1, Passed: 5")));
        Assert.NotNull(Replay(same).Line);
    }

    [Fact]
    public void Worker_StuckSimilarity_sets_how_alike_turns_must_be()
    {
        // The same edit each time, failing with a slightly different message each time.
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];
        var lines = Transcript(words.Select(w =>
            Turn("Edit", """{"file_path":"src/Words.cs","old_string":"Split(' ')","new_string":"Split()"}""", $"String to replace not found in file (near {w}).", error: true)));

        Assert.Null(Replay(lines).Line); // alike, but under the default bar
        Assert.NotNull(Replay(lines, new StuckDetection(Similarity: 0.8)).Line);
    }

    [Fact]
    public void The_same_failing_tool_call_narrated_differently_each_time_is_a_loop()
    {
        // The story's trigger is "the last N assistant turns or tool calls": here the calls and their results repeat, the words do not.
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];
        var lines = Transcript(words.Select(w =>
            Turn("Edit", """{"file_path":"src/Words.cs","old_string":"Split(' ')","new_string":"Split()"}""", "String to replace not found in file.",
                error: true, text: $"Retrying the split fix ({w}).")));
        Assert.True(StuckDetector.Similarity("Retrying the split fix (alpha).", "Retrying the split fix (bravo).") < StuckDetection.DefaultSimilarity);

        var (line, reason) = Replay(lines);

        Assert.Equal(StuckFixtures.ResultLines(lines)[4], line);
        Assert.Contains("the last 5 turns made near-identical tool calls with near-identical results", reason);
        Assert.Contains("repeating tool calls: Edit", reason);
    }

    [Fact]
    public void Reads_of_different_files_that_share_a_head_and_tail_are_not_a_loop()
    {
        // Generated files: the same long boilerplate at the top and bottom, entirely different code in between.
        var head = string.Concat(Enumerable.Range(0, 60).Select(i => $"// <auto-generated> designer boilerplate line {i} for the form host\n"));
        var tail = string.Concat(Enumerable.Range(0, 60).Select(i => $"    // end of generated region {i}: do not edit by hand\n"));
        string[] forms = ["Customer", "Order", "Invoice", "Shipment", "Supplier", "Warehouse"];
        var lines = Transcript(forms.Select(form => Turn("Read", $$"""{"file_path":"src/Forms/{{form}}Form.Designer.cs"}""",
            head + string.Concat(Enumerable.Range(0, 40).Select(i => $"        this.{form.ToLowerInvariant()}Field{(char)('A' + i % 26)} = new {form}Control(\"{form} {(char)('a' + i % 26)}\");\n")) + tail)));
        Assert.True(lines.Max(l => l.Length) > 2 * 4000); // far longer than a head-and-tail cut of 4000 characters

        Assert.Null(Replay(lines).Line);
    }

    [Fact]
    public void The_same_command_whose_long_output_changed_only_in_its_middle_is_not_a_loop()
    {
        // A long build log whose start and end are the same each time, with a different error in the middle.
        var head = string.Concat(Enumerable.Range(0, 60).Select(i => $"  Restoring and building project {i} of the solution\n"));
        var tail = string.Concat(Enumerable.Range(0, 60).Select(i => $"  Build summary line {i}: see the log above\n"));
        string[] errors = ["CS0103 The name 'words' does not exist", "CS1002 ; expected", "CS0029 Cannot implicitly convert type",
            "CS0161 not all code paths return a value", "CS0246 The type or namespace name could not be found", "CS1513 } expected"];
        var lines = Transcript(errors.Select(e => Turn("Bash", """{"command":"dotnet build"}""",
            head + string.Concat(Enumerable.Repeat($"src/Words.cs: build error {e}, at the line shown\n", 40)) + tail, error: true)));

        Assert.Null(Replay(lines).Line);
    }

    [Fact]
    public void Reads_of_paths_that_differ_only_in_digits_are_not_a_loop()
    {
        var lines = Transcript(Enumerable.Range(1, 6).Select(i => Turn("Read", $$"""{"file_path":"src/Migrations/2026100{{i}}_Step{{i}}.cs"}""",
            $"1\tpublic partial class Step{i} : Migration\n2\t{{\n3\t    protected override void Up() => Run({i});\n4\t}}")));

        Assert.Null(Replay(lines).Line);
    }

    [Fact]
    public void A_flaky_test_command_that_passes_and_fails_is_not_a_loop_though_its_output_otherwise_differs_only_in_digits()
    {
        // The same command; its long output differs only in counts and timings — and in whether the run passed.
        var log = string.Concat(Enumerable.Range(0, 80).Select(i => $"  Test project {i}: discovered tests and ran them\n"));
        string Run(bool passed, int seconds) => log + (passed
            ? $"Passed!  - Failed: 0, Passed: 42, Skipped: 0, Total: 42, Duration: {seconds} s"
            : $"Failed!  - Failed: 1, Passed: 41, Skipped: 0, Total: 42, Duration: {seconds} s");
        bool[] outcomes = [false, true, false, false, true, false];
        var lines = Transcript(outcomes.Select((passed, i) => Turn("Bash", """{"command":"dotnet test"}""", Run(passed, 3 + i))));
        Assert.True(StuckDetector.Similarity(Run(true, 3), Run(false, 4)) >= StuckDetection.DefaultSimilarity); // alike as text

        Assert.Null(Replay(lines).Line);
        Assert.NotEqual(StuckDetector.Outcome(false, Run(true, 3)), StuckDetector.Outcome(false, Run(false, 3)));
        // The same command failing the same way each time, its timings aside, is a loop.
        Assert.NotNull(Replay(Transcript(outcomes.Select((_, i) => Turn("Bash", """{"command":"dotnet test"}""", Run(false, 3 + i))))).Line);
    }

    [Fact]
    public void A_long_session_keeps_only_the_turns_it_can_still_compare_and_still_finds_a_loop_at_its_end()
    {
        var detector = new StuckDetector(StuckDetection.Default);
        var window = StuckDetection.DefaultRepeats * StuckDetector.MaxCycleTurns;
        var (maxTurns, maxCalls) = (0, 0);
        // 300 different turns, every third a tool call that never gets its result.
        for (var i = 0; i < 300; i++)
        {
            string[] lines = i % 3 == 0
                ? [$$$"""{"type":"assistant","message":{"id":"long{{{i}}}","content":[{"type":"tool_use","id":"lost{{{i}}}","name":"Bash","input":{"command":"step {{{i}}}"}}]}}"""]
                : Turn("Read", $$"""{"file_path":"src/File{{i}}.cs"}""", $"contents of file {i}: {new string((char)('a' + i % 26), i % 7 + 1)}");
            foreach (var line in lines)
            {
                Assert.Null(detector.Accept(line));
                (maxTurns, maxCalls) = (Math.Max(maxTurns, detector.RetainedTurns), Math.Max(maxCalls, detector.PendingCalls));
            }
        }

        Assert.InRange(maxTurns, window, window + 1);
        Assert.InRange(maxCalls, 1, window + 1);
        var loop = Enumerable.Range(0, 5).SelectMany(_ => Turn("Edit", """{"file_path":"src/Words.cs","old_string":"a","new_string":"b"}""", "String to replace not found in file.", error: true));
        Assert.Contains(loop.ToList(), l => detector.Accept(l) is not null);
    }

    [Fact]
    public void The_router_banner_and_feedback_footer_are_no_part_of_a_turn()
    {
        var banner = """{"type":"assistant","message":{"id":"m-banner-ID","content":[{"type":"text","text":"✦ **Weave Router** → claude-opus-5 · best pick for this turn\n\n"}]}}""";
        var footer = """{"type":"assistant","message":{"id":"m-footer-ID","content":[{"type":"text","text":"\n\n_Weave Router feedback:_ `/rf +` good experience · `/rf -` poor experience"}]}}""";
        var lines = Enumerable.Range(0, 10).SelectMany(i => new[] { banner.Replace("ID", $"{i}"), footer.Replace("ID", $"{i}") }).ToArray();

        Assert.Null(Replay(lines, new StuckDetection(Repeats: 2)).Line); // as text-only turns they would repeat
    }

    [Fact]
    public void Normalising_collapses_whitespace_and_digit_runs()
    {
        Assert.Equal("attempt 0 of 0: took 0.0 s", StuckDetector.Normalise("  attempt 12 of\t\t3:\n took 4.56 s "));
        Assert.Equal(1.0, StuckDetector.Similarity("retry 1 at 10:01", "retry   22 at 11:59"));
        Assert.True(StuckDetector.Similarity("Read src/Words.cs", "Edit tests/WordsTests.cs") < 0.5);
    }

    [Fact]
    public void Thinking_alone_is_no_turn_and_non_json_or_other_lines_change_nothing()
    {
        var thinking = """{"type":"assistant","message":{"id":"m-think","content":[{"type":"thinking","thinking":"","signature":"x"}]}}""";
        var lines = Enumerable.Repeat(thinking, 10).Concat(["not json", """{"type":"system","subtype":"init"}""", """{"type":"result"}"""]).ToArray();

        Assert.Null(Replay(lines, new StuckDetection(Repeats: 2)).Line);
    }

    [Fact]
    public void It_trips_once()
    {
        var detector = new StuckDetector(StuckDetection.Default);
        var trips = StuckFixtures.Loop.Concat(StuckFixtures.Loop).Count(l => detector.Accept(l) is not null);

        Assert.Equal(1, trips);
        Assert.NotNull(detector.Reason);
    }

    [Theory]
    [InlineData(1, 0.96)]
    [InlineData(5, 0)]
    [InlineData(5, 1.01)]
    [InlineData(5, double.NaN)]
    public void Out_of_range_settings_are_refused(int repeats, double similarity)
    {
        Assert.Throws<InvalidOperationException>(() => new StuckDetection(repeats, similarity).Validate());
        Assert.Throws<InvalidOperationException>(() => new StuckDetector(new StuckDetection(repeats, similarity)));
    }

    private static int _ids;

    /// <summary>One turn: optional text, one tool call and its result, in stream-json lines.</summary>
    internal static string[] Turn(string tool, string inputJson, string result, bool error = false, string? text = null)
    {
        var n = Interlocked.Increment(ref _ids);
        var blocks = new List<string>();
        if (text is not null)
        {
            blocks.Add($$"""{"type":"assistant","message":{"id":"m{{n}}","content":[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}]},"session_id":"s"}""");
        }
        blocks.Add($$"""{"type":"assistant","message":{"id":"m{{n}}","content":[{"type":"tool_use","id":"t{{n}}","name":"{{tool}}","input":{{inputJson}}}]},"session_id":"s"}""");
        blocks.Add($$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t{{n}}","content":{{JsonSerializer.Serialize(result)}},"is_error":{{(error ? "true" : "false")}}}]},"session_id":"s"}""");
        return [.. blocks];
    }

    internal static string[] Transcript(IEnumerable<string[]> turns) =>
        [.. new[] { """{"type":"system","subtype":"init","session_id":"s"}""" }.Concat(turns.SelectMany(t => t))];
}
