using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.CodeGraph;
using DarkFactory.Orchestrator.Gate;

namespace DarkFactory.Orchestrator.Mcp;

/// <summary>
/// The planner's cross-repository CodeGraph tools (sc-25874, E2): what only <see cref="McpProfile.Planner"/> is granted beyond the
/// reviewers' seven (<see cref="ReviewTools.CodeGraphTools"/>), their input schemas as the loopback proxy lists them, and the
/// arguments the proxy rebuilds for CodeGraph (unknown ones dropped, strings cut, numbers clamped, as
/// <see cref="ReviewTools.CodeGraphArguments"/> does for the seven).
/// </summary>
public static class PlannerCodeGraph
{
    public const string SearchProjects = "search_projects";
    public const string TraceDataLineage = "trace_data_lineage";
    public const string GetServiceClusters = "get_service_clusters";
    public const string GetClusterDetail = "get_cluster_detail";
    public const string AnalyzeImpactFile = CodeGraphMcpClient.AnalyzeImpactFileTool;
    public const string FileHotspot = CodeGraphMcpClient.FileHotspotTool;

    /// <summary>
    /// The CodeGraph tools that expose links between repositories, for the planner only: the one agent that must see every repository
    /// a plan may touch and how they interact. Each was checked against CodeGraph's source (michaeltrefry/CodeGraph main at f95d231,
    /// <c>src/CodeGraph.Services/Assistant/CodeGraphMcpServer.cs</c>) to reach no LLM and no embedding model at call time and to
    /// return no stored model text: its handler reads only the graph store (<c>IGraphStore</c>), never
    /// <c>IAnalysisModelProvider</c>, <c>GraphAssistant</c>, <c>IConventionEmbeddingService</c>, the wiki store or a model client.
    /// <list type="bullet">
    /// <item><c>search_projects</c>: <c>store.SearchRepositoriesAsync</c> and <c>GetSyncStatesAsync</c> — each indexed repository's
    /// name, language/framework flags, index time, repo URL and default-branch index commit (rows indexing wrote).</item>
    /// <item><c>analyze_impact_file</c>: <c>ImpactAnalysisService.AnalyzeFileImpactAsync</c> (constructed with <c>IGraphStore</c> and a
    /// logger only), the union of the inbound dependents of a file's nodes, as counts and a blast radius.</item>
    /// <item><c>trace_data_lineage</c>: <c>GraphQueryEngine.TraceDataLineageAsync</c> (<c>IGraphStore</c> + logger): node lookup, two
    /// graph traversals over data edges, and <c>FindCrossRepoEdgesAsync</c> (the <c>cross_repo_edges</c> rows of the model's project).</item>
    /// <item><c>get_service_clusters</c>, <c>get_cluster_detail</c>: <c>CommunityDetectionService</c> (<c>IGraphStore</c> + logger)
    /// reading the stored <c>repo_clusters</c> rows and the cross-repo edges. The clusters are computed by Louvain community
    /// detection and betweenness centrality over those edges (arithmetic, no model), and a cluster's label is never written by any
    /// code on main (<c>DetectCommunitiesAsync</c> stores none), so the answer shows "Cluster N": no stored model text. That holds only while nothing writes
    /// <c>repo_clusters.cluster_label</c> (the handlers print <c>cluster.Label ?? "Cluster N"</c>): checked at CodeGraph f95d231, and to
    /// be re-checked — these two tools dropped if the label becomes model text — when CodeGraph gains a cluster labeller.</item>
    /// <item><c>file_hotspot</c>: <c>store.GetFileMetricAsync</c> — one file's stored vitals (risk, concern, health, churn,
    /// complexity: numbers computed from git history and the parse) and the commit they describe.</item>
    /// </list>
    /// None was dropped. Still off every list (a test pins it): <c>get_architecture</c>, <c>get_service_summary</c>,
    /// <c>get_project_health</c>, <c>project_report</c> (stored or live model output), <c>search_conventions</c> and
    /// <c>rag_search</c> (the embedding service), every memory tool, and the Shortcut/RabbitMQ/MySQL hub tools (external systems).
    /// <para>
    /// <see cref="Global"/> tools are not about one project: their answers name every project CodeGraph indexes, private ones outside
    /// the plan's repositories included. Accepted for the planner (owner's rule, Phase 3: its input is the owner's own plan story and
    /// its output goes only to the owner's board); never for triage, which reads untrusted issue text and posts on the issue.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Tools =
        [SearchProjects, AnalyzeImpactFile, TraceDataLineage, GetServiceClusters, GetClusterDetail, FileHotspot];

    /// <summary>Whether <paramref name="tool"/> answers about every indexed project rather than one (no project is set or pinned).</summary>
    public static bool Global(string tool) => tool is SearchProjects or GetServiceClusters or GetClusterDetail;

    /// <summary>
    /// Whether CodeGraph takes the pinned project of <paramref name="tool"/> in an argument named <c>repo</c> (the per-file tools)
    /// rather than <c>project</c>.
    /// </summary>
    public static bool ProjectInRepoArgument(string tool) => tool is AnalyzeImpactFile or FileHotspot;

