using System.Text;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// The path tiers of <c>factory/gate.yaml</c> (sc-25381), most restrictive first. A touched path is in the first tier whose
/// patterns match it; a path no tier's patterns match is <see cref="Normal"/>.
/// </summary>
public enum Tier
{
    /// <summary>The gate's own policy, CI workflows, CODEOWNERS, the factory's prompts: always escalates.</summary>
    Sealed,
    /// <summary>Auth, migrations, infra, secrets, payments: built and reviewed, merged only after escalation.</summary>
    Protected,
    /// <summary>Docs and tests: the lightest checks.</summary>
    Free,
    /// <summary>Every other path.</summary>
    Normal,
}

public static class Tiers
{
    /// <summary>The tiers in precedence order: a path is in the first that matches it (Normal matches everything).</summary>
    public static readonly IReadOnlyList<Tier> Precedence = [Tier.Sealed, Tier.Protected, Tier.Free, Tier.Normal];

    /// <summary>The tier's key in <c>gate.yaml</c>.</summary>
    public static string Key(this Tier tier) => tier switch
    {
        Tier.Sealed => "sealed",
        Tier.Protected => "protected",
        Tier.Free => "free",
        Tier.Normal => "normal",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };
}

/// <summary>The named checks a tier can require. <see cref="CiGreen"/> and <see cref="ReviewPass"/> every tier must list (E2).</summary>
public static class GateChecks
{
    /// <summary>Every CI check on the head commit finished and passed.</summary>
    public const string CiGreen = "ci-green";
    /// <summary>The review panel passed the head commit (every reviewer of a family other than the implementer's).</summary>
    public const string ReviewPass = "review-pass";
    /// <summary>The panel's verdict on the head includes the security review.</summary>
    public const string SecurityReview = "security-review";
    /// <summary>The change is within the policy's risk threshold: diff size and fix rounds.</summary>
    public const string RiskThreshold = "risk-threshold";
    /// <summary>
    /// The tests the PR adds fail on the base commit (with the PR's test files applied) and pass on the head, both run
    /// sandboxed (sc-25382, <see cref="NewTestsCheck"/>): a new test that already passes on the base checks nothing.
    /// </summary>
    public const string NewTestsFailOnBase = "new-tests-fail-on-base";

    public static readonly IReadOnlyList<string> All = [CiGreen, ReviewPass, SecurityReview, RiskThreshold, NewTestsFailOnBase];
}

/// <summary>Repository paths as git names them: case-sensitive, relative to the repository root, '/'-separated.</summary>
public static class RepoPath
{
    /// <summary>
    /// The canonical form of <paramref name="path"/>: leading <c>./</c>, empty and <c>.</c> segments removed and <c>..</c>
    /// resolved. Null when it cannot name a file in the repository (empty, absolute, a <c>..</c> that climbs above the root,
    /// a NUL): such a path is never matched against a pattern, so it cannot be dressed up to look like a lighter tier.
    /// </summary>
    public static string? Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\0'))
        {
            return null;
        }
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}

/// <summary>
/// One path pattern of a tier, anchored at the repository root and matched case-sensitively (as git compares paths) unless
/// parsed with <c>ignoreCase</c>: <c>*</c> matches within one path segment, <c>?</c> one character of a segment, a
/// <c>**</c> segment any number of segments; a pattern ending in <c>/</c> matches everything under that directory. A
/// leading <c>./</c> is ignored; a <c>..</c> or <c>.</c> segment, an absolute pattern, or <c>**</c> inside a segment is
/// invalid.
/// </summary>
public sealed class PathPattern
{
    private readonly Regex _regex;
    private readonly Regex? _cover;

    private PathPattern(string text, Regex regex, Regex? cover)
    {
        Text = text;
        _regex = regex;
        _cover = cover;
    }

    public string Text { get; }

    public bool Matches(string normalizedPath) => _regex.IsMatch(normalizedPath);

