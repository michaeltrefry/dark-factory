using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A value of an old-style (OpenStep) property list, the format of an Xcode project's <c>project.pbxproj</c>.</summary>
public abstract record PbxValue;

/// <summary>A string (quoted or not).</summary>
public sealed record PbxString(string Value) : PbxValue;

/// <summary>An array, <c>( a, b, )</c>.</summary>
public sealed record PbxArray(IReadOnlyList<PbxValue> Items) : PbxValue
{
    /// <summary>Its items as strings, or null when one is not a string.</summary>
    public IReadOnlyList<string>? Strings => Items.All(i => i is PbxString) ? Items.Cast<PbxString>().Select(s => s.Value).ToList() : null;
}

/// <summary>A dictionary, <c>{ key = value; }</c> (no key twice).</summary>
public sealed record PbxDict(IReadOnlyDictionary<string, PbxValue> Entries) : PbxValue
{
    public string? Str(string key) => Entries.GetValueOrDefault(key) as PbxString is { } s ? s.Value : null;

    public PbxDict? Dict(string key) => Entries.GetValueOrDefault(key) as PbxDict;

    /// <summary>The array of strings under <paramref name="key"/>; empty when absent; null when it is something else.</summary>
    public IReadOnlyList<string>? Strings(string key) => Entries.GetValueOrDefault(key) switch
    {
        null => [],
        PbxArray a => a.Strings,
        _ => null,
    };
}

/// <summary>
/// A reader of old-style (OpenStep) property lists, as Xcode writes <c>project.pbxproj</c> (Phase 5, sc-26131): dictionaries,
/// arrays, quoted strings with their escapes, unquoted strings, and <c>/* */</c> and <c>//</c> comments. Anything else — a
/// data literal, an unterminated string, a key twice in one dictionary, nesting deeper than <see cref="MaxDepth"/>, a file
/// over <see cref="MaxChars"/> — throws <see cref="FormatException"/>: the gate then cannot read the project, never reads
/// it as having no targets.
/// </summary>
public static class Pbx
{
    public const int MaxDepth = 64;

    public const int MaxChars = 16 * 1024 * 1024;

    public static PbxDict Parse(string text)
    {
        if (text.Length > MaxChars)
        {
            throw new FormatException($"it is larger than {MaxChars} characters");
        }
        var at = 0;
        var value = Value(text, ref at, 0);
        Skip(text, ref at);
        if (at != text.Length)
        {
            throw new FormatException($"it has text after its root value (offset {at})");
        }
        return value as PbxDict ?? throw new FormatException("its root value is not a dictionary");
    }

    private static bool IsBare(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '+' or '/' or ':' or '.' or '-';

    private static void Skip(string text, ref int at)
    {
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            else if (text.AsSpan(at).StartsWith("//"))
            {
                at = text.IndexOf('\n', at) is var end and >= 0 ? end + 1 : text.Length;
            }
            else if (text.AsSpan(at).StartsWith("/*"))
            {
                var end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end >= 0 ? end + 2 : throw new FormatException($"a comment at offset {at} is not closed");
            }
            else
            {
                return;
            }
        }
    }

