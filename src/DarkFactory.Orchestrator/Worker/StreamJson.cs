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
            }
        }
    }
}
