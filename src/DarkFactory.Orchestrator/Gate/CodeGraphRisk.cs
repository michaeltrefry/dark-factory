using System.Globalization;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>CodeGraph's blast radius of a file (<c>analyze_impact_file</c>'s <c>blastRadius</c>), least to worst.</summary>
public enum BlastRadius
{
    None,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>The blast radius names CodeGraph and <c>factory/gate.yaml</c> use: <c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>critical</c>.</summary>
public static class BlastRadii
{
    public static string Name(BlastRadius radius) => radius.ToString().ToLowerInvariant();

    /// <summary>Parses a lowercase name exactly (no numbers, no other case): <c>factory/gate.yaml</c>'s own spelling.</summary>
    public static bool TryParse(string? text, out BlastRadius radius) => TryParse(text, StringComparison.Ordinal, out radius);

    /// <summary>
    /// Parses CodeGraph's <c>blastRadius</c> (sc-26254): a name in any case (<c>High</c> is <c>high</c>); no numbers, no other word, no
    /// surrounding space. Anything else from a file CodeGraph has data for is a misread (<see cref="UnrecognisedBlastRadius"/>).
    /// </summary>
    public static bool TryParseAnswer(string? text, out BlastRadius radius) => TryParse(text, StringComparison.OrdinalIgnoreCase, out radius);

    private static bool TryParse(string? text, StringComparison comparison, out BlastRadius radius)
    {
        foreach (var r in Enum.GetValues<BlastRadius>())
        {
            if (string.Equals(text, Name(r), comparison))
            {
                radius = r;
                return true;
            }
        }
        radius = default;
        return false;
    }
}

/// <summary>
/// One <c>analyze_impact_file</c> answer (CodeGraph C7, sc-25735): whether it is an error, its text, the commit it names
/// (<c>structuredContent.commitSha</c>, <see cref="CodeGraphMcpClient.Commit"/>), the typed not-indexed answer of a SHA with no ready
/// overlay (C4), an error answer's code (<c>structuredContent.error.code</c>), and the tool's own <c>found</c>, <c>blastRadius</c> and
/// <c>path</c> (the file it answered about, which must be the one asked about), and C3's (sc-26137) <c>languageIndexed</c> and
/// <c>language</c> (<see cref="LanguageCoverage"/>; both null in an answer of a CodeGraph that predates C3).
/// <c>Source</c>: CommitAnswer's <c>source</c> (<c>overlay</c> or <c>default-branch</c>; null when not answered).
/// </summary>
public sealed record ImpactFileAnswer(bool IsError, string Text, string? CommitSha, CodeGraphNotIndexed? NotIndexed, string? ErrorCode,
    bool? Found, string? BlastRadius, string? Path = null, bool? LanguageIndexed = null, string? Language = null, string? Source = null);

/// <summary>
/// One <c>file_hotspot</c> answer (C7, michaeltrefry/CodeGraph PR #76): whether it is an error, its text, the commit its metrics were
/// computed at (<c>commitSha</c>: null when <c>found</c> is false, and null for metrics computed before CodeGraph recorded the commit),
/// its <c>source</c> (<c>default-branch</c> or <c>overlay</c>), an error answer's code, the tool's own <c>found</c>, <c>riskScore</c> and
/// <c>path</c> (the file it answered about, which must be the one asked about), and <c>indexCommitSha</c> — the current default-branch
/// index commit, given whether or not the file has metrics — and C3's (sc-26137) <c>languageIndexed</c> and <c>language</c>
/// (<see cref="LanguageCoverage"/>).
/// </summary>
public sealed record HotspotAnswer(bool IsError, string Text, string? CommitSha, string? Source, string? ErrorCode, bool? Found, double? RiskScore,
    string? Path = null, string? IndexCommitSha = null, bool? LanguageIndexed = null, string? Language = null);

/// <summary>
/// A touched file whose language CodeGraph does not index (sc-26133, P5 S7): its blast radius and hotspot are unknown, not none, so
/// <c>risk-threshold</c> records it as having no CodeGraph data (sc-26254) and decides on the change's other inputs.
/// <see cref="Language"/>: the language's name (CodeGraph's, else the factory's by extension), or null when neither names one.
/// <see cref="Flagged"/>: CodeGraph said so (<c>languageIndexed: false</c>); false — CodeGraph answered no flag (it predates C3) and the
/// factory knows that language was not indexed before C3 (<see cref="LanguageCoverage.UnindexedBeforeC3"/>).
/// <see cref="Missing"/>: when CodeGraph still answered data for one of the two inputs (<see cref="CodeGraphRisk.Judged"/> keeps it), the
/// input that is missing (<c>blast radius</c> or <c>hotspot</c>); null — both are.
/// <see cref="ByImpact"/>, <see cref="ByHotspot"/>: which answer said <c>languageIndexed: false</c> — only that tool's own misread of
/// the file is excused by it (<see cref="CodeGraphRisk.Judged"/>, sc-26134).
/// </summary>
public sealed record UnindexedFile(string Path, string? Language, bool Flagged, string? Missing = null, bool ByImpact = false, bool ByHotspot = false)
{
    /// <summary>How the gate's row names it: the file and its language.</summary>
    public override string ToString() =>
        $"{Path} ({Language ?? "language unknown"}{(Flagged ? "" : "; CodeGraph answered no languageIndexed flag (before C3)")})";

