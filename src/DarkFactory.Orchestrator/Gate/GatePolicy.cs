using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>One tier of the policy: the path patterns that put a path in it (none for normal) and the checks it requires.</summary>
public sealed record TierRule(Tier Tier, IReadOnlyList<PathPattern> Paths, IReadOnlySet<string> Checks);

/// <summary>The risk threshold: a change over any limit is not merged by the gate (when its tiers require the check).</summary>
public sealed record RiskThreshold(int MaxChangedLines, int MaxChangedFiles, int MaxFixRounds);

/// <summary>
/// A target repository's merge policy, <c>factory/gate.yaml</c> on the PR's base branch (never the PR's own head, which
/// could rewrite the rules it is judged by). Read fresh on every evaluation (E1). Version 2 (sc-25381) — path tiers and a
/// risk threshold:
/// <code>
/// version: 2
/// tiers:
///   sealed:                       # always escalates
///     paths: [factory/gate.yaml, .github/workflows/, CODEOWNERS, .github/CODEOWNERS, docs/CODEOWNERS, factory/prompts/]
///     checks: [ci-green, review-pass, security-review]
///   protected:                    # built and reviewed; merged only after escalation
///     paths: ["**/auth/", "**/migrations/", infra/, "**/secrets/", "**/payments/"]
///     checks: [ci-green, review-pass, security-review, risk-threshold, new-tests-fail-on-base]
///   normal:                       # every path no other tier matches (no paths key)
///     checks: [ci-green, review-pass, risk-threshold, new-tests-fail-on-base]
///   free:
///     paths: [docs/, tests/]
///     checks: [ci-green, review-pass]
/// risk:
///   max_changed_lines: 800
///   max_changed_files: 40
///   max_fix_rounds: 2             # 1-3: lowers the factory's hard cap (Lifecycle.MaxFixRounds), never raises it
/// </code>
/// A path is in the first of sealed, protected, free whose patterns match it (<see cref="PathPattern"/>: anchored at the
/// root; sealed and protected patterns match case-insensitively, free ones case-sensitively, so a path's case can only
/// move it to a stricter tier), else normal. Every tier and every risk field is required.
/// <para>
/// The policy can tighten the gate but never loosen it below floors enforced in code (E2), so there is deliberately no way
/// to switch a rule off:
/// <list type="bullet">
/// <item>checks: every tier lists <c>ci-green</c> and <c>review-pass</c>; sealed and protected also <c>security-review</c>;
/// protected and normal also <c>risk-threshold</c> and <c>new-tests-fail-on-base</c> (<see cref="FloorChecks"/>; so a code
/// change in those tiers that adds no test, or only tests that already pass on the base, does not merge; free and sealed
/// changes need no new test unless the policy adds the check);</item>
/// <item>sealed paths: the sealed tier must cover the gate's own files and directories (<see cref="MustBeSealed"/>; a
/// directory only by a pattern covering everything under it, never by naming files in it);</item>
/// <item>security-review paths: a touched path calls the security review in when its tier lists <c>security-review</c>
/// <em>or</em> it matches the code floor <see cref="RiskyPaths"/> (scripts, build and container files, dependency
/// manifests, keys, security-sensitive names) — <see cref="SecurityReviewReasons"/>. The policy can add paths that need the
/// security review; it cannot remove the floor.</item>
/// </list>
/// </para>
/// Version 1 (<c>require: {ci, review}</c>, sc-25378) is rejected with a reason: it names no tiers. Anything invalid means
/// no merge and an escalation.
/// </summary>
public sealed class GatePolicy
{
    public const string Path = "factory/gate.yaml";
    public const int Version = 2;

    /// <summary>
    /// Files the sealed tier must match, or the policy is invalid: the policy itself and CODEOWNERS wherever GitHub reads it.
    /// A policy that unseals its own file could be loosened by the PRs it judges.
    /// </summary>
    public static readonly IReadOnlyList<string> MustBeSealedFiles = [Path, "CODEOWNERS", ".github/CODEOWNERS", "docs/CODEOWNERS"];

