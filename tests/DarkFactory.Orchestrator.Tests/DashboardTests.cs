using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DarkFactory.Orchestrator.Tests;

public class DashboardBindingTests
{
    private static readonly IPAddress[] Local = [IPAddress.Loopback, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("100.101.102.103"), IPAddress.Parse("fd7a:115c:a1e0::1"), IPAddress.Parse("203.0.113.9")];

    [Fact]
    public void Unset_binds_loopback_only()
    {
        Assert.Equal([IPAddress.Loopback], DashboardBinding.Addresses(null, () => Local));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("100.101.102.103")] // Tailscale (CGNAT range)
    [InlineData("fd7a:115c:a1e0::1")] // Tailscale IPv6 (ULA)
    public void A_private_address_on_a_local_interface_is_bound_beside_loopback(string configured)
    {
        Assert.Equal([IPAddress.Loopback, IPAddress.Parse(configured)], DashboardBinding.Addresses(configured, () => Local));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("203.0.113.9")]  // public, even though it is local
    [InlineData("10.99.99.99")]  // private, but no interface has it
    [InlineData("169.254.1.1")]  // link-local
    [InlineData("fe80::1")]
    [InlineData("not-an-ip")]
    [InlineData("example.com")]
    public void Wildcard_public_loopback_foreign_and_malformed_addresses_are_refused(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DashboardBinding.Addresses(configured, () => Local));
        Assert.Contains("Dashboard:BindAddress", ex.Message);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fc00::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Private_ranges(string address, bool expected)
    {
        Assert.Equal(expected, DashboardBinding.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("10.99.99.99")]
    public void The_host_refuses_to_start_on_a_disallowed_bind_address(string configured)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:HostPort"] = "0",
            ["Dashboard:BindAddress"] = configured,
        }).Build();

        Assert.Throws<InvalidOperationException>(() => FactoryHost.Build(new FactoryOptions(config, new InMemorySecrets())));
    }

    [Theory]
    [InlineData("*", "no wildcards")]
    [InlineData("*.example.com", "no wildcards")]
    [InlineData("evil.com:1", "no wildcards")]  // a port
    [InlineData("evil.com/x", "no wildcards")]  // a path
    [InlineData("mac .ts.net", "no wildcards")] // whitespace
    [InlineData("192.168.1.20", "not a DNS host name")]
    [InlineData("[fd7a::1]", "no wildcards")]
    [InlineData("bad_name!", "not a DNS host name")]
    public void The_host_refuses_to_start_on_a_host_name_that_is_not_one_plain_dns_name(string configured, string why)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:HostPort"] = "0",
            ["Dashboard:HostName"] = configured,
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryHost.Build(new FactoryOptions(config, new InMemorySecrets())));
        Assert.Contains($"Dashboard:HostName '{configured}' refused: {why}", ex.Message);
    }

    [Theory]
    [InlineData("mac.tail1234.ts.net")]
    [InlineData("localhost")]
    [InlineData(null)]
    public void A_plain_dns_host_name_is_accepted(string? configured)
    {
        Assert.Equal(configured, DashboardBinding.ValidHostName(configured));
    }
}

