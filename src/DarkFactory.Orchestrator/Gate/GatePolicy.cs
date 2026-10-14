using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>One tier of the policy: the path patterns that put a path in it (none for normal) and the checks it requires.</summary>
public sealed record TierRule(Tier Tier, IReadOnlyList<PathPattern> Paths, IReadOnlySet<string> Checks);

/// <summary>
/// The risk threshold: a change over any limit is not merged by the gate (when its tiers require the check).
/// <see cref="MaxBlastRadius"/>: version 3's <c>max_blast_radius</c>, the worst CodeGraph blast radius a touched file may have at the
/// head (null: the policy sets none, version 2); the code floor (<see cref="GatePolicy.BlastRadiusFloor"/>) applies whatever it says.
/// <see cref="MaxHotspot"/>: version 3's <c>max_hotspot</c>, the highest hotspot score (CodeGraph's <c>riskScore</c>) a touched file
/// may have on the default branch (null: <c>off</c>, or version 2; there is no code floor for hotspots).
/// <see cref="MaxChangesetLines"/> and <see cref="MaxChangesetFiles"/>: version 4's <c>max_changeset_lines</c> and
/// <c>max_changeset_files</c>, the size limits of a plan's changeset pull request (sc-25873), which <see cref="LinesLimit"/> and
/// <see cref="FilesLimit"/> apply in place of <see cref="MaxChangedLines"/> and <see cref="MaxChangedFiles"/> (null: versions 2 and
/// 3, where a changeset pull request is held to the ordinary limits like any other).
/// </summary>
public sealed record RiskThreshold(int MaxChangedLines, int MaxChangedFiles, int MaxFixRounds, BlastRadius? MaxBlastRadius = null,
    double? MaxHotspot = null, int? MaxChangesetLines = null, int? MaxChangesetFiles = null)
{
    /// <summary>The changed-lines limit of a pull request and the policy key that sets it: a changeset's own limit when the policy has one.</summary>
    public (int Max, string Key) LinesLimit(bool changeset) =>
        changeset && MaxChangesetLines is { } max ? (max, "max_changeset_lines") : (MaxChangedLines, "max_changed_lines");

    /// <summary>The changed-files limit of a pull request and the policy key that sets it: a changeset's own limit when the policy has one.</summary>
    public (int Max, string Key) FilesLimit(bool changeset) =>
        changeset && MaxChangesetFiles is { } max ? (max, "max_changeset_files") : (MaxChangedFiles, "max_changed_files");
}

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
/// Version 3 (sc-25709) is version 2 plus two required risk keys, CodeGraph's inputs to <c>risk-threshold</c>:
/// <code>
/// risk:
///   ...
///   max_blast_radius: high        # low | medium | high: the worst blast radius a touched file may have at the head
///   max_hotspot: off              # off, or a number >= 0: the highest hotspot score (riskScore) a touched file may have
/// </code>
/// <c>max_blast_radius</c> is a maximum, like <c>max_changed_lines</c>: a file over it fails. The code floor
/// (<see cref="BlastRadiusFloor"/>, high: a critical blast radius always fails) applies to every version, so version 3 can only
/// tighten it; <c>critical</c> — which would allow a critical file — is looser than the floor and invalid. <c>max_hotspot</c> is
/// required too (every key is), and <c>off</c> is its explicit opt-out: hotspots have no code floor, they are recorded and
/// reported, and fail only a repository that sets a limit. Version 2 parses as before and runs at the floor with no hotspot limit.
/// Version 4 (sc-25873) is version 3 plus two required risk keys, the size limits of a plan's changeset pull request:
/// <code>
/// risk:
///   ...
///   max_changeset_lines: 4000     # a whole number >= 1: the most changed lines a changeset pull request may have
///   max_changeset_files: 150      # a whole number >= 1: the most changed files a changeset pull request may have
/// </code>
/// A changeset pull request (a plan item's, from its changeset branch into the default branch: the sum of the plan's stories) is
/// held to these two in place of <c>max_changed_lines</c> and <c>max_changed_files</c>, which are sized for one story; every other
/// pull request — a plan's story into its changeset branch included — keeps the ordinary two. Which pull request is a changeset's is
/// a ledger fact the pipeline passes in (<see cref="ChangeFacts.Changeset"/>), never read from a branch name. The four limits are
/// independent whole numbers of at least 1: no rule ties a changeset limit to the per-story one (a smaller one only tightens the
/// gate, which a policy may always do). There is no code floor on size, so none is loosened: the check is not skipped for a
/// changeset, and under versions 2 and 3 — which have no changeset keys, each version's key set being strict — a changeset pull
/// request is held to the ordinary limits as before.
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

    /// <summary>The newest policy version; <see cref="Versions"/> are all the gate accepts.</summary>
    public const int Version = 4;

    /// <summary>
    /// The versions the gate accepts: 2 (runs at the code floor of every CodeGraph input), 3 (CodeGraph's limits) and 4 (a
    /// changeset pull request's own size limits).
    /// </summary>
    public static readonly IReadOnlyList<int> Versions = [2, 3, 4];

    /// <summary>The risk keys of every version; each later version requires its own on top, and no version accepts a later one's.</summary>
    private static readonly string[] RiskKeys = ["max_changed_lines", "max_changed_files", "max_fix_rounds"];
    private static readonly string[] CodeGraphRiskKeys = ["max_blast_radius", "max_hotspot"];
    private static readonly string[] ChangesetRiskKeys = ["max_changeset_lines", "max_changeset_files"];

    /// <summary>
    /// The code floor of <c>risk-threshold</c>'s CodeGraph blast radius (sc-25709, E7): the worst blast radius a touched file may
    /// have whatever the policy says — high, so a critical file always fails. A version 3 <c>max_blast_radius</c> can only lower it.
    /// </summary>
    public const BlastRadius BlastRadiusFloor = BlastRadius.High;

    /// <summary>The <c>max_hotspot</c> value that sets no hotspot limit.</summary>
    public const string HotspotOff = "off";

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
            .Concat(GateCheckSeam.Off(GateCheckSeam.FloorSecurityReviewPaths) ? []
                : RiskyPaths.Touched(change.Paths.Select(p => p.Path).Where(p => !tiered.Contains(p))))
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
        if (Versions.FirstOrDefault(v => version == v.ToString(CultureInfo.InvariantCulture)) is not (> 0 and var number))
        {
            throw new GatePolicyException($"{Path}: version must be {string.Join(", ", Versions.SkipLast(1))} or {Versions[^1]}.");
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
        Expect(riskNode, [.. RiskKeys, .. number >= 3 ? CodeGraphRiskKeys : [], .. number >= 4 ? ChangesetRiskKeys : []], "risk");
        // max_fix_rounds can only lower the factory's hard cap (Lifecycle.MaxFixRounds), never raise it: 1 to that cap.
        var risk = new RiskThreshold(Int(riskNode, "max_changed_lines", 1), Int(riskNode, "max_changed_files", 1),
            Int(riskNode, "max_fix_rounds", 1, Ledger.Lifecycle.MaxFixRounds),
            number == 2 ? null : MaxBlastRadius(riskNode), number == 2 ? null : MaxHotspot(riskNode),
            number >= 4 ? Int(riskNode, "max_changeset_lines", 1) : null, number >= 4 ? Int(riskNode, "max_changeset_files", 1) : null);

        var policy = new GatePolicy(tiers, risk);
        var uncovered = MustBeSealedFiles.Where(f => policy.TierOf(f).Tier != Tier.Sealed)
            .Concat(MustBeSealedDirectories.Where(d => !tiers[Tier.Sealed].Paths.Any(p => p.CoversEverythingUnder(d))));
        if (uncovered.FirstOrDefault() is { } path && !GateCheckSeam.Off(GateCheckSeam.FloorSealedPaths))
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
        if (FloorChecks[tier].FirstOrDefault(c => !checks.Contains(c)) is { } missing && !GateCheckSeam.Off(GateCheckSeam.FloorChecks))
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

    /// <summary>
    /// <c>risk.max_blast_radius</c>: low, medium or high. Critical (it would allow a critical file) is looser than
    /// <see cref="BlastRadiusFloor"/> and refused.
    /// </summary>
    private static BlastRadius MaxBlastRadius(YamlMappingNode node)
    {
        var text = Scalar(node, "max_blast_radius", "risk");
        var allowed = string.Join(", ", Enum.GetValues<BlastRadius>().Where(r => r is > BlastRadius.None and <= BlastRadiusFloor).Select(BlastRadii.Name));
        var known = BlastRadii.TryParse(text, out var radius);
        if (known && radius > BlastRadiusFloor && !GateCheckSeam.Off(GateCheckSeam.FloorBlastRadius))
        {
            throw new GatePolicyException(
                $"{Path}: risk.max_blast_radius '{text}' is looser than the code floor ({BlastRadii.Name(BlastRadiusFloor)}: a {BlastRadii.Name(BlastRadius.Critical)} blast radius always fails); it must be one of {allowed}.");
        }
        return known && radius > BlastRadius.None
            ? radius
            : throw new GatePolicyException($"{Path}: risk.max_blast_radius must be one of {allowed}.");
    }

    /// <summary><c>risk.max_hotspot</c>: <c>off</c> (null), or a number of at least 0 — the highest hotspot score (riskScore) allowed.</summary>
    private static double? MaxHotspot(YamlMappingNode node)
    {
        var text = Scalar(node, "max_hotspot", "risk");
        if (text == HotspotOff)
        {
            return null;
        }
        return text is { Length: > 0 } && text.All(c => char.IsAsciiDigit(c) || c == '.')
            && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new GatePolicyException(
                $"{Path}: risk.max_hotspot must be {HotspotOff} or a number of at least 0 (the highest hotspot score, CodeGraph's riskScore, a touched file may have).");
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
            @"(^|/)([^/]+\.(cs|fs|vb)proj|Directory\.(Packages|Build)\.props|nuget\.config|global\.json|dotnet-tools\.json|package(-lock)?\.json|yarn\.lock|pnpm-lock\.yaml|requirements[^/]*\.txt|pyproject\.toml|poetry\.lock|go\.(mod|sum)|Cargo\.(toml|lock)|Gemfile(\.lock)?|Package(@swift-[^/]*)?\.swift|Package\.resolved)$",
            Options)),
        // Rust: a build script runs at build time, the toolchain file picks the compiler, cargo's config can set runners, linkers and registries.
        ("Rust build and toolchain configuration", new Regex(@"(^|/)(build\.rs|rust-toolchain[^/]*|\.cargo/config[^/]*)$", Options)),
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
