using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>Which direct connections get their TLS handshake split.</summary>
public enum FragmentScope
{
    /// <summary>Known throttled services plus the user's list (and the user's own direct rules).</summary>
    Auto,
    /// <summary>Only the user's list (and the user's own direct rules).</summary>
    ListOnly,
    /// <summary>Every direct connection — the pre-0.1.33 behaviour; can break some sites.</summary>
    AllDirect
}
/// <summary>How the handshake is split: TCP segments, TLS records, or both.</summary>
public enum FragmentMethod { Split, Records, Both }

public sealed class DirectTrafficPolicy
{
    public string[] Include { get; set; } = [];
    public string[] Exclude { get; set; } = [];
    public bool PreferTcp { get; set; }
    public FragmentScope Scope { get; set; } = FragmentScope.Auto;
    public FragmentMethod Method { get; set; } = FragmentMethod.Both;
    /// <summary>
    /// Bundled GeoSite sets that Auto covers: services that are commonly slowed down by SNI.
    /// QUIC to them is refused in Auto, because only a TCP handshake can be split.
    /// </summary>
    public static readonly string[] ThrottledServices = ["youtube", "discord", "twitter", "instagram", "facebook", "tiktok"];

    public sealed record Targets(string[] Domains, string[] Networks, string[] ExactDomains)
    {
        public bool Enabled => Domains.Length + Networks.Length + ExactDomains.Length > 0;
        public string[] Entries => [.. Domains, .. ExactDomains.Select(name => "^" + name), .. Networks];
    }

    public static Targets ParseList(IEnumerable<string> entries)
    {
        var parts = entries.Select(line => (line ?? "").Split('#', 2)[0].Trim()).ToArray();
        var normal = DestinationGuard.Parse(parts.Where(line => !line.StartsWith('^')), 8192, true);
        var strict = parts.Where(line => line.StartsWith('^')).Select(line => line[1..]).ToArray();
        if (strict.Any(line => line.Length == 0 || line.StartsWith('*'))) throw new UserError("После ^ укажите точное доменное имя.");
        var exact = DestinationGuard.Parse(strict, 8192, true);
        if (exact.Networks.Length > 0 || normal.Entries.Length + exact.Domains.Length > 8192) throw new UserError("Проверьте точные имена и размер списка.");
        return new(normal.Domains, normal.Networks, exact.Domains);
    }

    /// <summary>The route-action fields for the chosen method.</summary>
    public static void Mark(JsonObject route, FragmentMethod method)
    {
        if (method is FragmentMethod.Split or FragmentMethod.Both) route["tls_fragment"] = true;
        if (method is FragmentMethod.Records or FragmentMethod.Both) route["tls_record_fragment"] = true;
    }

    /// <summary>
    /// Rewrites the direct routes the generator marked with <c>tls_fragment</c>. A user's own direct
    /// rule keeps fragmentation (minus exclusions): that site was chosen on purpose. The catch-all
    /// direct route is fragmented only within <see cref="Scope"/>, so ordinary sites are untouched.
    /// </summary>
    internal void Apply(JsonArray routes, JsonArray sets)
    {
        var include = ParseList(Include ?? []);
        var exclude = ParseList(Exclude ?? []);
        var setTags = sets.OfType<JsonObject>().Select(set => (string?)set["tag"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        void AddSet(string tag, Targets list)
        {
            var entries = new JsonArray();
            if (list.Domains.Length != 0) entries.Add(new JsonObject { ["domain_suffix"] = Array(list.Domains) });
            if (list.ExactDomains.Length != 0) entries.Add(new JsonObject { ["domain"] = Array(list.ExactDomains) });
            if (list.Networks.Length != 0) entries.Add(new JsonObject { ["ip_cidr"] = Array(list.Networks) });
            sets.Add(new JsonObject { ["type"] = "inline", ["tag"] = tag, ["rules"] = entries }); setTags.Add(tag);
        }
        if (include.Enabled) AddSet("direct-include", include);
        if (exclude.Enabled) AddSet("direct-exclude", exclude);
        var services = Scope == FragmentScope.Auto
            ? ThrottledServices.Where(name => GeoCatalog.Contains(RuleKind.GeoSite, name)).Select(name => GeoCatalog.Tag(RuleKind.GeoSite, name)).ToArray()
            : [];
        foreach (var name in ThrottledServices.Where(name => services.Contains(GeoCatalog.Tag(RuleKind.GeoSite, name))))
            if (setTags.Add(GeoCatalog.Tag(RuleKind.GeoSite, name))) sets.Add(GeoCatalog.Inline(RuleKind.GeoSite, name));
        // Sets that put the catch-all route in scope; an empty list means "no catch-all fragmentation".
        string[] scopeSets = Scope switch
        {
            FragmentScope.AllDirect => [],
            FragmentScope.ListOnly => include.Enabled ? ["direct-include"] : [],
            _ => [.. include.Enabled ? new[] { "direct-include" } : [], .. services]
        };

        for (var i = routes.Count - 1; i >= 0; i--)
        {
            if (routes[i] is not JsonObject original || (bool?)original["tls_fragment"] != true) continue;
            original.Remove("tls_fragment");
            var baseline = (JsonObject)original.DeepClone();
            baseline.Remove("action"); baseline.Remove("outbound");
            var catchAll = baseline.Count == 0;
            // null = no restriction (whole route); empty = nothing in scope.
            string[]? fragmentWithin = !catchAll || Scope == FragmentScope.AllDirect ? null : scopeSets;
            // QUIC cannot be split, so refuse it and browsers retry over TCP: always for the bundled
            // services in Auto, and for the whole fragmented scope when the user prefers TCP.
            string[]? quicWithin = PreferTcp ? fragmentWithin : catchAll && services.Length > 0 ? services : [];
            JsonObject Match(JsonObject transport, string[]? within)
            {
                var parts = new JsonArray();
                if (!catchAll) parts.Add(baseline.DeepClone());
                if (within is not null) parts.Add(new JsonObject { ["rule_set"] = Array(within) });
                if (exclude.Enabled) parts.Add(new JsonObject { ["rule_set"] = "direct-exclude", ["invert"] = true });
                parts.Add(transport);
                return new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = parts };
            }
            if (!catchAll && !exclude.Enabled) Mark(original, Method);
            else if (fragmentWithin is not { Length: 0 })
            {
                var tcp = Match(new JsonObject { ["network"] = "tcp" }, fragmentWithin);
                tcp["action"] = "route"; tcp["outbound"] = "direct"; Mark(tcp, Method);
                routes.Insert(i, tcp);
            }
            if (quicWithin is not { Length: 0 })
            {
                var quic = Match(new JsonObject { ["network"] = "udp", ["port"] = 443, ["protocol"] = "quic" }, quicWithin);
                quic["action"] = "reject"; routes.Insert(i, quic);
            }
        }
    }

    private static JsonArray Array(string[] values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
