using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>
/// The loopback MCP proxy (sc-25707, E6): how a Claude Code session that plans or triages reaches CodeGraph and Kanban. Hosted by the
/// process that runs the session, on <c>127.0.0.1</c> only (a free port; never the dashboard's private bind address), apart from the
/// dashboard (no cookie login: its own per-session bearer credential). It holds the upstream tokens (<see cref="McpUpstreams"/>,
/// owner-side, E5); a session gets only <c>--mcp-config</c> naming this proxy and a credential valid for that session alone
/// (<see cref="Grant"/>), revoked when the session ends (<see cref="McpProxyGrant.Dispose"/>).
/// <para>
/// One endpoint per upstream, <c>/mcp/&lt;server&gt;</c> (MCP Streamable HTTP, stateless: every answer is one JSON response).
/// Requests are refused unless the <c>Host</c> header is <c>127.0.0.1:&lt;port&gt;</c> or <c>localhost:&lt;port&gt;</c> and carry no
/// <c>Origin</c> (DNS rebinding), and unless they carry a live credential (else 401). A grant serves exactly its
/// <see cref="McpProfile"/>'s tools (a subset of each upstream's allowlist, <see cref="McpUpstream.Allowlist"/>; an upstream it grants
/// no tool of answers 404): <c>tools/list</c> shows only those; <c>tools/call</c> refuses any other tool (JSON-RPC error, nothing
/// forwarded), and any call past <see cref="MaxCallsPerGrant"/>. Every relayed answer is cut at <see cref="MaxAnswerChars"/>. CodeGraph is pinned to the grant's repository like the reviewers'
/// tools: its project is the one whose GitHub URL is exactly the repository's (<see cref="ICodeGraph.FindProjectAsync"/>), a call
/// naming any other <c>project</c> is refused, the arguments are rebuilt by <see cref="ReviewTools.CodeGraphArguments"/> (anything
/// else is dropped) with the project set, and <c>read_node_source</c> is shown only for a node of that project. Kanban has no
/// repository mapping, so its (allowlisted, read-only) calls are not repository-scoped; their arguments are forwarded as sent.
/// </para>
/// </summary>
public sealed class McpProxy : IAsyncDisposable
{
    /// <summary>The URL path of an upstream's endpoint: <c>/mcp/&lt;server&gt;</c>.</summary>
    public const string PathPrefix = "/mcp/";

    /// <summary>The largest request body read.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>JSON-RPC: the method does not exist.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>JSON-RPC: invalid params (a tool off the allowlist, a query scoped to another repository).</summary>
    public const int InvalidParams = -32602;

    /// <summary>JSON-RPC: the request is not a single JSON-RPC request object.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>
    /// The most characters (and UTF-8 bytes, <see cref="ReviewTools.MaxResultBytes"/>) of one relayed tool answer; a longer one is
    /// cut with a <c>[cut: …]</c> marker (<see cref="ReviewTools.Bounded"/>).
    /// </summary>
    public const int MaxAnswerChars = ReviewTools.MaxResultBytes;

    /// <summary>
    /// The most <c>tools/call</c> requests one grant (one session) may make; every later one answers <see cref="InvalidParams"/>
    /// and is not forwarded.
    /// </summary>
    public const int MaxCallsPerGrant = 100;

    private readonly WebApplication _app;
    private readonly McpUpstreams _upstreams;
    private readonly ConcurrentDictionary<string, McpProxyGrant> _grants = new(StringComparer.Ordinal);

    private McpProxy(WebApplication app, McpUpstreams upstreams) => (_app, _upstreams) = (app, upstreams);

    /// <summary>The proxy's address once started, e.g. <c>http://127.0.0.1:53123/</c>.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The upstreams this proxy serves.</summary>
    public McpUpstreams Upstreams => _upstreams;

