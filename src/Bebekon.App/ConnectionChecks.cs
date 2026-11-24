namespace Bebekon.App;

// Runs only inside the explicit smoke harness with synthetic state and fake IPC.
internal static class ConnectionChecks
{
    internal static async Task RunAsync(string root)
    {
        var store = new StateStore(root); var state = new AppState();
        var subscription = new Subscription { Id = "fixture", Name = "Fixture" }; state.Subscriptions.Add(subscription);
        var node = VlessParser.Parse("vless://11111111-1111-1111-1111-111111111111@127.0.0.1:9?security=none&type=tcp#One");
        node.SubscriptionId = subscription.Id; state.Servers.Add(node); state.SelectedServerId = node.Id;
        store.Save(state);
        var service = new FakeService();
        TaskCompletionSource? blockedProbe = null; TaskCompletionSource? probeEntered = null;
        using var vm = new MainViewModel(store, service, async (_, token) =>
        {
            var pending = blockedProbe;
            if (pending is not null) { blockedProbe = null; probeEntered?.TrySetResult(); await pending.Task.WaitAsync(token); }
        });
        await vm.ConnectAsync(); Require(vm.Connected && service.Starts == 1, "Initial connection");
        Require(vm.UploadRate == 1.0.ToString("0.00") && vm.DownloadRate == 2.0.ToString("0.00"), "Real unit conversion and independent directions");
        Require(vm.UploadHistory.Count > 0 && vm.UploadHistory[^1] == 1 && vm.DownloadHistory[^1] == 2, "Measured samples feed the correct chart direction");

        var renamed = VlessParser.Parse("vless://11111111-1111-1111-1111-111111111111@127.0.0.1:9?type=tcp&security=none#Renamed");
        vm.ReplaceServers(subscription, [renamed]);
        await Task.Delay(800); Require(vm.SelectedServer?.Id == node.Id && service.Starts == 1, "Refresh must preserve selection without restarting");

        for (var i = 0; i < 3; i++) { vm.ActiveProfile.Rules.Add(new() { Name = $"Site {i}", Values = [$"site{i}.example"] }); vm.RulesChanged(); await Task.Delay(80); }
        await Until(() => service.Starts == 2 && vm.Connected);
        Require(service.Last?.Profile.Rules.Count == 3, "Coalesced rules use the latest snapshot");
        await Task.Delay(5200); Require(service.Starts == 2 && vm.Connected, "Routing remains stable beyond five seconds");

        // An old network probe must not restart the new session after a rule edit.
        blockedProbe = new(TaskCreationOptions.RunContinuationsAsynchronously); var staleProbe = blockedProbe;
        probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = vm.CheckRecoveryAsync(); await probeEntered.Task;
        vm.IsWholePc = true; await Until(() => service.Starts == 3 && vm.Connected);
        staleProbe.SetException(new IOException("Synthetic stale probe failure.")); await recovery;
        await Task.Delay(800); Require(service.Starts == 3 && vm.Connected, "Stale recovery must not interrupt the replacement session");

        // Manual off takes precedence over an already scheduled settings change.
        vm.IsWholePc = false; await vm.DisconnectAsync(); await Task.Delay(800);
        Require(service.Starts == 3 && !vm.Connected && vm.DownloadRate == "—", "Manual off cancels pending apply and resets counters");
        Require(vm.DownloadHistory.Count == 0 && vm.UploadHistory.Count == 0, "Disconnected traffic history is cleared");

        // Editing during startup must apply once, after the old snapshot completes.
        blockedProbe = new(TaskCreationOptions.RunContinuationsAsynchronously); var startupProbe = blockedProbe;
        probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = vm.ConnectAsync(); await probeEntered.Task;
        vm.ActiveProfile.Rules.Add(new() { Name = "During startup", Values = ["during-start.example"] }); vm.RulesChanged();
        startupProbe.SetResult(); await connecting; await Until(() => service.Starts == 5 && vm.Connected);
        Require(service.Last?.Profile.Rules.Count == 4, "Edits during startup cannot be lost");
        await vm.DisconnectAsync();

        // A click while connecting cancels the probe and cannot reconnect later.
        blockedProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        probeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connecting = vm.ConnectAsync(); await probeEntered.Task;
        await vm.DisconnectAsync(); await connecting; await Task.Delay(800);
        Require(!vm.Connected && !service.Running && service.Starts == 6, "Cancellation during connection remains off");
        Require(service.MaxConcurrentTransitions == 1, "Start/stop transitions are serialized");
        vm.SelectedServer!.Transport = "xhttp";
        await vm.ConnectAsync(); Require(vm.Connected && service.Last?.Server.Transport == "xhttp", "XHTTP reaches helper without premature sing-box-only generation");
        await vm.DisconnectAsync();
        vm.SelectedServer!.Transport = "tcp";
        await FolderAsync(vm, service);
        await StabilityAsync(vm, service, subscription);
    }
    /// <summary>A folder is only a wrapper: its rules reach the helper unchanged with their own routes.</summary>
    private static async Task FolderAsync(MainViewModel vm, FakeService service)
    {
        var vpn = new RoutingRule { Name = "Work VPN", Values = ["portal.work.example"], UseVpn = true };
        var direct = new RoutingRule { Name = "Work direct", Values = ["git.work.example"], UseVpn = false };
        vm.ActiveProfile.Rules.Add(vpn); vm.ActiveProfile.Rules.Add(direct); vm.RulesChanged();
        var folder = vm.CreateFolder("Работа", "Violet", "Briefcase", [vpn, direct]);
        await vm.ConnectAsync(); var starts = service.Starts;
        var sent = service.Last!.Profile.Rules;
        Require(sent.Single(r => r.Name == "Work VPN").Target == RouteTarget.Vpn && sent.Single(r => r.Name == "Work direct").Target == RouteTarget.Direct &&
            sent.All(r => r.Name is not ("Work VPN" or "Work direct") || r.FolderId == folder.Id), "Folder rules keep their own routes and reach the helper as they are");
        folder.Collapsed = true; vm.ToggleFolder.Execute(vm.VisibleRules.OfType<FolderRow>().Single()); await Task.Delay(900);
        Require(service.Starts == starts, "Collapsing a folder is visual and must not restart the tunnel");
        vm.ActiveProfile.Rules.Remove(vpn); vm.ActiveProfile.Rules.Remove(direct); vm.ActiveProfile.Folders.Remove(folder); vm.RulesChanged();
        await Until(() => service.Starts == starts + 1 && vm.Connected);
        await vm.DisconnectAsync();
    }
    /// <summary>Unattended drops: the user asked to stay connected, so the client must come back by itself.</summary>
    private static async Task StabilityAsync(MainViewModel vm, FakeService service, Subscription subscription)
    {
        await vm.ConnectAsync(); var starts = service.Starts; Require(vm.Connected, "Stability baseline");

        // Providers hand out a different front domain for the same node on each download.
        var rotated = VlessParser.Parse("vless://11111111-1111-1111-1111-111111111111@rotated.example:9?type=tcp&security=none#Renamed");
        vm.ReplaceServers(subscription, [rotated]); await Task.Delay(1200);
        Require(service.Starts == starts && vm.Connected && vm.SelectedServer?.Host == "rotated.example", "Rotated dial address keeps the working tunnel and is used next time");

        // A busy helper pipe is not a lost tunnel.
        service.FailStatus = 3; await Task.Delay(4500);
        Require(service.Starts == starts && vm.Connected, "Transient status failures do not disconnect");

        // The core exits on its own: reconnect without a click, with the refreshed address.
        service.Crash(); await Until(() => service.Starts == starts + 1 && vm.Connected, 15);
        Require(service.Last?.Server.Host == "rotated.example", "Reconnect uses the refreshed server");

        // A settings restart that fails must not leave the VPN off.
        service.FailStarts = 1; vm.IsWholePc = !vm.IsWholePc;
        await Until(() => service.Starts == starts + 3 && vm.Connected, 20);

        // A drop followed by a manual off stays off.
        service.FailStarts = 100; service.Crash(); await Until(() => vm.ReconnectPending, 5);
        await vm.DisconnectAsync(); var offStarts = service.Starts; await Task.Delay(3000);
        Require(!vm.Connected && !vm.ReconnectPending && service.Starts == offStarts, "Manual off cancels automatic reconnect");
        service.FailStarts = 0;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Connection regression: " + message); }
    private static async Task Until(Func<bool> condition, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class FakeService : IServiceClient
    {
        public int Starts, MaxConcurrentTransitions, FailStatus, FailStarts;
        private int transitions;
        public bool Running;
        private bool crashed;
        public ConnectSpec? Last;
        public void Crash() { Running = false; crashed = true; }
        public async Task<ServiceResponse> SendAsync(ServiceRequest request, bool startService = false, CancellationToken ct = default)
        {
            var transition = request.Operation is "StartCore" or "StopCore" or "RestartCore";
            if (transition) MaxConcurrentTransitions = Math.Max(MaxConcurrentTransitions, ++transitions);
            try
            {
                await Task.Delay(20, ct);
                if (request.Operation == "GetStatus" && FailStatus > 0) { FailStatus--; throw new TimeoutException("Synthetic busy helper pipe."); }
                if (request.Operation is "StartCore" or "RestartCore")
                {
                    if (request.Operation == "StartCore") Require(!Running, "No duplicate start");
                    Starts++; Last = request.Spec;
                    if (FailStarts > 0) { FailStarts--; Running = false; crashed = true; return new(false, new(ConnectionState.Error, "Synthetic start failure."), "Synthetic start failure."); }
                    Running = true; crashed = false;
                }
                if (request.Operation == "StopCore") { Running = false; crashed = false; }
                return new(true, new(Running ? ConnectionState.Connected : crashed ? ConnectionState.Error : ConnectionState.Disconnected,
                    ConnectedAt: Running ? DateTimeOffset.UtcNow : null, CorePid: Running ? 123 : null,
                    Traffic: Running ? new(125_000, 250_000, 2_000_000, 4_000_000, DateTimeOffset.UtcNow) : null));
            }
            finally { if (transition) transitions--; }
        }
    }
}
