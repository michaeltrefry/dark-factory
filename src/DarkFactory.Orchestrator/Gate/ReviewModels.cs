using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Which models may sit on the review panel (owner decisions 2026-10-08, sc-25379, and 2026-10-09, sc-25391): every reviewer
/// is a Claude Opus 5 or newer and every second model is Claude, whichever models the implementer used. Deterministic and
/// fail-closed: an id this parser does not read as such is not eligible, and an answer counts only when the router says the
/// pinned model served it — or, for a pinned Claude Opus, a Claude Opus of the same or a higher version (the router may
/// upgrade, e.g. its <c>model_mapping</c> maps <c>claude-opus-5</c> to <c>claude-opus-5-5</c>, never downgrade).
/// </summary>
public static partial class ReviewModels
{
    /// <summary>The oldest Claude Opus a reviewer may be.</summary>
    public static readonly (int Major, int Minor) OpusFloor = (5, 0);

    public const string FloorText = "Claude Opus 5 or newer";

    // claude-opus-<major>[(-|.)<minor>][-<yyyymmdd>], after any "provider/" prefix; anything else is not read as an Opus id.
    [GeneratedRegex(@"^claude-opus-(?<major>\d{1,3})(?:[-.](?<minor>\d{1,2}))?(?:-(?<date>\d{8}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex OpusId();

    [GeneratedRegex(@"-\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatedSnapshot();

    // claude-<name>-<major>.<minor>…: the dotted spelling of a Claude version, rewritten to Anthropic's dashed one.
    [GeneratedRegex(@"^(claude-[a-z]+-\d{1,3})\.(\d{1,2})(?=-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedVersion();

    /// <summary>
    /// The canonical id: lower case, any <c>provider/</c> prefix dropped, and a dotted Claude version in the dashed form
    /// (<c>claude-opus-5.5</c> is <c>claude-opus-5-5</c>), so both spellings name one model wherever ids are compared.
    /// </summary>
    private static string Id(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        return DottedVersion().Replace(id[(id.LastIndexOf('/') + 1)..], "$1-$2");
    }

    /// <summary>Whether <paramref name="model"/> is a Claude model id (<c>claude-…</c>, after any <c>provider/</c> prefix).</summary>
    public static bool IsClaude(string? model) =>
        !string.IsNullOrWhiteSpace(model) && Id(model) is var id && id.StartsWith("claude-", StringComparison.Ordinal) && id.Length > 7;

    /// <summary>The Claude Opus version <paramref name="model"/> names (a missing minor is 0), or null when it is not an Opus id.</summary>
    public static (int Major, int Minor)? OpusVersion(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || OpusId().Match(Id(model)) is not { Success: true } m)
        {
            return null;
        }
        var major = int.Parse(m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = m.Groups["minor"].Success ? int.Parse(m.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (major, minor);
    }

    /// <summary>Whether <paramref name="model"/> may review: a Claude Opus at or above <see cref="OpusFloor"/>.</summary>
    public static bool MeetsReviewFloor(string? model) =>
        OpusVersion(model) is { } v && (v.Major > OpusFloor.Major || (v.Major == OpusFloor.Major && v.Minor >= OpusFloor.Minor));

    /// <summary>
    /// Whether the router's <paramref name="served"/> model counts as <paramref name="pinned"/>: the same id (case and any
    /// <c>provider/</c> prefix aside) or that id's dated snapshot (<c>&lt;pinned&gt;-yyyymmdd</c>); or, when
    /// <paramref name="pinned"/> is a Claude Opus, a Claude Opus of the same or a higher version (an upgrade, never a
    /// downgrade). Any other pinned model (e.g. <c>claude-sonnet-5</c>) counts only as itself or its snapshot.
    /// </summary>
    public static bool Serves(string pinned, string? served) =>
        !string.IsNullOrWhiteSpace(served)
        && (IsSnapshotOf(Id(pinned), Id(served))
            || (OpusVersion(pinned) is { } p && OpusVersion(served) is { } s && (s.Major > p.Major || (s.Major == p.Major && s.Minor >= p.Minor))));

    /// <summary>
    /// Whether two ids name one model: the same id (case, any <c>provider/</c> prefix and the dotted spelling aside), or one
    /// the other's dated snapshot. Compares two pinned ids, or two served ids; never a pin with what the router served
    /// (that is <see cref="Serves"/>).
    /// </summary>
    public static bool SamePinned(string a, string b)
    {
        var (x, y) = (Id(a), Id(b));
        return IsSnapshotOf(x, y) || IsSnapshotOf(y, x);
    }

    // s is p, or p's dated snapshot (both canonical ids).
    private static bool IsSnapshotOf(string p, string s) =>
        s == p || (s.StartsWith(p + "-", StringComparison.Ordinal) && s.Length == p.Length + 9 && DatedSnapshot().IsMatch(s));

    /// <summary>
    /// Why a panel call pinned to <paramref name="model"/> (a role reviewer when <paramref name="reviewer"/>, else a second
    /// model) and answered by <paramref name="served"/> cannot count, or null when it can.
    /// </summary>
    public static string? CallProblem(string model, string? served, bool reviewer)
    {
        if (reviewer ? !MeetsReviewFloor(model) : !IsClaude(model))
        {
            return $"{model} is not a {(reviewer ? FloorText : "Claude model")}";
        }
        if (string.IsNullOrWhiteSpace(served))
        {
            return $"the router did not say which model answered the call pinned to {model}";
        }
        return Serves(model, served) ? null
            : $"the router served '{served}', not the pinned {model}{(OpusVersion(model) is null ? "" : " or a newer Claude Opus")}";
    }

    /// <summary>
    /// Every reason <paramref name="review"/>'s models cannot count: its reviewer is not a <see cref="FloorText"/> or was not
    /// served as pinned; a second model is not Claude, was not served as pinned, was pinned to the reviewer's own model
    /// (<see cref="SamePinned"/>), or was served as the model that served the reviewer (an Opus pin may be served as a newer
    /// Opus, so two different pins can be answered by one model, which then confirmed its own finding).
    /// </summary>
    public static IEnumerable<string> Problems(RoleReview review)
    {
        if (CallProblem(review.Model, review.ServedModel, reviewer: true) is { } problem)
        {
            yield return $"the {review.Role} reviewer: {problem}";
        }
        foreach (var c in review.Findings.Select(f => f.Confirmation).OfType<Confirmation>())
        {
            if (CallProblem(c.Model, c.ServedModel, reviewer: false) is { } confirmProblem)
            {
                yield return $"a second model on a {review.Role} finding: {confirmProblem}";
            }
            else if (SamePinned(review.Model, c.Model))
            {
                yield return $"a second model on a {review.Role} finding ({c.Model}) is the reviewer's own model";
            }
            else if (review.ServedModel is { } reviewerServed && c.ServedModel is { } confirmerServed && SamePinned(reviewerServed, confirmerServed))
            {
                yield return $"a second model on a {review.Role} finding ({c.Model}) was served as '{c.ServedModel}', "
                    + $"the model that served the reviewer ({review.Model}): it would confirm its own finding";
            }
        }
    }
}

/// <summary>Why no eligible reviewer or second model could be chosen; the item escalates with it.</summary>
public sealed class ReviewerChoiceException(string message) : Exception(message);

public static class ReviewerChoice
{
    /// <summary>
    /// The first of <paramref name="candidates"/> that is a <see cref="ReviewModels.FloorText"/>. Throws
    /// <see cref="ReviewerChoiceException"/> when there is none.
    /// </summary>
    public static string Choose(IReadOnlyList<string> candidates, string setting = "Review:Models") =>
        candidates.FirstOrDefault(ReviewModels.MeetsReviewFloor)
        ?? throw new ReviewerChoiceException(candidates.Count == 0
            ? $"No reviewer model is configured: reviewers must be a {ReviewModels.FloorText}; set {setting} (or Review:Models) to one the router routes."
            : $"No configured reviewer model ({string.Join(", ", candidates)}) is a {ReviewModels.FloorText}; set {setting}.");

    /// <summary>
    /// The second model for a blocking finding <paramref name="reviewerModels"/> reported: the first of
    /// <paramref name="candidates"/> that is a Claude model, not pinned to any of the reviewer's pinned models
    /// (<see cref="ReviewModels.SamePinned"/>) and, when the router said which model served the review
    /// (<paramref name="reviewerServed"/>), not one the router may serve as that model (<see cref="ReviewModels.Serves"/>:
    /// a pinned <c>claude-opus-5</c> may be served as the reviewer's <c>claude-opus-5-5</c>). Throws
    /// <see cref="ReviewerChoiceException"/> when there is none.
    /// </summary>
    public static string ChooseConfirmer(IReadOnlyList<string> candidates, IReadOnlyCollection<string> reviewerModels,
        string? reviewerServed = null, string setting = "Review:Confirm:Models") =>
        candidates.FirstOrDefault(c => ReviewModels.IsClaude(c) && !reviewerModels.Any(r => ReviewModels.SamePinned(c, r))
            && (reviewerServed is null || !ReviewModels.Serves(c, reviewerServed)))
        ?? throw new ReviewerChoiceException(
            $"No configured second model ({string.Join(", ", candidates)}) is a Claude model other than the reviewer's "
            + $"({string.Join(", ", reviewerModels)}{(reviewerServed is null ? "" : $", served as {reviewerServed}")}), "
            + $"so a blocking finding cannot be confirmed; set {setting}.");
}
