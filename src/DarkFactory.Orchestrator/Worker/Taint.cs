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
/// </summary>
public static class Taint
{
    public const string IssueText = "issue-text";
    public const string OutsiderComment = "outsider-comment";

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
