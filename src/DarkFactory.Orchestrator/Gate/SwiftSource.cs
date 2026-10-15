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

    /// <summary>The modifiers written before <c>func</c> (<c>static</c>, <c>class</c>, <c>private</c>, <c>override</c>, …; sc-26129).</summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Whether the function has a generic parameter clause (<c>func f&lt;T&gt;()</c>).</summary>
    public bool Generic { get; init; }

    /// <summary>What its signature says after the parameter list, up to its body: <c>async throws</c>, <c>-&gt; Int</c>, a <c>where</c> clause.</summary>
    public string Tail { get; init; } = "";

    /// <summary>Whether it is declared inside a conditional-compilation block (<c>#if</c>), directly or through its type.</summary>
    public bool Conditional { get; init; }

    /// <summary>The type or extension it is declared in (its innermost), or null at the top level.</summary>
    public SwiftType? Container { get; init; }

    /// <summary>
    /// The <c>arguments:</c> of its <c>@Test</c>, its tokens joined by single spaces (comments and layout left out), or
    /// null when it has none: what a parameterized test runs over (sc-26129).
    /// </summary>
    public string? TestArguments => Attributes.FirstOrDefault(a => a.IsTest && a.HasArguments) is { Arguments: { } text } ? SwiftSource.LabelledTail(text, "arguments") : null;

    /// <summary>
    /// Its text from the <c>func</c> keyword through its body (or the end of its signature when it has none): what tells a
    /// method moved unchanged from a new one (sc-26129).
    /// </summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// A type or extension declared at a file's top level or in another type's body (sc-26129): its keyword, its names from the
/// top level (an extension's dotted name split), what its inheritance clause names (each entry's tokens, unspaced:
/// <c>XCTestCase</c>, <c>XCTest.XCTestCase</c>, <c>Base&lt;Int&gt;</c>), whether it has generic parameters, its attributes
/// and whether it is inside a conditional-compilation block.
/// </summary>
public sealed record SwiftType(string Keyword, IReadOnlyList<string> Names, IReadOnlyList<string> Inherits, bool Generic, IReadOnlyList<SwiftAttribute> Attributes, bool Conditional)
{
    public string Name => Names[^1];
}

/// <summary>A parsed Swift source file: its text, its imported modules and every function declared at its top level or in a type body.</summary>
public sealed record SwiftFile(string Text, IReadOnlyList<string> Imports, IReadOnlyList<SwiftFunc> Funcs)
{
    /// <summary>Every type and extension the file declares at its top level or in a type's body (sc-26129).</summary>
    public IReadOnlyList<SwiftType> Types { get; init; } = [];

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
/// One declaration of a file's top level or of a type's body, with its span (<see cref="SwiftSource.Layout"/>, sc-26128):
/// from its first attribute or modifier to its last token (a function's body, a type's closing brace). <see cref="Keyword"/>
/// is the declaration's keyword (<c>func</c>, <c>struct</c>, <c>import</c>, <c>var</c>, …; <c>#if</c> and its kin for a
/// conditional-compilation line; empty for tokens that start with no keyword). A type's <see cref="BodyOpen"/> is the offset
/// of its body's <c>{</c> (−1 for every other item) and <see cref="Children"/> its members.
/// </summary>
public sealed record SwiftItem(string Keyword, string Name, int Start, int End, int BodyOpen, IReadOnlyList<SwiftItem> Children)
{
    public bool IsType => BodyOpen >= 0 || SwiftSource.IsTypeKeyword(Keyword);

    public bool IsImport => Keyword == "import";

