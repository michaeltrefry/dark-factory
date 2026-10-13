using System.Text;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>An attribute of a Rust item: its path (<c>test</c>, <c>tokio::test</c>, <c>cfg</c>) and its whole text, tokens joined by one space.</summary>
public sealed record RustAttribute(string Path, string Text)
{
    /// <summary>The path's last segment (<c>test</c> of <c>tokio::test</c>).</summary>
    public string Name => Path[(Path.LastIndexOf(':') + 1)..];

    /// <summary>
    /// A <c>cfg</c> whose predicate holds only in test builds, evaluated (never searched for the word): <c>test</c>;
    /// <c>all(…)</c> with an operand that is test-only; <c>any(…)</c> whose operands are all test-only. <c>not(…)</c> and
    /// every other predicate are not: <c>cfg(all(test, not(miri)))</c> is test-only, <c>cfg(any(test, feature = "x"))</c>
    /// and <c>cfg(not(test))</c> are production code.
    /// </summary>
    public bool IsCfgTest => Path == "cfg" && RustSource.IsTestOnlyCfg(Text);
}

/// <summary>One import of a <c>use</c> item (<see cref="RustSource.UseLeaves"/>).</summary>
public sealed record RustUseLeaf(string Statement, string Key, IReadOnlyList<(int Start, int End)> Spans)
{
    /// <summary>The name the import binds in its module (<c>b</c> of <c>a::b</c>, <c>c</c> of <c>a::b as c</c>, <c>a</c> of <c>a::{self}</c>); null for a glob, <c>as _</c> and an <c>extern crate</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Whether a position inside the item's text lies in one of this import's tokens.</summary>
    public bool Holds(int position) => Spans.Any(s => s.Start <= position && position < s.End);
}

/// <summary>
/// One item of a Rust module, found by <see cref="RustSource.Parse"/>: <see cref="Kind"/> is <c>mod</c>, <c>fn</c>,
/// <c>use</c>, <c>macro</c> (an item-position macro invocation) or <c>item</c> (anything else); <see cref="Start"/> (its
/// first attribute) to <see cref="End"/> is its text; an inline module's or a braced macro's body is between
/// <see cref="BodyOpen"/> and <see cref="BodyClose"/> (the positions of its braces; -1 for <c>mod x;</c>) and holds
/// <see cref="Items"/>. <see cref="ParameterAttributes"/> are the attributes on a function's parameters (rstest's
/// <c>#[case]</c>, <c>#[values(…)]</c>). An <c>extern crate x;</c> is a <c>use</c> too (an import).
/// </summary>
public sealed record RustItem(string Kind, string? Name, int Start, int End, int BodyOpen, int BodyClose,
    IReadOnlyList<RustAttribute> Attributes, IReadOnlyList<RustAttribute> ParameterAttributes, IReadOnlyList<RustItem> Items)
{
    public const string Mod = "mod", Fn = "fn", Use = "use", Macro = "macro", Other = "item";

    /// <summary>An inline module's inner attributes (<c>mod tests { #![cfg(test)] … }</c>).</summary>
    public IReadOnlyList<RustAttribute> InnerAttributes { get; init; } = [];

    public bool IsInlineMod => Kind == Mod && BodyOpen >= 0;

    /// <summary>Compiled only into test builds (<see cref="RustAttribute.IsCfgTest"/>, on the item or as an inline module's inner attribute).</summary>
    public bool IsGated => Attributes.Any(a => a.IsCfgTest) || InnerAttributes.Any(a => a.IsCfgTest);

    /// <summary>
    /// Attributes that make a function a test libtest runs: <c>#[test]</c> under any path (<c>tokio::test</c>,
    /// <c>async_std::test</c>, <c>sqlx::test</c>, …), rstest's, test-case's, quickcheck's and test-strategy's.
    /// </summary>
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal) { "test", "rstest", "test_case", "test_matrix", "quickcheck", "proptest" };

    /// <summary>Attributes that make one function several tests, each named <c>function::case</c>.</summary>
    private static readonly HashSet<string> CaseAttributes = new(StringComparer.Ordinal) { "test_case", "test_matrix", "case", "values", "files", "apply" };

    public bool IsTestFn => Kind == Fn && Name is not null && Attributes.Any(a => TestAttributes.Contains(a.Name));

    /// <summary>The attributes that give a test function its cases (on the function and on its parameters).</summary>
    public IReadOnlyList<string> CaseRows => Attributes.Concat(ParameterAttributes).Where(a => CaseAttributes.Contains(a.Name)).Select(a => a.Text).ToList();
}

