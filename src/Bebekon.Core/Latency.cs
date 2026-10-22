using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace Bebekon.Core;

public sealed record LatencyResult(long? Milliseconds, LatencyMode Mode, DateTimeOffset MeasuredAt, bool TimedOut = false, string? Country = null);
public sealed class LatencyService(string executable)
{
    private readonly ConcurrentDictionary<string, LatencyResult> cache = new();
    private readonly SemaphoreSlim fastLimit = new(6);
    private readonly SemaphoreSlim exactLimit = new(6);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    // Process launch, config check and antivirus scans are not network latency. They get their
    // own budget, so several parallel checks no longer report a reachable server as a timeout.
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private readonly LocationLookup locations = new();
    /// <summary>After an HTTPS check, ask a location service through the server for nameless nodes.</summary>
    public bool DetectCountries { get; set; }
    public async Task<LatencyResult> MeasureAsync(Server server, LatencyMode mode, bool force, CancellationToken ct, Action? started = null)
    {
        ct.ThrowIfCancellationRequested();
        var key = server.Id + mode + ServerRefresh.ConnectionKey(server);
        if (!force && cache.TryGetValue(key, out var prior) && DateTimeOffset.UtcNow - prior.MeasuredAt < TimeSpan.FromMinutes(3)) return prior;
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var limit = mode is LatencyMode.Tcp or LatencyMode.Icmp ? fastLimit : exactLimit;
        await limit.WaitAsync(ct);
        try
        {
            started?.Invoke();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (mode is LatencyMode.Tcp or LatencyMode.Icmp) deadline.CancelAfter(ProbeTimeout);
            var probeToken = deadline.Token;
            long? ms = null;
            string? country = null;
            var probeTimedOut = false;
            try
            {
                if (mode == LatencyMode.Tcp)
                {
                    ms = await TcpLatency.MeasureAsync(server.Host, server.Port, probeToken);
                }
                else if (mode == LatencyMode.Icmp)
                {
                    var addresses = await Dns.GetHostAddressesAsync(server.Host, probeToken);
                    var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                    if (address is not null)
                    {
                        var times = new List<long>();
                        for (var i = 0; i < 3; i++)
                        {
                            using var ping = new Ping();
                            var reply = await ping.SendPingAsync(address, ProbeTimeout, cancellationToken: probeToken);
                            if (reply.Status != IPStatus.Success) { probeTimedOut = reply.Status == IPStatus.TimedOut; break; }
                            times.Add(reply.RoundtripTime);
                        }
                        if (times.Count == 3) { times.Sort(); ms = times[1]; }
                    }
                }
                else if (server.Supported) (ms, probeTimedOut, country) = await ExactAsync(server, mode == LatencyMode.HttpsHead ? HttpMethod.Head : HttpMethod.Get, probeToken);
            }
            catch (Exception e) when (e is SocketException or PingException or HttpRequestException or OperationCanceledException or UserError or IOException) { ct.ThrowIfCancellationRequested(); }
            ct.ThrowIfCancellationRequested();
            var result = new LatencyResult(ms, mode, DateTimeOffset.UtcNow, deadline.IsCancellationRequested || probeTimedOut, country); cache[key] = result; return result;
        }
        finally { limit.Release(); }
    }
    public static int FreePort()
    {
        // Windows reserves different ranges for TCP and UDP. Mixed/SOCKS bridges need both.
        for (var attempt = 0; attempt < 256; attempt++)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            try { tcp.Start(); return port; } catch (SocketException) { }
        }
        throw new UserError("Не удалось найти свободный локальный порт VPN.");
    }
    private async Task<(long? Milliseconds, bool TimedOut, string? Country)> ExactAsync(Server server, HttpMethod method, CancellationToken ct)
    {
        var detect = DetectCountries && CountryInfo.Resolve(server.Name) is null && server.DetectedCountry.Length == 0;
        var root = Path.Combine(Paths.UserRoot, "runtime", "probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var spec = new ConnectSpec(server, new(), new(), FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        try
        {
            // Startup and child-process shutdown can wait on the OS. Keep both off the
            // WPF dispatcher so completed results, map motion and Cancel remain responsive.
            return await Task.Run(async () => {
                using var core = new CoreSession(executable, new(Paths.Logs, "core"));
                // TrustTunnel's startup includes connecting to the endpoint, so it is network time.
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    startup.CancelAfter(server.IsTrustTunnel ? ProbeTimeout : StartupTimeout);
                    try { await core.StartAsync(spec, root, true, startup.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ((long?)null, true, (string?)null); }
                }
                using var http = ProbeClient(spec); http.Timeout = Timeout.InfiniteTimeSpan;
                long ms;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deadline.CancelAfter(ProbeTimeout);
                    try { ms = await MeasureHttpAsync(http, method, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ((long?)null, true, (string?)null); }
                }
                string? country = null;
                if (detect)
                {
                    // The service sees the VPN exit address, never the user's own IP.
                    using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct); lookup.CancelAfter(TimeSpan.FromSeconds(12));
                    try { country = (await locations.LookupAsync(http, null, lookup.Token).ConfigureAwait(false))?.Country; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    if (string.IsNullOrEmpty(country)) country = null;
                }
                return ((long?)ms, false, country);
            }, ct).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, true); }
    }
    internal static async Task<long> MeasureHttpAsync(HttpClient http, HttpMethod method, CancellationToken ct)
    {
        // Measure one complete, cold request, as on Android. Repeating requests both slows
        // a large scan and reports a warm HTTP connection rather than initial VPN access.
        var sw = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(method, "https://www.gstatic.com/generate_204");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException("HTTPS-проверка не вернула ожидаемый ответ 204.");
        return sw.ElapsedMilliseconds;
    }
    public static HttpClient ProbeClient(ConnectSpec spec) => new(new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + spec.ProbePort) { Credentials = new NetworkCredential("bebekon", spec.ProbePassword) }, UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
    public static async Task<string> VpnIpAsync(ConnectSpec spec, CancellationToken ct)
    {
        using var http = ProbeClient(spec); var text = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
        if (!IPAddress.TryParse(text, out _)) throw new UserError("Сервис проверки IP вернул неверный ответ."); return text;
    }
}
