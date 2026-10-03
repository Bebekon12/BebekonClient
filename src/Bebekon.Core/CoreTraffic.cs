using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed record TrafficSnapshot(double UploadBytesPerSecond, double DownloadBytesPerSecond,
    long UploadBytes, long DownloadBytes, DateTimeOffset SampledAt);

/// <summary>Service-private loopback statistics. The control secret never crosses IPC.</summary>
public sealed class CoreTraffic : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private readonly int port = LatencyService.FreePort();
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private TrafficSnapshot? snapshot;
    private Task? loop;
    public TrafficSnapshot? Snapshot => !stop.IsCancellationRequested && Volatile.Read(ref snapshot) is { } current && DateTimeOffset.UtcNow - current.SampledAt < TimeSpan.FromSeconds(4) ? current : null;

    public string AddToConfig(string config)
    {
        var document = JsonNode.Parse(config)!;
        document["experimental"] = new JsonObject { ["clash_api"] = new JsonObject {
            ["external_controller"] = "127.0.0.1:" + port, ["secret"] = secret,
            ["access_control_allow_origin"] = new JsonArray("http://localhost"),
            ["access_control_allow_private_network"] = false
        }};
        return document.ToJsonString(Json.Options);
    }
    public void Start()
    {
        if (loop is not null) throw new InvalidOperationException("Traffic sampling already started.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        loop = ReadAsync();
    }
    private async Task ReadAsync()
    {
        long lastUp = 0, lastDown = 0; var clock = Stopwatch.StartNew(); var lastTime = clock.Elapsed.TotalSeconds;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var response = await http.GetAsync("http://127.0.0.1:" + port + "/connections", stop.Token);
                response.EnsureSuccessStatusCode();
                using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stop.Token));
                var up = data.RootElement.GetProperty("uploadTotal").GetInt64();
                var down = data.RootElement.GetProperty("downloadTotal").GetInt64();
                var time = clock.Elapsed.TotalSeconds; var elapsed = time - lastTime;
                Volatile.Write(ref snapshot, new TrafficSnapshot(
                    elapsed > 0 ? Math.Max(0, up - lastUp) / elapsed : 0,
                    elapsed > 0 ? Math.Max(0, down - lastDown) / elapsed : 0,
                    up, down, DateTimeOffset.UtcNow));
                lastUp = up; lastDown = down; lastTime = time;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException or ObjectDisposedException) { }
            try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public void Dispose() { stop.Cancel(); http.Dispose(); Volatile.Write(ref snapshot, null); }
}