    /// <summary>Why the file has no CodeGraph data, as <see cref="NoCodeGraphData.Reason"/> says it.</summary>
    public string Reason => (Missing is null ? "" : $"{Missing}: ") +
        $"its language ({Language ?? "unknown"}) is not indexed by CodeGraph{(Flagged ? "" : " (CodeGraph answered no languageIndexed flag: before C3)")}";
}

/// <summary>
/// A touched file for which CodeGraph gave the gate no input (sc-26254, E5 as amended), and why — only the amendment's causes:
/// CodeGraph not configured, unreachable or refusing (a call that cannot be made, an HTTP 4xx such as 403 <c>tool_not_entitled</c>, the
/// whole read cut off at <c>Gate:CiTimeoutMinutes</c>), the repository not indexed, a language CodeGraph does not index, or C4's typed
/// <c>notIndexed</c> answer whose default-branch read then finds nothing either (itself not indexed, or unreachable). <c>risk-threshold</c>
/// then decides on the file's other inputs; the reason is recorded. An answer a reachable CodeGraph gives that cannot be used is a misread
/// (<see cref="CodeGraphMisread"/>), never this.
/// </summary>
public sealed record NoCodeGraphData(string Path, string Reason);

/// <summary>
/// A touched file a reachable CodeGraph misanswered (sc-26134): an answer the gate cannot use that is none of
/// <see cref="NoCodeGraphData"/>'s causes — an error answer that is not C4's typed <c>notIndexed</c>, an answer for another commit or
/// another file, one without <c>found</c>, a default-branch answer naming no commit or from an overlay, a hotspot without a usable score
/// or commit — once the default-branch read of the blast radius (tried when the head's answer is unusable) gave nothing usable either; and
/// every file of a read made for another head. Not missing data: it blocks <c>risk-threshold</c> (the item escalates), as
/// <see cref="UnrecognisedBlastRadius"/> does — except a misread of the hotspot (<see cref="Hotspot"/>: <c>file_hotspot</c>'s answer, or a
/// hotspot the inputs leave out), which blocks only where the policy sets <c>max_hotspot</c>: without a hotspot limit the hotspot is not
/// judged, and the misread is only recorded in the gate row.
/// </summary>
public sealed record CodeGraphMisread(string Path, string Reason, bool Hotspot = false);

/// <summary>
/// Whether CodeGraph indexes a touched file's language (sc-26133, P5 S7; the CodeGraph side is C3, sc-26137). The contract: the
/// structured content of <c>analyze_impact_file</c> and <c>file_hotspot</c> carries <c>languageIndexed</c> (boolean: false only for a
/// source file of a language CodeGraph does not index; true for an indexed language and for a file that is not code) and
/// <c>language</c> (CodeGraph's capitalised name of the file's language — <c>Swift</c>, <c>C#</c>, <c>C++</c> — or null). Since
/// sc-26254 (owner decision 2026-10-15: the factory works with or without CodeGraph) such a file is recorded as having no CodeGraph
/// data, never blocking. Rule (<see cref="Of"/>), per file, after both answers: <c>languageIndexed: false</c> in either → unindexed;
/// <c>true</c> in either and <c>false</c> in neither → indexed; neither carries the flag (a CodeGraph that predates C3) → unindexed
/// only when the file's extension is one of <see cref="UnindexedBeforeC3"/> and CodeGraph had no graph data for it
/// (<c>analyze_impact_file</c>'s <c>found</c> not true: a CodeGraph with graph data for a file indexes its language). CodeGraph's
/// <c>language</c> is shown only when it is an inert name (<see cref="Name"/>); otherwise the factory's name by extension stands in.
/// </summary>
public static class LanguageCoverage
{
    /// <summary>
    /// The code languages, by extension (case-insensitive), CodeGraph's main did not index before C3 (checked against its source,
    /// 2026-10-14: Swift is indexed only on a feature branch). <c>.m</c> is also MATLAB's: such a file is recorded with no CodeGraph
    /// data too until C3 says otherwise (sc-26254).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UnindexedBeforeC3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".swift"] = "Swift",
        [".m"] = "Objective-C",
        [".mm"] = "Objective-C++",
        [".kt"] = "Kotlin",
        [".kts"] = "Kotlin",
        [".scala"] = "Scala",
        [".dart"] = "Dart",
    };

    /// <summary>The longest language name shown.</summary>
    public const int MaxNameLength = 60;

    /// <summary>The language <see cref="UnindexedBeforeC3"/> names for <paramref name="path"/>'s extension, or null.</summary>
    public static string? KnownUnindexed(string path) =>
        UnindexedBeforeC3.TryGetValue(System.IO.Path.GetExtension(path), out var language) ? language : null;

    /// <summary>
    /// CodeGraph's <c>language</c> as the gate may show it (E3: it reaches gate reasons, rows and escalation comments): trimmed, at
    /// most <see cref="MaxNameLength"/> characters, and only ASCII letters, digits, <c>+ # . _ -</c> and spaces; anything else (a line
    /// break, a backtick, markup) → null, so the factory's own name stands in.
    /// </summary>
    public static string? Name(string? language)
    {
        var name = language?.Trim();
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '#' or '.' or '_' or ' ' or '-'))
        {
            return null;
        }
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a file whose language CodeGraph does not index, from both answers' fields (a hotspot answer
    /// not read: null, null); null: it is not.
    /// </summary>
    public static UnindexedFile? Of(string path, bool? impactIndexed, string? impactLanguage, bool? hotspotIndexed, string? hotspotLanguage,
        bool? found)
    {
        if (impactIndexed == false || hotspotIndexed == false)
        {
            var language = (impactIndexed == false ? Name(impactLanguage) : null) ?? (hotspotIndexed == false ? Name(hotspotLanguage) : null)
                ?? KnownUnindexed(path);
            return new UnindexedFile(path, language, Flagged: true, ByImpact: impactIndexed == false, ByHotspot: hotspotIndexed == false);
        }
        if (impactIndexed == true || hotspotIndexed == true)
        {
            return null;
        }
        return found != true && KnownUnindexed(path) is { } known ? new UnindexedFile(path, known, Flagged: false) : null;
    }

    /// <summary>
    /// Adds <paramref name="file"/> to <paramref name="files"/>, one entry per path: CodeGraph's word (<see cref="UnindexedFile.Flagged"/>)
    /// replaces the factory's inference for the same path, never the other way round.
    /// </summary>
    public static void Add(List<UnindexedFile> files, UnindexedFile? file)
    {
        if (file is null)
        {
            return;
        }
        var at = files.FindIndex(u => u.Path == file.Path);
        if (at < 0)
        {
            files.Add(file);
        }
        else if (file.Flagged && !files[at].Flagged)
        {
            files[at] = file;
        }
    }
}

/// <summary>
/// CodeGraph's per-file risk tools, owner-side (sc-25709): <c>analyze_impact_file</c> and <c>file_hotspot</c>. Called only by the
/// orchestrator for the merge gate's <c>risk-threshold</c>; they are on no allowlist (<see cref="ReviewTools.Names"/>,
/// <see cref="McpServers"/>, any <see cref="McpProfile"/>), so no reviewer, session or proxy grant can call them. Each throws when
/// CodeGraph cannot be reached (<see cref="McpHttpException"/> for an HTTP refusal, e.g. 403 <c>tool_not_entitled</c>).
/// </summary>
public interface ICodeGraphRisk : ICodeGraphOverlays
{
    /// <summary>
    /// <c>analyze_impact_file(repo, path, sha, depth)</c>: the file's blast radius at <paramref name="sha"/>; null — no <c>sha</c> sent, the
    /// default-branch index's answer (sc-26254: the gate's read when no overlay of the head is ready).
    /// </summary>
    Task<ImpactFileAnswer> AnalyzeImpactFileAsync(string project, string path, string? sha, int depth, CancellationToken ct);

