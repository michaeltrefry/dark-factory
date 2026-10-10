using System.Text.Json;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>What the orchestrator hands a worker session, for the taint rule (<see cref="Taint.Of(WorkerInput)"/>).</summary>
public enum WorkerInput
{
    /// <summary>A GitHub issue's own title and body (the triage worker's prompt).</summary>
    IssueText,

    /// <summary>A PR or issue comment by someone who is not a collaborator of the repo.</summary>
    OutsiderComment,

    /// <summary>A Shortcut story from the watch scope (the owner's board).</summary>
    Story,

    /// <summary>An approved triage's summary and proposed fix (what an issue item's implementer sees instead of the issue).</summary>
    TriageSummary,

    /// <summary>The review panel's confirmed blocking findings (a review fixer).</summary>
    ReviewFindings,

    /// <summary>Excerpts of a failing CI job's log (a CI fixer).</summary>
    CiLog,
}

/// <summary>
/// The taint rule (E4, Rule of Two): a worker session that has read untrusted content — an issue's text, an outsider's comment,
/// a web page — is tainted, and a tainted session never gets a push token, for the rest of its life (also on resume and after a
/// crash). The taint is a ledger row keyed by the Claude session id (<see cref="Ledger.SessionTaint"/>), written by the
/// orchestrator before the session's id is checkpointed (input handed over) or before the next stream line is read (a web tool
/// used); nothing clears it. Every push of a worker's work needs a <see cref="PushGrant"/>, which
/// <see cref="Ledger.WorkLedger.GrantPushAsync"/> issues only for sessions with no taint row.
/// <para>
/// Deliberate decision on CI logs: they do not taint. They are the output of the repo's own CI running the base branch plus the
/// factory's own (sandboxed, reviewed-in-progress) changes; an issue item's implementer saw only the triage summary, so the log
/// carries no more attacker influence than the code under review already does, and that code goes back through the review panel
/// and CI. The excerpt is still redacted, bounded and fenced as data (<see cref="Gate.CiHeal.Excerpt"/>). Tainting them would leave
/// no CI fixer able to push, i.e. no CI self-heal at all.
/// </para>
/// <para>
/// The taint of a tool use survives an interrupted run: the callback's write is not cancellable (a Ctrl-C or Pause cannot abort a
/// started commit), and before a session is resumed its stored stream (<c>session_events</c>) is replayed and re-tainted
/// (<see cref="Ledger.WorkLedger.ReplayTaintsAsync"/>), failing the run if it cannot be read. Residual: an orchestrator killed
/// outright (SIGKILL, power loss) after the worker wrote the tool_use line but before that line was stored — still in the pipe
/// buffer, or queued in the session capture — leaves no trace the factory can replay, while Claude's own transcript of the
/// session has it; resuming that session could then push. Closing it would need the worker's own transcript, which lives in the
/// <c>_factory</c> user's home.
/// </para>
/// <para>
/// The target repo's own Claude settings (<see cref="OfRepoSettings"/>) taint the session up front, and again if the worker wrote
/// them during its run. Deliberate, accepted residual: the worker's allowed <c>dotnet build/test/restore</c> run code the repo and
/// its packages supply (MSBuild targets, analyzers, test code, package restore), and the worker reads their output. That code
/// runs in the sandbox (no credential but the router key), is the code under review or a dependency the repo already pins, goes
/// back through the review panel and CI, and the factory cannot build or test anything without it; tainting every session that
/// builds would leave no worker able to push.
/// </para>
/// <para>
/// The session that reads an issue's text (the triage worker) holds no write capability at all (<see cref="WorkerTools.ReadOnly"/>:
/// Read, Glob, Grep; no edit, command, sub-agent or web tool; refused unsandboxed), so it cannot change a file that an untainted
/// session later pushes; its comment and label are posted by the orchestrator, and an issue item's implementer sees only the
/// approved triage, fenced as data (<see cref="PromptFence.Spec"/>). Its answer is posted on the issue, so what it can read is
/// confined to its own triage worktree: its one file allow rule is <see cref="WorkerTools.ReadRule"/> of that worktree (beside it only
/// the allowlisted read-only MCP tools of the loopback proxy, <see cref="WorkerTools.McpAllowed"/>, sc-25707), in
/// <c>dontAsk</c> mode (any read outside it would prompt, so it is denied), with reads outside the working directory blocked and
/// no settings file loaded that could widen that — not the work root's other clones or kept worktrees, not the worker user's home
/// (other sessions' transcripts). The posted free text is bounded (<see cref="Issues.TriageParser.MaxTitle"/>,
/// <see cref="Issues.TriageParser.MaxText"/>) and fenced. Residual, accepted: it can restate what it read inside its own worktree in
/// that answer — the target repo's own code at its default branch, the repo the issue is about — and the answer routes nothing by
/// the model's say-so. A symlink committed in that repo cannot lead it out: the owner removes every symlink whose resolved target
/// is outside the worktree after checkout and before the session starts (<see cref="Issues.TriageWorktree.RemoveOutOfTreeSymlinks"/>),
/// and the worker user's Claude Code must be at least <see cref="WorkerSandbox.MinClaudeVersion"/>, so the read block is not
/// silently ignored. Residual, accepted: Read and the read block are enforced by Claude Code itself, and its enforcement on Glob and
/// Grep (a search given an explicit path or pattern outside the working directory) is best-effort and not verified by the factory —
/// a gap there would let a search reach what the <c>_factory</c> user can read (other clones, kept worktrees, its home), bounded
/// only by the answer's size and fence.
/// </para>
/// </summary>
public static class Taint
{
    public const string IssueText = "issue-text";
    public const string OutsiderComment = "outsider-comment";

