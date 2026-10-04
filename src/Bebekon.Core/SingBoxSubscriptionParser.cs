using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bebekon.Core;

/// <summary>Whitelist outbound connection settings; provider config is never executed.</summary>
internal static class SingBoxSubscriptionParser
{
    internal static List<Server> Parse(JsonElement root)
    {
        CheckDuplicates(root);
        var nodes = P(root, "outbounds");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 5000) throw Invalid();
        var result = new List<Server>();
        foreach (var node in nodes.EnumerateArray())
        {
            var type = T(node, "type");
            if (type is "direct" or "block" or "dns" or "selector" or "urltest") continue;
            var tls = P(node, "tls"); var reality = P(tls, "reality"); var transport = P(node, "transport"); var headers = P(transport, "headers");
            var s = new Server { Type = type, Name = T(node, "tag"), Host = T(node, "server"), Port = N(node, "server_port", 443), Uuid = T(node, "uuid"), Password = T(node, "password", T(node, "auth_str")), Cipher = T(node, type == "vmess" ? "security" : "method", "auto"), AlterId = N(node, "alter_id"), GlobalPadding = B(node, "global_padding"), AuthenticatedLength = B(node, "authenticated_length"), Plugin = T(node, "plugin"), PluginOptions = T(node, "plugin_opts"), Security = B(reality, "enabled") ? "reality" : B(tls, "enabled") ? "tls" : "none", Sni = T(tls, "server_name"), Fingerprint = B(P(tls, "utls"), "enabled") ? T(P(tls, "utls"), "fingerprint", "chrome") : "", TlsInsecure = B(tls, "insecure"), PublicKey = T(reality, "public_key"), ShortId = T(reality, "short_id"), Alpn = A(tls, "alpn"), Flow = T(node, "flow"), PacketEncoding = T(node, "packet_encoding", type == "vless" ? "xudp" : "none"), UdpEnabled = T(node, "network") != "tcp", Transport = T(transport, "type", "tcp"), Path = T(transport, "path", "/"), ServiceName = T(transport, "service_name"), UpMbps = N(node, "up_mbps"), DownMbps = N(node, "down_mbps"), ServerPorts = A(node, "server_ports") };
            if (s.PacketEncoding.Length == 0) s.PacketEncoding = "none";
            if (s.Name.Length == 0) s.Name = s.Host;
            var host = P(transport, "host"); s.TransportHost = s.Transport == "ws" ? T(headers, "Host", T(headers, "host")) : host.ValueKind == JsonValueKind.Array ? A(transport, "host").FirstOrDefault() ?? "" : T(transport, "host");
            if (type == "hysteria") { s.ObfsPassword = T(node, "obfs"); if (P(node, "auth").ValueKind != JsonValueKind.Undefined) s.Password = ProtocolParser.Decode(T(node, "auth")); }
            if (type == "hysteria2") { s.Obfs = T(P(node, "obfs"), "type"); s.ObfsPassword = T(P(node, "obfs"), "password"); }
            var hop = T(node, "hop_interval", "30s"); if (!hop.EndsWith('s') || !int.TryParse(hop[..^1], out var seconds)) throw Invalid(); s.HopIntervalSeconds = seconds;
            var allowed = new HashSet<string> { "type", "tag", "server", "server_port", "server_ports", "hop_interval", "uuid", "password", "auth", "auth_str", "method", "security", "alter_id", "global_padding", "authenticated_length", "plugin", "plugin_opts", "tls", "transport", "flow", "packet_encoding", "network", "up_mbps", "down_mbps", "obfs", "connect_timeout", "domain_resolver" };
            if (type is not ("vless" or "vmess" or "shadowsocks" or "trojan" or "hysteria" or "hysteria2")) s.UnsupportedReason = "Этот VPN-протокол sing-box пока не поддерживается: " + type;
            else if (node.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || Extra(tls, "enabled", "server_name", "insecure", "alpn", "utls", "reality") || Extra(transport, "type", "path", "host", "headers", "service_name") || Extra(headers, "Host", "host") || Extra(reality, "enabled", "public_key", "short_id") || Extra(P(tls, "utls"), "enabled", "fingerprint") || type == "hysteria2" && Extra(P(node, "obfs"), "type", "password") || host.ValueKind == JsonValueKind.Array && host.GetArrayLength() > 1) s.UnsupportedReason = "Дополнительные параметры этого outbound sing-box пока не поддерживаются.";
            if (s.Supported) ProtocolConfig.Validate(s);
            s.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Name + ServerRefresh.ConnectionKey(s))))[..24]; result.Add(s);
        }
        if (result.Count == 0) throw new UserError("В JSON sing-box не найдено VPN-серверов.");
        return result.DistinctBy(s => s.Id).ToList();
    }
    private static bool Extra(JsonElement e, params string[] names) => e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any(p => !names.Contains(p.Name));
    private static JsonElement P(JsonElement e, string name) { if (e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default; if (e.ValueKind != JsonValueKind.Object) throw Invalid(); return e.TryGetProperty(name, out var v) ? v : default; }
    private static string T(JsonElement e, string name, string fallback = "") { var v = P(e, name); if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fallback; if (v.ValueKind != JsonValueKind.String) throw Invalid(); return v.GetString()!; }
    private static int N(JsonElement e, string name, int fallback = 0) { var v = P(e, name); if (v.ValueKind == JsonValueKind.Undefined) return fallback; if (!v.TryGetInt32(out var n)) throw Invalid(); return n; }
    private static bool B(JsonElement e, string name) { var v = P(e, name); if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False) return false; if (v.ValueKind != JsonValueKind.True) throw Invalid(); return true; }
    private static List<string> A(JsonElement e, string name) { var v = P(e, name); if (v.ValueKind == JsonValueKind.Undefined) return []; if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw Invalid(); return v.EnumerateArray().Select(x => x.GetString()!).ToList(); }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(); foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw Invalid(); CheckDuplicates(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) CheckDuplicates(item);
    }
    private static UserError Invalid() => new("Повреждённая JSON-подписка sing-box.");
}
