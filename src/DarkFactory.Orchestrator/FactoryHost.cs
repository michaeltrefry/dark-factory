using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Dashboard.Components;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DarkFactory.Orchestrator;

/// <summary>
/// The long-running <c>factory work</c> host: Kestrel on 127.0.0.1 (and the configured private-network
/// address, <see cref="DashboardBinding"/>) serving the login-protected dashboard and <see cref="SessionHub"/>,
/// fed by <see cref="SessionEventRelay"/>. Long-running services (e.g. intake) register as hosted
/// services, one line each, in <see cref="Build"/>.
/// </summary>
public static class FactoryHost
{
    /// <summary>Host names the host always answers to (DNS-rebinding guard); the configured bind address and host name are added.</summary>
    public static readonly string[] AllowedHosts = ["127.0.0.1", "localhost"];

    /// <summary>Hub paths a cross-origin browser page may not open (the session hub and the dashboard's circuit hub).</summary>
    private static readonly string[] HubPaths = [SessionHub.Path, "/_blazor"];

    /// <param name="configure">Extra or replacement services (tests swap the router cost source here).</param>
    /// <exception cref="InvalidOperationException">The configured <c>Dashboard:BindAddress</c> or <c>Dashboard:HostName</c> is not allowed.</exception>
    public static WebApplication Build(FactoryOptions options, Action<IServiceCollection>? configure = null)
    {
        var addresses = DashboardBinding.Addresses(options.DashboardBindAddress);
        var hostName = DashboardBinding.ValidHostName(options.DashboardHostName);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // The factory assembly, not the entry assembly (a test host), owns the components and static assets.
            ApplicationName = typeof(FactoryHost).Assembly.GetName().Name,
        });
        // Serves blazor.web.js from the build output (a no-op when there is no static web assets manifest).
        builder.WebHost.UseStaticWebAssets();
        // Never a wildcard: 127.0.0.1, plus the one validated private address when configured (E8).
        builder.WebHost.ConfigureKestrel(k =>
        {
            foreach (var address in addresses)
            {
                k.Listen(address, options.HostPort);
            }
        });
        builder.Services.AddHostFiltering(o =>
        {
            o.AllowedHosts = AllowedHosts
                .Concat(addresses.Skip(1).Select(DashboardBinding.HostName))
                .Concat(hostName is not null ? [hostName] : [])
                .ToList();
            o.AllowEmptyHosts = false;
        });
        builder.Services.AddSessionCapture(options);
        builder.Services.AddDashboard(options);
        configure?.Invoke(builder.Services);

        var app = builder.Build(); // the web host applies host filtering ahead of everything
        // Browsers send Origin on cross-site WebSocket/fetch requests: only same-origin pages may use the hubs.
        // Non-browser clients send no Origin and are allowed (the host filter above and the login still apply).
        app.Use(async (context, next) =>
        {
            if (HubPaths.Any(p => context.Request.Path.StartsWithSegments(p))
                && context.Request.Headers.Origin is { Count: > 0 } origin
                && !IsSameOrigin(origin.ToString(), context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.UseAuthentication();
        app.UseAuthorization(); // every endpoint but the login page and form needs the login (fallback policy)
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapDashboardAccount();
        app.MapHub<SessionHub>(SessionHub.Path);
        return app;
    }

    /// <summary>The dashboard: login, pipeline and session pages on a Blazor Server circuit. It only reads the ledger.</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services, FactoryOptions options)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddDashboardAuth(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDashboardData, DashboardData>();
        services.AddSingleton<ISessionViewers>(sp => sp.GetRequiredService<SessionBroadcaster>());
        return services;
    }

    /// <summary><c>factory work</c>: the session hub host plus the intake loop polling the watch scope, in one process.</summary>
    public static WebApplication BuildWork(FactoryOptions options) => Build(options, services => services.AddIntake(options));

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
        services.AddSingleton<PipelineChanges>();
        services.AddSingleton(sp => new SessionEventRelay(
            options.LedgerConnectionString, sp.GetRequiredService<SessionBroadcaster>(), Console.Out, pipeline: sp.GetRequiredService<PipelineChanges>()));
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

    /// <summary>The host's loopback address once started, e.g. <c>http://127.0.0.1:47822</c>.</summary>
    public static string Address(this WebApplication app) => app.Addresses().First(a => a.Contains("://127.0.0.1:", StringComparison.Ordinal));

    /// <summary>Every address the host listens on once started.</summary>
    public static IReadOnlyList<string> Addresses(this WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.ToList();
}
