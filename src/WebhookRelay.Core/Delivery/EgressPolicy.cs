using System.Net;
using System.Net.Sockets;

namespace WebhookRelay.Core.Delivery;

/// <summary>
/// Server-side request forgery guard. Endpoint URLs are supplied by tenants, so without this the relay would happily
/// POST to 169.254.169.254 (cloud metadata), localhost admin ports or the internal network. The check runs on the
/// resolved IP at connect time, not on the hostname, so DNS rebinding cannot slip past it.
/// </summary>
public static class EgressPolicy
{
    public static bool IsPublicUnicast(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false,
        };
    }

    private static bool IsPublicV4(byte[] b) => !(
        b[0] == 0 ||                                   // 0.0.0.0/8 "this network"
        b[0] == 10 ||                                  // 10.0.0.0/8 private
        b[0] == 127 ||                                 // loopback
        (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // 100.64.0.0/10 carrier-grade NAT
        (b[0] == 169 && b[1] == 254) ||                // link-local, includes cloud metadata 169.254.169.254
        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||   // 172.16.0.0/12 private
        (b[0] == 192 && b[1] == 0 && b[2] == 0) ||     // 192.0.0.0/24 IETF protocol assignments
        (b[0] == 192 && b[1] == 168) ||                // 192.168.0.0/16 private
        (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || // 198.18.0.0/15 benchmarking
        b[0] >= 224);                                  // multicast, reserved, broadcast

    private static bool IsPublicV6(IPAddress address)
    {
        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        // 64:ff9b::/96 NAT64 and 2002::/16 6to4 embed an IPv4 address; judge the embedded one.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
        {
            return IsPublicV4(b[12..16]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(b[2..6]);
        }

        // Only global unicast (2000::/3) is public; this also rejects ::, ::1 and the deprecated IPv4-compatible range.
        return (b[0] & 0xE0) == 0x20;
    }
}
