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

    /// <summary>
    /// The imports the overlay wrote into each production file beyond the base's own (<see cref="CargoNewTests.Graft"/>),
    /// by path, each statement normalized (<see cref="RustSource.Normalize"/>): one of these that does not compile on the
    /// base is left out of the retry, where an error in any other production <c>use</c> is the base's own.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> AddedUses { get; init; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
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
/// The base run's overlay (the PR's test code applied to the base), under one rule: a new test may count as not building
/// on the base only for something the base lacks, never for an import, helper or attribute the overlay left out; where
/// the test code cannot be applied faithfully the check is unsupported. Integration tests and files of a
/// <c>#[cfg(test)]</c> module are test code as a whole and come from the head. A production file with inline tests
/// cannot: it is written (through owner-side git, as the retries' files are) as its <em>base</em> version with the head's
/// test code grafted in (<see cref="Graft"/>) — every <c>#[cfg(test)]</c> module replaced by the head's (or added,
/// removed when the head has none), and every import of the head the base's module lacks added — so the production code
/// under test stays the base's. A file the base lacks (added) holding new tests is written with its imports and its test
/// code only (production items left out, an empty <c>fn main</c> kept for a binary) and its <c>mod</c> declaration added
/// to the base's parent module, so a test of new code is compiled on the base and fails to there, in its own function. A
/// package manifest's <c>dev-dependencies</c> tables are test code too: the base's manifest gets the head's (a new
/// package's manifest is applied whole).
/// </para>
/// <para>
/// Unisolable (the check is <c>unsupported</c>): a changed <c>.rs</c> file the reader cannot read; a new test outside a
/// package's <c>src/</c> and <c>tests/</c>, or in a module shared by integration tests (<c>tests/common/mod.rs</c>); a
/// package with a custom target layout (<c>[[test]]</c>, <c>autotests</c>/<c>autobins</c>, a <c>path</c> that is not the
/// default); a test function, or a test inside a macro, outside every <c>#[cfg(test)]</c> module that the base does not
/// have unchanged, and a <c>#[cfg(test)]</c> item that is not a module which the base lacks (neither can be told from
/// the production code beside it); two items of one kind and name in a module (cfg alternates); a renamed production
/// file holding new tests; <c>dev-dependencies</c> written as dotted keys; a new file whose <c>mod</c> declaration is
/// not at the top of its parent module's file.
/// </para>
/// <para>
/// Runs, from the worktree root, per package holding a new test: <c>cargo --version</c> (a toolchain probe: failing,
/// the run is <see cref="TestRunStatus.ToolchainMissing"/>, never a pass), <c>cargo fetch</c> (the restore: the only
/// step that may use the network, as <c>dotnet restore</c> is), <c>cargo test --offline --no-run --message-format=json</c>
/// for the targets holding new tests (the build: cargo's JSON diagnostics, a stable format, on standard output), then
/// one <c>cargo test --offline … -- --exact &lt;path&gt;</c> per new test (<c>--lib</c>, <c>--bins</c> or
/// <c>--test &lt;name&gt;</c>; a parameterized test by its <c>path::</c> prefix). Results: libtest's JSON output is
/// nightly-only, so the stable human-readable lines are read (<c>running N tests</c>,
/// <c>test &lt;name&gt; ... ok|FAILED|ignored</c> and <c>test result: …</c>), and only when they have libtest's shape and
/// agree with the exit code (<see cref="ParseResults"/>, which also says what that cannot catch); anything else is an
/// unreadable report, an <c>error</c>.
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

    /// <summary>
    /// Three: rustc reports in stages. An import that does not resolve on the base hides the errors of the code that uses
    /// its name, and those may hide type errors: each retry leaves out what the one before could not compile, so a test
    /// that does build still gets its real result after a test beside it failed in a later stage.
    /// </summary>
    public int BaseRetries => 3;

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

        var addedUses = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var replacements = await OverlayAsync(head, @base, changes, added.Select(id => headTests[id].Package).Distinct().ToList(),
            added.Select(id => headTests[id].Path).ToHashSet(StringComparer.Ordinal), testFiles, whole, problems, addedUses, ct);
        return new CargoNewTestSet(testFiles, added, problems)
        {
            Projects = added.ToDictionary(id => id, id => headTests[id].Package.ManifestPath, StringComparer.Ordinal),
            BaseReplacements = replacements,
            Locations = added.ToDictionary(id => id, id => (headTests[id].Path, headTests[id].InFile), StringComparer.Ordinal),
            WholeFiles = whole,
            AddedUses = addedUses,
        };
    }

    /// <summary>
    /// The base run's overlay for the <paramref name="packages"/> holding new tests, beyond their integration tests: adds
    /// to <paramref name="testFiles"/> and <paramref name="whole"/>, and returns the files written with other content than
    /// the head's (grafted production files, files the base lacks with their test code only, manifests).
    /// </summary>
    private static async Task<Dictionary<string, string>> OverlayAsync(Tree head, Tree @base, IReadOnlyList<ChangedFile> changes, IReadOnlyList<Package> packages,
        IReadOnlySet<string> withNewTests, List<ChangedFile> testFiles, HashSet<string> whole, List<string> problems,
        Dictionary<string, IReadOnlySet<string>> addedUses, CancellationToken ct)
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
                else if (withNewTests.Contains(path) && change.OldPath is { } old && old != path)
                {
                    // Its production code is the base's, under another name: written with its test code alone, a test of
                    // that code would not compile, and count as failing on a base where it passes.
                    problems.Add($"{path} holds new tests and is {old} renamed: the gate cannot apply its tests to the base apart from the production code that moved with it (rename the file in a pull request of its own)");
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
            var imports = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                text = Graft(before, file, declarations.GetValueOrDefault(path) ?? [], problems, path,
                    emptyMain: before is null && Regex.IsMatch(path, @"(^|/)src/(main\.rs|bin/[^/]+\.rs|bin/[^/]+/main\.rs)$", RegexOptions.CultureInvariant), imports);
            }
            catch (FormatException e)
            {
                problems.Add($"the PR's test code cannot be applied to {path} at the base ({e.Message})");
                continue;
            }
            if (imports.Count > 0)
            {
                addedUses[path] = imports;
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
    /// applied. Test code is what a <c>#[cfg(test)]</c> item holds (<see cref="RustItem.IsGated"/>); nothing else of the
    /// head is, since the gate cannot tell a helper beside a test from production code. In each module the two share (the
    /// file, then inline modules that are not <c>#[cfg(test)]</c>, by name), every <c>#[cfg(test)]</c> module of the head
    /// replaces the base's module of that name or is added at the module's end, and one the head no longer has is removed.
    /// Imports are not test code, but a test reaches them through <c>use super::*</c>: every single import of the head
    /// (<see cref="RustSource.UseLeaves"/>) whose name the base's module does not import is added at the module's end and
    /// recorded in <paramref name="addedUses"/>, so that a test never fails to compile on the base for an import the
    /// overlay left out (one that does not resolve there is left out of the retry, <see cref="ExplainBuildFailure"/>). A
    /// module only the head has is added with its imports and its test code alone. Everything else stays the base's.
    /// <para>
    /// A problem (the check is unsupported) wherever the test code cannot be applied faithfully: a test function, or a
    /// macro holding tests, outside every <c>#[cfg(test)]</c> module that the base does not have unchanged (the items it
    /// uses beside it would be left behind); a <c>#[cfg(test)]</c> item that is not a module which the base lacks; two
    /// items of one kind and name in a module (cfg alternates), which cannot be matched to the base's.
    /// </para>
    /// With no base text (the base lacks the file) the result is the head's inner attributes, imports and
    /// <c>#[cfg(test)]</c> items alone, plus an empty <c>fn main</c> when asked. <paramref name="declarations"/> are added
    /// at the end unless the base already has a module of that name.
    /// </summary>
    public static string Graft(string? baseText, RustFile head, IReadOnlyList<(string Name, string Text)> declarations, List<string> problems, string path,
        bool emptyMain = false, ISet<string>? addedUses = null)
    {
        static IEnumerable<RustItem> Flatten(IEnumerable<RustItem> items) => items.SelectMany(i => Flatten(i.Items).Prepend(i));
        static bool HoldsTests(RustItem item) => item is { Kind: RustItem.Macro, BodyOpen: >= 0 } && Flatten(item.Items).Any(i => i.IsTestFn);
        static bool HasTestCode(IEnumerable<RustItem> items) => Flatten(items).Any(i => i.IsGated || i.IsTestFn);
        int Line(RustItem item) => head.Text.AsSpan(0, item.Start).Count('\n') + 1;

        // A test outside every #[cfg(test)] module that the base does not have as it is.
        void Outside(RustItem item) => problems.Add(item.Kind == RustItem.Macro
            ? $"{path}:{Line(item)} adds or changes tests inside {item.Name}! outside every #[cfg(test)] module: the gate cannot apply them to the base apart from the production code beside them (move the macro into a #[cfg(test)] module)"
            : $"{path}:{Line(item)} adds or changes the test {item.Name} outside every #[cfg(test)] module: the gate cannot apply it to the base apart from the production code beside it (move it into a #[cfg(test)] module)");

        // The single imports of a use item that the base's module does not have, as statements.
        string Imports(RustItem use, IReadOnlySet<string>? keys, IReadOnlySet<string>? names)
        {
            var text = new StringBuilder();
            foreach (var leaf in RustSource.UseLeaves(head.TextOf(use)))
            {
                if (keys?.Contains(leaf.Key) != true && !(leaf.Name is { } name && names?.Contains(name) == true))
                {
                    text.Append(leaf.Statement).Append('\n');
                    addedUses?.Add(RustSource.Normalize(leaf.Statement));
                }
            }
            return text.ToString();
        }

        // A module the base lacks: its inner attributes, its imports and its test code (inner modules' likewise).
        string Strip(IReadOnlyList<RustItem> items, IReadOnlyList<RustAttribute> inner)
        {
            var text = new StringBuilder();
            foreach (var attribute in inner)
            {
                text.Append("#![").Append(attribute.Text).Append("]\n");
            }
            foreach (var item in items)
            {
                if (item.IsTestFn || (!item.IsGated && HoldsTests(item)))
                {
                    Outside(item);
                }
                else if (item.IsGated)
                {
                    text.Append(head.TextOf(item)).Append('\n');
                }
                else if (item.Kind == RustItem.Use)
                {
                    text.Append(Imports(item, null, null));
                }
                else if (item.IsInlineMod && HasTestCode(item.Items))
                {
                    text.Append(head.Text, item.Start, item.BodyOpen + 1 - item.Start).Append('\n').Append(Strip(item.Items, item.InnerAttributes)).Append("}\n");
                }
            }
            return text.ToString();
        }

        if (baseText is null)
        {
            var only = new StringBuilder(Strip(head.Items, head.InnerAttributes));
            foreach (var (_, text) in declarations)
            {
                only.Append(text).Append('\n');
            }
            if (emptyMain && head.Items.Any(i => i is { Kind: RustItem.Fn, Name: "main", IsGated: false, IsTestFn: false }))
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
            var imported = baseItems.Where(i => i.Kind == RustItem.Use && !i.IsGated).SelectMany(i => RustSource.UseLeaves(@base.TextOf(i))).ToList();
            var keys = imported.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
            var names = imported.Select(l => l.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);

            // Items are matched to the base's by kind and name: with two of them on either side (cfg alternates) they cannot be.
            var twice = new HashSet<(string, string?)>();
            bool Twice(RustItem item)
            {
                if (baseItems.Count(i => i.Kind == item.Kind && i.Name == item.Name) < 2 && headItems.Count(i => i.Kind == item.Kind && i.Name == item.Name) < 2)
                {
                    return false;
                }
                if (twice.Add((item.Kind, item.Name)))
                {
                    problems.Add($"{path}:{Line(item)} has more than one {item.Kind} {item.Name} in one module at the base or the head (cfg alternates): the gate cannot match them to apply the PR's test code to the base");
                }
                return true;
            }

            foreach (var item in headItems)
            {
                if (item.Kind == RustItem.Mod && item.IsGated)
                {
                    var text = head.TextOf(item);
                    if (Twice(item))
                    {
                        continue;
                    }
                    if (baseItems.FirstOrDefault(i => i.Kind == item.Kind && i.Name == item.Name) is not { } old)
                    {
                        add.Append('\n').Append(text).Append('\n');
                    }
                    else if (@base.TextOf(old) != text)
                    {
                        edits.Add((old.Start, old.End, text));
                    }
                }
                else if (item.IsTestFn)
                {
                    if (!Twice(item) && (baseItems.FirstOrDefault(i => i.Kind == item.Kind && i.Name == item.Name) is not { } old
                        || RustSource.Normalize(@base.TextOf(old)) != RustSource.Normalize(head.TextOf(item))))
                    {
                        Outside(item);
                    }
                }
                else if (item.IsGated || HoldsTests(item))
                {
                    if (!loose.Contains(RustSource.Normalize(head.TextOf(item))))
                    {
                        if (item.IsGated)
                        {
                            problems.Add($"{path}:{Line(item)} adds or changes a #[cfg(test)] item that is not a module, outside every #[cfg(test)] module: the gate cannot apply it to the base apart from the production code beside it (move it into a #[cfg(test)] module)");
                        }
                        else
                        {
                            Outside(item);
                        }
                    }
                }
                else if (item.Kind == RustItem.Use)
                {
                    if (Imports(item, keys, names) is { Length: > 0 } imports)
                    {
                        add.Append('\n').Append(imports);
                    }
                }
                else if (item.IsInlineMod)
                {
                    var same = baseItems.Concat(headItems).Where(i => i.Kind == RustItem.Mod && i.Name == item.Name).ToList();
                    if (baseItems.Count(i => i.Kind == RustItem.Mod && i.Name == item.Name) > 1 || headItems.Count(i => i.Kind == RustItem.Mod && i.Name == item.Name) > 1)
                    {
                        // Alternates without test code are production code: nothing to apply.
                        if (same.Any(i => i.IsGated || HasTestCode(i.Items)))
                        {
                            Twice(item);
                        }
                    }
                    else if (baseItems.FirstOrDefault(i => i.IsInlineMod && i.Name == item.Name) is { } shared)
                    {
                        Apply(shared.Items, item.Items, shared.BodyClose, []);
                    }
                    else if (HasTestCode(item.Items))
                    {
                        add.Append('\n').Append(head.Text, item.Start, item.BodyOpen + 1 - item.Start).Append('\n').Append(Strip(item.Items, item.InnerAttributes)).Append("}\n");
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

        return ApplyEdits(baseText, edits);
    }

    /// <summary>
    /// <paramref name="text"/> with each edit's range replaced by its text. Two edits of one place (overlapping ranges, or
    /// two insertions at one position) would corrupt the file silently, so they throw <see cref="FormatException"/>: the
    /// file is then one the PR's test code cannot be applied to (unsupported), never a damaged base.
    /// </summary>
    public static string ApplyEdits(string text, IReadOnlyList<(int Start, int End, string Text)> edits)
    {
        // Applied from the end of the file backwards.
        var ordered = edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End).ToList();
        for (var n = 1; n < ordered.Count; n++)
        {
            if (ordered[n].End > ordered[n - 1].Start || (ordered[n].Start, ordered[n].End) == (ordered[n - 1].Start, ordered[n - 1].End))
            {
                throw new FormatException($"two changes of the PR's test code fall on one place of the file, at offsets {ordered[n].Start} and {ordered[n - 1].Start}");
            }
        }
        var result = new StringBuilder(text);
        foreach (var (start, end, replacement) in ordered)
        {
            result.Remove(start, end - start).Insert(start, replacement);
        }
        return result.ToString();
    }

    // ---- The runs ---------------------------------------------------------------------------------------------------------

    private static readonly Regex TestId = new(@"^(?<package>[^:]+):(?<target>lib|bins|test/[^:]+):(?<path>.+?)(?<cases>::\*)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>cargo --version</c>; then per package manifest holding a new test <c>cargo fetch</c> (the only step that may
    /// use the network), then <c>cargo test --offline --no-run --message-format=json</c> for its targets with new tests
    /// (output kept as the build log), then per new test one <c>cargo test --offline … --no-fail-fast -- --exact path</c>
    /// (a parameterized one: <c>-- path::</c>), its output kept under the test's id. <c>--offline</c>: the build and the
    /// tests resolve and download nothing, they use what the fetch left. Run from the worktree root with <c>--manifest-path</c>; nothing is written under
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
            "test", "--manifest-path", $"./{g.Key}", "-p", g.First().Match.Groups["package"].Value, "--offline", "--no-run", "--message-format=json",
            .. g.Select(t => t.Match.Groups["target"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SelectMany(Target),
        ]) { CaptureOutput = $"cargo-build-{n}" }));
        foreach (var (id, match, manifest) in byManifest.SelectMany(g => g))
        {
            var path = match.Groups["path"].Value;
            steps.Add(new TestStep(TestPhase.Test, Cargo,
            [
                "test", "--manifest-path", $"./{manifest}", "-p", match.Groups["package"].Value, "--offline", .. Target(match.Groups["target"].Value), "--color", "never",
                "--no-fail-fast", "--", "--color", "never", .. match.Groups["cases"].Success ? new[] { $"{path}::" } : ["--exact", path],
            ]) { CaptureOutput = id });
        }
        return Task.FromResult<IReadOnlyList<TestStep>>(steps);
    }

    private static readonly Regex ResultLine = new(@"^test (?<name>\S+)(?: - should panic)? \.\.\. (?<result>ok|FAILED|ignored)(?:, .*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex RunningLine = new(@"^running (?<count>\d{1,9}) tests?$", RegexOptions.CultureInvariant);
    private static readonly Regex SummaryLine = new(
        @"^test result: (?<result>ok|FAILED)\. (?<passed>\d{1,9}) passed; (?<failed>\d{1,9}) failed; (?<ignored>\d{1,9}) ignored; (?<measured>\d{1,9}) measured; \d{1,9} filtered out",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Each new test's cases from its own <c>cargo test</c> run's standard output (the result named by the test's id, with
    /// the run's exit code): libtest's lines <c>test &lt;name&gt; ... ok|FAILED|ignored</c> whose name is the test's path
    /// (a parameterized test: under it). The output is read only when it has libtest's shape and agrees with the exit
    /// code: per test binary a <c>running N tests</c> line, then N result lines, then one <c>test result:</c> summary
    /// whose passed/failed/ignored counts are those lines' (and add up to N); at least one such block; no result line
    /// outside one; no test with two different results; exit code 0 with no failure anywhere, or a non-zero exit code
    /// with a failed test. Anything else — no exit code, no summary (the harness crashed or was replaced), counts that
    /// differ, a failure with exit code 0 or the reverse — is unreadable and named (never a result).
    /// <para>
    /// This catches a test that prints while it runs, not one that sets out to forge: the test process writes all of this
    /// output and chooses its exit code, so a test that prints a whole libtest report and calls
    /// <c>std::process::exit(101)</c> is read as failed on the base. Accepted: such a test is reviewed code that lies
    /// about itself, as one that asserts on the source tree would be.
    /// </para>
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
                : !Blocks(lines) ? "it is not libtest's report, in which each `running N tests` line is followed by N test lines and their summary (a test wrote to standard output, or the output is not libtest's)"
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
    /// Whether the output is a sequence of libtest's blocks: <c>running N tests</c>, then exactly N test lines, then a
    /// summary counting those lines (N = passed + failed + ignored + measured), with no test line or summary outside a block.
    /// </summary>
    private static bool Blocks(IReadOnlyList<string> lines)
    {
        static int Number(Match match, string group) => int.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
        (int Running, int Passed, int Failed, int Ignored)? block = null;
        foreach (var line in lines)
        {
            if (RunningLine.Match(line) is { Success: true } running)
            {
                if (block is not null)
                {
                    return false;
                }
                block = (Number(running, "count"), 0, 0, 0);
            }
            else if (ResultLine.Match(line) is { Success: true } test)
            {
                if (block is not var (count, passed, failed, ignored))
                {
                    return false;
                }
                var result = test.Groups["result"].Value;
                block = (count, passed + (result == "ok" ? 1 : 0), failed + (result == "FAILED" ? 1 : 0), ignored + (result == "ignored" ? 1 : 0));
            }
            else if (SummaryLine.Match(line) is { Success: true } summary)
            {
                if (block is not var (count, passed, failed, ignored) || (passed, failed, ignored) != (Number(summary, "passed"), Number(summary, "failed"), Number(summary, "ignored"))
                    || count != passed + failed + ignored + Number(summary, "measured"))
                {
                    return false;
                }
                block = null;
            }
        }
        return block is null;
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
    /// position (its unit). It says something about the new tests only inside what the overlay applied: a file that is
    /// test code as a whole (<see cref="CargoNewTestSet.WholeFiles"/>), in a production file a <c>#[cfg(test)]</c> item, or
    /// an import the overlay added (<see cref="CargoNewTestSet.AddedUses"/>); an error anywhere else (the base's
    /// production code, its own imports, a file not applied, no file, a manifest or linker error) is unattributable, and
    /// so is a build that names no error, and an ambiguous name (E0659: two imports, never something the base lacks). A
    /// new test whose own function is a unit, or that is inside a macro that is one, does not build on the base. The
    /// retry's files leave every outermost unit out; of a <c>use</c> only the single imports the error lies in
    /// (<see cref="RustSource.UseLeaves"/>: the others stay, so a test that needs one of them still compiles). An error
    /// in test code outside every item, between a module's items, or in a module's own header (nothing a test lacks on
    /// the base shows there) is unremovable.
    /// </summary>
    public BuildFailure ExplainBuildFailure(NewTestSet set, IReadOnlyDictionary<string, string> applied, IReadOnlyList<BuildError> errors, IReadOnlyList<string> tests)
    {
        var cargo = set as CargoNewTestSet;
        var unattributable = new List<string>();
        var unremovable = new List<string>();
        var files = new SortedDictionary<string, (RustFile File, HashSet<RustItem> Units, Dictionary<RustItem, HashSet<int>> Imports)>(StringComparer.Ordinal);
        if (errors.Count == 0)
        {
            unattributable.Add("the build failed without naming a compiler error (see the build's output: a manifest, dependency or linker error, or no rustc)");
        }
        foreach (var error in errors)
        {
            if (cargo is null || error.Path is not { } path || !Understands(path) || !applied.TryGetValue(path, out var text) || error.Code == Ambiguous)
            {
                unattributable.Add(error.ToString());
                continue;
            }
            if (!files.TryGetValue(path, out var file))
            {
                try
                {
                    files[path] = file = (RustSource.Parse(text), new HashSet<RustItem>(ReferenceEqualityComparer.Instance), new Dictionary<RustItem, HashSet<int>>(ReferenceEqualityComparer.Instance));
                }
                catch (FormatException)
                {
                    unattributable.Add(error.ToString());
                    continue;
                }
            }
            var position = file.File.Position(error.Line, error.Column);
            var chain = position < 0 ? [] : file.File.ItemsAt(position);
            var last = chain.Count > 0 ? chain[^1] : null;
            var added = last is { Kind: RustItem.Use } && cargo.AddedUses.TryGetValue(path, out var imports) && imports.Contains(RustSource.Normalize(file.File.TextOf(last)));
            if (position < 0 || !(cargo.WholeFiles.Contains(path) || chain.Any(i => i.IsGated) || added))
            {
                unattributable.Add(error.ToString());
            }
            else if (last is null || (last.BodyOpen >= 0 && position >= last.BodyOpen) || last.Kind == RustItem.Mod)
            {
                unremovable.Add(error.ToString());
            }
            else
            {
                file.Units.Add(last);
                if (last.Kind == RustItem.Use)
                {
                    var leaves = RustSource.UseLeaves(file.File.TextOf(last));
                    var hit = Enumerable.Range(0, leaves.Count).Where(n => leaves[n].Holds(position - last.Start)).ToList();
                    var hits = file.Imports.TryGetValue(last, out var earlier) ? earlier : file.Imports[last] = [];
                    // An error in none of its imports (its attributes, its keyword) is the whole item's.
                    hits.UnionWith(hit.Count > 0 ? hit : Enumerable.Range(0, leaves.Count));
                }
            }
        }

        var notBuilt = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, (file, units, imports)) in files.Where(f => f.Value.Units.Count > 0))
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
            var described = new List<string>();
            foreach (var unit in outermost)
            {
                var line = file.Text.AsSpan(0, unit.Start).Count('\n') + 1;
                var leaves = unit.Kind == RustItem.Use ? RustSource.UseLeaves(file.TextOf(unit)) : [];
                text.Remove(unit.Start, unit.End - unit.Start);
                if (imports.TryGetValue(unit, out var hits) && hits.Count < leaves.Count)
                {
                    // The imports the error does not lie in stay, each as its own statement.
                    text.Insert(unit.Start, string.Join('\n', leaves.Where((_, n) => !hits.Contains(n)).Select(l => l.Statement)));
                    described.AddRange(leaves.Where((_, n) => hits.Contains(n)).Select(l => $"{path}:{line} {l.Statement}").Reverse());
                }
                else
                {
                    described.Add($"{path}:{line} {Describe(file, unit)}");
                }
            }
            removed.AddRange(described.AsEnumerable().Reverse());
            replacements[path] = text.ToString();
        }
        return new BuildFailure(tests.Where(notBuilt.Contains).ToList(), unattributable, unremovable, removed, replacements);
    }

    /// <summary>rustc's code for a name two imports both give (an ambiguity, e.g. the base's glob import and one the overlay added).</summary>
    private const string Ambiguous = "E0659";

    private static string Describe(RustFile file, RustItem item) => item.Kind switch
    {
        RustItem.Fn => $"fn {item.Name}",
        RustItem.Mod => $"mod {item.Name}",
        RustItem.Macro => $"{item.Name}!",
        _ => Regex.Replace(file.TextOf(item), @"\s+", " ") is var text && text.Length > 60 ? text[..60] + "…" : Regex.Replace(file.TextOf(item), @"\s+", " "),
    };
}
