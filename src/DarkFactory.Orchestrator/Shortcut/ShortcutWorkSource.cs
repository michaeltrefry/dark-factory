using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DarkFactory.Orchestrator.WorkSources;
using Microsoft.Extensions.Configuration;

namespace DarkFactory.Orchestrator.Shortcut;

/// <summary>
/// The Shortcut teams and epics the factory watches. A story is in scope if it is in any listed
/// team or epic. An empty scope watches nothing (fail safe), never the whole workspace.
/// </summary>
public sealed record WatchScope(IReadOnlyList<string> Teams, IReadOnlyList<int> Epics)
{
    public static readonly WatchScope None = new([], []);

    public bool IsEmpty => Teams.Count == 0 && Epics.Count == 0;

    /// <summary>
    /// Reads <c>Shortcut:Watch:Teams</c> (mention names or ids) and <c>Shortcut:Watch:Epics</c> (ids),
    /// each a comma-separated value or an array section (<c>Shortcut__Watch__Teams__0</c>).
    /// </summary>
    public static WatchScope From(IConfiguration config)
    {
        var teams = Values(config, "Shortcut:Watch:Teams").Select(t => t.TrimStart('@')).ToList();
        var epics = Values(config, "Shortcut:Watch:Epics")
            .Select(e => int.TryParse(e, out var id) && id > 0
                ? id
                : throw new InvalidOperationException($"Shortcut:Watch:Epics: '{e}' is not an epic id."))
            .ToList();
        return new WatchScope(teams, epics);
    }

    private static IEnumerable<string> Values(IConfiguration config, string key)
    {
        var section = config.GetSection(key);
        var raw = section.Value is { } single ? single.Split(',') : section.GetChildren().Select(c => c.Value ?? "");
        return raw.Select(v => v.Trim()).Where(v => v.Length > 0);
    }
}

/// <summary>
/// <see cref="IWorkSource"/> over the Shortcut REST API v3 with the orchestrator's own token.
/// Ready = a <c>To Do</c> story inside the <see cref="WatchScope"/>. Workflow state ids are resolved
/// by name from the API, never hardcoded. The factory user is the token's member.
/// Stories are listed per watched team (<c>GET groups/{id}/stories</c>, documented limit/offset paging)
/// and per watched epic (<c>GET epics/{id}/stories</c>, documented to return all of them); the
/// <c>POST stories/search</c> endpoint documents no paging or cap, so it is not used. A 429 is retried
/// after its Retry-After; a 502/503/504 on a call that is safe to repeat (GET, PUT) is retried with backoff.
/// </summary>
public sealed class ShortcutWorkSource(HttpClient http, string apiToken, WatchScope scope, TimeProvider? time = null) : IWorkSource
{
    public static readonly Uri DefaultBaseAddress = Gateway.OutboundHttp.ShortcutApiBase;

    public const string ClaimLabel = "factory-claimed";
    public const string ReadyState = "To Do";

    /// <summary>Calls per request before an error is thrown (the first try plus retries).</summary>
    public const int MaxAttempts = 4;

    /// <summary>The longest a Retry-After is honoured; a longer one waits this long.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(120);

