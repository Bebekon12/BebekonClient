using System.Text.Json;

namespace Bebekon.Core;

public static class ServerRefresh
{
    public static string ConnectionKey(Server s) => JsonSerializer.Serialize(new {
        s.Type, s.Password, s.Cipher, s.AlterId, s.GlobalPadding, s.AuthenticatedLength, s.Plugin, s.PluginOptions, s.Obfs, s.ObfsPassword, s.UpMbps, s.DownMbps, s.ServerPorts, s.HopIntervalSeconds, s.TlsInsecure, s.CertificatePin, s.VerifyCertificateName, s.XhttpMode, s.XhttpExtra,
        s.Host, s.Port, s.Uuid, s.Security, s.Sni, s.PublicKey, s.ShortId, s.Fingerprint,
        s.Alpn, s.Flow, s.PacketEncoding, s.UdpEnabled, s.Transport, s.ServiceName, s.Path, s.TransportHost, s.UnsupportedReason
    });

    public static List<Server> Merge(string subscriptionId, IEnumerable<Server> previous,
        IEnumerable<Server> incoming, string? selectedId)
    {
        var available = previous.Where(s => s.SubscriptionId == subscriptionId).ToList();
        var result = new List<Server>();
        foreach (var node in incoming)
        {
            var old = available.FirstOrDefault(s => s.Id == node.Id)
                ?? available.FirstOrDefault(s => s.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase) && ConnectionKey(s) == ConnectionKey(node))
                ?? available.FirstOrDefault(s => ConnectionKey(s) == ConnectionKey(node))
                ?? available.FirstOrDefault(s => s.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase));
            node.SubscriptionId = subscriptionId;
            if (old is not null)
            {
                available.Remove(old); node.Id = old.Id; node.Favorite = old.Favorite;
                if (ConnectionKey(old) == ConnectionKey(node)) { node.Latency = old.Latency; node.LatencyMs = old.LatencyMs; }
            }
            else node.Id = subscriptionId + ":" + node.Id;
            result.Add(node);
        }
        // Never silently choose a different server when the provider removes the selection.
        if (available.FirstOrDefault(s => s.Id == selectedId) is { } selected) result.Add(selected);
        return result;
    }
}
