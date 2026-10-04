using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Bebekon.Core;

public sealed class TrustTunnelOptions
{
    public string Username { get; set; } = "";
    public List<string> Addresses { get; set; } = [];
    public string CustomSni { get; set; } = "";
    public bool HasIpv6 { get; set; } = true;
    public string Certificate { get; set; } = "";
    public string ClientRandom { get; set; } = "";
    public bool AntiDpi { get; set; }
    public bool PostQuantum { get; set; } = true;
    public List<string> DnsUpstreams { get; set; } = [];
}

internal static class TrustTunnelConfig
{
    internal const string Version = "1.1.7";
    internal static UserError Invalid() => new("Некорректная конфигурация TrustTunnel. Проверьте адреса, логин, пароль и параметры TLS.");
    internal static bool Safe(string text, int max) => text.Length <= max && !text.Any(char.IsControl);
    internal static bool Host(string text) => Safe(text, 253) && Uri.CheckHostName(text) != UriHostNameType.Unknown
        && (!IPAddress.TryParse(text, out var ip) || !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any));
    internal static (string Host, int Port) Address(string address)
    {
        if (!Safe(address, 300) || !Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" && uri.AbsolutePath != "" || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Port is < 1 or > 65535 || !Host(uri.IdnHost.Trim('[', ']'))) throw Invalid();
        return (uri.IdnHost.Trim('[', ']'), uri.Port);
    }
    internal static void Validate(Server s)
    {
        var o = s.TrustTunnel;
        if (o is null || o.Addresses is null || o.DnsUpstreams is null || !Host(s.Host) || !Host(s.Sni) || s.Port is < 1 or > 65535
            || s.Security != "tls" || s.Transport is not ("http2" or "http3" or "auto")
            || o.Addresses.Count is < 1 or > 16 || !Safe(o.Username, 1024) || o.Username.Length == 0 || o.Username.Contains(':')
            || !Safe(s.Password, 4096) || s.Password.Length == 0 || !Safe(o.CustomSni, 253) || o.CustomSni.Length > 0 && !Host(o.CustomSni)
            || !Safe(s.Name, 1024) || o.DnsUpstreams.Count > 16 || o.DnsUpstreams.Any(v => !DnsAddress(v))) throw Invalid();
        foreach (var address in o.Addresses) Address(address);
        var first = Address(o.Addresses[0]);
        if (s.Host != first.Host || s.Port != first.Port) throw Invalid();
        var parts = o.ClientRandom.Split('/');
        if (parts.Length > 2 || parts.Any(v => v.Length > 64 || v.Length % 2 != 0 || v.Any(c => !Uri.IsHexDigit(c)))
            || parts.Length == 2 && (parts[0].Length == 0 || parts[1].Length != parts[0].Length)) throw Invalid();
        if (o.Certificate.Length > 0) ValidateCertificate(o.Certificate);
    }
    private static bool DnsAddress(string text)
    {
        if (!Safe(text, 2048) || text.Length == 0) return false;
        if (text.StartsWith("sdns://", StringComparison.Ordinal)) return text.Length > 7;
        if (IPAddress.TryParse(text, out _)) return true;
        if (!text.Contains("://", StringComparison.Ordinal)) { try { Address(text); return true; } catch (UserError) { return false; } }
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "tcp" or "tls" or "https" or "quic" && u.UserInfo.Length == 0 && Host(u.IdnHost.Trim('[', ']'));
    }
    internal static void ValidateCertificate(string pem)
    {
        if (pem.Length > 65536) throw Invalid();
        try
        {
            var remaining = pem.AsSpan(); int count = 0;
            while (PemEncoding.TryFind(remaining, out var fields))
            {
                if (!remaining[fields.Label].SequenceEqual("CERTIFICATE")) throw Invalid();
                if (!remaining[..fields.Location.Start.GetOffset(remaining.Length)].Trim().IsEmpty) throw Invalid();
                using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(remaining[fields.Base64Data].ToString()));
                if (++count > 16) throw Invalid();
                remaining = remaining[fields.Location.End.GetOffset(remaining.Length)..];
            }
            if (count == 0 || !remaining.Trim().IsEmpty) throw Invalid();
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw Invalid(); }
    }
    internal static string Generate(Server s, LoopbackBridge socks, IEnumerable<int> relayPorts)
    {
        Validate(s); var o = s.TrustTunnel!;
        static string Q(string value) => JsonSerializer.Serialize(value);
        static string Array(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Q)) + "]";
        static string Bool(bool value) => value ? "true" : "false";
        // Only connection fields are copied. TUN, DNS policy, exclusions, files and hooks remain ours.
        return $"loglevel = \"info\"\nvpn_mode = \"general\"\nkillswitch_enabled = true\npost_quantum_group_enabled = {Bool(o.PostQuantum)}\nexclusions = []\n\n[endpoint]\n"
            + $"hostname = {Q(s.Sni)}\naddresses = {Array(relayPorts.Select(p => "127.0.0.1:" + p))}\nusername = {Q(o.Username)}\npassword = {Q(s.Password)}\n"
            + $"has_ipv6 = {Bool(o.HasIpv6)}\ncustom_sni = {Q(o.CustomSni)}\nskip_verification = {Bool(s.TlsInsecure)}\ncertificate = {Q(o.Certificate)}\n"
            + $"upstream_protocol = {Q(s.Transport)}\nanti_dpi = {Bool(o.AntiDpi)}\nclient_random = {Q(o.ClientRandom)}\ndns_upstreams = {Array(o.DnsUpstreams)}\n\n[listener.socks]\n"
            + $"address = \"127.0.0.1:{socks.Port}\"\nusername = \"bebekon\"\npassword = {Q(socks.Password)}\n";
    }
}
