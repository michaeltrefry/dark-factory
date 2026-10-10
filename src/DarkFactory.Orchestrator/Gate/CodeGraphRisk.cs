using System.Globalization;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;

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
/// overlay (C4), an error answer's code (<c>structuredContent.error.code</c>), and the tool's own <c>found</c> and <c>blastRadius</c>.
/// </summary>
public sealed record ImpactFileAnswer(bool IsError, string Text, string? CommitSha, CodeGraphNotIndexed? NotIndexed, string? ErrorCode,
    bool? Found, string? BlastRadius);

/// <summary>
/// One <c>file_hotspot</c> answer (C7): whether it is an error, its text, the commit it names, its <c>source</c>
/// (<c>default-branch</c> or <c>overlay</c>), an error answer's code, and the tool's own <c>found</c> and <c>riskScore</c>.
/// </summary>
public sealed record HotspotAnswer(bool IsError, string Text, string? CommitSha, string? Source, string? ErrorCode, bool? Found, double? RiskScore);

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
/// One touched file's hotspot score — CodeGraph's <c>riskScore</c> is "the hotspot score" — on the default branch at
/// <see cref="CommitSha"/> (<see cref="Found"/> false: no metrics, counted as 0).
/// </summary>
public sealed record FileHotspot(string Path, double Score, bool Found, string CommitSha);