    /// <summary>A <c>#if</c>, <c>#elseif</c>, <c>#else</c> or <c>#endif</c> line: no declaration, and nothing a test lacks shows there.</summary>
    public bool IsDirective => Keyword is "#if" or "#elseif" or "#else" or "#endif";
}

/// <summary>A file's declarations with their spans (<see cref="SwiftSource.Layout"/>), and a compiler position's place among them.</summary>
public sealed record SwiftLayout(string Text, IReadOnlyList<SwiftItem> Items)
{
    /// <summary>
    /// The offset in <see cref="Text"/> of swiftc's 1-based <paramref name="line"/> and <paramref name="column"/> (a column
    /// counts UTF-8 bytes, as swiftc's do), clamped to the line; −1 when the line is not in the file.
    /// </summary>
    public int Position(int line, int column)
    {
        if (line < 1)
        {
            return -1;
        }
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
        var end = Text.IndexOf('\n', start);
        end = end < 0 ? Text.Length : end;
        var at = start;
        for (var bytes = 0; at < end && bytes < column - 1; at++)
        {
            bytes += char.IsHighSurrogate(Text[at]) ? 4 : System.Text.Encoding.UTF8.GetByteCount(Text.AsSpan(at, 1));
            if (char.IsHighSurrogate(Text[at]))
            {
                at++;
            }
        }
        return Math.Min(at, Math.Max(end - 1, start));
    }

    /// <summary>The items holding <paramref name="position"/>, outermost first (a type, then its member).</summary>
    public IReadOnlyList<SwiftItem> ItemsAt(int position)
    {
        var chain = new List<SwiftItem>();
        for (var items = Items; items.FirstOrDefault(i => i.Start <= position && position < i.End) is { } item; items = item.Children)
        {
            chain.Add(item);
        }
        return chain;
    }

    /// <summary>The 1-based line of <paramref name="position"/>.</summary>
    public int LineOf(int position) => Text.AsSpan(0, position).Count('\n') + 1;
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
        var types = new List<SwiftType>();
        new Reader(text, tokens, close, funcs, imports, types).Scope(0, tokens.Count, [], null);
        // The safety net: every @Test the lexer saw must be on a function the reader recorded. One it lost (in a body it
        // skipped, swallowed by a declaration it did not understand) would otherwise be a test the gate never sees.
        var written = TestAttributeTokens(text, tokens);
        var recorded = funcs.Sum(f => f.Attributes.Count(a => a.IsTest));
        if (written != recorded)
        {
            throw new FormatException($"it has {written} @Test attribute(s) but {recorded} on the test functions this reader found");
        }
        return new SwiftFile(text, imports, funcs) { Types = types };
    }

    /// <summary>
    /// The text from a top-level <paramref name="label"/><c>:</c> of an argument list to its end, as its tokens joined by
    /// single spaces (so layout and comments do not count); null when the label is not there. <c>@Test</c>'s
    /// <c>arguments:</c> is its last parameter, so this is every collection it names.
    /// </summary>
    public static string? LabelledTail(string arguments, string label)
    {
        List<Token> tokens;
        try
        {
            tokens = Lex(arguments);
        }
        catch (FormatException)
        {
            return arguments; // unreadable: the text itself, so any change to it is seen
        }
        var depth = 0;
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            depth += tokens[i].Kind switch { Kind.Open => 1, Kind.Close => -1, _ => 0 };
            if (depth == 0 && tokens[i].Kind == Kind.Ident && arguments[tokens[i].Start..tokens[i].End] == label && arguments[tokens[i + 1].Start..tokens[i + 1].End] == ":")
            {
                return string.Join(" ", tokens.Skip(i).Select(t => arguments[t.Start..t.End]));
            }
        }
        return null;
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
    public static IReadOnlyList<(string Name, bool CustomPath)> TestTargets(string manifest) =>
        TestTargetPaths(manifest).Select(t => (t.Name, t.CustomPath)).ToList();

