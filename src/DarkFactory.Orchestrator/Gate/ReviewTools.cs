using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>One line <c>grep</c> found: the file, its 1-based line number and the line's text.</summary>
public sealed record GrepMatch(string Path, int Line, string Text);

/// <summary>The first <paramref name="Items"/> of a bounded read, and whether there were more (the read stopped there).</summary>
public sealed record Bounded<T>(IReadOnlyList<T> Items, bool More);

/// <summary>
/// The repository at a commit, read owner-side for a reviewer's <c>read_file</c>, <c>list_files</c> and <c>grep</c> (sc-25705,
/// sc-25706): from the gate's clone, by object (no checkout, so no filter, hook or symlink of the PR's runs or is followed).
/// </summary>
public interface IReviewFiles
{
    /// <summary>
    /// The text of <paramref name="path"/> at <paramref name="sha"/>, or null when no regular file is there (none, a directory, a
    /// symlink, a submodule). Throws <see cref="BinaryFileException"/> for a file that is not UTF-8 text, and
    /// <see cref="InvalidOperationException"/> for one over <see cref="ReviewTools.MaxFileBytes"/>.
    /// </summary>
    Task<string?> ReadAsync(RepoRef repo, string sha, string path, CancellationToken ct);

    /// <summary>
    /// The file paths under <paramref name="directory"/> (null: the whole tree) at <paramref name="sha"/>, recursively: at most
    /// <see cref="ReviewTools.MaxListedFiles"/> (fewer when a byte bound stops the read first), <c>More</c> when there were others.
    /// </summary>
    Task<Bounded<string>> ListAsync(RepoRef repo, string sha, string? directory, CancellationToken ct);

    /// <summary>
    /// The lines of text files under <paramref name="directory"/> (null: the whole tree) at <paramref name="sha"/> that match the
    /// POSIX extended regular expression <paramref name="pattern"/>, at most <paramref name="perFile"/> per file and at most
    /// <see cref="ReviewTools.MaxGrepMatches"/> in all (fewer when a byte bound stops the read first), <c>More</c> when there were
    /// others. Throws when git refuses the pattern.
    /// </summary>
    Task<Bounded<GrepMatch>> GrepAsync(RepoRef repo, string sha, string pattern, string? directory, int perFile, CancellationToken ct);
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
    /// One of the reviewers' CodeGraph tools (<see cref="ReviewTools.CodeGraphTools"/>; any other name is refused, never sent) with
    /// <paramref name="arguments"/> as built by <see cref="ReviewTools"/> (the project always the PR repository's). Throws when
    /// CodeGraph cannot be reached or asked.
    /// </summary>
    Task<CodeGraphAnswer> CallAsync(string tool, JsonObject arguments, CancellationToken ct);
}

/// <summary>
/// One review or second-opinion session's tool state: the repository and the head and base commits it reads, its budget of result
/// characters (<see cref="ReviewTools.Budget"/>) and what it has used, and the CodeGraph project once resolved.
/// </summary>
public sealed class ReviewToolSession(RepoRef repo, string headSha, string baseSha, int budget)
{
    public RepoRef Repo { get; } = repo;
    public string HeadSha { get; } = headSha;
    public string BaseSha { get; } = baseSha;

    /// <summary>The most characters of tool results this session sends the model, all calls together.</summary>
    public int Budget { get; } = budget;

    /// <summary>The characters of tool results sent so far.</summary>
    public int Used { get; internal set; }

    public int Remaining => Budget - Used;

    internal bool ProjectResolved { get; set; }
    internal string? Project { get; set; }

    /// <summary>The Kanban tools this session was offered (<see cref="ReviewTools.DefinitionsAsync"/>); empty without Kanban.</summary>
    public IReadOnlyList<string> KanbanTools { get; internal set; } = [];
}

/// <summary>What one tool call gave the model (<see cref="Content"/>, before fencing) and how the verdict records it.</summary>
public sealed record ToolOutcome(string Content, bool IsError, ToolCall Record);

