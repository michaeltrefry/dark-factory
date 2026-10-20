using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Ledger;

namespace DarkFactory.Orchestrator;

/// <summary>
/// Narrow, read-only admission proof. Never infer truncation from escalation prose: reconstruct the factory's exact failure
/// from a complete structured panel, then require its adjacent escalation and current-attempt session provenance.
/// Legacy panels have no typed stop/budget fields; only the factory's exact historical error is projected.
/// </summary>
internal static class IncompleteReviewEvidence
{
    internal sealed record Evidence(long VerdictId, long EscalationId);
    internal const string LegacyTruncation = "The answer ended early (max_tokens).";

    public static Evidence Require(List<LedgerEntry> history, string head, string pull, IReadOnlyList<string> required)
    {
        static void Check(bool yes) { if (!yes) throw new InvalidOperationException("no unconsumed, complete current-attempt panel failed solely by output truncation with its linked escalation"); }
        var attempt = history.FindLastIndex(e => e.Step is null && e.Detail != "unpaused" && TransitionContext.StartsAttempt(e.State));
        Check(attempt >= 0);
        var rows = history.Skip(attempt + 1).ToList();
        var vi = rows.FindLastIndex(e => e.Step == RunPipeline.Steps.Verdict);
        Check(vi >= 0);
        var row = rows[vi];
        Check(row.State == WorkState.Review && row.Detail is not null);
        using var json = JsonDocument.Parse(row.Detail!);
        Check(NoDuplicates(json.RootElement));
        var root = json.RootElement;
        Check(root.ValueKind == JsonValueKind.Object && new[] { "sha", "verdict", "summary", "risky", "reviews" }.All(n => root.TryGetProperty(n, out _)));
        Check(root.GetProperty("risky").ValueKind == JsonValueKind.Array
            && root.GetProperty("risky").EnumerateArray().All(p => p.ValueKind == JsonValueKind.String));
        Check(root.GetProperty("reviews").ValueKind == JsonValueKind.Array);
        foreach (var reviewJson in root.GetProperty("reviews").EnumerateArray())
        {
            Check(reviewJson.ValueKind == JsonValueKind.Object
                && new[] { "role", "served", "class", "session", "prompt", "findings", "summary", "error", "carried" }.All(n => reviewJson.TryGetProperty(n, out _)));
            Check(reviewJson.GetProperty("findings").ValueKind == JsonValueKind.Array
                && reviewJson.GetProperty("summary").ValueKind == JsonValueKind.String);
            if (reviewJson.TryGetProperty("failure", out var failure))
                Check(failure.ValueKind is JsonValueKind.Null or JsonValueKind.String);
        }
        var v = ReviewVerdict.FromDetail(row.Detail);
        Check(v is { Verdict: ReviewVerdict.Fail } && v.HeadSha == head && v.Reviews.Count > 0);
        var verdict = v!;
        Check(rows.Count(e => e.Step == RunPipeline.Steps.Verdict && ReviewVerdict.FromDetail(e.Detail)?.HeadSha == head) == 1);
        Check(verdict.Reviews.Count == required.Count && required.All(role => verdict.Reviews.Count(r => r is not null && r.Role == role) == 1));
        Check(ReviewRoles.Required(verdict.RiskyPaths.Count > 0).All(role => required.Contains(role)));
        var reviewStart = rows.FindLastIndex(vi, e => e.Step is null && e.State == WorkState.Review && e.Detail != "unpaused");
        Check(reviewStart >= 0);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var legacyPanel = verdict.Reviews.All(r => r.Failure is null && r.Turns is { Count: > 0 }
            && r.Turns.All(t => t is not null && t.StopReason is null && t.MaxOutputTokens is null));
        var truncated = false;
        foreach (var review in verdict.Reviews)
        {
            Check(review.CarriedFrom is null && review.ServedClass == ReviewModels.Class && !string.IsNullOrWhiteSpace(review.ServedModel)
                && review.Findings is not null && !string.IsNullOrWhiteSpace(review.Session) && !string.IsNullOrWhiteSpace(review.Prompt));
            Check(sessions.Add(review.Session!));
            Check(review.Findings!.All(f => f is not null && f.Severity == Finding.Optional && f.Confirmation is null && !f.Downgraded));
            var sessionRows = rows.Skip(reviewStart + 1).Take(vi - reviewStart - 1).Where(e => e.Step == RunPipeline.Steps.ReviewSession
                && e.Detail == $"{review.Session} {ReviewModels.Class} {head} {review.Role} {review.Prompt}").ToList();
            Check(sessionRows.Count == 1);
            Check(review.Turns is { Count: > 0 and <= RouterReviewer.MaxTurns + 1 });
            var turns = review.Turns!;
            Check(turns.Select((t, i) => t is not null && t.Turn == i + 1 && t.ServedClass == ReviewModels.Class
                && !string.IsNullOrWhiteSpace(t.ServedModel)).All(ok => ok));
            Check(turns[^1].ServedModel == review.ServedModel);
            var legacy = legacyPanel;
            if (!legacy)
            {
                Check(turns.All(t => t.MaxOutputTokens is > 0 && t.MaxOutputTokens == turns[0].MaxOutputTokens));
                Check(turns.Take(turns.Count - 1).All(t => t.StopReason == "tool_use"));
            }
            if (review.Error is null)
            {
                Check(review.Failure is null && (legacy || turns[^1].StopReason is "end_turn" or "stop_sequence"));
            }
            else
            {
                // Typed records must agree with their actual turn stop, budget, and deterministic error. Legacy is exact,
                // never a substring (nor a user-supplied reason). A mixed typed/legacy role is not a historical record.
                Check(review.Findings!.Count == 0);
                var expectedError = (turns.Count > RouterReviewer.MaxTurns ? "No usable final answer after the turn cap: " : "") + LegacyTruncation;
                Check(review.Error == expectedError && (legacy
                    || review.Failure == ReviewFailure.OutputTruncated && turns[^1].StopReason == "max_tokens"));
                truncated = true;
            }
        }
        Check(truncated);
        Check(ReviewPanel.Decide(head, verdict.RiskyPaths, verdict.Reviews).Summary == verdict.Summary);
        var after = rows.Skip(vi + 1).Where(e => !SlotUse.IsBookkeeping(e.Step)).ToList();
        Check(after.Count > 0 && after[0].Step is null && after[0].State == WorkState.Escalated);
        var escalation = after[0];
        Check(escalation.Detail == $"ReviewFailedException: The review panel failed {pull} at {Ci.Short(head)}: {verdict.Summary}");
        Check(after.Skip(1).All(e => e.Step == RunPipeline.Steps.EscalationComment));
        foreach (var consumed in history.Where(e => e.Step is null && e.State == WorkState.Recovering && e.Detail != "unpaused"))
        {
            var recovery = JsonSerializer.Deserialize<RunPipeline.Recovery>(consumed.Detail ?? "");
            Check(recovery is not null && recovery.SourceVerdictId != row.Id && recovery.SourceEscalationId != escalation.Id);
        }
        return new(row.Id, escalation.Id);
    }

    private static bool NoDuplicates(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count()
            && value.EnumerateObject().All(p => NoDuplicates(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(NoDuplicates),
        _ => true,
    };
}
