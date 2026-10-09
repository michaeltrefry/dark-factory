using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A blocking finding still open on a head commit, with the panel role that reported it.</summary>
public sealed record OpenFinding(string Role, Finding Finding)
{
    public override string ToString() =>
        $"[{Role}] {Finding}{(Finding.Confirmation is { Outcome: Confirmation.Confirmed } c ? $", confirmed by {c.ServedModel ?? c.Model}" : "")} — {Finding.Detail}";
}

/// <summary>
/// One fix round's progress check (sc-25380), stored as the <c>fix-progress</c> checkpoint: the fixed commit and the commit
/// the fixer pushed, the blocking findings on each (from the panel's verdicts), the CI checks that passed on the fixed
/// commit, those that reached no verdict there (<c>unknown_before</c>: cancelled, stale or without a conclusion — compared
/// on the new head like a passed one, since nothing shows they were already failing), and those of them that fail or have no
/// run on the new one (from GitHub's executed check results, never the fixer's report, E5), and the outcome:
/// <see cref="Progress"/> or <see cref="Failed"/>.
/// </summary>
public sealed record FixProgress(
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("from")] string FromSha,
    [property: JsonPropertyName("to")] string ToSha,
    [property: JsonPropertyName("blocking_before")] int BlockingBefore,
    [property: JsonPropertyName("blocking_after")] int BlockingAfter,
    [property: JsonPropertyName("passed_before")] IReadOnlyList<string> PassedBefore,
    [property: JsonPropertyName("unknown_before")] IReadOnlyList<string> UnknownBefore,
    [property: JsonPropertyName("failing_now")] IReadOnlyList<string> FailingNow,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason)
{
    public const string Progress = "progress";
    public const string Failed = "failed";

    [JsonIgnore]
    public bool MadeProgress => Outcome == Progress;

    public string ToDetail() => JsonSerializer.Serialize(this);

    /// <summary>The progress record a ledger detail holds, or null when it is not one.</summary>
    public static FixProgress? FromDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<FixProgress>(detail) is { FromSha: not null, Outcome: not null } p
                ? p with { PassedBefore = p.PassedBefore ?? [], UnknownBefore = p.UnknownBefore ?? [], FailingNow = p.FailingNow ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The fix loop's deterministic rules (E1/E2: no model decides any of them): which failed verdicts a fixer may work on, which
/// panel roles review the fixer's push again, and whether a round made progress.
/// </summary>
public static class FixLoop
{
    /// <summary>Every blocking finding of <paramref name="verdict"/> (after confirmation), in role order.</summary>
    public static IReadOnlyList<OpenFinding> Open(ReviewVerdict verdict) =>
        verdict.Reviews.SelectMany(r => r.Findings.Where(f => f.IsBlocking).Select(f => new OpenFinding(r.Role, f))).ToList();

    /// <summary>
    /// The findings a fixer gets for a failed verdict: its blocking findings, when the verdict failed only because of them and
    /// each was confirmed by a second model. Null when anything else failed it — a role's unusable answer, a required role
    /// missing, a blocking finding whose second model's answer was unusable — which a fixer cannot act on: that escalates.
    /// </summary>
    public static IReadOnlyList<OpenFinding>? Fixable(ReviewVerdict verdict)
    {
        if (verdict.Passed || verdict.Reviews.Any(r => !r.Clean)
            || ReviewRoles.Required(verdict.RiskyPaths.Count > 0).Any(role => verdict.Reviews.All(r => r.Role != role)))
        {
            return null;
        }
        var open = Open(verdict);
        return open.Count > 0 && open.All(f => f.Finding.Confirmation?.Outcome == Confirmation.Confirmed) ? open : null;
    }

    /// <summary>The findings as a list, one per line, for an escalation comment.</summary>
    public static string Describe(IEnumerable<OpenFinding> findings)
    {
        var text = new StringBuilder();
        foreach (var finding in findings)
        {
            text.Append("- ").AppendLine(finding.ToString());
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Whether a role's review of the fixed commit must be redone on the fixer's push rather than carried: it had a blocking
    /// finding (or an unusable answer), or its models break the panel's rule (<see cref="ReviewModels.Problems"/>), which the
    /// merge gate would refuse.
    /// </summary>
    public static bool MustReviewAgain(RoleReview review) =>
        !review.Clean
        || review.Findings.Any(f => f.IsBlocking)
        || ReviewModels.Problems(review).Any();

    /// <summary>
    /// The reviews of <paramref name="previous"/> (the fixed commit's verdict) carried into the new head's verdict: every
    /// <paramref name="required"/> role's that need not be redone (<see cref="MustReviewAgain"/>), marked with the commit it
    /// was made on. Every other required role reviews the new head: those with an open blocking finding, and any the previous
    /// verdict did not have (e.g. security, when the fix touches a risky path).
    /// </summary>
    public static IReadOnlyList<RoleReview> Carried(ReviewVerdict previous, IReadOnlyList<string> required) =>
        previous.Reviews
            .Where(r => required.Contains(r.Role) && !MustReviewAgain(r))
            .Select(r => r with { CarriedFrom = r.CarriedFrom ?? previous.HeadSha })
            .ToList();

    /// <summary>
    /// The CI checks (check runs and commit statuses, by name) that passed on a commit: every reported run of the name
    /// finished, with a passing conclusion.
    /// </summary>
    public static IReadOnlyList<string> PassedChecks(CiFacts facts) =>
        facts.Checks.GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(g => g.All(c => c.Completed && Ci.Passes(c.Conclusion)))
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The CI checks that reached no verdict on a commit: not passed, and no run of the name finished with a failing
    /// conclusion — each run that did not pass is still running, was cancelled (e.g. by a concurrency group when the fixer
    /// pushed), went stale, or finished without a conclusion.
    /// </summary>
    public static IReadOnlyList<string> UnknownChecks(CiFacts facts) =>
        facts.Checks.GroupBy(c => c.Name, StringComparer.Ordinal)
            .Where(g => !g.All(c => c.Completed && Ci.Passes(c.Conclusion)) && !g.Any(c => c.Completed && !Ci.Passes(c.Conclusion) && !NoVerdict(c)))
            .Select(g => g.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool NoVerdict(CheckFact check) => !check.Completed || check.Conclusion is null or "cancelled" or "stale";

    /// <summary>
    /// Whether a commit's CI has finished: it was read in full, has at least one check, and every check run and counted check
    /// suite (<see cref="Ci.CountedSuites"/>) is completed.
    /// </summary>
    public static bool Settled(CiFacts facts) =>
        facts.Complete && facts.Checks.Count > 0 && facts.Checks.All(c => c.Completed) && Ci.CountedSuites(facts).All(s => s.Completed);

    /// <summary>
    /// Of <paramref name="watched"/> (the checks that passed, or reached no verdict, on the fixed commit), the checks that fail
    /// on <paramref name="now"/> (a run of the name finished without passing), those with no run of the name there, and those
    /// with one still running.
    /// </summary>
    public static (IReadOnlyList<string> Failing, IReadOnlyList<string> Missing, IReadOnlyList<string> Pending) Regressions(
        IReadOnlyList<string> watched, CiFacts now)
    {
        var (failing, missing, pending) = (new List<string>(), new List<string>(), new List<string>());
        foreach (var name in watched)
        {
            var runs = now.Checks.Where(c => c.Name == name).ToList();
            if (runs.Any(c => c.Completed && !Ci.Passes(c.Conclusion)))
            {
                failing.Add(name);
            }
            else if (runs.Count == 0)
            {
                missing.Add(name);
            }
            else if (runs.Any(c => !c.Completed))
            {
                pending.Add(name);
            }
        }
        return (failing, missing, pending);
    }

    /// <summary>
    /// A round whose fixer was found looping and interrupted (sc-25388): failed, with nothing pushed — the head is still the
    /// fixed commit and its blocking findings are all still open.
    /// </summary>
    public static FixProgress Stuck(int round, ReviewVerdict fixedVerdict, string reason)
    {
        var open = Open(fixedVerdict).Count;
        return new FixProgress(round, fixedVerdict.HeadSha, fixedVerdict.HeadSha, open, open, [], [], [], FixProgress.Failed,
            $"the fixer was stuck in a loop and was interrupted ({reason}); nothing was pushed");
    }

    /// <summary>
    /// The round's outcome: progress only when the new head's review is usable, the blocking findings went down, both
    /// commits' CI was read in full (<paramref name="unread"/> names the commit that was not), and no check that passed — or
    /// reached no verdict (<paramref name="unknownBefore"/>) — on the fixed commit fails (<paramref name="failingNow"/>) or has
    /// no run (<paramref name="missingNow"/>) on the new one; otherwise a failed round, with why.
    /// </summary>
    public static FixProgress Judge(int round, ReviewVerdict previous, ReviewVerdict current, IReadOnlyList<string> passedBefore,
        IReadOnlyList<string> failingNow, IReadOnlyList<string>? unknownBefore = null, IReadOnlyList<string>? missingNow = null,
        string? unread = null)
    {
        var unknown = unknownBefore ?? [];
        var missing = missingNow ?? [];
        var (before, after) = (Open(previous).Count, Open(current).Count);
        var (from, to) = (Ci.Short(previous.HeadSha), Ci.Short(current.HeadSha));
        string Named(string name) => unknown.Contains(name) ? $"{name} (no verdict on {from})" : name;
        string? failed = null;
        if (current.Reviews.Any(r => !r.Clean))
        {
            failed = "the review of the new head is unusable";
        }
        else if (after >= before)
        {
            failed = $"blocking findings did not go down ({before} → {after})";
        }
        else if (unread is not null)
        {
            failed = $"CI could not be read in full on {Ci.Short(unread)}, so no check can be shown not to have regressed";
        }
        else if (failingNow.Count > 0 || missing.Count > 0)
        {
            failed = string.Join("; ", new[]
            {
                failingNow.Count > 0 ? $"checks that passed on {from} fail on {to}: {string.Join(", ", failingNow.Select(Named))}" : null,
                missing.Count > 0 ? $"checks that passed on {from} are missing on {to}: {string.Join(", ", missing.Select(Named))}" : null,
            }.OfType<string>());
        }
        var progress = $"blocking findings {before} → {after}; no check that passed on {from} fails"
            + (unknown.Count > 0 ? $"; {string.Join(", ", unknown)} reached no verdict on {from} (cancelled or unfinished) and pass on {to}" : "");
        return new FixProgress(round, previous.HeadSha, current.HeadSha, before, after, passedBefore, unknown,
            [.. failingNow, .. missing],
            failed is null ? FixProgress.Progress : FixProgress.Failed,
            failed ?? progress);
    }
}
