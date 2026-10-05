using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bebekon.Core;

internal static class TcpLatency
{
    public static async Task<long> MeasureAsync(string host, int port, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        var local = NetworkAddresses.Read().Address;
        var physical = local is null ? null : NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == local));
        var times = new List<long>();
        for (var sample = 0; sample < 3; sample++) {
            Exception? failure = null;
            foreach (var destination in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)) {
                try {
                    using var tcp = new TcpClient(destination.AddressFamily);
                    if (!IPAddress.IsLoopback(destination)) {
                        // A TUN's local TCP stack can acknowledge SYN immediately. Bind
                        // only this probe to the physical uplink to measure the remote port.
                        var properties = physical?.GetIPProperties() ?? throw new SocketException((int)SocketError.NetworkUnreachable);
                        var source = properties.UnicastAddresses.Select(a => a.Address)
                            .FirstOrDefault(a => a.AddressFamily == destination.AddressFamily && !a.IsIPv6LinkLocal)
                            ?? throw new SocketException((int)SocketError.NetworkUnreachable);
                        var index = destination.AddressFamily == AddressFamily.InterNetwork ? IPAddress.HostToNetworkOrder(properties.GetIPv4Properties().Index) : properties.GetIPv6Properties().Index;
                        // Winsock IP_UNICAST_IF / IPV6_UNICAST_IF = 31.
                        tcp.Client.SetSocketOption(destination.AddressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, (SocketOptionName)31, index);
                        tcp.Client.Bind(new IPEndPoint(source, 0));
                    }
                    var clock = Stopwatch.StartNew();
                    await tcp.ConnectAsync(destination, port, token);
                    times.Add(clock.ElapsedMilliseconds); failure = null; break;
                } catch (Exception e) when (e is SocketException or NetworkInformationException) { failure = e; }
            }
            if (failure is not null) throw failure;
            if (times.Count != sample + 1) throw new SocketException((int)SocketError.HostNotFound);
        }
        times.Sort(); return times[1];
    }
}
