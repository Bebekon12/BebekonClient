using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Bebekon.Core;

/// <summary>A domain the running tunnel recently carried, for "add to group" on the Connections page.</summary>
public sealed record RecentDestination(string Host, int Port, string? Process, string Route, DateTimeOffset LastSeen, int Connections);

/// <summary>
/// In-memory only, bounded and cleared with the core. Built from the core's own connection list,
/// which the helper already reads for traffic counters; nothing is written to disk or logs.
/// </summary>
public sealed class RecentDestinationLog
{
    public const int Limit = 400;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, RecentDestination> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> seen = [];

    /// <summary>Reads the "connections" array of a Clash API /connections response.</summary>
    public void Observe(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var current = new HashSet<string>();
        foreach (var connection in list.EnumerateArray())
        {
            if (!connection.TryGetProperty("metadata", out var meta)) continue;
            var host = Text(meta, "host");
            if (host.Length == 0) host = Text(meta, "sniffHost");
            host = host.TrimEnd('.').ToLowerInvariant();
            // Domains only: bare IPs cannot join a domain group, and loopback/probe traffic is internal.
            if (host.Length is 0 or > 253 || IPAddress.TryParse(host, out _) || !host.Contains('.') || host.EndsWith(".localhost", StringComparison.Ordinal)) continue;
            var id = Text(connection, "id");
            var process = Path.GetFileName(Text(meta, "processPath")) is { Length: > 0 } path ? path : Text(meta, "process");
            // Bebekon's own probes, location lookup and protocol bridges are not user traffic.
            var inbound = Text(meta, "type");
            if (inbound.Contains("probe", StringComparison.Ordinal) || inbound.Contains("xray-direct", StringComparison.Ordinal) || inbound.Contains("relay", StringComparison.Ordinal)) continue;
            var route = Route(connection);
            _ = int.TryParse(Text(meta, "destinationPort"), out var port);
            var isNew = id.Length > 0 && current.Add(id) && !seen.Contains(id);
            entries.AddOrUpdate(host,
                _ => new(host, port, process.Length == 0 ? null : process, route, now, 1),
                (_, old) => old with { LastSeen = now, Port = port, Route = route, Process = process.Length == 0 ? old.Process : process, Connections = old.Connections + (isNew ? 1 : 0) });
        }
        lock (seen) { seen.Clear(); seen.UnionWith(current); }
        foreach (var stale in entries.Values.Where(entry => now - entry.LastSeen > Window).Select(entry => entry.Host).ToArray()) entries.TryRemove(stale, out _);
        if (entries.Count > Limit)
            foreach (var old in entries.Values.OrderBy(entry => entry.LastSeen).Take(entries.Count - Limit).Select(entry => entry.Host).ToArray()) entries.TryRemove(old, out _);
    }
    public RecentDestination[] Snapshot() => entries.Values.OrderByDescending(entry => entry.LastSeen).ThenBy(entry => entry.Host, StringComparer.Ordinal).ToArray();

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => ""
    } : "";
    /// <summary>Maps the first outbound in the chain to what the user sees: VPN, Direct, NetBird or Block.</summary>
    private static string Route(JsonElement connection)
    {
        var tag = connection.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array && chains.GetArrayLength() > 0
            ? chains[chains.GetArrayLength() - 1].GetString() ?? "" : "";
        return tag switch
        {
            "direct" or "direct-bridge" => "Direct",
            "block" => "Block",
            _ when tag.StartsWith("netbird", StringComparison.Ordinal) => "NetBird",
            _ when tag.StartsWith("vpn", StringComparison.Ordinal) || tag.StartsWith("auto-", StringComparison.Ordinal) => "VPN",
            _ when tag.Length == 0 => "Direct",
            _ => "VPN"
        };
    }
}
