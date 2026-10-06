using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// The dashboard's single local account: a password checked against the stored ASP.NET Identity
/// (PBKDF2) hash, a cookie session shared by pages, the Blazor circuit and the session hub, and
/// every endpoint but the login page refused without it.
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
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.LoginPath = LoginPath;
            o.LogoutPath = LogoutPath;
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
        services.AddSingleton(new PasswordHashSource(() => options.DashboardPasswordHash));
        return services;
    }

    /// <summary>The login and logout form posts (antiforgery-checked; login is rate limited per client address).</summary>
    public static void MapDashboardAccount(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LoginPostPath, (HttpContext context, PasswordHashSource hash, [FromForm] string? password, [FromForm] string? returnUrl) =>
                LoginAsync(context, hash, password, returnUrl))
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
        endpoints.MapPost(LogoutPath, async (HttpContext context) =>
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect(LoginPath);
            })
            .WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    private static async Task<IResult> LoginAsync(HttpContext context, PasswordHashSource hash, string? password, string? returnUrl)
    {
        var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        if (string.IsNullOrEmpty(password) || hash.Get() is not { } stored || !VerifyPassword(stored, password))
        {
            return Results.Redirect($"{LoginPath}?error=1&returnUrl={Uri.EscapeDataString(target)}");
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName)], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(target);
    }

    /// <summary>Only same-site paths: never <c>//host</c>, <c>/\host</c> or an absolute URL (open redirect).</summary>
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

/// <summary>Reads the stored hash at each login, so a new <c>set-password</c> applies without a restart.</summary>
public sealed class PasswordHashSource(Func<string> get)
{
    /// <summary>The hash, or null when none is set (every login then fails).</summary>
    public string? Get()
    {
        try
        {
            return get();
        }
        catch (MissingCredentialException)
        {
            return null;
        }
    }
}
