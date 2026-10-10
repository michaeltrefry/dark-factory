using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// The review panel's roles. Correctness and spec conformance review every change; security one that touches a path whose
/// tier in <c>factory/gate.yaml</c> requires <c>security-review</c> or that the code floor <see cref="RiskyPaths"/> matches
/// (<see cref="GatePolicy.SecurityReviewReasons"/>). Each
/// role has its own prompt file (<see cref="ReviewPrompts"/>); every role reviews on the high model class (<see cref="ReviewModels"/>).
/// </summary>
public static class ReviewRoles
{
    public const string Correctness = "correctness";
    public const string SpecConformance = "spec-conformance";
    public const string Security = "security";

    public static readonly IReadOnlyList<string> All = [Correctness, SpecConformance, Security];

    /// <summary>The roles that must review a change: security only when its tiers require the security review.</summary>
    public static IReadOnlyList<string> Required(bool risky) => risky ? All : [Correctness, SpecConformance];
}

/// <summary>One versioned reviewer prompt: its path in the factory repo, its text and the SHA-256 of its bytes.</summary>
public sealed record ReviewPrompt(string Path, string Text, string Sha256)
{
    /// <summary>How the ledger names the prompt a session used: <c>factory/prompts/x.md@sha256:&lt;hex&gt;</c>.</summary>
    public string Id => $"{Path}@sha256:{Sha256}";
}

/// <summary>
/// The panel's prompts: <c>factory/prompts/*.md</c> in the dark-factory repository (a sealed path), compiled into the
/// orchestrator as embedded resources. They are deliberately never read from the target repository (a PR could rewrite
/// the prompt it is judged by) nor from disk at run time (nothing a worker or a later edit writes changes a built
/// factory's reviewers). Changing a prompt is a dark-factory PR; each review session records the prompt's hash.
/// </summary>
public static class ReviewPrompts
{
    public const string Directory = "factory/prompts";
    public const string ConfirmName = "confirm";

    private static readonly Dictionary<string, ReviewPrompt> Cache = new(StringComparer.Ordinal);

    /// <summary>The prompt of a review role (<see cref="ReviewRoles"/>).</summary>
    public static ReviewPrompt For(string role) => Load(role);

    /// <summary>The second opinion's prompt: does a blocking finding reproduce from the code.</summary>
    public static ReviewPrompt Confirm => Load(ConfirmName);

    private static ReviewPrompt Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }
            var path = $"{Directory}/{name}.md";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)
                ?? throw new InvalidOperationException($"The reviewer prompt {path} is not compiled into the factory.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var prompt = new ReviewPrompt(path, new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Cache[name] = prompt;
            return prompt;
        }
    }
}

/// <summary>
/// A second opinion on one blocking finding (its own high-class router session). <see cref="Outcome"/>: <c>confirmed</c> (it
/// reproduces from the code), <c>not-confirmed</c> (the finding is downgraded to optional), or <c>unusable</c> (no clear answer
/// on the high class: the finding stays blocking, fail-closed). <see cref="ServedModel"/> and <see cref="ServedClass"/> are what
/// the router said served the call (the model for reporting; the class is what counts, <see cref="ReviewModels"/>).
/// </summary>
public sealed record Confirmation(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("served")] string? ServedModel,
    [property: JsonPropertyName("class")] string? ServedClass,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolCall>? Tools = null)
{
    public const string Confirmed = "confirmed";
    public const string NotConfirmed = "not-confirmed";
    public const string Unusable = "unusable";

    /// <summary>The served model as reports name it.</summary>
    [JsonIgnore]
    public string ServedName => ServedModel ?? "(the router named no served model)";
}

