using System.Net;
using System.Net.Sockets;
using DarkFactory.Orchestrator.Dashboard;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Router;
using DarkFactory.Orchestrator.Sessions;
using DarkFactory.Orchestrator.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

    private async Task<StartedHost> StartAsync(Dictionary<string, string?>? extra = null)
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
        var app = FactoryHost.Build(new FactoryOptions(config, DashboardLogin.Secrets()),
            services => services.AddSingleton<ISessionCostSource>(new NoCosts()));
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
