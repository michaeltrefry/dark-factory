using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>The repository's file paths at the PR's base commit; <see cref="Truncated"/> when GitHub returned only part.</summary>
public sealed record RepoFiles(IReadOnlyList<string> Paths, bool Truncated);

/// <summary>
/// One panel role's review of the diff of exactly one head commit against its base, with the story and the base's file list
/// (so a reviewer can tell a new file from a duplicate of an existing one). <see cref="Session"/> is the router session the
/// call is accounted under; the pipeline names it in the ledger (<c>review-session</c>, with the prompt's hash) before the
/// call, so a call that never returns still has a readable cost (E9).
/// </summary>
public sealed record ReviewRequest(WorkStory Story, string Repo, PullFacts Pull, string Diff, RepoFiles Files, string Role, ReviewPrompt Prompt,
    string Model, string Session);

/// <summary>A second model's check of one blocking <see cref="Finding"/> a <see cref="Role"/> reviewer reported.</summary>
public sealed record ConfirmRequest(WorkStory Story, string Repo, PullFacts Pull, string Diff, RepoFiles Files, string Role, Finding Finding,
    ReviewPrompt Prompt, string Model, string Session);

/// <summary>
/// The router refused a review call for usage (429/529, or its exhaustion or rate-limit body): the plans ran out, not the
/// review. The pipeline pauses the factory for usage instead of escalating the item.
/// </summary>
public sealed class RouterUsageLimitedException(string message) : Exception(message);

public interface IReviewer
{
    /// <summary>
    /// One role's review with <see cref="ReviewRequest.Model"/> pinned, accounted under <see cref="ReviewRequest.Session"/>.
    /// An answer that is not a clean findings line from the pinned model is a <see cref="RoleReview"/> with an
    /// <see cref="RoleReview.Error"/>; a call that cannot be made throws (<see cref="RouterUsageLimitedException"/> when the
    /// router refused it for usage).
    /// </summary>
    Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct);

    /// <summary>
    /// Asks a second model whether a blocking finding reproduces from the code. An answer that is not a clean confirmation
    /// line from the pinned model is <see cref="Confirmation.Unusable"/>; a call that cannot be made throws.
    /// </summary>
    Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct);
}

/// <summary>
/// The panel's calls through the Weave router (never a provider directly), each pinned with <c>x-weave-force-model</c> (the
/// router's headless <c>/force-model</c>) to a Claude model (<see cref="ReviewModels"/>). The router key is the only
/// credential sent, as the worker sends it. The system prompt is the role's prompt file, verbatim (<see cref="ReviewPrompts"/>).
/// Reviewers read a diff and answer; they have no tools and change nothing. Every call streams (<see cref="MessageStream"/>):
/// <c>Review:TimeoutMinutes</c> (the client's timeout) bounds the whole call, and a stream silent for the idle gap fails.
/// </summary>
public sealed class RouterReviewer(HttpClient http, string routerKey, TimeSpan? idleTimeout = null) : IReviewer
{
    /// <summary>The longest gap between two lines of an answer stream before the call fails as stalled.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdleTimeout;

    public const string ForceModelHeader = "x-weave-force-model";

    /// <summary>
    /// The header Claude Code names its session with; the router accounts (and scopes the force-model pin to) each review
    /// call under its own fresh id, so the pin never reaches a worker's session and the call's cost is readable (E9).
    /// </summary>
    public const string SessionHeader = "X-Claude-Code-Session-Id";

    /// <summary>A diff longer than this is not reviewed in one call; the review fails rather than judge part of the change.</summary>
    public const int MaxDiffChars = 400_000;

    /// <summary>At most this many of the base's file paths go into a prompt (the prompt says when the list is cut).</summary>
    public const int MaxFilePaths = 5_000;

    private const int MaxTokens = 8192;
    private const int MaxTitle = 300;
    private const int MaxDetail = 2_000;

    public async Task<RoleReview> ReviewAsync(ReviewRequest request, CancellationToken ct)
    {
        if (request.Diff.Length > MaxDiffChars)
        {
            return new RoleReview(request.Role, request.Model, null, request.Session, request.Prompt.Id, [], "",
                $"The diff is {request.Diff.Length} characters, more than the {MaxDiffChars} one review reads; not reviewed.");
        }
        var (served, stop, text) = await CallAsync(request.Model, request.Session, request.Prompt.Text, BuildPrompt(request), ct);
        return InterpretReview(request.Role, request.Model, served, stop, text) with { Session = request.Session, Prompt = request.Prompt.Id };
    }