    /// <summary><c>file_hotspot(repo, path)</c>: the file's hotspot metrics on the default branch.</summary>
    Task<HotspotAnswer> FileHotspotAsync(string project, string path, CancellationToken ct);
}

/// <summary>
/// One touched file's blast radius at the head (<see cref="Found"/> false: CodeGraph has no data for it, counted as none).
/// <see cref="DefaultBranchCommit"/> (sc-26254): read from the default-branch index at that commit, because no overlay of the head was
/// ready — labelled "on the default branch as of &lt;sha&gt;, not the PR head" and held to the same limits; null: read at the head.
/// </summary>
public sealed record FileBlastRadius(string Path, BlastRadius Radius, bool Found, string? DefaultBranchCommit = null)
{
    /// <summary>How the gate names where the radius was read, for <paramref name="head"/>.</summary>
    public string Where(string head) =>
        DefaultBranchCommit is { } c ? $"on the default branch as of {c}, not the PR head" : $"at {head}";
}

/// <summary>
/// A blast radius CodeGraph answered for a file it has data for (<c>found: true</c>) that the gate does not recognise (sc-26254): a
/// misread, not missing data — it blocks <c>risk-threshold</c>. <see cref="Value"/> is CodeGraph's text, shown only when inert.
/// </summary>
public sealed record UnrecognisedBlastRadius(string Path, string? Value)
{
    /// <summary>The blocking reason.</summary>
    public string Reason => $"risk threshold: CodeGraph answered an unrecognised blast radius {Shown} for {Path}";

    /// <summary>The value as the gate may show it (E3): a short plain word in a code span, else said not shown.</summary>
    public string Shown => Value is { Length: > 0 and <= 40 } v && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
        ? $"`{v}`" : Value is null ? "(none)" : "(not shown: not a plain word)";
}

/// <summary>
/// One touched file's hotspot score — CodeGraph's <c>riskScore</c> is "the hotspot score" — on the default branch, recorded at
/// <see cref="CommitSha"/>: the commit the metrics were computed at, or the default-branch index commit when the file has no metrics
/// (<see cref="Found"/> false: counted as 0) or CodeGraph did not record the metrics' commit (<see cref="VitalsCommitUnknown"/>: the
/// score is used, labelled "vitals commit unknown; index at &lt;sha&gt;"). <see cref="IndexCommitSha"/>: the default-branch index commit
/// CodeGraph named (null: the same as <see cref="CommitSha"/>), which keys <see cref="HotspotCache"/>. <see cref="LanguageIndexed"/>
/// and <see cref="Language"/>: the answer's C3 fields (sc-26137; <see cref="LanguageCoverage"/>), kept with a cached score.
/// </summary>
public sealed record FileHotspot(string Path, double Score, bool Found, string CommitSha, string? IndexCommitSha = null, bool VitalsCommitUnknown = false,
    bool? LanguageIndexed = null, string? Language = null)
{
    /// <summary>The default-branch index commit the score was read against.</summary>
    public string IndexCommit => IndexCommitSha ?? CommitSha;

    /// <summary>How the <c>gate</c> row names the commit the score is recorded at.</summary>
    public string CommitLabel => VitalsCommitUnknown ? $"vitals commit unknown; index at {CommitSha}" : $"at {CommitSha}";
}

