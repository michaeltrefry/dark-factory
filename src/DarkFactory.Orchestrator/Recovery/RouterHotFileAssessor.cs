using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Router;

namespace DarkFactory.Orchestrator.Recovery;

/// <summary>A fresh high-class assessment over immutable evidence, using the panel's bounded streaming tool loop.</summary>
public sealed class RouterHotFileAssessor(RouterReviewer reviewer, Func<HotFileSnapshot, CancellationToken, Task<string>> evidence,
    ISessionCostSource? costs = null, IReviewFiles? files = null) : IHotFileAssessor
{
    public async Task<HotFileAssessment> AssessAsync(HotFileSnapshot snapshot, string session, CancellationToken ct)
    {
        var material = await CaptureAsync(snapshot, ct);
        var captured = RecoverySnapshot.FromDetail(material)
            ?? throw new InvalidOperationException("Required recovery snapshot is not readable.");
        if (!captured.Complete || captured.IncidentSnapshot != snapshot.Id || captured.Repo != snapshot.Repo || captured.Generation != snapshot.Generation)
            throw new InvalidOperationException("Required recovery snapshot is incomplete or names another incident.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var prompt = ReviewPrompts.For("hot-file-assessment");
        var request = JsonSerializer.Serialize(new { version = 1, snapshot.Id, snapshot.Generation, snapshot.Repo, snapshot.EvidenceHash,
            materialHash = RecoverySnapshot.Hash(material), material = captured });
        var bindings = new RecoveryAssessmentTools(captured, files, request.Length + prompt.Text.Length).Bindings;
        AssessmentConversation? conversation = null;
        HotFileAssessment Inconclusive(string why) => new(snapshot.Id, snapshot.Generation, snapshot.Repo, conversation?.Class,
            "inconclusive", snapshot.EvidenceHash) { Problem = why, MaterialSnapshot = captured.Id };
        HotFileAssessment parsed;
        try
        {
            conversation = await reviewer.AssessAsync(session, prompt.Text, request, bindings, deadline.Token);
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
            try { cost = await costs.GetSessionCostAsync(session, deadline.Token); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Unknown spend is never zero spend. */ }
        }
        // Freshness gates dispositions as well as safe resolution. Revalidate after accounting, just before returning.
        if (parsed.Decision is "safe" or "unsafe")
        {
            try
            {
                if (await CaptureAsync(snapshot, ct) != material) parsed = Inconclusive("Snapshot evidence changed during assessment.");
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

    private async Task<string> CaptureAsync(HotFileSnapshot snapshot, CancellationToken ct)
    {
        var material = await evidence(snapshot, ct);
        if (Encoding.UTF8.GetByteCount(material) > RecoverySnapshot.MaxBytes)
            throw new InvalidOperationException("Required recovery evidence exceeds 2 MiB; not assessed.");
        return material;
    }
}
