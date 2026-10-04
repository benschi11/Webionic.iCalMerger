using System.Net;
using System.Net.Sockets;

namespace Webionic.ICalMerger.Fetching;

public static class IpGuard
{
    /// <summary>True, wenn die Adresse ein öffentliches Ziel ist (kein Loopback, privat, Link-Local usw.).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 0
                 || b[0] == 10
                 || (b[0] == 100 && b[1] is >= 64 and <= 127)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || b[0] >= 224);
    }
}
