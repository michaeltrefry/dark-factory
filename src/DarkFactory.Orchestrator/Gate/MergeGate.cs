using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A pull request as the gate sees it, read fresh from GitHub.</summary>
public sealed record PullFacts(int Number, string HtmlUrl, bool Open, bool Merged, bool Draft, string HeadSha, string BaseRef, string BaseSha,
    string? MergeCommitSha);

/// <summary>One CI check of a commit: a check run, or a commit status mapped onto the same shape.</summary>
public sealed record CheckFact(string Name, bool Completed, string? Conclusion);

/// <summary>
/// One check suite of a commit: what an App (e.g. <c>github-actions</c>, one suite per workflow run) registered for it, before
/// or alongside its check runs. <see cref="Runs"/> is how many check runs it has.
/// </summary>
public sealed record CheckSuiteFact(string App, bool Completed, string? Conclusion, int Runs);

/// <summary>
/// Every CI check and check suite GitHub reports for one commit. <see cref="Complete"/> is false when GitHub reported more
/// than was read.
/// </summary>
public sealed record CiFacts(string HeadSha, IReadOnlyList<CheckFact> Checks, bool Complete = true, IReadOnlyList<CheckSuiteFact>? Suites = null);

public enum CiState
{
    /// <summary>At least one check, every one finished with success, neutral or skipped.</summary>
    Green,
    /// <summary>A check is still running (or none has started yet).</summary>
    Pending,
    /// <summary>A check failed, or the checks could not all be read (a check that cannot run counts as failed, E2).</summary>
    Failed,
}

public static class Ci
{
    private static readonly HashSet<string> Passing = new(StringComparer.Ordinal) { "success", "neutral", "skipped" };

    /// <summary>GitHub Actions' App: it registers one check suite per workflow run, before that run's check runs exist.</summary>
    public const string ActionsApp = "github-actions";

    /// <summary>
    /// The commit's CI state, and why when it is not green. Green needs at least one check, every check finished with a
    /// passing conclusion, and every check suite finished: a workflow whose run is registered (its suite) but has no check
    /// run yet would otherwise be invisible, and CI would read green early; a suite that finished without passing (e.g. a
    /// workflow's <c>startup_failure</c>, which has no check run) fails. A suite of another App with no check runs is
    /// ignored: GitHub creates one for every installed App with checks access, and an App that never runs checks (e.g. the
    /// Claude App) leaves it queued forever. Limitation: a workflow GitHub has not registered at all
    /// yet (no suite: e.g. one triggered later by another workflow) is still invisible.
    /// </summary>
    public static (CiState State, string Why) Evaluate(CiFacts facts)
    {
        if (!facts.Complete)
        {
            return (CiState.Failed, $"more CI checks on {Short(facts.HeadSha)} than the gate could read");
        }
        var suites = CountedSuites(facts);
        var failed = facts.Checks.Where(c => c.Completed && !Passing.Contains(c.Conclusion ?? "")).Select(c => $"{c.Name} ({c.Conclusion ?? "no conclusion"})")
            .Concat(suites.Where(s => s.Completed && !Passing.Contains(s.Conclusion ?? "")).Select(s => $"{s.App} check suite ({s.Conclusion ?? "no conclusion"})"))
            .ToList();
        if (failed.Count > 0)
        {
            return (CiState.Failed, $"CI failed on {Short(facts.HeadSha)}: {string.Join(", ", failed)}");
        }
        if (facts.Checks.Count == 0)
        {
            return (CiState.Pending, $"no CI check has reported on {Short(facts.HeadSha)}");
        }
        var running = facts.Checks.Where(c => !c.Completed).Select(c => c.Name)
            .Concat(suites.Where(s => !s.Completed).Select(s => $"{s.App} check suite"))
            .ToList();
        return running.Count > 0
            ? (CiState.Pending, $"CI still running on {Short(facts.HeadSha)}: {string.Join(", ", running)}")
            : (CiState.Green, $"CI green on {Short(facts.HeadSha)} ({facts.Checks.Count} checks)");
    }

    /// <summary>The check suites CI counts: GitHub Actions' and any other App's with check runs (see <see cref="Evaluate"/>).</summary>
    public static IReadOnlyList<CheckSuiteFact> CountedSuites(CiFacts facts) =>
        (facts.Suites ?? []).Where(s => s.Runs > 0 || s.App == ActionsApp).ToList();

    /// <summary>Whether a finished check's conclusion passes (success, neutral or skipped).</summary>
    public static bool Passes(string? conclusion) => Passing.Contains(conclusion ?? "");

    internal static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}

