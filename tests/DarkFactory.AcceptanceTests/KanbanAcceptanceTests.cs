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
/// P4b-AT4 (sc-25982): <c>factory work</c> starts with no Shortcut token and works a Kanban item, then stops it by its id.
/// </summary>
public class KanbanAcceptanceTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(45);

    /// <summary>
    /// P4b-AT4 (sc-25982): with KanbanBoard configured and no Shortcut token anywhere, <c>factory work</c>'s start-up check
    /// passes after validating the Kanban scope, the host starts, and its intake loop lists the Ready item (giving it its
    /// <c>kb-N</c>), claims it on the real board and works it to an opened pull request. Then <c>factory stop --item kb-N</c>
    /// (the same code path) stops it: Cancelled, the pull request a draft, the item back in Backlog with the claim released —
    /// so the test leaves the board item where a person can make it Ready again. The watched project must hold no other Ready
    /// item: the host claims every ready item it sees.
    /// </summary>
    [Fact]
    public async Task P4b_AT4_factory_work_starts_with_no_shortcut_token_and_works_a_kanban_item()
    {
        Harness.RequireOptIn();
        var itemId = Harness.RequireEnv("FACTORY_E2E_P4B_WORK_KANBAN_ITEM",
            "the id (GUID) of a Ready, unclaimed, unblocked KanbanBoard item whose fix lands in the sandbox repo, the only Ready item of its project");
        Assert.True(Guid.TryParse(itemId, out var guid), $"FACTORY_E2E_P4B_WORK_KANBAN_ITEM='{itemId}' is not a GUID");
        var project = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_PROJECT", "the key of that item's project (the test's Kanban:Watch:Projects)");
        if (Environment.GetEnvironmentVariable("SHORTCUT_API_TOKEN") is not null)
        {
            Assert.Skip("Unset SHORTCUT_API_TOKEN for this test: it shows the factory starting with no Shortcut token, and the environment would supply one.");
        }
        var writeToken = Harness.RequireSecret(o => o.KanbanWriteToken);
        var appId = Harness.RequireSecret(o => o.GitHubAppId);
        var appKey = Harness.RequireSecret(o => o.GitHubAppPrivateKeyPem);
        await using var e2e = await E2e.StartAsync("df_p4b_at4", ItemNaming.Kanban);
        var ct = TestContext.Current.CancellationToken;
        // KanbanBoard alone: no Shortcut token (the keychain's is hidden from these options), no Shortcut scope, no watched issues.
        var options = e2e.Options(new Dictionary<string, string?>
        {
            [KanbanScope.Setting] = project,
            ["Shortcut:ApiToken"] = "",
            ["Shortcut:Watch:Teams"] = "",
            ["Shortcut:Watch:Epics"] = "",
            ["GitHub:Watch:Repos"] = "",
        }, SecretAccounts.ShortcutApiToken);
        Assert.False(options.TryGet(o => o.ShortcutApiToken, out _), "the test's options still find a Shortcut token");

        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var board = new KanbanBoard(KanbanWrites.Client(kanbanHttp, writeToken));
        var before = await board.GetItemAsync(guid, ct);
        Assert.True(before is { Status: KanbanStatus.Ready, ClaimMarker: null } && before.OpenBlockers.Count == 0,
            $"The item must be Ready, unclaimed and unblocked; it is {before.Status}, claimed by {before.Claimant ?? "nobody"}, "
            + $"with {before.OpenBlockers.Count} open blocker(s).");

        // `factory work`'s start-up check: passes with the Kanban source alone, its scope validated on the real board.
        var stderr = new StringWriter();
        using (var sources = new WorkSourceRegistry(options))
        {
            var refusal = await FactoryRunner.CheckWorkStartAsync(options, sources, stderr, ct);
            Assert.True(refusal is null, $"factory work would not start: {refusal}");
            Assert.Equal([ItemNaming.Kanban], sources.Configured.Select(s => s.Naming));
            Assert.Null(sources.Find(ItemNaming.Shortcut));
        }
        Console.WriteLine($"[p4b-at4] start-up notes: {stderr}");

        string name;
        await using (var app = FactoryHost.BuildWork(options))
        {
            await app.StartAsync(ct);
            try
            {
                // The intake loop lists the item — its key is given then, with no `factory kanban key` — claims it and works it.
                int? key = null;
                await E2e.WaitForAsync("the intake loop to list the Kanban item", TimeSpan.FromMinutes(2), async () =>
                {
                    await using var db = e2e.Db();
                    return (key = (await db.KanbanItems.AsNoTracking().SingleOrDefaultAsync(k => k.ItemId == guid, ct))?.Id) is not null;
                }, ct);
                name = ItemNaming.Kanban.Format(key!.Value);
                await E2e.WaitForAsync($"{name} to reach Review", RunTimeout, async () =>
                {
                    var rows = await HistoryAsync(e2e, name, ct);
                    var last = rows.LastOrDefault(e => e.Step is null);
                    Assert.False(last?.State == WorkState.Escalated, $"{name} escalated: {last?.Detail}");
                    return rows.Any(e => e.Step is null && e.State == WorkState.Review);
                }, ct);

                var history = await HistoryAsync(e2e, name, ct);
                Assert.Single(history, e => e.Step == RunPipeline.Steps.Claimed);
                var prUrl = Assert.Single(history, e => e.Step == RunPipeline.Steps.Linked).Detail!.Split(' ')[0];
                var claimed = await board.GetItemAsync(guid, ct);
                Assert.Equal(KanbanWorkSource.Marker(guid), claimed.ClaimMarker);
                Assert.Contains(claimed.Status, new[] { KanbanStatus.InProgress, KanbanStatus.Done }); // Done if the gate went on to merge it
                Assert.Single(claimed.Links!, l => l.Kind == "pr" && l.Url == prUrl);

                // `factory stop --item kb-N`, through the same code path, while the host runs: the item's own source is found by its id.
                if (claimed.Status == KanbanStatus.Done)
                {
                    Console.WriteLine($"[p4b-at4] {name} merged before it could be stopped; the stop is not checked.");
                    return;
                }
                var stop = await FactoryRunner.ControlAsync(options, "stop", Orchestrator.Controls.ControlScope.Item(name), "P4b-AT4", Console.Out, ct);
                Console.WriteLine($"[p4b-at4] {stop.Message}");
                await E2e.WaitForAsync($"{name} to be Cancelled", TimeSpan.FromMinutes(13), async () =>
                    (await HistoryAsync(e2e, name, ct)).LastOrDefault(e => e.Step is null)?.State is WorkState.Cancelled or WorkState.Merge or WorkState.Watch, ct);

                var final = (await HistoryAsync(e2e, name, ct)).Last(e => e.Step is null).State;
                if (final != WorkState.Cancelled)
                {
                    Console.WriteLine($"[p4b-at4] {name} merged ({final}) before the stop took effect; the stop is not checked.");
                    return;
                }
                var stopped = await board.GetItemAsync(guid, ct);
                Assert.Equal((KanbanStatus.Backlog, null), (stopped.Status, stopped.ClaimMarker));
                await using var ledger = e2e.Db();
                var repo = RepoRef.Parse((await ledger.WorkItems.AsNoTracking().SingleAsync(w => w.ExternalId == name, ct)).Repo);
                using var github = OutboundHttp.GitHubApi();
                var token = (await new GitHubApp(github, appId, appKey, TimeProvider.System).CreateInstallationTokenAsync(repo, ct)).Token;
                using var request = GitHubApp.Request(HttpMethod.Get,
                    $"repos/{repo.Owner}/{repo.Name}/pulls?state=all&head={Uri.EscapeDataString($"{repo.Owner}:{ItemNaming.Kanban.BranchName(key.Value)}")}",
                    "Bearer", token);
                using var response = await github.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var pull = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray(), p => p.GetProperty("html_url").GetString() == prUrl);
                Assert.True(pull.GetProperty("draft").GetBoolean(), $"{prUrl} is not a draft after the stop");
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<List<LedgerEntry>> HistoryAsync(E2e e2e, string name, CancellationToken ct)
    {
        await using var db = e2e.Db();
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(i => i.Source == ItemNaming.Kanban.Source && i.ExternalId == name, ct);
        return item is null ? [] : await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
    }

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
        await using var e2e = await E2e.StartAsync("df_p4b_at1", ItemNaming.Kanban);
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
        Assert.Contains(item.Id, await WorkSourceRegistry.Kanban(options, kanbanHttp).ListReadyAsync(ct));

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
