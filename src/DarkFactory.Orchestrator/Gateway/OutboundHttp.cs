using System.Net;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The factory's only outbound HTTP (E2, sc-25390): every <see cref="HttpClient"/> to GitHub's REST API (both Apps, the
/// owner's admin token for rulesets, CI job logs and the blob-storage download GitHub redirects them to), to Shortcut, to
/// the router and to CodeGraph is built here, one named client per service; the typed clients (<c>GitHubApp</c>, <c>GitHubPullRequests</c>,
/// <c>GitHubGate</c>, <c>GitHubIssuesClient</c>, <c>RepoProtection</c>, <c>GitHubAppSetup</c>, <c>ShortcutWorkSource</c>,
/// <c>RouterClient</c>, <c>RouterReviewer</c>, <c>CodeGraphMcpClient</c>) use the one they are given. The <c>DarkFactory.Analyzers</c> gateway lint fails
/// the build on an HTTP client, handler, socket or web request made anywhere else in the orchestrator or the acceptance tests (DF0001) and on a
/// service host named outside this folder (DF0004). Model calls go only to the router (Phase 1 E1): no provider host is
/// named anywhere (DF0003).
/// </summary>
public static class OutboundHttp
{
    /// <summary>GitHub's REST API.</summary>
    public static readonly Uri GitHubApiBase = new("https://api.github.com/");

    /// <summary>Shortcut's REST API (v3).</summary>
    public static readonly Uri ShortcutApiBase = new("https://api.app.shortcut.com/api/v3/");

    /// <summary>A client for GitHub's REST API; a redirect (a job log's signed download URL) is followed without the Authorization header.</summary>
    public static HttpClient GitHubApi() => new() { BaseAddress = GitHubApiBase };

    /// <summary>A client for Shortcut's REST API.</summary>
    public static HttpClient ShortcutApi() => new() { BaseAddress = ShortcutApiBase };

    /// <summary>A client for the router at <paramref name="baseUrl"/> (<c>Router:BaseUrl</c>), with <paramref name="timeout"/> when given.</summary>
    public static HttpClient RouterApi(Uri baseUrl, TimeSpan? timeout = null)
    {
        var http = new HttpClient { BaseAddress = baseUrl };
        if (timeout is { } t)
        {
            http.Timeout = t;
        }
        return http;
    }

    /// <summary>The hosted CodeGraph (<c>CodeGraph:BaseUrl</c>'s default): reviewers' <c>analyze_impact</c> goes to its MCP endpoint.</summary>
    public static readonly Uri CodeGraphDefaultBase = new("https://codegraph-api.trefry.net/");

    /// <summary>How long one request to CodeGraph may take (the reviewer's whole call is bounded apart, <c>Review:TimeoutMinutes</c>).</summary>
    public static readonly TimeSpan CodeGraphTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A client for CodeGraph at <paramref name="baseUrl"/> (<c>CodeGraph:BaseUrl</c>; the typed client is <c>CodeGraphMcpClient</c>).</summary>
    public static HttpClient CodeGraphApi(Uri baseUrl) => new() { BaseAddress = baseUrl, Timeout = CodeGraphTimeout };

    /// <summary>
    /// A client for the factory's own dashboard at <paramref name="baseAddress"/> (the live acceptance tests' login): it keeps
    /// <paramref name="cookies"/> and does not follow redirects.
    /// </summary>
    public static HttpClient Dashboard(Uri baseAddress, CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = baseAddress };
}
