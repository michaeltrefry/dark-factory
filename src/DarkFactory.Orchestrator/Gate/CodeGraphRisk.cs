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

    /// <summary>Parses a lowercase name exactly (no numbers, no other case).</summary>
    public static bool TryParse(string? text, out BlastRadius radius)
    {
        foreach (var r in Enum.GetValues<BlastRadius>())
        {
            if (text == Name(r))
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
/// </summary>
public sealed record ImpactFileAnswer(bool IsError, string Text, string? CommitSha, CodeGraphNotIndexed? NotIndexed, string? ErrorCode,
    bool? Found, string? BlastRadius, string? Path = null, bool? LanguageIndexed = null, string? Language = null);

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
/// <c>risk-threshold</c> fails closed on it. <see cref="Language"/>: the language's name (CodeGraph's, else the factory's by extension),
/// or null when neither names one. <see cref="Flagged"/>: CodeGraph said so (<c>languageIndexed: false</c>); false — CodeGraph answered
/// no flag (it predates C3) and the factory knows that language was not indexed before C3 (<see cref="LanguageCoverage.UnindexedBeforeC3"/>).
/// </summary>
public sealed record UnindexedFile(string Path, string? Language, bool Flagged)
{
    /// <summary>How the gate's reason and row name it: the file and its language.</summary>
    public override string ToString() =>
        $"{Path} ({Language ?? "language unknown"}{(Flagged ? "" : "; CodeGraph answered no languageIndexed flag (before C3)")})";
}

/// <summary>
/// Whether CodeGraph indexes a touched file's language (sc-26133, P5 S7; the CodeGraph side is C3, sc-26137). The contract: the
/// structured content of <c>analyze_impact_file</c> and <c>file_hotspot</c> carries <c>languageIndexed</c> (boolean: false only for a
/// source file of a language CodeGraph does not index; true for an indexed language and for a file that is not code) and
/// <c>language</c> (CodeGraph's capitalised name of the file's language — <c>Swift</c>, <c>C#</c>, <c>C++</c> — or null). Owner
/// decision 2026-10-14: the gate fails closed on a changed code file whose language CodeGraph does not index, and passes once it does.
/// Rule (<see cref="Of"/>), per file, after BOTH answers: <c>languageIndexed: false</c> in either → unindexed; <c>true</c> in either and
/// <c>false</c> in neither → indexed; neither carries the flag (a CodeGraph that predates C3; E7: .NET and Rust are unchanged) →
/// unindexed only when the file's extension is one of <see cref="UnindexedBeforeC3"/> and CodeGraph had no graph data for it
/// (<c>analyze_impact_file</c>'s <c>found</c> not true: a CodeGraph with graph data for a file indexes its language). CodeGraph's
/// <c>language</c> is shown only when it is an inert name (<see cref="Name"/>); otherwise the factory's name by extension stands in.
/// </summary>
public static class LanguageCoverage
{
    /// <summary>
    /// The code languages, by extension (case-insensitive), CodeGraph's main did not index before C3 (checked against its source,
    /// 2026-10-14: Swift is indexed only on a feature branch). <c>.m</c> is also MATLAB's: such a file is blocked too until C3 says
    /// otherwise — a false block, never a false pass.
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
            return new UnindexedFile(path, language, Flagged: true);
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
    /// <summary><c>analyze_impact_file(repo, path, sha, depth)</c>: the file's blast radius at <paramref name="sha"/>.</summary>
    Task<ImpactFileAnswer> AnalyzeImpactFileAsync(string project, string path, string sha, int depth, CancellationToken ct);

    /// <summary><c>file_hotspot(repo, path)</c>: the file's hotspot metrics on the default branch.</summary>
    Task<HotspotAnswer> FileHotspotAsync(string project, string path, CancellationToken ct);
}

/// <summary>One touched file's blast radius at the head (<see cref="Found"/> false: CodeGraph has no data for it, counted as none).</summary>
public sealed record FileBlastRadius(string Path, BlastRadius Radius, bool Found);

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
/// default-branch exception of E3). <see cref="Unreadable"/>: what could not be read (CodeGraph unreachable or refusing, the repository
/// not indexed, no ready overlay of the head, an answer for another commit or file, or an unreadable one; the reader stops at the
/// first): non-empty, <c>risk-threshold</c> is pending unless a file already read is over a limit — the gate waits like pending CI and
/// escalates at <c>Gate:CiTimeoutMinutes</c> (E4). <see cref="OverlayUnusable"/>: CodeGraph answered that it has no usable overlay of
/// the head (C4's <c>notIndexed</c>, e.g. a ready overlay gone stale once the default branch moved past its base), so a recorded ready
/// overlay must not be reused: the next read requests the overlay again. <see cref="Unindexed"/>: the touched files whose language
/// CodeGraph does not index (<see cref="LanguageCoverage"/>, sc-26133): each fails <c>risk-threshold</c> closed.
/// </summary>
public sealed record CodeGraphRisk(string HeadSha, IReadOnlyList<FileBlastRadius> BlastRadius, IReadOnlyList<FileHotspot> Hotspots,
    IReadOnlyList<string> Unreadable, bool OverlayUnusable = false, IReadOnlyList<UnindexedFile>? Unindexed = null)
{
    /// <summary>The touched files whose language CodeGraph does not index (none when <see cref="Unindexed"/> is null).</summary>
    public IReadOnlyList<UnindexedFile> UnindexedFiles => Unindexed ?? [];

    /// <summary>
    /// How many files <see cref="Describe"/> lists beyond those over a limit and the worst and hottest (and how many unreadable entries
    /// it quotes), so one <c>gate</c> row stays well inside the reports' budget (<see cref="Ledger.LedgerReport.MaxFactsLength"/>) whatever
    /// the PR's size: the rest are counted.
    /// </summary>
    public const int MaxListed = 20;

    /// <summary>The file with the worst blast radius (the first such), or null when none was read.</summary>
    public FileBlastRadius? Worst => BlastRadius.OrderByDescending(f => f.Radius).FirstOrDefault();

    /// <summary>The file with the highest hotspot score (the first such), or null when none was read.</summary>
    public FileHotspot? Hottest => Hotspots.OrderByDescending(f => f.Score).FirstOrDefault();

    /// <summary>
    /// The inputs as the <c>gate</c> checkpoint records them: each file's blast radius at the head and each hotspot score with its commit
    /// — every file over <paramref name="limit"/> or <paramref name="maxHotspot"/>, the worst and the hottest, then the first
    /// <see cref="MaxListed"/> others, and how many more there are — and what could not be read (<see cref="UnreadableText"/>).
    /// </summary>
    public string Describe(BlastRadius? limit = null, double? maxHotspot = null)
    {
        var worst = Worst;
        var hottest = Hottest;
        var blast = Listed(BlastRadius, f => (limit is { } l && f.Radius > l) || ReferenceEquals(f, worst),
            f => $"{f.Path} {BlastRadii.Name(f.Radius)}{(f.Found ? "" : " (no data)")}", ", ");
        var hot = Hotspots.GroupBy(f => f.CommitLabel).Select(g => $"{g.Key}: " + Listed(g.ToList(),
            f => (maxHotspot is { } m && f.Score > m) || ReferenceEquals(f, hottest), f => $"{f.Path} {Score(f.Score)}{(f.Found ? "" : " (no data)")}", ", "));
        return $"codegraph blast radius at {HeadSha}: {(BlastRadius.Count == 0 ? "none read" : blast)}"
            + $"; hotspot score (riskScore) on the default branch: {(Hotspots.Count == 0 ? "none read" : string.Join("; ", hot))}"
            + (UnindexedFiles.Count == 0 ? "" : $"; language not indexed by CodeGraph: {Listed(UnindexedFiles, _ => false, u => u.ToString(), ", ")}")
            + (Unreadable.Count == 0 ? "" : $"; unreadable: {UnreadableText}");
    }

    /// <summary>What could not be read: the first <see cref="MaxListed"/> entries, and how many more.</summary>
    public string UnreadableText => Listed(Unreadable, _ => false, u => u, "; ");

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
/// and <c>depth</c> = <see cref="ImpactDepth"/> (only once CodeGraph's overlay of the head is ready: <see cref="Overlays"/>, waited
/// for by the pipeline), then per file <c>file_hotspot</c> (default branch only). It stops at the first input it cannot read
/// (<see cref="CodeGraphRisk.Unreadable"/>): the gate is pending then anyway, and reads everything again. A file CodeGraph has no data
/// for (<c>found: false</c>, e.g. a new file) counts as blast radius none and hotspot 0 (owner decision 2026-10-10); its hotspot is
/// recorded at CodeGraph's <c>indexCommitSha</c>, since C7 names no metrics commit (<c>commitSha</c> null) for a file without metrics.
/// </summary>
public sealed class CodeGraphRiskReader(ICodeGraphRisk codeGraph, TimeSpan overlayTimeout, TimeSpan? pollInterval = null)
{
    /// <summary>The impact depth asked of <c>analyze_impact_file</c> (C7's default).</summary>
    public const int ImpactDepth = 3;

    /// <summary>CodeGraph's <c>source</c> of an answer read from the default-branch index (CommitAnswer, C4).</summary>
    public const string DefaultBranchSource = "default-branch";

    /// <summary>The wait for CodeGraph's overlay of the head (<c>CodeGraph:OverlayTimeoutMinutes</c>), on the same client.</summary>
    public ReviewOverlays Overlays { get; } = new(codeGraph, overlayTimeout, pollInterval);

    /// <param name="hotspots">
    /// Hotspot scores read earlier in the same wait: one read at the ready overlay's base commit is used instead of asking again.
    /// </param>
    public async Task<CodeGraphRisk> ReadAsync(RepoRef repo, string headSha, CodeGraphOverlay overlay, IReadOnlyList<string> paths, CancellationToken ct,
        HotspotCache? hotspots = null)
    {
        var unreadable = new List<string>();
        var blast = new List<FileBlastRadius>();
        var scores = new List<FileHotspot>();
        var unindexed = new List<UnindexedFile>();
        // Each file's language is judged once both its answers are read (Judge, LanguageCoverage.Of); a file whose hotspot was not read
        // (the read stopped at an unreadable input) is unindexed already when its impact answer says so.
        var impacts = new Dictionary<string, ImpactFileAnswer>();
        CodeGraphRisk Result(bool overlayUnusable = false)
        {
            foreach (var (path, answer) in impacts)
            {
                if (answer.LanguageIndexed == false)
                {
                    LanguageCoverage.Add(unindexed, LanguageCoverage.Of(path, false, answer.Language, null, null, answer.Found));
                }
            }
            return new(headSha, blast, scores, unreadable, overlayUnusable, unindexed);
        }
        void Judge(string path, FileHotspot score)
        {
            var impact = impacts[path];
            LanguageCoverage.Add(unindexed,
                LanguageCoverage.Of(path, impact.LanguageIndexed, impact.Language, score.LanguageIndexed, score.Language, impact.Found));
        }
        string? project;
        try
        {
            project = await codeGraph.FindProjectAsync(repo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            unreadable.Add($"CodeGraph could not be reached or asked: {Cut(ex.Message)}");
            return Result();
        }
        if (project is null)
        {
            unreadable.Add($"{repo.FullName}: {ReviewTools.NotIndexed}");
            return Result();
        }
        var head = headSha.ToLowerInvariant();
        if (!overlay.IsReady || !string.Equals(overlay.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            // E3: the blast radius is read for exactly the head, which CodeGraph answers only from a ready overlay of it.
            unreadable.Add($"blast radius: no ready CodeGraph overlay of {headSha} ({overlay.Describe})");
            return Result();
        }
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (await Call(() => codeGraph.AnalyzeImpactFileAsync(project, path, headSha, ImpactDepth, ct), CodeGraphMcpClient.AnalyzeImpactFileTool, path, unreadable, ct)
                is not { } answer)
            {
                return Result();
            }
            if (Impact(answer, path, head) is { } why)
            {
                unreadable.Add(why);
                // CodeGraph has no usable overlay of the head (e.g. the recorded one went stale): the next read requests it again.
                return Result(overlayUnusable: answer.NotIndexed is not null);
            }
            impacts[path] = answer;
            blast.Add(answer.Found == true && BlastRadii.TryParse(answer.BlastRadius, out var radius)
                ? new FileBlastRadius(path, radius, true)
                : new FileBlastRadius(path, BlastRadius.None, false));
        }
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (hotspots is not null && hotspots.TryGet(path, overlay.BaseSha, out var known))
            {
                scores.Add(known!);
                Judge(path, known!);
                continue;
            }
            if (await Call(() => codeGraph.FileHotspotAsync(project, path, ct), CodeGraphMcpClient.FileHotspotTool, path, unreadable, ct) is not { } answer)
            {
                return Result();
            }
            if (Hotspot(answer, path) is { } why)
            {
                unreadable.Add(why);
                return Result();
            }
            var score = Score(answer, path) with { LanguageIndexed = answer.LanguageIndexed, Language = answer.Language };
            Judge(path, score);
            scores.Add(score);
            hotspots?.Add(score);
        }
        return Result();
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

    /// <summary>Why an impact answer cannot be used for <paramref name="head"/>, or null when it can.</summary>
    private static string? Impact(ImpactFileAnswer answer, string path, string head) => answer switch
    {
        { NotIndexed: { } n } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} is not indexed for the head ({Cut(n.Code)}, status {Cut(n.Status)})",
        { IsError: true } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        _ when answer.CommitSha != head => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered for {answer.CommitSha ?? "an unnamed commit"}, not the head",
        _ when !SamePath(answer.Path, path) => OtherPath(CodeGraphMcpClient.AnalyzeImpactFileTool, answer.Path, path),
        { Found: null } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered no 'found'",
        { Found: true } when !BlastRadii.TryParse(answer.BlastRadius, out _) =>
            $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered an unknown blast radius '{Cut(answer.BlastRadius ?? "(none)")}'",
        _ => null,
    };

    /// <summary>Why a hotspot answer cannot be used, or null when it can.</summary>
    private static string? Hotspot(HotspotAnswer answer, string path) => answer switch
    {
        { IsError: true } => $"{path}: {CodeGraphMcpClient.FileHotspotTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        // E3's exception is the default branch only, recorded with its commit: an answer naming no commit at all (neither the metrics'
        // commitSha nor the index's indexCommitSha), or not from the default branch, is not used.
        { CommitSha: null, IndexCommitSha: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} named no commit (neither commitSha nor indexCommitSha)",
        { Source: { } source } when source != DefaultBranchSource => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered from '{Cut(source)}', not the default branch",
        _ when !SamePath(answer.Path, path) => OtherPath(CodeGraphMcpClient.FileHotspotTool, answer.Path, path),
        { Found: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered no 'found'",
        { Found: true, RiskScore: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered no riskScore",
        { Found: true, RiskScore: < 0 or double.NaN or double.PositiveInfinity } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered an invalid riskScore",
        _ => null,
    };

    /// <summary>Whether an answer's <c>path</c> names the file asked about (both normalized as the gate normalizes a diff's paths).</summary>
    private static bool SamePath(string? answered, string asked) =>
        answered is not null && RepoPath.Normalize(answered) is { } a && a == RepoPath.Normalize(asked);

    private static string OtherPath(string tool, string? answered, string asked) =>
        $"{asked}: {tool} answered about {(answered is null ? "no path" : $"'{Cut(answered)}'")}, not {asked}";

    /// <summary>One call; a call that cannot be made (unreachable, refused over HTTP) is recorded as unreadable and answers null.</summary>
    private static async Task<T?> Call<T>(Func<Task<T>> call, string tool, string path, List<string> unreadable, CancellationToken ct) where T : class
    {
        try
        {
            return await call();
        }
        catch (McpHttpException ex) when (ex.StatusCode == 403)
        {
            unreadable.Add(ex.Code == McpHttpException.NotEntitled
                ? $"{path}: not entitled to {tool}"
                : $"{path}: {tool}: CodeGraph answered 403{(ex.Code is { } code ? $" ({Cut(code)})" : "")}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            unreadable.Add($"{path}: {tool} could not be sent: {Cut(ex.Message)}");
        }
        return null;
    }

    private static string Cut(string s) => s.Length > 300 ? s[..300] : s;
}
