using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>A user-defined group of domains (with subdomains) that a GeoSite rule can reference as <c>my:name</c>.</summary>
public sealed class DomainSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Domains { get; set; } = [];
    public override string ToString() => Name;
}

/// <summary>
/// User sets live only in the user's encrypted state. Before a profile reaches the privileged helper
/// or leaves the PC, references are expanded into ordinary domain rules, so the helper still accepts
/// only bundled GeoSite sets.
/// </summary>
public static class DomainSets
{
    public const string Prefix = "my:";
    public const int MaxDomains = 2000;
    private const string Format = "bebekon-domain-set";
    public static bool IsReference(string value) => value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    public static string Reference(DomainSet set) => Prefix + set.Name;
    public static string NameOf(string reference) => reference[Prefix.Length..];

    /// <summary>Validates and canonicalizes a set: a short name and normalized, de-duplicated domains.</summary>
    public static DomainSet Normalize(string name, IEnumerable<string> domains, string? id = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 40 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new UserError("Название группы: 1–40 символов, буквы, цифры, «-», «_» или «.» без пробелов.");
        var values = domains.SelectMany(d => d.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(d => !d.StartsWith('#')).Select(d => RuleValidation.SiteHost(d, dropWww: true)).ToList();
        if (values.Count == 0) throw new UserError("Добавьте в группу хотя бы один домен.");
        if (values.Count > MaxDomains) throw new UserError($"В группе может быть до {MaxDomains} доменов.");
        // Reuse the rule validator: same IDN, host and format rules as an ordinary site rule.
        var probe = new RoutingRule { Name = name, Kind = RuleKind.Site, Values = values };
        RuleValidation.Validate(probe);
        return new() { Id = id ?? Guid.NewGuid().ToString("N"), Name = name, Domains = probe.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() };
    }

    public static DomainSet? Find(IEnumerable<DomainSet> sets, string reference)
        => IsReference(reference) ? sets.FirstOrDefault(s => s.Name.Equals(NameOf(reference), StringComparison.OrdinalIgnoreCase)) : null;
    public static IEnumerable<RoutingRule> RulesUsing(IEnumerable<Profile> profiles, DomainSet set)
        => profiles.SelectMany(p => p.Rules).Where(r => r.Kind == RuleKind.GeoSite && r.Values.Any(v => IsReference(v) && NameOf(v).Equals(set.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Returns a copy of the profile where every GeoSite rule that references user sets becomes a
    /// GeoSite rule for its bundled sets (if any) directly followed by a site rule with the sets' domains.
    /// Order, route and all options are preserved, so priority does not change.
    /// </summary>
    public static Profile Expand(Profile profile, IReadOnlyCollection<DomainSet> sets)
    {
        if (!profile.Rules.Any(r => r.Kind == RuleKind.GeoSite && r.Values.Any(IsReference))) return profile;
        var copy = Clone(profile);
        copy.Rules.Clear();
        foreach (var rule in profile.Rules)
        {
            if (rule.Kind != RuleKind.GeoSite || !rule.Values.Any(IsReference)) { copy.Rules.Add(Clone(rule)); continue; }
            var bundled = rule.Values.Where(v => !IsReference(v)).ToList();
            var domains = new List<string>();
            foreach (var reference in rule.Values.Where(IsReference))
                domains.AddRange((Find(sets, reference) ?? throw new UserError($"Группа «{NameOf(reference)}» удалена. Измените правило «{rule.Name}»."))
                    .Domains);
            if (bundled.Count > 0) { var geo = Clone(rule); geo.Values = bundled; copy.Rules.Add(geo); }
            var site = Clone(rule); site.Kind = RuleKind.Site; site.Values = domains.Distinct(StringComparer.Ordinal).ToList();
            if (bundled.Count > 0) site.Id = rule.Id + "-my";
            copy.Rules.Add(site);
        }
        return copy;
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json.Options), Json.Options)!;

    /// <summary>Plain JSON a user can save as a file or paste into a chat.</summary>
    public static string Export(DomainSet set) => new JsonObject
    {
        ["format"] = Format, ["version"] = 1, ["name"] = set.Name,
        ["domains"] = new JsonArray(set.Domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static DomainSet Import(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > 256 * 1024) throw new UserError("Вставьте текст группы Bebekon или выберите файл.");
        // Tolerate text copied from a chat with surrounding words or code fences.
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new UserError("Это не группа доменов Bebekon.");
        try
        {
            var node = JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new JsonException();
            if (node["format"]?.GetValue<string>() != Format) throw new UserError("Это не группа доменов Bebekon.");
            if (node["version"]?.GetValue<int>() is not 1) throw new UserError("Группа создана более новой версией Bebekon. Обновите приложение.");
            var domains = (node["domains"] as JsonArray ?? throw new JsonException()).Select(d => d?.GetValue<string>() ?? throw new JsonException());
            return Normalize(node["name"]?.GetValue<string>() ?? "", domains);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new UserError("Текст группы повреждён или неполный."); }
    }
}
