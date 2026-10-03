using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class TrafficTests
{
    [Fact]
    public async Task RealCoreCountsUploadDownloadAndProtectsItsStatisticsEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync(lifetime.Token); await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 }) { }
            var body = new byte[256 * 1024];
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), lifetime.Token);
            await stream.WriteAsync(body, lifetime.Token);
        });
        try
        {
            using var telemetry = new CoreTraffic();
            var spec = CoreTests.Spec(); spec.Settings.TunnelMode = TunnelMode.Proxy;
            var config = JsonNode.Parse(telemetry.AddToConfig(ConfigGenerator.Generate(spec)))!;
            var proxyPort = LatencyService.FreePort(); config["inbounds"]![1]!["listen_port"] = proxyPort;
            var path = Path.Combine(root, "config.json"); await File.WriteAllTextAsync(path, config.ToJsonString());
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
            using var core = new CoreProcess(exe, new(root, "core")); await core.StartAsync(path, lifetime.Token); telemetry.Start();
            using var unauthorized = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var rejected = await unauthorized.GetAsync("http://" + (string)config["experimental"]!["clash_api"]!["external_controller"]! + "/connections", lifetime.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = true, Proxy = new WebProxy("http://127.0.0.1:" + proxyPort) });
            var data = await http.GetByteArrayAsync($"http://127.0.0.1:{port}/", lifetime.Token); Assert.Equal(256 * 1024, data.Length); await responseTask;
            while (telemetry.Snapshot?.DownloadBytes < data.Length || telemetry.Snapshot is null) await Task.Delay(100, lifetime.Token);
            var sample = telemetry.Snapshot!;
            Assert.True(sample.DownloadBytesPerSecond > 0); Assert.True(sample.UploadBytes > 0); Assert.True(sample.UploadBytesPerSecond > 0);
            telemetry.Dispose(); Assert.Null(telemetry.Snapshot);
        }
        finally { lifetime.Cancel(); listener.Stop(); Directory.Delete(root, true); }
    }
}
