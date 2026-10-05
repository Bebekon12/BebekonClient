using System.Net.Http;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    private GeoPoint? mapOrigin;
    public GeoPoint? MapOrigin { get => mapOrigin; internal set => Set(ref mapOrigin, value); }
    private Task? originLookup;
    private bool locatingBeforeTunnel;
    private DateTimeOffset originChecked;
    private DateTimeOffset originAttempted;
    public bool MapLocationEnabled {
        get => Settings.MapLocation;
        set { Settings.MapLocation = value; Save(); Notify(); QueueApply(); if (!value) MapOrigin = null; else { originChecked = default; originAttempted = default; _ = RefreshMapOriginAsync(); } }
    }
    private Task RefreshMapOriginAsync()
    {
        if (originLookup is { IsCompleted: false }) return originLookup;
        if (Connected && (spec is null || spec.OriginProbePort == 0)) return Task.CompletedTask;
        if (!MapLocationEnabled || ConnectionBusy && !locatingBeforeTunnel || testProbe is not null || Connected && appliedConfigurationVersion != configurationVersion || DateTimeOffset.UtcNow - originChecked < TimeSpan.FromMinutes(30) || DateTimeOffset.UtcNow - originAttempted < TimeSpan.FromSeconds(30)) return Task.CompletedTask;
        return originLookup = LookupMapOriginAsync();
    }
    private async Task LookupMapOriginAsync()
    {
        var uplink = uplinkSignature;
        var connectedSpec = Connected ? spec : null;
        originAttempted = DateTimeOffset.UtcNow;
        foreach (var fallback in new[] { false, true }) {
            try {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(4));
                using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2), AllowAutoRedirect = false };
                if (connectedSpec is not null) {
                    handler.UseProxy = true;
                    handler.Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + connectedSpec.OriginProbePort) { Credentials = new System.Net.NetworkCredential("bebekon", connectedSpec.ProbePassword) };
                }
                using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 16 * 1024 };
                var text = await http.GetStringAsync(fallback ? "https://ipapi.co/json/" : "https://ipwho.is/?fields=success,latitude,longitude", limit.Token);
                var result = fallback ? MapLocation.ParseFallback(text) : MapLocation.Parse(text);
                // Never accept a result from a replaced tunnel or a different uplink.
                if (MapLocationEnabled && (!ConnectionBusy || locatingBeforeTunnel) && (connectedSpec is null ? !Connected : ReferenceEquals(spec, connectedSpec)) && uplink == uplinkSignature && !lifetime.IsCancellationRequested) { MapOrigin = result; originChecked = DateTimeOffset.UtcNow; }
                return;
            } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (lifetime.IsCancellationRequested) return;
        }
    }
}
