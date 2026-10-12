using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// What <see cref="CargoNewTests"/> found, beyond <see cref="NewTestSet"/>: where each new test's function is
/// (<see cref="Locations"/>: its file and its path inside that file, e.g. <c>tests::adds</c>) and which applied files are
/// test code as a whole (<see cref="WholeFiles"/>: integration tests and <c>#[cfg(test)]</c> module files; every other
/// applied <c>.rs</c> file is a production file with test code grafted in).
/// </summary>
public sealed record CargoNewTestSet(IReadOnlyList<ChangedFile> TestFiles, IReadOnlyList<string> Tests, IReadOnlyList<string> Unisolable)
    : NewTestSet(TestFiles, Tests, Unisolable)
{
    public IReadOnlyDictionary<string, (string Path, string InFile)> Locations { get; init; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public IReadOnlySet<string> WholeFiles { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Rust/cargo (sc-25875), beside <see cref="XunitNewTests"/> and at least as strict. Nothing is compiled to find tests:
/// <see cref="RustSource"/> reads each changed <c>.rs</c> file's items from its tokens.
/// <para>
/// New tests: the functions with a test attribute (<see cref="RustItem.IsTestFn"/>: <c>#[test]</c> under any path such
/// as <c>#[tokio::test]</c>, <c>#[rstest]</c>, <c>#[test_case(…)]</c>, …) in the head versions of the changed files of a
/// cargo package (the nearest <c>Cargo.toml</c> with a <c>[package]</c>), in <c>tests/</c> (integration tests) or
/// <c>src/</c> (inline, usually in a <c>#[cfg(test)]</c> module), whose id no base version of a changed file has. The id
/// is <c>package:target:path</c>, the path as libtest names the test: <c>words:lib:count::tests::blank</c> (a unit test
/// of the library: the modules of its file, <c>src/count.rs</c>, then its inline modules), <c>tool:bins:tests::runs</c>
/// (of a binary), <c>words:test/api:deep::nested</c> (of the integration test <c>tests/api.rs</c>). A function with
/// cases (<c>#[test_case]</c>, rstest's <c>#[case]</c>/<c>#[values]</c>) is one test, <c>…::name::*</c>: every case under
/// it. A test of a renamed file that the old file had, and one moved unchanged from where the PR removes it, is not new.
/// New cases on an existing parameterized test are unisolable, as new rows on an xUnit theory are.
/// </para>
/// <para>
/// The base run's overlay (the PR's test code applied to the base). Integration tests and files of a
/// <c>#[cfg(test)]</c> module are test code as a whole and come from the head. A production file with inline tests
/// cannot: it is written (through owner-side git, as the retry's files are) as its <em>base</em> version with the head's
/// test code grafted in (<see cref="Graft"/>) — every <c>#[cfg(test)]</c> module replaced by the head's (or added,
/// removed when the head has none), and every test function outside one replaced or added — so the production code under
/// test stays the base's. A file the base lacks (added or renamed) holding new tests is written with its test code only
/// (production items left out, an empty <c>fn main</c> kept for a binary) and its <c>mod</c> declaration added to the
/// base's parent module, so a test of new code is compiled on the base and fails to there, in its own declaration. A
/// package manifest's <c>dev-dependencies</c> tables are test code too: the base's manifest gets the head's (a new
/// package's manifest is applied whole).
/// </para>
/// <para>
/// Unisolable (the check is <c>unsupported</c>): a changed <c>.rs</c> file the reader cannot read; a new test outside a
/// package's <c>src/</c> and <c>tests/</c>, or in a module shared by integration tests (<c>tests/common/mod.rs</c>); a
/// package with a custom target layout (<c>[[test]]</c>, <c>autotests</c>/<c>autobins</c>, a <c>path</c> that is not the
/// default); a <c>#[cfg(test)]</c> item that is not a module, or a test inside a macro, outside every
/// <c>#[cfg(test)]</c> module, that the base version lacks (it cannot be told from the production code beside it);
/// <c>dev-dependencies</c> written as dotted keys; a new file whose <c>mod</c> declaration is not at the top of its
/// parent module's file.
/// </para>
/// <para>
/// Runs, from the worktree root, per package holding a new test: <c>cargo --version</c> (a toolchain probe: failing,
/// the run is <see cref="TestRunStatus.ToolchainMissing"/>, never a pass), <c>cargo fetch</c> (the restore: the only
/// step that is meant to use the network, as <c>dotnet restore</c> is), <c>cargo test --no-run --message-format=json</c>
/// for the targets holding new tests (the build: cargo's JSON diagnostics, a stable format, on standard output), then
/// one <c>cargo test … -- --exact &lt;path&gt;</c> per new test (<c>--lib</c>, <c>--bins</c> or <c>--test &lt;name&gt;</c>;
/// a parameterized test by its <c>path::</c> prefix). Results: libtest's JSON output is nightly-only, so the stable
/// human-readable lines are read (<c>test &lt;name&gt; ... ok|FAILED|ignored</c> and <c>test result: …</c>), and because
/// a test can print such lines itself they are believed only when they agree with what it cannot forge
/// (<see cref="ParseResults"/>: the process's exit code and the summary's counts); anything else is an unreadable
/// report, an <c>error</c>.
/// </para>
/// </summary>
public sealed class CargoNewTests : INewTestStrategy
{
    public string Name => "rust-cargo";

    /// <summary>The program every step runs, found on the test runs' PATH (the launch helper's fixed one when sandboxed).</summary>
    public const string Cargo = "cargo";

    private const string Manifest = "Cargo.toml";

    /// <summary>The suffix of a parameterized test's id: every case libtest names under the function.</summary>
    public const string Cases = "::*";

    public bool Understands(string path) => path.EndsWith(".rs", StringComparison.Ordinal);

    /// <summary>Nothing: cargo's build output is kept from standard output (<see cref="TestStep.CaptureOutput"/>), no log file is read.</summary>
    public string BuildLogPattern => "cargo-build-*.none";

    private sealed record Package(string Directory, string Name, string ManifestPath, string ManifestText);

    /// <summary>Where a file's tests are: the package, cargo's target (<c>lib</c>, <c>bins</c>, <c>test/name</c>) and the modules the file is.</summary>
    private sealed record Location(Package Package, string? Target, IReadOnlyList<string> Modules, string? Problem)
    {
        public string Id(string inFile, bool cases) => $"{Package.Name}:{Target}:{string.Join("::", Modules.Append(inFile))}{(cases ? Cases : "")}";
    }

    /// <summary>One commit's files, read once each.</summary>
    private sealed class Tree(TestSource source, string sha)
    {
        private HashSet<string>? _files;
        private readonly Dictionary<string, string?> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RustFile?> _parsed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Package?> _packages = new(StringComparer.Ordinal);

        /// <summary>Why each file that could not be read as Rust source could not.</summary>
        public Dictionary<string, string> Unreadable { get; } = new(StringComparer.Ordinal);

        public async Task<bool> HasAsync(string path, CancellationToken ct) =>
            (_files ??= (await source.FilesAsync(sha, ct)).ToHashSet(StringComparer.Ordinal)).Contains(path);

        public async Task<string?> ReadAsync(string path, CancellationToken ct)
        {
            if (!_texts.TryGetValue(path, out var text))
            {
                _texts[path] = text = await HasAsync(path, ct) ? await source.ReadAsync(sha, path, ct) : null;
            }
            return text;
        }

        public async Task<RustFile?> ParseAsync(string path, CancellationToken ct)
        {
            if (!_parsed.TryGetValue(path, out var file))
            {
                file = null;
                if (await ReadAsync(path, ct) is { } text)
                {
                    try
                    {
                        file = RustSource.Parse(text);
                    }
                    catch (FormatException e)
                    {
                        Unreadable[path] = e.Message;
                    }
                }
                _parsed[path] = file;
            }
            return file;
        }

        /// <summary>The package <paramref name="path"/> belongs to: the nearest directory above it whose <c>Cargo.toml</c> has a <c>[package]</c> with a name.</summary>
        public async Task<Package?> PackageAsync(string path, CancellationToken ct)
        {
            for (var directory = Parent(path); ; directory = Parent(directory))
            {
                if (!_packages.TryGetValue(directory, out var package))
                {
                    var manifest = Join(directory, Manifest);
                    package = await ReadAsync(manifest, ct) is { } text && PackageName(text) is { } name ? new Package(directory, name, manifest, text) : null;
                    _packages[directory] = package;
                }
                if (package is not null || directory.Length == 0)
                {
                    return package;
                }
            }
        }
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and >= 0 ? path[..slash] : "";

    private static string Join(string directory, string relative) => directory.Length == 0 ? relative : $"{directory}/{relative}";

    private static string? Relative(string directory, string path) =>
        directory.Length == 0 ? path : path.StartsWith(directory + "/", StringComparison.Ordinal) ? path[(directory.Length + 1)..] : null;

    private static string Stem(string file) => file.EndsWith(".rs", StringComparison.Ordinal) ? file[..^3] : file;

    // ---- Cargo.toml, as far as the gate reads it -------------------------------------------------------------------------

    private static readonly Regex TableHeader = new(@"^\s*(\[\[?)\s*(?<name>[^\]]+?)\s*\]\]?\s*(#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyValue = new(@"^\s*(?<key>[A-Za-z0-9_.\-""']+)\s*=\s*(?<value>.*?)\s*(#[^""']*)?$", RegexOptions.CultureInvariant);

    /// <summary>A manifest's tables in order: the header (unquoted, no spaces; empty for the top), whether it is an array table, its lines and its plain keys.</summary>
    private static List<(string Header, bool Array, List<string> Lines, Dictionary<string, string> Keys)> Tables(string manifest)
    {
        var tables = new List<(string, bool, List<string>, Dictionary<string, string>)> { ("", false, [], new(StringComparer.Ordinal)) };
        foreach (var line in manifest.Split('\n'))
        {
            if (TableHeader.Match(line.TrimEnd('\r')) is { Success: true } header)
            {
                tables.Add((header.Groups["name"].Value.Replace("\"", "").Replace("'", "").Replace(" ", ""), header.Groups[1].Value == "[[", [], new(StringComparer.Ordinal)));
            }
            else if (KeyValue.Match(line.TrimEnd('\r')) is { Success: true } pair)
            {
                tables[^1].Item4[pair.Groups["key"].Value.Trim('"', '\'')] = pair.Groups["value"].Value.Trim().Trim('"', '\'');
            }
            tables[^1].Item3.Add(line);
        }
        return tables;
    }

    /// <summary>The <c>[package]</c> table's <c>name</c>, or null when the manifest is no package's (a virtual workspace).</summary>
    public static string? PackageName(string manifest) =>
        Tables(manifest).Where(t => t.Header == "package").Select(t => t.Keys.GetValueOrDefault("name")).FirstOrDefault(n => n is { Length: > 0 } && !n.Contains(':'));

    private static bool IsDevDependencies(string header) =>
        Regex.IsMatch(header, @"^(target\..+\.)?dev[-_]dependencies(\..+)?$", RegexOptions.CultureInvariant);

    private static readonly Regex DottedDevDependencies = new(@"^\s*(target\..+\.)?dev[-_]dependencies\s*[.=]", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The manifest's <c>dev-dependencies</c> tables (also a target's, and their sub-tables), as written.</summary>
    public static string DevDependencies(string manifest) =>
        string.Join('\n', Tables(manifest).Where(t => IsDevDependencies(t.Header)).Select(t => string.Join('\n', t.Lines).TrimEnd()));

    /// <summary><paramref name="baseManifest"/> with its <c>dev-dependencies</c> tables replaced by <paramref name="headManifest"/>'s.</summary>
    public static string WithDevDependencies(string baseManifest, string headManifest)
    {
        var kept = string.Join('\n', Tables(baseManifest).Where(t => !IsDevDependencies(t.Header)).SelectMany(t => t.Lines)).TrimEnd();
        var dev = DevDependencies(headManifest);
        return dev.Length == 0 ? kept + "\n" : $"{kept}\n\n{dev}\n";
    }

    /// <summary>Why a package's targets are not where the gate assumes them (so its test names cannot be derived from paths), if they are not.</summary>
    private static string? CustomLayout(Package package, string target)
    {
        foreach (var (header, _, _, keys) in Tables(package.ManifestText))
        {
            var path = keys.GetValueOrDefault("path");
            switch (header)
            {
                case "package" when keys.Keys.FirstOrDefault(k => k is "autotests" or "autobins" or "autolib") is { } auto:
                    return $"{package.ManifestPath} sets {auto}";
                case "test" when target.StartsWith("test/", StringComparison.Ordinal):
                    return $"{package.ManifestPath} declares its integration tests itself ([[test]])";
                case "lib" when target == "lib" && path is not (null or "src/lib.rs"):
                    return $"{package.ManifestPath} puts its library at {path}";
                case "bin" when target == "bins" && path is not null && !Regex.IsMatch(path, @"^src/(main\.rs|bin/[^/]+\.rs|bin/[^/]+/main\.rs)$", RegexOptions.CultureInvariant):
                    return $"{package.ManifestPath} puts a binary at {path}";
            }
        }
        return null;
    }

    // ---- Where a file's modules and tests are -----------------------------------------------------------------------------

    /// <summary>The files that may declare the module a <c>src/</c> file is, and that module's name; null for a crate root.</summary>
    private static (IReadOnlyList<string> Files, string Name)? Declaring(Package package, string path)
    {
        var source = Join(package.Directory, "src");
        var relative = Relative(source, path)!;
        if (relative is "lib.rs" or "main.rs" || Regex.IsMatch(relative, @"^bin/([^/]+\.rs|[^/]+/main\.rs)$", RegexOptions.CultureInvariant))
        {
            return null;
        }
        // The module's own directory entry: src/a/b.rs and src/a/b/mod.rs are both module b of src/a.
        var entry = relative.EndsWith("/mod.rs", StringComparison.Ordinal) ? Parent(path) : Stem(path);
        var (name, parent) = (entry[(entry.LastIndexOf('/') + 1)..], Parent(entry));
        if (parent == source)
        {
            return ([Join(source, "lib.rs"), Join(source, "main.rs")], name);
        }
        return Parent(parent) == Join(source, "bin") ? ([Join(parent, "main.rs")], name) : ([parent + ".rs", Join(parent, "mod.rs")], name);
    }

    private static async Task<RustItem?> DeclarationAsync(Tree tree, string file, string name, CancellationToken ct) =>
        (await tree.ParseAsync(file, ct))?.Items.FirstOrDefault(i => i.Kind == RustItem.Mod && i.Name == name);

    /// <summary>Whether a <c>src/</c> file is test code as a whole: <c>#![cfg(test)]</c>, or it or a module above it is declared <c>#[cfg(test)] mod x;</c>.</summary>
    private static async Task<bool> GatedFileAsync(Tree tree, Package package, string path, CancellationToken ct)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            if (await tree.ParseAsync(path, ct) is { IsGated: true })
            {
                return true;
            }
            if (Declaring(package, path) is not var (files, name))
            {
                return false;
            }
            string? above = null;
            foreach (var file in files)
            {
                if (await DeclarationAsync(tree, file, name, ct) is { } declaration)
                {
                    if (declaration.IsGated)
                    {
                        return true;
                    }
                    above ??= file;
                }
            }
            if (above is null)
            {
                return false;
            }
            path = above;
        }
        return false;
    }

    private static async Task<Location?> LocateAsync(Tree tree, string path, CancellationToken ct)
    {
        if (await tree.PackageAsync(path, ct) is not { } package)
        {
            return null;
        }
        var parts = Stem(Relative(package.Directory, path)!).Split('/').ToList();
        List<string> Modules(IEnumerable<string> of)
        {
            var modules = of.ToList();
            if (modules.Count > 0 && modules[^1] == "mod")
            {
                modules.RemoveAt(modules.Count - 1);
            }
            return modules;
        }
        switch (parts[0])
        {
            case "tests" when parts.Count == 2:
                return new Location(package, $"test/{parts[1]}", [], null);
            case "tests" when parts.Count > 2 && await tree.HasAsync(Join(package.Directory, $"tests/{parts[1]}/main.rs"), ct):
                return new Location(package, $"test/{parts[1]}", parts.Count == 3 && parts[2] == "main" ? [] : Modules(parts.Skip(2)), null);
            case "tests":
                return new Location(package, null, [], "it is in a module shared by integration tests, which cargo runs as part of each of them");
            case "src" when parts.Count >= 3 && parts[1] == "bin":
                return new Location(package, "bins", parts.Count == 4 && parts[3] == "main" ? [] : Modules(parts.Skip(3)), null);
            case "src" when parts.Count == 2 && parts[1] is "lib" or "main":
                return new Location(package, parts[1] == "lib" ? "lib" : "bins", [], null);
            case "src" when parts.Count >= 2:
            {
                // A module of the library when the library declares it (or is all there is), else of the binary.
                var (library, binary) = (Join(package.Directory, "src/lib.rs"), Join(package.Directory, "src/main.rs"));
                var modules = Modules(parts.Skip(1));
                var first = modules.Count > 0 ? modules[0] : "";
                var target = await DeclarationAsync(tree, library, first, ct) is not null ? "lib"
                    : await DeclarationAsync(tree, binary, first, ct) is not null ? "bins"
                    : await tree.HasAsync(library, ct) ? "lib" : "bins";
                return new Location(package, target, modules, null);
            }
            default:
                return new Location(package, null, [], "it is outside the package's src/ and tests/ (cargo test does not run it by default)");
        }
    }

    // ---- Finding the new tests and building the base's overlay ------------------------------------------------------------

    public async Task<NewTestSet> IdentifyAsync(TestSource source, IReadOnlyList<ChangedFile> changes, CancellationToken ct)
    {
        if (!changes.Any(c => c.Paths.Any(Understands)))
        {
            return NewTestSet.None;
        }
        var (head, @base) = (new Tree(source, source.HeadSha), new Tree(source, source.BaseSha));
        var problems = new List<string>();

        // The base's tests of the changed files: by id where they are, and (a renamed file's) where the head has the file.
        var baseRows = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var baseBodies = new List<(string Id, string Name, string Body)>();
        foreach (var change in changes.Where(c => c.OldPath is { } old && Understands(old)))
        {
            if (await @base.ParseAsync(change.OldPath!, ct) is not { } file)
            {
                continue;
            }
            var places = new List<Location?> { await LocateAsync(@base, change.OldPath!, ct) };
            if (change.NewPath is { } renamed && renamed != change.OldPath && Understands(renamed))
            {
                places.Add(await LocateAsync(head, renamed, ct));
            }
            foreach (var (inFile, fn, _) in file.TestFns())
            {
                foreach (var place in places.Where(p => p?.Target is not null))
                {
                    var id = place!.Id(inFile, fn.CaseRows.Count > 0);
                    baseRows.TryAdd(id, fn.CaseRows);
                    baseBodies.Add((id, fn.Name!, RustSource.Normalize(file.TextOf(fn))));
                }
            }
        }

        // The head's tests of the changed files.
        var headTests = new Dictionary<string, (string Path, string InFile, RustItem Fn, string Body, Package Package, string Target)>(StringComparer.Ordinal);
        var unlocated = new List<(string Path, string InFile, string Why)>();
        foreach (var change in changes.Where(c => c.NewPath is { } path && Understands(path)))
        {
            var path = change.NewPath!;
            if (await head.ParseAsync(path, ct) is not { } file)
            {
                if (head.Unreadable.TryGetValue(path, out var why))
                {
                    problems.Add($"{path} cannot be read as Rust source ({why}), so the tests it may add cannot be found");
                }
                continue;
            }
            var tests = file.TestFns().ToList();
            if (tests.Count == 0)
            {
                continue;
            }
            var place = await LocateAsync(head, path, ct);
            // What the base version had, wherever the file was: such a test is not new even where it cannot be run.
            var before = change.OldPath is { } old && await @base.ParseAsync(old, ct) is { } was ? was.TestFns().Select(t => t.Path).ToHashSet(StringComparer.Ordinal) : [];
            foreach (var (inFile, fn, _) in tests)
            {
                if (place?.Target is null)
                {
                    if (!before.Contains(inFile))
                    {
                        unlocated.Add((path, inFile, place?.Problem ?? "it is in no cargo package (no Cargo.toml with a [package] above it)"));
                    }
                    continue;
                }
                headTests.TryAdd(place.Id(inFile, fn.CaseRows.Count > 0), (path, inFile, fn, RustSource.Normalize(file.TextOf(fn)), place.Package, place.Target));
            }
        }
        problems.AddRange(unlocated.Select(u => $"{u.Path} has the new test {u.InFile}, which the gate cannot run: {u.Why}"));

        // New: no base version of a changed file has the id, and it was not moved unchanged from where the PR removes it.
        var moved = baseBodies.Where(b => !headTests.ContainsKey(b.Id)).Select(b => (b.Name, b.Body)).ToHashSet();
        var added = headTests.Where(t => !baseRows.ContainsKey(t.Key) && !moved.Contains((t.Value.Fn.Name!, t.Value.Body))).Select(t => t.Key).ToList();
        foreach (var (id, test) in headTests)
        {
            if (baseRows.TryGetValue(id, out var rows) && test.Fn.CaseRows.Except(rows, StringComparer.Ordinal).Any())
            {
                problems.Add($"new cases on the existing parameterized test {id} cannot be run apart from its old cases (add them as a new test function)");
            }
        }
        foreach (var (package, target) in added.Select(id => (headTests[id].Package, headTests[id].Target)).Distinct())
        {
            if (CustomLayout(package, target) is { } custom)
            {
                problems.Add($"{custom}, so the gate cannot tell how cargo names the package's new tests");
            }
        }

        // The files that are test code as a whole, in any package: everything the PR changes under a package's tests/.
        var testFiles = new List<ChangedFile>();
        var whole = new HashSet<string>(StringComparer.Ordinal);
        async Task<bool> IntegrationAsync(Tree tree, string? path) =>
            path is not null && await tree.PackageAsync(path, ct) is { } package && Relative(package.Directory, path)!.StartsWith("tests/", StringComparison.Ordinal);
        foreach (var change in changes)
        {
            // Not another stack's test file there (tests/check.py): cargo does not run it, so it stays unclaimed (unsupported).
            if (change.Paths.Any(p => !Understands(p) && NewTestsCheck.LooksLikeTest(p)))
            {
                continue;
            }
            if (await IntegrationAsync(head, change.NewPath) || await IntegrationAsync(@base, change.OldPath))
            {
                testFiles.Add(change);
                if (change.NewPath is { } path)
                {
                    whole.Add(path);
                }
            }
        }
        if (added.Count == 0)
        {
            return new CargoNewTestSet(testFiles, [], problems);
        }

        var replacements = await OverlayAsync(head, @base, changes, added.Select(id => headTests[id].Package).Distinct().ToList(),
            added.Select(id => headTests[id].Path).ToHashSet(StringComparer.Ordinal), testFiles, whole, problems, ct);
        return new CargoNewTestSet(testFiles, added, problems)
        {
            Projects = added.ToDictionary(id => id, id => headTests[id].Package.ManifestPath, StringComparer.Ordinal),
            BaseReplacements = replacements,
            Locations = added.ToDictionary(id => id, id => (headTests[id].Path, headTests[id].InFile), StringComparer.Ordinal),
            WholeFiles = whole,
        };
    }

    /// <summary>
    /// The base run's overlay for the <paramref name="packages"/> holding new tests, beyond their integration tests: adds
    /// to <paramref name="testFiles"/> and <paramref name="whole"/>, and returns the files written with other content than
    /// the head's (grafted production files, files the base lacks with their test code only, manifests).
    /// </summary>
    private static async Task<Dictionary<string, string>> OverlayAsync(Tree head, Tree @base, IReadOnlyList<ChangedFile> changes, IReadOnlyList<Package> packages,
        IReadOnlySet<string> withNewTests, List<ChangedFile> testFiles, HashSet<string> whole, List<string> problems, CancellationToken ct)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        // The mod declarations a base file must get so that a file the base lacks is compiled there.
        var declarations = new Dictionary<string, List<(string Name, string Text)>>(StringComparer.Ordinal);
        var absent = new Dictionary<string, Package>(StringComparer.Ordinal);
        var grafted = new Dictionary<string, Package>(StringComparer.Ordinal);

        async Task EnsureAsync(Package package, string path)
        {
            if (!absent.TryAdd(path, package) || Declaring(package, path) is not var (files, name))
            {
                return;
            }
            var declared = false;
            foreach (var file in files)
            {
                if (await DeclarationAsync(head, file, name, ct) is not { } declaration || await head.ParseAsync(file, ct) is not { } parent)
                {
                    continue;
                }
                declared = true;
                if (!declaration.IsGated)
                {
                    // A #[cfg(test)] declaration is test code and grafted with the rest; any other is added as it is written.
                    (declarations.TryGetValue(file, out var list) ? list : declarations[file] = []).Add((name, parent.TextOf(declaration)));
                }
                if (await @base.HasAsync(file, ct))
                {
                    grafted.TryAdd(file, package);
                }
                else
                {
                    await EnsureAsync(package, file);
                }
            }
            if (!declared)
            {
                problems.Add($"{path} is new and the gate cannot find its mod declaration at the top of {string.Join(" or ", files)}, so its tests cannot be applied to the base");
            }
        }

        foreach (var package in packages)
        {
            var source = Join(package.Directory, "src") + "/";
            foreach (var change in changes.Where(c => c.NewPath is { } p && p.StartsWith(source, StringComparison.Ordinal) && p.EndsWith(".rs", StringComparison.Ordinal)))
            {
                var path = change.NewPath!;
                if (await head.ParseAsync(path, ct) is null)
                {
                    continue; // unreadable: already a problem
                }
                if (await GatedFileAsync(head, package, path, ct))
                {
                    testFiles.Add(change);
                    whole.Add(path);
                }
                else if (await @base.HasAsync(path, ct))
                {
                    grafted.TryAdd(path, package);
                }
                else if (withNewTests.Contains(path))
                {
                    await EnsureAsync(package, path);
                }
            }
            if (!await @base.HasAsync(package.ManifestPath, ct))
            {
                // A new package: its manifest whole, and its crate roots (with their test code only) so that it builds.
                testFiles.Add(new ChangedFile('A', null, package.ManifestPath));
                foreach (var root in new[] { source + "lib.rs", source + "main.rs" })
                {
                    if (await head.HasAsync(root, ct) && !await @base.HasAsync(root, ct) && !whole.Contains(root))
                    {
                        await EnsureAsync(package, root);
                    }
                }
            }
            else if (await @base.ReadAsync(package.ManifestPath, ct) is { } before && before != package.ManifestText)
            {
                if (DottedDevDependencies.IsMatch(before) || DottedDevDependencies.IsMatch(package.ManifestText))
                {
                    problems.Add($"{package.ManifestPath} writes dev-dependencies as dotted keys, which the gate cannot apply to the base apart from the other dependencies");
                }
                else if (DevDependencies(before) != DevDependencies(package.ManifestText))
                {
                    replacements[package.ManifestPath] = WithDevDependencies(before, package.ManifestText);
                    testFiles.Add(new ChangedFile('M', package.ManifestPath, package.ManifestPath));
                }
            }
        }

        foreach (var path in grafted.Keys.Concat(absent.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
        {
            if (await head.ParseAsync(path, ct) is not { } file)
            {
                continue;
            }
            var before = absent.ContainsKey(path) ? null : await @base.ReadAsync(path, ct);
            string text;
            try
            {
                text = Graft(before, file, declarations.GetValueOrDefault(path) ?? [], problems, path,
                    emptyMain: before is null && Regex.IsMatch(path, @"(^|/)src/(main\.rs|bin/[^/]+\.rs|bin/[^/]+/main\.rs)$", RegexOptions.CultureInvariant));
            }
            catch (FormatException e)
            {
                problems.Add($"{path} at the base cannot be read as Rust source ({e.Message}), so the PR's test code cannot be applied to it");
                continue;
            }
            if (text != before)
            {
                replacements[path] = text;
                testFiles.Add(before is null ? new ChangedFile('A', null, path) : new ChangedFile('M', path, path));
            }
        }
        return replacements;
    }

    /// <summary>
    /// A production file for the base run: <paramref name="baseText"/> with the test code of <paramref name="head"/>
    /// applied. In each module the two share (the file, then inline modules that are not <c>#[cfg(test)]</c>, by name),
    /// every <c>#[cfg(test)]</c> module and every test function of the head replaces the base's item of that name or is
    /// added at the module's end; a <c>#[cfg(test)]</c> module or item the head no longer has is removed; a module only
    /// the head has is added with its test code alone. Everything else stays the base's. A <c>#[cfg(test)]</c> item that is not a
    /// module, and a macro holding tests, are test code that cannot be matched to the base's: one the base lacks is a
    /// problem (unisolable). With no base text (the base lacks the file) the result is the head's test code alone — there
    /// every <c>#[cfg(test)]</c> item — plus an empty <c>fn main</c> when asked. <paramref name="declarations"/> are
    /// added at the end unless the base already has a module of that name.
    /// </summary>
    public static string Graft(string? baseText, RustFile head, IReadOnlyList<(string Name, string Text)> declarations, List<string> problems, string path,
        bool emptyMain = false)
    {
        static bool IsTestCode(RustItem item) => item.IsGated || item.IsTestFn;
        static bool HoldsTests(RustItem item) => item is { Kind: RustItem.Macro, BodyOpen: >= 0 } && Flatten(item.Items).Any(i => i.IsTestFn);
        static IEnumerable<RustItem> Flatten(IEnumerable<RustItem> items) => items.SelectMany(i => Flatten(i.Items).Prepend(i));

        // A module's test code alone: its #[cfg(test)] items, test functions, macros holding tests, and inner modules' likewise.
        string Strip(IReadOnlyList<RustItem> items)
        {
            var text = new StringBuilder();
            foreach (var item in items)
            {
                if (IsTestCode(item) || HoldsTests(item))
                {
                    text.Append(head.TextOf(item)).Append('\n');
                }
                else if (item.IsInlineMod && Strip(item.Items) is { Length: > 0 } inner)
                {
                    text.Append(head.Text, item.Start, item.BodyOpen + 1 - item.Start).Append('\n').Append(inner).Append("}\n");
                }
            }
            return text.ToString();
        }

        if (baseText is null)
        {
            var only = new StringBuilder(Strip(head.Items));
            foreach (var (_, text) in declarations)
            {
                only.Append(text).Append('\n');
            }
            if (emptyMain && head.Items.Any(i => i is { Kind: RustItem.Fn, Name: "main" } && !IsTestCode(i)))
            {
                only.Append("fn main() {}\n");
            }
            return only.ToString();
        }

        var @base = RustSource.Parse(baseText);
        var edits = new List<(int Start, int End, string Text)>();
        void Apply(IReadOnlyList<RustItem> baseItems, IReadOnlyList<RustItem> headItems, int end, IEnumerable<string> also)
        {
            var add = new StringBuilder();
            static bool IsLoose(RustItem i) => (i.IsGated && i.Kind != RustItem.Mod && !i.IsTestFn) || (!i.IsGated && HoldsTests(i));
            var loose = baseItems.Where(IsLoose).Select(i => RustSource.Normalize(@base.TextOf(i))).ToHashSet(StringComparer.Ordinal);
            var kept = headItems.Where(IsLoose).Select(i => RustSource.Normalize(head.TextOf(i))).ToHashSet(StringComparer.Ordinal);
            foreach (var item in headItems)
            {
                if ((item.Kind == RustItem.Mod && item.IsGated) || item.IsTestFn)
                {
                    var text = head.TextOf(item);
                    if (baseItems.FirstOrDefault(i => i.Kind == item.Kind && i.Name == item.Name) is not { } old)
                    {
                        add.Append('\n').Append(text).Append('\n');
                    }
                    else if (@base.TextOf(old) != text)
                    {
                        edits.Add((old.Start, old.End, text));
                    }
                }
                else if (item.IsGated || HoldsTests(item))
                {
                    if (!loose.Contains(RustSource.Normalize(head.TextOf(item))))
                    {
                        var line = head.Text.AsSpan(0, item.Start).Count('\n') + 1;
                        problems.Add(item.IsGated
                            ? $"{path}:{line} adds or changes a #[cfg(test)] item that is not a module, outside every #[cfg(test)] module: the gate cannot apply it to the base apart from the production code beside it (move it into a #[cfg(test)] module)"
                            : $"{path}:{line} adds or changes tests inside {item.Name}! outside every #[cfg(test)] module: the gate cannot apply them to the base apart from the production code beside them (move the macro into a #[cfg(test)] module)");
                    }
                }
                else if (item.IsInlineMod)
                {
                    if (baseItems.FirstOrDefault(i => i.IsInlineMod && i.Name == item.Name) is { } shared)
                    {
                        Apply(shared.Items, item.Items, shared.BodyClose, []);
                    }
                    else if (Strip([item]) is { Length: > 0 } alone)
                    {
                        add.Append('\n').Append(alone);
                    }
                }
            }
            // Test code the head no longer has: a #[cfg(test)] module, and a #[cfg(test)] item that is not one.
            foreach (var gone in baseItems.Where(i => (i.Kind == RustItem.Mod && i.IsGated && !headItems.Any(h => h.Kind == RustItem.Mod && h.Name == i.Name))
                || (i.IsGated && IsLoose(i) && !kept.Contains(RustSource.Normalize(@base.TextOf(i))))))
            {
                edits.Add((gone.Start, gone.End, ""));
            }
            foreach (var text in also)
            {
                add.Append('\n').Append(text).Append('\n');
            }
            if (add.Length > 0)
            {
                edits.Add((end, end, add.ToString()));
            }
        }
        Apply(@base.Items, head.Items, baseText.Length,
            declarations.Where(d => !@base.Items.Any(i => i.Kind == RustItem.Mod && i.Name == d.Name)).Select(d => d.Text));

        var result = new StringBuilder(baseText);
        foreach (var (start, end, text) in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            result.Remove(start, end - start).Insert(start, text);
        }
        return result.ToString();
    }

    // ---- The runs ---------------------------------------------------------------------------------------------------------

    private static readonly Regex TestId = new(@"^(?<package>[^:]+):(?<target>lib|bins|test/[^:]+):(?<path>.+?)(?<cases>::\*)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>cargo --version</c>; then per package manifest holding a new test <c>cargo fetch</c>, then
    /// <c>cargo test --no-run --message-format=json</c> for its targets with new tests (output kept as the build log), then
    /// per new test one <c>cargo test … --no-fail-fast -- --exact path</c> (a parameterized one: <c>-- path::</c>), its
    /// output kept under the test's id. Run from the worktree root with <c>--manifest-path</c>; nothing is written under
    /// <paramref name="resultsDirectory"/>.
    /// </summary>
    public Task<IReadOnlyList<TestStep>> StepsAsync(TestSource source, string commit, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects,
        string resultsDirectory, CancellationToken ct)
    {
        var parsed = tests.Select(id => (Id: id, Match: TestId.Match(id),
                Manifest: projects.TryGetValue(id, out var m) ? m : throw new InvalidOperationException($"The new test {id} has no package manifest.")))
            .Select(t => t.Match.Success ? t : throw new InvalidOperationException($"'{t.Id}' is not a cargo test id.")).ToList();
        var byManifest = parsed.GroupBy(t => t.Manifest, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        static string[] Target(string target) => target.StartsWith("test/", StringComparison.Ordinal) ? ["--test", target["test/".Length..]] : [$"--{target}"];
        var steps = new List<TestStep> { new(TestPhase.Toolchain, Cargo, ["--version"]) };
        steps.AddRange(byManifest.Select(g => new TestStep(TestPhase.Restore, Cargo, ["fetch", "--manifest-path", $"./{g.Key}"])));
        steps.AddRange(byManifest.Select((g, n) => new TestStep(TestPhase.Build, Cargo,
        [
            "test", "--manifest-path", $"./{g.Key}", "-p", g.First().Match.Groups["package"].Value, "--no-run", "--message-format=json",
            .. g.Select(t => t.Match.Groups["target"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SelectMany(Target),
        ]) { CaptureOutput = $"cargo-build-{n}" }));
        foreach (var (id, match, manifest) in byManifest.SelectMany(g => g))
        {
            var path = match.Groups["path"].Value;
            steps.Add(new TestStep(TestPhase.Test, Cargo,
            [
                "test", "--manifest-path", $"./{manifest}", "-p", match.Groups["package"].Value, .. Target(match.Groups["target"].Value), "--color", "never",
                "--no-fail-fast", "--", "--color", "never", .. match.Groups["cases"].Success ? new[] { $"{path}::" } : ["--exact", path],
            ]) { CaptureOutput = id });
        }
        return Task.FromResult<IReadOnlyList<TestStep>>(steps);
    }

    private static readonly Regex ResultLine = new(@"^test (?<name>\S+)(?: - should panic)? \.\.\. (?<result>ok|FAILED|ignored)(?:, .*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SummaryLine = new(
        @"^test result: (?<result>ok|FAILED)\. (?<passed>\d{1,9}) passed; (?<failed>\d{1,9}) failed; (?<ignored>\d{1,9}) ignored; \d{1,9} measured; \d{1,9} filtered out",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Each new test's cases from its own <c>cargo test</c> run's standard output (the result named by the test's id, with
    /// the run's exit code): libtest's lines <c>test &lt;name&gt; ... ok|FAILED|ignored</c> whose name is the test's path
    /// (a parameterized test: under it). A test can print such lines itself, so an output is read only when it agrees with
    /// what the test cannot forge: at least one <c>test result:</c> summary, whose passed/failed/ignored counts add up to
    /// the lines'; no test with two different results; exit code 0 with no failure anywhere, or a non-zero exit code with
    /// a failed test. Anything else — no exit code, no summary (the harness crashed or was replaced), counts that differ,
    /// a failure with exit code 0 or the reverse — is unreadable and named (never a result).
    /// </summary>
    public TestResults ParseResults(IEnumerable<ResultFile> resultFiles)
    {
        var results = new Dictionary<string, IReadOnlyList<TestCaseResult>>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        foreach (var file in resultFiles)
        {
            if (TestId.Match(file.Name) is not { Success: true } id)
            {
                unreadable.Add($"{file.Name}: it is not the output of a cargo test run of one test");
                continue;
            }
            if (file.ExitCode is not { } exit)
            {
                unreadable.Add($"{file.Name}: cargo's exit code is unknown");
                continue;
            }
            var lines = file.Content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var tests = lines.Select(l => ResultLine.Match(l)).Where(m => m.Success).Select(m => (Name: m.Groups["name"].Value, Result: m.Groups["result"].Value)).ToList();
            var summaries = lines.Select(l => SummaryLine.Match(l)).Where(m => m.Success).ToList();
            int Sum(string group) => summaries.Sum(m => int.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture));
            int Count(string result) => tests.Count(t => t.Result == result);
            var failed = Count("FAILED") > 0 || summaries.Any(m => m.Groups["result"].Value == "FAILED");
            var why = summaries.Count == 0 ? $"cargo test printed no test result summary (exit code {exit}), so nothing shows which tests ran"
                : (Sum("passed"), Sum("failed"), Sum("ignored")) != (Count("ok"), Count("FAILED"), Count("ignored"))
                    ? "its test lines do not add up to its summaries (a test wrote to standard output, or the output is not libtest's)"
                : tests.GroupBy(t => t.Name, StringComparer.Ordinal).Any(g => g.Select(t => t.Result).Distinct().Count() > 1)
                    ? "it reports one test with two different results"
                : exit == 0 && failed ? "it reports a failed test but cargo exited with code 0"
                : exit != 0 && Count("FAILED") == 0 ? $"cargo exited with code {exit} but no test is reported as failed"
                : null;
            if (why is not null)
            {
                unreadable.Add($"{file.Name}: {why}");
                continue;
            }
            var path = id.Groups["path"].Value;
            var cases = tests.Where(t => id.Groups["cases"].Success ? t.Name.StartsWith(path + "::", StringComparison.Ordinal) : t.Name == path)
                .Select(t => new TestCaseResult(t.Name, t.Result switch { "ok" => TestCaseResult.Passed, "FAILED" => TestCaseResult.Failed, _ => TestCaseResult.Skipped })).ToList();
            if (cases.Count > 0)
            {
                results[file.Name] = results.TryGetValue(file.Name, out var earlier) ? [.. earlier, .. cases] : cases;
            }
        }
        return new TestResults(results, unreadable);
    }

    /// <summary>
    /// The errors in cargo's JSON build output (<c>--message-format=json</c>, one message per line): every
    /// <c>compiler-message</c> of level <c>error</c> (rustc's closing "aborting due to…" line left out) with its primary
    /// span's file, line and column and its code (<c>E0425</c>, a lint's name, or <c>rustc</c> when it has none, e.g. a
    /// syntax error). rustc names files relative to the cargo workspace's root, which the message does not give: the file
    /// is found from the package's manifest path (absolute, under one of <paramref name="roots"/>) as the one directory
    /// at or above the package under which that relative name lies inside the package; a file that cannot be placed so (a
    /// dependency's, a generated one) gives an error without a path, which attributes nothing. Lines that are not JSON
    /// (cargo prints none there) are ignored: a build that failed with no error read is itself unattributable.
    /// </summary>
    public IReadOnlyList<BuildError> ParseBuildErrors(string log, IReadOnlyList<string> roots)
    {
        var errors = new List<BuildError>();
        foreach (var line in log.Split('\n').Where(l => l.TrimStart().StartsWith('{')))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Text(root, "reason") != "compiler-message" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || Text(message, "level") is not { } level || !level.StartsWith("error", StringComparison.Ordinal))
                {
                    continue;
                }
                var text = Text(message, "message") ?? "";
                if (text.StartsWith("aborting due to", StringComparison.Ordinal))
                {
                    continue;
                }
                var code = message.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "code") ?? "rustc" : "rustc";
                var span = message.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array
                    ? spans.EnumerateArray().FirstOrDefault(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("is_primary", out var p) && p.ValueKind == JsonValueKind.True)
                    : default;
                var file = span.ValueKind == JsonValueKind.Object ? Place(Text(span, "file_name"), Text(root, "manifest_path"), roots) : null;
                errors.Add(file is null ? new BuildError(null, 0, 0, code, text)
                    : new BuildError(file, Number(span, "line_start"), Number(span, "column_start"), code, text));
            }
            catch (JsonException)
            {
                // Not a cargo message.
            }
        }
        return errors.Distinct().ToList();
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>The repository path of a file rustc names relative to the workspace root, given the package's absolute manifest path; null when it cannot be placed.</summary>
    private static string? Place(string? file, string? manifest, IReadOnlyList<string> roots)
    {
        if (file is null || manifest is null || !Path.IsPathRooted(manifest))
        {
            return null;
        }
        var separator = Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(manifest);
        foreach (var root in roots)
        {
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + separator;
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var package = Parent(full[prefix.Length..].Replace(separator, '/'));
            if (Path.IsPathRooted(file))
            {
                var absolute = Path.GetFullPath(file);
                return absolute.StartsWith(prefix, StringComparison.Ordinal) ? absolute[prefix.Length..].Replace(separator, '/') : null;
            }
            var name = file.Replace('\\', '/');
            if (name.Split('/').Contains(".."))
            {
                return null;
            }
            // The workspace root is a directory above the package under which the name falls inside the package (one only,
            // else the file cannot be placed), or the package's own directory when none is.
            var placed = new List<string>();
            for (var workspace = package; workspace.Length > 0;)
            {
                workspace = Parent(workspace);
                if (name.StartsWith(Relative(workspace, package) + "/", StringComparison.Ordinal))
                {
                    placed.Add(Join(workspace, name));
                }
            }
            return placed.Count == 0 ? Join(package, name) : placed.Count == 1 ? placed[0] : null;
        }
        return null;
    }

    /// <summary>
    /// Each error is placed, by <see cref="RustSource"/> on the applied file as it was built, in the innermost item at its
    /// position (its unit). It says something about the new tests only inside test code: a file that is test code as a
    /// whole (<see cref="CargoNewTestSet.WholeFiles"/>), or in a grafted production file a <c>#[cfg(test)]</c> item or a
    /// test function; an error anywhere else (the base's production code, a file not applied, no file, a manifest or
    /// linker error) is unattributable, and so is a build that names no error. A new test whose own function, or an item
    /// containing it (a module whose own header holds the error), is a unit does not build on the base. The retry's files
    /// leave every outermost unit out. An error in test code outside every item, or between a module's items, is
    /// unremovable.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var cargo = set as CargoNewTestSet;
        var unattributable = new List<string>();
        var unremovable = new List<string>();
        var files = new SortedDictionary<string, (RustFile File, HashSet<RustItem> Units)>(StringComparer.Ordinal);
        if (errors.Count == 0)
        {
            unattributable.Add("the build failed without naming a compiler error (see the build's output: a manifest, dependency or linker error, or no rustc)");
        }
        foreach (var error in errors)
        {
            if (cargo is null || error.Path is not { } path || !Understands(path) || !applied.TryGetValue(path, out var text))
            {
                unattributable.Add(error.ToString());
                continue;
            }
            if (!files.TryGetValue(path, out var file))
            {
                try
                {
                    files[path] = file = (RustSource.Parse(text), new HashSet<RustItem>(ReferenceEqualityComparer.Instance));
                }
                catch (FormatException)
                {
                    unattributable.Add(error.ToString());
                    continue;
                }
            }
            var position = file.File.Position(error.Line, error.Column);
            var chain = position < 0 ? [] : file.File.ItemsAt(position);
            if (position < 0 || !(cargo.WholeFiles.Contains(path) || chain.Any(i => i.IsGated || i.IsTestFn)))
            {
                unattributable.Add(error.ToString());
            }
            else if (chain.Count == 0 || (chain[^1] is { BodyOpen: >= 0 } holder && position >= holder.BodyOpen))
            {
                unremovable.Add(error.ToString());
            }
            else
            {
                file.Units.Add(chain[^1]);
            }
        }

        var notBuilt = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, (file, units)) in files.Where(f => f.Value.Units.Count > 0))
        {
            foreach (var (inFile, fn, ancestors) in file.TestFns())
            {
                if (ancestors.Append(fn).Any(units.Contains))
                {
                    notBuilt.UnionWith(tests.Where(id => cargo!.Locations.TryGetValue(id, out var at) && at == (path, inFile)));
                }
            }
            var outermost = units.Where(u => !file.ItemsAt(u.Start).TakeWhile(i => !ReferenceEquals(i, u)).Any(units.Contains)).OrderByDescending(u => u.Start).ToList();
            var text = new StringBuilder(file.Text);
            foreach (var unit in outermost)
            {
                text.Remove(unit.Start, unit.End - unit.Start);
            }
            removed.AddRange(outermost.AsEnumerable().Reverse().Select(u => $"{path}:{file.Text.AsSpan(0, u.Start).Count('\n') + 1} {Describe(file, u)}"));
            replacements[path] = text.ToString();
        }
        return new BuildFailure(tests.Where(notBuilt.Contains).ToList(), unattributable, unremovable, removed, replacements);
    }

    private static string Describe(RustFile file, RustItem item) => item.Kind switch
    {
        RustItem.Fn => $"fn {item.Name}",
        RustItem.Mod => $"mod {item.Name}",
        RustItem.Macro => $"{item.Name}!",
        _ => Regex.Replace(file.TextOf(item), @"\s+", " ") is var text && text.Length > 60 ? text[..60] + "…" : Regex.Replace(file.TextOf(item), @"\s+", " "),
    };
}
