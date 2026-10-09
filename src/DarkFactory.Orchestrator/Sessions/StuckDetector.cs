using System.Text;
using System.Text.Json;

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
    /// The Aura project's bar (≥ 0.96 over normalised generations). With digits and whitespace normalised away, two turns this
    /// alike differ by about one character in thirty (a trigram changes per character): a reworded sentence, a different file
    /// or command, a test that now passes or a different edit all fall well below it, while a model repeating itself verbatim
    /// (bar a counter or a temp path) stays above it.
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
/// not, it is not what the worker did); a turn with nothing else is no turn. A turn is complete once every tool call in it has
/// its result, or once a later turn has started.</item>
/// <item>Each turn is <b>normalised</b>: every run of whitespace becomes one space and every run of digits one <c>0</c> (counters,
/// timings, line numbers, ids), then it is cut to <see cref="MaxSignatureChars"/> (its head and tail halves).</item>
/// <item>Two turns' <b>similarity</b> is the Sørensen–Dice coefficient of their character-trigram multisets (1 = the same).</item>
/// <item>The session is <b>stuck</b> when, for some cycle length p from 1 to <see cref="MaxCycleTurns"/>, its last
/// <see cref="StuckDetection.Repeats"/> × p complete turns are that many repetitions of one p-turn cycle: each of them is at least
/// <see cref="StuckDetection.Similarity"/> alike to the turn p later. The whole recent sequence must repeat, so running the
/// same test command after each of several different edits is not a loop (the edits differ), nor is the same command whose
/// output changed; the same failing edit, or the same read-then-failing-edit pair, over and over is.</item>
/// </list>
/// Only complete turns are compared, so a loop trips once the last repetition's tool results are in: the worker is then
/// interrupted at its next tool call. Trips once; later lines are ignored.
/// </summary>
public sealed class StuckDetector(StuckDetection options)
{
    /// <summary>The longest repeating cycle looked for, in turns (e.g. read → failing edit is 2; edit → build → test is 3).</summary>
    public const int MaxCycleTurns = 4;

    /// <summary>A turn longer than this (normalised) is compared by its first and last halves of this many characters.</summary>
    public const int MaxSignatureChars = 4000;

    private readonly StuckDetection _options = options.Validate();
    private readonly List<Turn> _turns = [];
    private readonly Dictionary<string, (Turn Turn, int Part)> _calls = new(StringComparer.Ordinal);
    private int _checkedComplete;

