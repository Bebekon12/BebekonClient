using System.Net.Http;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    private GeoPoint? mapOrigin;
    public GeoPoint? MapOrigin { get => mapOrigin; internal set => Set(ref mapOrigin, value); }
    private Task? originLookup;
    private bool locatingBeforeTunnel;
    private DateTimeOffset originChecked;
    public bool MapLocationEnabled {
        get => Settings.MapLocation;
        set { Settings.MapLocation = value; Save(); Notify(); if (!value) MapOrigin = null; else { originChecked = default; _ = RefreshMapOriginAsync(); } }
    }
    private Task RefreshMapOriginAsync()
    {
        if (originLookup is { IsCompleted: false }) return originLookup;
        if (!MapLocationEnabled || Connected || ConnectionBusy && !locatingBeforeTunnel || testProbe is not null || DateTimeOffset.UtcNow - originChecked < TimeSpan.FromMinutes(30)) return Task.CompletedTask;
        return originLookup = LookupMapOriginAsync();
    }
    private async Task LookupMapOriginAsync()
    {
        var uplink = uplinkSignature;
        try {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(3));
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2), AllowAutoRedirect = false };
            using var http = new HttpClient(handler);
            var text = await http.GetStringAsync("https://ipwho.is/?fields=success,latitude,longitude", limit.Token);
            var result = MapLocation.Parse(text);
            // Never replace the origin with our VPN's egress IP; no coordinates are persisted.
            if (MapLocationEnabled && !Connected && (!ConnectionBusy || locatingBeforeTunnel) && uplink == uplinkSignature && !lifetime.IsCancellationRequested) { MapOrigin = result; originChecked = DateTimeOffset.UtcNow; }
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException) { }
    }
}
