using System.Net;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

internal static class ProtocolConfig
{
    private static readonly HashSet<string> Ciphers = new(StringComparer.Ordinal) { "none", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305", "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb", "rc4-md5", "chacha20-ietf", "xchacha20" };
    internal static void Validate(Server s)
    {
        if (!s.Supported) throw new UserError(s.UnsupportedReason!);
        if (s.Type == "trusttunnel") { TrustTunnelConfig.Validate(s); return; }
        if (s.Type is not ("vless" or "vmess" or "shadowsocks" or "trojan" or "hysteria" or "hysteria2") || s.Port is < 1 or > 65535 || Uri.CheckHostName(s.Host) == UriHostNameType.Unknown
            || IPAddress.TryParse(s.Host, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            || s.Security is not ("none" or "tls" or "reality") || s.Transport is not ("tcp" or "grpc" or "ws" or "http" or "httpupgrade" or "xhttp")
            || s.PacketEncoding is not ("none" or "xudp" or "packetaddr") || s.Alpn.Count > 16 || s.Alpn.Any(v => !Safe(v, 128))
            || !Safe(s.Sni, 253) || !Safe(s.Path, 4096) || !Safe(s.ServiceName, 1024) || !Safe(s.TransportHost, 253) || !Safe(s.Fingerprint, 64)
            || !Safe(s.Password, 4096) || !Safe(s.PluginOptions, 4096) || !Safe(s.ObfsPassword, 4096)) throw Invalid();
        if (s.Type is "vless" or "vmess" && !Guid.TryParse(s.Uuid, out _)) throw new UserError("Некорректный UUID сервера.");
        if (s.Type == "vless" && (s.Flow is not ("" or "xtls-rprx-vision") || s.Flow.Length > 0 && (s.Transport != "tcp" || s.Security == "none"))) throw Invalid();
        if (s.Type == "vmess" && (s.Cipher is not ("auto" or "none" or "zero" or "aes-128-gcm" or "chacha20-poly1305" or "aes-128-ctr") || s.AlterId is < 0 or > 65535 || s.Security == "reality")) throw Invalid();
        if (s.Type == "trojan" && (s.Password.Length == 0 || s.Security == "reality")) throw Invalid();
        if (s.Type == "shadowsocks")
        {
            if (!Ciphers.Contains(s.Cipher) || s.Password.Length == 0 && s.Cipher != "none" || s.Plugin is not ("" or "obfs-local" or "v2ray-plugin") || s.Security != "none" || s.Transport != "tcp") throw Invalid();
            if (s.Cipher.StartsWith("2022-", StringComparison.Ordinal))
            { try { foreach (var key in s.Password.Split(':')) if (Convert.FromBase64String(key).Length != (s.Cipher == "2022-blake3-aes-128-gcm" ? 16 : 32)) throw Invalid(); } catch (FormatException) { throw Invalid(); } }
        }
        if (s.Type is "hysteria" or "hysteria2")
        {
            if (s.Security != "tls" || s.Transport != "tcp" || s.Password.Length == 0 || s.UpMbps is < 0 or > 1_000_000 || s.DownMbps is < 0 or > 1_000_000 || s.HopIntervalSeconds is < 1 or > 86400 || s.ServerPorts.Count > 64) throw Invalid();
            if (s.ServerPorts.Count > 0) ProtocolParser.PortRanges(string.Join(',', s.ServerPorts));
            if (s.Type == "hysteria" && (s.UpMbps == 0 || s.DownMbps == 0) || s.Type == "hysteria2" && (s.Obfs is not ("" or "salamander" or "gecko") || s.Obfs.Length > 0 && s.ObfsPassword.Length == 0)) throw Invalid();
        }
        if (s.Security == "reality")
        {
            try { if (Convert.FromBase64String(s.PublicKey.Replace('-', '+').Replace('_', '/').PadRight(44, '=')).Length != 32) throw Invalid(); } catch (FormatException) { throw Invalid(); }
            if (s.Sni.Length == 0 || s.ShortId.Length > 16 || s.ShortId.Length % 2 != 0 || s.ShortId.Any(c => !Uri.IsHexDigit(c))) throw Invalid();
        }
        if (s.Transport == "xhttp") { if (s.Type != "vless" || s.TlsInsecure || !Safe(s.VerifyCertificateName, 253) || s.CertificatePin.Length > 0 && (s.Security != "tls" || s.CertificatePin.Length != 64 || s.CertificatePin.Any(c => !Uri.IsHexDigit(c)))) throw Invalid(); XhttpConfig.Options(s); }
    }
    private static bool Safe(string text, int max) => text.Length <= max && !text.Any(char.IsControl);
    private static UserError Invalid() => new("Некорректные или неподдерживаемые параметры VPN-сервера.");
    internal static JsonObject Outbound(Server s, string tag)
    {
        Validate(s);
        var vpn = new JsonObject { ["type"] = s.Type, ["tag"] = tag, ["server"] = s.Host, ["server_port"] = s.Port, ["domain_resolver"] = "direct-dns", ["connect_timeout"] = "4s" };
        if (!s.UdpEnabled) vpn["network"] = "tcp";
        if (s.Type is "vless" or "vmess") { vpn["uuid"] = s.Uuid; vpn["packet_encoding"] = s.PacketEncoding == "none" ? "" : s.PacketEncoding; }
        if (s.Type == "vless" && s.Flow.Length > 0) vpn["flow"] = s.Flow;
        if (s.Type == "vmess") { vpn["security"] = s.Cipher; vpn["alter_id"] = s.AlterId; vpn["global_padding"] = s.GlobalPadding; vpn["authenticated_length"] = s.AuthenticatedLength; }
        if (s.Type is "shadowsocks" or "trojan" or "hysteria2") vpn["password"] = s.Password;
        if (s.Type == "shadowsocks") { vpn["method"] = s.Cipher; if (s.Plugin.Length > 0) { vpn["plugin"] = s.Plugin; vpn["plugin_opts"] = s.PluginOptions; } }
        if (s.Type is "hysteria" or "hysteria2")
        {
            if (s.UpMbps > 0) vpn["up_mbps"] = s.UpMbps; if (s.DownMbps > 0) vpn["down_mbps"] = s.DownMbps;
            if (s.ServerPorts.Count > 0) { vpn.Remove("server_port"); vpn["server_ports"] = new JsonArray(s.ServerPorts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()); vpn["hop_interval"] = s.HopIntervalSeconds + "s"; }
            if (s.Type == "hysteria") { vpn["auth_str"] = s.Password; if (s.ObfsPassword.Length > 0) vpn["obfs"] = s.ObfsPassword; }
            else if (s.Obfs.Length > 0) vpn["obfs"] = new JsonObject { ["type"] = s.Obfs, ["password"] = s.ObfsPassword };
        }
        if (s.Security != "none")
        {
            var tls = new JsonObject { ["enabled"] = true, ["server_name"] = s.Sni.Length > 0 ? s.Sni : s.Host, ["insecure"] = s.TlsInsecure };
            if (s.Alpn.Count > 0) tls["alpn"] = new JsonArray(s.Alpn.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            if (s.Fingerprint.Length > 0 && s.Type is not ("hysteria" or "hysteria2")) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = s.Fingerprint };
            if (s.Security == "reality") tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = s.PublicKey, ["short_id"] = s.ShortId };
            vpn["tls"] = tls;
        }
        if (s.Transport != "tcp")
        {
            var t = new JsonObject { ["type"] = s.Transport };
            if (s.Transport == "grpc") t["service_name"] = s.ServiceName;
            else { t["path"] = s.Path; if (s.TransportHost.Length > 0) { if (s.Transport == "ws") t["headers"] = new JsonObject { ["Host"] = s.TransportHost }; else if (s.Transport == "http") t["host"] = new JsonArray(s.TransportHost); else t["host"] = s.TransportHost; } }
            vpn["transport"] = t;
        }
        return vpn;
    }
}
