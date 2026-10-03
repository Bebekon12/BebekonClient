using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class LatencyDeadlineTests
{
    [Fact]
    public async Task SilentVpnPeerTimesOutInFiveSecondsAndCancellationStaysDistinct()
    {
        using var peer = new TcpListener(IPAddress.Loopback, 0); peer.Start();
        var node = CoreTests.Node(); node.Host = "127.0.0.1"; node.Port = ((IPEndPoint)peer.LocalEndpoint).Port;
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
        var latency = new LatencyService(exe); var clock = Stopwatch.StartNew();
        var result = await latency.MeasureAsync(node, LatencyMode.Exact, true, default);
        Assert.True(result.TimedOut); Assert.Null(result.Milliseconds);
        Assert.InRange(clock.Elapsed.TotalSeconds, 4.5, 7);
        using var stop = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => latency.MeasureAsync(node, LatencyMode.Exact, true, stop.Token));
    }
}
