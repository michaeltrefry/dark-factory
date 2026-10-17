using System.Net.Http;
using System.Text.Json;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>
/// A fake hot-file assessor model that reads before it answers, as the recovery assessor requires (sc-26335): first, over as
/// many tool turns as needed, a <c>read_evidence</c> of every merged diff in the request's index and of every evidence id the
/// final answer cites; then the answer. Stateless: what is still to read comes from the request's own earlier tool calls.
/// </summary>
public static class AssessmentReads
{
    private const int PerTurn = 10;

    public static HttpResponseMessage Respond(RecordedRequest request, string answer, string? modelClass = "high") =>
        SseAnswers.Response(Next(request.Body!, answer), modelClass: modelClass);

    /// <summary>The next turn's events: a tool turn of evidence still unread, or the answer.</summary>
    public static string Next(string body, string answer)
    {
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var requested = messages.Where(m => m.GetProperty("role").GetString() == "assistant")
            .SelectMany(m => Blocks(m)).Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
            .Select(b => b.GetProperty("input").TryGetProperty("id", out var id) ? id.GetString() : null).ToHashSet();
        var unread = Needed(Text(messages[0]), answer).Where(id => !requested.Contains(id)).ToArray();
        if (unread.Length == 0) return SseAnswers.Answer(answer);
        var turn = messages.Length;
        return SseAnswers.ToolTurn(unread.Take(PerTurn).Select((id, n) => ($"read-{turn}-{n}", "read_evidence",
            JsonSerializer.Serialize(new { id }))).ToArray());
    }

    private static IEnumerable<string> Needed(string evidence, string answer)
    {
        var start = evidence.IndexOf("{\"version\":1", StringComparison.Ordinal);
        var end = evidence.LastIndexOf('}');
        var ids = new List<string>();
        if (start >= 0 && end > start)
        {
            using var request = JsonDocument.Parse(evidence[start..(end + 1)]);
            ids.AddRange(request.RootElement.GetProperty("material").GetProperty("Evidence").EnumerateArray()
                .Where(e => e.GetProperty("Category").GetString() == "merged-diff").Select(e => e.GetProperty("Id").GetString()!));
        }
        try
        {
            using var parsed = JsonDocument.Parse(answer);
            var root = parsed.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("citations", out var top) && top.ValueKind == JsonValueKind.Array)
                    ids.AddRange(top.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!));
                if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
                    foreach (var finding in findings.EnumerateArray())
                        if (finding.ValueKind == JsonValueKind.Object && finding.TryGetProperty("citations", out var cited) && cited.ValueKind == JsonValueKind.Array)
                            ids.AddRange(cited.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!));
            }
        }
        catch (JsonException) { }
        return ids.Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<JsonElement> Blocks(JsonElement message) =>
        message.GetProperty("content") is { ValueKind: JsonValueKind.Array } content ? content.EnumerateArray() : [];

    private static string Text(JsonElement message) => message.GetProperty("content") is { ValueKind: JsonValueKind.String } text
        ? text.GetString()! : string.Concat(Blocks(message).Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()));
}
