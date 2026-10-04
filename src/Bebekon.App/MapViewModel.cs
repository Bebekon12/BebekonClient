using System.Net.Http;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    private GeoPoint? mapOrigin;
    public GeoPoint? MapOrigin { get => mapOrigin; internal set => Set(ref mapOrigin, value); }
    private bool fetchingOrigin;
    private DateTimeOffset originChecked;
    public bool MapLocationEnabled {
        get => Settings.MapLocation;
        set { Settings.MapLocation = value; Save(); Notify(); if (!value) MapOrigin = null; else { originChecked = default; _ = RefreshMapOriginAsync(); } }
    }
    private async Task RefreshMapOriginAsync()
    {
        if (!MapLocationEnabled || Connected || ConnectionBusy || fetchingOrigin || testProbe is not null || DateTimeOffset.UtcNow - originChecked < TimeSpan.FromMinutes(30)) return;
        fetchingOrigin = true; originChecked = DateTimeOffset.UtcNow; var uplink = uplinkSignature;
        try {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4), AllowAutoRedirect = false };
            using var http = new HttpClient(handler);
            var text = await http.GetStringAsync("https://ipwho.is/?fields=success,latitude,longitude", limit.Token);
            var result = MapLocation.Parse(text);
            // Never replace the origin with our VPN's egress IP; no coordinates are persisted.
            if (MapLocationEnabled && !Connected && !ConnectionBusy && uplink == uplinkSignature && !lifetime.IsCancellationRequested) MapOrigin = result;
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException) { }
        finally { fetchingOrigin = false; }
    }
}