    /// <summary>
    /// Whether this pattern alone matches every path under <paramref name="directory"/> (e.g. <c>.github/workflows/</c>),
    /// decided from the pattern's form rather than by trying example paths: it is a directory pattern (trailing <c>/</c>)
    /// or ends in <c>**</c>, and the directory it names matches <paramref name="directory"/> or one of its ancestors. A
    /// pattern naming files (<c>.github/workflows/ci.yml</c>, <c>.github/workflows/*.yml</c>) never covers a directory.
    /// </summary>
    public bool CoversEverythingUnder(string directory)
    {
        if (_cover is null)
        {
            return false;
        }
        var segments = directory.Trim('/').Split('/');
        for (var depth = 0; depth <= segments.Length; depth++)
        {
            // "" (the root), "a/", "a/b/", ...: the pattern matches everything under an ancestor-or-self of the directory.
            if (_cover.IsMatch(depth == 0 ? "" : string.Join('/', segments[..depth]) + "/"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses <paramref name="text"/>; null, with why, when it is not a valid pattern.</summary>
    public static PathPattern? TryParse(string text, out string? error, bool ignoreCase = false)
    {
        error = null;
        var body = text;
        while (body.StartsWith("./", StringComparison.Ordinal))
        {
            body = body[2..];
        }
        var directory = body.EndsWith('/');
        body = body.TrimEnd('/');
        if (body.Length == 0 || text.StartsWith('/') || text.Contains('\0'))
        {
            error = $"'{text}' is not a path pattern";
            return null;
        }
        var segments = body.Split('/');
        if (segments.FirstOrDefault(s => s is "" or "." or ".." || (s.Contains("**") && s != "**")) is { } bad)
        {
            error = $"'{text}' has an invalid segment '{bad}' ('.', '..', empty, or '**' inside a segment)";
            return null;
        }
        var regex = new StringBuilder("^");
        // What a directory must look like (with a trailing '/') for this pattern to match everything under it.
        string? cover = null;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (segments[i] == "**")
            {
                // "**/" any number of leading directories; a trailing "**" anything below.
                if (last)
                {
                    cover = regex + "$";
                }
                regex.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }
            foreach (var c in segments[i])
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
            if (!last)
            {
                regex.Append('/');
            }
        }
        if (directory && segments[^1] != "**")
        {
            cover = regex + "/$";
            regex.Append("/.+");
        }
        regex.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new PathPattern(text, new Regex(regex.ToString(), options), cover is null ? null : new Regex(cover, options));
    }

    public override string ToString() => Text;
}

/// <summary>
/// What a unified diff touches: every path (both sides of a rename or copy, deleted files too), its changed lines, and its
/// file entries (<see cref="Files"/>: one per <c>diff --git</c> header, so a rename counts once, as GitHub's
/// <c>changed_files</c> does).
/// </summary>
public sealed record DiffFacts(IReadOnlyList<string> Paths, int ChangedLines, int Files);

/// <summary>
/// Reads a unified diff (git's format, as GitHub's compare returns it) line by line: the file headers (<c>diff --git</c>,
/// <c>---</c>/<c>+++</c>, <c>rename</c>/<c>copy from|to</c>) only outside a hunk — so an added line that looks like a header
/// is not taken for one — and the <c>+</c>/<c>-</c> lines inside hunks as changed lines. C-quoted paths are unquoted.
/// </summary>
public static class DiffPaths
{
    public static IReadOnlyList<string> Of(string diff) => Parse(diff).Paths;

    public static DiffFacts Parse(string diff)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var changed = 0;
        var files = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                files++;
                foreach (var path in GitHeaderPaths(line["diff --git ".Length..]))
                {
                    paths.Add(path);
                }
                continue;
            }
            if (inHunk)
            {
                if (line.Length > 0 && line[0] is '+' or '-')
                {
                    changed++;
                }
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var name = Unquote(line[4..].Split('\t')[0]);
                if (name is not null && (name.StartsWith("a/", StringComparison.Ordinal) || name.StartsWith("b/", StringComparison.Ordinal)))
                {
                    paths.Add(name[2..]);
                }
            }
            else if (Rename.Match(line) is { Success: true } m && Unquote(m.Groups["p"].Value) is { } renamed)
            {
                paths.Add(renamed);
            }
        }
        return new DiffFacts(paths.ToList(), changed, files);
    }

