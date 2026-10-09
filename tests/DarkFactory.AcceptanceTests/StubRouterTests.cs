using System.Net;
using System.Net.Http.Json;
using DarkFactory.Orchestrator.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.AcceptanceTests;

/// <summary>AT5's stub router itself (not live): it must answer the usage read and proxy everything else.</summary>
public class StubRouterTests
{
    [Fact]
    public async Task Usage_read_is_answered_by_the_stub_and_every_other_path_reaches_the_real_router()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        upstream.MapFallback("{**path}", (HttpContext c) => Results.Text($"upstream {c.Request.Path}"));
        await upstream.StartAsync(ct);
        var upstreamUrl = new Uri(upstream.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.First());

        await using var stub = await StubRouter.StartAsync(upstreamUrl, DateTimeOffset.UtcNow.AddMinutes(2), ct);
        using var http = OutboundHttp.RouterApi(stub.BaseUrl);

        var usage = await http.GetFromJsonAsync<SubscriptionUsage>("v1/subscriptions/usage", ct);
        Assert.NotNull(usage);
        Assert.True(usage.AllExhausted);
        Assert.Equal(1, usage.KnownCredentials);
        Assert.Equal(stub.ResumesAt, usage.ResumesAt);
        Assert.Equal(1, stub.UsageReads);

        Assert.Equal("upstream /v1/sessions/abc/cost", await http.GetStringAsync("v1/sessions/abc/cost", ct));
        Assert.Equal("upstream /v1/messages.json", await http.GetStringAsync("v1/messages.json", ct));
    }
}
