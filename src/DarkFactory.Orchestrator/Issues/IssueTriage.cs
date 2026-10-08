using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Issues;

[JsonConverter(typeof(JsonStringEnumConverter<IssueType>))]
public enum IssueType
{
    Bug,
    Feature,
    Question,
    Duplicate,
}

/// <summary>Where a triaged issue goes (the orchestrator decides it from the triage's structured output, E2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IssueRoute>))]
public enum IssueRoute
{
    /// <summary>A collaborator's issue with an apparent fix: it becomes a work item at once.</summary>
    Build,

    /// <summary>An outsider's bug or feature: triage comment and the <c>awaiting-approval</c> label; nothing is built until a collaborator approves.</summary>
    AwaitingApproval,

    /// <summary>A collaborator's issue with no apparent fix (or a triage that could not be read): triage comment and the <c>needs-human</c> label.</summary>
    NeedsHuman,

    /// <summary>A question: answered by the triage comment, never built.</summary>
    Question,

    /// <summary>A duplicate: the triage comment names what it duplicates, never built.</summary>
    Duplicate,
}

/// <summary>The fix the triage worker proposes: what to change and every repository path it would change.</summary>
public sealed record ProposedFix(string Description, IReadOnlyList<string> Paths);

/// <summary>
/// The triage worker's structured answer (<see cref="TriageParser"/>). Model output derived from untrusted issue text: it is
/// shown on the issue only inside a fence, and it is the only thing about the issue the implementing worker ever sees (E4).
/// </summary>
public sealed record Triage(IssueType Type, string Title, string Summary, IReadOnlyList<string> AffectedRepos, double Confidence, bool Reproduced,
    ProposedFix? Fix, string? DuplicateOf)
{
    /// <summary>The confidence at or above which a fix counts as confident without a reproduction (the apparent-fix threshold).</summary>
    public const double ConfidentAt = 0.8;

    /// <summary>Bugs and features are built; questions and duplicates never are.</summary>
    public bool Buildable => Type is IssueType.Bug or IssueType.Feature;
}

public sealed class TriageFormatException(string message) : Exception(message);

/// <summary>
/// Reads the triage worker's answer: the last fenced <c>json</c> block of its final message (or the whole message when it is one
/// JSON object). Every field is checked; anything malformed throws <see cref="TriageFormatException"/> saying what.
/// </summary>
public static partial class TriageParser
{
    public const int MaxTitle = 120;
    public const int MaxText = 4000;
    public const int MaxPaths = 50;

