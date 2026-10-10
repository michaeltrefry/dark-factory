using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A CodeGraph for the merge gate's <c>risk-threshold</c> (sc-25709), in memory: the project is found, the overlay of any commit is
/// ready at once, and by default every file has no data (<c>found: false</c>: blast radius none, hotspot 0) — so a pipeline test's
/// gate passes the check unless the test says otherwise through <see cref="Impact"/>, <see cref="Hotspot"/>, <see cref="Overlay"/>
/// or <see cref="Project"/>.
/// </summary>
public sealed class FakeCodeGraphRisk : ICodeGraphRisk
{
    /// <summary>The default-branch index commit hotspots are read at.</summary>
    public const string IndexCommit = "c0ffee0123456789abcdef0123456789abcdef01";

    public string? Project { get; set; } = "Sandbox";

    /// <summary>When set, the overlay answer for a requested commit (else ready at that commit).</summary>
    public Func<string, OverlayAnswer>? Overlay { get; set; }

    /// <summary>When set, the impact answer for (path, sha); throw from it to make the call fail.</summary>
    public Func<string, string, ImpactFileAnswer>? Impact { get; set; }

    /// <summary>When set, the hotspot answer for a path; throw from it to make the call fail.</summary>
    public Func<string, HotspotAnswer>? Hotspot { get; set; }

    /// <summary>Every call: tool and its main argument.</summary>
    public List<(string Tool, string Argument)> Calls { get; } = [];

    public static CodeGraphRiskReader Reader(FakeCodeGraphRisk? fake = null) =>
        new(fake ?? new FakeCodeGraphRisk(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));

    /// <summary>A found file's impact answer at <paramref name="sha"/>.</summary>
    public static ImpactFileAnswer Found(string sha, BlastRadius radius) =>
        new(false, $"Commit: {sha}", sha, null, null, true, BlastRadii.Name(radius));

    /// <summary>A found file's hotspot answer with <paramref name="riskScore"/>.</summary>
    public static HotspotAnswer Hot(double riskScore) =>
        new(false, $"Commit: {IndexCommit}", IndexCommit, CodeGraphRiskReader.DefaultBranchSource, null, true, riskScore);

    public Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct)
    {
        Calls.Add(("search_projects", repo.FullName));
        return Task.FromResult(Project);
    }

    public Task<OverlayAnswer> RequestOverlayAsync(string project, string gitRef, CancellationToken ct)
    {
        Calls.Add(("request_overlay", gitRef));
        return Task.FromResult(Overlay?.Invoke(gitRef) ?? new OverlayAnswer(false, "ready", 7, "ready", gitRef, IndexCommit, null));
    }

    public Task<OverlayAnswer> OverlayStatusAsync(long overlayId, CancellationToken ct)
    {
        Calls.Add(("get_overlay_status", overlayId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Task.FromResult(new OverlayAnswer(false, "failed", overlayId, "failed", null, null, "polled"));
    }

    public Task<ImpactFileAnswer> AnalyzeImpactFileAsync(string project, string path, string sha, int depth, CancellationToken ct)
    {
        Calls.Add(("analyze_impact_file", path));
        return Task.FromResult(Impact?.Invoke(path, sha) ?? new ImpactFileAnswer(false, $"Commit: {sha}", sha, null, null, false, "none"));
    }

    public Task<HotspotAnswer> FileHotspotAsync(string project, string path, CancellationToken ct)
    {
        Calls.Add(("file_hotspot", path));
        return Task.FromResult(Hotspot?.Invoke(path)
            ?? new HotspotAnswer(false, $"Commit: {IndexCommit}", IndexCommit, CodeGraphRiskReader.DefaultBranchSource, null, false, null));
    }
}

/// <summary>CodeGraph's inputs to <c>risk-threshold</c> as the merge gate's unit tests hand them in.</summary>
internal static class TestRisk
{
    /// <summary>Every file the diff touches with no CodeGraph data (blast radius none, hotspot 0) at <paramref name="head"/>.</summary>
    public static CodeGraphRisk None(string head, string? diff) => With(head, diff ?? "", _ => BlastRadius.None, _ => 0);

    /// <summary>Every file the diff touches with the blast radius and hotspot score the functions give (found).</summary>
    public static CodeGraphRisk With(string head, string diff, Func<string, BlastRadius> radius, Func<string, double> score)
    {
        var files = MergeGate.TouchedFiles(diff);
        return new CodeGraphRisk(head, files.Select(f => new FileBlastRadius(f, radius(f), true)).ToList(),
            files.Select(f => new FileHotspot(f, score(f), true, FakeCodeGraphRisk.IndexCommit)).ToList(), []);
    }
}
