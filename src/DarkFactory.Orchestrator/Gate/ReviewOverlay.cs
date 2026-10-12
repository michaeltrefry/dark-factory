using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// One answer of CodeGraph's overlay tools (<c>request_overlay</c>, <c>get_overlay_status</c>; CodeGraph C6, michaeltrefry/CodeGraph
/// PR #75): whether it is an error (<c>isError</c>: unknown repository, invalid ref, overlays unavailable, temporarily unavailable, …)
/// with its text and code (<c>structuredContent.error.code</c>), and the overlay's id, status (<c>queued</c>, <c>indexing</c>,
/// <c>ready</c>, <c>failed</c>, <c>expired</c>), head and base commits, error (<c>overlayError</c>) and whether a ready overlay is
/// stale (<c>stale</c>: its base is no longer the default-branch index commit, so read tools will not use it).
/// </summary>
public sealed record OverlayAnswer(bool IsError, string Text, long? OverlayId, string? Status, string? HeadSha, string? BaseSha, string? Error,
    bool Stale = false, string? ErrorCode = null);

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
    /// new or the live one CodeGraph already has for the same repository and commit. Throws when CodeGraph cannot be reached
    /// (<see cref="McpHttpException"/> for an HTTP refusal, e.g. 403 <c>tool_not_entitled</c>).
    /// </summary>
    Task<OverlayAnswer> RequestOverlayAsync(string project, string gitRef, CancellationToken ct);

    /// <summary><c>get_overlay_status(overlayId)</c>. Throws when CodeGraph cannot be reached.</summary>
    Task<OverlayAnswer> OverlayStatusAsync(long overlayId, CancellationToken ct);
}

