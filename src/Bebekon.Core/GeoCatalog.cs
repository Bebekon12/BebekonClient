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
    public static JsonObject Inline(RuleKind kind, string value)
    {
        var tag = Tag(kind, value);
        if (!Resources.TryGetValue(tag, out var resource)) throw new UserError("Набор GeoSite / GeoIP отсутствует в приложении.");
        using var stream = typeof(GeoCatalog).Assembly.GetManifestResourceStream(resource)!;
        var source = JsonNode.Parse(stream)!;
        return new() { ["type"] = "inline", ["tag"] = tag, ["rules"] = source["rules"]!.DeepClone() };
    }
}
