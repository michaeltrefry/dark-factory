using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// A pull request as the gate sees it, read fresh from GitHub. <see cref="ChangedFiles"/>: GitHub's <c>changed_files</c>
/// (null when not read), which the gate holds the diff's file count to so a diff missing files cannot pass.
/// </summary>
public sealed record PullFacts(int Number, string HtmlUrl, bool Open, bool Merged, bool Draft, string HeadSha, string BaseRef, string BaseSha,
    string? MergeCommitSha, int? ChangedFiles = null);

/// <summary>
/// One CI check of a commit: a check run, or a commit status mapped onto the same shape. <see cref="Id"/>: the check run's id
/// (for GitHub Actions also its job's id, whose log a CI fixer is given); null for a commit status.
/// </summary>
public sealed record CheckFact(string Name, bool Completed, string? Conclusion, long? Id = null);

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
/// in the ledger's <c>verdict</c> checkpoint. <see cref="RiskyPaths"/>: the touched paths that called the security review in
/// (<see cref="GatePolicy.SecurityReviewReasons"/>: by their tier, named with it, or by the code floor, named with why); <see cref="Reviews"/>: each role's review with its findings after confirmation (<see cref="ReviewPanel.Decide"/>).
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
    /// <summary>
    /// The head commit has no verdict (a push after the review voided it, E3), or only one recorded under an earlier panel
    /// rule (<see cref="MergeGate.Superseded"/>): review it again first.
    /// </summary>
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
/// The change the gate judges, read for this evaluation: the unified diff of the candidate head against the base
/// (<see cref="Diff"/>, or <see cref="DiffError"/> when it could not be read) and the fix rounds the ledger records since
/// the last Implement.
/// </summary>
public sealed record ChangeFacts(string? Diff, string? DiffError, int FixRounds);

/// <summary>
/// The merge gate (E1): deterministic code over facts read fresh for this evaluation — the policy text from the base
/// branch, the PR, the diff at its head commit, that commit's CI, the ledger's verdicts and fix rounds. No model and no
/// score feeds it; there is no bypass (E2). The touched paths and their tiers come from the diff at the head (E3), never
/// from the plan, the PR or a worker's claim.
/// </summary>
public static class MergeGate
{
    /// <param name="policyText">The base branch's <c>factory/gate.yaml</c>, or null when it does not exist.</param>
    /// <param name="policyError">Why the policy could not be read at all (e.g. GitHub refused); overrides <paramref name="policyText"/>.</param>
    /// <param name="change">The diff of the PR's head commit against its base, and the fix rounds used.</param>
    /// <param name="verdicts">Every verdict in the item's ledger, oldest first.</param>
    /// <param name="newTests">
    /// The recorded result of the <c>new-tests-fail-on-base</c> check for this base and head (<see cref="NewTestsCheck"/>),
    /// or null when it was not run; required only when a touched tier lists the check, and then null fails it (E2).
    /// </param>
    public static GateDecision Evaluate(string? policyText, string? policyError, PullFacts pull, ChangeFacts change, CiFacts ci,
        IReadOnlyList<ReviewVerdict> verdicts, NewTestsResult? newTests = null)
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
        GatePolicy policy;
        try
        {
            policy = GatePolicy.Parse(policyText);
        }
        catch (GatePolicyException ex)
        {
            return new GateDecision(GateOutcome.Blocked, head, [ex.Message]);
        }

        if (!pull.Open || pull.Merged)
        {
            return new GateDecision(GateOutcome.Blocked, head, [$"PR #{pull.Number} is not open"]);
        }

