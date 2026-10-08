using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// An in-memory GitHub (the subset <see cref="IGitHubIssues"/> covers): issues with labels and comments, users' permissions,
/// files on default branches. Every write is recorded in <see cref="Writes"/>; a comment written through it is the factory App's.
/// </summary>
public sealed class FakeGitHubIssues(TimeProvider time) : IGitHubIssues
{
    public const long FactoryApp = 4242;

    private sealed class Issue
    {
        public required RepoRef Repo { get; init; }
        public int Number { get; init; }
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public required string Author { get; init; }
        public bool Open { get; set; } = true;
        public List<string> Labels { get; } = [];
        public List<IssueComment> Comments { get; } = [];
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private readonly List<Issue> _issues = [];
    private long _nextComment = 1000;

    public long AppId => FactoryApp;

    /// <summary>Permissions by login (anyone else has none).</summary>
    public Dictionary<string, RepoPermission> Permissions { get; } = new();

    /// <summary>Files on each repo's default branch, by "owner/name:path".</summary>
    public Dictionary<string, string> Files { get; } = new();

    /// <summary>Writes in order, e.g. "comment acme/widgets#12", "label acme/widgets#12 awaiting-approval".</summary>
    public List<string> Writes { get; } = [];

    /// <summary>When set, the next comment post reaches GitHub and then fails (the response is lost).</summary>
    public bool LoseNextCommentResponse { get; set; }

    /// <summary>When set, every call fails (GitHub unreachable).</summary>
    public Exception? Down { get; set; }

    public void Open(RepoRef repo, int number, string title, string body, string author)
    {
        _issues.Add(new Issue { Repo = repo, Number = number, Title = title, Body = body, Author = author, UpdatedAt = time.GetUtcNow() });
    }

    public void Edit(RepoRef repo, int number, string body)
    {
        var issue = Find(repo, number);
        issue.Body = body;
        issue.UpdatedAt = time.GetUtcNow();
    }

    public void Close(RepoRef repo, int number) => Find(repo, number).Open = false;

    /// <summary>Someone (not the factory) comments; <paramref name="editedLater"/> marks it edited after posting.</summary>
    public long Reply(RepoRef repo, int number, string author, string body, bool editedLater = false, bool bot = false)
    {
        var issue = Find(repo, number);
        var at = time.GetUtcNow();
        var id = ++_nextComment;
        issue.Comments.Add(new IssueComment(id, author, bot, body, at, editedLater ? at.AddMinutes(5) : at, bot ? 99 : null));
        issue.UpdatedAt = at;
        return id;
    }

    public IReadOnlyList<IssueComment> CommentsOn(RepoRef repo, int number) => Find(repo, number).Comments;

    public IReadOnlyList<IssueComment> FactoryComments(RepoRef repo, int number) => Find(repo, number).Comments.Where(c => c.AppId == FactoryApp).ToList();

    public IReadOnlyList<string> LabelsOf(RepoRef repo, int number) => Find(repo, number).Labels;

    public bool IsOpen(RepoRef repo, int number) => Find(repo, number).Open;

    public Task<IReadOnlyList<IssueFacts>> ListUpdatedAsync(RepoRef repo, DateTimeOffset? since, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult<IReadOnlyList<IssueFacts>>(_issues
            .Where(i => i.Repo == repo && i.Open && (since is null || i.UpdatedAt >= since))
            .OrderBy(i => i.UpdatedAt)
            .Select(Facts)
            .ToList());
    }

    public Task<IssueFacts> GetAsync(RepoRef repo, int number, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult(Facts(Find(repo, number)));
    }

    public Task<IReadOnlyList<IssueComment>> ListCommentsAsync(RepoRef repo, int number, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult<IReadOnlyList<IssueComment>>(Find(repo, number).Comments.OrderBy(c => c.Id).ToList());
    }

    public Task<RepoPermission> PermissionAsync(RepoRef repo, string login, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult(Permissions.GetValueOrDefault(login, RepoPermission.None));
    }

    public Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult(Files.GetValueOrDefault($"{repo}:{path}"));
    }

    public Task<long> CommentAsync(RepoRef repo, int number, string body, CancellationToken ct)
    {
        ThrowIfDown();
        var issue = Find(repo, number);
        var at = time.GetUtcNow();
        var id = ++_nextComment;
        issue.Comments.Add(new IssueComment(id, "dark-factory[bot]", true, body, at, at, FactoryApp));
        issue.UpdatedAt = at;
        Writes.Add($"comment {repo}#{number}");
        if (LoseNextCommentResponse)
        {
            LoseNextCommentResponse = false;
            throw new HttpRequestException("connection reset after the comment was created");
        }
        return Task.FromResult(id);
    }

    public Task AddLabelsAsync(RepoRef repo, int number, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ThrowIfDown();
        var issue = Find(repo, number);
        foreach (var label in labels.Where(l => !issue.Labels.Contains(l)))
        {
            issue.Labels.Add(label);
        }
        Writes.Add($"label {repo}#{number} {string.Join(",", labels)}");
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(RepoRef repo, int number, string label, CancellationToken ct)
    {
        ThrowIfDown();
        if (Find(repo, number).Labels.Remove(label))
        {
            Writes.Add($"unlabel {repo}#{number} {label}");
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync(RepoRef repo, int number, CancellationToken ct)
    {
        ThrowIfDown();
        Find(repo, number).Open = false;
        Writes.Add($"close {repo}#{number}");
        return Task.CompletedTask;
    }

    private void ThrowIfDown()
    {
        if (Down is { } down)
        {
            throw down;
        }
    }

    private Issue Find(RepoRef repo, int number) => _issues.Single(i => i.Repo == repo && i.Number == number);

    private static IssueFacts Facts(Issue i) =>
        new(i.Number, i.Title, i.Body, i.Author, false, i.Open, i.Labels.ToList(), $"https://github.com/{i.Repo}/issues/{i.Number}", i.UpdatedAt);
}
