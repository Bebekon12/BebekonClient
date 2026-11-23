using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>A user-defined group of domains (with subdomains) that a GeoSite rule can reference as <c>my:name</c>.</summary>
public sealed class DomainSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Domains { get; set; } = [];
    /// <summary>Accent shown next to the group: one of <see cref="DomainSets.Colors"/>.</summary>
    public string Color { get; set; } = "Blue";
    /// <summary>Icon shown next to the group: one of <see cref="DomainSets.Icons"/>.</summary>
    public string Icon { get; set; } = "Folder";
    /// <summary>Optional HTTPS address the group follows; its domains are replaced on refresh.</summary>
    public string? SourceUrl { get; set; }
    public DateTimeOffset? Updated { get; set; }
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
    public static readonly string[] Colors = ["Blue", "Cyan", "Emerald", "Amber", "Rose", "Violet"];
    public static readonly string[] Icons = ["Folder", "Briefcase", "Network", "Shield", "Bolt", "Star", "Monitor", "Servers"];
    public const string LinkPrefix = "bebekon://group/";
    public static bool IsReference(string value) => value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    public static string Reference(DomainSet set) => Prefix + set.Name;
    public static string NameOf(string reference) => reference[Prefix.Length..];

    public const string KeywordPrefix = "keyword:";
    /// <summary>What a group entry means: a domain with subdomains, a word in the address, or an IP/CIDR.</summary>
    public static RuleKind KindOf(string entry) =>
        entry.StartsWith(KeywordPrefix, StringComparison.OrdinalIgnoreCase) ? RuleKind.Contains :
        LooksLikeAddress(entry) ? RuleKind.Network : RuleKind.Site;
    private static bool LooksLikeAddress(string entry)
    {
        var address = entry.Split('/')[0];
        // "10.20" parses as an IPv4 address in .NET; require the full dotted form or an IPv6 colon.
        return System.Net.IPAddress.TryParse(address, out _) && (address.Contains(':') || address.Count(c => c == '.') == 3);
    }
    public static string ValueOf(string entry) => KindOf(entry) == RuleKind.Contains ? entry[KeywordPrefix.Length..] : entry;

    /// <summary>
    /// Validates and canonicalizes a group: a short name and normalized, de-duplicated entries —
    /// domains, keyword:word entries and IP/CIDR — each checked like the matching rule type.
    /// </summary>
    public static DomainSet Normalize(string name, IEnumerable<string> domains, string? id = null, DomainSet? style = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 40 || !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new UserError("Название группы: 1–40 символов, буквы, цифры, «-», «_» или «.» без пробелов.");
        var raw = domains.SelectMany(d => d.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(d => !d.StartsWith('#')).ToList();
        if (raw.Count == 0) throw new UserError("Добавьте в группу хотя бы один домен, слово или адрес.");
        if (raw.Count > MaxDomains) throw new UserError($"В группе может быть до {MaxDomains} записей.");
        var entries = new List<string>();
        foreach (var group in raw.GroupBy(KindOf))
        {
            var values = group.Select(entry => group.Key switch
            {
                RuleKind.Contains => entry[KeywordPrefix.Length..].Trim().ToLowerInvariant(),
                RuleKind.Site => RuleValidation.SiteHost(entry, dropWww: true),
                _ => entry
            }).ToList();
            // Reuse the rule validator: the same checks as an ordinary rule of that type.
            var probe = new RoutingRule { Name = name, Kind = group.Key, Values = values };
            RuleValidation.Validate(probe);
            entries.AddRange(probe.Values.Select(v => group.Key == RuleKind.Contains ? KeywordPrefix + v : v));
        }
        return new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"), Name = name,
            Domains = entries.Distinct(StringComparer.Ordinal).OrderBy(e => KindOf(e) switch { RuleKind.Site => 0, RuleKind.Contains => 1, _ => 2 }).ThenBy(e => e, StringComparer.Ordinal).ToList(),
            Color = Colors.Contains(style?.Color) ? style!.Color : "Blue", Icon = Icons.Contains(style?.Icon) ? style!.Icon : "Folder",
            SourceUrl = style?.SourceUrl, Updated = style?.Updated
        };
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
            var entries = new List<string>();
            foreach (var reference in rule.Values.Where(IsReference))
                entries.AddRange((Find(sets, reference) ?? throw new UserError($"Группа «{NameOf(reference)}» удалена. Измените правило «{rule.Name}»."))
                    .Domains);
            var first = true;
            void Add(RuleKind kind, List<string> values, string suffix)
            {
                if (values.Count == 0) return;
                var part = Clone(rule); part.Kind = kind; part.Values = values;
                if (!first) part.Id = rule.Id + suffix;
                first = false; copy.Rules.Add(part);
            }
            // One rule per entry type, in a fixed order, all with the original route and options.
            Add(RuleKind.GeoSite, bundled, "-geo");
            Add(RuleKind.Site, entries.Where(e => KindOf(e) == RuleKind.Site).Distinct(StringComparer.Ordinal).ToList(), "-my");
            Add(RuleKind.Contains, entries.Where(e => KindOf(e) == RuleKind.Contains).Select(ValueOf).Distinct(StringComparer.Ordinal).ToList(), "-kw");
            Add(RuleKind.Network, entries.Where(e => KindOf(e) == RuleKind.Network).Distinct(StringComparer.Ordinal).ToList(), "-net");
        }
        return copy;
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json.Options), Json.Options)!;

    /// <summary>
    /// Downloads the domains a group follows: a shared Bebekon group, or a plain list (one domain per
    /// line, # comments; v2fly "domain:" and "full:" entries are accepted, patterns are skipped).
    /// </summary>
    public static async Task<List<string>> FetchAsync(string url, HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new UserError("Укажите адрес https:// со списком доменов.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string text;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 512 * 1024) throw new UserError("Список по ссылке больше 512 КБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[16384]; int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0) { buffer.Write(chunk, 0, read); if (buffer.Length > 512 * 1024) throw new UserError("Список по ссылке больше 512 КБ."); }
            text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new UserError("Не удалось загрузить список группы по ссылке."); }
        if (text.Contains("\"" + Format + "\"", StringComparison.Ordinal)) return Import(text).Domains;
        var domains = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('#')[0].Trim()).Where(line => line.Length > 0)
            .Select(line => line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ? line[7..] : line.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ? line[5..] : line)
            .Where(line => !line.Contains(':') && !line.Contains('*') && !line.Contains(' ')).ToList();
        if (domains.Count == 0) throw new UserError("По ссылке нет списка доменов.");
        return domains;
    }

    /// <summary>Plain JSON a user can save as a file or paste into a chat.</summary>
    public static string Export(DomainSet set) => Document(set).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    private static JsonObject Document(DomainSet set) => new()
    {
        ["format"] = Format, ["version"] = 1, ["name"] = set.Name, ["color"] = set.Color, ["icon"] = set.Icon,
        ["domains"] = new JsonArray(set.Domains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
    };

    /// <summary>A one-line link for chats: bebekon://group/&lt;base64url(deflate(json))&gt;. Opening it only shows an import preview.</summary>
    public static string ToLink(DomainSet set)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(Document(set).ToJsonString());
        using var packed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(json);
        return LinkPrefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public static bool IsLink(string? text) => text?.TrimStart().StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase) == true;
    /// <summary>Unpacks a group link into the shared JSON text, bounded against oversized payloads.</summary>
    public static string FromLink(string link)
    {
        link = link.Trim();
        if (!IsLink(link) || link.Length > 64 * 1024) throw new UserError("Ссылка на группу Bebekon повреждена.");
        try
        {
            var data = link[LinkPrefix.Length..].Split('?', '#')[0].Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            using var input = new System.IO.Compression.DeflateStream(new MemoryStream(Convert.FromBase64String(data)), System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = input.Read(buffer)) > 0) { output.Write(buffer, 0, read); if (output.Length > 256 * 1024) throw new InvalidDataException(); }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException) { throw new UserError("Ссылка на группу Bebekon повреждена."); }
    }

    public static DomainSet Import(string text)
    {
        text = text.Trim();
        if (text.Contains(LinkPrefix, StringComparison.OrdinalIgnoreCase))
            text = FromLink(text[text.IndexOf(LinkPrefix, StringComparison.OrdinalIgnoreCase)..].Split([' ', '\r', '\n', '\t'], 2)[0]);
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
            var style = new DomainSet { Color = node["color"]?.GetValue<string>() ?? "Blue", Icon = node["icon"]?.GetValue<string>() ?? "Folder" };
            return Normalize(node["name"]?.GetValue<string>() ?? "", domains, style: style);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new UserError("Текст группы повреждён или неполный."); }
    }
}