/// <summary>
/// CodeGraph's inputs to <c>risk-threshold</c> for one evaluation (sc-25709), read fresh: each touched file's blast radius for exactly
/// <see cref="HeadSha"/> (E3) and its hotspot score on the default branch, each recorded with the commit CodeGraph named (the
/// default-branch exception of E3). <see cref="NoData"/> (sc-26254): the touched files CodeGraph gave no usable input for, and why
/// (<see cref="NoCodeGraphData"/>: CodeGraph unreachable or refusing, the repository or the file's language not indexed, C4's
/// <c>notIndexed</c> with nothing on the default branch either; a file may be named once per tool) — the gate decides on the file's
/// other inputs and records the reason.
/// <see cref="OverlayUnusable"/>: CodeGraph answered that it has no usable overlay of the head (C4's <c>notIndexed</c>, e.g. a ready
/// overlay gone stale once the default branch moved past its base), so a recorded ready overlay must not be reused: the gate requests
/// the overlay again once. <see cref="Unindexed"/>: the touched files whose language CodeGraph does not index
/// (<see cref="LanguageCoverage"/>, sc-26133): their blast radius and hotspot are not used, and each is recorded as having no
/// CodeGraph data (<see cref="Judged"/>). <see cref="Unrecognised"/>: blast radii CodeGraph answered for a file it has data for that the gate does not
/// recognise — they block (sc-26254: a misread is not missing data). <see cref="Misread"/>: the files a reachable CodeGraph misanswered
/// (<see cref="CodeGraphMisread"/>, sc-26134) — they block too.
/// </summary>
public sealed record CodeGraphRisk(string HeadSha, IReadOnlyList<FileBlastRadius> BlastRadius, IReadOnlyList<FileHotspot> Hotspots,
    IReadOnlyList<NoCodeGraphData> NoData, bool OverlayUnusable = false, IReadOnlyList<UnindexedFile>? Unindexed = null,
    IReadOnlyList<UnrecognisedBlastRadius>? Unrecognised = null, IReadOnlyList<CodeGraphMisread>? Misread = null)
{
    /// <summary>The files CodeGraph misanswered (none when <see cref="Misread"/> is null).</summary>
    public IReadOnlyList<CodeGraphMisread> Misreads => Misread ?? [];

    /// <summary>Every one of <paramref name="files"/> misread at <paramref name="headSha"/> for the one <paramref name="reason"/>.</summary>
    public static CodeGraphRisk AllMisread(string headSha, IEnumerable<string> files, string reason) =>
        new(headSha, [], [], [], Misread: files.Select(f => new CodeGraphMisread(f, reason)).ToList());

    /// <summary>
    /// The blocking reasons of <see cref="Misreads"/>, one per reason — <c>risk threshold: CodeGraph misanswered (&lt;reason&gt;):
    /// &lt;files&gt;</c> — each reason's files, and the reasons, capped at <see cref="MaxListed"/> with how many more. A hotspot misread
    /// (<see cref="CodeGraphMisread.Hotspot"/>) blocks only when <paramref name="hotspotJudged"/> (the policy sets <c>max_hotspot</c>).
    /// </summary>
    public IReadOnlyList<string> MisreadReasons(bool hotspotJudged)
    {
        var groups = Misreads.Where(m => hotspotJudged || !m.Hotspot).GroupBy(m => m.Reason, StringComparer.Ordinal).ToList();
        var reasons = groups.Take(MaxListed)
            .Select(g => $"risk threshold: CodeGraph misanswered ({g.Key}): {Listed(g.Select(m => m.Path).ToList(), _ => false, p => p, ", ")}")
            .ToList();
        if (groups.Count > MaxListed)
        {
            reasons.Add($"risk threshold: CodeGraph misanswered … {groups.Count - MaxListed} more reason(s)");
        }
        return reasons;
    }
    /// <summary>The blast radii CodeGraph answered that the gate does not recognise (none when <see cref="Unrecognised"/> is null).</summary>
    public IReadOnlyList<UnrecognisedBlastRadius> UnrecognisedRadii => Unrecognised ?? [];

    /// <summary>The touched files whose language CodeGraph does not index (none when <see cref="Unindexed"/> is null).</summary>
    public IReadOnlyList<UnindexedFile> UnindexedFiles => Unindexed ?? [];

    /// <summary>Every one of <paramref name="files"/> with no CodeGraph data at <paramref name="headSha"/>, for the one <paramref name="reason"/>.</summary>
    public static CodeGraphRisk None(string headSha, IEnumerable<string> files, string reason) =>
        new(headSha, [], [], files.Select(f => new NoCodeGraphData(f, reason)).ToList());

    /// <summary>
    /// Refreshing an overlay cannot retract observations already made in this evaluation. Keep the worst usable radius/score per
    /// file, and every blocking misread; only unanswered inputs can become unavailable. Both reads must describe the same PR head.
    /// </summary>
    public CodeGraphRisk PreserveObservations(CodeGraphRisk previous)
    {
        if (HeadSha != previous.HeadSha)
        {
            throw new ArgumentException("CodeGraph observations belong to different PR heads", nameof(previous));
        }
        var blast = BlastRadius.Concat(previous.BlastRadius).GroupBy(b => b.Path, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(b => b.Radius).ThenByDescending(b => b.Found).First()).ToList();
        var hot = Hotspots.Concat(previous.Hotspots).GroupBy(h => h.Path, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Score).ThenByDescending(h => h.Found).First()).ToList();
        var misread = Misreads.Concat(previous.Misreads).Distinct().ToList();
        var unrecognised = UnrecognisedRadii.Concat(previous.UnrecognisedRadii).Distinct().ToList();
        // A language flag in one read must not erase a blocking observation in the other. A read's own flag still explains that
        // read's unusable answer when Judged is applied (including an interrupted read's partial observations).
        var unindexed = UnindexedFiles.Where(u => !previous.Misreads.Any(m => m.Path == u.Path)
                && !previous.UnrecognisedRadii.Any(r => r.Path == u.Path))
            .Concat(previous.UnindexedFiles.Where(u => !Misreads.Any(m => m.Path == u.Path)
                && !UnrecognisedRadii.Any(r => r.Path == u.Path))).Distinct().ToList();
        var noData = NoData.Concat(previous.NoData).Distinct().Where(n =>
            !((blast.Any(b => b.Path == n.Path) || unrecognised.Any(r => r.Path == n.Path)
                || misread.Any(m => m.Path == n.Path && !m.Hotspot))
                && (hot.Any(h => h.Path == n.Path) || misread.Any(m => m.Path == n.Path && m.Hotspot)))).ToList();
        return this with { BlastRadius = blast, Hotspots = hot, NoData = noData, Unindexed = unindexed,
            Unrecognised = unrecognised, Misread = misread };
    }

    /// <summary>
    /// What the gate judges, for exactly <paramref name="files"/>: the blast radii and hotspots CodeGraph answered, and every other file
    /// with no CodeGraph data and why (<see cref="NoData"/>); a file the inputs leave out is a misread (<see cref="LeftOut"/>). A file whose language CodeGraph does not
    /// index keeps whatever data CodeGraph did answer for it (<c>found: true</c>: a critical blast radius from one tool still blocks), and
    /// only the missing part is recorded as no data, its language the reason (<see cref="AllNoData"/>).
    /// </summary>
    public CodeGraphRisk Judged(IReadOnlyList<string> files)
    {
        var unindexed = UnindexedFiles.Select(u => u.Path).ToHashSet(StringComparer.Ordinal);
        var blast = BlastRadius.Where(f => !unindexed.Contains(f.Path) || f.Found).ToList();
        var hot = Hotspots.Where(f => !unindexed.Contains(f.Path) || f.Found).ToList();
        var stillUnindexed = new List<UnindexedFile>();
        foreach (var u in UnindexedFiles)
        {
            var hasBlast = blast.Any(b => b.Path == u.Path);
            var hasHot = hot.Any(h => h.Path == u.Path);
            if (!hasBlast || !hasHot)
            {
                stillUnindexed.Add(u with { Missing = hasBlast ? "hotspot" : hasHot ? "blast radius" : null });
            }
        }
        // An unindexed file's reason is its language (AllNoData). A tool's own answer saying CodeGraph indexes no code of that language
        // explains that tool's unusable answer about the file — never the other tool's, whose misread stands (sc-26134).
        var noData = NoData.Where(n => !unindexed.Contains(n.Path)).ToList();
        var misread = Misreads.Where(m => !UnindexedFiles.Any(u => u.Path == m.Path && (m.Hotspot ? u.ByHotspot : u.ByImpact))).ToList();
        foreach (var file in files.Where(f => !unindexed.Contains(f) && misread.All(m => m.Path != f) && noData.All(n => n.Path != f)))
        {
            // A touched file the inputs leave out is not missing data — CodeGraph was not asked, or its answer was lost: a misread
            // (sc-26134), as for a read made for another head.
            if (!blast.Any(b => b.Path == file) && !UnrecognisedRadii.Any(u => u.Path == file))
            {
                misread.Add(new CodeGraphMisread(file, LeftOut));
            }
            else if (hot.All(h => h.Path != file))
            {
                misread.Add(new CodeGraphMisread(file, LeftOut, Hotspot: true));
            }
        }
        return this with { BlastRadius = blast, Hotspots = hot, NoData = noData, Unindexed = stillUnindexed, Misread = misread };
    }

    /// <summary>Why a touched file the inputs leave out is a misread (<see cref="Judged"/>).</summary>
    public const string LeftOut = "CodeGraph's inputs leave it out";

    /// <summary>
    /// How many files <see cref="Describe"/> lists beyond those over a limit and the worst and hottest (and how many files with no data
    /// it names per reason, and how many reasons), so one <c>gate</c> row stays well inside the reports' budget
    /// (<see cref="Ledger.LedgerReport.MaxFactsLength"/>) whatever the PR's size: the rest are counted.
    /// </summary>
    public const int MaxListed = 20;

    /// <summary>The file with the worst blast radius (the first such), or null when none was read.</summary>
    public FileBlastRadius? Worst => BlastRadius.OrderByDescending(f => f.Radius).FirstOrDefault();

    /// <summary>The file with the highest hotspot score (the first such), or null when none was read.</summary>
    public FileHotspot? Hottest => Hotspots.OrderByDescending(f => f.Score).FirstOrDefault();

    /// <summary>
    /// Every file with no CodeGraph data and why: <see cref="NoData"/>, and each unindexed file (<see cref="UnindexedFiles"/>) with its
    /// language as the reason.
    /// </summary>
    public IReadOnlyList<NoCodeGraphData> AllNoData => [.. NoData, .. UnindexedFiles.Select(u => new NoCodeGraphData(u.Path, u.Reason))];

    /// <summary>How many distinct files have no CodeGraph data.</summary>
    public int NoDataFiles => AllNoData.Select(n => n.Path).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// The inputs as the <c>gate</c> checkpoint records them: each file's blast radius at the head and each hotspot score with its commit
    /// — every file over <paramref name="limit"/> or <paramref name="maxHotspot"/>, the worst and the hottest, then the first
    /// <see cref="MaxListed"/> others, and how many more there are — and the files with no CodeGraph data (<see cref="NoDataText"/>).
    /// </summary>
    public string Describe(BlastRadius? limit = null, double? maxHotspot = null)
    {
        var worst = Worst;
        var hottest = Hottest;
        string BlastList(IReadOnlyList<FileBlastRadius> files) => Listed(files, f => (limit is { } l && f.Radius > l) || ReferenceEquals(f, worst),
            f => $"{Inert(f.Path)} {BlastRadii.Name(f.Radius)}{(f.Found ? "" : " (no data)")}", ", ");
        var atHead = BlastRadius.Where(f => f.DefaultBranchCommit is null).ToList();
        var onDefault = BlastRadius.Where(f => f.DefaultBranchCommit is not null).GroupBy(f => f.DefaultBranchCommit!)
            .Select(g => $"; codegraph blast radius on the default branch as of {Inert(g.Key)}, not the PR head: {BlastList(g.ToList())}");
        var hot = Hotspots.GroupBy(f => f.CommitLabel).Select(g => $"{Inert(g.Key)}: " + Listed(g.ToList(),
            f => (maxHotspot is { } m && f.Score > m) || ReferenceEquals(f, hottest), f => $"{Inert(f.Path)} {Score(f.Score)}{(f.Found ? "" : " (no data)")}", ", "));
        return $"codegraph blast radius at {Inert(HeadSha)}: {(atHead.Count == 0 ? "none read" : BlastList(atHead))}"
            + string.Concat(onDefault)
            + (UnrecognisedRadii.Count == 0 ? "" : $"; unrecognised blast radius: {string.Join(", ", UnrecognisedRadii.Select(u => $"{Inert(u.Path)} {u.Shown}"))}")
            + (Misreads.Count == 0 ? "" : "; CodeGraph misanswered: " + Listed(Misreads.GroupBy(m => m.Reason, StringComparer.Ordinal).ToList(), _ => false,
                g => $"{Inert(g.Key)}{(maxHotspot is null && g.All(m => m.Hotspot) ? " (hotspot, not judged: no max_hotspot)" : "")}: "
                    + Listed(g.Select(m => m.Path).ToList(), _ => false, Inert, ", "), "; "))
            + $"; hotspot score (riskScore) on the default branch: {(Hotspots.Count == 0 ? "none read" : string.Join("; ", hot))}"
            + (AllNoData.Count == 0 ? "" : $"{NoDataSeparator}{NoDataText}");
    }

    /// <summary>
    /// The one character that separates the files with no CodeGraph data from the rest of a <c>gate</c> row (U+241E, SYMBOL FOR RECORD
    /// SEPARATOR). No other text of the row can hold it: every text <see cref="Describe"/> writes, and every reason of the decision
    /// (<see cref="GateDecision.Detail"/>), goes through <see cref="Inert"/> — so CodeGraph's text, a path or a check name cannot move
    /// where the reports split the row (sc-26254).
    /// </summary>
    public const char Separator = '\u241E';

    /// <summary>What stands between the rest of the <c>gate</c> row and <see cref="NoDataText"/>.</summary>
    public const string NoDataSeparator = " \u241E ";

    /// <summary><paramref name="text"/> with <see cref="Separator"/> replaced, so it cannot be taken for it.</summary>
    public static string Inert(string text) => text.Replace(Separator, '?');

    /// <summary>Where the files with no CodeGraph data start in a <c>gate</c> row (<see cref="Describe"/>), as the reports find them.</summary>
    public const string NoDataMark = NoDataSeparator + NoDataPrefix;

    /// <summary>How a <c>gate</c> row and the reports name a file with no CodeGraph data (sc-26254).</summary>
    public const string NoDataPrefix = "no CodeGraph data: ";

    /// <summary>
    /// The files with no CodeGraph data, by reason — <c>no CodeGraph data: &lt;reason&gt;: &lt;files&gt;</c> — each reason's files, and
    /// the reasons, capped at <see cref="MaxListed"/> with how many more.
    /// </summary>
    public string NoDataText => Listed(AllNoData.GroupBy(n => n.Reason, StringComparer.Ordinal).ToList(), _ => false,
        g => $"{NoDataPrefix}{Inert(g.Key)}: {Listed(g.Select(n => n.Path).ToList(), _ => false, Inert, ", ")}", "; ");

    /// <summary>Every item <paramref name="always"/> picks, the first <see cref="MaxListed"/> of the others (in order), and "… N more".</summary>
    private static string Listed<T>(IReadOnlyList<T> items, Func<T, bool> always, Func<T, string> text, string separator)
    {
        var others = 0;
        var shown = new List<string>();
        foreach (var item in items)
        {
            if (always(item) || others++ < MaxListed)
            {
                shown.Add(text(item));
            }
        }
        var more = others - MaxListed;
        return string.Join(separator, shown) + (more > 0 ? $"{separator}… {more} more" : "");
    }

    public static string Score(double score) => score.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// The hotspot scores one gate wait has read (sc-25709), by file and the default-branch index commit CodeGraph named