/// <summary>
/// The tools a review or second-opinion session may call (sc-25705, sc-25706), all run by the orchestrator, owner-side (E1): the
/// model asks, the factory reads. Every role and every second opinion is offered exactly <see cref="Names"/> (E2: a read-only
/// allowlist in code).
/// <list type="bullet">
/// <item>Repository tools (<see cref="IReviewFiles"/>, the gate's clone): <c>read_file</c>, <c>list_files</c> and <c>grep</c> at
/// the PR's head commit (default) or its base commit (<c>ref</c>). A path is normalised (<see cref="RepoPath"/>); one that would
/// leave the repository is refused before anything is read. Bounded: a file over <see cref="MaxFileBytes"/> is not read, a
/// listing shows at most <see cref="MaxListedFiles"/> paths, a grep pattern is at most <see cref="MaxPatternChars"/> characters
/// and shows at most <see cref="MaxGrepMatches"/> lines (<see cref="MaxGrepPerFile"/> per file, each cut at
/// <see cref="MaxGrepLineChars"/>).</item>
/// <item>CodeGraph tools (<see cref="CodeGraphTools"/>, <see cref="ICodeGraph"/>): graph reads only, never one that runs a model.
/// CodeGraph's index is of the default branch, so every answer is labelled with the commit it describes, or
/// <see cref="UnknownCommit"/>, and "not the PR head" (E3). Every call is asked about the PR's own repository: the project argument
/// is the CodeGraph project whose GitHub URL is the repository's (<see cref="ICodeGraph.FindProjectAsync"/>, resolved once per
/// session), never one the model names (no tool offers a project argument, and one sent anyway is ignored). The answers are not
/// filtered: <c>analyze_impact</c>, <c>trace_call_path</c>, <c>find_consumers</c> and <c>find_publishers</c> follow edges into
/// other indexed projects, so an answer may name nodes of other projects that depend on or call this one (the tools' note says
/// so).</item>
/// <item>Kanban tools (sc-25707, only with a Kanban upstream configured, <c>Kanban:Token</c>): the upstream's tools on its read-only
/// allowlist (<see cref="Mcp.McpServers.KanbanTools"/>), listed from the upstream once per session with their own input schemas
/// (<see cref="DefinitionsAsync"/>) and forwarded owner-side through the same upstream registry the loopback proxy uses. Kanban has
/// no repository mapping, so its calls are not repository-scoped. A Kanban write tool is on no allowlist: never offered or
/// forwarded.</item>
/// </list>
/// A tool name that is not on the list answers an error result and is never forwarded. A tool that cannot answer — no such file,
/// a binary file, a repository CodeGraph does not index, CodeGraph unreachable or not configured — returns an error result the
/// model sees and the verdict records; it never fails the review by itself. Each result is cut at <see cref="MaxResultBytes"/>
/// UTF-8 bytes and at what is left of the session's budget (<see cref="Budget"/>), with an explicit <c>[cut: …]</c> marker; a call
/// once the budget is spent is not run and answers an error.
/// </summary>
public sealed partial class ReviewTools(IReviewFiles? files, ICodeGraph? codeGraph, Mcp.McpUpstream? kanban = null, TextWriter? log = null)
{
    /// <summary>The longest JSON input a Kanban tool is sent.</summary>
    public const int MaxKanbanArgumentChars = 2_000;

