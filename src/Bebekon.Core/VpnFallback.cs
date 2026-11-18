using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

internal sealed class VpnFallback(HttpClient controller, Uri localEndpoint, VpnFallback.Group[] groups)
{
    public const string Prefix = "vpn-fallback-";
    public const string Offline = "vpn-unavailable";
    private const string HealthUrl = "https://www.gstatic.com/generate_204";

    internal sealed record Group(string Tag, string[] OrderedOutbounds)
    {
        public string Current { get; set; } = OrderedOutbounds[0];
        public volatile bool Healthy;
    }

    public static Group[] ReadGroups(JsonObject config) =>
        (config["outbounds"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(node => (string?)node["type"] == "selector" && ((string?)node["tag"])?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .Select(node => new Group((string)node["tag"]!, node["outbounds"]!.AsArray().Select(item => (string)item!)
                .Where(tag => tag != Offline).ToArray()))
            .ToArray();

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            do { await RefreshAsync(ct); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    internal async Task RefreshAsync(CancellationToken ct)
    {
        using var probes = new SemaphoreSlim(4);
        var health = new ConcurrentDictionary<string, Lazy<Task<bool>>>(StringComparer.Ordinal);
        Task<bool> Available(string tag) => health.GetOrAdd(tag, candidate =>
            new Lazy<Task<bool>>(() => ProbeAsync(candidate, probes, ct))).Value;

        async Task SelectAsync(Group group)
        {
            var selected = Offline;
            foreach (var candidate in group.OrderedOutbounds)
            {
                if (await Available(candidate)) { selected = candidate; break; }
            }
            if (group.Current == selected) { group.Healthy = selected != Offline; return; }
            group.Healthy = false;
            try
            {
                using var reply = await controller.PutAsJsonAsync(ProxyUri(group.Tag), new { name = selected }, ct);
                if (reply.IsSuccessStatusCode)
                {
                    group.Current = selected;
                    group.Healthy = selected != Offline;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        await Task.WhenAll(groups.Select(SelectAsync));
    }

    private async Task<bool> ProbeAsync(string tag, SemaphoreSlim limit, CancellationToken ct)
    {
        await limit.WaitAsync(ct);
        try
        {
            var request = new Uri(ProxyUri(tag).AbsoluteUri + "/delay?timeout=4000&url=" + Uri.EscapeDataString(HealthUrl));
            using var reply = await controller.GetAsync(request, ct);
            return reply.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        finally { limit.Release(); }
    }

    private Uri ProxyUri(string tag) => new(localEndpoint, "proxies/" + Uri.EscapeDataString(tag));
}
