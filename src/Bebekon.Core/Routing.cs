using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

public static class RuleValidation
{
    public static bool IsDomainRule(RuleKind kind) => kind is RuleKind.Site or RuleKind.Contains or RuleKind.GeoSite;
    /// <summary>Accepts what users paste: "https://www.example.com/page?q" → "www.example.com".</summary>
    public static string SiteHost(string value, bool dropWww = false)
    {
        var v = value.Trim();
        var scheme = v.IndexOf("://", StringComparison.Ordinal); if (scheme >= 0) v = v[(scheme + 3)..];
        v = v.Split('/', '?', '#')[0]; v = v[(v.LastIndexOf('@') + 1)..];
        if (v.Count(c => c == ':') == 1) v = v[..v.IndexOf(':')];
        v = v.TrimEnd('.'); if (v.StartsWith("*.", StringComparison.Ordinal)) v = v[2..]; v = v.TrimStart('.');
        // "www." would leave out the bare domain and every other subdomain of the site.
        if (dropWww && v.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && v.Count(c => c == '.') >= 2) v = v[4..];
        return v;
    }
    public static void Validate(RoutingRule rule)
    {
        if (!Enum.IsDefined(rule.Kind) || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 160 || rule.Values.Count is < 1 or > 500) throw new UserError("Укажите название и адрес правила.");
        if (!Enum.IsDefined(rule.Target)) throw new UserError("Неизвестное направление правила.");
        if (rule.ServerId is { } serverId && (serverId.Length is < 1 or > 128 || serverId.Any(char.IsControl))) throw new UserError("Некорректный сервер правила.");
        if (rule.Port is < 1 or > 65535 || rule.Transport is { } transport && !Enum.IsDefined(transport))
            throw new UserError("Укажите TCP или UDP и порт от 1 до 65535.");
        if (rule.AllowedRoutes?.Any(target => !Enum.IsDefined(target)) == true ||
            rule.ForbiddenRoutes?.Any(target => !Enum.IsDefined(target)) == true || !rule.Allows(rule.Target))
            throw new UserError("Выбранный маршрут запрещён политикой правила.");
        if (rule.UnavailableRoute is { } alternative && (alternative != RouteTarget.Block &&
            (alternative != RouteTarget.Direct || rule.Target != RouteTarget.NetBird || !rule.Allows(alternative))))
            throw new UserError("Резервный Direct можно назначить только для правила NetBird, которое разрешает Direct.");
        if (rule.FallbackServerIds is { Count: > 0 } backup &&
            (rule.Target != RouteTarget.Vpn || backup.Count > 8 || backup.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))))
            throw new UserError("Укажите до восьми резервных серверов Bebekon VPN.");
        if (rule.SystemProtection)
        {
            if (rule.Target == RouteTarget.Direct || rule.UnavailableRoute == RouteTarget.Direct ||
                !(rule.Kind is RuleKind.Application or RuleKind.Network || rule.HasApplicationScope && rule.Target is RouteTarget.Vpn or RouteTarget.Block))
                throw new UserError("Выберите VPN или Block без резерва Direct. Для домена область WFP задаётся локальными приложениями и поддерживает Bebekon VPN или Block.");
            if (rule.HasApplicationScope)
            {
                if (rule.ProtectedExecutables is not { Count: > 0 and <= 128 } paths)
                    throw new UserError("Выберите приложения, через которые открывается этот ресурс. WFP защищает весь трафик их исполняемых файлов.");
                rule.ProtectedExecutables = paths.Select(ProtectedExecutable).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        for (var i = 0; i < rule.Values.Count; i++)
        {
            var v = rule.Values[i].Trim();
            if (v.Length is < 1 or > 2048 || v.Any(char.IsControl)) throw new UserError("Некорректное значение правила.");
            switch (rule.Kind)
            {
                case RuleKind.Site:
                    v = SiteHost(v);
                    if (v.Contains('/') || v.Contains(':') || v.Contains('*')) throw new UserError("Введите домен, например figma.com, без https:// и пути.");
                    try { v = new IdnMapping().GetAscii(v).ToLowerInvariant(); } catch (ArgumentException) { throw new UserError("Некорректный домен."); }
                    if (Uri.CheckHostName(v) != UriHostNameType.Dns || !v.Contains('.') || IPAddress.TryParse(v, out _)) throw new UserError("Введите полное доменное имя.");
                    break;
                case RuleKind.Application:
                    if (!v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new UserError("Выберите приложение .exe.");
                    if (v.Contains('\\') && !Path.IsPathFullyQualified(v)) throw new UserError("Укажите полный путь к приложению.");
                    if (rule.SystemProtection) v = ProtectedExecutable(v);
                    break;
                case RuleKind.Network:
                    var p = v.Split('/');
                    if (p.Length > 2 || !IPAddress.TryParse(p[0], out var ip) || p.Length == 2 && (!int.TryParse(p[1], out var prefix) || prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))) throw new UserError("Укажите IP или подсеть, например 192.168.1.0/24.");
                    var address = ip.GetAddressBytes();
                    var bits = p.Length == 1 ? address.Length * 8 : int.Parse(p[1], CultureInfo.InvariantCulture);
                    for (var part = 0; part < address.Length; part++)
                        address[part] &= (byte)(0xff << (8 - Math.Clamp(bits - part * 8, 0, 8)));
                    v = $"{new IPAddress(address)}/{bits}";
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
        if (profile.SystemRules.Sum(rule => rule.Values.Count) > 128)
            throw new UserError("В профиле можно защитить до 128 путей приложений или подсетей Windows.");
        var domainScopes = profile.ActiveRules.Where(rule => rule.HasApplicationScope).ToArray();
        if (domainScopes.Length != 0)
        {
            var appScopes = profile.SystemRules.Where(rule => rule.Kind == RuleKind.Application).ToArray();
            foreach (var domain in domainScopes)
            {
                if (appScopes.Any(scope => scope.Id != domain.Id &&
                    scope.Values.Intersect(domain.ProtectedExecutables!, StringComparer.OrdinalIgnoreCase).Any() &&
                    (scope.Target != domain.Target || scope.ServerId != domain.ServerId ||
                     !(scope.FallbackServerIds ?? []).SequenceEqual(domain.FallbackServerIds ?? []))))
                    throw new UserError("Для одного защищённого приложения задайте одинаковое направление и одинаковые серверы во всех доменных правилах.");
            }
        }
    }

    private static string ProtectedExecutable(string value)
    {
        var path = value?.Trim() ?? "";
        if (path.Length is < 1 or > 2048 || path.Any(char.IsControl) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UserError("Для защиты WFP выберите полный локальный путь к исполняемому файлу.");
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { throw new UserError("Некорректный путь к защищённому приложению."); }
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
public sealed record ConnectSpec(Server Server, Profile Profile, Settings Settings, int ProbePort, string ProbePassword, List<Server>? RuleServers = null, int OriginProbePort = 0, List<Server>? AutoServers = null, string? NetBirdPeer = null, string[]? NetBirdControlHosts = null)
{
    [JsonIgnore] public NetBirdRouting? NetBird { get; init; }
    [JsonIgnore] public string? NetBirdExecutable { get; init; }
    [JsonIgnore] public bool AllowsOriginLookup => !Profile.HasProtectedRules && !(Settings.KillSwitch && Settings.TunnelMode == TunnelMode.Tun);

    internal IEnumerable<Server> ResolveRuleServers()
    {
        var required = Profile.VpnServerIds.Where(id => id != Server.Id).Take(65).ToArray();
        if (required.Length > 64) throw new UserError("В одном профиле можно использовать до 64 дополнительных серверов.");
        foreach (var id in required)
        {
            var matches = RuleServers?.Where(node => node.Id == id).Take(2).ToArray() ?? [];
            var pinned = Profile.ActiveRules.Any(rule => rule.Target == RouteTarget.Vpn &&
                rule.ServerId == id && rule.FallbackServerIds is not { Count: > 0 });
            if (matches.Length == 1 && (pinned || matches[0].Supported)) yield return matches[0];
            else if (matches.Length > 1 || pinned)
                throw new UserError("Сервер одного из правил недоступен. Измените правило или выберите «Авто».");
        }
    }
}
public static class ConfigGenerator
{
    public const string CoreVersion = "1.14.2";
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    public static string Generate(ConnectSpec spec, bool probeOnly = false, XhttpRuntime? runtime = null)
        => Generate(spec, probeOnly, runtime, null);
    internal static string Generate(ConnectSpec spec, bool probeOnly, XhttpRuntime? runtime, TrustTunnelRuntime? trustTunnel)
    {
        var s = spec.Server;
        if (spec.ProbePort is < 1024 or > 65535 || spec.ProbePassword.Length < 24 || spec.Settings.Mtu is < 1280 or > 9000 || !Enum.IsDefined(spec.Settings.TunnelMode)) throw new UserError("Некорректные параметры подключения.");
        if (spec.OriginProbePort != 0 && (spec.OriginProbePort is < 1024 or > 65535 || spec.OriginProbePort == spec.ProbePort || spec.OriginProbePort == 17890)) throw new UserError("Некорректный порт определения местоположения.");
        RuleValidation.Validate(spec.Profile);
        var vpn = Vpn(s, "vpn", runtime, trustTunnel);
        var outbounds = new JsonArray { vpn, new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = "direct-dns" } };
        if (!probeOnly && AutoServers(spec) is { Count: >= 2 } auto)
        {
            // "vpn" becomes a urltest group: sing-box measures every server and moves to the
            // fastest working one, also after a server or network failure.
            outbounds[0] = new JsonObject { ["type"] = "urltest", ["tag"] = "vpn", ["outbounds"] = Strings(auto.Select((_, i) => "auto-" + i)), ["url"] = "https://www.gstatic.com/generate_204", ["interval"] = "3m", ["tolerance"] = 50, ["idle_timeout"] = "30m" };
            for (var i = 0; i < auto.Count; i++) outbounds.Add(ProtocolConfig.Outbound(auto[i], "auto-" + i));
        }
        // Direct DNS uses the uplink's own resolver (router / phone hotspot / ISP). Public DoH is
        // throttled on many mobile networks and delayed even the VPN server's address lookup.
        var dnsServers = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "direct-dns" }, Dns("vpn-dns", "vpn") };
        var netBirdRouting = !probeOnly ? spec.NetBird : null;
        var netBirdDaemon = !probeOnly && spec.Settings.TunnelMode == TunnelMode.Tun ? spec.NetBirdExecutable : null;
        var netBirdControlHosts = netBirdDaemon is null ? [] : NetBirdClient.NormalizeControlHosts(spec.NetBirdControlHosts);
        var netBird = new NetBirdConfig(spec.Profile.UsesNetBird ? netBirdRouting : null, outbounds, dnsServers);
        var serverTags = new Dictionary<string, string> { [s.Id] = "vpn" };
        if (!probeOnly)
        {
            foreach (var node in spec.ResolveRuleServers())
            {
                var tag = "vpn-rule-" + serverTags.Count;
                serverTags.Add(node.Id, tag); outbounds.Add(Vpn(node, tag, runtime, trustTunnel)); dnsServers.Add(Dns(tag + "-dns", tag));
            }
        }
        var inbounds = new JsonArray { new JsonObject { ["type"] = "mixed", ["tag"] = "vpn-probe", ["listen"] = "127.0.0.1", ["listen_port"] = spec.ProbePort, ["users"] = new JsonArray { new JsonObject { ["username"] = "bebekon", ["password"] = spec.ProbePassword } } } };
        if (!probeOnly)
        {
            if (spec.Settings.TunnelMode == TunnelMode.Tun) inbounds.Add(new JsonObject { ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "Bebekon", ["address"] = Strings(["172.29.255.1/30", "fd84:be:be::1/126"]), ["mtu"] = spec.Settings.Mtu, ["auto_route"] = true, ["strict_route"] = spec.Settings.DnsProtection && !spec.Settings.CompatibilityMode, ["dns_mode"] = netBirdRouting is null ? "hijack" : "native", ["stack"] = spec.Settings.CompatibilityMode ? "gvisor" : "mixed" });
            else inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "proxy-in", ["listen"] = "127.0.0.1", ["listen_port"] = 17890 });
        }
        var routes = new JsonArray {
            new JsonObject { ["inbound"] = Strings(["vpn-probe"]), ["action"] = "route", ["outbound"] = "vpn" },
            new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" },
            new JsonObject { ["action"] = "sniff", ["sniffer"] = Strings(["http", "tls", "quic", "dns"]), ["timeout"] = "300ms" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        if (netBirdDaemon is not null)
        {
            // Keep the registered daemon's transport on the uplink. Ordinary traffic to
            // the same hosts still follows the profile.
            routes.Insert(2, new JsonObject
            {
                ["inbound"] = Strings(["tun-in"]),
                ["process_path_regex"] = Strings(["(?i)^" + Regex.Escape(netBirdDaemon) + "$"]),
                ["action"] = "route", ["outbound"] = "direct"
            });
        }
        if (runtime is not null) { inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "xray-direct", ["listen"] = "127.0.0.1", ["listen_port"] = runtime.Direct.Port, ["users"] = new JsonArray { new JsonObject { ["username"] = "bebekon", ["password"] = runtime.Direct.Password } } }); routes.Insert(0, new JsonObject { ["inbound"] = Strings(["xray-direct"]), ["action"] = "route", ["outbound"] = "direct" }); }
        trustTunnel?.AddRelays(inbounds, routes);
        var dnsRules = new JsonArray(); var sets = new JsonArray(); var setTags = new HashSet<string>();
        if (netBirdControlHosts.Length != 0)
        {
            // Resolving the control plane must work before the corporate adapter exists.
            dnsRules.Add(DnsAction(new JsonObject { ["domain"] = Strings(netBirdControlHosts) }, "direct"));
        }
        var fallbackTags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!probeOnly && spec.Settings.MapLocation && spec.AllowsOriginLookup && spec.OriginProbePort != 0) {
            // A private authenticated channel only for our opt-in HTTPS location lookup.
            // No domain/process exceptions are inserted into the user's traffic policy.
            inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = "origin-probe", ["listen"] = "127.0.0.1", ["listen_port"] = spec.OriginProbePort,
                ["users"] = new JsonArray { new JsonObject { ["username"] = "bebekon", ["password"] = spec.ProbePassword } } });
            routes.Insert(0, new JsonObject { ["inbound"] = Strings(["origin-probe"]), ["domain"] = Strings(MapLocation.LookupHosts), ["port"] = 443, ["action"] = "route", ["outbound"] = "direct" });
            routes.Insert(1, new JsonObject { ["inbound"] = Strings(["origin-probe"]), ["action"] = "reject" });
        }
        foreach (var rule in probeOnly ? [] : spec.Profile.RulesByPrecedence)
        {
            if (rule.Kind is RuleKind.GeoSite or RuleKind.GeoIp)
                foreach (var value in rule.Values) if (setTags.Add(GeoCatalog.Tag(rule.Kind, value))) sets.Add(GeoCatalog.Inline(rule.Kind, value));
            if (rule.Target == RouteTarget.NetBird && rule.Kind == RuleKind.Site)
            {
                foreach (var group in rule.Values.GroupBy(domain => netBird.ForDomain(domain, UnavailableTag(rule))))
                {
                    routes.Add(RouteAction(Match(rule, group, forTraffic: true), group.Key, spec.Settings.TlsFragment));
                    dnsRules.Add(DnsAction(Match(rule, group), group.Key));
                }
                continue;
            }
            var tag = rule.Target switch
            {
                RouteTarget.Vpn => VpnRuleTag(rule, serverTags, outbounds, dnsServers, fallbackTags),
                RouteTarget.NetBird => (rule.Kind is RuleKind.Network or RuleKind.GeoIp ? netBird.AddressTag : netBird.DefaultTag) ?? UnavailableTag(rule),
                _ => RouteTag(rule.Target, netBird)
            };
            routes.Add(RouteAction(Match(rule, forTraffic: true), tag, spec.Settings.TlsFragment));
            if (rule.Kind is not (RuleKind.Network or RuleKind.GeoIp) &&
                !(tag is null && (rule.Port is not null || rule.Transport is not null)))
                dnsRules.Add(DnsAction(Match(rule), tag));
        }
        var finalTag = probeOnly ? "vpn" : RouteTag(spec.Profile.DefaultRoute, netBird);
        if (finalTag is null)
        {
            routes.Add(RouteAction(new(), null, false));
            dnsRules.Add(DnsAction(new(), null));
        }
        // Fragmentation only ever applies to direct connections; inside the VPN it would only add delay.
        else if (spec.Settings.TlsFragment && finalTag == "direct") routes.Add(RouteAction(new(), finalTag, true));
        var route = new JsonObject { ["rules"] = routes, ["final"] = finalTag ?? "vpn", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "direct-dns" };
        if (sets.Count > 0) route["rule_set"] = sets;
        return new JsonObject {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false, ["disabled"] = false },
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules, ["final"] = (finalTag ?? "vpn") + "-dns", ["reverse_mapping"] = true, ["cache_capacity"] = 4096, ["strategy"] = "prefer_ipv4" },
            ["inbounds"] = inbounds, ["outbounds"] = outbounds, ["route"] = route
        }.ToJsonString(Json.Options);
    }
    /// <summary>
    /// Native sing-box servers only: XHTTP and TrustTunnel need per-server companion processes.
    /// The selected server goes first: urltest uses it until the first measurement finishes.
    /// At most 64 are measured, in the caller's order (the app passes the fastest known first).
    /// </summary>
    public static List<Server> AutoServers(ConnectSpec spec) => spec.Settings.AutoServer && spec.AutoServers is { } servers
        ? servers.Prepend(spec.Server).Where(AutoEligible).DistinctBy(n => n.Id).Take(64).ToList() : [];
    public static bool AutoEligible(Server n) => n.Supported && n.Type != "trusttunnel" && n.Transport != "xhttp";
    private static string? UnavailableTag(RoutingRule rule) =>
        rule.UnavailableRoute == RouteTarget.Direct && rule.Allows(RouteTarget.Direct) ? "direct" : null;
    private static string? VpnRuleTag(RoutingRule rule, IReadOnlyDictionary<string, string> serverTags, JsonArray outbounds,
        JsonArray dnsServers, Dictionary<string, string> fallbackTags)
    {
        var primary = rule.ServerId is { } id ? serverTags.GetValueOrDefault(id) : "vpn";
        if (rule.FallbackServerIds is not { Count: > 0 } backup) return primary;
        var candidates = backup.Select(id => serverTags.GetValueOrDefault(id)).Prepend(primary)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return null;
        var key = string.Join(',', candidates);
        if (fallbackTags.TryGetValue(key, out var existing)) return existing;
        if (fallbackTags.Count == 0)
            outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = VpnFallback.Offline });
        var tag = VpnFallback.Prefix + fallbackTags.Count;
        fallbackTags.Add(key, tag);
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector", ["tag"] = tag, ["outbounds"] = Strings(candidates.Append(VpnFallback.Offline)),
            ["default"] = candidates[0], ["interrupt_exist_connections"] = true
        });
        dnsServers.Add(Dns(tag + "-dns", tag));
        return tag;
    }
    private static string? RouteTag(RouteTarget target, NetBirdConfig netBird) => target switch
    {
        RouteTarget.Direct => "direct",
        RouteTarget.Vpn => "vpn",
        RouteTarget.NetBird => netBird.DefaultTag,
        _ => null
    };
    private static JsonObject RouteAction(JsonObject match, string? tag, bool tlsFragment)
    {
        match["action"] = tag is null ? "reject" : "route";
        if (tag is not null) match["outbound"] = tag;
        if (tag == "direct" && tlsFragment) match["tls_fragment"] = true;
        return match;
    }
    private static JsonObject DnsAction(JsonObject match, string? tag)
    {
        match["action"] = tag is null ? "predefined" : "route";
        if (tag is null) match["rcode"] = "REFUSED";
        else match["server"] = tag + "-dns";
        return match;
    }
    private static JsonObject Dns(string tag, string detour) => new() { ["type"] = "https", ["tag"] = tag, ["server"] = "1.1.1.1", ["detour"] = detour, ["tls"] = new JsonObject { ["server_name"] = "cloudflare-dns.com" } };
    private static JsonObject Vpn(Server s, string tag, XhttpRuntime? runtime, TrustTunnelRuntime? trustTunnel)
    {
        ProtocolConfig.Validate(s);
        if (s.Type != "trusttunnel" && s.Transport != "xhttp") return ProtocolConfig.Outbound(s, tag);
        LoopbackBridge bridge;
        if (s.Type == "trusttunnel") bridge = trustTunnel?.Nodes.GetValueOrDefault(s.Id)?.Socks ?? throw new UserError("Для TrustTunnel требуется запуск комплектного клиента.");
        else if (runtime is null || !runtime.Bridges.TryGetValue(s.Id, out bridge!)) throw new UserError("Для XHTTP требуется запуск комплектного ядра Xray.");
        var vpn = new JsonObject { ["type"] = "socks", ["tag"] = tag, ["server"] = "127.0.0.1", ["server_port"] = bridge.Port, ["version"] = "5", ["username"] = "bebekon", ["password"] = bridge.Password, ["connect_timeout"] = "4s" };
        if (!s.UdpEnabled) vpn["network"] = "tcp"; return vpn;
    }
    private static JsonObject Match(RoutingRule r, IEnumerable<string>? values = null, bool forTraffic = false)
    {
        var match = MatchDestination(r, values);
        if (forTraffic && r.Port is { } port) match["port"] = port;
        if (forTraffic && r.Transport is { } transport) match["network"] = transport == RuleTransport.Tcp ? "tcp" : "udp";
        // The application's destination port and protocol are unavailable to a DNS query.
        return match;
    }

    private static JsonObject MatchDestination(RoutingRule r, IEnumerable<string>? values)
    {
        var key = r.Kind switch { RuleKind.Site => "domain_suffix", RuleKind.Contains => "domain_keyword", RuleKind.Network => "ip_cidr", _ => "process_name" };
        if (r.Kind is RuleKind.GeoSite or RuleKind.GeoIp) return new JsonObject { ["rule_set"] = Strings(r.Values.Select(v => GeoCatalog.Tag(r.Kind, v))) };
        if (r.Kind != RuleKind.Application) return new JsonObject { [key] = Strings(values ?? r.Values) };
        var paths = r.Values.Where(Path.IsPathFullyQualified).ToList(); var names = r.Values.Where(v => !Path.IsPathFullyQualified(v)).ToList();
        if (paths.Count > 0 && names.Count > 0) return new JsonObject { ["type"] = "logical", ["mode"] = "or", ["rules"] = new JsonArray { new JsonObject { ["process_path"] = Strings(paths) }, new JsonObject { ["process_name"] = Strings(names) } } };
        return new JsonObject { [paths.Count > 0 ? "process_path" : "process_name"] = Strings(paths.Count > 0 ? paths : names) };
    }
}
