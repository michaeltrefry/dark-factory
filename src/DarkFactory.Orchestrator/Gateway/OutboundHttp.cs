using System.Net;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>
/// The factory's only outbound HTTP (E2, sc-25390): every <see cref="HttpClient"/> to GitHub's REST API (both Apps, the
/// owner's admin token for rulesets, CI job logs and the blob-storage download GitHub redirects them to), to Shortcut, to
/// the router, to CodeGraph and to Kanban, and a prepare recipe's artifact downloads (sc-26232), are built here, one named client per service; the typed clients (<c>GitHubApp</c>, <c>GitHubPullRequests</c>,
/// <c>GitHubGate</c>, <c>GitHubIssuesClient</c>, <c>RepoProtection</c>, <c>GitHubAppSetup</c>, <c>ShortcutWorkSource</c>,
/// <c>RouterClient</c>, <c>RouterReviewer</c>, <c>CodeGraphMcpClient</c>, <c>McpHttpClient</c>) use the one they are given. The <c>DarkFactory.Analyzers</c> gateway lint fails
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

    /// <summary>
    /// A client for GitHub's REST API; a redirect (a job log's signed download URL) is followed without the Authorization header.
    /// Its reads are retried on a transient answer (<see cref="GitHubReadRetry"/>): every GitHub read of the gate and the pipeline
    /// (and of the issue intake, the Apps' installation lookups and the CLI) goes through here.
    /// </summary>
    public static HttpClient GitHubApi() => GitHubApi(new HttpClientHandler(), TimeProvider.System);

    /// <summary><see cref="GitHubApi()"/> over <paramref name="inner"/> and <paramref name="time"/> (tests: a fake API and instant delays).</summary>
    internal static HttpClient GitHubApi(HttpMessageHandler inner, TimeProvider time) =>
        new(new GitHubReadRetry(inner, time)) { BaseAddress = GitHubApiBase };

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

    /// <summary>The hosted KanbanBoard's MCP endpoint (<c>Kanban:McpUrl</c>'s default; sc-25707).</summary>
    public static readonly Uri KanbanDefaultMcpUrl = new("https://kanban-mcp.trefry.net/mcp");

    /// <summary>The hosted KanbanBoard's web UI (<c>Kanban:WebUrl</c>'s default; sc-25981): only links are built on it, nothing is requested from it.</summary>
    public static readonly Uri KanbanDefaultWebUrl = new("https://kanban.trefry.net/");

    /// <summary>How long one request to Kanban may take.</summary>
    public static readonly TimeSpan KanbanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A client for KanbanBoard's MCP endpoint <paramref name="mcpUrl"/> (<c>Kanban:McpUrl</c>; requests go to the base address itself).</summary>
    public static HttpClient KanbanApi(Uri mcpUrl) => new() { BaseAddress = mcpUrl, Timeout = KanbanTimeout };

    /// <summary>
    /// The client of a prepare recipe's artifact downloads (sc-26232), owner-side only: every connection goes to an address
    /// <paramref name="resolver"/> answered and <see cref="ArtifactAddresses"/> allows (public unicast; checked and pinned in the
    /// connect callback), no proxy, no redirect followed by the handler (the downloader follows at most a few itself, each URL checked
    /// again), nothing decompressed. No timeout of its own: the downloader bounds each download as a whole (headers and body).
    /// </summary>
    public static HttpClient ArtifactDownload(IArtifactResolver resolver) =>
        new(ArtifactTransport.Handler(resolver)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// A client for the factory's own dashboard at <paramref name="baseAddress"/> (the live acceptance tests' login): it keeps
    /// <paramref name="cookies"/> and does not follow redirects.
    /// </summary>
    public static HttpClient Dashboard(Uri baseAddress, CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = baseAddress };

    /// <summary>
    /// A client for one endpoint of the factory's own loopback MCP proxy (<c>McpProxy.Endpoint</c>; the live acceptance tests' MCP
    /// client of it, sc-25710): a loopback address only, anything else refused; it does not follow redirects (a redirect could lead
    /// off loopback).
    /// </summary>
    public static HttpClient LoopbackMcp(Uri endpoint) => endpoint.IsLoopback
        ? new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpoint, Timeout = CodeGraphTimeout }
        : throw new ArgumentException($"'{endpoint}' is not a loopback address: only the factory's own MCP proxy is reached this way.", nameof(endpoint));
}
