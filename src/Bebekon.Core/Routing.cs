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
public sealed record ConnectSpec(Server Server, Profile Profile, Settings Settings, int ProbePort, string ProbePassword);
public static class ConfigGenerator
{
    public const string CoreVersion = "1.14.2";
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static string Generate(ConnectSpec spec, bool probeOnly = false)
    {
        var s = spec.Server;
        if (!s.Supported) throw new UserError(s.UnsupportedReason!);
        // Validate model received by the elevated service; it never accepts executable paths or raw config.
        if (!Guid.TryParse(s.Uuid, out _) || s.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(s.Host) || s.Transport is not ("tcp" or "grpc" or "ws" or "http" or "httpupgrade") || s.Security is not ("none" or "tls" or "reality")) throw new UserError("Некорректный сервер.");
        if (spec.ProbePort is < 1024 or > 65535 || spec.ProbePassword.Length < 24 || spec.Settings.Mtu is < 1280 or > 9000 || !Enum.IsDefined(spec.Settings.TunnelMode)) throw new UserError("Некорректные параметры подключения.");
        RuleValidation.Validate(spec.Profile);
        var vpn = new JsonObject { ["type"] = "vless", ["tag"] = "vpn", ["server"] = s.Host, ["server_port"] = s.Port, ["uuid"] = s.Uuid, ["domain_resolver"] = "direct-dns", ["connect_timeout"] = "4s" };
        if (s.Flow.Length > 0) vpn["flow"] = s.Flow;
        if (s.Security != "none")
        {
            var tls = new JsonObject { ["enabled"] = true, ["server_name"] = s.Sni.Length > 0 ? s.Sni : s.Host };
            if (s.Alpn.Count > 0) tls["alpn"] = Strings(s.Alpn);
            if (s.Fingerprint.Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = s.Fingerprint };
            if (s.Security == "reality") tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = s.PublicKey, ["short_id"] = s.ShortId };
            vpn["tls"] = tls;
        }
        if (s.Transport != "tcp")
        {
            var t = new JsonObject { ["type"] = s.Transport };
            if (s.Transport == "grpc") t["service_name"] = s.ServiceName;
            else { t["path"] = s.Path; if (s.TransportHost.Length > 0) { if (s.Transport == "ws") t["headers"] = new JsonObject { ["Host"] = s.TransportHost }; else if (s.Transport == "http") t["host"] = Strings([s.TransportHost]); else t["host"] = s.TransportHost; } }
            vpn["transport"] = t;
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
        var dnsRules = new JsonArray();
        foreach (var rule in spec.Profile.Rules)
        {
            var match = Match(rule); match["action"] = "route"; match["outbound"] = rule.UseVpn ? "vpn" : "direct"; routes.Add(match);
            if (rule.Kind != RuleKind.Network) { var dm = Match(rule); dm["action"] = "route"; dm["server"] = rule.UseVpn ? "vpn-dns" : "direct-dns"; dnsRules.Add(dm); }
        }
        var config = new JsonObject {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false, ["disabled"] = false },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray {
                new JsonObject { ["type"] = "https", ["tag"] = "direct-dns", ["server"] = "1.1.1.1", ["detour"] = "direct", ["tls"] = new JsonObject { ["server_name"] = "cloudflare-dns.com" } },
                new JsonObject { ["type"] = "https", ["tag"] = "vpn-dns", ["server"] = "1.1.1.1", ["detour"] = "vpn", ["tls"] = new JsonObject { ["server_name"] = "cloudflare-dns.com" } }
            }, ["rules"] = dnsRules, ["final"] = spec.Profile.DefaultRoute == RouteTarget.Vpn || probeOnly ? "vpn-dns" : "direct-dns", ["reverse_mapping"] = true, ["cache_capacity"] = 4096, ["strategy"] = "prefer_ipv4" },
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray { vpn, new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "direct-dns" } },
            ["route"] = new JsonObject { ["rules"] = routes, ["final"] = spec.Profile.DefaultRoute == RouteTarget.Vpn || probeOnly ? "vpn" : "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "direct-dns" }
        };
        return config.ToJsonString(Json.Options);
    }
    private static JsonObject Match(RoutingRule r)
    {
        var key = r.Kind switch { RuleKind.Site => "domain_suffix", RuleKind.Contains => "domain_keyword", RuleKind.Network => "ip_cidr", _ => "process_name" };
        if (r.Kind != RuleKind.Application) return new JsonObject { [key] = Strings(r.Values) };
        var paths = r.Values.Where(Path.IsPathFullyQualified).ToList(); var names = r.Values.Where(v => !Path.IsPathFullyQualified(v)).ToList();
        if (paths.Count > 0 && names.Count > 0) return new JsonObject { ["type"] = "logical", ["mode"] = "or", ["rules"] = new JsonArray { new JsonObject { ["process_path"] = Strings(paths) }, new JsonObject { ["process_name"] = Strings(names) } } };
        return new JsonObject { [paths.Count > 0 ? "process_path" : "process_name"] = Strings(paths.Count > 0 ? paths : names) };
    }
}
