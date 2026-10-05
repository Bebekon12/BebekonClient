using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public sealed class MapLocationTests
{
    [Fact] public void GenericServerUsesValidatedEgressCoordinatesAndCountry()
    {
        Assert.Null(CountryInfo.Resolve("testvpn"));
        var result = MapLocation.ParseEndpoint("{\"success\":true,\"longitude\":18.1,\"latitude\":59.3,\"country_code\":\"SE\"}");
        Assert.Equal(new GeoPoint(18.1, 59.3), result.Point); Assert.Equal("SE", result.Country);
        Assert.Equal("Швеция", CountryInfo.CountryName(result.Country));
        Assert.Equal("", MapLocation.ParseEndpoint("{\"success\":true,\"longitude\":18.1,\"latitude\":59.3,\"country_code\":\"ZZ\"}").Country);
        Assert.Throws<FormatException>(() => MapLocation.ParseEndpoint("{\"success\":false,\"longitude\":18.1,\"latitude\":59.3}"));
    }
    [Fact] public async Task OfficialCoreAcceptsAndRestrictsTheOriginChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try {
            var probePort = LatencyService.FreePort(); int originPort;
            do { originPort = LatencyService.FreePort(); } while (originPort == probePort || originPort == 17890);
            var spec = CoreTests.Spec(settings: new() { TunnelMode = TunnelMode.Proxy }) with { ProbePort = probePort, OriginProbePort = originPort };
            var config = System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Generate(spec))!;
            var inbounds = config["inbounds"]!.AsArray();
            inbounds.Remove(inbounds.Single(i => (string?)i?["tag"] == "proxy-in"));
            var file = Path.Combine(root, "origin.json"); await File.WriteAllTextAsync(file, config.ToJsonString());
            using var core = new CoreProcess(executable, new(root, "core")); await core.ValidateAsync(file, limit.Token); await core.StartAsync(file, limit.Token);
            using var http = LatencyService.ProbeClient(spec with { ProbePort = originPort });
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("https://example.com/", limit.Token));
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://ipwho.is/", limit.Token));
        } finally { Directory.Delete(root, true); }
    }
    [Fact] public void FallbackValidatesCoordinatesAndServiceErrors()
    {
        Assert.Equal(new GeoPoint(37.6, 55.7), MapLocation.ParseFallback("{\"ip\":\"203.0.113.1\",\"longitude\":37.6,\"latitude\":55.7}"));
        Assert.Throws<FormatException>(() => MapLocation.ParseFallback("{\"error\":true}"));
        Assert.Throws<FormatException>(() => MapLocation.ParseFallback("{\"ip\":\"203.0.113.1\",\"longitude\":181,\"latitude\":55.7}"));
    }
    [Fact] public void OnlyTheClientsLocationRequestsBypassWholePcRouting()
    {
        var spec = CoreTests.Spec(new() { DefaultRoute = RouteTarget.Vpn }) with { OriginProbePort = 18001 };
        var config = System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Generate(spec))!;
        var rule = config["route"]!["rules"]!.AsArray().Single(r => r?["domain"]?.ToJsonString().Contains("ipwho.is") == true)!;
        Assert.Equal("origin-probe", (string?)rule["inbound"]![0]); Assert.Equal(443, (int?)rule["port"]); Assert.Equal("direct", (string?)rule["outbound"]);
        var inlet = config["inbounds"]!.AsArray().Single(r => (string?)r?["tag"] == "origin-probe")!;
        Assert.Equal("127.0.0.1", (string?)inlet["listen"]); Assert.Equal(spec.ProbePassword, (string?)inlet["users"]![0]!["password"]);
        Assert.Contains(config["route"]!["rules"]!.AsArray(), r => (string?)r?["action"] == "reject" && (string?)r?["inbound"]?[0] == "origin-probe");
        Assert.Equal("vpn", (string?)config["route"]!["final"]);
        spec.Settings.MapLocation = false;
        Assert.DoesNotContain("ipwho.is", ConfigGenerator.Generate(spec));
        spec.Settings.MapLocation = true;
        Assert.DoesNotContain("ipwho.is", ConfigGenerator.Generate(spec, probeOnly: true));
    }
    [Fact] public void ParsesOnlyValidGeographicCoordinates()
    {
        Assert.Equal(new GeoPoint(37.6, 55.7), MapLocation.Parse("{\"success\":true,\"longitude\":37.6,\"latitude\":55.7}"));
        Assert.Throws<FormatException>(() => MapLocation.Parse("{\"success\":true,\"longitude\":181,\"latitude\":55.7}"));
        Assert.Throws<FormatException>(() => MapLocation.Parse("{\"success\":false,\"longitude\":37.6,\"latitude\":55.7}"));
        Assert.Equal(-2, MapLocation.LongitudeDelta(358));
    }
    [Theory]
    [InlineData(24.6, 56.9, 37.6, 55.7)]
    [InlineData(179, 50, -179, 55)]
    [InlineData(-74, 40, 140, 35)]
    [InlineData(15, -50, 20, 70)]
    public void ViewportKeepsBothRouteEndpointsInsideTheMap(double longitude, double latitude, double originLongitude, double originLatitude)
    {
        var destination = new GeoPoint(longitude, latitude); var origin = new GeoPoint(originLongitude, originLatitude);
        var viewport = MapLocation.Viewport(destination, origin);
        foreach (var point in new[] { destination, origin }) {
            Assert.InRange(Math.Abs(MapLocation.LongitudeDelta(point.Longitude - viewport.Longitude)), 0, viewport.LongitudeSpan / 2 - 10);
            Assert.InRange(Math.Abs(point.Latitude - viewport.Latitude), 0, viewport.LatitudeSpan / 2);
        }
        if (Math.Abs(MapLocation.LongitudeDelta(longitude - originLongitude)) < 20) Assert.True(viewport.LongitudeSpan < 120);
    }
}
