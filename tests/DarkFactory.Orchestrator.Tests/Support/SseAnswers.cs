using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DarkFactory.Orchestrator.Tests.Support;

/// <summary>Anthropic Messages server-sent-event streams, as the router streams a model's answer (<c>"stream": true</c>).</summary>
public static class SseAnswers
{
    public static string Event(string type, object data) =>
        $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";

    public static string MessageStart(string model) => Event("message_start", new
    {
        type = "message_start",
        message = new
        {
            id = "msg_1", type = "message", role = "assistant", model, content = Array.Empty<object>(), stop_reason = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 1 },
        },
    });

    public static string TextBlockStart(int index) =>
        Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = "" } });

    public static string TextDelta(int index, string text) =>
        Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text } });

    public static string BlockStop(int index) => Event("content_block_stop", new { type = "content_block_stop", index });

    public static string Ping => Event("ping", new { type = "ping" });

    public static string MessageDelta(string stop) =>
        Event("message_delta", new { type = "message_delta", delta = new { stop_reason = stop, stop_sequence = (string?)null }, usage = new { output_tokens = 15 } });

    public static string MessageStop => Event("message_stop", new { type = "message_stop" });

    public static string Error(string type, string message) => Event("error", new { type = "error", error = new { type, message } });

    /// <summary>A whole answer: <paramref name="text"/> in one text block, cut into a few deltas, with a ping in between.</summary>
    public static string Answer(string text, string model = "claude-opus-5-5", string stop = "end_turn")
    {
        var sb = new StringBuilder(MessageStart(model)).Append(TextBlockStart(0)).Append(Ping);
        for (var i = 0; i < text.Length; i += 7)
        {
            sb.Append(TextDelta(0, text.Substring(i, Math.Min(7, text.Length - i))));
        }
        return sb.Append(BlockStop(0)).Append(MessageDelta(stop)).Append(MessageStop).ToString();
    }

    public static HttpResponseMessage Response(string events, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };

    /// <summary>A response whose body the test writes chunk by chunk, as a router streams it (completing the channel ends it).</summary>
    public static HttpResponseMessage Streamed(ChannelReader<string> chunks)
    {
        var content = new StreamContent(new ChannelStream(chunks));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ChannelStream(ChannelReader<string> chunks) : Stream
    {
        private ReadOnlyMemory<byte> _pending;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_pending.IsEmpty)
            {
                if (!await chunks.WaitToReadAsync(ct))
                {
                    return 0;
                }
                if (chunks.TryRead(out var chunk))
                {
                    _pending = Encoding.UTF8.GetBytes(chunk);
                }
            }
            var n = Math.Min(buffer.Length, _pending.Length);
            _pending[..n].CopyTo(buffer);
            _pending = _pending[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