    /// <summary>Team listing page size (the API's maximum, 1000).</summary>
    public int PageSize { get; init; } = 1000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private static string BoardStateName(BoardState state) => state switch
    {
        BoardState.Claimed => "In Progress",
        BoardState.Merged => "Done",
        BoardState.Stopped => "Backlog",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private string? _me;
    private List<Workflow>? _workflows;
    private List<Group>? _groups;

    public async Task<IReadOnlyList<int>> ListReadyAsync(CancellationToken ct)
    {
        if (scope.IsEmpty)
        {
            return [];
        }
        var me = await MeAsync(ct);
        var ready = await ReadyStateIdsAsync(ct);
        var teamIds = await TeamIdsAsync(ct);

        var found = new Dictionary<int, Story>();
        foreach (var team in teamIds)
        {
            foreach (var story in await TeamStoriesAsync(team, ct))
            {
                found.TryAdd(story.Id, story);
            }
        }
        foreach (var epic in scope.Epics)
        {
            foreach (var story in await SendAsync<List<Story>>(HttpMethod.Get, $"epics/{epic}/stories", null, ct))
            {
                found.TryAdd(story.Id, story);
            }
        }

        // The listings return every story of the team or epic: keep the ready ones, and re-check the
        // scope so nothing outside it can slip through.
        return found.Values
            .Where(s => !s.Archived && ready.Contains(s.WorkflowStateId))
            .Where(s => InScope(s, teamIds))
            .Where(s => !HeldByAnother(s, me))
            .Select(s => s.Id)
            .Order()
            .ToList();
    }

    public async Task<ClaimResult> ClaimAsync(int id, bool ignoreScope, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var story = await GetStoryAsync(id, ct);
        if (await ClaimRefusalAsync(story, me, ignoreScope, ct) is { } refusal)
        {
            return ClaimResult.Refused(refusal);
        }
        if (ClaimedBy(story, me))
        {
            return ClaimResult.Ok;
        }
        await UpdateAsync(id, new
        {
            owner_ids = story.OwnerIds.Append(me).Distinct(),
            labels = LabelParams(story.LabelNames.Append(ClaimLabel)),
        }, ct);
        var after = await GetStoryAsync(id, ct);
        return ClaimedBy(after, me)
            ? ClaimResult.Ok
            : ClaimResult.Refused(
                $"the claim did not stick (read back owners [{string.Join(", ", after.OwnerIds)}], labels [{string.Join(", ", after.LabelNames)}])");
    }

    public async Task<bool> InScopeAsync(int id, CancellationToken ct) =>
        !scope.IsEmpty && InScope(await GetStoryAsync(id, ct), await TeamIdsAsync(ct));

    /// <summary>Resolves every watched team and reads every watched epic; throws naming the first that does not exist.</summary>
    public async Task ValidateScopeAsync(CancellationToken ct)
    {
        await TeamIdsAsync(ct);
        foreach (var epic in scope.Epics)
        {
            try
            {
                await SendAsync<Epic>(HttpMethod.Get, $"epics/{epic}", null, ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Shortcut:Watch:Epics: cannot read Shortcut epic {epic}: {ex.Message}", ex);
            }
        }
    }

    public async Task ReleaseAsync(int id, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var story = await GetStoryAsync(id, ct);
        if (!story.OwnerIds.Contains(me) && !story.LabelNames.Contains(ClaimLabel))
        {
            return;
        }
        await UpdateAsync(id, new
        {
            owner_ids = story.OwnerIds.Where(o => o != me),
            labels = LabelParams(story.LabelNames.Where(l => l != ClaimLabel)),
        }, ct);
    }

    public async Task<WorkSpec> ReadSpecAsync(int id, CancellationToken ct)
    {
        var story = await GetStoryAsync(id, ct);
        var work = new WorkStory(story.Id, story.Name, story.Description, story.StoryType, story.AppUrl);
        if (story.EpicId is not { } epicId)
        {
            return new WorkSpec(work, null, []);
        }
        var epic = await SendAsync<Epic>(HttpMethod.Get, $"epics/{epicId}", null, ct);
        var documents = new List<WorkDocument>();
        foreach (var doc in await SendAsync<List<DocSlim>>(HttpMethod.Get, $"epics/{epicId}/documents", null, ct))
        {
            var full = await SendAsync<Doc>(HttpMethod.Get, $"documents/{doc.Id}", null, ct);
            documents.Add(new WorkDocument(full.Title ?? doc.Title ?? "", full.ContentMarkdown, full.AppUrl ?? doc.AppUrl ?? ""));
        }
        return new WorkSpec(work, new WorkEpic(epic.Id, epic.Name, epic.Description, epic.AppUrl), documents);
    }

    public async Task ReportStateAsync(int id, BoardState state, string? comment, CancellationToken ct)
    {
        if (state == BoardState.Stopped && string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Stopping an item needs a comment saying why.", nameof(comment));
        }
        var story = await GetStoryAsync(id, ct);
        var target = await StateIdAsync(story.WorkflowId, BoardStateName(state), ct);
        if (state == BoardState.Stopped)
        {
            var me = await MeAsync(ct);
            await UpdateAsync(id, new
            {
                workflow_state_id = target,
                owner_ids = story.OwnerIds.Where(o => o != me),
                labels = LabelParams(story.LabelNames.Where(l => l != ClaimLabel)),
            }, ct);
        }
        else if (story.WorkflowStateId != target)
        {
            await UpdateAsync(id, new { workflow_state_id = target }, ct);
        }
        if (comment is not null)
        {
            await CommentAsync(id, comment, ct);
        }
    }

    public async Task CommentAsync(int id, string text, CancellationToken ct) =>
        await SendAsync<IdOnly>(HttpMethod.Post, $"stories/{id}/comments", new { text = WorkSourceComments.Attributed(text) }, ct);

    public async Task LinkAsync(int id, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var story = await GetStoryAsync(id, ct);
        var links = story.ExternalLinks.Concat(urls).Distinct().ToList();
        if (links.Count != story.ExternalLinks.Count)
        {
            await UpdateAsync(id, new { external_links = links }, ct);
        }
    }

    public async Task<IReadOnlyList<int>> CreateChildrenAsync(int parentId, IReadOnlyList<ChildItem> children, CancellationToken ct)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].BlockedBy.Any(b => b < 0 || b >= i))
            {
                throw new ArgumentException($"Child {i} may only be blocked by earlier children.", nameof(children));
            }
        }
        var parent = await GetStoryAsync(parentId, ct);
        // Plan output waits in Backlog: nothing picks a child up until it is moved to To Do.
        var backlog = await StateIdAsync(parent.WorkflowId, BoardStateName(BoardState.Stopped), ct);
        // A retry after a partial failure reuses what the earlier call created: an unarchived story with
        // the child's exact name in the parent's epic and team, and its blocker relations.
        var existing = (await SiblingsAsync(parent, ct))
            .Where(s => !s.Archived && s.Id != parentId && s.EpicId == parent.EpicId && s.GroupId == parent.GroupId)
            .ToList();
        var ids = new List<int>();
        foreach (var child in children)
        {
            var reused = existing.FirstOrDefault(s => s.Name == child.Name && !ids.Contains(s.Id));
            int id;
            if (reused is not null)
            {
                id = reused.Id;
            }
            else
            {
                var body = new Dictionary<string, object>
                {
                    ["name"] = child.Name,
                    ["description"] = child.Description,
                    ["story_type"] = child.StoryType,
                    ["workflow_state_id"] = backlog,
                };
                if (parent.GroupId is { } group)
                {
                    body["group_id"] = group;
                }
                if (parent.EpicId is { } epic)
                {
                    body["epic_id"] = epic;
                }
                id = checked((int)(await SendAsync<IdOnly>(HttpMethod.Post, "stories", body, ct)).Id);
            }
            ids.Add(id);
            foreach (var blocker in child.BlockedBy)
            {
                if (reused is not null && reused.StoryLinks.Any(l => l.Verb == "blocks" && l.SubjectId == ids[blocker] && l.ObjectId == id))
                {
                    continue;
                }
                await SendAsync<IdOnly>(HttpMethod.Post, "story-links",
                    new { subject_id = ids[blocker], verb = "blocks", object_id = id }, ct);
            }
        }
        return ids;
    }

    /// <summary>The stories that share the parent's epic (or, without one, its team); none if it has neither.</summary>
    private async Task<List<Story>> SiblingsAsync(Story parent, CancellationToken ct) =>
        parent.EpicId is { } epic ? await SendAsync<List<Story>>(HttpMethod.Get, $"epics/{epic}/stories", null, ct)
        : parent.GroupId is { } group ? await TeamStoriesAsync(group, ct)
        : [];

    /// <summary>Every story of a team, following limit/offset pages until a short page.</summary>
    private async Task<List<Story>> TeamStoriesAsync(string teamId, CancellationToken ct)
    {
        var stories = new List<Story>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await SendAsync<List<Story>>(HttpMethod.Get, $"groups/{teamId}/stories?limit={PageSize}&offset={offset}", null, ct);
            stories.AddRange(page);
            if (page.Count < PageSize)
            {
                return stories;
            }
        }
    }

    private async Task<string?> ClaimRefusalAsync(Story story, string me, bool ignoreScope, CancellationToken ct)
    {
        if (story.Archived)
        {
            return "the story is archived";
        }
        if (HeldByAnother(story, me))
        {
            return $"the story carries the {ClaimLabel} label without the factory as owner: another claimant holds it";
        }
        if (!ClaimedBy(story, me) && !(await ReadyStateIdsAsync(ct)).Contains(story.WorkflowStateId))
        {
            return $"the story is no longer in {ReadyState}";
        }
        if (!ignoreScope && (scope.IsEmpty || !InScope(story, await TeamIdsAsync(ct))))
        {
            return "the story is outside the watch scope";
        }
        return null;
    }

    private static bool ClaimedBy(Story story, string me) => story.OwnerIds.Contains(me) && story.LabelNames.Contains(ClaimLabel);

    private static bool HeldByAnother(Story story, string me) => story.LabelNames.Contains(ClaimLabel) && !story.OwnerIds.Contains(me);

    private bool InScope(Story story, IReadOnlyList<string> teamIds) =>
        (story.GroupId is { } g && teamIds.Contains(g)) || (story.EpicId is { } e && scope.Epics.Contains(e));

    private Task<Story> GetStoryAsync(int id, CancellationToken ct) => SendAsync<Story>(HttpMethod.Get, $"stories/{id}", null, ct);

    private Task UpdateAsync(int id, object body, CancellationToken ct) => SendAsync<IdOnly>(HttpMethod.Put, $"stories/{id}", body, ct);

    private static IEnumerable<object> LabelParams(IEnumerable<string> names) => names.Distinct().Select(n => new { name = n });

    private async Task<string> MeAsync(CancellationToken ct) =>
        _me ??= (await SendAsync<Member>(HttpMethod.Get, "member", null, ct)).Id;

    private async Task<List<Workflow>> WorkflowsAsync(CancellationToken ct) =>
        _workflows ??= await SendAsync<List<Workflow>>(HttpMethod.Get, "workflows", null, ct);

    private async Task<List<long>> ReadyStateIdsAsync(CancellationToken ct) =>
        (await WorkflowsAsync(ct)).SelectMany(w => w.States).Where(s => s.Name == ReadyState).Select(s => s.Id).ToList();

    private async Task<long> StateIdAsync(long workflowId, string name, CancellationToken ct)
    {
        var workflow = (await WorkflowsAsync(ct)).SingleOrDefault(w => w.Id == workflowId)
            ?? throw new InvalidOperationException($"Shortcut workflow {workflowId} not found.");
        return workflow.States.SingleOrDefault(s => s.Name == name)?.Id
            ?? throw new InvalidOperationException($"Shortcut workflow '{workflow.Name}' has no '{name}' state.");
    }

    private async Task<List<string>> TeamIdsAsync(CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var team in scope.Teams)
        {
            ids.Add(await TeamIdAsync(team, ct));
        }
        return ids;
    }

    private async Task<string> TeamIdAsync(string team, CancellationToken ct)
    {
        _groups ??= await SendAsync<List<Group>>(HttpMethod.Get, "groups", null, ct);
        return _groups.FirstOrDefault(g => string.Equals(g.MentionName, team, StringComparison.OrdinalIgnoreCase)
                || string.Equals(g.Id, team, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new InvalidOperationException($"Shortcut:Watch:Teams: no Shortcut team '{team}'.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Shortcut-Token", apiToken);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>(ct)
                    ?? throw new InvalidOperationException($"Shortcut {method} {path} returned an empty body.");
            }
            if (attempt < MaxAttempts && RetryDelay(method, response, attempt) is { } delay)
            {
                await Task.Delay(delay, _time, ct);
                continue;
            }
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Shortcut {method} {path} failed: {(int)response.StatusCode} {response.ReasonPhrase} {detail[..Math.Min(detail.Length, 300)]}".TrimEnd());
        }
    }

    /// <summary>
    /// How long to wait before retrying, or null to fail now. A 429 was not processed, so any call is
    /// retried; a 502/503/504 may have been, so only calls that are safe to repeat (GET, PUT) are.
    /// </summary>
    private TimeSpan? RetryDelay(HttpMethod method, HttpResponseMessage response, int attempt)
    {
        var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                && (method == HttpMethod.Get || method == HttpMethod.Put));
        if (!retryable)
        {
            return null;
        }
        var retryAfter = response.Headers.RetryAfter is { } header
            ? header.Delta ?? (header.Date is { } date ? date - _time.GetUtcNow() : null)
            : null;
        var delay = retryAfter ?? TimeSpan.FromSeconds(1 << (attempt - 1));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private sealed record IdOnly([property: JsonPropertyName("id")] long Id);

    private sealed record Member([property: JsonPropertyName("id")] string Id);

    private sealed record Group(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("mention_name")] string MentionName);

    private sealed record WorkflowState(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record Workflow(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("states")] List<WorkflowState> States);

    private sealed record Label([property: JsonPropertyName("name")] string Name);

    private sealed record StoryLink(
        [property: JsonPropertyName("verb")] string Verb,
        [property: JsonPropertyName("subject_id")] int SubjectId,
        [property: JsonPropertyName("object_id")] int ObjectId);

    private sealed record Story(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("story_type")] string StoryType,
        [property: JsonPropertyName("app_url")] string AppUrl,
        [property: JsonPropertyName("archived")] bool Archived,
        [property: JsonPropertyName("workflow_id")] long WorkflowId,
        [property: JsonPropertyName("workflow_state_id")] long WorkflowStateId,
        [property: JsonPropertyName("group_id")] string? GroupId,
        [property: JsonPropertyName("epic_id")] int? EpicId,
        [property: JsonPropertyName("owner_ids")] List<string>? Owners,
        [property: JsonPropertyName("labels")] List<Label>? Labels,
        [property: JsonPropertyName("external_links")] List<string>? Links,
        [property: JsonPropertyName("story_links")] List<StoryLink>? Relations)
    {
        public List<string> OwnerIds => Owners ?? [];
        public List<string> LabelNames => Labels?.Select(l => l.Name).ToList() ?? [];
        public List<string> ExternalLinks => Links ?? [];
        public List<StoryLink> StoryLinks => Relations ?? [];
    }

    private sealed record Epic(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("app_url")] string AppUrl);

    private sealed record DocSlim(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("app_url")] string? AppUrl);

    private sealed record Doc(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("content_markdown")] string? ContentMarkdown,
        [property: JsonPropertyName("app_url")] string? AppUrl);
}
