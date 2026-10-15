using System.Net;
using System.Net.Sockets;

namespace DarkFactory.Orchestrator.Gateway;

/// <summary>Resolves a host name to its addresses (the DNS seam of the artifact downloads, sc-26232; tests give their own).</summary>
public interface IArtifactResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>The system resolver.</summary>
public sealed class DnsArtifactResolver : IArtifactResolver
{
    public static readonly DnsArtifactResolver Instance = new();

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) => await Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>The download was refused before or while connecting (an address or URL the rules do not allow), saying why.</summary>
public sealed class ArtifactAddressException(string message) : Exception(message);

/// <summary>
/// Which addresses a recipe's artifact may be downloaded from (sc-26232): public unicast only. Refused: unspecified, loopback,
/// private (RFC 1918), shared/CGNAT (100.64/10), link-local, multicast, broadcast and reserved IPv4, the IETF/documentation and
/// benchmarking ranges, and in IPv6 the unspecified and loopback addresses, unique-local (fc00::/7), link-local (fe80::/10),
/// site-local (fec0::/10), multicast (ff00::/8), documentation (2001:db8::/32), discard (100::/64) — and any IPv6 form that carries
/// an IPv4 address (mapped, compatible, NAT64 64:ff9b::/96, 6to4 2002::/16, Teredo 2001::/32) is judged by that IPv4 address.
/// </summary>
public static class ArtifactAddresses
{
    private static readonly (byte[] Prefix, int Bits, string Why)[] V4 =
    [
        ([0, 0, 0, 0], 8, "an unspecified (0.0.0.0/8) address"),
        ([10, 0, 0, 0], 8, "a private (10/8) address"),
        ([100, 64, 0, 0], 10, "a shared/CGNAT (100.64/10) address"),
        ([127, 0, 0, 0], 8, "a loopback address"),
        ([169, 254, 0, 0], 16, "a link-local address"),
        ([172, 16, 0, 0], 12, "a private (172.16/12) address"),
        ([192, 0, 0, 0], 24, "an IETF protocol (192.0.0/24) address"),
        ([192, 0, 2, 0], 24, "a documentation address"),
        ([192, 88, 99, 0], 24, "a 6to4 relay address"),
        ([192, 168, 0, 0], 16, "a private (192.168/16) address"),
        ([198, 18, 0, 0], 15, "a benchmarking address"),
        ([198, 51, 100, 0], 24, "a documentation address"),
        ([203, 0, 113, 0], 24, "a documentation address"),
        ([224, 0, 0, 0], 4, "a multicast address"),
        ([240, 0, 0, 0], 4, "a reserved or broadcast address"),
    ];

