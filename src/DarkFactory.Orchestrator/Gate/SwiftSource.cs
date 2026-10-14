namespace DarkFactory.Orchestrator.Gate;

/// <summary>An attribute of a Swift declaration: its name as written (<c>Test</c>, <c>Testing.Test</c>, <c>MainActor</c>) and the text of its arguments (null when it has none).</summary>
public sealed record SwiftAttribute(string Name, string? Arguments)
{
    /// <summary>Swift Testing's test attribute, <c>@Test</c> or <c>@Testing.Test</c>.</summary>
    public bool IsTest => Name is "Test" or "Testing.Test";

    /// <summary>
    /// A parameterized test: <c>@Test(…, arguments: …)</c>, a top-level <c>arguments:</c> label of the attribute's
    /// arguments (read from their tokens, so a display name that says "arguments:" is not one).
    /// </summary>
    public bool HasArguments => Arguments is { } text && SwiftSource.HasTopLevelLabel(text, "arguments");
}

/// <summary>
/// One function declared at a file's top level or in a type's body (<see cref="SwiftSource.Parse"/>): its name as written,
/// the types it is declared in, outermost first (an extension's extended type, dotted names split: <c>extension A.B</c> is
/// <c>A</c>, <c>B</c>), its parameter list's text and its attributes.
/// </summary>
public sealed record SwiftFunc(string Name, IReadOnlyList<string> Types, string Parameters, IReadOnlyList<SwiftAttribute> Attributes, int Start)
{
    public bool IsTest => Attributes.Any(a => a.IsTest);

    /// <summary>A function with parameters, or a <c>@Test(arguments:)</c>: Swift Testing runs it once per argument (a parameterized test).</summary>
    public bool IsParameterized => Parameters.Trim().Length > 0 || Attributes.Any(a => a.IsTest && a.HasArguments);

    /// <summary>
    /// The function as Swift Testing names it: <c>name()</c>, or with its parameters' argument labels <c>name(x:_:)</c>.
    /// </summary>
    public string Signature => $"{Name}({string.Concat(SwiftSource.Labels(Parameters).Select(l => l + ":"))})";
}

/// <summary>A parsed Swift source file: its text, its imported modules and every function declared at its top level or in a type body.</summary>
public sealed record SwiftFile(string Text, IReadOnlyList<string> Imports, IReadOnlyList<SwiftFunc> Funcs)
{
    /// <summary>Swift Testing's tests: the functions with <c>@Test</c>.</summary>
    public IEnumerable<SwiftFunc> Tests => Funcs.Where(f => f.IsTest);

    /// <summary>
    /// XCTest's tests, as XCTest finds them: in a file that imports XCTest, every function of a type whose name starts with
    /// <c>test</c> and that takes no parameter and has no <c>@Test</c> (XCTest runs every such method of an
    /// <c>XCTestCase</c> subclass; which types are subclasses is not read, so this may hold a method of another type too).
    /// </summary>
    public IEnumerable<SwiftFunc> XCTestMethods => Imports.Contains("XCTest")
        ? Funcs.Where(f => !f.IsTest && f.Types.Count > 0 && f.Name.StartsWith("test", StringComparison.Ordinal) && f.Parameters.Trim().Length == 0)
        : [];
}

