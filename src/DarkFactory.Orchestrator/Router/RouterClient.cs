using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Router;

public sealed record SessionCost(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("actual_cost_usd_micros")] long ActualCostUsdMicros);

/// <summary>Reads the Weave router's committed cost for a Claude Code session id.</summary>
public sealed class RouterClient(HttpClient http, string routerKey)
{
    /// <summary>Returns null while the router has no committed telemetry for the session (404).</summary>
    public async Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/sessions/{Uri.EscapeDataString(sessionId)}/cost");
        request.Headers.Add(ClaudeWorker.RouterKeyHeader, routerKey);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Router session cost failed: {(int)response.StatusCode}");
        }
        return await response.Content.ReadFromJsonAsync<SessionCost>(ct);
    }
}