/// <summary>
/// The review panel's verdict on one head commit (E3: bound to <see cref="HeadSha"/>; a new push voids it). Stored as JSON
/// in the ledger's <c>verdict</c> checkpoint. <see cref="RiskyPaths"/>: the touched paths that called the security review
/// in; <see cref="Reviews"/>: each role's review with its findings after confirmation (<see cref="ReviewPanel.Decide"/>).
/// </summary>
public sealed record ReviewVerdict(
    [property: JsonPropertyName("sha")] string HeadSha,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("risky")] IReadOnlyList<string> RiskyPaths,
    [property: JsonPropertyName("reviews")] IReadOnlyList<RoleReview> Reviews)
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    private const int MaxSummary = 4000;

    [JsonIgnore]
    public bool Passed => Verdict == Pass;

    public string ToDetail() => JsonSerializer.Serialize(this with { Summary = Summary.Length > MaxSummary ? Summary[..MaxSummary] : Summary });

    /// <summary>The verdict a ledger detail holds, or null when it is not one.</summary>
    public static ReviewVerdict? FromDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ReviewVerdict>(detail) is { HeadSha: not null, Verdict: not null } v
                ? v with { RiskyPaths = v.RiskyPaths ?? [], Reviews = v.Reviews ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum GateOutcome
{
    /// <summary>Every rule holds for the head commit: merge exactly that commit.</summary>
    Merge,
    /// <summary>The head commit has no verdict (a push after the review voided it, E3): review it again first.</summary>
    ReviewHead,
    /// <summary>A rule does not hold, or the policy is unreadable or invalid: no merge, escalate.</summary>
    Blocked,
}

/// <summary>The gate's decision on one evaluation, with every reason it did not merge.</summary>
public sealed record GateDecision(GateOutcome Outcome, string HeadSha, IReadOnlyList<string> Reasons)
{
    public string Detail => $"{Outcome} {Ci.Short(HeadSha)}: {string.Join("; ", Reasons)}";
}

/// <summary>
/// The merge gate (E1): deterministic code over facts read fresh for this evaluation — the policy text from the base
/// branch, the PR, its head commit's CI and the ledger's verdicts. No model and no score feeds it; there is no bypass (E2).
/// </summary>
public static class MergeGate
{
    /// <param name="policyText">The base branch's <c>factory/gate.yaml</c>, or null when it does not exist.</param>
    /// <param name="policyError">Why the policy could not be read at all (e.g. GitHub refused); overrides <paramref name="policyText"/>.</param>
    /// <param name="verdicts">Every verdict in the item's ledger, oldest first.</param>
    /// <param name="implementerModels">Every model the implementer's sessions reported.</param>
    public static GateDecision Evaluate(string? policyText, string? policyError, PullFacts pull, CiFacts ci,
        IReadOnlyList<ReviewVerdict> verdicts, IReadOnlyCollection<string> implementerModels)
    {
        var head = pull.HeadSha;
        var reasons = new List<string>();

        if (policyError is not null)
        {
            return new GateDecision(GateOutcome.Blocked, head, [$"{GatePolicy.Path} on {pull.BaseRef} could not be read: {policyError}"]);
        }
        if (policyText is null)
        {
            return new GateDecision(GateOutcome.Blocked, head, [$"{GatePolicy.Path} does not exist on {pull.BaseRef}"]);
        }
        try
        {
            GatePolicy.Parse(policyText);
        }
        catch (GatePolicyException ex)
        {
            return new GateDecision(GateOutcome.Blocked, head, [ex.Message]);
        }

        if (!pull.Open || pull.Merged)
        {
            return new GateDecision(GateOutcome.Blocked, head, [$"PR #{pull.Number} is not open"]);
        }

        // review: pass — a panel verdict on this exact head commit: every required role reviewed, every reviewer and second
        // model of a family the implementer did not use, no blocking finding left.
        var verdict = verdicts.LastOrDefault(v => v.HeadSha == head);
        if (verdict is null)
        {
            return new GateDecision(GateOutcome.ReviewHead, head, [$"head {Ci.Short(head)} has no review verdict"]);
        }
        if (!verdict.Passed)
        {
            reasons.Add($"the review of {Ci.Short(head)} is '{verdict.Verdict}'");
        }
        foreach (var role in ReviewRoles.Required(verdict.RiskyPaths.Count > 0).Where(r => verdict.Reviews.All(v => v.Role != r)))
        {
            reasons.Add($"the review of {Ci.Short(head)} has no {role} review");
        }
        try
        {
            var families = ReviewerChoice.ImplementerFamilies(implementerModels);
            var models = verdict.Reviews.Select(r => (What: $"the {r.Role} reviewer", Served: r.ServedModel ?? r.Model, r.Family))
                .Concat(verdict.Reviews.SelectMany(r => r.Findings).Select(f => f.Confirmation).OfType<Confirmation>()
                    .Select(c => (What: "a second model", Served: c.ServedModel ?? c.Model, c.Family)));
            foreach (var (what, served, family) in models.Where(m => m.Family is null || families.Contains(m.Family)))
            {
                reasons.Add($"{what} ({served}, family {family ?? "unknown"}) is not of a family other than the implementer's ({string.Join(", ", families)})");
            }
        }
        catch (ReviewerChoiceException ex)
        {
            reasons.Add(ex.Message);
        }

        // ci: green — on this exact head commit.
        if (ci.HeadSha != head)
        {
            reasons.Add($"CI was read for {Ci.Short(ci.HeadSha)}, not the head {Ci.Short(head)}");
        }
        else if (Ci.Evaluate(ci) is var (state, why) && state != CiState.Green)
        {
            reasons.Add(why);
        }

        if (pull.Draft)
        {
            reasons.Add($"PR #{pull.Number} is a draft");
        }
        return reasons.Count > 0
            ? new GateDecision(GateOutcome.Blocked, head, reasons)
            : new GateDecision(GateOutcome.Merge, head,
                ["ci green", $"review pass by {string.Join(", ", verdict.Reviews.Select(r => $"{r.Role}: {r.ServedModel ?? r.Model} ({r.Family}){(r.CarriedFrom is { } carried ? $", carried from {Ci.Short(carried)}" : "")}"))}"]);
    }
}