/// <summary>A parsed Rust source file: its text, top-level items and inner attributes (<c>#![…]</c>).</summary>
public sealed record RustFile(string Text, IReadOnlyList<RustItem> Items, IReadOnlyList<RustAttribute> InnerAttributes)
{
    /// <summary>The whole file is compiled only into test builds (<c>#![cfg(test)]</c>).</summary>
    public bool IsGated => InnerAttributes.Any(a => a.IsCfgTest);

    /// <summary>An item's text.</summary>
    public string TextOf(RustItem item) => Text[item.Start..item.End];

    /// <summary>
    /// Every test function with its path inside the file (inline modules, then its name: <c>tests::adds</c>) and the items
    /// that contain it, outermost first. A test inside a braced item macro (<c>proptest! { … }</c>) is named as in the
    /// module holding the macro.
    /// </summary>
    public IEnumerable<(string Path, RustItem Fn, IReadOnlyList<RustItem> Ancestors)> TestFns() => TestFns(Items, [], []);

    private static IEnumerable<(string, RustItem, IReadOnlyList<RustItem>)> TestFns(IReadOnlyList<RustItem> items, IReadOnlyList<string> modules, IReadOnlyList<RustItem> ancestors)
    {
        foreach (var item in items)
        {
            if (item.IsTestFn)
            {
                yield return (string.Join("::", modules.Append(item.Name!)), item, ancestors);
            }
            else if (item.IsInlineMod || item is { Kind: RustItem.Macro, BodyOpen: >= 0 })
            {
                IReadOnlyList<string> inner = item.Kind == RustItem.Mod ? [.. modules, item.Name!] : modules;
                foreach (var found in TestFns(item.Items, inner, [.. ancestors, item]))
                {
                    yield return found;
                }
            }
        }
    }

    /// <summary>The items that contain <paramref name="position"/>, outermost first (inline modules and braced macros are descended into).</summary>
    public IReadOnlyList<RustItem> ItemsAt(int position)
    {
        var chain = new List<RustItem>();
        var items = Items;
        while (items.FirstOrDefault(i => i.Start <= position && position < i.End) is { } item)
        {
            chain.Add(item);
            items = item.Items;
        }
        return chain;
    }

    /// <summary>The position of a 1-based line and column (clamped to the line), or -1 when the file has no such line.</summary>
    public int Position(int line, int column)
    {
        var start = 0;
        for (var n = 1; n < line; n++)
        {
            var next = Text.IndexOf('\n', start);
            if (next < 0)
            {
                return -1;
            }
            start = next + 1;
        }
        if (line < 1 || start > Text.Length)
        {
            return -1;
        }
        var end = Text.IndexOf('\n', start);
        end = end < 0 ? Text.Length : end;
        return Math.Min(start + Math.Max(column - 1, 0), Math.Max(end - 1, start));
    }
}

/// <summary>
/// A lexer-level reader of Rust source (sc-25875): no Rust parser ships with .NET and nothing is compiled, so the gate
/// finds a file's items — modules, functions and their attributes — from its tokens. The lexer knows what can hide a
/// <c>#[test]</c> or a brace: line and (nested) block comments, string, byte-string and C-string literals with escapes,
/// raw strings (<c>r#"…"#</c>, any number of <c>#</c>), character literals as against lifetimes, and raw identifiers. The
/// item reader then walks module bodies only: an item is its attributes, visibility and qualifiers, a keyword, and
/// everything up to its closing brace or semicolon; function, <c>impl</c>, <c>trait</c> and type bodies are skipped whole
/// (no test libtest runs is declared in one), inline modules and braced item macros (<c>proptest! { … }</c>) are read
/// as items again, <c>macro_rules!</c> bodies (templates, not items) are not. Unbalanced delimiters or an unterminated
/// literal throw <see cref="FormatException"/>: the caller then treats the file as one it cannot read (never as holding
/// no tests).
/// </summary>
public static class RustSource
{
    private enum Kind { Ident, Punct, Open, Close, Literal, Lifetime }