        // The tiers of the paths the head actually changes (a check that cannot run counts as failed: an unread diff blocks).
        if (change.DiffError is not null || change.Diff is null)
        {
            return new GateDecision(GateOutcome.Blocked, head,
                [$"the diff of {Ci.Short(head)} could not be read ({change.DiffError ?? "no diff"}), so the tiers of its paths are unknown"]);
        }
        // A diff that leaves files out (e.g. GitHub trimming a very large comparison) could leave out a sealed path: its file
        // count must equal the PR's changed_files (a rename is one of each).
        var diffFiles = DiffPaths.Parse(change.Diff).Files;
        if (pull.ChangedFiles != diffFiles)
        {
            return new GateDecision(GateOutcome.Blocked, head,
                [$"the diff of {Ci.Short(head)} is incomplete: it has {diffFiles} file(s), the PR {pull.ChangedFiles?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unread number of"} changed file(s), so the tiers of its paths are unknown"]);
        }
        var classified = policy.Classify(change.Diff);
        var sealedPaths = classified.In(Tier.Sealed);
        if (sealedPaths.Count > 0)
        {
            reasons.Add($"{Ci.Short(head)} touches sealed path(s), which always escalate: {string.Join(", ", sealedPaths)}");
        }

        // review-pass — a panel verdict on this exact head commit: every required role reviewed, every reviewer and second
        // model a Claude model served as pinned (every reviewer a Claude Opus 5.5 or newer), no blocking finding left.
        var verdict = verdicts.LastOrDefault(v => v.HeadSha == head);
        if (verdict is null)
        {
            return reasons.Count > 0
                ? new GateDecision(GateOutcome.Blocked, head, reasons)
                : new GateDecision(GateOutcome.ReviewHead, head, [$"head {Ci.Short(head)} has no review verdict"]);
        }
        if (!verdict.Passed)
        {
            reasons.Add($"the review of {Ci.Short(head)} is '{verdict.Verdict}'");
        }
        // security-review: required by the tiers the gate derived itself or the code floor of risky paths (or by the
        // verdict's own risky paths).
        var requiredBy = policy.SecurityReviewReasons(classified);
        foreach (var role in ReviewRoles.Required(requiredBy.Count > 0 || verdict.RiskyPaths.Count > 0).Where(r => verdict.Reviews.All(v => v.Role != r)))
        {
            reasons.Add($"the review of {Ci.Short(head)} has no {role} review"
                + (role == ReviewRoles.Security && requiredBy.Count > 0 ? $" ({GateChecks.SecurityReview} is required by {string.Join(", ", requiredBy)})" : ""));
        }
        var modelProblems = verdict.Reviews.SelectMany(ReviewModels.Problems).ToList();
        reasons.AddRange(modelProblems);

        // ci-green — on this exact head commit.
        if (ci.HeadSha != head)
        {
            reasons.Add($"CI was read for {Ci.Short(ci.HeadSha)}, not the head {Ci.Short(head)}");
        }
        else if (Ci.Evaluate(ci) is var (state, why) && state != CiState.Green)
        {
            reasons.Add(why);
        }

        // risk-threshold — diff size and fix rounds, when a touched tier requires it.
        if (classified.Requires(GateChecks.RiskThreshold))
        {
            var risk = policy.Risk;
            if (classified.ChangedLines > risk.MaxChangedLines)
            {
                reasons.Add($"risk threshold: {classified.ChangedLines} changed lines exceed max_changed_lines {risk.MaxChangedLines}");
            }
            if (classified.ChangedFiles > risk.MaxChangedFiles)
            {
                reasons.Add($"risk threshold: {classified.ChangedFiles} changed files exceed max_changed_files {risk.MaxChangedFiles}");
            }
            if (change.FixRounds > risk.MaxFixRounds)
            {
                reasons.Add($"risk threshold: {change.FixRounds} fix rounds exceed max_fix_rounds {risk.MaxFixRounds}");
            }
        }

        // new-tests-fail-on-base — the executed runs recorded for exactly this base and head (sc-25382).
        if (classified.Requires(GateChecks.NewTestsFailOnBase))
        {
            if (newTests is null)
            {
                reasons.Add($"{GateChecks.NewTestsFailOnBase}: the tests the PR adds were not run on {Ci.Short(head)}");
            }
            else if (newTests.HeadSha != head || newTests.BaseSha != pull.BaseSha)
            {
                reasons.Add($"{GateChecks.NewTestsFailOnBase}: the tests were run for {Ci.Short(newTests.BaseSha)}...{Ci.Short(newTests.HeadSha)}, not {Ci.Short(pull.BaseSha)}...{Ci.Short(head)}");
            }
            else if (!newTests.Passed)
            {
                reasons.Add($"{GateChecks.NewTestsFailOnBase} ({newTests.Outcome}): {newTests.Reason}");
            }
        }

        if (pull.Draft)
        {
            reasons.Add($"PR #{pull.Number} is a draft");
        }

        // Protected: built and reviewed like any change, but merged only after escalation — the gate escalates instead.
        var protectedPaths = classified.In(Tier.Protected);
        if (protectedPaths.Count > 0)
        {
            reasons.Add($"{Ci.Short(head)} touches protected path(s), merged only after escalation: {string.Join(", ", protectedPaths)}");
        }

        if (reasons.Count > 0)
        {
            // A verdict recorded under an earlier panel rule whose models are its only fault: the current panel reviews the head once.
            return modelProblems.Count > 0 && reasons.All(modelProblems.Contains) && Superseded(verdict, verdicts)
                ? new GateDecision(GateOutcome.ReviewHead, head,
                    [$"the verdict on {Ci.Short(head)} was recorded under an earlier panel rule", .. modelProblems])
                : new GateDecision(GateOutcome.Blocked, head, reasons);
        }
        var tiers = Tiers.Precedence.Where(t => classified.In(t).Count > 0).Select(t => $"{t.Key()} {classified.In(t).Count}");
        var passed = new List<string>
        {
            $"paths: {string.Join(", ", tiers)}; checks: {string.Join(", ", GateChecks.All.Where(classified.Requires))}",
            "ci green",
            $"review pass by {string.Join(", ", verdict.Reviews.Select(r => $"{r.Role}: {r.ServedModel ?? r.Model}{(r.CarriedFrom is { } carried ? $", carried from {Ci.Short(carried)}" : "")}"))}",
        };
        if (classified.Requires(GateChecks.RiskThreshold))
        {
            passed.Add($"risk within threshold ({classified.ChangedLines} changed lines, {classified.ChangedFiles} files, {change.FixRounds} fix rounds)");
        }
        if (classified.Requires(GateChecks.NewTestsFailOnBase))
        {
            passed.Add(newTests!.Reason);
        }
        return new GateDecision(GateOutcome.Merge, head, passed);
    }

    /// <summary>
    /// Whether <paramref name="verdict"/> was recorded under an earlier panel rule (e.g. a GPT or pre-5.5 Opus reviewer, before
    /// sc-25379) and is replaced by one review from the current panel: its models break <see cref="ReviewModels.Problems"/>
    /// and it is the only verdict on its head among <paramref name="verdicts"/>. The current panel's verdict on that head is a
    /// second one, so it never qualifies: a head is re-reviewed for this at most once, and a fresh verdict whose models still
    /// break the rule escalates.
    /// </summary>
    public static bool Superseded(ReviewVerdict verdict, IReadOnlyList<ReviewVerdict> verdicts) =>
        verdicts.Count(v => v.HeadSha == verdict.HeadSha) == 1 && verdict.Reviews.SelectMany(ReviewModels.Problems).Any();
}
