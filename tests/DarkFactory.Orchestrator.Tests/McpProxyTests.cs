using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>sc-25707: the loopback MCP proxy that planning and triage sessions reach CodeGraph and Kanban through.</summary>
public class McpProxyTests
{
    private const string CodeGraphToken = "cg_upstream_secret_7f3a";
    private const string KanbanToken = "kb_upstream_secret_91c2";
    private const string Project = "R Service";
    private static readonly RepoRef Repo = new("o", "r");

    private const string Listing =
        "- **r-tools** [csharp] (indexed: 2026-10-01)\n  Repo: https://github.com/o/r-tools\n"
        + "- **R Service** [csharp] (indexed: 2026-10-03)\n  Repo: https://github.com/O/R.git\n"
        + "- **Other** [csharp] (indexed: 2026-10-03)\n  Repo: https://github.com/someone/else\n";

    /// <summary>CodeGraph's tools as its server lists them: the allowlist plus model-running and other tools no session may see.</summary>
    private static readonly string[] CodeGraphListed =
        [.. ReviewTools.CodeGraphTools, "ask", "project_report", "rag_search", "search_projects", "memory_store", "codegraph_search"];

    /// <summary>Kanban's tools as KanbanBoard lists them: its read tools and its write tools.</summary>
    private static readonly string[] KanbanListed =
        ["ListProjects", "GetBoard", "ListEpics", "GetEpic", "ListEpicDocuments", "GetEpicDocument", "ListWorkItems", "ListEpicWorkItems",
            "GetIssues", "CreateWorkItem", "UpdateWorkItem", "DeleteWorkItem", "MoveWorkItem", "AddWorkItemComment", "CreateEpic"];

    private static FakeMcpServer CodeGraphServer(Func<string, JsonObject, JsonObject>? answer = null) =>
        new("/mcp", CodeGraphListed, (tool, args) => tool == "search_projects"
            ? FakeMcpServer.Text(Listing)
            : answer?.Invoke(tool, args) ?? FakeMcpServer.Text($"{tool} of {args["name"]} in {args["project"]}"));

    private static FakeMcpServer KanbanServer() =>
        new("/mcp", KanbanListed, (tool, args) => FakeMcpServer.Text($"kanban {tool} {args.ToJsonString()}"));

    private static McpUpstreams Upstreams(FakeMcpServer codeGraph, FakeMcpServer? kanban = null) => new(
        new CodeGraphMcpClient(codeGraph.Client("https://codegraph.test/"), CodeGraphToken),
        kanban is null ? null : new McpUpstream(McpServers.Kanban, new McpHttpClient(kanban.Client("https://kanban.test/mcp"), KanbanToken, "Kanban", ""),
            McpServers.KanbanTools, repoScoped: false));