    private static PbxValue Value(string text, ref int at, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"it nests deeper than {MaxDepth} levels");
        }
        Skip(text, ref at);
        if (at >= text.Length)
        {
            throw new FormatException("it ends where a value is expected");
        }
        switch (text[at])
        {
            case '{':
            {
                at++;
                var entries = new Dictionary<string, PbxValue>(StringComparer.Ordinal);
                while (true)
                {
                    Skip(text, ref at);
                    if (at < text.Length && text[at] == '}')
                    {
                        at++;
                        return new PbxDict(entries);
                    }
                    var key = Value(text, ref at, depth + 1) as PbxString ?? throw new FormatException($"a dictionary key before offset {at} is not a string");
                    Expect(text, ref at, '=');
                    var item = Value(text, ref at, depth + 1);
                    Expect(text, ref at, ';');
                    if (!entries.TryAdd(key.Value, item))
                    {
                        throw new FormatException($"the key {key.Value} is in one dictionary twice");
                    }
                }
            }
            case '(':
            {
                at++;
                var items = new List<PbxValue>();
                while (true)
                {
                    Skip(text, ref at);
                    if (at < text.Length && text[at] == ')')
                    {
                        at++;
                        return new PbxArray(items);
                    }
                    items.Add(Value(text, ref at, depth + 1));
                    Skip(text, ref at);
                    if (at < text.Length && text[at] == ',')
                    {
                        at++;
                    }
                    else if (at >= text.Length || text[at] != ')')
                    {
                        throw new FormatException($"an array item at offset {at} is followed by neither , nor )");
                    }
                }
            }
            case '"':
                return new PbxString(Quoted(text, ref at));
            default:
            {
                var start = at;
                while (at < text.Length && IsBare(text[at]))
                {
                    at++;
                }
                return at > start ? new PbxString(text[start..at]) : throw new FormatException($"an unexpected character '{text[at]}' at offset {at}");
            }
        }
    }

    private static void Expect(string text, ref int at, char c)
    {
        Skip(text, ref at);
        if (at >= text.Length || text[at] != c)
        {
            throw new FormatException($"'{c}' is expected at offset {at}");
        }
        at++;
    }

    private static string Quoted(string text, ref int at)
    {
        var start = at++;
        var value = new StringBuilder();
        while (true)
        {
            if (at >= text.Length)
            {
                throw new FormatException($"a string at offset {start} is not closed");
            }
            var c = text[at++];
            if (c == '"')
            {
                return value.ToString();
            }
            if (c != '\\')
            {
                value.Append(c);
                continue;
            }
            if (at >= text.Length)
            {
                throw new FormatException($"a string at offset {start} is not closed");
            }
            var e = text[at++];
            switch (e)
            {
                case '"' or '\\' or '\'':
                    value.Append(e);
                    break;
                case 'n': value.Append('\n'); break;
                case 't': value.Append('\t'); break;
                case 'r': value.Append('\r'); break;
                case 'a': value.Append('\a'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'v': value.Append('\v'); break;
                case 'U':
                    if (at + 4 > text.Length || !int.TryParse(text.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException($"a \\U escape at offset {at} is not four hex digits");
                    }
                    value.Append((char)code);
                    at += 4;
                    break;
                case >= '0' and <= '7':
                {
                    var octal = e - '0';
                    for (var n = 0; n < 2 && at < text.Length && text[at] is >= '0' and <= '7'; n++)
                    {
                        octal = octal * 8 + (text[at++] - '0');
                    }
                    value.Append((char)octal);
                    break;
                }
                default:
                    throw new FormatException($"an unknown escape \\{e} at offset {at - 1}");
            }
        }
    }
}

/// <summary>Whether a file is a member of a target's sources: yes, no, or not known (and why: the gate then fails closed).</summary>
public sealed record XcodeMembership(bool Member, string? Unknown)
{
    public static readonly XcodeMembership Yes = new(true, null);
    public static readonly XcodeMembership No = new(false, null);

    public static XcodeMembership NotKnown(string why) => new(false, why);
}

/// <summary>A native target of an Xcode project: its object id, name and product type.</summary>
public sealed record XcodeTarget(string Id, string Name, string ProductType, PbxDict Object)
{
    public const string UnitTestBundle = "com.apple.product-type.bundle.unit-test";
    public const string UITestBundle = "com.apple.product-type.bundle.ui-testing";

    public bool IsUnitTests => ProductType == UnitTestBundle;
}

/// <summary>
/// An Xcode project read from its <c>project.pbxproj</c> (Phase 5, sc-26131): its native targets, which source files each
/// compiles, and the SDK a target builds for. Membership is read both ways Xcode records it: classic file references (a
/// <c>PBXBuildFile</c> in the target's <c>PBXSourcesBuildPhase</c>, its <c>PBXFileReference</c>'s path resolved through
/// its groups) and Xcode 16+ synchronized folders (<c>PBXFileSystemSynchronizedRootGroup</c>: every file under the folder,
/// less a <c>PBXFileSystemSynchronizedBuildFileExceptionSet</c>'s <c>membershipExceptions</c> for that target, plus another
/// folder's exceptions that name the target). Whatever is not read — an absolute or variable path, a source tree other than
/// the group, the project root and the build products, a folder's explicit file types or folders, an exception set of
/// another kind, a project directory set elsewhere — is <see cref="XcodeMembership.NotKnown"/>, never "not a member".
/// </summary>
public sealed class XcodeProject
{
    /// <summary>The project's repository path (<c>App/Words.xcodeproj</c>).</summary>
    public string Path { get; }

    /// <summary>The directory holding the project (the root of its source tree: <c>App</c>, or empty at the repository root).</summary>
    public string Directory { get; }

    public IReadOnlyList<XcodeTarget> Targets { get; }

    private readonly IReadOnlyDictionary<string, PbxDict> _objects;
    private readonly PbxDict _project;
    private readonly string? _mainGroup;
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    private XcodeProject(string path, IReadOnlyDictionary<string, PbxDict> objects, PbxDict project)
    {
        Path = path;
        var slash = path.LastIndexOf('/');
        Directory = slash < 0 ? "" : path[..slash];
        _objects = objects;
        _project = project;
        _mainGroup = project.Str("mainGroup");
        foreach (var (id, o) in objects)
        {
            foreach (var child in o.Strings("children") ?? [])
            {
                if (!_parents.TryAdd(child, id))
                {
                    _ambiguous.Add(child);
                }
            }
        }
        Targets = (project.Strings("targets") ?? throw new FormatException("its project's targets are not a list of ids"))
            .Select(id => objects.TryGetValue(id, out var t) ? (id, t) : throw new FormatException($"its target {id} is not one of its objects"))
            .Where(t => t.t.Str("isa") == "PBXNativeTarget")
            .Select(t => new XcodeTarget(t.id, t.t.Str("name") ?? throw new FormatException($"its target {t.id} has no name"), t.t.Str("productType") ?? "", t.t))
            .ToList();
    }

    /// <summary>
    /// Reads <paramref name="pbxproj"/>, the text of <paramref name="projectPath"/>'s <c>project.pbxproj</c>. Throws
    /// <see cref="FormatException"/> when it is not a project the gate can read.
    /// </summary>
    public static XcodeProject Parse(string projectPath, string pbxproj)
    {
        var root = Pbx.Parse(pbxproj);
        var objects = root.Dict("objects") ?? throw new FormatException("it has no objects dictionary");
        var map = new Dictionary<string, PbxDict>(StringComparer.Ordinal);
        foreach (var (id, value) in objects.Entries)
        {
            map[id] = value as PbxDict ?? throw new FormatException($"its object {id} is not a dictionary");
        }
        var rootId = root.Str("rootObject") ?? throw new FormatException("it has no rootObject");
        var project = map.GetValueOrDefault(rootId) is { } p && p.Str("isa") == "PBXProject" ? p : throw new FormatException("its rootObject is not a PBXProject");
        if (project.Str("projectDirPath") is { Length: > 0 } dir)
        {
            throw new FormatException($"its project directory is set elsewhere (projectDirPath {dir})");
        }
        return new XcodeProject(projectPath, map, project);
    }

    private PbxDict? Object(string id) => _objects.GetValueOrDefault(id);

    /// <summary>
    /// Where an object (a file reference or a group) is in the repository: its path ("" for the root); null with no problem
    /// when it is not in the repository at all (a build product, an SDK file); else why it cannot be told.
    /// </summary>
    internal (string? Path, string? Problem) Locate(string id, int depth = 0)
    {
        if (depth > Pbx.MaxDepth)
        {
            return (null, "its groups nest too deep (or in a cycle)");
        }
        if (Object(id) is not { } o)
        {
            return (null, $"it names an object {id} that is not in the project");
        }
        var path = o.Str("path");
        if (path is not null && (path.Contains("$(", StringComparison.Ordinal) || path.Contains("${", StringComparison.Ordinal)))
        {
            return (null, $"the path {path} holds a build variable");
        }
        string baseDirectory;
        switch (o.Str("sourceTree") ?? "<group>")
        {
            case "<group>":
                if (id == _mainGroup)
                {
                    baseDirectory = Directory;
                }
                else if (_ambiguous.Contains(id))
                {
                    return (null, $"the object {id} is in two groups");
                }
                else if (_parents.TryGetValue(id, out var parent))
                {
                    var (located, problem) = Locate(parent, depth + 1);
                    if (located is null)
                    {
                        return (null, problem);
                    }
                    baseDirectory = located;
                }
                else
                {
                    return (null, $"the object {id} is in no group");
                }
                break;
            case "SOURCE_ROOT":
                baseDirectory = Directory;
                break;
            case "BUILT_PRODUCTS_DIR" or "SDKROOT" or "DEVELOPER_DIR":
                return (null, null);
            case var tree:
                return (null, $"the object {id} has the source tree {tree}, which the gate does not read");
        }
        var combined = path is null ? baseDirectory : baseDirectory.Length == 0 ? path : $"{baseDirectory}/{path}";
        if (combined.Length == 0 || combined.Split('/').All(s => s is "" or "."))
        {
            return ("", null);
        }
        return RepoPath.Normalize(combined) is { } normalized ? (normalized, null) : (null, $"the path {combined} leaves the repository");
    }

    private static readonly HashSet<string> RootGroupKeys = new(StringComparer.Ordinal) { "isa", "path", "name", "sourceTree", "exceptions" };

    private static readonly HashSet<string> ExceptionSetKeys = new(StringComparer.Ordinal)
    {
        "isa", "target", "membershipExceptions", "additionalCompilerFlagsByRelativePath", "attributesByRelativePath", "publicHeaders", "privateHeaders",
    };

    private static bool Under(string directory, string path, out string relative)
    {
        relative = directory.Length == 0 ? path : path.StartsWith(directory + "/", StringComparison.Ordinal) ? path[(directory.Length + 1)..] : "";
        return relative.Length > 0;
    }

    private static bool Listed(IReadOnlyList<string> entries, string relative) =>
        entries.Any(e => e == relative || relative.StartsWith(e.TrimEnd('/') + "/", StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="path"/> (a repository path of a <c>.swift</c> file) is compiled by <paramref name="target"/>'s
    /// sources; with <paramref name="classicOnly"/>, through a classic file reference (synchronized folders not read).
    /// </summary>
    public XcodeMembership Membership(XcodeTarget target, string path, bool classicOnly = false)
    {
        var member = false;
        // Classic file references: a build file of the target's Sources phase.
        foreach (var phaseId in target.Object.Strings("buildPhases") ?? [])
        {
            if (Object(phaseId) is not { } phase)
            {
                return XcodeMembership.NotKnown($"{target.Name}'s build phase {phaseId} is not in the project");
            }
            if (phase.Str("isa") != "PBXSourcesBuildPhase")
            {
                continue;
            }
            foreach (var fileId in phase.Strings("files") ?? throw new FormatException($"{target.Name}'s Sources phase lists no files"))
            {
                if (Object(fileId) is not { } buildFile)
                {
                    return XcodeMembership.NotKnown($"{target.Name}'s Sources phase names a build file {fileId} that is not in the project");
                }
                if (buildFile.Str("fileRef") is not { } fileRef)
                {
                    continue; // a package product, not a file of the repository
                }
                var (located, problem) = Locate(fileRef);
                if (problem is not null)
                {
                    return XcodeMembership.NotKnown($"a source file of {target.Name} cannot be located: {problem}");
                }
                member |= located == path;
            }
        }

        if (classicOnly)
        {
            return member ? XcodeMembership.Yes : XcodeMembership.No;
        }

        // Synchronized folders.
        var own = (target.Object.Strings("fileSystemSynchronizedGroups") ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var (groupId, group) in _objects.Where(o => o.Value.Str("isa") == "PBXFileSystemSynchronizedRootGroup"))
        {
            var mine = own.Contains(groupId);
            var exceptions = group.Strings("exceptions") ?? [];
            if (!mine && exceptions.Count == 0)
            {
                continue;
            }
            var (directory, problem) = Locate(groupId);
            if (problem is not null)
            {
                if (mine)
                {
                    return XcodeMembership.NotKnown($"{target.Name}'s synchronized folder cannot be located: {problem}");
                }
                continue;
            }
            if (directory is null || !Under(directory, path, out var relative))
            {
                continue;
            }
            if (group.Entries.Keys.FirstOrDefault(k => !RootGroupKeys.Contains(k)) is { } unread)
            {
                return XcodeMembership.NotKnown($"the synchronized folder {directory} sets {unread}, which the gate does not read");
            }
            var excepted = false;
            foreach (var exceptionId in exceptions)
            {
                if (Object(exceptionId) is not { } set)
                {
                    return XcodeMembership.NotKnown($"the synchronized folder {directory} names an exception set {exceptionId} that is not in the project");
                }
                if (set.Str("isa") != "PBXFileSystemSynchronizedBuildFileExceptionSet")
                {
                    return XcodeMembership.NotKnown($"the synchronized folder {directory} has an exception set of the kind {set.Str("isa")}, which the gate does not read");
                }
                if (set.Entries.Keys.FirstOrDefault(k => !ExceptionSetKeys.Contains(k)) is { } key)
                {
                    return XcodeMembership.NotKnown($"an exception set of the synchronized folder {directory} sets {key}, which the gate does not read");
                }
                if (set.Str("target") != target.Id)
                {
                    continue;
                }
                var listed = set.Strings("membershipExceptions") ?? throw new FormatException("membershipExceptions is not a list");
                excepted |= Listed(listed, relative);
            }
            // In the target's own folder an exception takes the file out; in another folder it puts the file in.
            member |= mine != excepted;
        }
        return member ? XcodeMembership.Yes : XcodeMembership.No;
    }

    /// <summary>The ids of the target's Sources phases.</summary>
    private IReadOnlyList<string> SourcesPhases(XcodeTarget target) =>
        (target.Object.Strings("buildPhases") ?? []).Where(id => Object(id)?.Str("isa") == "PBXSourcesBuildPhase").ToList();

    /// <summary>
    /// The base project's text with <paramref name="target"/>'s classic Sources changed for the base run (sc-26131): every
    /// file of <paramref name="add"/> compiled (a new <c>PBXFileReference</c> with <c>sourceTree = SOURCE_ROOT</c>, in the
    /// main group, and a <c>PBXBuildFile</c> of it in the target's one Sources phase) and every build file of
    /// <paramref name="remove"/> taken out of that phase — what the PR's own project file does for its new and removed test
    /// files, without anything else the PR changed in it (its production targets stay the base's). The edit is textual, at
    /// the places Xcode writes these lists, and then checked: the result must read as a project in which every other object
    /// is exactly the base's, the two lists changed by exactly those ids, and the target compiling exactly the files it
    /// should, else null and why — the base run is then not made (fail closed).
    /// </summary>
    public (string? Text, string? Problem) WithClassicSources(string text, XcodeTarget target, IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        if (SourcesPhases(target) is not [var phaseId])
        {
            return (null, $"{target.Name} does not have exactly one Sources build phase");
        }
        if (_mainGroup is null)
        {
            return (null, "the project has no main group");
        }
        var phase = Object(phaseId)!;
        var removed = new List<string>();
        foreach (var path in remove)
        {
            var ids = (phase.Strings("files") ?? []).Where(id => Object(id)?.Str("fileRef") is { } fileRef && Locate(fileRef).Path == path).ToList();
            if (ids.Count == 0)
            {
                return (null, $"no build file of {target.Name}'s Sources names {path}");
            }
            removed.AddRange(ids);
        }
        var newObjects = new System.Text.StringBuilder();
        var (fileRefs, buildFiles) = (new List<string>(), new List<string>());
        foreach (var path in add)
        {
            string relative;
            if (Directory.Length == 0)
            {
                relative = path;
            }
            else if (path.StartsWith(Directory + "/", StringComparison.Ordinal))
            {
                relative = path[(Directory.Length + 1)..];
            }
            else
            {
                return (null, $"{path} is outside the project's directory {Directory}");
            }
            var (fileRef, buildFile) = (GraftId("file", target.Id, path), GraftId("build", target.Id, path));
            if (Object(fileRef) is not null || Object(buildFile) is not null)
            {
                return (null, $"the ids made for {path} are already the project's");
            }
            fileRefs.Add(fileRef);
            buildFiles.Add(buildFile);
            newObjects.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"\t\t{fileRef} = {{isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = {Quote(relative)}; sourceTree = SOURCE_ROOT; }};\n")
                .Append(System.Globalization.CultureInfo.InvariantCulture, $"\t\t{buildFile} = {{isa = PBXBuildFile; fileRef = {fileRef}; }};\n");
        }

        var edited = text;
        if (!TryEditList(ref edited, phaseId, "files", buildFiles, removed) || !TryEditList(ref edited, _mainGroup, "children", fileRefs, []))
        {
            return (null, "the lists of the project file could not be found where Xcode writes them");
        }
        var objects = System.Text.RegularExpressions.Regex.Match(edited, @"\bobjects\s*=\s*\{");
        if (!objects.Success)
        {
            return (null, "the project file's objects could not be found");
        }
        edited = edited.Insert(objects.Index + objects.Length, "\n" + newObjects);

        // The check: everything else exactly the base's.
        XcodeProject result;
        try
        {
            result = Parse(Path, edited);
        }
        catch (FormatException e)
        {
            return (null, $"the edited project file does not read ({e.Message})");
        }
        var added = fileRefs.Concat(buildFiles).ToHashSet(StringComparer.Ordinal);
        if (result._objects.Count != _objects.Count + added.Count || !added.All(result._objects.ContainsKey))
        {
            return (null, "the edited project file does not hold exactly the base's objects and the new ones");
        }
        foreach (var (id, original) in _objects)
        {
            var now = result._objects[id];
            var expected = id == phaseId ? WithList(original, "files", buildFiles, removed)
                : id == _mainGroup ? WithList(original, "children", fileRefs, [])
                : original;
            if (!Same(expected, now))
            {
                return (null, $"the edited project file changed the object {id}, which it should not");
            }
        }
        var edTarget = result.Targets.Single(t => t.Id == target.Id);
        foreach (var path in add)
        {
            if (!result.Membership(edTarget, path).Member)
            {
                return (null, $"the edited project does not compile {path} into {target.Name}");
            }
        }
        foreach (var path in remove)
        {
            if (result.Membership(edTarget, path, classicOnly: true).Member)
            {
                return (null, $"the edited project still compiles {path} into {target.Name}");
            }
        }
        return (edited, null);
    }

    private static string GraftId(string kind, string target, string path) =>
        "DF" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{target}\n{path}")))[..22];

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Adds <paramref name="add"/> to and removes <paramref name="remove"/> from the list <paramref name="key"/> of the object <paramref name="id"/>, in the text.</summary>
    private static bool TryEditList(ref string text, string id, string key, IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        if (add.Count == 0 && remove.Count == 0)
        {
            return true;
        }
        var definitions = System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^\s*" + System.Text.RegularExpressions.Regex.Escape(id) + @"\b(?:\s*/\*[^*\n]*\*/)?\s*=\s*\{");
        if (definitions.Count != 1)
        {
            return false;
        }
        var start = definitions[0].Index + definitions[0].Length;
        var list = System.Text.RegularExpressions.Regex.Match(text[start..], @"\b" + key + @"\s*=\s*\(");
        if (!list.Success)
        {
            return false;
        }
        var open = start + list.Index + list.Length;
        var close = open;
        while (close < text.Length && text[close] != ')')
        {
            // A comment or a quoted string may hold a ')'.
            if (text.AsSpan(close).StartsWith("/*"))
            {
                close = text.IndexOf("*/", close + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length;
            }
            else if (text[close] == '"')
            {
                close++;
                while (close < text.Length && text[close] != '"')
                {
                    close += text[close] == '\\' ? 2 : 1;
                }
                close++;
            }
            else
            {
                close++;
            }
        }
        if (close >= text.Length)
        {
            return false;
        }
        var body = text[open..close];
        foreach (var gone in remove)
        {
            var entry = System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^\s*" + System.Text.RegularExpressions.Regex.Escape(gone) + @"(?:\s*/\*[^*\n]*\*/)?\s*,[ \t]*\n?");
            if (entry.Count != 1)
            {
                return false;
            }
            body = body.Remove(entry[0].Index, entry[0].Length);
        }
        body = body.TrimEnd('\t', ' ') + string.Concat(add.Select(a => $"\t\t\t\t{a},\n")) + "\t\t\t";
        text = text[..open] + "\n" + body.TrimStart('\n') + text[close..];
        return true;
    }

    private static PbxDict WithList(PbxDict original, string key, IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var items = (original.Strings(key) ?? []).Where(i => !remove.Contains(i)).Concat(add).Select(i => (PbxValue)new PbxString(i)).ToList();
        var entries = new Dictionary<string, PbxValue>(original.Entries, StringComparer.Ordinal) { [key] = new PbxArray(items) };
        return new PbxDict(entries);
    }

    private static bool Same(PbxValue a, PbxValue b) => (a, b) switch
    {
        (PbxString x, PbxString y) => x.Value == y.Value,
        (PbxArray x, PbxArray y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
        (PbxDict x, PbxDict y) => x.Entries.Count == y.Entries.Count
            && x.Entries.All(e => y.Entries.TryGetValue(e.Key, out var other) && Same(e.Value, other)),
        _ => false,
    };

    /// <summary>A build setting of <paramref name="configuration"/> for the target (its own, else the project's): the value, or why it cannot be told.</summary>
    private (string? Value, string? Problem) Setting(XcodeTarget target, string configuration, string name) =>
        SettingOrUnset(target, configuration, name) is (null, null) ? (null, $"{name} is set neither by the target nor by the project") : SettingOrUnset(target, configuration, name);

    /// <summary>As <see cref="Setting"/>, but (null, null) when neither the target nor the project sets it (Xcode's default then applies).</summary>
    private (string? Value, string? Problem) SettingOrUnset(XcodeTarget target, string configuration, string name)
    {
        foreach (var (owner, label) in new[] { (target.Object, $"the target {target.Name}"), (_project, "the project") })
        {
            if (owner.Str("buildConfigurationList") is not { } listId || Object(listId) is not { } list)
            {
                return (null, $"{label} has no build configuration list");
            }
            var config = (list.Strings("buildConfigurations") ?? []).Select(Object).FirstOrDefault(c => c?.Str("name") == configuration);
            if (config is null)
            {
                return (null, $"{label} has no {configuration} configuration");
            }
            if (config.Dict("buildSettings")?.Entries.GetValueOrDefault(name) is { } value)
            {
                return value is PbxString s ? (s.Value, null) : (null, $"{label}'s {name} is not a single value");
            }
            if (config.Str("baseConfigurationReference") is not null)
            {
                return (null, $"{label}'s {configuration} configuration is based on an .xcconfig file, which may set {name}");
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Why the test bundle <paramref name="target"/> builds in <paramref name="configuration"/> is not named for the target —
    /// its <c>PRODUCT_NAME</c> is set to something but <c>$(TARGET_NAME)</c> or the target's name, or cannot be told (an
    /// <c>.xcconfig</c>) — or null. xcodebuild names a bundle's tests by the bundle (<c>&lt;product&gt;/…</c> in the result
    /// bundle), while <c>-only-testing:</c> takes the target's name.
    /// </summary>
    public string? ProductNameProblem(XcodeTarget target, string configuration)
    {
        var (name, problem) = SettingOrUnset(target, configuration, "PRODUCT_NAME");
        return problem is not null ? $"its product name cannot be told ({problem})"
            : name is null or "$(TARGET_NAME)" or "${TARGET_NAME}" || name == target.Name ? null
            : $"its PRODUCT_NAME is {name}, not the target's name {target.Name}: xcodebuild names its tests by the bundle, which the gate does not map to the target yet";
    }

    /// <summary>
    /// Whether <paramref name="target"/> builds for macOS in <paramref name="configuration"/> (its <c>SDKROOT</c> is
    /// <c>macosx</c>, or <c>auto</c> with <c>macosx</c> among its <c>SUPPORTED_PLATFORMS</c>); else what it builds for, or
    /// why that cannot be told.
    /// </summary>
    public (bool Mac, string? Why) BuildsForMac(XcodeTarget target, string configuration)
    {
        var (sdk, problem) = Setting(target, configuration, "SDKROOT");
        if (sdk is null)
        {
            return (false, problem);
        }
        if (sdk.StartsWith("macosx", StringComparison.Ordinal))
        {
            return (true, null);
        }
        if (sdk == "auto")
        {
            var (platforms, why) = Setting(target, configuration, "SUPPORTED_PLATFORMS");
            return platforms is null ? (false, why)
                : platforms.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("macosx") ? (true, null)
                : (false, $"it builds for {platforms} only");
        }
        return (false, $"it builds for the {sdk} SDK");
    }
}

/// <summary>One test target a scheme's test action runs (its <c>TestableReference</c>), and whether the scheme filters its tests.</summary>
public sealed record XcodeTestable(string BlueprintId, string Container, bool Filtered);

/// <summary>
/// A shared scheme (<c>xcshareddata/xcschemes/&lt;name&gt;.xcscheme</c>): its name, the configuration its test action
/// builds (<c>Debug</c> when it names none), and the test targets that action runs; <see cref="Problem"/> when the test
/// action is one the gate does not read (test plans with no single default, or beside the action's own test targets);
/// <see cref="TestPlan"/> when it runs its default test plan.
/// </summary>
public sealed record XcodeScheme(string Name, string TestConfiguration, IReadOnlyList<XcodeTestable> Testables, string? Problem)
{
    /// <summary>Reads a scheme file; throws <see cref="FormatException"/> when it is not one (no DTD is read).</summary>
    public static XcodeScheme Parse(string name, string text)
    {
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new FormatException("it has no root element");
        }
        catch (XmlException e)
        {
            throw new FormatException($"it is not well-formed XML ({e.Message})");
        }
        if (root.Name.LocalName != "Scheme")
        {
            throw new FormatException($"its root element is <{root.Name.LocalName}>, not <Scheme>");
        }
        var test = root.Element("TestAction");
        if (test is null)
        {
            return new XcodeScheme(name, "Debug", [], null);
        }
        var configuration = test.Attribute("buildConfiguration")?.Value is { Length: > 0 } c ? c : "Debug";
        string? problem = null;
        string? plan = null;
        if (test.Element("TestPlans") is { } plans)
        {
            // xcodebuild runs the scheme's default plan when given no -testPlan (the gate never gives one: the wrapper refuses it).
            var defaults = plans.Elements("TestPlanReference").Where(r => r.Attribute("default")?.Value == "YES").ToList();
            if (defaults is not [var only] || only.Attribute("reference")?.Value is not { Length: > 0 } reference)
            {
                problem = "its test action names no single default test plan";
            }
            else if (test.Element("Testables")?.Elements().Any() == true)
            {
                problem = "its test action has both a test plan and test targets of its own";
            }
            else
            {
                plan = reference;
            }
        }
        var testables = (test.Element("Testables")?.Elements("TestableReference") ?? [])
            .Where(t => t.Attribute("skipped")?.Value != "YES")
            .SelectMany(t => t.Elements("BuildableReference").Select(b => new XcodeTestable(
                b.Attribute("BlueprintIdentifier")?.Value ?? "", b.Attribute("ReferencedContainer")?.Value ?? "",
                t.Element("SkippedTests") is not null || t.Element("SelectedTests") is not null || t.Attribute("useTestSelectionWhitelist")?.Value == "YES")))
            .ToList();
        return new XcodeScheme(name, configuration, testables, problem) { TestPlan = plan };
    }

    /// <summary>The <c>container:</c> reference of the test action's default test plan, when it runs one (its test targets are then the plan's).</summary>
    public string? TestPlan { get; init; }

    /// <summary>
    /// The repository path a <c>container:</c> reference names, relative to <paramref name="directory"/> (the directory
    /// holding the project or workspace the scheme belongs to); null when it names none there.
    /// </summary>
    public static string? ContainerPath(string container, string directory)
    {
        if (!container.StartsWith("container:", StringComparison.Ordinal))
        {
            return null;
        }
        var relative = container["container:".Length..];
        return relative.StartsWith('/') ? null : RepoPath.Normalize(directory.Length == 0 ? relative : $"{directory}/{relative}");
    }
}

/// <summary>
/// One test target of a test plan: the target (its id and its <c>container:</c> reference, relative to the directory of the
/// project or workspace whose scheme runs the plan — as Xcode 26.5 resolves it) and the plan's own filters of its tests.
/// </summary>
public sealed record XcodePlanTarget(string Identifier, string Container, IReadOnlyList<string>? Selected, IReadOnlyList<string> Skipped)
{
    /// <summary>
    /// Why the plan does not run the test whose place in the target is <paramref name="key"/> (<c>Type/f()</c>, the id
    /// without the target), or null: a skipped entry names it or a type holding it, or the plan selects tests and none of
    /// its entries does.
    /// </summary>
    public string? Excludes(string key)
    {
        static bool Names(string entry, string key) => key == entry || key.StartsWith(entry.TrimEnd('/') + "/", StringComparison.Ordinal);
        return Skipped.FirstOrDefault(s => Names(s, key)) is { } skipped ? $"skips it ({skipped})"
            : Selected is not null && !Selected.Any(s => Names(s, key)) ? "selects some of the target's tests, not it"
            : null;
    }
}

/// <summary>
/// An Xcode test plan (<c>.xctestplan</c>, JSON) as far as the gate reads it, failing closed: exactly one configuration,
/// options only of the kinds that neither repeat a test nor change what its result means (<see cref="Options"/>), and test
/// targets whose filters are lists of names. Anything else is a <see cref="FormatException"/>.
/// </summary>
public static class XcodeTestPlan
{
    /// <summary>The largest test plan read.</summary>
    public const int MaxChars = 1024 * 1024;

    /// <summary>
    /// The options a plan may set. Not among them, so refused: <c>testRepetitionMode</c> and <c>maximumTestRepetitions</c>
    /// (a test run several times, retried on failure or until it fails), and every option this does not know.
    /// </summary>
    public static readonly IReadOnlySet<string> Options = new HashSet<string>(StringComparer.Ordinal)
    {
        "addressSanitizer", "areLocalizationScreenshotsEnabled", "codeCoverage", "commandLineArgumentEntries", "defaultTestExecutionTimeAllowance",
        "diagnosticCollectionPolicy", "environmentVariableEntries", "language", "locationScenario", "mainThreadCheckerEnabled",
        "mallocStackLoggingOptions", "maximumTestExecutionTimeAllowance", "preferredScreenCaptureFormat", "region", "systemAttachmentLifetime",
        "targetForVariableExpansion", "testExecutionOrdering", "testTimeoutsEnabled", "threadSanitizer", "uiTestingScreenshotsLifetime",
        "undefinedBehaviorSanitizer", "userAttachmentLifetime",
    };

    private static readonly IReadOnlySet<string> TargetKeys = new HashSet<string>(StringComparer.Ordinal)
        { "target", "enabled", "parallelizable", "randomExecutionOrdering", "selectedTests", "skippedTests" };

    /// <summary>The test targets the plan runs (an <c>enabled: false</c> one is not among them); throws <see cref="FormatException"/> for anything not read.</summary>
    public static IReadOnlyList<XcodePlanTarget> Parse(string text)
    {
        if (text.Length > MaxChars)
        {
            throw new FormatException($"it is larger than {MaxChars} characters");
        }
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException($"it is not JSON ({e.Message})");
        }
        Keys(root, "the plan", "configurations", "defaultOptions", "testTargets", "version");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1")
        {
            throw new FormatException("its version is not 1");
        }
        if (Array(root, "configurations") is not [var configuration])
        {
            throw new FormatException("it has not exactly one configuration (each would run every test again)");
        }
        Keys(configuration, "its configuration", "id", "name", "options");
        foreach (var options in new[] { configuration.TryGetProperty("options", out var o) ? o : default, root.TryGetProperty("defaultOptions", out var d) ? d : default })
        {
            if (options.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }
            if (options.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("its options are not an object");
            }
            foreach (var option in options.EnumerateObject().Where(p => !Options.Contains(p.Name)))
            {
                throw new FormatException($"it sets the option {option.Name}, which the gate does not read");
            }
        }
        var targets = new List<XcodePlanTarget>();
        foreach (var entry in Array(root, "testTargets"))
        {
            Keys(entry, "a test target", [.. TargetKeys]);
            if (!entry.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("a test target has no target");
            }
            Keys(target, "a test target's target", "containerPath", "identifier", "name");
            var container = Str(target, "containerPath");
            var identifier = Str(target, "identifier");
            if (entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new FormatException("a test target's enabled is not true or false");
            }
            if (enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            targets.Add(new XcodePlanTarget(identifier, container, entry.TryGetProperty("selectedTests", out _) ? Names(entry, "selectedTests") : null,
                entry.TryGetProperty("skippedTests", out _) ? Names(entry, "skippedTests") : []));
        }
        return targets;
    }

    private static void Keys(JsonElement element, string what, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{what} is not an object");
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new FormatException($"{what} has {property.Name}, which the gate does not read");
            }
        }
    }

    private static List<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList()
            : throw new FormatException($"its {name} is not a list");

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s
            : throw new FormatException($"a test target's {name} is not a string");

    private static List<string> Names(JsonElement entry, string name) =>
        Array(entry, name).Select(n => n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s ? s
            : throw new FormatException($"a test target's {name} holds something but a test's name (a form the gate does not read)")).ToList();
}

/// <summary>The projects an Xcode workspace (<c>contents.xcworkspacedata</c>) names, as repository paths.</summary>
public static class XcodeWorkspace
{
    /// <summary>
    /// The repository paths of the projects <paramref name="contents"/> (the text of
    /// <c>&lt;workspace&gt;/contents.xcworkspacedata</c>, the workspace in <paramref name="directory"/>) names: <c>group:</c>
    /// locations relative to their enclosing groups, <c>container:</c> to the workspace's directory; an absolute location
    /// or one leaving the repository is not in it. Throws <see cref="FormatException"/> when it cannot be read.
    /// </summary>
    public static IReadOnlyList<string> Projects(string contents, string directory)
    {
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(contents), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new FormatException("it has no root element");
        }
        catch (XmlException e)
        {
            throw new FormatException($"it is not well-formed XML ({e.Message})");
        }
        var projects = new List<string>();
        void Walk(XElement element, string at, int depth)
        {
            if (depth > Pbx.MaxDepth)
            {
                throw new FormatException("its groups nest too deep");
            }
            foreach (var child in element.Elements())
            {
                var location = child.Attribute("location")?.Value ?? "";
                var resolved = location.StartsWith("group:", StringComparison.Ordinal) ? Join(at, location["group:".Length..])
                    : location.StartsWith("container:", StringComparison.Ordinal) ? Join(directory, location["container:".Length..])
                    : null;
                if (child.Name.LocalName == "Group")
                {
                    Walk(child, resolved ?? at, depth + 1);
                }
                else if (child.Name.LocalName == "FileRef" && resolved is { } path && path.EndsWith(".xcodeproj", StringComparison.Ordinal))
                {
                    projects.Add(path);
                }
            }
        }
        Walk(root, directory, 0);
        return projects;
    }

    private static string? Join(string? at, string relative) =>
        at is null || relative.StartsWith('/') ? null : relative.Length == 0 ? at : RepoPath.Normalize(at.Length == 0 ? relative : $"{at}/{relative}");
}
