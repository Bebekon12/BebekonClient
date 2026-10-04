using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Bebekon.Core;

/// <summary>Reads server data only. Provider rules, DNS, listeners and external providers are never executed.</summary>
internal static class ClashSubscriptionParser
{
    public static List<Server> Parse(string content)
    {
        try
        {
            // Bound syntactic depth before the DOM is built, including sections that are not imported.
            var parser = new Parser(new StringReader(content));
            int depth = 0, events = 0, documents = 0;
            while (parser.MoveNext())
            {
                if (++events > 100_000) throw Invalid();
                if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw Invalid();
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
                if (parser.Current is DocumentStart && ++documents > 1) throw Invalid();
            }
            var stream = new YamlStream(); stream.Load(new StringReader(content));
            if (stream.Documents.Count != 1) throw Invalid();
            var root = stream.Documents[0].RootNode;
            int visits = 0;
            Validate(root, new(ReferenceEqualityComparer.Instance), 0, ref visits);
            var reader = new Reader();
            var config = reader.Map(root);
            if (!config.TryGetValue("proxies", out var proxies) || proxies is not YamlSequenceNode list)
                throw new UserError("Ожидается подписка Clash/Mihomo с массивом proxies. Внешние proxy-providers пока не поддерживаются.");
            if (list.Children.Count is < 1 or > 5000) throw new UserError("Подписка Clash пустая или содержит слишком много серверов.");
            var servers = new List<Server>();
            for (var index = 0; index < list.Children.Count; index++)
            {
                try { servers.Add(ReadServer(reader, reader.Map(list.Children[index]))); }
                catch (UserError error) { throw new UserError($"Сервер {index + 1}: {error.Message}"); }
            }
            return servers.DistinctBy(server => server.Id).ToList();
        }
        catch (Exception error) when (error is YamlException or ArgumentException or InvalidOperationException or FormatException)
        {
            // YAML parser diagnostics can contain UUIDs, addresses or subscription secrets.
            throw Invalid();
        }
    }