    [GeneratedRegex(@"```json[ \t]*\r?\n(.*?)\r?\n[ \t]*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoName();

    [GeneratedRegex(@"^[A-Za-z0-9_./@+-][A-Za-z0-9 _./@+-]*$")]
    private static partial Regex PathText();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static Triage Parse(string? text)
    {
        var blocks = JsonBlock().Matches(text ?? "");
        var json = blocks.Count > 0 ? blocks[^1].Groups[1].Value : (text ?? "").Trim();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new TriageFormatException("the answer is not one JSON object");
        }
        catch (JsonException ex)
        {
            throw new TriageFormatException($"the answer has no readable JSON block ({ex.Message})");
        }
        var type = String(root, "type", required: true)!.ToLowerInvariant() switch
        {
            "bug" => IssueType.Bug,
            "feature" => IssueType.Feature,
            "question" => IssueType.Question,
            "duplicate" => IssueType.Duplicate,
            var other => throw new TriageFormatException($"type '{other}' is not bug, feature, question or duplicate"),
        };
        var title = Whitespace().Replace(String(root, "title", required: true)!, " ").Trim();
        if (title.Length == 0)
        {
            throw new TriageFormatException("title is empty");
        }
        var summary = String(root, "summary", required: true)!.Trim();
        if (summary.Length == 0)
        {
            throw new TriageFormatException("summary is empty");
        }
        var repos = Strings(root, "affected_repos").Select(r => r.Trim()).ToList();
        if (repos.FirstOrDefault(r => !RepoName().IsMatch(r) || r.Split('/').Any(p => p.StartsWith('.'))) is { } badRepo)
        {
            throw new TriageFormatException($"affected_repos has '{Cut(badRepo, 80)}', not owner/name");
        }
        var confidence = root["confidence"] is JsonValue c && c.TryGetValue<double>(out var value) ? value
            : throw new TriageFormatException("confidence is not a number");
        if (double.IsNaN(confidence) || confidence < 0 || confidence > 1)
        {
            throw new TriageFormatException("confidence is not between 0 and 1");
        }
        var reproduced = root["reproduced"] switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw new TriageFormatException("reproduced is not true or false"),
        };
        ProposedFix? fix = null;
        if (root["proposed_fix"] is JsonObject fixNode)
        {
            var paths = Strings(fixNode, "paths").Select(p => p.Trim()).ToList();
            if (paths.Count > MaxPaths)
            {
                throw new TriageFormatException($"proposed_fix.paths lists more than {MaxPaths} paths");
            }
            var normalized = new List<string>();
            foreach (var path in paths)
            {
                if (!PathText().IsMatch(path) || RepoPath.Normalize(path) is not { } p)
                {
                    throw new TriageFormatException($"proposed_fix.paths has '{Cut(path, 80)}', not a path inside the repository");
                }
                normalized.Add(p);
            }
            fix = new ProposedFix(Cut(String(fixNode, "description", required: false)?.Trim() ?? "", MaxText), normalized.Distinct().ToList());
        }
        else if (root["proposed_fix"] is not null && root["proposed_fix"]!.GetValueKind() != JsonValueKind.Null)
        {
            throw new TriageFormatException("proposed_fix is not an object");
        }
        var duplicateOf = String(root, "duplicate_of", required: false)?.Trim();
        return new Triage(type, Cut(title, MaxTitle), Cut(summary, MaxText), repos.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), confidence,
            reproduced, fix, string.IsNullOrEmpty(duplicateOf) ? null : Cut(duplicateOf, 200));
    }

    private static string? String(JsonObject node, string key, bool required) => node[key] switch
    {
        null when !required => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.Null && !required => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw new TriageFormatException($"{key} is {(required ? "missing or " : "")}not a string"),
    };

    private static List<string> Strings(JsonObject node, string key) => node[key] switch
    {
        null => [],
        JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new TriageFormatException($"{key} lists a non-string"))
            .ToList(),
        _ => throw new TriageFormatException($"{key} is not a list"),
    };

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>Content hashes: an issue's version (its title and body) and a triage's (what an approval binds to).</summary>
public static class IssueHashes
{
    /// <summary>The issue's version: its title and body, line endings normalized. An edit to either is a new version.</summary>
    public static string Version(string title, string? body) => Hash($"{title}\n\n{body ?? ""}".Replace("\r\n", "\n", StringComparison.Ordinal));

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>What routing decided for one triage.</summary>
/// <param name="Target">The one watched repo the work would change (null when the triage names none, or several).</param>
/// <param name="Releasable">Whether a collaborator's <c>Approved</c> can release this triage to be built.</param>
public sealed record RouteDecision(IssueRoute Route, string Why, RepoRef? Target, bool Releasable);

/// <summary>
/// The routing rule (deterministic, from the triage's structured output, the author's permission and the target's gate policy):
/// <list type="bullet">
/// <item>a triage that could not be read → needs-human (nothing to build from);</item>
/// <item>a question or duplicate → its own route: a comment, never a build;</item>
/// <item>an author who is not a collaborator (<see cref="RepoPermission.IsCollaborator"/>) → awaiting-approval;</item>
/// <item>a collaborator's issue with an apparent fix (<see cref="ApparentFix"/>) → build; without one → needs-human.</item>
/// </list>
/// A bug or feature whose triage names exactly one watched repo is releasable: a collaborator's <c>Approved</c> builds it (also a
/// needs-human one: a collaborator's explicit approval outranks the apparent-fix rule). The factory builds one repo per work item,
/// so a triage naming several repos (or one the factory does not watch) needs a human.
/// </summary>
public static class IssueRouting
{
    /// <summary>The one watched repo the triage's work is in, or why there is none.</summary>
    public static (RepoRef? Target, string? WhyNot) Target(Triage triage, IReadOnlyCollection<RepoRef> watched)
    {
        if (triage.AffectedRepos.Count != 1)
        {
            return (null, triage.AffectedRepos.Count == 0
                ? "the triage names no affected repo"
                : $"the triage names {triage.AffectedRepos.Count} affected repos ({string.Join(", ", triage.AffectedRepos)}); the factory builds one repo per work item");
        }
        var named = triage.AffectedRepos[0];
        var target = watched.FirstOrDefault(r => string.Equals(r.FullName, named, StringComparison.OrdinalIgnoreCase));
        return target is null ? (null, $"{named} is not a repo the factory watches") : (target, null);
    }

