using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.Shortcut;

namespace DarkFactory.Orchestrator.GitHub;

/// <summary>
/// Applies the factory's repository rulesets (E4) idempotently, authenticated as a
/// repository admin (the owner's token, not the App): the default branch only changes
/// through a pull request, and every ref outside <c>factory/**</c> can be created,
/// updated or deleted only by repository admins — so the App can push only factory/*.
/// </summary>
public sealed class RepoProtection(HttpClient http, string adminToken, TextWriter log)
{
    /// <summary>Built-in "Repository admin" role id for ruleset bypass actors.</summary>
    public const int RepositoryAdminRoleId = 5;

    public const string MainRulesetName = "dark-factory: main requires a pull request";
    public const string BranchesRulesetName = "dark-factory: only admins write outside factory/**";
    public const string TagsRulesetName = "dark-factory: only admins write tags";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record Ruleset(
        string Name,
        string Target,
        string Enforcement,
        Conditions Conditions,
        IReadOnlyList<Rule> Rules,
        [property: JsonPropertyName("bypass_actors")] IReadOnlyList<BypassActor> BypassActors);

    public sealed record Conditions([property: JsonPropertyName("ref_name")] RefName RefName);

    public sealed record RefName(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

    public sealed record Rule(string Type, PullRequestParameters? Parameters = null);

    public sealed record PullRequestParameters(
        [property: JsonPropertyName("required_approving_review_count")] int RequiredApprovingReviewCount,
        [property: JsonPropertyName("dismiss_stale_reviews_on_push")] bool DismissStaleReviewsOnPush,
        [property: JsonPropertyName("require_code_owner_review")] bool RequireCodeOwnerReview,
        [property: JsonPropertyName("require_last_push_approval")] bool RequireLastPushApproval,
        [property: JsonPropertyName("required_review_thread_resolution")] bool RequiredReviewThreadResolution);

    public sealed record BypassActor(
        [property: JsonPropertyName("actor_id")] int ActorId,
        [property: JsonPropertyName("actor_type")] string ActorType,
        [property: JsonPropertyName("bypass_mode")] string BypassMode);

    private static readonly BypassActor[] AdminsOnly = [new(RepositoryAdminRoleId, "RepositoryRole", "always")];

    private static readonly Rule[] NoWrites = [new("creation"), new("update"), new("deletion")];

    public static IReadOnlyList<Ruleset> DesiredRulesets() =>
    [
        // No bypass: even the owner lands changes on main through a PR (approvals: 0, so they can merge).
        new(MainRulesetName, "branch", "active",
            new Conditions(new RefName(["~DEFAULT_BRANCH"], [])),
            [
                new("pull_request", new PullRequestParameters(0, false, false, false, false)),
                new("non_fast_forward"),
                new("deletion"),
            ],
            []),
        new(BranchesRulesetName, "branch", "active",
            new Conditions(new RefName(["~ALL"], [$"refs/heads/{Git.GitWorkspace.BranchPrefix}**"])),
            NoWrites,
            AdminsOnly),
        // contents:write would otherwise let the App push tags.
        new(TagsRulesetName, "tag", "active",
            new Conditions(new RefName(["~ALL"], [])),
            NoWrites,
            AdminsOnly),
    ];

    public static string Serialize(Ruleset ruleset) => JsonSerializer.Serialize(ruleset, Json);

    /// <summary>Creates each missing ruleset and overwrites any existing one with the same name.</summary>
    public async Task ApplyAsync(RepoRef repo, CancellationToken ct)
    {
        var path = $"repos/{repo.Owner}/{repo.Name}/rulesets";
        using var list = GitHubApp.Request(HttpMethod.Get, $"{path}?includes_parents=false&per_page=100", "Bearer", adminToken);
        using var listResponse = await http.SendAsync(list, ct);
        await EnsureSuccess(listResponse, repo, "list rulesets", ct);
        // GitHub does not enforce unique ruleset names; overwrite the first of any duplicates.
        var existing = (await listResponse.Content.ReadFromJsonAsync<ExistingRuleset[]>(ct))!
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var ruleset in DesiredRulesets())
        {
            var update = existing.TryGetValue(ruleset.Name, out var id);
            using var request = GitHubApp.Request(update ? HttpMethod.Put : HttpMethod.Post,
                update ? $"{path}/{id}" : path, "Bearer", adminToken);
            request.Content = new StringContent(Serialize(ruleset), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            await EnsureSuccess(response, repo, $"{(update ? "update" : "create")} ruleset '{ruleset.Name}'", ct);
            log.WriteLine($"{(update ? "updated" : "created")} ruleset '{ruleset.Name}' on {repo}");
        }
    }

    /// <summary>
    /// Resolves the owner's admin token: GH_TOKEN, then GITHUB_TOKEN, then <c>gh auth token</c>;
    /// an empty or whitespace value counts as unset.
    /// </summary>
    public static string? ResolveAdminToken(Func<string, string?> env, Func<string?> ghAuthToken) =>
        NonEmpty(env("GH_TOKEN")) ?? NonEmpty(env("GITHUB_TOKEN")) ?? NonEmpty(ghAuthToken());

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>`github-repo protect`: 0 on success, 1 when GitHub refuses or is unreachable (message only, never the token).</summary>
    public static async Task<int> RunAsync(HttpClient http, string adminToken, RepoRef repo, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            await new RepoProtection(http, adminToken, output).ApplyAsync(repo, ct);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, RepoRef repo, string action, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"GitHub refused to {action} on {repo} (403): {body}. Rulesets on a private repository owned by a " +
                "free personal account need GitHub Pro (or a public repo / a Team organization); the token must also have admin on the repo.");
        }
        await GitHubApp.EnsureSuccess(response, action, ct);
    }

    private sealed record ExistingRuleset(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name);
}
