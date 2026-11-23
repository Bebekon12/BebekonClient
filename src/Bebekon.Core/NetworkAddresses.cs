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
    /// <summary>
    /// True when a physical uplink has a global IPv6 address and an IPv6 gateway. Without that,
    /// IPv6 offered by the tunnel would only lead to connections the direct route cannot complete.
    /// </summary>
    public static bool HasIPv6Uplink()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up && n.Name != "Bebekon" &&
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                n.GetIPProperties() is var properties &&
                properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6 && !g.Address.Equals(IPAddress.IPv6Any)) &&
                properties.UnicastAddresses.Any(a => IsGlobalIPv6(a.Address)));
        }
        catch (NetworkInformationException) { return true; }
    }
    /// <summary>Global unicast 2000::/3, excluding Teredo (2001::/32) and 6to4 (2002::/16) relays.</summary>
    public static bool IsGlobalIPv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var b = address.GetAddressBytes();
        return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) && !(b[0] == 0x20 && b[1] == 0x02);
    }
    public static LocalAddressSnapshot Read()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Where(n => n.Name != "Bebekon" && !new[] { "tun", "tap", "vpn", "loopback", "wireguard", "zerotier", "tailscale", "hamachi" }.Any(word => (n.Name + " " + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Select(n => (Network: n, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .SelectMany(n => n.Properties.UnicastAddresses.Select(a => (n.Network, a.Address)))
                .Where(n => !IPAddress.IsLoopback(n.Address) && !n.Address.IsIPv6LinkLocal)
                // A Hyper-V/WSL external switch moves the Wi-Fi address to a "vEthernet" adapter.
                // Prefer real adapters, but keep such a switch as the uplink when it is the only one.
                .OrderBy(n => new[] { "virtual", "hyper-v", "vethernet" }.Any(word => (n.Network.Name + " " + n.Network.Description).Contains(word, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
                .ThenBy(n => n.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ThenBy(n => n.Network.Id);
            var selected = candidates.FirstOrDefault();
            if (selected.Address is not null)
                return new(selected.Address.ToString(), IsPrivate(selected.Address), selected.Network.Name + " · " + (selected.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6") + "\n" + string.Join("\n", candidates.Select(n => n.Address.ToString()).Distinct()));
        }
        catch (NetworkInformationException) { }
        return new(null, false, "—");
    }
}
