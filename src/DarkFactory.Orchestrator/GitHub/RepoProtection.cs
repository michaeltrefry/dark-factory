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
/// updated or deleted only by repository admins — so the App can push only factory/*. With the merge gate's App id
/// (<c>factory github-app setup --gate</c>) that App may also bypass the second rule, but only by merging a pull request
/// (<c>bypass_mode: pull_request</c>): the gate merges; it can never push, and the workers' App can do neither.
/// </summary>
public sealed class RepoProtection(HttpClient http, string adminToken, TextWriter log, long? gateAppId = null)
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

    /// <summary>
    /// Every parameter GitHub's pull_request rule accepts, always sent: one left out takes GitHub's default, and
    /// <c>require_extra_approval_for_unattributed_changes</c> defaulted to <c>true</c>, which blocked every PR of unsigned
    /// commits (the workers') although no approval is required. The read-back in <see cref="ApplyAsync"/> fails on any
    /// parameter GitHub stores that is not listed here.
    /// </summary>
    public sealed record PullRequestParameters(
        [property: JsonPropertyName("required_approving_review_count")] int RequiredApprovingReviewCount,
        [property: JsonPropertyName("dismiss_stale_reviews_on_push")] bool DismissStaleReviewsOnPush,
        [property: JsonPropertyName("require_code_owner_review")] bool RequireCodeOwnerReview,
        [property: JsonPropertyName("require_last_push_approval")] bool RequireLastPushApproval,
        [property: JsonPropertyName("required_review_thread_resolution")] bool RequiredReviewThreadResolution,
        [property: JsonPropertyName("require_extra_approval_for_unattributed_changes")] bool RequireExtraApprovalForUnattributedChanges,
        [property: JsonPropertyName("required_reviewers")] IReadOnlyList<JsonElement> RequiredReviewers,
        [property: JsonPropertyName("allowed_merge_methods")] IReadOnlyList<string> AllowedMergeMethods);

    /// <summary>
    /// The main ruleset's PR rule: no approvals and no review conditions (the merge gate is the review, and a PR's
    /// author cannot approve it), no extra approval for unattributed (unsigned) commits, no required reviewers, and
    /// every merge method GitHub offers (the gate merges with <c>merge</c>; the owner may use any).
    /// </summary>
    public static readonly PullRequestParameters MainPullRequest = new(
        RequiredApprovingReviewCount: 0,
        DismissStaleReviewsOnPush: false,
        RequireCodeOwnerReview: false,
        RequireLastPushApproval: false,
        RequiredReviewThreadResolution: false,
        RequireExtraApprovalForUnattributedChanges: false,
        RequiredReviewers: [],
        AllowedMergeMethods: ["merge", "squash", "rebase"]);

    public sealed record BypassActor(
        [property: JsonPropertyName("actor_id")] long ActorId,
        [property: JsonPropertyName("actor_type")] string ActorType,
        [property: JsonPropertyName("bypass_mode")] string BypassMode);

    private static readonly BypassActor[] AdminsOnly = [new(RepositoryAdminRoleId, "RepositoryRole", "always")];

    private static readonly Rule[] NoWrites = [new("creation"), new("update"), new("deletion")];

    /// <summary>The merge gate's App, bypassing the factory/** rule only to merge a pull request.</summary>
    public static BypassActor GateMerges(long gateAppId) => new(gateAppId, "Integration", "pull_request");

    public static IReadOnlyList<Ruleset> DesiredRulesets(long? gateAppId = null) =>
    [
        // No bypass: even the owner lands changes on main through a PR (approvals: 0, so they can merge).
        new(MainRulesetName, "branch", "active",
            new Conditions(new RefName(["~DEFAULT_BRANCH"], [])),
            [
                new("pull_request", MainPullRequest),
                new("non_fast_forward"),
                new("deletion"),
            ],
            []),
        new(BranchesRulesetName, "branch", "active",
            new Conditions(new RefName(["~ALL"], [$"refs/heads/{Git.GitWorkspace.BranchPrefix}**"])),
            NoWrites,
            gateAppId is { } gate ? [.. AdminsOnly, GateMerges(gate)] : AdminsOnly),
        // contents:write would otherwise let the App push tags.
        new(TagsRulesetName, "tag", "active",
            new Conditions(new RefName(["~ALL"], [])),
            NoWrites,
            AdminsOnly),
    ];

    public static string Serialize(Ruleset ruleset) => JsonSerializer.Serialize(ruleset, Json);

    /// <summary>
    /// Creates each missing ruleset and overwrites any existing one with the same name, then reads each back and
    /// throws (naming every difference) unless GitHub holds exactly what was sent. Of several existing rulesets with one
    /// managed name only the first is written; the others are left as they are and named in a warning, since they still apply.
    /// </summary>
    public async Task ApplyAsync(RepoRef repo, CancellationToken ct)
    {
        var path = $"repos/{repo.Owner}/{repo.Name}/rulesets";
        using var list = GitHubApp.Request(HttpMethod.Get, $"{path}?includes_parents=false&per_page=100", "Bearer", adminToken);
        using var listResponse = await http.SendAsync(list, ct);
        await EnsureSuccess(listResponse, repo, "list rulesets", ct);
        // GitHub does not enforce unique ruleset names; overwrite the first of any duplicates.
        var byName = (await listResponse.Content.ReadFromJsonAsync<ExistingRuleset[]>(ct))!
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Id).ToList());
        var existing = byName.ToDictionary(e => e.Key, e => e.Value[0]);

        foreach (var ruleset in DesiredRulesets(gateAppId))
        {
            var update = existing.TryGetValue(ruleset.Name, out var id);
            using var request = GitHubApp.Request(update ? HttpMethod.Put : HttpMethod.Post,
                update ? $"{path}/{id}" : path, "Bearer", adminToken);
            request.Content = new StringContent(Serialize(ruleset), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            await EnsureSuccess(response, repo, $"{(update ? "update" : "create")} ruleset '{ruleset.Name}'", ct);
            if (!update)
            {
                id = await response.Content.ReadFromJsonAsync<ExistingRuleset>(ct) is { Id: > 0 } created
                    ? created.Id
                    : throw new InvalidOperationException($"GitHub created ruleset '{ruleset.Name}' on {repo} but returned no id.");
            }
            log.WriteLine($"{(update ? "updated" : "created")} ruleset '{ruleset.Name}' on {repo}");

            using var read = GitHubApp.Request(HttpMethod.Get, $"{path}/{id}", "Bearer", adminToken);
            using var readResponse = await http.SendAsync(read, ct);
            await EnsureSuccess(readResponse, repo, $"read back ruleset '{ruleset.Name}'", ct);
            using var stored = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync(ct));
            var differences = Differences(ruleset, stored.RootElement);
            if (differences.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Ruleset '{ruleset.Name}' (id {id}) on {repo} does not hold what was sent:{Environment.NewLine}  " +
                    string.Join($"{Environment.NewLine}  ", differences));
            }
            log.WriteLine($"verified ruleset '{ruleset.Name}' on {repo}");
            if (byName.TryGetValue(ruleset.Name, out var ids) && ids.Count > 1)
            {
                log.WriteLine($"warning: {repo} also has ruleset id(s) {string.Join(", ", ids.Skip(1))} named '{ruleset.Name}', "
                    + $"left unchanged and unverified; they still apply. Delete them in the repository's rulesets settings if stale.");
            }
        }
    }

    private static readonly string[] VerifiedFields = ["name", "target", "enforcement", "conditions", "bypass_actors"];

    /// <summary>
    /// Every way a stored ruleset differs from the one sent: the top-level fields, the rule types, and each rule's
    /// parameters, including one GitHub stores that was not sent (a GitHub default). Arrays compare as sets.
    /// </summary>
    public static IReadOnlyList<string> Differences(Ruleset sent, JsonElement stored)
    {
        var expected = JsonDocument.Parse(Serialize(sent)).RootElement;
        var differences = new List<string>();
        foreach (var field in VerifiedFields)
        {
            Compare(field, expected.GetProperty(field), Field(stored, field), differences);
        }

        var storedRules = Field(stored, "rules") is { ValueKind: JsonValueKind.Array } rules ? rules.EnumerateArray().ToList() : [];
        var sentRules = expected.GetProperty("rules").EnumerateArray().ToList();
        Compare("rule types", Types(sentRules), Types(storedRules), differences);
        foreach (var rule in sentRules)
        {
            var type = rule.GetProperty("type").GetString()!;
            var match = storedRules.FirstOrDefault(r => Field(r, "type") is { ValueKind: JsonValueKind.String } t && t.GetString() == type);
            if (match.ValueKind != JsonValueKind.Object)
            {
                continue; // already reported under "rule types"
            }
            var sentParameters = Field(rule, "parameters");
            var storedParameters = Field(match, "parameters");
            foreach (var name in Names(sentParameters).Union(Names(storedParameters)).Order(StringComparer.Ordinal))
            {
                Compare($"{type}.{name}", Field(sentParameters, name), Field(storedParameters, name), differences);
            }
        }
        return differences;

        static JsonElement Types(List<JsonElement> rules) => JsonSerializer.SerializeToElement(
            rules.Select(r => Field(r, "type") is { ValueKind: JsonValueKind.String } t ? t.GetString() : null));

        static IEnumerable<string> Names(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().Select(p => p.Name) : [];
    }

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static void Compare(string what, JsonElement sent, JsonElement stored, List<string> differences)
    {
        if (Canonical(sent) != Canonical(stored))
        {
            differences.Add($"{what}: sent {Canonical(sent)}, GitHub holds {Canonical(stored)}");
        }
    }

    /// <summary>Order-insensitive JSON text (object keys and array elements sorted); a missing value is <c>(absent)</c>.</summary>
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined => "(absent)",
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{JsonSerializer.Serialize(p.Name)}:{Canonical(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical).Order(StringComparer.Ordinal)) + "]",
        _ => element.GetRawText(),
    };

    /// <summary>
    /// Resolves the owner's admin token: GH_TOKEN, then GITHUB_TOKEN, then <c>gh auth token</c>;
    /// an empty or whitespace value counts as unset.
    /// </summary>
    public static string? ResolveAdminToken(Func<string, string?> env, Func<string?> ghAuthToken) =>
        NonEmpty(env("GH_TOKEN")) ?? NonEmpty(env("GITHUB_TOKEN")) ?? NonEmpty(ghAuthToken());

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>`github-repo protect`: 0 on success, 1 when GitHub refuses or is unreachable (message only, never the token).</summary>
    public static async Task<int> RunAsync(HttpClient http, string adminToken, RepoRef repo, TextWriter output, TextWriter error, CancellationToken ct,
        long? gateAppId = null)
    {
        try
        {
            if (gateAppId is null)
            {
                output.WriteLine("No merge gate App (`factory github-app setup --gate`): the rulesets let nothing merge pull requests but admins.");
            }
            await new RepoProtection(http, adminToken, output, gateAppId).ApplyAsync(repo, ct);
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
