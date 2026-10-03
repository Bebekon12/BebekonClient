using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bebekon.Core;

public sealed record LocalAddressSnapshot(string? Address, bool Private, string Detail);
public static class NetworkAddresses
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }
    public static LocalAddressSnapshot Read()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Where(n => n.Name != "Bebekon" && !new[] { "tun", "tap", "vpn", "virtual", "hyper-v", "loopback", "wireguard", "zerotier", "tailscale", "hamachi" }.Any(word => (n.Name + " " + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(n => (Network: n, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .SelectMany(n => n.Properties.UnicastAddresses.Select(a => (n.Network, a.Address)))
                .Where(n => !IPAddress.IsLoopback(n.Address) && !n.Address.IsIPv6LinkLocal)
                .OrderBy(n => n.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ThenBy(n => n.Network.Id);
            var selected = candidates.FirstOrDefault();
            if (selected.Address is not null)
                return new(selected.Address.ToString(), IsPrivate(selected.Address), selected.Network.Name + " · " + (selected.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6") + "\n" + string.Join("\n", candidates.Select(n => n.Address.ToString()).Distinct()));
        }
        catch (NetworkInformationException) { }
        return new(null, false, "—");
    }
}