    private readonly record struct Token(Kind Kind, int Start, int End);

    public static RustFile Parse(string text)
    {
        var parser = new Parser(text);
        var inner = new List<RustAttribute>();
        var position = 0;
        var items = parser.Items(ref position, parser.Count, inner);
        return new RustFile(text, items, inner);
    }

    /// <summary>The text's tokens joined by one space: the same for two spellings that differ only in whitespace and comments.</summary>
    public static string Normalize(string text)
    {
        var tokens = Lex(text);
        return string.Join(' ', tokens.Select(t => text[t.Start..t.End]));
    }

    /// <summary>Whether a <c>cfg(…)</c> attribute's text (<see cref="RustAttribute.Text"/>) holds only in test builds (<see cref="RustAttribute.IsCfgTest"/>).</summary>
    public static bool IsTestOnlyCfg(string attribute)
    {
        List<Token> tokens;
        try
        {
            tokens = Lex(attribute);
        }
        catch (FormatException)
        {
            return false;
        }
        string Word(int i) => attribute[tokens[i].Start..tokens[i].End];
        var close = Pairs(tokens);
        if (close is null || tokens.Count < 4 || Word(0) != "cfg" || Word(1) != "(" || close[1] != tokens.Count - 1)
        {
            return false;
        }
        // One predicate, the tokens from..to: a word, a word = "value", or all/any/not ( operands ).
        bool TestOnly(int from, int to)
        {
            if (to - from == 1)
            {
                return tokens[from].Kind == Kind.Ident && Word(from) == "test";
            }
            if (to - from < 3 || tokens[from].Kind != Kind.Ident || Word(from + 1) != "(" || close[from + 1] != to - 1)
            {
                return false;
            }
            var operands = new List<bool>();
            for (var start = from + 2; start < to - 1;)
            {
                var end = start;
                while (end < to - 1 && Word(end) != ",")
                {
                    end = tokens[end].Kind == Kind.Open ? close[end] + 1 : end + 1;
                }
                if (end > start)
                {
                    operands.Add(TestOnly(start, end));
                }
                start = end + 1;
            }
            return Word(from) switch
            {
                "all" => operands.Any(o => o),
                "any" => operands.Count > 0 && operands.All(o => o),
                _ => false,
            };
        }
        return TestOnly(2, tokens.Count - 1);
    }

