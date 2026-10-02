using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Windows.Input;
using Microsoft.Win32;

namespace Bebekon.App;

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly StateStore store;
    private readonly ServiceClient service = new();
    private readonly LatencyService latency = new(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"));
    private readonly SafeLog log = new(Paths.Logs, "app");
    private readonly SemaphoreSlim connectionGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? scan;
    private CancellationTokenSource? applyDelay;
    private CancellationTokenSource? networkDelay;
    private bool recovering;
    public AppState Data { get; }
    public Settings Settings => Data.Settings;
    public ObservableCollection<ServerRow> ServerRows { get; } = [];
    public ObservableCollection<RoutingRule> VisibleRules { get; } = [];
    public List<Preset> Presets { get; }
    private PageModel page;
    public PageModel Page { get => page; private set => Set(ref page, value); }
    private string pageName = "Home";
    public string PageName { get => pageName; private set => Set(ref pageName, value); }
    private bool collapsed;
    public bool SidebarCollapsed { get => collapsed; set { if (Set(ref collapsed, value)) { Notify(nameof(SidebarWidth)); Notify(nameof(NavTextVisibility)); } } }
    public GridLength SidebarWidth => new(SidebarCollapsed ? 76 : 214);
    public Visibility NavTextVisibility => SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    private Server? selectedServer;
    public Server? SelectedServer { get => selectedServer; set { if (Set(ref selectedServer, value)) { Data.SelectedServerId = value?.Id; Notify(nameof(ServerLabel)); Save(); QueueApply(); } } }
    public string ServerLabel => SelectedServer?.Name ?? T("Выберите сервер", "Choose a server");
    private Profile activeProfile;
    public Profile ActiveProfile { get => activeProfile; set { if (value is null || ReferenceEquals(activeProfile, value)) return; DetachProfile(activeProfile); Set(ref activeProfile, value); Data.SelectedProfileId = value.Id; AttachProfile(); RefreshRules(); Notify(nameof(ModeLabel)); Notify(nameof(IsWholePc)); Notify(nameof(EmptyRulesDetail)); Notify(nameof(StatusDetail)); Save(); QueueApply(); } }
    public bool IsWholePc { get => ActiveProfile.DefaultRoute == RouteTarget.Vpn; set { if (value == IsWholePc) return; ActiveProfile.DefaultRoute = value ? RouteTarget.Vpn : RouteTarget.Direct; Notify(); Notify(nameof(ModeLabel)); Notify(nameof(EmptyRulesDetail)); Notify(nameof(StatusDetail)); Save(); QueueApply(); } }
    public string ModeLabel => IsWholePc ? T("Весь ПК", "Entire PC") : T("По правилам", "By rules");
    private ConnectionState state;
    public ConnectionState State { get => state; private set { if (Set(ref state, value)) { NotifyStatus(); CommandManager.InvalidateRequerySuggested(); } } }
    public bool Connected => State == ConnectionState.Connected;
    public bool ConnectionBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;
    public string ConnectLabel => State switch { ConnectionState.Connecting => T("Подключаем…", "Connecting…"), ConnectionState.Disconnecting => T("Отключаем…", "Disconnecting…"), ConnectionState.Connected => T("Отключить", "Disconnect"), _ => T("Подключить", "Connect") };
    public string StatusLabel => State switch { ConnectionState.Connected => T("Защищено", "Protected"), ConnectionState.Connecting => T("Подключение", "Connecting"), ConnectionState.Disconnecting => T("Отключение", "Disconnecting"), ConnectionState.Error => T("Ошибка подключения", "Connection error"), _ => T("Готов к подключению", "Ready to connect") };
    public string StatusDetail => Connected ? Settings.TunnelMode == TunnelMode.Proxy ? T("Выбранный трафик приложений с прокси идёт через VPN", "Selected traffic from proxy-aware applications uses VPN") : IsWholePc ? T("Весь трафик идёт через VPN", "All traffic goes through VPN") : T("Выбранный трафик идёт через VPN", "Selected traffic goes through VPN") : T("Ваш интернет использует обычное подключение", "Your internet uses your normal connection");
    public string EmptyRulesDetail => IsWholePc ? T("Без правил весь трафик идёт через VPN", "Without rules all traffic goes through VPN") : T("Без правил весь трафик идёт напрямую", "Without rules all traffic goes direct");
    public string FooterLabel => Connected ? T("онлайн", "online") : "offline";
    public string FooterMode => Connected ? Settings.TunnelMode == TunnelMode.Tun ? "TUN" : "Proxy" : "Offline";
    private string vpnIp = "—";
    public string VpnIp { get => vpnIp; private set => Set(ref vpnIp, value); }
    private string session = "—";
    public string Session { get => session; private set => Set(ref session, value); }
    private DateTimeOffset? started;
    private ConnectSpec? spec;
    private int monitorId;
    private int configurationVersion;
    private string? banner;
    public string? Banner { get => banner; set { if (Set(ref banner, value)) Notify(nameof(BannerVisibility)); } }
    public Visibility BannerVisibility => string.IsNullOrWhiteSpace(Banner) ? Visibility.Collapsed : Visibility.Visible;
    private string serverSearch = "", ruleSearch = "";
    public string ServerSearch { get => serverSearch; set { if (Set(ref serverSearch, value)) RefreshServers(); } }
    public string RuleSearch { get => ruleSearch; set { if (Set(ref ruleSearch, value)) RefreshRules(); } }
    private int sortIndex;
    public int SortIndex { get => sortIndex; set { if (Set(ref sortIndex, value)) RefreshServers(); } }
    private int latencyIndex;
    public int LatencyIndex { get => latencyIndex; set { if (Set(ref latencyIndex, value)) { scan?.Cancel(); foreach (var s in Data.Servers) { s.Latency = "—"; s.LatencyMs = null; } RefreshServers(); if (PageName == "Servers") _ = ScanSafelyAsync(); } } }
    private bool listMode;
    public bool ListMode { get => listMode; set { if (Set(ref listMode, value)) { Notify(nameof(SecondColumn)); RefreshServers(); } } }
    public GridLength SecondColumn => new(ListMode ? 0 : 1, GridUnitType.Star);
    private bool scanning;
    public bool Scanning { get => scanning; private set { if (Set(ref scanning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string CoreVersion => ConfigGenerator.CoreVersion;
    public string ServerCount => Data.Servers.Count.ToString();
    public string RuleCount => ActiveProfile.Rules.Count.ToString();
    public Visibility NoServers => Data.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRules => ActiveProfile.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSubscriptions => Data.Subscriptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ICommand Navigate { get; }
    public ICommand CollapseSidebar { get; }
    public ICommand ToggleConnect { get; }
    public ICommand SelectServer { get; }
    public ICommand FavoriteServer { get; }
    public ICommand PingAll { get; }
    public ICommand RefreshSubscriptions { get; }
    public ICommand AddSubscription { get; }
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

    public MainViewModel(StateStore? storage = null)
    {
        store = storage ?? new(); Data = store.Load();
        Settings.TunnelMode = TunnelMode.Tun;
        if (storage is null) Settings.StartWithWindows = AutoStart.IsEnabled;
        activeProfile = Data.Profiles.FirstOrDefault(p => p.Id == Data.SelectedProfileId) ?? Data.Profiles[0];
        selectedServer = Settings.RestoreServer ? Data.Servers.FirstOrDefault(s => s.Id == Data.SelectedServerId) : null;
        page = new HomePage(this);
        Presets = System.Text.Json.JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "resources", "services.json")), Json.Options)!;
        AttachProfile(); RefreshServers(); RefreshRules();
        Navigate = new Command(p => Go((string)p!)); CollapseSidebar = new Command(_ => SidebarCollapsed = !SidebarCollapsed);
        ToggleConnect = Async(_ => ToggleAsync(), () => !ConnectionBusy);
        SelectServer = new Command(p => { var node = (Server)p!; if (!node.Supported) Banner = node.UnsupportedReason; else SelectedServer = node; });
        FavoriteServer = new Command(p => { var s = (Server)p!; s.Favorite = !s.Favorite; Save(); });
        PingAll = Async(_ => ScanAsync(true), () => !Scanning);
        RefreshSubscriptions = Async(async _ => { foreach (var sub in Data.Subscriptions.ToArray()) await RefreshSubAsync(sub); });
        AddSubscription = Async(async _ => { var sub = Dialogs.Subscription(null); if (sub is null) return; var servers = await SubscriptionLoader.LoadAsync(sub.Source, lifetime.Token); Data.Subscriptions.Add(sub); ReplaceServers(sub, servers); Save(); Go("Servers"); });
        EditSubscription = Async(async p => { var old = (Subscription)p!; var sub = Dialogs.Subscription(old); if (sub is null) return; var servers = await SubscriptionLoader.LoadAsync(sub.Source, lifetime.Token); old.Name = sub.Name; old.Source = sub.Source; ReplaceServers(old, servers); Save(); Notify(nameof(NoSubscriptions)); });
        RefreshSubscription = Async(p => RefreshSubAsync((Subscription)p!));
        DeleteSubscription = Async(async p => { var sub = (Subscription)p!; if (!Dialogs.Confirm(T("Удалить подписку?", "Delete subscription?"), sub.Name)) return; if (Connected && SelectedServer?.SubscriptionId == sub.Id) await DisconnectAsync(); Data.Subscriptions.Remove(sub); foreach (var s in Data.Servers.Where(s => s.SubscriptionId == sub.Id).ToArray()) Data.Servers.Remove(s); if (!Data.Servers.Contains(SelectedServer!)) SelectedServer = Data.Servers.FirstOrDefault(); Save(); RefreshServers(); Notify(nameof(NoSubscriptions)); });
        AddRule = new Command(_ => { var rule = Dialogs.Rule(null); if (rule is not null) { ActiveProfile.Rules.Add(rule); RulesChanged(); } });
        EditRule = new Command(p => { var rule = (RoutingRule)p!; var edit = Dialogs.Rule(rule); if (edit is null) return; var i = ActiveProfile.Rules.IndexOf(rule); rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules[i] = edit; RulesChanged(); });
        DeleteRule = new Command(p => { var rule = (RoutingRule)p!; rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules.Remove(rule); RulesChanged(); });
        AddPreset = new Command(_ => { var preset = Dialogs.Preset(Presets); if (preset is null) return; ActiveProfile.Rules.Add(new() { Name = preset.Name, Values = [..preset.Domains] }); RulesChanged(); });
        AddApplication = Async(async _ => { var app = await Dialogs.ApplicationAsync(); if (app is null) return; ActiveProfile.Rules.Add(new() { Kind = RuleKind.Application, Name = app.Name, Values = [app.Path] }); RulesChanged(); });
        AddProfile = new Command(_ => { var name = Dialogs.Text(T("Создать профиль", "Create profile"), T("Название", "Name"), "Работа"); if (string.IsNullOrWhiteSpace(name)) return; var profile = new Profile { Name = name }; Data.Profiles.Add(profile); ActiveProfile = profile; Save(); });
        DeleteProfile = new Command(_ => { if (Data.Profiles.Count <= 1) { Banner = T("Нужен хотя бы один профиль.", "Keep at least one profile."); return; } if (!Dialogs.Confirm(T("Удалить профиль?", "Delete profile?"), ActiveProfile.Name)) return; var old = ActiveProfile; ActiveProfile = Data.Profiles.First(p => p != old); Data.Profiles.Remove(old); Save(); });
        ExportProfile = new Command(_ => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Bebekon profile (*.json)|*.json", FileName = ActiveProfile.Name + ".json" }; if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, ProfileCodec.Export(ActiveProfile)); });
        ImportProfile = new Command(_ => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Bebekon profile (*.json)|*.json" }; if (dialog.ShowDialog() != true) return; try { var profile = ProfileCodec.Import(File.ReadAllText(dialog.FileName)); Data.Profiles.Add(profile); ActiveProfile = profile; } catch (Exception e) { Report(e); } });
        SaveSettings = new Command(_ => { if (Settings.Mtu is < 1280 or > 9000) { Banner = "MTU: 1280–9000"; return; } I18n.Set(Settings.Language); AutoStart.Set(Settings.StartWithWindows); Save(); NotifyStatus(); QueueApply(); Banner = T("Настройки сохранены", "Settings saved"); });
        ChangeTunnelMode = new Command(p => { if ((string)p! == "DNS") { Go("Settings"); return; } Settings.TunnelMode = (string)p! == "TUN" ? TunnelMode.Tun : TunnelMode.Proxy; Save(); Notify(nameof(Settings)); QueueApply(); NotifyStatus(); });
        OpenLogs = Async(async _ => { try { var reply = await service.SendAsync(new("GetLogs")); if (reply.Ok && reply.Message is not null) new SafeLog(Paths.Logs, "service").Write(reply.Message); } catch { } OpenFolder(Paths.Logs); });
        OpenConfig = new Command(_ => OpenFolder(store.Root));
        RestartService = Async(async _ => { await EnsureServiceAsync(); var reconnect = Connected; if (reconnect) await DisconnectAsync(); try { await service.SendAsync(new("Shutdown")); await Task.Delay(350); } catch { } var reply = await service.SendAsync(new("GetStatus"), true); if (!reply.Ok) throw new UserError(reply.Message ?? "Service unavailable."); if (reconnect) await ConnectAsync(); Banner = T("Служба перезапущена", "Service restarted"); });
        DismissBanner = new Command(_ => Banner = null);
        I18n.Set(Settings.Language);
        SystemEvents.PowerModeChanged += OnPower; NetworkChange.NetworkAvailabilityChanged += OnNetwork; NetworkChange.NetworkAddressChanged += OnAddress;
    }
    private AsyncCommand Async(Func<object?, Task> action, Func<bool>? can = null) => new(action, Report, can);
    private string T(string ru, string en) => Settings.Language == "English" ? en : ru;
    public async Task InitializeAsync()
    {
        try { var response = await service.SendAsync(new("GetStatus")); ApplyStatus(response.Status); if (Connected) { await DisconnectAsync(); await ConnectAsync(); } }
        catch { SystemProxy.Restore(); }
        if (Settings.AutoConnect && !Connected && SelectedServer is not null) try { await ConnectAsync(); } catch (Exception e) { Report(e); }
    }
    public void Go(string name)
    {
        scan?.Cancel(); PageName = name; Page = name switch { "Servers" => new ServersPage(this), "Rules" => new RulesPage(this), "Subscriptions" => new SubscriptionsPage(this), "Settings" => new SettingsPage(this), _ => new HomePage(this) };
        if (name == "Servers" && Data.Servers.Count > 0) _ = ScanSafelyAsync();
    }
    private async Task ScanSafelyAsync() { try { while (Scanning && !lifetime.IsCancellationRequested) await Task.Delay(50, lifetime.Token); if (PageName == "Servers") await ScanAsync(false); } catch (OperationCanceledException) { } catch (Exception e) { Report(e); } }
    private async Task ScanAsync(bool force)
    {
        if (Scanning) return; Scanning = true; scan?.Dispose(); scan = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = scan.Token; var mode = LatencyIndex == 0 ? LatencyMode.Fast : LatencyMode.Exact;
        try { await Task.WhenAll(Data.Servers.ToArray().Select(async s => { var result = await latency.MeasureAsync(s, mode, force, token); token.ThrowIfCancellationRequested(); if (mode != (LatencyIndex == 0 ? LatencyMode.Fast : LatencyMode.Exact)) return; if (mode == LatencyMode.Exact && !s.Supported) s.Latency = "—"; else s.Latency = result.Milliseconds is { } ms ? ms + " ms" : T("Недоступен", "Unavailable"); s.LatencyMs = result.Milliseconds; })); RefreshServers(); }
        catch (OperationCanceledException) { }
        finally { Scanning = false; }
    }
    private async Task RefreshSubAsync(Subscription sub) { var servers = await SubscriptionLoader.LoadAsync(sub.Source, lifetime.Token); ReplaceServers(sub, servers); Save(); }
    private void ReplaceServers(Subscription sub, List<Server> incoming)
    {
        var oldId = SelectedServer?.Id; var fav = Data.Servers.Where(s => s.Favorite).Select(s => s.Id).ToHashSet();
        foreach (var s in Data.Servers.Where(s => s.SubscriptionId == sub.Id).ToArray()) Data.Servers.Remove(s);
        foreach (var s in incoming) { s.SubscriptionId = sub.Id; s.Favorite = fav.Contains(s.Id); Data.Servers.Add(s); }
        sub.Updated = DateTimeOffset.Now; sub.ServerCount = incoming.Count; SelectedServer = Data.Servers.FirstOrDefault(s => s.Id == oldId) ?? Data.Servers.FirstOrDefault(s => s.Supported);
        RefreshServers(); Notify(nameof(NoSubscriptions)); Data.Subscriptions = new(Data.Subscriptions); Notify(nameof(Data));
    }
    public void RefreshServers()
    {
        IEnumerable<Server> servers = Data.Servers.Where(s => s.Name.Contains(ServerSearch, StringComparison.OrdinalIgnoreCase));
        servers = SortIndex == 0 ? servers.OrderBy(s => s.LatencyMs ?? long.MaxValue).ThenBy(s => s.Name) : servers.OrderBy(s => s.Name);
        var array = servers.ToArray(); ServerRows.Clear(); for (var i = 0; i < array.Length; i += ListMode ? 1 : 2) ServerRows.Add(new(array[i], !ListMode && i + 1 < array.Length ? array[i + 1] : null)); Notify(nameof(ServerCount)); Notify(nameof(NoServers));
    }
    private void RefreshRules() { VisibleRules.Clear(); foreach (var r in ActiveProfile.Rules.Where(r => r.Name.Contains(RuleSearch, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(RuleSearch, StringComparison.OrdinalIgnoreCase))) VisibleRules.Add(r); Notify(nameof(RuleCount)); Notify(nameof(NoRules)); }
    private void AttachProfile() { foreach (var r in ActiveProfile.Rules) { r.PropertyChanged -= OnRuleChanged; r.PropertyChanged += OnRuleChanged; } }
    private void DetachProfile(Profile p) { foreach (var r in p.Rules) r.PropertyChanged -= OnRuleChanged; }
    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RoutingRule.UseVpn)) { Save(); QueueApply(); } }
    private void RulesChanged() { AttachProfile(); RefreshRules(); Save(); QueueApply(); }
    public void Reorder(RoutingRule source, RoutingRule target) { var a = ActiveProfile.Rules.IndexOf(source); var b = ActiveProfile.Rules.IndexOf(target); if (a < 0 || b < 0 || a == b) return; ActiveProfile.Rules.Move(a, b); RulesChanged(); }
    private void QueueApply()
    {
        configurationVersion++;
        if (!Connected) return; applyDelay?.Cancel(); applyDelay?.Dispose(); applyDelay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); _ = ApplyLaterAsync(applyDelay.Token);
    }
    private async Task ApplyLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(650, token); if (!Connected) return; await DisconnectAsync(); await ConnectAsync(); }
        catch (OperationCanceledException) { } catch (Exception e) { Report(e); }
    }
    private async Task ToggleAsync() { if (Connected) await DisconnectAsync(); else await ConnectAsync(); }
    public async Task ConnectAsync()
    {
        if (!await connectionGate.WaitAsync(0)) return;
        try
        {
            if (Connected) return;
            if (SelectedServer is null) { Go("Subscriptions"); Banner = T("Добавьте подписку и выберите сервер.", "Add a subscription and choose a server."); return; }
            if (!SelectedServer.Supported) throw new UserError(SelectedServer.UnsupportedReason!);
            State = ConnectionState.Connecting; Banner = null; VpnIp = "—";
            spec = new(SelectedServer, ActiveProfile, Settings, LatencyService.FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
            spec = System.Text.Json.JsonSerializer.Deserialize<ConnectSpec>(System.Text.Json.JsonSerializer.Serialize(spec, Json.Options), Json.Options)!;
            var version = configurationVersion;
            store.SaveRuntime(ConfigGenerator.Generate(spec));
            await EnsureServiceAsync();
            var reply = await service.SendAsync(new("StartCore", spec), true, lifetime.Token);
            if (!reply.Ok) throw new UserError(reply.Message ?? reply.Status.Error ?? "Не удалось подключиться.");
            using (var http = LatencyService.ProbeClient(spec)) { using var probe = await http.GetAsync("https://www.gstatic.com/generate_204", lifetime.Token); probe.EnsureSuccessStatusCode(); }
            if (Settings.TunnelMode == TunnelMode.Proxy) SystemProxy.Enable();
            ApplyStatus(reply.Status); _ = MonitorAsync();
            try { VpnIp = await LatencyService.VpnIpAsync(spec, lifetime.Token); } catch { VpnIp = "—"; }
            if (version != configurationVersion) QueueApply();
        }
        catch
        {
            try { await service.SendAsync(new("StopCore")); } catch { } SystemProxy.Restore(); State = ConnectionState.Error; throw;
        }
        finally { connectionGate.Release(); }
    }
    private async Task EnsureServiceAsync()
    {
        await ServiceInstaller.EnsureInstalledAsync(() => Banner = T(
            "Подтвердите запрос Windows: установка службы TUN требуется один раз.",
            "Approve the Windows prompt: the TUN helper needs to be installed once."), lifetime.Token);
        Banner = null;
    }
    public async Task DisconnectAsync()
    {
        await connectionGate.WaitAsync();
        try { monitorId++; State = ConnectionState.Disconnecting; var reply = await service.SendAsync(new("StopCore"), ct: lifetime.Token); if (!reply.Ok) throw new UserError(reply.Message ?? "Не удалось остановить VPN."); ApplyStatus(reply.Status); VpnIp = "—"; Session = "—"; }
        catch { State = ConnectionState.Error; throw; }
        finally { SystemProxy.Restore(); connectionGate.Release(); }
    }
    private async Task MonitorAsync()
    {
        var id = ++monitorId;
        try { while (id == monitorId && Connected && !lifetime.IsCancellationRequested) { await Task.Delay(1000, lifetime.Token); if (id != monitorId || !Connected) break; var response = await service.SendAsync(new("GetStatus"), ct: lifetime.Token); if (id != monitorId) break; ApplyStatus(response.Status); if (started is { } time) Session = (DateTimeOffset.UtcNow - time).ToString(@"hh\:mm\:ss"); if (!Connected) SystemProxy.Restore(); } }
        catch (OperationCanceledException) { } catch (Exception e) { if (id == monitorId) { SystemProxy.Restore(); State = ConnectionState.Error; Report(e); } }
    }
    private void ApplyStatus(ServiceStatus s) { State = s.State; started = s.ConnectedAt; if (s.Error is not null) Banner = s.Error; }
    private void NotifyStatus() { foreach (var n in new[] { nameof(Connected), nameof(ConnectionBusy), nameof(ConnectLabel), nameof(StatusLabel), nameof(StatusDetail), nameof(FooterLabel), nameof(FooterMode) }) Notify(n); StatusChanged?.Invoke(); }
    public void Report(Exception e) { log.Write("Operation failed: " + e.GetType().Name); Banner = e is UserError ? e.Message : e is OperationCanceledException ? T("Время ожидания истекло.", "Operation timed out.") : T("Не удалось выполнить действие. Проверьте подключение и параметры.", "Action failed. Check your connection and settings."); }
    private void Save() { try { store.Save(Data); } catch (Exception e) { Report(e); } }
    private static void OpenFolder(string dir) { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = true }); }
    private void OnPower(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) ScheduleNetworkCheck(); }
    private void OnNetwork(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) OnPower(this, new(PowerModes.Resume)); }
    private void OnAddress(object? sender, EventArgs e) => ScheduleNetworkCheck();
    private void ScheduleNetworkCheck() => Application.Current.Dispatcher.InvokeAsync(async () =>
    {
        if (!Connected || recovering) return;
        networkDelay?.Cancel(); networkDelay?.Dispose(); networkDelay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = networkDelay.Token;
        try { await Task.Delay(1000, token); await RecoverAsync(); } catch (OperationCanceledException) { }
    });
    private async Task RecoverAsync()
    {
        if (recovering || !Connected) return; recovering = true;
        try
        {
            try { var reply = await service.SendAsync(new("GetStatus")); if (reply.Status.State != ConnectionState.Connected) throw new UserError("VPN needs reconnect."); if (spec is not null) { using var client = LatencyService.ProbeClient(spec); using var r = await client.GetAsync("https://www.gstatic.com/generate_204", lifetime.Token); r.EnsureSuccessStatusCode(); } }
            catch { try { await DisconnectAsync(); } catch { State = ConnectionState.Error; } await ConnectAsync(); }
        }
        catch (Exception e) { Report(e); }
        finally { recovering = false; }
    }
    public void Dispose() { Save(); lifetime.Cancel(); scan?.Cancel(); applyDelay?.Cancel(); networkDelay?.Cancel(); SystemEvents.PowerModeChanged -= OnPower; NetworkChange.NetworkAvailabilityChanged -= OnNetwork; NetworkChange.NetworkAddressChanged -= OnAddress; }
}
