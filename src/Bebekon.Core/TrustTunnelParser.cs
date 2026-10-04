using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Bebekon.Core;

/// <summary>Official TrustTunnel TLV v0–2, exported TOML and subscription JSON. Never executes provider settings.</summary>
public static class TrustTunnelParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool LooksLikeToml(string content) => Regex.IsMatch(content, @"(?:^|[\r\n])\s*(?:\[endpoint\]|hostname\s*=|addresses\s*=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static bool LooksLikeJson(JsonElement e) => e.ValueKind == JsonValueKind.Object && (e.TryGetProperty("endpoint", out _) || e.TryGetProperty("hostname", out _) && (e.TryGetProperty("addresses", out _) || e.TryGetProperty("address", out _)))
        || e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0 && LooksLikeJson(e[0]);
    public static List<Server> ParseToml(string content)
    {
        try { var root = TomlSerializer.Deserialize<TomlTable>(content, new TomlSerializerOptions { MaxDepth = 16 }) ?? throw TrustTunnelConfig.Invalid(); return ParseJson(JsonSerializer.SerializeToElement(root)); }
        catch (Exception e) when (e is TomlException or JsonException or InvalidOperationException) { throw new UserError("Повреждённая TOML-конфигурация TrustTunnel."); }
    }
    internal static List<Server> ParseJson(JsonElement root)
    {
        try
        {
            CheckDuplicates(root);
            var nodes = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
            if (nodes.Length is < 1 or > 5000) throw TrustTunnelConfig.Invalid();
            return nodes.Select(ParseEndpoint).DistinctBy(s => s.Id).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or CryptographicException) { throw TrustTunnelConfig.Invalid(); }
    }
    private static Server ParseEndpoint(JsonElement source)
    {
        var root = source; if (root.ValueKind != JsonValueKind.Object) throw TrustTunnelConfig.Invalid();
        if (root.TryGetProperty("endpoint", out var endpoint)) root = endpoint;
        if (root.ValueKind != JsonValueKind.Object) throw TrustTunnelConfig.Invalid();
        string Text(string key, string fallback = "") => !root.TryGetProperty(key, out var v) ? fallback : v.ValueKind == JsonValueKind.String ? v.GetString()! : throw TrustTunnelConfig.Invalid();
        bool Flag(string key, bool fallback) => !root.TryGetProperty(key, out var v) ? fallback : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw TrustTunnelConfig.Invalid();
        List<string> Strings(string key) => !root.TryGetProperty(key, out var v) ? [] : v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(p => p.ValueKind == JsonValueKind.String) ? v.EnumerateArray().Select(p => p.GetString()!).ToList() : throw TrustTunnelConfig.Invalid();
        if (root.TryGetProperty("version", out var schemaVersion) && (!schemaVersion.TryGetInt32(out var v) || v != 1)) throw new UserError("Эта версия JSON-подписки TrustTunnel пока не поддерживается.");
        var addresses = Strings("addresses"); if (addresses.Count == 0 && root.TryGetProperty("address", out _)) addresses.Add(Text("address"));
        if (addresses.Count == 0) throw TrustTunnelConfig.Invalid();
        var first = TrustTunnelConfig.Address(addresses[0]);
        // Future endpoint tuning must not be silently lost. A full client's outer listener/routing sections are ignored.
        var known = new[] { "version", "hostname", "address", "addresses", "username", "password", "custom_sni", "has_ipv6", "skip_verification", "certificate", "upstream_protocol", "anti_dpi", "client_random", "client_random_prefix", "name", "dns_upstreams", "subscription_url", "post_quantum_group_enabled" };
        if (root.EnumerateObject().Any(p => !known.Contains(p.Name))) throw new UserError("Конфигурация содержит параметры TrustTunnel, которые эта версия пока не поддерживает.");
        var o = new TrustTunnelOptions { Username = Text("username"), Addresses = addresses, CustomSni = Text("custom_sni"), HasIpv6 = Flag("has_ipv6", true), Certificate = Text("certificate"), ClientRandom = Text("client_random", Text("client_random_prefix")), AntiDpi = Flag("anti_dpi", false), DnsUpstreams = Strings("dns_upstreams") };
        if (source.TryGetProperty("post_quantum_group_enabled", out var postQuantum)) o.PostQuantum = postQuantum.ValueKind is JsonValueKind.True or JsonValueKind.False ? postQuantum.GetBoolean() : throw TrustTunnelConfig.Invalid();
        var s = new Server { Type = "trusttunnel", Host = first.Host, Port = first.Port, Name = Text("name", first.Host), Security = "tls", Sni = Text("hostname"), Password = Text("password"), TlsInsecure = Flag("skip_verification", false), Transport = Text("upstream_protocol", "http2").ToLowerInvariant(), TrustTunnel = o };
        if (s.Name.Length == 0) s.Name = first.Host;
        TrustTunnelConfig.Validate(s);
        s.Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Name + ServerRefresh.ConnectionKey(s))))[..24];
        return s;
    }
    private static void CheckDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(); foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw TrustTunnelConfig.Invalid(); CheckDuplicates(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) CheckDuplicates(v);
    }
    public static Server ParseLink(string input)
    {
        var decoded = DecodeLink(input);
        if (!decoded.Endpoint.ContainsKey("addresses")) throw new UserError("Эта ссылка TrustTunnel содержит подписку. Добавьте её через страницу «Подписки».");
        return ParseEndpoint(JsonSerializer.SerializeToElement(decoded.Endpoint));
    }
    internal static string? SubscriptionUrl(string input) => DecodeLink(input).Subscription;
    private static (JsonObject Endpoint, string? Subscription) DecodeLink(string input)
    {
        try
        {
            if (!input.StartsWith("tt://", StringComparison.OrdinalIgnoreCase) || input.Length > 131072) throw TrustTunnelConfig.Invalid();
            var text = input[5..]; if (text.StartsWith('?')) text = text[1..];
            if (text.Length == 0 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw TrustTunnelConfig.Invalid();
            var data = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));
            var endpoint = new JsonObject(); var addresses = new JsonArray(); int offset = 0; ulong version = 0; string? subscription = null;
            while (offset < data.Length)
            {
                var tag = VarInt(data, ref offset); var length = VarInt(data, ref offset);
                if (length > (ulong)(data.Length - offset)) throw TrustTunnelConfig.Invalid();
                var value = data.AsSpan(offset, (int)length).ToArray(); offset += (int)length;
                bool Boolean() => value.Length == 1 && value[0] is 0 or 1 ? value[0] == 1 : throw TrustTunnelConfig.Invalid();
                string String() => Utf8.GetString(value);
                switch (tag)
                {
                    case 0: int p = 0; version = VarInt(value, ref p); if (p != value.Length || version > 2) throw new UserError("Эта версия ссылки TrustTunnel пока не поддерживается. Обновите клиент."); break;
                    case 1: endpoint["hostname"] = String(); break;
                    case 2: addresses.Add(String()); if (addresses.Count > 16) throw TrustTunnelConfig.Invalid(); break;
                    case 3: endpoint["custom_sni"] = String(); break;
                    case 4: endpoint["has_ipv6"] = Boolean(); break;
                    case 5: endpoint["username"] = String(); break;
                    case 6: endpoint["password"] = String(); break;
                    case 7: endpoint["skip_verification"] = Boolean(); break;
                    case 8: endpoint["certificate"] = CertificatePem(value); break;
                    case 9: endpoint["upstream_protocol"] = value.Length == 1 ? value[0] switch { 1 => "http2", 2 => "http3", _ => throw TrustTunnelConfig.Invalid() } : throw TrustTunnelConfig.Invalid(); break;
                    case 10: endpoint["anti_dpi"] = Boolean(); break;
                    case 11: endpoint["client_random"] = String(); break;
                    case 12: endpoint["name"] = String(); break;
                    case 13:
                        var dns = new JsonArray(); int n = 0;
                        while (n < value.Length) { var size = VarInt(value, ref n); if (size > (ulong)(value.Length - n) || dns.Count >= 16) throw TrustTunnelConfig.Invalid(); dns.Add(Utf8.GetString(value.AsSpan(n, (int)size))); n += (int)size; }
                        endpoint["dns_upstreams"] = dns; break;
                    case 14: subscription = String(); break;
                    // Unknown tags are ignored as specified; their full varint value is never truncated.
                }
            }
            if (addresses.Count > 0) endpoint["addresses"] = addresses;
            if (subscription is not null && (version < 2 || !Uri.TryCreate(subscription, UriKind.Absolute, out var u) || u.Scheme != "https" || !TrustTunnelConfig.Host(u.IdnHost))) throw TrustTunnelConfig.Invalid();
            if (subscription is null || addresses.Count > 0) ParseEndpoint(JsonSerializer.SerializeToElement(endpoint));
            return (endpoint, subscription);
        }
        catch (Exception e) when (e is FormatException or DecoderFallbackException or AsnContentException or CryptographicException or ArgumentException or OverflowException) { throw TrustTunnelConfig.Invalid(); }
    }
    private static ulong VarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length) throw TrustTunnelConfig.Invalid();
        int size = 1 << (data[offset] >> 6); if (size > data.Length - offset) throw TrustTunnelConfig.Invalid();
        ulong result = (ulong)(data[offset++] & 63); for (int i = 1; i < size; i++) result = (result << 8) | data[offset++]; return result;
    }
    private static string CertificatePem(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 1 or > 65536) throw TrustTunnelConfig.Invalid();
        var result = new StringBuilder(); int count = 0;
        while (!value.IsEmpty)
        {
            var tag = AsnDecoder.ReadEncodedValue(value, AsnEncodingRules.DER, out _, out _, out int consumed);
            if (tag != Asn1Tag.Sequence || ++count > 16) throw TrustTunnelConfig.Invalid();
            using var cert = X509CertificateLoader.LoadCertificate(value[..consumed]); result.AppendLine(cert.ExportCertificatePem()); value = value[consumed..];
        }
        return result.ToString();
    }
}
