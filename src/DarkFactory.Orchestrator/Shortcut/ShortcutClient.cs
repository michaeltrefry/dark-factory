using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DarkFactory.Orchestrator.Shortcut;

public sealed record ShortcutStory(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("story_type")] string StoryType,
    [property: JsonPropertyName("app_url")] string AppUrl);

public interface IStorySource
{
    Task<ShortcutStory> GetStoryAsync(int id, CancellationToken ct);
}

/// <summary>Reads stories from the Shortcut REST API v3 with the orchestrator's own token.</summary>
public sealed class ShortcutClient(HttpClient http, string apiToken) : IStorySource
{
    public static readonly Uri DefaultBaseAddress = new("https://api.app.shortcut.com/api/v3/");

    public async Task<ShortcutStory> GetStoryAsync(int id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"stories/{id}");
        request.Headers.Add("Shortcut-Token", apiToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Shortcut GET stories/{id} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        return await response.Content.ReadFromJsonAsync<ShortcutStory>(ct)
            ?? throw new InvalidOperationException($"Shortcut returned an empty body for story {id}.");
    }
}