/// <summary>
/// A lexer-level reader of Swift source (Phase 5, sc-26127), as <see cref="RustSource"/> is of Rust: no Swift parser ships
/// with .NET and nothing is compiled, so the gate finds a file's functions, the types they are declared in and their
/// attributes from its tokens. The lexer knows what can hide an <c>@Test</c> or a brace: line comments and nested block
/// comments; string literals — single-line, multi-line (<c>"""</c>), raw (<c>#"…"#</c>, any number of <c>#</c>) — with
/// their escapes and interpolations (<c>\(…)</c>, <c>\#(…)</c>), which may hold strings of their own; extended regex
/// literals (<c>#/…/#</c>); and backticked identifiers. The declaration reader walks the file's top level and type bodies
/// (<c>struct</c>, <c>class</c>, <c>enum</c>, <c>actor</c>, <c>extension</c>, <c>protocol</c>) only: function, property,
/// initializer and closure bodies are skipped whole (Swift Testing runs no test declared in one). Unbalanced delimiters
/// or an unterminated literal throw <see cref="FormatException"/>: the caller then treats the file as one it cannot read,
/// never as holding no tests. So does an <c>@Test</c> the reader does not put on a function it records (one before a
/// declaration it does not understand, or in a body it skips): the file's test attributes are counted from its tokens and
/// must all be on recorded functions. Not read, accepted: a bare regex literal (<c>/…/</c>, which the language tells from division
/// by context) holding a quote or a brace can mis-lex; that fails as unbalanced far more often than it hides a test.
/// </summary>
public static class SwiftSource
{
    private enum Kind { Ident, Punct, Open, Close, Literal }

    private readonly record struct Token(Kind Kind, int Start, int End);

    public static SwiftFile Parse(string text)
    {
        var tokens = Lex(text);
        var close = Pairs(text, tokens) ?? throw new FormatException("its brackets do not balance");
        var funcs = new List<SwiftFunc>();
        var imports = new List<string>();
        new Reader(text, tokens, close, funcs, imports).Scope(0, tokens.Count, []);
        // The safety net: every @Test the lexer saw must be on a function the reader recorded. One it lost (in a body it
        // skipped, swallowed by a declaration it did not understand) would otherwise be a test the gate never sees.
        var written = TestAttributeTokens(text, tokens);
        var recorded = funcs.Sum(f => f.Attributes.Count(a => a.IsTest));
        if (written != recorded)
        {
            throw new FormatException($"it has {written} @Test attribute(s) but {recorded} on the test functions this reader found");
        }
        return new SwiftFile(text, imports, funcs);
    }

