using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Bebekon.Core;

public sealed record NetBirdProbeResult(string InterfaceName, IPAddress? Resolver, IPAddress? Reached, int Port);

/// <summary>Probes a confirmed corporate path by binding both DNS and TCP to the NetBird interface.</summary>
public static class NetBirdProbe
{
    public static Task<NetBirdProbeResult> VerifyAsync(NetBirdRouting routing, RouteQuery target, CancellationToken ct)
        => VerifyAsync(routing, target, 53, ct);

    internal static async Task<NetBirdProbeResult> VerifyAsync(NetBirdRouting routing, RouteQuery target, int dnsPort, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var port = target.Port ?? 443;
        IPAddress? resolver = null;
        IPAddress[] addresses;
        if (target.Address is { } address) addresses = [address];
        else
        {
            var domain = target.Domain ?? throw new UserError("Укажите корпоративный домен или IP.");
            var zone = routing.ZoneFor(domain);
            var server = zone is null ? routing.EffectiveDefaultResolver : zone.Resolver;
            if (!IPAddress.TryParse(server, out resolver) || !UsesNetBird(routing, resolver))
                throw new UserError("Для этого домена не подтверждён разрешённый DNS-сервер на интерфейсе NetBird.");
            var responses = await Task.WhenAll(QueryAsync(routing, resolver, dnsPort, domain, 1, token),
                QueryAsync(routing, resolver, dnsPort, domain, 28, token));
            addresses = responses.SelectMany(result => result).Distinct().ToArray();
        }

        var verifiedResolver = addresses.Length > 0 ? resolver : null;
        foreach (var ip in addresses.Where(address => UsesNetBird(routing, address)).Take(4))
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = Bind(routing, ip, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), attempt.Token);
                if (UsesNetBird(routing, ip)) return new(routing.InterfaceName, verifiedResolver, ip, port);
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
        }
        return new(routing.InterfaceName, verifiedResolver, null, port);
    }

    private static async Task<IPAddress[]> QueryAsync(NetBirdRouting routing, IPAddress resolver, int port, string domain, ushort type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        var request = CreateQuery(domain, type);
        var endpoint = new IPEndPoint(resolver, port);
        try
        {
            using var socket = Bind(routing, resolver, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(endpoint, token);
            await socket.SendAsync(request, SocketFlags.None, token);
            var buffer = new byte[4096];
            var length = await socket.ReceiveAsync(buffer, SocketFlags.None, token);
            var response = buffer.AsMemory(0, length);
            if (IsTruncated(response.Span, request))
            {
                using var tcp = Bind(routing, resolver, SocketType.Stream, ProtocolType.Tcp);
                await tcp.ConnectAsync(endpoint, token);
                using var stream = new NetworkStream(tcp);
                var prefix = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)request.Length);
                await stream.WriteAsync(prefix, token);
                await stream.WriteAsync(request, token);
                await stream.ReadExactlyAsync(prefix, token);
                var reply = new byte[Read16(prefix, 0)];
                await stream.ReadExactlyAsync(reply, token);
                response = reply;
            }
            return ReadAnswer(response.Span, request, type);
        }
        catch (Exception ex) when (ex is SocketException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static bool UsesNetBird(NetBirdRouting routing, IPAddress address) =>
        IndexFor(routing, address) is { } index && WindowsRoutes.BestInterfaceIndex(address) == index;

    private static int? IndexFor(NetBirdRouting routing, IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? routing.IPv4Index : routing.IPv6Index;

    private static Socket Bind(NetBirdRouting routing, IPAddress address, SocketType type, ProtocolType protocol)
    {
        if (IndexFor(routing, address) is not { } index || WindowsRoutes.BestInterfaceIndex(address) != index)
            throw new UserError("Маршрут через интерфейс NetBird изменился. Повторите проверку.");
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        var socket = new Socket(address.AddressFamily, type, protocol);
        try
        {
            const SocketOptionName unicastInterface = (SocketOptionName)31; // IP_UNICAST_IF / IPV6_UNICAST_IF
            socket.SetSocketOption(v4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, unicastInterface,
                v4 ? IPAddress.HostToNetworkOrder(index) : index);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    internal static byte[] CreateQuery(string domain, ushort type)
    {
        var labels = domain.TrimEnd('.').Split('.');
        if (domain.Length > 253 || labels.Any(label => label.Length is 0 or > 63 ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new UserError("Укажите корректное полное доменное имя.");
        var query = new byte[12 + domain.TrimEnd('.').Length + 6];
        BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)RandomNumberGenerator.GetInt32(65536));
        query[2] = 1; // recursion desired
        query[5] = 1; // one question
        var offset = 12;
        foreach (var label in labels)
        {
            query[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, query.AsSpan(offset));
        }
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(++offset), type);
        query[offset + 3] = 1; // IN
        return query;
    }

    private static bool IsTruncated(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request) =>
        response.Length >= 12 && Read16(response, 0) == Read16(request, 0) && (Read16(response, 2) & 0x8200) == 0x8200;

    internal static IPAddress[] ReadAnswer(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request, ushort type)
    {
        if (response.Length < request.Length || Read16(response, 0) != Read16(request, 0) ||
            (Read16(response, 2) & 0xFA0F) != 0x8000 || Read16(response, 4) != 1 ||
            !response[12..request.Length].SequenceEqual(request[12..])) return [];
        var offset = request.Length;
        var results = new List<IPAddress>();
        var count = Read16(response, 6);
        for (var i = 0; i < count; i++)
        {
            if (!SkipName(response, ref offset) || response.Length - offset < 10) return [];
            var kind = Read16(response, offset);
            var @class = Read16(response, offset + 2);
            var length = Read16(response, offset + 8);
            offset += 10;
            if (length > response.Length - offset) return [];
            if (@class == 1 && kind == type && length == (type == 1 ? 4 : 16))
                results.Add(new IPAddress(response.Slice(offset, length)));
            offset += length;
        }
        return results.ToArray();
    }

    private static bool SkipName(ReadOnlySpan<byte> response, ref int offset)
    {
        while (offset < response.Length)
        {
            var length = response[offset++];
            if (length == 0) return true;
            if ((length & 0xc0) == 0xc0)
                return offset < response.Length && ((length & 0x3f) << 8 | response[offset++]) < response.Length;
            if (length > 63 || length > response.Length - offset) return false;
            offset += length;
        }
        return false;
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
}