/// <summary>
/// What became of the CodeGraph overlay a review asked for, as the verdict records it (sc-25708, E3): <see cref="Outcome"/>
/// <c>ready</c> (CodeGraph indexed exactly <see cref="HeadSha"/>: reviewers' CodeGraph calls pass it as <c>sha</c>), <c>failed</c>
/// (the overlay failed or expired, or is still stale after one new request), <c>refused</c> (CodeGraph refused the request: the token
/// not entitled to <c>request_overlay</c> (HTTP 403 <c>tool_not_entitled</c>), unknown repository, invalid ref, overlays
/// unavailable), <c>timed-out</c> (not ready within <c>CodeGraph:OverlayTimeoutMinutes</c>) or <c>unavailable</c> (no CodeGraph token,
/// the repository not indexed, CodeGraph unreachable or its call timing out, or temporarily unavailable until the timeout). Anything
/// but ready: the tools answer from the default-branch index, labelled as not the PR head. None is a review failure (E4).
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

    /// <summary>The <c>codegraph-overlay</c> checkpoint's Detail (<see cref="RunPipeline.Steps.CodeGraphOverlay"/>).</summary>
    public string ToDetail() => System.Text.Json.JsonSerializer.Serialize(this);

    /// <summary>A <c>codegraph-overlay</c> checkpoint's overlay, or null when its Detail is not one.</summary>
    public static CodeGraphOverlay? FromDetail(string? detail)
    {
        if (detail is null)
        {
            return null;
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CodeGraphOverlay>(detail) is { Outcome: not null, HeadSha: not null } overlay ? overlay : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

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
/// Pause or Stop ends the wait as it ends the panel's calls). A refused request (an error answer, or HTTP 403 for a token not
/// entitled to the tool), an overlay that failed or expired, or no CodeGraph at all ends the wait at once. A call that cannot be
/// made (CodeGraph unreachable, its HTTP client's own timeout) is <c>unavailable</c> for the request, and is read again until the
/// timeout for a status read. A <c>temporarily_unavailable</c> answer is requested again every <see cref="PollInterval"/> (still so
/// at the timeout: <c>unavailable</c>). A ready overlay that is <c>stale</c> (its base is no longer the default-branch index commit,
/// so CodeGraph's read tools will not use it; CodeGraph never answers a request with a stale overlay, so a new request queues a fresh
/// one) is requested once more; stale again, it is <c>failed</c>. None of them fails the review.
/// </summary>
public sealed class ReviewOverlays(ICodeGraphOverlays? codeGraph, TimeSpan timeout, TimeSpan? pollInterval = null)
{
    /// <summary>How often a queued or indexing overlay's status is read.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    /// <summary>The default of <c>CodeGraph:OverlayTimeoutMinutes</c>.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Why an overlay still stale after one new request is not used.</summary>
    public const string StaleReason = "the overlay is stale (default branch moved past its base)";

    /// <summary>CodeGraph's error code for a request it cannot serve now (C6's <c>TemporarilyUnavailableCode</c>).</summary>
    public const string TemporarilyUnavailable = "temporarily_unavailable";

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
        string? unavailable = null;
        var staleAnswers = 0;
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
            // Anything but the wait's own cancellation (a Pause, Stop, shutdown or the deadline), e.g. the HTTP client's own timeout.
            catch (Exception ex) when (ex is not OperationCanceledException || !bounded.IsCancellationRequested)
            {
                return new(CodeGraphOverlay.Unavailable, headSha, Reason: $"CodeGraph could not be reached or asked: {Cut(ex.Message)}");
            }
            while (true)
            {
                OverlayAnswer answer;
                try
                {
                    answer = await codeGraph.RequestOverlayAsync(project, headSha, bounded.Token);
                }
                catch (McpHttpException ex) when (ex.StatusCode == 403)
                {
                    // CodeGraph's tool entitlement refuses an unentitled token before the tool runs: HTTP 403, code tool_not_entitled.
                    return new(CodeGraphOverlay.Refused, headSha, Reason: ex.Code == McpHttpException.NotEntitled
                        ? $"not entitled to {CodeGraphMcpClient.RequestOverlayTool}"
                        : $"request_overlay: CodeGraph answered 403{(ex.Code is { } code ? $" ({Cut(code)})" : "")}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !bounded.IsCancellationRequested)
                {
                    return new(CodeGraphOverlay.Unavailable, headSha, Reason: $"request_overlay could not be sent: {Cut(ex.Message)}");
                }
                if (answer.IsError && answer.ErrorCode == TemporarilyUnavailable)
                {
                    // CodeGraph could not look the ref up now: ask again until the timeout.
                    unavailable = Cut(answer.Text);
                    await Task.Delay(PollInterval, time, bounded.Token);
                    await checkControls(ct);
                    continue;
                }
                unavailable = null;
                if (answer.IsError)
                {
                    return new(CodeGraphOverlay.Refused, headSha, answer.OverlayId, answer.BaseSha,
                        $"request_overlay{(answer.ErrorCode is { } code ? $" ({Cut(code)})" : "")}: {Cut(answer.Text)}");
                }
                if (answer.OverlayId is null && answer.Status != StatusReady)
                {
                    return new(CodeGraphOverlay.Refused, headSha, null, answer.BaseSha,
                        $"request_overlay answered no overlay id (status {answer.Status ?? "none"})");
                }
                overlayId = answer.OverlayId;
                while (true)
                {
                    if (answer.Status == StatusReady && answer.Stale && string.Equals(answer.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
                    {
                        if (staleAnswers++ > 0)
                        {
                            return new(CodeGraphOverlay.Failed, headSha, answer.OverlayId, answer.BaseSha, StaleReason);
                        }
                        break; // request it again: CodeGraph queues a fresh overlay of the head against the current default branch
                    }
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
                    catch (Exception ex) when (ex is not OperationCanceledException || !bounded.IsCancellationRequested)
                    {
                        // A status read that could not be made (unreachable, the HTTP client's own timeout) is not the overlay's
                        // failure: read again until the timeout.
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
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return unavailable is not null
                ? new(CodeGraphOverlay.Unavailable, headSha, overlayId, null,
                    $"CodeGraph was temporarily unavailable until the timeout ({Timeout.TotalMinutes:0.##} min): {unavailable}")
                : new(CodeGraphOverlay.TimedOut, headSha, overlayId, null,
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
