using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed record NetBirdDnsZone(string Domain, bool IncludesSubdomains, string? Resolver)
{
    internal bool Matches(string host) => host.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
        IncludesSubdomains && host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase);
}

public sealed record NetBirdRouting(string InterfaceName, int? IPv4Index, int? IPv6Index, string? DefaultResolver, IReadOnlyList<NetBirdDnsZone> Zones)
{
    public static async Task<NetBirdRouting?> DiscoverAsync(string? peerAddress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !IPAddress.TryParse(peerAddress, out var peer) ||
            IPAddress.IsLoopback(peer) || peer.Equals(IPAddress.Any) || peer.Equals(IPAddress.IPv6Any)) return null;

        var matching = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            !adapter.Name.Equals("Bebekon", StringComparison.OrdinalIgnoreCase) &&
            adapter.GetIPProperties().UnicastAddresses.Any(entry => entry.Address.Equals(peer))).Take(2).ToArray();
        if (matching.Length != 1) return null;

        var adapter = matching[0];
        var properties = adapter.GetIPProperties();
        var v4 = properties.GetIPv4Properties()?.Index;
        var v6 = properties.GetIPv6Properties()?.Index;
        bool OnNetBird(IPAddress ip) => WindowsRoutes.BestInterfaceIndex(ip) is { } index &&
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? index == v4 : index == v6);

        var primary = properties.DnsAddresses.FirstOrDefault(OnNetBird)?.ToString();
        IReadOnlyList<NetBirdDnsZone> zones = [];
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        const string query = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            ConvertTo-Json -Compress -InputObject @(Get-DnsClientNrptPolicy -Effective |
              Select-Object Namespace,NameServers,DnsSecValidationRequired,DnsSecQueryIPsecRequired,DirectAccessEnabled,DirectAccessQueryIPsecRequired)
            """;
        try
        {
            var json = await WindowsNetworkCommand.ReadAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", query], TimeSpan.FromSeconds(4), ct);
            zones = ReadZones(json, OnNetBird);
        }
        catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException ||
                                  e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A runtime can still use a verified resolver assigned to the NetBird interface.
        }
        return new(adapter.Name, v4, v6, primary, zones);
    }

    internal static IReadOnlyList<NetBirdDnsZone> ReadZones(string json, Func<IPAddress, bool> onNetBird)
    {
        using var document = JsonDocument.Parse(json);
        var zones = new List<NetBirdDnsZone>();
        foreach (var policy in document.RootElement.EnumerateArray())
        {
            if (!policy.TryGetProperty("Namespace", out var names)) continue;
            // These policies need the Windows DNS client. A raw DNS outbound must refuse them.
            var requiresWindows = new[] { "DnsSecValidationRequired", "DnsSecQueryIPsecRequired", "DirectAccessEnabled", "DirectAccessQueryIPsecRequired" }
                .Any(field => policy.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True);
            var resolver = !requiresWindows && policy.TryGetProperty("NameServers", out var servers)
                ? Strings(servers).SelectMany(value => value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    .Select(value => IPAddress.TryParse(value, out var ip) ? ip : null).FirstOrDefault(ip => ip is not null && onNetBird(ip))
                : null;
            foreach (var name in Strings(names))
            {
                var domain = name.Trim().Trim('.').ToLowerInvariant();
                if (Uri.CheckHostName(domain) == UriHostNameType.Dns && domain.Contains('.'))
                    zones.Add(new(domain, name.TrimStart().StartsWith('.'), resolver?.ToString()));
            }
        }
        return zones.OrderByDescending(zone => zone.Domain.Length).ThenBy(zone => zone.IncludesSubdomains)
            .DistinctBy(zone => (zone.Domain, zone.IncludesSubdomains)).ToArray();
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Array => element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!),
        _ => []
    };
}

internal sealed class NetBirdConfig
{
    private readonly NetBirdRouting? routing;
    private readonly Dictionary<string, string> tags = new(StringComparer.Ordinal);
    public string? DefaultTag { get; }
    public string? AddressTag { get; }

    public NetBirdConfig(NetBirdRouting? routing, JsonArray outbounds, JsonArray dnsServers)
    {
        if (routing is null) return;
        this.routing = routing;
        var scoped = routing.Zones.Select(zone => zone.Resolver).OfType<string>().Distinct().ToArray();
        var defaultResolver = routing.DefaultResolver ?? (scoped.Length == 1 ? scoped[0] : null);
        foreach (var resolver in scoped.Append(defaultResolver).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var tag = "netbird-" + tags.Count;
            tags.Add(resolver, tag);
            dnsServers.Add(new JsonObject
            {
                ["type"] = "udp", ["tag"] = tag + "-dns", ["server"] = resolver, ["bind_interface"] = routing.InterfaceName
            });
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct", ["tag"] = tag, ["bind_interface"] = routing.InterfaceName, ["domain_resolver"] = tag + "-dns"
            });
        }
        if (defaultResolver is not null) DefaultTag = tags[defaultResolver];
        AddressTag = DefaultTag ?? tags.Values.FirstOrDefault();
        if (AddressTag is null)
        {
            // IP-only rules can use a verified adapter before split DNS is ready.
            AddressTag = "netbird-ip";
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct", ["tag"] = AddressTag, ["bind_interface"] = routing.InterfaceName,
                ["domain_resolver"] = "netbird-ip-hosts"
            });
            dnsServers.Add(new JsonObject { ["type"] = "hosts", ["tag"] = "netbird-ip-hosts", ["predefined"] = new JsonObject() });
        }
    }

    public string? ForDomain(string domain, string? unavailableTag)
    {
        var zone = routing?.Zones.FirstOrDefault(zone => zone.Matches(domain));
        return zone is null ? DefaultTag ?? unavailableTag : zone.Resolver is { } resolver ? tags[resolver] : null;
    }
}

public static class WindowsRoutes
{
    public static int? BestInterfaceIndex(IPAddress destination)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var serialized = new IPEndPoint(destination, 0).Serialize();
        var address = serialized.Buffer.ToArray();
        return GetBestInterfaceEx(address, out var index) == 0 ? index : null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx([In] byte[] destination, out int index);
}
