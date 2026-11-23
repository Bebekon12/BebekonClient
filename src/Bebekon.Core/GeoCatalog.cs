using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>Trusted, embedded headless rule sets. No user-writable paths or runtime downloads.</summary>
public static class GeoCatalog
{
    private const string Prefix = "Bebekon.Core.Geo.";
    private static readonly Dictionary<string, string> Resources = typeof(GeoCatalog).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
        .ToDictionary(n => n[Prefix.Length..^5], n => n, StringComparer.Ordinal);
    public static IReadOnlyList<string> Available(RuleKind kind) => Resources.Keys.Where(n => n.StartsWith(KindPrefix(kind), StringComparison.Ordinal)).Select(n => n[KindPrefix(kind).Length..]).Order().ToArray();
    private static string KindPrefix(RuleKind kind) => kind switch { RuleKind.GeoSite => "geosite-", RuleKind.GeoIp => "geoip-", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public static string Tag(RuleKind kind, string value) => KindPrefix(kind) + value.ToLowerInvariant();
    public static bool Contains(RuleKind kind, string value) => Resources.ContainsKey(Tag(kind, value));
    /// <summary>Exact and suffix domains of a bundled GeoSite set; patterns a group cannot express are counted, not copied.</summary>
    public static (List<string> Domains, int Skipped) Domains(string value)
    {
        var rules = Inline(RuleKind.GeoSite, value)["rules"]!.AsArray();
        var domains = new List<string>(); var skipped = 0;
        foreach (var rule in rules.OfType<JsonObject>())
            foreach (var (key, list) in rule)
            {
                var items = list switch { JsonArray array => array.Select(v => v?.GetValue<string>()).OfType<string>().ToArray(), JsonValue one when one.TryGetValue<string>(out var text) => [text], _ => [] };
                if (key is "domain" or "domain_suffix") domains.AddRange(items.Select(d => d.TrimStart('.')));
                else skipped += items.Length;
            }
        return (domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
    public static JsonObject Inline(RuleKind kind, string value)
    {
        var tag = Tag(kind, value);
        if (!Resources.TryGetValue(tag, out var resource)) throw new UserError("Набор GeoSite / GeoIP отсутствует в приложении.");
        using var stream = typeof(GeoCatalog).Assembly.GetManifestResourceStream(resource)!;
        var source = JsonNode.Parse(stream)!;
        return new() { ["type"] = "inline", ["tag"] = tag, ["rules"] = source["rules"]!.DeepClone() };
    }
}