    /// <summary>Starts a proxy for <paramref name="upstreams"/> on 127.0.0.1 (a free port).</summary>
    public static async Task<McpProxy> StartAsync(McpUpstreams upstreams, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(McpProxy).Assembly.GetName().Name });
        // No configuration at all (no Kestrel:Endpoints or URLs from the environment can add a listener), no request log (a call's
        // arguments or answer), and no console lifetime (Ctrl-C belongs to the process's own handling, not this proxy).
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, 0);
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
        });
        var app = builder.Build();
        var proxy = new McpProxy(app, upstreams);
        app.Run(proxy.HandleAsync);
        await app.StartAsync(ct);
        var addresses = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.ToList();
        if (addresses.Count != 1 || !addresses[0].StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            throw new InvalidOperationException($"The MCP proxy must listen on 127.0.0.1 only, not {string.Join(", ", addresses)}.");
        }
        proxy.BaseAddress = new Uri(addresses[0].TrimEnd('/') + "/");
        return proxy;
    }

    /// <summary>A host lifetime that hooks no signal: the proxy is started and stopped by its owner only.</summary>
    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A credential for one session about <paramref name="repo"/>, granted exactly <paramref name="profile"/>'s tools of the configured
    /// upstreams (an upstream the profile grants no tool of is not served to it): valid until the grant is disposed (the session
    /// ended), then refused.
    /// </summary>
    public McpProxyGrant Grant(RepoRef repo, McpProfile profile)
    {
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tools = _upstreams.All
            .Select(u => (u.Name, Tools: (IReadOnlyList<string>)profile.ToolsOf(u.Name).Where(u.Allows).ToList()))
            .Where(s => s.Tools.Count > 0)
            .ToList();
        var grant = new McpProxyGrant(this, credential, repo, tools);
        _grants[Hash(credential)] = grant;
        return grant;
    }

    internal void Revoke(McpProxyGrant grant) => _grants.TryRemove(Hash(grant.Credential), out _);

    /// <summary>The endpoint of <paramref name="server"/>.</summary>
    public Uri Endpoint(string server) => new(BaseAddress, $"mcp/{server}");

    public async ValueTask DisposeAsync()
    {
        _grants.Clear();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    private static string Hash(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private bool HostAllowed(HttpRequest request)
    {
        var port = BaseAddress.Port;
        var host = request.Headers.Host.ToString();
        return host == $"127.0.0.1:{port}" || host == $"localhost:{port}";
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (!HostAllowed(request) || request.Headers.ContainsKey("Origin"))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var auth = request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)
            || !_grants.TryGetValue(Hash(auth["Bearer ".Length..].Trim()), out var grant) || grant.Revoked)
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var path = request.Path.Value ?? "";
        var server = path.StartsWith(PathPrefix, StringComparison.Ordinal) ? path[PathPrefix.Length..] : null;
        if (server is null || grant.ToolsOf(server).Count == 0 || _upstreams.Named(server) is not { } upstream)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (HttpMethods.IsDelete(request.Method))
        {
            response.StatusCode = StatusCodes.Status200OK; // stateless: no session to end
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed; // no server-initiated stream
            return;
        }
        JsonObject? message;
        try
        {
            message = await JsonNode.ParseAsync(request.Body, cancellationToken: context.RequestAborted) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            message = null;
        }
        if (message is null || message["method"] is not JsonValue m || !m.TryGetValue<string>(out var method))
        {
            await WriteAsync(response, Error(null, InvalidRequest, "Not a JSON-RPC request object."));
            return;
        }
        var id = message["id"]?.DeepClone();
        if (id is null)
        {
            response.StatusCode = StatusCodes.Status202Accepted; // a notification (notifications/initialized, cancelled)
            return;
        }
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        JsonObject answer;
        try
        {
            answer = method switch
            {
                "initialize" => Result(id, Initialize(parameters, upstream)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, await ListAsync(upstream, grant, context.RequestAborted)),
                "tools/call" when !grant.CountCall() =>
                    Error(id, InvalidParams, $"This session's {MaxCallsPerGrant} tool calls are spent; nothing more is forwarded."),
                "tools/call" => await CallAsync(id, parameters, upstream, grant, context.RequestAborted),
                _ => Error(id, MethodNotFound, $"Method '{Cut(method, 100)}' is not served."),
            };
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            answer = Error(id, -32603, $"{upstream.Service} could not be reached or asked: {Cut(ex.Message, 500)}");
        }
        await WriteAsync(response, answer);
    }

    private static JsonObject Initialize(JsonObject parameters, McpUpstream upstream) => new()
    {
        ["protocolVersion"] = parameters["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var asked) ? asked : McpHttpClient.ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = $"dark-factory-{upstream.Name}", ["version"] = "1" },
    };

    /// <summary>The upstream's allowlisted tools (for CodeGraph, with the input schemas the proxy accepts: no project argument).</summary>
    private static async Task<JsonObject> ListAsync(McpUpstream upstream, McpProxyGrant grant, CancellationToken ct)
    {
        var tools = new JsonArray();
        foreach (var tool in (await upstream.ListToolsAsync(ct)).Where(t => grant.Allows(upstream.Name, t.Name)))
        {
            var schema = upstream.RepoScoped && CodeGraphSchema(tool.Name) is { } own ? own : tool.InputSchema;
            var description = upstream.RepoScoped
                ? $"{tool.Description} (Asked about {grant.Repo.FullName} only; CodeGraph indexes its default branch.)"
                : tool.Description;
            tools.Add(new JsonObject { ["name"] = tool.Name, ["description"] = description, ["inputSchema"] = schema.DeepClone() });
        }
        return new JsonObject { ["tools"] = tools };
    }

    /// <summary>The reviewers' input schema of a CodeGraph tool (the arguments <see cref="ReviewTools.CodeGraphArguments"/> keeps).</summary>
    private static JsonObject? CodeGraphSchema(string tool) =>
        ReviewTools.Definitions.Select(d => JsonSerializer.SerializeToNode(d) as JsonObject)
            .FirstOrDefault(d => d?["name"]?.GetValue<string>() == tool)?["input_schema"]?.DeepClone() as JsonObject;

    private async Task<JsonObject> CallAsync(JsonNode id, JsonObject parameters, McpUpstream upstream, McpProxyGrant grant, CancellationToken ct)
    {
        if (parameters["name"] is not JsonValue n || !n.TryGetValue<string>(out var tool) || !upstream.Allows(tool)
            || !grant.Allows(upstream.Name, tool))
        {
            // Off the allowlist or the session's profile (E2): refused here, nothing forwarded.
            return Error(id, InvalidParams,
                $"Unknown tool; this session's {upstream.Service} tools are {string.Join(", ", grant.ToolsOf(upstream.Name))}.");
        }
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        if (!upstream.RepoScoped)
        {
            // Kanban: no repository mapping, so not repository-scoped; read-only by its allowlist.
            var relayed = await upstream.CallAsync(tool, (JsonObject)arguments.DeepClone(), ct);
            return Relayed(id, relayed.Text, relayed.IsError);
        }
        var codeGraph = _upstreams.CodeGraph!;
        var resolved = await grant.ProjectAsync(codeGraph, ct);
        foreach (var (key, value) in arguments)
        {
            if (string.Equals(key, "project", StringComparison.OrdinalIgnoreCase)
                && (resolved is null || value is not JsonValue p || !p.TryGetValue<string>(out var named) || named != resolved))
            {
                return Error(id, InvalidParams, $"Queries are pinned to {grant.Repo.FullName}'s CodeGraph project; another project is refused.");
            }
        }
        if (resolved is null)
        {
            return Result(id, McpHttpClient.ErrorResult($"{grant.Repo.FullName}: {ReviewTools.NotIndexed}; {tool} cannot answer."));
        }
        using var doc = JsonDocument.Parse(arguments.ToJsonString());
        if (ReviewTools.CodeGraphArguments(tool, doc.RootElement) is not { } built)
        {
            return Result(id, McpHttpClient.ErrorResult(ReviewTools.CodeGraphUsage(tool)));
        }
        if (tool != ReviewTools.ReadNodeSource)
        {
            built["project"] = resolved;
        }
        var answer = await codeGraph.CallAsync(tool, built, ct);
        if (tool == ReviewTools.ReadNodeSource && !answer.IsError && ReviewTools.NodeProject(answer.Text) != resolved)
        {
            return Result(id, McpHttpClient.ErrorResult($"Node {built["nodeId"]} is not a node of {resolved} with readable source; not shown."));
        }
        return Relayed(id, answer.Text, answer.IsError);
    }

    /// <summary>An upstream answer as the session gets it: its text, cut at <see cref="MaxAnswerChars"/> with a marker.</summary>
    private static JsonObject Relayed(JsonNode id, string text, bool isError) => Result(id, new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = ReviewTools.Bounded(text, MaxAnswerChars) }),
        ["isError"] = isError,
    });

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(HttpResponse response, JsonObject body)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        await response.WriteAsync(body.ToJsonString());
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>
/// One session's credential for the <see cref="McpProxy"/>: the bearer token, the repository its CodeGraph queries are pinned to, the
/// upstream tools it may use (its <see cref="McpProfile"/> on the configured upstreams), and its <c>--mcp-config</c> JSON. Disposed
/// when the session ends: the proxy then refuses the credential.
/// </summary>
public sealed class McpProxyGrant : IDisposable
{
    private readonly McpProxy _proxy;
    private readonly SemaphoreSlim _resolve = new(1, 1);
    private readonly IReadOnlyList<(string Server, IReadOnlyList<string> Tools)> _tools;
    private bool _projectResolved;
    private string? _project;
    private int _calls;