    private static Server ReadServer(Reader reader, Dictionary<string, YamlNode> node)
    {
        var type = Text(node, "type").ToLowerInvariant();
        type = type switch { "ss" => "shadowsocks", "hy2" => "hysteria2", _ => type };
        var host = Text(node, "server").Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || !int.TryParse(Text(node, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535) throw Invalid();
        var uuid = Guid.Empty;
        if (type is "vless" or "vmess" && !Guid.TryParse(Text(node, "uuid"), out uuid)) throw new UserError("В подписке указан неверный UUID.");
        var network = Text(node, "network", "tcp").ToLowerInvariant();
        if (network.Length == 0) network = "tcp";
        var reality = reader.OptionalMap(node, "reality-opts");
        var security = reality.Count > 0 ? "reality" : (Flag(node, "tls") || type is "trojan" or "hysteria" or "hysteria2") ? "tls" : "none";
        var query = new Dictionary<string, string>
        {
            ["type"] = network == "h2" ? "http" : network,
            ["security"] = security, ["sni"] = Text(node, "servername", Text(node, "sni")),
            ["fp"] = Text(node, "client-fingerprint", "chrome"), ["flow"] = Text(node, "flow"),
            ["pbk"] = Text(reality, "public-key"), ["sid"] = Text(reality, "short-id"),
            ["alpn"] = string.Join(',', Strings(node, "alpn")),
            ["allowInsecure"] = type == "vless" && Flag(node, "skip-cert-verify") ? "1" : "0",
            ["pcs"] = network is "xhttp" or "splithttp" ? Text(node, "fingerprint") : "",
            ["encryption"] = Text(node, "encryption", "none"),
            // Current Mihomo defaults to XUDP; legacy packet-addr / xudp flags also occur in subscriptions.
            ["packetEncoding"] = Text(node, "packet-encoding") switch
            {
                "packet" or "packetaddr" => "packetaddr",
                "" or "none" => Flag(node, "packet-addr") && !Flag(node, "xudp") ? "packetaddr" : "xudp",
                var encoding => encoding
            },
            ["udp"] = Flag(node, "udp") ? "1" : "0"
        };
        string? unsupported = reality.Count > 0 && !Flag(node, "tls") ? "Для Reality в Clash требуется включённый TLS." : null;
        if (network == "grpc")
        {
            var grpc = reader.OptionalMap(node, "grpc-opts");
            query["serviceName"] = Text(grpc, "grpc-service-name");
            if (grpc.Keys.Any(key => key != "grpc-service-name")) unsupported = "Дополнительные параметры gRPC из Clash пока не поддерживаются.";
        }
        else if (network == "ws")
        {
            var ws = reader.OptionalMap(node, "ws-opts");
            query["path"] = Text(ws, "path", "/");
            var headers = reader.OptionalMap(ws, "headers");
            var hostKey = headers.Keys.FirstOrDefault(key => key.Equals("Host", StringComparison.OrdinalIgnoreCase));
            query["host"] = hostKey is null ? "" : Text(headers, hostKey);
            if (security != "none" && query["sni"].Length == 0) query["sni"] = query["host"];
            if (headers.Count > 1 || headers.Keys.Any(key => !key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                || ws.Keys.Any(key => key is not ("path" or "headers" or "v2ray-http-upgrade")))
                unsupported = "Дополнительные параметры WebSocket из Clash пока не поддерживаются.";
            if (Flag(ws, "v2ray-http-upgrade")) query["type"] = "httpupgrade";
        }
        else if (network == "h2")
        {
            var h2 = reader.OptionalMap(node, "h2-opts");
            query["path"] = Text(h2, "path", "/");
            var hosts = Strings(h2, "host"); query["host"] = hosts.FirstOrDefault() ?? "";
            if (security == "none") unsupported = "Транспорт HTTP/2 без TLS из Clash пока не поддерживается.";
            if (hosts.Length > 1 || h2.Keys.Any(key => key is not ("host" or "path"))) unsupported = "Дополнительные параметры HTTP/2 из Clash пока не поддерживаются.";
        }
        else if (network is "xhttp" or "splithttp")
        {
            var http = reader.OptionalMap(node, "xhttp-opts"); query["type"] = "xhttp";
            query["path"] = Text(http, "path", "/"); query["host"] = Text(http, "host"); query["mode"] = Text(http, "mode", "auto");
            var extra = http.TryGetValue("extra", out var raw) ? ToObject(reader, raw) : http.Where(p => p.Key is not ("path" or "host" or "mode")).ToDictionary(p => p.Key, p => ToObject(reader, p.Value));
            query["extra"] = JsonSerializer.Serialize(extra);
        }
        else if (network == "http")
            unsupported = "Маскировка HTTP/1 из Clash пока не поддерживается. Она отличается от транспорта HTTP/2.";
        if (node.ContainsKey("name-cert-verify") || node.ContainsKey("shadow-tls-opts") || node.ContainsKey("restls-opts") || node.ContainsKey("jls-opts") || node.ContainsKey("ws-headers")
            || network is not ("xhttp" or "splithttp") && Text(node, "fingerprint").Length > 0 || Text(node, "certificate").Length > 0 || Text(node, "private-key").Length > 0
            || Text(node, "dialer-proxy").Length > 0 || Text(node, "interface-name").Length > 0 || node.ContainsKey("routing-mark")
            || Flag(reader.OptionalMap(node, "smux"), "enabled") || Flag(reader.OptionalMap(node, "ech-opts"), "enable")
            || reality.Keys.Any(key => key is not ("public-key" or "short-id")))
            unsupported = "Дополнительные параметры TLS или подключения из Clash пока не поддерживаются.";
        var uri = new UriBuilder("vless", host, port)
        {
            UserName = uuid.ToString(), Fragment = Text(node, "name", host),
            Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        var server = VlessParser.Parse(uri.Uri.AbsoluteUri);
        server.Type = type;
        if (type != "vless") server.Flow = "";
        if (type is "vmess") { server.Cipher = Text(node, "cipher", "auto"); server.AlterId = Integer(node, "alterId"); server.GlobalPadding = Flag(node, "global-padding"); server.AuthenticatedLength = Flag(node, "authenticated-length"); }
        if (type is "shadowsocks" or "trojan" or "hysteria" or "hysteria2") server.Password = Text(node, "password", Text(node, "auth-str", Text(node, "auth")));
        if (type == "shadowsocks")
        {
            server.Cipher = Text(node, "cipher"); server.Plugin = Text(node, "plugin");
            if (server.Plugin is "simple-obfs" or "obfs") server.Plugin = "obfs-local";
            var opts = reader.OptionalMap(node, "plugin-opts");
            server.PluginOptions = string.Join(';', opts.Select(p => Text(opts, p.Key).ToLowerInvariant() switch { "true" => p.Key, "false" => "", _ => (server.Plugin == "obfs-local" ? p.Key switch { "mode" => "obfs", "host" => "obfs-host", _ => p.Key } : p.Key) + "=" + Text(opts, p.Key) }).Where(p => p.Length > 0));
        }
        if (type is "hysteria" or "hysteria2")
        {
            server.Fingerprint = ""; server.UpMbps = Bandwidth(node, "up", type == "hysteria" ? 100 : 0); server.DownMbps = Bandwidth(node, "down", type == "hysteria" ? 100 : 0);
            server.Obfs = type == "hysteria2" ? Text(node, "obfs") : ""; server.ObfsPassword = Text(node, type == "hysteria2" ? "obfs-password" : "obfs");
            if (type == "hysteria" && node.ContainsKey("auth") && !node.ContainsKey("auth-str")) server.Password = ProtocolParser.Decode(server.Password);
            if (node.ContainsKey("ports")) server.ServerPorts = ProtocolParser.PortRanges(Text(node, "ports"));
            if (node.ContainsKey("hop-interval")) server.HopIntervalSeconds = Integer(node, "hop-interval");
        }
        if (type != "vless") server.TlsInsecure = Flag(node, "skip-cert-verify");
        server.UnsupportedReason ??= unsupported;
        if (type is not ("vless" or "vmess" or "shadowsocks" or "trojan" or "hysteria" or "hysteria2")) server.UnsupportedReason = "Этот VPN-протокол пока не поддерживается: " + type;
        if (server.Supported) ProtocolConfig.Validate(server);
        server.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.Name + ServerRefresh.ConnectionKey(server))))[..24];
        return server;
    }

