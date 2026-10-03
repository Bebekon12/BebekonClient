using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

/// <summary>Country labels supplied by the provider; this does not infer server geolocation.</summary>
public static class CountryInfo
{
    private static readonly Dictionary<string, string[]> Names = Load();
    private static readonly Regex Prefix = new(@"^\[?(?<code>[a-z]{2})\]?(?=$|[\s·|:/_\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly (string Name, string Code)[] Aliases = Names
        .SelectMany(p => p.Value.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n, Code: p.Key)))
        .Concat(new (string Name, string Code)[] { ("USA", "US"), ("UK", "GB"), ("Britain", "GB"), ("Великобритания", "GB"), ("Нидерланды", "NL"), ("Корея", "KR"), ("Европа", "EU"), ("Europe", "EU") })
        .OrderByDescending(p => p.Name.Length).ToArray();

    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(CountryInfo).Assembly.GetManifestResourceStream("Bebekon.Core.Countries.json")!;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.EnumerateArray().Select(value => value.GetString() ?? "").ToArray());
    }
    public static string? Resolve(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var runes = label.EnumerateRunes().ToArray();
        for (var i = 0; i < runes.Length - 1; i++)
        {
            if (runes[i].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[i + 1].Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                var code = $"{(char)('A' + runes[i].Value - 0x1F1E6)}{(char)('A' + runes[i + 1].Value - 0x1F1E6)}";
                if (Names.ContainsKey(code)) return code;
            }
        }
        var match = Prefix.Match(label.Trim());
        if (match.Success)
        {
            var code = match.Groups["code"].Value.ToUpperInvariant();
            if (code == "UK") return "GB";
            if (Names.ContainsKey(code)) return code;
        }
        foreach (var (name, code) in Aliases)
        {
            var index = label.IndexOf(name, StringComparison.InvariantCultureIgnoreCase);
            if (index >= 0 && (index == 0 || !char.IsLetter(label[index - 1])) &&
                (index + name.Length == label.Length || !char.IsLetter(label[index + name.Length]))) return code;
        }
        return null;
    }
    public static string DisplayName(string label)
    {
        var text = label.Trim();
        if (text.EnumerateRunes().Take(2).All(r => r.Value is >= 0x1F1E6 and <= 0x1F1FF) && text.Length >= 4)
            text = text[4..].TrimStart(' ', '·', '|', '-', ':');
        var prefix = Prefix.Match(text);
        if (prefix.Success && Resolve(prefix.Value) is not null)
            text = text[prefix.Length..].TrimStart(' ', '·', '|', '-', ':');
        return string.IsNullOrWhiteSpace(text) ? label : text;
    }
}
