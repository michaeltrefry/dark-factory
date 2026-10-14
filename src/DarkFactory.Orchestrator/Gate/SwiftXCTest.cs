namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// One XCTest test as XCTest runs it (sc-26129): the class it runs in (an <c>XCTestCase</c> subclass), the method, and where
/// the method is declared — its file and its class, which differs from <see cref="Class"/> for a method the class inherits.
/// </summary>
public sealed record XCTestMethod(string Class, SwiftFunc Method, string Path)
{
    /// <summary>The method as Swift Testing and xcodebuild write a function: <c>testBlank()</c>.</summary>
    public string Signature => Method.Signature;

    /// <summary>The test's place in its module: <c>Class/testBlank()</c>.</summary>
    public string Key => $"{Class}/{Signature}";
}

/// <summary>A test-looking method the gate cannot run apart (<see cref="SwiftXCTest.Discover"/>): its place (<c>Class/testX()</c>), its file and why.</summary>
public sealed record XCTestProblem(string Key, string Path, string Why);

/// <summary>What <see cref="SwiftXCTest.Discover"/> found in a module: the tests, the methods it cannot judge, and every test-looking method's place.</summary>
public sealed record XCTestModule(IReadOnlyList<XCTestMethod> Tests, IReadOnlyList<XCTestProblem> Problems, IReadOnlySet<string> Candidates);

/// <summary>
/// XCTest's tests of one module (a SwiftPM test target or an Xcode unit-test target), found by syntax over every file of
/// the module (Phase 5, sc-26129; nothing compiled, as for Swift Testing). XCTest runs, in every class that inherits
/// <c>XCTestCase</c> — directly or through classes of the module — each instance method whose name starts with
/// <c>test</c> and that takes no parameter, its own and those it inherits; so a method a class inherits is a test of that
/// class too (<c>Sub/testInBase()</c>), and a new method of a base class is a new test of every subclass.
/// <para>
/// Fail closed: a test-looking method (<see cref="IsCandidate"/>) the gate cannot tell is a test, or cannot run apart, is a
/// <see cref="XCTestProblem"/> — never silently left out — when its class inherits a class the module does not declare
/// (another module's <c>XCTestCase</c> subclass, say: the gate cannot read it); its class is nested in another type,
/// generic, declared twice, or declared under <c>#if</c>; it is in an extension of a test class (or of a type the module does
/// not declare, in a file that imports XCTest); it is under <c>#if</c>, <c>private</c>/<c>fileprivate</c>, generic, has an
/// <c>@objc</c>/<c>@nonobjc</c> attribute (on it or its class), or returns anything or has effects but <c>async</c> and
/// <c>throws</c>. A <c>static</c> or <c>class</c> method is not a test (XCTest runs instance methods). A class that inherits
/// nothing, or first names a type the module declares as no class (a protocol), is no test class.
/// </para>
/// </summary>
public static class SwiftXCTest
{
    private static readonly HashSet<string> XCTestCase = new(StringComparer.Ordinal) { "XCTestCase", "XCTest.XCTestCase" };

    /// <summary>Base classes that are known not to be XCTestCase.</summary>
    private static readonly HashSet<string> NotTestCase = new(StringComparer.Ordinal) { "NSObject", "Foundation.NSObject", "ObjectiveC.NSObject" };

    /// <summary>
    /// A method that looks like an XCTest test wherever it is: named <c>test…</c>, no parameter, no <c>@Test</c>, in a class
    /// or an extension. Whether XCTest runs it is <see cref="Discover"/>'s question.
    /// </summary>
    public static bool IsCandidate(SwiftFunc f) =>
        !f.IsTest && f.Name.Trim('`').StartsWith("test", StringComparison.Ordinal) && f.Parameters.Trim().Length == 0 && f.Container is { Keyword: "class" or "extension" };

    private enum Kind { Test, NotTest, Unknown }

