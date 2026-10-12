using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;
using DarkFactory.Orchestrator.Git;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Mcp;
using DarkFactory.Orchestrator.Plans;
using DarkFactory.Orchestrator.Shortcut;
using DarkFactory.Orchestrator.Tests.Support;
using DarkFactory.Orchestrator.Worker;
using DarkFactory.Orchestrator.WorkSources;
using static DarkFactory.Orchestrator.Tests.RunPipelineTests;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// sc-25874: the planner's cross-repository tools — a proxy grant over a plan's set of repositories, the model-free cross-repository
/// CodeGraph tools only the planner profile holds, and the three tool lists pinned.
/// </summary>
public class PlannerCrossRepoProxyTests
{
    private const string CodeGraphToken = "cg_upstream_secret_7f3a";
    private static readonly RepoRef Widgets = new("acme", "widgets");
    private static readonly RepoRef Gears = new("acme", "gears");

    private const string Listing =
        "- **Widgets** [rust] (indexed: 2026-10-01)\n  Repo: https://github.com/acme/widgets\n"
        + "- **gears-engine** [rust] (indexed: 2026-10-03)\n  Repo: https://github.com/Acme/Gears.git\n"
        + "- **Secret** [csharp] (indexed: 2026-10-03)\n  Repo: https://github.com/someone/secret\n";

    /// <summary>Tools CodeGraph lists that no session may ever see: model output (stored or live), embeddings, memory, external hubs.</summary>
    private static readonly string[] OffEveryList =
        ["get_architecture", "get_service_summary", "get_project_health", "project_report", "search_conventions", "rag_search", "memory_store",
            "store_memory_v2", "stories-search", "stories-create", "list_schemas", "get_schema_catalog", "ask", "list_projects", "get_fleet_health"];

    private static FakeMcpServer CodeGraphServer(Func<string, JsonObject, JsonObject>? answer = null) =>
        new("/mcp", [.. McpServers.CodeGraphTools, .. OffEveryList], (tool, args) => answer?.Invoke(tool, args)
            ?? (tool == "search_projects" ? FakeMcpServer.Text(Listing) : FakeMcpServer.Text($"{tool} {args.ToJsonString()}")));

    private static McpUpstreams Upstreams(FakeMcpServer codeGraph) =>
        new(new CodeGraphMcpClient(codeGraph.Client("https://codegraph.test/"), CodeGraphToken), null);

