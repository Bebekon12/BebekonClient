using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public static class RuleValidation
{
    public static void Validate(RoutingRule rule)
    {
        if (!Enum.IsDefined(rule.Kind) || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 160 || rule.Values.Count is < 1 or > 500) throw new UserError("Укажите название и адрес правила.");
        if (rule.ServerId is { } serverId && (serverId.Length is < 1 or > 128 || serverId.Any(char.IsControl))) throw new UserError("Некорректный сервер правила.");
        for (var i = 0; i < rule.Values.Count; i++)
        {
            var v = rule.Values[i].Trim();
            if (v.Length is < 1 or > 2048 || v.Any(char.IsControl)) throw new UserError("Некорректное значение правила.");
            switch (rule.Kind)
            {
                case RuleKind.Site:
                    v = v.TrimEnd('.');
                    if (v.Contains('/') || v.Contains(':') || v.Contains('*')) throw new UserError("Введите домен, например figma.com, без https:// и пути.");
                    try { v = new IdnMapping().GetAscii(v).ToLowerInvariant(); } catch (ArgumentException) { throw new UserError("Некорректный домен."); }
                    if (Uri.CheckHostName(v) != UriHostNameType.Dns || !v.Contains('.') || IPAddress.TryParse(v, out _)) throw new UserError("Введите полное доменное имя.");
                    break;
                case RuleKind.Application:
                    if (!v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new UserError("Выберите приложение .exe.");
                    if (v.Contains('\\') && !Path.IsPathFullyQualified(v)) throw new UserError("Укажите полный путь к приложению.");
                    break;
                case RuleKind.Network:
                    var p = v.Split('/');
                    if (p.Length > 2 || !IPAddress.TryParse(p[0], out var ip) || p.Length == 2 && (!int.TryParse(p[1], out var prefix) || prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))) throw new UserError("Укажите IP или подсеть, например 192.168.1.0/24.");
                    if (p.Length == 1) v += ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128";
                    break;
                case RuleKind.GeoSite:
                case RuleKind.GeoIp:
                    v = v.ToLowerInvariant();
                    if (!GeoCatalog.Contains(rule.Kind, v)) throw new UserError("Этот набор не встроен в приложение. Выберите набор из списка.");
                    break;
            }
            rule.Values[i] = v;
        }
    }
    public static void Validate(Profile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 160 || !Enum.IsDefined(profile.DefaultRoute) || profile.Rules.Count > 5000) throw new UserError("Некорректный профиль.");
        foreach (var rule in profile.Rules) Validate(rule);
        if (profile.Rules.Select(r => r.Id).Distinct().Count() != profile.Rules.Count) throw new UserError("В профиле повторяются идентификаторы правил.");
    }
}
public static class ProfileCodec
{
    public static string Export(Profile profile) { RuleValidation.Validate(profile); return JsonSerializer.Serialize(profile, Json.Options); }
    public static Profile Import(string content)
    {
        if (content.Length > 4 * 1024 * 1024) throw new UserError("Файл профиля слишком большой.");
        try { var p = JsonSerializer.Deserialize<Profile>(content, Json.Options) ?? throw new UserError("Пустой профиль."); RuleValidation.Validate(p); p.Id = Guid.NewGuid().ToString("N"); return p; }
        catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException) { throw new UserError("Повреждённый файл профиля Bebekon."); }
    }
}
public sealed record ConnectSpec(Server Server, Profile Profile, Settings Settings, int ProbePort, string ProbePassword, List<Server>? RuleServers = null);
public static class ConfigGenerator
{
    public const string CoreVersion = "1.14.2";
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static string Generate(ConnectSpec spec, bool probeOnly = false, XhttpRuntime? runtime = null)
    {
        var s = spec.Server;
        if (spec.ProbePort is < 1024 or > 65535 || spec.ProbePassword.Length < 24 || spec.Settings.Mtu is < 1280 or > 9000 || !Enum.IsDefined(spec.Settings.TunnelMode)) throw new UserError("Некорректные параметры подключения.");
        RuleValidation.Validate(spec.Profile);
        var vpn = Vpn(s, "vpn", runtime);
        var outbounds = new JsonArray { vpn, new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "direct-dns" } };
        var dnsServers = new JsonArray { Dns("direct-dns", "direct"), Dns("vpn-dns", "vpn") };
        var serverTags = new Dictionary<string, string> { [s.Id] = "vpn" };
        if (!probeOnly)
        {
            var required = spec.Profile.Rules.Where(r => r.UseVpn && r.ServerId is not null).Select(r => r.ServerId!).Distinct().Where(id => id != s.Id).ToArray();
            if (required.Length > 64) throw new UserError("В одном профиле можно использовать до 64 дополнительных серверов.");
            foreach (var id in required)
            {
                var nodes = spec.RuleServers?.Where(n => n.Id == id).ToArray();
                if (nodes is null || nodes.Length != 1) throw new UserError("Сервер одного из правил недоступен. Измените правило или выберите «Авто».");
                var tag = "vpn-rule-" + serverTags.Count;
                serverTags.Add(id, tag); outbounds.Add(Vpn(nodes[0], tag, runtime)); dnsServers.Add(Dns(tag + "-dns", tag));
            }
        }
        var inbounds = new JsonArray { new JsonObject { ["type"] = "mixed", ["tag"] = "vpn-probe", ["listen"] = "127.0.0.1", ["listen_port"] = spec.ProbePort, ["users"] = new JsonArray { new JsonObject { ["username"] = "bebekon", ["password"] = spec.ProbePassword } } } };
        if (!probeOnly)
        {
            if (spec.Settings.TunnelMode == TunnelMode.Tun) inbounds.Add(new JsonObject { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "Bebekon", ["address"] = Strings(["172.29.255.1/30", "fd84:be:be::1/126"]), ["mtu"] = spec.Settings.Mtu, ["auto_route"] = true, ["strict_route"] = spec.Settings.DnsProtection && !spec.Settings.CompatibilityMode, ["dns_mode"] = "hijack", ["stack"] = "mixed" });
            else inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "proxy-in", ["listen"] = "127.0.0.1", ["listen_port"] = 17890 });
        }
        var routes = new JsonArray {
            new JsonObject { ["inbound"] = Strings(["vpn-probe"]), ["action"] = "route", ["outbound"] = "vpn" },
            new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" },
            new JsonObject { ["action"] = "sniff", ["sniffer"] = Strings(["http", "tls", "quic", "dns"]), ["timeout"] = "300ms" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        if (runtime is not null) { inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "xray-direct", ["listen"] = "127.0.0.1", ["listen_port"] = runtime.Direct.Port, ["users"] = new JsonArray { new JsonObject { ["username"] = "bebekon", ["password"] = runtime.Direct.Password } } }); routes.Insert(0, new JsonObject { ["inbound"] = Strings(["xray-direct"]), ["action"] = "route", ["outbound"] = "direct" }); }
        var dnsRules = new JsonArray(); var sets = new JsonArray(); var setTags = new HashSet<string>();
        foreach (var rule in probeOnly ? [] : spec.Profile.Rules)
        {
            if (rule.Kind is RuleKind.GeoSite or RuleKind.GeoIp)
                foreach (var value in rule.Values) if (setTags.Add(GeoCatalog.Tag(rule.Kind, value))) sets.Add(GeoCatalog.Inline(rule.Kind, value));
            var tag = !rule.UseVpn ? "direct" : rule.ServerId is null ? "vpn" : serverTags[rule.ServerId];
            var match = Match(rule); match["action"] = "route"; match["outbound"] = tag; routes.Add(match);
            if (rule.Kind is not (RuleKind.Network or RuleKind.GeoIp)) { var dm = Match(rule); dm["action"] = "route"; dm["server"] = tag == "vpn" ? "vpn-dns" : tag + "-dns"; dnsRules.Add(dm); }
        }
        var route = new JsonObject { ["rules"] = routes, ["final"] = spec.Profile.DefaultRoute == RouteTarget.Vpn || probeOnly ? "vpn" : "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "direct-dns" };
        if (sets.Count > 0) route["rule_set"] = sets;
        return new JsonObject {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false, ["disabled"] = false },
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules, ["final"] = spec.Profile.DefaultRoute == RouteTarget.Vpn || probeOnly ? "vpn-dns" : "direct-dns", ["reverse_mapping"] = true, ["cache_capacity"] = 4096, ["strategy"] = "prefer_ipv4" },
            ["inbounds"] = inbounds, ["outbounds"] = outbounds, ["route"] = route
        }.ToJsonString(Json.Options);
    }
    private static JsonObject Dns(string tag, string detour) => new() { ["type"] = "https", ["tag"] = tag, ["server"] = "1.1.1.1", ["detour"] = detour, ["tls"] = new JsonObject { ["server_name"] = "cloudflare-dns.com" } };
    private static JsonObject Vpn(Server s, string tag, XhttpRuntime? runtime)
    {
        ProtocolConfig.Validate(s);
        if (s.Transport != "xhttp") return ProtocolConfig.Outbound(s, tag);
        if (runtime is null || !runtime.Bridges.TryGetValue(s.Id, out var bridge)) throw new UserError("Для XHTTP требуется запуск комплектного ядра Xray.");
        var vpn = new JsonObject { ["type"] = "socks", ["tag"] = tag, ["server"] = "127.0.0.1", ["server_port"] = bridge.Port, ["version"] = "5", ["username"] = "bebekon", ["password"] = bridge.Password, ["connect_timeout"] = "4s" };
        if (!s.UdpEnabled) vpn["network"] = "tcp"; return vpn;
    }
    private static JsonObject Match(RoutingRule r)
    {
        var key = r.Kind switch { RuleKind.Site => "domain_suffix", RuleKind.Contains => "domain_keyword", RuleKind.Network => "ip_cidr", _ => "process_name" };
        if (r.Kind is RuleKind.GeoSite or RuleKind.GeoIp) return new JsonObject { ["rule_set"] = Strings(r.Values.Select(v => GeoCatalog.Tag(r.Kind, v))) };
        if (r.Kind != RuleKind.Application) return new JsonObject { [key] = Strings(r.Values) };
        var paths = r.Values.Where(Path.IsPathFullyQualified).ToList(); var names = r.Values.Where(v => !Path.IsPathFullyQualified(v)).ToList();
        if (paths.Count > 0 && names.Count > 0) return new JsonObject { ["type"] = "logical", ["mode"] = "or", ["rules"] = new JsonArray { new JsonObject { ["process_path"] = Strings(paths) }, new JsonObject { ["process_name"] = Strings(names) } } };
        return new JsonObject { [paths.Count > 0 ? "process_path" : "process_name"] = Strings(paths.Count > 0 ? paths : names) };
    }
}
