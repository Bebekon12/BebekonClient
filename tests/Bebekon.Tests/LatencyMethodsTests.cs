using System.Net;
using System.Net.Sockets;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public sealed class LatencyMethodsTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    [Theory] [InlineData("GET")] [InlineData("HEAD")]
    public async Task HttpsProbeUsesSelectedVerbThreeTimes(string verb)
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) => { requests.Add(request.Method.Method); Assert.Equal("https://www.gstatic.com/generate_204", request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }));
        Assert.True(await LatencyService.MeasureHttpAsync(http, new HttpMethod(verb), default) >= 0);
        Assert.Equal(new[] { verb, verb, verb }, requests);
    }
    [Fact] public async Task HttpFailureAndCancellationAreNotSuccessfulLatency()
    {
        using var failed = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await Assert.ThrowsAsync<HttpRequestException>(() => LatencyService.MeasureHttpAsync(failed, HttpMethod.Get, default));
        using var pending = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.NoContent); }));
        using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LatencyService.MeasureHttpAsync(pending, HttpMethod.Head, stop.Token));
    }
    [Fact] public async Task TcpCacheDoesNotLeakAcrossMethodsOrChangedEndpoints()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var node = CoreTests.Node(); node.Host = "127.0.0.1"; node.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var service = new LatencyService("unused.exe");
        var tcp = await service.MeasureAsync(node, LatencyMode.Tcp, true, default); Assert.NotNull(tcp.Milliseconds);
        listener.Stop(); Assert.Equal(tcp, await service.MeasureAsync(node, LatencyMode.Tcp, false, default));
        var icmp = await service.MeasureAsync(node, LatencyMode.Icmp, false, default); Assert.Equal(LatencyMode.Icmp, icmp.Mode); Assert.NotNull(icmp.Milliseconds);
        node.Port = LatencyService.FreePort(); Assert.Null((await service.MeasureAsync(node, LatencyMode.Tcp, false, default)).Milliseconds);
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MeasureAsync(node, LatencyMode.Icmp, false, stopped.Token));
    }
}
