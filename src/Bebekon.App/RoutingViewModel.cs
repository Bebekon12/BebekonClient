using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Input;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    public IReadOnlyList<RouteChoice> RouteChoices => RouteChoice.Options;
    public RouteTarget DefaultDirection
    {
        get => ActiveProfile.DefaultRoute;
        set
        {
            if (value == ActiveProfile.DefaultRoute || !Enum.IsDefined(value)) return;
            ActiveProfile.DefaultRoute = value;
            Notify(nameof(DefaultDirection)); Notify(nameof(IsWholePc)); Notify(nameof(ModeLabel));
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

    private string netBirdSummary = "—", netBirdInterfaceInfo = "—", windowsNetwork = "—", uplink = "—", wfpResult = "";
    private string? netBirdInterface;
    public string NetBirdSummary { get => netBirdSummary; private set => Set(ref netBirdSummary, value); }
    public string NetBirdInterfaceInfo { get => netBirdInterfaceInfo; private set => Set(ref netBirdInterfaceInfo, value); }
    public string WindowsNetwork { get => windowsNetwork; private set => Set(ref windowsNetwork, value); }
    public string Uplink { get => uplink; private set => Set(ref uplink, value); }
    public string WfpResult { get => wfpResult; private set => Set(ref wfpResult, value); }
    public int ProtectedRuleCount => ActiveProfile.ActiveRules.Count(rule => rule.SystemProtection);
    public ICommand RefreshConnections { get; private set; } = null!;
    public ICommand ChangeNetBird { get; private set; } = null!;
    public ICommand CheckRoute { get; private set; } = null!;
    public ICommand RefreshProtection { get; private set; } = null!;
    public ICommand TestWfp { get; private set; } = null!;
    public ICommand ClearProtection { get; private set; } = null!;

    private void InitializeRoutingCommands()
    {
        RefreshConnections = Async(_ => ReadConnectionsAsync(lifetime.Token));
        ChangeNetBird = Async(async value =>
        {
            await NetBirdClient.SetConnectedAsync(string.Equals(value as string, "Up", StringComparison.Ordinal), lifetime.Token);
            await ReadConnectionsAsync(lifetime.Token);
        });
        CheckRoute = new Command(_ => Dialogs.RouteCheck(this));
        RefreshProtection = Async(_ => ReadProtectionAsync(lifetime.Token));
        TestWfp = Async(async _ => WfpResult = await TestWfpAsync(lifetime.Token));
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
        });
    }

    private async Task ReadConnectionsAsync(CancellationToken ct)
    {
        var status = await NetBirdClient.GetStatusAsync(ct);
        ShowNetBird(status);
        var routing = await NetBirdRouting.DiscoverAsync(status.PeerAddress, ct);
        netBirdInterface = routing?.InterfaceName;
        NetBirdInterfaceInfo = routing is null ? T("Интерфейс NetBird не найден.", "NetBird interface was not found.") :
            string.Join(Environment.NewLine,
                routing.InterfaceName,
                T("DNS · ", "DNS · ") + string.Join(", ", routing.Zones.Select(zone => zone.Resolver).Append(routing.DefaultResolver).OfType<string>().Distinct().DefaultIfEmpty("—")),
                T("Сети · ", "Networks · ") + string.Join(", ", status.Networks.DefaultIfEmpty("—")));

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
        NetBirdSummary = status.Connected ? T("Подключён", "Connected") + " · " + status.PeerAddress :
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
        var response = await service.SendAsync(new("GetStatus"), true, ct);
        if (response.Ok) Protection = response.Status.Protection;
    }

    internal async Task<string> TestWfpAsync(CancellationToken ct)
    {
        if (testProbe is null && !ServiceInstaller.IsInstalled)
            return T("Для проверки нужна установленная служба Bebekon VPN.", "The installed Bebekon helper is needed for the test.");
        var response = await service.SendAsync(new("TestProtection"), true, ct);
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
        if (!Connected || current is null || testProbe is not null || query.Executable is not null || query.Transport == RuleTransport.Udp)
            return T("Не проверено для этих параметров.", "Unverified for these parameters.");
        var decision = RoutingPolicy.Analyze(current.Profile, query);
        if (decision.Direction == RouteTarget.NetBird && !decision.DependsOnConnectionDetails && query.Address is { } address)
            return await VerifyNetBirdAddressAsync(current, address, query.Port ?? 443, ct);
        if (decision.Direction != RouteTarget.Vpn || decision.Rule?.FallbackServerIds is { Count: > 0 } ||
            decision.Rule?.ServerId is { } pinned && pinned != current.Server.Id ||
            query.Domain is null && query.Address is null)
            return T("Для действующего маршрута нет подходящей изолированной пробы.", "The active route has no suitable isolated probe.");

        using var http = LatencyService.ProbeClient(current);
        var uri = new UriBuilder("https", query.Domain ?? query.Address!.ToString(), query.Port ?? 443).Uri;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!ReferenceEquals(current, spec) || !Connected) return T("Во время проверки политика изменилась.", "The policy changed during the check.");
        string publicIp;
        try { publicIp = await LatencyService.VpnIpAsync(current, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { publicIp = "—"; }
        return T($"Проба через Bebekon VPN · HTTP {(int)response.StatusCode} · публичный IP {publicIp}",
                 $"Bebekon VPN probe · HTTP {(int)response.StatusCode} · public IP {publicIp}");
    }

    private async Task<string> VerifyNetBirdAddressAsync(ConnectSpec current, IPAddress address, int port, CancellationToken ct)
    {
        var routing = await NetBirdRouting.DiscoverAsync(current.NetBirdPeer, ct);
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        var index = v4 ? routing?.IPv4Index : routing?.IPv6Index;
        if (index is null || WindowsRoutes.BestInterfaceIndex(address) != index)
            return T("Windows не подтвердила маршрут через интерфейс NetBird.", "Windows did not verify a route through the NetBird interface.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        const SocketOptionName unicastInterface = (SocketOptionName)31; // IP_UNICAST_IF / IPV6_UNICAST_IF
        socket.SetSocketOption(v4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, unicastInterface,
            v4 ? IPAddress.HostToNetworkOrder(index.Value) : index.Value);
        await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
        return T($"TCP до {address} через {routing!.InterfaceName}", $"TCP to {address} through {routing!.InterfaceName}");
    }
}
