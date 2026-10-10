namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// Test-only (sc-25391, E6): lets <c>GateCheckCoverageTests</c> break one named gate check at a time — the check stops blocking, as a
/// regression that removed it would — to prove each test tagged <c>[FailsGateCheck]</c> for it fails then. Internal, set only through
/// <see cref="Disable"/>, for the calling async flow only (an <see cref="AsyncLocal{T}"/>: other runs, threads and tests see every
/// check on), and read by nothing but <see cref="Off"/>: no configuration, environment variable or CLI option reaches it, and no
/// production code calls <see cref="Disable"/> (both checked by <c>GateCheckCoverageTests</c>), so there is no runtime bypass (E2).
/// </summary>
internal static class GateCheckSeam
{
    /// <summary>The checks a tier requires (<see cref="GatePolicy.FloorChecks"/>): a policy dropping one is refused.</summary>
    internal const string FloorChecks = "policy-floor:checks";

    /// <summary>The paths the sealed tier must cover (<see cref="GatePolicy.MustBeSealed"/>): a policy unsealing one is refused.</summary>
    internal const string FloorSealedPaths = "policy-floor:sealed-paths";

    /// <summary>The code floor of paths that call the security review in (<see cref="RiskyPaths"/>), whatever their tier.</summary>
    internal const string FloorSecurityReviewPaths = "policy-floor:security-review-paths";

    /// <summary><see cref="MergeGate"/> blocks when the base's policy is unreadable, missing or invalid.</summary>
    internal const string PreconditionPolicy = "precondition:policy";

    /// <summary><see cref="MergeGate"/> blocks a PR that is closed, merged or a draft.</summary>
    internal const string PreconditionPrOpen = "precondition:pr-open";

    /// <summary><see cref="MergeGate"/> blocks when the head's diff is unreadable or leaves files out (the tiers of its paths are unknown).</summary>
    internal const string PreconditionDiffComplete = "precondition:diff-complete";

    /// <summary><see cref="MergeGate"/> escalates every change touching a sealed path, whatever the review, the PR or the plan claim.</summary>
    internal const string SealedEscalation = "sealed-escalation";

    private static readonly AsyncLocal<string?> Disabled = new();

    /// <summary>Whether <paramref name="check"/> is disabled in this async flow (never, outside a coverage test).</summary>
    internal static bool Off(string check) => Disabled.Value == check;

    /// <summary>Disables <paramref name="check"/> for the calling async flow until the result is disposed.</summary>
    internal static IDisposable Disable(string check)
    {
        var previous = Disabled.Value;
        Disabled.Value = check;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Disabled.Value = previous;
    }
}
