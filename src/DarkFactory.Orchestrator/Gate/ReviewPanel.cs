using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// The review panel's roles. Correctness and spec conformance review every change; security only one that touches a
/// risky path (<see cref="RiskyPaths"/>). Each role has its own prompt file (<see cref="ReviewPrompts"/>) and its own
/// ordered model list (<see cref="ReviewPanelModels"/>).
/// </summary>
public static class ReviewRoles
{
    public const string Correctness = "correctness";
    public const string SpecConformance = "spec-conformance";
    public const string Security = "security";

    public static readonly IReadOnlyList<string> All = [Correctness, SpecConformance, Security];

    /// <summary>The roles that must review a change: security only when it touches a risky path.</summary>
    public static IReadOnlyList<string> Required(bool risky) => risky ? All : [Correctness, SpecConformance];

    /// <summary>The role's configuration section name (<c>Review:&lt;name&gt;:Models</c>).</summary>
    public static string ConfigName(string role) => role switch
    {
        Correctness => "Correctness",
        SpecConformance => "SpecConformance",
        Security => "Security",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "not a review role"),
    };
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

    /// <summary>The second model's prompt: does a blocking finding reproduce from the code.</summary>
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

/// <summary>The files a unified diff touches (both sides of a rename), read from its headers.</summary>
public static class DiffPaths
{
    private static readonly Regex GitHeader = new(@"^diff --git (?:""?a/(?<a>[^""]+?)""?) (?:""?b/(?<b>[^""]+?)""?)$", RegexOptions.Multiline);
    private static readonly Regex FileHeader = new(@"^(?:---|\+\+\+) ""?[ab]/(?<p>[^""\t\r\n]+)""?", RegexOptions.Multiline);
    private static readonly Regex Rename = new(@"^(?:rename|copy) (?:from|to) (?<p>[^\r\n]+)$", RegexOptions.Multiline);

    public static IReadOnlyList<string> Of(string diff)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in GitHeader.Matches(diff))
        {
            paths.Add(m.Groups["a"].Value);
            paths.Add(m.Groups["b"].Value);
        }
        foreach (Match m in FileHeader.Matches(diff))
        {
            paths.Add(m.Groups["p"].Value.TrimEnd());
        }
        foreach (Match m in Rename.Matches(diff))
        {
            paths.Add(m.Groups["p"].Value.Trim().Trim('"'));
        }
        return paths.ToList();
    }
}

/// <summary>
/// Which paths make a change risky, so the security reviewer joins the panel. Deterministic code, matched
/// case-insensitively against every path the diff touches.
/// </summary>
public static class RiskyPaths
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Each rule: what it guards, and the path pattern.</summary>
    public static readonly IReadOnlyList<(string Why, Regex Pattern)> Rules =
    [
        ("CI and repository automation", new Regex(@"^\.github/", Options)),
        ("the factory's own policy and prompts", new Regex(@"^factory/", Options)),
        ("scripts", new Regex(@"(^|/)scripts?/|\.(sh|bash|zsh|ps1)$", Options)),
        ("build and container definitions", new Regex(@"(^|/)(Dockerfile[^/]*|docker-compose[^/]*\.ya?ml|Makefile)$", Options)),
        ("dependencies", new Regex(
            @"(^|/)([^/]+\.(cs|fs|vb)proj|Directory\.(Packages|Build)\.props|nuget\.config|global\.json|dotnet-tools\.json|package(-lock)?\.json|yarn\.lock|pnpm-lock\.yaml|requirements[^/]*\.txt|pyproject\.toml|poetry\.lock|go\.(mod|sum)|Cargo\.(toml|lock)|Gemfile(\.lock)?)$",
            Options)),
        ("keys and environment files", new Regex(@"\.(pem|key|p12|pfx|crt|cer)$|(^|/)\.env", Options)),
        ("security-sensitive code", new Regex(
            @"auth|secret|credential|token|crypto|passw|permission|sandbox|sudo|keychain|login|oauth|jwt|cert|ssh|security|acl|ruleset|protect",
            Options)),
    ];

    /// <summary>What <see cref="Touched(IReadOnlyList{string}, string)"/> reports for a change whose paths cannot be read.</summary>
    public const string Unparsed = "(unparsed diff: no file headers, so the change counts as risky)";

    /// <summary>The touched paths that are risky, each with why; empty when the change is not risky.</summary>
    public static IReadOnlyList<string> Touched(IEnumerable<string> paths) =>
        paths.Select(p => Rules.FirstOrDefault(r => r.Pattern.IsMatch(p)) is { Pattern: not null } rule ? $"{p} ({rule.Why})" : null)
            .OfType<string>()
            .ToList();

    /// <summary>
    /// <see cref="Touched(IEnumerable{string})"/> for the <paramref name="paths"/> read from <paramref name="diff"/>. A
    /// non-empty diff none of whose paths could be read is risky (<see cref="Unparsed"/>): a check that cannot run counts as
    /// failed (E2), so the security review is not skipped.
    /// </summary>
    public static IReadOnlyList<string> Touched(IReadOnlyList<string> paths, string diff) =>
        paths.Count == 0 && !string.IsNullOrWhiteSpace(diff) ? [Unparsed] : Touched(paths);
}

