namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary><c>factory/gate.yaml</c> texts the gate tests share.</summary>
internal static class TestPolicies
{
    /// <summary>A valid version-2 policy: every tier, the floor checks, a risk threshold the fix-loop tests stay inside.</summary>
    public static string Standard(int maxLines = 400, int maxFiles = 20, int maxFixRounds = 3, string normalChecks = "ci-green, review-pass, risk-threshold") => $"""
        # Dark Factory merge gate policy (test)
        version: 2
        tiers:
          sealed:
            paths: [factory/gate.yaml, .github/workflows/, CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS, factory/prompts/]
            checks: [ci-green, review-pass, security-review]
          protected:
            paths: ["**/auth/", "**/migrations/", infra/, "**/secrets/", "**/payments/"]
            checks: [ci-green, review-pass, security-review, risk-threshold]
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

    /// <summary>A one-file diff of <paramref name="path"/> adding <paramref name="lines"/> lines.</summary>
    public static string Diff(string path, int lines = 1, string marker = "change") =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1,{lines} @@\n"
        + string.Concat(Enumerable.Range(0, lines).Select(i => $"+{marker} {i}\n"));
}
