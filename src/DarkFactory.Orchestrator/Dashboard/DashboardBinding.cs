using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// Where the <c>factory work</c> host listens (E8): 127.0.0.1 always, plus at most one configured
/// private-network address. Never a wildcard, a public address, or an address this machine doesn't have.
/// </summary>
public static class DashboardBinding
{
    /// <summary>
    /// The addresses to listen on. Throws <see cref="InvalidOperationException"/> for a configured
    /// address that is not a private (RFC 1918, CGNAT/Tailscale 100.64/10, ULA fc00::/7) unicast
    /// address assigned to a local interface.
    /// </summary>
    public static IReadOnlyList<IPAddress> Addresses(string? configured, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        if (configured is null)
        {
            return [IPAddress.Loopback];
        }
        if (!IPAddress.TryParse(configured, out var address))
        {
            throw Refuse(configured, "not an IP address");
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw Refuse(configured, "a wildcard would listen on every interface");
        }
        if (IPAddress.IsLoopback(address))
        {
            throw Refuse(configured, "loopback is always bound; set a private-network address or leave it unset");
        }
        if (!IsPrivate(address))
        {
            throw Refuse(configured, "only private-network addresses are allowed (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7)");
        }
        if (!(localAddresses ?? LocalAddresses)().Any(a => a.Equals(address)))
        {
            throw Refuse(configured, "no local interface has this address");
        }
        return [IPAddress.Loopback, address];
    }

    /// <summary>
    /// <c>Dashboard:HostName</c> as host filtering will match it: one plain DNS name, never a pattern.
    /// Throws <see cref="InvalidOperationException"/> for a wildcard (<c>*</c>, <c>*.example.com</c>, which
    /// would widen host filtering and with it the DNS-rebinding guard), a port, a path, whitespace, or
    /// anything that is not a DNS host name (an IP address belongs in <c>Dashboard:BindAddress</c>).
    /// </summary>
    public static string? ValidHostName(string? configured)
    {
        if (configured is null)
        {
            return null;
        }
        if (configured.Any(c => c is '*' or ':' or '/' || char.IsWhiteSpace(c)))
        {
            throw RefuseHostName(configured, "no wildcards, ports, paths or whitespace");
        }
        if (Uri.CheckHostName(configured) != UriHostNameType.Dns)
        {
            throw RefuseHostName(configured, "not a DNS host name");
        }
        return configured;
    }

    public static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127), // CGNAT, which Tailscale uses
            AddressFamily.InterNetworkV6 => (b[0] & 0xFE) == 0xFC, // unique local, incl. Tailscale's fd7a:115c:a1e0::/48
            _ => false,
        };
    }

    /// <summary>The Host header value for an address (IPv6 in brackets), as host filtering compares it.</summary>
    public static string HostName(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    public static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address);

    private static InvalidOperationException Refuse(string configured, string why) =>
        new($"Dashboard:BindAddress '{configured}' refused: {why}.");

    private static InvalidOperationException RefuseHostName(string configured, string why) =>
        new($"Dashboard:HostName '{configured}' refused: {why}.");
}