public class DashboardLoginsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static (DashboardLogins Logins, InMemorySecrets Secrets, FixedTime Time) Create()
    {
        var secrets = DashboardLogin.Secrets();
        var time = new FixedTime(Now);
        var hash = new PasswordHashSource(() => secrets.Get(SecretAccounts.DashboardPasswordHash)!, time, TimeSpan.Zero);
        return (new DashboardLogins(hash, time), secrets, time);
    }

    private static ClaimsPrincipal Login(InMemorySecrets secrets, DateTimeOffset? expires = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "dashboard"), .. DashboardLogins.Issue(secrets.Values[SecretAccounts.DashboardPasswordHash])], "test"));
        DashboardLogins.SetExpiry(user, expires);
        return user;
    }

    [Fact]
    public void Login_checks_reuse_a_recent_read_of_the_hash_and_a_login_read_refreshes_it()
    {
        var reads = 0;
        var stored = "hash-1";
        var time = new FixedTime(Now);
        var source = new PasswordHashSource(() => { reads++; return stored; }, time, TimeSpan.FromSeconds(5));

        Assert.Equal("hash-1", source.Recent());
        stored = "hash-2";
        time.Now = Now.AddSeconds(4);
        Assert.Equal("hash-1", source.Recent()); // within the cache duration: no keychain read
        Assert.Equal(1, reads);

        Assert.Equal("hash-2", source.Get());    // a login reads afresh...
        Assert.Equal("hash-2", source.Recent()); // ...and the checks see what it saw
        Assert.Equal(2, reads);

        stored = "hash-3";
        time.Now = Now.AddSeconds(10);
        Assert.Equal("hash-3", source.Recent()); // expired: read again
    }

    [Fact]
    public void A_login_holds_until_its_cookie_expiry_passes()
    {
        var (logins, secrets, time) = Create();
        var user = Login(secrets, Now.AddHours(1));
        Assert.True(logins.IsValid(user));

        time.Now = Now.AddHours(1);

        Assert.False(logins.IsValid(user));
    }

    [Fact]
    public void The_stamp_does_not_reveal_the_hash_and_changes_with_it()
    {
        var hash = DashboardAuth.HashPassword("one password here");
        var stamp = DashboardLogins.Stamp(hash);

        Assert.Equal(24, stamp.Length); // 16 bytes
        Assert.DoesNotContain(stamp, hash);
        Assert.NotEqual(stamp, DashboardLogins.Stamp(DashboardAuth.HashPassword("one password here")));
    }

    [Fact]
    public void Logins_without_a_stamp_or_id_or_with_no_stored_hash_do_not_hold()
    {
        var (logins, secrets, _) = Create();
        var user = Login(secrets);
        Assert.True(logins.IsValid(user));

        Assert.False(logins.IsValid(new ClaimsPrincipal(new ClaimsIdentity(user.Claims.Where(c => c.Type != DashboardLogins.StampClaim), "test"))));
        Assert.False(logins.IsValid(new ClaimsPrincipal(new ClaimsIdentity(user.Claims.Where(c => c.Type != DashboardLogins.LoginIdClaim), "test"))));
        Assert.False(logins.IsValid(new ClaimsPrincipal(new ClaimsIdentity(user.Claims)))); // not authenticated
        secrets.Values.Remove(SecretAccounts.DashboardPasswordHash);
        Assert.False(logins.IsValid(user));
    }

    [Fact]
    public async Task A_circuit_becomes_anonymous_once_its_login_ends()
    {
        var (logins, secrets, _) = Create();
        using var provider = new DashboardAuthStateProvider(NullLoggerFactory.Instance, logins,
            new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromMilliseconds(50) });
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Login(secrets))));
        var changed = new TaskCompletionSource<AuthenticationState>();
        provider.AuthenticationStateChanged += async state => changed.TrySetResult(await state);
        await Task.Delay(300); // several revalidations while the login holds
        Assert.False(changed.Task.IsCompleted);

        secrets.Values[SecretAccounts.DashboardPasswordHash] = DashboardAuth.HashPassword("a brand new password");

        var state = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(state.User.Identity?.IsAuthenticated);
    }
}

