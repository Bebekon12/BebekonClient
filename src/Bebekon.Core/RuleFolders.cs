using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>
/// A folder in the rules list. Purely visual: the rules inside keep their own routes and priority,
/// they are only kept next to each other, can be collapsed and shared together.
/// </summary>
public sealed class RuleFolder : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string name = "";
    public string Name { get => name; set => Set(ref name, value); }
    private string color = "Blue";
    public string Color { get => color; set => Set(ref color, value); }
    private string icon = "Folder";
    public string Icon { get => icon; set => Set(ref icon, value); }
    private bool collapsed;
    public bool Collapsed { get => collapsed; set => Set(ref collapsed, value); }
    public override string ToString() => Name;
}

public static class RuleFolders
{
    public static readonly string[] Colors = ["Blue", "Cyan", "Emerald", "Amber", "Rose", "Violet"];
    public static readonly string[] Icons = ["Folder", "Briefcase", "Network", "Shield", "Bolt", "Star", "Monitor", "Servers", "Rules", "Application"];
    public const string LinkPrefix = "bebekon://folder/";
    private const string Format = "bebekon-rule-folder";
    public const int MaxRules = 500;

    public static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new UserError("Название папки: от 1 до 60 символов.");
        return name;
    }

    public static IEnumerable<RoutingRule> RulesIn(Profile profile, RuleFolder folder) => profile.Rules.Where(r => r.FolderId == folder.Id);

    /// <summary>Plain JSON with the folder and its rules. Server pins are left out: servers differ between users.</summary>
    public static string Export(RuleFolder folder, IEnumerable<RoutingRule> rules) => Document(folder, rules).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    private static JsonObject Document(RuleFolder folder, IEnumerable<RoutingRule> rules)
    {
        var list = new JsonArray();
        foreach (var rule in rules)
        {
            var node = JsonSerializer.SerializeToNode(rule, Json.Options)!.AsObject();
            foreach (var field in new[] { "id", "folderId", "serverId", "fallbackServerIds", "createdAt" }) node.Remove(field);
            list.Add(node);
        }
        return new JsonObject { ["format"] = Format, ["version"] = 1, ["name"] = folder.Name, ["color"] = folder.Color, ["icon"] = folder.Icon, ["rules"] = list };
    }

    /// <summary>A one-line link for chats: bebekon://folder/&lt;base64url(deflate(json))&gt;. Opening it only shows a preview.</summary>
    public static string ToLink(RuleFolder folder, IEnumerable<RoutingRule> rules)
    {
        var json = Encoding.UTF8.GetBytes(Document(folder, rules).ToJsonString());
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(json);
        return LinkPrefix + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public static bool IsLink(string? text) => text?.TrimStart().StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase) == true;
    private static string FromLink(string link)
    {
        if (link.Length > 128 * 1024) throw new UserError("Ссылка на папку Bebekon повреждена.");
        try
        {
            var data = link[LinkPrefix.Length..].Split('?', '#')[0].Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            using var input = new DeflateStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = input.Read(buffer)) > 0) { output.Write(buffer, 0, read); if (output.Length > 1024 * 1024) throw new InvalidDataException(); }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException) { throw new UserError("Ссылка на папку Bebekon повреждена."); }
    }

    /// <summary>Reads a shared folder (link, text or file content). Rules get new ids and are validated like any rule.</summary>
    public static (RuleFolder Folder, List<RoutingRule> Rules) Import(string text)
    {
        text = text.Trim();
        var at = text.IndexOf(LinkPrefix, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) text = FromLink(text[at..].Split([' ', '\r', '\n', '\t'], 2)[0]);
        if (text.Length is 0 or > 1024 * 1024) throw new UserError("Вставьте ссылку или текст папки Bebekon.");
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new UserError("Это не папка правил Bebekon.");
        try
        {
            var node = JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new JsonException();
            if (node["format"]?.GetValue<string>() != Format) throw new UserError("Это не папка правил Bebekon.");
            if (node["version"]?.GetValue<int>() is not 1) throw new UserError("Папка создана более новой версией Bebekon. Обновите приложение.");
            var folder = new RuleFolder
            {
                Name = NormalizeName(node["name"]?.GetValue<string>() ?? ""),
                Color = Colors.Contains(node["color"]?.GetValue<string>()) ? node["color"]!.GetValue<string>() : "Blue",
                Icon = Icons.Contains(node["icon"]?.GetValue<string>()) ? node["icon"]!.GetValue<string>() : "Folder"
            };
            var rules = new List<RoutingRule>();
            foreach (var item in node["rules"] as JsonArray ?? throw new JsonException())
            {
                var rule = item.Deserialize<RoutingRule>(Json.Options) ?? throw new JsonException();
                rule.Id = Guid.NewGuid().ToString("N"); rule.FolderId = folder.Id; rule.ServerId = null; rule.FallbackServerIds = null; rule.CreatedAt = DateTimeOffset.UtcNow;
                if (rule.Kind == RuleKind.GeoSite && rule.Values.Any(DomainSets.IsReference)) throw new UserError("Папка ссылается на группу доменов, которой у вас нет.");
                RuleValidation.Validate(rule);
                rules.Add(rule);
            }
            if (rules.Count > MaxRules) throw new UserError($"В папке может быть до {MaxRules} правил.");
            return (folder, rules);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        { throw new UserError("Текст папки повреждён или неполный."); }
    }

    /// <summary>
    /// 0.1.32–0.1.33 had "domain groups" referenced from GeoSite rules as my:name. They become folders:
    /// each referencing rule is replaced by the concrete rules it stood for, inside a folder named after
    /// it; unused groups become folders with disabled Direct rules, so nothing changes behaviour.
    /// </summary>
    public static bool MigrateDomainGroups(AppState state)
    {
        if (state.DomainSets.Count == 0 && !state.Profiles.Any(p => p.Rules.Any(r => r.Values.Any(DomainSets.IsReference)))) return false;
        var sets = state.DomainSets.ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in state.Profiles)
        {
            for (var i = 0; i < profile.Rules.Count; i++)
            {
                var rule = profile.Rules[i];
                if (rule.Kind != RuleKind.GeoSite || !rule.Values.Any(DomainSets.IsReference)) continue;
                var known = new Profile { Rules = [rule] };
                foreach (var reference in rule.Values.Where(DomainSets.IsReference)) used.Add(DomainSets.NameOf(reference));
                List<RoutingRule> replacement;
                try { replacement = [.. DomainSets.Expand(known, sets).Rules]; }
                catch (UserError) { replacement = [.. rule.Values.Where(v => !DomainSets.IsReference(v)).Any() ? DomainSets.Expand(StripMissing(rule), sets).Rules : []]; }
                var style = DomainSets.Find(sets, rule.Values.First(DomainSets.IsReference));
                var folder = new RuleFolder { Name = rule.Name.Length > 0 ? rule.Name : "Группа", Color = style?.Color ?? "Blue", Icon = style?.Icon ?? "Folder" };
                profile.Folders.Add(folder);
                profile.Rules.RemoveAt(i);
                foreach (var part in replacement) { part.FolderId = folder.Id; profile.Rules.Insert(i++, part); }
                i--;
            }
        }
        var target = state.Profiles.FirstOrDefault();
        if (target is not null)
            foreach (var set in sets.Where(s => !used.Contains(s.Name)))
            {
                var folder = new RuleFolder { Name = set.Name, Color = set.Color, Icon = set.Icon };
                target.Folders.Add(folder);
                var holder = new RoutingRule { Name = set.Name, Kind = RuleKind.GeoSite, Values = [DomainSets.Reference(set)], Enabled = false, FolderId = folder.Id };
                foreach (var part in DomainSets.Expand(new Profile { Rules = [holder] }, sets).Rules) { part.FolderId = folder.Id; part.Enabled = false; target.Rules.Add(part); }
            }
        state.DomainSets.Clear();
        return true;
    }
    private static Profile StripMissing(RoutingRule rule)
    {
        var copy = JsonSerializer.Deserialize<RoutingRule>(JsonSerializer.Serialize(rule, Json.Options), Json.Options)!;
        copy.Values = [.. copy.Values.Where(v => !DomainSets.IsReference(v))];
        return new Profile { Rules = [copy] };
    }
}