    /// <summary>For each opening delimiter's token its closing token, or null when the delimiters do not balance.</summary>
    private static Dictionary<int, int>? Pairs(List<Token> tokens)
    {
        var close = new Dictionary<int, int>();
        var open = new Stack<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == Kind.Open)
            {
                open.Push(i);
            }
            else if (tokens[i].Kind == Kind.Close)
            {
                if (open.Count == 0)
                {
                    return null;
                }
                close[open.Pop()] = i;
            }
        }
        return open.Count == 0 ? close : null;
    }

    /// <summary>
    /// The single imports of a <c>use</c> item's text (<see cref="RustFile.TextOf"/>), its tree flattened:
    /// <c>pub use a::{b, c::{d as e, *}};</c> is <c>pub use a::b;</c>, <c>pub use a::c::d as e;</c> and
    /// <c>pub use a::c::*;</c> — so that one import that does not resolve can be left out without the others. Each leaf
    /// has its own statement (the item's attributes and visibility kept), its key (the path and alias alone) and the
    /// spans, relative to the item's text, of the tokens it is made of (a prefix shared by several leaves is in each).
    /// An <c>extern crate</c>, and a tree this reader does not follow, is one leaf: the item as written.
    /// </summary>
    public static IReadOnlyList<RustUseLeaf> UseLeaves(string item)
    {
        var tokens = Lex(item);
        string Word(int i) => item[tokens[i].Start..tokens[i].End];
        var whole = new RustUseLeaf(item.Trim(), Normalize(item), [(0, item.Length)]);
        var close = Pairs(tokens);
        var at = 0;
        while (close is not null && at + 1 < tokens.Count && Word(at) == "#" && Word(at + 1) == "[")
        {
            at = close[at + 1] + 1;
        }
        if (close is not null && at < tokens.Count && Word(at) == "pub")
        {
            at = at + 1 < tokens.Count && Word(at + 1) == "(" ? close[at + 1] + 1 : at + 1;
        }
        if (close is null || at >= tokens.Count || Word(at) != "use" || Word(tokens.Count - 1) != ";")
        {
            return [whole];
        }
        var header = item[..tokens[at].End].Trim();
        string Path(IEnumerable<int> path)
        {
            var text = new StringBuilder();
            var word = false;
            foreach (var t in path)
            {
                var isWord = tokens[t].Kind is Kind.Ident or Kind.Literal;
                text.Append(word && isWord ? " " : "").Append(Word(t));
                word = isWord;
            }
            return text.ToString();
        }
        var leaves = new List<RustUseLeaf>();
        var followed = true;
        void Expand(List<int> prefix, int from, int to)
        {
            var group = from;
            while (group < to && tokens[group].Kind != Kind.Open)
            {
                group++;
            }
            if (group == to)
            {
                if (from == to)
                {
                    return;
                }
                var own = Enumerable.Range(from, to - from).ToList();
                // `a::{self as b}`: the prefix itself, which only the braced form imports under that name.
                var path = Word(from) == "self" && prefix.Count > 0 ? $"{Path(prefix)}{{{Path(own)}}}" : Path(prefix.Concat(own));
                var last = Word(to - 1);
                var name = last is "*" or "_" ? null
                    : own.Count == 1 && last == "self" ? prefix.Where(t => tokens[t].Kind is Kind.Ident or Kind.Literal).Select(Word).LastOrDefault()
                    : last;
                leaves.Add(new RustUseLeaf($"{header} {path};", path, prefix.Concat(own).Select(t => (tokens[t].Start, tokens[t].End)).ToList()) { Name = name });
                return;
            }
            if (Word(group) != "{" || close[group] != to - 1)
            {
                followed = false;
                return;
            }
            var inner = prefix.Concat(Enumerable.Range(from, group - from)).ToList();
            for (var start = group + 1; start < to - 1;)
            {
                var end = start;
                while (end < to - 1 && Word(end) != ",")
                {
                    end = tokens[end].Kind == Kind.Open ? close[end] + 1 : end + 1;
                }
                Expand(inner, start, end);
                start = end + 1;
            }
        }
        Expand([], at + 1, tokens.Count - 1);
        return followed && leaves.Count > 0 ? leaves : [whole];
    }

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i = BlockComment(s, i);
            }
            else if (IsIdentStart(c))
            {
                var j = i + 1;
                while (j < s.Length && IsIdentPart(s[j]))
                {
                    j++;
                }
                var word = s[i..j];
                if (j < s.Length && s[j] == '"' && word is "b" or "c")
                {
                    tokens.Add(new Token(Kind.Literal, i, j = String(s, j)));
                }
                else if (j < s.Length && s[j] == '\'' && word == "b")
                {
                    tokens.Add(new Token(Kind.Literal, i, j = Quote(s, j, tokens: null)));
                }
                else if (word is "r" or "br" or "cr" && RawString(s, j) is { } rawEnd)
                {
                    tokens.Add(new Token(Kind.Literal, i, j = rawEnd));
                }
                else if (word == "r" && j + 1 < s.Length && s[j] == '#' && IsIdentStart(s[j + 1]))
                {
                    // A raw identifier (r#fn): the name without its prefix, never a keyword.
                    var start = ++j;
                    while (j < s.Length && IsIdentPart(s[j]))
                    {
                        j++;
                    }
                    tokens.Add(new Token(Kind.Literal, start - 2, j));
                }
                else
                {
                    tokens.Add(new Token(Kind.Ident, i, j));
                }
                i = j;
            }
            else if (c == '"')
            {
                var end = String(s, i);
                tokens.Add(new Token(Kind.Literal, i, end));
                i = end;
            }
            else if (c == '\'')
            {
                i = Quote(s, i, tokens);
            }
            else if (char.IsAsciiDigit(c))
            {
                var j = i + 1;
                while (j < s.Length && (IsIdentPart(s[j]) || (s[j] == '.' && j + 1 < s.Length && char.IsAsciiDigit(s[j + 1]))))
                {
                    j++;
                }
                tokens.Add(new Token(Kind.Literal, i, j));
                i = j;
            }
            else
            {
                tokens.Add(new Token(c is '(' or '[' or '{' ? Kind.Open : c is ')' or ']' or '}' ? Kind.Close : Kind.Punct, i, i + 1));
                i++;
            }
        }
        return tokens;
    }

    /// <summary>The end of the block comment at <paramref name="i"/> (they nest).</summary>
    private static int BlockComment(string s, int i)
    {
        var depth = 0;
        while (i + 1 < s.Length)
        {
            if (s[i] == '/' && s[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (s[i] == '*' && s[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }
        throw new FormatException("a block comment is not closed");
    }

    /// <summary>The end of the string literal whose opening quote is at <paramref name="i"/>.</summary>
    private static int String(string s, int i)
    {
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] == '\\')
            {
                j++;
            }
            else if (s[j] == '"')
            {
                return j + 1;
            }
        }
        throw new FormatException("a string literal is not closed");
    }

    /// <summary>The end of a raw string whose <c>#</c>s or quote start at <paramref name="i"/> (after its <c>r</c>), or null when none starts there.</summary>
    private static int? RawString(string s, int i)
    {
        var hashes = 0;
        while (i + hashes < s.Length && s[i + hashes] == '#')
        {
            hashes++;
        }
        if (i + hashes >= s.Length || s[i + hashes] != '"')
        {
            return null;
        }
        var close = "\"" + new string('#', hashes);
        var end = s.IndexOf(close, i + hashes + 1, StringComparison.Ordinal);
        return end < 0 ? throw new FormatException("a raw string literal is not closed") : end + close.Length;
    }

    /// <summary>
    /// A character literal or a lifetime at the quote at <paramref name="i"/>: <c>'\…'</c> and <c>'x'</c> (one character,
    /// then a quote) are literals, anything else a lifetime or loop label. Adds the token (when <paramref name="tokens"/>
    /// is given) and returns its end.
    /// </summary>
    private static int Quote(string s, int i, List<Token>? tokens)
    {
        if (i + 1 < s.Length && s[i + 1] == '\\')
        {
            for (var j = i + 2; j < s.Length; j++)
            {
                if (s[j] == '\\')
                {
                    j++;
                }
                else if (s[j] == '\'')
                {
                    tokens?.Add(new Token(Kind.Literal, i, j + 1));
                    return j + 1;
                }
            }
            throw new FormatException("a character literal is not closed");
        }
        var width = i + 2 < s.Length && char.IsSurrogatePair(s[i + 1], s[i + 2]) ? 2 : 1;
        if (i + 1 + width < s.Length && s[i + 1 + width] == '\'')
        {
            tokens?.Add(new Token(Kind.Literal, i, i + 2 + width));
            return i + 2 + width;
        }
        var end = i + 1;
        while (end < s.Length && IsIdentPart(s[end]))
        {
            end++;
        }
        tokens?.Add(new Token(Kind.Lifetime, i, end));
        return end;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly List<Token> _tokens;
        /// <summary>For each opening delimiter's token, its closing token.</summary>
        private readonly Dictionary<int, int> _close = [];

        public Parser(string text)
        {
            _text = text;
            _tokens = Lex(text);
            var open = new Stack<int>();
            for (var i = 0; i < _tokens.Count; i++)
            {
                if (_tokens[i].Kind == Kind.Open)
                {
                    open.Push(i);
                }
                else if (_tokens[i].Kind == Kind.Close)
                {
                    if (open.Count == 0 || Pair(_text[_tokens[open.Peek()].Start]) != _text[_tokens[i].Start])
                    {
                        throw new FormatException($"an unmatched '{_text[_tokens[i].Start]}' at offset {_tokens[i].Start}");
                    }
                    _close[open.Pop()] = i;
                }
            }
            if (open.Count > 0)
            {
                throw new FormatException($"an unclosed '{_text[_tokens[open.Peek()].Start]}' at offset {_tokens[open.Peek()].Start}");
            }
        }

        public int Count => _tokens.Count;

        private static char Pair(char open) => open switch { '(' => ')', '[' => ']', _ => '}' };

        private string Text(int i) => _text[_tokens[i].Start.._tokens[i].End];

        private bool Is(int i, char c) => i < _tokens.Count && _tokens[i].Kind is Kind.Punct or Kind.Open or Kind.Close && _text[_tokens[i].Start] == c;

        private bool IsIdent(int i) => i < _tokens.Count && _tokens[i].Kind == Kind.Ident;

        private bool IsIdent(int i, string word) => IsIdent(i) && _tokens[i].End - _tokens[i].Start == word.Length
            && string.CompareOrdinal(_text, _tokens[i].Start, word, 0, word.Length) == 0;

        /// <summary>A name: an identifier, or a raw identifier (lexed as a literal <c>r#name</c>) without its prefix.</summary>
        private string? Name(int i) => i >= _tokens.Count ? null
            : _tokens[i].Kind == Kind.Ident ? Text(i)
            : _tokens[i].Kind == Kind.Literal && Text(i) is ['r', '#', .. var raw] && raw.Length > 0 && IsIdentStart(raw[0]) ? raw
            : null;

        /// <summary>The attribute whose bracket opens at token <paramref name="open"/>.</summary>
        private RustAttribute Attribute(int open)
        {
            var end = _close[open];
            var path = new StringBuilder();
            for (var i = open + 1; i < end && (IsIdent(i) || Is(i, ':')); i++)
            {
                path.Append(Text(i));
            }
            return new RustAttribute(path.ToString(), string.Join(' ', Enumerable.Range(open + 1, end - open - 1).Select(Text)));
        }

        /// <summary>The items from token <paramref name="i"/> up to (not including) token <paramref name="end"/>.</summary>
        public List<RustItem> Items(ref int i, int end, List<RustAttribute>? inner)
        {
            var items = new List<RustItem>();
            while (i < end)
            {
                if (Is(i, ';'))
                {
                    i++;
                    continue;
                }
                var first = i;
                var attributes = new List<RustAttribute>();
                while (Is(i, '#'))
                {
                    if (Is(i + 1, '!') && Is(i + 2, '['))
                    {
                        inner?.Add(Attribute(i + 2));
                        i = _close[i + 2] + 1;
                        if (attributes.Count == 0)
                        {
                            first = i;
                        }
                    }
                    else if (Is(i + 1, '['))
                    {
                        attributes.Add(Attribute(i + 1));
                        i = _close[i + 1] + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                if (i >= end)
                {
                    break;
                }
                if (Item(ref i, end, first, attributes) is { } item)
                {
                    items.Add(item);
                }
            }
            return items;
        }

        /// <summary>The first token at or after <paramref name="i"/> (before <paramref name="end"/>) that is <paramref name="stop"/> outside any delimiter, else <paramref name="end"/>.</summary>
        private int Find(int i, int end, Func<int, bool> stop)
        {
            while (i < end && !stop(i))
            {
                i = _tokens[i].Kind == Kind.Open ? _close[i] + 1 : i + 1;
            }
            return Math.Min(i, end);
        }

        private RustItem? Item(ref int i, int end, int first, List<RustAttribute> attributes)
        {
            RustItem Make(string kind, string? name, int last, int open = -1, IReadOnlyList<RustItem>? items = null, IReadOnlyList<RustAttribute>? parameters = null) =>
                new(kind, name, _tokens[first].Start, _tokens[Math.Min(last, _tokens.Count) - 1].End, open < 0 ? -1 : _tokens[open].Start,
                    open < 0 ? -1 : _tokens[_close[open]].Start, attributes, parameters ?? [], items ?? []);

            if (IsIdent(i, "pub"))
            {
                i++;
                if (Is(i, '('))
                {
                    i = _close[i] + 1;
                }
            }
            // Qualifiers before the item's keyword: default, async, unsafe, const (of a const fn), extern "abi" (of a fn).
            while (i < end)
            {
                if ((IsIdent(i, "default") || IsIdent(i, "async") || IsIdent(i, "unsafe") || IsIdent(i, "safe")) && IsIdent(i + 1))
                {
                    i++;
                }
                else if (IsIdent(i, "const") && (IsIdent(i + 1, "fn") || IsIdent(i + 1, "unsafe") || IsIdent(i + 1, "async") || IsIdent(i + 1, "extern")))
                {
                    i++;
                }
                else if (IsIdent(i, "extern") && !IsIdent(i + 1, "crate"))
                {
                    var next = i + 1 < end && _tokens[i + 1].Kind == Kind.Literal ? i + 2 : i + 1;
                    if (IsIdent(next, "fn"))
                    {
                        i = next;
                        continue;
                    }
                    // An extern block: its items are foreign declarations, never tests.
                    var block = Find(next, end, t => Is(t, '{') || Is(t, ';'));
                    i = block < end && Is(block, '{') ? _close[block] + 1 : Math.Min(block + 1, end);
                    return Make(RustItem.Other, null, i);
                }
                else
                {
                    break;
                }
            }
            if (i >= end)
            {
                return null;
            }
            if (!IsIdent(i))
            {
                // Not an item (a stray token inside a macro's body): skip it, or its whole group.
                i = _tokens[i].Kind == Kind.Open ? _close[i] + 1 : i + 1;
                return null;
            }
            var keyword = Text(i);
            switch (keyword)
            {
                case "mod" when Name(i + 1) is { } name && Is(i + 2, ';'):
                    i += 3;
                    return Make(RustItem.Mod, name, i);
                case "mod" when Name(i + 1) is { } name && Is(i + 2, '{'):
                {
                    var open = i + 2;
                    var inside = open + 1;
                    var inner = new List<RustAttribute>();
                    var items = Items(ref inside, _close[open], inner);
                    i = _close[open] + 1;
                    return Make(RustItem.Mod, name, i, open, items) with { InnerAttributes = inner };
                }
                case "fn" when Name(i + 1) is { } name:
                {
                    var parameters = new List<RustAttribute>();
                    var open = Find(i + 2, end, t => Is(t, '(') || Is(t, '{') || Is(t, ';'));
                    if (open < end && Is(open, '('))
                    {
                        for (var t = open + 1; t < _close[open]; t++)
                        {
                            if (Is(t, '#') && Is(t + 1, '['))
                            {
                                parameters.Add(Attribute(t + 1));
                                t = _close[t + 1];
                            }
                        }
                        open = Find(_close[open] + 1, end, t => Is(t, '{') || Is(t, ';'));
                    }
                    i = open < end && Is(open, '{') ? _close[open] + 1 : Math.Min(open + 1, end);
                    return Make(RustItem.Fn, name, i, parameters: parameters);
                }
                case "use" or "type" or "static" or "const" or "extern":
                {
                    var import = keyword == "use" || (keyword == "extern" && IsIdent(i + 1, "crate"));
                    i = Math.Min(Find(i + 1, end, t => Is(t, ';')) + 1, end);
                    return Make(import ? RustItem.Use : RustItem.Other, null, i);
                }
                case "struct" or "union" or "enum" or "trait" or "impl":
                {
                    var name = Name(i + 1);
                    var stop = Find(i + 1, end, t => Is(t, '{') || Is(t, ';'));
                    i = stop < end && Is(stop, '{') ? _close[stop] + 1 : Math.Min(stop + 1, end);
                    return Make(RustItem.Other, name, i);
                }
            }
            // A macro in item position: path ! [name] (…); or path ! [name] { … }.
            var bang = i;
            while (IsIdent(bang) || Is(bang, ':'))
            {
                bang++;
            }
            if (bang < end && Is(bang, '!') && bang > i)
            {
                var macro = Text(bang - 1);
                var group = IsIdent(bang + 1) ? bang + 2 : bang + 1;
                if (group < end && _tokens[group].Kind == Kind.Open)
                {
                    var braced = Is(group, '{');
                    i = _close[group] + 1;
                    if (!braced && Is(i, ';'))
                    {
                        i++;
                    }
                    if (!braced || macro == "macro_rules")
                    {
                        return Make(RustItem.Macro, macro, i);
                    }
                    var inside = group + 1;
                    return Make(RustItem.Macro, macro, i, group, Items(ref inside, _close[group], null));
                }
            }
            // Anything else: up to its semicolon or the end of its first brace group.
            var last = Find(i + 1, end, t => Is(t, '{') || Is(t, ';'));
            i = last < end && Is(last, '{') ? _close[last] + 1 : Math.Min(last + 1, end);
            return Make(RustItem.Other, null, i);
        }
    }
}
