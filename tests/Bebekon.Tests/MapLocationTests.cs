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
}
