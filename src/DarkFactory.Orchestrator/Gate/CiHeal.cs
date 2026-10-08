using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Why a red CI run on a head commit is or is not the PR's to fix (sc-25383), stored as the <c>ci-failure</c> checkpoint:
/// the checks a CI fixer is given (<see cref="Fixable"/>, by name) and every failure that is not the PR's
/// (<see cref="NotThePrs"/>, with why), and the cancelled checks a code failure excuses (<see cref="Cancelled"/>, by name:
/// e.g. a matrix's fail-fast cancelling its other legs when one fails). Facts only — names and conclusions from GitHub's
/// executed results (E5) — never log text, which can carry anything the code under test printed (E4).
/// </summary>
public sealed record CiTriage(
    [property: JsonPropertyName("sha")] string HeadSha,
    [property: JsonPropertyName("base")] string BaseSha,
    [property: JsonPropertyName("fixable")] IReadOnlyList<string> Fixable,
    [property: JsonPropertyName("not_the_prs")] IReadOnlyList<string> NotThePrs,
    [property: JsonPropertyName("cancelled")] IReadOnlyList<string>? Cancelled = null)
{
    /// <summary>
    /// The checks the fixed commit's CI must run and pass before the item goes on (<see cref="CiHeal.Unreported"/>): those
    /// the fixer was given and those cancelled alongside them.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> Expected => Fixable.Concat(Cancelled ?? []);

    /// <summary>A CI fixer may work on it: at least one failure is the PR's, and every failure is.</summary>
    [JsonIgnore]
    public bool Healable => Fixable.Count > 0 && NotThePrs.Count == 0;

    public string ToDetail() => JsonSerializer.Serialize(this);

    /// <summary>The triage a ledger detail holds, or null when it is not one.</summary>
    public static CiTriage? FromDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CiTriage>(detail) is { HeadSha: not null } t
                ? t with { Fixable = t.Fixable ?? [], NotThePrs = t.NotThePrs ?? [], Cancelled = t.Cancelled ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One failing check the CI fixer is given, with its cleaned, redacted and bounded log excerpt.</summary>
public sealed record CiFailureLog(string Check, string? Conclusion, string Excerpt);

/// <summary>
/// The CI self-heal rules (sc-25383; deterministic, E1/E2: no model decides any of them): which red CI a fixer may work on,
/// what of a failing job's log reaches the fixer, and which review roles judge the fixer's push again.
/// </summary>
public static class CiHeal
{
    /// <summary>
    /// Conclusions that mean the code under test failed: a fixer can act on them. Anything else that does not pass —
    /// <c>cancelled</c>, <c>stale</c>, <c>action_required</c>, <c>startup_failure</c>, no conclusion — is CI itself, not the PR.
    /// </summary>
    public static readonly IReadOnlySet<string> CodeFailures = new HashSet<string>(StringComparer.Ordinal) { "failure", "timed_out" };

    /// <summary>At most this many failing checks get a log excerpt in the fixer's prompt (the rest are named only).</summary>
    public const int MaxLoggedChecks = 4;

    /// <summary>Each log excerpt is at most this many characters.</summary>
    public const int MaxExcerptChars = 6000;

    /// <summary>Whether every check run, commit status and counted check suite (<see cref="Ci.CountedSuites"/>) of a commit has finished.</summary>
    public static bool Finished(CiFacts facts) => facts.Checks.All(c => c.Completed) && Ci.CountedSuites(facts).All(s => s.Completed);

    /// <summary>
    /// Triage of a commit whose CI failed (read in full and finished): each failing check run or commit status is the PR's
    /// (<see cref="CiTriage.Fixable"/>) when it failed with a code failure (<see cref="CodeFailures"/>) and the same check is
    /// not red on the PR's base commit too; otherwise it is not the PR's, with why — red on the base too (a fixer of this PR
    /// cannot make it pass), or a conclusion that is CI's own (cancelled, stale, a workflow that could not start, …). A
    /// counted check suite that finished with such a conclusion is not the PR's either. Except <c>cancelled</c> when the PR has
    /// a code failure to fix: a matrix's fail-fast (GitHub's default) cancels the other legs when one fails, so a cancelled
    /// check or suite next to the PR's failure is that failure's consequence, not CI's own fault — it is recorded
    /// (<see cref="CiTriage.Cancelled"/>, checks by name) and must then run and pass on the fixed commit
    /// (<see cref="Unreported"/>, and CI green needs every check and suite to pass). Every other conclusion of CI's own
    /// (stale, action_required, startup_failure, none) still escalates, and a cancelled check with no code failure next to it
    /// does too. <paramref name="baseCi"/> null (the base's CI could not be read) means no failure is known there.
    /// </summary>
    public static CiTriage Triage(CiFacts head, CiFacts? baseCi, string baseSha)
    {
        var redOnBase = (baseCi?.Checks ?? []).Where(c => c.Completed && !Ci.Passes(c.Conclusion)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var failing = head.Checks.Where(c => c.Completed && !Ci.Passes(c.Conclusion)).ToList();
        var fixable = failing.Where(c => CodeFailures.Contains(c.Conclusion ?? "") && !redOnBase.Contains(c.Name)).Select(c => c.Name).Distinct().ToList();
        // A cancellation is excused only next to a failure a fixer can act on.
        var excuse = fixable.Count > 0;
        var (notThePrs, cancelled) = (new List<string>(), new List<string>());
        foreach (var check in failing)
        {
            var conclusion = check.Conclusion ?? "no conclusion";
            if (excuse && check.Conclusion == Cancelled)
            {
                if (!cancelled.Contains(check.Name))
                {
                    cancelled.Add(check.Name);
                }
            }
            else if (!CodeFailures.Contains(check.Conclusion ?? ""))
            {
                notThePrs.Add($"{check.Name} ({conclusion}): CI did not run it to a result, so no code change can fix it");
            }
            else if (redOnBase.Contains(check.Name))
            {
                notThePrs.Add($"{check.Name} ({conclusion}): also red on the base {Ci.Short(baseSha)}, so it is not this PR's failure");
            }
        }
        foreach (var suite in Ci.CountedSuites(head).Where(s => s.Completed && !Ci.Passes(s.Conclusion) && !CodeFailures.Contains(s.Conclusion ?? "")
            && !(excuse && s.Conclusion == Cancelled)))
        {
            notThePrs.Add($"{suite.App} check suite ({suite.Conclusion ?? "no conclusion"}): CI did not run it to a result, so no code change can fix it");
        }
        if (fixable.Count == 0 && notThePrs.Count == 0)
        {
            notThePrs.Add("CI failed with no failing check to fix");
        }
        return new CiTriage(head.HeadSha, baseSha, fixable, notThePrs, cancelled);
    }

    private const string Cancelled = "cancelled";

    /// <summary>
    /// Of the checks every CI triage of the item named (<see cref="CiTriage.Expected"/>: the checks fixers were given and the
    /// ones cancelled alongside them), those with no run at all on <paramref name="facts"/>' commit. CI green only means every
    /// check that reported passed; a check a fix round depends on that never reported has not passed, so CI waits for it.
    /// </summary>
    public static IReadOnlyList<string> Unreported(IEnumerable<CiTriage> triages, CiFacts facts) =>
        triages.SelectMany(t => t.Expected).Distinct(StringComparer.Ordinal)
            .Where(name => !facts.Checks.Any(c => c.Name == name))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    private static readonly Regex Timestamp = new(@"^\uFEFF?\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z ", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B-\x1F\x7F]", RegexOptions.CultureInvariant);
    private const string Redacted = "[redacted]";

    /// <summary>Patterns of credentials a log can carry; each match (or the value part, for key=value forms) is replaced.</summary>
    private static readonly IReadOnlyList<Regex> Secrets =
    [
        new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)", RegexOptions.CultureInvariant),
        new(@"\b(gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})", RegexOptions.CultureInvariant),
        new(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant),
        new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant),
        new(@"\bxox[abposr]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant),
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant),
    ];

    /// <summary>
    /// key=value / key: value forms (env vars, JSON, YAML, headers, connection strings) whose key is an identifier ending in a
    /// credential word — <c>GITHUB_TOKEN=</c>, <c>AWS_SECRET_ACCESS_KEY=</c>, <c>DB_PASSWORD=</c>, <c>"api_key": </c>,
    /// <c>X-Router-Key: </c>, <c>Password=…;</c> (an Authorization header's scheme is kept). The key must be followed by
    /// <c>:</c> or <c>=</c>, so prose such as "Unexpected token ';'" is left alone.
    /// </summary>
    private static readonly Regex KeyValue = new(
        @"(?<key>(?<![A-Za-z0-9])[A-Za-z0-9_.\-]*?(authorization|token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|private[_-]?key|[_-]key|credentials?)[""']?\s*[:=]\s*[""']?\s*((bearer|basic|token)\s+)?)(?<value>[^\s""',;]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A command-line flag naming a credential, its value after whitespace (<c>--password hunter2</c>, <c>--with-token abc</c>).</summary>
    private static readonly Regex FlagValue = new(
        @"(?<key>(?<![A-Za-z0-9\-])--?[A-Za-z0-9\-]*(password|passwd|pwd|token|secret|api-key|access-key|private-key)\s+[""']?)(?<value>[^\s""',;]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A bearer credential anywhere ("Bearer &lt;value&gt;").</summary>
    private static readonly Regex BearerValue = new(@"(?<key>\bbearer\s+)(?<value>[A-Za-z0-9._~+/\-]{8,}=*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UrlCredentials = new(@"(?<scheme>[a-z][a-z0-9+.\-]*://)[^\s/@:]+(:[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Replaces credentials in <paramref name="text"/> (private keys, GitHub/Anthropic/OpenAI-style/AWS/Slack tokens, JWTs,
    /// credentials in URLs, and the value of key=value or <c>Bearer</c> forms naming a token, key, secret or password) with
    /// <c>[redacted]</c>, so nothing a log printed of a secret reaches a prompt (E4). GitHub itself masks the repository's
    /// secrets as <c>***</c>; this covers what it does not know about.
    /// </summary>
    public static string Redact(string text)
    {
        foreach (var pattern in Secrets)
        {
            text = pattern.Replace(text, Redacted);
        }
        text = UrlCredentials.Replace(text, m => $"{m.Groups["scheme"].Value}{Redacted}@");
        foreach (var pattern in new[] { KeyValue, FlagValue })
        {
            text = pattern.Replace(text, m => m.Groups["value"].Value.StartsWith(Redacted, StringComparison.Ordinal) ? m.Value : $"{m.Groups["key"].Value}{Redacted}");
        }
        return BearerValue.Replace(text, m => $"{m.Groups["key"].Value}{Redacted}");
    }

    /// <summary>
    /// The part of a failing job's log a fixer gets: ANSI escapes, control characters and GitHub's per-line timestamps
    /// removed, credentials redacted (<see cref="Redact"/>), then at most <paramref name="maxChars"/> characters ending at the
    /// last <c>##[error]</c> line (where Actions reports the failing step; cleanup steps after it are dropped), or at the
    /// end of the log when it has none, starting on a line boundary and saying how much was left out.
    /// </summary>
    public static string Excerpt(string log, int maxChars = MaxExcerptChars)
    {
        var text = Timestamp.Replace(ControlChars.Replace(Ansi.Replace(log.Replace("\r\n", "\n"), ""), ""), "");
        var lastError = text.LastIndexOf("##[error]", StringComparison.Ordinal);
        var end = lastError < 0 ? text.Length : text.IndexOf('\n', lastError) is var nl and >= 0 ? nl : text.Length;
        // Redacted before cutting, so a cut cannot split a secret into a part no pattern matches.
        var kept = Redact(text[..end]).TrimEnd();
        var dropped = text.Length - end;
        if (kept.Length > maxChars)
        {
            var cut = kept.Length - maxChars;
            var lineStart = kept.IndexOf('\n', cut);
            cut = lineStart >= 0 && lineStart < kept.Length - 1 ? lineStart + 1 : cut;
            kept = $"[{cut} earlier characters omitted]\n{kept[cut..]}";
        }
        return dropped > 0 && lastError >= 0 ? $"{kept}\n[{dropped} characters after the last error omitted]" : kept;
    }

    /// <summary>
    /// The review roles that judge a CI fixer's push again rather than carrying their review of the commit it fixed (whose
    /// verdict passed): a role re-reviews when the fix changed a file in its scope. Correctness and spec conformance judge
    /// every file of the change, so any file the fix touches is theirs (a fixer can make CI green by changing or removing
    /// behaviour, which either may catch). Security judges the paths that call the security review in, so it re-reviews only
    /// when the fix itself touches such a path (by the base policy's tiers or the code floor,
    /// <see cref="GatePolicy.SecurityReviewReasons"/>). <paramref name="fixDiff"/>: the diff of the pushed commit against the
    /// one it fixed.
    /// </summary>
    public static IReadOnlySet<string> TouchedRoles(GatePolicy policy, string fixDiff)
    {
        var change = policy.Classify(fixDiff);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        // An empty diff (the fix left the tree as it was) touches no role, so every review carries; an unreadable diff never
        // gets here (GetDiffAsync throws).
        if (!string.IsNullOrWhiteSpace(fixDiff))
        {
            roles.Add(ReviewRoles.Correctness);
            roles.Add(ReviewRoles.SpecConformance);
        }
        if (policy.SecurityReviewReasons(change).Count > 0)
        {
            roles.Add(ReviewRoles.Security);
        }
        return roles;
    }

    /// <summary>
    /// The reviews of <paramref name="previous"/> (the verdict on the commit a CI fix round fixed) carried into the pushed
    /// commit's verdict: those <see cref="FixLoop.Carried"/> would carry, minus every role the fix touched
    /// (<see cref="TouchedRoles"/>).
    /// </summary>
    public static IReadOnlyList<RoleReview> Carried(ReviewVerdict previous, IReadOnlyList<string> required, GatePolicy policy, string fixDiff)
    {
        var touched = TouchedRoles(policy, fixDiff);
        return FixLoop.Carried(previous, required).Where(r => !touched.Contains(r.Role)).ToList();
    }

    /// <summary>The failing checks as a list, one per line, for an escalation comment.</summary>
    public static string Describe(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append("- ").AppendLine(line);
        }
        return text.ToString().TrimEnd();
    }
}