    /// <summary>The tests of the module made of <paramref name="files"/> (each file's path and parsed source).</summary>
    public static XCTestModule Discover(IReadOnlyList<(string Path, SwiftFile File)> files)
    {
        var classes = files.SelectMany(f => f.File.Types.Where(t => t.Keyword == "class").Select(t => (f.Path, Type: t))).ToList();
        var topClasses = classes.Where(c => c.Type.Names.Count == 1).ToLookup(c => c.Type.Name, StringComparer.Ordinal);
        var otherTypes = files.SelectMany(f => f.File.Types).Where(t => t.Keyword is "struct" or "enum" or "protocol" or "actor" && t.Names.Count == 1)
            .Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var memo = new Dictionary<string, (Kind Kind, string? Why)>(StringComparer.Ordinal);

        (Kind Kind, string? Why) KindOfBase(string name, HashSet<string> seen)
        {
            if (XCTestCase.Contains(name))
            {
                return (Kind.Test, null);
            }
            if (NotTestCase.Contains(name) || otherTypes.Contains(name) && !topClasses.Contains(name))
            {
                return (Kind.NotTest, null);
            }
            if (!topClasses.Contains(name))
            {
                return (Kind.Unknown, $"it inherits {name}, which this module does not declare, so whether it is an XCTestCase subclass cannot be told");
            }
            return KindOfClass(name, seen);
        }

        (Kind Kind, string? Why) KindOfClass(string name, HashSet<string> seen)
        {
            if (memo.TryGetValue(name, out var known))
            {
                return known;
            }
            if (!seen.Add(name))
            {
                return (Kind.Unknown, $"its class hierarchy through {name} is circular");
            }
            var decls = topClasses[name].ToList();
            (Kind, string?) result = decls.Count != 1 ? (Kind.Unknown, $"the class {name} is declared {decls.Count} times in the module (conditional compilation?)")
                : decls[0].Type.Inherits.Count == 0 ? (Kind.NotTest, null)
                : KindOfBase(decls[0].Type.Inherits[0], seen);
            memo[name] = result;
            return result;
        }

        (Kind Kind, string? Why) KindOf(SwiftType type) =>
            type.Names.Count == 1 ? KindOfClass(type.Name, new HashSet<string>(StringComparer.Ordinal))
            : type.Inherits.Count == 0 ? (Kind.NotTest, null)
            : KindOfBase(type.Inherits[0], new HashSet<string>(StringComparer.Ordinal));

        var problems = new List<XCTestProblem>();
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var own = new Dictionary<string, List<XCTestMethod>>(StringComparer.Ordinal);
        foreach (var (path, file) in files)
        {
            foreach (var f in file.Funcs.Where(IsCandidate))
            {
                var container = f.Container!;
                var key = $"{string.Join("/", container.Names)}/{f.Signature}";
                candidates.Add(key);
                if (container.Keyword == "extension")
                {
                    // An extension of a test class, or of XCTestCase or a type the module does not declare in a file that
                    // imports XCTest, may add a test the gate does not read as one.
                    var extended = string.Join(".", container.Names);
                    var maybeTest = topClasses.Contains(extended) ? KindOfClass(extended, new HashSet<string>(StringComparer.Ordinal)).Kind != Kind.NotTest
                        : otherTypes.Contains(extended) ? false
                        : XCTestCase.Contains(extended) || file.Imports.Contains("XCTest");
                    if (maybeTest && !IsStatic(f))
                    {
                        problems.Add(new XCTestProblem(key, path, $"it is declared in an extension of {string.Join(".", container.Names)}, which the gate does not read as a test class's own method"));
                    }
                    continue;
                }
                var (classKind, classWhy) = KindOf(container);
                if (classKind == Kind.NotTest || IsStatic(f))
                {
                    continue;
                }
                if (classKind == Kind.Unknown)
                {
                    problems.Add(new XCTestProblem(key, path, $"its class {container.Name}: {classWhy}"));
                    continue;
                }
                if (Why(f, container) is { } why)
                {
                    problems.Add(new XCTestProblem(key, path, why));
                    continue;
                }
                (own.TryGetValue(container.Name, out var list) ? list : own[container.Name] = []).Add(new XCTestMethod(container.Name, f, path));
            }
        }

        // Every test class's tests: its own, then those it inherits (a method it overrides is its own).
        var tests = new List<XCTestMethod>();
        foreach (var (path, type) in classes.Where(c => c.Type.Names.Count == 1 && topClasses[c.Type.Name].Count() == 1))
        {
            if (KindOfClass(type.Name, new HashSet<string>(StringComparer.Ordinal)).Kind != Kind.Test)
            {
                continue;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var mine = new List<XCTestMethod>();
            for (var at = type; at is not null;)
            {
                if (!ReferenceEquals(at, type) && ClassWhy(at) is { } inherited)
                {
                    // Its base class's methods are not read as tests (they are problems of their own): neither are its own copies of them.
                    problems.Add(new XCTestProblem($"{type.Name}/(inherited from {at.Name})", path, $"the class {type.Name} inherits {at.Name}, whose tests the gate cannot run: {inherited}"));
                }
                foreach (var method in own.GetValueOrDefault(at.Name) ?? [])
                {
                    if (names.Add(method.Signature))
                    {
                        mine.Add(method with { Class = type.Name });
                    }
                }
                at = at.Inherits.Count > 0 && topClasses[at.Inherits[0]].ToList() is [var super] ? super.Type : null;
            }
            if (ClassWhy(type) is { } why)
            {
                // The class's own methods are problems already; what it inherits it cannot be run with either.
                problems.AddRange(mine.Where(m => !ReferenceEquals(m.Method.Container, type)).Select(m => new XCTestProblem(m.Key, path, why)));
                continue;
            }
            tests.AddRange(mine);
        }
        return new XCTestModule(tests, problems, candidates);
    }

    private static bool IsStatic(SwiftFunc f) => f.Modifiers.Any(m => m is "static" or "class");

    private static bool ObjC(IEnumerable<SwiftAttribute> attributes) => attributes.Any(a => a.Name is "objc" or "nonobjc" or "objcMembers");

    /// <summary>Why a test class cannot be run apart by the gate, or null.</summary>
    private static string? ClassWhy(SwiftType type) =>
        type.Names.Count > 1 ? $"its class {string.Join(".", type.Names)} is nested in another type, which the gate cannot name to XCTest"
        : type.Generic ? $"its class {type.Name} is generic, which XCTest does not run"
        : type.Conditional ? $"its class {type.Name} is declared under #if (conditional compilation), so whether it is built cannot be told"
        : ObjC(type.Attributes) ? $"its class {type.Name} has an Objective-C name attribute, so XCTest may name it otherwise"
        : null;

    /// <summary>Why a method of a test class is not a test the gate can run apart, or null.</summary>
    private static string? Why(SwiftFunc f, SwiftType container)
    {
        if (ClassWhy(container) is { } why)
        {
            return why;
        }
        var tail = f.Tail.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return f.Name.Contains('`') ? "it has a backticked name, which the gate cannot name to XCTest"
            : f.Conditional ?"it is declared under #if (conditional compilation), so whether it is built cannot be told"
            : f.Modifiers.Any(m => m is "private" or "fileprivate") ? "it is private, which XCTest may not find"
            : f.Generic ? "it is generic, which XCTest does not run"
            : ObjC(f.Attributes) ? "it has an Objective-C name attribute, so XCTest may name it otherwise"
            : !(tail is [] or ["async"] or ["throws"] or ["async", "throws"]) ? $"its signature ends '{f.Tail.Trim()}': XCTest runs a method that returns nothing (async and throws allowed)"
            : null;
    }
}
