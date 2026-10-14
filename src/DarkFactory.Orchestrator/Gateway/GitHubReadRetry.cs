using System.Net;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// Retries GitHub reads on a transient answer (sc-25992: a single 503 on a pull request read escalated an item in the live P4b-AT5
/// run): a GET or HEAD answered 502, 503 or 504, failed in the network (<see cref="HttpRequestException"/>), or with no response
/// headers within <see cref="AttemptTimeout"/>, is sent again after <see cref="Backoff"/> (1 s, then 2 s), at most
/// <see cref="MaxAttempts"/> times in all. The last attempt's answer or failure is the caller's, exactly as without a retry, and the
/// last attempt has no timeout of its own (the client's overall timeout bounds it as before). Nothing else is retried: a write
/// (POST, PATCH, PUT, DELETE) may have been processed, and keeps its caller's own idempotency rules. A cancellation by the caller
/// is never retried. Like the Shortcut client's retries of its reads; GitHub's other answers (a 4xx, rate limits) are unchanged.
/// </summary>
internal sealed class GitHubReadRetry(HttpMessageHandler inner, TimeProvider time) : DelegatingHandler(inner)
{
    /// <summary>Attempts per read, the first included.</summary>
    public const int MaxAttempts = 3;

    /// <summary>How long an attempt but the last waits for GitHub's response headers before it counts as transient.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The wait before attempt <paramref name="next"/> (2, 3, …): 1 s, then 2 s.</summary>
    public static TimeSpan Backoff(int next) => TimeSpan.FromSeconds(1 << (next - 2));

    private static bool IsRead(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsRead(request.Method))
        {
            return await base.SendAsync(request, ct);
        }
        for (var attempt = 1; ; attempt++)
        {
            if (attempt == MaxAttempts)
            {
                return await base.SendAsync(request, ct);
            }
            using var timeout = new CancellationTokenSource(AttemptTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var response = await base.SendAsync(request, linked.Token);
                if (!IsTransient(response.StatusCode))
                {
                    return response;
                }
                response.Dispose();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
            }
            await Task.Delay(Backoff(attempt + 1), time, ct);
        }
    }
}
