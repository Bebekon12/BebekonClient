namespace Bebekon.Core;

/// <summary>
/// "Add to Bebekon" links on provider pages: bebekon://add/&lt;link&gt; (the Happ-style form) and
/// bebekon://import?url=&lt;link&gt;. They only prefill the subscription field; the user still
/// confirms, and nothing connects or changes on its own. Same rules as the Android client.
/// </summary>
public static class ImportLink
{
    public const string Scheme = "bebekon";
    private static readonly string[] Schemes = ["https", "vless", "vmess", "ss", "trojan", "hysteria", "hysteria2", "hy2", "tuic", "tt"];

    public static bool IsLink(string? text) => text?.TrimStart().StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The link to import, or null when the bebekon:// link is malformed or points elsewhere.</summary>
    public static string? Decode(string text)
    {
        var value = text.Trim();
        if (!IsLink(value) || value.Length > 16_384) return null;
        var rest = value[(Scheme.Length + 3)..];
        string? link = null;
        if (rest.StartsWith("add/", StringComparison.OrdinalIgnoreCase)) { link = rest[4..]; if (!link.Contains("://")) link = DecodePart(link); }
        else if (rest.StartsWith("import?", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("install-config?", StringComparison.OrdinalIgnoreCase))
            link = rest[(rest.IndexOf('?') + 1)..].Split('&').FirstOrDefault(p => p.StartsWith("url=", StringComparison.OrdinalIgnoreCase)) is { } part ? DecodePart(part[4..]) : null;
        link = link?.Trim();
        if (string.IsNullOrEmpty(link) || link.Any(char.IsControl)) return null;
        var separator = link.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !Schemes.Contains(link[..separator].ToLowerInvariant())) return null;
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length > 0)) return null;
        return link;
    }

    private static string? DecodePart(string value) { try { return Uri.UnescapeDataString(value); } catch (UriFormatException) { return null; } }
}
