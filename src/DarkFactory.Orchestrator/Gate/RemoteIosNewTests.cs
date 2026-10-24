using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A sealed remote request: an S1 owner-CLI fixture or the gate's typed discovered-target execution plan.</summary>
public sealed record IosNewTestsRequest(string Base, string Head, IReadOnlyList<string> Tests)
{
    /// <summary>A gate-generated execution plan, not repository commands. Null retains the S1 owner CLI fixture.</summary>
    public string? Execution { get; init; }
    public string? SourceRepository { get; init; }
    /// <summary>Remaining invocation budget, including hosted queueing, transported independently of the hashed plan.</summary>
    public double ExecutionBudgetSeconds { get; init; }
}
/// <summary>Code-written diagnostics only; never construct with transport bodies or repository text.</summary>
public sealed class IosWorkflowException(string code, string reason) : Exception($"{code}: {reason}");
/// <summary>Only the transport's pre-POST boundary may assert that no workflow dispatch could have been sent.</summary>
public sealed class IosWorkflowNotSentException : Exception
{
    internal IosWorkflowNotSentException(string reason, bool cancelled = false) : base("remote-dispatch-not-sent: " + reason) => Cancelled = cancelled;
    public bool Cancelled { get; }
}
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
    /// <summary>Cancel only the correlated run whose sealed provenance the caller already validated.</summary>
    Task CancelAsync(RepoRef repo, IosWorkflowRun run, CancellationToken ct);
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
public sealed partial class RemoteIosNewTests(IIosWorkflowGitHub github, string? stateDirectory = null, GuiWorkflowSettings? gui = null)
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(35);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim invocation = new(1);
    private Outstanding? outstanding;
    private sealed record Outstanding(string Nonce, string Commit);
    private sealed class RemoteFailure(string code, string reason) : Exception($"{code}: {reason}");
    private sealed class RemoteCancelled(string reason, Exception inner, CancellationToken token) : OperationCanceledException(reason, inner, token);
    private static string Failure(Exception ex) => ex is RemoteFailure or RemoteCancelled or IosWorkflowException or IosWorkflowNotSentException ? ex.Message
        : ex is OperationCanceledException or TimeoutException ? "remote-deadline: The iOS invocation deadline expired; no usable executed evidence."
        : ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException
            ? "remote-invalid-evidence: The sealed iOS workflow or its executed evidence could not be validated. Verify the installed workflow and result artifacts."
            : "remote-transport: The iOS workflow transport failed. Check gate App Actions permissions and GitHub availability; no usable executed evidence.";
    public const int MaxResultBytes = 2 * 1024 * 1024;
    public const int MaxExecutionBytes = 48 * 1024;

    public async Task<NewTestsResult> RunAsync(RepoRef repo, IosNewTestsRequest request, CancellationToken ct)
    {
        NewTestsResult Error(string why) => NewTestsResult.Without(request.Base, request.Head, NewTestsOutcome.Error, why, "ios-remote");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        var token = deadline.Token;
        try
        {
            if (!RemoteIosWorkflow.Sha(request.Base) || !RemoteIosWorkflow.Sha(request.Head) || request.Base == request.Head
                || request.Tests.Count is < 1 or > 20 || request.Tests.Distinct(StringComparer.Ordinal).Count() != request.Tests.Count
                || request.Tests.Any(t => !Regex.IsMatch(t, "^TallyTests/TallyTests/test[A-Za-z0-9_]{1,100}$", RegexOptions.CultureInvariant)))
                return Error("Invalid single-fixture iOS request (commits or new test identifiers).");
            var (text, nonce) = await InvokeAsync(repo, request, () => Timeout - elapsed.Elapsed, token);
            return Parse(text, nonce, request);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Do not surface transport response bodies: these may contain credentials or arbitrary repository text.
            return Error(Failure(ex));
        }
    }

    /// <remarks>Production supplies an owner-only directory under WorkRoot, closed to sandbox users before use.
    /// The reservation precedes dispatch: a crash or lost dispatch answer never grants permission to start a replacement.
    /// A missing correlation is uncertain, not proof that nothing was dispatched.</remarks>
    private async Task<(string Text, string Nonce)> InvokeAsync(RepoRef repo, IosNewTestsRequest request,
        Func<TimeSpan> remaining, CancellationToken token)
    {
        await invocation.WaitAsync(token);
        FileStream? repositoryLock = null;
        string? journal = null;
        try
        {
            if (stateDirectory is not null)
            {
                Directory.CreateDirectory(stateDirectory);
                await Worker.WorkerSandbox.CloseDirectoryToOwnerAsync(stateDirectory, token);
                outstanding = null; // Durable state is per repository, not the previous invocation's repository.
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repo.Key)));
                journal = System.IO.Path.Combine(stateDirectory, key + ".json");
                while (repositoryLock is null)
                {
                    token.ThrowIfCancellationRequested();
                    try { repositoryLock = new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(50), token); }
                }
                if (File.Exists(journal) || Directory.Exists(journal))
                {
                    try
                    {
                        // A journal is owner-written and tiny; malformed or unreadable state never permits a dispatch.
                        if (new FileInfo(journal).Length > 1024) throw new InvalidDataException();
                        outstanding = JsonSerializer.Deserialize<Outstanding>(await File.ReadAllTextAsync(journal, token));
                        if (outstanding is null || !Regex.IsMatch(outstanding.Nonce, "^[0-9a-f]{32}$") || !RemoteIosWorkflow.Sha(outstanding.Commit))
                            throw new InvalidDataException();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    { throw new RemoteFailure("remote-journal-invalid", "The owner-side iOS dispatch journal cannot be validated. Confirm the prior workflow is terminal before repairing its journal; no replacement dispatched."); }
                }
            }
            if (outstanding is not null)
            {
                if (!await CleanupAsync(repo, outstanding))
                {
                    if (token.IsCancellationRequested) throw new RemoteCancelled(Uncertain().Message, new TimeoutException(), token);
                    throw Uncertain();
                }
                Clear();
            }
            var workflow = await github.ReadDefaultWorkflowAsync(repo, token);
            if (workflow is null || !(gui is null ? RemoteIosWorkflow.Trusted(workflow) : RemoteGuiWorkflow.Trusted(workflow)))
                throw new RemoteFailure("remote-workflow-untrusted", "Install the exact sealed factory iOS workflow on the target default branch; no workflow dispatched.");
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            token.ThrowIfCancellationRequested();
            outstanding = new(nonce, workflow.Commit);
            if (journal is not null)
            {
                var temporary = journal + "." + nonce + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(outstanding), token);
                File.Move(temporary, journal, true);
            }
            try
            {
                var seconds = remaining().TotalSeconds;
                if (seconds <= 0) throw new IosWorkflowNotSentException("The iOS invocation deadline expired before dispatch; no workflow was sent.", cancelled: true);
                await github.DispatchAsync(repo, workflow, request with { ExecutionBudgetSeconds = seconds }, nonce, token);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var run = Correlated(await github.RunsAsync(repo, token), outstanding);
                    if (run?.Status == "completed")
                    {
                        Clear(); // Terminal is enough to release the no-overlap barrier, never enough to prove tests.
                        if (run.Conclusion != "success")
                            throw new RemoteFailure("remote-workflow-failed", "The sealed iOS workflow completed without success. Inspect its GitHub Actions jobs; no usable executed evidence.");
                        return (await github.ResultAsync(repo, run, nonce, token), nonce);
                    }
                    await Task.Delay(PollInterval, token);
                }
            }
            catch (IosWorkflowNotSentException ex)
            {
                Clear(); // Positive pre-POST proof, unlike a missing or failed dispatch response.
                if (ex.Cancelled && token.IsCancellationRequested) throw new RemoteCancelled(ex.Message, ex, token);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (outstanding is not null)
                {
                    var stopped = await CleanupAsync(repo, outstanding);
                    if (stopped) Clear();
                    var why = stopped ? "remote-cancelled: The correlated iOS workflow is terminal; no remote execution remains outstanding." : Uncertain().Message;
                    if (token.IsCancellationRequested) throw new RemoteCancelled(why, ex, token);
                    if (!stopped) throw Uncertain();
                }
                throw;
            }
        }
        finally { repositoryLock?.Dispose(); invocation.Release(); }

        void Clear()
        {
            // Remove durable state first. A failed removal must leave the in-memory barrier in place too.
            try { if (journal is not null) File.Delete(journal); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new RemoteFailure("remote-journal-cleanup-failed", "The iOS dispatch journal could not be cleared. No replacement is authorized; repair the owner-side ios-dispatches journal before retrying.");
            }
            outstanding = null;
        }
        RemoteFailure Uncertain() => new("remote-cancel-uncertain",
            "The prior iOS dispatch (factory-ios-" + outstanding?.Nonce + ") could not be confirmed terminal (its correlation may not yet be visible). No replacement will be dispatched. "
            + "Check the factory iOS runs in GitHub Actions and retry to reconcile; remove the owner-side ios-dispatches journal only after confirming that dispatch cannot still run.");
    }

    private IosWorkflowRun? Correlated(IReadOnlyList<IosWorkflowRun> runs, Outstanding pending)
    {
        var matches = runs.Where(r => r.Title == "factory-ios-" + pending.Nonce).ToArray();
        if (matches.Length > 1) throw new RemoteFailure("remote-correlation-ambiguous", "Multiple iOS runs match the dispatch correlation; inspect GitHub Actions before retrying.");
        var run = matches.SingleOrDefault();
        if (run is not null && (run.Id <= 0 || run.Event != "workflow_dispatch" || run.HeadSha != pending.Commit
            || run.Path != (gui is null ? RemoteIosWorkflow.Path : RemoteGuiWorkflow.Path) || run.Attempt != 1))
            throw new RemoteFailure("remote-provenance-invalid", "The correlated iOS run does not match the sealed default-branch dispatch; no result or cancellation authorized.");
        return run;
    }

    private async Task<bool> CleanupAsync(RepoRef repo, Outstanding pending)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        long? cancelled = null;
        try
        {
            while (true)
            {
                cleanup.Token.ThrowIfCancellationRequested();
                var run = Correlated(await github.RunsAsync(repo, cleanup.Token), pending);
                if (run?.Status == "completed") return true;
                if (run is not null && cancelled != run.Id)
                {
                    await github.CancelAsync(repo, run, cleanup.Token);
                    cancelled = run.Id;
                }
                // Cleanup has its own small bounded window, independent of Pause/Stop/deadline cancellation.
                await Task.Delay(PollInterval > TimeSpan.Zero ? PollInterval : TimeSpan.FromMilliseconds(1), cleanup.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    // The hosted runner selects these explicit simulator identifiers; never accept an available/latest fallback.
    public const string SimulatorRuntime = "com.apple.CoreSimulator.SimRuntime.iOS-18-5";
    public const string SimulatorDeviceType = "com.apple.CoreSimulator.SimDeviceType.iPhone-16";

    internal sealed record EnvironmentIdentity(
        [property: JsonPropertyName("runtime")] string Runtime,
        [property: JsonPropertyName("deviceType")] string DeviceType,
        [property: JsonPropertyName("xcode")] string Xcode,
        [property: JsonPropertyName("build")] string Build);

    internal static EnvironmentIdentity EnvironmentOf(JsonElement root, bool gui = false)
    {
        var environment = root.GetProperty("environment");
        var identity = new EnvironmentIdentity(environment.GetProperty("runtime").GetString()!,
            environment.GetProperty("deviceType").GetString()!, environment.GetProperty("xcode").GetString()!,
            environment.GetProperty("build").GetString()!);
        if ((!gui && (identity.Runtime != SimulatorRuntime || identity.DeviceType != SimulatorDeviceType))
            || string.IsNullOrWhiteSpace(identity.Xcode) || string.IsNullOrWhiteSpace(identity.Build))
            throw new InvalidDataException("Unproven iOS environment identity.");
        return identity;
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
        EnvironmentOf(root);
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
