using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// What the helper copy's <c>id</c> reports: its own name and uid, and the uid of each name it is asked about (any other
/// name: no such user). <paramref name="PinnedUid"/> is the copy's <c>sandbox_uid</c> (what setup-worker-user.sh pins).
/// </summary>
internal sealed record FakeIdentity(string User, string Uid, IReadOnlyDictionary<string, string> UidsByName, string PinnedUid = "")
{
    /// <summary>The sandbox role account as the helper's sweep checks expect it (name <c>_dftest</c>, uid 450, pinned): lets a copy's uid sweep run.</summary>
    public static FakeIdentity SandboxRole { get; } = new("_dftest", "450", new Dictionary<string, string> { ["_dftest"] = "450" }, "450");
}

internal sealed record SafeHelperOptions
{
    /// <summary>null: the real <c>id</c>, <c>sandbox_user</c> stays <c>_factory</c> (we are not) and <c>sandbox_uid</c> unpinned, so the uid sweep never runs.</summary>
    public FakeIdentity? Identity { get; init; }

    /// <summary>"pid|start time" lines (<see cref="SafeHelper.Register"/>): processes outside the helper's tree that the test made and the seam may kill.</summary>
    public string? Registry { get; init; }

    /// <summary>A shell snippet printing "pid ppid" lines, the pass number in <c>$n</c>, in place of the process table. null: the real table, restricted to the test's own processes.</summary>
    public string? FakeListing { get; init; }

    /// <summary>A mutation of the helper's own logic, applied before the copy is checked. One that touches the seam or the listing is refused.</summary>
    public Func<string, string>? Mutate { get; init; }

    /// <summary>
    /// The seam signals only the helper and its own tree (so the helper still stops its watcher and exits) and logs every
    /// other target it would kill as "log" instead: for tests of the seam's own rules, which then cannot signal anything else.
    /// </summary>
    public bool LogOnly { get; init; }
}

