using DarkFactory.Orchestrator.WorkSources;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>An in-memory work source serving one story (any id) and recording every board write.</summary>
public sealed class FakeWorkSource(WorkStory story, bool commentFails = false) : IWorkSource
{
    public ItemNaming Naming => ItemNaming.Shortcut;
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
    public string? RecoveryClaimRefusal { get; set; }
    public Task<string?> RecoveryClaimRefusalAsync(int id, CancellationToken ct) => Task.FromResult(RecoveryClaimRefusal);

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

    /// <summary>Blockers someone related on the board beside the plan's own (story id → the ids blocking it).</summary>
    public Dictionary<int, List<int>> ExtraBlockers { get; } = [];

    /// <summary>A created child's blockers (its plan's, as created) and any <see cref="ExtraBlockers"/>.</summary>
    public Task<IReadOnlyList<Blocker>> BlockersAsync(int id, CancellationToken ct)
    {
        var blockers = Children.Where(c => c.Id == id).SelectMany(c => c.Child.BlockedBy.Select(b => Children[b].Id))
            .Concat(ExtraBlockers.GetValueOrDefault(id) ?? []).Distinct().Order().Select(b => new Blocker(b, FinishedOnBoard.Contains(b))).ToList();
        return Task.FromResult<IReadOnlyList<Blocker>>(blockers);
    }

    /// <summary>The stories the board shows finished (done, not archived).</summary>
    public HashSet<int> FinishedOnBoard { get; } = [];

    /// <summary>The children that left the backlog (released, or moved by hand: add one here).</summary>
    public HashSet<int> OutOfBacklog { get; } = [];

    /// <summary>When set, moving a child to ready throws this (the board is unreachable).</summary>
    public Exception? MakeReadyFails { get; set; }

    public Task<ReadyResult> MakeReadyAsync(int id, CancellationToken ct)
    {
        if (MakeReadyFails is { } fails)
        {
            return Task.FromException<ReadyResult>(fails);
        }
        if (!OutOfBacklog.Add(id))
        {
            return Task.FromResult(ReadyResult.AlreadyLeft);
        }
        Writes.Add($"ready {id}");
        return Task.FromResult(ReadyResult.Moved);
    }

    public Task<bool> InBacklogAsync(int id, CancellationToken ct) => Task.FromResult(Children.Any(c => c.Id == id) && !OutOfBacklog.Contains(id));

    private Task Write(string write)
    {
        Writes.Add(write);
        return Task.CompletedTask;
    }
}