    /// <summary>The longest input schema (serialized JSON) of a Kanban tool that is offered; a longer one leaves the tool out.</summary>
    public const int MaxKanbanSchemaChars = 4 * 1024;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _skippedSchemas = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a Kanban tool's <paramref name="schema"/> goes to the router as it is: a JSON object whose <c>type</c> is
    /// <c>"object"</c>, at most <see cref="MaxKanbanSchemaChars"/> characters serialized. Anything else could fail every review.
    /// </summary>
    public static bool UsableSchema(JsonObject? schema) =>
        schema is not null
        && schema["type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "object"
        && schema.ToJsonString().Length <= MaxKanbanSchemaChars;

    /// <summary>
    /// The tool definitions <paramref name="session"/> is offered: <see cref="Definitions"/>, then (with a Kanban upstream) each of its
    /// allowlisted tools the upstream lists, with its own description and input schema. A Kanban listing that fails offers no Kanban
    /// tool (the review goes on without them); a tool whose schema is not usable (<see cref="UsableSchema"/>) is left out (logged
    /// once per tool). The offered Kanban names are kept on the session: only those are forwarded.
    /// </summary>
    public async Task<IReadOnlyList<object>> DefinitionsAsync(ReviewToolSession session, CancellationToken ct)
    {
        if (kanban is null)
        {
            return Definitions;
        }
        IReadOnlyList<Mcp.McpToolInfo> listed;
        try
        {
            listed = await kanban.ListToolsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            listed = [];
        }
        var offered = new List<Mcp.McpToolInfo>();
        foreach (var tool in listed.Where(t => kanban.Allows(t.Name) && !Names.Contains(t.Name)))
        {
            if (UsableSchema(tool.InputSchema))
            {
                offered.Add(tool);
            }
            else if (_skippedSchemas.TryAdd(tool.Name, true))
            {
                log?.WriteLine($"[review] Kanban tool {tool.Name} left out: its input schema is not a JSON object of type \"object\" "
                    + $"within {MaxKanbanSchemaChars} characters");
            }
        }
        session.KanbanTools = offered.Select(t => t.Name).ToList();
        return
        [
            .. Definitions,
            .. offered.Select(t => (object)new
            {
                name = t.Name,
                description = $"Kanban board (read-only; not about this repository's code): {Cut(t.Description ?? t.Name, 1_000)}",
                input_schema = t.InputSchema,
            }),
        ];
    }

    public const string ReadFile = "read_file";
    public const string ListFiles = "list_files";
    public const string Grep = "grep";
    public const string AnalyzeImpact = "analyze_impact";
    public const string SearchGraph = "search_graph";
    public const string TraceCallPath = "trace_call_path";
    public const string FindConsumers = "find_consumers";
    public const string FindPublishers = "find_publishers";
    public const string GetCodeSnippet = "get_code_snippet";
    public const string ReadNodeSource = "read_node_source";

    /// <summary>The repository tools, read from the gate's clone.</summary>
    public static readonly IReadOnlyList<string> RepoTools = [ReadFile, ListFiles, Grep];

    /// <summary>
    /// The CodeGraph tools a reviewer may call: graph and source reads that run no model (E2: the owner spends no API credits on
    /// them). Each was checked against CodeGraph's source (michaeltrefry/CodeGraph main at 2b4c529,
    /// <c>src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs</c>): its handler reaches only the graph store
    /// (<c>IGraphStore</c>, Neo4j) and the indexed source files, never <c>IAnalysisModelProvider</c>, <c>GraphAssistant</c>, an
    /// embedding service or an Anthropic/OpenAI client.
    /// <list type="bullet">
    /// <item><c>analyze_impact</c>: <c>ImpactAnalysisService</c> (constructed with <c>IGraphStore</c> and a logger only) walks
    /// dependency edges in the graph.</item>
    /// <item><c>search_graph</c>: <c>GraphQueryEngine.SearchAsync</c> (<c>IGraphStore</c> + logger) → <c>SearchNodesAsync</c>, then
    /// file trust scores from the store. Chosen over <c>codegraph_search</c>, which only wraps this same handler in a JSON
    /// envelope.</item>
    /// <item><c>trace_call_path</c>: <c>GraphQueryEngine.TraceCallPathAsync</c>, a graph traversal.</item>
    /// <item><c>find_consumers</c>, <c>find_publishers</c>: <c>GraphQueryEngine.FindConsumersAsync</c> /
    /// <c>FindPublishersAsync</c>, graph queries.</item>
    /// <item><c>get_code_snippet</c>: <c>RepoFileResolver.ReadAllLinesAsync</c>, CodeGraph's own checkout of the indexed project
    /// (a file read, path-checked by CodeGraph too).</item>
    /// <item><c>read_node_source</c>: <c>store.FindNodeByIdAsync</c> then the same file read. A node id names a node of any
    /// project, so its answer is shown only when its header names the PR repository's project
    /// (<see cref="NodeProject"/>); otherwise the model is told it is not shown.</item>
    /// </list>
    /// Left out on purpose: anything LLM-backed (the assistant/Ask, <c>project_report</c>, <c>get_service_summary</c> and the
    /// analysis/review tools, whose text a model wrote or writes), <c>rag_search</c> and <c>search_conventions</c> (the convention
    /// embedding service, and conventions are not the PR's code), <c>trace_data_lineage</c>, cluster, health, architecture,
    /// schema, memory, queue and tracker tools (not about the code under review, or writes), and <c>search_projects</c>/
    /// <c>list_projects</c> (only the orchestrator resolves the project). <see cref="CodeGraph.CodeGraphMcpClient"/> refuses any
    /// tool not on this list before it sends anything.
    /// </summary>
    public static readonly IReadOnlyList<string> CodeGraphTools =
        [AnalyzeImpact, SearchGraph, TraceCallPath, FindConsumers, FindPublishers, GetCodeSnippet, ReadNodeSource];

    /// <summary>Every tool a session is offered, in the order the request lists them.</summary>
    public static readonly IReadOnlyList<string> Names = [.. RepoTools, .. CodeGraphTools];

    /// <summary>At most this many UTF-8 bytes of one tool's result go to the model (the result says when it was cut).</summary>
    public const int MaxResultBytes = 60_000;

    /// <summary>A file larger than this is not read at all (<c>read_file</c> answers an error).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>At most this many paths of a <c>list_files</c> are shown (the result gives the total).</summary>
    public const int MaxListedFiles = 2_000;

    /// <summary>The longest pattern <c>grep</c> runs.</summary>
    public const int MaxPatternChars = 200;

    /// <summary>At most this many matching lines of one <c>grep</c> are shown (the result says when there were more).</summary>
    public const int MaxGrepMatches = 200;

    /// <summary>At most this many matching lines of one file are read by one <c>grep</c>.</summary>
    public const int MaxGrepPerFile = 20;

    /// <summary>A matching line longer than this is cut.</summary>
    public const int MaxGrepLineChars = 300;

    /// <summary>The longest string argument sent to CodeGraph (a name, a pattern).</summary>
    public const int MaxCodeGraphArgumentChars = 300;

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
    public static ReviewToolSession Session(RepoRef repo, string headSha, string baseSha, int promptChars) =>
        new(repo, headSha, baseSha, Budget(promptChars));

    /// <summary>At most this many characters of a call's arguments are recorded in the verdict.</summary>
    public const int MaxRecordedArguments = 1_000;

    /// <summary>The commit a CodeGraph answer is recorded under when CodeGraph did not say which commit its index describes.</summary>
    public const string UnknownCommit = "unknown";

    private const string RefHead = "head";
    private const string RefBase = "base";

    private static object RefProperty => new
    {
        type = "string",
        @enum = new[] { RefHead, RefBase },
        description = "Which commit: head (the pull request's head, default) or base (the commit the diff is against).",
    };

    private static object Str(string description) => new { type = "string", description };
    private static object Int(string description) => new { type = "integer", description };

    private const string CodeGraphNote = "CodeGraph indexes the repository's default branch, not this pull request: each answer names the "
        + "commit it describes. Queries are asked about this repository; answers may name nodes of other indexed projects that "
        + "depend on or call it.";

    /// <summary>The tools as the Messages API request lists them, one per <see cref="Names"/> entry, in that order.</summary>
    public static readonly object[] Definitions =
    [
        new
        {
            name = ReadFile,
            description = "Read one file of the repository at the pull request's head commit (or its base). Use it before claiming "
                + "anything about code the diff does not show. The answer is the file's text (cut when very long).",
            input_schema = new
            {
                type = "object",
                properties = new { path = Str("The file's path from the repository root, e.g. src/App/Program.cs."), @ref = RefProperty },
                required = new[] { "path" },
            },
        },
        new
        {
            name = ListFiles,
            description = $"List the files of the repository under a directory (recursively) at the head commit (or its base); at most {MaxListedFiles} paths.",
            input_schema = new
            {
                type = "object",
                properties = new { path = Str("A directory from the repository root, e.g. src/App; omit for the whole repository."), @ref = RefProperty },
            },
        },
        new
        {
            name = Grep,
            description = $"Search the repository's text files at the head commit (or its base) for lines matching a POSIX extended regular "
                + $"expression; answers path:line: text, at most {MaxGrepMatches} lines ({MaxGrepPerFile} per file).",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    pattern = Str($"The regular expression (POSIX extended, at most {MaxPatternChars} characters)."),
                    path = Str("A directory or file to search under, from the repository root; omit for the whole repository."),
                    @ref = RefProperty,
                },
                required = new[] { "pattern" },
            },
        },
        new
        {
            name = AnalyzeImpact,
            description = $"Ask CodeGraph what depends on a code element (its blast radius, by risk). {CodeGraphNote}",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    name = Str("Qualified name or name of the code element, e.g. OrderService.CreateOrder."),
                    depth = Int("Max traversal depth, 1 to 5 (default 3)."),
                },
                required = new[] { "name" },
            },
        },
        new
        {
            name = SearchGraph,
            description = $"Search CodeGraph for code elements (classes, methods, routes, events, ...) by name pattern. {CodeGraphNote}",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    namePattern = Str("Name pattern to search for (% is a wildcard)."),
                    label = Str("Optional node type: Class, Method, Route, Service, Event, Queue, Table, Interface, ..."),
                    limit = Int("Max results, 1 to 50 (default 20)."),
                },
                required = new[] { "namePattern" },
            },
        },
        new
        {
            name = TraceCallPath,
            description = $"Ask CodeGraph for the callers (inbound) or callees (outbound) of a function or method. {CodeGraphNote}",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    functionName = Str("Function or method name to trace."),
                    direction = new { type = "string", @enum = new[] { "inbound", "outbound", "both" }, description = "Default both." },
                    depth = Int("Levels to trace, 1 to 5 (default 3)."),
                },
                required = new[] { "functionName" },
            },
        },
        new
        {
            name = FindConsumers,
            description = $"Ask CodeGraph which services and methods consume an event, endpoint or model. {CodeGraphNote}",
            input_schema = new { type = "object", properties = new { name = Str("Event, endpoint or model name.") }, required = new[] { "name" } },
        },
        new
        {
            name = FindPublishers,
            description = $"Ask CodeGraph which services publish to a queue, exchange or event type. {CodeGraphNote}",
            input_schema = new { type = "object", properties = new { name = Str("Queue, exchange or event name.") }, required = new[] { "name" } },
        },
        new
        {
            name = GetCodeSnippet,
            description = $"Read lines of a file from CodeGraph's copy of the default branch (use read_file for the pull request's own code). {CodeGraphNote}",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    filePath = Str("The file's path from the repository root."),
                    startLine = Int("First line, 1-based (0 or omitted: the beginning)."),
                    endLine = Int("Last line (0 or omitted: the end)."),
                },
                required = new[] { "filePath" },
            },
        },
        new
        {
            name = ReadNodeSource,
            description = $"Read the source of a CodeGraph node by its id (only nodes of this repository are shown). {CodeGraphNote}",
            input_schema = new { type = "object", properties = new { nodeId = Int("The node's id in CodeGraph.") }, required = new[] { "nodeId" } },
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
            : await RunCallAsync(use, session, session.Remaining, ct);
        session.Used += outcome.Content.Length;
        return outcome;
    }

    private async Task<ToolOutcome> RunCallAsync(AnswerBlock use, ReviewToolSession session, int maxChars, CancellationToken ct)
    {
        var name = use.Name ?? "(no tool named)";
        var arguments = Cut(use.InputJson ?? "", MaxRecordedArguments);
        var kanbanTool = kanban is not null && session.KanbanTools.Contains(name) && kanban.Allows(name);
        if (!Names.Contains(name) && !kanbanTool)
        {
            // Off the allowlist (E2): answered here, never forwarded to CodeGraph, Kanban or anything else.
            return Error(name, arguments,
                $"There is no tool named '{Cut(name, 100)}'; the tools are {string.Join(", ", [.. Names, .. session.KanbanTools])}.");
        }
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
        var call = new Call(name, arguments, input, session, maxChars);
        if (kanbanTool)
        {
            return await KanbanAsync(call, use.InputJson ?? "{}", ct);
        }
        return name switch
        {
            ReadFile => await ReadFileAsync(call, ct),
            ListFiles => await ListFilesAsync(call, ct),
            Grep => await GrepAsync(call, ct),
            _ => await CodeGraphAsync(call, ct),
        };
    }

    /// <summary>One call being run: the tool, its recorded arguments, its input, the session and the most characters it may answer.</summary>
    private sealed record Call(string Tool, string Arguments, JsonElement Input, ReviewToolSession Session, int MaxChars)
    {
        public ToolOutcome Fail(string why) => Error(Tool, Arguments, why);

        public ToolOutcome Answer(string header, string body, string hashed, bool isError = false, string? commit = null) =>
            new(body.Length == 0 ? header : $"{header}\n{Bounded(body, MaxChars - header.Length - 1)}", isError,
                new ToolCall(Tool, Arguments, Sha256(hashed), isError, commit));
    }

    /// <summary>A call past the session's call cap: answered (every tool_use needs its result) with an error, and recorded.</summary>
    public static ToolOutcome OverBudget(AnswerBlock use, int budget) =>
        Error(use.Name ?? "(no tool named)", Cut(use.InputJson ?? "", MaxRecordedArguments),
            $"Not run: this session has used its {budget} tool calls. Answer from what you have.");

    /// <summary>A call the model asked for in its last turn with tools: not run, since the next turn is for its final answer.</summary>
    public static ToolOutcome AtTurnCap(AnswerBlock use, int turns) =>
        Error(use.Name ?? "(no tool named)", Cut(use.InputJson ?? "", MaxRecordedArguments),
            $"Not run: this session has used its {turns} turns with tools. Give your final answer now.");

    /// <summary>The commit a repository tool reads (<c>ref</c>: head, the default, or base), or null when <c>ref</c> is anything else.</summary>
    private static string? Commit(Call call) => Text(call.Input, "ref") switch
    {
        null or RefHead => call.Session.HeadSha,
        RefBase => call.Session.BaseSha,
        _ => null,
    };

    /// <summary>
    /// A repository path argument: null and empty (or <c>.</c>) are the root when <paramref name="optional"/>; otherwise the
    /// normalised path, or <c>Ok: false</c> when it would leave the repository (<see cref="RepoPath.Normalize"/>).
    /// </summary>
    private static (bool Ok, string? Path) PathArgument(Call call, bool optional)
    {
        var asked = Text(call.Input, "path");
        if (optional && (asked is null || asked.Trim() is "" or "." or "./" or "/"))
        {
            return (true, null);
        }
        return asked is not null && RepoPath.Normalize(asked) is { } path ? (true, path) : (false, null);
    }

    private async Task<ToolOutcome> ReadFileAsync(Call call, CancellationToken ct)
    {
        if (PathArgument(call, optional: false) is not (true, { } path))
        {
            return call.Fail("read_file needs a 'path' relative to the repository root, inside it.");
        }
        if (Commit(call) is not { } sha)
        {
            return call.Fail("'ref' is head or base.");
        }
        if (files is null)
        {
            return call.Fail("read_file is not available in this session.");
        }
        string? text;
        try
        {
            text = await files.ReadAsync(call.Session.Repo, sha, path, ct);
        }
        catch (BinaryFileException)
        {
            return call.Fail($"{path} at {sha}: {BinaryNotShown}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return call.Fail($"{path} could not be read at {sha}: {Cut(ex.Message, 500)}");
        }
        return text is null ? call.Fail($"There is no file {path} at {sha}.") : call.Answer($"{path} at {sha}:", text, text);
    }

    private async Task<ToolOutcome> ListFilesAsync(Call call, CancellationToken ct)
    {
        if (PathArgument(call, optional: true) is not (true, var directory))
        {
            return call.Fail("list_files takes a 'path' relative to the repository root, inside it.");
        }
        if (Commit(call) is not { } sha)
        {
            return call.Fail("'ref' is head or base.");
        }
        if (files is null)
        {
            return call.Fail("list_files is not available in this session.");
        }
        Bounded<string> paths;
        try
        {
            paths = await files.ListAsync(call.Session.Repo, sha, directory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return call.Fail($"The files under {directory ?? "the root"} could not be listed at {sha}: {Cut(ex.Message, 500)}");
        }
        var listed = paths.Items.Take(MaxListedFiles).ToList();
        var listing = string.Join('\n', listed);
        var header = paths.More || paths.Items.Count > MaxListedFiles
            ? $"More than {listed.Count} files under {directory ?? "the repository root"} at {sha} (the first {listed.Count} shown):"
            : $"{listed.Count} files under {directory ?? "the repository root"} at {sha}:";
        return call.Answer(header, listing, listing);
    }

    private async Task<ToolOutcome> GrepAsync(Call call, CancellationToken ct)
    {
        if (Text(call.Input, "pattern") is not { Length: > 0 } pattern)
        {
            return call.Fail("grep needs a 'pattern'.");
        }
        if (pattern.Length > MaxPatternChars || pattern.Contains('\0') || pattern.Contains('\n'))
        {
            return call.Fail($"grep's pattern is one line of at most {MaxPatternChars} characters.");
        }
        if (PathArgument(call, optional: true) is not (true, var directory))
        {
            return call.Fail("grep takes a 'path' relative to the repository root, inside it.");
        }
        if (Commit(call) is not { } sha)
        {
            return call.Fail("'ref' is head or base.");
        }
        if (files is null)
        {
            return call.Fail("grep is not available in this session.");
        }
        Bounded<GrepMatch> matches;
        try
        {
            matches = await files.GrepAsync(call.Session.Repo, sha, pattern, directory, MaxGrepPerFile, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return call.Fail($"grep could not search at {sha}: {Cut(ex.Message, 500)}");
        }
        var found = matches.Items.Take(MaxGrepMatches).ToList();
        var shown = string.Join('\n', found.Select(m => $"{m.Path}:{m.Line}: {Cut(m.Text, MaxGrepLineChars)}"));
        var header = matches.More || matches.Items.Count > MaxGrepMatches
            ? $"More than {found.Count} matching lines under {directory ?? "the repository root"} at {sha} (at most {MaxGrepPerFile} per file; "
                + $"the first {found.Count} shown):"
            : found.Count == 0
                ? $"No line matches under {directory ?? "the repository root"} at {sha}."
                : $"{found.Count} matching lines under {directory ?? "the repository root"} at {sha} (at most {MaxGrepPerFile} per file):";
        return call.Answer(header, shown, shown);
    }

    /// <summary>
    /// A CodeGraph tool: its arguments built from the model's input (bounded, the project always the PR repository's) and the answer
    /// labelled with the commit it describes.
    /// </summary>
    private async Task<ToolOutcome> CodeGraphAsync(Call call, CancellationToken ct)
    {
        if (CodeGraphArguments(call.Tool, call.Input) is not { } arguments)
        {
            return call.Fail(CodeGraphUsage(call.Tool));
        }
        if (codeGraph is null)
        {
            return call.Fail($"CodeGraph is not configured for this factory (no CodeGraph token); {call.Tool} cannot answer.");
        }
        var session = call.Session;
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
                return call.Fail($"{session.Repo.FullName}: {NotIndexed}; {call.Tool} cannot answer.");
            }
            project = found;
            if (call.Tool != ReadNodeSource)
            {
                arguments["project"] = project;
            }
            answer = await codeGraph.CallAsync(call.Tool, arguments, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return call.Fail($"CodeGraph could not be reached or asked: {Cut(ex.Message, 500)}");
        }
        if (call.Tool == ReadNodeSource && !answer.IsError && NodeProject(answer.Text) != project)
        {
            // A node id can name a node of any project: only one of the PR repository's is shown (the answer is not passed on).
            return call.Fail($"Node {arguments["nodeId"]} is not a node of {project} with readable source; not shown.");
        }
        var commit = answer.Commit ?? UnknownCommit;
        var label = answer.Commit is { } sha
            ? $"CodeGraph's index of {project}'s default branch as of {sha}, not the PR head {session.HeadSha}"
            : $"CodeGraph's index of {project}'s default branch, commit unknown, not the PR head {session.HeadSha}";
        return call.Answer($"{label}{(answer.IsError ? " (CodeGraph answered with an error)" : "")}:", answer.Text, answer.Text, answer.IsError, commit);
    }

    /// <summary>
    /// A Kanban tool (on the allowlist and offered to this session): the model's input object forwarded as it is (at most
    /// <see cref="MaxKanbanArgumentChars"/> characters of JSON) through the upstream registry's Kanban client. Not repository-scoped.
    /// </summary>
    private async Task<ToolOutcome> KanbanAsync(Call call, string inputJson, CancellationToken ct)
    {
        if (inputJson.Length > MaxKanbanArgumentChars || JsonNode.Parse(inputJson) is not JsonObject arguments)
        {
            return call.Fail($"{call.Tool} takes a JSON object of at most {MaxKanbanArgumentChars} characters.");
        }
        Mcp.McpToolResult answer;
        try
        {
            answer = await kanban!.CallAsync(call.Tool, arguments, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return call.Fail($"Kanban could not be reached or asked: {Cut(ex.Message, 500)}");
        }
        return call.Answer($"Kanban's {call.Tool}{(answer.IsError ? " (Kanban answered with an error)" : "")}:", answer.Text, answer.Text, answer.IsError);
    }

    /// <summary>
    /// The arguments CodeGraph is sent for <paramref name="call"/> (without the project, which only the orchestrator sets), or null
    /// when a required one is missing or malformed. Strings are cut at <see cref="MaxCodeGraphArgumentChars"/>; numbers clamped.
    /// </summary>
    public static JsonObject? CodeGraphArguments(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? Required(string name) => Text(input, name) is { } s && s.Trim().Length > 0 ? Cut(s.Trim(), MaxCodeGraphArgumentChars) : null;
        int? Number(string name, int min, int max) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : null;
        JsonObject With(JsonObject o, string name, int? value)
        {
            if (value is { } v)
            {
                o[name] = v;
            }
            return o;
        }
        switch (tool)
        {
            case AnalyzeImpact:
                return Required("name") is { } element ? With(new JsonObject { ["name"] = element }, "depth", Number("depth", 1, 5)) : null;
            case SearchGraph:
                if (Required("namePattern") is not { } pattern)
                {
                    return null;
                }
                var search = With(new JsonObject { ["namePattern"] = pattern }, "limit", Number("limit", 1, 50));
                if (Required("label") is { } label)
                {
                    search["label"] = label;
                }
                return search;
            case TraceCallPath:
                if (Required("functionName") is not { } function)
                {
                    return null;
                }
                var direction = Text(input, "direction") ?? "both";
                return direction is "inbound" or "outbound" or "both"
                    ? With(new JsonObject { ["functionName"] = function, ["direction"] = direction }, "depth", Number("depth", 1, 5))
                    : null;
            case FindConsumers or FindPublishers:
                return Required("name") is { } target ? new JsonObject { ["name"] = target } : null;
            case GetCodeSnippet:
                if (Text(input, "filePath") is not { } asked || RepoPath.Normalize(asked) is not { } file)
                {
                    return null;
                }
                return With(With(new JsonObject { ["filePath"] = file }, "startLine", Number("startLine", 0, int.MaxValue)),
                    "endLine", Number("endLine", 0, int.MaxValue));
            case ReadNodeSource:
                return input.TryGetProperty("nodeId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var nodeId) && nodeId >= 0
                    ? new JsonObject { ["nodeId"] = nodeId }
                    : null;
            default:
                return null;
        }
    }

    public static string CodeGraphUsage(string tool) => tool switch
    {
        AnalyzeImpact => "analyze_impact needs the 'name' of a code element.",
        SearchGraph => "search_graph needs a 'namePattern'.",
        TraceCallPath => "trace_call_path needs a 'functionName'; 'direction' is inbound, outbound or both.",
        FindConsumers or FindPublishers => $"{tool} needs a 'name'.",
        GetCodeSnippet => "get_code_snippet needs a 'filePath' relative to the repository root, inside it.",
        _ => "read_node_source needs a numeric 'nodeId'.",
    };

    [GeneratedRegex(@"\A## .+ — (?<project>[^\r\n]+?)[ \t]*\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex NodeHeader();

    [GeneratedRegex(@"\ACommit: [0-9a-fA-F]{40}[ \t]*\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex CommitLine();

    /// <summary>
    /// The project <c>read_node_source</c>'s answer says the node belongs to: its header line <c>## &lt;name&gt; (&lt;label&gt;) — &lt;project&gt;</c>
    /// (after a <c>Commit:</c> line, when CodeGraph gives one), or null when the answer has no such header (a node not found, one
    /// without a source file).
    /// </summary>
    public static string? NodeProject(string text)
    {
        var body = CommitLine().Match(text) is { Success: true } commit ? text[commit.Length..] : text;
        return NodeHeader().Match(body) is { Success: true } m ? m.Groups["project"].Value : null;
    }

    private static ToolOutcome Error(string tool, string arguments, string why) =>
        new(why, true, new ToolCall(tool, arguments, Sha256(why), Error: true));

    /// <summary>
    /// <paramref name="text"/> cut, with an explicit marker, to at most <see cref="MaxResultBytes"/> UTF-8 bytes and
    /// <paramref name="maxChars"/> characters (what is left of the session's budget), never splitting a surrogate pair.
    /// </summary>
    public static string Bounded(string text, int maxChars)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= MaxResultBytes && text.Length <= maxChars)
        {
            return text;
        }
        var limit = Math.Max(0, Math.Min(text.Length, maxChars));
        var (keep, used) = (0, 0);
        while (keep < limit)
        {
            // One character at a time (a surrogate pair together), while it fits both caps.
            var width = char.IsHighSurrogate(text[keep]) && keep + 1 < text.Length && char.IsLowSurrogate(text[keep + 1]) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(keep, width));
            if (keep + width > limit || used + size > MaxResultBytes)
            {
                break;
            }
            (keep, used) = (keep + width, used + size);
        }
        return $"{text[..keep]}\n[cut: {bytes} bytes, the first {used} bytes shown]";
    }

    /// <summary>The lowercase hex SHA-256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
