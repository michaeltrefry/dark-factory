using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>What the helper copy's <c>id</c> reports: its own name and uid, and the uid of each name it is asked about (any other name: no such user).</summary>
internal sealed record FakeIdentity(string User, string Uid, IReadOnlyDictionary<string, string> UidsByName)
{
    /// <summary>The sandbox role account as the helper's two sweep checks expect it (name <c>_dftest</c>, uid 450): lets a copy's uid sweep run.</summary>
    public static FakeIdentity SandboxRole { get; } = new("_dftest", "450", new Dictionary<string, string> { ["_dftest"] = "450" });
}

internal sealed record SafeHelperOptions
{
    /// <summary>null: the real <c>id</c>, and <c>sandbox_user</c> stays <c>_factory</c> (we are not), so the uid sweep never runs.</summary>
    public FakeIdentity? Identity { get; init; }

    /// <summary>"pid|start time" lines (<see cref="SafeHelper.Register"/>): processes outside the helper's tree that the test made and the seam may kill.</summary>
    public string? Registry { get; init; }

    /// <summary>A shell snippet printing "pid ppid" lines, the pass number in <c>$n</c>, in place of the process table. null: the real table, restricted to the test's own processes.</summary>
    public string? FakeListing { get; init; }

    /// <summary>A mutation of the helper's own logic (never of the seam), applied before the copy is checked.</summary>
    public Func<string, string>? Mutate { get; init; }
}

