using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Windows.Input;
using Microsoft.Win32;

namespace Bebekon.App;

public sealed partial class MainViewModel : Observable, IDisposable
{
    private readonly StateStore store;
    private readonly IServiceClient service;
    private readonly Func<ConnectSpec, CancellationToken, Task>? testProbe;
    private readonly Func<Server, LatencyMode, bool, CancellationToken, Action?, Task<LatencyResult>>? testLatency;
    private readonly LatencyService latency = new(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"));
    private readonly SafeLog log = new(Paths.Logs, "app");
    private readonly SemaphoreSlim connectionGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? scan;
    private Task? activeScan;
    private int latencyGeneration;
    private int latencyRequest;
    private bool pingSortPending;
    private readonly Dictionary<string, int> latestLatencyRequests = [];
    private CancellationTokenSource? applyDelay;
    private CancellationTokenSource? networkDelay;
    private bool recovering;
    private bool desiredConnected;
    private CancellationTokenSource? connectionAttempt;
    private string uplinkSignature = UplinkSignature();
    public AppState Data { get; }
    public Settings Settings => Data.Settings;
    public bool AnimationsEnabled { get => Settings.Animations; set { Settings.Animations = value; AppearanceChanged(nameof(AnimationsEnabled)); } }
    public bool GlowEnabled { get => Settings.GlowEffects; set { Settings.GlowEffects = value; AppearanceChanged(nameof(GlowEnabled)); } }
    public bool PureBlack { get => Settings.PureBlack; set { Settings.PureBlack = value; AppearanceChanged(nameof(PureBlack)); } }
    public string AccentColor { get => Settings.AccentColor; set { if (Settings.AccentColor == value) return; Settings.AccentColor = value; AppearanceChanged(nameof(AccentColor)); } }
    private void AppearanceChanged(string name) { ThemeManager.Apply(Settings); Save(); Notify(name); }
    public bool IsTunMode { get => Settings.TunnelMode == TunnelMode.Tun; set => SetTunnelMode(value ? TunnelMode.Tun : TunnelMode.Proxy); }
    public bool IsProxyMode { get => Settings.TunnelMode == TunnelMode.Proxy; set => SetTunnelMode(value ? TunnelMode.Proxy : TunnelMode.Tun); }
    public ObservableCollection<ServerRow> ServerRows { get; } = [];
    public ObservableCollection<RoutingRule> VisibleRules { get; } = [];
    public List<Preset> Presets { get; }
    private PageModel page;
    public PageModel Page { get => page; private set => Set(ref page, value); }
    private string pageName = "Home";
    public string PageName { get => pageName; private set => Set(ref pageName, value); }
    private bool collapsed;
    public bool SidebarCollapsed { get => collapsed; set { if (Set(ref collapsed, value)) { Notify(nameof(SidebarWidth)); Notify(nameof(NavTextVisibility)); } } }
    public GridLength SidebarWidth => new(SidebarCollapsed ? 76 : 200);
    public Visibility NavTextVisibility => SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    private Server? selectedServer;
    public Server? SelectedServer { get => selectedServer; set { var changed = selectedServer?.Id != value?.Id || (selectedServer is not null && value is not null && ServerRefresh.ConnectionKey(selectedServer) != ServerRefresh.ConnectionKey(value)); if (Set(ref selectedServer, value)) { Data.SelectedServerId = value?.Id; Notify(nameof(ServerLabel)); Notify(nameof(SelectedCountry)); Save(); if (changed) { ResetMapDestination(); QueueApply(); } } } }
    public string ServerLabel => AutoActive ? T("Авто", "Auto") + (ActiveAutoServer is { } active ? " · " + active.DisplayName : "") : SelectedServer?.DisplayName ?? T("Выберите сервер", "Choose a server");
    /// <summary>Visible servers urltest may use; the core takes the first one until it has measured them.</summary>
    private bool AutoActive => Settings.AutoServer && DisplayServers.Count(ConfigGenerator.AutoEligible) >= 2;
    /// <summary>Fastest known first, so a large subscription keeps its best 64 servers.</summary>
    private List<Server> AutoPool => DisplayServers.Where(ConfigGenerator.AutoEligible).OrderBy(s => s.LatencyMs is >= 0 ? s.LatencyMs : long.MaxValue).ToList();
    /// <summary>Order-independent identity of the auto pool; a changed pool needs a new core config.</summary>
    private static string AutoKey(IEnumerable<Server>? servers) => string.Join("\n", (servers ?? []).Where(ConfigGenerator.AutoEligible).Select(ServerRefresh.ConnectionKey).Order(StringComparer.Ordinal));
    /// <summary>Maps the core's "auto-N" tag back through the list used for the running connection.</summary>
    private Server? ActiveAutoServer => Connected && spec is { } current && traffic?.ActiveOutbound is { } tag && int.TryParse(tag.AsSpan(5), out var index) && ConfigGenerator.AutoServers(current) is { Count: >= 2 } list && index >= 0 && index < list.Count ? list[index] : null;
    public bool TlsFragmentEnabled { get => Settings.TlsFragment; set { if (Settings.TlsFragment == value) return; Settings.TlsFragment = value; Notify(); Save(); QueueApply(); } }
    public bool AutoServerEnabled { get => Settings.AutoServer; set { if (Settings.AutoServer == value) return; Settings.AutoServer = value; Notify(); Notify(nameof(ServerLabel)); Save(); QueueApply(); if (value && !AutoActive) Banner = T("Для автовыбора нужно минимум два сервера VLESS, VMess, Trojan, Shadowsocks, Hysteria2 или TUIC.", "Auto needs at least two VLESS, VMess, Trojan, Shadowsocks, Hysteria2 or TUIC servers."); } }
    private Profile activeProfile;
    public Profile ActiveProfile { get => activeProfile; set { if (value is null || ReferenceEquals(activeProfile, value)) return; DetachProfile(activeProfile); Set(ref activeProfile, value); Data.SelectedProfileId = value.Id; AttachProfile(); RefreshRules(); Notify(nameof(ModeLabel)); Notify(nameof(IsWholePc)); Notify(nameof(EmptyRulesDetail)); Notify(nameof(StatusDetail)); Save(); QueueApply(); } }
    public bool IsWholePc { get => ActiveProfile.DefaultRoute == RouteTarget.Vpn; set { if (value == IsWholePc) return; ActiveProfile.DefaultRoute = value ? RouteTarget.Vpn : RouteTarget.Direct; Notify(); Notify(nameof(ModeLabel)); Notify(nameof(EmptyRulesDetail)); Notify(nameof(StatusDetail)); Save(); QueueApply(); } }
    public string ModeLabel => IsWholePc ? T("Весь ПК", "Entire PC") : T("По правилам", "By rules");
    private ConnectionState state;
    public ConnectionState State { get => state; private set { if (Set(ref state, value)) { NotifyStatus(); CommandManager.InvalidateRequerySuggested(); } } }
    public bool Connected => State == ConnectionState.Connected;
    public bool ConnectionBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;
    public string ConnectLabel => State switch { ConnectionState.Connecting => T("Отменить", "Cancel"), ConnectionState.Disconnecting => T("Отключаем…", "Disconnecting…"), ConnectionState.Connected => T("Отключить", "Disconnect"), _ => T("Подключить", "Connect") };
    public string StatusLabel => State switch { ConnectionState.Connected => T("Подключено", "Connected"), ConnectionState.Connecting => T("Подключение", "Connecting"), ConnectionState.Disconnecting => T("Отключение", "Disconnecting"), ConnectionState.Error => T("Ошибка подключения", "Connection error"), _ => T("Не подключено", "Not connected") };
    public string StatusDetail => Connected ? Settings.TunnelMode == TunnelMode.Proxy ? T("Выбранный трафик приложений с прокси идёт через VPN", "Selected traffic from proxy-aware applications uses VPN") : IsWholePc ? T("Весь трафик идёт через VPN", "All traffic goes through VPN") : T("Выбранный трафик идёт через VPN", "Selected traffic goes through VPN") : T("Ваш интернет использует обычное подключение", "Your internet uses your normal connection");
    public string EmptyRulesDetail => IsWholePc ? T("Без правил весь трафик идёт через VPN", "Without rules all traffic goes through VPN") : T("Без правил весь трафик идёт напрямую", "Without rules all traffic goes direct");
    public string FooterLabel => Connected ? T("онлайн", "online") : T("офлайн", "offline");
    public string FooterMode => Connected ? Settings.TunnelMode == TunnelMode.Tun ? "TUN" : T("Прокси", "Proxy") : T("Нет подключения", "Disconnected");
    private string vpnIp = "—";
    public string VpnIp { get => vpnIp; private set { if (Set(ref vpnIp, value)) Notify(nameof(PublicIpLabel)); } }
    public string PublicIpLabel => T("Публичный IP VPN", "VPN public IP") + (System.Net.IPAddress.TryParse(VpnIp, out var address) ? " · " + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6") : "");
    private LocalAddressSnapshot localAddresses = NetworkAddresses.Read();
    public string LocalIp => localAddresses.Address ?? "—";
    public string LocalIpLabel => T(localAddresses.Private ? "Приватный IP" : "Локальный IP", localAddresses.Private ? "Private IP" : "Local IP");
    public string LocalIpDetail => localAddresses.Detail;
    private string session = "—";
    public string Session { get => session; private set => Set(ref session, value); }
    private TrafficSnapshot? traffic;
    public IReadOnlyList<double> DownloadHistory { get; private set; } = Array.Empty<double>();
    public IReadOnlyList<double> UploadHistory { get; private set; } = Array.Empty<double>();
    public string DownloadRate => Rate(traffic?.DownloadBytesPerSecond);
    public string UploadRate => Rate(traffic?.UploadBytesPerSecond);
    public string DownloadTotal => Total(traffic?.DownloadBytes);
    public string UploadTotal => Total(traffic?.UploadBytes);
    private string Rate(double? bytes) => !Connected || bytes is null ? "—" : (bytes.Value * 8 / 1_000_000).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
    private static string Total(long? bytes) => bytes is null ? "—" : bytes >= 1_073_741_824 ? (bytes.Value / 1_073_741_824.0).ToString("0.00") + " GB" : (bytes.Value / 1_048_576.0).ToString("0.00") + " MB";
    private DateTimeOffset? started;
    private ConnectSpec? spec;
    private int monitorId;
    private int configurationVersion;
    private int appliedConfigurationVersion = -1;
    private string? banner;
    public string? Banner { get => banner; set { if (Set(ref banner, value)) Notify(nameof(BannerVisibility)); } }
    public Visibility BannerVisibility => string.IsNullOrWhiteSpace(Banner) ? Visibility.Collapsed : Visibility.Visible;
    private string serverSearch = "", ruleSearch = "";
    public string ServerSearch { get => serverSearch; set { if (Set(ref serverSearch, value)) RefreshServers(); } }
    public string RuleSearch { get => ruleSearch; set { if (Set(ref ruleSearch, value)) RefreshRules(); } }
    private int ruleSortIndex;
    public int RuleSortIndex { get => ruleSortIndex; set { if (Set(ref ruleSortIndex, value)) { RefreshRules(); Notify(nameof(RulesOrderHint)); } } }
    public string RulesOrderHint => RuleSortIndex == 0 ? T("Сначала новые. Для изменения порядка выполнения выберите «По приоритету».", "Newest first. Choose ‘By priority’ to change execution order.") : T("Правила сверху имеют приоритет. Перетащите строку, чтобы изменить порядок.", "Top rules have priority. Drag a row to change execution order.");
    private int sortIndex;
    public int SortIndex { get => sortIndex; set { if (Set(ref sortIndex, value)) RefreshServers(); } }
    private int latencyIndex = 1;
    public int LatencyIndex { get => latencyIndex; set { if (value is < 0 or > 3) return; if (Set(ref latencyIndex, value)) { latencyGeneration++; scan?.Cancel(); Settings.LatencyMode = (LatencyMode)value; Save(); foreach (var s in Data.Servers) { s.Latency = "—"; s.LatencyMs = null; } RefreshServers(); if (PageName == "Servers") _ = ScanSafelyAsync(); } } }
    private bool listMode = true;
    public bool ListMode { get => listMode; set { if (Set(ref listMode, value)) { Notify(nameof(SecondColumn)); RefreshServers(); } } }
    public GridLength SecondColumn => new(ListMode ? 0 : 1, GridUnitType.Star);
    private bool scanning;
    private int scanGeneration;
    public bool Scanning { get => scanning; private set { if (Set(ref scanning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string CoreVersion => ConfigGenerator.CoreVersion;
    public string ServerCount => DisplayServers.Count().ToString();
    public string RuleCount => ActiveProfile.Rules.Count.ToString();
    public Visibility NoServers => !DisplayServers.Any() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRules => ActiveProfile.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSubscriptions => Data.Subscriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ICommand Navigate { get; }
    public ICommand CollapseSidebar { get; }
    public ICommand ToggleConnect { get; }
    public ICommand SelectServer { get; }
    public ICommand ConfigureTrustTunnel { get; }
    public ICommand FavoriteServer { get; }
    public ICommand PingAll { get; }
    public ICommand PingServer { get; }
    public ICommand ChangeRoutingMode { get; }
    public ICommand CancelPing { get; }
    public ICommand RefreshSubscriptions { get; }
    public ICommand OpenProviderLink { get; }
    public ICommand ExportBackup { get; }
    public ICommand ImportBackup { get; }
    public ICommand AddSubscription { get; }
    public ICommand AddTrustTunnel { get; }
    public ICommand ToggleSubscriptionVisibility { get; }
    public ICommand ImportQuickSubscription { get; }
    private string quickSource = "";
    public string QuickSource { get => quickSource; set { if (Set(ref quickSource, value)) CommandManager.InvalidateRequerySuggested(); } }
    private bool importing;
    public bool Importing { get => importing; private set { if (Set(ref importing, value)) CommandManager.InvalidateRequerySuggested(); } }
    public ICommand EditSubscription { get; }
    public ICommand RefreshSubscription { get; }
    public ICommand DeleteSubscription { get; }
    public ICommand AddRule { get; }
    public ICommand EditRule { get; }
    public ICommand DeleteRule { get; }
    public ICommand AddPreset { get; }
    public ICommand AddApplication { get; }
    public ICommand AddProfile { get; }
    public ICommand DeleteProfile { get; }
    public ICommand ExportProfile { get; }
    public ICommand ImportProfile { get; }
    public ICommand SaveSettings { get; }
    public ICommand ChangeTunnelMode { get; }
    public ICommand OpenLogs { get; }
    public ICommand OpenConfig { get; }
    public ICommand RestartService { get; }
    public ICommand DismissBanner { get; }
    public event Action? StatusChanged;

    public MainViewModel(StateStore? storage = null) : this(storage, new ServiceClient(), null) { }
    internal MainViewModel(StateStore? storage, IServiceClient client, Func<ConnectSpec, CancellationToken, Task>? probe, Func<Server, LatencyMode, bool, CancellationToken, Action?, Task<LatencyResult>>? latencyProbe = null)
    {
        service = client; testProbe = probe; testLatency = latencyProbe;
        store = storage ?? new(); Data = store.Load();
        latencyIndex = Enum.IsDefined(Settings.LatencyMode) ? (int)Settings.LatencyMode : 1;
        if (Settings.DesignVersion < 1) { Settings.AccentColor = "Blue"; Settings.DesignVersion = 1; store.Save(Data); }
        Settings.TunnelMode = TunnelMode.Tun;
        if (storage is null) Settings.StartWithWindows = AutoStart.IsEnabled;
        activeProfile = Data.Profiles.FirstOrDefault(p => p.Id == Data.SelectedProfileId) ?? Data.Profiles[0];
        selectedServer = Settings.RestoreServer ? Data.Servers.FirstOrDefault(s => s.Id == Data.SelectedServerId) : null;
        page = new HomePage(this);
        Presets = PresetCatalog.Load();
        AttachProfile(); RefreshServers(); RefreshRules();
        Navigate = new Command(p => Go((string)p!)); CollapseSidebar = new Command(_ => SidebarCollapsed = !SidebarCollapsed);
        ToggleConnect = new Command(_ => _ = ToggleSafelyAsync(), () => State != ConnectionState.Disconnecting && !updateInstalling);
        SelectServer = new Command(p => { var node = (Server)p!; if (!node.Supported) Banner = node.UnsupportedReason; else SelectedServer = node; });
        ConfigureTrustTunnel = new Command(p =>
        {
            if (p is not Server { IsTrustTunnel: true } node) return;
            var transport = Dialogs.TrustTunnelTransport(node.Transport);
            if (transport is null || transport == node.Transport) return;
            node.Transport = transport; node.PreferredTrustTunnelTransport = transport; node.Notify(nameof(Server.Protocol)); node.Notify(nameof(Server.ConnectionLabel));
            node.Latency = "—"; node.LatencyMs = null; Save(); RefreshServers();
            if (SelectedServer?.Id == node.Id || ActiveProfile.Rules.Any(r => r.UseVpn && r.ServerId == node.Id)) QueueApply();
        });
        FavoriteServer = new Command(p => { var s = (Server)p!; s.Favorite = !s.Favorite; Save(); });
        PingAll = Async(_ => ScanAsync(true), () => !Scanning);
        PingServer = Async(async p => { if ((p as Server ?? SelectedServer) is { } node) await MeasureServerAsync(node, true, lifetime.Token); }, () => !Scanning);
        ChangeRoutingMode = new Command(_ => { var mode = Dialogs.RoutingMode(IsWholePc); if (mode is { } all) IsWholePc = all; });
        CancelPing = new Command(_ => { scanGeneration++; scan?.Cancel(); });
        RefreshSubscriptions = Async(async _ => { foreach (var sub in Data.Subscriptions.ToArray()) await RefreshSubAsync(sub); });
        AddTrustTunnel = new Command(_ => { var sub = Dialogs.ManualTrustTunnel(); if (sub is null) return; var nodes = VlessParser.ParseSubscription(sub.Source); Data.Subscriptions.Add(sub); ReplaceServers(sub, nodes); Save(); Go("Servers"); });
        ToggleSubscriptionVisibility = new Command(p => { if (p is not Subscription sub) return; sub.Hidden = !sub.Hidden; Save(); RefreshServers(); });
        AddSubscription = Async(async _ => { var sub = Dialogs.Subscription(null); if (sub is null) return; var (servers, info) = await SubscriptionLoader.LoadWithInfoAsync(sub.Source, lifetime.Token); sub.Apply(info); Data.Subscriptions.Add(sub); ReplaceServers(sub, servers); Save(); Go("Servers"); });
        ImportQuickSubscription = Async(async _ =>
        {
            Importing = true;
            try
            {
                var source = QuickSource.Trim();
                var sub = new Subscription { Source = source, Name = Dialogs.SuggestSubscriptionName(source) };
                var (nodes, info) = await SubscriptionLoader.LoadWithInfoAsync(source, lifetime.Token);
                sub.Apply(info); if (info.Title.Length > 0) sub.Name = info.Title;
                Data.Subscriptions.Add(sub); ReplaceServers(sub, nodes); Save(); QuickSource = ""; Go("Servers");
            }
            finally { Importing = false; }
        }, () => !Importing && !string.IsNullOrWhiteSpace(QuickSource));
        EditSubscription = Async(async p => { var old = (Subscription)p!; var sub = Dialogs.Subscription(old); if (sub is null) return; var servers = await SubscriptionLoader.LoadAsync(sub.Source, lifetime.Token); old.Name = sub.Name; old.Source = sub.Source; ReplaceServers(old, servers); Save(); Notify(nameof(NoSubscriptions)); });
        RefreshSubscription = Async(p => RefreshSubAsync((Subscription)p!));
        ExportBackup = new Command(_ =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Bebekon backup (*.json)|*.json", FileName = "bebekon-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, BackupCodec.Export(Data, ActiveProfile));
            Banner = T("Резервная копия сохранена. В ней ссылки подписок — храните файл в надёжном месте.", "Backup saved. It contains subscription links; keep the file safe.");
        });
        ImportBackup = Async(async _ =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Bebekon backup (*.json)|*.json|All files|*.*" };
            if (dialog.ShowDialog() != true) return;
            var data = BackupCodec.Import(await File.ReadAllTextAsync(dialog.FileName, lifetime.Token));
            int added = 0, failed = data.Skipped;
            // Subscriptions download current servers; nothing already present is replaced or removed.
            foreach (var item in data.Subscriptions.Where(i => Data.Subscriptions.All(s => s.Source != i.Source)))
            {
                try { var (servers, info) = await SubscriptionLoader.LoadWithInfoAsync(item.Source, lifetime.Token); var sub = new Subscription { Name = item.Name, Source = item.Source, Hidden = item.Hidden }; sub.Apply(info); Data.Subscriptions.Add(sub); ReplaceServers(sub, servers); added++; }
                catch (Exception e) when (e is UserError or HttpRequestException or OperationCanceledException && !lifetime.IsCancellationRequested) { failed++; }
            }
            static string Signature(RoutingRule r) => $"{r.Kind}|{r.UseVpn}|{string.Join('\n', r.Values).ToLowerInvariant()}";
            var existing = ActiveProfile.Rules.Select(Signature).ToHashSet();
            var rules = data.Rules.Where(r => existing.Add(Signature(r))).ToList();
            foreach (var rule in rules) ActiveProfile.Rules.Add(rule);
            if (data.Settings is { } settings && settings.Mtu is >= 1280 and <= 9000)
                foreach (var property in typeof(Settings).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Settings.DesignVersion))) property.SetValue(Settings, property.GetValue(settings));
            Save(); if (rules.Count > 0) RulesChanged(); else QueueApply();
            Banner = T($"Восстановлено подписок: {added}, правил: {rules.Count}", $"Restored subscriptions: {added}, rules: {rules.Count}") + (failed > 0 ? T($" · не удалось: {failed}", $" · failed: {failed}") : "");
        });
        // Provider links are validated as https when stored (ProviderInfo.SafeLink).
        OpenProviderLink = new Command(p => { if (ProviderInfo.SafeLink(p as string) is { Length: > 0 } link) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); });
        DeleteSubscription = Async(async p => { var sub = (Subscription)p!; if (!Dialogs.Confirm(T("Удалить подписку?", "Delete subscription?"), sub.Name)) return; if (Connected && SelectedServer?.SubscriptionId == sub.Id) await DisconnectAsync(); Data.Subscriptions.Remove(sub); foreach (var s in Data.Servers.Where(s => s.SubscriptionId == sub.Id).ToArray()) Data.Servers.Remove(s); if (!Data.Servers.Contains(SelectedServer!)) SelectedServer = Data.Servers.FirstOrDefault(); Save(); RefreshServers(); Notify(nameof(NoSubscriptions)); });
        AddRule = new Command(_ => { var rule = Dialogs.Rule(null, Data.Servers); if (rule is not null) { ActiveProfile.Rules.Add(rule); RulesChanged(); } });
        EditRule = new Command(p => { var rule = (RoutingRule)p!; var edit = Dialogs.Rule(rule, Data.Servers); if (edit is null) return; var i = ActiveProfile.Rules.IndexOf(rule); rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules[i] = edit; RulesChanged(); });
        DeleteRule = new Command(p => { var rule = (RoutingRule)p!; rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules.Remove(rule); RulesChanged(); });
        AddPreset = new Command(_ => { var preset = Dialogs.Preset(Presets); if (preset is null) return; var count = PresetCatalog.Apply(preset, ActiveProfile); RulesChanged(); Banner = count == 0 ? T("Этот набор уже добавлен.", "This preset has already been added.") : T($"Добавлено правил: {count}.", $"Added {count} rules."); });
        AddApplication = Async(async _ => { var app = await Dialogs.ApplicationAsync(); if (app is null) return; ActiveProfile.Rules.Add(new() { CreatedAt = DateTimeOffset.UtcNow, Kind = RuleKind.Application, Name = app.Name, Values = [app.Path] }); RulesChanged(); });
        AddProfile = new Command(_ => { var name = Dialogs.Text(T("Создать профиль", "Create profile"), T("Название", "Name"), "Работа"); if (string.IsNullOrWhiteSpace(name)) return; var profile = new Profile { Name = name }; Data.Profiles.Add(profile); ActiveProfile = profile; Save(); });
        DeleteProfile = new Command(_ => { if (Data.Profiles.Count <= 1) { Banner = T("Нужен хотя бы один профиль.", "Keep at least one profile."); return; } if (!Dialogs.Confirm(T("Удалить профиль?", "Delete profile?"), ActiveProfile.Name)) return; var old = ActiveProfile; ActiveProfile = Data.Profiles.First(p => p != old); Data.Profiles.Remove(old); Save(); });
        ExportProfile = new Command(_ => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Bebekon profile (*.json)|*.json", FileName = ActiveProfile.Name + ".json" }; if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, ProfileCodec.Export(ActiveProfile)); });
        ImportProfile = new Command(_ => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Bebekon profile (*.json)|*.json" }; if (dialog.ShowDialog() != true) return; try { var profile = ProfileCodec.Import(File.ReadAllText(dialog.FileName)); Data.Profiles.Add(profile); ActiveProfile = profile; } catch (Exception e) { Report(e); } });
        SaveSettings = new Command(_ => { if (Settings.Mtu is < 1280 or > 9000) { Banner = "MTU: 1280–9000"; return; } I18n.Set(Settings.Language); AutoStart.Set(Settings.StartWithWindows); Save(); NotifyStatus(); QueueApply(); Banner = T("Настройки сохранены", "Settings saved"); });
        ChangeTunnelMode = new Command(p => { if ((string)p! == "DNS") { Go("Settings"); return; } SetTunnelMode((string)p! == "TUN" ? TunnelMode.Tun : TunnelMode.Proxy); });
        OpenLogs = Async(async _ => { try { var reply = await service.SendAsync(new("GetLogs")); if (reply.Ok && reply.Message is not null) new SafeLog(Paths.Logs, "service").Write(reply.Message); } catch { } OpenFolder(Paths.Logs); });
        OpenConfig = new Command(_ => OpenFolder(store.Root));
        RestartService = Async(async _ => { await EnsureServiceAsync(); var reconnect = Connected; if (reconnect) await DisconnectAsync(); try { await service.SendAsync(new("Shutdown")); await Task.Delay(350); } catch { } var reply = await service.SendAsync(new("GetStatus"), true); if (!reply.Ok) throw new UserError(reply.Message ?? "Service unavailable."); if (reconnect) await ConnectAsync(); Banner = T("Служба перезапущена", "Service restarted"); });
        DismissBanner = new Command(_ => Banner = null);
        I18n.Set(Settings.Language);
        ThemeManager.Apply(Settings);
        InitializeUpdates();
        SystemEvents.PowerModeChanged += OnPower; NetworkChange.NetworkAvailabilityChanged += OnNetwork; NetworkChange.NetworkAddressChanged += OnAddress;
    }
    private AsyncCommand Async(Func<object?, Task> action, Func<bool>? can = null) => new(action, Report, can);
    private void SetTunnelMode(TunnelMode mode)
    {
        if (Settings.TunnelMode == mode) return;
        Settings.TunnelMode = mode; Save(); Notify(nameof(Settings)); Notify(nameof(IsTunMode)); Notify(nameof(IsProxyMode)); QueueApply(); NotifyStatus();
    }
    private string T(string ru, string en) => Settings.Language == "English" ? en : ru;
    public async Task InitializeAsync(bool systemStartup = false)
    {
        _ = WatchUpdatesAsync();
        try { var response = await service.SendAsync(new("GetStatus")); ApplyStatus(response.Status); if (Connected) { await DisconnectAsync(); await ConnectAsync(); } }
        catch { SystemProxy.Restore(); }
        _ = RefreshMapOriginAsync();
        WarnSubscription();
        _ = AutoRefreshAsync();
        if (Settings.AutoConnect && !Connected && SelectedServer is not null) await AutoConnectAsync(systemStartup);
    }
    private readonly Dictionary<string, DateTimeOffset> refreshAttempts = [];
    /// <summary>Providers rotate servers; keep subscriptions current. A failure keeps the old servers and retries after an hour.</summary>
    private async Task AutoRefreshAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now;
                foreach (var sub in Data.Subscriptions.Where(s => s.RefreshDue(now) && (!refreshAttempts.TryGetValue(s.Id, out var last) || now - last >= TimeSpan.FromHours(1))).ToArray())
                {
                    refreshAttempts[sub.Id] = now;
                    try { await RefreshSubAsync(sub); log.Write("Subscription refreshed automatically."); }
                    catch (Exception e) when (e is not OperationCanceledException || !lifetime.IsCancellationRequested) { log.Write("Automatic subscription refresh failed: " + e.GetType().Name); }
                }
                await Task.Delay(TimeSpan.FromMinutes(30), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task AutoConnectAsync(bool systemStartup)
    {
        // Right after Windows starts, Wi-Fi and DNS often come up later than the app. Wait for a
        // network instead of failing with a timeout, then retry once if the first attempt is early.
        for (var attempt = 0; attempt < (systemStartup ? 2 : 1); attempt++)
        {
            try
            {
                for (var waited = 0; systemStartup && waited < 90 && !NetworkInterface.GetIsNetworkAvailable(); waited++) await Task.Delay(1000, lifetime.Token);
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token);
                if (Connected || ConnectionBusy) return;
                await ConnectAsync(); return;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            // Never repeat a declined administrator prompt or a server/config problem on its own.
            catch (Exception e) when (e is not UserError && attempt + 1 < (systemStartup ? 2 : 1)) { }
            catch (Exception e) { Report(e); return; }
        }
    }
    /// <summary>Prefills a provider's bebekon:// link; adding it still takes the user's click.</summary>
    public void OfferImport(string text)
    {
        if (ImportLink.Decode(text) is not { } link) { Banner = T("Ссылка Bebekon повреждена или ведёт не на подписку.", "The Bebekon link is damaged or does not point to a subscription."); return; }
        QuickSource = link; Go("Subscriptions");
        Banner = T("Проверьте ссылку от провайдера и нажмите «Добавить».", "Check the provider's link and press Add.");
    }
    public void Go(string name)
    {
        scanGeneration++; scan?.Cancel(); PageName = name; Page = name switch { "Servers" => new ServersPage(this), "Rules" => new RulesPage(this), "Subscriptions" => new SubscriptionsPage(this), "Settings" => new SettingsPage(this), _ => new HomePage(this) };
        if (name == "Servers" && Data.Servers.Count > 0) _ = ScanSafelyAsync();
    }
    private async Task ScanSafelyAsync()
    {
        var generation = ++scanGeneration;
        try { while (Scanning && !lifetime.IsCancellationRequested) await Task.Delay(50, lifetime.Token); if (!lifetime.IsCancellationRequested && generation == scanGeneration && PageName == "Servers") await ScanAsync(false); }
        catch (OperationCanceledException) { } catch (Exception e) { Report(e); }
    }
    private Task ScanAsync(bool force) => activeScan = RunScanAsync(force);
    public async Task StopScansAsync()
    {
        scanGeneration++; scan?.Cancel();
        if (activeScan is not null) await activeScan;
    }
    private async Task RunScanAsync(bool force)
    {
        if (Scanning) return; SortIndex = 0; Scanning = true; scan?.Dispose(); scan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = scan.Token;
        var nodes = DisplayServers.ToArray();
        foreach (var node in nodes) if (node.LatencyMs is null) node.Latency = T("В очереди", "Queued");
        try { await Task.WhenAll(nodes.Select(s => MeasureServerAsync(s, force, token))); RefreshServers(); }
        catch (OperationCanceledException) { }
        finally { foreach (var node in nodes.Where(n => n.Latency is "В очереди" or "Queued" or "Проверка…" or "Checking…")) node.Latency = "—"; Scanning = false; }
    }
    private async Task MeasureServerAsync(Server node, bool force, CancellationToken token)
    {
        var mode = (LatencyMode)LatencyIndex;
        var generation = latencyGeneration;
        var request = ++latencyRequest;
        latestLatencyRequests[node.Id] = request;
        var identity = ServerRefresh.ConnectionKey(node);
        latency.DetectCountries = MapLocationEnabled;
        bool Current() => generation == latencyGeneration && latestLatencyRequests.GetValueOrDefault(node.Id) == request && (Data.Servers.Contains(node) || ReferenceEquals(SelectedServer, node)) && identity == ServerRefresh.ConnectionKey(node);
        var result = await (testLatency ?? latency.MeasureAsync)(node, mode, force, token, () => { if (Current() && node.LatencyMs is null) node.Latency = T("Проверка…", "Checking…"); });
        token.ThrowIfCancellationRequested();
        if (!Current()) return;
        var supported = node.Supported || mode is LatencyMode.Tcp or LatencyMode.Icmp;
        node.Latency = !supported ? "—" : result.Milliseconds is { } ms
            ? ms + T(" мс", " ms")
            : result.TimedOut ? T("Таймаут", "Timeout") : T("Недоступен", "Unavailable");
        node.MeasuredMode = result.Mode;
        node.LatencyMs = result.Milliseconds ?? (supported ? -1 : null);
        if (result.Country is { } country && node.CountryCode.Length == 0) { node.DetectedCountry = country; Save(); if (ReferenceEquals(node, SelectedServer)) Notify(nameof(SelectedCountry)); }
        QueuePingSort();
    }
    private async void QueuePingSort()
    {
        if (SortIndex != 0 || pingSortPending) return;
        pingSortPending = true;
        try { await Task.Delay(120, lifetime.Token); if (SortIndex == 0) RefreshServers(); }
        catch (OperationCanceledException) { }
        finally { pingSortPending = false; }
    }
    private async Task RefreshSubAsync(Subscription sub) { var (servers, info) = await SubscriptionLoader.LoadWithInfoAsync(sub.Source, lifetime.Token); sub.Apply(info); ReplaceServers(sub, servers); Save(); WarnSubscription(); }
    /// <summary>Warn before the provider cuts the selected server off (expiry or traffic limit).</summary>
    private void WarnSubscription() { if (Data.Subscriptions.FirstOrDefault(s => s.Id == SelectedServer?.SubscriptionId) is { HasWarning: true } sub) Banner = sub.Name + ": " + sub.Warning; }
    internal void ReplaceServers(Subscription sub, List<Server> incoming)
    {
        var oldId = SelectedServer?.Id;
        var merged = ServerRefresh.Merge(sub.Id, Data.Servers, incoming, oldId);
        foreach (var s in Data.Servers.Where(s => s.SubscriptionId == sub.Id).ToArray()) Data.Servers.Remove(s);
        foreach (var s in merged) Data.Servers.Add(s);
        sub.Updated = DateTimeOffset.Now; sub.ServerCount = incoming.Count;
        SelectedServer = oldId is null ? Data.Servers.FirstOrDefault(s => s.Supported) : Data.Servers.FirstOrDefault(s => s.Id == oldId) ?? SelectedServer;
        RefreshServers(); Notify(nameof(NoSubscriptions)); Data.Subscriptions = new(Data.Subscriptions); Notify(nameof(Data));
    }
    private IEnumerable<Server> DisplayServers => Data.Servers.Where(s => !Data.Subscriptions.Any(sub => sub.Id == s.SubscriptionId && sub.Hidden));
    public void RefreshServers()
    {
        foreach (var server in Data.Servers) server.SubscriptionLabel = Data.Subscriptions.FirstOrDefault(s => s.Id == server.SubscriptionId)?.Name ?? "";
        IEnumerable<Server> servers = DisplayServers.Where(s => s.Name.Contains(ServerSearch, StringComparison.OrdinalIgnoreCase) || s.SubscriptionLabel.Contains(ServerSearch, StringComparison.OrdinalIgnoreCase));
        servers = SortIndex == 0 ? servers.OrderBy(s => s.LatencyMs is >= 0 ? s.LatencyMs : long.MaxValue).ThenBy(s => s.Name) : servers.OrderBy(s => s.Name);
        var array = servers.ToArray(); var rows = new List<ServerRow>();
        for (var i = 0; i < array.Length; i += ListMode ? 1 : 2) rows.Add(new(array[i], !ListMode && i + 1 < array.Length ? array[i + 1] : null));
        // Move existing rows instead of clearing the list on every completed probe.
        for (var i = 0; i < rows.Count; i++) {
            if (i < ServerRows.Count && ServerRows[i] == rows[i]) continue;
            var existing = ServerRows.IndexOf(rows[i]);
            if (existing >= 0) ServerRows.Move(existing, i); else ServerRows.Insert(i, rows[i]);
        }
        while (ServerRows.Count > rows.Count) ServerRows.RemoveAt(ServerRows.Count - 1);
        Notify(nameof(ServerCount)); Notify(nameof(NoServers)); Notify(nameof(ServerLabel));
        // Auto mode follows refreshed or hidden subscriptions; latency changes alone don't reconnect.
        if (Settings.AutoServer && desiredConnected && spec?.AutoServers is { } running && AutoKey(running) != AutoKey(DisplayServers)) QueueApply();
    }
    private void RefreshRules() { VisibleRules.Clear(); var rules = ActiveProfile.Rules.Where(r => r.Name.Contains(RuleSearch, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(RuleSearch, StringComparison.OrdinalIgnoreCase)); if (RuleSortIndex == 0) rules = rules.OrderByDescending(r => r.CreatedAt); foreach (var r in rules) VisibleRules.Add(r); Notify(nameof(RuleCount)); Notify(nameof(NoRules)); }
    private void AttachProfile() { foreach (var r in ActiveProfile.Rules) { r.PropertyChanged -= OnRuleChanged; r.PropertyChanged += OnRuleChanged; } }
    private void DetachProfile(Profile p) { foreach (var r in p.Rules) r.PropertyChanged -= OnRuleChanged; }
    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RoutingRule.UseVpn)) { Save(); QueueApply(); } }
    internal void RulesChanged() { AttachProfile(); RefreshRules(); Save(); QueueApply(); }
    public void Reorder(RoutingRule source, RoutingRule target) { var a = ActiveProfile.Rules.IndexOf(source); var b = ActiveProfile.Rules.IndexOf(target); if (a < 0 || b < 0 || a == b) return; ActiveProfile.Rules.Move(a, b); RulesChanged(); }
    private void QueueApply()
    {
        configurationVersion++;
        ScheduleApply();
    }
    private void ScheduleApply()
    {
        if (!desiredConnected) return;
        applyDelay?.Cancel(); applyDelay?.Dispose(); applyDelay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = ApplyLaterAsync(applyDelay.Token);
    }
    private async Task ApplyLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(650, token); await connectionGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!desiredConnected || appliedConfigurationVersion == configurationVersion) return;
                await StopConnectionCoreAsync();
                // Keep the gate across stop/start; later edits are captured by the new snapshot.
                if (desiredConnected) await ConnectCoreAsync();
            }
            finally { connectionGate.Release(); }
        }
        catch (OperationCanceledException) { } catch (Exception e) { Report(e); }
    }
    private async Task ToggleSafelyAsync() { try { if (desiredConnected || Connected) await DisconnectAsync(); else await ConnectAsync(); } catch (Exception e) { Report(e); } }
    public async Task ConnectAsync()
    {
        desiredConnected = true;
        await connectionGate.WaitAsync(lifetime.Token);
        try { if (desiredConnected && !Connected) await ConnectCoreAsync(); }
        finally { connectionGate.Release(); }
    }
    private async Task ConnectCoreAsync()
    {
        monitorId++; networkDelay?.Cancel();
        connectionAttempt?.Dispose(); connectionAttempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = connectionAttempt.Token;
        try
        {
            if (SelectedServer is null) { desiredConnected = false; Go("Subscriptions"); Banner = T("Добавьте подписку и выберите сервер.", "Add a subscription and choose a server."); return; }
            if (!SelectedServer.Supported) throw new UserError(SelectedServer.UnsupportedReason!);
            State = ConnectionState.Connecting; Banner = null; VpnIp = "—"; SetTraffic(null);
            // Finish a direct-IP location lookup before installing TUN routes. Otherwise a quick
            // connect discards the origin, or a late lookup could mistake VPN egress for the user.
            locatingBeforeTunnel = true;
            // Location services can be slow or blocked; the map must never delay connecting for long.
            try { await RefreshMapOriginAsync().WaitAsync(TimeSpan.FromSeconds(3), token); }
            catch (TimeoutException) { }
            finally { locatingBeforeTunnel = false; }
            token.ThrowIfCancellationRequested();
            spec = new(SelectedServer, ActiveProfile, Settings, LatencyService.FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), Data.Servers.Where(s => ActiveProfile.Rules.Any(r => r.UseVpn && r.ServerId == s.Id)).ToList(),
                AutoServers: Settings.AutoServer ? AutoPool : null);
            if (MapLocationEnabled) {
                int originPort;
                do { originPort = LatencyService.FreePort(); } while (originPort == spec.ProbePort || originPort == 17890);
                spec = spec with { OriginProbePort = originPort };
            }
            spec = System.Text.Json.JsonSerializer.Deserialize<ConnectSpec>(System.Text.Json.JsonSerializer.Serialize(spec, Json.Options), Json.Options)!;
            var version = configurationVersion;
            store.SaveConnection(spec);
            await EnsureServiceAsync();
            token.ThrowIfCancellationRequested();
            var reply = await service.SendAsync(new("StartCore", spec), true, token);
            if (!reply.Ok) throw new UserError(reply.Message ?? reply.Status.Error ?? "Не удалось подключиться.");
            await ProbeConnectionAsync(spec, token);
            token.ThrowIfCancellationRequested();
            if (spec.Settings.TunnelMode == TunnelMode.Proxy && testProbe is null) SystemProxy.Enable();
            appliedConfigurationVersion = version;
            ApplyStatus(reply.Status); _ = MonitorAsync();
            // IP lookup is cosmetic; don't hold the connection gate for its HTTP timeout.
            if (testProbe is null) _ = UpdateIpAsync(spec, monitorId, token);
            if (version != configurationVersion) ScheduleApply();
        }
        catch
        {
            try { await service.SendAsync(new("StopCore"), true); } catch { }
            if (testProbe is null) SystemProxy.Restore();
            SetTraffic(null); State = token.IsCancellationRequested ? ConnectionState.Disconnected : ConnectionState.Error;
            if (!token.IsCancellationRequested) { desiredConnected = false; throw; }
        }
    }
    private async Task ProbeConnectionAsync(ConnectSpec current, CancellationToken token)
    {
        if (testProbe is not null) { await testProbe(current, token); return; }
        // A cold first request over a phone hotspot or mobile link can need several round trips.
        // One slow endpoint must not tear down a working tunnel, so allow time and a second target.
        Exception? failure = null;
        foreach (var target in new[] { "https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204" })
        {
            try
            {
                using var http = LatencyService.ProbeClient(current); http.Timeout = TimeSpan.FromSeconds(10);
                using var response = await http.GetAsync(target, token); response.EnsureSuccessStatusCode(); return;
            }
            catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !token.IsCancellationRequested) { failure = e; }
        }
        throw failure!;
    }
    private async Task UpdateIpAsync(ConnectSpec current, int generation, CancellationToken token)
    {
        try { var value = await LatencyService.VpnIpAsync(current, token); if (generation == monitorId && Connected) VpnIp = value; } catch { }
    }
    private async Task EnsureServiceAsync()
    {
        if (testProbe is not null) return;
        await ServiceInstaller.EnsureInstalledAsync(() => Banner = T(
            "Подтвердите запрос Windows: требуется установка или обновление службы TUN.",
            "Approve the Windows prompt: the TUN helper needs installation or an update."), lifetime.Token);
        Banner = null;
    }
    public async Task DisconnectAsync()
    {
        desiredConnected = false; applyDelay?.Cancel(); networkDelay?.Cancel(); connectionAttempt?.Cancel(); monitorId++;
        await connectionGate.WaitAsync();
        try { await StopConnectionCoreAsync(); }
        catch { State = ConnectionState.Error; throw; }
        finally { connectionGate.Release(); }
    }
    private async Task StopConnectionCoreAsync()
    {
        monitorId++; networkDelay?.Cancel(); connectionAttempt?.Cancel(); State = ConnectionState.Disconnecting;
        try
        {
            var reply = await service.SendAsync(new("StopCore"), true, lifetime.Token);
            if (!reply.Ok) throw new UserError(reply.Message ?? "Не удалось остановить VPN.");
            ApplyStatus(reply.Status);
        }
        finally { if (testProbe is null) SystemProxy.Restore(); SetTraffic(null); VpnIp = "—"; Session = "—"; }
    }
    private async Task MonitorAsync()
    {
        var id = ++monitorId;
        try { while (id == monitorId && Connected && !lifetime.IsCancellationRequested) { await Task.Delay(1000, lifetime.Token); if (id != monitorId || !Connected) break; var response = await service.SendAsync(new("GetStatus"), ct: lifetime.Token); if (id != monitorId) break; ApplyStatus(response.Status); _ = RefreshMapOriginAsync(); _ = RefreshMapDestinationAsync(); if (started is { } time) Session = (DateTimeOffset.UtcNow - time).ToString(@"hh\:mm\:ss"); if (!Connected && testProbe is null) SystemProxy.Restore(); } }
        catch (OperationCanceledException) { } catch (Exception e) { if (id == monitorId) { if (testProbe is null) SystemProxy.Restore(); State = ConnectionState.Error; SetTraffic(null); Report(e); } }
    }
    private void ApplyStatus(ServiceStatus s) { State = s.State; started = s.ConnectedAt; SetTraffic(s.State == ConnectionState.Connected ? s.Traffic : null); if (s.Error is not null) Banner = s.Error; }
    private void SetTraffic(TrafficSnapshot? value)
    {
        if (value is null) { DownloadHistory = Array.Empty<double>(); UploadHistory = Array.Empty<double>(); }
        else if (traffic?.SampledAt != value.SampledAt)
        {
            // One bounded history per direction, sampled from the core rather than decorative data.
            DownloadHistory = DownloadHistory.TakeLast(119).Append(Math.Max(0, value.DownloadBytesPerSecond * 8 / 1_000_000)).ToArray();
            UploadHistory = UploadHistory.TakeLast(119).Append(Math.Max(0, value.UploadBytesPerSecond * 8 / 1_000_000)).ToArray();
        }
        var outbound = traffic?.ActiveOutbound; traffic = value;
        if (outbound != value?.ActiveOutbound) Notify(nameof(ServerLabel));
        foreach (var name in new[] { nameof(DownloadRate), nameof(UploadRate), nameof(DownloadTotal), nameof(UploadTotal), nameof(DownloadHistory), nameof(UploadHistory) }) Notify(name);
    }
    private void NotifyStatus() { foreach (var n in new[] { nameof(Connected), nameof(ConnectionBusy), nameof(ConnectLabel), nameof(StatusLabel), nameof(StatusDetail), nameof(FooterLabel), nameof(FooterMode), nameof(DownloadRate), nameof(UploadRate) }) Notify(n); StatusChanged?.Invoke(); }
    public void Report(Exception e) { log.Write("Operation failed: " + e.GetType().Name); Banner = e switch {
        UserError => e.Message,
        OperationCanceledException or TimeoutException => T("Сервер не ответил вовремя. Проверьте интернет или выберите другой сервер.", "The server did not respond in time. Check your internet or choose another server."),
        // The tunnel started, but the HTTPS check through it failed.
        HttpRequestException => T("Сервер не пропускает трафик. Выберите другой сервер; если не помогает — включите «Совместимость» в настройках.", "The server does not pass traffic. Choose another server; if that does not help, enable Compatibility in settings."),
        _ => T("Не удалось выполнить действие. Проверьте подключение и параметры.", "Action failed. Check your connection and settings.") }; }
    private void Save() { try { store.Save(Data); } catch (Exception e) { Report(e); } }
    private static void OpenFolder(string dir) { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = true }); }
    private void OnPower(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) ScheduleNetworkCheck(true); }
    private void OnNetwork(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) ScheduleNetworkCheck(); }
    private void OnAddress(object? sender, EventArgs e) => ScheduleNetworkCheck();
    private void ScheduleNetworkCheck(bool force = false) => Application.Current.Dispatcher.InvokeAsync(async () =>
    {
        localAddresses = NetworkAddresses.Read(); Notify(nameof(LocalIp)); Notify(nameof(LocalIpLabel)); Notify(nameof(LocalIpDetail));
        var currentUplink = UplinkSignature(); var changed = currentUplink != uplinkSignature; uplinkSignature = currentUplink;
        if (changed) { originChecked = default; originAttempted = default; MapOrigin = null; _ = RefreshMapOriginAsync(); }
        if ((!force && !changed) || !desiredConnected || !Connected || recovering) return;
        networkDelay?.Cancel(); networkDelay?.Dispose(); networkDelay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = networkDelay.Token;
        var generation = monitorId;
        try { await Task.Delay(2000, token); await RecoverAsync(generation, token); } catch (OperationCanceledException) { }
    });
    internal Task CheckRecoveryAsync() => RecoverAsync(monitorId, lifetime.Token);
    private async Task RecoverAsync(int generation, CancellationToken token)
    {
        if (recovering || !Connected || !desiredConnected || generation != monitorId) return; recovering = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var reply = await service.SendAsync(new("GetStatus"), ct: token);
                    if (reply.Status.State != ConnectionState.Connected) throw new UserError("VPN needs reconnect.");
                    if (spec is not null) await ProbeConnectionAsync(spec, token);
                    return;
                }
                catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    if (generation != monitorId || !desiredConnected || !Connected) return;
                    if (attempt == 0) await Task.Delay(1500, token);
                }
            }
            await connectionGate.WaitAsync(token);
            try { if (generation == monitorId && desiredConnected && Connected) { await StopConnectionCoreAsync(); if (desiredConnected) await ConnectCoreAsync(); } }
            finally { connectionGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Report(e); }
        finally { recovering = false; }
    }
    private static string UplinkSignature()
    {
        try
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Where(n => !new[] { "tun", "tap", "vpn", "virtual", "hyper-v", "loopback", "wireguard", "zerotier", "tailscale", "hamachi" }.Any(word => (n.Name + " " + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase)) && n.Name != "Bebekon")
                .Select(n => (n.Id, Properties: n.GetIPProperties())).Where(n => n.Properties.GatewayAddresses.Count > 0)
                .Select(n => n.Id + ":" + string.Join(",", n.Properties.UnicastAddresses.Select(a => a.Address.ToString()).Order())).Order());
        }
        catch (NetworkInformationException) { return ""; }
    }
    public void Dispose() { Save(); desiredConnected = false; lifetime.Cancel(); updateCancellation?.Cancel(); updater?.Dispose(); connectionAttempt?.Cancel(); scan?.Cancel(); applyDelay?.Cancel(); networkDelay?.Cancel(); SystemEvents.PowerModeChanged -= OnPower; NetworkChange.NetworkAvailabilityChanged -= OnNetwork; NetworkChange.NetworkAddressChanged -= OnAddress; }
}