    /// <summary>
    /// <see cref="TestTargets"/> with each declared <c>path:</c> (sc-26134): its plain string, or null when there is none or
    /// it is not a plain string literal (<c>CustomPath</c> then still says one is declared).
    /// </summary>
    public static IReadOnlyList<(string Name, bool CustomPath, string? Path)> TestTargetPaths(string manifest)
    {
        var tokens = Lex(manifest);
        var close = Pairs(manifest, tokens) ?? throw new FormatException("its brackets do not balance");
        string Word(int i) => manifest[tokens[i].Start..tokens[i].End];
        var targets = new List<(string, bool, string?)>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != Kind.Ident || Word(i) != "testTarget" || Word(i + 1) != "(")
            {
                continue;
            }
            string? name = null;
            var path = false;
            string? pathValue = null;
            for (var j = i + 2; j + 2 < close[i + 1]; j = tokens[j].Kind == Kind.Open ? close[j] + 1 : j + 1)
            {
                if (tokens[j].Kind == Kind.Ident && Word(j + 1) == ":")
                {
                    if (Word(j) == "name" && tokens[j + 2].Kind == Kind.Literal && PlainString(Word(j + 2)) is { } value)
                    {
                        name ??= value;
                    }
                    if (Word(j) == "path" && !path && tokens[j + 2].Kind == Kind.Literal)
                    {
                        pathValue = PlainString(Word(j + 2));
                    }
                    path |= Word(j) == "path";
                }
            }
            if (name is not null)
            {
                targets.Add((name, path, pathValue));
            }
        }
        return targets;
    }

    /// <summary>Whether <paramref name="keyword"/> declares a type or an extension.</summary>
    public static bool IsTypeKeyword(string keyword) => TypeKeywords.Contains(keyword);

    /// <summary>The keywords that declare something (a new item starts at one on its own line).</summary>
    private static readonly HashSet<string> DeclKeywords = new(StringComparer.Ordinal)
    {
        "func", "var", "let", "init", "deinit", "subscript", "typealias", "associatedtype", "struct", "class", "enum", "actor", "extension",
        "protocol", "import", "case", "operator", "precedencegroup", "macro",
    };

    /// <summary>The modifiers that may begin a declaration's line (not <c>async</c>, <c>sending</c> and the like, which also continue a signature or a type).</summary>
    private static bool IsLeadingModifier(string word) => Modifiers.Contains(word) && word is not ("async" or "sending" or "consuming" or "borrowing" or "isolated");

    /// <summary>
    /// A file's declarations with their spans (sc-26128): what a base build's compiler error is placed in. The top level
    /// and every type's body are split into items, each from its first attribute or modifier through its declaration
    /// (bodies and bracketed groups whole); a new item starts at an attribute, a modifier or a declaration keyword that
    /// begins a line (or follows a <c>;</c> or a body's <c>}</c>) once the item before has its keyword, so a multi-line signature or an
    /// attribute on the line above its declaration stays one item. A conditional-compilation line (<c>#if …</c>) is an
    /// item of its own. Throws <see cref="FormatException"/> as <see cref="Parse"/> does when the brackets do not balance.
    /// </summary>
    public static SwiftLayout Layout(string text)
    {
        var tokens = Lex(text);
        var close = Pairs(text, tokens) ?? throw new FormatException("its brackets do not balance");
        return new SwiftLayout(text, Segment(text, tokens, close, 0, tokens.Count));
    }

    private static List<SwiftItem> Segment(string text, List<Token> tokens, Dictionary<int, int> close, int from, int to)
    {
        string Word(int i) => text[tokens[i].Start..tokens[i].End];
        bool LineStart(int i) => i == from || Word(i - 1) == ";" || text.AsSpan(tokens[i - 1].End, tokens[i].Start - tokens[i - 1].End).Contains('\n');
        // After a body ({ … }) a declaration may follow on the same line: `struct S { @Test func a() {} @Test func b() {} }`.
        bool AfterBody(int i) => i > from && Word(i - 1) == "}";
        bool ModifiersThenDeclaration(int i)
        {
            while (i < to && tokens[i].Kind == Kind.Ident && Modifiers.Contains(Word(i)))
            {
                i++;
            }
            return i < to && tokens[i].Kind == Kind.Ident && DeclKeywords.Contains(Word(i));
        }
        var items = new List<SwiftItem>();
        int start = -1, end = -1, bodyOpen = -1;
        string keyword = "", name = "";
        List<SwiftItem> children = [];
        void Flush()
        {
            if (start >= 0)
            {
                items.Add(new SwiftItem(keyword, name, start, end, bodyOpen, children));
            }
            (start, end, bodyOpen, keyword, name, children) = (-1, -1, -1, "", "", []);
        }
        var i = from;
        while (i < to)
        {
            var token = tokens[i];
            var word = Word(i);
            if (word is "#if" or "#elseif" or "#else" or "#endif")
            {
                // A conditional-compilation line: an item of its own, through the end of its line.
                Flush();
                var j = i + 1;
                while (j < to && !LineStart(j))
                {
                    j = tokens[j].Kind == Kind.Open ? close[j] + 1 : j + 1;
                }
                items.Add(new SwiftItem(word, "", token.Start, tokens[j - 1].End, -1, []));
                i = j;
                continue;
            }
            var next = i + 1 < to ? Word(i + 1) : "";
            var classModifier = word == "class" && (AfterClassModifier.Contains(next) || Modifiers.Contains(next));
            var actorWord = word == "actor" && !IsActorDeclaration(text, tokens, i, to);
            var declares = token.Kind == Kind.Ident && !classModifier && !actorWord && (DeclKeywords.Contains(word) || word.StartsWith('#'));
            // A modifier that may also continue a signature (`async`, `consuming`, …) starts a declaration only when modifiers
            // and then a declaration keyword follow it (`async func h()` on its own line, not `    async throws -> Int`).
            var prefix = (word == "@" && i + 1 < to && tokens[i + 1].Kind == Kind.Ident) || classModifier
                || (token.Kind == Kind.Ident && (IsLeadingModifier(word) || (Modifiers.Contains(word) && ModifiersThenDeclaration(i))));
            if ((declares || prefix) && start >= 0 && keyword.Length > 0 && (LineStart(i) || AfterBody(i)))
            {
                Flush();
            }
            if (start < 0)
            {
                start = token.Start;
            }
            if (declares && keyword.Length == 0)
            {
                keyword = word;
                name = i + 1 < to && tokens[i + 1].Kind == Kind.Ident ? next : "";
            }
            if (token.Kind == Kind.Open)
            {
                if (word == "{" && bodyOpen < 0 && TypeKeywords.Contains(keyword))
                {
                    bodyOpen = token.Start;
                    children = Segment(text, tokens, close, i + 1, close[i]);
                }
                end = tokens[close[i]].End;
                i = close[i] + 1;
                continue;
            }
            end = token.End;
            i++;
            if (word == ";")
            {
                Flush();
            }
        }
        Flush();
        return items;
    }

    /// <summary>Whether the <c>actor</c> at <paramref name="at"/> declares an actor: a name follows, then its body, an inheritance clause, generic parameters or a where clause.</summary>
    private static bool IsActorDeclaration(string text, List<Token> tokens, int at, int to) =>
        at + 2 < to && tokens[at + 1].Kind == Kind.Ident && text[tokens[at + 2].Start..tokens[at + 2].End] is "{" or ":" or "<" or "where";

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

    /// <summary>Words that name no declaration of a module (literals, <c>self</c>, operators spelled as words).</summary>
    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.Ordinal)
    {
        "true", "false", "nil", "self", "Self", "super", "in", "as", "is", "try", "await", "let", "var", "inout", "some", "any", "_",
    };

    /// <summary>
    /// The identifiers an expression's tokens name (backticks removed; a leading <c>arguments:</c> label left out), or null
    /// when the reader cannot resolve it to names: its tokens do not lex, or a string literal interpolates
    /// (<c>\(…)</c>) an expression the lexer does not split (sc-26129).
    /// </summary>
    public static IReadOnlySet<string>? Identifiers(string expression)
    {
        List<Token> tokens;
        try
        {
            tokens = Lex(expression);
        }
        catch (FormatException)
        {
            return null;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var word = expression[tokens[i].Start..tokens[i].End];
            if (i == 0 && word == "arguments" && tokens.Count > 1 && expression[tokens[1].Start..tokens[1].End] == ":")
            {
                i++;
                continue;
            }
            if (tokens[i].Kind == Kind.Literal && (word.Contains("\\(", StringComparison.Ordinal) || word.Contains("\\#", StringComparison.Ordinal)))
            {
                return null;
            }
            if (tokens[i].Kind == Kind.Ident && word.Trim('`') is var name && !ExpressionKeywords.Contains(name) && !name.StartsWith('$') && !name.StartsWith('#'))
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// The names a file declares at its top level and in its types' bodies, at any depth, each with what a change to it
    /// shows as (sc-26129): a function's, property's or case's whole text; a type's header (attributes, name, inheritance)
    /// with the texts of its members that are not functions — a new test method does not change its suite, a new case or
    /// stored property does. Layout does not count (<see cref="Collapse"/>). Throws <see cref="FormatException"/> as
    /// <see cref="Layout"/> does.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> Declarations(string text)
    {
        var found = new List<(string, string)>();
        string Of(SwiftItem item)
        {
            if (item.BodyOpen < 0)
            {
                return Collapse(text[item.Start..item.End]);
            }
            var members = item.Children.Where(c => c.Keyword != "func").Select(Of);
            return Collapse(text[item.Start..item.BodyOpen]) + " { " + string.Join(" ; ", members) + " }";
        }
        void Walk(IEnumerable<SwiftItem> items)
        {
            foreach (var item in items)
            {
                if (item.Name.Length > 0)
                {
                    found.Add((item.Name.Trim('`'), Of(item)));
                }
                Walk(item.Children);
            }
        }
        Walk(Layout(text).Items);
        return found;
    }

    /// <summary>A text with every run of whitespace made one space, trimmed.</summary>
    public static string Collapse(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The names whose declarations differ between the base and the head versions of a set of files (each a text, or null
    /// where the commit lacks it): a name declared in one and not the other, or with other texts (<see cref="Declarations"/>).
    /// Null when a version cannot be laid out (the caller fails closed).
    /// </summary>
    public static IReadOnlySet<string>? ChangedDeclarations(IEnumerable<string?> @base, IEnumerable<string?> head)
    {
        static Dictionary<string, List<string>>? Index(IEnumerable<string?> texts)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var text in texts.OfType<string>())
            {
                IReadOnlyList<(string Name, string Text)> declarations;
                try
                {
                    declarations = Declarations(text);
                }
                catch (FormatException)
                {
                    return null;
                }
                foreach (var (name, body) in declarations)
                {
                    (index.TryGetValue(name, out var list) ? list : index[name] = []).Add(body);
                }
            }
            return index;
        }
        if (Index(@base) is not { } before || Index(head) is not { } after)
        {
            return null;
        }
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in before.Keys.Union(after.Keys))
        {
            var (a, b) = (before.GetValueOrDefault(name) ?? [], after.GetValueOrDefault(name) ?? []);
            if (!a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                changed.Add(name);
            }
        }
        return changed;
    }

    /// <summary>
    /// Why an existing parameterized test's cases may change with the pull request though its <c>arguments:</c> do not
    /// (sc-26129), or null: an identifier of its arguments or of its parameter list names a declaration that
    /// <paramref name="changed"/> (<see cref="ChangedDeclarations"/> of the test target's changed files) holds; or, failing
    /// closed, the arguments cannot be resolved to names, or the changed files cannot be read, while the test target's
    /// files change at all.
    /// </summary>
    public static string? ArgumentsReachChange(SwiftFunc test, IReadOnlySet<string>? changed)
    {
        if (test.TestArguments is not { } arguments || changed is { Count: 0 })
        {
            return null;
        }
        if (changed is null)
        {
            return "the declarations of the test target's changed files cannot be read, so whether its arguments: change cannot be told";
        }
        if (Identifiers(arguments) is not { } names || Identifiers(test.Parameters) is not { } parameters)
        {
            return "its arguments: are not a form the gate can resolve to names, and the pull request changes declarations of its test target";
        }
        return names.Concat(parameters).Where(changed.Contains).Order(StringComparer.Ordinal).FirstOrDefault() is { } name
            ? $"its arguments: name {name}, which the pull request changes in its test target"
            : null;
    }

    /// <summary>A text with each line's leading indentation (and a trailing carriage return) removed.</summary>
    public static string WithoutIndentation(string text) => string.Join('\n', text.Split('\n').Select(l => l.TrimEnd('\r').TrimStart(' ', '\t')));

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
    internal static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "fileprivate", "internal", "open", "package", "static", "final", "override", "mutating", "nonmutating",
        "nonisolated", "isolated", "required", "convenience", "dynamic", "lazy", "weak", "unowned", "optional", "indirect", "distributed",
        "consuming", "borrowing", "sending", "async", "prefix", "postfix", "infix",
    };

    /// <summary>What may follow <c>class</c> when it is a modifier (<c>class func</c>, <c>class var</c>), not a type.</summary>
    private static readonly HashSet<string> AfterClassModifier = new(StringComparer.Ordinal) { "func", "var", "let", "subscript", "init", "deinit", "typealias" };

    /// <summary>The kinds an import may name (<c>import struct Foo.Bar</c>).</summary>
    private static readonly HashSet<string> ImportKinds = new(StringComparer.Ordinal) { "struct", "class", "enum", "protocol", "typealias", "func", "var", "let", "actor" };

    private sealed class Reader(string text, List<Token> tokens, Dictionary<int, int> close, List<SwiftFunc> funcs, List<string> imports, List<SwiftType> types)
    {
        private string Word(int i) => text[tokens[i].Start..tokens[i].End];

        private bool NewLineBefore(int i) => i > 0 && text.AsSpan(tokens[i - 1].End, tokens[i].Start - tokens[i - 1].End).Contains('\n');

        /// <summary>
        /// Reads the declarations of tokens <paramref name="from"/>..<paramref name="to"/> (exclusive), declared in
        /// <paramref name="names"/> (the type <paramref name="container"/>, null at the top level). Conditional-compilation
        /// blocks are counted (<c>#if</c> … <c>#endif</c>), so what is declared inside one is marked.
        /// </summary>
        public void Scope(int from, int to, IReadOnlyList<string> names, SwiftType? container)
        {
            var pending = new List<SwiftAttribute>();
            var modifiers = new List<string>();
            var conditional = 0;
            var outer = container?.Conditional ?? false;
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
                    modifiers.Add(word);
                    // private(set), nonisolated(unsafe): the modifier's own argument.
                    i = i + 1 < to && Word(i + 1) == "(" && tokens[i + 1].Start == tokens[i].End ? close[i + 1] + 1 : i + 1;
                    continue;
                }
                if (word == "class" && i + 1 < to && (AfterClassModifier.Contains(Word(i + 1)) || Modifiers.Contains(Word(i + 1))))
                {
                    modifiers.Add(word);
                    i++;
                    continue;
                }
                if (word is "#if")
                {
                    conditional++;
                }
                else if (word is "#endif")
                {
                    conditional = Math.Max(0, conditional - 1);
                }
                if (word == "func" && tokens[i].Kind == Kind.Ident)
                {
                    var recorded = funcs.Count;
                    i = Func(i, to, names, pending, modifiers, container, outer || conditional > 0);
                    pending = funcs.Count > recorded ? [] : Drop(pending, at: i);
                    modifiers = [];
                    continue;
                }
                if (tokens[i].Kind == Kind.Ident && TypeKeywords.Contains(word))
                {
                    i = Type(i, to, names, pending, outer || conditional > 0);
                    pending = Drop(pending, at: i);
                    modifiers = [];
                    continue;
                }
                if (word == "import" && names.Count == 0)
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
                    modifiers = [];
                    i = j + 1;
                    continue;
                }
                pending = Drop(pending, at: i);
                modifiers = [];
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

        /// <summary>A function: its name (an identifier, or an operator's characters) up to its parameter list, and its signature's tail. Its body is left to the scope, which skips it.</summary>
        private int Func(int at, int to, IReadOnlyList<string> names, List<SwiftAttribute> attributes, List<string> modifiers, SwiftType? container, bool conditional)
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
            var generic = false;
            while (j < to && tokens[j].Kind != Kind.Open)
            {
                generic |= Word(j) == "<";
                j++;
            }
            if (j >= to || name.Length == 0 || Word(j) != "(")
            {
                return j;
            }
            // The tail: effects, a result type, a where clause — up to the body, a `;`, or the next declaration on a line of its own.
            var k = close[j] + 1;
            while (k < to && Word(k) is not ("{" or ";") && !(NewLineBefore(k) && (Word(k) is "@" or "}" || Word(k).StartsWith('#') || DeclKeywords.Contains(Word(k)) || Modifiers.Contains(Word(k)))))
            {
                k = tokens[k].Kind == Kind.Open ? close[k] + 1 : k + 1;
            }
            var tail = k > close[j] + 1 ? text[tokens[close[j] + 1].Start..tokens[k - 1].End] : "";
            var last = k < to && Word(k) == "{" ? close[k] : k - 1;
            funcs.Add(new SwiftFunc(name, names, text[tokens[j].End..tokens[close[j]].Start], attributes, tokens[at].Start)
            {
                Source = text[tokens[at].Start..tokens[last].End],
                Modifiers = [.. modifiers],
                Generic = generic,
                Tail = tail,
                Conditional = conditional,
                Container = container,
            });
            return close[j] + 1;
        }

        /// <summary>A type or extension: its (dotted) name, its generic clause and inheritance clause, then its body read as a scope of its own.</summary>
        private int Type(int at, int to, IReadOnlyList<string> outerNames, List<SwiftAttribute> attributes, bool conditional)
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
            var generic = j < to && Word(j) == "<";
            var inherits = new List<string>();
            var entry = new System.Text.StringBuilder();
            var (inClause, seenClause) = (false, false);
            var angle = 0;
            while (j < to && Word(j) != "{")
            {
                var word = Word(j);
                var before = angle;
                angle = Math.Max(0, angle + (word == "<" ? 1 : word == ">" ? -1 : 0));
                if (word == "where" && before == 0)
                {
                    inClause = false;
                    seenClause = true;
                }
                else if (word == ":" && !inClause && !seenClause && before == 0)
                {
                    inClause = seenClause = true;
                }
                else if (inClause && word == "," && before == 0)
                {
                    inherits.Add(entry.ToString());
                    entry.Clear();
                }
                else if (inClause)
                {
                    entry.Append(tokens[j].Kind == Kind.Open ? text[tokens[j].Start..tokens[close[j]].End] : word);
                }
                j = tokens[j].Kind == Kind.Open ? close[j] + 1 : j + 1;
            }
            if (entry.Length > 0)
            {
                inherits.Add(entry.ToString());
            }
            if (j >= to)
            {
                return j;
            }
            var type = new SwiftType(Word(at), [.. outerNames, .. names], inherits, generic, [.. attributes], conditional);
            types.Add(type);
            Scope(j + 1, close[j], type.Names, type);
            return close[j] + 1;
        }
    }
}