/// <summary>
/// CodeGraph's inputs to <c>risk-threshold</c> for one evaluation (sc-25709), read fresh: each touched file's blast radius for exactly
/// <see cref="HeadSha"/> (E3) and its hotspot score on the default branch, each recorded with the commit CodeGraph named (the
/// default-branch exception of E3). <see cref="Unreadable"/>: what could not be read (CodeGraph unreachable or refusing, the repository
/// not indexed, no ready overlay of the head, an answer for another commit or an unreadable one): non-empty, <c>risk-threshold</c> is
/// pending — the gate waits like pending CI and escalates at <c>Gate:CiTimeoutMinutes</c> (E4).
/// </summary>
public sealed record CodeGraphRisk(string HeadSha, IReadOnlyList<FileBlastRadius> BlastRadius, IReadOnlyList<FileHotspot> Hotspots,
    IReadOnlyList<string> Unreadable)
{
    /// <summary>The file with the worst blast radius (the first such), or null when none was read.</summary>
    public FileBlastRadius? Worst => BlastRadius.OrderByDescending(f => f.Radius).FirstOrDefault();

    /// <summary>The file with the highest hotspot score (the first such), or null when none was read.</summary>
    public FileHotspot? Hottest => Hotspots.OrderByDescending(f => f.Score).FirstOrDefault();

    /// <summary>Every input as the <c>gate</c> checkpoint records it: each file's blast radius at the head, each hotspot score with its commit.</summary>
    public string Describe() =>
        $"codegraph blast radius at {HeadSha}: "
        + (BlastRadius.Count == 0 ? "none read" : string.Join(", ", BlastRadius.Select(f => $"{f.Path} {BlastRadii.Name(f.Radius)}{(f.Found ? "" : " (no data)")}")))
        + "; hotspot score (riskScore) on the default branch: "
        + (Hotspots.Count == 0 ? "none read" : string.Join("; ", Hotspots.GroupBy(f => f.CommitSha).Select(g =>
            $"at {g.Key}: {string.Join(", ", g.Select(f => $"{f.Path} {Score(f.Score)}{(f.Found ? "" : " (no data)")}"))}")))
        + (Unreadable.Count == 0 ? "" : $"; unreadable: {string.Join("; ", Unreadable)}");

    public static string Score(double score) => score.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads <see cref="CodeGraphRisk"/> for a PR head (sc-25709), owner-side: the project pinned by exact repository URL
/// (<see cref="ICodeGraphOverlays.FindProjectAsync"/>), then per touched file <c>analyze_impact_file</c> with <c>sha</c> = the head
/// and <c>depth</c> = <see cref="ImpactDepth"/> (only once CodeGraph's overlay of the head is ready: <see cref="Overlays"/>, waited
/// for by the pipeline), and <c>file_hotspot</c> (default branch only). A file CodeGraph has no data for (<c>found: false</c>, e.g. a
/// new file) counts as blast radius none and hotspot 0 (owner decision 2026-10-10); one it cannot read is <see cref="CodeGraphRisk.Unreadable"/>.
/// </summary>
public sealed class CodeGraphRiskReader(ICodeGraphRisk codeGraph, TimeSpan overlayTimeout, TimeSpan? pollInterval = null)
{
    /// <summary>The impact depth asked of <c>analyze_impact_file</c> (C7's default).</summary>
    public const int ImpactDepth = 3;

    /// <summary>CodeGraph's <c>source</c> of an answer read from the default-branch index (CommitAnswer, C4).</summary>
    public const string DefaultBranchSource = "default-branch";

    /// <summary>The wait for CodeGraph's overlay of the head (<c>CodeGraph:OverlayTimeoutMinutes</c>), on the same client.</summary>
    public ReviewOverlays Overlays { get; } = new(codeGraph, overlayTimeout, pollInterval);

    public async Task<CodeGraphRisk> ReadAsync(RepoRef repo, string headSha, CodeGraphOverlay overlay, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var unreadable = new List<string>();
        string? project = null;
        try
        {
            project = await codeGraph.FindProjectAsync(repo, ct);
            if (project is null)
            {
                unreadable.Add($"{repo.FullName}: {ReviewTools.NotIndexed}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            unreadable.Add($"CodeGraph could not be reached or asked: {Cut(ex.Message)}");
        }
        var blast = new List<FileBlastRadius>();
        var hotspots = new List<FileHotspot>();
        if (project is null)
        {
            return new CodeGraphRisk(headSha, blast, hotspots, unreadable);
        }
        var head = headSha.ToLowerInvariant();
        if (!overlay.IsReady || !string.Equals(overlay.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            // E3: the blast radius is read for exactly the head, which CodeGraph answers only from a ready overlay of it.
            unreadable.Add($"blast radius: no ready CodeGraph overlay of {headSha} ({overlay.Describe})");
        }
        else
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (await Call(() => codeGraph.AnalyzeImpactFileAsync(project, path, headSha, ImpactDepth, ct), CodeGraphMcpClient.AnalyzeImpactFileTool, path, unreadable, ct)
                    is not { } answer)
                {
                    continue;
                }
                if (Impact(answer, path, head) is { } why)
                {
                    unreadable.Add(why);
                    continue;
                }
                blast.Add(answer.Found == true && BlastRadii.TryParse(answer.BlastRadius, out var radius)
                    ? new FileBlastRadius(path, radius, true)
                    : new FileBlastRadius(path, BlastRadius.None, false));
            }
        }
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (await Call(() => codeGraph.FileHotspotAsync(project, path, ct), CodeGraphMcpClient.FileHotspotTool, path, unreadable, ct) is not { } answer)
            {
                continue;
            }
            if (Hotspot(answer, path) is { } why)
            {
                unreadable.Add(why);
                continue;
            }
            hotspots.Add(answer.Found == true ? new FileHotspot(path, answer.RiskScore!.Value, true, answer.CommitSha!) : new FileHotspot(path, 0, false, answer.CommitSha!));
        }
        return new CodeGraphRisk(headSha, blast, hotspots, unreadable);
    }

    /// <summary>Why an impact answer cannot be used for <paramref name="head"/>, or null when it can.</summary>
    private static string? Impact(ImpactFileAnswer answer, string path, string head) => answer switch
    {
        { NotIndexed: { } n } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} is not indexed for the head ({Cut(n.Code)}, status {Cut(n.Status)})",
        { IsError: true } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        _ when answer.CommitSha != head => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered for {answer.CommitSha ?? "an unnamed commit"}, not the head",
        { Found: null } => $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered no 'found'",
        { Found: true } when !BlastRadii.TryParse(answer.BlastRadius, out _) =>
            $"{path}: {CodeGraphMcpClient.AnalyzeImpactFileTool} answered an unknown blast radius '{Cut(answer.BlastRadius ?? "(none)")}'",
        _ => null,
    };

    /// <summary>Why a hotspot answer cannot be used, or null when it can.</summary>
    private static string? Hotspot(HotspotAnswer answer, string path) => answer switch
    {
        { IsError: true } => $"{path}: {CodeGraphMcpClient.FileHotspotTool}{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}",
        // E3's exception is the default branch only, recorded with its commit: an answer naming no commit, or not from it, is not used.
        { CommitSha: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} named no commit",
        { Source: { } source } when source != DefaultBranchSource => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered from '{Cut(source)}', not the default branch",
        { Found: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered no 'found'",
        { Found: true, RiskScore: null } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered no riskScore",
        { Found: true, RiskScore: < 0 or double.NaN or double.PositiveInfinity } => $"{path}: {CodeGraphMcpClient.FileHotspotTool} answered an invalid riskScore",
        _ => null,
    };

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
