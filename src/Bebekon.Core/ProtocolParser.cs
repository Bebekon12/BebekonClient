using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>Connection data only; no provider listeners, routing, files or commands are imported.</summary>
public static class ProtocolParser
{
    public static bool IsLink(string text) => new[] { "vless", "vmess", "ss", "trojan", "hysteria", "hysteria2", "hy2", "tt" }.Any(p => text.StartsWith(p + "://", StringComparison.OrdinalIgnoreCase));
    public static Server Parse(string input)
    {
        input = input.Trim();
        if (input.StartsWith("tt://", StringComparison.OrdinalIgnoreCase)) return TrustTunnelParser.ParseLink(input);
        if (input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return VlessParser.Parse(input);
        try
        {
            Server server;
            if (input.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) && !input[8..].Contains('@')) server = VmessJson(input[8..]);
            else if (input.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) server = Shadowsocks(input);
            else server = UriServer(input);
            server.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24];
            if (server.Name.Length == 0) server.Name = server.Host;
            if (IPAddress.TryParse(server.Host, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
                server.UnsupportedReason = "Провайдер вернул запись без рабочего адреса сервера. Проверьте подписку и привязку HWID.";
            if (server.Supported) ProtocolConfig.Validate(server);
            return server;
        }
        catch (UserError) { throw; }
        catch (Exception e) when (e is FormatException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
        { throw new UserError("Повреждённая ссылка VPN: проверьте адрес, порт и параметры протокола."); }
    }
    internal static string Decode(string input)
    {
        var b = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b.PadRight((b.Length + 3) / 4 * 4, '=')));
    }
    internal static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        { var kv = pair.Split('=', 2); if (!result.TryAdd(Uri.UnescapeDataString(kv[0]), kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "")) throw new UserError("Ссылка содержит повторяющиеся параметры."); }
        return result;
    }
    private static Server UriServer(string input)
    {
        // Hysteria port hopping uses :443,5000-6000, which System.Uri cannot parse.
        List<string> ports = [];
        var schemeEnd = input.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw new UserError("Неизвестный VPN-протокол.");
        var scheme = input[..schemeEnd].ToLowerInvariant();
        if (scheme is "hy2" or "hysteria2" or "hysteria")
        {
            var end = input.IndexOfAny(['/', '?', '#'], schemeEnd + 3); if (end < 0) end = input.Length;
            var authority = input[(schemeEnd + 3)..end]; var colon = authority.LastIndexOf(':');
            if (colon > authority.LastIndexOf('@') && colon > authority.LastIndexOf(']'))
            {
                var list = authority[(colon + 1)..];
                if (list.Contains(',') || list.Contains('-'))
                { ports = PortRanges(list); var first = ports[0].Split(':')[0]; input = input[..(schemeEnd + 3 + colon + 1)] + first + input[end..]; }
            }
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || scheme is not ("vmess" or "trojan" or "hysteria" or "hysteria2" or "hy2")) throw new UserError("Неизвестный VPN-протокол.");
        var q = Query(uri); string Get(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback = 0) => q.ContainsKey(key) ? int.Parse(Get(key), CultureInfo.InvariantCulture) : fallback;
        var s = new Server { Type = scheme == "hy2" ? "hysteria2" : scheme, Host = uri.IdnHost.Trim('[', ']'), Port = uri.Port < 0 && scheme != "vmess" ? 443 : uri.Port, Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')), Password = Uri.UnescapeDataString(uri.UserInfo), Uuid = Uri.UnescapeDataString(uri.UserInfo), Security = Get("security", scheme == "vmess" ? "none" : "tls").ToLowerInvariant(), Sni = Get("sni", Get("peer", Get("serverName"))), Fingerprint = Get("fp", ""), Transport = Get("type", "tcp").ToLowerInvariant(), Path = Get("path", "/"), TransportHost = Get("host"), ServiceName = Get("serviceName"), Alpn = Get("alpn").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), Cipher = Get("scy", "auto"), AlterId = Number("alterId"), TlsInsecure = Get("insecure", Get("allowInsecure", "0")) is "1" or "true", UpMbps = Number("upmbps", Number("up")), DownMbps = Number("downmbps", Number("down")), ServerPorts = ports, HopIntervalSeconds = Number("hopInterval", 30) };
        if (s.Type == "hysteria") { s.Password = Get("auth", s.Password); s.ObfsPassword = Get("obfsParam", Get("obfs")); if (s.UpMbps == 0) s.UpMbps = 100; if (s.DownMbps == 0) s.DownMbps = 100; if (Get("protocol", "udp") != "udp") s.UnsupportedReason = "Hysteria поддерживается по UDP/QUIC; режим fake TCP пока не поддерживается."; }
        if (s.Type == "hysteria2") { s.Obfs = Get("obfs"); s.ObfsPassword = Get("obfs-password"); if (Get("mport").Length > 0) s.ServerPorts = PortRanges(Get("mport")); }
        if (Get("pinSHA256").Length > 0 || Get("ech").Length > 0) s.UnsupportedReason = "Параметры pinSHA256 / ECH этой ссылки пока не поддерживаются; используйте сертификат с обычной проверкой TLS.";
        if (s.Transport is "h2") s.Transport = "http";
        return s;
    }
    private static Server VmessJson(string encoded)
    {
        var root = JsonNode.Parse(Decode(encoded), documentOptions: new() { MaxDepth = 16 }) as JsonObject ?? throw new UserError("Повреждённая ссылка VMess.");
        string Text(string key, string fallback = "") => root[key] is JsonValue value ? value.ToString() : fallback;
        var tls = Text("tls").ToLowerInvariant();
        var s = new Server { Type = "vmess", Host = Text("add"), Port = int.Parse(Text("port"), CultureInfo.InvariantCulture), Uuid = Text("id"), Name = Text("ps"), Cipher = Text("scy", "auto"), AlterId = int.Parse(Text("aid", "0"), CultureInfo.InvariantCulture), Security = tls is "tls" or "true" or "1" ? "tls" : "none", Sni = Text("sni"), Fingerprint = Text("fp", ""), Transport = Text("net", "tcp").ToLowerInvariant(), TransportHost = Text("host"), Path = Text("path", "/"), Alpn = Text("alpn").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), TlsInsecure = Text("allowInsecure") is "true" or "1" };
        if (s.Transport == "h2") s.Transport = "http";
        if (s.Transport == "grpc") s.ServiceName = Text("path");
        if (Text("type", "none") is not ("" or "none")) s.UnsupportedReason = "Маскировка заголовков VMess пока не поддерживается.";
        if (s.Sni.Length == 0 && s.Security == "tls") s.Sni = s.TransportHost;
        return s;
    }
    private static Server Shadowsocks(string input)
    {
        var data = input[5..];
        if (!data.Split('#')[0].Split('?')[0].Contains('@'))
        { var suffixAt = data.IndexOfAny(['#', '?']); var suffix = suffixAt < 0 ? "" : data[suffixAt..]; data = Decode(suffixAt < 0 ? data : data[..suffixAt]) + suffix; }
        var at = data.LastIndexOf('@'); if (at < 1) throw new FormatException();
        var credentials = Uri.UnescapeDataString(data[..at]);
        if (!credentials.Contains(':')) credentials = Decode(credentials);
        var split = credentials.IndexOf(':'); if (split < 1) throw new FormatException();
        var uri = new Uri("ss://" + data[(at + 1)..]); var q = Query(uri);
        var s = new Server { Type = "shadowsocks", Host = uri.IdnHost.Trim('[', ']'), Port = uri.Port, Cipher = credentials[..split].ToLowerInvariant(), Password = credentials[(split + 1)..], Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')), Security = "none", Fingerprint = "" };
        if (q.TryGetValue("plugin", out var plugin))
        { var parts = plugin.Split(';', 2); s.Plugin = parts[0] == "simple-obfs" ? "obfs-local" : parts[0]; s.PluginOptions = parts.Length == 2 ? parts[1] : ""; }
        return s;
    }
    internal static List<string> PortRanges(string value)
    {
        var ports = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ports.Length is < 1 or > 64) throw new UserError("Некорректный список портов Hysteria.");
        foreach (var part in ports)
        { var range = part.Replace('-', ':').Split(':'); if (range.Length > 2 || range.Any(p => !int.TryParse(p, out var n) || n is < 1 or > 65535) || range.Length == 2 && int.Parse(range[0]) > int.Parse(range[1])) throw new UserError("Некорректный список портов Hysteria."); }
        return ports.Select(p => p.Replace('-', ':')).ToList();
    }
}
