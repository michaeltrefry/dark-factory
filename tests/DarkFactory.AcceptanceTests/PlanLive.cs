using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// What Phase 4a's live plan tests (P4-AT1, P4-AT2, P4-AT3) decide from a plan or read from the board, kept apart from the live
/// tests so it runs in every <c>dotnet test</c> (<see cref="PlanLiveTests"/>): a live run never fails, or passes vacuously, on the
/// harness itself (sc-25877).
/// </summary>
internal static class PlanLive
{
    /// <summary>What a live run that did not exercise the epic's scenario fails with: rerun it on a story that does.</summary>
    public const string Inconclusive = "INCONCLUSIVE";

    /// <summary>
    /// P4-AT1 (the epic: "at least 3 stories with a dependency chain"): null when <paramref name="plan"/> has three or more stories
    /// and a chain of three — a story blocked by a story that itself has a blocker — else why this run proves nothing about
    /// release in dependency order (<see cref="Inconclusive"/>).
    /// </summary>
    public static string? ChainProblem(Plan plan)
    {
        if (plan.Stories.Count < 3)
        {
            return $"{Inconclusive}: the planner made {plan.Stories.Count} stories; P4-AT1 needs at least 3 with a dependency chain. "
                + "Describe work that needs three steps, each building on the one before.";
        }
        var blocked = plan.Stories.Where(s => s.BlockedBy.Count > 0).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        return plan.Stories.Any(s => s.BlockedBy.Any(blocked.Contains))
            ? null
            : $"{Inconclusive}: no story of the plan is blocked by a story that itself has a blocker (blockers: "
                + string.Join("; ", plan.Stories.Select(s => $"{s.Key} <- [{string.Join(", ", s.BlockedBy)}]"))
                + "); P4-AT1 needs a chain of three. Describe work whose steps each build on the one before.";
    }

    /// <summary>
    /// P4-AT2: null when some story of <paramref name="plan"/> waits on a repository's changeset
    /// (<see cref="PlanStory.BlockedByChangesets"/>), else why this run proves nothing about changeset blockers.
    /// </summary>
    public static string? ChangesetBlockerProblem(Plan plan) =>
        plan.Stories.Any(s => s.BlockedByChangesets.Count > 0)
            ? null
            : $"{Inconclusive}: no story of the plan waits on a repository's changeset (blocked_by_changesets is empty on all "
                + $"{plan.Stories.Count}); P4-AT2 needs one. Describe work in which the second repository needs what the first one's "
                + "changeset puts on its default branch (e.g. it pins or consumes the first repository's released change).";

    /// <summary>A planner answer <see cref="PlanParser"/> refuses for a dependency cycle (P4-AT3): a needs b, b needs a.</summary>
    public static string CyclicAnswer => Answer(Story("a", "[dark-factory P4-AT3] cycle A", "b"), Story("b", "[dark-factory P4-AT3] cycle B", "a"));

    /// <summary>A planner answer <see cref="PlanParser"/> refuses for its size (P4-AT3): one story more than <see cref="PlanParser.MaxStories"/>.</summary>
    public static string OversizedAnswer =>
        Answer([.. Enumerable.Range(1, PlanParser.MaxStories + 1).Select(i => Story($"s{i}", $"[dark-factory P4-AT3] oversized {i}"))]);

    private static JsonObject Story(string key, string title, params string[] blockedBy) => new()
    {
        ["key"] = key,
        ["title"] = title,
        ["description"] = "Seeded by P4-AT3; the plan is refused, so this story is never created.",
        ["acceptance"] = new JsonArray($"{key} is observable"),
        ["type"] = "chore",
        ["blocked_by"] = new JsonArray([.. blockedBy.Select(b => (JsonNode)b)]),
    };

    private static string Answer(params JsonObject[] stories) =>
        "I read the code.\n\n```json\n" + new JsonObject { ["stories"] = new JsonArray([.. stories]) }.ToJsonString() + "\n```";

