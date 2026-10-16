using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Router;

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
        if (parsed.Decision is "safe" or "unsafe")
        {
            try
            {
                if (await CaptureAsync(snapshot, deadline.Token) != material)
                    parsed = parsed with { Decision = "inconclusive" };
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Missing context on readback is not evidence that the original capture is still current.
                parsed = parsed with { Decision = "inconclusive" };
            }
        }
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