/// <summary>
/// The one way a test runs <c>scripts/factory-worker-launch</c> (or anything built from it): a copy in which every signal goes
/// through a seam and the uid sweep's process listing is the test's own. The real helper's exit sweeps every process of its
/// uid; as the owner that would take down their whole session (2026-10-06), so no test executes the real file, and
/// <c>SafeHelperGuardTests</c> fails if one tries.
/// <para>The seam (<c>send_signal</c>) logs every target to <see cref="Targets"/> and, per target: signals it if it is the
/// helper itself, in the helper's process tree, the group of the worker the helper started, or a <see cref="Registry"/> pid
/// whose start time still matches (so a reused pid is never hit); skips it (no signal) if it is not the current user's or is
/// another process of this test host's; and otherwise refuses — logs it to <see cref="Refusals"/> and exits 99 before
/// signalling anything in that call.</para>
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class SafeHelper
{
    /// <summary>The production signal primitive and process listing: both must be present once, and both are replaced.</summary>
    public const string SignalPrimitive = "send_signal() { kill \"$@\" 2>/dev/null; }";
    public const string ListPrimitive =
        "list_uid_procs() { { ps -U \"$self_uid\" -o pid=,ppid=; ps -u \"$self_uid\" -o pid=,ppid=; } 2>/dev/null || true; }";

    private const string SeamStart = "# >>> SafeHelper seam";
    private const string SeamEnd = "# <<< SafeHelper seam";

    private static readonly Lazy<string> CurrentUid = new(() =>
    {
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/id", ["-u"]) { RedirectStandardOutput = true })!;
        var uid = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Matches("^[0-9]+$", uid);
        return uid;
    });

    private static string SourcePath => System.IO.Path.Combine(SandboxSupport.RepoRoot, "scripts", "factory-worker-launch");

    /// <summary>The production helper's text, for assertions about it. Never executed.</summary>
    public static string Source => File.ReadAllText(SourcePath);

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

    /// <summary>Records a process the test made (outside the helper's tree) as one the seam may kill, with its start time.</summary>
    public static void Register(string registry, int pid) =>
        File.AppendAllText(registry, $"{pid}|{StartTime(pid)}\n");

    /// <summary><c>ps -o lstart=</c>, whitespace collapsed, as the seam compares it.</summary>
    public static string StartTime(int pid)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "lstart=", "-p", pid.ToString()]) { RedirectStandardOutput = true })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var start = Regex.Replace(text, @"\s+", " ").Trim();
        Assert.False(start.Length == 0, $"process {pid} has no start time (gone?)");
        return start;
    }

    /// <summary>Writes the copy to <paramref name="dir"/> after checking it, and returns it.</summary>
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
        AssertSafe(copy);
        var path = SandboxSupport.Executable(dir, $"safe-helper-{id}", copy);
        return new SafeHelper(path, targets, refusals, passes);
    }

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
        Assert.Equal(1, Count(source, "\nset -eu\n"));

        var registry = options.Registry ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(targets)!, "no-registry");
        var host = Environment.ProcessId.ToString();
        var uid = CurrentUid.Value;
        var listing = options.FakeListing is { } fake
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
                /bin/ps -A -o pid=,ppid=,uid= 2>/dev/null | /usr/bin/awk -v self="$$" -v host={{host}} -v uid={{uid}} -v reg="$reg" -v worker="${worker:-}" -v targets="$*" '
                    function under(p, root,   n) { n = 0; while (p != root && (p in up) && n++ < 100000) p = up[p]; return p == root }
                    { up[$1] = $2; owner[$1] = $3 }
                    END {
                        nt = split(targets, t, " ")
                        for (i = 1; i <= nt; i++) {
                            x = t[i]
                            if (x ~ /^-[0-9]+$/) print ((worker != "" && substr(x, 2) == worker) ? "kill" : "refuse"), x
                            else if (x !~ /^[0-9]+$/) print "refuse", x
                            else if (x == self || under(x, self) || index(reg, " " x " ")) print "kill", x
                            else if (!(x in up) || owner[x] != uid || under(x, host)) print "skip", x
                            else print "refuse", x
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
            var cases = string.Concat(identity.UidsByName.Select(kv => $"        '-u {kv.Key}') echo '{kv.Value}' ;;\n"));
            var fakeId = "id() {\n    case \"$*\" in\n"
                + $"        -u) echo '{identity.Uid}' ;;\n"
                + $"        -un) echo '{identity.User}' ;;\n"
                + cases
                + "        *) echo \"id: $*: no such user\" >&2; return 1 ;;\n    esac\n}\n";
            copy = copy
                .Replace("\nset -eu\n", "\nset -eu\n" + fakeId)
                .Replace("\nsandbox_user=_factory\n", $"\nsandbox_user={identity.User}\n");
        }
        if (options.Mutate is { } mutate)
        {
            copy = mutate(copy);
        }
        return copy;
    }

    /// <summary>
    /// The copy is safe to run: the seam is in, and outside it nothing can signal or list the user's processes — no
    /// <c>kill</c>/<c>pkill</c>/<c>killall</c> command and no <c>ps -U</c>/<c>-u</c> enumeration anywhere in its code.
    /// </summary>
    public static void AssertSafe(string copy)
    {
        var start = copy.IndexOf(SeamStart, StringComparison.Ordinal);
        var end = copy.IndexOf(SeamEnd, StringComparison.Ordinal);
        if (start < 0 || end < start || Count(copy, SeamStart) != 1 || Count(copy, SeamEnd) != 1)
        {
            throw new InvalidOperationException("helper copy has no (single) SafeHelper seam");
        }
        var outside = copy[..start] + copy[(end + SeamEnd.Length)..];
        var code = string.Join('\n', outside.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Select(l => Regex.Replace(l, @"\s#\s.*$", "")));
        if (Regex.Match(code, @"(?<![\w-])(kill|pkill|killall)(?![\w-])") is { Success: true } signal)
        {
            throw new InvalidOperationException($"helper copy signals outside the seam: '{signal.Value}' in\n{code}");
        }
        if (Regex.IsMatch(code, @"\bps\b[^\n|;]*\s-[A-Za-z]*[Uu]\b") || code.Contains(ListPrimitive, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("helper copy lists processes by uid outside the seam");
        }
        if (!code.Contains("list_uid_procs() {", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("helper copy has no replacement process listing");
        }
    }

    private static int Count(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;
}