/// <summary>
/// The one way a test runs <c>scripts/factory-worker-launch</c> (or anything built from it): a copy in which every signal goes
/// through a seam and the uid sweep's process listing is the test's own. The real helper's exit sweeps every process of its
/// uid; as the owner that would take down their whole session (2026-10-06), so no test executes the real file, and
/// <c>SafeHelperTests</c> fails if one tries. The helper's text never leaves this class except through a checked copy
/// (<see cref="BuildCopy"/>) or <see cref="SourceContains"/>.
/// <para>The seam (<c>send_signal</c>) logs every target to <see cref="Targets"/> and, per target: signals it if it is the
/// helper itself, in the helper's process tree, a <see cref="SafeHelperOptions.Registry"/> pid whose start time still matches
/// (so a reused pid is never hit), or the group of the worker the helper started while every member of that group is one of
/// those or this test host's own; skips it (no signal) if it is not the current user's or is another process of this test
/// host's; and otherwise — pid or group 0 or 1 included — refuses: logs it to <see cref="Refusals"/> and exits 99 before
/// signalling anything in that call.</para>
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class SafeHelper
{
    /// <summary>The production signal primitive and process listing: both must be present once, and both are replaced.</summary>
    public const string SignalPrimitive = """
        send_signal() {
            local a n
            for a in "$@"; do
                case "$a" in -[A-Z]* | --) continue ;; esac
                n=${a#-}
                case "$n" in '' | *[!0-9]*) return 1 ;; esac
                [ "$((10#$n))" -gt 1 ] || return 1
            done
            kill "$@" 2>/dev/null
        }
        """;

    public const string ListPrimitive =
        "list_uid_procs() { { ps -U \"$self_uid\" -o pid=,ppid=; ps -u \"$self_uid\" -o pid=,ppid=; } 2>/dev/null || true; }";

    private const string SeamStart = "# >>> SafeHelper seam";
    private const string SeamEnd = "# <<< SafeHelper seam";
    private const string ListingStart = "# >>> SafeHelper listing";
    private const string ListingEnd = "# <<< SafeHelper listing";

    private static readonly Lazy<string> CurrentUid = new(() =>
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/id", ["-u"]) { RedirectStandardOutput = true })!;
        var uid = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Matches("^[0-9]+$", uid);
        return uid;
    });

    private static string SourcePath => System.IO.Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-worker-launch");

    /// <summary>The production helper's text. Never executed, and never handed out.</summary>
    private static string Source => File.ReadAllText(SourcePath);

    private SafeHelper(string path, string targets, string refusals, string? passes)
    {
        Path = path;
        TargetsFile = targets;
        RefusalsFile = refusals;
        PassesFile = passes;
    }

    public string Path { get; }

    private string TargetsFile { get; }

    private string RefusalsFile { get; }

    private string? PassesFile { get; }

    /// <summary>Every SIGKILL target that is a pid (not a process group), whatever the seam did with it.</summary>
    public int[] Targets => Lines(TargetsFile)
        .Select(l => l.Split(' '))
        .Where(f => f.Length == 3 && f[1] == "KILL" && int.TryParse(f[2], out var pid) && pid > 0)
        .Select(f => int.Parse(f[2]))
        .ToArray();

    /// <summary>The seam's log, one "kill|skip|refuse SIGNAL target" line per target.</summary>
    public string[] TargetLog => Lines(TargetsFile);

    public string[] Refusals => Lines(RefusalsFile);

    /// <summary>How many times the fake listing was read (only with <see cref="SafeHelperOptions.FakeListing"/>).</summary>
    public int Passes => int.Parse(File.ReadAllText(PassesFile ?? throw new InvalidOperationException("no fake listing")).Trim());

    private static string[] Lines(string file) => File.Exists(file) ? File.ReadAllLines(file) : [];

    /// <summary>Whether the production helper's text contains <paramref name="value"/> (for assertions about it).</summary>
    public static bool SourceContains(string value) => Source.Contains(value, StringComparison.Ordinal);

    /// <summary>Records a process the test made (outside the helper's tree) as one the seam may kill, with its start time.</summary>
    public static void Register(string registry, OwnProcess process) =>
        File.AppendAllText(registry, $"{process.Pid}|{process.Start}\n");

    /// <summary>Writes the checked copy to <paramref name="dir"/> and returns it.</summary>
    public static SafeHelper Create(string dir, SafeHelperOptions? options = null)
    {
        options ??= new SafeHelperOptions();
        var id = Guid.NewGuid().ToString("N")[..8];
        var targets = System.IO.Path.Combine(dir, $"seam-{id}.targets");
        var refusals = System.IO.Path.Combine(dir, $"seam-{id}.refusals");
        var passes = options.FakeListing is null ? null : System.IO.Path.Combine(dir, $"seam-{id}.passes");
        if (passes is not null)
        {
            File.WriteAllText(passes, "0\n");
        }
        var copy = BuildCopy(options, targets, refusals, passes);
        var path = SandboxSupport.Executable(dir, $"safe-helper-{id}", copy);
        return new SafeHelper(path, targets, refusals, passes);
    }

    /// <summary>The copy's text, always checked (<see cref="AssertSafe"/>, and the seam and listing exactly as generated).</summary>
    public static string BuildCopy(SafeHelperOptions options, string targets, string refusals, string? passes)
    {
        foreach (var path in new[] { targets, refusals, passes, options.Registry })
        {
            Assert.True(path is null || !path.Contains('\''), $"quote in {path}");
        }
        var source = Source;
        Assert.Equal(1, Count(source, SignalPrimitive));
        Assert.Equal(1, Count(source, ListPrimitive));
        Assert.Equal(1, Count(source, "\nsandbox_user=_factory\n"));
        Assert.Equal(1, Count(source, "\nsandbox_uid=\n"));
        Assert.Equal(1, Count(source, "\nset -eu\n"));

        var registry = options.Registry ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(targets)!, "no-registry");
        var host = Environment.ProcessId.ToString();
        var uid = CurrentUid.Value;
        if (options.FakeListing is { } fakeCode)
        {
            AssertNothingHidden(StripComments(fakeCode), "in the fake listing");
        }
        var listingBody = options.FakeListing is { } fake
            ? "list_uid_procs() { n=$(( $(cat '" + passes + "') + 1 )); echo \"$n\" >'" + passes + "'; " + fake + "; }"
            : $$"""
                list_uid_procs() {
                    local reg
                    reg=" $(__seam_registry | /usr/bin/tr '\n' ' ') "
                    /bin/ps -A -o pid=,ppid=,uid= 2>/dev/null | /usr/bin/awk -v host={{host}} -v uid={{uid}} -v reg="$reg" '
                        function under(p, root,   n) { n = 0; while (p != root && (p in up) && n++ < 100000) p = up[p]; return p == root }
                        { up[$1] = $2; owner[$1] = $3 }
                        END { for (p in up) if (owner[p] == uid && p != host && (under(p, host) || index(reg, " " p " "))) print p, up[p] }'
                }
                """;
        var listing = $"{ListingStart}\n{listingBody}\n{ListingEnd}";
        var seam = $$"""
            {{SeamStart}}
            __seam_registry() {
                local rp rl cur
                [ -f '{{registry}}' ] || return 0
                while IFS='|' read -r rp rl; do
                    case "$rp" in '' | *[!0-9]*) continue ;; esac
                    cur=$(/bin/ps -o lstart= -p "$rp" 2>/dev/null | /usr/bin/awk '{ $1 = $1; print }')
                    if [ -n "$cur" ] && [ "$cur" = "$rl" ]; then echo "$rp"; fi
                done <'{{registry}}'
                return 0
            }
            __seam_classify() {
                local reg
                reg=" $(__seam_registry | /usr/bin/tr '\n' ' ') "
                /bin/ps -A -o pid=,ppid=,uid=,pgid= 2>/dev/null | /usr/bin/awk -v self="$$" -v host={{host}} -v uid={{uid}} -v reg="$reg" -v worker="${worker:-}" -v logonly={{(options.LogOnly ? 1 : 0)}} -v targets="$*" '
                    function under(p, root,   n) { n = 0; while (p != root && (p in up) && n++ < 100000) p = up[p]; return p == root }
                    function own(p) { return p == self || under(p, self) || under(p, host) || index(reg, " " p " ") }
                    function out(c, x) { if (c == "kill" && logonly && !(x == self || under(x, self))) c = "log"; print c, x }
                    function group_ok(g,   p) {
                        if (worker !~ /^[0-9]+$/ || g != worker + 0 || g <= 1) return 0
                        for (p in grp) if (grp[p] + 0 == g && !own(p)) return 0
                        return 1
                    }
                    { up[$1] = $2; owner[$1] = $3; grp[$1] = $4 }
                    END {
                        nt = split(targets, t, " ")
                        for (i = 1; i <= nt; i++) {
                            x = t[i]
                            if (x ~ /^-[0-9]+$/) out(group_ok(substr(x, 2) + 0) ? "kill" : "refuse", x)
                            else if (x !~ /^[0-9]+$/ || x + 0 <= 1) out("refuse", x)
                            else if (x == self || under(x, self) || index(reg, " " x " ")) out("kill", x)
                            else if (!(x in up) || owner[x] != uid || under(x, host)) out("skip", x)
                            else out("refuse", x)
                        }
                    }'
            }
            send_signal() {
                local sig=-TERM cls x refused=0 kills=()
                case "${1:-}" in -[A-Z]*) sig=$1; shift ;; esac
                [ "${1:-}" != -- ] || shift
                [ $# -gt 0 ] || return 1
                while read -r cls x; do
                    echo "$cls ${sig#-} $x" >>'{{targets}}'
                    case "$cls" in
                        kill) kills+=("$x") ;;
                        refuse) refused=1; echo "${sig#-} $x" >>'{{refusals}}' ;;
                    esac
                done < <(__seam_classify "$@")
                if [ "$refused" -ne 0 ]; then
                    echo "factory-worker-launch test seam: refusing to signal a process that is not the test's own" >&2
                    exit 99
                fi
                [ ${#kills[@]} -gt 0 ] || return 1
                kill "$sig" -- "${kills[@]}" 2>/dev/null
            }
            {{SeamEnd}}
            """;
        var copy = source.Replace(SignalPrimitive, seam).Replace(ListPrimitive, listing);
        if (options.Identity is { } identity)
        {
            Assert.Matches("^[0-9]*$", identity.PinnedUid);
            var cases = string.Concat(identity.UidsByName.Select(kv => $"        '-u {kv.Key}') echo '{kv.Value}' ;;\n"));
            var fakeId = "id() {\n    case \"$*\" in\n"
                + $"        -u) echo '{identity.Uid}' ;;\n"
                + $"        -un) echo '{identity.User}' ;;\n"
                + cases
                + "        *) echo \"id: $*: no such user\" >&2; return 1 ;;\n    esac\n}\n";
            copy = copy
                .Replace("\nset -eu\n", "\nset -eu\n" + fakeId)
                .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={identity.User}\n")
                .Replace("\nsandbox_uid=\n", $"\nsandbox_uid={identity.PinnedUid}\n");
        }
        if (options.Mutate is { } mutate)
        {
            copy = mutate(copy);
        }
        // A mutation is of the helper's own logic: the seam and the listing must be exactly what was generated.
        if (Block(copy, SeamStart, SeamEnd) != seam || Block(copy, ListingStart, ListingEnd) != listing)
        {
            throw new InvalidOperationException("helper copy's seam or listing is not the generated one (a mutation touched it)");
        }
        AssertSafe(copy);
        return copy;
    }

    /// <summary>
    /// The copy is safe to run: the seam and listing are in, once each, and outside them nothing can signal, list the user's
    /// processes or run hidden code — no <c>kill</c>/<c>pkill</c>/<c>killall</c>/<c>killpg</c>, <c>launchctl</c>,
    /// <c>osascript</c>, <c>eval</c>, <c>timeout</c>, <c>sudo</c>, an interpreter, <c>ps</c> or a uid <c>pgrep</c>, an awk
    /// <c>system()</c> or a <c>$'…'</c> string, also when spelled with quotes or backslashes inside the word. Only comments
    /// are ignored: whole-line ones and a <c>#</c> starting a word outside quotes.
    /// </summary>
    public static void AssertSafe(string copy)
    {
        var outside = Outside(Outside(copy, SeamStart, SeamEnd, "seam"), ListingStart, ListingEnd, "listing");
        var code = StripComments(outside);
        AssertNothingHidden(code, "outside the seam");
        if (code.Contains(ListPrimitive, StringComparison.Ordinal) || code.Contains("list_uid_procs() {", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("helper copy defines a process listing outside the generated one");
        }
    }

    private static readonly Regex Denied = new(
        @"(?<![\w.-])(kill|pkill|killall|killpg|launchctl|osascript|eval|timeout|gtimeout|shutdown|halt|reboot|sudo|su|doas"
        + @"|perl|python[0-9.]*|ruby|node|source|ps)(?![\w-])"
        + @"|(?<![\w.-])(ba|z|k|da)?sh\s+-|\bpgrep\b[^\n|;]*\s-[A-Za-z]*[Uu]\b|\bsystem\s*\(");

    private static void AssertNothingHidden(string code, string where)
    {
        if (code.Contains("$'", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"helper copy has a $'…' string {where}");
        }
        // ki""ll, k\ill and 'kill' are all kill to the shell.
        var words = code.Replace("\\\n", " ").Replace("\"", "").Replace("'", "").Replace("\\", "");
        if (Denied.Match(words) is { Success: true } hit)
        {
            throw new InvalidOperationException($"helper copy signals, lists processes or runs hidden code {where}: '{hit.Value}' in\n{code}");
        }
    }

    /// <summary>The copy without its one <paramref name="start"/>…<paramref name="end"/> block.</summary>
    private static string Outside(string copy, string start, string end, string what)
    {
        var s = copy.IndexOf(start, StringComparison.Ordinal);
        var e = copy.IndexOf(end, StringComparison.Ordinal);
        if (s < 0 || e < s || Count(copy, start) != 1 || Count(copy, end) != 1)
        {
            throw new InvalidOperationException($"helper copy has no (single) SafeHelper {what}");
        }
        return copy[..s] + copy[(e + end.Length)..];
    }

    private static string? Block(string copy, string start, string end)
    {
        var s = copy.IndexOf(start, StringComparison.Ordinal);
        var e = copy.IndexOf(end, StringComparison.Ordinal);
        return s < 0 || e < s ? null : copy[s..(e + end.Length)];
    }

    /// <summary>Drops shell comments: a <c>#</c> at the start of a word, outside quotes, to the end of its line.</summary>
    private static string StripComments(string text)
    {
        var result = new StringBuilder(text.Length);
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                quote = c == '\'' ? '\0' : quote;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                result.Append(c).Append(text[++i]);
                continue;
            }
            else if (quote == '"')
            {
                quote = c == '"' ? '\0' : quote;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is ';' or '&' or '|' or '(' or ')'))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                if (i < text.Length)
                {
                    result.Append('\n');
                }
                continue;
            }
            result.Append(c);
        }
        return result.ToString();
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;
}
