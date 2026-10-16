using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>The safe-path assessor uses the same high-class streaming transport as the panel, with a dedicated sealed prompt.</summary>
public sealed class RouterHotFileAssessor(RouterReviewer reviewer, Func<HotFileSnapshot, CancellationToken, Task<string>> evidence,
    ISessionCostSource? costs = null) : IHotFileAssessor
{
    public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var material = await CaptureAsync(snapshot, deadline.Token);
        var request = JsonSerializer.Serialize(new { version = 1, snapshot.Id, snapshot.Generation, snapshot.Repo, snapshot.EvidenceHash,
            materialHash = Hash(material), material });
        var (answer, servedClass) = await reviewer.AssessAsync(session, ReviewPrompts.For("hot-file-assessment").Text, request, deadline.Token);
        var parsed = Parse(answer, servedClass);
        // A safe answer about yesterday's tip/specs/pending heads cannot release today's repository.
        if (parsed.Decision == "safe" && await CaptureAsync(snapshot, deadline.Token) != material)
            parsed = parsed with { Decision = "inconclusive" };
        SessionCost? cost = null;
        if (costs is not null)
        {
            try { cost = await costs.GetSessionCostAsync(session, deadline.Token); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Unknown spend is never zero spend. */ }
        }
        return parsed with { Material = material, MaterialHash = Hash(material), CostUsd = cost is { RequestCount: > 0 } ? cost.ActualCostUsdMicros / 1_000_000m : null };
    }

    private async Task<string> CaptureAsync(HotFileSnapshot snapshot, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var material = await evidence(snapshot, deadline.Token);
        if (Encoding.UTF8.GetByteCount(material) > 2 * 1024 * 1024) throw new InvalidOperationException("Required recovery evidence exceeds 2 MiB; not assessed.");
        return material;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static HotFileAssessment Parse(StreamedAnswer answer, string? servedClass)
    {
        if (answer.StopReason != "end_turn" || answer.ToolUses.Any() || servedClass != "high"
            || Encoding.UTF8.GetByteCount(answer.Text) > 32 * 1024) throw new InvalidOperationException("Unusable high-class assessment stream.");
        using var json = JsonDocument.Parse(answer.Text);
        var root = json.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string[] allowed = ["version", "snapshot", "generation", "repo", "decision", "evidenceHash"];
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name) || !fields.Add(property.Name)) throw new InvalidOperationException("Unknown or duplicate assessment field.");
        if (fields.Count != allowed.Length || root.GetProperty("version").GetInt32() != 1) throw new InvalidOperationException("Assessment schema version/fields invalid.");
        var decision = root.GetProperty("decision").GetString();
        if (decision is not ("safe" or "unsafe" or "inconclusive")) throw new InvalidOperationException("Unsupported recovery decision.");
        return new(root.GetProperty("snapshot").GetString()!, root.GetProperty("generation").GetInt32(), root.GetProperty("repo").GetString()!,
            servedClass, decision, root.GetProperty("evidenceHash").GetString()!);
    }
}

/// <summary>Acquire the concrete safe-path context using only owner-side reads. Missing material throws, never a partial safe prompt.</summary>
public sealed class HotFileEvidence(IDbContextFactory<LedgerDbContext> contexts, IGateGitHub github,
    Func<ItemNaming, IWorkSource> sources)
{
    public async Task<string> CaptureAsync(HotFileSnapshot snapshot, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var repo = RepoRef.Parse(snapshot.Repo);
        var merged = new List<object>();
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var merge in snapshot.Merges)
        {
            var item = await db.WorkItems.AsNoTracking().SingleAsync(i => i.Id == merge.Item, ct);
            var rows = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id && e.Id <= merge.Row).OrderBy(e => e.Id).ToListAsync(ct);
            var link = rows.LastOrDefault(e => e.Step == RunPipeline.Steps.Linked)?.Detail;
            var number = PullNumber(link, repo);
            var pull = await github.GetPullAsync(repo, number, ct);
            if (!pull.Merged || pull.BaseRef != merge.Base || pull.MergeCommitSha != merge.Commit)
                throw new InvalidOperationException("Merged PR evidence does not match its ledger merge.");
            var spec = await SpecAsync(item, ct);
            if (!rows.Any(r => r.Step == RunPipeline.Steps.GatePassed && r.Detail == pull.HeadSha)
                || !rows.Any(r => r.Step == RunPipeline.Steps.Verdict)) throw new InvalidOperationException("Required executed gate/verdict evidence is missing.");
            var diff = await github.GetDiffAsync(repo, pull.BaseSha, pull.HeadSha, ct);
            if (pull.ChangedFiles != DiffPaths.Parse(diff).Files) throw new InvalidOperationException("Required merged diff is incomplete.");
            merged.Add(new { merge, spec, pull, diff, rows = rows.Select(r => new { r.Id, r.State, r.Step, r.Detail, r.Outcome }) });
            if (!tips.ContainsKey(merge.Base)) tips[merge.Base] = (await github.CompareAsync(repo, merge.Base, merge.Commit, ct)).BaseSha;
        }
        var pendingItems = (await db.WorkItems.AsNoTracking().Where(i => i.State != WorkState.Done && i.State != WorkState.Cancelled && i.State != WorkState.Watch)
            .OrderBy(i => i.Id).ToListAsync(ct)).Where(i => RepoRef.Parse(i.Repo).Is(repo)).ToList();
        if (pendingItems.Count > 100) throw new InvalidOperationException("Required pending-work evidence exceeds 100 items.");
        var pending = new List<object>();
        foreach (var item in pendingItems)
        {
            var rows = await db.LedgerEntries.AsNoTracking().Where(e => e.WorkItemId == item.Id).OrderBy(e => e.Id).ToListAsync(ct);
            var link = rows.LastOrDefault(e => e.Step == RunPipeline.Steps.Linked)?.Detail;
            PullFacts? pull = null;
            if (link is not null)
            {
                var number = PullNumber(link, repo);
                pull = await github.GetPullAsync(repo, number, ct);
                if (pull.Number != number || string.IsNullOrWhiteSpace(pull.HeadSha))
                    throw new InvalidOperationException("Required pending PR head evidence is inconsistent.");
            }
            else if (item.State is WorkState.Review or WorkState.Fixing or WorkState.CI or WorkState.CIHealing or WorkState.MergeGate or WorkState.Merge)
                throw new InvalidOperationException("Required pending PR link is missing.");
            pending.Add(new { item.Id, item.ExternalId, item.State, item.PlanItemId, item.EpicId, pull, spec = await SpecAsync(item, ct),
                rows = rows.Select(r => new { r.Id, r.State, r.Step, r.Detail }) });
        }
        return JsonSerializer.Serialize(new { tips, merged, pending });
    }

    private static int PullNumber(string? link, RepoRef repo)
    {
        var url = link?.Split(' ', '\n').FirstOrDefault(p => p.StartsWith("https://", StringComparison.Ordinal));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com")
            throw new InvalidOperationException("Required PR link is missing.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !RepoRef.Parse(parts[0] + "/" + parts[1]).Is(repo) || parts[2] != "pull"
            || !int.TryParse(parts[3], out var number) || number <= 0)
            throw new InvalidOperationException("PR link does not name the incident repository.");
        return number;
    }

    private async Task<WorkSpec> SpecAsync(WorkItem item, CancellationToken ct)
    {
        var reference = ItemNaming.ParseAny(item.ExternalId) ?? throw new InvalidOperationException("Unknown evidence work source.");
        return await sources(reference.Naming).ReadSpecAsync(reference.Id, ct);
    }
}
