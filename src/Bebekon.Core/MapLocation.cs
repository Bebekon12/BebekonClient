using System.Text.Json;

namespace Bebekon.Core;

public sealed record GeoPoint(double Longitude, double Latitude);
public static class MapLocation
{
    public static GeoPoint Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var lon = root.GetProperty("longitude").GetDouble(); var lat = root.GetProperty("latitude").GetDouble();
        if (!root.GetProperty("success").GetBoolean() || !double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90) throw new FormatException("Location unavailable");
        return new(lon, lat);
    }
    public static double LongitudeDelta(double delta) => (delta + 540) % 360 - 180;
}
