using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>One way of opening a site directly and how it went.</summary>
public sealed record ThrottleResult(FragmentMethod? Method, int Succeeded, int Attempts, long? MedianMs)
{
    public bool Works => Succeeded == Attempts && Attempts > 0;
}

/// <summary>
/// Finds which TLS splitting the user's provider needs for a site. A short-lived local sing-box
/// (loopback proxy only) dials the site directly from the physical uplink address, bypassing any
/// running tunnel, once without splitting and once per method.
/// </summary>
public static class ThrottleProbe
{
    public static readonly FragmentMethod?[] Variants = [null, FragmentMethod.Split, FragmentMethod.Records, FragmentMethod.Both];

    public static async Task<ThrottleResult[]> TestAsync(string singBox, string host, IPAddress? uplink, int attempts, CancellationToken ct, Action<FragmentMethod?>? progress = null)
    {
        host = RuleValidation.SiteHost(host.Trim()).ToLowerInvariant();
        if (!host.Contains('.') || Uri.CheckHostName(host) != UriHostNameType.Dns) throw new UserError("Укажите домен сайта, например youtube.com.");
        var results = new List<ThrottleResult>();
        var root = Path.Combine(Paths.UserRoot, "runtime", "throttle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var method in Variants)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(method);
                results.Add(await TestOneAsync(singBox, host, uplink, method, attempts, root, ct));
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return [.. results];
    }

    /// <summary>
    /// Opens each site once directly from the uplink without any splitting by Bebekon. Used with zapret
    /// running: its WinDivert driver works on the network adapter, so these connections pass through it.
    /// Stops at the first site that fails.
    /// </summary>
    public static async Task<(bool Works, long? MedianMs, string? Failed)> TestSitesAsync(string singBox, IReadOnlyList<string> hosts, IPAddress? uplink, CancellationToken ct)
    {
        var root = Path.Combine(Paths.UserRoot, "runtime", "zapret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var times = new List<long>();
            foreach (var raw in hosts)
            {
                var host = RuleValidation.SiteHost(raw.Trim()).ToLowerInvariant();
                var result = await TestOneAsync(singBox, host, uplink, null, 1, root, ct);
                if (!result.Works) return (false, null, host);
                times.Add(result.MedianMs ?? 0);
            }
            times.Sort();
            return (true, times.Count == 0 ? null : times[times.Count / 2], null);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>The fastest method that always worked, or null when the site opens without splitting (or never).</summary>
    public static FragmentMethod? Recommend(IReadOnlyList<ThrottleResult> results)
    {
        if (results.FirstOrDefault(r => r.Method is null) is { Works: true }) return null;
        return results.Where(r => r.Method is not null && r.Works).OrderBy(r => r.MedianMs).FirstOrDefault()?.Method;
    }

    internal static string Config(int port, IPAddress? uplink, FragmentMethod? method)
    {
        var route = new JsonObject { ["inbound"] = new JsonArray("probe"), ["action"] = "route", ["outbound"] = "direct" };
        if (method is { } m) DirectTrafficPolicy.Mark(route, m);
        var direct = new JsonObject { ["type"] = "direct", ["tag"] = "direct", ["domain_resolver"] = new JsonObject { ["server"] = "local", ["strategy"] = "ipv4_only" } };
        if (uplink is not null) direct[uplink.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "inet4_bind_address" : "inet6_bind_address"] = uplink.ToString();
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = false },
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["tag"] = "probe", ["listen"] = "127.0.0.1", ["listen_port"] = port }),
            ["outbounds"] = new JsonArray(direct),
            ["route"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject { ["inbound"] = new JsonArray("probe"), ["action"] = "sniff" }, route), ["final"] = "direct" },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" }) }
        }.ToJsonString(Json.Options);
    }

    private static async Task<ThrottleResult> TestOneAsync(string singBox, string host, IPAddress? uplink, FragmentMethod? method, int attempts, string root, CancellationToken ct)
    {
        var port = LatencyService.FreePort();
        var path = Path.Combine(root, (method?.ToString() ?? "none") + ".json");
        await File.WriteAllTextAsync(path, Config(port, uplink, method), ct);
        using var core = new CoreProcess(singBox, new(Path.Combine(root), "throttle"));
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await core.StartAsync(path, startup.Token);
        }
        var times = new List<long>();
        for (var i = 0; i < attempts; i++)
        {
            // A new connection each time: the handshake is what the provider inspects.
            using var http = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + port), UseProxy = true, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.Zero })
            { Timeout = TimeSpan.FromSeconds(8) };
            var clock = Stopwatch.StartNew();
            try
            {
                // Read a little of the body: throttling often lets the handshake through and stalls the data.
                using var response = await http.GetAsync("https://" + host + "/", HttpCompletionOption.ResponseHeadersRead, ct);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[64 * 1024]; var total = 0;
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct); read.CancelAfter(TimeSpan.FromSeconds(6));
                int n; while (total < 256 * 1024 && (n = await body.ReadAsync(buffer, read.Token)) > 0) total += n;
                times.Add(clock.ElapsedMilliseconds);
            }
            catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        times.Sort();
        return new(method, times.Count, attempts, times.Count == 0 ? null : times[times.Count / 2]);
    }
}
