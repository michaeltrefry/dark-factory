using System.Diagnostics;

namespace DarkFactory.Orchestrator.Tests;

/// <summary>
/// The AC2 live probe (sc-26789, S3): what a worker in its own Aqua login can LOOK UP of the owner's per-session Mach
/// services — the pasteboard, distnoted and WindowServer — without ever sending a message to any of them. The probe is a
/// tiny C program the live test compiles owner-side (read-only: a lookup, nothing else) and then runs twice: as the owner
/// (its own session, the baseline) and as each sandbox user through the real installed launch helper, in that user's own
/// GUI login domain (the helper refuses to run it anywhere else).
/// <para>
/// There is no public API that names a Mach service's receiver without exchanging messages with it (xpc credentials are
/// unknown before a message is sent; <c>bootstrap_look_up</c> returns a capability, not an identity), so the evidence has
/// exactly two tiers, and neither is overstated: a lookup that fails in the worker's domain (refused, or the name is not
/// in its namespace) proves this lookup gave the worker nothing; a lookup that succeeds proves nothing about the receiver —
/// GUI and user domains share one flat lookup namespace — and the send right is released without a single message having
/// been sent, recorded as "succeeded, receiver identity unverified". Whether that satisfies "cannot obtain" is the
/// owner's decision, recorded with the verbatim output; the probe never claims it.
/// </para>
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class MachLookupProbe
{
    /// <summary>The owner's per-session services the probe looks up (the pasteboard server, distnoted and WindowServer).</summary>
    public static readonly string[] Services = ["com.apple.pasteboard.1", "com.apple.distnoted", "com.apple.windowserver"];

    /// <summary>
    /// The probe itself. Lookup only: <c>bootstrap_look_up</c> per name, print the outcome, and on success release the send
    /// right with <c>mach_port_deallocate</c> — never <c>mach_msg</c>, never an xpc connection, no pasteboard, notification or
    /// window call: nothing is sent to any service, so nothing about the owner's session is read or changed.
    /// </summary>
    public const string Source = """
        #include <mach/mach.h>
        #include <servers/bootstrap.h>
        #include <stdio.h>
        #include <unistd.h>
        int main(int argc, char **argv) {
            for (int i = 1; i < argc; i++) {
                mach_port_t port = MACH_PORT_NULL;
                kern_return_t kr = bootstrap_look_up(bootstrap_port, argv[i], &port);
                if (kr != KERN_SUCCESS) {
                    printf("probe %s uid=%d REFUSED kr=%d\n", argv[i], geteuid(), (int)kr);
                } else {
                    printf("probe %s uid=%d SUCCEEDED port=%d\n", argv[i], geteuid(), (int)port);
                    mach_port_deallocate(mach_task_self(), port);
                    printf("probe %s released-without-send\n", argv[i]);
                }
            }
            return 0;
        }
        """;

    /// <summary>Compiles the probe with clang into <paramref name="directory"/> (owner-side, read-only); null when there is no clang.</summary>
    public static async Task<string?> CompileAsync(string directory, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        var source = Path.Combine(directory, "mach-probe.c");
        var binary = Path.Combine(directory, "mach-probe");
        await File.WriteAllTextAsync(source, Source, ct);
        using var clang = Process.Start(new ProcessStartInfo("/usr/bin/clang", ["-o", binary, source]) { RedirectStandardError = true })!;
        await clang.WaitForExitAsync(ct);
        return clang.ExitCode == 0 && File.Exists(binary) ? binary : null;
    }

    /// <summary>One service's lookup outcome in one domain. <see cref="ReleasedWithoutSend"/> is false only for a refused lookup (no right existed to release).</summary>
    public sealed record Outcome(string Service, int Uid, bool LookedUp, int Kr, bool ReleasedWithoutSend)
    {
        /// <summary>
        /// The tier, said plainly: <c>refused</c> (the lookup gave this domain nothing) or <c>succeeded-unverified</c> — a send
        /// right existed, so NOTHING about isolation is proven (the receiver was never identified and no message was sent).
        /// </summary>
        public string Tier => LookedUp ? "succeeded-unverified: receiver identity unknown, nothing sent — not AC2 evidence by itself" : "refused";
    }

    /// <summary>Reads the probe's verbatim lines into its outcomes; an output that is not exactly the probe's own words throws.</summary>
    public static IReadOnlyList<Outcome> Parse(string output)
    {
        var outcomes = new List<Outcome>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var released = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (ParseLine(line, " REFUSED kr=") is { } refused)
            {
                Assert.True(reported.Add(refused.Service), $"the probe reported {refused.Service} twice");
                outcomes.Add(new Outcome(refused.Service, refused.Uid, LookedUp: false, refused.Number, ReleasedWithoutSend: false));
            }
            else if (ParseLine(line, " SUCCEEDED port=") is { } succeeded)
            {
                Assert.True(reported.Add(succeeded.Service), $"the probe reported {succeeded.Service} twice");
                outcomes.Add(new Outcome(succeeded.Service, succeeded.Uid, LookedUp: true, 0, ReleasedWithoutSend: false));
            }
            else if (line.StartsWith("probe ", StringComparison.Ordinal) && line.EndsWith(" released-without-send", StringComparison.Ordinal))
            {
                var service = line["probe ".Length..^" released-without-send".Length];
                Assert.Contains(service, reported); // its own SUCCEEDED line came first
                Assert.DoesNotContain(service, released);
                released.Add(service);
            }
            else
            {
                throw new InvalidOperationException($"not the probe's output: {line}");
            }
        }
        // Every succeeded lookup released its send right, and only a succeeded one can have.
        foreach (var outcome in outcomes)
        {
            Assert.Equal(outcome.LookedUp, released.Contains(outcome.Service));
        }
        return outcomes.Select(o => released.Contains(o.Service) ? o with { ReleasedWithoutSend = true } : o).ToList();
    }

    private static (string Service, int Uid, int Number)? ParseLine(string line, string middle)
    {
        if (!line.StartsWith("probe ", StringComparison.Ordinal) || !line.Contains(middle, StringComparison.Ordinal))
        {
            return null;
        }
        var head = line[..line.IndexOf(middle, StringComparison.Ordinal)];
        var fields = head.Split(' '); // "probe", "<service>", "uid=<n>"
        if (fields.Length != 3 || fields[0] != "probe" || !fields[2].StartsWith("uid=", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"not the probe's output: {line}");
        }
        var number = int.Parse(line[(head.Length + middle.Length)..]);
        return (fields[1], int.Parse(fields[2][4..]), number);
    }
}
