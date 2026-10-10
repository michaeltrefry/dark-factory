using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A file of the repository at a commit, read owner-side for a reviewer's <c>read_file</c> (sc-25705).</summary>
public interface IReviewFiles
{
    /// <summary>
    /// The text of <paramref name="path"/> at <paramref name="sha"/>, or null when no such file exists there. Throws
    /// <see cref="BinaryFileException"/> for a file that is not UTF-8 text.
    /// </summary>
    Task<string?> ReadFileAsync(RepoRef repo, string sha, string path, CancellationToken ct);
}

/// <summary>The file asked for is binary (not valid UTF-8, or holding a NUL byte): a reviewer is not shown it.</summary>
public sealed class BinaryFileException(string path) : Exception($"{path} is a binary file.");

/// <summary>
/// CodeGraph's answer to one tool call: its text, whether CodeGraph said it is an error, and the commit of the default branch
/// its index describes (E3), null when CodeGraph did not say.
/// </summary>
public sealed record CodeGraphAnswer(string Text, bool IsError, string? Commit);

/// <summary>The hosted CodeGraph, owner-side (its token never reaches a worker or a prompt, E5).</summary>
public interface ICodeGraph
{
    /// <summary>
    /// The CodeGraph project that indexes <paramref name="repo"/> (its listed GitHub URL is exactly the repository's), or null when
    /// none does. Throws when CodeGraph cannot be reached or asked.
    /// </summary>
    Task<string?> FindProjectAsync(RepoRef repo, CancellationToken ct);

    /// <summary>
    /// CodeGraph's <c>analyze_impact</c>: the blast radius of changing <paramref name="name"/> in <paramref name="project"/>, at most
    /// <paramref name="depth"/> levels deep (CodeGraph's default when null). Throws when CodeGraph cannot be reached or asked.
    /// </summary>
    Task<CodeGraphAnswer> AnalyzeImpactAsync(string name, int? depth, string project, CancellationToken ct);
}

/// <summary>
/// One review or second-opinion session's tool state: the repository and head it reads, its budget of result characters
/// (<see cref="ReviewTools.Budget"/>) and what it has used, and the CodeGraph project once resolved.
/// </summary>
public sealed class ReviewToolSession(RepoRef repo, string headSha, int budget)
{
    public RepoRef Repo { get; } = repo;
    public string HeadSha { get; } = headSha;

    /// <summary>The most characters of tool results this session sends the model, all calls together.</summary>
    public int Budget { get; } = budget;

    /// <summary>The characters of tool results sent so far.</summary>
    public int Used { get; internal set; }

    public int Remaining => Budget - Used;

    internal bool ProjectResolved { get; set; }
    internal string? Project { get; set; }
}

/// <summary>What one tool call gave the model (<see cref="Content"/>, before fencing) and how the verdict records it.</summary>
public sealed record ToolOutcome(string Content, bool IsError, ToolCall Record);

/// <summary>
/// The tools a review or second-opinion session may call (sc-25705), all run by the orchestrator, owner-side (E1): the model
/// asks, the factory reads. <c>read_file</c> reads one repository path at the PR's head commit (<see cref="IReviewFiles"/>, the
/// gate's read-only GitHub access); <c>analyze_impact</c> asks CodeGraph (<see cref="ICodeGraph"/>), whose index is of the
/// default branch, so every answer is labelled with the commit it describes, or <see cref="UnknownCommit"/>, and "not the PR
/// head" (E3). It always asks about the PR's own repository: the project is the CodeGraph project whose GitHub URL is the
/// repository's (<see cref="ICodeGraph.FindProjectAsync"/>, resolved once per session), never one the model names. A tool that
/// cannot answer — no such file, a binary file, a repository CodeGraph does not index, CodeGraph unreachable or not configured —
/// returns an error result the model sees and the verdict records; it never fails the review by itself. Each result is cut at
/// <see cref="MaxResultChars"/> and at what is left of the session's budget (<see cref="Budget"/>); a call once the budget is
/// spent is not run and answers an error.
/// </summary>
public sealed class ReviewTools(IReviewFiles? files, ICodeGraph? codeGraph)
{
    public const string ReadFile = "read_file";
    public const string AnalyzeImpact = "analyze_impact";

