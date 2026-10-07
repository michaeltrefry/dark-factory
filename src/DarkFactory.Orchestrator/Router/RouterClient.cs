using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Router;

public sealed record SessionCost(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("request_count")] long RequestCount,
    [property: JsonPropertyName("actual_cost_usd_micros")] long ActualCostUsdMicros);

/// <summary>Where a session's spend comes from (E9).</summary>
public interface ISessionCostSource
{
    /// <summary>Returns null while no cost is committed for the session yet.</summary>
    Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// The router's subscription usage (<c>GET /v1/subscriptions/usage</c>), as far as the factory uses it.
/// <see cref="AllExhausted"/> is true only when at least one routable credential is known and every one is exhausted
/// (paid overage included); <see cref="ResumesAt"/> is the earliest known resume. Readings live in the router
/// process's memory, so after a router restart nothing is known until traffic flows.
/// <see cref="Credentials"/> lists the credentials the caller's router key can be routed onto, enrolled plans included.
/// </summary>
public sealed record SubscriptionUsage(
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf,
    [property: JsonPropertyName("all_exhausted")] bool AllExhausted,
    [property: JsonPropertyName("resumes_at")] DateTimeOffset? ResumesAt,
    [property: JsonPropertyName("known_credentials")] int KnownCredentials,
    [property: JsonPropertyName("credentials")] IReadOnlyList<UsageCredential>? Credentials = null)
{
    /// <summary>
    /// Plans enrolled on the router for this key (<c>router login claude|codex</c>: source <c>managed</c>, or
    /// <c>shared</c> by another member) and not disabled. Credentials presented or observed from a client's own login
    /// do not count: in <c>router-key</c> mode the worker presents none.
    /// </summary>
    public int EnrolledCredentials =>
        Credentials?.Count(c => c.Source is "managed" or "shared" && c.Enabled != false) ?? 0;
}

/// <summary>One credential of the router's usage report, as far as the enrollment check uses it (no token is ever reported).</summary>
public sealed record UsageCredential(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("enabled")] bool? Enabled = null);

/// <summary>Where the factory reads whether its plans are exhausted.</summary>
public interface IUsageSource
{
    Task<SubscriptionUsage> GetUsageAsync(CancellationToken ct);
}

/// <summary>Reads the Weave router's committed cost for a Claude Code session id, and its subscription usage.</summary>
public sealed class RouterClient(HttpClient http, string routerKey) : ISessionCostSource, IUsageSource
{
    /// <summary>Throws when the router does not answer with a usage report.</summary>
    public async Task<SubscriptionUsage> GetUsageAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/subscriptions/usage");
        request.Headers.Add(ClaudeWorker.RouterKeyHeader, routerKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Router subscription usage failed: {(int)response.StatusCode}");
        }
        return await response.Content.ReadFromJsonAsync<SubscriptionUsage>(ct)
            ?? throw new InvalidOperationException("Router subscription usage was empty.");
    }

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
