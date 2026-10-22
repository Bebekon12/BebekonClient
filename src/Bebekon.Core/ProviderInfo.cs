using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace Bebekon.Core;

/// <summary>
/// Optional subscription response headers used by Happ, Clash and panels such as Remnawave or
/// Marzban. Every value is display data only: nothing here changes routing or connection settings.
/// </summary>
public sealed class ProviderInfo
{
    public long Upload { get; set; }
    public long Download { get; set; }
    /// <summary>0 means unlimited traffic.</summary>
    public long Total { get; set; }
    public DateTimeOffset? Expire { get; set; }
    /// <summary>0 when the provider gave no interval.</summary>
    public int UpdateIntervalHours { get; set; }
    public string SupportUrl { get; set; } = "";
    public string WebPageUrl { get; set; } = "";
    public string Announce { get; set; } = "";
    public string Title { get; set; } = "";

    public static ProviderInfo From(HttpResponseHeaders headers)
    {
        string? Header(string name) => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var info = new ProviderInfo
        {
            UpdateIntervalHours = UpdateInterval(Header("profile-update-interval")),
            SupportUrl = SafeLink(Header("support-url")),
            WebPageUrl = SafeLink(Header("profile-web-page-url")),
            Announce = Text(Header("announce"), 300),
            Title = Text(Header("profile-title"), 80),
        };
        info.ParseUsage(Header("subscription-userinfo"));
        return info;
    }

    /// <summary>"upload=1; download=2; total=3; expire=1700000000" in any order; unknown keys are ignored.</summary>
    public void ParseUsage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 512) return;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0) continue;
            switch (pair[0].ToLowerInvariant())
            {
                case "upload": Upload = value; break;
                case "download": Download = value; break;
                case "total": Total = value; break;
                case "expire": Expire = value is > 0 and < 32503680000 ? DateTimeOffset.FromUnixTimeSeconds(value) : null; break;
            }
        }
    }

    public static int UpdateInterval(string? value) => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours > 0 ? Math.Min(hours, 168) : 0;

    /// <summary>Only https links are kept; a subscription cannot make the app open other schemes.</summary>
    public static string SafeLink(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.Length <= 512 && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && !text.Any(char.IsControl) ? uri.AbsoluteUri : "";
    }

    /// <summary>Plain or "base64:" text, single-spaced and bounded.</summary>
    public static string Text(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            try { text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text[7..].Trim())); } catch (Exception e) when (e is FormatException or DecoderFallbackException) { return ""; }
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return new string(text.Where(c => !char.IsControl(c)).ToArray()) is var clean && clean.Length > max ? clean[..max].TrimEnd() + "…" : clean;
    }

    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit >= 3 && value < 100 ? value.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) : Math.Round(value).ToString(CultureInfo.InvariantCulture)) + " " + units[unit];
    }
}
