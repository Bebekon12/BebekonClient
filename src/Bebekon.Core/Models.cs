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
public enum RouteTarget { Direct, Vpn, NetBird, Block }
public enum RuleTransport { Tcp, Udp }
public enum RuleKind { Site, Contains, Application, Network, GeoSite, GeoIp }
public enum LatencyQuality { Unknown, Good, Moderate, Poor }
public static class LatencyDisplay
{
    public static LatencyQuality Quality(long? milliseconds, LatencyMode mode = LatencyMode.Tcp) => milliseconds switch {
        null => LatencyQuality.Unknown, < 0 => LatencyQuality.Poor,
        _ when milliseconds <= (mode is LatencyMode.HttpsGet or LatencyMode.HttpsHead ? 300 : 100) => LatencyQuality.Good,
        _ when milliseconds <= (mode is LatencyMode.HttpsGet or LatencyMode.HttpsHead ? 600 : 250) => LatencyQuality.Moderate,
        _ => LatencyQuality.Poor
    };
}
public enum TunnelMode { Tun, Proxy }
public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }
public enum LatencyMode { Tcp, HttpsGet, HttpsHead, Icmp }

public sealed class Server : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SubscriptionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    // Default preserves VLESS nodes saved by earlier versions.
    public string Type { get; set; } = "vless";
    public string Password { get; set; } = "";
    public TrustTunnelOptions? TrustTunnel { get; set; }
    public string PreferredTrustTunnelTransport { get; set; } = "";
    [JsonIgnore] public bool IsTrustTunnel => Type == "trusttunnel";
    public string Cipher { get; set; } = "auto";
    public int AlterId { get; set; }
    public bool GlobalPadding { get; set; }
    public bool AuthenticatedLength { get; set; }
    public string Plugin { get; set; } = "";
    public string PluginOptions { get; set; } = "";
    public string Obfs { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public int UpMbps { get; set; }
    public int DownMbps { get; set; }
    public List<string> ServerPorts { get; set; } = [];
    public int HopIntervalSeconds { get; set; } = 30;
    public string CertificatePin { get; set; } = "";
    public string VerifyCertificateName { get; set; } = "";
    public bool TlsInsecure { get; set; }
    public string XhttpMode { get; set; } = "auto";
    public string XhttpExtra { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Security { get; set; } = "none";
    public string Sni { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public List<string> Alpn { get; set; } = [];
    public string Flow { get; set; } = "";
    public string PacketEncoding { get; set; } = "xudp";
    public bool UdpEnabled { get; set; } = true;
    public string Transport { get; set; } = "tcp";
    public string ServiceName { get; set; } = "";
    public string Path { get; set; } = "/";
    public string TransportHost { get; set; } = "";
    private string? unsupportedReason;
    public string? UnsupportedReason { get => TlsInsecure ? "Небезопасный TLS: проверка сертификата VPN-сервера обязательна. Попросите провайдера безопасную конфигурацию." : unsupportedReason; set => unsupportedReason = value; }
    private bool favorite;
    public bool Favorite { get => favorite; set { if (Set(ref favorite, value)) Notify(nameof(Star)); } }
    [JsonIgnore] public string Star => Favorite ? "★" : "☆";
    [JsonIgnore] public bool Supported => UnsupportedReason is null;
    [JsonIgnore] public string Protocol => (Type == "trusttunnel" ? "TrustTunnel" : Type == "shadowsocks" ? "Shadowsocks" : Type == "hysteria2" ? "Hysteria 2" : Type == "hysteria" ? "Hysteria" : Type.ToUpperInvariant()) + " · " + (Type == "trusttunnel" ? Transport switch { "http3" => "HTTP/3", "auto" => "Auto", _ => "HTTP/2" } : Type is "hysteria" or "hysteria2" ? "QUIC" : Transport == "grpc" ? "gRPC" : Transport.ToUpperInvariant()) + (TlsInsecure ? " · TLS без проверки" : "");
    private string subscriptionLabel = "";
    [JsonIgnore] public string SubscriptionLabel { get => subscriptionLabel; set { if (Set(ref subscriptionLabel, value)) Notify(nameof(ConnectionLabel)); } }
    [JsonIgnore] public string ConnectionLabel => SubscriptionLabel.Length == 0 ? Protocol : Protocol + " · " + SubscriptionLabel;
    [JsonIgnore] public string DisplayName => CountryInfo.DisplayName(Name);
    private string detectedCountry = "";
    /// <summary>VPN exit country found through the server itself when its name has none.</summary>
    public string DetectedCountry { get => detectedCountry; set { if (Set(ref detectedCountry, value.Length == 2 && CountryInfo.Resolve(value) == value ? value : "")) Notify(nameof(CountryCode)); } }
    [JsonIgnore] public string CountryCode => CountryInfo.Resolve(Name) ?? DetectedCountry;
    private string latency = "—";
    [JsonIgnore] public string Latency { get => latency; set => Set(ref latency, value); }
    private long? latencyMs;
    [JsonIgnore] public long? LatencyMs { get => latencyMs; set { if (Set(ref latencyMs, value)) Notify(nameof(LatencyQuality)); } }
    private LatencyMode measuredMode;
    [JsonIgnore] public LatencyMode MeasuredMode { get => measuredMode; set { if (Set(ref measuredMode, value)) Notify(nameof(LatencyQuality)); } }
    [JsonIgnore] public LatencyQuality LatencyQuality => LatencyDisplay.Quality(LatencyMs, MeasuredMode);
    [JsonIgnore] public string Mark => Name.Length > 0 ? Name[..Math.Min(2, Name.Length)].ToUpperInvariant() : "↗";
}
public sealed class RoutingRule : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset? CreatedAt { get; set; }
    public string Name { get; set; } = "";
    private int priority;
    private bool canMoveUp, canMoveDown;
    /// <summary>1-based position in the order the core checks rules; 0 when not shown.</summary>
    [JsonIgnore] public int Priority { get => priority; set => Set(ref priority, value); }
    [JsonIgnore] public bool CanMoveUp { get => canMoveUp; set => Set(ref canMoveUp, value); }
    [JsonIgnore] public bool CanMoveDown { get => canMoveDown; set => Set(ref canMoveDown, value); }
    private string? groupIcon;
    /// <summary>For a rule with a user group: that group's icon; otherwise the rule kind's.</summary>
    [JsonIgnore] public string? GroupIcon { get => groupIcon; set { if (Set(ref groupIcon, value)) Notify(nameof(DisplayIcon)); } }
    [JsonIgnore] public string DisplayIcon => groupIcon ?? Kind.ToString();
    public RuleKind Kind { get; set; }
    public List<string> Values { get; set; } = [];
    public string? ServerId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RuleTransport? Transport { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Port { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RouteTarget[]? AllowedRoutes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RouteTarget[]? ForbiddenRoutes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? FallbackServerIds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RouteTarget? UnavailableRoute { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SystemProtection { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? ProtectedExecutables { get; set; }
    private bool useVpn = true;
    private bool enabled = true;
    private RouteTarget? destination;
    // Profiles saved before Destination was introduced still restore both legacy routes.
    public bool UseVpn
    {
        get => Target != RouteTarget.Direct;
        set
        {
            if (useVpn == value) return;
            useVpn = value;
            if (destination is null) NotifyRoute();
        }
    }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RouteTarget? Destination
    {
        get => destination;
        set { if (Set(ref destination, value)) NotifyRoute(); }
    }
    [JsonIgnore] public RouteTarget Target => destination ?? (useVpn ? RouteTarget.Vpn : RouteTarget.Direct);
    [JsonIgnore] public bool HasApplicationScope => SystemProtection && RuleValidation.IsDomainRule(Kind);
    public bool Allows(RouteTarget route) => route == RouteTarget.Block ||
        ((AllowedRoutes is not { Length: > 0 } || AllowedRoutes.Contains(route)) &&
         ForbiddenRoutes?.Contains(route) != true);
    internal RoutingRule SystemScope() => HasApplicationScope
        ? new()
        {
            Id = Id, Name = Name, Kind = RuleKind.Application, Values = ProtectedExecutables ?? [],
            Destination = Target, ServerId = ServerId, FallbackServerIds = FallbackServerIds,
            AllowedRoutes = AllowedRoutes, ForbiddenRoutes = ForbiddenRoutes, SystemProtection = true
        }
        : this;
    [JsonIgnore] public string RouteLabel => Target switch
    {
        RouteTarget.Vpn => "Bebekon VPN",
        RouteTarget.Direct => "Direct",
        RouteTarget.NetBird => "NetBird",
        _ => "Block"
    };
    private void NotifyRoute()
    {
        Notify(nameof(Target));
        Notify(nameof(RouteLabel));
        Notify(nameof(UseVpn));
    }
    [JsonIgnore] public string Description => Kind switch { RuleKind.Application => "Программа · " + string.Join(", ", Values.Select(System.IO.Path.GetFileName)), RuleKind.Network => "IP / подсеть · " + string.Join(", ", Values), RuleKind.GeoSite => "Сайты и сервисы · " + string.Join(", ", Values.Select(v => DomainSets.IsReference(v) ? "★ " + DomainSets.NameOf(v) : v)), RuleKind.GeoIp => "IP-адреса набора · " + string.Join(", ", Values), RuleKind.Contains => "Адрес содержит · " + string.Join(", ", Values), _ => "Сайт и поддомены · " + string.Join(", ", Values) };
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
    [JsonIgnore] public IEnumerable<RoutingRule> ActiveRules => Rules.Where(rule => rule.Enabled);
    [JsonIgnore] public IEnumerable<RoutingRule> SystemRules => ActiveRules.Where(rule => rule.SystemProtection).Select(rule => rule.SystemScope());
    [JsonIgnore] public IEnumerable<RoutingRule> RulesByPrecedence => SystemRules.Concat(ActiveRules.Where(rule => !rule.SystemProtection || rule.HasApplicationScope));
    [JsonIgnore] public bool UsesNetBird => DefaultRoute == RouteTarget.NetBird || ActiveRules.Any(rule => rule.Target == RouteTarget.NetBird);
    [JsonIgnore] public bool HasProtectedRules => ActiveRules.Any(rule => rule.SystemProtection);
    [JsonIgnore] public IEnumerable<string> VpnServerIds => ActiveRules
        .Where(rule => rule.Target == RouteTarget.Vpn)
        .SelectMany(rule => new[] { rule.ServerId }.OfType<string>().Concat(rule.FallbackServerIds ?? []))
        .Distinct(StringComparer.Ordinal);
}
public sealed class Subscription : Observable
{
    private bool hidden;
    public bool Hidden { get => hidden; set { if (Set(ref hidden, value)) Notify(nameof(VisibilityIcon)); } }
    [JsonIgnore] public string VisibilityIcon => Hidden ? "EyeOff" : "Eye";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Подписка";
    public string Source { get; set; } = "";
    public DateTimeOffset? Updated { get; set; }
    public int ServerCount { get; set; }
    [JsonIgnore] public string SafeSource => Uri.TryCreate(Source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.GetLeftPart(UriPartial.Authority) + "/••••••" : "Защищённая подписка";
    [JsonIgnore] public string Summary => $"{ServerCount} серверов · Обновлено: {Updated?.ToLocalTime().ToString("dd.MM HH:mm") ?? "ещё не обновлялось"}";
    // Provider data from the last refresh (subscription-userinfo and related headers).
    public long Upload { get; set; }
    public long Download { get; set; }
    public long Total { get; set; }
    public DateTimeOffset? Expire { get; set; }
    public int UpdateIntervalHours { get; set; }
    public string SupportUrl { get; set; } = "";
    public string WebPageUrl { get; set; } = "";
    public string Announce { get; set; } = "";
    public void Apply(ProviderInfo info)
    {
        (Upload, Download, Total, Expire, UpdateIntervalHours) = (info.Upload, info.Download, info.Total, info.Expire, info.UpdateIntervalHours);
        (SupportUrl, WebPageUrl, Announce) = (info.SupportUrl, info.WebPageUrl, info.Announce);
        foreach (var name in new[] { nameof(UsageLabel), nameof(Warning), nameof(HasUsage), nameof(HasWarning), nameof(HasSupport), nameof(HasWebPage), nameof(HasAnnounce) }) Notify(name);
    }
    [JsonIgnore] public string UsageLabel => UsageText(DateTimeOffset.Now);
    public string UsageText(DateTimeOffset now)
    {
        var used = Upload + Download; var parts = new List<string>();
        if (Total > 0) parts.Add($"Осталось {ProviderInfo.Size(Math.Max(0, Total - used))} из {ProviderInfo.Size(Total)}");
        else if (used > 0) parts.Add($"Использовано {ProviderInfo.Size(used)} · без лимита");
        if (Expire is { } expire) parts.Add(expire <= now ? "срок истёк" : "до " + expire.ToLocalTime().ToString("dd.MM.yyyy"));
        return string.Join(" · ", parts);
    }
    [JsonIgnore] public string Warning => WarningText(DateTimeOffset.Now);
    /// <summary>Shown before a provider cuts the connection: expiry within 3 days or under 10% traffic left.</summary>
    public string WarningText(DateTimeOffset now)
    {
        if (Expire is { } expire && expire <= now) return "Подписка истекла — продлите её у провайдера";
        if (Expire is { } soon && soon - now < TimeSpan.FromDays(3)) return $"Подписка закончится через {Math.Max(1, (int)Math.Ceiling((soon - now).TotalDays))} дн.";
        if (Total > 0 && Total - (Upload + Download) <= Total / 10) return Upload + Download >= Total ? "Трафик закончился" : "Осталось мало трафика";
        return "";
    }
    /// <summary>Remote subscriptions refresh at the provider's interval, otherwise every 12 hours.</summary>
    public bool RefreshDue(DateTimeOffset now) => Remote && (Updated is null || now - Updated.Value >= TimeSpan.FromHours(UpdateIntervalHours > 0 ? UpdateIntervalHours : 12));
    [JsonIgnore] public bool Remote
    {
        get
        {
            var source = Source.Trim();
            if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
            if (!source.StartsWith("tt://", StringComparison.OrdinalIgnoreCase) || source.Contains('\n')) return false;
            try { return TrustTunnelParser.SubscriptionUrl(source) is not null; } catch (UserError) { return false; }
        }
    }
    [JsonIgnore] public bool HasUsage => UsageLabel.Length > 0;
    [JsonIgnore] public bool HasWarning => Warning.Length > 0;
    [JsonIgnore] public bool HasSupport => SupportUrl.Length > 0;
    [JsonIgnore] public bool HasWebPage => WebPageUrl.Length > 0;
    [JsonIgnore] public bool HasAnnounce => Announce.Length > 0;
}
public sealed class Settings
{
    public List<string>? EnabledIntegrations { get; set; }
    [JsonIgnore] public bool NetBirdEnabled => EnabledIntegrations?.Contains("netbird", StringComparer.OrdinalIgnoreCase) == true;
    /// <summary>Bring NetBird up from Bebekon at start and on connect; SSO sign-in opens in the browser.</summary>
    public bool NetBirdAutoConnect { get; set; } = true;
    public int DesignVersion { get; set; }
    public LatencyMode LatencyMode { get; set; } = LatencyMode.HttpsGet;
    /// <summary>Seconds between automatic checks of the current server; 0 turns them off.</summary>
    public int AutoPingSeconds { get; set; } = 10;
    public static readonly int[] AutoPingChoices = [0, 5, 10, 30, 60];
    public bool CheckForUpdates { get; set; } = true;
    public string UpdateSource { get; set; } = "";
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
    public bool MapLocation { get; set; } = true;
    public string ObhodFolder { get; set; } = "";
    public string ObhodProfile { get; set; } = "";
    public bool ScanOnOpen { get; set; }
    /// <summary>sing-box urltest across the visible servers instead of the selected one.</summary>
    public bool AutoServer { get; set; }
    /// <summary>Split the TLS ClientHello of direct (non-VPN) connections, against throttling by SNI.</summary>
    public bool TlsFragment { get; set; }
    public DirectTrafficPolicy DirectPolicy { get; set; } = new();
    /// <summary>Block traffic outside the tunnel while connected or reconnecting (TUN mode, see KillSwitch).</summary>
    public bool KillSwitch { get; set; }
    public bool PersistentKillSwitch { get; set; }
    public bool GlowEffects { get; set; } = true;
    public bool PureBlack { get; set; }
    public string AccentColor { get; set; } = "Blue";
}
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public ObservableCollection<Subscription> Subscriptions { get; set; } = [];
    public ObservableCollection<Server> Servers { get; set; } = [];
    public ObservableCollection<Profile> Profiles { get; set; } = [new()];
    /// <summary>User domain sets referenced by GeoSite rules as <c>my:name</c>.</summary>
    public List<DomainSet> DomainSets { get; set; } = [];
    public string? SelectedServerId { get; set; }
    public string? SelectedProfileId { get; set; }
}
public sealed record Preset(string Name, string Icon, List<string> Domains, List<RoutingRule>? Rules = null, string? Description = null)
{
    [JsonIgnore] public int RuleCount => Rules?.Count ?? 1;
    [JsonIgnore] public string Summary => Description ?? (Rules is null ? string.Join(", ", Domains) : $"{Rules.Count} правила · {Rules.Count(r => r.UseVpn)} через VPN · {Rules.Count(r => !r.UseVpn)} без VPN");
    public List<RoutingRule> CreateRules() => Rules is null ? [new() { CreatedAt = DateTimeOffset.UtcNow, Name = Name, Values = [.. Domains] }] : Rules.OrderBy(r => r.UseVpn).Select(r => new RoutingRule { CreatedAt = DateTimeOffset.UtcNow, Name = r.Name, Kind = r.Kind, Values = [.. r.Values], UseVpn = r.UseVpn, ServerId = r.ServerId }).ToList();
}
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
public sealed class UserError(string message) : Exception(message);
