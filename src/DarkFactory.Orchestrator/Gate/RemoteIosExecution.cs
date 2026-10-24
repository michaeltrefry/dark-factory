using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Prepare;

namespace DarkFactory.Orchestrator.Gate;

public sealed partial class RemoteIosNewTests
{
    /// <summary>
    /// Executes a discovered Xcode iOS set on an isolated hosted VM. Discovery, overlay construction, compiler attribution,
    /// retries and judging remain the ordinary new-tests check's; this boundary transports only a typed execution plan.
    /// No local helper, owner cache path, installation credential or arbitrary TestStep reaches the hosted principal.
    /// </summary>
    public async Task<TestRunReport> RunExecutionAsync(RepoRef repo, string baseSha, string headSha, TestRunSpec spec,
        IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> projects, PrepareRecipe? prepare, CancellationToken ct)
    {
        if (gui is not null && !gui.Allows(repo))
            return TestRunReport.Failed(TestRunStatus.NoResults, "gui-source-refused: The repository is not in Gate:Gui:TrustedRepos.");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!RemoteIosWorkflow.Sha(baseSha) || !RemoteIosWorkflow.Sha(headSha) || baseSha == headSha
                || tests.Count is < 1 or > 200 || tests.Distinct(StringComparer.Ordinal).Count() != tests.Count
                || tests.Any(t => t.Length > 1000 || t.Any(char.IsControl)))
                throw new InvalidDataException("Invalid iOS execution identity.");
            var side = spec.Commit == baseSha ? "base" : spec.Commit == headSha ? "head" : throw new InvalidDataException("Unknown execution commit.");
            if ((side == "head" && (spec.OverlayFrom is not null || spec.Replacements.Count != 0))
                || (side == "base" && spec.OverlayFrom != headSha)) throw new InvalidDataException("Invalid overlay commit.");
            var builds = tests.GroupBy(t => projects[t], StringComparer.Ordinal).Select(g => {
                var b = XcodeNewTests.Build.From(g.Key);
                if (gui is null && b.Platform != XcodeProject.IOS) throw new InvalidDataException("Non-iOS execution plan.");
                return new { flag = b.Flag, container = b.Container, scheme = b.Scheme, tests = g.ToArray() };
            }).ToArray();
            // Recipe declarations, not TestSteps expanded to an owner's private cache. Every artifact is independently
            // fetched and hash-verified by the sealed collector before any source code is run as its separate principal.
            var recipe = prepare is null ? null : new {
                artifacts = prepare.Artifacts.Select(a => new { name = a.Name, url = a.Url.AbsoluteUri, sha256 = a.Sha256,
                    maxBytes = a.MaxBytes, unpack = a.Unpack switch { ArtifactUnpack.None => "none", ArtifactUnpack.Zip => "zip", ArtifactUnpack.Tar => "tar", _ => "tar.gz" }, maxUnpackedBytes = a.MaxUnpackedBytes }),
                steps = prepare.Steps.Select(s => new { name = s.Name, argv = s.Argv, workingDirectory = s.WorkingDirectory,
                    environment = s.Environment, timeoutSeconds = s.Timeout?.TotalSeconds })
            };
            var descriptor = JsonSerializer.Serialize(new { side, commit = spec.Commit, overlayFrom = spec.OverlayFrom,
                overlayPaths = spec.OverlayPaths, deletePaths = spec.DeletePaths, replacements = spec.Replacements, builds, prepare = recipe });
            if (gui is not null)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(descriptor)!;
                node["sourceRepository"] = repo.FullName;
                foreach (var (build, group) in node["builds"]!.AsArray().Zip(tests.GroupBy(t => projects[t], StringComparer.Ordinal)))
                    build!["platform"] = XcodeNewTests.Build.From(group.Key).Platform;
                descriptor = node.ToJsonString();
            }
            if (Encoding.UTF8.GetByteCount(descriptor) > MaxExecutionBytes) throw new InvalidDataException("Oversized iOS execution descriptor.");
            var request = new IosNewTestsRequest(baseSha, headSha, tests) { Execution = descriptor, SourceRepository = gui is null ? null : repo.FullName };
            var (text, nonce) = await InvokeAsync(gui?.Collector ?? repo, request, () => Timeout - elapsed.Elapsed, deadline.Token);
            return ParseExecution(text, nonce, request, spec.Strategy, side, spec.Commit, prepare);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Neither transport bodies nor arbitrary exception text (which may include secrets) become evidence.
            return TestRunReport.Failed(ex is OperationCanceledException or TimeoutException ? TestRunStatus.TimedOut : TestRunStatus.NoResults,
                Failure(ex));
        }
    }

    private TestRunReport ParseExecution(string text, string nonce, IosNewTestsRequest request, INewTestStrategy strategy, string side, string commit, PrepareRecipe? prepare)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxResultBytes) throw new InvalidDataException("Oversized execution result.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        Unique(root);
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce
            || root.GetProperty("base").GetString() != request.Base || root.GetProperty("head").GetString() != request.Head
            || root.GetProperty("side").GetString() != side
            || root.GetProperty("executionHash").GetString() != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Execution!))))
            throw new InvalidDataException("Unbound execution result.");
        var environment = EnvironmentOf(root, gui is not null);
        if (gui is null ? environment.Xcode != "16.4" || environment.Build != "16F6"
            : root.GetProperty("guiConfigurationHash").GetString() != gui.ConfigurationSha256) throw new InvalidDataException("Unpinned execution environment.");
        var execution = root.GetProperty("execution");
        var sourceRoot = execution.GetProperty("root").GetString()!;
        // A label used only for diagnostics normalization, never an authority to read a local path.
        if (!Regex.IsMatch(sourceRoot, @"^/(?:private/)?tmp/factory-ios-[A-Za-z0-9_-]+/" + side + "$", RegexOptions.CultureInvariant)
            || execution.GetProperty("commit").GetString() != commit) throw new InvalidDataException("Wrong execution root or commit.");
        var log = execution.GetProperty("log").GetString()!;
        var files = execution.GetProperty("files").Deserialize<ResultFile[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing reports.");
        var status = execution.GetProperty("status").GetString();
        if (status == "prepare-failed")
        {
            if (files.Length != 0 || log.Length != 0) throw new InvalidDataException("Unexpected prepare evidence.");
            var failure = execution.GetProperty("failure");
            var code = failure.GetProperty("code").GetString();
            var step = failure.GetProperty("step");
            var reason = code switch
            {
                "prepare-step-failed" => "A declared prepare step exited unsuccessfully. Check its declared command, inputs and dependencies.",
                "prepare-step-timeout" => "A declared prepare step exceeded its execution budget. Reduce its work or increase the configured test timeout.",
                "artifact-fetch-failed" => "A declared artifact could not be downloaded, verified or unpacked. Check its URL, digest and size limits.",
                "package-hydration-failed" => "Package hydration failed on the hosted runner. Check dependency declarations and package availability.",
                "package-ipc-unavailable" => "The hosted runner cannot provide the exact isolated package endpoints. Check loopback binding and sandbox support; no broader network access is permitted.",
                "package-storage-unavailable" => "The hosted runner cannot verify isolated temporary and cache directories owned by its fresh source role. Check the role's Darwin directory setup; owner directories are never granted.",
                "package-signature-refused" => "Native SwiftPM refused a package signature or its supplied trust policy. Check the package signing evidence and registry trust configuration; validation is never bypassed.",
                "package-auth-required" => "A package requires credentials unavailable to the hosted runner. Use an anonymously accessible pinned dependency or a declared prepared artifact; owner credentials are never forwarded.",
                "package-unavailable" => "A package was not found or requires credentials unavailable to the hosted runner. Check its URL and anonymous availability; owner credentials are never forwarded.",
                _ => throw new InvalidDataException("Unknown prepare failure.")
            };
            var stepFailure = code is "prepare-step-failed" or "prepare-step-timeout";
            if (stepFailure)
            {
                var index = step.GetInt32();
                if (prepare is null || index < 0 || index >= prepare.Steps.Count) throw new InvalidDataException("Unknown prepare step.");
                reason = $"Prepare step {index + 1}: " + reason;
            }
            else if (step.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Unexpected prepare step.");
            return TestRunReport.Failed(TestRunStatus.NoResults, "remote-" + code + ": " + reason);
        }
        if (status == "build-failed")
        {
            if (files.Length != 0) throw new InvalidDataException("Reports from a failed build.");
            return TestRunReport.Failed(TestRunStatus.BuildFailed, log) with { BuildErrors = strategy.ParseBuildErrors(log, [sourceRoot]) };
        }
        if (status != "ran") throw new InvalidDataException("No executed evidence.");
        var expected = request.Tests.SelectMany(t => new[] { XcodeNewTests.OutputName(t), XcodeNewTests.JsonName(t) }).ToHashSet(StringComparer.Ordinal);
        if (files.Length != expected.Count || !expected.SetEquals(files.Select(f => f.Name)) || files.Any(f => f.ExitCode is null || f.Content is null))
            throw new InvalidDataException("Incomplete or unexpected execution reports.");
        var parsed = strategy.ParseResults(files);
        if (parsed.Unreadable.Count != 0 || parsed.Cases.Count != request.Tests.Count || !request.Tests.All(t => parsed.Cases.TryGetValue(t, out var cases) && cases.Count > 0))
            throw new InvalidDataException("Incomplete or unreadable per-test evidence.");
        return new TestRunReport(TestRunStatus.Ran, parsed.Cases, log);
    }
}
