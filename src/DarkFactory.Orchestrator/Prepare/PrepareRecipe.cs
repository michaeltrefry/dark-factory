using System.Globalization;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Gateway;
using YamlDotNet.RepresentationModel;

namespace DarkFactory.Orchestrator.Prepare;

/// <summary>How a downloaded artifact is laid out in the cache (<see cref="PrepareArtifact.Unpack"/>).</summary>
public enum ArtifactUnpack
{
    /// <summary>The downloaded file as it is.</summary>
    None,
    Zip,
    Tar,
    TarGz,
}

/// <summary>
/// One artifact a recipe declares (sc-26232): downloaded only by the harness, owner-side, from <see cref="Url"/> (https), at most
/// <see cref="MaxBytes"/> bytes, kept only when its SHA-256 is <see cref="Sha256"/>, then unpacked (<see cref="Unpack"/>, at most
/// <see cref="MaxUnpackedBytes"/>) into the read-only artifact cache. <c>${artifact:&lt;name&gt;}</c> in a step names its path there.
/// </summary>
public sealed record PrepareArtifact(string Name, Uri Url, string Sha256, long MaxBytes, ArtifactUnpack Unpack, long MaxUnpackedBytes);

/// <summary>
/// One step a recipe declares: <see cref="Argv"/> (no shell: the program and its arguments as written), run in
/// <see cref="WorkingDirectory"/> (relative to the repository root; "." is the root) with <see cref="Environment"/> added, bounded by
/// <see cref="Timeout"/> when set (always within the run's own budget). Placeholders (<see cref="PrepareRecipe.WorktreePlaceholder"/>,
/// <c>${artifact:&lt;name&gt;}</c>) are expanded when the step is run.
/// </summary>
public sealed record PrepareStep(string Name, IReadOnlyList<string> Argv, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, TimeSpan? Timeout);

/// <summary>A recipe the parser refused, saying why (the gate's check is then an <c>error</c>; a worker's <c>factory-prepare</c> prints it).</summary>
public sealed class PrepareRecipeException(string message) : Exception(message);

/// <summary>
/// A repository's prepare recipe (sc-26232): <see cref="Path"/> at the repository root, a build recipe like <c>Package.swift</c> or
/// <c>build.rs</c> — reviewed (the path is on the security-review floor, <c>RiskyPaths</c>) and merged by the factory like any code.
/// It declares the artifacts a checkout needs (fetched by the harness alone, hash-pinned, into a cache no sandbox user can write) and
/// the steps that prepare a checkout (run as the sandbox user, before the gate's build of that commit and by a worker's
/// <c>factory-prepare</c>). Each commit is prepared by its own recipe. A repository without the file is prepared as before: nothing.
/// The schema is strict and versioned (<see cref="Parse"/>): an unknown key, a missing one, or a value off its rules refuses the
/// whole recipe.
/// </summary>
public sealed record PrepareRecipe(IReadOnlyList<PrepareArtifact> Artifacts, IReadOnlyList<PrepareStep> Steps)
{
    /// <summary>The recipe's path in a repository (not under the sealed <c>factory/</c>: a repository's own build recipe).</summary>
    public const string Path = ".factory/prepare.yaml";

    /// <summary>The directory holding it (checked not to be a symbolic link before the file is read in a worktree).</summary>
    public const string Directory = ".factory";

    public const int Version = 1;

    /// <summary>The worktree being prepared, as an absolute path.</summary>
    public const string WorktreePlaceholder = "${worktree}";

    public const int MaxArtifacts = 16;
    public const int MaxSteps = 32;
    public const int MaxArgs = 256;
    public const int MaxEnvironment = 64;
    public const int MaxValueChars = 4096;

    /// <summary>The largest artifact a recipe may declare (its <c>max_bytes</c>).</summary>
    public const long MaxArtifactBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The most an archive may unpack to (its <c>max_unpacked_bytes</c>; default 4 × <c>max_bytes</c>, at most this).</summary>
    public const long MaxUnpackedLimit = 16L * 1024 * 1024 * 1024;

    public const int MaxTimeoutMinutes = 120;


