using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Input;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    public IReadOnlyList<RouteChoice> RouteChoices => RouteChoice.Options.Where(option =>
        NetBirdEnabled || option.Target != RouteTarget.NetBird || DefaultDirection == RouteTarget.NetBird).ToArray();
    public bool NetBirdEnabled
    {
        get => Settings.NetBirdEnabled;
        set
        {
            if (value == NetBirdEnabled) return;
            Settings.EnabledIntegrations ??= [];
            Settings.EnabledIntegrations.RemoveAll(id => string.Equals(id, "netbird", StringComparison.OrdinalIgnoreCase));
            if (value) Settings.EnabledIntegrations.Add("netbird");
            netBirdConnected = false;
            netBirdInstalled = false;
            NetBirdSummary = "—";
            NetBirdPeer = null;
            NetBirdInterfaceInfo = "—";
            Notify(); Notify(nameof(RouteChoices)); Notify(nameof(NetBirdAction));
            Save(); QueueApply();
            if (liveEnvironment) RefreshConnections.Execute(null);
        }
    }
    private bool netBirdConnected, netBirdInstalled, netBirdConnecting, netBirdLeftOff;
    public bool NetBirdAutoConnect { get => Settings.NetBirdAutoConnect; set { if (Settings.NetBirdAutoConnect == value) return; Settings.NetBirdAutoConnect = value; Save(); Notify(); if (value) { netBirdLeftOff = false; _ = AutoConnectNetBirdAsync(); } } }
    /// <summary>
    /// Brings NetBird up through its service, without the NetBird tray app. When the session expired,
    /// the sign-in page opens in the default browser and the command waits for the user to finish.
    /// </summary>
    private async Task ConnectNetBirdAsync()
    {
        if (netBirdConnecting) return;
        netBirdConnecting = true; NetBirdSummary = T("Подключение", "Connecting");
        try
        {
            await NetBirdClient.ConnectAsync(url => Application.Current.Dispatcher.Invoke(() =>
            {
                Banner = T("Войдите в NetBird на открывшейся странице браузера. Окно NetBird открывать не нужно — Bebekon подключится сам.",
                    "Sign in to NetBird on the page opened in your browser. The NetBird window is not needed; Bebekon connects by itself.");
                try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
                catch (Win32Exception) { Banner = T("Откройте в браузере страницу входа NetBird: ", "Open the NetBird sign-in page: ") + url.AbsoluteUri; }
            }), lifetime.Token);
            Banner = T("NetBird подключён", "NetBird connected");
        }
        finally { netBirdConnecting = false; await ReadConnectionsAsync(lifetime.Token); }
    }
    /// <summary>At start and on connect: bring NetBird up unless the user turned it off in this session.</summary>
    private async Task AutoConnectNetBirdAsync()
    {
        if (!liveEnvironment || !Settings.NetBirdAutoConnect || !NetBirdEnabled || netBirdLeftOff || netBirdConnecting) return;
        try
        {
            var status = await NetBirdClient.GetStatusAsync(lifetime.Token);
            if (!status.Installed || status.Connected || status.State is "Unavailable" or "Connecting") return;
            await ConnectNetBirdAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UserError e) { Banner = e.Message; }
    }
    public string NetBirdAction => netBirdConnected ? T("Отключить NetBird", "Disconnect NetBird") : T("Подключить NetBird", "Connect NetBird");
    private string? netBirdPeer;
    public string? NetBirdPeer { get => netBirdPeer; private set => Set(ref netBirdPeer, value); }
    public RouteTarget DefaultDirection
    {
        get => ActiveProfile.DefaultRoute;
        set
        {
            if (value == ActiveProfile.DefaultRoute || !Enum.IsDefined(value)) return;
            ActiveProfile.DefaultRoute = value;
            Notify(nameof(DefaultDirection)); Notify(nameof(RouteChoices)); Notify(nameof(IsWholePc)); Notify(nameof(ModeLabel));
            Notify(nameof(EmptyRulesDetail)); Notify(nameof(StatusDetail));
            Save(); QueueApply();
        }
    }

    public bool PersistentProtection
    {
        get => Settings.PersistentKillSwitch;
        set
        {
            if (Settings.PersistentKillSwitch == value) return;
            Settings.PersistentKillSwitch = value;
            Notify();
            Save(); QueueApply();
        }
    }

    private string netBirdSummary = "—", netBirdInterfaceInfo = "—", windowsNetwork = "—", uplink = "—", wfpResult = "", wfpEvents = "";
    private string? netBirdInterface;
    public string NetBirdSummary { get => netBirdSummary; private set => Set(ref netBirdSummary, value); }
    public string NetBirdInterfaceInfo { get => netBirdInterfaceInfo; private set => Set(ref netBirdInterfaceInfo, value); }
    public string WindowsNetwork { get => windowsNetwork; private set => Set(ref windowsNetwork, value); }
    public string Uplink { get => uplink; private set => Set(ref uplink, value); }
    public string WfpResult { get => wfpResult; private set => Set(ref wfpResult, value); }
    public string WfpEvents { get => wfpEvents; private set => Set(ref wfpEvents, value); }
    public string ProtectionSummary => Protection switch
    {
        null => T("Обновите состояние, чтобы проверить защиту Windows.", "Refresh the status to check Windows protection."),
        { Error: not null } => T("Служба не смогла подтвердить защиту Windows.", "The helper could not verify Windows protection."),
        { Filters: > 0 } => T("Windows применяет правила блокировки соединений.", "Windows is enforcing connection filters."),
        _ => T("Системная блокировка сейчас выключена.", "System blocking is currently off.")
    };
    public int ProtectedRuleCount => ActiveProfile.ActiveRules.Count(rule => rule.SystemProtection);
    internal DestinationGuard ActiveDestinations => Protection is { DestinationGuardEnabled:true, GuardTargets: { } targets }
        ? DestinationGuard.Parse(targets) : DestinationGuard.Empty;
    public string DestinationDetails => Protection?.GuardTargets is { Length: > 0 } entries
        ? T($"Адресов: {entries.Length} · ", $"Targets: {entries.Length} · ") + (Protection.DestinationGuardEnabled ? T("защита включена","protection on") : T("защита выключена","protection off")) + Environment.NewLine + string.Join(", ",entries.Take(3)) + (entries.Length > 3 ? " …" : "")
        : T("Добавьте домены, IP или подсети, доступные только через VPN.", "Add domains, IPs or networks that should only be reachable through the VPN.");
    public ICommand EditDestinations { get; private set; } = null!;
    public ICommand ToggleDestinations { get; private set; } = null!;
    public string DestinationToggle => Protection?.DestinationGuardEnabled == true ? T("Выключить","Turn off") : T("Включить","Turn on");
    private async Task ChangeDestinationsAsync(bool edit)
    {
        await EnsureServiceAsync();
        var state = await service.SendAsync(new("GetStatus"), true, lifetime.Token);
        if (!state.Ok || state.Status.Protection?.GuardTargets is not { } prior)
            throw new UserError(T("Обновите службу Bebekon для защиты выбранных адресов.", "Update the Bebekon helper for destination protection."));
        var entries = edit ? Dialogs.Lines(T("Защита выбранных адресов","Protected destinations"),
            T("Домен с поддоменами, IP или CIDR — по одному в строке. Сохранение включает защиту. Домены используют DNS Windows; для собственного DNS приложения и уже известных реальных адресов добавьте IP/CIDR.",
              "Enter one domain (including subdomains), IP or CIDR per line. Saving enables protection. Domains use Windows DNS; add IPs for applications with private DNS or previously cached real addresses."),
            prior, lines => DestinationGuard.Parse(lines).Entries, pick: () => Dialogs.PickFromRules(keywords: false)) : prior;
        if (entries == null) return;
        var enable = entries.Length > 0 && (edit || !state.Status.Protection.DestinationGuardEnabled);
        await connectionGate.WaitAsync(lifetime.Token);
        try
        {
            var reply = await service.SendAsync(new("SetDestinationGuard", GuardTargets: entries, EnableGuard: enable), true, lifetime.Token);
            ApplyStatus(reply.Status);
            if (!reply.Ok) throw new UserError(reply.Message ?? T("Не удалось изменить защиту адресов.", "Could not apply destination protection."));
        }
        finally { connectionGate.Release(); }
    }
    public ICommand ConfigureDirectTraffic { get; private set; } = null!;
    public ICommand ConfigureObhod { get; private set; } = null!;
    public ICommand RefreshConnections { get; private set; } = null!;
    public ICommand ChangeNetBird { get; private set; } = null!;
    public ICommand CheckRoute { get; private set; } = null!;
    public ICommand RefreshProtection { get; private set; } = null!;
    public ICommand TestWfp { get; private set; } = null!;
    public ICommand ClearProtection { get; private set; } = null!;
    public ICommand ApplyStoredProtection { get; private set; } = null!;

    /// <summary>From the throttling check: a site blocked outright goes through VPN with one click.</summary>
    private void RouteSiteViaVpn(string host)
    {
        var rule = new RoutingRule { CreatedAt = DateTimeOffset.UtcNow, Name = host, Kind = RuleKind.Site, Values = [host], UseVpn = true };
        RuleValidation.Validate(rule);
        ActiveProfile.Rules.Insert(0, rule); RulesChanged();
    }
    private void InitializeRoutingCommands()
    {
        EditDestinations = Async(_ => ChangeDestinationsAsync(true), () => !ConnectionBusy);
        ToggleDestinations = Async(_ => ChangeDestinationsAsync(false), () => !ConnectionBusy && Protection?.GuardTargets is { Length: > 0 });
        ConfigureDirectTraffic = new Command(_ =>
        {
            var policy = Dialogs.DirectPolicyEditor(Settings.DirectPolicy, Settings.ObhodFolder, RouteSiteViaVpn);
            if (policy == null) return;
            Settings.DirectPolicy = policy; Save(); if (TlsFragmentEnabled) QueueApply();
        });
        ConfigureObhod = new Command(_ => Dialogs.ManageObhod(Settings, Save));
        RefreshConnections = Async(_ => ReadConnectionsAsync(lifetime.Token));
        ChangeNetBird = Async(async value =>
        {
            if (!NetBirdEnabled) return;
            if (netBirdConnected) { netBirdLeftOff = true; await NetBirdClient.SetConnectedAsync(false, lifetime.Token); await ReadConnectionsAsync(lifetime.Token); }
            else { netBirdLeftOff = false; await ConnectNetBirdAsync(); }
        }, () => NetBirdEnabled && netBirdInstalled);
        CheckRoute = new Command(_ => Dialogs.RouteCheck(this));
        RefreshProtection = Async(_ => ReadProtectionAsync(lifetime.Token));
        TestWfp = Async(async _ => WfpResult = await TestWfpAsync(lifetime.Token));
        ApplyStoredProtection = Async(async _ =>
        {
            await connectionGate.WaitAsync(lifetime.Token);
            try
            {
                if (Connected || ConnectionBusy) return;
                await EnsureServiceAsync();
                var netBird = NetBirdEnabled ? await NetBirdClient.GetStatusAsync(lifetime.Token) : null;
                var policy = new StoredProtectionSpec(ExpandedProfile, Settings.KillSwitch && Settings.PersistentKillSwitch, netBird?.PeerAddress, NetBirdEnabled);
                var reply = await service.SendAsync(new("ApplyStoredProtection", Protection: policy), true, lifetime.Token);
                Protection = reply.Status.Protection;
                if (!reply.Ok) throw new UserError(reply.Message ?? T("Не удалось применить защиту WFP.", "Could not apply WFP protection."));
            }
            finally { connectionGate.Release(); }
        }, () => !Connected && !ConnectionBusy && (ActiveProfile.HasProtectedRules || Settings.KillSwitch && Settings.PersistentKillSwitch));
        ClearProtection = Async(async _ =>
        {
            if (!Dialogs.Confirm(T("Снять текущую защиту WFP?", "Clear active WFP protection?"),
                T("Служба остановит Bebekon VPN и снимет блокировку. Сохранённая политика применяется при следующем подключении.",
                    "The helper will stop Bebekon VPN and clear the current block. The saved policy applies on the next connection."))) return;
            desiredConnected = false;
            applyDelay?.Cancel();
            connectionAttempt?.Cancel();
            await connectionGate.WaitAsync(lifetime.Token);
            try
            {
                var reply = await service.SendAsync(new("ClearProtection"), true, lifetime.Token);
                if (!reply.Ok) throw new UserError(reply.Message ?? T("Не удалось снять защиту.", "Could not clear protection."));
                ApplyStatus(reply.Status);
                if (testProbe is null) SystemProxy.Restore();
            }
            finally { connectionGate.Release(); }
        }, () => Protection is { Filters: > 0 } or { Error: not null });
    }

    private async Task ReadConnectionsAsync(CancellationToken ct)
    {
        var status = NetBirdEnabled ? await NetBirdClient.GetStatusAsync(ct) : null;
        if (NetBirdEnabled && status is not null) ShowNetBird(status);
        var routing = NetBirdEnabled && status is not null ? await NetBirdRouting.DiscoverAsync(status.PeerAddress, ct) : null;
        if (!NetBirdEnabled) routing = null;
        netBirdInterface = routing?.InterfaceName;
        NetBirdInterfaceInfo = routing is null ? T("Интерфейс NetBird не найден.", "NetBird interface was not found.") :
            string.Join(Environment.NewLine,
                routing.InterfaceName,
                T("DNS · ", "DNS · ") + string.Join(", ", routing.Zones.Select(zone => zone.Resolver).Append(routing.DefaultResolver).OfType<string>().Distinct().DefaultIfEmpty("—")),
                T("Сети · ", "Networks · ") + string.Join(", ", (status?.Networks ?? []).DefaultIfEmpty("—")));

        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback).ToArray();
        var v4 = WindowsRoutes.BestInterfaceIndex(IPAddress.Parse("192.0.2.1"));
        var v6 = WindowsRoutes.BestInterfaceIndex(IPAddress.Parse("2001:db8::1"));
        string Name(int? index) => index is null ? "—" :
            adapters.FirstOrDefault(adapter =>
                adapter.GetIPProperties().GetIPv4Properties()?.Index == index ||
                adapter.GetIPProperties().GetIPv6Properties()?.Index == index)?.Name ?? index.ToString()!;
        Uplink = string.Join(", ", adapters.Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            adapter.GetIPProperties().GatewayAddresses.Count > 0 &&
            !adapter.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
            !adapter.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)).Select(adapter => adapter.Name).DefaultIfEmpty("—"));
        WindowsNetwork = string.Join(Environment.NewLine, new[] { $"IPv4 · {Name(v4)}", $"IPv6 · {Name(v6)}" }
            .Concat(adapters.Where(adapter => adapter.Name == "Bebekon" || adapter.Name == routing?.InterfaceName)
                .Select(adapter => $"{adapter.Name} · DNS {string.Join(", ", adapter.GetIPProperties().DnsAddresses)}")));
    }

    private void ShowNetBird(NetBirdStatus status)
    {
        netBirdConnected = status.Connected;
        netBirdInstalled = status.Installed;
        NetBirdPeer = status.PeerAddress;
        Notify(nameof(NetBirdAction));
        NetBirdSummary = status.Connected ? T("Подключён", "Connected") :
            !status.Installed ? T("Не установлен", "Not installed") :
            status.State switch
            {
                "Idle" => T("Отключён", "Disconnected"),
                "Connecting" => T("Подключение", "Connecting"),
                "NeedsLogin" => T("Требуется вход", "Sign-in required"),
                "SessionExpired" => T("Сеанс истёк", "Session expired"),
                "LoginFailed" => T("Ошибка входа", "Sign-in failed"),
                _ => T("Состояние недоступно", "Status unavailable")
            };
    }

    internal string ExpectedInterface(RouteTarget target) => target switch
    {
        RouteTarget.Vpn => Settings.TunnelMode == TunnelMode.Tun ? "Bebekon (TUN)" : "Bebekon (Proxy)",
        RouteTarget.NetBird => netBirdInterface ?? T("Интерфейс NetBird", "NetBird interface"),
        RouteTarget.Direct => Uplink,
        _ => "—"
    };

    private async Task ReadProtectionAsync(CancellationToken ct)
    {
        if (testProbe is null && !ServiceInstaller.IsInstalled) return;
        var response = await service.SendAsync(new("ProtectionEvents"), true, ct);
        Protection = response.Status.Protection;
        WfpEvents = !response.Ok ? T("Журнал WFP недоступен.", "The WFP event log is unavailable.") :
            response.BlockEvents is not { Length: > 0 } ? T("За последние 10 минут нет событий активных фильтров.", "No events from active filters in the last 10 minutes.") :
            string.Join(Environment.NewLine, response.BlockEvents.Select(entry =>
            {
                var protocol = entry.Protocol switch { 6 => "TCP", 17 => "UDP", { } number => number.ToString(), _ => "" };
                var destination = entry.Address ?? "—";
                if (entry.Address is { } ip && entry.Port is { } port)
                    destination = ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";
                return $"{entry.Timestamp.ToLocalTime():HH:mm:ss} · {protocol} {destination} · #{entry.FilterId} · {entry.Reason}";
            }));
    }

    internal Task<string> TestWfpAsync(CancellationToken ct) => TestWfpAsync(null, ct);

    internal async Task<string> TestRuleWfpAsync(RouteQuery query, string? expectedRule, CancellationToken ct)
    {
        var profile = Connected && spec is { } active ? active.Profile : ExpandedProfile;
        var decision = RoutingPolicy.Analyze(profile, query, ActiveDestinations);
        if (decision.ProtectedDestination && expectedRule == null)
            return await TestWfpAsync(null, ct, destination: true);
        var rule = profile.ActiveRules.FirstOrDefault(item => item.Id == decision.Rule?.Id);
        if (rule is null || !rule.SystemProtection || expectedRule is not null && rule.Id != expectedRule)
            return T("Не проверено · для этой цели не найдено соответствующее защищённое правило в применённой политике.",
                     "Unverified · no matching protected rule was found in the applied policy.");
        return string.Join(Environment.NewLine, rule.Name,
            T("Имитация потери разрешённого пути на изолированном loopback-получателе.", "Loss of the allowed path is simulated against an isolated loopback receiver."),
            await TestWfpAsync(rule, ct));
    }

    private async Task<string> TestWfpAsync(RoutingRule? rule, CancellationToken ct, bool destination = false)
    {
        if (testProbe is null && !ServiceInstaller.IsInstalled)
            return T("Для проверки нужна установленная служба Bebekon VPN.", "The installed Bebekon helper is needed for the test.");
        var response = await service.SendAsync(new(destination ? "TestDestinationGuard" : rule is null ? "TestProtection" : "TestRuleProtection", TestRule: rule), true, ct);
        if (!response.Ok || response.WfpTest is not { Length: > 0 }) return response.Message ?? T("Служба не вернула результат WFP.", "The helper did not return a WFP result.");
        Protection = response.Status.Protection;
        return string.Join(Environment.NewLine, response.WfpTest.Select(check =>
        {
            var outcome = check.Outcome switch
            {
                WfpTestOutcome.Passed => T("Пройден", "Passed"),
                WfpTestOutcome.Failed => T("Не пройден", "Failed"),
                _ => T("Не проверено", "Unverified")
            };
            return $"{check.AddressFamily} {check.Transport.ToString().ToUpperInvariant()} · {outcome}" +
                (check.FilterId is { } id ? $" · WFP #{id}" : "") + (check.Detail is { } detail ? $" · {detail}" : "");
        }));
    }

    internal async Task<string> VerifyRouteAsync(RouteQuery query, CancellationToken ct)
    {
        var current = spec;
        if (testProbe is not null || query.Transport == RuleTransport.Udp || query.Domain is null && query.Address is null)
            return T("Не проверено для этих параметров.", "Unverified for these parameters.");
        var profile = Connected && current is not null ? current.Profile : ExpandedProfile;
        var target = query with { Port = query.Port ?? 443, Transport = RuleTransport.Tcp };
        var decision = RoutingPolicy.Analyze(profile, target, ActiveDestinations);
        if (decision.Direction == RouteTarget.NetBird && !NetBirdEnabled)
            return T("Интеграция NetBird выключена. Политика блокирует этот маршрут.", "NetBird integration is off. The policy blocks this route.");
        if (decision.Direction == RouteTarget.NetBird && !decision.DependsOnConnectionDetails)
            return await VerifyNetBirdAsync(target, ct);
        if (!Connected || current is null || decision.Direction != RouteTarget.Vpn || decision.Rule?.FallbackServerIds is { Count: > 0 } ||
            decision.Rule?.ServerId is { } pinned && pinned != current.Server.Id)
            return T("Для действующего маршрута нет подходящей изолированной пробы.", "The active route has no suitable isolated probe.");

        using var http = LatencyService.ProbeClient(current);
        var uri = new UriBuilder("https", query.Domain ?? query.Address!.ToString(), query.Port ?? 443).Uri;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!ReferenceEquals(current, spec) || !Connected) return T("Во время проверки политика изменилась.", "The policy changed during the check.");
        string publicIp;
        try { publicIp = await LatencyService.VpnIpAsync(current, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { publicIp = "—"; }
        return T($"Проба через Bebekon VPN · HTTP {(int)response.StatusCode} · публичный IP {publicIp}",
                 $"Bebekon VPN probe · HTTP {(int)response.StatusCode} · public IP {publicIp}") + Environment.NewLine +
               T("Фактический путь DNS · Не проверено", "Actual DNS path · Unverified");
    }

    private async Task<string> VerifyNetBirdAsync(RouteQuery target, CancellationToken ct)
    {
        var status = await NetBirdClient.GetStatusAsync(ct);
        var routing = await NetBirdRouting.DiscoverAsync(status.PeerAddress, ct);
        if (routing is null)
            return T("Windows не подтвердила маршрут через интерфейс NetBird.", "Windows did not verify a route through the NetBird interface.");
        var result = await NetBirdProbe.VerifyAsync(routing, target, ct);
        var reachability = result.Reached is { } address
            ? T($"TCP {address} · порт {result.Port} · интерфейс {result.InterfaceName}",
                $"TCP {address} · port {result.Port} · interface {result.InterfaceName}")
            : T("TCP через NetBird · соединение не подтверждено", "TCP via NetBird · connection unverified");
        if (target.Domain is null) return reachability;
        return reachability + Environment.NewLine + (result.Resolver is { } dns
            ? T($"DNS · ответ {dns} через {result.InterfaceName}", $"DNS · reply from {dns} via {result.InterfaceName}")
            : T("Фактический путь DNS · Не проверено", "Actual DNS path · Unverified"));
    }
}
