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
        var source = FactoryRunner.CreateKanbanSource(options, kanbanHttp);
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
            IntakeLoop Loop() => new(source, runner, new IntakeOptions(TimeSpan.FromSeconds(60)), TimeProvider.System,
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
            return await RunPipeline.InFlightAsync(new WorkLedger(db, TimeProvider.System), ct, naming: ItemNaming.Kanban);
        }

        public async Task<IReadOnlyList<int>> RenewableAsync(CancellationToken ct)
        {
            await using var db = e2e.Db();
            return await RunPipeline.RenewableAsync(new WorkLedger(db, TimeProvider.System), ct, naming: ItemNaming.Kanban);
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
