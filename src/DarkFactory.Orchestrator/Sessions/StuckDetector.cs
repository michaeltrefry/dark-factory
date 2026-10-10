using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Sessions;

/// <summary>
/// When a live worker session counts as stuck (sc-25388): its last <see cref="Repeats"/> turns — or its last <see cref="Repeats"/>
/// cycles of up to <see cref="StuckDetector.MaxCycleTurns"/> turns — are near-identical, each at least <see cref="Similarity"/>
/// alike to the same turn one cycle later (<see cref="StuckDetector"/>). Silence never counts: a session that is only quiet (no
/// event for <see cref="Dashboard.IDashboardData.QuietThreshold"/>) is marked on the dashboard and left alone.
/// </summary>
/// <param name="Repeats"><c>Worker:StuckRepeats</c> (≥ 2, default 5): how many times in a row the turn (or cycle) must occur.</param>
/// <param name="Similarity"><c>Worker:StuckSimilarity</c> (0 &lt; s ≤ 1, default 0.96): how alike two turns must be to count as the same.</param>
public sealed record StuckDetection(int Repeats = StuckDetection.DefaultRepeats, double Similarity = StuckDetection.DefaultSimilarity)
{
    public const int DefaultRepeats = 5;

    /// <summary>
    /// The Aura project's bar (≥ 0.96 over normalised generations). Two texts this alike differ by about one character in thirty
    /// (a trigram changes per character): a reworded sentence, a different file or command, a different edit or different output
    /// all fall well below it, while a model repeating itself verbatim (bar a counter, a timing or whitespace) stays above it.
    /// </summary>
    public const double DefaultSimilarity = 0.96;

    public static readonly StuckDetection Default = new();

    /// <summary>Throws when a value is out of range (the factory refuses to start).</summary>
    public StuckDetection Validate() =>
        Repeats < 2 ? throw new InvalidOperationException($"Worker:StuckRepeats must be at least 2, not {Repeats}.")
        : !(Similarity > 0 && Similarity <= 1) ? throw new InvalidOperationException($"Worker:StuckSimilarity must be more than 0 and at most 1, not {Similarity}.")
        : this;
}

/// <summary>
/// Watches one live stream-json session for a loop (sc-25388). Deterministic, over what the session did, never over time:
/// <list type="bullet">
/// <item>A <b>turn</b> is one assistant message (its <c>message.id</c>): its text blocks, its tool calls (tool name and input
/// JSON) and each call's result (the <c>tool_result</c> that answers it, with its error flag). Thinking is left out (redacted or
/// not, it is not what the worker did), and so is the Weave Router's banner and feedback footer; a turn with nothing else is no
/// turn. A turn is complete once every tool call in it has its result, or once a later turn has started.</item>
/// <item>A turn is compared in parts, each <b>normalised</b> on its own: its <b>calls</b> (tool names and inputs, whitespace
/// collapsed, digits kept — <c>Step1.cs</c> and <c>Step2.cs</c> are different files), its <b>results</b> and its <b>text</b>
/// (whitespace collapsed and every run of digits made one <c>0</c>: counters, timings, line numbers, ids), and each result's
/// <b>outcome</b> (its error flag and its pass/fail/error words, which must match exactly: a test run that passed is never the
/// same as one that failed, however alike their output).</item>
/// <item>Two texts' <b>similarity</b> is the Sørensen–Dice coefficient of their character-trigram multisets (1 = the same), over
/// the whole normalised text (profiled once per turn).</item>
/// <item>Two turns are <b>the same</b> when their outcomes match and their calls, and their text with their results, are each at
/// least <see cref="StuckDetection.Similarity"/> alike; their <b>tool calls are the same</b> when both made tool calls, their
/// outcomes match and their calls and results (text left out) are each that alike — the same failing edit narrated differently
/// each time.</item>
/// <item>The session is <b>stuck</b> when, for some cycle length p from 1 to <see cref="MaxCycleTurns"/>, its last
/// <see cref="StuckDetection.Repeats"/> × p complete turns are that many repetitions of one p-turn cycle — each the same as
/// (or with the same tool calls as) the turn p later. The whole recent sequence must repeat, so running the same test command
/// after each of several different edits is not a loop (the edits differ), nor is the same command whose output changed; the
/// same failing edit, or the same read-then-failing-edit pair, over and over is.</item>
/// </list>
/// Only complete turns are compared, so a loop trips once the last repetition's tool results are in: the worker is then
/// interrupted at its next tool call. Trips once; later lines are ignored. Only the last <c>Repeats × MaxCycleTurns</c> complete
/// turns are kept.
/// </summary>
public sealed partial class StuckDetector(StuckDetection options)
{
    /// <summary>The longest repeating cycle looked for, in turns (e.g. read → failing edit is 2; edit → build → test is 3).</summary>
    public const int MaxCycleTurns = 4;