    /// <summary>
    /// The ids of every story (archived ones too) in <paramref name="parent"/>'s epic and in its team, read straight from the
    /// Shortcut API: where a plan's stories are created, so "nothing was created" is "this set did not grow".
    /// </summary>
    public static async Task<HashSet<int>> SiblingsAsync(HttpClient shortcut, JsonElement parent, CancellationToken ct)
    {
        var ids = new HashSet<int>();
        if (parent.TryGetProperty("epic_id", out var epic) && epic.ValueKind == JsonValueKind.Number)
        {
            ids.UnionWith((await shortcut.GetFromJsonAsync<JsonElement>($"epics/{epic.GetInt32()}/stories", ct))
                .EnumerateArray().Select(s => s.GetProperty("id").GetInt32()));
        }
        if (parent.TryGetProperty("group_id", out var team) && team.ValueKind == JsonValueKind.String)
        {
            const int page = 1000;
            for (var offset = 0; ; offset += page)
            {
                var stories = (await shortcut.GetFromJsonAsync<JsonElement>($"groups/{team.GetString()}/stories?limit={page}&offset={offset}", ct))
                    .EnumerateArray().Select(s => s.GetProperty("id").GetInt32()).ToList();
                ids.UnionWith(stories);
                if (stories.Count < page)
                {
                    break;
                }
            }
        }
        Assert.True(ids.Count > 0, "the plan story has neither an epic nor a team: put it in one (its stories are created there)");
        return ids;
    }

    /// <summary>
    /// The plan item's closeout exactly as <see cref="RunPipeline"/> rendered it when it posted it (sc-25871, sc-25872): from the
    /// plan item's rows written before its posted <c>closeout</c> row — the item was in Merge, in <paramref name="primary"/> — and
    /// each story's own rows and title in the throwaway ledger. Session costs are read now, as <c>ReviewGateTests</c> does.
    /// </summary>
    public static async Task<string> ExpectedPlanCloseoutAsync(E2e e2e, int planId, IReadOnlyList<int> stories, RepoRef primary, CancellationToken ct)
    {
        var history = await e2e.HistoryAsync(planId, ct);
        var closeout = history.FindIndex(e => e.Step == RunPipeline.Steps.Closeout && e.Detail == RunPipeline.Posted);
        Assert.True(closeout >= 0, $"sc-{planId}'s ledger has no posted closeout row");
        var before = history.Take(closeout).ToList();
        var reports = new List<PlanStoryReport>();
        await using (var db = e2e.Db())
        {
            foreach (var story in stories)
            {
                var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.ExternalId == StoryId.Format(story), ct);
                reports.Add(new PlanStoryReport(item.ExternalId, item.Title, await e2e.HistoryAsync(story, ct)));
            }
        }
        return WorkSourceComments.Attributed(LedgerReport.PlanCloseout(StoryId.Format(planId), reports, null,
            (Changeset.CurrentPass(before), await e2e.SessionCostsAsync(planId, ct)), Changeset.States(before, primary, WorkState.Merge)));
    }

    /// <summary>A merged story's closeout exactly as the pipeline rendered it: from its rows written before its posted <c>closeout</c> row.</summary>
    public static async Task<string> ExpectedStoryCloseoutAsync(E2e e2e, int storyId, CancellationToken ct)
    {
        var history = await e2e.HistoryAsync(storyId, ct);
        var closeout = history.FindIndex(e => e.Step == RunPipeline.Steps.Closeout && e.Detail == RunPipeline.Posted);
        Assert.True(closeout >= 0, $"sc-{storyId}'s ledger has no posted closeout row");
        return WorkSourceComments.Attributed(LedgerReport.MergedCloseout(StoryId.Format(storyId), history.Take(closeout).ToList(),
            await e2e.SessionCostsAsync(storyId, ct)));
    }

    /// <summary>Line endings as the board may return them, made one.</summary>
    public static string Lines(string? text) => (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
}
