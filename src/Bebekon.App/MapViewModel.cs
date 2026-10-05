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
    private GeoEndpoint? mapEndpoint;
    private string? mapEndpointServer;
    private string? endpointLookupKey;
    private Task? endpointLookup;
    private DateTimeOffset endpointAttempted;
    public GeoPoint? MapDestination => MapLocationEnabled && SelectedServer?.Id == mapEndpointServer ? mapEndpoint?.Point : null;
    public string MapCountry => MapDestination is null ? "" : mapEndpoint?.Country ?? "";
    private void ResetMapDestination() { mapEndpoint = null; mapEndpointServer = null; endpointLookupKey = null; Notify(nameof(MapDestination)); Notify(nameof(MapCountry)); }
    public bool MapLocationEnabled {
        get => Settings.MapLocation;
        set { Settings.MapLocation = value; Save(); Notify(); QueueApply(); ResetMapDestination(); if (!value) MapOrigin = null; else { originChecked = default; originAttempted = default; _ = RefreshMapOriginAsync(); } }
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
    private Task RefreshMapDestinationAsync()
    {
        if (endpointLookup is { IsCompleted: false }) return endpointLookup;
        if (!MapLocationEnabled || !Connected || ConnectionBusy || spec is null || spec.OriginProbePort == 0 || appliedConfigurationVersion != configurationVersion || CountryInfo.Resolve(SelectedServer?.Name) is not null || !System.Net.IPAddress.TryParse(VpnIp, out var address)) return Task.CompletedTask;
        var key = spec.Server.Id + "/" + address;
        if (endpointLookupKey == key && (mapEndpoint is not null || DateTimeOffset.UtcNow - endpointAttempted < TimeSpan.FromSeconds(30))) return Task.CompletedTask;
        if (endpointLookupKey != key) { mapEndpoint = null; mapEndpointServer = null; Notify(nameof(MapDestination)); Notify(nameof(MapCountry)); }
        endpointLookupKey = key; endpointAttempted = DateTimeOffset.UtcNow;
        return endpointLookup = LookupMapDestinationAsync(spec, address.ToString());
    }
    private async Task LookupMapDestinationAsync(ConnectSpec current, string ip)
    {
        foreach (var fallback in new[] { false, true }) {
            try {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(TimeSpan.FromSeconds(4));
                using var http = LatencyService.ProbeClient(current with { ProbePort = current.OriginProbePort }); http.MaxResponseContentBufferSize = 16 * 1024;
                var text = await http.GetStringAsync(fallback ? $"https://ipapi.co/{ip}/json/" : $"https://ipwho.is/{ip}?fields=success,latitude,longitude,country_code", limit.Token);
                var result = MapLocation.ParseEndpoint(text, fallback);
                if (MapLocationEnabled && Connected && ReferenceEquals(spec, current) && SelectedServer?.Id == current.Server.Id && VpnIp == ip) {
                    mapEndpoint = result; mapEndpointServer = current.Server.Id; Notify(nameof(MapDestination)); Notify(nameof(MapCountry));
                }
                return;
            } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (lifetime.IsCancellationRequested) return;
        }
    }
}
