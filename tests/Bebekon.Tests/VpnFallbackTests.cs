using System.Net;
using System.Net.Http.Json;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class VpnFallbackTests
{
    [Fact]
    public async Task PrefersPrimaryThenReserveThenBlockAndRecoversToPrimary()
    {
        using var handler = new Controller();
        using var http = new HttpClient(handler);
        var group = new VpnFallback.Group("vpn-fallback-0", ["vpn", "vpn-rule-1"]);
        var fallback = new VpnFallback(http, new Uri("http://127.0.0.1:17855/"), [group]);

        handler.Available = ["vpn", "vpn-rule-1"];
        await fallback.RefreshAsync(default);
        Assert.Equal(new[] { "vpn" }, handler.Probes);
        Assert.Empty(handler.Selections);
        Assert.True(group.Healthy);

        handler.Available = ["vpn-rule-1"];
        await fallback.RefreshAsync(default);
        handler.Available = [];
        await fallback.RefreshAsync(default);
        Assert.False(group.Healthy);
        handler.Available = ["vpn", "vpn-rule-1"];
        await fallback.RefreshAsync(default);

        Assert.Equal(new[] { "vpn-rule-1", VpnFallback.Offline, "vpn" }, handler.Selections);
        Assert.DoesNotContain("direct", handler.Probes);
        Assert.True(group.Healthy);
    }

    private sealed class Controller : HttpMessageHandler
    {
        public HashSet<string> Available = [];
        public List<string> Probes = [];
        public List<string> Selections = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadFromJsonAsync<Selection>(cancellationToken);
                Selections.Add(body!.Name);
                return new(HttpStatusCode.NoContent);
            }
            var candidate = request.RequestUri!.AbsolutePath.Split('/')[2];
            Probes.Add(candidate);
            return new(Available.Contains(candidate) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        }

        private sealed record Selection(string Name);
    }
}
