using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Controls;
using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4a's epic acceptance test P4-AT2 (sc-25873): a real planner plans a <c>plan</c>-labelled story that names two repositories
/// into stories in both; the factory creates a changeset branch in each, works each story in its own repository against that
/// repository's changeset branch, and merges the two changesets into their default branches one repository at a time, in the
/// plan's order, each by its own gated pull request. Runbook: <c>docs/acceptance.md</c>.
/// </summary>
public class MultiRepoPlanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(90);

    [Fact]
    public async Task P4_AT2_a_plan_across_two_repositories_gets_a_changeset_in_each_and_merges_them_in_the_plans_order()
    {
        Harness.RequireOptIn();
        var storyId = E2e.Story("FACTORY_E2E_P4_MULTI_PLAN_STORY",
            "a To Do story labelled `plan` (sc-<id>) with two `Repo:` lines (the sandbox, then FACTORY_E2E_P4_SECOND_REPO), describing work in both "
                + "where the second repository needs what the first one's changeset puts on its default branch");
        var second = RepoRef.Parse(Harness.RequireEnv("FACTORY_E2E_P4_SECOND_REPO",
            "a second sandbox repository (owner/name) with both GitHub Apps installed, a factory/gate.yaml, CI on factory/changeset-*, indexed in CodeGraph"));
        await using var e2e = await E2e.StartAsync("df_e2e_plan2");
        // The second repository is one of the factory's configured repositories for this run.
        var options = e2e.Options(new Dictionary<string, string?> { ["GitHub:Watch:Repos"] = second.FullName });
        await ReviewGateTests.RequireGateReadyAsync(options, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        var ct = timeout.Token;
        var first = options.DefaultRepo;
        using var shortcut = OutboundHttp.ShortcutApi();
        shortcut.DefaultRequestHeaders.Add("Shortcut-Token", Harness.Options.ShortcutApiToken);
        var parent = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{storyId}", ct);
        Assert.Equal([first.FullName, second.FullName],
            RepoResolver.ResolveAll(parent.GetProperty("description").GetString(), first).Select(r => r.FullName));
        List<int> created = [];
        try
        {
            var planned = await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct);
            Assert.True(planned.Succeeded, planned.Error);
            Assert.Equal(WorkState.Planned, planned.State);
            var history = await e2e.HistoryAsync(storyId, ct);
            created = [.. PlanRelease.StoriesOf(history.Last(e => e.Step == RunPipeline.Steps.PlanStories).Detail)
                .Select(s => StoryId.TryParse(s, out var id) ? id : throw new FormatException(s))];
            var plan = Plan.FromDetail(Assert.Single(history, e => e.Step == RunPipeline.Steps.Plan).Detail);
            if (plan.MergeOrder.Count != 2)
            {
                Assert.Fail($"INCONCLUSIVE: the planner put every story in one repository ({string.Join(", ", plan.MergeOrder)}); "
                    + "describe work that needs a change in both.");
            }
            Assert.False(string.IsNullOrWhiteSpace(plan.Interactions));
            // Without a story that waits on a changeset, the "released only after that changeset's merge" check below checks nothing.
            if (PlanLive.ChangesetBlockerProblem(plan) is { } inconclusive)
            {
                Assert.Fail(inconclusive);
            }

            // A changeset branch in each repository, recorded and pushed before the stories were recorded as the plan's.
            var changesets = Changeset.Ordered(history, first);
            Assert.Equal(plan.MergeOrder, changesets.Select(c => c.Repo));
            Assert.All(changesets, c => Assert.Equal($"factory/changeset-sc-{storyId}", c.Branch));
            var steps = history.Where(e => e.Step is not null).Select(e => e.Step).ToList();
            Assert.True(steps.LastIndexOf(RunPipeline.Steps.ChangesetPushed) < steps.IndexOf(RunPipeline.Steps.PlanStories), string.Join(' ', steps));
            // Each story is pinned by the code to the repository its plan entry names.
            for (var i = 0; i < created.Count; i++)
            {
                var story = await shortcut.GetFromJsonAsync<JsonElement>($"stories/{created[i]}", ct);
                Assert.Equal(plan.Stories[i].Repo, RepoResolver.Resolve(story.GetProperty("description").GetString(), first).FullName);
            }

            // Drive it: the plan item's run (release, or a changeset pull request's next step), then every released story's own run.
            var merged = new HashSet<int>();
            RunOutcome last = planned;
            for (var round = 0; round < 4 * created.Count + 12 && last.State != WorkState.Watch; round++)
            {
                last = await FactoryRunner.RunAsync(options, storyId, ignoreScope: true, Console.Out, ct);
                Assert.True(last.Succeeded, last.Error);
                var now = await e2e.HistoryAsync(storyId, ct);
                var delivered = Changeset.Delivered(now).Select(d => d.Repo).ToList();
                // One repository at a time, in the plan's order: the second changeset has no pull request before the first merged.
                if (!delivered.Contains(plan.MergeOrder[0]))
                {
                    Assert.Empty(await E2e.PullRequestsAsync(RepoRef.Parse(plan.MergeOrder[1]), changesets[1].Branch, ct));
                }
                var released = now.Where(e => e.Step == RunPipeline.Steps.PlanReleased).Select(e => PlanRelease.StoriesOf(e.Detail)[0]).ToList();
                for (var i = 0; i < created.Count; i++)
                {
                    // A story waiting on a changeset is released only after that changeset's Merge row.
                    if (released.Contains($"sc-{created[i]}"))
                    {
                        Assert.All(plan.Stories[i].BlockedByChangesets, r => Assert.Contains(r, delivered));
                    }
                    if (!released.Contains($"sc-{created[i]}") || !merged.Add(created[i]))
                    {
                        continue;
                    }
                    var worked = await FactoryRunner.RunAsync(options, created[i], ignoreScope: true, Console.Out, ct);
                    Assert.True(worked.Succeeded, worked.Error);
                    Assert.Equal(WorkState.Watch, worked.State);
                    // Its pull request is in its own repository, against that repository's changeset branch.
                    var repo = RepoRef.Parse(plan.Stories[i].Repo!);
                    var pull = Assert.Single(await E2e.PullRequestsAsync(repo, created[i], ct));
                    Assert.Equal(changesets[0].Branch, pull.GetProperty("base").GetProperty("ref").GetString());
                    Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
                }
            }

            Assert.Equal(WorkState.Watch, last.State);
            var final = await e2e.HistoryAsync(storyId, ct);
            Assert.Equal(plan.MergeOrder, Changeset.Delivered(final).Select(d => d.Repo));
            Assert.Equal(2, final.Count(e => e.Step is null && e.State == WorkState.Merge));
            Assert.Contains(final, e => e.Step is null && e.State == WorkState.Planned && e.Detail?.StartsWith("the changeset of ", StringComparison.Ordinal) == true);
            foreach (var (changeset, delivery) in changesets.Zip(Changeset.Delivered(final)))
            {
                var repo = RepoRef.Parse(changeset.Repo);
                var pull = Assert.Single(await E2e.PullRequestsAsync(repo, changeset.Branch, ct));
                Assert.Equal(changeset.Base, pull.GetProperty("base").GetProperty("ref").GetString());
                Assert.NotEqual(JsonValueKind.Null, pull.GetProperty("merged_at").ValueKind);
                Assert.Equal((ChangesetDelivery.Merged, pull.GetProperty("html_url").GetString()), (delivery.How, delivery.Pull));
                Assert.Contains("The plan changes 2 repositories.", pull.GetProperty("body").GetString());
            }
            // Each merge names the repository it went into, and the closeout lists both.
            Assert.Equal(plan.MergeOrder, final.Where(e => e.Step == RunPipeline.Steps.MergeFiles).Select(e => MergeFiles.FromDetail(e.Detail)!.Repo));
            Assert.Equal(RunPipeline.Posted, Assert.Single(final, e => e.Step == RunPipeline.Steps.Closeout).Detail);
            Assert.Contains(await E2e.StoryCommentsAsync(storyId, ct), c => c.Contains($"sc-{storyId}'s plan is complete")
                && plan.MergeOrder.All(r => c.Contains($"- `{r}`: merged into")));
        }
        finally
        {
            foreach (var id in created)
            {
                try
                {
                    using var response = await shortcut.PutAsJsonAsync($"stories/{id}", new { archived = true }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[cleanup] could not archive sc-{id}: {ex.Message}");
                }
            }
        }
    }
}