    /// <summary>The target repo's own Claude settings can bring content in outside the tool stream (<see cref="OfRepoSettings"/>).</summary>
    public const string RepoSettings = "repo-settings";

    /// <summary>
    /// The settings files Claude Code loads for a worker (<c>--setting-sources project,local</c>), relative to the worktree, and the
    /// project MCP file (ignored under <c>--strict-mcp-config</c>, checked anyway).
    /// </summary>
    public static readonly string[] RepoSettingsFiles = [".claude/settings.json", ".claude/settings.local.json"];
    public const string McpFile = ".mcp.json";

    /// <summary>Settings keys that run a command or start an MCP server outside the worker's tool calls, so its stream would not show it.</summary>
    private static readonly string[] CommandSettings =
        ["hooks", "enableAllProjectMcpServers", "enabledMcpjsonServers", "apiKeyHelper", "awsAuthRefresh", "awsCredentialExport",
            "otelHeadersHelper", "statusLine"];

    /// <summary>The tools that read the web; workers are denied them (<see cref="ClaudeWorker.DeniedTools"/>) and their use still taints.</summary>
    public static readonly string[] WebTools = ["WebFetch", "WebSearch"];

    private const int MaxToolName = 64;

    /// <summary>The taint reason a session gets when handed <paramref name="input"/>, or null when that input does not taint.</summary>
    public static string? Of(WorkerInput input) => input switch
    {
        WorkerInput.IssueText => IssueText,
        WorkerInput.OutsiderComment => OutsiderComment,
        WorkerInput.Story or WorkerInput.TriageSummary or WorkerInput.ReviewFindings or WorkerInput.CiLog => null,
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, "no taint rule for this input"),
    };

    /// <summary>
    /// <see cref="RepoSettings"/> when the worktree at <paramref name="worktree"/> carries Claude settings that could feed the session
    /// content its stream-json would not show: a settings file (<see cref="RepoSettingsFiles"/>) defining hooks, project MCP servers or
    /// a command helper (<see cref="CommandSettings"/>), or a <c>permissions.allow</c> rule beyond <see cref="ClaudeWorker.AllowedTools"/>
    /// (e.g. <c>Bash(curl:*)</c>); or a <see cref="McpFile"/> naming any server. A file that cannot be read or parsed taints too (E2:
    /// unknown settings are not trusted). Null when none of that is there.
    /// </summary>
    public static string? OfRepoSettings(string worktree)
    {
        // A .claude that is a symlink (or not a directory) is never read through: it taints.
        var claude = new FileInfo(Path.Combine(worktree, ".claude"));
        try
        {
            if (claude.LinkTarget is not null || (claude.Exists && !Directory.Exists(claude.FullName)))
            {
                return RepoSettings;
            }
        }
        catch (IOException)
        {
            return RepoSettings;
        }
        foreach (var file in RepoSettingsFiles)
        {
            if (Read(Path.Combine(worktree, file)) is not { } settings)
            {
                continue;
            }
            using (settings.Document)
            {
                if (settings.Document?.RootElement is not { ValueKind: JsonValueKind.Object } root
                    || CommandSettings.Any(key => root.TryGetProperty(key, out _))
                    || (root.TryGetProperty("permissions", out var permissions) && !OnlyAllowsWorkerTools(permissions)))
                {
                    return RepoSettings;
                }
            }
        }
        if (Read(Path.Combine(worktree, McpFile)) is { } mcp)
        {
            using (mcp.Document)
            {
                if (mcp.Document?.RootElement is not { ValueKind: JsonValueKind.Object } root
                    || (root.TryGetProperty("mcpServers", out var servers)
                        && (servers.ValueKind != JsonValueKind.Object || servers.EnumerateObject().Any())))
                {
                    return RepoSettings;
                }
            }
        }
        return null;
    }

    private static bool OnlyAllowsWorkerTools(JsonElement permissions)
    {
        if (permissions.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!permissions.TryGetProperty("allow", out var allow))
        {
            return true;
        }
        return allow.ValueKind == JsonValueKind.Array && allow.EnumerateArray().All(rule =>
            rule.ValueKind == JsonValueKind.String && ClaudeWorker.AllowedTools.Contains(rule.GetString(), StringComparer.Ordinal));
    }

    /// <summary>
    /// The file parsed, or null when there is no file at all. A null document — which taints — when it is refused by
    /// <see cref="SafeFile.Read"/> (a symlink, FIFO or other non-regular file, too large, unreadable) or is not JSON.
    /// </summary>
    private static Holder? Read(string path)
    {
        var file = SafeFile.Read(path);
        if (file.IsMissing)
        {
            return null;
        }
        try
        {
            return new Holder(file.Text is { } text ? JsonDocument.Parse(text) : null);
        }
        catch (JsonException)
        {
            return new Holder(null);
        }
    }

    private sealed record Holder(JsonDocument? Document);

    /// <summary>
    /// The taint reason of a worker's use of <paramref name="tool"/>: <c>web:&lt;tool&gt;</c> for a web tool, <c>mcp:&lt;tool&gt;</c> for
    /// any MCP tool (workers run with no MCP server, <c>--strict-mcp-config</c>, so one is something outside the factory's control),
    /// else null.
    /// </summary>
    public static string? ForTool(string tool)
    {
        var name = tool[..Math.Min(tool.Length, MaxToolName)];
        if (WebTools.Contains(tool, StringComparer.Ordinal))
        {
            return $"web:{name}";
        }
        return tool.StartsWith("mcp__", StringComparison.Ordinal) ? $"mcp:{name}" : null;
    }
}

/// <summary>A push of a worker session's work was refused because the session is tainted (E4).</summary>
public sealed class SessionTaintedException(string sessionId, string reason)
    : Exception($"Worker session {sessionId} is tainted ({reason}): it read untrusted content, so it holds no push token (E4). "
        + "Its unpushed work is discarded; a re-run starts a fresh session.")
{
    public string SessionId { get; } = sessionId;
    public string Reason { get; } = reason;
}

/// <summary>
/// The right to push the work of <see cref="Sessions"/>: issued only by <see cref="Ledger.WorkLedger.GrantPushAsync"/> after it found
/// none of them tainted, and required by <see cref="Git.IRepoWorkspace.CommitAndPushAsync"/> before a push token is minted.
/// </summary>
public sealed class PushGrant
{
    internal PushGrant(IReadOnlyList<string> sessions) => Sessions = sessions;

    /// <summary>The worker sessions whose work the push publishes, none tainted when the grant was issued.</summary>
    public IReadOnlyList<string> Sessions { get; }
}
