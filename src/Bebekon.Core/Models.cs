using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bebekon.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public enum RouteTarget { Direct, Vpn }
public enum RuleKind { Site, Contains, Application, Network, GeoSite, GeoIp }
public enum LatencyQuality { Unknown, Good, Moderate, Poor }
public static class LatencyDisplay
{
    public static LatencyQuality Quality(long? milliseconds) => milliseconds switch { null => LatencyQuality.Unknown, < 0 => LatencyQuality.Poor, <= 100 => LatencyQuality.Good, <= 200 => LatencyQuality.Moderate, _ => LatencyQuality.Poor };
}
public enum TunnelMode { Tun, Proxy }
public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }
public enum LatencyMode { Fast, Exact }

public sealed class Server : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SubscriptionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Uuid { get; set; } = "";
    public string Security { get; set; } = "none";
    public string Sni { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public List<string> Alpn { get; set; } = [];
    public string Flow { get; set; } = "";
    public string Transport { get; set; } = "tcp";
    public string ServiceName { get; set; } = "";
    public string Path { get; set; } = "/";
    public string TransportHost { get; set; } = "";
    public string? UnsupportedReason { get; set; }
    private bool favorite;
    public bool Favorite { get => favorite; set { if (Set(ref favorite, value)) Notify(nameof(Star)); } }
    [JsonIgnore] public string Star => Favorite ? "★" : "☆";
    [JsonIgnore] public bool Supported => UnsupportedReason is null;
    [JsonIgnore] public string Protocol => "VLESS · " + (Transport == "grpc" ? "gRPC" : Transport.ToUpperInvariant());
    private string subscriptionLabel = "";
    [JsonIgnore] public string SubscriptionLabel { get => subscriptionLabel; set { if (Set(ref subscriptionLabel, value)) Notify(nameof(ConnectionLabel)); } }
    [JsonIgnore] public string ConnectionLabel => SubscriptionLabel.Length == 0 ? Protocol : Protocol + " · " + SubscriptionLabel;
    [JsonIgnore] public string DisplayName => CountryInfo.DisplayName(Name);
    private string latency = "—";
    [JsonIgnore] public string Latency { get => latency; set => Set(ref latency, value); }
    private long? latencyMs;
    [JsonIgnore] public long? LatencyMs { get => latencyMs; set => Set(ref latencyMs, value); }
    [JsonIgnore] public string Mark => Name.Length > 0 ? Name[..Math.Min(2, Name.Length)].ToUpperInvariant() : "↗";
}
public sealed class RoutingRule : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public RuleKind Kind { get; set; }
    public List<string> Values { get; set; } = [];
    public string? ServerId { get; set; }
    private bool useVpn = true;
    public bool UseVpn { get => useVpn; set { if (Set(ref useVpn, value)) { Notify(nameof(Target)); Notify(nameof(RouteLabel)); } } }
    [JsonIgnore] public RouteTarget Target => UseVpn ? RouteTarget.Vpn : RouteTarget.Direct;
    [JsonIgnore] public string RouteLabel => UseVpn ? "Через VPN" : "Без VPN";
    [JsonIgnore] public string Description => Kind switch { RuleKind.Application => "Программа · " + string.Join(", ", Values.Select(System.IO.Path.GetFileName)), RuleKind.Network => "IP / подсеть · " + string.Join(", ", Values), RuleKind.GeoSite => "Сайты и сервисы · " + string.Join(", ", Values), RuleKind.GeoIp => "IP-адреса набора · " + string.Join(", ", Values), RuleKind.Contains => "Адрес содержит · " + string.Join(", ", Values), _ => "Сайт и поддомены · " + string.Join(", ", Values) };
    [JsonIgnore] public string Glyph => Kind switch { RuleKind.Application => "▦", RuleKind.Network => "⌘", _ => "◎" };
}
public sealed class Profile : Observable
{
    public override string ToString() => Name;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Основной";
    private RouteTarget defaultRoute = RouteTarget.Direct;
    public RouteTarget DefaultRoute { get => defaultRoute; set => Set(ref defaultRoute, value); }
    public ObservableCollection<RoutingRule> Rules { get; set; } = [];
}
public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Ultima";
    public string Source { get; set; } = "";
    public DateTimeOffset? Updated { get; set; }
    public int ServerCount { get; set; }
    [JsonIgnore] public string SafeSource => Source.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ? "VLESS · защищённая ссылка" : Uri.TryCreate(Source, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) + "/••••••" : "Защищённая подписка";
    [JsonIgnore] public string Summary => $"{ServerCount} серверов · Обновлено: {Updated?.ToLocalTime().ToString("dd.MM HH:mm") ?? "ещё не обновлялось"}";
}
public sealed class Settings
{
    public string Language { get; set; } = "Русский";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool RestoreServer { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool DnsProtection { get; set; } = true;
    public bool CompatibilityMode { get; set; }
    public int Mtu { get; set; } = 1500;
    public TunnelMode TunnelMode { get; set; } = TunnelMode.Tun;
    public bool Animations { get; set; } = true;
    public bool GlowEffects { get; set; } = true;
    public bool PureBlack { get; set; }
    public string AccentColor { get; set; } = "Cyan";
}
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public ObservableCollection<Subscription> Subscriptions { get; set; } = [];
    public ObservableCollection<Server> Servers { get; set; } = [];
    public ObservableCollection<Profile> Profiles { get; set; } = [new()];
    public string? SelectedServerId { get; set; }
    public string? SelectedProfileId { get; set; }
}
public sealed record Preset(string Name, string Icon, List<string> Domains, List<RoutingRule>? Rules = null)
{
    [JsonIgnore] public int RuleCount => Rules?.Count ?? 1;
    [JsonIgnore] public string Summary => Rules is null ? string.Join(", ", Domains) : $"{Rules.Count} правила · {Rules.Count(r => r.UseVpn)} через VPN · {Rules.Count(r => !r.UseVpn)} без VPN";
    public List<RoutingRule> CreateRules() => Rules is null ? [new() { Name = Name, Values = [.. Domains] }] : Rules.OrderBy(r => r.UseVpn).Select(r => new RoutingRule { Name = r.Name, Kind = r.Kind, Values = [.. r.Values], UseVpn = r.UseVpn, ServerId = r.ServerId }).ToList();
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
public sealed class UserError(string message) : Exception(message);
