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

    /// <summary>
    /// P4b-AT3 (sc-25983; docs/acceptance.md): on the real board, with a second caller (another identity's full-scope token) — a
    /// second caller's claim on an item the factory holds is refused; an in-flight item's claim is renewed by the intake poll
    /// past its first expiry; a write on a stale version is refused and nothing is written; and an in-flight item the second
    /// caller took is paused, the reason naming them. No model runs: the item is a throwaway one the test creates in the
    /// Backlog (never Ready, so no factory takes it), in flight only in the throwaway ledger, and its runs are not started.
    /// </summary>
    [Fact]
    public async Task P4b_AT3_a_held_claim_refuses_a_second_caller_is_renewed_past_its_first_expiry_and_a_stale_write_changes_nothing()
    {
        Harness.RequireOptIn();
        var project = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_PROJECT", "the key of a KanbanBoard project the test may create a throwaway item in");
        var otherToken = Harness.RequireEnv("FACTORY_E2E_P4B_KANBAN_OTHER_TOKEN",
            "a full-scope KanbanBoard token of ANOTHER identity than the factory's (the second caller)");
        var writeToken = Harness.RequireSecret(o => o.KanbanWriteToken);
        await using var e2e = await E2e.StartAsync("df_p4b_at3");
        var ct = TestContext.Current.CancellationToken;
        var options = e2e.Options(new Dictionary<string, string?> { [KanbanScope.Setting] = project });
        using var kanbanHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        using var otherHttp = OutboundHttp.KanbanApi(options.KanbanMcpUrl);
        var board = new KanbanBoard(KanbanWrites.Client(kanbanHttp, writeToken));
        var other = new KanbanBoard(KanbanWrites.Client(otherHttp, otherToken));
        var source = WorkSourceRegistry.Kanban(options, kanbanHttp);
        var projectId = (await board.ListProjectsAsync(ct)).Single(p => string.Equals(p.Key, project, StringComparison.OrdinalIgnoreCase)).Id;

        // A throwaway item, created once however often the create is sent (E3: the key decides, not the call).
        var createKey = $"dark-factory:e2e:p4b-at3:{Guid.NewGuid():N}";
        var created = await board.CreateItemAsync(projectId, "[dark-factory fixture] P4b-AT3 safe writes", "Task",
            "Created by the dark-factory acceptance test P4b-AT3; safe to delete.", null, createKey, ct);
        Assert.True(created.Created);
        var guid = created.Id;
        try
        {
            var repeated = await board.CreateItemAsync(projectId, "[dark-factory fixture] P4b-AT3 safe writes", "Task", null, null, createKey, ct);
            Assert.Equal((guid, false), (repeated.Id, repeated.Created));
            Assert.Equal(KanbanStatus.Backlog, created.Status);

            // In flight in the throwaway ledger, as Intake leaves a claimed item: its poll renews its claim.
            var name = await FactoryRunner.KanbanKeyAsync(options, guid, ct);
            var id = (ItemNaming.ParseAny(name) ?? throw new InvalidOperationException($"'{name}' is no work item id.")).Id;
            await using (var seed = e2e.Db())
            {
                var ledger = new WorkLedger(seed, TimeProvider.System);
                var row = await ledger.GetOrCreateAsync(ItemNaming.Kanban.Source, name, created.Title, options.DefaultRepo.FullName, null, ct);
                await ledger.CheckpointAsync(row, RunPipeline.Steps.Claimed, null, null, ct);
                await ledger.RecordAsync(row, WorkState.Implement, null, null, ct);
            }
            var runner = new SeededRunner(e2e, source);
            IntakeLoop Loop() => new([new IntakeLane(source, runner)], new IntakeOptions(TimeSpan.FromSeconds(60)), TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<IntakeLoop>.Instance);

            // The factory holds the item, on a short first lifetime; a second caller's claim on it is refused.
            var marker = KanbanWorkSource.Marker(guid);
            var firstLifetime = TimeSpan.FromSeconds(20);
            var held = await board.ClaimAsync(guid, marker, (int)firstLifetime.TotalSeconds, ct);
            var firstExpiry = held.ClaimExpiresAtUtc ?? throw new InvalidOperationException("The claim's answer names no expiry.");
            var refused = await Assert.ThrowsAsync<KanbanException>(() => other.ClaimAsync(guid, "p4b-at3-second-caller", 300, ct));
            Assert.Equal(KanbanException.AlreadyClaimed, refused.Code);

            // One intake poll renews it for the longest lifetime the board allows.
            await Loop().PollOnceAsync(ct);
            var renewed = await board.GetItemAsync(guid, ct);
            Assert.Equal((marker, held.ClaimantId), (renewed.ClaimMarker, renewed.ClaimantId));
            Assert.True(renewed.ClaimExpiresAtUtc > firstExpiry.AddHours(23), $"renewed until {renewed.ClaimExpiresAtUtc:o}, first expiry {firstExpiry:o}");
            Assert.Empty(runner.Lost);

            // Past its first expiry it is still the factory's, and the second caller is still refused.
            await Task.Delay(firstLifetime + TimeSpan.FromSeconds(5), ct);
            var later = await board.GetItemAsync(guid, ct);
            Assert.Equal((marker, held.ClaimantId), (later.ClaimMarker, later.ClaimantId));
            Assert.Equal(KanbanException.AlreadyClaimed,
                (await Assert.ThrowsAsync<KanbanException>(() => other.ClaimAsync(guid, "p4b-at3-second-caller", 300, ct))).Code);

            // A write on a stale version is refused and writes nothing: the second caller moved the item since the factory read it.
            var read = await board.GetItemAsync(guid, ct);
            var theirs = await other.SetStatusAsync(guid, KanbanStatus.InProgress, read.Version, ct);
            var stale = await Assert.ThrowsAsync<KanbanException>(() => board.SetStatusAsync(guid, KanbanStatus.Done, read.Version, ct));
            Assert.Equal(KanbanException.Conflict, stale.Code);
            var unchanged = await board.GetItemAsync(guid, ct);
            Assert.Equal((KanbanStatus.InProgress, theirs.Version), (unchanged.Status, unchanged.Version));
            Assert.Equal(read.Version + 1, theirs.Version);

            // The second caller takes the item (the factory's claim gone): the next poll pauses it, the reason naming them.
            await board.ReleaseClaimAsync(guid, marker, ct);
            var taken = await other.ClaimAsync(guid, "p4b-at3-second-caller", 300, ct);
            await Loop().PollOnceAsync(ct);
            var what = Assert.Single(runner.Lost);
            Assert.NotNull(what);
            await using var db = e2e.Db();
            var item = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Source == ItemNaming.Kanban.Source && w.ExternalId == name, ct);
            var history = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
            Assert.Equal(WorkState.Paused, item.State);
            var paused = history.Last(e => e.Step is null);
            Assert.True(RunPipeline.IsClaimLost(paused.Detail), paused.Detail);
            Assert.Contains($"claimed by {UntrustedText.CodeSpan(taken.Claimant!)}", paused.Detail);
            Assert.Equal(StepOutcome.Deferred, paused.Outcome);
            Assert.Equal(RunPipeline.Steps.Parked, history[^1].Step);
            // Their claim, their column and the version stand: the factory wrote nothing but its comment.
            var after = await board.GetItemAsync(guid, ct);
            Assert.Equal((taken.ClaimantId, "p4b-at3-second-caller", KanbanStatus.InProgress, theirs.Version),
                (after.ClaimantId, after.ClaimMarker, after.Status, after.Version));
            // Parked: no later poll lists or renews it.
            Assert.DoesNotContain(id, await runner.InFlightAsync(ct));
            Assert.Empty(runner.Runs.Where(r => r == id).Skip(1)); // run (a no-op here) by the first poll only
        }
        finally
        {
            // Best effort: both claims released and the throwaway item closed (hidden from the board), whatever failed above.
            foreach (var release in new Func<Task>[]
            {
                () => other.ReleaseClaimAsync(guid, "p4b-at3-second-caller", CancellationToken.None),
                () => board.ReleaseClaimAsync(guid, KanbanWorkSource.Marker(guid), CancellationToken.None),
                async () => await board.SetStatusAsync(guid, "Closed", (await board.GetItemAsync(guid, CancellationToken.None)).Version, CancellationToken.None),
            })
            {
                try
                {
                    await release();
                }
                catch (Exception ex) when (ex is KanbanException or HttpRequestException)
                {
                }
            }
        }
    }

    /// <summary>
    /// The intake loop's runner over the throwaway ledger for P4b-AT3: the production in-flight listing and lost-claim pause
    /// (<see cref="RunPipeline.InFlightAsync"/>, <see cref="RunPipeline.ClaimLostAsync"/>), and runs that are recorded, not started.
    /// </summary>
    private sealed class SeededRunner(E2e e2e, IWorkSource source) : IItemRunner
    {
        public List<int> Runs { get; } = [];
        public List<string?> Lost { get; } = [];

        public async Task<IReadOnlyList<int>> InFlightAsync(CancellationToken ct)
        {
            await using var db = e2e.Db();
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ItemNaming.Kanban, ct);
        }

        public async Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct)
        {
            await using var db = e2e.Db();
            return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), ItemNaming.Kanban, ct);
        }

        public Task<RunOutcome> RunAsync(int id, CancellationToken ct)
        {
            Runs.Add(id);
            return Task.FromResult(new RunOutcome(0, WorkState.Implement, null, null, null));
        }

        public async Task<string?> ClaimLostAsync(int id, string reason, CancellationToken ct)
        {
            await using var db = e2e.Db();
            var what = await RunPipeline.ClaimLostAsync(source, new WorkLedger(db, TimeProvider.System), new PostgresRunLocks(e2e.Ledger), id, reason,
                Console.Out, ct);
            Lost.Add(what);
            return what;
        }
    }
}