    /// <summary>How many <c>@Test</c> and <c>@Testing.Test</c> attributes the tokens hold, however they are spaced.</summary>
    private static int TestAttributeTokens(string text, List<Token> tokens)
    {
        string Word(int i) => i < tokens.Count ? text[tokens[i].Start..tokens[i].End] : "";
        var count = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (Word(i) == "@" && (Word(i + 1) == "Test" || (Word(i + 1) == "Testing" && Word(i + 2) == "." && Word(i + 3) == "Test")))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// The test targets a manifest (<c>Package.swift</c>) declares: each <c>testTarget(name: "…", …)</c> call's name, and
    /// whether the call sets a <c>path:</c> (its sources are then not at <c>Tests/&lt;name&gt;/</c>). A name that is not a
    /// plain string literal is not read (a manifest that computes it declares nothing this reader can see).
    /// </summary>
    public static IReadOnlyList<(string Name, bool CustomPath)> TestTargets(string manifest)
    {
        var tokens = Lex(manifest);
        var close = Pairs(manifest, tokens) ?? throw new FormatException("its brackets do not balance");
        string Word(int i) => manifest[tokens[i].Start..tokens[i].End];
        var targets = new List<(string, bool)>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Ident || Word(i) != "testTarget" || Word(i + 1) != "(")
            {
                continue;
            }
            string? name = null;
            var path = false;
            for (var j = i + 2; j + 2 < close[i + 1]; j = tokens[j].Kind == Kind.Open ? close[j] + 1 : j + 1)
            {
                if (tokens[j].Kind == Kind.Ident && Word(j + 1) == ":")
                {
                    if (Word(j) == "name" && tokens[j + 2].Kind == Kind.Literal && PlainString(Word(j + 2)) is { } value)
                    {
                        name ??= value;
                    }
                    path |= Word(j) == "path";
                }
            }
            if (name is not null)
            {
                targets.Add((name, path));
            }
        }
        return targets;
    }

    /// <summary>A literal's value when it is a plain, single-line string with no escape or interpolation (<c>"Words"</c>), else null.</summary>
    private static string? PlainString(string literal) =>
        literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"' && !literal.StartsWith("\"\"\"", StringComparison.Ordinal)
            && literal[1..^1] is var inner && !inner.Contains('\\') && !inner.Contains('"') ? inner : null;

    /// <summary>Whether a parenthesized argument list's text has <paramref name="label"/><c>:</c> at its top level.</summary>
    public static bool HasTopLevelLabel(string arguments, string label)
    {
        List<Token> tokens;
        try
        {
            tokens = Lex(arguments);
        }
        catch (FormatException)
        {
            return true; // unreadable arguments: fail closed (treated as parameterized)
        }
        var depth = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            depth += tokens[i].Kind switch { Kind.Open => 1, Kind.Close => -1, _ => 0 };
            if (depth == 0 && tokens[i].Kind == Kind.Ident && arguments[tokens[i].Start..tokens[i].End] == label && arguments[tokens[i + 1].Start..tokens[i + 1].End] == ":")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The argument labels of a parameter list's text (<c>_ x: Int, y: Int</c> → <c>_</c>, <c>y</c>): each parameter's first word.</summary>
    public static IReadOnlyList<string> Labels(string parameters)
    {
        if (parameters.Trim().Length == 0)
        {
            return [];
        }
        var tokens = Lex(parameters);
        var labels = new List<string>();
        var depth = 0;
        var first = true;
        foreach (var token in tokens)
        {
            var word = parameters[token.Start..token.End];
            if (depth == 0 && first && token.Kind == Kind.Ident)
            {
                labels.Add(word);
                first = false;
            }
            depth += token.Kind switch { Kind.Open => 1, Kind.Close => -1, _ => 0 };
            if (depth == 0 && word == ",")
            {
                first = true;
            }
        }
        return labels;
    }

    // ---- The lexer ---------------------------------------------------------------------------------------------------------

    private static List<Token> Lex(string text)
    {
        var lexer = new Lexer(text);
        var tokens = new List<Token>();
        while (lexer.Next() is { } token)
        {
            tokens.Add(token);
        }
        return tokens;
    }

    private sealed class Lexer(string text)
    {
        private int _at;

        private char At(int i) => i < text.Length ? text[i] : '\0';

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$' || c > 0x7f;

        private static bool IsIdentPart(char c) => IsIdentStart(c) || char.IsDigit(c);

        /// <summary>The next token, or null at the end of the text.</summary>
        public Token? Next()
        {
            while (_at < text.Length)
            {
                var c = text[_at];
                if (char.IsWhiteSpace(c))
                {
                    _at++;
                }
                else if (c == '/' && At(_at + 1) == '/')
                {
                    var end = text.IndexOf('\n', _at);
                    _at = end < 0 ? text.Length : end + 1;
                }
                else if (c == '/' && At(_at + 1) == '*')
                {
                    SkipBlockComment();
                }
                else
                {
                    break;
                }
            }
            if (_at >= text.Length)
            {
                return null;
            }
            var start = _at;
            var ch = text[_at];
            if (ch == '"' || (ch == '#' && Hashes(_at) is var n && (At(_at + n) == '"' || At(_at + n) == '/')))
            {
                var hashes = Hashes(_at);
                if (At(_at + hashes) == '/')
                {
                    SkipRegex(hashes);
                }
                else
                {
                    SkipString(hashes);
                }
                return new Token(Kind.Literal, start, _at);
            }
            if (ch == '#' && IsIdentStart(At(_at + 1)))
            {
                _at++;
                while (_at < text.Length && IsIdentPart(text[_at]))
                {
                    _at++;
                }
                return new Token(Kind.Ident, start, _at);
            }
            if (ch == '`')
            {
                var end = text.IndexOf('`', _at + 1);
                if (end < 0 || text.AsSpan(_at, end - _at).Contains('\n'))
                {
                    throw new FormatException($"an unterminated backticked identifier at {start}");
                }
                _at = end + 1;
                return new Token(Kind.Ident, start, _at);
            }
            if (IsIdentStart(ch))
            {
                while (_at < text.Length && IsIdentPart(text[_at]))
                {
                    _at++;
                }
                return new Token(Kind.Ident, start, _at);
            }
            if (char.IsDigit(ch))
            {
                while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
                {
                    _at++;
                }
                return new Token(Kind.Literal, start, _at);
            }
            _at++;
            return new Token(ch switch { '(' or '[' or '{' => Kind.Open, ')' or ']' or '}' => Kind.Close, _ => Kind.Punct }, start, _at);
        }

        private int Hashes(int from)
        {
            var n = 0;
            while (At(from + n) == '#')
            {
                n++;
            }
            return n;
        }

        private void SkipBlockComment()
        {
            var start = _at;
            var depth = 0;
            while (_at < text.Length)
            {
                if (text[_at] == '/' && At(_at + 1) == '*')
                {
                    depth++;
                    _at += 2;
                }
                else if (text[_at] == '*' && At(_at + 1) == '/')
                {
                    depth--;
                    _at += 2;
                    if (depth == 0)
                    {
                        return;
                    }
                }
                else
                {
                    _at++;
                }
            }
            throw new FormatException($"an unterminated block comment at {start}");
        }

        /// <summary>Whether <paramref name="count"/> <c>#</c> follow position <paramref name="from"/>.</summary>
        private bool HashesAt(int from, int count)
        {
            for (var k = 0; k < count; k++)
            {
                if (At(from + k) != '#')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>A string literal with <paramref name="hashes"/> <c>#</c> around it: single-line or multi-line, with escapes and interpolations.</summary>
        private void SkipString(int hashes)
        {
            var start = _at;
            _at += hashes;
            var multi = string.CompareOrdinal(text, _at, "\"\"\"", 0, 3) == 0;
            _at += multi ? 3 : 1;
            while (true)
            {
                if (_at >= text.Length)
                {
                    throw new FormatException($"an unterminated string literal at {start}");
                }
                var c = text[_at];
                if (c == '\\' && HashesAt(_at + 1, hashes))
                {
                    var after = _at + 1 + hashes;
                    if (At(after) == '(')
                    {
                        _at = after;
                        SkipInterpolation();
                    }
                    else
                    {
                        _at = after + 1;
                    }
                    continue;
                }
                if (multi && string.CompareOrdinal(text, _at, "\"\"\"", 0, 3) == 0 && HashesAt(_at + 3, hashes))
                {
                    _at += 3 + hashes;
                    return;
                }
                if (!multi && c == '"' && HashesAt(_at + 1, hashes))
                {
                    _at += 1 + hashes;
                    return;
                }
                if (!multi && c == '\n')
                {
                    throw new FormatException($"an unterminated string literal at {start}");
                }
                _at++;
            }
        }

        /// <summary>An interpolation <c>(…)</c> inside a string, read as tokens (it may hold strings, comments and parentheses of its own).</summary>
        private void SkipInterpolation()
        {
            var start = _at;
            var depth = 0;
            while (Next() is { } token)
            {
                var c = text[token.Start];
                if (token.Kind == Kind.Open && c == '(')
                {
                    depth++;
                }
                else if (token.Kind == Kind.Close && c == ')' && --depth == 0)
                {
                    return;
                }
            }
            throw new FormatException($"an unterminated string interpolation at {start}");
        }

        /// <summary>An extended regex literal <c>#/…/#</c> with <paramref name="hashes"/> <c>#</c>.</summary>
        private void SkipRegex(int hashes)
        {
            var start = _at;
            _at += hashes + 1;
            while (_at < text.Length)
            {
                if (text[_at] == '\\')
                {
                    _at += 2;
                    continue;
                }
                if (text[_at] == '/' && HashesAt(_at + 1, hashes))
                {
                    _at += 1 + hashes;
                    return;
                }
                _at++;
            }
            throw new FormatException($"an unterminated regex literal at {start}");
        }
    }

    /// <summary>For each opening bracket's token its closing token, or null when they do not balance (or a kind does not match its pair).</summary>
    private static Dictionary<int, int>? Pairs(string text, List<Token> tokens)
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
                if (open.Count == 0 || (text[tokens[open.Peek()].Start], text[tokens[i].Start]) is not ('(', ')') and not ('[', ']') and not ('{', '}'))
                {
                    return null;
                }
                close[open.Pop()] = i;
            }
        }
        return open.Count == 0 ? close : null;
    }

    // ---- The declaration reader --------------------------------------------------------------------------------------------

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal) { "struct", "class", "enum", "actor", "extension", "protocol" };

    /// <summary>Words that may stand between a declaration's attributes and its keyword.</summary>
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "fileprivate", "internal", "open", "package", "static", "final", "override", "mutating", "nonmutating",
        "nonisolated", "isolated", "required", "convenience", "dynamic", "lazy", "weak", "unowned", "optional", "indirect", "distributed",
        "consuming", "borrowing", "sending", "async", "prefix", "postfix", "infix",
    };

    /// <summary>What may follow <c>class</c> when it is a modifier (<c>class func</c>, <c>class var</c>), not a type.</summary>
    private static readonly HashSet<string> AfterClassModifier = new(StringComparer.Ordinal) { "func", "var", "let", "subscript", "init", "deinit", "typealias" };

    /// <summary>The kinds an import may name (<c>import struct Foo.Bar</c>).</summary>
    private static readonly HashSet<string> ImportKinds = new(StringComparer.Ordinal) { "struct", "class", "enum", "protocol", "typealias", "func", "var", "let", "actor" };

    private sealed class Reader(string text, List<Token> tokens, Dictionary<int, int> close, List<SwiftFunc> funcs, List<string> imports)
    {
        private string Word(int i) => text[tokens[i].Start..tokens[i].End];

        /// <summary>Reads the declarations of tokens <paramref name="from"/>..<paramref name="to"/> (exclusive), declared in <paramref name="types"/>.</summary>
        public void Scope(int from, int to, IReadOnlyList<string> types)
        {
            var pending = new List<SwiftAttribute>();
            var i = from;
            while (i < to)
            {
                var word = Word(i);
                if (word == "@" && i + 1 < to && tokens[i + 1].Kind == Kind.Ident)
                {
                    var name = Word(i + 1);
                    var j = i + 2;
                    while (j + 1 < to && Word(j) == "." && tokens[j + 1].Kind == Kind.Ident && tokens[j].Start == tokens[j - 1].End)
                    {
                        name += "." + Word(j + 1);
                        j += 2;
                    }
                    string? arguments = null;
                    if (j < to && Word(j) == "(")
                    {
                        arguments = text[tokens[j].End..tokens[close[j]].Start];
                        j = close[j] + 1;
                    }
                    pending.Add(new SwiftAttribute(name, arguments));
                    i = j;
                    continue;
                }
                if (tokens[i].Kind == Kind.Ident && Modifiers.Contains(word))
                {
                    // private(set), nonisolated(unsafe): the modifier's own argument.
                    i = i + 1 < to && Word(i + 1) == "(" && tokens[i + 1].Start == tokens[i].End ? close[i + 1] + 1 : i + 1;
                    continue;
                }
                if (word == "class" && i + 1 < to && (AfterClassModifier.Contains(Word(i + 1)) || Modifiers.Contains(Word(i + 1))))
                {
                    i++;
                    continue;
                }
                if (word == "func" && tokens[i].Kind == Kind.Ident)
                {
                    var recorded = funcs.Count;
                    i = Func(i, to, types, pending);
                    pending = funcs.Count > recorded ? [] : Drop(pending, at: i);
                    continue;
                }
                if (tokens[i].Kind == Kind.Ident && TypeKeywords.Contains(word))
                {
                    i = Type(i, to, types);
                    pending = Drop(pending, at: i);
                    continue;
                }
                if (word == "import" && types.Count == 0)
                {
                    // import Foo, import struct Foo.Bar, @testable import Foo: the module is the first word of the path.
                    var j = i + 1;
                    if (j + 1 < to && ImportKinds.Contains(Word(j)) && tokens[j + 1].Kind == Kind.Ident)
                    {
                        j++;
                    }
                    if (j < to && tokens[j].Kind == Kind.Ident)
                    {
                        imports.Add(Word(j));
                    }
                    pending = Drop(pending, at: i);
                    i = j + 1;
                    continue;
                }
                pending = Drop(pending, at: i);
                i = tokens[i].Kind == Kind.Open ? close[i] + 1 : i + 1;
            }
            Drop(pending, at: to);
        }

        /// <summary>
        /// Attributes that belong to no function the reader recorded are dropped; a test attribute among them throws (the
        /// file is then one the gate cannot read): a lost <c>@Test</c> would be a test the gate never sees.
        /// </summary>
        private List<SwiftAttribute> Drop(List<SwiftAttribute> pending, int at)
        {
            if (pending.Any(a => a.IsTest))
            {
                throw new FormatException($"an @Test attribute near offset {(at < tokens.Count ? tokens[at].Start : text.Length)} is on no function this reader understands");
            }
            return [];
        }

        /// <summary>A function: its name (an identifier, or an operator's characters) up to its parameter list. Its body is left to the scope, which skips it.</summary>
        private int Func(int at, int to, IReadOnlyList<string> types, List<SwiftAttribute> attributes)
        {
            var j = at + 1;
            var name = "";
            if (j < to && tokens[j].Kind == Kind.Ident)
            {
                name = Word(j++);
            }
            else
            {
                while (j < to && tokens[j].Kind == Kind.Punct)
                {
                    name += Word(j++);
                }
            }
            // A generic parameter clause (its angle brackets are no delimiters to the lexer), then the parameter list. A
            // brace first is not a function this reader understands: the scope reads on from there.
            while (j < to && tokens[j].Kind != Kind.Open)
            {
                j++;
            }
            if (j >= to || name.Length == 0 || Word(j) != "(")
            {
                return j;
            }
            funcs.Add(new SwiftFunc(name, types, text[tokens[j].End..tokens[close[j]].Start], attributes, tokens[at].Start));
            return close[j] + 1;
        }

        /// <summary>A type or extension: its (dotted) name, then its body read as a scope of its own.</summary>
        private int Type(int at, int to, IReadOnlyList<string> types)
        {
            var j = at + 1;
            var names = new List<string>();
            // No name right after the keyword: not a declaration (actor is a contextual keyword, so also an identifier:
            // `let actor = 1`, `var actor: A`). Read on from the next token, never scanning ahead to a brace.
            if (j >= to || tokens[j].Kind != Kind.Ident)
            {
                return at + 1;
            }
            names.Add(Word(j));
            j++;
            while (j + 1 < to && Word(j) == "." && tokens[j + 1].Kind == Kind.Ident)
            {
                names.Add(Word(j + 1));
                j += 2;
            }
            // An actor declaration's name is followed by its body, an inheritance clause, generic parameters or a where
            // clause; anything else is `actor` used as an identifier.
            if (Word(at) == "actor" && (j >= to || Word(j) is not ("{" or ":" or "<" or "where")))
            {
                return at + 1;
            }
            while (j < to && Word(j) != "{")
            {
                j = tokens[j].Kind == Kind.Open ? close[j] + 1 : j + 1;
            }
            if (j >= to)
            {
                return j;
            }
            Scope(j + 1, close[j], [.. types, .. names]);
            return close[j] + 1;
        }
    }
}