/// <summary>
/// One tool call a panel session made (sc-25705, <see cref="ReviewTools"/>), as the verdict records it: the tool, its arguments as
/// the model sent them (cut at <see cref="ReviewTools.MaxRecordedArguments"/> characters), the SHA-256 of the result the model was
/// given (before fencing), whether that result was an error, and for a CodeGraph answer the commit it describes
/// (<see cref="ReviewTools.UnknownCommit"/> when CodeGraph did not say).
/// </summary>
public sealed record ToolCall(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("arguments")] string Arguments,
    [property: JsonPropertyName("sha256")] string ResultSha256,
    [property: JsonPropertyName("error")] bool Error = false,
    [property: JsonPropertyName("commit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null);

/// <summary>One finding of a panel role. <see cref="Downgraded"/>: it was blocking, and the second opinion did not confirm it.</summary>
public sealed record Finding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("confirmation")] Confirmation? Confirmation = null,
    [property: JsonPropertyName("downgraded")] bool Downgraded = false)
{
    public const string Blocking = "blocking";
    public const string Optional = "optional";

    [JsonIgnore]
    public bool IsBlocking => Severity == Blocking;

    /// <summary>
    /// The finding after its second opinion answered: not confirmed → optional (and does not block); confirmed or an unusable
    /// answer → still blocking.
    /// </summary>
    public Finding ConfirmedBy(Confirmation confirmation) => confirmation.Outcome == Confirmation.NotConfirmed
        ? this with { Severity = Optional, Downgraded = true, Confirmation = confirmation }
        : this with { Confirmation = confirmation };

    public override string ToString() => $"{Title}{(File is null ? "" : $" ({File}{(Line is null ? "" : $":{Line}")})")}";
}

/// <summary>
/// One panel role's review of one head commit: the model and the model class the router said served it (the class is what
/// counts, <see cref="ReviewModels"/>; the model is for reporting), the router session, the prompt (path and hash), the findings, and <see cref="Error"/> when the answer was unusable (which
/// fails the review).
/// <see cref="CarriedFrom"/>: after a fix round, a role with no blocking finding is not asked again (sc-25380); its review of
/// the fixed commit (named here) is carried into the new head's verdict.
/// </summary>
public sealed record RoleReview(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("served")] string? ServedModel,
    [property: JsonPropertyName("class")] string? ServedClass,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("carried")] string? CarriedFrom = null,
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolCall>? Tools = null)
{
    [JsonIgnore]
    public bool Clean => Error is null;

    /// <summary>The served model as reports name it.</summary>
    [JsonIgnore]
    public string ServedName => ServedModel ?? "(the router named no served model)";
}

public static class ReviewPanel
{
    /// <summary>
    /// The panel's verdict on <paramref name="headSha"/> (deterministic, E1: models only produce findings). Pass only when
    /// every required role (security when <paramref name="riskyPaths"/> is not empty) reviewed with a usable answer and no
    /// finding is blocking after confirmation. (An answer not served on the high class is unusable already,
    /// <see cref="RouterReviewer.InterpretReview"/>; the merge gate checks every call's class again, <see cref="ReviewModels.Problems"/>.)
    /// </summary>
    public static ReviewVerdict Decide(string headSha, IReadOnlyList<string> riskyPaths, IReadOnlyList<RoleReview> reviews)
    {
        var problems = new List<string>();
        foreach (var role in ReviewRoles.Required(riskyPaths.Count > 0).Where(r => reviews.All(v => v.Role != r)))
        {
            problems.Add($"no {role} review");
        }
        foreach (var review in reviews)
        {
            if (review.Error is { } error)
            {
                problems.Add($"{review.Role} ({review.ServedName}): {error}");
            }
            foreach (var finding in review.Findings.Where(f => f.IsBlocking))
            {
                var how = finding.Confirmation is { Outcome: Confirmation.Confirmed } c ? $"confirmed by {c.ServedName}"
                    : finding.Confirmation is { } u ? $"unconfirmed, the second opinion was unusable: {u.Reason}"
                    : "not checked by a second opinion";
                problems.Add($"blocking {review.Role} finding by {review.ServedName}, {how}: {finding} — {finding.Detail}");
            }
        }
        var optional = reviews.Sum(r => r.Findings.Count(f => !f.IsBlocking));
        var downgraded = reviews.Sum(r => r.Findings.Count(f => f.Downgraded));
        var roles = string.Join(", ", reviews.Select(r =>
            $"{r.Role} by {r.ServedName}{(r.CarriedFrom is { } from ? $" (carried from {Ci.Short(from)})" : "")}"));
        return problems.Count > 0
            ? new ReviewVerdict(headSha, ReviewVerdict.Fail, string.Join("; ", problems), riskyPaths, reviews)
            : new ReviewVerdict(headSha, ReviewVerdict.Pass,
                $"{roles}: no blocking finding ({optional} optional, {downgraded} of them downgraded because the second opinion did not confirm them)",
                riskyPaths, reviews);
    }
}
