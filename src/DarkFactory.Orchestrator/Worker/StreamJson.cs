using System.Text.Json;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// Folds <c>claude -p --output-format stream-json --verbose</c> output lines into the
/// session id and the final result. Non-JSON lines are ignored.
/// </summary>
public sealed class StreamJsonState
{
    public string? SessionId { get; private set; }
    public bool SawResult { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultSubtype { get; private set; }
    public string? ResultText { get; private set; }

    /// <summary>The result's <c>terminal_reason</c> (e.g. <c>completed</c>, or <c>hook_stopped</c> when a hook ended the session).</summary>
    public string? TerminalReason { get; private set; }

    public void Accept(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (SessionId is null && root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                SessionId = sid.GetString();
            }
            if (root.TryGetProperty("type", out var type) && type.ValueEquals("result"))
            {
                SawResult = true;
                ResultIsError = root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                ResultSubtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
                ResultText = root.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                TerminalReason = root.TryGetProperty("terminal_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            }
        }
    }
}

/// <summary>What the session-event store needs to know about one stream-json line.</summary>
public sealed record StreamJsonEvent(string Type, string? Subtype, string? SessionId)
{
    /// <summary>The type of a line that is not a JSON object; such lines are kept, never dropped.</summary>
    public const string Raw = "raw";

    private const int MaxNameLength = 64;

    public static StreamJsonEvent Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return new StreamJsonEvent(Raw, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Name(root, "type") ?? "unknown";
            var subtype = Name(root, "subtype");
            if (subtype is null && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                && content.GetArrayLength() > 0 && content[0].ValueKind == JsonValueKind.Object)
            {
                subtype = Name(content[0], "type"); // tool_use, tool_result, text, thinking
            }
            var sessionId = root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
            return new StreamJsonEvent(type, subtype, sessionId);
        }
        catch (JsonException)
        {
            return new StreamJsonEvent(Raw, null, null);
        }
    }

    private static string? Name(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } s
            ? s[..Math.Min(s.Length, MaxNameLength)]
            : null;
}
