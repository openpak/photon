using System.Net;
using System.Net.Sockets;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// Determines whether a peer endpoint is on a trusted local network. Anonymous (tokenless)
/// authentication for the game's LAN mode is only ever permitted from loopback / private-range
/// addresses, never from the public internet.
/// </summary>
public static class TrustedNetwork
{
    public static bool IsLanOrLoopback(IPEndPoint? endpoint)
    {
        if (endpoint is null) return false;
        var ip = endpoint.Address;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 169 && b[1] == 254);                // 169.254.0.0/16 link-local
        }
        return false;
    }
}
