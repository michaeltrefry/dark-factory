using System.Net;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DarkFactory.Orchestrator;

/// <summary>
/// The long-running <c>factory work</c> host: Kestrel on 127.0.0.1 serving <see cref="SessionHub"/>.
/// Long-running services (e.g. intake) register as hosted services, one line each, in <see cref="Build"/>.
/// </summary>
public static class FactoryHost
{
    /// <param name="configure">Extra or replacement services (tests swap the router cost source here).</param>
    public static WebApplication Build(FactoryOptions options, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Loopback only until the host has a login (S7).
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.HostPort));
        builder.Services.AddSessionCapture(options);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapHub<SessionHub>(SessionHub.Path);
        return app;
    }

    /// <summary>
    /// The session hub and a <see cref="SessionRecorder"/> whose events reach its viewers live;
    /// pipelines run in this host take the recorder from here.
    /// </summary>
    public static IServiceCollection AddSessionCapture(this IServiceCollection services, FactoryOptions options)
    {
        services.AddSignalR();
        services.AddSingleton<IDbContextFactory<LedgerDbContext>>(
            new LedgerDbContextFactory(LedgerDbContext.PostgresOptions(options.LedgerConnectionString)));
        services.AddSingleton<SessionBroadcaster>();
        services.AddSingleton<ISessionEventPublisher>(sp => sp.GetRequiredService<SessionBroadcaster>());
        services.AddSingleton<ISessionCostSource>(_ =>
            new RouterClient(new HttpClient { BaseAddress = options.RouterBaseUrl }, options.RouterKey));
        services.AddSingleton(sp => new SessionRecorder(
            sp.GetRequiredService<IDbContextFactory<LedgerDbContext>>(),
            sp.GetRequiredService<ISessionEventPublisher>(),
            sp.GetRequiredService<ISessionCostSource>(),
            TimeProvider.System,
            Console.Out));
        return services;
    }

    /// <summary>The host's base address once started, e.g. <c>http://127.0.0.1:47822</c>.</summary>
    public static string Address(this WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
}
