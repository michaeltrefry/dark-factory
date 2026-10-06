using System.Net;
using System.Text;
using DarkFactory.Orchestrator;
using DarkFactory.Orchestrator.Ledger;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.Tests.Support;

public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, Dictionary<string, string> Headers, string? Body);

/// <summary>A fake HTTP API: routes "METHOD /path" to canned responses and records every request.</summary>
public sealed class FakeApi : HttpMessageHandler
{
    private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new();
    public List<RecordedRequest> Requests { get; } = [];

    public FakeApi On(string methodAndPath, HttpStatusCode status, string json) =>
        On(methodAndPath, _ => Json(status, json));

    public FakeApi On(string methodAndPath, Func<RecordedRequest, HttpResponseMessage> handler)
    {
        _routes[methodAndPath] = handler;
        return this;
    }

    public HttpClient Client(string baseAddress) => new(this) { BaseAddress = new Uri(baseAddress) };

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, headers, body);
        Requests.Add(recorded);
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        return _routes.TryGetValue(key, out var handler)
            ? handler(recorded)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no fake route for {key}") };
    }
}

public sealed class InMemorySecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new();
    public string? Get(string account) => Values.GetValueOrDefault(account);
    public void Set(string account, string value) => Values[account] = value;
}

public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public static class TestDb
{
    public static LedgerDbContext Create() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
