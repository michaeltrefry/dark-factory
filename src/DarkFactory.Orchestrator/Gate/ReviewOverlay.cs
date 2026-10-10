using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// One answer of CodeGraph's overlay tools (<c>request_overlay</c>, <c>get_overlay_status</c>; CodeGraph sc-25726): whether it is an
/// error (refused: no entitlement, unknown repository, no such tool, …) with its text, and the overlay's id, status (<c>queued</c>,
/// <c>indexing</c>, <c>ready</c>, <c>failed</c>, <c>expired</c>), head and base commits and error, from <c>structuredContent</c>.
/// </summary>
public sealed record OverlayAnswer(bool IsError, string Text, long? OverlayId, string? Status, string? HeadSha, string? BaseSha, string? Error);

/// <summary>
/// CodeGraph's overlay index of one commit, owner-side (sc-25708): requested and polled only by the orchestrator. The two tools are
/// on no allowlist (<see cref="ReviewTools.Names"/>, <see cref="Mcp.McpServers"/>, any <see cref="Mcp.McpProfile"/>): no reviewer,
/// session or proxy grant can call them.
/// </summary>
public interface ICodeGraphOverlays
{
    /// <summary>The CodeGraph project indexing <paramref name="repo"/> (<see cref="ICodeGraph.FindProjectAsync"/>), or null.</summary>
    Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct);

    /// <summary>
    /// <c>request_overlay(repo, ref)</c>: an overlay of <paramref name="project"/> at <paramref name="gitRef"/> (a full commit SHA here),
    /// new or the live one CodeGraph already has for the same repository and commit. Throws when CodeGraph cannot be reached.
    /// </summary>
    Task<OverlayAnswer> RequestOverlayAsync(string project, string gitRef, CancellationToken ct);

    /// <summary><c>get_overlay_status(overlayId)</c>. Throws when CodeGraph cannot be reached.</summary>
    Task<OverlayAnswer> OverlayStatusAsync(long overlayId, CancellationToken ct);
}

