using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed record DestinationGuard(string[] Domains, string[] Networks)
{
    public static DestinationGuard Empty { get; } = new([], []);
    public const string Resolver = "172.29.255.2";
    public const string FakeV4 = "198.19.0.0/16";
    public const string FakeV6 = "fd84:be:be:1::/96";
    public bool Enabled => Domains.Length != 0 || Networks.Length != 0;
    public string[] Entries => [.. Domains, .. Networks];
    public string[] FilterNetworks => Domains.Length == 0 ? Networks :
        [.. Networks, Resolver + "/32", FakeV4, FakeV6];

    public bool Matches(RouteQuery target)
    {
        var name = target.Domain?.TrimEnd('.');
        return name != null && Domains.Any(d => name.Equals(d,StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + d,StringComparison.OrdinalIgnoreCase)) ||
            target.Address != null && Networks.Any(n => IPNetwork.Parse(n).Contains(target.Address));
    }

    public void CheckEndpoints(ConnectSpec spec)
    {
        if (!Enabled) return;
        var endpoints = ConfigGenerator.AutoServers(spec).Concat(spec.ResolveRuleServers()).Prepend(spec.Server)
            .Select(server => server.Host).Concat(spec.NetBirdControlHosts ?? []);
        foreach (var host in endpoints)
        {
            var endpoint = IPAddress.TryParse(host, out var address) ? new RouteQuery(Address: address) : new RouteQuery(host);
            if (Matches(endpoint)) throw new UserError("Адрес сервера VPN или управления NetBird попал в список защиты. Уберите его из списка либо выберите другой сервер.");
        }
    }

    public static DestinationGuard Parse(IEnumerable<string> lines) => Parse(lines, 128, false);
    internal static DestinationGuard Parse(IEnumerable<string> lines, int limit, bool allowDefaultRoute)
    {
        var domains = new SortedSet<string>(StringComparer.Ordinal);
        var networks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var entry = raw?.Trim() ?? "";
            if (entry.Length == 0) continue;
            if (entry.Length > 253 || entry.Any(char.IsControl)) throw new UserError("Некорректный адрес в списке защиты.");
            if (entry.Contains('/') || IPAddress.TryParse(entry, out _))
            {
                var rule = new RoutingRule { Name = "IP", Kind = RuleKind.Network, Values = [entry] };
                RuleValidation.Validate(rule);
                if (!allowDefaultRoute && rule.Values[0].EndsWith("/0", StringComparison.Ordinal)) throw new UserError("Для всей сети используйте глобальный Kill Switch.");
                networks.Add(rule.Values[0]);
            }
            else
            {
                entry = entry.TrimEnd('.');
                if (entry.StartsWith("*.", StringComparison.Ordinal)) entry = entry[2..];
                try { entry = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(entry).ToLowerInvariant(); }
                catch (ArgumentException) { throw new UserError("Укажите полное доменное имя без URL."); }
                if (!entry.Contains('.') || entry.Length > 253 || Uri.CheckHostName(entry) != UriHostNameType.Dns ||
                    entry.Split('.').Any(part => part.Length is 0 or > 63 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                    throw new UserError("Укажите полное доменное имя без URL.");
                domains.Add(entry);
            }
            if (domains.Count + networks.Count > limit) throw new UserError($"Список может содержать до {limit} адресов.");
        }
        return new([.. domains], [.. networks]);
    }

    internal void Configure(JsonArray routes, JsonArray servers, JsonArray rules)
    {
        static JsonArray Items(string[] values) => new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        if (Networks.Length > 0)
            routes.Add(new JsonObject { ["ip_cidr"] = Items(Networks), ["action"] = "route", ["outbound"] = "vpn" });
        if (Domains.Length == 0) return;
        servers.Add(new JsonObject { ["type"] = "fakeip", ["tag"] = "protected-addresses", ["inet4_range"] = FakeV4, ["inet6_range"] = FakeV6 });
        rules.Add(new JsonObject { ["domain_suffix"] = Items(Domains), ["query_type"] = Items(["A", "AAAA"]),
            ["action"] = "route", ["server"] = "protected-addresses" });
        // HTTPS/SVCB answers may contain real IP hints which would outlive the tunnel.
        rules.Add(new JsonObject { ["domain_suffix"] = Items(Domains), ["action"] = "predefined", ["rcode"] = "NOERROR" });
        routes.Add(new JsonObject { ["domain_suffix"] = Items(Domains), ["action"] = "route", ["outbound"] = "vpn" });
        // A stale synthetic address without a recoverable domain must never fall through to Direct.
        routes.Add(new JsonObject { ["ip_cidr"] = Items([FakeV4, FakeV6]), ["action"] = "reject" });
    }
}
