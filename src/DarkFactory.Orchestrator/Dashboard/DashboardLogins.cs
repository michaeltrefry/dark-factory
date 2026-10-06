using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.SignalR;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// Whether a dashboard login still holds: it carries a stamp of the password hash it was issued
/// under (a new <c>set-password</c> ends it), an id that logging out revokes, and — on circuits and
/// hub connections, which keep the principal they connected with — the expiry of its cookie.
/// </summary>
public sealed class DashboardLogins(PasswordHashSource hash, TimeProvider time)
{
    public const string StampClaim = "df_stamp";
    public const string LoginIdClaim = "df_login";
    public const string ExpiresClaim = "df_expires";

    /// <summary>Revoked login id → when its cookie expires at the latest (then it can be forgotten).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revoked = new();

    /// <summary>A stamp of the hash that does not reveal it: base64 of the first 16 bytes of its SHA-256.</summary>
    public static string Stamp(string passwordHash) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)).AsSpan(0, 16));

    /// <summary>The claims of a new login under <paramref name="passwordHash"/>.</summary>
    public static Claim[] Issue(string passwordHash) =>
        [new(StampClaim, Stamp(passwordHash)), new(LoginIdClaim, Guid.NewGuid().ToString("N"))];

    public bool IsValid(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true
            || user.FindFirst(StampClaim)?.Value is not { } stamp
            || hash.Recent() is not { } current
            || !string.Equals(stamp, Stamp(current), StringComparison.Ordinal))
        {
            return false;
        }
        if (user.FindFirst(LoginIdClaim)?.Value is not { } id || _revoked.ContainsKey(id))
        {
            return false;
        }
        return user.FindFirst(ExpiresClaim)?.Value is not { } expires
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            || DateTimeOffset.FromUnixTimeSeconds(unix) > time.GetUtcNow();
    }

    /// <summary>Ends this login (its cookie, wherever it is, and every circuit and hub connection it opened).</summary>
    public void Revoke(ClaimsPrincipal user)
    {
        var now = time.GetUtcNow();
        foreach (var (old, until) in _revoked)
        {
            if (until <= now)
            {
                _revoked.TryRemove(old, out _);
            }
        }
        if (user.FindFirst(LoginIdClaim)?.Value is { } id)
        {
            _revoked[id] = now + DashboardAuth.ExpireTimeSpan; // no copy of the cookie outlives this
        }
    }

    /// <summary>Records the cookie's expiry on the request's principal (not persisted in the cookie).</summary>
    public static void SetExpiry(ClaimsPrincipal user, DateTimeOffset? expiresUtc)
    {
        if (user.Identity is not ClaimsIdentity identity)
        {
            return;
        }
        foreach (var claim in identity.FindAll(ExpiresClaim).ToList())
        {
            identity.RemoveClaim(claim);
        }
        if (expiresUtc is { } e)
        {
            identity.AddClaim(new Claim(ExpiresClaim, e.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }
    }
}

/// <summary>
/// A dashboard circuit's login, rechecked every <see cref="DashboardAuthOptions.RevalidationInterval"/>:
/// once it no longer holds, the circuit becomes anonymous and the router sends the page to the login page.
/// </summary>
public sealed class DashboardAuthStateProvider(ILoggerFactory loggerFactory, DashboardLogins logins, DashboardAuthOptions options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => options.RevalidationInterval;

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(logins.IsValid(authenticationState.User));
}

/// <summary>
/// Session hub connections whose login no longer holds are closed: on their next invocation, and by a
/// sweep every <see cref="DashboardAuthOptions.RevalidationInterval"/> (a viewer only receives, so it may never invoke again).
/// </summary>
public sealed class HubLoginGuard(DashboardLogins logins, DashboardAuthOptions options, TimeProvider time) : BackgroundService, IHubFilter
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!logins.IsValid(invocationContext.Context.User))
        {
            invocationContext.Context.Abort();
            throw new HubException("The dashboard login is no longer valid; log in again.");
        }
        return await next(invocationContext);
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        _connections[context.Context.ConnectionId] = context.Context;
        return next(context);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        _connections.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }

    /// <summary>Closes every connection whose login no longer holds (its session feeds are left on disconnect).</summary>
    public void Sweep()
    {
        foreach (var connection in _connections.Values)
        {
            if (!logins.IsValid(connection.User))
            {
                connection.Abort();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RevalidationInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Sweep();
        }
    }
}