    /// <summary>At most this many characters of one tool's result go to the model (the result says when it was cut).</summary>
    public const int MaxResultChars = 60_000;

    /// <summary>
    /// The characters one session's prompt and tool results may take together: the session's tool results get this less its
    /// prompt (story, file list and diff), but at least <see cref="MinSessionBudget"/>. Every turn resends the whole
    /// conversation, so an unbounded one would outgrow what the router accepts.
    /// </summary>
    public const int MaxSessionChars = 200_000;

    /// <summary>The smallest budget of tool-result characters a session gets, however long its prompt.</summary>
    public const int MinSessionBudget = 8_000;

    /// <summary>The tool-result budget of a session whose prompt is <paramref name="promptChars"/> characters.</summary>
    public static int Budget(int promptChars) => Math.Max(MinSessionBudget, MaxSessionChars - promptChars);

    /// <summary>The error a CodeGraph lookup answers for a repository CodeGraph does not index.</summary>
    public const string NotIndexed = "repository not indexed in CodeGraph";

    /// <summary>The error a read_file answers for a binary file.</summary>
    public const string BinaryNotShown = "binary file, not shown";

    /// <summary>A new session's tool state.</summary>
    public static ReviewToolSession Session(RepoRef repo, string headSha, int promptChars) => new(repo, headSha, Budget(promptChars));

    /// <summary>At most this many characters of a call's arguments are recorded in the verdict.</summary>
    public const int MaxRecordedArguments = 1_000;

    /// <summary>The commit a CodeGraph answer is recorded under when CodeGraph did not say which commit its index describes.</summary>
    public const string UnknownCommit = "unknown";

