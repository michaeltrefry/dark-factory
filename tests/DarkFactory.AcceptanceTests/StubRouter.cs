using System.Net;
using DarkFactory.Orchestrator.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.AcceptanceTests;

/// <summary>
/// AT5's stub router: an in-test Kestrel on 127.0.0.1 that answers <c>GET /v1/subscriptions/usage</c> with every plan
/// exhausted until <see cref="ResumesAt"/> (and nothing exhausted from then on), and proxies every other request —
/// model calls (streamed), session costs — unchanged to the real router.
/// </summary>
internal sealed class StubRouter : IAsyncDisposable
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host",
    };

    private readonly WebApplication _app;
    private readonly HttpClient _upstream;
    private int _usageReads;

    private StubRouter(WebApplication app, HttpClient upstream, DateTimeOffset resumesAt) =>
        (_app, _upstream, ResumesAt) = (app, upstream, resumesAt);

    public DateTimeOffset ResumesAt { get; }

    /// <summary>How many usage reports the stub has served (the factory polls it).</summary>
    public int UsageReads => Volatile.Read(ref _usageReads);

    public Uri BaseUrl => new(_app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First());

    public static async Task<StubRouter> StartAsync(Uri realRouter, DateTimeOffset resumesAt, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        // No automatic decompression: bodies pass through byte for byte with their Content-Encoding.
        var upstream = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = realRouter,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var stub = new StubRouter(app, upstream, resumesAt);
        app.MapGet("/v1/subscriptions/usage", stub.Usage);
        // A catch-all endpoint, not app.Run: terminal middleware runs before any endpoint, so it would proxy the usage
        // read too. "{**path}" rather than MapFallback's default pattern, which skips paths that look like file names.
        app.MapFallback("{**path}", stub.ProxyAsync);
        await app.StartAsync(ct);
        return stub;
    }

    private IResult Usage()
    {
        Interlocked.Increment(ref _usageReads);
        var now = DateTimeOffset.UtcNow;
        return Results.Json(now < ResumesAt
            ? new SubscriptionUsage(now, AllExhausted: true, ResumesAt, KnownCredentials: 1)
            : new SubscriptionUsage(now, AllExhausted: false, null, KnownCredentials: 1));
    }

    private async Task ProxyAsync(HttpContext context)
    {
        var request = context.Request;
        using var forward = new HttpRequestMessage(new HttpMethod(request.Method),
            new Uri(_upstream.BaseAddress!, request.Path.Value!.TrimStart('/') + request.QueryString));
        if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            forward.Content = new StreamContent(request.Body);
        }
        foreach (var (name, values) in request.Headers)
        {
            if (HopByHop.Contains(name))
            {
                continue;
            }
            if (!forward.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values))
            {
                forward.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)values);
            }
        }
        using var response = await _upstream.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!HopByHop.Contains(name))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }
        // Streamed (server-sent events for model calls): each chunk is written and flushed as it arrives.
        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _upstream.Dispose();
    }
}