    private static async Task<JsonObject> Rpc(McpProxy proxy, string credential, string method, JsonObject? parameters = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, proxy.Endpoint(McpServers.CodeGraph))
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = method, ["params"] = parameters ?? new JsonObject() }
                .ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static Task<JsonObject> Call(McpProxy proxy, McpProxyGrant grant, string tool, JsonObject arguments) =>
        Rpc(proxy, grant.Credential, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });

    private static string Text(JsonObject body) => body["result"]!["content"]![0]!["text"]!.GetValue<string>();

    /// <summary>The upstream's <c>tools/call</c> requests other than the orchestrator's own project lookups (which carry <c>search</c> = a repo name).</summary>
    private static List<(string Tool, JsonObject Arguments)> Forwarded(FakeMcpServer codeGraph) => codeGraph.Calls
        .Where(c => !(c.Tool == "search_projects" && c.Arguments["search"]?.GetValue<string>() is "widgets" or "gears" or "secret" or "unknown")).ToList();

    private static void AssertRefused(JsonObject body, string says)
    {
        Assert.Equal(McpProxy.InvalidParams, body["error"]!["code"]!.GetValue<int>());
        Assert.Null(body["result"]);
        Assert.Contains(says, body["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_planner_granted_two_repositories_gets_answers_for_each_and_a_third_is_refused_with_nothing_forwarded()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant([Widgets, Gears], McpProfile.Planner);
        Assert.Equal([Widgets, Gears], grant.Repos);

        // Each project, named by its repository (case-insensitively, like GitHub's names) or by its CodeGraph project.
        var widgets = await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "Spin%", ["repo"] = "acme/widgets" });
        var gears = await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "Spin%", ["repo"] = "Acme/Gears" });
        var byProject = await Call(proxy, grant, "analyze_impact", new JsonObject { ["name"] = "Spin", ["project"] = "gears-engine" });
        var both = await Call(proxy, grant, "find_consumers", new JsonObject { ["name"] = "Spun", ["repo"] = "acme/gears", ["project"] = "gears-engine" });

        Assert.StartsWith("CodeGraph's index of Widgets's default branch, commit unknown:\nsearch_graph ", Text(widgets));
        Assert.StartsWith("CodeGraph's index of gears-engine's default branch, commit unknown:\nsearch_graph ", Text(gears));
        Assert.StartsWith("CodeGraph's index of gears-engine's default branch, commit unknown:\nanalyze_impact ", Text(byProject));
        Assert.False(both["result"]!["isError"]!.GetValue<bool>());
        var sent = Forwarded(codeGraph);
        Assert.Equal(["Widgets", "gears-engine", "gears-engine", "gears-engine"], sent.Select(c => c.Arguments["project"]!.GetValue<string>()));
        Assert.All(sent, c => Assert.Null(c.Arguments["repo"])); // the session's selector is never forwarded
        // Each repository's project was resolved once for the session, by exact repository URL.
        Assert.Equal(2, codeGraph.Calls.Count - sent.Count);

        // A third repository or project, none at all, two that disagree, or a selector that is not a string: refused, nothing forwarded.
        var before = codeGraph.Calls.Count;
        AssertRefused(await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "S%", ["repo"] = "someone/secret" }), "acme/widgets, acme/gears");
        AssertRefused(await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "S%", ["project"] = "Secret" }), "another project is refused");
        AssertRefused(await Call(proxy, grant, "trace_data_lineage", new JsonObject { ["modelName"] = "Order", ["repo"] = "acme/gears", ["project"] = "Secret" }),
            "another project is refused");
        AssertRefused(await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "S%" }), "needs 'repo'");
        AssertRefused(await Call(proxy, grant, "analyze_impact_file", new JsonObject { ["path"] = "src/a.rs" }), "needs 'repo'");
        AssertRefused(await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "S%", ["repo"] = "acme/widgets", ["project"] = "gears-engine" }),
            "different repositories");
        AssertRefused(await Call(proxy, grant, "search_graph", new JsonObject { ["namePattern"] = "S%", ["repo"] = 3 }), "must be one of");
        // A tool about every project still refuses a third repository named anyway.
        AssertRefused(await Call(proxy, grant, "search_projects", new JsonObject { ["repo"] = "someone/secret" }), "must be one of");
        // Off the planner's allowlist: refused whatever it names.
        foreach (var off in OffEveryList)
        {
            AssertRefused(await Call(proxy, grant, off, new JsonObject { ["repo"] = "acme/widgets", ["project"] = "Widgets" }), "Unknown tool");
        }
        Assert.Equal(before, codeGraph.Calls.Count);
    }

    [Fact]
    public async Task A_multi_repository_grant_lists_a_required_repo_argument_on_each_tool_about_one_project_and_says_so()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant([Widgets, Gears], McpProfile.Planner);
        using var single = proxy.Grant(Widgets, McpProfile.Planner);

        var tools = (await Rpc(proxy, grant.Credential, "tools/list"))["result"]!["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        var alone = (await Rpc(proxy, single.Credential, "tools/list"))["result"]!["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

        Assert.Equal(McpServers.CodeGraphTools.Order(), tools.Keys.Order());
        Assert.Equal(McpServers.CodeGraphTools.Order(), alone.Keys.Order());
        string[] unscoped = ["search_projects", "get_service_clusters", "get_cluster_detail", "read_node_source"];
        foreach (var (name, tool) in tools)
        {
            var schema = tool["inputSchema"]!;
            Assert.Null(schema["properties"]?["project"]); // only the proxy sets a project
            Assert.Null(schema["properties"]?["sha"]);
            var repo = schema["properties"]?["repo"];
            var required = (schema["required"] as JsonArray ?? []).Select(r => r!.GetValue<string>()).ToList();
            if (unscoped.Contains(name))
            {
                Assert.Null(repo);
                Assert.DoesNotContain("repo", required);
            }
            else
            {
                Assert.Equal(["acme/widgets", "acme/gears"], repo!["enum"]!.AsArray().Select(e => e!.GetValue<string>()));
                Assert.Contains("repo", required);
                Assert.Contains("named by the required 'repo' argument: acme/widgets, acme/gears", tool["description"]!.GetValue<string>());
            }
            // One repository: no repo argument at all, and the description of a tool about one project is the single-repository one.
            Assert.Null(alone[name]["inputSchema"]!["properties"]?["repo"]);
        }
        Assert.Contains("every project CodeGraph indexes", tools["search_projects"]["description"]!.GetValue<string>());
        Assert.Contains("Shown only for a node of acme/widgets, acme/gears", tools["read_node_source"]["description"]!.GetValue<string>());
        Assert.Equal("upstream search_graph (Asked about acme/widgets only; CodeGraph indexes its default branch, and each answer starts "
            + "with the commit of that index it describes, or says the commit is unknown.)", alone["search_graph"]["description"]!.GetValue<string>());
        // The seven keep the reviewers' schemas, plus the selector.
        Assert.Equal(["namePattern", "label", "limit", "repo"], tools["search_graph"]["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["namePattern", "repo"], tools["search_graph"]["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task A_node_is_shown_only_when_it_belongs_to_a_project_of_the_grants_set()
    {
        var codeGraph = CodeGraphServer((tool, args) => tool switch
        {
            "search_projects" => FakeMcpServer.Text(Listing),
            "read_node_source" => args["nodeId"]!.GetValue<long>() switch
            {
                1 => FakeMcpServer.Text("## Spin (Function) — Widgets\n\nfn spin() {}"),
                2 => FakeMcpServer.Text("## Mesh (Function) — gears-engine\n\nfn mesh() {}"),
                3 => FakeMcpServer.Text("## Vault (Class) — Secret\n\nclass Vault {}"),
                _ => FakeMcpServer.Structured("Node not found.", new JsonObject(), isError: true),
            },
            _ => FakeMcpServer.Text("ok"),
        });
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant([Widgets, Gears], McpProfile.Planner);

        var widgets = await Call(proxy, grant, "read_node_source", new JsonObject { ["nodeId"] = 1 });
        var gears = await Call(proxy, grant, "read_node_source", new JsonObject { ["nodeId"] = 2 });
        var outside = await Call(proxy, grant, "read_node_source", new JsonObject { ["nodeId"] = 3 });
        var missing = await Call(proxy, grant, "read_node_source", new JsonObject { ["nodeId"] = 9 });
        var malformed = await Call(proxy, grant, "read_node_source", new JsonObject { ["nodeId"] = "x" });

        Assert.Equal("CodeGraph's index of Widgets's default branch, commit unknown:\n## Spin (Function) — Widgets\n\nfn spin() {}", Text(widgets));
        Assert.StartsWith("CodeGraph's index of gears-engine's default branch, commit unknown:\n## Mesh", Text(gears));
        Assert.True(outside["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("Node 3 is not a node of Widgets or gears-engine with readable source; not shown.", Text(outside));
        Assert.DoesNotContain("class Vault", outside.ToJsonString());
        Assert.Equal("CodeGraph's read_node_source (CodeGraph answered with an error):\nNode not found.", Text(missing));
        Assert.Equal("read_node_source needs a numeric 'nodeId'.", Text(malformed));

        // A single-repository grant (triage's, a one-repository plan's) is as before: only its own project's nodes.
        using var single = proxy.Grant(Widgets, McpProfile.Triage);
        Assert.StartsWith("CodeGraph's index of Widgets's default branch", Text(await Call(proxy, single, "read_node_source", new JsonObject { ["nodeId"] = 1 })));
        Assert.Equal("Node 2 is not a node of Widgets with readable source; not shown.",
            Text(await Call(proxy, single, "read_node_source", new JsonObject { ["nodeId"] = 2 })));
    }

    [Fact]
    public async Task Only_the_planner_profile_is_granted_several_repositories_or_a_cross_repository_tool()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);

        var ex = Assert.Throws<ArgumentException>(() => proxy.Grant([Widgets, Gears], McpProfile.Triage));
        Assert.Contains("Only the planner profile", ex.Message);
        Assert.Throws<ArgumentException>(() => proxy.Grant([], McpProfile.Planner));
        // The same repository twice (GitHub's names are case-insensitive) is one repository.
        using var same = proxy.Grant([Widgets, new RepoRef("ACME", "Widgets")], McpProfile.Triage);
        Assert.Equal([Widgets], same.Repos);

        // A tool on the planner's list requested under the triage profile: not listed, refused, never forwarded.
        using var triage = proxy.Grant(Widgets, McpProfile.Triage);
        var listed = (await Rpc(proxy, triage.Credential, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["search_graph", "get_code_snippet", "read_node_source"], listed);
        foreach (var tool in PlannerCodeGraph.Tools)
        {
            AssertRefused(await Call(proxy, triage, tool, new JsonObject { ["path"] = "src/a.rs", ["modelName"] = "M", ["clusterId"] = 1 }), "Unknown tool");
        }
        Assert.Empty(codeGraph.Calls);
        Assert.DoesNotContain(SandboxTriageRunner.TriageTools(Upstreams(codeGraph), proxied: true).McpAllowed,
            r => PlannerCodeGraph.Tools.Any(t => r.EndsWith("__" + t, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Each_cross_repository_tool_is_forwarded_with_rebuilt_arguments_and_the_project_the_proxy_sets()
    {
        var codeGraph = CodeGraphServer();
        await using var proxy = await McpProxy.StartAsync(Upstreams(codeGraph), CancellationToken.None);
        using var grant = proxy.Grant([Widgets, Gears], McpProfile.Planner);
        var longName = new string('m', ReviewTools.MaxCodeGraphArgumentChars + 50);

        var lineage = await Call(proxy, grant, "trace_data_lineage",
            new JsonObject { ["modelName"] = longName, ["repo"] = "acme/gears", ["sha"] = new string('a', 40), ["extra"] = true });
        var impact = await Call(proxy, grant, "analyze_impact_file",
            new JsonObject { ["path"] = "./src/../src/lib.rs", ["depth"] = 99, ["repo"] = "acme/widgets", ["sha"] = new string('a', 40) });
        var hotspot = await Call(proxy, grant, "file_hotspot", new JsonObject { ["path"] = "Cargo.toml", ["repo"] = "acme/gears", ["project"] = "gears-engine" });
        var projects = await Call(proxy, grant, "search_projects",
            new JsonObject { ["search"] = " gear* ", ["group"] = "rust", ["page"] = 0, ["pageSize"] = 5000, ["project"] = "Widgets", ["x"] = 1 });
        var clusters = await Call(proxy, grant, "get_service_clusters", new JsonObject { ["anything"] = "dropped" });
        var detail = await Call(proxy, grant, "get_cluster_detail", new JsonObject { ["clusterId"] = 4, ["project"] = "gears-engine" });

        var sent = Forwarded(codeGraph).ToDictionary(c => c.Tool, c => c.Arguments.ToJsonString());
        Assert.Equal($$"""{"modelName":"{{longName[..ReviewTools.MaxCodeGraphArgumentChars]}}","project":"gears-engine"}""", sent["trace_data_lineage"]);
        // CodeGraph's per-file tools take the project as "repo"; no sha (the default branch), the depth clamped, the path normalised.
        Assert.Equal("""{"path":"src/lib.rs","depth":5,"repo":"Widgets"}""", sent["analyze_impact_file"]);
        Assert.Equal("""{"path":"Cargo.toml","repo":"gears-engine"}""", sent["file_hotspot"]);
        // Not about one project: no project is set, whatever the call named.
        Assert.Equal("""{"search":"gear*","group":"rust","page":1,"pageSize":100}""", sent["search_projects"]);
        Assert.Equal("{}", sent["get_service_clusters"]);
        Assert.Equal("""{"clusterId":4}""", sent["get_cluster_detail"]);

        Assert.StartsWith("CodeGraph's index of gears-engine's default branch, commit unknown:\n", Text(lineage));
        Assert.StartsWith("CodeGraph's index of Widgets's default branch, commit unknown:\n", Text(impact));
        Assert.StartsWith("CodeGraph's index of gears-engine's default branch, commit unknown:\n", Text(hotspot));
        Assert.All(new[] { projects, clusters, detail }, a => Assert.Contains("across every project it indexes (default branches):\n", Text(a)));
        Assert.StartsWith("CodeGraph's search_projects, across every project it indexes", Text(projects));

        // A missing or malformed required argument answers the tool's usage and forwards nothing.
        var before = codeGraph.Calls.Count;
        foreach (var (tool, bad) in new (string, JsonObject)[]
        {
            ("trace_data_lineage", new JsonObject { ["repo"] = "acme/gears", ["modelName"] = "  " }),
            ("analyze_impact_file", new JsonObject { ["repo"] = "acme/gears", ["path"] = "../outside.rs" }),
            ("file_hotspot", new JsonObject { ["repo"] = "acme/gears", ["path"] = "/etc/passwd" }),
            ("get_cluster_detail", new JsonObject { ["clusterId"] = "four" }),
        })
        {
            var answer = await Call(proxy, grant, tool, bad);
            Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
            Assert.Equal(PlannerCodeGraph.Usage(tool), Text(answer));
        }
        Assert.Equal(before, codeGraph.Calls.Count);

        // One repository: the same tools, the project set without any selector.
        using var single = proxy.Grant(Gears, McpProfile.Planner);
        await Call(proxy, single, "file_hotspot", new JsonObject { ["path"] = "src/mesh.rs" });
        Assert.Equal("""{"path":"src/mesh.rs","repo":"gears-engine"}""", codeGraph.Calls[^1].Arguments.ToJsonString());
        // ... and on the per-file tools, where 'repo' is CodeGraph's own project argument, one naming anything but that repository
        // is refused, nothing forwarded; its own name, or its pinned project, is accepted and overwritten by the proxy. (On any
        // other tool a 'repo' is dropped as an unknown argument: McpProxyTests pins that.)
        var pinnedBefore = codeGraph.Calls.Count;
        foreach (var other in new JsonNode[] { "Secret", "someone/secret", "acme/widgets", "GEARS-ENGINE", 7 })
        {
            foreach (var tool in new[] { "file_hotspot", "analyze_impact_file" })
            {
                AssertRefused(await Call(proxy, single, tool, new JsonObject { ["path"] = "src/mesh.rs", ["Repo"] = other.DeepClone() }),
                    "pinned to acme/gears; another repository is refused");
            }
        }
        Assert.Equal(pinnedBefore, codeGraph.Calls.Count);
        foreach (var (own, path) in new[] { (" ACME/Gears ", "src/by-name.rs"), ("gears-engine", "src/by-project.rs") })
        {
            await Call(proxy, single, "analyze_impact_file", new JsonObject { ["path"] = path, ["repo"] = own });
            Assert.Equal($$"""{"path":"{{path}}","repo":"gears-engine"}""", codeGraph.Calls[^1].Arguments.ToJsonString());
        }
        // A repository CodeGraph does not index: an error result naming it, nothing asked.
        using var unindexed = proxy.Grant([Widgets, new RepoRef("acme", "unknown")], McpProfile.Planner);
        var none = await Call(proxy, unindexed, "file_hotspot", new JsonObject { ["path"] = "a.rs", ["repo"] = "acme/unknown" });
        Assert.Equal($"acme/unknown: {ReviewTools.NotIndexed}; file_hotspot cannot answer.", Text(none));
    }

    [Fact]
    public void The_cross_repository_arguments_keep_only_what_each_tool_takes()
    {
        static string? Built(string tool, string json) => PlannerCodeGraph.Arguments(tool, JsonDocument.Parse(json).RootElement)?.ToJsonString();

        Assert.Equal("{}", Built("search_projects", "{}"));
        Assert.Equal("""{"search":"x","page":1000,"pageSize":1}""", Built("search_projects", """{"search":"x","page":99999,"pageSize":0,"group":7}"""));
        Assert.Equal("""{"modelName":"Order"}""", Built("trace_data_lineage", """{"modelName":" Order ","project":"Other","sha":"x"}"""));
        Assert.Null(Built("trace_data_lineage", """{"modelName":5}"""));
        Assert.Equal("{}", Built("get_service_clusters", """{"repo":"x"}"""));
        Assert.Equal("""{"clusterId":3}""", Built("get_cluster_detail", """{"clusterId":3,"repo":"x"}"""));
        Assert.Null(Built("get_cluster_detail", "{}"));
        Assert.Equal("""{"path":"a/b.rs","depth":1}""", Built("analyze_impact_file", """{"path":"a/./b.rs","depth":-4,"repo":"Other","sha":"x"}"""));
        Assert.Null(Built("analyze_impact_file", """{"depth":2}"""));
        Assert.Equal("""{"path":"a.rs"}""", Built("file_hotspot", """{"path":"a.rs","repo":"Other"}"""));
        Assert.Null(Built("file_hotspot", """{"path":"a/../../b.rs"}"""));
        Assert.Null(Built("file_hotspot", "[]"));
        // A path is bounded after normalising, and never cut (a cut path would name another file): past the bound, a usage error.
        var longest = new string('p', PlannerCodeGraph.MaxPathChars);
        Assert.Equal($$"""{"path":"{{longest}}"}""", Built("file_hotspot", $$"""{"path":"./{{longest}}"}"""));
        Assert.Null(Built("file_hotspot", $$"""{"path":"{{longest}}q"}"""));
        Assert.Null(Built("analyze_impact_file", $$"""{"path":"{{longest}}q","depth":2}"""));
        // Not one of the planner's tools: nothing is built here (the reviewers' seven have their own, ReviewTools.CodeGraphArguments).
        Assert.Null(Built("analyze_impact", """{"name":"X"}"""));
        Assert.Null(Built("get_architecture", "{}"));
        Assert.All(PlannerCodeGraph.Tools, t => Assert.NotNull(PlannerCodeGraph.InputSchema(t)));
        Assert.All(ReviewTools.CodeGraphTools, t => Assert.Null(PlannerCodeGraph.InputSchema(t)));
    }

    [Fact]
    public void The_reviewers_the_triage_and_the_planner_tool_lists_are_pinned()
    {
        // The reviewers' tool list and the triage profile did not change with sc-25874.
        Assert.Equal(["read_file", "list_files", "grep", "analyze_impact", "search_graph", "trace_call_path", "find_consumers", "find_publishers",
            "get_code_snippet", "read_node_source"], ReviewTools.Names);
        Assert.Equal(["analyze_impact", "search_graph", "trace_call_path", "find_consumers", "find_publishers", "get_code_snippet", "read_node_source"],
            ReviewTools.CodeGraphTools);
        Assert.Equal(["search_graph", "get_code_snippet", "read_node_source"], McpProfile.Triage.ToolsOf(McpServers.CodeGraph));
        Assert.Empty(McpProfile.Triage.ToolsOf(McpServers.Kanban));
        // The planner: the reviewers' seven, then the six model-free cross-repository tools; Kanban's read tools.
        Assert.Equal(["search_projects", "analyze_impact_file", "trace_data_lineage", "get_service_clusters", "get_cluster_detail", "file_hotspot"],
            PlannerCodeGraph.Tools);
        Assert.Equal([.. ReviewTools.CodeGraphTools, .. PlannerCodeGraph.Tools], McpProfile.Planner.ToolsOf(McpServers.CodeGraph));
        Assert.Equal(McpServers.KanbanTools, McpProfile.Planner.ToolsOf(McpServers.Kanban));
        Assert.Equal(McpProfile.Planner.ToolsOf(McpServers.CodeGraph), McpServers.CodeGraphTools);
        // No cross-repository tool is a reviewers' tool.
        Assert.DoesNotContain(PlannerCodeGraph.Tools, ReviewTools.Names.Contains);
        Assert.DoesNotContain(ReviewTools.Definitions,
            d => PlannerCodeGraph.Tools.Contains(JsonSerializer.SerializeToElement(d).GetProperty("name").GetString()!));

        // Stored or live model output, embeddings, memory and the external hubs stay off every list.
        string[] never =
        [
            "get_architecture", "get_service_summary", "get_project_health", "get_fleet_health", "project_report", "search_conventions",
            "list_conventions", "get_convention", "rag_search", "ask", "codegraph_search", "memory_store", "store_memory_v2", "memory_search",
            "stories-search", "stories-create", "epics-search", "list_schemas", "get_schema_catalog", "list_queues", "list_projects",
            "request_overlay", "get_overlay_status",
        ];
        foreach (var tool in never)
        {
            Assert.DoesNotContain(tool, ReviewTools.Names);
            Assert.DoesNotContain(tool, McpServers.CodeGraphTools);
            Assert.DoesNotContain(tool, McpServers.KanbanTools.Except(["list_projects"])); // Kanban's own list_projects is a board read
            Assert.DoesNotContain(McpServers.ToolRule(McpServers.CodeGraph, tool), McpServers.AllToolRules);
            Assert.Throws<ArgumentException>(() => WorkerTools.ReadOnly.WithMcp([McpServers.ToolRule(McpServers.CodeGraph, tool)]));
        }
        Assert.DoesNotContain(McpServers.CodeGraphTools, t => t.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>sc-25874: a plan item's repository set, the planner's multi-repository seat and what its prompt says of it.</summary>
public class PlanRepositorySetTests
{
    private static readonly RepoRef Widgets = new("acme", "widgets");
    private static readonly RepoRef Gears = new("acme", "gears");
    private static readonly RepoRef Default = new("o", "default");
    private static readonly WorkItem Item = new() { Id = 5, Source = "shortcut", ExternalId = "sc-5", Title = "t", Repo = "acme/widgets" };

    [Fact]
    public void Every_repo_line_of_a_description_is_resolved_in_order_once_and_the_first_is_the_primary()
    {
        const string Description = "Span two engines.\n\nRepo: acme/widgets\n- Repo: `acme/gears`\nRepo: ACME/Widgets\nIt mentions Repo: in passing o/x.";

        Assert.Equal([Widgets, Gears], RepoResolver.ResolveAll(Description, Default));
        Assert.Equal(RepoResolver.Resolve(Description, Default), RepoResolver.ResolveAll(Description, Default)[0]);
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets", Default));
        Assert.Equal([Default], RepoResolver.ResolveAll("No line here.", Default));
        Assert.Equal([Default], RepoResolver.ResolveAll(null, Default));
        Assert.Throws<ArgumentException>(() => RepoResolver.ResolveAll("Repo: acme/widgets\nRepo: ../evil", Default));
    }

    [Fact]
    public void Repo_lines_inside_a_fenced_block_never_join_the_planners_repository_set()
    {
        var other = new RepoRef("c", "d");
        // A planner-created story: the code-written Repo: line, then planner text in a tilde fence (UntrustedText.Fenced).
        Assert.Equal([Widgets], RepoResolver.ResolveAll($"Repo: acme/widgets\n\n{UntrustedText.Fenced("Do it.\nRepo: c/d\n~~~\nRepo: c/d")}", Default));
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets\n```md\nRepo: c/d\n```", Default));
        // A fence is closed only by at least as many of the same marker, alone on its line.
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets\n~~~~\n~~~\nRepo: c/d\n~~~~", Default));
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets\n~~~\n```\nRepo: c/d\n~~~", Default));
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets\n~~~\n~~~ x\nRepo: c/d\n~~~", Default));
        Assert.Equal([Widgets, Gears], RepoResolver.ResolveAll("Repo: acme/widgets\n```\nRepo: c/d\n`````\nRepo: acme/gears", Default));
        // ... and what follows the closing line is read again; an indented or quoted fence is one too.
        Assert.Equal([Widgets, Gears], RepoResolver.ResolveAll("Repo: acme/widgets\n  > ~~~\nRepo: c/d\n~~~\r\nRepo: acme/gears", Default));
        // Never closed: everything after it is skipped.
        Assert.Equal([Widgets], RepoResolver.ResolveAll("Repo: acme/widgets\n~~~\nRepo: c/d\n\nRepo: acme/gears", Default));
        Assert.Equal([Default], RepoResolver.ResolveAll("```\nRepo: c/d", Default));
        // Fewer than three markers open nothing; Resolve is unchanged (it reads the first line anywhere).
        Assert.Equal([Widgets, other], RepoResolver.ResolveAll("Repo: acme/widgets\n~~\nRepo: c/d", Default));
        Assert.Equal(other, RepoResolver.Resolve("~~~\nRepo: c/d\n~~~", Default));
    }

    /// <summary>A real seat directory: each repository's "checkout" is a directory under it, as <see cref="GitWorkspace.PrepareSeatAsync"/> lays it out.</summary>
    private sealed class SeatWorkspaces(string root) : IRepoWorkspace, ISeatWorkspace
    {
        public List<string> Calls { get; } = [];
        public Action<string>? OnPrepared { get; set; }

        public Task<SeatWorkspace> PrepareSeatAsync(IReadOnlyList<RepoRef> repos, string branch, CancellationToken ct)
        {
            Calls.Add($"seat {string.Join(' ', repos)} {branch}");
            var seat = Path.Combine(root, branch.Replace('/', '-'));
            var checkouts = repos.Select(r => (r, new Workspace(Directory.CreateDirectory(Path.Combine(seat, r.Owner, r.Name)).FullName, branch, "main", "/admin"))).ToList();
            OnPrepared?.Invoke(seat);
            return Task.FromResult(new SeatWorkspace(seat, checkouts));
        }

        public Task RemoveSeatAsync(SeatWorkspace seat, CancellationToken ct)
        {
            Calls.Add($"remove-seat {Path.GetFileName(seat.Path)}");
            return Task.CompletedTask;
        }

        public Task<Workspace> PrepareAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CommitAndPushAsync(RepoRef repo, Workspace workspace, string message, PushGrant grant, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task PushAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<Workspace> RestoreAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<Workspace?> ReopenAsync(RepoRef repo, string branch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> HeadAsync(Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<BaseMerge> MergeBaseAsync(RepoRef repo, Workspace workspace, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ConflictMarkersAsync(RepoRef repo, string sha, IReadOnlyList<string> paths, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class Sessions : IMcpSessions
    {
        public List<(string Repos, McpProfile Profile)> Opened { get; } = [];

        public Task<IMcpSession> OpenAsync(RepoRef repo, McpProfile profile, CancellationToken ct) => OpenAsync([repo], profile, ct);

        public Task<IMcpSession> OpenAsync(IReadOnlyList<RepoRef> repos, McpProfile profile, CancellationToken ct)
        {
            Opened.Add((string.Join(' ', repos), profile));
            return Task.FromResult<IMcpSession>(new Session());
        }

        private sealed class Session : IMcpSession
        {
            public string ConfigPath => "/work/mcp-sessions/s.json";
            public IReadOnlyList<string> Servers => [McpServers.CodeGraph];
            public IReadOnlyList<string> ToolRules => ["mcp__codegraph__search_projects"];
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_plan_of_two_repositories_gets_one_seat_holding_a_checkout_of_each_under_its_one_read_rule()
    {
        var root = Directory.CreateTempSubdirectory("df-seat-").FullName;
        try
        {
            var outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "owner's file");
            var workspaces = new SeatWorkspaces(root)
            {
                OnPrepared = seat =>
                {
                    // The repositories' own symlinks: one inside its checkout, one out of the seat, one into the other repository's checkout.
                    File.WriteAllText(Path.Combine(seat, "acme", "widgets", "Cargo.toml"), "[package]");
                    File.WriteAllText(Path.Combine(seat, "acme", "gears", "Cargo.toml"), "[package]");
                    File.CreateSymbolicLink(Path.Combine(seat, "acme", "widgets", "inside"), "Cargo.toml");
                    File.CreateSymbolicLink(Path.Combine(seat, "acme", "widgets", "escape"), outside);
                    File.CreateSymbolicLink(Path.Combine(seat, "acme", "gears", "sibling"), Path.Combine(seat, "acme", "widgets", "Cargo.toml"));
                },
            };
            var sessions = new Sessions();
            var log = new StringWriter();
            var worker = new FakeWorker { Tools = WorkerTools.ReadOnly.WithMcp(["mcp__codegraph__search_projects"]) };

            var seat = await new WorkerPlanRunner(workspaces, worker, log, sessions).RunAsync(Item, [Widgets, Gears], (s, _) =>
            {
                Assert.DoesNotContain(workspaces.Calls, c => c.StartsWith("remove"));
                return Task.FromResult(s);
            }, CancellationToken.None);

            Assert.Equal(["seat acme/widgets acme/gears factory/plan-sc-5", "remove-seat factory-plan-sc-5"], workspaces.Calls);
            Assert.Equal(Path.Combine(root, "factory-plan-sc-5"), seat.Path);
            Assert.Equal([new PlanCheckout(Widgets, "acme/widgets"), new PlanCheckout(Gears, "acme/gears")], seat.Checkouts);
            // One Read rule, the seat's directory: every checkout is inside it.
            var rules = seat.Worker.Tools.AllowedIn(seat.Path);
            Assert.Equal(WorkerTools.ReadRule(seat.Path), Assert.Single(rules, r => r.StartsWith("Read(", StringComparison.Ordinal)));
            Assert.Equal(["mcp__codegraph__search_projects"], rules.Where(r => !r.StartsWith("Read(", StringComparison.Ordinal)));
            Assert.All(seat.Checkouts!, c => Assert.StartsWith(seat.Path + Path.DirectorySeparatorChar, Path.GetFullPath(Path.Combine(seat.Path, c.Directory))));
            // Each checkout's symlinks that resolve outside it are gone, the other repository's checkout counting as outside.
            Assert.True(File.Exists(Path.Combine(seat.Path, "acme", "widgets", "inside")));
            Assert.Null(new FileInfo(Path.Combine(seat.Path, "acme", "widgets", "escape")).LinkTarget);
            Assert.False(File.Exists(Path.Combine(seat.Path, "acme", "widgets", "escape")));
            Assert.False(File.Exists(Path.Combine(seat.Path, "acme", "gears", "sibling")));
            Assert.Contains("removed symlink acme/widgets/escape", log.ToString());
            Assert.Contains("removed symlink acme/gears/sibling", log.ToString());
            Assert.Contains("outside acme/gears's checkout", log.ToString());
            Assert.Equal("owner's file", File.ReadAllText(outside));
            // One proxy session over the whole set, the planner's profile.
            Assert.Equal(("acme/widgets acme/gears", McpProfile.Planner), Assert.Single(sessions.Opened));

            // Left under a worker that would not stop; removed when the session fails.
            workspaces.Calls.Clear();
            workspaces.OnPrepared = null;
            await Assert.ThrowsAsync<WorkerFailedException>(() => new WorkerPlanRunner(workspaces, worker, TextWriter.Null).RunAsync<int>(Item, [Widgets, Gears],
                (_, _) => throw WorkerStillRunning.Mark(new WorkerFailedException("still running")), CancellationToken.None));
            Assert.DoesNotContain(workspaces.Calls, c => c.StartsWith("remove"));
            await Assert.ThrowsAsync<WorkerFailedException>(() => new WorkerPlanRunner(workspaces, worker, TextWriter.Null).RunAsync<int>(Item, [Widgets, Gears],
                (_, _) => throw new WorkerFailedException("boom"), CancellationToken.None));
            Assert.Contains("remove-seat factory-plan-sc-5", workspaces.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Workspaces_that_hold_one_checkout_per_seat_refuse_a_plan_of_several_repositories()
    {
        var workspaces = new FakeWorkspaces();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkerPlanRunner(workspaces, new FakeWorker { Tools = WorkerTools.ReadOnly },
            TextWriter.Null).RunAsync(Item, [Widgets, Gears], (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.Contains("plans 2 repositories", ex.Message);
        Assert.Empty(workspaces.Calls);
    }

    [Fact]
    public void The_prompt_of_a_plan_of_several_repositories_says_which_directory_is_which_and_how_to_name_one()
    {
        var story = new WorkStory(12, "Share the mesh format", "Repo: acme/widgets\nRepo: acme/gears", "feature", "u", Labels: ["plan"]);
        var spec = new WorkSpec(story, null, []);
        PlanCheckout[] checkouts = [new(Widgets, "acme/widgets"), new(Gears, "acme/gears")];

        var prompt = PlanPrompt.Build(spec, Widgets, checkouts);
        var one = PlanPrompt.Build(spec, Widgets);

        Assert.Contains("- acme/widgets/ is acme/widgets (the plan's primary repository)\n- acme/gears/ is acme/gears\n", prompt);
        Assert.Contains("you cannot edit files or run commands", prompt);
        Assert.Contains("Every story is\nworked in acme/widgets", prompt);
        Assert.DoesNotContain("in this\nrepository only", prompt);
        // One repository: the prompt is the one before sc-25874, whether no checkouts or an empty list is given.
        Assert.Contains("The current directory is a checkout of acme/widgets's default branch. You can only read and\nsearch it (Read, Glob, Grep)", one);
        Assert.Contains("merge on its own, in this\nrepository only. Slice by mechanism", one);
        Assert.Equal(one, PlanPrompt.Build(spec, Widgets, []));

        string[] rules = ["mcp__codegraph__search_graph", "mcp__codegraph__search_projects", "mcp__codegraph__get_service_clusters"];
        var note = PlanPrompt.McpNote(rules, [Widgets, Gears]);
        Assert.Contains("a required \"repo\" argument naming it: one of acme/widgets, acme/gears", note);
        Assert.Contains("search_projects, get_service_clusters are not about one repository", note);
        var single = PlanPrompt.McpNote(["mcp__codegraph__search_graph"], [Widgets]);
        Assert.Equal(PlanPrompt.McpNote(["mcp__codegraph__search_graph"]), single);
        Assert.Contains("the code graph of this repository", single);
        Assert.DoesNotContain("\"repo\"", single);
        Assert.DoesNotContain("not about one repository", single);
    }
}
