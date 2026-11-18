using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed class DirectTrafficPolicy
{
    public string[] Include { get; set; } = [];
    public string[] Exclude { get; set; } = [];
    public bool PreferTcp { get; set; }

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

    internal void Apply(JsonArray routes, JsonArray sets)
    {
        var include = ParseList(Include ?? []);
        var exclude = ParseList(Exclude ?? []);
        if (!include.Enabled && !exclude.Enabled && !PreferTcp) return;
        void AddSet(string tag, Targets list)
        {
            var entries = new JsonArray();
            if (list.Domains.Length != 0) entries.Add(new JsonObject { ["domain_suffix"] = Array(list.Domains) });
            if (list.ExactDomains.Length != 0) entries.Add(new JsonObject { ["domain"] = Array(list.ExactDomains) });
            if (list.Networks.Length != 0) entries.Add(new JsonObject { ["ip_cidr"] = Array(list.Networks) });
            sets.Add(new JsonObject { ["type"] = "inline", ["tag"] = tag, ["rules"] = entries });
        }
        if (include.Enabled) AddSet("direct-include", include);
        if (exclude.Enabled) AddSet("direct-exclude", exclude);
        // Refine only routes already selected for Direct. Infrastructure, VPN, Block
        // and NetBird retain their original precedence and actions.
        for (var i = routes.Count - 1; i >= 0; i--)
        {
            if (routes[i] is not JsonObject original || (bool?)original["tls_fragment"] != true) continue;
            original.Remove("tls_fragment");
            JsonObject Match(JsonObject transport)
            {
                var parts = new JsonArray();
                var baseline = (JsonObject)original.DeepClone();
                baseline.Remove("action"); baseline.Remove("outbound");
                if (baseline.Count > 0) parts.Add(baseline);
                if (include.Enabled) parts.Add(new JsonObject { ["rule_set"] = "direct-include" });
                if (exclude.Enabled) parts.Add(new JsonObject { ["rule_set"] = "direct-exclude", ["invert"] = true });
                parts.Add(transport);
                return new JsonObject { ["type"] = "logical", ["mode"] = "and", ["rules"] = parts };
            }
            var tcp = Match(new JsonObject { ["network"] = "tcp" });
            tcp["action"] = "route"; tcp["outbound"] = "direct"; tcp["tls_fragment"] = true;
            routes.Insert(i,tcp);
            if (PreferTcp)
            {
                var quic = Match(new JsonObject { ["network"] = "udp", ["port"] = 443, ["protocol"] = "quic" });
                quic["action"] = "reject"; routes.Insert(i,quic);
            }
        }
    }

    private static JsonArray Array(string[] values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
