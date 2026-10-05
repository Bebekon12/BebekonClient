using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Bebekon.Core;

/// <summary>HTTPS-only provider fallback, shared origin/exit rate-limit backoff; no stored location.</summary>
public sealed class LocationLookup
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> blocked = new();
    public async Task<GeoEndpoint?> LookupAsync(HttpClient http, string? ip, CancellationToken token)
    {
        if (!string.IsNullOrEmpty(ip) && !IPAddress.TryParse(ip, out _)) throw new FormatException("Invalid location IP");
        var urls = new[] { "https://api.ipapi.is/" + (string.IsNullOrEmpty(ip) ? "" : "?q=" + ip), $"https://ipwho.is/{ip}?fields=success,ip,latitude,longitude,country_code", "https://ipapi.co/" + (string.IsNullOrEmpty(ip) ? "" : ip + "/") + "json/" };
        for (var provider = 0; provider < urls.Length; provider++) {
            var uri = new Uri(urls[provider]);
            if (blocked.TryGetValue(uri.Host, out var until) && until > DateTimeOffset.UtcNow) continue;
            try {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(4));
                using var response = await http.GetAsync(uri, limit.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) {
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) {
                        var retry = response.Headers.RetryAfter; var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromHours(1);
                        blocked[uri.Host] = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 60, 86400));
                    }
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
                var result = provider == 0 ? MapLocation.ParseIpapiIs(text) : MapLocation.ParseEndpoint(text, provider == 2);
                if (!string.IsNullOrEmpty(ip)) {
                    using var doc = JsonDocument.Parse(text);
                    if (!IPAddress.TryParse(doc.RootElement.GetProperty("ip").GetString(), out var echoed) || !echoed.Equals(IPAddress.Parse(ip))) throw new FormatException("Location IP mismatch");
                }
                return result;
            } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { token.ThrowIfCancellationRequested(); }
        }
        return null;
    }
}
