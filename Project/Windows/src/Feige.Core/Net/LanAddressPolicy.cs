using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Feige.Core.Net;

/// <summary>The same destination policy applies to advertised addresses and every direct TCP attempt.</summary>
public static class LanAddressPolicy
{
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
              bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    public static void Require(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsLocal(endpoint.Address) || endpoint.Port == 0)
            throw new ArgumentException("仅支持局域网、链路本地或环回地址，不接受公网地址和域名。");
        if (endpoint.Address.IsIPv6LinkLocal && endpoint.Address.ScopeId == 0)
            throw new ArgumentException("IPv6 链路本地地址需要本机网卡编号，例如 fe80::1234%12；也可使用对方的局域网 IPv4 地址。");
    }

    public static bool IsSameSubnet(IPAddress address, IPAddress local, int prefixLength)
    {
        if (address.AddressFamily != local.AddressFamily) return false;
        if (address.IsIPv6LinkLocal && address.ScopeId != 0 && local.ScopeId != address.ScopeId) return false;
        var a = address.GetAddressBytes(); var b = local.GetAddressBytes();
        if (prefixLength <= 0 || prefixLength > a.Length * 8) return false;
        int whole = prefixLength / 8, remaining = prefixLength % 8;
        return a.AsSpan(0, whole).SequenceEqual(b.AsSpan(0, whole)) &&
            (remaining == 0 || (a[whole] & (0xff << (8 - remaining))) == (b[whole] & (0xff << (8 - remaining))));
    }

    public static IReadOnlyList<(IPAddress Address, int PrefixLength)> LocalSubnets()
    {
        var result = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    foreach (var item in nic.GetIPProperties().UnicastAddresses)
                        if (IsLocal(item.Address) && !IPAddress.IsLoopback(item.Address)) result.Add((item.Address, item.PrefixLength));
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }
}
