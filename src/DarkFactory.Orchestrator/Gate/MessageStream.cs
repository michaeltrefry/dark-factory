using System.Text;
using System.Text.Json;

namespace DarkFactory.Orchestrator.Gate;

/// <summary>A model's answer as the router streamed it: the model it says served the call, why it stopped, and its text.</summary>
public sealed record StreamedAnswer(string? Served, string? StopReason, string Text);

/// <summary>
/// Assembles an Anthropic Messages server-sent-event stream (<c>"stream": true</c>) into one answer: the served model from
/// <c>message_start</c>, the text of every text block (<c>content_block_start</c>/<c>content_block_delta</c> text deltas, each
/// block followed by a newline), the stop reason from <c>message_delta</c>. The router cancels a call that has sent its client
/// nothing for 10 s, so the panel's calls stream (sc-25391). Fails closed: an <c>error</c> event throws (usage-limited when it
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
                    data.Append(data.Length > 0 ? "\n" : "").Append(line.AsSpan(5).TrimStart(' '));
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
                    inText = root.TryGetProperty("content_block", out var block) && Str(block, "type") == "text";
                    if (inText)
                    {
                        text.Append(Str(block, "text"));
                    }
                    break;
                case "content_block_delta":
                    if (inText && root.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta")
                    {
                        text.Append(Str(delta, "text"));
                    }
                    break;
                case "content_block_stop":
                    if (inText)
                    {
                        text.Append('\n');
                    }
                    inText = false;
                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta) && Str(messageDelta, "stop_reason") is { } reason)
                    {
                        stop = reason;
                    }
                    break;
                case "message_stop":
                    return new StreamedAnswer(served, stop, text.ToString());
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