    /// <summary>
    /// Directories one sealed pattern must cover entirely (<see cref="PathPattern.CoversEverythingUnder"/>), or the policy is
    /// invalid: CI workflows and the factory's prompts. Listing some files in them is not enough.
    /// </summary>
    public static readonly IReadOnlyList<string> MustBeSealedDirectories = [".github/workflows/", "factory/prompts/"];

    /// <summary>Everything the sealed tier must cover: <see cref="MustBeSealedFiles"/> and <see cref="MustBeSealedDirectories"/>.</summary>
    public static readonly IReadOnlyList<string> MustBeSealed = [.. MustBeSealedFiles, .. MustBeSealedDirectories];

    /// <summary>The checks each tier must list at least (E2): a policy can require more, never fewer.</summary>
    public static readonly IReadOnlyDictionary<Tier, string[]> FloorChecks = new Dictionary<Tier, string[]>
    {
        [Tier.Sealed] = [GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.SecurityReview],
        [Tier.Protected] = [GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.SecurityReview, GateChecks.RiskThreshold, GateChecks.NewTestsFailOnBase],
        [Tier.Normal] = [GateChecks.CiGreen, GateChecks.ReviewPass, GateChecks.RiskThreshold, GateChecks.NewTestsFailOnBase],
        [Tier.Free] = [GateChecks.CiGreen, GateChecks.ReviewPass],
    };

    private GatePolicy(IReadOnlyDictionary<Tier, TierRule> tiers, RiskThreshold risk)
    {
        TierRules = tiers;
        Risk = risk;
    }

    public IReadOnlyDictionary<Tier, TierRule> TierRules { get; }

    public RiskThreshold Risk { get; }

    /// <summary>
    /// The tier of one path as a diff names it. A path that cannot name a repository file (<see cref="RepoPath.Normalize"/>)
    /// is sealed: it cannot be shown to be in a lighter tier.
    /// </summary>
    public TouchedPath TierOf(string path)
    {
        if (RepoPath.Normalize(path) is not { } normalized)
        {
            return new TouchedPath(path, Tier.Sealed, "not a path inside the repository");
        }
        var tier = Tiers.Precedence.First(t => t == Tier.Normal || TierRules[t].Paths.Any(p => p.Matches(normalized)));
        return new TouchedPath(normalized, tier);
    }

    /// <summary>
    /// Classifies the change a unified diff (of the candidate head against the base) makes: every path it touches — both
    /// sides of a rename, deleted files too — with its tier, the union of the touched tiers' checks, and its size. A
    /// non-empty diff none of whose paths can be read is sealed (a check that cannot run counts as failed, E2); an empty
    /// diff is a normal change of nothing.
    /// </summary>
    public Classification Classify(string diff)
    {
        var facts = DiffPaths.Parse(diff);
        var paths = facts.Paths.Select(TierOf).ToList();
        if (paths.Count == 0)
        {
            paths.Add(string.IsNullOrWhiteSpace(diff)
                ? new TouchedPath("(empty diff)", Tier.Normal, "no file changed")
                : new TouchedPath("(unparsed diff)", Tier.Sealed, "no file header could be read"));
        }
        var checks = paths.SelectMany(p => TierRules[p.Tier].Checks).ToHashSet(StringComparer.Ordinal);
        return new Classification(paths, checks, facts.ChangedLines, facts.Paths.Count);
    }

    /// <summary>The touched paths whose tier requires the security review: they call the security reviewer in.</summary>
    public IReadOnlyList<TouchedPath> SecurityReviewPaths(Classification change) =>
        change.Paths.Where(p => TierRules[p.Tier].Checks.Contains(GateChecks.SecurityReview)).ToList();