    internal McpProxyGrant(McpProxy proxy, string credential, RepoRef repo, IReadOnlyList<(string Server, IReadOnlyList<string> Tools)> tools) =>
        (_proxy, Credential, Repo, _tools) = (proxy, credential, repo, tools);

    /// <summary>The session's bearer credential (never in argv; only in its <c>--mcp-config</c> file).</summary>
    public string Credential { get; }

    public RepoRef Repo { get; }

    /// <summary>The MCP server names the session is given (<see cref="McpServers"/>).</summary>
    public IReadOnlyList<string> Servers => _tools.Select(t => t.Server).ToList();

    /// <summary>The tools of <paramref name="server"/> the session is granted (empty: the server is not served to it).</summary>
    public IReadOnlyList<string> ToolsOf(string server) => _tools.FirstOrDefault(t => t.Server == server).Tools ?? [];

    public bool Allows(string server, string tool) => ToolsOf(server).Contains(tool, StringComparer.Ordinal);

    /// <summary>The session's granted tools as Claude Code allow rules (<c>mcp__&lt;server&gt;__&lt;tool&gt;</c>).</summary>
    public IReadOnlyList<string> ToolRules => _tools.SelectMany(t => t.Tools.Select(n => McpServers.ToolRule(t.Server, n))).ToList();

