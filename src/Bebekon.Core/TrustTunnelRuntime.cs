using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

internal sealed record TrustTunnelRelay(string Tag, int Port, string Host, int RemotePort);
internal sealed record TrustTunnelNode(Server Server, LoopbackBridge Socks, IReadOnlyList<TrustTunnelRelay> Relays);

internal sealed class TrustTunnelRuntime
{
    internal IReadOnlyDictionary<string, TrustTunnelNode> Nodes { get; }
    private TrustTunnelRuntime(Dictionary<string, TrustTunnelNode> nodes) => Nodes = nodes;
    internal static TrustTunnelRuntime? Create(ConnectSpec spec, bool probeOnly, XhttpRuntime? xhttp)
    {
        var nodes = new List<Server> { spec.Server };
        if (!probeOnly)
            foreach (var id in spec.Profile.Rules.Where(r => r.UseVpn && r.ServerId is not null).Select(r => r.ServerId!).Distinct().Where(id => id != spec.Server.Id))
            { var matches = spec.RuleServers?.Where(s => s.Id == id).ToArray(); if (matches?.Length != 1) throw new UserError("Сервер правила недоступен."); nodes.Add(matches[0]); }
        var selected = nodes.Where(s => s.Type == "trusttunnel").ToArray(); if (selected.Length == 0) return null;
        if (selected.Length > 8) throw new UserError("В одном подключении можно использовать до 8 серверов TrustTunnel.");
        var used = new HashSet<int> { spec.ProbePort, spec.OriginProbePort, 17890 };
        if (xhttp is not null) { used.Add(xhttp.Direct.Port); foreach (var b in xhttp.Bridges.Values) used.Add(b.Port); }
        int Port() { int port; do { port = LatencyService.FreePort(); } while (!used.Add(port)); return port; }
        var result = new Dictionary<string, TrustTunnelNode>();
        foreach (var server in selected)
        {
            ProtocolConfig.Validate(server); var relays = new List<TrustTunnelRelay>();
            foreach (var address in server.TrustTunnel!.Addresses)
            { var remote = TrustTunnelConfig.Address(address); relays.Add(new("trusttunnel-relay-" + result.Count + "-" + relays.Count, Port(), remote.Host, remote.Port)); }
            result.Add(server.Id, new(server, new(Port(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24))), relays));
        }
        return new(result);
    }
    internal void AddRelays(JsonArray inbounds, JsonArray routes)
    {
        foreach (var relay in Nodes.Values.SelectMany(n => n.Relays))
        {
            // TLS/QUIC remains end-to-end. The raw TCP/UDP listener can reach only this fixed endpoint.
            inbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = relay.Tag, ["listen"] = "127.0.0.1", ["listen_port"] = relay.Port });
            // Must precede DNS interception, sniffing and every user rule: endpoint sockets never re-enter VPN.
            routes.Insert(0, new JsonObject { ["inbound"] = new JsonArray(relay.Tag), ["action"] = "route", ["outbound"] = "direct", ["override_address"] = relay.Host, ["override_port"] = relay.RemotePort, ["udp_connect"] = true, ["udp_timeout"] = "5m" });
        }
    }
}