    /// <summary>
    /// Null when the fix is apparent: a reproduced failure or a confidence of at least <see cref="Triage.ConfidentAt"/>, a proposed
    /// fix naming the paths it changes, and none of them sealed or protected under the target's <c>factory/gate.yaml</c> on its
    /// default branch (<paramref name="policy"/>; a missing or invalid policy cannot show that, so it is not apparent). Else why not.
    /// </summary>
    public static string? ApparentFix(Triage triage, GatePolicy? policy, string? policyError)
    {
        if (!triage.Reproduced && triage.Confidence < Triage.ConfidentAt)
        {
            return $"the failure was not reproduced and the confidence ({triage.Confidence.ToString("0.##", CultureInfo.InvariantCulture)}) is under "
                + Triage.ConfidentAt.ToString("0.##", CultureInfo.InvariantCulture);
        }
        if (triage.Fix is not { Paths.Count: > 0 } fix)
        {
            return "the triage proposes no fix naming the paths it would change";
        }
        if (policy is null)
        {
            return $"the target repo's {GatePolicy.Path} could not be read ({policyError ?? "it does not exist"}), so the fix's paths cannot be shown to be outside protected and sealed paths";
        }
        var guarded = fix.Paths.Select(policy.TierOf).Where(p => p.Tier is Tier.Sealed or Tier.Protected).ToList();
        return guarded.Count == 0 ? null : $"the fix touches {string.Join(", ", guarded)}";
    }

    public static RouteDecision Decide(Triage? triage, string? triageError, RepoPermission author, IReadOnlyCollection<RepoRef> watched,
        GatePolicy? policy, string? policyError)
    {
        if (triage is null)
        {
            return new RouteDecision(IssueRoute.NeedsHuman, $"the triage could not be completed: {triageError}", null, false);
        }
        if (triage.Type == IssueType.Question)
        {
            return new RouteDecision(IssueRoute.Question, "a question: the factory answers it here and builds nothing", null, false);
        }
        if (triage.Type == IssueType.Duplicate)
        {
            return new RouteDecision(IssueRoute.Duplicate, "a duplicate: the factory builds nothing for it", null, false);
        }
        var (target, noTarget) = Target(triage, watched);
        if (!author.IsCollaborator)
        {
            return new RouteDecision(IssueRoute.AwaitingApproval,
                $"the author is not a collaborator ({author}): nothing is built until a collaborator approves"
                + (noTarget is null ? "" : $"; and {noTarget}, so an approval cannot release it as triaged"),
                target, target is not null);
        }
        if (target is null)
        {
            return new RouteDecision(IssueRoute.NeedsHuman, noTarget!, null, false);
        }
        return ApparentFix(triage, policy, policyError) is { } notApparent
            ? new RouteDecision(IssueRoute.NeedsHuman, $"no apparent fix: {notApparent}", target, true)
            : new RouteDecision(IssueRoute.Build, "a collaborator's issue with an apparent fix: the factory builds it", target, true);
    }

    /// <summary>The label a route puts on the issue, if any.</summary>
    public static string? Label(IssueRoute route) => route switch
    {
        IssueRoute.AwaitingApproval => IssueLabels.AwaitingApproval,
        IssueRoute.NeedsHuman => IssueLabels.NeedsHuman,
        _ => null,
    };
}

public static class IssueLabels
{
    public const string AwaitingApproval = "awaiting-approval";
    public const string NeedsHuman = "needs-human";

    /// <summary>The issue's work item is the factory's (claimed); removed when it stops or merges.</summary>
    public const string Claimed = "factory-claimed";
}

/// <summary>
/// One triage of one issue version, as recorded in the ledger (the <c>triaged</c> checkpoint) before anything is posted: the
/// triage (or why there is none), the author's permission and the route. <see cref="Hash"/> is what an approval binds to.
/// </summary>
public sealed record TriageRecord(string Version, string Hash, Triage? Triage, string? Error, string Author, string AuthorPermission,
    IssueRoute Route, string Why, string? Target, bool Releasable)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TriageRecord Create(string version, Triage? triage, string? error, string author, RepoPermission permission, RouteDecision route)
    {
        var hash = IssueHashes.Hash($"{version}\n{JsonSerializer.Serialize(triage, Json)}\n{error}");
        return new TriageRecord(version, hash, triage, error, author, permission.ToString(), route.Route, route.Why, route.Target?.FullName,
            route.Releasable);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TriageRecord FromJson(string json) => JsonSerializer.Deserialize<TriageRecord>(json, Json)!;
}

/// <summary>
/// What the orchestrator posts on an issue, and how it recognises its own comments and an approval. Its comments carry a hidden
/// marker naming the triage they show (<c>&lt;!-- dark-factory:triage &lt;hash&gt; --&gt;</c>), trusted only on comments its own
/// App wrote: a retried post finds the one already there instead of posting twice, and an approval binds to the triage comment
/// before it.
/// </summary>
public static partial class IssueComments
{
    [GeneratedRegex(@"<!-- dark-factory:triage ([0-9a-f]{16}) -->")]
    private static partial Regex TriageMarker();

