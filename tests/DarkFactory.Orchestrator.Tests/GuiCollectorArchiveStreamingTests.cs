using System.Security.Cryptography;
using System.Text.Json;
using DarkFactory.Orchestrator.Gateway;

namespace DarkFactory.Orchestrator.Tests;

public sealed class GuiCollectorArchiveStreamingTests
{
    const int Buffer = 81920;
    const string Metadata = "https://api.github.com/repos/actions/runner/releases/latest";

    [Fact]
    public async Task Large_virtual_archive_is_verified_with_bounded_reads_and_writes_not_whole_archive_allocations()
    {
        const long bytes = 64L * 1024 * 1024 + 17;
        await using var source = new PatternStream(bytes);
        await using var output = new CountingOutput();
        await GuiCollectorArchive.CopyVerifiedAsync(source, output, Expected(bytes), bytes, CancellationToken.None);
        Assert.Equal(bytes, output.Bytes);
        Assert.InRange(source.LargestRead, 1, Buffer);
        Assert.InRange(output.LargestWrite, 1, Buffer);
        Assert.True(source.Reads > 100);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("unbounded-proof")]
    public async Task Streaming_copy_never_turns_hash_size_or_unbounded_proof_failures_into_a_verified_snapshot(string corrupt)
    {
        const long length = Buffer * 3L + 1;
        await using var source = new PatternStream(length + (corrupt == "short" ? -1 : corrupt == "long" ? 1 : 0));
        await using var output = new CountingOutput();
        var hash = corrupt == "hash" ? new string('0', 64) : Expected(length);
        var bytes = corrupt == "unbounded-proof" ? GuiCollectorArchive.MaxBytes + 1 : length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.CopyVerifiedAsync(source, output, hash, bytes, CancellationToken.None));
        Assert.InRange(source.LargestRead, 0, Buffer);
        Assert.InRange(output.LargestWrite, 0, Buffer);
    }

    [Fact]
    public async Task Streaming_verification_honors_cancellation_mid_archive_without_finishing_or_returning_proof()
    {
        using var stop = new CancellationTokenSource();
        await using var source = new PatternStream(Buffer * 20L, () => stop.Cancel());
        await using var output = new CountingOutput();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GuiCollectorArchive.CopyVerifiedAsync(source, output, Expected(Buffer * 20L), Buffer * 20L, stop.Token));
        Assert.True(source.Position < source.Length);
        Assert.True(output.Bytes < source.Length);
    }

    [Fact]
    public async Task Private_acquired_snapshot_keeps_its_same_path_and_official_receipt_hash_without_a_second_copy()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "df-gui-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string name = "actions-runner-osx-arm64-2.321.0.tar.gz";
            var file = Path.Combine(directory, name);
            var bytes = new byte[Buffer * 2 + 11];
            Array.Fill(bytes, (byte)0x5a);
            await File.WriteAllBytesAsync(file, bytes);
            var hash = GuiCollectorDeployment.Hash(bytes);
            var receipt = Path.Combine(directory, "verified-release.json");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { version = "2.321.0", archive = name, sha256 = hash, bytes = bytes.LongLength, metadata = Metadata }));
            var proof = await GuiCollectorArchive.VerifySnapshotAsync(file, CancellationToken.None);
            Assert.Equal(file, proof.Path);
            Assert.Equal(hash, proof.Sha256);
            Assert.Equal(bytes.LongLength, proof.Bytes);
            Assert.Equal(2, Directory.EnumerateFiles(directory).Count());
            await File.WriteAllBytesAsync(file, new byte[bytes.Length]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => GuiCollectorArchive.VerifySnapshotAsync(file, CancellationToken.None));
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(directory, true); }
    }

    static string Expected(long bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Buffer];
        Array.Fill(buffer, (byte)0x5a);
        while (bytes > 0) { var count = (int)Math.Min(bytes, buffer.Length); hash.AppendData(buffer, 0, count); bytes -= count; }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    sealed class PatternStream(long bytes, Action? cancel = null) : Stream
    {
        long position;
        public int Reads, LargestRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Reads++; LargestRead = Math.Max(LargestRead, buffer.Length);
            if (Reads == 3 && cancel is not null) { cancel(); ct.ThrowIfCancellationRequested(); }
            var count = (int)Math.Min(buffer.Length, bytes - position);
            buffer.Span[..count].Fill(0x5a); position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class CountingOutput : Stream
    {
        public long Bytes;
        public int LargestWrite;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Bytes += buffer.Length; LargestWrite = Math.Max(LargestWrite, buffer.Length); return ValueTask.CompletedTask; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Bytes += count; LargestWrite = Math.Max(LargestWrite, count); }
    }
}
