using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// A target repository's merge policy, <c>factory/gate.yaml</c> on the PR's base branch (never the PR's own head, which
/// could rewrite the rules it is judged by). Read fresh on every gate evaluation (E1). The only accepted form, for now:
/// <code>
/// version: 1
/// require:
///   ci: green
///   review: pass
/// </code>
/// Anything else (a missing or unknown key, another value, YAML that does not parse) is invalid: no merge, escalate.
/// There is deliberately no way to switch a rule off (E2).
/// </summary>
public sealed record GatePolicy
{
    public const string Path = "factory/gate.yaml";

    /// <summary>The rules this version of the gate knows, each with the only value it accepts.</summary>
    public static readonly IReadOnlyDictionary<string, string> Rules = new Dictionary<string, string>
    {
        ["ci"] = "green",
        ["review"] = "pass",
    };

    private GatePolicy()
    {
    }

    public static readonly GatePolicy Default = new();

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
        var keys = Keys(root, "the top level");
        Expect(keys, ["version", "require"], "the top level");
        if (Scalar(root, "version") != "1")
        {
            throw new GatePolicyException($"{Path}: version must be 1.");
        }
        if (root.Children[new YamlScalarNode("require")] is not YamlMappingNode require)
        {
            throw new GatePolicyException($"{Path}: require must be a mapping of rules.");
        }
        Expect(Keys(require, "require"), Rules.Keys, "require");
        foreach (var (rule, value) in Rules)
        {
            if (Scalar(require, rule) != value)
            {
                throw new GatePolicyException($"{Path}: require.{rule} must be '{value}'.");
            }
        }
        return Default;
    }

    private static List<string> Keys(YamlMappingNode node, string where) =>
        node.Children.Keys.Select(k => k is YamlScalarNode { Value: { } v } ? v : throw new GatePolicyException($"{Path}: {where} has a non-scalar key.")).ToList();

    private static void Expect(List<string> keys, IEnumerable<string> expected, string where)
    {
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

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children[new YamlScalarNode(key)] is YamlScalarNode { Value: var v } ? v?.Trim() : null;
}

public sealed class GatePolicyException(string message) : Exception(message);