    /// <summary>The triage hash a comment of the factory's own App shows, or null.</summary>
    public static string? TriageHash(IssueComment comment, long appId) =>
        comment.AppId == appId && TriageMarker().Match(comment.Body) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// Whether the comment approves: its whole text, line endings normalized and surrounding whitespace trimmed, is exactly
    /// <c>Approved</c> (case-sensitive), and it was never edited. So <c>approved</c>, <c>Approved.</c>, <c>LGTM, Approved</c>, a quote
    /// (<c>&gt; Approved</c>), a code block, or a comment edited to say Approved later, never count.
    /// </summary>
    public static bool IsApproval(IssueComment comment) =>
        !comment.Edited && comment.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() == "Approved";

    /// <summary>The triage comment: the structured fields, the model's free text inside an inert fence, what happens next, and the marker.</summary>
    public static string Triage(TriageRecord record)
    {
        var text = new StringBuilder();
        text.AppendLine($"{WorkSources.WorkSourceComments.Author} Triage of this issue (triage `{record.Hash}`, issue version `{record.Version}`).");
        text.AppendLine();
        if (record.Triage is { } t)
        {
            text.AppendLine($"- Type: {t.Type.ToString().ToLowerInvariant()}");
            text.AppendLine($"- Affected repos: {(t.AffectedRepos.Count == 0 ? "none named" : string.Join(", ", t.AffectedRepos.Select(r => $"`{r}`")))}");
            text.AppendLine($"- Confidence: {t.Confidence.ToString("0.##", CultureInfo.InvariantCulture)}; reproduced: {(t.Reproduced ? "yes" : "no")}");
            if (t.Fix is { Paths.Count: > 0 } fix)
            {
                text.AppendLine($"- Proposed fix changes: {string.Join(", ", fix.Paths.Select(p => $"`{p}`"))}");
            }
            text.AppendLine();
            text.AppendLine(Fence($"{t.Title}\n\n{t.Summary}"
                + (t.Fix is { Description.Length: > 0 } f ? $"\n\nProposed fix: {f.Description}" : "")
                + (t.DuplicateOf is { } d ? $"\n\nDuplicate of: {d}" : "")));
        }
        else
        {
            text.AppendLine(Fence(record.Error ?? "the triage could not be completed"));
        }
        text.AppendLine();
        text.AppendLine($"Route: **{RouteName(record.Route)}**: {record.Why}.");
        text.AppendLine(record.Route switch
        {
            IssueRoute.Build => "The factory is building this; its pull request closes this issue when it merges.",
            IssueRoute.AwaitingApproval or IssueRoute.NeedsHuman when record.Releasable =>
                $"A collaborator (write, maintain or admin) can reply with a comment that says exactly `Approved` to have the factory build triage `{record.Hash}`. Editing the issue re-triages it and needs a new approval.",
            IssueRoute.AwaitingApproval or IssueRoute.NeedsHuman => "A human needs to look; the factory will not build this triage.",
            _ => "The factory builds nothing for it.",
        });
        text.Append($"<!-- dark-factory:triage {record.Hash} -->");
        return text.ToString();
    }

    public static string RouteName(IssueRoute route) => route switch
    {
        IssueRoute.Build => "build",
        IssueRoute.AwaitingApproval => "awaiting approval",
        IssueRoute.NeedsHuman => "needs a human",
        IssueRoute.Question => "question",
        IssueRoute.Duplicate => "duplicate",
        _ => route.ToString(),
    };

    /// <summary>Model text inside a tilde fence nothing in it can close: no mention, link or markup in it renders.</summary>
    private static string Fence(string text) => $"~~~~text\n{text.Replace("~~~", "~ ~ ~", StringComparison.Ordinal)}\n~~~~";
}