    private static int Integer(Dictionary<string, YamlNode> node, string key) => int.Parse(Text(node, key, "0"), CultureInfo.InvariantCulture);
    private static int Bandwidth(Dictionary<string, YamlNode> node, string key, int fallback)
    {
        var text = Text(node, key, fallback.ToString(CultureInfo.InvariantCulture));
        var match = Regex.Match(text, @"^([0-9]+)(?:\s*(?:[Mm][Bb][Pp][Ss]))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) throw Invalid(); return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
    private static object? ToObject(Reader reader, YamlNode node) => node switch
    {
        YamlMappingNode => reader.Map(node).ToDictionary(p => p.Key, p => ToObject(reader, p.Value)),
        YamlSequenceNode seq => seq.Children.Select(n => ToObject(reader, n)).ToArray(),
        YamlScalarNode { Value: { } value } => value.ToLowerInvariant() switch { "true" => true, "false" => false, _ => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)n : value },
        _ => throw Invalid()
    };
    private static void Validate(YamlNode node, HashSet<YamlNode> path, int depth, ref int visits)
    {
        // Anchors are allowed; cycles and exponential alias expansion are bounded.
        if (depth > 32 || ++visits > 100_000 || !path.Add(node)) throw Invalid();
        if (node is YamlMappingNode mapping)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode { Value: { } key } || !keys.Add(key)) throw Invalid();
                Validate(pair.Value, path, depth + 1, ref visits);
            }
        }
        else if (node is YamlSequenceNode sequence)
            foreach (var item in sequence.Children) Validate(item, path, depth + 1, ref visits);
        else if (node is not YamlScalarNode) throw Invalid();
        path.Remove(node);
    }

    private sealed class Reader
    {
        private readonly Dictionary<YamlNode, Dictionary<string, YamlNode>> maps = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, YamlNode> Map(YamlNode node)
        {
            if (maps.TryGetValue(node, out var cached)) return cached;
            if (node is not YamlMappingNode mapping) throw Invalid();
            var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (((YamlScalarNode)pair.Key).Value != "<<") continue;
                var merges = pair.Value is YamlSequenceNode sequence ? sequence.Children : [pair.Value];
                foreach (var merge in merges)
                    foreach (var field in Map(merge)) result.TryAdd(field.Key, field.Value);
            }
            foreach (var pair in mapping.Children)
            {
                var key = ((YamlScalarNode)pair.Key).Value!;
                if (key != "<<") result[key] = pair.Value;
            }
            maps[node] = result;
            return result;
        }
        public Dictionary<string, YamlNode> OptionalMap(Dictionary<string, YamlNode> node, string key) => node.TryGetValue(key, out var value) ? Map(value) : new(StringComparer.Ordinal);
    }
    private static string Text(Dictionary<string, YamlNode> node, string key, string fallback = "")
    {
        if (!node.TryGetValue(key, out var value)) return fallback;
        if (value is not YamlScalarNode scalar || scalar.Value is null) throw Invalid();
        return scalar.Value;
    }
    private static bool Flag(Dictionary<string, YamlNode> node, string key) => Text(node, key, "false").ToLowerInvariant() switch
    {
        "true" => true, "false" => false, _ => throw Invalid()
    };
    private static string[] Strings(Dictionary<string, YamlNode> node, string key)
    {
        if (!node.TryGetValue(key, out var value)) return [];
        if (value is not YamlSequenceNode sequence || sequence.Children.Any(item => item is not YamlScalarNode { Value: not null })) throw Invalid();
        return sequence.Children.Select(item => ((YamlScalarNode)item).Value!).ToArray();
    }
    private static UserError Invalid() => new("Повреждённая подписка Clash/Mihomo: проверьте формат и параметры VPN.");
}
