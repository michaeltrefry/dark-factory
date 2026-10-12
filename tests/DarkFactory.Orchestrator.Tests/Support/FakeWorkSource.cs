using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>An in-memory work source serving one story (any id) and recording every board write.</summary>
public sealed class FakeWorkSource(WorkStory story, bool commentFails = false) : IWorkSource
{
    public bool CommentFails { get; set; } = commentFails;
    public List<string> Comments { get; } = [];
    /// <summary>Board writes in order, e.g. "claim 77", "state 77 Claimed", "link 77 a b".</summary>
    public List<string> Writes { get; } = [];
    public WorkEpic? Epic { get; set; }
    public IReadOnlyList<WorkDocument> Documents { get; init; } = [];

    public Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);

    /// <summary>When set, claims are refused with this reason and nothing is written.</summary>
    public string? RefuseClaim { get; set; }
    public bool InScope { get; set; } = true;

    public async Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct)
    {
        if (RefuseClaim is not null)
        {
            return ClaimResult.Refused(RefuseClaim);
        }
        await Write($"claim {id}");
        return ClaimResult.Ok;
    }

    public Task<bool> InScopeAsync(int id, CancellationToken ct) => Task.FromResult(InScope);

    /// <summary>When set, the start-up scope check throws this.</summary>
    public Exception? ValidateScopeFails { get; set; }

    public Task ValidateScopeAsync(CancellationToken ct) => ValidateScopeFails is { } fails ? Task.FromException(fails) : Task.CompletedTask;

    public Task ReleaseAsync(int id, CancellationToken ct) => Write($"release {id}");

    /// <summary>When set, reading a story's spec throws this (the board is unreachable).</summary>
    public Exception? ReadSpecFails { get; set; }

    public Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct) =>
        ReadSpecFails is { } fails ? Task.FromException<WorkSpec>(fails) : Task.FromResult(new WorkSpec(story with { Id = id }, Epic, Documents));

    public Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct)
    {
        if (comment is not null)
        {
            Comments.Add(WorkSourceComments.Attributed(comment));
        }
        return Write($"state {id} {state}");
    }

    public Task CommentAsync(int id, string text, CancellationToken ct)
    {
        if (CommentFails)
        {
            throw new InvalidOperationException("Shortcut down");
        }
        Comments.Add(WorkSourceComments.Attributed(text));
        return Task.CompletedTask;
    }

    public Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct) => Write($"link {id} {string.Join(" ", urls)}");

    /// <summary>Whether a story labelled <c>plan</c> is planned into children (default: no, like a source that creates none).</summary>
    public bool PlansItems { get; set; }

    /// <summary>When set, child creation is refused with this reason and nothing is created.</summary>
    public string? RefuseChildren { get; set; }

    /// <summary>The children created so far, each once (by name and description, like the Shortcut adapter).</summary>
    public List<(int Id, ChildItem Child)> Children { get; } = [];

    public Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct)
    {
        if (!PlansItems)
        {
            throw new NotSupportedException();
        }
        if (RefuseChildren is { } refusal)
        {
            throw new ChildrenRefusedException(refusal);
        }
        var ids = new List<int>();
        foreach (var child in children)
        {
            if (Children.FirstOrDefault(c => c.Child.Name == child.Name && c.Child.Description == child.Description) is { Id: > 0 } known)
            {
                ids.Add(known.Id);
                continue;
            }
            ids.Add(9000 + Children.Count);
            Children.Add((ids[^1], child));
            Writes.Add($"child {ids[^1]} of {parentId}");
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    private Task Write(string write)
    {
        Writes.Add(write);
        return Task.CompletedTask;
    }
}