public class DashboardPasswordTests
{
    [Fact]
    public void Set_password_stores_only_a_verifiable_hash()
    {
        var secrets = new InMemorySecrets();
        var answers = new Queue<string>(["a long enough secret", "a long enough secret"]);
        var output = new StringWriter();

        Assert.Equal(0, SetPassword.Run(secrets, _ => answers.Dequeue(), output, TextWriter.Null));

        var stored = secrets.Values[SecretAccounts.DashboardPasswordHash];
        Assert.DoesNotContain("a long enough secret", stored);
        Assert.True(DashboardAuth.VerifyPassword(stored, "a long enough secret"));
        Assert.False(DashboardAuth.VerifyPassword(stored, "a long enough secreT"));
        Assert.DoesNotContain("a long enough secret", output.ToString());
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("a long enough secret", "a different secret")]
    [InlineData(null, null)]
    public void Set_password_refuses_short_or_mismatched_passwords_and_stores_nothing(string? first, string? second)
    {
        var secrets = new InMemorySecrets();
        var answers = new Queue<string?>([first, second]);

        Assert.Equal(2, SetPassword.Run(secrets, _ => answers.Dequeue(), TextWriter.Null, TextWriter.Null));
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public void A_corrupt_stored_hash_matches_nothing()
    {
        Assert.False(DashboardAuth.VerifyPassword("not a hash", "anything"));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/sessions/abc", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_local_return_urls_are_followed(string? url, bool expected)
    {
        Assert.Equal(expected, DashboardAuth.IsLocalUrl(url));
    }
}

/// <summary>The dashboard host on Kestrel against the compose Postgres: login, refusals, binding.</summary>
public sealed class DashboardHostTests : IAsyncLifetime
{
    private TempPostgresDatabase? _db;

    public async ValueTask InitializeAsync()
    {
        _db = await TempPostgresDatabase.CreateAsync("df_dashboard");
        await LedgerMigrations.MigrateAsync(_db.ConnectionString, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/sessions/some-session")]
    [InlineData("GET", "/no-such-page")]
    public async Task Unauthenticated_page_requests_are_sent_to_the_login_page(string method, string path)
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());

        using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"{app.Address()}/login?ReturnUrl=", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("POST", "/hubs/sessions/negotiate?negotiateVersion=1")]
    [InlineData("GET", "/hubs/sessions")]
    [InlineData("POST", "/_blazor/negotiate?negotiateVersion=1")]
    [InlineData("GET", "/_framework/blazor.web.js")]
    public async Task Unauthenticated_hub_circuit_and_script_requests_are_refused(string method, string path)
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());

        using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logging_in_sets_a_strict_http_only_cookie_that_opens_pages_the_circuit_and_the_hub()
    {
        await using var app = await StartAsync();
        var cookies = new CookieContainer();
        using var http = DashboardLogin.Client(app.Address(), cookies);

        using var login = await DashboardLogin.SubmitAsync(http, DashboardLogin.Password, "/sessions/x");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/sessions/x", login.Headers.Location!.OriginalString);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(DashboardAuth.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie.Replace("samesite", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase); // plain HTTP

        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Pipeline</h1>", html);
        Assert.Contains("_framework/blazor.web.js", html);
        Assert.Contains("Log out", html);
        using var script = await http.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);

        using var hub = await http.PostAsync(SessionHub.Path + "/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        using var circuit = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, circuit.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_goes_back_to_the_login_page_without_a_session()
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());

        using var login = await DashboardLogin.SubmitAsync(http, "wrong password!!", "/");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/login?error=1", login.Headers.Location!.OriginalString);
        Assert.False(login.Headers.TryGetValues("Set-Cookie", out var set) && set.Any(c => c.StartsWith(DashboardAuth.CookieName + "=", StringComparison.Ordinal)));
        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("Wrong password.", await http.GetStringAsync(login.Headers.Location));
    }

