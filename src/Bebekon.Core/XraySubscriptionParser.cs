using System.Text.Json;

namespace Bebekon.Core;

/// <summary>Extracts VLESS server settings, never provider routes, DNS, listeners or executable config.</summary>
internal static class XraySubscriptionParser
{
    public static List<Server> Parse(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new() { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            CheckDuplicates(document.RootElement);
            var profiles = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
            if (profiles.Length is < 1 or > 5000) throw Invalid();
            var servers = new List<Server>();
            for (var index = 0; index < profiles.Length; index++)
            {
                try { ReadProfile(profiles[index], servers); }
                catch (UserError error) { throw new UserError($"Конфигурация {index + 1}: {error.Message}"); }
            }
            if (servers.Count == 0) throw new UserError("В JSON-подписке не найдено серверов VLESS.");
            return servers.DistinctBy(server => server.Id).ToList();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or UriFormatException)
        {
            // Json/URI exceptions can include credentials. Only show a fixed message.
            throw Invalid();
        }
    }

    private static void ReadProfile(JsonElement profile, List<Server> servers)
    {
        var outbounds = Property(profile, "outbounds");
        if (outbounds.ValueKind != JsonValueKind.Array) throw new UserError("Ожидается JSON Xray с массивом outbounds.");
        foreach (var outbound in outbounds.EnumerateArray())
        {
            var protocol = Text(outbound, "protocol").ToLowerInvariant();
            if (protocol is "freedom" or "blackhole" or "dns") continue;
            if (protocol != "vless") throw new UserError("В JSON-подписке есть VPN-протокол, отличный от VLESS.");
            var settings = Property(outbound, "settings");
            var stream = Property(outbound, "streamSettings");
            var name = Text(profile, "remarks", Text(profile, "ps", Text(outbound, "tag")));
            var vnext = Property(settings, "vnext");
            if (vnext.ValueKind == JsonValueKind.Undefined)
            {
                // Newer Xray uses address/port/id directly in settings.
                AddServer(settings, settings, stream, outbound, name, servers);
                continue;
            }
            if (vnext.ValueKind != JsonValueKind.Array || vnext.GetArrayLength() == 0) throw Invalid();
            foreach (var node in vnext.EnumerateArray())
            {
                var users = Property(node, "users");
                if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0) throw Invalid();
                foreach (var user in users.EnumerateArray()) AddServer(node, user, stream, outbound, name, servers);
            }
        }
    }

    private static void AddServer(JsonElement node, JsonElement user, JsonElement stream, JsonElement outbound, string name, List<Server> servers)
    {
        if (servers.Count >= 5000) throw new UserError("Подписка содержит слишком много серверов.");
        var address = Text(node, "address");
        var port = Property(node, "port");
        if (port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var number) || number is < 1 or > 65535) throw Invalid();
        if (!Guid.TryParse(Text(user, "id"), out var uuid)) throw new UserError("В JSON указан неверный идентификатор VLESS.");
        if (string.IsNullOrWhiteSpace(address) || Uri.CheckHostName(address.Trim('[', ']')) == UriHostNameType.Unknown) throw Invalid();
        var network = Text(stream, "network", "tcp").ToLowerInvariant();
        network = network switch { "raw" => "tcp", "h2" => "http", _ => network };
        var security = Text(stream, "security", "none").ToLowerInvariant();
        var tls = Property(stream, security == "reality" ? "realitySettings" : "tlsSettings");
        var query = new Dictionary<string, string>
        {
            ["type"] = network, ["security"] = security,
            ["encryption"] = Text(user, "encryption", "none"), ["flow"] = Text(user, "flow"),
            ["sni"] = Text(tls, "serverName"), ["fp"] = Text(tls, "fingerprint", "chrome"),
            ["pbk"] = Text(tls, "publicKey", Text(tls, "password")), ["sid"] = Text(tls, "shortId"),
            ["alpn"] = string.Join(',', Strings(tls, "alpn")),
            ["allowInsecure"] = Flag(tls, "allowInsecure") ? "1" : "0"
        };
        string? unsupported = null;
        if (network == "grpc")
        {
            var grpc = Property(stream, "grpcSettings");
            query["serviceName"] = Text(grpc, "serviceName");
            var mode = Property(grpc, "mode");
            if (Flag(grpc, "multiMode") || mode.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False)
                && (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("" or "gun")))
                unsupported = "Многопоточный режим gRPC из JSON пока не поддерживается.";
            if (Text(grpc, "authority").Length > 0) unsupported = "Пользовательский authority gRPC из JSON пока не поддерживается.";
        }
        else if (network is "ws" or "http" or "httpupgrade")
        {
            var transport = Property(stream, network + "Settings");
            query["path"] = Text(transport, "path", "/");
            if (network == "ws")
            {
                var headers = Property(transport, "headers");
                query["host"] = Text(headers, "Host", Text(headers, "host"));
                if (headers.ValueKind == JsonValueKind.Object && headers.EnumerateObject().Any(header => !header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)))
                    unsupported = "Дополнительные заголовки WebSocket из JSON пока не поддерживаются.";
                var earlyData = Property(transport, "maxEarlyData");
                if (earlyData.ValueKind != JsonValueKind.Undefined && (earlyData.ValueKind != JsonValueKind.Number || !earlyData.TryGetInt32(out var value) || value != 0))
                    unsupported = "WebSocket early data из JSON пока не поддерживается.";
            }
            else if (network == "http")
            {
                var hosts = Strings(transport, "host");
                query["host"] = hosts.FirstOrDefault() ?? "";
                if (hosts.Length > 1) unsupported = "Несколько транспортных Host из JSON пока не поддерживаются.";
            }
            else query["host"] = Text(transport, "host");
        }
        else if (network == "tcp")
        {
            var tcp = Property(stream, "tcpSettings");
            if (tcp.ValueKind == JsonValueKind.Undefined) tcp = Property(stream, "rawSettings");
            query["headerType"] = Text(Property(tcp, "header"), "type", "none");
        }
        if (Flag(Property(outbound, "mux"), "enabled")) unsupported = "Xray Mux из JSON пока не поддерживается.";
        if (Property(outbound, "proxySettings").ValueKind == JsonValueKind.Object
            || Property(stream, "sockopt").ValueKind == JsonValueKind.Object
            || Property(user, "reverse").ValueKind == JsonValueKind.Object)
            unsupported = "Дополнительные сетевые параметры Xray из JSON пока не поддерживаются.";
        var uri = new UriBuilder("vless", address.Trim('[', ']'), number)
        {
            UserName = uuid.ToString(), Fragment = name,
            Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        var server = VlessParser.Parse(uri.Uri.AbsoluteUri);
        server.UnsupportedReason ??= unsupported;
        servers.Add(server);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        return element.TryGetProperty(name, out var value) ? value : default;
    }
    private static string Text(JsonElement element, string name, string fallback = "")
    {
        var value = Property(element, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        return value.GetString()!;
    }
    private static bool Flag(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False) return false;
        if (value.ValueKind != JsonValueKind.True) throw Invalid();
        return true;
    }
    private static string[] Strings(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw Invalid();
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new UserError("В JSON-подписке повторяются поля.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
    private static UserError Invalid() => new("Повреждённая JSON-подписка Xray: проверьте формат и параметры VLESS.");
}