    /// <summary>Variables a step may not set: the ones the launch helper owns, the loader's, the shells' and the router's.</summary>
    private static readonly HashSet<string> RefusedVariables = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "TMPDIR", "USER", "LOGNAME", "SHELL", "IFS", "ENV", "BASH_ENV", "PS4", "SHELLOPTS", "BASHOPTS",
        "MSBUILDDISABLENODEREUSE", "DOTNET_CLI_USE_MSBUILD_SERVER",
    };

    private static readonly string[] RefusedVariablePrefixes = ["DYLD_", "LD_", "ANTHROPIC_", "BASH_FUNC_"];

    private static readonly Regex NamePattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex VariablePattern = new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainProgram = new(@"^[A-Za-z0-9_+][A-Za-z0-9._+-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\$\{(?<body>[^}]*)\}", RegexOptions.CultureInvariant);

    /// <summary>Parses <paramref name="yaml"/>; throws <see cref="PrepareRecipeException"/> saying what is wrong.</summary>
    public static PrepareRecipe Parse(string yaml)
    {
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or ArgumentException)
        {
            throw Fail($"is not valid YAML: {ex.Message}");
        }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw Fail("must be one YAML mapping");
        }
        Keys(root, ["version", "artifacts", "steps"], ["version", "steps"], "the top level");
        if (Scalar(root, "version", "the top level") != Version.ToString(CultureInfo.InvariantCulture))
        {
            throw Fail($"version must be {Version}");
        }

        var artifacts = new List<PrepareArtifact>();
        if (Child(root, "artifacts") is { } artifactsNode)
        {
            var list = Sequence(artifactsNode, "artifacts", 0, MaxArtifacts);
            for (var i = 0; i < list.Count; i++)
            {
                var artifact = ParseArtifact(list[i], $"artifacts[{i}]");
                if (artifacts.Any(a => a.Name == artifact.Name))
                {
                    throw Fail($"artifacts[{i}]: the name '{artifact.Name}' is declared twice");
                }
                artifacts.Add(artifact);
            }
        }

        var names = artifacts.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var steps = new List<PrepareStep>();
        var stepList = Sequence(Child(root, "steps")!, "steps", 1, MaxSteps);
        for (var i = 0; i < stepList.Count; i++)
        {
            var step = ParseStep(stepList[i], $"steps[{i}]", names);
            if (steps.Any(s => s.Name == step.Name))
            {
                throw Fail($"steps[{i}]: the name '{step.Name}' is used twice");
            }
            steps.Add(step);
        }
        return new PrepareRecipe(artifacts, steps);
    }

    private static PrepareArtifact ParseArtifact(YamlNode node, string where)
    {
        var map = node as YamlMappingNode ?? throw Fail($"{where} must be a mapping");
        Keys(map, ["name", "url", "sha256", "max_bytes", "unpack", "max_unpacked_bytes"], ["name", "url", "sha256", "max_bytes"], where);
        var name = Name(Scalar(map, "name", where), $"{where}.name");
        var urlText = Scalar(map, "url", where);
        if (urlText.Length > 2048 || !Uri.TryCreate(urlText, UriKind.Absolute, out var url))
        {
            throw Fail($"{where}.url must be an absolute URL");
        }
        if (ArtifactUrls.Problem(url) is { } problem)
        {
            throw Fail($"{where}.url {problem}");
        }
        var sha = Scalar(map, "sha256", where);
        if (!Hex64.IsMatch(sha))
        {
            throw Fail($"{where}.sha256 must be 64 hexadecimal characters (the artifact's SHA-256)");
        }
        var maxBytes = Long(map, "max_bytes", where, 1, MaxArtifactBytes);
        var unpack = Child(map, "unpack") is null ? ArtifactUnpack.None : Scalar(map, "unpack", where) switch
        {
            "none" => ArtifactUnpack.None,
            "zip" => ArtifactUnpack.Zip,
            "tar" => ArtifactUnpack.Tar,
            "tar.gz" => ArtifactUnpack.TarGz,
            var other => throw Fail($"{where}.unpack must be none, zip, tar or tar.gz, not '{other}'"),
        };
        long maxUnpacked;
        if (Child(map, "max_unpacked_bytes") is not null)
        {
            if (unpack == ArtifactUnpack.None)
            {
                throw Fail($"{where}.max_unpacked_bytes is only for an artifact that is unpacked");
            }
            maxUnpacked = Long(map, "max_unpacked_bytes", where, 1, MaxUnpackedLimit);
        }
        else
        {
            maxUnpacked = unpack == ArtifactUnpack.None ? maxBytes : Math.Min(MaxUnpackedLimit, maxBytes * 4);
        }
        return new PrepareArtifact(name, url, sha.ToLowerInvariant(), maxBytes, unpack, maxUnpacked);
    }

    private static PrepareStep ParseStep(YamlNode node, string where, IReadOnlySet<string> artifacts)
    {
        var map = node as YamlMappingNode ?? throw Fail($"{where} must be a mapping");
        Keys(map, ["name", "argv", "cwd", "env", "timeout_minutes"], ["name", "argv"], where);
        var name = Name(Scalar(map, "name", where), $"{where}.name");
        var argvNodes = Sequence(Child(map, "argv")!, $"{where}.argv", 1, MaxArgs);
        var argv = new List<string>();
        for (var i = 0; i < argvNodes.Count; i++)
        {
            var value = argvNodes[i] is YamlScalarNode { Value: { } v } ? v : throw Fail($"{where}.argv[{i}] must be a plain value");
            argv.Add(Value(value, $"{where}.argv[{i}]", artifacts));
        }
        ProgramProblem(argv[0], $"{where}.argv[0]");

        var cwd = Child(map, "cwd") is null ? "." : Scalar(map, "cwd", where);
        if (RelativeDirectory(cwd) is null)
        {
            throw Fail($"{where}.cwd must be a relative directory inside the repository with no '..', not '{cwd}'");
        }

        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Child(map, "env") is { } envNode)
        {
            var envMap = envNode as YamlMappingNode ?? throw Fail($"{where}.env must be a mapping of variable names to values");
            if (envMap.Children.Count > MaxEnvironment)
            {
                throw Fail($"{where}.env sets more than {MaxEnvironment} variables");
            }
            foreach (var (key, value) in envMap.Children)
            {
                var variable = key is YamlScalarNode { Value: { } k } ? k : throw Fail($"{where}.env has a non-scalar key");
                if (!VariablePattern.IsMatch(variable))
                {
                    throw Fail($"{where}.env: '{variable}' is not a variable name");
                }
                if (RefusedVariables.Contains(variable) || RefusedVariablePrefixes.Any(p => variable.StartsWith(p, StringComparison.Ordinal)))
                {
                    throw Fail($"{where}.env may not set {variable} (the launch helper, the loader, the shell or the router owns it)");
                }
                var text = value is YamlScalarNode { Value: { } t } ? t : throw Fail($"{where}.env.{variable} must be a plain value");
                environment[variable] = Value(text, $"{where}.env.{variable}", artifacts);
            }
        }

        TimeSpan? timeout = Child(map, "timeout_minutes") is null ? null : TimeSpan.FromMinutes(Long(map, "timeout_minutes", where, 1, MaxTimeoutMinutes));
        return new PrepareStep(name, argv, RelativeDirectory(cwd)!, environment, timeout);
    }

    /// <summary>
    /// Why <paramref name="program"/> is not a program a step may run, as an exception: it must be an absolute path with no
    /// <c>..</c>, a placeholder followed by such a path (<c>${artifact:sdk}/bin/tool</c>, <c>${worktree}/tools/gen</c>), or a plain
    /// name resolved on the launch helper's fixed PATH. Which program it is, is not judged: a step may run any program (a shell, an
    /// interpreter, a build tool) and so reach the network as the sandbox user, as <c>cargo build</c> or <c>swift build</c> can — no
    /// list of programs could stop that, so none is kept (CLAUDE.md's residual). An artifact is declared so that the harness fetches
    /// it hash-pinned; a step that downloads anything itself is the recipe's own, reviewed code.
    /// </summary>
    private static void ProgramProblem(string program, string where)
    {
        var placeholderLed = program.StartsWith("${", StringComparison.Ordinal);
        if (!placeholderLed && !program.StartsWith('/') && !PlainProgram.IsMatch(program))
        {
            throw Fail($"{where} must be an absolute path, a ${{artifact:…}}/${{worktree}} path or a plain program name, not '{program}'");
        }
        if (placeholderLed && Placeholder.Match(program) is not { Success: true, Index: 0 } lead)
        {
            throw Fail($"{where} must start with a placeholder followed by a path");
        }
        if (program.Contains('=', StringComparison.Ordinal) || program.Split('/').Any(s => s is ".." or "."))
        {
            throw Fail($"{where} may hold no '=' and no '.' or '..' path segment: '{program}'");
        }
    }

    /// <summary>A value with its placeholders checked (each one known; the artifact named declared): the value as written.</summary>
    private static string Value(string value, string where, IReadOnlySet<string> artifacts)
    {
        if (value.Length > MaxValueChars || value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
        {
            throw Fail($"{where} must be one line of at most {MaxValueChars} characters");
        }
        foreach (Match m in Placeholder.Matches(value))
        {
            var body = m.Groups["body"].Value;
            if (body == "worktree")
            {
                continue;
            }
            if (!body.StartsWith("artifact:", StringComparison.Ordinal))
            {
                throw Fail($"{where} has an unknown placeholder ${{{body}}} (known: ${{worktree}}, ${{artifact:<name>}})");
            }
            if (!artifacts.Contains(body["artifact:".Length..]))
            {
                throw Fail($"{where} names an artifact the recipe does not declare: ${{{body}}}");
            }
        }
        var rest = Placeholder.Replace(value, "");
        if (rest.Contains("${", StringComparison.Ordinal))
        {
            throw Fail($"{where} has an unterminated placeholder");
        }
        return value;
    }

    /// <summary>
    /// Expands <paramref name="value"/>'s placeholders: <see cref="WorktreePlaceholder"/> to <paramref name="worktree"/> and each
    /// <c>${artifact:&lt;name&gt;}</c> to <paramref name="artifacts"/>[name] (paths in the read-only artifact cache).
    /// </summary>
    public static string Expand(string value, string worktree, IReadOnlyDictionary<string, string> artifacts) =>
        Placeholder.Replace(value, m => m.Groups["body"].Value is var body && body == "worktree" ? worktree
            : artifacts.TryGetValue(body["artifact:".Length..], out var path) ? path
            : throw new PrepareRecipeException($"{Path}: no cached path for ${{{body}}}"));

    /// <summary>
    /// <paramref name="cwd"/> as a normalised relative directory ('/'-separated, "." the root), or null when it is absolute,
    /// leaves the repository or names a <c>.</c>/<c>..</c> segment other than a lone ".".
    /// </summary>
    public static string? RelativeDirectory(string cwd)
    {
        if (cwd is "." or "./" or "")
        {
            return ".";
        }
        if (cwd.Length > 512 || cwd.StartsWith('/') || cwd.Contains('\\') || cwd.Contains('\0') || cwd.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }
        var segments = cwd.TrimEnd('/').Split('/');
        return segments.Any(s => s is "" or "." or "..") ? null : string.Join('/', segments);
    }

    private static string Name(string value, string where) => NamePattern.IsMatch(value)
        ? value
        : throw Fail($"{where} must be 1-64 characters of a-z, 0-9, '.', '_' or '-' (starting with a letter or digit), not '{value}'");

    private static PrepareRecipeException Fail(string why) => new($"{Path}: {why.TrimEnd('.')}.");

    private static void Keys(YamlMappingNode node, IReadOnlyList<string> allowed, IReadOnlyList<string> required, string where)
    {
        var keys = node.Children.Keys
            .Select(k => k is YamlScalarNode { Value: { } v } ? v : throw Fail($"{where} has a non-scalar key"))
            .ToList();
        if (keys.FirstOrDefault(k => !allowed.Contains(k, StringComparer.Ordinal)) is { } unknown)
        {
            throw Fail($"has an unknown key '{unknown}' in {where} (allowed: {string.Join(", ", allowed)})");
        }
        if (required.FirstOrDefault(k => !keys.Contains(k, StringComparer.Ordinal)) is { } missing)
        {
            throw Fail($"is missing '{missing}' in {where}");
        }
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static string Scalar(YamlMappingNode node, string key, string where) => Child(node, key) switch
    {
        YamlScalarNode { Value: { } v } => v.Trim(),
        null => throw Fail($"is missing '{key}' in {where}"),
        _ => throw Fail($"{where}.{key} must be a plain value"),
    };

    private static long Long(YamlMappingNode node, string key, string where, long min, long max) =>
        long.TryParse(Scalar(node, key, where), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw Fail($"{where}.{key} must be a whole number from {min} to {max}");

    private static IList<YamlNode> Sequence(YamlNode node, string where, int min, int max) =>
        node is YamlSequenceNode sequence && sequence.Children.Count >= min && sequence.Children.Count <= max
            ? sequence.Children
            : throw Fail($"{where} must be a list of {min} to {max} entries");
}
