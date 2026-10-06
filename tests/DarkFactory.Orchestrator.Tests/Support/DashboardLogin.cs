using System.Net;
using System.Text.RegularExpressions;
using DarkFactory.Orchestrator.Dashboard;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>Logs in to a test host through its real login page and form (antiforgery token included).</summary>
public static partial class DashboardLogin
{
    public const string Password = "correct horse battery staple";

    private static readonly Lazy<string> Hash = new(() => DashboardAuth.HashPassword(Password));

    /// <summary>A secret store holding the hash of <see cref="Password"/>.</summary>
    public static InMemorySecrets Secrets()
    {
        var secrets = new InMemorySecrets();
        secrets.Values[SecretAccounts.DashboardPasswordHash] = Hash.Value;
        return secrets;
    }

    /// <summary>A client that keeps cookies and does not follow redirects.</summary>
    public static HttpClient Client(string baseAddress, CookieContainer? cookies = null) =>
        new(new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseAddress),
        };

    /// <summary>The login page's antiforgery token (its cookie lands in the client's container).</summary>
    public static async Task<string> TokenAsync(HttpClient http) => TokenFrom(await http.GetStringAsync(DashboardAuth.LoginPath));

    /// <summary>The antiforgery token of the (first) form in a page.</summary>
    public static string TokenFrom(string html) =>
        WebUtility.HtmlDecode(TokenInput().Match(html) is { Success: true } m
            ? m.Groups[1].Value
            : throw new InvalidOperationException("The page has no antiforgery token."));

    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, IDictionary<string, string> form, string? token)
    {
        var fields = new Dictionary<string, string>(form);
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }
        return await http.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    public static async Task<HttpResponseMessage> SubmitAsync(HttpClient http, string password, string? returnUrl = null) =>
        await PostAsync(http, DashboardAuth.LoginPostPath,
            new Dictionary<string, string> { ["password"] = password, ["returnUrl"] = returnUrl ?? "" },
            await TokenAsync(http));

    /// <summary>Logs in with <see cref="Password"/>; the returned container holds the session cookie.</summary>
    public static async Task<CookieContainer> LoginAsync(string baseAddress)
    {
        var cookies = new CookieContainer();
        using var http = Client(baseAddress, cookies);
        using var response = await SubmitAsync(http, Password);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location?.OriginalString != "/")
        {
            throw new InvalidOperationException($"Login failed: {(int)response.StatusCode} {response.Headers.Location}");
        }
        return cookies;
    }

    [GeneratedRegex(""""<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"""")]
    private static partial Regex TokenInput();
}