    /// <summary>A Claude Code-like MCP client of the proxy: Host 127.0.0.1:port, no Origin.</summary>
    private static async Task<(HttpStatusCode Status, JsonObject? Body)> Rpc(McpProxy proxy, string server, string? credential, string method,
        JsonObject? parameters = null, Action<HttpRequestMessage>? adjust = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, proxy.Endpoint(server))
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = method, ["params"] = parameters ?? new JsonObject() }
                .ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        adjust?.Invoke(request);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
    }

    private static Task<(HttpStatusCode Status, JsonObject? Body)> Call(McpProxy proxy, string server, string credential, string tool, JsonObject arguments) =>
        Rpc(proxy, server, credential, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });

    private static List<string> Listed(JsonObject body) =>
        body["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task A_session_lists_and_calls_the_allowlisted_codegraph_tools_through_the_proxy_and_its_credential_dies_with_the_session()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        Assert.Equal("127.0.0.1", proxy.BaseAddress.Host);
        var grant = proxy.Grant(Repo, McpProfile.Planner);

        var (status, init) = await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "initialize",
            new JsonObject { ["protocolVersion"] = McpHttpClient.ProtocolVersion });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(init!["result"]!["capabilities"]!["tools"]);

        var (_, list) = await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list");
        Assert.Equal(ReviewTools.CodeGraphTools.Order(), Listed(list!).Order());
        // No listed tool takes a project: the proxy pins it.
        Assert.All(list!["result"]!["tools"]!.AsArray(), t => Assert.Null(t!["inputSchema"]!["properties"]?["project"]));

        var (_, called) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "analyze_impact", new JsonObject { ["name"] = "X.Count" });
        var result = called!["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal($"analyze_impact of X.Count in {Project}", result["content"]![0]!["text"]!.GetValue<string>());
        var (tool, arguments) = Assert.Single(codeGraph.Calls, c => c.Tool != "search_projects");
        Assert.Equal("analyze_impact", tool);
        Assert.Equal(Project, arguments["project"]!.GetValue<string>()); // pinned to the item's repository, resolved by exact URL
        Assert.All(codeGraph.Api.Requests, r => Assert.Equal($"Bearer {CodeGraphToken}", r.Headers["Authorization"]));

        // The session ends: the credential is refused from then on, for every method.
        grant.Dispose();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Call(proxy, McpServers.CodeGraph, grant.Credential, "analyze_impact", new JsonObject { ["name"] = "X" })).Status);
        // Another session's credential is its own.
        var other = proxy.Grant(Repo, McpProfile.Planner);
        Assert.NotEqual(grant.Credential, other.Credential);
        Assert.Equal(HttpStatusCode.OK, (await Rpc(proxy, McpServers.CodeGraph, other.Credential, "tools/list")).Status);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("project_report")]
    [InlineData("rag_search")]
    [InlineData("search_projects")]
    [InlineData("memory_store")]
    [InlineData("codegraph_search")]
    [InlineData("Analyze_Impact")]
    public async Task A_tool_off_the_allowlist_is_hidden_from_the_listing_and_refused_without_being_forwarded(string offList)
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Planner);

        Assert.DoesNotContain(offList, Listed((await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list")).Body!));
        var (status, body) = await Call(proxy, McpServers.CodeGraph, grant.Credential, offList, new JsonObject { ["name"] = "X", ["query"] = "q" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(McpProxy.InvalidParams, body!["error"]!["code"]!.GetValue<int>());
        Assert.Null(body["result"]);
        Assert.Empty(codeGraph.Calls); // nothing forwarded (not even the project lookup)
    }

    [Fact]
    public async Task A_codegraph_query_scoped_to_another_repository_is_refused_and_a_foreign_node_is_not_shown()
    {
        var codeGraph = CodeGraphServer((tool, args) => tool == "read_node_source"
            ? FakeMcpServer.Text("## Secret (Class) — Other\n\nclass Secret {}")
            : FakeMcpServer.Text("ok"));
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Planner);

        foreach (var project in new[] { "Other", "r-tools", "r service" })
        {
            var (_, body) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "search_graph",
                new JsonObject { ["namePattern"] = "Secret%", ["project"] = project });
            Assert.Equal(McpProxy.InvalidParams, body!["error"]!["code"]!.GetValue<int>());
            Assert.Contains("o/r", body["error"]!["message"]!.GetValue<string>());
        }
        Assert.DoesNotContain(codeGraph.Calls, c => c.Tool == "search_graph");

        // Naming the item's own project is fine; an unknown argument is dropped, not forwarded.
        var (_, own) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "search_graph",
            new JsonObject { ["namePattern"] = "X%", ["project"] = Project, ["repo"] = "someone/else" });
        Assert.Equal("ok", own!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var forwarded = Assert.Single(codeGraph.Calls, c => c.Tool == "search_graph").Arguments;
        Assert.Equal(Project, forwarded["project"]!.GetValue<string>());
        Assert.Null(forwarded["repo"]);

        // A node id names a node of any project: one of another project is not shown.
        var (_, node) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "read_node_source", new JsonObject { ["nodeId"] = 42 });
        Assert.True(node!["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain("class Secret", node.ToJsonString());

        // A repository CodeGraph does not index answers an error, and nothing else is asked.
        using var unindexed = proxy.Grant(new RepoRef("nobody", "nothing"), McpProfile.Planner);
        var (_, none) = await Call(proxy, McpServers.CodeGraph, unindexed.Credential, "analyze_impact", new JsonObject { ["name"] = "X" });
        Assert.Contains(ReviewTools.NotIndexed, none!["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_proxy_answers_only_loopback_hosts_without_an_origin_and_with_a_live_credential()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Planner);

        Assert.Equal(HttpStatusCode.OK, (await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list",
            adjust: r => r.Headers.Host = $"localhost:{proxy.BaseAddress.Port}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list",
            adjust: r => r.Headers.Host = $"evil.example:{proxy.BaseAddress.Port}")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list",
            adjust: r => r.Headers.Add("Origin", "http://evil.example"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(proxy, McpServers.CodeGraph, null, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(proxy, McpServers.CodeGraph, "made-up", "tools/list")).Status);
        // An upstream that is not configured (no Kanban token) is not served.
        Assert.Equal(HttpStatusCode.NotFound, (await Rpc(proxy, McpServers.Kanban, grant.Credential, "tools/list")).Status);
        // Only the one allowed request (Host localhost) reached the upstream.
        Assert.Single(codeGraph.Api.Requests, r => r.Body?.Contains("tools/list") == true);
    }

    [Fact]
    public async Task A_configured_kanban_upstream_is_served_read_only_through_the_proxy_and_not_repository_scoped()
    {
        var codeGraph = CodeGraphServer();
        var kanban = KanbanServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph, kanban), CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Planner);
        Assert.Equal([McpServers.CodeGraph, McpServers.Kanban], grant.Servers);

        var listed = Listed((await Rpc(proxy, McpServers.Kanban, grant.Credential, "tools/list")).Body!);
        // The read tools KanbanBoard has (GetWorkItem and SearchWorkItems, allowlisted, come with KanbanBoard sc-25711: not listed yet).
        Assert.Equal(["ListProjects", "GetBoard", "ListEpics", "GetEpic", "ListEpicDocuments", "GetEpicDocument", "ListWorkItems",
            "ListEpicWorkItems", "GetIssues"], listed);

        var (_, board) = await Call(proxy, McpServers.Kanban, grant.Credential, "GetBoard", new JsonObject { ["projectId"] = 3 });
        Assert.Equal("kanban GetBoard {\"projectId\":3}", board!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var (tool, arguments) = Assert.Single(kanban.Calls);
        Assert.Equal("GetBoard", tool);
        Assert.Null(arguments["project"]); // Kanban has no repository mapping: nothing pinned
        Assert.All(kanban.Api.Requests, r => Assert.Equal($"Bearer {KanbanToken}", r.Headers["Authorization"]));
        Assert.All(kanban.Api.Requests, r => Assert.Equal("/mcp", r.PathAndQuery)); // Kanban:McpUrl is the endpoint itself

        foreach (var write in new[] { "CreateWorkItem", "UpdateWorkItem", "DeleteWorkItem", "MoveWorkItem", "AddWorkItemComment", "CreateEpic" })
        {
            Assert.Equal(McpProxy.InvalidParams,
                (await Call(proxy, McpServers.Kanban, grant.Credential, write, new JsonObject { ["title"] = "x" })).Body!["error"]!["code"]!.GetValue<int>());
        }
        Assert.Single(kanban.Calls); // no write was forwarded
        // The session's CodeGraph tools are still the CodeGraph allowlist, never Kanban's (each endpoint serves its own upstream).
        Assert.Equal(McpProxy.InvalidParams,
            (await Call(proxy, McpServers.CodeGraph, grant.Credential, "GetBoard", new JsonObject())).Body!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_session_config_file_names_the_proxy_holds_only_the_session_credential_and_is_deleted_and_refused_after_the_session()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph, KanbanServer()), CancellationToken.None);
        var directory = Path.Combine(Directory.CreateTempSubdirectory("df-mcp-").FullName, McpProxySessions.DirectoryName);
        var sessions = new McpProxySessions(proxy, directory);

        string credential;
        string path;
        await using (var session = await sessions.OpenAsync(Repo, McpProfile.Planner, CancellationToken.None))
        {
            path = session.ConfigPath;
            Assert.True(Path.IsPathFullyQualified(path));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(CodeGraphToken, text);
            Assert.DoesNotContain(KanbanToken, text);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }
            var config = JsonNode.Parse(text)!["mcpServers"]!.AsObject();
            Assert.Equal([McpServers.CodeGraph, McpServers.Kanban], config.Select(s => s.Key));
            Assert.All(config, s =>
            {
                Assert.Equal("http", s.Value!["type"]!.GetValue<string>());
                Assert.StartsWith("http://127.0.0.1:", s.Value["url"]!.GetValue<string>());
            });
            credential = config[McpServers.CodeGraph]!["headers"]!["Authorization"]!.GetValue<string>()["Bearer ".Length..];
            Assert.Equal(HttpStatusCode.OK, (await Rpc(proxy, McpServers.CodeGraph, credential, "tools/list")).Status);
        }
        Assert.False(File.Exists(path));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(proxy, McpServers.CodeGraph, credential, "tools/list")).Status);

        // A crashed process's leftover files are swept (their credentials died with that process's proxy).
        File.WriteAllText(Path.Combine(directory, "left.json"), "{}");
        McpProxySessions.Sweep(directory);
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    // ---- the triage session, end to end (a fake claude that is an MCP client of the proxy) ----

    /// <summary>
    /// A fake claude: it dumps its argv and env, reads its --mcp-config, then (as Claude Code's MCP client would) initializes the
    /// codegraph server, lists its tools and calls analyze_impact through the proxy, saving what it saw and the credential it was
    /// given, and streams a tool_use of the MCP tool and a result.
    /// </summary>
    private const string FakeClaudePython = """
        import json, os, sys, urllib.request
        dump = sys.argv[1]
        args = sys.argv[2:]
        open(os.path.join(dump, "args.txt"), "w").write("\n".join(args))
        open(os.path.join(dump, "env.txt"), "w").write("\n".join(f"{k}={v}" for k, v in os.environ.items()))
        config = json.load(open(args[args.index("--mcp-config") + 1]))
        server = config["mcpServers"]["codegraph"]
        open(os.path.join(dump, "credential.txt"), "w").write(server["headers"]["Authorization"][len("Bearer "):])
        def rpc(method, params):
            body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
            req = urllib.request.Request(server["url"], data=body, method="POST",
                headers=dict(server["headers"], **{"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}))
            return json.load(urllib.request.urlopen(req))
        rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "fake", "version": "1"}})
        listed = [t["name"] for t in rpc("tools/list", {})["result"]["tools"]]
        answer = rpc("tools/call", {"name": "search_graph", "arguments": {"namePattern": "X%"}})["result"]["content"][0]["text"]
        refused = {t: rpc("tools/call", {"name": t, "arguments": {"name": "X"}})["error"]["code"]
            for t in ["ask", "analyze_impact", "trace_call_path", "find_consumers", "find_publishers"]}
        try:
            urllib.request.urlopen(urllib.request.Request(server["url"].replace("/mcp/codegraph", "/mcp/kanban"),
                data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}).encode(), method="POST",
                headers=dict(server["headers"], **{"Content-Type": "application/json"})))
            kanban = 200
        except urllib.error.HTTPError as e:
            kanban = e.code
        open(os.path.join(dump, "mcp.json"), "w").write(json.dumps({"servers": sorted(config["mcpServers"]), "listed": listed,
            "answer": answer, "refused": refused, "kanban": kanban}))
        print(json.dumps({"type": "system", "subtype": "init", "session_id": "triage-mcp"}))
        print(json.dumps({"type": "assistant", "session_id": "triage-mcp", "message": {"content": [
            {"type": "tool_use", "id": "t1", "name": "mcp__codegraph__search_graph", "input": {"namePattern": "X%"}}]}}))
        print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "done", "session_id": "triage-mcp"}))
        """;

    [Fact]
    public async Task A_triage_session_calls_codegraph_through_the_proxy_and_its_env_argv_and_worktree_hold_no_upstream_token()
    {
        var root = Directory.CreateTempSubdirectory("df-mcp-triage-").FullName;
        var worktree = Directory.CreateDirectory(Path.Combine(root, "worktree")).FullName;
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# r\n");
        var dump = Directory.CreateDirectory(Path.Combine(root, "dump")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var python = Path.Combine(bin, "fake-claude.py");
        File.WriteAllText(python, FakeClaudePython);
        var script = Path.Combine(bin, "fake-claude.sh");
        SandboxSupport.ExecutableAt(script, $"#!/bin/sh\nexec python3 '{python}' '{dump}' \"$@\"\n");

        var codeGraph = CodeGraphServer();
        var upstreams = Upstreams(codeGraph, KanbanServer());
        await using var proxy = await McpProxy.StartAsync(upstreams, CancellationToken.None);
        var configs = Path.Combine(root, McpProxySessions.DirectoryName);
        // Kanban is configured here, but the triage profile grants none of it (as production, which does not even start its upstream).
        var tools = WorkerTools.ReadOnly.WithMcp(McpProfile.Triage.ToolRules(upstreams));
        Assert.True(tools.IsReadOnly);
        var worker = new ClaudeWorker(script, new Uri("http://localhost:8080/"), "rk_worker", WorkerAuth.RouterKey, TimeSpan.FromMinutes(1), tools: tools);
        var workspaces = new DirWorkspaces(worktree);
        await using var db = TestDb.Create();
        var ledger = new WorkLedger(db, TimeProvider.System);
        var item = await ledger.GetOrCreateAsync("github", "gh-1", "t", "o/r", null, CancellationToken.None);
        var taints = new List<string>();

        var result = await new WorkerTriageRunner(workspaces, worker, null, TextWriter.Null, new McpProxySessions(proxy, configs)).RunAsync(item, Repo,
            "triage prompt", (_, _) => Task.CompletedTask, (_, reason, _) => { taints.Add(reason); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.Succeeded, result.StderrTail);
        // Its config names only codegraph; it listed exactly the triage profile's tools and called through the proxy, pinned to the
        // item's repository; every other CodeGraph tool was refused, and Kanban is not served to it (404).
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(dump, "mcp.json")))!;
        string[] triageTools = ["search_graph", "get_code_snippet", "read_node_source"];
        Assert.Equal([McpServers.CodeGraph], mcp["servers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(triageTools.Order(), mcp["listed"]!.AsArray().Select(t => t!.GetValue<string>()).Order());
        Assert.Equal($"search_graph of  in {Project}", mcp["answer"]!.GetValue<string>());
        Assert.All(mcp["refused"]!.AsObject(), r => Assert.Equal(McpProxy.InvalidParams, r.Value!.GetValue<int>()));
        Assert.Equal(5, mcp["refused"]!.AsObject().Count);
        Assert.Equal(404, mcp["kanban"]!.GetValue<int>());
        Assert.Equal(["search_projects", "search_graph"], codeGraph.Calls.Select(c => c.Tool));
        // The MCP use tainted the session too (on top of the issue text).
        Assert.Equal([Taint.IssueText, "mcp:mcp__codegraph__search_graph"], taints);

        // E5: no upstream token in the worker's env, argv or worktree; E6: no proxy credential in argv either (only the file's path).
        var credential = File.ReadAllText(Path.Combine(dump, "credential.txt"));
        Assert.True(credential.Length >= 32);
        var args = File.ReadAllText(Path.Combine(dump, "args.txt"));
        var env = File.ReadAllText(Path.Combine(dump, "env.txt"));
        Assert.Contains("ANTHROPIC_AUTH_TOKEN=rk_worker", env); // the dump ran
        foreach (var secret in new[] { CodeGraphToken, KanbanToken, credential })
        {
            Assert.DoesNotContain(secret, args);
            Assert.DoesNotContain(secret, env);
            Assert.All(Directory.EnumerateFiles(worktree, "*", SearchOption.AllDirectories), f => Assert.DoesNotContain(secret, File.ReadAllText(f)));
        }
        var argv = args.Split('\n').ToList();
        Assert.Contains("--strict-mcp-config", argv);
        var configPath = argv[argv.IndexOf("--mcp-config") + 1];
        Assert.StartsWith(configs, configPath);
        Assert.False(configPath.StartsWith(worktree, StringComparison.Ordinal)); // outside the worktree its reads are confined to
        Assert.Equal(triageTools.Select(t => $"mcp__codegraph__{t}").Order(),
            argv.Skip(argv.IndexOf("--allowedTools") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).Where(a => a.StartsWith("mcp__")).Order());
        // Its prompt names the granted tools only.
        var sentPrompt = string.Join("\n", argv.Skip(argv.IndexOf("-p") + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));
        Assert.Contains("mcp__codegraph__search_graph", sentPrompt);
        Assert.DoesNotContain("analyze_impact", sentPrompt);
        Assert.DoesNotContain("kanban", sentPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("dontAsk", argv[argv.IndexOf("--permission-mode") + 1]);
        Assert.Equal("", argv[argv.IndexOf("--setting-sources") + 1]);

        // The session ended: its config file is gone and its credential refused.
        Assert.False(File.Exists(configPath));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Rpc(proxy, McpServers.CodeGraph, credential, "tools/list")).Status);
    }

    [Fact]
    public async Task Implementer_and_fixer_launches_carry_no_mcp_configuration()
    {
        // Implementers and fixers (review, CI and conflict fixers all run WorkerTools.Implementer) get --strict-mcp-config and no server.
        Assert.Empty(WorkerTools.Implementer.McpAllowed);
        var implementer = ClaudeWorker.BuildArguments("go", tools: WorkerTools.Implementer).ToList();
        Assert.Contains("--strict-mcp-config", implementer);
        Assert.DoesNotContain("--mcp-config", implementer);
        Assert.DoesNotContain(implementer, a => a.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ClaudeWorker.BuildArguments("go", tools: WorkerTools.Implementer, mcpConfigPath: "/tmp/x.json"));

        // A real launch as the pipeline makes it: argv has no MCP config; handing an implementer one is refused before anything starts.
        var root = Directory.CreateTempSubdirectory("df-mcp-impl-").FullName;
        var argsDump = Path.Combine(root, "args.txt");
        var script = Path.Combine(root, "fake-claude.sh");
        SandboxSupport.ExecutableAt(script, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > "{{argsDump}}"
            echo '{"type":"system","subtype":"init","session_id":"impl-1"}'
            echo '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"impl-1"}'
            """);
        var worker = new ClaudeWorker(script, new Uri("http://localhost:8080/"), "rk", WorkerAuth.RouterKey, TimeSpan.FromMinutes(1));
        Assert.True((await worker.RunAsync(root, "implement", null, WorkerModelClass.Mid, null, CancellationToken.None)).Succeeded);
        var argv = File.ReadAllLines(argsDump);
        Assert.Contains("--strict-mcp-config", argv);
        Assert.DoesNotContain("--mcp-config", argv);
        File.Delete(argsDump);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            worker.RunAsync(root, "implement", null, WorkerModelClass.Mid, null, Path.Combine(root, "x.json"), CancellationToken.None));
        Assert.False(File.Exists(argsDump)); // never started

        // The read-only tools accept exactly the allowlisted MCP tools.
        Assert.True(WorkerTools.ReadOnly.WithMcp(McpServers.AllToolRules).IsReadOnly);
        Assert.Throws<ArgumentException>(() => WorkerTools.ReadOnly.WithMcp(["mcp__codegraph__ask"]));
        Assert.Throws<ArgumentException>(() => WorkerTools.ReadOnly.WithMcp(["mcp__kanban__CreateWorkItem"]));
        Assert.Throws<ArgumentException>(() => WorkerTools.ReadOnly.WithMcp(["mcp__codegraph"]));
        // Only read-only, confined tools take MCP tools: an implementer's never do.
        Assert.Throws<ArgumentException>(() => WorkerTools.Implementer.WithMcp(McpProfile.Triage.ToolRules(Upstreams(CodeGraphServer()))));
    }

    [Fact]
    public async Task A_triage_grant_serves_only_the_codegraph_tools_whose_answers_stay_in_the_pinned_project_and_no_kanban()
    {
        var codeGraph = CodeGraphServer();
        var kanban = KanbanServer();
        var upstreams = Upstreams(codeGraph, kanban);
        await using var proxy = await McpProxy.StartAsync(upstreams, CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Triage);
        string[] triageTools = ["search_graph", "get_code_snippet", "read_node_source"];

        Assert.Equal([McpServers.CodeGraph], grant.Servers);
        Assert.Equal([McpServers.CodeGraph], JsonNode.Parse(grant.McpConfigJson())!["mcpServers"]!.AsObject().Select(s => s.Key));
        Assert.Equal(triageTools.Select(t => $"mcp__codegraph__{t}"), McpProfile.Triage.ToolRules(upstreams));
        Assert.Equal(triageTools.Order(), Listed((await Rpc(proxy, McpServers.CodeGraph, grant.Credential, "tools/list")).Body!).Order());
        foreach (var crossProject in new[] { "analyze_impact", "trace_call_path", "find_consumers", "find_publishers" })
        {
            var (_, body) = await Call(proxy, McpServers.CodeGraph, grant.Credential, crossProject, new JsonObject { ["name"] = "X" });
            Assert.Equal(McpProxy.InvalidParams, body!["error"]!["code"]!.GetValue<int>());
        }
        Assert.Empty(codeGraph.Calls); // nothing forwarded, not even the project lookup
        var (_, own) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "search_graph", new JsonObject { ["namePattern"] = "X%" });
        Assert.False(own!["result"]!["isError"]!.GetValue<bool>());
        // Kanban is configured, but not served to a triage credential at all.
        Assert.Equal(HttpStatusCode.NotFound, (await Rpc(proxy, McpServers.Kanban, grant.Credential, "tools/list")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(proxy, McpServers.Kanban, grant.Credential, "GetBoard", new JsonObject())).Status);
        Assert.Empty(kanban.Api.Requests);
        // The production triage worker's --allowedTools: exactly these MCP tools, even with Kanban configured.
        Assert.Equal(triageTools.Select(t => $"mcp__codegraph__{t}"), SandboxTriageRunner.TriageTools(upstreams, proxied: true).McpAllowed);
        Assert.Empty(SandboxTriageRunner.TriageTools(upstreams, proxied: false).McpAllowed);
        // The planner profile (Phase 4) keeps every allowlisted tool of both.
        using var planner = proxy.Grant(Repo, McpProfile.Planner);
        Assert.Equal(McpServers.AllToolRules, planner.ToolRules);
    }

    [Fact]
    public async Task A_relayed_answer_is_cut_with_a_marker_and_a_grant_past_its_call_budget_is_refused()
    {
        var huge = new string('x', McpProxy.MaxAnswerChars * 2);
        var codeGraph = CodeGraphServer((_, _) => FakeMcpServer.Text(huge));
        var kanban = new FakeMcpServer("/mcp", KanbanListed, (_, _) => FakeMcpServer.Text(huge));
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph, kanban), CancellationToken.None);
        using var grant = proxy.Grant(Repo, McpProfile.Planner);

        foreach (var (server, tool, args) in new[]
        {
            (McpServers.CodeGraph, "search_graph", new JsonObject { ["namePattern"] = "X%" }),
            (McpServers.Kanban, "GetBoard", new JsonObject { ["projectId"] = 3 }),
        })
        {
            var text = (await Call(proxy, server, grant.Credential, tool, args)).Body!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("[cut: ", text);
            Assert.True(text.Length < McpProxy.MaxAnswerChars + 200, $"{server}: {text.Length}");
        }

        // The budget: two calls made above; the rest up to the cap are served, the next is refused and not forwarded.
        for (var i = 2; i < McpProxy.MaxCallsPerGrant; i++)
        {
            Assert.NotNull((await Call(proxy, McpServers.Kanban, grant.Credential, "GetBoard", new JsonObject())).Body!["result"]);
        }
        var forwarded = kanban.Calls.Count;
        var (_, over) = await Call(proxy, McpServers.CodeGraph, grant.Credential, "search_graph", new JsonObject { ["namePattern"] = "X%" });
        Assert.Equal(McpProxy.InvalidParams, over!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(forwarded, kanban.Calls.Count);
        Assert.Single(codeGraph.Calls, c => c.Tool == "search_graph");
        // Another session's budget is its own.
        using var other = proxy.Grant(Repo, McpProfile.Planner);
        Assert.NotNull((await Call(proxy, McpServers.Kanban, other.Credential, "GetBoard", new JsonObject())).Body!["result"]);
    }

    [Fact]
    public async Task A_session_config_the_worker_user_cannot_read_by_acl_is_a_factory_wide_failure_and_leaves_nothing()
    {
        await using var proxy = await McpProxy.StartAsync(Upstreams(CodeGraphServer()), CancellationToken.None);
        var directory = Path.Combine(Directory.CreateTempSubdirectory("df-mcp-acl-").FullName, McpProxySessions.DirectoryName);
        const string granted = "drwx------+ 2 me staff 64 Oct 10 12:00 x\n 0: user:_factory inherited allow list,search,read,readattr\n";
        const string fileGranted = "-rw-------+ 1 me staff 9 Oct 10 12:00 x\n 0: user:_factory inherited allow read,readattr\n";
        string Acl(string path) => path.EndsWith(".json", StringComparison.Ordinal) ? fileGranted : granted;

        await using (var ok = await new McpProxySessions(proxy, directory, "_factory", (p, _) => Task.FromResult(Acl(p)))
            .OpenAsync(Repo, McpProfile.Triage, CancellationToken.None))
        {
            Assert.True(File.Exists(ok.ConfigPath));
        }

        // The file carries no read entry for the worker user: refused factory-wide, its file deleted, nothing granted.
        var seen = new List<string>();
        var missing = new McpProxySessions(proxy, directory, "_factory", (p, _) =>
        {
            seen.Add(p);
            return Task.FromResult(p.EndsWith(".json", StringComparison.Ordinal) ? "-rw-------  1 me staff 9 Oct 10 12:00 x\n" : granted);
        });
        var ex = await Assert.ThrowsAsync<WorkSources.FactoryUnavailableException>(() => missing.OpenAsync(Repo, McpProfile.Triage, CancellationToken.None));
        Assert.Contains("setup-worker-user.sh", ex.Message);
        Assert.Empty(Directory.EnumerateFiles(directory));
        Assert.Equal(2, seen.Count);
        // Without a worker user (Worker:RunAs=none) nothing is checked.
        await using (await new McpProxySessions(proxy, directory, null, (_, _) => throw new InvalidOperationException("not asked"))
            .OpenAsync(Repo, McpProfile.Triage, CancellationToken.None))
        {
        }
    }

    [Theory]
    [InlineData(" 0: user:_factory inherited allow read,readattr", "read", true)]
    [InlineData(" 0: user:_factory allow list,search,read", "search", true)]
    [InlineData(" 0: user:_factory inherited allow readattr,readextattr", "read", false)]
    [InlineData(" 0: user:_factory2 inherited allow read", "read", false)]
    [InlineData(" 0: group:_factory inherited allow read", "read", false)]
    [InlineData(" 0: user:_factory deny read\n 1: user:_factory allow read", "read", false)]
    [InlineData("-rw-------  1 me staff 9 Oct 10 12:00 user:_factory allow read", "read", false)]
    public void An_acl_listing_grants_a_right_only_through_an_allow_entry_for_that_user_and_no_deny(string listing, string right, bool grants) =>
        Assert.Equal(grants, McpProxySessions.AclGrants(listing, "_factory", right));

    [Fact]
    public async Task A_failing_mcp_config_sweep_or_proxy_start_is_factory_wide()
    {
        var upstreams = Upstreams(CodeGraphServer());
        var configs = Directory.CreateTempSubdirectory("df-mcp-sweep-").FullName;
        File.WriteAllText(Path.Combine(configs, "left.json"), "{}");
        File.SetUnixFileMode(configs, UnixFileMode.UserRead | UnixFileMode.UserExecute); // its files cannot be deleted
        try
        {
            var swept = await Assert.ThrowsAsync<WorkSources.FactoryUnavailableException>(() =>
                SandboxTriageRunner.StartMcpProxyAsync(upstreams, configs, CancellationToken.None));
            Assert.StartsWith("the MCP proxy: ", swept.Message);
        }
        finally
        {
            File.SetUnixFileMode(configs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var started = await Assert.ThrowsAsync<WorkSources.FactoryUnavailableException>(() =>
            SandboxTriageRunner.StartMcpProxyAsync(upstreams, configs, CancellationToken.None,
                (_, _) => throw new IOException("address in use")));
        Assert.Contains("address in use", started.Message);
        // Without CodeGraph no proxy is started.
        Assert.Null(await SandboxTriageRunner.StartMcpProxyAsync(McpUpstreams.None, configs, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("not started")));
    }

    [Fact]
    public void Each_upstream_has_one_read_only_allowlist_shared_by_the_proxy_and_the_reviewers()
    {
        var codeGraph = new CodeGraphMcpClient(new HttpClient(), "t");
        Assert.Same(ReviewTools.CodeGraphTools, codeGraph.Upstream.Allowlist);
        Assert.True(codeGraph.Upstream.RepoScoped);
        Assert.Equal(["ListProjects", "GetBoard", "ListEpics", "GetEpic", "ListEpicDocuments", "GetEpicDocument", "ListWorkItems",
            "ListEpicWorkItems", "GetIssues", "GetWorkItem", "SearchWorkItems"], McpServers.KanbanTools);
        Assert.DoesNotContain(McpServers.KanbanTools, t => t.StartsWith("Create") || t.StartsWith("Update") || t.StartsWith("Delete")
            || t.StartsWith("Move") || t.EndsWith("Comment"));
        Assert.Equal(ReviewTools.CodeGraphTools.Count + McpServers.KanbanTools.Count, McpServers.AllToolRules.Count);
    }

    /// <summary>A triage checkout that is a fixed local directory (left in place, so the test can read it after the run).</summary>
    private sealed class DirWorkspaces(string dir) : Git.IRepoWorkspace
    {
        public Task<Git.Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) =>
            Task.FromResult(new Git.Workspace(dir, branch, "main", Path.Combine(dir, ".admin")));
        public Task RemoveAsync(RepoRef repo, Git.Workspace workspace, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CommitAndPushAsync(RepoRef repo, Git.Workspace workspace, string message, PushGrant grant, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task PushAsync(RepoRef repo, Git.Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<Git.Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<Git.Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> HeadAsync(Git.Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<BaseMerge> MergeBaseAsync(RepoRef repo, Git.Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
