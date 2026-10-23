using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>S1's concrete Tally simulator fixture. S2 owns general strategy routing, ledger reuse and control-aware waits.</summary>
public sealed record IosNewTestsRequest(string Base, string Head, IReadOnlyList<string> Tests);
public sealed record IosSealedWorkflow(string DefaultBranch, string Commit, string Content);
public sealed record IosWorkflowRun(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("display_title")] string Title,
    [property: JsonPropertyName("head_sha")] string HeadSha,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("run_attempt")] int Attempt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion);

/// <summary>Owner-side GitHub access. Dispatch must obtain a repo-scoped gate Actions-write token before any write.</summary>
public interface IIosWorkflowGitHub
{
    Task<IosSealedWorkflow?> ReadDefaultWorkflowAsync(RepoRef repo, CancellationToken ct);
    Task DispatchAsync(RepoRef repo, IosSealedWorkflow workflow, IosNewTestsRequest request, string correlation, CancellationToken ct);
    Task<IReadOnlyList<IosWorkflowRun>> RunsAsync(RepoRef repo, CancellationToken ct);
    Task<string> ResultAsync(RepoRef repo, IosWorkflowRun run, string correlation, CancellationToken ct);
}

/// <summary>The installable workflow is compiled in, including its runner: no helper or action is read from the PR.</summary>
public static class RemoteIosWorkflow
{
    public const string Path = ".github/workflows/factory-ios-new-tests.yml";
    internal static string Runner { get; } = Resource("factory-ios-remote.py");
    public static string Text { get; } = Resource("factory-ios-new-tests.yml").Replace("          # FACTORY_RUNNER",
        string.Join('\n', Runner.TrimEnd().Split('\n').Select(line => "          " + line)), StringComparison.Ordinal);
    private static string Resource(string name)
    {
        using var stream = typeof(RemoteIosWorkflow).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static bool Sha(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    public static bool Trusted(IosSealedWorkflow workflow) => Sha(workflow.Commit) && !string.IsNullOrWhiteSpace(workflow.DefaultBranch)
        && workflow.Content == Text;
}

/// <summary>A fully bounded remote invocation returning the ordinary per-test result; never treats a job's green check as evidence.</summary>
public sealed class RemoteIosNewTests(IIosWorkflowGitHub github)
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxPolls { get; init; } = 180;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(35);
    public const int MaxResultBytes = 128 * 1024;

    public async Task<NewTestsResult> RunAsync(RepoRef repo, IosNewTestsRequest request, CancellationToken ct)
    {
        NewTestsResult Error(string why) => NewTestsResult.Without(request.Base, request.Head, NewTestsOutcome.Error, why, "ios-remote");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        var token = deadline.Token;
        try
        {
            if (!RemoteIosWorkflow.Sha(request.Base) || !RemoteIosWorkflow.Sha(request.Head) || request.Base == request.Head
                || request.Tests.Count is < 1 or > 20 || request.Tests.Distinct(StringComparer.Ordinal).Count() != request.Tests.Count
                || request.Tests.Any(t => !Regex.IsMatch(t, "^TallyTests/TallyTests/test[A-Za-z0-9_]{1,100}$", RegexOptions.CultureInvariant)))
                return Error("Invalid single-fixture iOS request (commits or new test identifiers).");
            var workflow = await github.ReadDefaultWorkflowAsync(repo, token);
            if (workflow is null || !RemoteIosWorkflow.Trusted(workflow))
                return Error("Missing or invalid factory iOS workflow on the target default branch; no workflow dispatched.");
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            await github.DispatchAsync(repo, workflow, request, nonce, token);
            for (var poll = 0; poll < MaxPolls; poll++)
            {
                var runs = (await github.RunsAsync(repo, token)).Where(r => r.Title == "factory-ios-" + nonce).ToArray();
                if (runs.Length > 1) return Error("Ambiguous iOS workflow dispatch correlation.");
                if (runs.SingleOrDefault() is { } run)
                {
                    if (run.Id <= 0 || run.Event != "workflow_dispatch" || run.HeadSha != workflow.Commit
                        || run.Path != RemoteIosWorkflow.Path || run.Attempt != 1)
                        return Error("iOS workflow run provenance does not match the sealed dispatch.");
                    if (run.Status == "completed")
                    {
                        if (run.Conclusion != "success") return Error("iOS workflow did not complete successfully: " + run.Conclusion);
                        return Parse(await github.ResultAsync(repo, run, nonce, token), nonce, request);
                    }
                }
                await Task.Delay(PollInterval, token);
            }
            return Error("iOS workflow never reported within the bounded polling window.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not surface transport response bodies: these may contain credentials or arbitrary repository text.
            return Error("iOS remote check failed (" + ex.GetType().Name + "); no usable executed evidence.");
        }
    }

    private static NewTestsResult Parse(string text, string nonce, IosNewTestsRequest request)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxResultBytes) throw new InvalidDataException("Oversized iOS result.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        var root = doc.RootElement;
        Unique(root);
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce
            || root.GetProperty("base").GetString() != request.Base || root.GetProperty("head").GetString() != request.Head)
            throw new InvalidDataException("Wrong result identity.");
        var rows = root.GetProperty("tests").EnumerateArray().ToArray();
        if (rows.Length != request.Tests.Count) throw new InvalidDataException("Incomplete result.");
        var results = new List<NewTestResult>();
        foreach (var (row, test) in rows.Zip(request.Tests))
        {
            if (row.GetProperty("test").GetString() != test) throw new InvalidDataException("Wrong test.");
            var b = row.GetProperty("base").GetString()!;
            var h = row.GetProperty("head").GetString()!;
            if (b is not ("passed" or "failed" or "not-built" or "skipped") || h is not ("passed" or "failed" or "skipped"))
                throw new InvalidDataException("Unproven test.");
            results.Add(new NewTestResult(test, b, h, b == "not-built" ? [] : [new(test, b)], [new(test, h)]));
        }
        var passed = results.All(t => t.Base is "failed" or "not-built" && t.Head == "passed");
        return new NewTestsResult(request.Base, request.Head, passed ? NewTestsOutcome.Pass : NewTestsOutcome.Rejected,
            passed ? "Each new iOS test fails or does not build on the base and passes on the head." : "The iOS executed evidence does not prove every new test.",
            "ios-remote", "remote", "remote", results);
    }

    internal static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate result property.");
                Unique(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Unique(item);
    }
}