    /// <summary>What <see cref="Reason"/> names the repeating tool calls after (<see cref="RepeatedTools"/> reads them back).</summary>
    public const string ToolsPrefix = "repeating tool calls: ";

    private readonly StuckDetection _options = options.Validate();
    private readonly List<Turn> _turns = [];
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private int _checkedComplete;

    /// <summary>Why the session is stuck, once it is (tool names and counts only, never transcript content).</summary>
    public string? Reason { get; private set; }

    /// <summary>How many turns are held (bounded: the window <see cref="Accept"/> compares, plus a turn in progress).</summary>
    internal int RetainedTurns => _turns.Count;

    /// <summary>How many tool calls are held waiting for their results.</summary>
    internal int PendingCalls => _calls.Count;

    /// <summary>
    /// Folds one stdout line in. Returns <see cref="Reason"/> on the line that makes the session stuck, else null (also on every
    /// line after). Non-JSON and unrelated lines change nothing.
    /// </summary>
    public string? Accept(string line)
    {
        if (Reason is not null || string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            if (type.ValueEquals("assistant"))
            {
                AcceptAssistant(message, content);
            }
            else if (type.ValueEquals("user"))
            {
                AcceptResults(content);
            }
            else
            {
                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return Check();
    }

    /// <summary>The tool names a <see cref="Reason"/> names as repeating (distinct, in order); empty when it names none.</summary>
    public static IReadOnlyList<string> RepeatedTools(string? reason)
    {
        var at = reason?.IndexOf(ToolsPrefix, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return [];
        }
        var rest = reason![(at + ToolsPrefix.Length)..];
        var end = rest.IndexOfAny([';', ')']);
        return (end < 0 ? rest : rest[..end]).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private void AcceptAssistant(JsonElement message, JsonElement content)
    {
        var id = message.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String ? mid.GetString() : null;
        var turn = _turns.Count > 0 && id is not null && _turns[^1].Id == id ? _turns[^1] : null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind))
            {
                continue;
            }
            if (kind.ValueEquals("text") && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var said = RouterChrome().Replace(text.GetString()!, "");
                if (!string.IsNullOrWhiteSpace(said))
                {
                    (turn ??= StartTurn(id)).AddText(said);
                }
            }
            else if (kind.ValueEquals("tool_use"))
            {
                var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?";
                var input = block.TryGetProperty("input", out var i) ? i.GetRawText() : "";
                turn ??= StartTurn(id);
                var call = turn.AddCall(name, input);
                if (block.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                {
                    _calls[callId.GetString()!] = call;
                }
                else
                {
                    call.Answer(false, ""); // nothing can answer it
                }
            }
        }
    }

    private Turn StartTurn(string? id)
    {
        var turn = new Turn(id);
        _turns.Add(turn);
        return turn;
    }

    private void AcceptResults(JsonElement content)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind) && kind.ValueEquals("tool_result")
                && block.TryGetProperty("tool_use_id", out var callId) && callId.ValueKind == JsonValueKind.String
                && _calls.Remove(callId.GetString()!, out var call))
            {
                var error = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                call.Answer(error, block.TryGetProperty("content", out var c) ? ResultText(c) : "");
            }
        }
    }

    private static string ResultText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString()!,
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
            .Select(b => b.ValueKind == JsonValueKind.Object && b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : b.GetRawText())),
        _ => content.GetRawText(),
    };

    /// <summary>The complete turns: every turn but the last, and the last once all its tool calls have their results.</summary>
    private int CompleteCount() =>
        _turns.Count == 0 ? 0 : _turns[^1] is { HasCalls: true, Pending: false } ? _turns.Count : _turns.Count - 1;

    private string? Check()
    {
        var complete = CompleteCount();
        if (complete <= _checkedComplete)
        {
            return null;
        }
        _checkedComplete = complete;
        var repeats = _options.Repeats;
        foreach (var toolsOnly in (bool[])[false, true])
        {
            for (var cycle = 1; cycle <= MaxCycleTurns && repeats * cycle <= complete; cycle++)
            {
                if (Repeating(complete, cycle, toolsOnly))
                {
                    var tools = _turns.Skip(complete - cycle).Take(cycle).SelectMany(t => t.Tools).ToList();
                    var what = cycle == 1 ? "turns" : $"cycles of {cycle} turns";
                    Reason = (toolsOnly ? $"the last {repeats} {what} made near-identical tool calls with near-identical results"
                            : $"the last {repeats} {what} were near-identical")
                        + $" (similarity ≥ {_options.Similarity:0.###} after normalising whitespace, and digits outside tool inputs)"
                        + (tools.Count > 0 ? $"; {ToolsPrefix}{string.Join(", ", tools)}" : "; no tool calls");
                    return Reason;
                }
            }
        }
        Prune(complete);
        return null;
    }

    /// <summary>Drops the complete turns older than any cycle <see cref="Check"/> can still compare (and their unanswered calls).</summary>
    private void Prune(int complete)
    {
        var drop = complete - _options.Repeats * MaxCycleTurns;
        if (drop <= 0)
        {
            return;
        }
        var dropped = _turns.Take(drop).ToHashSet();
        _turns.RemoveRange(0, drop);
        _checkedComplete -= drop;
        foreach (var (id, call) in _calls.ToList())
        {
            if (dropped.Contains(call.Turn))
            {
                _calls.Remove(id);
            }
        }
    }

    /// <summary>Whether the last <c>repeats × cycle</c> of the first <paramref name="complete"/> turns repeat one <paramref name="cycle"/>-turn cycle.</summary>
    private bool Repeating(int complete, int cycle, bool toolsOnly)
    {
        for (var j = 0; j < cycle; j++)
        {
            var last = _turns[complete - 1 - j];
            for (var k = 1; k < _options.Repeats; k++)
            {
                if (!Same(_turns[complete - 1 - j - k * cycle], last, toolsOnly))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private bool Same(Turn a, Turn b, bool toolsOnly)
    {
        var bar = _options.Similarity;
        if ((toolsOnly && !(a.HasCalls && b.HasCalls)) || a.Outcome != b.Outcome || Similarity(a.Calls, b.Calls) < bar)
        {
            return false;
        }
        return toolsOnly ? Similarity(a.Results, b.Results) >= bar : Similarity(a.Content, b.Content) >= bar;
    }

    /// <summary>Collapses every run of whitespace to one space (and trims): how tool inputs are compared.</summary>
    public static string CollapseWhitespace(string text) => Normalise(text, digits: false);

    /// <summary>Collapses whitespace and makes every run of digits one <c>0</c>: how text and tool results are compared.</summary>
    public static string Normalise(string text) => Normalise(text, digits: true);

    private static string Normalise(string text, bool digits)
    {
        var sb = new StringBuilder(text.Length);
        var (space, digit) = (false, false);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                (space, digit) = (true, false);
                continue;
            }
            if (digits && char.IsAsciiDigit(ch))
            {
                if (!digit)
                {
                    AppendSpace();
                    sb.Append('0');
                }
                digit = true;
                continue;
            }
            AppendSpace();
            digit = false;
            sb.Append(ch);
        }
        return sb.ToString();

        void AppendSpace()
        {
            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }
            space = false;
        }
    }

    /// <summary>
    /// A tool result's outcome: its error flag and how often each pass/fail/error word occurs in it (case-insensitive). Two
    /// results whose outcomes differ are different results, however alike their text.
    /// </summary>
    public static string Outcome(bool error, string result)
    {
        var words = OutcomeWords().Matches(result).Select(m => m.Value.ToLowerInvariant())
            .GroupBy(w => w, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{g.Count()}");
        return $"{(error ? "error" : "ok")}[{string.Join(",", words)}]";
    }

    /// <summary>The Sørensen–Dice coefficient of two normalised texts' character-trigram multisets (texts under 3 characters: equal or not).</summary>
    public static double Similarity(string a, string b) => Similarity(new Profiled(Normalise(a)), new Profiled(Normalise(b)));

    private static double Similarity(Profiled a, Profiled b)
    {
        var (sizeA, sizeB) = (a.Size, b.Size);
        if (sizeA == 0 || sizeB == 0)
        {
            return a.Text == b.Text ? 1 : 0;
        }
        var shared = 0;
        foreach (var (gram, count) in a.Grams)
        {
            if (b.Grams.TryGetValue(gram, out var other))
            {
                shared += Math.Min(count, other);
            }
        }
        return 2.0 * shared / (sizeA + sizeB);
    }

    /// <summary>The Weave Router's per-session banner (<c>✦ **Weave Router** → model · …</c>) and its feedback footer.</summary>
    [GeneratedRegex(@"✦ \*\*Weave Router\*\* →[^\n]*|_Weave Router feedback:_[^\n]*")]
    private static partial Regex RouterChrome();

    [GeneratedRegex(@"\b(?:pass(?:ed|es|ing)?|fail(?:ed|s|ure|ures|ing)?|errors?|succeed(?:ed|s)?|success(?:ful)?|ok)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OutcomeWords();

    /// <summary>A normalised text and its character-trigram multiset, built once.</summary>
    private sealed class Profiled
    {
        public Profiled(string text)
        {
            Text = text;
            for (var i = 0; i + 2 < text.Length; i++)
            {
                var key = ((ulong)text[i] << 32) | ((ulong)text[i + 1] << 16) | text[i + 2];
                Grams[key] = Grams.GetValueOrDefault(key) + 1;
                Size++;
            }
        }

        public string Text { get; }
        public Dictionary<ulong, int> Grams { get; } = [];
        public int Size { get; }
    }

    private sealed class Call(Turn turn, string name, string input)
    {
        public Turn Turn { get; } = turn;
        public string Name { get; } = name;
        public string Key { get; } = $"{name}: {CollapseWhitespace(input)}";
        public bool Answered { get; private set; }
        public string Result { get; private set; } = "";
        public string Outcome { get; private set; } = "";

        public void Answer(bool error, string result)
        {
            (Answered, Result, Outcome) = (true, $"{(error ? "error: " : "")}{result}", StuckDetector.Outcome(error, result));
            Turn.Changed();
        }
    }

    private sealed class Turn(string? id)
    {
        private readonly List<string> _texts = [];
        private readonly List<Call> _calls = [];
        private Profiled? _callsProfile, _results, _content;
        private string? _outcome;

        public string? Id { get; } = id;
        public IEnumerable<string> Tools => _calls.Select(c => c.Name);
        public bool HasCalls => _calls.Count > 0;
        public bool Pending => _calls.Any(c => !c.Answered);

        public void AddText(string text)
        {
            _texts.Add(text);
            Changed();
        }

        public Call AddCall(string name, string input)
        {
            var call = new Call(this, name, input);
            _calls.Add(call);
            Changed();
            return call;
        }

        public void Changed() => (_callsProfile, _results, _content, _outcome) = (null, null, null, null);

        /// <summary>Tool names and inputs (digits kept).</summary>
        public Profiled Calls => _callsProfile ??= new Profiled(string.Join("\n", _calls.Select(c => c.Key)));

        /// <summary>The tool results, normalised.</summary>
        public Profiled Results => _results ??= new Profiled(Normalise(ResultsText));

        /// <summary>The text and the tool results, normalised.</summary>
        public Profiled Content => _content ??= new Profiled(Normalise(string.Join("\n", _texts) + "\n" + ResultsText));

        public string Outcome => _outcome ??= string.Join("|", _calls.Select(c => c.Outcome));

        private string ResultsText => string.Join("\n", _calls.Select(c => c.Result));
    }
}
