using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Controls;

/// <summary>Stops one item now if no run holds it (<see cref="ItemStopper.StopAsync"/>); returns what happened.</summary>
public interface IItemStops
{
    Task<string> StopAsync(int storyId, CancellationToken ct);
}

/// <summary>
/// Pause, Continue and Stop at a scope: what <c>factory pause|continue|stop</c> and the dashboard's buttons do.
/// Pause and Continue only write the scope's control; running pipelines (any process) honour it within about a
/// second, and the intake loop resumes a user-paused item once nothing pauses it. Stop marks each non-terminal
/// item in the scope Stopping, then stops it at once if no run holds it (with <paramref name="stops"/>); a running
/// item is stopped by its own run. Without <paramref name="stops"/> the next run or poll of the item stops it.
/// </summary>
public sealed class ControlActions(IControls controls, IDbContextFactory<LedgerDbContext> contexts, IItemStops? stops = null)
{
    public async Task<string> PauseAsync(string scope, string by, CancellationToken ct)
    {
        await controls.SetAsync(scope, ControlState.Paused, by, ct);
        return $"{scope} paused: nothing new starts there, and running workers stop at their next tool call.";
    }

    public async Task<string> ContinueAsync(string scope, string by, CancellationToken ct)
    {
        if ((await controls.GetAsync(scope, ct))?.State == ControlState.Stopping)
        {
            return $"{scope} is being stopped; it cannot be continued.";
        }
        await controls.SetAsync(scope, ControlState.Running, by, ct);
        return $"{scope} continued.";
    }

    public async Task<string> StopAsync(string scope, string by, CancellationToken ct)
    {
        if (!ControlScope.IsValid(scope))
        {
            throw new ArgumentException($"'{scope}' is not a control scope.", nameof(scope));
        }
        var stories = new List<int>();
        if (ControlScope.ItemStory(scope) is { } story)
        {
            stories.Add(story);
        }
        else
        {
            var epic = ControlScope.EpicOf(scope);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var items = await db.WorkItems.AsNoTracking()
                .Where(i => i.Source == RunPipeline.Source && i.State != WorkState.Done && i.State != WorkState.Cancelled
                    && (epic == null || i.EpicId == epic))
                .OrderBy(i => i.Id)
                .Select(i => i.ExternalId)
                .ToListAsync(ct);
            stories.AddRange(items.Select(e => StoryId.TryParse(e, out var id) ? id : 0).Where(id => id > 0));
        }
        if (stories.Count == 0)
        {
            return $"{scope}: no active items to stop.";
        }
        var results = new List<string>();
        foreach (var id in stories)
        {
            await controls.SetAsync(ControlScope.Item(StoryId.Format(id)), ControlState.Stopping, by, ct);
            if (stops is null)
            {
                results.Add($"{StoryId.Format(id)}: stop requested; its next run stops it.");
                continue;
            }
            try
            {
                results.Add(await stops.StopAsync(id, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The item keeps its Stopping control: the intake loop's next poll (or a retry) finishes the stop.
                results.Add($"{StoryId.Format(id)}: stop NOT finished ({ex.Message}); it stays Stopping and the next poll or `factory stop` retries it.");
            }
        }
        return string.Join('\n', results);
    }
}
