using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DarkFactory.Orchestrator.Worker;

/// <summary>
/// A small file in a worker-writable tree, read owner-side without trusting it: <see cref="Missing"/>, its <see cref="Text"/>, or a
/// <see cref="Refusal"/>. The worker can put anything at the path, so the read never follows a symlink, never blocks (a FIFO would hang
/// a plain open forever while the caller holds the item's run lock), refuses anything that is not a regular file and anything larger
/// than <see cref="SafeFile.MaxBytes"/> (a sparse multi-GB file), and turns every I/O or memory failure into a refusal.
/// </summary>
public sealed record SafeFileRead(string? Text, string? Refusal)
{
    public static readonly SafeFileRead Missing = new(null, null);

    public bool IsMissing => Text is null && Refusal is null;
}

public static class SafeFile
{
    /// <summary>The largest file read (1 MiB); a larger one is refused.</summary>
    public const int MaxBytes = 1 << 20;

    public static SafeFileRead Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                return new(null, "is a symlink");
            }
            if (Directory.Exists(path))
            {
                return new(null, "is not a regular file");
            }
            if (!info.Exists)
            {
                return SafeFileRead.Missing;
            }
            // O_NOFOLLOW: a symlink swapped in since the check fails the open; O_NONBLOCK: a FIFO opens at once instead of waiting
            // for a writer (and is refused below, not read).
            var fd = open(path, OpenFlags());
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                return File.Exists(path) || new FileInfo(path).LinkTarget is not null
                    ? new(null, $"cannot be opened (errno {errno})")
                    : SafeFileRead.Missing;
            }
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1);
            if (!stream.CanSeek)
            {
                return new(null, "is not a regular file"); // a FIFO or socket
            }
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }
            return total > MaxBytes
                ? new(null, $"is larger than {MaxBytes} bytes")
                : new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer, 0, total), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or DecoderFallbackException
            or NotSupportedException or ArgumentException)
        {
            return new(null, $"cannot be read ({ex.GetType().Name})");
        }
    }

    /// <summary><c>O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC</c> for this platform.</summary>
    private static int OpenFlags()
    {
        if (OperatingSystem.IsMacOS())
        {
            return 0x4 | 0x100 | 0x1000000;
        }
        if (OperatingSystem.IsLinux())
        {
            var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;
            return 0x800 | noFollow | 0x80000;
        }
        throw new PlatformNotSupportedException("SafeFile reads only on macOS and Linux.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