    private static readonly (byte[] Prefix, int Bits, string Why)[] V6 =
    [
        (new byte[16], 128, "the unspecified address"),
        ([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], 128, "the loopback address"),
        ([0x01, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 64, "a discard (100::/64) address"),
        ([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 32, "a documentation (2001:db8::/32) address"),
        ([0xfc, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 7, "a unique-local (fc00::/7) address"),
        ([0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 10, "a link-local (fe80::/10) address"),
        ([0xfe, 0xc0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 10, "a site-local (fec0::/10) address"),
        ([0xff, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 8, "a multicast address"),
    ];

    /// <summary>Why <paramref name="address"/> may not be downloaded from, or null for a public unicast address.</summary>
    public static string? Blocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return V4.FirstOrDefault(r => Matches(bytes, r.Prefix, r.Bits)).Why;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return "not an IP address";
        }
        if (address.ScopeId != 0)
        {
            return "a scoped (link-local) address";
        }
        var b = address.GetAddressBytes();
        if (V6.FirstOrDefault(r => Matches(b, r.Prefix, r.Bits)).Why is { } why)
        {
            return why;
        }
        // An IPv6 form that carries an IPv4 address is judged by that address: mapped ::ffff:a.b.c.d, compatible ::a.b.c.d,
        // NAT64 64:ff9b::a.b.c.d (and its local-use 64:ff9b:1::/48), 6to4 2002:AABB:CCDD::, Teredo 2001:0::/32 (the client's
        // address, inverted, in the last 32 bits).
        if (address.IsIPv4MappedToIPv6 || (b.Take(12).All(x => x == 0)))
        {
            return Embedded(b[12..16], "an IPv4-mapped or -compatible address");
        }
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
        {
            return Embedded(b[12..16], "a NAT64 address");
        }
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return Embedded(b[2..6], "a 6to4 address");
        }
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
        {
            return Embedded(b[12..16].Select(x => (byte)~x).ToArray(), "a Teredo address");
        }
        return null;
    }

    private static string? Embedded(byte[] v4, string kind) =>
        Blocked(new IPAddress(v4)) is { } why ? $"{kind} of {why}" : null;

    private static bool Matches(byte[] address, byte[] prefix, int bits)
    {
        for (var i = 0; i < bits; i++)
        {
            var mask = (byte)(0x80 >> (i % 8));
            if ((address[i / 8] & mask) != (prefix[i / 8] & mask))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The address to connect to for <paramref name="host"/> (sc-26232): every address <paramref name="resolver"/> answers must be
    /// allowed (<see cref="Blocked"/>) — one blocked address refuses the host, so a name that answers both a public and a private
    /// address cannot be used to reach the private one — and the first is returned, to be connected to as it is (pinned: no second
    /// lookup between the check and the connection, so a rebinding answer changes nothing). A host that is an IP literal is judged
    /// as that address.
    /// </summary>
    public static async Task<IPAddress> ChooseAsync(string host, IArtifactResolver resolver, CancellationToken ct)
    {
        var literal = host.Trim('[', ']');
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(literal, out var ip) ? [ip] : await resolver.ResolveAsync(host, ct);
        if (addresses.Count == 0)
        {
            throw new ArtifactAddressException($"{host} resolves to no address");
        }
        foreach (var address in addresses)
        {
            if (Blocked(address) is { } why)
            {
                throw new ArtifactAddressException($"{host} resolves to {address}, {why}: artifacts are downloaded from public addresses only");
            }
        }
        return addresses[0];
    }
}

/// <summary>The rules a recipe artifact's URL must keep before anything is resolved (sc-26232).</summary>
public static class ArtifactUrls
{
    /// <summary>
    /// Why <paramref name="url"/> may not be downloaded, as the rest of a sentence about it, or null: https only, no user
    /// information, no fragment, a host name (an IP literal must be a public address, <see cref="ArtifactAddresses.Blocked"/>;
    /// <c>localhost</c> and <c>*.localhost</c> are refused by name).
    /// </summary>
    public static string? Problem(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return "must be an https URL";
        }
        if (url.UserInfo.Length > 0)
        {
            return "may not carry a user name or password";
        }
        if (url.Fragment.Length > 0)
        {
            return "may not carry a fragment";
        }
        if (url.Host.Length == 0)
        {
            return "has no host";
        }
        var host = url.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "names the local host";
        }
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip) && ArtifactAddresses.Blocked(ip) is { } why)
        {
            return $"names {ip}, {why}";
        }
        return null;
    }
}

/// <summary>
/// The HTTP transport of the artifact downloads (sc-26232), the only one: <see cref="OutboundHttp.ArtifactDownload"/> builds it.
/// Every connection is made to the address <see cref="ArtifactAddresses.ChooseAsync"/> checked in the same callback (the host's
/// every resolved address must be public; the one connected to is the one checked), no proxy is used (an environment proxy would
/// connect elsewhere), no redirect is followed by the handler (the downloader follows a bounded number itself, checking each), and
/// nothing is decompressed (the hash is of the bytes as served).
/// </summary>
internal static class ArtifactTransport
{
    public static SocketsHttpHandler Handler(IArtifactResolver resolver) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var address = await ArtifactAddresses.ChooseAsync(context.DnsEndPoint.Host, resolver, ct);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
