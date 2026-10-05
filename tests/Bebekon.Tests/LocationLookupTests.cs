using Bebekon.Core;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Bebekon.Tests;
public sealed class LocationLookupTests
{
    private const string Anonymous = "{\"ip\":\"144.31.126.255\",\"is_bogon\":false,\"country\":\"Finland\",\"lat\":60.16952,\"lon\":24.93545}";
    private const string Primary = "{\"success\":true,\"ip\":\"144.31.126.255\",\"country_code\":\"FI\",\"latitude\":60.16952,\"longitude\":24.93545}";
    [Fact] public void AnonymousAndKeyedFormatsIdentifyCountryAndValidateCoordinates()
    {
        var result = MapLocation.ParseIpapiIs(Anonymous);
        Assert.Equal("FI", result.Country); Assert.Equal(new GeoPoint(24.93545, 60.16952), result.Point);
        Assert.Equal(result, MapLocation.ParseIpapiIs("{\"ip\":\"144.31.126.255\",\"location\":{\"country_code\":\"FI\",\"latitude\":60.16952,\"longitude\":24.93545}}"));
        Assert.Throws<FormatException>(() => MapLocation.ParseIpapiIs(Anonymous.Replace("false", "true")));
        Assert.Throws<FormatException>(() => MapLocation.ParseIpapiIs(Anonymous.Replace("60.16952", "91")));
        Assert.Throws<FormatException>(() => MapLocation.ParseIpapiIs("{\"error\":\"limit exceeded\"}"));
    }
    [Fact] public async Task QuotaAndForbiddenProvidersAreNotRetriedOnEveryTrafficUpdate()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request => {
            calls.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.ipapi.is") { var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)); return limited; }
            return new(HttpStatusCode.OK) { Content = new StringContent(Primary) };
        }));
        var lookup = new LocationLookup();
        Assert.Equal("FI", (await lookup.LookupAsync(http, "144.31.126.255", default))?.Country);
        Assert.Equal("FI", (await lookup.LookupAsync(http, "144.31.126.255", default))?.Country);
        Assert.Equal(new[] { "api.ipapi.is", "ipwho.is", "ipwho.is" }, calls);
    }
    [Fact] public async Task AnotherIPsResponseAndTlsFailureFallBackToValidatedExit()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request => {
            calls++; Assert.Equal("https", request.RequestUri!.Scheme);
            if (request.RequestUri.Host == "api.ipapi.is") return new(HttpStatusCode.OK) { Content = new StringContent(Anonymous.Replace("144.31.126.255", "8.8.8.8")) };
            if (request.RequestUri.Host == "ipwho.is") throw new HttpRequestException("TLS unavailable");
            return new(HttpStatusCode.OK) { Content = new StringContent(Primary.Replace("\"success\":true,", "")) };
        }));
        Assert.Equal("FI", (await new LocationLookup().LookupAsync(http, "144.31.126.255", default))?.Country); Assert.Equal(3, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
