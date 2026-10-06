using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// The dashboard's single local account: a password checked against the stored ASP.NET Identity
/// (PBKDF2) hash, a cookie session shared by pages, the Blazor circuit and the session hub, and
/// every endpoint but the login page refused without it. A login stops working (<see cref="DashboardLogins"/>)
/// on logout, on a new <c>set-password</c>, and at its expiry — on requests, open circuits and hub connections.
/// </summary>
public static class DashboardAuth
{
    public const string LoginPath = "/login";
    public const string LoginPostPath = "/account/login";
    public const string LogoutPath = "/account/logout";
    public const string CookieName = "df_dashboard";
    public const string LoginRateLimitPolicy = "login";

    /// <summary>Login attempts allowed per client address per minute.</summary>
    public const int LoginAttemptsPerMinute = 5;

    /// <summary>A login cookie's lifetime, renewed (sliding) as it is used.</summary>
    public static readonly TimeSpan ExpireTimeSpan = TimeSpan.FromHours(12);

    private const string UserName = "dashboard";

    /// <summary>Paths that are not pages: refused with 401 instead of redirected to the login page.</summary>
    private static readonly string[] ApiPaths = [Sessions.SessionHub.Path, "/_blazor", "/_framework"];

    public static string HashPassword(string password) => new PasswordHasher<string>().HashPassword(UserName, password);

    public static bool VerifyPassword(string hash, string password)
    {
        try
        {
            return new PasswordHasher<string>().VerifyHashedPassword(UserName, hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false; // a corrupt stored hash matches nothing
        }
    }

    public static IServiceCollection AddDashboardAuth(this IServiceCollection services, FactoryOptions options)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            // Secure whenever the request is HTTPS. The host serves plain HTTP, so on a LAN address the
            // cookie crosses the network in clear: reach it over Tailscale (encrypted) rather than a shared LAN.
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = ExpireTimeSpan;
            o.SlidingExpiration = true;
            o.LoginPath = LoginPath;
            o.LogoutPath = LogoutPath;
            // Each request: refuse a cookie from before the last set-password, or from a logout.
            o.Events.OnValidatePrincipal = async context =>
            {
                var logins = context.HttpContext.RequestServices.GetRequiredService<DashboardLogins>();
                if (context.Principal is { } principal)
                {
                    // Circuits and hub connections keep this principal: they end when the cookie would have.
                    DashboardLogins.SetExpiry(principal, context.Properties.ExpiresUtc);
                }
                if (!logins.IsValid(context.Principal))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
            o.Events.OnRedirectToLogin = context =>
            {
                if (ApiPaths.Any(p => context.Request.Path.StartsWithSegments(p)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery(o =>
        {
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = LoginAttemptsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        services.AddSingleton(new DashboardAuthOptions());
        services.AddSingleton(sp => new PasswordHashSource(
            () => options.DashboardPasswordHash, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<DashboardAuthOptions>().HashCacheDuration));
        services.AddSingleton(sp => new DashboardLogins(sp.GetRequiredService<PasswordHashSource>(), sp.GetRequiredService<TimeProvider>()));
        // Circuits and hub connections hold the login they connected with: recheck it while they stay open.
        services.AddScoped<AuthenticationStateProvider, DashboardAuthStateProvider>();
        services.AddSingleton<HubLoginGuard>();
        services.AddSignalR().AddHubOptions<Sessions.SessionHub>(o => o.AddFilter<HubLoginGuard>());
        services.AddHostedService(sp => sp.GetRequiredService<HubLoginGuard>());
        return services;
    }

    /// <summary>The login and logout form posts (antiforgery-checked; login is rate limited per client address).</summary>
    public static void MapDashboardAccount(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LoginPostPath, (HttpContext context, PasswordHashSource hash, DashboardLogins logins, [FromForm] string? password, [FromForm] string? returnUrl) =>
                LoginAsync(context, hash, logins, password, returnUrl))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
        endpoints.MapPost(LogoutPath, async (HttpContext context, DashboardLogins logins) =>
            {
                logins.Revoke(context.User); // the cookie stops working everywhere, not only in this browser
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect(LoginPath);
            })
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    private static async Task<IResult> LoginAsync(HttpContext context, PasswordHashSource hash, DashboardLogins logins, string? password, string? returnUrl)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (string.IsNullOrEmpty(password) || hash.Get() is not { } stored || !VerifyPassword(stored, password))
        {
            return Results.Redirect($"{LoginPath}?error=1&returnUrl={Uri.EscapeDataString(target)}");
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName), .. DashboardLogins.Issue(stored)], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(target);
    }

    /// <summary>Only same-site paths: never <c>//host</c>, <c>/\host</c> or an absolute URL (open redirect).</summary>
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

/// <summary>Timings of the login checks (tests shorten them).</summary>
public sealed record DashboardAuthOptions
{
    /// <summary>How often an open circuit or hub connection rechecks its login.</summary>
    public TimeSpan RevalidationInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long one read of the stored hash serves the login checks (a keychain read spawns a process).</summary>
    public TimeSpan HashCacheDuration { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The stored hash. Each login reads it afresh, so a new <c>set-password</c> applies without a restart;
/// the checks of existing logins reuse a read for up to the cache duration.
/// </summary>
public sealed class PasswordHashSource(Func<string> get, TimeProvider time, TimeSpan cacheDuration)
{
    private readonly Lock _lock = new();
    private (string? Hash, DateTimeOffset At)? _cached;

    /// <summary>The hash read now, or null when none is set (every login then fails).</summary>
    public string? Get()
    {
        string? hash;
        try
        {
            hash = get();
        }
        catch (MissingCredentialException)
        {
            hash = null;
        }
        lock (_lock)
        {
            _cached = (hash, time.GetUtcNow());
        }
        return hash;
    }

    /// <summary>The hash as read at most the cache duration ago.</summary>
    public string? Recent()
    {
        lock (_lock)
        {
            if (_cached is { } c && time.GetUtcNow() - c.At < cacheDuration)
            {
                return c.Hash;
            }
        }
        return Get();
    }
}