/// <summary>
/// The panel's model lists, each in order of preference. A role reviews with the first of its list whose family the
/// implementer did not use; a blocking finding is confirmed by the first of <see cref="Confirm"/> that is not the
/// reviewer's model and not of the implementer's family, preferring one also of a family other than the reviewer's
/// (<see cref="ReviewerChoice"/>).
/// </summary>
public sealed record ReviewPanelModels(IReadOnlyDictionary<string, IReadOnlyList<string>> Roles, IReadOnlyList<string> Confirm)
{
    /// <summary>The router's deployed anthropic and openai models: the families the router key is known to serve.</summary>
    public static readonly IReadOnlyList<string> DefaultReviewers = ["gpt-5.5", "claude-opus-5"];

    /// <summary>Two models per family, so a second model exists whichever family the implementer and reviewer used.</summary>
    public static readonly IReadOnlyList<string> DefaultConfirmers = ["gpt-5.5", "claude-opus-5", "gpt-5.4-mini", "claude-sonnet-5"];

    public static readonly ReviewPanelModels Default = Uniform(DefaultReviewers, DefaultConfirmers);

    /// <summary>Every role with the same <paramref name="reviewers"/>.</summary>
    public static ReviewPanelModels Uniform(IReadOnlyList<string> reviewers, IReadOnlyList<string>? confirmers = null) =>
        new(ReviewRoles.All.ToDictionary(r => r, _ => reviewers), confirmers ?? DefaultConfirmers);

    public IReadOnlyList<string> For(string role) =>
        Roles.TryGetValue(role, out var models) ? models : throw new ReviewerChoiceException($"No reviewer models are configured for the {role} review.");
}

/// <summary>
/// A second model's answer on one blocking finding. <see cref="Outcome"/>: <c>confirmed</c> (it reproduces from the code),
/// <c>not-confirmed</c> (the finding is downgraded to optional), or <c>unusable</c> (no clear answer from the pinned family:
/// the finding stays blocking, fail-closed).
/// </summary>
public sealed record Confirmation(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("served")] string? ServedModel,
    [property: JsonPropertyName("family")] string? Family,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("reason")] string Reason)
{
    public const string Confirmed = "confirmed";
    public const string NotConfirmed = "not-confirmed";
    public const string Unusable = "unusable";
}

/// <summary>One finding of a panel role. <see cref="Downgraded"/>: it was blocking, and the second model did not confirm it.</summary>
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
    /// The finding after its second model answered: not confirmed → optional (and does not block); confirmed or an unusable
    /// answer → still blocking.
    /// </summary>
    public Finding ConfirmedBy(Confirmation confirmation) => confirmation.Outcome == Confirmation.NotConfirmed
        ? this with { Severity = Optional, Downgraded = true, Confirmation = confirmation }
        : this with { Confirmation = confirmation };

    public override string ToString() => $"{Title}{(File is null ? "" : $" ({File}{(Line is null ? "" : $":{Line}")})")}";
}

/// <summary>
/// One panel role's review of one head commit: the model pinned, the model the router said answered and its family, the
/// router session, the prompt (path and hash), the findings, and <see cref="Error"/> when the answer was unusable (which
/// fails the review).
/// </summary>
public sealed record RoleReview(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("served")] string? ServedModel,
    [property: JsonPropertyName("family")] string? Family,
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("error")] string? Error = null)
{
    [JsonIgnore]
    public bool Clean => Error is null;
}

public static class ReviewPanel
{
    /// <summary>
    /// The panel's verdict on <paramref name="headSha"/> (deterministic, E1: models only produce findings). Pass only when
    /// every required role (security when <paramref name="riskyPaths"/> is not empty) reviewed with a usable answer and no
    /// finding is blocking after confirmation.
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
                problems.Add($"{review.Role} ({review.ServedModel ?? review.Model}): {error}");
            }
            foreach (var finding in review.Findings.Where(f => f.IsBlocking))
            {
                var how = finding.Confirmation is { Outcome: Confirmation.Confirmed } c ? $"confirmed by {c.ServedModel ?? c.Model}"
                    : finding.Confirmation is { } u ? $"unconfirmed, the second model's answer was unusable: {u.Reason}"
                    : "not checked by a second model";
                problems.Add($"blocking {review.Role} finding by {review.ServedModel ?? review.Model}, {how}: {finding} — {finding.Detail}");
            }
        }
        var optional = reviews.Sum(r => r.Findings.Count(f => !f.IsBlocking));
        var downgraded = reviews.Sum(r => r.Findings.Count(f => f.Downgraded));
        var roles = string.Join(", ", reviews.Select(r => $"{r.Role} by {r.ServedModel ?? r.Model}"));
        return problems.Count > 0
            ? new ReviewVerdict(headSha, ReviewVerdict.Fail, string.Join("; ", problems), riskyPaths, reviews)
            : new ReviewVerdict(headSha, ReviewVerdict.Pass,
                $"{roles}: no blocking finding ({optional} optional, {downgraded} of them downgraded because the second model did not confirm them)",
                riskyPaths, reviews);
    }
}
