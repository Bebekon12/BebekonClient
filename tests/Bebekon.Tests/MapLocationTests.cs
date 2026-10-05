using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public sealed class MapLocationTests
{
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
