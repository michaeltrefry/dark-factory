using System.Net;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DarkFactory.Orchestrator;

/// <summary>
/// The long-running <c>factory work</c> host: Kestrel on 127.0.0.1 serving <see cref="SessionHub"/>,
/// fed by <see cref="SessionEventRelay"/>. Long-running services (e.g. intake) register as hosted
/// services, one line each, in <see cref="Build"/>.
/// </summary>
public static class FactoryHost
{
    /// <summary>Host names the hub answers to (DNS-rebinding guard): it is loopback-only and has no login yet (S7).</summary>
    public static readonly string[] AllowedHosts = ["127.0.0.1", "localhost"];

    /// <param name="configure">Extra or replacement services (tests swap the router cost source here).</param>
    public static WebApplication Build(FactoryOptions options, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Loopback only until the host has a login (S7).
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.HostPort));
        builder.Services.AddHostFiltering(o =>
        {
            o.AllowedHosts = AllowedHosts;
            o.AllowEmptyHosts = false;
        });
        builder.Services.AddSessionCapture(options);
        configure?.Invoke(builder.Services);

        var app = builder.Build(); // the web host applies host filtering ahead of everything
        // Browsers send Origin on cross-site WebSocket/fetch requests: only same-origin pages may use the hub.
        // Non-browser clients send no Origin and are allowed (the host filter above still applies).
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(SessionHub.Path)
                && context.Request.Headers.Origin is { Count: > 0 } origin
                && !IsSameOrigin(origin.ToString(), context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapHub<SessionHub>(SessionHub.Path);
        return app;
    }

    private static bool IsSameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o)
        && string.Equals($"{o.Scheme}://{o.Authority}", $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The session hub, its ledger relay, and a <see cref="SessionRecorder"/> for pipelines run in
    /// this host. Events reach viewers through the relay, wherever they were recorded.
    /// </summary>
    public static IServiceCollection AddSessionCapture(this IServiceCollection services, FactoryOptions options)
    {
        services.AddSignalR();
        services.AddSingleton<IDbContextFactory<LedgerDbContext>>(
            new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)));
        services.AddSingleton(new SessionHubOptions());
        services.AddSingleton(sp => new SessionBroadcaster(
            sp.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<SessionHub>>(),
            sp.GetRequiredService<IDbContextFactory<LedgerDbContext>>(),
            sp.GetRequiredService<SessionHubOptions>(),
            Console.Out));
        services.AddSingleton(sp => new SessionEventRelay(options.LedgerConnectionString, sp.GetRequiredService<SessionBroadcaster>(), Console.Out));
        services.AddHostedService(sp => sp.GetRequiredService<SessionEventRelay>());
        services.AddSingleton<ISessionCostSource>(_ =>
            new RouterClient(new HttpClient { BaseAddress = options.RouterBaseUrl }, options.RouterKey));
        services.AddSingleton(sp => new SessionRecorder(
            sp.GetRequiredService<IDbContextFactory<LedgerDbContext>>(),
            sp.GetRequiredService<ISessionCostSource>(),
            TimeProvider.System,
            Console.Out));
        return services;
    }

    /// <summary>The host's base address once started, e.g. <c>http://127.0.0.1:47822</c>.</summary>
    public static string Address(this WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
}