    private static readonly Regex Rename = new(@"^(?:rename|copy) (?:from|to) (?<p>.+)$");

    /// <summary>The two paths of a <c>diff --git a/x b/y</c> header (each side possibly C-quoted).</summary>
    private static IEnumerable<string> GitHeaderPaths(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var end = QuotedEnd(rest);
            if (end < 0)
            {
                yield break;
            }
            var a = Unquote(rest[..(end + 1)]);
            var b = Unquote(rest[(end + 1)..].TrimStart());
            foreach (var p in new[] { a, b }.OfType<string>().Where(p => p.Length > 2))
            {
                yield return p[2..];
            }
            yield break;
        }
        // Unquoted: "a/<p> b/<p>" for an unrenamed file (split in the middle, so a path containing " b/" is not cut),
        // else the first " b/" or ' "b/' after "a/".
        if (rest.Length % 2 == 1 && rest[..(rest.Length / 2)] is var left && rest[(rest.Length / 2 + 1)..] is var right
            && left.StartsWith("a/", StringComparison.Ordinal) && right.StartsWith("b/", StringComparison.Ordinal) && left[2..] == right[2..])
        {
            yield return left[2..];
            yield break;
        }
        if (!rest.StartsWith("a/", StringComparison.Ordinal))
        {
            yield break;
        }
        var quotedB = rest.IndexOf(" \"b/", StringComparison.Ordinal);
        var plainB = rest.IndexOf(" b/", StringComparison.Ordinal);
        var split = quotedB >= 0 && (plainB < 0 || quotedB < plainB) ? quotedB : plainB;
        if (split < 0)
        {
            yield break;
        }
        yield return rest[2..split];
        if (Unquote(rest[(split + 1)..]) is { Length: > 2 } bPath)
        {
            yield return bPath[2..];
        }
    }

    private static int QuotedEnd(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\')
            {
                i++;
            }
            else if (s[i] == '"')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>A C-quoted path (git's <c>core.quotePath</c> form: escapes and octal UTF-8 bytes) unquoted; others as is.</summary>
    internal static string? Unquote(string text)
    {
        text = text.Trim();
        if (!(text.Length >= 2 && text[0] == '"' && text[^1] == '"'))
        {
            return text.Length == 0 ? null : text;
        }
        var bytes = new List<byte>();
        for (var i = 1; i < text.Length - 1; i++)
        {
            var c = text[i];
            if (c != '\\')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            if (++i >= text.Length - 1)
            {
                return null;
            }
            var e = text[i];
            if (e is >= '0' and <= '7' && i + 2 < text.Length - 1)
            {
                bytes.Add((byte)Convert.ToInt32(text.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(e switch
            {
                'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13,
                _ => (byte)e,
            });
        }
        return new UTF8Encoding(false, false).GetString(bytes.ToArray());
    }
}

/// <summary>One touched path and its tier; <see cref="Why"/> says why it was put there when not by a pattern.</summary>
public sealed record TouchedPath(string Path, Tier Tier, string? Why = null)
{
    public override string ToString() => Why is null ? $"{Path} ({Tier.Key()})" : $"{Path} ({Tier.Key()}: {Why})";
}

/// <summary>
/// A change classified by a policy: every touched path with its tier, the checks the touched tiers require together, and
/// the change's size.
/// </summary>
public sealed record Classification(IReadOnlyList<TouchedPath> Paths, IReadOnlySet<string> RequiredChecks, int ChangedLines, int ChangedFiles)
{
    public IReadOnlyList<TouchedPath> In(Tier tier) => Paths.Where(p => p.Tier == tier).ToList();

    public bool Requires(string check) => RequiredChecks.Contains(check);
}
