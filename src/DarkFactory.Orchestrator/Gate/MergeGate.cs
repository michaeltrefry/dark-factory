using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// A pull request as the gate sees it, read fresh from GitHub. <see cref="ChangedFiles"/>: GitHub's <c>changed_files</c>
/// (null when not read), which the gate holds the diff's file count to so a diff missing files cannot pass.
/// <see cref="DefaultBranch"/>: the base repository's default branch as the same answer names it (null when it names none), which
/// an item of no plan must target (sc-25872).
/// </summary>
public sealed record PullFacts(int Number, string HtmlUrl, bool Open, bool Merged, bool Draft, string HeadSha, string BaseRef, string BaseSha,
    string? MergeCommitSha, int? ChangedFiles = null, string? DefaultBranch = null);

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
/// <see cref="Overlay"/>: what became of CodeGraph's overlay of the head that this review's sessions asked (sc-25708; null when no
/// role was reviewed here, e.g. every review carried, or the verdict predates it). A report only: it never decides the verdict.
/// </summary>
public sealed record ReviewVerdict(
    [property: JsonPropertyName("sha")] string HeadSha,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("risky")] IReadOnlyList<string> RiskyPaths,
    [property: JsonPropertyName("reviews")] IReadOnlyList<RoleReview> Reviews,
    [property: JsonPropertyName("codegraph"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CodeGraphOverlay? Overlay = null)
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
    /// <summary>
    /// Nothing blocks but <c>risk-threshold</c>'s CodeGraph inputs, which could not be read (sc-25709, E4): the gate waits and reads
    /// everything again, like pending CI, and escalates once <c>Gate:CiTimeoutMinutes</c> have passed.
    /// </summary>
    Pending,
}

/// <summary>
/// The gate's decision on one evaluation, with every reason it did not merge. <see cref="Inputs"/>: CodeGraph's inputs to
/// <c>risk-threshold</c> (<see cref="CodeGraphRisk.Describe"/>, sc-25709), recorded in the <c>gate</c> checkpoint with the decision.
/// </summary>
public sealed record GateDecision(GateOutcome Outcome, string HeadSha, IReadOnlyList<string> Reasons, string? Inputs = null)
{
    /// <summary>The pull request was judged as a plan's changeset (<see cref="ChangeFacts.Changeset"/>): an input fact, recorded with the decision.</summary>
    public bool Changeset { get; init; }

    /// <summary>How <see cref="Detail"/> marks a decision on a changeset pull request, after the head.</summary>
    public const string ChangesetMark = " (changeset pull request)";

    public string Detail =>
        $"{Outcome} {Ci.Short(HeadSha)}{(Changeset ? ChangesetMark : "")}: {string.Join("; ", Reasons)}{(Inputs is null ? "" : $" | {Inputs}")}";
}

/// <summary>
/// The change the gate judges, read for this evaluation: the unified diff of the candidate head against the base
/// (<see cref="Diff"/>, or <see cref="DiffError"/> when it could not be read) and the fix rounds the ledger records since
/// the last Implement. <see cref="Changeset"/>: the pull request is a plan's changeset pull request (sc-25873) — the ledger's fact
/// (the item is a plan item whose recorded changeset branch is the pull request's head), never a reading of the branch's name, which
/// an adopted or worker-chosen name could imitate. It only selects which size limits <c>risk-threshold</c> applies
/// (<see cref="RiskThreshold.LinesLimit"/>); no check is skipped for it.
/// </summary>
public sealed record ChangeFacts(string? Diff, string? DiffError, int FixRounds, bool Changeset = false);