    /// <summary>The tools as the Messages API request lists them.</summary>
    public static readonly object[] Definitions =
    [
        new
        {
            name = ReadFile,
            description = "Read one file of the repository at the pull request's head commit. Use it to check a claim against code "
                + "the diff does not show. The answer is the file's text (cut when very long).",
            input_schema = new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "The file's path from the repository root, e.g. src/App/Program.cs." } },
                required = new[] { "path" },
            },
        },
        new
        {
            name = AnalyzeImpact,
            description = "Ask CodeGraph what depends on a code element (its blast radius, by risk). CodeGraph indexes the repository's "
                + "default branch, not this pull request: each answer names the commit it describes. It always answers about this repository.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    name = new { type = "string", description = "Qualified name or name of the code element, e.g. OrderService.CreateOrder." },
                    depth = new { type = "integer", description = "Max traversal depth, 1 to 5 (default 3)." },
                },
                required = new[] { "name" },
            },
        },
    ];

    /// <summary>
    /// Runs one tool_use block of <paramref name="session"/>, its result bounded by what is left of the session's budget, and charges
    /// the result to it. A call once the budget is spent is not run: it answers an error.
    /// </summary>
    public async Task<ToolOutcome> RunAsync(AnswerBlock use, ReviewToolSession session, CancellationToken ct)
    {
        var outcome = session.Remaining <= 0
            ? Error(use.Name ?? "(no tool named)", Cut(use.InputJson ?? "", MaxRecordedArguments),
                $"Not run: this session has used its budget of {session.Budget} characters of tool results. Answer from what you have.")
            : await RunCallAsync(use, session, Math.Min(MaxResultChars, session.Remaining), ct);
        session.Used += outcome.Content.Length;
        return outcome;
    }

    private async Task<ToolOutcome> RunCallAsync(AnswerBlock use, ReviewToolSession session, int limit, CancellationToken ct)
    {
        var name = use.Name ?? "(no tool named)";
        var arguments = Cut(use.InputJson ?? "", MaxRecordedArguments);
        JsonElement input;
        try
        {
            using var doc = JsonDocument.Parse(use.InputJson ?? "");
            input = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(name, arguments, "The tool input is not JSON.");
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            return Error(name, arguments, "The tool input is not a JSON object.");
        }
        return name switch
        {
            ReadFile => await ReadFileAsync(input, arguments, session.Repo, session.HeadSha, limit, ct),
            AnalyzeImpact => await AnalyzeImpactAsync(input, arguments, session, limit, ct),
            _ => Error(name, arguments, $"There is no tool named '{Cut(name, 100)}'; the tools are {ReadFile} and {AnalyzeImpact}."),
        };
    }

    /// <summary>A call past the session's call cap: answered (every tool_use needs its result) with an error, and recorded.</summary>
    public static ToolOutcome OverBudget(AnswerBlock use, int budget) =>
        Error(use.Name ?? "(no tool named)", Cut(use.InputJson ?? "", MaxRecordedArguments),
            $"Not run: this session has used its {budget} tool calls. Answer from what you have.");

    private async Task<ToolOutcome> ReadFileAsync(JsonElement input, string arguments, RepoRef repo, string headSha, int limit, CancellationToken ct)
    {
        if (Str(input, "path") is not { } asked || RepoPath.Normalize(asked) is not { } path)
        {
            return Error(ReadFile, arguments, "read_file needs a 'path' relative to the repository root, inside it.");
        }
        if (files is null)
        {
            return Error(ReadFile, arguments, "read_file is not available in this session.");
        }
        string? text;
        try
        {
            text = await files.ReadFileAsync(repo, headSha, path, ct);
        }
        catch (BinaryFileException)
        {
            return Error(ReadFile, arguments, $"{path} at {headSha}: {BinaryNotShown}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Error(ReadFile, arguments, $"{path} could not be read at {headSha}: {Cut(ex.Message, 500)}");
        }
        if (text is null)
        {
            return Error(ReadFile, arguments, $"There is no file {path} at {headSha}.");
        }
        return new ToolOutcome($"{path} at {headSha}:\n{Bounded(text, limit)}", false, new ToolCall(ReadFile, arguments, Sha256(text)));
    }

    private async Task<ToolOutcome> AnalyzeImpactAsync(JsonElement input, string arguments, ReviewToolSession session, int limit, CancellationToken ct)
    {
        if (Str(input, "name") is not { Length: > 0 } element)
        {
            return Error(AnalyzeImpact, arguments, "analyze_impact needs the 'name' of a code element.");
        }
        int? depth = input.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 5) : null;
        if (codeGraph is null)
        {
            return Error(AnalyzeImpact, arguments, "CodeGraph is not configured for this factory (no CodeGraph token); analyze_impact cannot answer.");
        }
        var headSha = session.HeadSha;
        CodeGraphAnswer answer;
        string project;
        try
        {
            if (!session.ProjectResolved)
            {
                session.Project = await codeGraph.FindProjectAsync(session.Repo, ct);
                session.ProjectResolved = true;
            }
            if (session.Project is not { } found)
            {
                return Error(AnalyzeImpact, arguments, $"{session.Repo.FullName}: {NotIndexed}; analyze_impact cannot answer.");
            }
            project = found;
            answer = await codeGraph.AnalyzeImpactAsync(element, depth, project, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Error(AnalyzeImpact, arguments, $"CodeGraph could not be reached or asked: {Cut(ex.Message, 500)}");
        }
        var commit = answer.Commit ?? UnknownCommit;
        var label = answer.Commit is { } sha
            ? $"CodeGraph's index of {project}'s default branch as of {sha}, not the PR head {headSha}"
            : $"CodeGraph's index of {project}'s default branch, commit unknown, not the PR head {headSha}";
        return new ToolOutcome($"{label}{(answer.IsError ? " (CodeGraph answered with an error)" : "")}:\n{Bounded(answer.Text, limit)}", answer.IsError,
            new ToolCall(AnalyzeImpact, arguments, Sha256(answer.Text), answer.IsError, commit));
    }

    private static ToolOutcome Error(string tool, string arguments, string why) =>
        new(why, true, new ToolCall(tool, arguments, Sha256(why), Error: true));

    private static string Bounded(string text, int limit) =>
        text.Length > limit ? $"{text[..limit]}\n[cut: {text.Length} characters, the first {limit} shown]" : text;

    /// <summary>The lowercase hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