/// (<c>indexCommitSha</c>, <see cref="FileHotspot.IndexCommit"/>): one is reused while the head's ready overlay is built on that same
/// commit (its base — the commit the lookup names), so a wait that reads again every poll asks for each file's hotspot once per
/// default-branch index commit.
/// </summary>
public sealed class HotspotCache
{
    private readonly Dictionary<(string Path, string Commit), FileHotspot> _scores = new();

    public bool TryGet(string path, string? commit, out FileHotspot? hotspot)
    {
        hotspot = null;
        return commit is not null && _scores.TryGetValue((path, commit.ToLowerInvariant()), out hotspot);
    }

    public void Add(FileHotspot hotspot) => _scores[(hotspot.Path, hotspot.IndexCommit.ToLowerInvariant())] = hotspot;
}

/// <summary>
/// Reads <see cref="CodeGraphRisk"/> for a PR head (sc-25709), owner-side: the project pinned by exact repository URL
/// (<see cref="ICodeGraphOverlays.FindProjectAsync"/>), then per touched file <c>analyze_impact_file</c> with <c>sha</c> = the head
/// and <c>depth</c> = <see cref="ImpactDepth"/> when CodeGraph's overlay of the head is ready (<see cref="Overlays"/>, waited for by the
/// pipeline, bounded by <c>CodeGraph:OverlayTimeoutMinutes</c>) — otherwise, or once CodeGraph answers C4's <c>notIndexed</c> for the
/// head, without <c>sha</c>: the default-branch index's blast radius, recorded with its commit and labelled not the PR head, held to the
/// same limits (sc-26254); a head answer for one file that cannot be used and is not that <c>notIndexed</c> (an error answer, another
/// commit or file, no <c>found</c>) has that file's blast radius read from the default branch the same way (sc-26134) — then per file
/// <c>file_hotspot</c> (default branch only). What CodeGraph cannot give for the amendment's causes (unreachable or refusing, the
/// repository not indexed, <c>notIndexed</c> with nothing on the default branch either; the language, judged after both answers) is
/// recorded per file as no data (<see cref="CodeGraphRisk.NoData"/>, sc-26254); an answer a reachable CodeGraph gives that cannot be
/// used, once the default-branch read did not replace it, is a misread (<see cref="CodeGraphRisk.Misread"/>, sc-26134), which blocks.
/// Either way the read goes on: an unusable answer concerns that file only; a call that cannot be made at all — CodeGraph unreachable, a 5xx, a
/// timeout, each tried <see cref="Attempts"/> times with the <see cref="Backoff"/> between — ends that tool's calls, its remaining files
/// recorded with the same reason, so an unreachable CodeGraph costs one call (and its retries) per tool, not one per file. A typed answer
/// (an error code, an HTTP 4xx such as 403 <c>tool_not_entitled</c>) is not retried. A blast radius CodeGraph answers for a found file
/// that the gate does not recognise is a misread, never missing data: it blocks (<see cref="CodeGraphRisk.Unrecognised"/>). A file
/// CodeGraph has no data for (<c>found: false</c>, e.g. a new file) counts as blast radius none and hotspot 0 (owner decision
/// 2026-10-10); its hotspot is recorded at CodeGraph's <c>indexCommitSha</c>, since C7 names no metrics commit (<c>commitSha</c> null)
/// for a file without metrics.
/// </summary>
/// <param name="delay">The wait between attempts (tests pass one that does not wait); null — <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
public sealed class CodeGraphRiskReader(ICodeGraphRisk codeGraph, TimeSpan overlayTimeout, TimeSpan? pollInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>The impact depth asked of <c>analyze_impact_file</c> (C7's default).</summary>
    public const int ImpactDepth = 3;

    /// <summary>CodeGraph's <c>source</c> of an answer read from the default-branch index (CommitAnswer, C4).</summary>
    public const string DefaultBranchSource = "default-branch";

    /// <summary>How many times a call that could not be made (unreachable, 5xx, timeout) is tried in all.</summary>
    public const int Attempts = 3;

    /// <summary>The waits before the second and the third attempt.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>The wait for CodeGraph's overlay of the head (<c>CodeGraph:OverlayTimeoutMinutes</c>), on the same client.</summary>
    public ReviewOverlays Overlays { get; } = new(codeGraph, overlayTimeout, pollInterval);

    /// <param name="hotspots">
    /// Hotspot scores read earlier (e.g. before an overlay was requested again): one read at the ready overlay's base commit is used
    /// instead of asking again.
    /// </param>
    public async Task<CodeGraphRisk> ReadAsync(RepoRef repo, string headSha, CodeGraphOverlay overlay, IReadOnlyList<string> paths, CancellationToken ct,
        HotspotCache? hotspots = null, Action<CodeGraphRisk>? interrupted = null)
    {
        var noData = new List<NoCodeGraphData>();
        var blast = new List<FileBlastRadius>();
        var scores = new List<FileHotspot>();
        var unindexed = new List<UnindexedFile>();
        var unrecognised = new List<UnrecognisedBlastRadius>();
        var misread = new List<CodeGraphMisread>();
        var overlayUnusable = false;
        var impacts = new Dictionary<string, ImpactFileAnswer>();
        var hotspotAnswers = new Dictionary<string, FileHotspot>();
        try
        {
            var (project, unreachable) = await Call(() => codeGraph.FindProjectAsync(repo, ct), "search_projects", ct);
            if (unreachable is not null)
            {
                return CodeGraphRisk.None(headSha, paths, $"CodeGraph could not be reached or asked: {unreachable}");
            }
            if (project is null)
            {
                return CodeGraphRisk.None(headSha, paths, $"{repo.FullName}: {ReviewTools.NotIndexed}");
            }
            var head = headSha.ToLowerInvariant();
            // Why the head is not read (null: it is): no ready overlay of it, or CodeGraph answered it has no usable one. Then each file's
            // blast radius is read from the default-branch index instead (sc-26254), labelled not the PR head.
            string? notAtHead = overlay.IsReady && string.Equals(overlay.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"no ready CodeGraph overlay of {headSha} ({overlay.Describe})";
            string? ended = null;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (ended is not null)
                {
                    noData.Add(new NoCodeGraphData(path, ended));
                    continue;
                }
                ImpactFileAnswer? answer = null;
                // The head's answer for this file when it could not be used and is not C4's typed notIndexed: a misread, unless the
                // default-branch read below gives usable data (sc-26134).
                string? headMisread = null;
                if (notAtHead is null)
                {
                    var (atHead, failed) = await Call(() => codeGraph.AnalyzeImpactFileAsync(project, path, headSha, ImpactDepth, ct),
                        CodeGraphMcpClient.AnalyzeImpactFileTool, ct);
                    if (atHead is null)
                    {
                        noData.Add(new NoCodeGraphData(path, failed!));
                        ended = $"{CodeGraphMcpClient.AnalyzeImpactFileTool} not asked: {failed}";
                        continue;
                    }
                    var why = Impact(atHead, head) ?? (SamePath(atHead.Path, path) ? null
                        : OtherPath(CodeGraphMcpClient.AnalyzeImpactFileTool, atHead.Path, path));
                    if (atHead.NotIndexed is not null)
                    {
                        // CodeGraph has no usable overlay of the head (e.g. the recorded one went stale): it answers no file of it, so this and
                        // every later file is read from the default branch, and the pipeline asks for the overlay again.
                        overlayUnusable = true;
                        notAtHead = why;
                    }
                    else if (why is not null)
                    {
                        // This file's head answer cannot be used: its default-branch blast radius is read instead, for this file only.
                        headMisread = why;
                    }
                    else
                    {
                        answer = atHead;
                    }
                }
                if (answer is null)
                {
                    var before = headMisread ?? notAtHead;
                    var (onDefault, failed) = await Call(() => codeGraph.AnalyzeImpactFileAsync(project, path, null, ImpactDepth, ct),
                        CodeGraphMcpClient.AnalyzeImpactFileTool, ct);
                    if (onDefault is null)
                    {
                        // CodeGraph cannot be asked any more: no data — unless the head already misanswered this file, which stays a misread.
                        var reason = $"blast radius: {before}; on the default branch: {failed}";
                        if (headMisread is null)
                        {
                            noData.Add(new NoCodeGraphData(path, reason));
                        }
                        else
                        {
                            misread.Add(new CodeGraphMisread(path, reason));
                        }
                        ended = $"{CodeGraphMcpClient.AnalyzeImpactFileTool} not asked: {failed}";
                        continue;
                    }
                    var unusable = DefaultBranchImpact(onDefault) ?? (SamePath(onDefault.Path, path) ? null
                        : OtherPath(CodeGraphMcpClient.AnalyzeImpactFileTool, onDefault.Path, path));
                    if (unusable is not null)
                    {
                        // Only C4's typed notIndexed on the default branch, after no head answer was misread, is no data; any other unusable
                        // answer of a reachable CodeGraph is a misread, and it blocks (sc-26134).
                        var reason = $"blast radius: {before}; on the default branch: {unusable}";
                        if (headMisread is null && onDefault.NotIndexed is not null)
                        {
                            noData.Add(new NoCodeGraphData(path, reason));
                        }
                        else
                        {
                            misread.Add(new CodeGraphMisread(path, reason));
                        }
                        continue;
                    }
                    answer = onDefault;
                }
                impacts[path] = answer;
                var commit = notAtHead is null && headMisread is null ? null : answer.CommitSha;
                if (answer.Found == true && !BlastRadii.TryParseAnswer(answer.BlastRadius, out _))
                {
                    // A misread, not missing data: CodeGraph has data for the file and the gate cannot tell how bad it is.
                    unrecognised.Add(new UnrecognisedBlastRadius(path, answer.BlastRadius));
                    continue;
                }
                blast.Add(answer.Found == true && BlastRadii.TryParseAnswer(answer.BlastRadius, out var radius)
                    ? new FileBlastRadius(path, radius, true, commit)
                    : new FileBlastRadius(path, BlastRadius.None, false, commit));
            }
            string? hotspotsEnded = null;
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (hotspots is not null && hotspots.TryGet(path, overlay.BaseSha, out var known))
                {
                    scores.Add(known!);
                    hotspotAnswers[path] = known!;
                    continue;
                }
                if (hotspotsEnded is not null)
                {
                    noData.Add(new NoCodeGraphData(path, hotspotsEnded));
                    continue;
                }
                var (answer, failed) = await Call(() => codeGraph.FileHotspotAsync(project, path, ct), CodeGraphMcpClient.FileHotspotTool, ct);
                if (answer is null)
                {
                    noData.Add(new NoCodeGraphData(path, failed!));
                    hotspotsEnded = $"{CodeGraphMcpClient.FileHotspotTool} not asked: {failed}";
                    continue;
                }
                if (Hotspot(answer, path) is { } why)
                {
                    // A reachable CodeGraph's unusable answer: a misread (sc-26134), which blocks where the policy judges hotspots.
                    misread.Add(new CodeGraphMisread(path, why, Hotspot: true));
                    continue;
                }
                var score = Score(answer, path) with { LanguageIndexed = answer.LanguageIndexed, Language = answer.Language };
                scores.Add(score);
                hotspotAnswers[path] = score;
                hotspots?.Add(score);
            }
            return Observations();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A deadline can interrupt even the first read after it already answered a blocking file. Hand those observations back
            // without swallowing cancellation (the pipeline distinguishes its backstop from a human Pause/Stop or shutdown).
            interrupted?.Invoke(Observations());
            throw;
        }

        CodeGraphRisk Observations()
        {
            // Each file's language is judged from whatever answers arrived, including on an interrupted read.
            foreach (var path in paths)
            {
                var impact = impacts.GetValueOrDefault(path);
                var score = hotspotAnswers.GetValueOrDefault(path);
                if (impact is null && score is null)
                {
                    continue;
                }
                // Without an impact answer the factory's own inference (no flag, no graph data) is not made.
                LanguageCoverage.Add(unindexed, LanguageCoverage.Of(path, impact?.LanguageIndexed, impact?.Language, score?.LanguageIndexed,
                    score?.Language, impact is null ? true : impact.Found));
            }
            return new CodeGraphRisk(headSha, blast, scores, noData, overlayUnusable, unindexed, unrecognised, misread);
        }
    }

    /// <summary>
    /// A usable hotspot answer as recorded (C7, PR #76): found with a metrics commit → its score at that commit; found without one
    /// (metrics computed before CodeGraph recorded commits) → its score at the index commit, labelled vitals commit unknown; not found
    /// → 0 at the index commit. <see cref="Hotspot"/> has already refused an answer naming neither commit.
    /// </summary>
    private static FileHotspot Score(HotspotAnswer answer, string path) => answer.Found == true
        ? answer.CommitSha is { } computed
            ? new FileHotspot(path, answer.RiskScore!.Value, true, computed, answer.IndexCommitSha)
            : new FileHotspot(path, answer.RiskScore!.Value, true, answer.IndexCommitSha!, answer.IndexCommitSha, VitalsCommitUnknown: true)
        : new FileHotspot(path, 0, false, answer.IndexCommitSha ?? answer.CommitSha!, answer.IndexCommitSha);

    /// <summary>
    /// Why an impact answer cannot be used for <paramref name="head"/> (the file's path checked apart), or null when it can. A found
    /// file's unrecognised blast radius is not refused here: the reader records it as a misread, which blocks.
    /// </summary>
    private static string? Impact(ImpactFileAnswer answer, string head) => answer switch
    {
        { NotIndexed: { } n } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} is not indexed for the head ({Cut(n.Code)}, status {Cut(n.Status)})",
        { IsError: true } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        _ when answer.CommitSha != head => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} answered for {answer.CommitSha ?? "an unnamed commit"}, not the head",
        { Found: null } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} answered no 'found'",
        _ => null,
    };

    /// <summary>
    /// Why a default-branch impact answer (no <c>sha</c> asked) cannot be used, or null when it can: it must name the index commit it
    /// describes (E3's exception, as the hotspots: recorded with its commit) and come from the default branch.
    /// </summary>
    private static string? DefaultBranchImpact(ImpactFileAnswer answer) => answer switch
    {
        { NotIndexed: { } n } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} is not indexed ({Cut(n.Code)}, status {Cut(n.Status)})",
        { IsError: true } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        { CommitSha: null } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} named no commit",
        { Source: { } source } when source != DefaultBranchSource => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} answered from '{Cut(source)}', not the default branch",
        { Found: null } => $"{CodeGraphMcpClient.AnalyzeImpactFileTool} answered no 'found'",
        _ => null,
    };

    /// <summary>Why a hotspot answer cannot be used, or null when it can.</summary>
    private static string? Hotspot(HotspotAnswer answer, string path) => answer switch
    {
        { IsError: true } => $"{CodeGraphMcpClient.FileHotspotTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        // E3's exception is the default branch only, recorded with its commit: an answer naming no commit at all (neither the metrics'
        // commitSha nor the index's indexCommitSha), or not from the default branch, is not used.
        { CommitSha: null, IndexCommitSha: null } => $"{CodeGraphMcpClient.FileHotspotTool} named no commit (neither commitSha nor indexCommitSha)",
        { Source: { } source } when source != DefaultBranchSource => $"{CodeGraphMcpClient.FileHotspotTool} answered from '{Cut(source)}', not the default branch",
        _ when !SamePath(answer.Path, path) => OtherPath(CodeGraphMcpClient.FileHotspotTool, answer.Path, path),
        { Found: null } => $"{CodeGraphMcpClient.FileHotspotTool} answered no 'found'",
        { Found: true, RiskScore: null } => $"{CodeGraphMcpClient.FileHotspotTool} answered no riskScore",
        { Found: true, RiskScore: < 0 or double.NaN or double.PositiveInfinity } => $"{CodeGraphMcpClient.FileHotspotTool} answered an invalid riskScore",
        _ => null,
    };

    /// <summary>Whether an answer's <c>path</c> names the file asked about (both normalized as the gate normalizes a diff's paths).</summary>
    private static bool SamePath(string? answered, string asked) =>
        answered is not null && RepoPath.Normalize(answered) is { } a && a == RepoPath.Normalize(asked);

    private static string OtherPath(string tool, string? answered, string asked) =>
        $"{tool} answered about {(answered is null ? "no path" : $"'{Cut(answered)}'")}, not {asked}";

    /// <summary>
    /// One call: its answer, or — a call that cannot be made — null and why. A transient failure (unreachable, an HTTP 5xx, 408 or 429,
    /// a timeout in the HTTP client, a broken answer) is tried again, <see cref="Attempts"/> times in all with the <see cref="Backoff"/>
    /// between; an HTTP 4xx CodeGraph answered on purpose (403 <c>tool_not_entitled</c>, …) is not, nor is the caller's cancellation.
    /// The answer itself, typed error codes included, is the caller's to judge: never retried here.
    /// </summary>
    private async Task<(T? Answer, string? Failed)> Call<T>(Func<Task<T?>> call, string tool, CancellationToken ct) where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await call(), null);
            }
            catch (McpHttpException ex) when (ex.StatusCode == 403)
            {
                return (null, ex.Code == McpHttpException.NotEntitled
                    ? $"not entitled to {tool}"
                    : $"{tool}: CodeGraph answered 403{(ex.Code is { } code ? $" ({Cut(code)})" : "")}");
            }
            catch (McpHttpException ex) when (ex.StatusCode is >= 400 and < 500 and not 408 and not 429)
            {
                return (null, $"{tool}: CodeGraph answered {ex.StatusCode}{(ex.Code is { } code ? $" ({Cut(code)})" : "")}: {Cut(ex.Message)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt >= Attempts)
                {
                    return (null, $"{tool} could not be sent ({attempt} attempts): {Cut(ex.Message)}");
                }
                await _delay(Backoff[Math.Min(attempt - 1, Backoff.Count - 1)], ct);
            }
        }
    }

    private static string Cut(string s) => s.Length > 300 ? s[..300] : s;
}
