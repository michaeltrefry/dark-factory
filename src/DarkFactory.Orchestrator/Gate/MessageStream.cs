using System.Text;
using System.Text.Json;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>
/// A model's answer as the router streamed it: the model it says served the call, why it stopped, and its text.
/// <see cref="Blocks"/>: its text and tool_use content blocks in order (what a tool loop sends back as the assistant's turn).
/// </summary>
public sealed record StreamedAnswer(string? Served, string? StopReason, string Text)
{
    public IReadOnlyList<AnswerBlock> Blocks { get; init; } = [];

    /// <summary>The answer's tool_use blocks.</summary>
    public IEnumerable<AnswerBlock> ToolUses => Blocks.Where(b => b.Type == AnswerBlock.ToolUse);
}

/// <summary>
/// One content block of a streamed answer: a <c>text</c> block (<see cref="Text"/>) or a <c>tool_use</c> block (<see cref="Id"/>,
/// <see cref="Name"/> and its input as the model streamed it, <see cref="InputJson"/>: not necessarily valid JSON).
/// </summary>
public sealed record AnswerBlock(string Type, string? Text, string? Id = null, string? Name = null, string? InputJson = null)
{
    public const string TextType = "text";
    public const string ToolUse = "tool_use";
}

/// <summary>
/// Assembles an Anthropic Messages server-sent-event stream (<c>"stream": true</c>) into one answer: the served model from
/// <c>message_start</c>, the text of every text block (<c>content_block_start</c>/<c>content_block_delta</c> text deltas, each
/// block followed by a newline), the stop reason from <c>message_delta</c>. The router cancels a call that has sent its client
/// nothing for 10 s, so the panel's calls stream (sc-25391). A <c>tool_use</c> block (its <c>input_json_delta</c> parts joined) is
/// kept in <see cref="StreamedAnswer.Blocks"/> for the reviewer's tool loop (sc-25705). Fails closed: an <c>error</c> event throws (usage-limited when it
/// is a usage refusal), and a stream that ends without <c>message_stop</c> or stalls longer than the idle gap throws rather
/// than count a partial answer.
/// </summary>
public static class MessageStream
{
    public static async Task<StreamedAnswer> ReadAsync(Stream body, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        string? served = null, stop = null, eventName = null;
        var text = new StringBuilder();
        var data = new StringBuilder();
        var inText = false;
        var blocks = new List<AnswerBlock>();
        StringBuilder? blockText = null;
        (string? Id, string? Name, StringBuilder Input, string Start)? tool = null;
        var events = 0;
        while (true)
        {
            string? line;
            // A fresh idle timer per read: the gap between two lines, not the whole answer, is bounded.
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        $"The router's answer stream stalled: nothing for {idleTimeout.TotalSeconds:0.#} s after {events} event(s).");
                }
            }
            if (line is null)
            {
                throw new InvalidOperationException($"The router's answer stream ended without message_stop after {events} event(s).");
            }
            if (line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // The SSE spec strips exactly one space after the colon.
                    var value = line.AsSpan(5);
                    data.Append(data.Length > 0 ? "\n" : "").Append(value.StartsWith(" ") ? value[1..] : value);
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line[6..].Trim();
                }
                continue;
            }
            if (data.Length == 0)
            {
                eventName = null;
                continue;
            }
            // A blank line dispatches the event.
            var payload = data.ToString();
            data.Clear();
            events++;
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"The router's answer stream sent an event that is not JSON: {Cut(payload)}");
            }
            var type = Str(root, "type") ?? eventName;
            eventName = null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message))
                    {
                        served = Str(message, "model");
                        stop = Str(message, "stop_reason") ?? stop;
                    }
                    break;
                case "content_block_start":
                    root.TryGetProperty("content_block", out var block);
                    inText = Str(block, "type") == AnswerBlock.TextType;
                    tool = null;
                    if (inText)
                    {
                        text.Append(Str(block, "text"));
                        blockText = new StringBuilder(Str(block, "text"));
                    }
                    else if (Str(block, "type") == AnswerBlock.ToolUse)
                    {
                        // The start carries the input as an object (normally empty); input_json_delta parts that follow replace it.
                        var start = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
                        tool = (Str(block, "id"), Str(block, "name"), new StringBuilder(), start);
                    }
                    break;
                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var delta))
                    {
                        break;
                    }
                    if (inText && Str(delta, "type") == "text_delta")
                    {
                        text.Append(Str(delta, "text"));
                        blockText?.Append(Str(delta, "text"));
                    }
                    else if (tool is { } partial && Str(delta, "type") == "input_json_delta")
                    {
                        partial.Input.Append(Str(delta, "partial_json"));
                    }
                    break;
                case "content_block_stop":
                    if (inText)
                    {
                        text.Append('\n');
                        blocks.Add(new AnswerBlock(AnswerBlock.TextType, blockText?.ToString() ?? ""));
                    }
                    else if (tool is { } done)
                    {
                        blocks.Add(new AnswerBlock(AnswerBlock.ToolUse, null, done.Id, done.Name,
                            done.Input.Length > 0 ? done.Input.ToString() : done.Start));
                    }
                    inText = false;
                    blockText = null;
                    tool = null;
                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta) && Str(messageDelta, "stop_reason") is { } reason)
                    {
                        stop = reason;
                    }
                    break;
                case "message_stop":
                    return new StreamedAnswer(served, stop, text.ToString()) { Blocks = blocks };
                case "error":
                    var why = $"The review call through the router failed mid-stream: {Cut(payload)}";
                    if (RouterReviewer.UsageLimited(0, payload))
                    {
                        throw new RouterUsageLimitedException(why);
                    }
                    throw new InvalidOperationException(why);
                default:
                    // ping, and any event type added later, carry nothing the answer needs.
                    break;
            }
        }
    }

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s) => s.Length > 500 ? s[..500] : s;
}
