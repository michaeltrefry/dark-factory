using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary><c>factory/gate.yaml</c> texts the gate tests share.</summary>
internal static class TestPolicies
{
    /// <summary>A valid version-2 policy: every tier, the floor checks, a risk threshold the fix-loop tests stay inside.</summary>
    public static string Standard(int maxLines = 400, int maxFiles = 20, int maxFixRounds = 3, string normalChecks = "ci-green, review-pass, risk-threshold, new-tests-fail-on-base") => $"""
        # Dark Factory merge gate policy (test)
        version: 2
        tiers:
          sealed:
            paths: [factory/gate.yaml, .github/workflows/, CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS, factory/prompts/]
            checks: [ci-green, review-pass, security-review]
          protected:
            paths: ["**/auth/", "**/migrations/", infra/, "**/secrets/", "**/payments/"]
            checks: [ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]
          normal:
            checks: [{normalChecks}]
          free:
            paths: [docs/, tests/]
            checks: [ci-green, review-pass]
        risk:
          max_changed_lines: {maxLines}
          max_changed_files: {maxFiles}
          max_fix_rounds: {maxFixRounds}
        """;

    /// <summary>The standard policy as version 3 (sc-25709), with <c>max_blast_radius</c> and <c>max_hotspot</c> as given.</summary>
    public static string V3(string maxBlastRadius, string maxHotspot) =>
        Standard().Replace("version: 2", "version: 3", StringComparison.Ordinal)
        + $"\n  max_blast_radius: {maxBlastRadius}\n  max_hotspot: {maxHotspot}";

    /// <summary>
    /// The standard policy as version 4 (sc-25873): version 3's keys (blast radius at the floor, no hotspot limit) and a changeset
    /// pull request's own size limits.
    /// </summary>
    public static string V4(int maxChangesetLines, int maxChangesetFiles, int maxLines = 400, int maxFiles = 20) =>
        Standard(maxLines, maxFiles).Replace("version: 2", "version: 4", StringComparison.Ordinal)
        + $"\n  max_blast_radius: high\n  max_hotspot: off\n  max_changeset_lines: {maxChangesetLines}\n  max_changeset_files: {maxChangesetFiles}";

    /// <summary>A repository's workflow files whose CI runs for every pull request and push (sc-25873): what the fakes answer by default.</summary>
    public static IReadOnlyList<WorkflowFile> Workflows { get; } = [new(".github/workflows/ci.yml", "name: ci\non: [push, pull_request]\njobs: {}\n")];

    /// <summary>
    /// <paramref name="pull"/> with GitHub's <c>changed_files</c> set to what <paramref name="change"/>'s diff holds, unless the
    /// test set it (e.g. to a different count, to show an incomplete diff blocks).
    /// </summary>
    public static PullFacts Counting(PullFacts pull, ChangeFacts change) =>
        pull.ChangedFiles is null && change.Diff is not null ? pull with { ChangedFiles = DiffPaths.Parse(change.Diff).Files } : pull;

    /// <summary>A one-file diff of <paramref name="path"/> adding <paramref name="lines"/> lines.</summary>
    public static string Diff(string path, int lines = 1, string marker = "change") =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1,{lines} @@\n"
        + string.Concat(Enumerable.Range(0, lines).Select(i => $"+{marker} {i}\n"));
}
