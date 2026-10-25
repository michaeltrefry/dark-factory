using System.Text.RegularExpressions;

namespace DarkFactory.Orchestrator;

public sealed record RepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    /// <summary>
    /// What two spellings of one repository share (sc-25877): GitHub's owner and repository names are case-insensitive, and each
    /// item carries its own <c>Repo:</c> line's spelling. Lower-case <c>owner/name</c>, for comparing and keying only — never for a
    /// path on disk (existing clones and worktrees are under the literal spelling) nor for anything shown or sent to GitHub.
    /// </summary>
    public string Key => FullName.ToLowerInvariant();

    /// <summary>Whether <paramref name="other"/> names the same repository, whatever case either is spelled in.</summary>
    public bool Is(RepoRef other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    public static RepoRef Parse(string fullName)
    {
        var parts = fullName.Trim().Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"Expected owner/name, got '{fullName}'.", nameof(fullName));
        }
        // Owner and name become path segments under the work root; "." / ".." / dot-prefixed would escape or hide.
        if (parts.Any(p => p.TrimStart().StartsWith('.')))
        {
            throw new ArgumentException($"Repo owner and name may not start with '.', got '{fullName}'.", nameof(fullName));
        }
        return new RepoRef(parts[0], parts[1]);
    }

    public override string ToString() => FullName;
}

/// <summary>The separately reviewed GUI control repository is infrastructure, never a model-authored work target.
/// Each instance reserves both the default trusted source-job controller and its configured collector, so moving settings
/// cannot reopen worker authority over an old controller still registered during migration. No mutable
/// process-global configuration is used. An All-repositories App installation does not grant
/// factory workers authority over the controller. Owner-admin deployment uses its separate explicit owner credential.</summary>
public static class FactoryRepositoryPolicy
{
    public const string ReservedController = Gate.RemoteGuiSourceWorkflow.Repository;
    private static readonly RepoRef DefaultController = RepoRef.Parse(ReservedController);

    public static RepoRef ControllerRepository(RepoRef? configured = null) => configured ?? DefaultController;

    public static bool IsReservedController(RepoRef repo, RepoRef? reservedController = null) =>
        repo.Is(DefaultController) || repo.Is(ControllerRepository(reservedController));

    private static RepoRef ReservedIdentity(RepoRef repo, RepoRef? configured) => repo.Is(DefaultController)
        ? DefaultController : ControllerRepository(configured);

    public static string? WorkRefusal(RepoRef repo, RepoRef? reservedController = null) => IsReservedController(repo, reservedController)
        ? $"Repository {ReservedIdentity(repo, reservedController)} is reserved for the trusted GUI controller: model-authored factory work is forbidden there. No work is claimed, implemented, pushed, dispatched or merged there."
        : null;

    public static void EnsureWorkAllowed(RepoRef repo, RepoRef? reservedController = null)
    {
        if (WorkRefusal(repo, reservedController) is { } reason) throw new InvalidOperationException(reason);
    }

    /// <summary>Token-issuer floor, checked before signing a JWT or making a GitHub request. The only controller write
    /// grant is the narrow trusted-workflow Actions scope, with optional Contents-read; it cannot edit contents, PRs, issues or repository settings.
    /// Explicit read scopes remain available. Empty/unknown scopes are refused rather than inheriting App defaults.</summary>
    public static void EnsureInstallationPermissions(RepoRef repo, IReadOnlyDictionary<string, string> permissions, RepoRef? reservedController = null)
    {
        if (!IsReservedController(repo, reservedController)) return;
        var readOnly = permissions.Count > 0 && permissions.All(p => p.Value is "read" or "none");
        var trustedDispatch = permissions.Count is 1 or 2 && permissions.TryGetValue("actions", out var actions) && actions is "read" or "write"
            && permissions.All(p => p.Key == "actions" || p.Key == "contents" && p.Value == "read");
        if (!readOnly && !trustedDispatch)
            throw new InvalidOperationException($"Repository {ReservedIdentity(repo, reservedController)} is reserved for the trusted GUI controller: factory installation tokens may not grant worker write authority there. Only explicit read scopes or trusted Actions-read/write with optional Contents-read are allowed.");
    }
}

/// <summary>
/// A story targets the configured default repo unless its description has a
/// <c>Repo: owner/name</c> line.
/// </summary>
public static partial class RepoResolver
{
    [GeneratedRegex(@"^[ \t>*_-]*Repo:[ \t*_]*`?([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)`?[ \t*_]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RepoLine();

    public static RepoRef Resolve(string? storyDescription, RepoRef defaultRepo) => Named(storyDescription) ?? defaultRepo;

    /// <summary>
    /// The repository the description's first <c>Repo: owner/name</c> line names, or null when it has none: what a work source
    /// with a repository of its own for the item (a Kanban project's <c>repository_url</c>) lets the item override.
    /// </summary>
    public static RepoRef? Named(string? storyDescription)
    {
        var match = RepoLine().Match(storyDescription ?? "");
        return match.Success ? RepoRef.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Every repository the description names, one per <c>Repo: owner/name</c> line, in order and without repeats (GitHub's names
    /// are case-insensitive); <paramref name="defaultRepo"/> alone when it has no such line. The first is <see cref="Resolve"/>'s
    /// unless that one is inside a fenced block: lines inside a Markdown fence (<c>```</c> or <c>~~~</c>) are not read here. A story
    /// the planner created carries planner text in a tilde fence after its code-written <c>Repo:</c> line (<c>UntrustedText.Fenced</c>);
    /// were it later labelled <c>plan</c>, a <c>Repo:</c> line in that text must not widen the next planner's checkouts and CodeGraph
    /// grant. Only a plan item's planner reads more than the first (sc-25874): every other item is worked in one repository.
    /// </summary>
    public static IReadOnlyList<RepoRef> ResolveAll(string? storyDescription, RepoRef defaultRepo)
    {
        var named = RepoLine().Matches(OutsideFences(storyDescription ?? "")).Select(m => RepoRef.Parse(m.Groups[1].Value))
            .DistinctBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        return named.Count > 0 ? named : [defaultRepo];
    }

    /// <summary>
    /// <paramref name="text"/> without its fenced blocks, fence lines included. A fence opens on a line that starts (after any
    /// indentation or quote markers: stricter than Markdown, which only skips more) with three or more backticks or tildes, and is
    /// closed only by a line of at least as many of the same marker and nothing else, as in Markdown; one never closed runs to the end.
    /// </summary>
    private static string OutsideFences(string text)
    {
        var kept = new List<string>();
        var marker = '\0';
        var length = 0;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t', '>').TrimEnd();
            var run = trimmed.Length > 0 && trimmed[0] is '`' or '~' ? trimmed.Length - trimmed.TrimStart(trimmed[0]).Length : 0;
            if (length == 0)
            {
                if (run >= 3)
                {
                    (marker, length) = (trimmed[0], run);
                }
                else
                {
                    kept.Add(line);
                }
            }
            else if (run >= length && trimmed[0] == marker && run == trimmed.Length)
            {
                length = 0;
            }
        }
        return string.Join('\n', kept);
    }
}
