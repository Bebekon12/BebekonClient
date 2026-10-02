using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Bebekon.Core;

public sealed record LatencyResult(long? Milliseconds, LatencyMode Mode, DateTimeOffset MeasuredAt);
public sealed class LatencyService(string executable)
{
    private readonly ConcurrentDictionary<string, LatencyResult> cache = new();
    private readonly SemaphoreSlim limit = new(6);
    public async Task<LatencyResult> MeasureAsync(Server server, LatencyMode mode, bool force, CancellationToken ct)
    {
        var key = server.Id + mode;
        if (!force && cache.TryGetValue(key, out var prior) && DateTimeOffset.UtcNow - prior.MeasuredAt < TimeSpan.FromMinutes(3)) return prior;
        await limit.WaitAsync(ct);
        try
        {
            long? ms = null;
            try
            {
                if (mode == LatencyMode.Fast)
                {
                    var times = new List<long>();
                    for (var i = 0; i < 3; i++) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(3000); using var tcp = new TcpClient(); var sw = Stopwatch.StartNew(); await tcp.ConnectAsync(server.Host, server.Port, timeout.Token); times.Add(sw.ElapsedMilliseconds); }
                    times.Sort(); ms = times[1];
                }
                else if (server.Supported) ms = await ExactAsync(server, ct);
            }
            catch (Exception e) when (e is SocketException or HttpRequestException or OperationCanceledException or UserError or IOException) { ct.ThrowIfCancellationRequested(); }
            var result = new LatencyResult(ms, mode, DateTimeOffset.UtcNow); cache[key] = result; return result;
        }
        finally { limit.Release(); }
    }
    public static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private async Task<long?> ExactAsync(Server server, CancellationToken ct)
    {
        var root = Path.Combine(Paths.UserRoot, "runtime", "probe-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var spec = new ConnectSpec(server, new(), new(), FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var path = Path.Combine(root, "probe.json");
        try
        {
            File.WriteAllText(path, ConfigGenerator.Generate(spec, true));
            using var core = new CoreProcess(executable, new(Paths.Logs, "core")); await core.StartAsync(path, ct);
            using var http = ProbeClient(spec); var times = new List<long>();
            for (var i = 0; i < 3; i++) { var sw = Stopwatch.StartNew(); using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.gstatic.com/generate_204"); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode(); times.Add(sw.ElapsedMilliseconds); }
            times.Sort(); return times[1];
        }
        finally { Directory.Delete(root, true); }
    }
    public static HttpClient ProbeClient(ConnectSpec spec) => new(new SocketsHttpHandler { Proxy = new WebProxy("http://127.0.0.1:" + spec.ProbePort) { Credentials = new NetworkCredential("bebekon", spec.ProbePassword) }, UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
    public static async Task<string> VpnIpAsync(ConnectSpec spec, CancellationToken ct)
    {
        using var http = ProbeClient(spec); var text = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim();
        if (!IPAddress.TryParse(text, out _)) throw new UserError("Сервис проверки IP вернул неверный ответ."); return text;
    }
}
