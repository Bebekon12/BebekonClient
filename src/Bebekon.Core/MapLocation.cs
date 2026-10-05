using System.Text.Json;

namespace Bebekon.Core;

public sealed record GeoPoint(double Longitude, double Latitude);
public static class MapLocation
{
    public sealed record MapViewport(double Longitude, double Latitude, double LongitudeSpan, double LatitudeSpan);
    public static MapViewport Viewport(GeoPoint? destination, GeoPoint? origin)
    {
        if (destination is null) return new(origin?.Longitude ?? 10, origin?.Latitude ?? 35, 240, 110);
        if (origin is null) return new(destination.Longitude, Math.Clamp(destination.Latitude + 3, -65, 75), 50, 32);
        var delta = LongitudeDelta(origin.Longitude - destination.Longitude);
        return new(LongitudeDelta(destination.Longitude + delta / 2), Math.Clamp((origin.Latitude + destination.Latitude) / 2 + 3, -70, 80), Math.Max(36, Math.Abs(delta) + 24), Math.Max(24, Math.Abs(origin.Latitude - destination.Latitude) + 24));
    }
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
