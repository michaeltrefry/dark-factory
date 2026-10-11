using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Mcp;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// CodeGraph's C7 risk tools' answers (sc-25735), field by field as michaeltrefry/CodeGraph PR #76 (head e11ac318) builds them in
/// <c>CodeGraphMcpServer.AnalyzeImpactFile</c> / <c>FileHotspot</c>, through <c>CommitAnswer.Create</c>:
/// <code>
/// var structured = new JsonObject { ["commitSha"] = commit.CommitSha, ["source"] = scope.Overlay is not null ? "overlay" : "default-branch" };
/// if (scope.IsPinned) structured["repo"] = scope.Project;
/// if (scope.Overlay is { } overlay) { structured["overlayId"] = overlay.Overlay.Id; structured["baseSha"] = overlay.Overlay.BaseSha; }
/// foreach (var (key, value) in extra) structured[key] = value?.DeepClone();
/// return Result($"{commit.HeaderLine}\n\n{text}", structured, isError: false);   // HeaderLine: "Commit: &lt;sha&gt;" or "Commit: unknown"
/// </code>
/// <c>analyze_impact_file</c>'s extra (PR #76): <c>["found"] = report is not null</c>, <c>["blastRadius"] = blastRadius</c> ("none" when
/// <c>found</c> is false), <c>["criticalCount"] = summary?.CriticalCount ?? 0</c> (likewise high, medium, low, totalAffected, crossRepoCount,
/// lowConfidenceCount), <c>["unmappedInboundEdges"] = unmapped</c> (null without an overlay); a pinned scope names the SHA asked about
/// (<c>if (scope.Sha is not null) return new CommitDescription(scope.Sha, commits)</c>), so a file without nodes still names the head.
/// <c>file_hotspot</c>'s extra (PR #76):
/// <code>
/// var indexCommit = await commitScopes.GetDefaultCommitAsync(repository.Name);
/// var commitSha = metric?.CommitSha;
/// double? concernScore = metric is null || commitSha is null ? null : metric.ConcernScore;
/// ["found"] = metric is not null, ["riskScore"] = metric?.RiskScore, ["concernScore"] = concernScore, ..., ["commitSha"] = commitSha,
/// ["indexCommitSha"] = indexCommit
/// var commit = new CommitDescription(commitSha, new Dictionary&lt;string, string?&gt; { [repository.Name] = commitSha });
/// </code>
/// so a file with no metrics (new) answers <c>commitSha: null</c> and only <c>indexCommitSha</c> names a commit.
/// </summary>
public static class C7Answers
{
    /// <summary>An <c>analyze_impact_file</c> answer pinned to <paramref name="sha"/> (from its overlay unless <paramref name="overlay"/> is false).</summary>
    public static JsonObject ImpactFile(string repo, string path, string sha, bool found, string blastRadius, bool overlay = true,
        long overlayId = 41, string? baseSha = FakeCodeGraphRisk.IndexCommit, int count = 2)
    {
        var structured = new JsonObject { ["commitSha"] = sha, ["source"] = overlay ? "overlay" : "default-branch", ["repo"] = repo };
        if (overlay)
        {
            structured["overlayId"] = overlayId;
            structured["baseSha"] = baseSha;
        }
        structured["repo"] = repo;
        structured["path"] = path;
        structured["depth"] = CodeGraphRiskReader.ImpactDepth;
        structured["found"] = found;
        structured["blastRadius"] = found ? blastRadius : "none";
        foreach (var field in new[] { "criticalCount", "highCount", "mediumCount", "lowCount", "totalAffected", "crossRepoCount", "lowConfidenceCount" })
        {
            structured[field] = found ? count : 0;
        }
        structured["unmappedInboundEdges"] = overlay ? 0 : null;
        var text = found
            ? $"# File Blast Radius: {repo}/{path}\n\n**Blast radius:** {blastRadius}"
            : $"# File Blast Radius: {repo}/{path}\n\nNo graph nodes in this file (found: false); blast radius: none.";
        return FakeMcpServer.Structured($"Commit: {sha}\n\n{text}", structured);
    }

    /// <summary>
    /// A <c>file_hotspot</c> answer (default branch): <paramref name="riskScore"/> null = no metrics (<c>found: false</c>, and then
    /// <c>commitSha</c> is null whatever <paramref name="commitSha"/> says); <paramref name="commitSha"/> null with a score = metrics computed
    /// before CodeGraph recorded commits (<c>concernScore</c> null too).
    /// </summary>
    public static JsonObject Hotspot(string repo, string path, double? riskScore, string? commitSha = FakeCodeGraphRisk.IndexCommit,
        string? indexCommitSha = FakeCodeGraphRisk.IndexCommit, string source = "default-branch")
    {
        var found = riskScore is not null;
        var computed = found ? commitSha : null;
        var structured = new JsonObject
        {
            ["commitSha"] = computed,
            ["source"] = source,
            ["repo"] = repo,
            ["path"] = path,
            ["found"] = found,
            ["riskScore"] = riskScore,
            ["concernScore"] = found && computed is not null ? 0.4 : null,
            ["healthScore"] = found ? 6.5 : null,
            ["churn365d"] = found ? 1.25 : null,
            ["complexityScore"] = found ? 7 : null,
            ["computedAt"] = found ? "2026-10-09T12:00:00Z" : null,
            ["indexCommitSha"] = indexCommitSha,
        };
        var text = found
            ? $"# File Hotspot: {repo}/{path}\n\n**Risk:** {riskScore:F1}\n**Computed:** 2026-10-09 12:00:00Z at commit {computed ?? "unknown (not recorded for this run)"}"
            : $"# File Hotspot: {repo}/{path}\n\nNo vitals metrics for this file on the default branch (found: false).";
        return FakeMcpServer.Structured($"Commit: {computed ?? "unknown"}\n\n{text}", structured);
    }

    /// <summary>A <c>tools/call</c> result as <see cref="McpHttpClient"/> hands it over (text joined, structured content, error flag).</summary>
    public static McpToolResult Result(JsonObject result) =>
        new(string.Join("\n", result["content"]!.AsArray().Select(b => b!["text"]!.GetValue<string>())),
            result["isError"] is JsonValue e && e.GetValue<bool>(), result["structuredContent"], result);

    /// <summary>An <c>analyze_impact_file</c> answer as the factory's client parses it.</summary>
    public static ImpactFileAnswer ParsedImpactFile(string repo, string path, string sha, bool found, string blastRadius) =>
        CodeGraphMcpClient.ImpactFile(Result(ImpactFile(repo, path, sha, found, blastRadius)));

    /// <summary>A <c>file_hotspot</c> answer as the factory's client parses it.</summary>
    public static HotspotAnswer ParsedHotspot(string repo, string path, double? riskScore, string? commitSha = FakeCodeGraphRisk.IndexCommit,
        string? indexCommitSha = FakeCodeGraphRisk.IndexCommit) =>
        CodeGraphMcpClient.Hotspot(Result(Hotspot(repo, path, riskScore, commitSha, indexCommitSha)));
}