/// <summary>
/// The merge gate (E1): deterministic code over facts read fresh for this evaluation — the policy text from the base
/// branch, the PR, the diff at its head commit, that commit's CI, the ledger's verdicts and fix rounds, and CodeGraph's blast
/// radius and hotspot score of each touched file (sc-25709: numbers CodeGraph computes from the code, read like CI). No model
/// feeds it; there is no bypass (E2). The touched paths and their tiers come from the diff at the head (E3), never
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
    /// <param name="risk">
    /// CodeGraph's inputs to <c>risk-threshold</c> for this head (sc-25709: each touched file's blast radius at the head, its hotspot
    /// score on the default branch), or null when they were not read (no CodeGraph configured); required only when a touched tier
    /// lists <c>risk-threshold</c>, and then null fails it (E4). Inputs that could not be read make the decision
    /// <see cref="GateOutcome.Pending"/> when nothing else blocks.
    /// </param>
    /// <param name="riskUnread">
    /// CodeGraph is configured but its inputs are not read yet (<paramref name="risk"/> null): the pipeline evaluates everything else
    /// first, so CodeGraph is asked only about a change nothing else blocks. Then a change that needs <c>risk-threshold</c> and that
    /// nothing else blocks is <see cref="GateOutcome.Pending"/> (never Merge) until it is evaluated again with the inputs.
    /// </param>
    public static GateDecision Evaluate(string? policyText, string? policyError, PullFacts pull, ChangeFacts change, CiFacts ci,
        IReadOnlyList<ReviewVerdict> verdicts, NewTestsResult? newTests = null, CodeGraphRisk? risk = null, bool riskUnread = false) =>
        Decide(policyText, policyError, pull, change, ci, verdicts, newTests, risk, riskUnread) with { Changeset = change.Changeset };

    private static GateDecision Decide(string? policyText, string? policyError, PullFacts pull, ChangeFacts change, CiFacts ci,
        IReadOnlyList<ReviewVerdict> verdicts, NewTestsResult? newTests, CodeGraphRisk? risk, bool riskUnread)
    {
        var head = pull.HeadSha;
        var reasons = new List<string>();

        if (policyError is not null)
        {
            return GateCheckSeam.Off(GateCheckSeam.PreconditionPolicy) ? SeamMerge(head, GateCheckSeam.PreconditionPolicy)
                : new GateDecision(GateOutcome.Blocked, head, [$"{GatePolicy.Path} on {pull.BaseRef} could not be read: {policyError}"]);
        }
        if (policyText is null)
        {
            return GateCheckSeam.Off(GateCheckSeam.PreconditionPolicy) ? SeamMerge(head, GateCheckSeam.PreconditionPolicy)
                : new GateDecision(GateOutcome.Blocked, head, [$"{GatePolicy.Path} does not exist on {pull.BaseRef}"]);
        }
        GatePolicy policy;
        try
        {
            policy = GatePolicy.Parse(policyText);
        }
        catch (GatePolicyException ex)
        {
            return GateCheckSeam.Off(GateCheckSeam.PreconditionPolicy) ? SeamMerge(head, GateCheckSeam.PreconditionPolicy)
                : new GateDecision(GateOutcome.Blocked, head, [ex.Message]);
        }

        if ((!pull.Open || pull.Merged) && !GateCheckSeam.Off(GateCheckSeam.PreconditionPrOpen))
        {
            return new GateDecision(GateOutcome.Blocked, head, [$"PR #{pull.Number} is not open"]);
        }

        // The tiers of the paths the head actually changes (a check that cannot run counts as failed: an unread diff blocks).
        if ((change.DiffError is not null || change.Diff is null) && !GateCheckSeam.Off(GateCheckSeam.PreconditionDiffComplete))
        {
            return new GateDecision(GateOutcome.Blocked, head,
                [$"the diff of {Ci.Short(head)} could not be read ({change.DiffError ?? "no diff"}), so the tiers of its paths are unknown"]);
        }
        var diff = change.Diff ?? "";
        // A diff that leaves files out (e.g. GitHub trimming a very large comparison) could leave out a sealed path: its file
        // count must equal the PR's changed_files (a rename is one of each).
        var diffFiles = DiffPaths.Parse(diff).Files;
        if (pull.ChangedFiles != diffFiles && !GateCheckSeam.Off(GateCheckSeam.PreconditionDiffComplete))
        {
            return new GateDecision(GateOutcome.Blocked, head,
                [$"the diff of {Ci.Short(head)} is incomplete: it has {diffFiles} file(s), the PR {pull.ChangedFiles?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unread number of"} changed file(s), so the tiers of its paths are unknown"]);
        }
        var classified = policy.Classify(diff);
        var sealedPaths = classified.In(Tier.Sealed);
        if (sealedPaths.Count > 0 && !GateCheckSeam.Off(GateCheckSeam.SealedEscalation))
        {
            reasons.Add($"{Ci.Short(head)} touches sealed path(s), which always escalate: {string.Join(", ", sealedPaths)}");
        }

        // review-pass — a panel verdict on this exact head commit: every required role reviewed, every review and second
        // opinion served on the high model class (the router's X-Weave-Model-Class; missing fails), no blocking finding left.
        var verdict = verdicts.LastOrDefault(v => v.HeadSha == head);
        if (verdict is null)
        {
            return reasons.Count > 0
                ? new GateDecision(GateOutcome.Blocked, head, reasons)
                : new GateDecision(GateOutcome.ReviewHead, head, [$"head {Ci.Short(head)} has no review verdict"]);
        }
        var reviewPass = !GateCheckSeam.Off(GateChecks.ReviewPass);
        if (!verdict.Passed && reviewPass)
        {
            reasons.Add($"the review of {Ci.Short(head)} is '{verdict.Verdict}'");
        }
        // security-review: required by the tiers the gate derived itself or the code floor of risky paths (or by the
        // verdict's own risky paths).
        var requiredBy = policy.SecurityReviewReasons(classified);
        var security = (requiredBy.Count > 0 || verdict.RiskyPaths.Count > 0) && !GateCheckSeam.Off(GateChecks.SecurityReview);
        foreach (var role in ReviewRoles.Required(security)
                     .Where(r => verdict.Reviews.All(v => v.Role != r) && (reviewPass || r == ReviewRoles.Security)))
        {
            reasons.Add($"the review of {Ci.Short(head)} has no {role} review"
                + (role == ReviewRoles.Security && requiredBy.Count > 0 ? $" ({GateChecks.SecurityReview} is required by {string.Join(", ", requiredBy)})" : ""));
        }
        var modelProblems = reviewPass ? verdict.Reviews.SelectMany(ReviewModels.Problems).ToList() : [];
        reasons.AddRange(modelProblems);

        // ci-green — on this exact head commit.
        var ciGreen = !GateCheckSeam.Off(GateChecks.CiGreen);
        if (ciGreen && ci.HeadSha != head)
        {
            reasons.Add($"CI was read for {Ci.Short(ci.HeadSha)}, not the head {Ci.Short(head)}");
        }
        else if (ciGreen && Ci.Evaluate(ci) is var (state, why) && state != CiState.Green)
        {
            reasons.Add(why);
        }

        // risk-threshold — diff size (a changeset pull request against the policy's changeset limits when it has them, sc-25873),
        // fix rounds and CodeGraph's inputs (sc-25709), when a touched tier requires it.
        var riskRequired = classified.Requires(GateChecks.RiskThreshold) && !GateCheckSeam.Off(GateChecks.RiskThreshold);
        var pending = new List<string>();
        var (limit, limitBy) = BlastRadiusLimit(policy.Risk);
        var unread = riskRequired && risk is null && riskUnread;
        var (maxLines, linesKey) = policy.Risk.LinesLimit(change.Changeset);
        var (maxFiles, filesKey) = policy.Risk.FilesLimit(change.Changeset);
        // Which limit judged a changeset pull request, said with it: its own, or the ordinary one where the policy has no other.
        var sized = !change.Changeset ? ""
            : policy.Risk.MaxChangesetLines is null
                ? " (a changeset pull request, held to the ordinary limits: this policy's version has no max_changeset_lines/max_changeset_files)"
                : " (a changeset pull request)";
        if (riskRequired)
        {
            var threshold = policy.Risk;
            if (classified.ChangedLines > maxLines)
            {
                reasons.Add($"risk threshold: {classified.ChangedLines} changed lines exceed {linesKey} {maxLines}{sized}");
            }
            if (classified.ChangedFiles > maxFiles)
            {
                reasons.Add($"risk threshold: {classified.ChangedFiles} changed files exceed {filesKey} {maxFiles}{sized}");
            }
            if (change.FixRounds > threshold.MaxFixRounds)
            {
                reasons.Add($"risk threshold: {change.FixRounds} fix rounds exceed max_fix_rounds {threshold.MaxFixRounds}");
            }
            if (!unread)
            {
                CodeGraphReasons(threshold, limit, limitBy, head, TouchedFiles(diff), risk, reasons, pending);
            }
        }
        var inputs = riskRequired ? risk?.Describe(limit, policy.Risk.MaxHotspot) : null;

        // new-tests-fail-on-base — the executed runs recorded for exactly this base and head (sc-25382).
        var newTestsRequired = classified.Requires(GateChecks.NewTestsFailOnBase) && !GateCheckSeam.Off(GateChecks.NewTestsFailOnBase);
        if (newTestsRequired)
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

        if (pull.Draft && !GateCheckSeam.Off(GateCheckSeam.PreconditionPrOpen))
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
                    [$"the verdict on {Ci.Short(head)} was recorded under an earlier panel rule", .. modelProblems], inputs)
                : new GateDecision(GateOutcome.Blocked, head, [.. reasons, .. pending], inputs);
        }
        if (pending.Count > 0)
        {
            // Only CodeGraph's inputs are missing: wait for them like pending CI (E4: never merged without them).
            return new GateDecision(GateOutcome.Pending, head, pending, inputs);
        }
        if (unread)
        {
            // Everything else holds: CodeGraph's inputs are read now, and the gate evaluated again with them.
            return new GateDecision(GateOutcome.Pending, head, [$"risk threshold: CodeGraph's inputs for {Ci.Short(head)} are still to be read"]);
        }
        var tiers = Tiers.Precedence.Where(t => classified.In(t).Count > 0).Select(t => $"{t.Key()} {classified.In(t).Count}");
        var passed = new List<string>
        {
            $"paths: {string.Join(", ", tiers)}; checks: {string.Join(", ", GateChecks.All.Where(classified.Requires))}",
            "ci green",
            $"review pass by {string.Join(", ", verdict.Reviews.Select(r => $"{r.Role}: {r.ServedName}{(r.CarriedFrom is { } carried ? $", carried from {Ci.Short(carried)}" : "")}"))}",
        };
        if (riskRequired)
        {
            var worst = risk!.Worst;
            var hottest = risk.Hottest;
            var size = change.Changeset
                ? $"{classified.ChangedLines} changed lines within {linesKey} {maxLines}, {classified.ChangedFiles} files within {filesKey} {maxFiles}"
                : $"{classified.ChangedLines} changed lines, {classified.ChangedFiles} files";
            passed.Add($"risk within threshold ({size}, {change.FixRounds} fix rounds, "
                + $"worst blast radius {(worst is null ? "none (no file)" : $"{BlastRadii.Name(worst.Radius)} ({worst.Path})")}, "
                + $"highest hotspot score {(hottest is null ? "none (no file)" : $"{CodeGraphRisk.Score(hottest.Score)} ({hottest.Path})")}"
                + $"{(policy.Risk.MaxHotspot is { } max ? $" within max_hotspot {CodeGraphRisk.Score(max)}" : ", max_hotspot off")})");
        }
        if (newTestsRequired)
        {
            passed.Add(newTests!.Reason);
        }
        return new GateDecision(GateOutcome.Merge, head, passed, inputs);
    }

    /// <summary>The repository files a diff touches (both sides of a rename, deletes too), normalized: the files CodeGraph is asked about.</summary>
    public static IReadOnlyList<string> TouchedFiles(string diff) =>
        DiffPaths.Parse(diff).Paths.Select(RepoPath.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Where <c>CodeGraph:Token</c> comes from, as the gate's reason and the start-up warning name it.</summary>
    public const string CodeGraphTokenSources =
        "CodeGraph:Token, env FACTORY_CODEGRAPH_TOKEN, or keychain account 'codegraph-token' (service 'dark-factory')";

    /// <summary>
    /// The blast radius <c>risk-threshold</c> allows and what sets it: the policy's <c>max_blast_radius</c> when it is no looser than the
    /// code floor (version 3 can only tighten it), else the floor (<see cref="GatePolicy.BlastRadiusFloor"/>: critical always fails).
    /// </summary>
    private static (BlastRadius? Limit, string By) BlastRadiusLimit(RiskThreshold threshold)
    {
        if (GateCheckSeam.Off(GateCheckSeam.FloorBlastRadius))
        {
            return threshold.MaxBlastRadius is { } policyMax ? (policyMax, $"max_blast_radius {BlastRadii.Name(policyMax)}") : (null, "");
        }
        return threshold.MaxBlastRadius is { } max && max <= GatePolicy.BlastRadiusFloor
            ? (max, $"max_blast_radius {BlastRadii.Name(max)}")
            : (GatePolicy.BlastRadiusFloor, $"the code floor ({BlastRadii.Name(BlastRadius.Critical)} always fails)");
    }

    /// <summary>
    /// <c>risk-threshold</c>'s CodeGraph inputs (sc-25709): each touched file's blast radius at exactly the head must be within
    /// <paramref name="limit"/> (<see cref="BlastRadiusLimit"/>), and, when the policy sets <c>max_hotspot</c>, each file's hotspot score
    /// (CodeGraph's riskScore, on the default branch) within it. Inputs not read at all (no CodeGraph configured), read for another
    /// commit or leaving a touched file out block. A file read over a limit blocks even when other inputs could not be read; otherwise
    /// inputs CodeGraph could not read are pending.
    /// </summary>
    private static void CodeGraphReasons(RiskThreshold threshold, BlastRadius? limit, string by, string head, IReadOnlyList<string> files,
        CodeGraphRisk? risk, List<string> reasons, List<string> pending)
    {
        if (risk is null)
        {
            reasons.Add($"risk threshold: CodeGraph's blast radius and hotspots were not read for {Ci.Short(head)} (CodeGraph is not configured: "
                + $"no CodeGraph token; set {CodeGraphTokenSources})");
            return;
        }
        if (!string.Equals(risk.HeadSha, head, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"risk threshold: CodeGraph's inputs were read for {Ci.Short(risk.HeadSha)}, not the head {Ci.Short(head)}");
            return;
        }
        var over = risk.BlastRadius.Where(f => limit is { } l && f.Radius > l).OrderByDescending(f => f.Radius).ToList();
        if (over.Count > 0)
        {
            reasons.Add($"risk threshold: blast radius at {Ci.Short(head)} over {by}: {string.Join(", ", over.Select(f => $"{f.Path} {BlastRadii.Name(f.Radius)}"))}");
        }
        if (threshold.MaxHotspot is { } maxHotspot)
        {
            var hot = risk.Hotspots.Where(f => f.Score > maxHotspot).OrderByDescending(f => f.Score).ToList();
            if (hot.Count > 0)
            {
                reasons.Add($"risk threshold: hotspot score (riskScore) over max_hotspot {CodeGraphRisk.Score(maxHotspot)}: "
                    + string.Join(", ", hot.Select(f => $"{f.Path} {CodeGraphRisk.Score(f.Score)} on the default branch {f.CommitLabel}")));
            }
        }
        if (risk.Unreadable.Count > 0)
        {
            // The files not read yet are left out by design: pending, or named beside a file already over a limit (which blocks).
            pending.Add($"risk threshold: CodeGraph could not be read for {Ci.Short(head)}: {risk.UnreadableText}");
            return;
        }
        var missing = files.Where(f => risk.BlastRadius.All(b => b.Path != f) || risk.Hotspots.All(h => h.Path != f)).ToList();
        if (missing.Count > 0)
        {
            reasons.Add($"risk threshold: CodeGraph's inputs leave out {string.Join(", ", missing)}");
        }
    }

    /// <summary>The decision when <see cref="GateCheckSeam"/> disabled the policy precondition and the policy is unusable (coverage tests only).</summary>
    private static GateDecision SeamMerge(string head, string check) => new(GateOutcome.Merge, head, [$"{check} disabled by the coverage test seam"]);

    /// <summary>
    /// Whether <paramref name="verdict"/> was recorded under an earlier panel rule (e.g. pinned reviewer models with no served
    /// class, before sc-25626) and is replaced by one review from the current panel: its calls break <see cref="ReviewModels.Problems"/>
    /// and it is the only verdict on its head among <paramref name="verdicts"/>. The current panel's verdict on that head is a
    /// second one, so it never qualifies: a head is re-reviewed for this at most once, and a fresh verdict whose calls still
    /// break the rule escalates.
    /// </summary>
    public static bool Superseded(ReviewVerdict verdict, IReadOnlyList<ReviewVerdict> verdicts) =>
        verdicts.Count(v => v.HeadSha == verdict.HeadSha) == 1 && verdict.Reviews.SelectMany(ReviewModels.Problems).Any();
}
