using System.Text.Json;
using System.Text.Json.Nodes;
using DarkFactory.Orchestrator.Sessions;

namespace DarkFactory.Orchestrator.Dashboard;

public enum TranscriptKind
{
    /// <summary>Assistant prose.</summary>
    Text,
    /// <summary>Assistant thinking (often redacted to a signature: shown only when it has text).</summary>
    Thinking,
    /// <summary>A tool call: the tool name and its input.</summary>
    ToolCall,
    /// <summary>A Bash tool call: the command.</summary>
    Command,
    /// <summary>A tool's result (for Bash, the command's output).</summary>
    ToolResult,
    /// <summary>The session's final result line.</summary>
    Result,
    /// <summary>A system event (init, progress counters).</summary>
    System,
    /// <summary>A line that is not stream-json, or an event type this view does not know: shown as-is.</summary>
    Raw,
}

/// <summary>One readable piece of a worker transcript.</summary>
public sealed record TranscriptEntry(long Sequence, TranscriptKind Kind, string Title, string Body, bool IsError = false);

/// <summary>
/// Turns stored stream-json events into readable entries. One instance per session view: it
/// remembers tool calls so their results can name the tool.
/// </summary>
public sealed class TranscriptFormatter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, string> _toolNames = [];

    public IReadOnlyList<TranscriptEntry> Format(SessionEventMessage e)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(e.Payload) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }
        if (json is null)
        {
            return [Raw(e)];
        }
        var entries = Str(json["type"]) switch
        {
            "assistant" => Content(e, json, assistant: true),
            "user" => Content(e, json, assistant: false),
            "system" => [SystemEntry(e, json)],
            "result" => [Result(e, json)],
            _ => null,
        };
        return entries is { Count: > 0 } ? entries : [Raw(e)];
    }

    private List<TranscriptEntry>? Content(SessionEventMessage e, JsonObject json, bool assistant)
    {
        var content = json["message"]?["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out var plain))
        {
            return [new TranscriptEntry(e.Sequence, TranscriptKind.Text, assistant ? "assistant" : "user", plain)];
        }
        if (content is not JsonArray blocks)
        {
            return null;
        }
        var entries = new List<TranscriptEntry>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch (Str(block["type"]))
            {
                case "text":
                    entries.Add(new(e.Sequence, TranscriptKind.Text, assistant ? "assistant" : "user", Str(block["text"]) ?? ""));
                    break;
                case "thinking" when !string.IsNullOrWhiteSpace(Str(block["thinking"])):
                    entries.Add(new(e.Sequence, TranscriptKind.Thinking, "thinking", Str(block["thinking"])!));
                    break;
                case "thinking" or "redacted_thinking":
                    break;
                case "tool_use":
                    entries.Add(ToolUse(e, block));
                    break;
                case "tool_result":
                    entries.Add(ToolResult(e, block));
                    break;
                default:
                    entries.Add(new(e.Sequence, TranscriptKind.Raw, Str(block["type"]) ?? "block", block.ToJsonString(Indented)));
                    break;
            }
        }
        return entries;
    }

    private TranscriptEntry ToolUse(SessionEventMessage e, JsonObject block)
    {
        var name = Str(block["name"]) ?? "tool";
        if (Str(block["id"]) is { } id)
        {
            _toolNames[id] = name;
        }
        var input = block["input"];
        if (name == "Bash" && Str(input?["command"]) is { } command)
        {
            var description = Str(input?["description"]);
            return new(e.Sequence, TranscriptKind.Command, description is null ? "Bash" : $"Bash — {description}", command);
        }
        return new(e.Sequence, TranscriptKind.ToolCall, name, input?.ToJsonString(Indented) ?? "");
    }

    private TranscriptEntry ToolResult(SessionEventMessage e, JsonObject block)
    {
        var name = Str(block["tool_use_id"]) is { } id && _toolNames.TryGetValue(id, out var n) ? n : "tool";
        var body = block["content"] switch
        {
            JsonValue s when s.TryGetValue<string>(out var text) => text,
            JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
                .Select(p => Str(p["type"]) == "text" ? Str(p["text"]) ?? "" : p.ToJsonString(Indented))),
            null => "",
            var other => other.ToJsonString(Indented),
        };
        var isError = block["is_error"] is JsonValue err && err.TryGetValue<bool>(out var b) && b;
        return new(e.Sequence, TranscriptKind.ToolResult, name == "Bash" ? "output" : $"{name} result", body, isError);
    }

    private static TranscriptEntry SystemEntry(SessionEventMessage e, JsonObject json)
    {
        var subtype = Str(json["subtype"]) ?? "system";
        if (subtype == "init")
        {
            var details = new[] { ("model", Str(json["model"])), ("cwd", Str(json["cwd"])), ("session", Str(json["session_id"])) }
                .Where(d => d.Item2 is not null)
                .Select(d => $"{d.Item1}: {d.Item2}");
            return new(e.Sequence, TranscriptKind.System, "session started", string.Join("\n", details));
        }
        return new(e.Sequence, TranscriptKind.System, subtype, "");
    }

    private static TranscriptEntry Result(SessionEventMessage e, JsonObject json)
    {
        var facts = new[]
        {
            ("turns", json["num_turns"]?.ToJsonString()),
            ("duration", json["duration_ms"] is { } ms ? $"{ms.ToJsonString()} ms" : null),
            ("cost reported by Claude", json["total_cost_usd"] is { } c ? $"${c.ToJsonString()}" : null),
        }.Where(f => f.Item2 is not null).Select(f => $"{f.Item1}: {f.Item2}");
        var isError = json["is_error"] is JsonValue err && err.TryGetValue<bool>(out var b) && b;
        var body = string.Join("\n", new[] { Str(json["result"]) ?? "", string.Join(" · ", facts) }.Where(s => s.Length > 0));
        return new(e.Sequence, TranscriptKind.Result, $"result: {Str(json["subtype"]) ?? "done"}", body, isError);
    }

    private static TranscriptEntry Raw(SessionEventMessage e) => new(e.Sequence, TranscriptKind.Raw, e.Type, e.Payload);

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
