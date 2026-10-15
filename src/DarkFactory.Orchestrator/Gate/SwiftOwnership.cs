namespace DarkFactory.Orchestrator.Gate;

/// <summary>Which strategy judges a changed <c>.swift</c> file (sc-26134).</summary>
public enum SwiftOwner
{
    /// <summary><see cref="SwiftNewTests"/>: a file of a SwiftPM package that no Xcode project's test target compiles.</summary>
    SwiftPM,

    /// <summary><see cref="XcodeNewTests"/>: a file in no SwiftPM package, or a file of a package outside its test targets that an Xcode project's test target compiles.</summary>
    Xcode,

    /// <summary>
    /// Neither can run its tests alone: it is in a SwiftPM test target's directory AND an Xcode project's test target compiles
    /// it (or whether one does cannot be told). <see cref="SwiftNewTests"/> names it unsupported; <see cref="XcodeNewTests"/> skips it.
    /// </summary>
    Both,
}

/// <summary>
/// The one rule that splits a commit's <c>.swift</c> files between <see cref="SwiftNewTests"/> and <see cref="XcodeNewTests"/>
/// (sc-26134), so a repository with a <c>Package.swift</c> AND an Xcode project under it has each file judged once:
/// <list type="bullet">
/// <item>In no SwiftPM package (no <c>Package.swift</c> above it): <see cref="SwiftOwner.Xcode"/>.</item>
/// <item>In a package's test targets — its <c>Tests/</c> directory (SwiftPM's default layout) or a test target's declared
/// <c>path:</c> — and compiled by no Xcode test target: <see cref="SwiftOwner.SwiftPM"/>; compiled by one, or not known:
/// <see cref="SwiftOwner.Both"/>.</item>
/// <item>In a package outside its test targets (an Xcode project's test folder beside a root <c>Package.swift</c>, or the
/// package's own sources): <see cref="SwiftOwner.Xcode"/> when an Xcode project's unit-test or UI-test target compiles it, or
/// whether one does cannot be told (Xcode then fails closed on it); otherwise <see cref="SwiftOwner.SwiftPM"/>, which names a
/// test there unsupported as before.</item>
/// </list>
/// Membership is read as <see cref="XcodeNewTests"/> reads it (<see cref="XcodeProject.Membership"/> over every
/// <c>*.xcodeproj/project.pbxproj</c> of the commit; a project that cannot be read makes it not known).
/// </summary>
public sealed class SwiftOwnership(Func<CancellationToken, Task<IReadOnlySet<string>>> files, Func<string, CancellationToken, Task<string?>> read)
{
    private List<(string Path, XcodeProject? Project, string? Problem)>? _projects;
    private readonly Dictionary<string, (IReadOnlyList<string> Directories, string? Problem)> _manifests = new(StringComparer.Ordinal);

    /// <summary>The nearest directory above <paramref name="path"/> holding a <c>Package.swift</c> ("" for the root), or null.</summary>
    public static string? PackageOf(IReadOnlySet<string> files, string path)
    {
        for (var directory = Parent(path); ; directory = Parent(directory))
        {
            if (files.Contains(directory.Length == 0 ? "Package.swift" : $"{directory}/Package.swift"))
            {
                return directory;
            }
            if (directory.Length == 0)
            {
                return null;
            }
        }
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    /// <summary>
    /// The directories of a package's test targets, relative to the package: <c>Tests</c> (the default layout) and each
    /// <c>testTarget(…, path: "…")</c>'s declared path. A manifest that cannot be read gives <c>Tests</c> alone.
    /// </summary>
    private async Task<IReadOnlyList<string>> TestDirectoriesAsync(string package, CancellationToken ct)
    {
        if (!_manifests.TryGetValue(package, out var known))
        {
            var directories = new List<string> { "Tests" };
            string? problem = null;
            try
            {
                var manifest = await read(package.Length == 0 ? "Package.swift" : $"{package}/Package.swift", ct) ?? "";
                directories.AddRange(SwiftSource.TestTargetPaths(manifest).Select(t => t.Path).OfType<string>()
                    .Select(p => p.Trim('/')).Where(p => p.Length > 0 && !p.Split('/').Any(s => s is "." or "..")));
            }
            catch (FormatException e)
            {
                problem = e.Message;
            }
            _manifests[package] = known = (directories, problem);
        }
        return known.Directories;
    }

    /// <summary>Whether <paramref name="path"/> is in a test target's directory of its SwiftPM package (<see cref="TestDirectoriesAsync"/>).</summary>
    public async Task<bool> InPackageTestsAsync(string path, CancellationToken ct)
    {
        if (PackageOf(await files(ct), path) is not { } package)
        {
            return false;
        }
        var relative = package.Length == 0 ? path : path[(package.Length + 1)..];
        return (await TestDirectoriesAsync(package, ct)).Any(d => relative.StartsWith(d + "/", StringComparison.Ordinal));
    }

    /// <summary>The Xcode test targets that compile <paramref name="path"/> (<c>Target of Project</c>), and why membership cannot be told where it cannot.</summary>
    public async Task<(List<string> Tests, List<string> Unknown)> XcodeTestTargetsAsync(string path, CancellationToken ct)
    {
        if (_projects is null)
        {
            _projects = [];
            foreach (var pbxproj in (await files(ct)).Where(f => f.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var project = pbxproj[..^"/project.pbxproj".Length];
                try
                {
                    _projects.Add((project, XcodeProject.Parse(project, await read(pbxproj, ct) ?? throw new FormatException("it cannot be read")), null));
                }
                catch (FormatException e)
                {
                    _projects.Add((project, null, $"{pbxproj} cannot be read ({e.Message})"));
                }
            }
        }
        var (tests, unknown) = (new List<string>(), new List<string>());
        foreach (var (projectPath, project, problem) in _projects)
        {
            if (project is null)
            {
                unknown.Add(problem!);
                continue;
            }
            foreach (var target in project.Targets.Where(t => t.IsTestBundle))
            {
                XcodeMembership membership;
                try
                {
                    membership = project.Membership(target, path);
                }
                catch (FormatException e)
                {
                    membership = XcodeMembership.NotKnown(e.Message);
                }
                if (membership.Unknown is { } why)
                {
                    unknown.Add($"{projectPath}: {why}");
                }
                else if (membership.Member)
                {
                    tests.Add($"{target.Name} of {projectPath}");
                }
            }
        }
        return (tests, unknown);
    }

    /// <summary>Which strategy judges <paramref name="path"/>, and why it is <see cref="SwiftOwner.Both"/> when it is.</summary>
    public async Task<(SwiftOwner Owner, string? Why)> OfAsync(string path, CancellationToken ct)
    {
        if (PackageOf(await files(ct), path) is null)
        {
            return (SwiftOwner.Xcode, null);
        }
        var inTests = await InPackageTestsAsync(path, ct);
        var (tests, unknown) = await XcodeTestTargetsAsync(path, ct);
        if (inTests)
        {
            return tests.Count > 0
                ? (SwiftOwner.Both, $"it is in a SwiftPM test target's directory and the Xcode test target {string.Join(", ", tests)} compiles it too, so which of them runs its tests cannot be told")
                : unknown.Count > 0
                ? (SwiftOwner.Both, $"it is in a SwiftPM test target's directory, and whether an Xcode test target compiles it too cannot be told: {string.Join("; ", unknown.Distinct().Take(5))}")
                : (SwiftOwner.SwiftPM, null);
        }
        return tests.Count > 0 || unknown.Count > 0 ? (SwiftOwner.Xcode, null) : (SwiftOwner.SwiftPM, null);
    }
}
