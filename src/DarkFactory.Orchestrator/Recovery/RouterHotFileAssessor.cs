using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>A fresh high-class assessment over immutable evidence, using the panel's bounded streaming tool loop.</summary>
public sealed class RouterHotFileAssessor(RouterReviewer reviewer, Func<HotFileSnapshot, CancellationToken, Task<string>> evidence,
    ISessionCostSource? costs = null, IReviewFiles? files = null,
    TimeSpan? costSettleDelay = null, IReadOnlyList<TimeSpan>? costRetryDelays = null,
    Func<TimeSpan, CancellationToken, Task>? costDelay = null, TimeProvider? time = null) : IHotFileAssessor
{
    public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
    {
        var material = await CaptureAsync(snapshot, ct);
        var captured = RecoverySnapshot.FromDetail(material)
            ?? throw new InvalidOperationException("Required recovery snapshot is not readable.");
        if (!captured.Complete || captured.IncidentSnapshot != snapshot.Id || captured.Repo != snapshot.Repo || captured.Generation != snapshot.Generation)
            throw new InvalidOperationException("Required recovery snapshot is incomplete or names another incident.");
        var prompt = ReviewPrompts.For("hot-file-assessment");
        // An index, not the bodies: up to 2 MiB of evidence inline would leave the tool budget at its floor and overrun the
        // context. Every entry is read by id through read_evidence, which serves exactly these hashed bodies.
        var request = JsonSerializer.Serialize(new { version = 1, snapshot.Id, snapshot.Generation, snapshot.Repo, snapshot.EvidenceHash,
            materialHash = RecoverySnapshot.Hash(material), material = Index(captured) });
        var bindings = new RecoveryAssessmentTools(captured, files, request.Length + prompt.Text.Length).Bindings;
        AssessmentConversation? conversation = null;
        HotFileAssessment Inconclusive(string why) => new(snapshot.Id, snapshot.Generation, snapshot.Repo, conversation?.Class,
            "inconclusive", snapshot.EvidenceHash) { Problem = why, MaterialSnapshot = captured.Id };
        HotFileAssessment parsed;
        try
        {
            conversation = await reviewer.AssessAsync(session, prompt.Text, request, bindings, ct);
            if (conversation.Class != "high" || conversation.Stop != "end_turn" || conversation.Problem is not null)
                throw new InvalidOperationException(conversation.Problem ?? "Unusable high-class assessment stream.");
            parsed = RecoveryDecision.Parse(conversation.Text, snapshot, captured);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            parsed = Inconclusive(ex.Message);
        }
        SessionCost? cost = null;
        if (costs is not null)
        {
            var retries = costRetryDelays ?? SessionRecorder.DefaultCostRetryDelays;
            var settle = costSettleDelay ?? SessionRecorder.DefaultCostSettleDelay;
            // Accounting is bounded separately from the conversation; its deadline may already have expired.
            using var accounting = new CancellationTokenSource(TimeSpan.FromSeconds(30) + settle
                + TimeSpan.FromTicks(retries.Sum(d => d.Ticks)), time ?? TimeProvider.System);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct, accounting.Token);
            try
            {
                await SessionCostAccounting.CollectAsync(costs, session, retries, settle,
                    costDelay ?? ((d, token) => Task.Delay(d, time ?? TimeProvider.System, token)), TextWriter.Null,
                    measured => { cost = measured; return Task.CompletedTask; }, bound.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Unknown spend is never zero spend. */ }
        }
        // Freshness gates dispositions as well as safe resolution. Revalidate after accounting, just before returning.
        if (parsed.Decision is "safe" or "unsafe")
        {
            try
            {
                if (await CaptureAsync(snapshot, ct) != material) parsed = Inconclusive("Snapshot evidence changed during assessment.") with { EvidenceChanged = true };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                parsed = Inconclusive("Snapshot evidence could not be revalidated.");
            }
        }
        return parsed with { Material = material, MaterialHash = RecoverySnapshot.Hash(material), Session = session, Prompt = prompt.Id,
            Tools = conversation?.Tools ?? [], Turns = conversation?.Turns ?? [],
            CostUsd = cost is { RequestCount: > 0 } ? cost.ActualCostUsdMicros / 1_000_000m : null };
    }

    /// <summary>The material as the model is first shown it: identity, base tips, what is missing, and per evidence entry its id,
    /// category, provenance, commits, SHA-256 and UTF-8 size — never its content.</summary>
    internal static object Index(RecoverySnapshot material) => new
    {
        material.Id, material.IncidentSnapshot, material.Repo, material.Generation, material.BaseTips, material.Missing,
        Evidence = material.Evidence.Select(e => new { e.Id, e.Category, e.Provenance, e.Commit, e.BaseCommit, e.Hash,
            Bytes = Encoding.UTF8.GetByteCount(e.Content) }).ToArray(),
    };

    private async Task<string> CaptureAsync(HotFileSnapshot snapshot, CancellationToken ct)
    {
        var material = await evidence(snapshot, ct);
        if (Encoding.UTF8.GetByteCount(material) > RecoverySnapshot.MaxBytes)
            throw new InvalidOperationException("Required recovery evidence exceeds 2 MiB; not assessed.");
        return material;
    }
}
