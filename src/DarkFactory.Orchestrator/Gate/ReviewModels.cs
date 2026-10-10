using DarkFactory.Orchestrator.Router;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Which panel answers count (owner decision 2026-10-09, epic E8, sc-25626): every review and second opinion is its own router
/// session on the <see cref="Class"/> model class (<see cref="ModelClass.Header"/>), never a pinned model id; the router picks
/// the model inside the class. Which model served — even the one that wrote the code, or the one that served the review a
/// second opinion checks — does not matter. Deterministic and fail-closed (E2): a call counts only when the router's
/// <see cref="ModelClass.ResponseHeader"/> says the high class served it; a missing header counts as failed.
/// </summary>
public static class ReviewModels
{
    /// <summary>The model class every review and second-opinion call names.</summary>
    public const string Class = ModelClass.High;

    /// <summary>Why a panel call answered under <paramref name="servedClass"/> cannot count, or null when it can.</summary>
    public static string? CallProblem(string? servedClass) =>
        servedClass == Class ? null
        : string.IsNullOrWhiteSpace(servedClass) ? $"the router did not say which model class served it (no {ModelClass.ResponseHeader} header)"
        : $"the router served it on the '{servedClass}' model class, not {Class}";

    /// <summary>
    /// Every reason <paramref name="review"/>'s calls cannot count: the review, or a second opinion on one of its findings, was
    /// not served on the <see cref="Class"/> class (or the router did not say which class served it).
    /// </summary>
    public static IEnumerable<string> Problems(RoleReview review)
    {
        if (CallProblem(review.ServedClass) is { } problem)
        {
            yield return $"the {review.Role} review: {problem}";
        }
        foreach (var c in review.Findings.Select(f => f.Confirmation).OfType<Confirmation>())
        {
            if (CallProblem(c.ServedClass) is { } confirmProblem)
            {
                yield return $"a second opinion on a {review.Role} finding: {confirmProblem}";
            }
        }
    }
}