/// <summary>
/// What became of the CodeGraph overlay a review asked for, as the verdict records it (sc-25708, E3): <see cref="Outcome"/>
/// <c>ready</c> (CodeGraph indexed exactly <see cref="HeadSha"/>: reviewers' CodeGraph calls pass it as <c>sha</c>), <c>failed</c>
/// (the overlay failed or expired), <c>refused</c> (CodeGraph refused the request: no entitlement, unknown repository, no overlay
/// tool), <c>timed-out</c> (not ready within <c>CodeGraph:OverlayTimeoutMinutes</c>) or <c>unavailable</c> (no CodeGraph token, the
/// repository not indexed, CodeGraph unreachable). Anything but ready: the tools answer from the default-branch index, labelled
/// as not the PR head. None is a review failure (E4).
/// </summary>
public sealed record CodeGraphOverlay(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("head")] string HeadSha,
    [property: JsonPropertyName("overlay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OverlayId = null,
    [property: JsonPropertyName("base"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseSha = null,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Refused = "refused";
    public const string TimedOut = "timed-out";
    public const string Unavailable = "unavailable";

    [JsonIgnore]
    public bool IsReady => Outcome == Ready;

    /// <summary>The outcome in a few words (log lines, prompts and reports).</summary>
    [JsonIgnore]
    public string Describe => IsReady
        ? $"ready (overlay {OverlayId}, head {HeadSha}, base {BaseSha ?? "unknown"})"
        : $"{Outcome}{(OverlayId is { } id ? $" (overlay {id})" : "")}{(Reason is { } why ? $": {why}" : "")}";
}

/// <summary>
/// The review's overlay wait (sc-25708): at the start of a review, before any panel call, the orchestrator — never a model — asks
/// CodeGraph for an overlay of the PR head (<c>request_overlay</c> with the PR repository's project and the head SHA) and polls
/// <c>get_overlay_status</c> every <see cref="PollInterval"/> until it is ready, failed or expired, for at most <see cref="Timeout"/>
/// (<c>CodeGraph:OverlayTimeoutMinutes</c>; the calls themselves included). Before each poll the run's controls are checked (a
/// Pause or Stop ends the wait as it ends the panel's calls). A refused request, an overlay that failed or expired, or no CodeGraph
/// at all ends the wait at once; none of them fails the review.
/// </summary>
public sealed class ReviewOverlays(ICodeGraphOverlays? codeGraph, TimeSpan timeout, TimeSpan? pollInterval = null)
{
    /// <summary>How often a queued or indexing overlay's status is read.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>The default of <c>CodeGraph:OverlayTimeoutMinutes</c>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public TimeSpan Timeout { get; } = timeout;

    public TimeSpan PollInterval { get; } = pollInterval ?? DefaultPollInterval;

    private const string StatusReady = "ready";
    private const string StatusFailed = "failed";
    private const string StatusExpired = "expired";
    private const string StatusQueued = "queued";
    private const string StatusIndexing = "indexing";

    /// <summary>
    /// Requests the overlay of <paramref name="headSha"/> and waits for it (see the class). <paramref name="checkControls"/> throws
    /// when a control stops the run; that, and <paramref name="ct"/>, end the wait by throwing.
    /// </summary>
    public async Task<CodeGraphOverlay> WaitAsync(RepoRef repo, string headSha, Func<CancellationToken, Task> checkControls, TimeProvider time,
        CancellationToken ct)
    {
        if (codeGraph is null)
        {
            return new(CodeGraphOverlay.Unavailable, headSha, Reason: "CodeGraph is not configured (no CodeGraph token)");
        }
        using var deadline = new CancellationTokenSource(Timeout, time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        long? overlayId = null;
        string? lastError = null;
        try
        {
            string project;
            try
            {
                if (await codeGraph.FindProjectAsync(repo, bounded.Token) is not { } found)
                {
                    return new(CodeGraphOverlay.Unavailable, headSha, Reason: $"{repo.FullName}: {ReviewTools.NotIndexed}");
                }
                project = found;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(CodeGraphOverlay.Unavailable, headSha, Reason: $"CodeGraph could not be reached or asked: {Cut(ex.Message)}");
            }
            OverlayAnswer answer;
            try
            {
                answer = await codeGraph.RequestOverlayAsync(project, headSha, bounded.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new(CodeGraphOverlay.Unavailable, headSha, Reason: $"request_overlay could not be sent: {Cut(ex.Message)}");
            }
            if (answer.IsError)
            {
                return new(CodeGraphOverlay.Refused, headSha, answer.OverlayId, answer.BaseSha, $"request_overlay: {Cut(answer.Text)}");
            }
            if (answer.OverlayId is null && answer.Status != StatusReady)
            {
                return new(CodeGraphOverlay.Refused, headSha, null, answer.BaseSha,
                    $"request_overlay answered no overlay id (status {answer.Status ?? "none"})");
            }
            overlayId = answer.OverlayId;
            while (true)
            {
                if (Settled(answer, headSha) is { } settled)
                {
                    return settled;
                }
                await Task.Delay(PollInterval, time, bounded.Token);
                await checkControls(ct);
                try
                {
                    answer = await codeGraph.OverlayStatusAsync(overlayId!.Value, bounded.Token);
                    lastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A status read that could not be made is not the overlay's failure: read again until the timeout.
                    lastError = Cut(ex.Message);
                    continue;
                }
                if (answer.IsError)
                {
                    return new(CodeGraphOverlay.Failed, headSha, overlayId, answer.BaseSha, $"get_overlay_status: {Cut(answer.Text)}");
                }
                answer = answer with { OverlayId = answer.OverlayId ?? overlayId };
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new(CodeGraphOverlay.TimedOut, headSha, overlayId, null,
                $"not ready within {Timeout.TotalMinutes:0.##} min{(lastError is null ? "" : $" (last status read failed: {lastError})")}");
        }
    }

    /// <summary>The outcome an overlay answer settles, or null while it is queued or indexing.</summary>
    private static CodeGraphOverlay? Settled(OverlayAnswer answer, string headSha) => answer.Status switch
    {
        StatusReady when string.Equals(answer.HeadSha, headSha, StringComparison.OrdinalIgnoreCase) =>
            new(CodeGraphOverlay.Ready, headSha, answer.OverlayId, answer.BaseSha?.ToLowerInvariant()),
        // E3: an overlay of another commit is never presented as the head's.
        StatusReady => new(CodeGraphOverlay.Failed, headSha, answer.OverlayId, answer.BaseSha,
            $"the ready overlay is of {answer.HeadSha ?? "an unnamed commit"}, not the PR head"),
        StatusFailed or StatusExpired => new(CodeGraphOverlay.Failed, headSha, answer.OverlayId, answer.BaseSha,
            $"the overlay is {answer.Status}{(answer.Error is { } e ? $": {Cut(e)}" : "")}"),
        StatusQueued or StatusIndexing => null,
        _ => new(CodeGraphOverlay.Failed, headSha, answer.OverlayId, answer.BaseSha, $"unknown overlay status '{Cut(answer.Status ?? "(none)")}'"),
    };

    private static string Cut(string s) => s.Length > 300 ? s[..300] : s;
}