    /// <summary>
    /// Why the change needs the security review, one entry per path that calls it in: each path whose tier requires it
    /// (<see cref="SecurityReviewPaths"/>, e.g. <c>src/auth/Login.cs (protected)</c>), then each other path the code floor
    /// <see cref="RiskyPaths"/> matches (e.g. <c>scripts/x.sh (scripts)</c>). Empty: no security review is required.
    /// </summary>
    public IReadOnlyList<string> SecurityReviewReasons(Classification change)
    {
        var byTier = SecurityReviewPaths(change);
        var tiered = byTier.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        return byTier.Select(p => p.ToString())
            .Concat(RiskyPaths.Touched(change.Paths.Select(p => p.Path).Where(p => !tiered.Contains(p))))
            .ToList();
    }

    /// <summary>Parses <paramref name="yaml"/>; throws <see cref="GatePolicyException"/> saying what is wrong.</summary>
    public static GatePolicy Parse(string yaml)
    {
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or ArgumentException)
        {
            throw new GatePolicyException($"{Path} is not valid YAML: {ex.Message}");
        }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new GatePolicyException($"{Path} must be one YAML mapping.");
        }
        var version = Scalar(root, "version", "the top level");
        if (version == "1")
        {
            throw new GatePolicyException(
                $"{Path} is version 1, which this gate no longer accepts: it needs version {Version}, with path tiers (sealed, protected, normal, free) and a risk threshold.");
        }
        if (version != Version.ToString(CultureInfo.InvariantCulture))
        {
            throw new GatePolicyException($"{Path}: version must be {Version}.");
        }
        Expect(root, ["version", "tiers", "risk"], "the top level");

        var tiersNode = Mapping(root, "tiers", "the top level");
        Expect(tiersNode, Tiers.Precedence.Select(t => t.Key()), "tiers");
        var tiers = new Dictionary<Tier, TierRule>();
        foreach (var tier in Tiers.Precedence)
        {
            tiers[tier] = ParseTier(tier, Mapping(tiersNode, tier.Key(), "tiers"));
        }

        var riskNode = Mapping(root, "risk", "the top level");
        Expect(riskNode, ["max_changed_lines", "max_changed_files", "max_fix_rounds"], "risk");
        // max_fix_rounds can only lower the factory's hard cap (Lifecycle.MaxFixRounds), never raise it: 1 to that cap.
        var risk = new RiskThreshold(Int(riskNode, "max_changed_lines", 1), Int(riskNode, "max_changed_files", 1),
            Int(riskNode, "max_fix_rounds", 1, Ledger.Lifecycle.MaxFixRounds));

        var policy = new GatePolicy(tiers, risk);
        var uncovered = MustBeSealedFiles.Where(f => policy.TierOf(f).Tier != Tier.Sealed)
            .Concat(MustBeSealedDirectories.Where(d => !tiers[Tier.Sealed].Paths.Any(p => p.CoversEverythingUnder(d))));
        if (uncovered.FirstOrDefault() is { } path)
        {
            throw new GatePolicyException(
                $"{Path}: the sealed tier must cover {path} (the gate's own files: {string.Join(", ", MustBeSealed)}; a directory by a pattern covering everything under it, such as {MustBeSealedDirectories[0]}).");
        }
        return policy;
    }

    private static TierRule ParseTier(Tier tier, YamlMappingNode node)
    {
        var where = $"tiers.{tier.Key()}";
        // Normal is every path no other tier matches: it has no patterns of its own.
        Expect(node, tier == Tier.Normal ? ["checks"] : ["paths", "checks"], where);
        var patterns = new List<PathPattern>();
        if (tier != Tier.Normal)
        {
            foreach (var text in List(node, "paths", where))
            {
                // Sealed and protected ignore case, so src/Auth/ is as protected as src/auth/; free stays exact.
                patterns.Add(PathPattern.TryParse(text, out var error, ignoreCase: tier is Tier.Sealed or Tier.Protected)
                    ?? throw new GatePolicyException($"{Path}: {where}.paths: {error}."));
            }
        }
        var checks = List(node, "checks", where).ToHashSet(StringComparer.Ordinal);
        if (checks.FirstOrDefault(c => !GateChecks.All.Contains(c)) is { } unknown)
        {
            throw new GatePolicyException($"{Path}: {where}.checks has an unknown check '{unknown}' (known: {string.Join(", ", GateChecks.All)}).");
        }
        if (FloorChecks[tier].FirstOrDefault(c => !checks.Contains(c)) is { } missing)
        {
            throw new GatePolicyException($"{Path}: {where}.checks must include {missing} (every {tier.Key()} change needs {string.Join(", ", FloorChecks[tier])}).");
        }
        return new TierRule(tier, patterns, checks);
    }

    private static void Expect(YamlMappingNode node, IEnumerable<string> expected, string where)
    {
        var keys = node.Children.Keys
            .Select(k => k is YamlScalarNode { Value: { } v } ? v : throw new GatePolicyException($"{Path}: {where} has a non-scalar key."))
            .ToList();
        var wanted = expected.ToHashSet(StringComparer.Ordinal);
        if (keys.FirstOrDefault(k => !wanted.Contains(k)) is { } unknown)
        {
            throw new GatePolicyException($"{Path}: unknown key '{unknown}' in {where}.");
        }
        if (wanted.FirstOrDefault(k => !keys.Contains(k)) is { } missing)
        {
            throw new GatePolicyException($"{Path}: {where} is missing '{missing}'.");
        }
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static string? Scalar(YamlMappingNode node, string key, string where) => Child(node, key) switch
    {
        null => throw new GatePolicyException($"{Path}: {where} is missing '{key}'."),
        YamlScalarNode { Value: var v } => v?.Trim(),
        _ => throw new GatePolicyException($"{Path}: {where}.{key} must be a value."),
    };

    private static YamlMappingNode Mapping(YamlMappingNode node, string key, string where) =>
        Child(node, key) as YamlMappingNode ?? throw new GatePolicyException($"{Path}: {key} in {where} must be a mapping.");

    private static List<string> List(YamlMappingNode node, string key, string where)
    {
        if (Child(node, key) is not YamlSequenceNode { Children.Count: > 0 } sequence)
        {
            throw new GatePolicyException($"{Path}: {where}.{key} must be a non-empty list.");
        }
        return sequence.Children
            .Select(c => c is YamlScalarNode { Value: { Length: > 0 } v } ? v.Trim()
                : throw new GatePolicyException($"{Path}: {where}.{key} must list plain values."))
            .ToList();
    }

    private static int Int(YamlMappingNode node, string key, int min, int? max = null)
    {
        var text = Scalar(node, key, "risk");
        return text is not null && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= min && (max is null || value <= max)
            ? value
            : throw new GatePolicyException(max is null
                ? $"{Path}: risk.{key} must be a whole number of at least {min}."
                : $"{Path}: risk.{key} must be a whole number from {min} to {max} (it can only lower the factory's hard cap of {max} fix rounds).");
    }
}

public sealed class GatePolicyException(string message) : Exception(message);

/// <summary>
/// The code floor of paths that always call the security review in, whatever their tier in <c>factory/gate.yaml</c>
/// (<see cref="GatePolicy.SecurityReviewReasons"/>): the policy can add security-review paths through its tiers but cannot
/// remove these (E2), as it cannot unseal <see cref="GatePolicy.MustBeSealed"/>. Matched case-insensitively against every
/// path the diff touches.
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

    /// <summary>The touched paths the floor matches, each with why; empty when none does.</summary>
    public static IReadOnlyList<string> Touched(IEnumerable<string> paths) =>
        paths.Select(p => Rules.FirstOrDefault(r => r.Pattern.IsMatch(p)) is { Pattern: not null } rule ? $"{p} ({rule.Why})" : null)
            .OfType<string>()
            .ToList();
}