    [Fact]
    public async Task Login_without_the_antiforgery_token_is_rejected()
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());
        await DashboardLogin.TokenAsync(http); // the antiforgery cookie alone is not enough

        using var login = await DashboardLogin.PostAsync(http, DashboardAuth.LoginPostPath,
            new Dictionary<string, string> { ["password"] = DashboardLogin.Password }, token: null);

        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_rate_limited_per_client()
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());
        var token = await DashboardLogin.TokenAsync(http);
        var form = new Dictionary<string, string> { ["password"] = "wrong password!!" };

        for (var i = 0; i < DashboardAuth.LoginAttemptsPerMinute; i++)
        {
            using var attempt = await DashboardLogin.PostAsync(http, DashboardAuth.LoginPostPath, form, token);
            Assert.Equal(HttpStatusCode.Redirect, attempt.StatusCode);
        }
        form["password"] = DashboardLogin.Password; // even the right password, once the limit is hit
        using var limited = await DashboardLogin.PostAsync(http, DashboardAuth.LoginPostPath, form, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Return_urls_off_the_site_are_ignored()
    {
        await using var app = await StartAsync();
        using var http = DashboardLogin.Client(app.Address());

        using var login = await DashboardLogin.SubmitAsync(http, DashboardLogin.Password, "//evil.example/x");

        Assert.Equal("/", login.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Control_posts_need_the_login_and_the_antiforgery_token_and_write_only_the_control()
    {
        await using var app = await StartAsync();
        var form = new Dictionary<string, string> { ["action"] = "pause", ["scope"] = "factory" };

        using (var anonymous = DashboardLogin.Client(app.Address()))
        {
            using var refused = await DashboardLogin.PostAsync(anonymous, DashboardControls.Path, form, await DashboardLogin.TokenAsync(anonymous));
            Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
            Assert.StartsWith($"{app.Address()}/login", refused.Headers.Location!.ToString());
        }
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        using var http = DashboardLogin.Client(app.Address(), cookies);
        using (var noToken = await DashboardLogin.PostAsync(http, DashboardControls.Path, form, null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        }
        using (var badScope = await DashboardLogin.PostAsync(http, DashboardControls.Path,
            new Dictionary<string, string> { ["action"] = "pause", ["scope"] = "everything" }, DashboardLogin.TokenFrom(await http.GetStringAsync("/"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
        }
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_db!.ConnectionString)))
        {
            Assert.Empty(await db.Controls.ToListAsync());
        }

        var page = await http.GetStringAsync("/");
        Assert.Contains("data-scope=\"factory\"", page); // the factory's Pause/Stop buttons
        using var paused = await DashboardLogin.PostAsync(http, DashboardControls.Path, form, DashboardLogin.TokenFrom(page));

        Assert.Equal(HttpStatusCode.Redirect, paused.StatusCode);
        Assert.Equal("/", paused.Headers.Location!.OriginalString);
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_db!.ConnectionString)))
        {
            var control = await db.Controls.SingleAsync();
            Assert.Equal(("factory", Controls.ControlState.Paused, DashboardControls.By), (control.Scope, control.State, control.ChangedBy));
        }
        Assert.Contains("Factory: <span class=\"control-state\">paused</span>", await http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Stop_from_the_dashboard_asks_for_confirmation_and_marks_the_item_stopping()
    {
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_db!.ConnectionString)))
        {
            await new WorkLedger(db, TimeProvider.System).GetOrCreateAsync(RunPipeline.Source, "sc-12", "Story", "acme/widgets", null, CancellationToken.None, 5);
        }
        await using var app = await StartAsync();
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        using var http = DashboardLogin.Client(app.Address(), cookies);
        var page = await http.GetStringAsync("/");
        Assert.Contains("data-scope=\"epic:5\"", page);
        Assert.Matches("<form[^>]*data-action=\"stop\"[^>]*data-scope=\"item:sc-12\"[^>]*onsubmit=\"return confirm", page);

        using var stop = await DashboardLogin.PostAsync(http, DashboardControls.Path,
            new Dictionary<string, string> { ["action"] = "stop", ["scope"] = "item:sc-12" }, DashboardLogin.TokenFrom(page));

        Assert.Equal(HttpStatusCode.Redirect, stop.StatusCode);
        await using (var db = new LedgerDbContext(LedgerDbContext.PostgresOptions(_db!.ConnectionString)))
        {
            // This host has no board or GitHub (no intake): the item waits Stopping for its next run to finish it.
            Assert.Equal(Controls.ControlState.Stopping, (await db.Controls.SingleAsync(c => c.Scope == "item:sc-12")).State);
        }
        Assert.Contains("<span class=\"control-state\">stopping</span>", await http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        await using var app = await StartAsync();
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        using var http = DashboardLogin.Client(app.Address(), cookies);
        var page = await http.GetStringAsync("/");
        using var logout = await DashboardLogin.PostAsync(http, DashboardAuth.LogoutPath, new Dictionary<string, string>(), DashboardLogin.TokenFrom(page));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var after = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task By_default_a_local_interface_address_other_than_loopback_cannot_be_connected_to()
    {
        var other = DashboardBinding.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            ?? IPAddress.Parse("127.0.0.2"); // loopback-only machine: another loopback address, which is not bound either
        await using var app = await StartAsync();
        var port = new Uri(app.Address()).Port;
        Assert.Equal([app.Address()], app.Addresses());

        using var client = new TcpClient();
        var connect = client.ConnectAsync(other, port);
        var ex = await Record.ExceptionAsync(() => connect.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(ex is SocketException or TimeoutException, $"connecting to {other}:{port} should fail, got {ex?.GetType().Name ?? "a connection"}");
    }

    [Fact]
    public async Task A_configured_private_address_is_served_beside_loopback_and_answers_to_its_own_host_name()
    {
        var address = DashboardBinding.LocalAddresses().FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && DashboardBinding.IsPrivate(a));
        if (address is null)
        {
            Assert.Skip("No private IPv4 address on a local interface.");
        }
        await using var app = await StartAsync(new() { ["Dashboard:BindAddress"] = address.ToString() });

        var lan = Assert.Single(app.Addresses(), a => a.Contains($"://{address}:", StringComparison.Ordinal));
        using var http = DashboardLogin.Client(lan);
        using var loginPage = await http.GetAsync(DashboardAuth.LoginPath);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        using var refused = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode); // the login applies there too

        using var rebound = new HttpRequestMessage(HttpMethod.Get, DashboardAuth.LoginPath);
        rebound.Headers.Host = "rebound.evil.example";
        using var wrongHost = await http.SendAsync(rebound);
        Assert.Equal(HttpStatusCode.BadRequest, wrongHost.StatusCode);
    }

    [Fact]
    public async Task A_configured_host_name_is_answered_to()
    {
        await using var app = await StartAsync(new() { ["Dashboard:HostName"] = "mac.tail1234.ts.net" });
        using var http = DashboardLogin.Client(app.Address());
        var port = new Uri(app.Address()).Port;

        using var named = new HttpRequestMessage(HttpMethod.Get, DashboardAuth.LoginPath);
        named.Headers.Host = $"mac.tail1234.ts.net:{port}";
        using var ok = await http.SendAsync(named);
        using var other = new HttpRequestMessage(HttpMethod.Get, DashboardAuth.LoginPath);
        other.Headers.Host = $"other.tail1234.ts.net:{port}";
        using var refused = await http.SendAsync(other);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_new_password_ends_existing_logins_on_pages_and_the_hub()
    {
        var secrets = DashboardLogin.Secrets();
        await using var app = await StartAsync(secrets: secrets);
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        using var http = DashboardLogin.Client(app.Address(), cookies);
        using (var before = await http.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        secrets.Values[SecretAccounts.DashboardPasswordHash] = DashboardAuth.HashPassword("a brand new password");

        using var page = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith($"{app.Address()}/login?ReturnUrl=", page.Headers.Location!.ToString());
        using var hub = await DashboardLogin.Client(app.Address(), cookies).PostAsync(SessionHub.Path + "/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);

        // The new password logs in at once (no restart, no stale cached hash).
        using var fresh = DashboardLogin.Client(app.Address());
        using var login = await DashboardLogin.SubmitAsync(fresh, "a brand new password");
        Assert.Equal("/", login.Headers.Location!.OriginalString);
        using var after = await fresh.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Logging_out_revokes_the_cookie_even_where_a_copy_is_still_held()
    {
        await using var app = await StartAsync();
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        var copy = new CookieContainer();
        copy.Add(cookies.GetAllCookies());
        using var http = DashboardLogin.Client(app.Address(), cookies);
        var page = await http.GetStringAsync("/");

        using var logout = await DashboardLogin.PostAsync(http, DashboardAuth.LogoutPath, new Dictionary<string, string>(), DashboardLogin.TokenFrom(page));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        using var stolen = DashboardLogin.Client(app.Address(), copy);
        using var refused = await stolen.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        using var hub = await stolen.PostAsync(SessionHub.Path + "/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
    }

    [Theory]
    [InlineData("set-password")]
    [InlineData("logout")]
    public async Task An_open_hub_connection_is_closed_once_its_login_ends_without_invoking_anything(string how)
    {
        var secrets = DashboardLogin.Secrets();
        await using var app = await StartAsync(secrets: secrets, auth: new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromMilliseconds(200), HashCacheDuration = TimeSpan.Zero });
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        await using var connection = await ConnectAsync(app, cookies);
        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await Task.Delay(600); // several sweeps while the login holds
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await EndLoginAsync(app, secrets, cookies, how);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_hub_invocation_after_the_login_ends_is_refused_and_closes_the_connection()
    {
        var secrets = DashboardLogin.Secrets();
        await using var app = await StartAsync(secrets: secrets, auth: new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromDays(30), HashCacheDuration = TimeSpan.Zero });
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        await using var connection = await ConnectAsync(app, cookies);
        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        var valid = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", "no-such-session"));
        Assert.Contains("Unknown session", valid.Message); // the login holds: the call itself ran

        await EndLoginAsync(app, secrets, cookies, "set-password");

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync("JoinSession", "no-such-session"));
        Assert.DoesNotContain("Unknown session", refused.Message);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_hub_connection_ends_when_its_cookie_would_have_expired()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await StartAsync(time: time, auth: new DashboardAuthOptions { RevalidationInterval = TimeSpan.FromDays(30), HashCacheDuration = TimeSpan.FromDays(30) });
        var cookies = await DashboardLogin.LoginAsync(app.Address());
        await using var connection = await ConnectAsync(app, cookies);
        var valid = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", "no-such-session"));
        Assert.Contains("Unknown session", valid.Message);

        time.Advance(DashboardAuth.ExpireTimeSpan + TimeSpan.FromMinutes(1));

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync("JoinSession", "no-such-session"));
        Assert.DoesNotContain("Unknown session", refused.Message);
    }

    [Fact]
    public async Task Dashboard_circuits_recheck_their_login()
    {
        await using var app = await StartAsync();
        await using var scope = app.App.Services.CreateAsyncScope();

        Assert.IsType<DashboardAuthStateProvider>(scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
    }

    private static async Task<HubConnection> ConnectAsync(StartedHost app, CookieContainer cookies)
    {
        var connection = new HubConnectionBuilder().WithUrl(app.Address() + SessionHub.Path, o => o.Cookies = cookies).Build();
        await connection.StartAsync();
        return connection;
    }

    private static async Task EndLoginAsync(StartedHost app, InMemorySecrets secrets, CookieContainer cookies, string how)
    {
        if (how == "set-password")
        {
            secrets.Values[SecretAccounts.DashboardPasswordHash] = DashboardAuth.HashPassword("a brand new password");
            return;
        }
        var copy = new CookieContainer();
        copy.Add(cookies.GetAllCookies()); // the hub client keeps its own copy of the cookie
        using var http = DashboardLogin.Client(app.Address(), copy);
        var page = await http.GetStringAsync("/");
        using var logout = await DashboardLogin.PostAsync(http, DashboardAuth.LogoutPath, new Dictionary<string, string>(), DashboardLogin.TokenFrom(page));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
    }

    private async Task<StartedHost> StartAsync(
        Dictionary<string, string?>? extra = null, InMemorySecrets? secrets = null, DashboardAuthOptions? auth = null, TimeProvider? time = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = _db!.ConnectionString,
            ["Factory:HostPort"] = "0",
        };
        foreach (var (k, v) in extra ?? [])
        {
            settings[k] = v;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var app = FactoryHost.Build(new FactoryOptions(config, secrets ?? DashboardLogin.Secrets()), services =>
        {
            services.AddSingleton<ISessionCostSource>(new NoCosts());
            // Checks of existing logins read the hash afresh unless a test says otherwise.
            services.AddSingleton(auth ?? new DashboardAuthOptions { HashCacheDuration = TimeSpan.Zero });
            if (time is not null)
            {
                services.AddSingleton(time);
            }
        });
        await app.StartAsync();
        return new StartedHost(app);
    }

    private sealed class NoCosts : ISessionCostSource
    {
        public Task<SessionCost?> GetSessionCostAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionCost?>(null);
    }
}

/// <summary>A started host that stops (closing connections while its services live) before it is disposed.</summary>
internal sealed class StartedHost(WebApplication app) : IAsyncDisposable
{
    public WebApplication App { get; } = app;

    public string Address() => App.Address();

    public IReadOnlyList<string> Addresses() => App.Addresses();

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
