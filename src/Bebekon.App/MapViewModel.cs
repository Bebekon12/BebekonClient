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
    private readonly LocationLookup mapLookup = new();
    public GeoPoint? MapDestination => MapLocationEnabled && SelectedServer?.Id == mapEndpointServer ? mapEndpoint?.Point : null;
    public string MapCountry => MapDestination is null ? "" : mapEndpoint?.Country ?? "";
    public string SelectedCountry => CountryInfo.Resolve(SelectedServer?.Name) ?? MapCountry;
    private void ResetMapDestination() { mapEndpoint = null; mapEndpointServer = null; endpointLookupKey = null; Notify(nameof(MapDestination)); Notify(nameof(MapCountry)); Notify(nameof(SelectedCountry)); }
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
            try {
                using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2), AllowAutoRedirect = false };
                if (connectedSpec is not null) {
                    handler.UseProxy = true;
                    handler.Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + connectedSpec.OriginProbePort) { Credentials = new System.Net.NetworkCredential("bebekon", connectedSpec.ProbePassword) };
                }
                using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 16 * 1024 };
                var result = await mapLookup.LookupAsync(http, null, lifetime.Token);
                // Never accept a result from a replaced tunnel or a different uplink.
                if (result is not null && MapLocationEnabled && (!ConnectionBusy || locatingBeforeTunnel) && (connectedSpec is null ? !Connected : ReferenceEquals(spec, connectedSpec)) && uplink == uplinkSignature && !lifetime.IsCancellationRequested) { MapOrigin = result.Point; originChecked = DateTimeOffset.UtcNow; }
                return;
            } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
    }
    private Task RefreshMapDestinationAsync()
    {
        if (endpointLookup is { IsCompleted: false }) return endpointLookup;
        if (!MapLocationEnabled || !Connected || ConnectionBusy || spec is null || spec.OriginProbePort == 0 || appliedConfigurationVersion != configurationVersion || CountryInfo.Resolve(SelectedServer?.Name) is not null || !System.Net.IPAddress.TryParse(VpnIp, out var address)) return Task.CompletedTask;
        var key = spec.Server.Id + "/" + address;
        if (endpointLookupKey == key && (mapEndpoint is not null || DateTimeOffset.UtcNow - endpointAttempted < TimeSpan.FromSeconds(30))) return Task.CompletedTask;
        if (endpointLookupKey != key) { mapEndpoint = null; mapEndpointServer = null; Notify(nameof(MapDestination)); Notify(nameof(MapCountry)); Notify(nameof(SelectedCountry)); }
        endpointLookupKey = key; endpointAttempted = DateTimeOffset.UtcNow;
        return endpointLookup = LookupMapDestinationAsync(spec, address.ToString());
    }
    private async Task LookupMapDestinationAsync(ConnectSpec current, string ip)
    {
            try {
                using var http = LatencyService.ProbeClient(current with { ProbePort = current.OriginProbePort }); http.MaxResponseContentBufferSize = 16 * 1024;
                var result = await mapLookup.LookupAsync(http, ip, lifetime.Token);
                if (result is not null && MapLocationEnabled && Connected && ReferenceEquals(spec, current) && SelectedServer?.Id == current.Server.Id && System.Net.IPAddress.TryParse(VpnIp, out var actual) && actual.Equals(System.Net.IPAddress.Parse(ip))) {
                    mapEndpoint = result; mapEndpointServer = current.Server.Id; Notify(nameof(MapDestination)); Notify(nameof(MapCountry)); Notify(nameof(SelectedCountry));
                }
                return;
            } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
    }
}