    /// <summary>Counts one <c>tools/call</c>; false once <see cref="McpProxy.MaxCallsPerGrant"/> have been made.</summary>
    internal bool CountCall() => Interlocked.Increment(ref _calls) <= McpProxy.MaxCallsPerGrant;

    public bool Revoked { get; private set; }

    /// <summary>
    /// The session's <c>--mcp-config</c>: one HTTP server per upstream, at the proxy's loopback endpoint, with the credential in its
    /// <c>Authorization</c> header.
    /// </summary>
    public string McpConfigJson()
    {
        var servers = new JsonObject();
        foreach (var server in Servers)
        {
            servers[server] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = _proxy.Endpoint(server).ToString(),
                ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {Credential}" },
            };
        }
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    /// <summary>The CodeGraph project of <see cref="Repo"/>, resolved once for the session (null: not indexed).</summary>
    internal async Task<string?> ProjectAsync(ICodeGraph codeGraph, CancellationToken ct)
    {
        await _resolve.WaitAsync(ct);
        try
        {
            if (!_projectResolved)
            {
                _project = await codeGraph.FindProjectAsync(Repo, ct);
                _projectResolved = true;
            }
            return _project;
        }
        finally
        {
            _resolve.Release();
        }
    }

    /// <summary>Revokes the credential: the proxy refuses it from now on.</summary>
    public void Dispose()
    {
        Revoked = true;
        _proxy.Revoke(this);
    }
}