    /// <summary>Why the session is stuck, once it is (tool names and counts only, never transcript content).</summary>
    public string? Reason { get; private set; }

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
            string? part = null;
            if (kind.ValueEquals("text") && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                part = $"text: {text.GetString()}";
            }
            else if (kind.ValueEquals("tool_use"))
            {
                var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?";
                var input = block.TryGetProperty("input", out var i) ? i.GetRawText() : "";
                part = $"tool {name}: {input}";
                if (turn is null)
                {
                    turn = StartTurn(id);
                }
                turn.Tools.Add(name);
                var at = turn.Add(part);
                if (block.TryGetProperty("id", out var callId) && callId.ValueKind == JsonValueKind.String)
                {
                    _calls[callId.GetString()!] = (turn, at);
                    turn.Pending++;
                }
                continue;
            }
            if (part is not null)
            {
                (turn ??= StartTurn(id)).Add(part);
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
                var result = block.TryGetProperty("content", out var c) ? ResultText(c) : "";
                call.Turn.Amend(call.Part, $" => {(error ? "error: " : "")}{result}");
                call.Turn.Pending--;
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
        _turns.Count == 0 ? 0 : _turns[^1] is { Tools.Count: > 0, Pending: 0 } ? _turns.Count : _turns.Count - 1;

    private string? Check()
    {
        var complete = CompleteCount();
        if (complete <= _checkedComplete)
        {
            return null;
        }
        _checkedComplete = complete;
        var repeats = _options.Repeats;
        for (var cycle = 1; cycle <= MaxCycleTurns && repeats * cycle <= complete; cycle++)
        {
            if (Repeating(complete, cycle))
            {
                var tools = _turns.Skip(complete - cycle).Take(cycle).SelectMany(t => t.Tools).ToList();
                Reason = $"the last {repeats} {(cycle == 1 ? "turns" : $"cycles of {cycle} turns")} were near-identical "
                    + $"(similarity ≥ {_options.Similarity:0.###} after normalising whitespace and digits)"
                    + (tools.Count > 0 ? $"; repeating tool calls: {string.Join(", ", tools)}" : "; no tool calls");
                return Reason;
            }
        }
        return null;
    }

    /// <summary>Whether the last <c>repeats × cycle</c> of the first <paramref name="complete"/> turns repeat one <paramref name="cycle"/>-turn cycle.</summary>
    private bool Repeating(int complete, int cycle)
    {
        for (var j = 0; j < cycle; j++)
        {
            var last = _turns[complete - 1 - j];
            for (var k = 1; k < _options.Repeats; k++)
            {
                if (Similarity(_turns[complete - 1 - j - k * cycle], last) < _options.Similarity)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The normalised form two turns are compared by (see the class summary).</summary>
    public static string Normalise(string text)
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
            if (char.IsAsciiDigit(ch))
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
        var s = sb.ToString();
        return s.Length <= MaxSignatureChars ? s : s[..(MaxSignatureChars / 2)] + s[^(MaxSignatureChars / 2)..];

        void AppendSpace()
        {
            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }
            space = false;
        }
    }

    /// <summary>The Sørensen–Dice coefficient of two normalised texts' character-trigram multisets (texts under 3 characters: equal or not).</summary>
    public static double Similarity(string a, string b) => Similarity(Trigrams(Normalise(a)), Trigrams(Normalise(b)), a, b);

    private static double Similarity(Turn a, Turn b) => Similarity(a.Profile, b.Profile, a.Signature, b.Signature);

    private static double Similarity(Dictionary<ulong, int> a, Dictionary<ulong, int> b, string textA, string textB)
    {
        var (sizeA, sizeB) = (a.Values.Sum(), b.Values.Sum());
        if (sizeA == 0 || sizeB == 0)
        {
            return Normalise(textA) == Normalise(textB) ? 1 : 0;
        }
        var shared = 0;
        foreach (var (gram, count) in a)
        {
            if (b.TryGetValue(gram, out var other))
            {
                shared += Math.Min(count, other);
            }
        }
        return 2.0 * shared / (sizeA + sizeB);
    }

    private static Dictionary<ulong, int> Trigrams(string s)
    {
        var grams = new Dictionary<ulong, int>();
        for (var i = 0; i + 2 < s.Length; i++)
        {
            var key = ((ulong)s[i] << 32) | ((ulong)s[i + 1] << 16) | s[i + 2];
            grams[key] = grams.GetValueOrDefault(key) + 1;
        }
        return grams;
    }

    private sealed class Turn(string? id)
    {
        private Dictionary<ulong, int>? _profile;
        private string? _signature;

        private readonly List<string> _parts = [];

        public string? Id { get; } = id;
        public List<string> Tools { get; } = [];
        public int Pending { get; set; }

        /// <summary>Adds a text or tool-call part; returns its index.</summary>
        public int Add(string part)
        {
            _parts.Add(part);
            (_signature, _profile) = (null, null);
            return _parts.Count - 1;
        }

        /// <summary>Appends a tool call's result to its part.</summary>
        public void Amend(int index, string suffix)
        {
            _parts[index] += suffix;
            (_signature, _profile) = (null, null);
        }

        /// <summary>The whole turn as compared.</summary>
        public string Signature => _signature ??= string.Join("\n", _parts);

        public Dictionary<ulong, int> Profile => _profile ??= Trigrams(Normalise(Signature));
    }
}
