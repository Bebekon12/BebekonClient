using System.Text.Json;

namespace Bebekon.Core;

public static class PresetCatalog
{
    public static List<Preset> Load()
    {
        using var services = typeof(PresetCatalog).Assembly.GetManifestResourceStream("Bebekon.Core.Presets.json")!;
        using var admin = typeof(PresetCatalog).Assembly.GetManifestResourceStream("Bebekon.Core.Admin.json")!;
        return [JsonSerializer.Deserialize<Preset>(admin, Json.Options)!, .. JsonSerializer.Deserialize<List<Preset>>(services, Json.Options)!];
    }
    // Compare against the existing profile, preserving intentional duplicates within the screenshot preset.
    public static int Apply(Preset preset, Profile profile)
    {
        static string Signature(RoutingRule r) => $"{r.Kind}|{r.UseVpn}|{r.ServerId}|{string.Join('\n', r.Values).ToLowerInvariant()}";
        var existing = profile.Rules.Select(Signature).ToHashSet(StringComparer.Ordinal);
        var added = preset.CreateRules().Where(r => !existing.Contains(Signature(r))).ToList();
        foreach (var rule in added) RuleValidation.Validate(rule);
        // Exceptions go before broad service sets already present in the profile.
        var directIndex = 0;
        foreach (var rule in added) { if (rule.UseVpn) profile.Rules.Add(rule); else profile.Rules.Insert(directIndex++, rule); }
        return added.Count;
    }
}
