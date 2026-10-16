using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>Content, not a live URL: its hash and origin survive subsequent board edits and branch movement.</summary>
public sealed record RecoveryEvidence(string Id, string Category, string Provenance, string? Commit, string Hash, string Content);
public sealed record MissingRecoveryEvidence(string Category, string Reason);

/// <summary>
/// Immutable acquisition, identified by its canonical content. The append-only ledger retains every acquisition used for a
/// model call, including incomplete ones. No captured timestamp is compared as evidence; commits, source content and ledger
/// versions are. File tools must use these commits, never the branch names in BaseTips.
/// </summary>
public sealed record RecoverySnapshot(string Id, string IncidentSnapshot, string Repo, int Generation,
    IReadOnlyDictionary<string, string> BaseTips, IReadOnlyList<RecoveryEvidence> Evidence,
    IReadOnlyList<MissingRecoveryEvidence> Missing)
{
    public const string Step = "recovery-snapshot";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxMerges = 50;
    public const int MaxPending = 100;
    public bool Complete => Missing.Count == 0;
    public static RecoverySnapshot? FromDetail(string? detail)
    {
        try { return detail is null ? null : JsonSerializer.Deserialize<RecoverySnapshot>(detail); }
        catch (JsonException) { return null; }
    }
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Owner-side read-only evidence acquisition. Work sources provide whole specs (epic and documents included); gate reads
/// explicitly report truncated trees/paged CI. A missing page, parent, result or byte budget is a named incompleteness,
/// never an omitted prerequisite. Acquisition itself writes only append-only evidence, never controls or work state.
/// </summary>
public sealed class HotFileEvidence(IDbContextFactory<LedgerDbContext> contexts, IGateGitHub github,
    Func<ItemNaming, IWorkSource> sources)
{
    public async Task<string> CaptureAsync(HotFileSnapshot trigger, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(trigger, ct);
        var material = JsonSerializer.Serialize(snapshot);
        // Store BEFORE the router call, not only in the answer after a session returns. A crash cannot lose its input.
        var anchor = trigger.Merges.FirstOrDefault()?.Item;
        await using var db = await contexts.CreateDbContextAsync(ct);
        anchor ??= await db.WorkItems.OrderBy(i => i.Id).Where(i => i.Repo.ToLower() == trigger.Repo.ToLower())
            .Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        if (anchor is { } itemId && !await db.LedgerEntries.AnyAsync(e => e.WorkItemId == itemId
                && e.Step == RecoverySnapshot.Step && e.Detail == material, ct))
        {
            var state = (await db.WorkItems.SingleAsync(i => i.Id == itemId, ct)).State;
            db.LedgerEntries.Add(new LedgerEntry { WorkItemId = itemId, State = state,
                Step = RecoverySnapshot.Step, Detail = material, RecordedAt = DateTimeOffset.UtcNow,
                Outcome = StepOutcomes.Of(state, state, RecoverySnapshot.Step, material) });
            await db.SaveChangesAsync(ct);
        }
        if (!snapshot.Complete) throw new InvalidOperationException("Incomplete recovery snapshot: "
            + string.Join("; ", snapshot.Missing.Select(m => $"{m.Category}: {m.Reason}")));
        return material;
    }

    public async Task<RecoverySnapshot> SnapshotAsync(HotFileSnapshot trigger, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var evidence = new List<RecoveryEvidence>();
        var missing = new List<MissingRecoveryEvidence>();
        var tips = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var repo = RepoRef.Parse(trigger.Repo);
        var budget = 0;
        await using var db = await contexts.CreateDbContextAsync(token);
        try
        {
            if (trigger.Merges.Count > RecoverySnapshot.MaxMerges)
                Missing("contributing-merges", $"More than {RecoverySnapshot.MaxMerges} merges.");
            else foreach (var merge in trigger.Merges)
            {
                await Read("merged", async () =>
                {
                    var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Id == merge.Item, token);
                    var rows = await Rows(item.Id, merge.Row);
                    if (!rows.Any(r => r.Id == merge.Row && r.Step == null && r.State == WorkState.Merge && r.Detail == merge.Commit))
                        throw new InvalidOperationException("The contributing merge does not match the ledger.");
                    var number = PullNumber(rows.LastOrDefault(e => e.Step == RunPipeline.Steps.Linked)?.Detail, repo);
                    var pull = await github.GetPullAsync(repo, number, token);
                    if (!pull.Merged || pull.Number != number || pull.BaseRef != merge.Base || pull.MergeCommitSha != merge.Commit
                        || string.IsNullOrWhiteSpace(pull.HeadSha) || string.IsNullOrWhiteSpace(pull.BaseSha))
                        throw new InvalidOperationException("Merged PR does not match its ledger merge.");
                    Add("merged", $"merge:{merge.Row}", $"ledger:{merge.Row}; PR:{number}", merge.Commit, new { merge, pull });
                    await Spec(item);
                    await Plan(item);
                    if (!rows.Any(r => r.Step == RunPipeline.Steps.GatePassed && r.Detail == pull.HeadSha)
                        || !rows.Any(r => r.Step == RunPipeline.Steps.Verdict))
                        Missing("executed-results", $"{item.ExternalId}: required executed gate/verdict missing.");
                    Add("executed-results", $"results:{merge.Row}", $"ledger through merge:{merge.Row}", pull.HeadSha, Results(rows));
                    await Read("merged-diff", async () =>
                    {
                        var diff = await github.GetDiffAsync(repo, pull.BaseSha, pull.HeadSha, token);
                        if (pull.ChangedFiles != DiffPaths.Parse(diff).Files) throw new InvalidOperationException("Missing diff page/files.");
                        Add("merged-diff", $"diff:{merge.Row}", $"PR:{number}; {pull.BaseSha}...{pull.HeadSha}", pull.HeadSha, diff);
                    });
                    await Read("ci", async () =>
                    {
                        var ci = await github.GetRecoveryCiAsync(repo, pull.HeadSha, token);
                        if (!ci.Complete || ci.HeadSha != pull.HeadSha) throw new InvalidOperationException("Missing CI page or wrong head.");
                        Add("ci", $"ci:{merge.Row}", $"GitHub executed checks:{pull.HeadSha}", pull.HeadSha, ci);
                    });
                    if (!tips.ContainsKey(merge.Base)) await Read("base-tip", async () =>
                    {
                        var tip = (await github.CompareAsync(repo, merge.Base, merge.Commit, token)).BaseSha;
                        if (string.IsNullOrWhiteSpace(tip)) throw new InvalidOperationException("No base tip.");
                        tips[merge.Base] = tip;
                    });
                });
            }
            var items = (await db.WorkItems.AsNoTracking().Where(i => i.State != WorkState.Done && i.State != WorkState.Cancelled && i.State != WorkState.Watch)
                .OrderBy(i => i.Id).ToListAsync(token)).Where(i => RepoRef.Parse(i.Repo).Is(repo)).ToList();
            if (items.Count > RecoverySnapshot.MaxPending) Missing("pending-work", $"More than {RecoverySnapshot.MaxPending} pending items.");
            else foreach (var item in items)
            {
                await Spec(item);
                await Plan(item);
                await Read("pending", async () =>
                {
                    var rows = await Rows(item.Id);
                    var link = rows.LastOrDefault(e => e.Step == RunPipeline.Steps.Linked)?.Detail;
                    PullFacts? pull = null;
                    if (link is not null)
                    {
                        var number = PullNumber(link, repo);
                        pull = await github.GetPullAsync(repo, number, token);
                        if (pull.Number != number || string.IsNullOrWhiteSpace(pull.HeadSha)) throw new InvalidOperationException("Missing/wrong pending PR head.");
                    }
                    else if (item.State is WorkState.Review or WorkState.Fixing or WorkState.CI or WorkState.CIHealing or WorkState.MergeGate or WorkState.Merge)
                        throw new InvalidOperationException("Missing pending PR link.");
                    string? head = pull?.HeadSha;
                    var pushed = rows.LastOrDefault(r => r.Step == RunPipeline.Steps.Pushed)?.Detail;
                    if (pull is null && pushed is not null)
                    {
                        // Initial Implement's pushed checkpoint names the branch; fix rounds may record a commit.
                        // Resolve the remote branch in either case, never treat the checkpoint text as an immutable head.
                        var pendingRef = Reference(item);
                        var branch = pushed.StartsWith("factory/", StringComparison.Ordinal) ? pushed : pendingRef.Naming.BranchName(pendingRef.Id);
                        head = (await github.CompareAsync(repo, branch, branch, token)).BaseSha;
                        if (string.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("Missing pending branch tip.");
                    }
                    else if (pull is null && item.State == WorkState.Implement && rows.Any(r => r.Step == RunPipeline.Steps.WorkerStarted))
                        throw new InvalidOperationException("Unpublished coding work has no reproducible pending head.");
                    var reference = Reference(item);
                    var blockers = await sources(reference.Naming).BlockersAsync(reference.Id, token);
                    Add("pending", $"pending:{item.Id}", $"ledger:{item.Id}@{item.Version}; {item.ExternalId}", pull?.HeadSha,
                        new { item.Id, item.ExternalId, item.State, item.Version, item.PlanItemId, item.EpicId, pull, blockers,
                            head, headState = head is null ? "not-pushed" : "published",
                            rows = Results(rows) });
                });
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Missing("acquisition-deadline", "Required context did not arrive within 5 minutes."); }
        var body = JsonSerializer.Serialize(new { trigger.Id, trigger.Repo, trigger.Generation, tips, evidence, missing });
        var result = new RecoverySnapshot(RecoverySnapshot.Hash(body), trigger.Id, trigger.Repo, trigger.Generation, tips, evidence.ToArray(), missing.ToArray());
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > RecoverySnapshot.MaxBytes)
        {
            Missing("context-bytes", "Required serialized context exceeds 2 MiB.");
            result = result with { Evidence = [], Missing = missing.ToArray() };
            result = result with { Id = RecoverySnapshot.Hash(JsonSerializer.Serialize(result with { Id = "" })) };
        }
        return result;

        void Missing(string category, string reason) => missing.Add(new(category, reason));
        void Add(string category, string id, string provenance, string? commit, object value)
        {
            if (evidence.Any(e => e.Id == id)) return;
            var content = value is string text ? text : JsonSerializer.Serialize(value);
            var entry = new RecoveryEvidence(id, category, provenance, commit, RecoverySnapshot.Hash(content), content);
            budget += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry));
            if (budget > RecoverySnapshot.MaxBytes)
            {
                if (!missing.Any(m => m.Category == "context-bytes")) Missing("context-bytes", "Required serialized context exceeds 2 MiB.");
                return;
            }
            evidence.Add(entry);
        }
        async Task Read(string category, Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (!token.IsCancellationRequested)
            { Missing(category, ex.Message); }
        }
        async Task Spec(WorkItem item) => await Read("spec", async () =>
        {
            if (evidence.Any(e => e.Id == $"spec:{item.Id}")) return;
            var reference = Reference(item);
            var spec = await sources(reference.Naming).ReadSpecAsync(reference.Id, token);
            if (spec.Story.Ref != item.ExternalId || item.EpicId is { } epic && spec.Epic?.Id != epic)
                throw new InvalidOperationException($"{item.ExternalId}: spec/epic identity mismatch.");
            Add("spec", $"spec:{item.Id}", $"{item.ExternalId}; {spec.Story.AppUrl}; epic and whole documents", null, spec);
        });
        async Task Plan(WorkItem item)
        {
            if (item.PlanItemId is not { } parentId) return;
            await Read("plan", async () =>
            {
                if (evidence.Any(e => e.Id == $"plan:{parentId}")) return;
                var parent = await db.WorkItems.AsNoTracking().SingleAsync(p => p.Id == parentId, token);
                await Spec(parent);
                var rows = await Rows(parentId);
                if (!rows.Any(r => r.Step == "plan")) throw new InvalidOperationException("Parent plan requirements missing.");
                Add("plan", $"plan:{parentId}", $"ledger:{parentId}@{parent.Version}; {parent.ExternalId}", null,
                    new { parent.Id, parent.ExternalId, parent.Version, rows = Results(rows) });
            });
        }
        async Task<List<LedgerEntry>> Rows(long item, long? through = null) => await db.LedgerEntries.AsNoTracking()
            .Where(e => e.WorkItemId == item && e.Step != RecoverySnapshot.Step && (through == null || e.Id <= through))
            .OrderBy(e => e.Id).ToListAsync(token);
    }

    private static object Results(IEnumerable<LedgerEntry> rows) => rows.Select(r => new { r.Id, r.State, r.Step, r.Detail, r.Outcome }).ToArray();
    private static ItemRef Reference(WorkItem item) => ItemNaming.ParseAny(item.ExternalId)
        ?? throw new InvalidOperationException("Unknown evidence work source.");
    private static int PullNumber(string? link, RepoRef repo)
    {
        var url = link?.Split(' ', '\n').FirstOrDefault(p => p.StartsWith("https://", StringComparison.Ordinal));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com") throw new InvalidOperationException("Required PR link is missing.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !RepoRef.Parse(parts[0] + "/" + parts[1]).Is(repo) || parts[2] != "pull"
            || !int.TryParse(parts[3], out var number) || number <= 0) throw new InvalidOperationException("PR link does not name the incident repository.");
        return number;
    }
}