    /// <summary>The label of a <see cref="Global"/> tool's relayed answer (no single project or commit to name).</summary>
    public static string GlobalLabel(string tool) => $"CodeGraph's {tool}, across every project it indexes (default branches)";

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }
        return schema;
    }

    /// <summary>The input schema the proxy lists for <paramref name="tool"/> (the arguments <see cref="Arguments"/> keeps), or null for a tool not of <see cref="Tools"/>.</summary>
    public static JsonObject? InputSchema(string tool) => tool switch
    {
        SearchProjects => Schema(new JsonObject
        {
            ["search"] = Str("Optional partial repository name (* and % are wildcards); omit to list every project."),
            ["group"] = Str("Optional source group."),
            ["page"] = Int($"Page, 1 to {MaxPage} (default 1)."),
            ["pageSize"] = Int("Page size, 1 to 100 (default 25)."),
        }),
        TraceDataLineage => Schema(new JsonObject { ["modelName"] = Str("Model, DTO or event class name to trace.") }, "modelName"),
        GetServiceClusters => Schema(new JsonObject()),
        GetClusterDetail => Schema(new JsonObject { ["clusterId"] = Int("A cluster id from get_service_clusters.") }, "clusterId"),
        AnalyzeImpactFile => Schema(new JsonObject
        {
            ["path"] = Str("The file's path from the repository root, e.g. src/Orders/OrderService.cs."),
            ["depth"] = Int("Max traversal depth, 1 to 5 (default 3)."),
        }, "path"),
        FileHotspot => Schema(new JsonObject { ["path"] = Str("The file's path from the repository root.") }, "path"),
        _ => null,
    };

    /// <summary>The longest normalised <c>path</c> a per-file tool is sent; a longer one is a usage error (never cut: a cut path names another file).</summary>
    public const int MaxPathChars = 1000;

    /// <summary>The largest <c>search_projects</c> page asked for.</summary>
    public const int MaxPage = 1000;

    /// <summary>
    /// The arguments CodeGraph is sent for <paramref name="tool"/> (without the project, which only the proxy sets, and never a
    /// <c>sha</c>: the default branch), or null when a required one is missing or malformed. Anything not named here is dropped.
    /// </summary>
    public static JsonObject? Arguments(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? Text(string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s
                ? (s.Length > ReviewTools.MaxCodeGraphArgumentChars ? s[..ReviewTools.MaxCodeGraphArgumentChars] : s)
                : null;
        int? Number(string name, int min, int max) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : null;
        string? File() => input.TryGetProperty("path", out var v) && v.ValueKind == JsonValueKind.String
            && RepoPath.Normalize(v.GetString()!) is { Length: <= MaxPathChars } path ? path : null;
        JsonObject With(JsonObject o, string name, JsonNode? value)
        {
            if (value is not null)
            {
                o[name] = value;
            }
            return o;
        }
        switch (tool)
        {
            case SearchProjects:
                return With(With(With(With(new JsonObject(), "search", Text("search")), "group", Text("group")),
                    "page", Number("page", 1, MaxPage)), "pageSize", Number("pageSize", 1, 100));
            case TraceDataLineage:
                return Text("modelName") is { } model ? new JsonObject { ["modelName"] = model } : null;
            case GetServiceClusters:
                return new JsonObject();
            case GetClusterDetail:
                return input.TryGetProperty("clusterId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var cluster)
                    ? new JsonObject { ["clusterId"] = cluster }
                    : null;
            case AnalyzeImpactFile:
                return File() is { } file ? With(new JsonObject { ["path"] = file }, "depth", Number("depth", 1, 5)) : null;
            case FileHotspot:
                return File() is { } hot ? new JsonObject { ["path"] = hot } : null;
            default:
                return null;
        }
    }

    public static string Usage(string tool) => tool switch
    {
        TraceDataLineage => "trace_data_lineage needs a 'modelName'.",
        GetClusterDetail => "get_cluster_detail needs a numeric 'clusterId' (from get_service_clusters).",
        AnalyzeImpactFile or FileHotspot => $"{tool} needs a 'path' relative to the repository root, inside it.",
        _ => $"{tool} takes a JSON object of arguments.",
    };

    /// <summary>
    /// <paramref name="schema"/> with the required <c>repo</c> argument a session granted several repositories names the one it asks
    /// about with: one of <paramref name="repos"/>' <c>owner/name</c> values, nothing else.
    /// </summary>
    public static JsonObject WithRepo(JsonObject schema, IReadOnlyList<RepoRef> repos)
    {
        var copy = (JsonObject)schema.DeepClone();
        var properties = copy["properties"] as JsonObject ?? new JsonObject();
        properties.Remove(RepoArgument);
        properties[RepoArgument] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(repos.Select(r => (JsonNode)r.FullName).ToArray()),
            ["description"] = "Which of this plan's repositories to ask about (owner/name).",
        };
        if (properties.Parent is null)
        {
            copy["properties"] = properties;
        }
        var required = copy["required"] as JsonArray ?? [];
        if (!required.Any(r => r is JsonValue v && v.TryGetValue<string>(out var name) && name == RepoArgument))
        {
            required.Add(RepoArgument);
        }
        if (required.Parent is null)
        {
            copy["required"] = required;
        }
        return copy;
    }

    /// <summary>The argument a session granted several repositories names one with: its <c>owner/name</c>.</summary>
    public const string RepoArgument = "repo";
}