    public async Task<Confirmation> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        if (request.Diff.Length > MaxDiffChars)
        {
            return new Confirmation(Confirmation.Unusable, request.Model, null, request.Session, request.Prompt.Id,
                $"The diff is {request.Diff.Length} characters, more than the {MaxDiffChars} one call reads.");
        }
        var (served, stop, text) = await CallAsync(request.Model, request.Session, request.Prompt.Text, BuildConfirmPrompt(request), ct);
        return InterpretConfirmation(request.Model, served, stop, text) with { Session = request.Session, Prompt = request.Prompt.Id };
    }

    private async Task<(string? Served, string? Stop, string Text)> CallAsync(string model, string session, string system, string user, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages");
        message.Headers.Add(SessionHeader, session);
        message.Headers.Add(ClaudeWorker.RouterKeyHeader, routerKey);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", routerKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        message.Headers.Add(ForceModelHeader, model);
        message.Content = JsonContent.Create(new
        {
            model,
            max_tokens = MaxTokens,
            system,
            messages = new[] { new { role = "user", content = user } },
            // Streamed: the router cancels a call that has sent its client nothing for 10 s, and a review takes longer.
            stream = true,
        });
        // The whole call, stream included, is bounded by the client's timeout (Review:TimeoutMinutes): HttpClient's own
        // timeout ends at the response headers, and the answer streams after them.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (http.Timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(http.Timeout);
        }
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                var why = $"The review call through the router failed: {(int)response.StatusCode} {Cut(body, 500)}";
                if (UsageLimited((int)response.StatusCode, body))
                {
                    throw new RouterUsageLimitedException(why);
                }
                throw new InvalidOperationException(why);
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                // Anything but a stream is not an answer the panel reads; a usage refusal that arrives this way still pauses.
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                var why = $"The review call through the router answered {(int)response.StatusCode} "
                    + $"{response.Content.Headers.ContentType?.MediaType ?? "(no content type)"}, not an event stream: {Cut(body, 500)}";
                if (IsErrorBody(body) && UsageLimited(0, body))
                {
                    throw new RouterUsageLimitedException(why);
                }
                throw new InvalidOperationException(why);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var answer = await MessageStream.ReadAsync(stream, _idleTimeout, deadline.Token);
            return (answer.Served, answer.StopReason, answer.Text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The review call through the router did not finish within {http.Timeout.TotalMinutes:0.##} min.");
        }
    }

    /// <summary>
    /// Whether a successful non-stream body is an error rather than a model's answer: an Anthropic error object
    /// (<c>{"type":"error",...}</c>), or anything without a JSON <c>content</c>. A model's answer may well talk about rate
    /// limits, so its text is never scanned for usage markers (the rule <see cref="WorkerResult.UsageLimited"/> follows).
    /// </summary>
    private static bool IsErrorBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("error"))
                || !root.TryGetProperty("content", out _);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether a failed router answer is a usage refusal: 429 (the router's "every subscription unavailable" answer, or an
    /// upstream rate limit), 529 (overloaded), or a body carrying one of the worker's usage markers
    /// (<see cref="WorkerResult.UsageLimitMarkers"/>: the router's exhaustion text, <c>rate_limit_error</c>, ...).
    /// </summary>
    public static bool UsageLimited(int status, string body) =>
        status is 429 or 529 || WorkerResult.UsageLimitMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why an answer cannot count at all (<see cref="ReviewModels.CallProblem"/>: the pinned model breaks the panel's rule, or
    /// the router did not say it served the pinned model; or the answer ended early), or null when it can.
    /// </summary>
    private static string? ServedProblem(string model, string? served, string? stopReason, bool reviewer) =>
        ReviewModels.CallProblem(model, served, reviewer) is { } problem ? $"The answer cannot count: {problem}."
        : stopReason is not null and not "end_turn" and not "stop_sequence" ? $"The answer ended early ({stopReason})." : null;

    /// <summary>
    /// Turns a role reviewer's answer into its review. Usable only when the router said the pinned model answered,
    /// the answer ended normally and its last line is the findings JSON; anything else is a review with an
    /// <see cref="RoleReview.Error"/> (which fails the panel). A finding whose severity is neither blocking nor optional
    /// counts as blocking.
    /// </summary>
    public static RoleReview InterpretReview(string role, string model, string? served, string? stopReason, string answer)
    {
        RoleReview Unusable(string why) => new(role, model, served, null, null, [], "", why);

        if (ServedProblem(model, served, stopReason, reviewer: true) is { } problem)
        {
            return Unusable(problem);
        }
        if (LastJsonLine(answer, "findings") is not { } json || json.GetProperty("findings").ValueKind != JsonValueKind.Array)
        {
            return Unusable("The reviewer's answer does not end with a findings line.");
        }
        var findings = new List<Finding>();
        foreach (var f in json.GetProperty("findings").EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object)
            {
                return Unusable("The reviewer's findings line holds something that is not a finding.");
            }
            var severity = Text(f, "severity")?.Trim().ToLowerInvariant() is Finding.Optional ? Finding.Optional : Finding.Blocking;
            var line = f.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) ? n : (int?)null;
            findings.Add(new Finding(severity, Cut(Text(f, "title") ?? "(untitled finding)", MaxTitle), Text(f, "file"), line,
                Cut(Text(f, "detail") ?? "", MaxDetail)));
        }
        return new RoleReview(role, model, served, null, null, findings, Cut(Text(json, "summary") ?? "", MaxDetail));
    }

    /// <summary>
    /// Turns a second model's answer into its confirmation: <see cref="Confirmation.Confirmed"/> or
    /// <see cref="Confirmation.NotConfirmed"/> only from a clean confirmation line of the pinned model; anything else is
    /// <see cref="Confirmation.Unusable"/>.
    /// </summary>
    public static Confirmation InterpretConfirmation(string model, string? served, string? stopReason, string answer)
    {
        if (ServedProblem(model, served, stopReason, reviewer: false) is { } problem)
        {
            return new Confirmation(Confirmation.Unusable, model, served, null, null, problem);
        }
        if (LastJsonLine(answer, "confirmed") is not { } json || json.GetProperty("confirmed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new Confirmation(Confirmation.Unusable, model, served, null, null, "The second model's answer does not end with a confirmation line.");
        }
        return new Confirmation(json.GetProperty("confirmed").GetBoolean() ? Confirmation.Confirmed : Confirmation.NotConfirmed, model, served,
            null, null, Cut(Text(json, "reason") ?? "", MaxDetail));
    }

    /// <summary>The answer's last line (code fences skipped) when it is a JSON object with <paramref name="property"/>; only that line counts.</summary>
    private static JsonElement? LastJsonLine(string answer, string property)
    {
        var line = answer.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal));
        if (line is null || !line.StartsWith('{'))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out _) ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    public static string BuildPrompt(ReviewRequest request) =>
        Context(request.Story, request.Repo, request.Pull, request.Files, request.Diff);

    public static string BuildConfirmPrompt(ConfirmRequest request) =>
        $"""
        {Context(request.Story, request.Repo, request.Pull, request.Files, request.Diff)}

        The {request.Role} reviewer's blocking finding:
        {FindingBlock(request.Finding)}
        """;

    /// <summary>
    /// A reviewer's finding as a <c>&lt;finding&gt;</c> block (<see cref="PromptFence"/>): its title, where and detail are model text, so
    /// they are data, not instructions. <paramref name="role"/>, when given, is the factory's own name of the reviewer's role.
    /// </summary>
    public static string FindingBlock(Finding finding, string? role = null) =>
        PromptFence.Block("finding",
            (role is null ? "" : $"Role: {role}\n")
            + $"Title: {finding.Title}\nWhere: {finding.File ?? "(no file named)"}{(finding.Line is { } line ? $":{line}" : "")}\nDetail: {finding.Detail}");

    private static string Context(WorkStory story, string repo, PullFacts pull, RepoFiles files, string diff)
    {
        var shown = files.Paths.Take(MaxFilePaths).ToList();
        var cut = files.Truncated || files.Paths.Count > shown.Count;
        return $"""
            Repository: {repo}
            Pull request: {pull.HtmlUrl} (head {pull.HeadSha}, base {pull.BaseRef})
            {story.Kind.Noun} {story.Ref} ({story.StoryType}), named and described in the <story> block.

            Story:
            {PromptFence.Block("story", $"Name: {story.Name}\n\n{story.Description}")}

            Files in the repository at the base commit ({shown.Count}{(cut ? ", list cut short" : "")}):
            {PromptFence.Block("files", string.Join('\n', shown))}

            The change (unified diff of the head commit against its base):
            {PromptFence.Block("diff", diff)}
            """;
    }
}
