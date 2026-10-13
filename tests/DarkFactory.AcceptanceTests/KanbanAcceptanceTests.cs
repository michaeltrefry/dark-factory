using System.Net.Http.Json;
using System.Text.Json;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// Phase 4b's P4b-AT1, first version (sc-25981; docs/acceptance.md): <c>factory run kb-N</c> takes a Ready item of a watched
/// KanbanBoard project to an opened pull request — claimed on the real board, worked, and the pull request and branch linked on
/// the item. It checks up to "pull request opened": what the review and the merge gate then decide is not asserted here.
/// </summary>
public class KanbanAcceptanceTests
{
    [Fact]
    public async Task P4b_AT1_factory_run_takes_a_ready_kanban_item_to_an_opened_pull_request_linked_on_the_item()
    {
        Harness.RequireOptIn();
        var itemId = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_ITEM",
            "the id (GUID) of a Ready, unclaimed, unblocked KanbanBoard item whose fix lands in the sandbox repo");
        Assert.True(Guid.TryParse(itemId, out var guid), $"FACTORY_E2E_P4B_KANBAN_ITEM='{itemId}' is not a GUID");
        var project = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_PROJECT", "the key of that item's project (the test's Kanban:Watch:Projects)");
        var writeToken = Harness.RequireSecret(o => o.KanbanWriteToken);
        var appId = Harness.RequireSecret(o => o.GitHubAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        await using var e2e = await E2e.StartAsync("df_p4b_at1");
        var ct = TestContext.Current.CancellationToken;
        var options = e2e.Options(new Dictionary<string, string?> { [KanbanScope.Setting] = project });

        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var board = new KanbanBoard(KanbanWrites.Client(kanbanHttp, writeToken));
        var before = await board.GetItemAsync(guid, ct);
        Assert.True(before is { Status: KanbanStatus.Ready, ClaimMarker: null } && before.OpenBlockers.Count == 0,
            $"The item must be Ready, unclaimed and unblocked; it is {before.Status}, claimed by {before.Claimant ?? "nobody"}, "
            + $"with {before.OpenBlockers.Count} open blocker(s).");

        // The item gets its key (the same one however often it is asked for), then `factory run kb-N`.
        var name = await FactoryRunner.KanbanKeyAsync(options, guid, ct);
        Assert.Equal(name, await FactoryRunner.KanbanKeyAsync(options, guid, ct));
        var item = ItemNaming.ParseAny(name) ?? throw new InvalidOperationException($"'{name}' is no work item id.");
        Assert.Equal(ItemNaming.Kanban, item.Naming);
        // It is listed ready, under that key, before the run.
        Assert.Contains(item.Id, await FactoryRunner.CreateKanbanSource(options, kanbanHttp).ListReadyAsync(ct));

        var outcome = await FactoryRunner.RunAsync(options, item, ignoreScope: false, Console.Out, ct);
        Console.WriteLine($"[p4b-at1] {name}: {outcome.State}{(outcome.Error is null ? "" : $" ({outcome.Error})")}");

        // Ledger: claimed, then the pull request and branch linked, under the item's key.
        await using var db = e2e.Db();
        var row = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Source == ItemNaming.Kanban.Source && w.ExternalId == name, ct);
        var history = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == row.Id).OrderBy(e => e.Id).ToListAsync(ct);
        Assert.Single(history, e => e.Step == RunPipeline.Steps.Claimed);
        var linked = Assert.Single(history, e => e.Step == RunPipeline.Steps.Linked);
        var links = linked.Detail!.Split(' ');
        Assert.Equal(2, links.Length);
        var (prUrl, branchUrl) = (links[0], links[1]);
        var repo = RepoRef.Parse(row.Repo);
        Assert.Equal(RunPipeline.BranchUrl(repo, ItemNaming.Kanban.BranchName(item.Id)), branchUrl);
        Assert.Contains(history, e => e.Step is null && e.State == WorkState.Review);

        // GitHub: the pull request exists, from the item's own branch.
        using var github = OutboundHttp.GitHubApi();
        var token = (await new GitHubApp(github, appId, appKey, TimeProvider.System).CreateInstallationTokenAsync(repo, ct)).Token;
        using var request = GitHubApp.Request(HttpMethod.Get,
            $"repos/{repo.Owner}/{repo.Name}/pulls?state=all&head={Uri.EscapeDataString($"{repo.Owner}:{ItemNaming.Kanban.BranchName(item.Id)}")}",
            "Bearer", token);
        using var response = await github.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray(), p => p.GetProperty("html_url").GetString() == prUrl);

        // The board: the factory's claim under the marker derived from the item's board id, the item taken out of Ready, and the
        // pull request and the branch as links of their kinds.
        var after = await board.GetItemAsync(guid, ct);
        Assert.Equal(KanbanWorkSource.Marker(guid), after.ClaimMarker);
        Assert.NotNull(after.ClaimantId);
        Assert.Contains(after.Status, new[] { KanbanStatus.InProgress, KanbanStatus.Done }); // Done if the gate went on to merge it
        Assert.Single(after.Links!, l => l.Kind == "pr" && l.Url == prUrl);
        Assert.Single(after.Links!, l => l.Kind == "branch" && l.Url == branchUrl);

        // A second run resumes from the ledger and writes nothing twice. (An escalated item re-enters Intake by design — a re-run
        // is its remedy — so the resume is only checked for an item the first run did not escalate.)
        if (outcome.State == WorkState.Escalated)
        {
            Console.WriteLine($"[p4b-at1] {name} escalated after its pull request was opened; the resume is not checked.");
            return;
        }
        await FactoryRunner.RunAsync(options, item, ignoreScope: false, Console.Out, ct);
        var resumed = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == row.Id).ToListAsync(ct);
        Assert.Single(resumed, e => e.Step == RunPipeline.Steps.Claimed);
        Assert.Single(resumed, e => e.Step == RunPipeline.Steps.Linked);
        var again = await board.GetItemAsync(guid, ct);
        Assert.Equal(after.Links!.Count, again.Links!.Count);
    }
}
