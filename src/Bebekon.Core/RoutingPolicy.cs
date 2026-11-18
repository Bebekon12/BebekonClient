using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

public sealed record RouteQuery(string? Domain = null, IPAddress? Address = null, string? Executable = null,
    RuleTransport? Transport = null, int? Port = null);

public sealed record RouteAnalysis(RoutingRule? Rule, RouteTarget Direction, RouteTarget WhenUnavailable, bool DependsOnConnectionDetails);

public static class RoutingPolicy
{
    public static RouteAnalysis Analyze(Profile profile, RouteQuery query)
    {
        var conditional = false;
        foreach (var rule in profile.RulesByPrecedence)
        {
            var match = Matches(rule, query);
            if (match is null) conditional = true;
            if (match == true)
            {
                var fallback = rule.FallbackServerIds is { Count: > 0 } ? RouteTarget.Vpn :
                    rule.UnavailableRoute ?? RouteTarget.Block;
                return new(rule, rule.Target, fallback, conditional);
            }
        }
        return new(null, profile.DefaultRoute, RouteTarget.Block, conditional);
    }

    public static string RuleSignature(RoutingRule rule) =>
        string.Join('|', rule.Kind, rule.Target, rule.ServerId, rule.Transport, rule.Port, rule.UnavailableRoute, rule.SystemProtection,
            string.Join(',', rule.AllowedRoutes?.Order() ?? Enumerable.Empty<RouteTarget>()),
            string.Join(',', rule.ForbiddenRoutes?.Order() ?? Enumerable.Empty<RouteTarget>()),
            string.Join(',', rule.FallbackServerIds ?? []), string.Join('\n', rule.Values).ToLowerInvariant(),
            string.Join('\n', (rule.ProtectedExecutables ?? []).Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant());

    private static bool? Matches(RoutingRule rule, RouteQuery query)
    {
        if (rule.Transport is { } transport && query.Transport is { } actual && transport != actual) return false;
        if (rule.Port is { } port && query.Port is { } actualPort && port != actualPort) return false;

        bool? selected = rule.Kind switch
        {
            RuleKind.Site => query.Domain is { } domain ? rule.Values.Any(value => HasSuffix(domain, value)) : null,
            RuleKind.Contains => query.Domain is { } name ? rule.Values.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase)) : null,
            RuleKind.Application => MatchApplication(rule.Values, query.Executable),
            RuleKind.Network => query.Address is { } address ? rule.Values.Any(value => InNetwork(address, value)) : null,
            RuleKind.GeoSite => query.Domain is { } host ? MatchGeo(rule, host, null) : null,
            RuleKind.GeoIp => query.Address is { } ip ? MatchGeo(rule, null, ip) : null,
            _ => null
        };
        if (selected == true && (rule.Transport is not null && query.Transport is null || rule.Port is not null && query.Port is null))
            return null;
        return selected;
    }

    private static bool? MatchApplication(IEnumerable<string> values, string? executable)
    {
        if (executable is null) return null;
        var resolvedPath = Path.IsPathFullyQualified(executable);
        var fileName = Path.GetFileName(executable);
        var sameName = false;
        foreach (var value in values)
        {
            if (!Path.GetFileName(value).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Path.IsPathFullyQualified(value) || value.Equals(executable, StringComparison.OrdinalIgnoreCase)) return true;
            sameName = true;
        }
        return !resolvedPath && sameName ? null : false;
    }

    private static bool? MatchGeo(RoutingRule rule, string? host, IPAddress? address)
    {
        var unknown = false;
        foreach (var name in rule.Values)
        {
            var rules = GeoCatalog.Inline(rule.Kind, name)["rules"]!.AsArray();
            foreach (var row in rules.OfType<JsonObject>())
            {
                if (address is not null && Values(row["ip_cidr"]).Any(range => InNetwork(address, range))) return true;
                if (host is null) continue;
                if (Values(row["domain"]).Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)) ||
                    Values(row["domain_suffix"]).Any(suffix => HasSuffix(host, suffix))) return true;
                foreach (var pattern in Values(row["domain_regex"]))
                {
                    try { if (Regex.IsMatch(host, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true; }
                    catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { unknown = true; }
                }
            }
        }
        return unknown ? null : false;
    }

    private static IEnumerable<string> Values(JsonNode? value) => value switch
    {
        JsonArray array => array.Select(node => (string)node!),
        JsonValue single => [single.GetValue<string>()],
        _ => []
    };

    private static bool InNetwork(IPAddress address, string range) =>
        IPAddress.TryParse(range, out var single) ? address.Equals(single) :
        IPNetwork.TryParse(range, out var network) && network.Contains(address);

    private static bool HasSuffix(string host, string suffix) => suffix.StartsWith('.') ?
        host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) :
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
}
