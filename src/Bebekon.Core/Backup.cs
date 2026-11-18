using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed record BackupSubscription(string Name, string Source, bool Hidden);
public sealed record BackupData(List<BackupSubscription> Subscriptions, List<RoutingRule> Rules, Settings? Settings, int Skipped, RouteTarget? DefaultRoute = null);

/// <summary>
/// A backup file shared with the Android client. Subscriptions keep only their source, so
/// importing re-downloads current servers. Site, keyword, IP and geo rules move between
/// platforms; program rules and settings restore only on Windows.
/// </summary>
public static class BackupCodec
{
    public const string Format = "bebekon-backup";
    private const int MaxLength = 4 * 1024 * 1024;
    private static readonly Dictionary<RuleKind, string> Kinds = new() { [RuleKind.Site] = "site", [RuleKind.Contains] = "contains", [RuleKind.Network] = "cidr", [RuleKind.GeoSite] = "geoSite", [RuleKind.GeoIp] = "geoIp", [RuleKind.Application] = "windowsApp" };

    public static string Export(AppState state, Profile profile) => new JsonObject
    {
        ["format"] = Format, ["version"] = 1, ["created"] = DateTimeOffset.UtcNow.ToString("O"), ["platform"] = "windows",
        ["subscriptions"] = new JsonArray(state.Subscriptions.Where(s => s.Source.Length > 0)
            .Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["source"] = s.Source, ["hidden"] = s.Hidden }).ToArray()),
        // Priority order: the first matching rule wins, as on Windows.
        ["rules"] = new JsonArray(profile.Rules.Select(r => (JsonNode?)ExportRule(r)).ToArray()),
        ["windowsDefaultRoute"] = JsonSerializer.SerializeToNode(profile.DefaultRoute, Json.Options),
        ["windowsSettings"] = JsonSerializer.SerializeToNode(state.Settings, Json.Options),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonObject ExportRule(RoutingRule rule)
    {
        if (!rule.Enabled || rule.Target is RouteTarget.NetBird or RouteTarget.Block || rule.Port is not null ||
            rule.Transport is not null || rule.AllowedRoutes is not null || rule.ForbiddenRoutes is not null ||
            rule.FallbackServerIds is { Count: > 0 } || rule.UnavailableRoute is not null || rule.SystemProtection)
        {
            // Other clients skip unknown kinds and never activate a reduced form of a Windows policy.
            return new() { ["kind"] = "windowsPolicy", ["policy"] = JsonSerializer.SerializeToNode(rule, Json.Options) };
        }
        return new()
        {
            ["name"] = rule.Name, ["kind"] = Kinds[rule.Kind], ["vpn"] = rule.Target == RouteTarget.Vpn,
            ["values"] = new JsonArray(rule.Values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        };
    }

    public static BackupData Import(string text)
    {
        if (text.Length > MaxLength) throw new UserError("Файл резервной копии слишком большой.");
        JsonNode? root;
        try { root = JsonNode.Parse(text); } catch (JsonException) { root = null; }
        if (root is not JsonObject o || (string?)o["format"] != Format || o["version"]?.GetValueKind() != JsonValueKind.Number || (int)o["version"]! != 1) throw new UserError("Это не резервная копия Bebekon VPN.");
        var skipped = 0;
        var subscriptions = new List<BackupSubscription>();
        foreach (var s in o["subscriptions"] as JsonArray ?? [])
        {
            var source = (s?["source"]?.GetValueKind() == JsonValueKind.String ? (string?)s["source"] : null)?.Trim() ?? "";
            if (source.Length is 0 or > MaxLength) { skipped++; continue; }
            var name = s?["name"]?.GetValueKind() == JsonValueKind.String ? ((string)s["name"]!).Trim() : "";
            subscriptions.Add(new(name.Length > 0 ? name[..Math.Min(80, name.Length)] : "Подписка", source, s?["hidden"]?.GetValueKind() == JsonValueKind.True));
        }
        var rules = new List<RoutingRule>();
        foreach (var r in o["rules"] as JsonArray ?? [])
        {
            try
            {
                if ((string?)r?["kind"] == "windowsPolicy")
                {
                    var policy = r["policy"]?.Deserialize<RoutingRule>(Json.Options) ?? throw new UserError("Не указана политика правила.");
                    RuleValidation.Validate(policy);
                    policy.Id = Guid.NewGuid().ToString("N");
                    rules.Add(policy);
                    continue;
                }
                var kind = Kinds.FirstOrDefault(k => k.Value == (string?)r?["kind"]).Key;
                if (r?["kind"] is null || !Kinds.ContainsValue((string)r["kind"]!)) { skipped++; continue; }
                var values = (r["values"] as JsonArray ?? []).Select(v => ((string?)v)?.Trim() ?? "").Where(v => v.Length > 0).ToList();
                var name = ((string?)r["name"])?.Trim() ?? "";
                var rule = new RoutingRule { CreatedAt = DateTimeOffset.UtcNow, Name = name.Length > 0 ? name[..Math.Min(160, name.Length)] : values.FirstOrDefault() ?? "Правило", Kind = kind, Values = values, UseVpn = r["vpn"]?.GetValueKind() != JsonValueKind.False };
                RuleValidation.Validate(rule); rules.Add(rule);
            }
            catch (Exception e) when (e is UserError or InvalidOperationException or FormatException or JsonException or NullReferenceException) { skipped++; }
        }
        Settings? settings = null;
        try { settings = o["windowsSettings"]?.Deserialize<Settings>(Json.Options); } catch (JsonException) { }
        RouteTarget? defaultRoute = null;
        try
        {
            if (o["windowsDefaultRoute"]?.Deserialize<RouteTarget>(Json.Options) is { } route && Enum.IsDefined(route)) defaultRoute = route;
        }
        catch (JsonException) { }
        return new(subscriptions, rules, settings, skipped, defaultRoute);
    }
}
