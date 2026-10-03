using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Bebekon.App;
/// <summary>Explicit developer-only render harness; synthetic fixtures never enter the user's settings.</summary>
internal static class SmokeHarness
{
    private static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "Bebekon.sln"))) return Path.Combine(dir.FullName, "artifacts", "ui-smoke");
        return Path.Combine(Path.GetTempPath(), "Bebekon-QA", "ui-smoke");
    }
    public static MainViewModel CreateViewModel()
    {
        Directory.CreateDirectory(Root);
        var store = new StateStore(Path.Combine(Root, "test-user"));
        var data = new AppState();
        foreach (var name in new[] { "🇱🇻 Латвия", "DE Германия", "EU Hysteria", "LT Литва", "LT Литва — YouTube", "LV Латвия #2", "NL Нидерланды", "NL Нидерланды #2", "SE Швеция" }) data.Servers.Add(VlessParser.Parse("vless://" + Guid.NewGuid() + "@127.0.0.1:9?security=none&type=tcp#" + Uri.EscapeDataString(name)));
        data.Subscriptions.Add(new() { Name = "UI test fixture", Source = "https://example.invalid/private-token", ServerCount = data.Servers.Count });
        foreach (var server in data.Servers) server.SubscriptionId = data.Subscriptions[0].Id;
        data.SelectedServerId = data.Servers[0].Id;
        data.Profiles[0].Rules.Add(new() { Name = "OpenAI / ChatGPT", Values = ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Claude / Anthropic", Values = ["claude.ai", "anthropic.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Telegram", Kind = RuleKind.Application, Values = ["Telegram.exe"] });
        data.Settings.TunnelMode = TunnelMode.Proxy;
        data.Settings.AccentColor = "Cyan"; data.Settings.DesignVersion = 0;
        store.Save(data);
        var vm = new MainViewModel(store);
        if (vm.AccentColor != "Blue" || store.Load().Settings.DesignVersion != 1) throw new InvalidOperationException("Existing settings must adopt the Night Track theme once.");
        vm.AccentColor = "Violet"; vm.Dispose(); vm = new MainViewModel(store);
        if (vm.AccentColor != "Violet") throw new InvalidOperationException("A later custom accent must survive restart.");
        vm.AccentColor = "Blue";
        if (vm.Settings.TunnelMode != TunnelMode.Tun) throw new InvalidOperationException("Startup must select TUN even after a prior proxy session.");
        if (vm.LatencyIndex != 1) throw new InvalidOperationException("Exact VPN latency must be the default.");
        foreach (var server in vm.Data.Servers) if (CountryInfo.Resolve(server.Name) is not { } code || FlagView.GetImage(code) is null) throw new InvalidOperationException("Every known fixture country must have a bundled flag.");
        return vm;
    }
    public static async Task RunAsync(MainWindow window, MainViewModel vm)
    {
        await Task.Delay(400); var report = new List<string> { "Startup selects TUN after a saved proxy session." };
        CheckTrayMenu(window, vm, report);
        await CheckCaptionButtonsAsync(window, vm, report);
        await ConnectionChecks.RunAsync(Path.Combine(Root, "connection-regression"));
        report.Add("Connection regressions: stable refreshed selection; coalesced rule edits; stable beyond 5 seconds; stale recovery ignored; manual off/cancel wins; edits during startup applied; traffic directions and units.");
        vm.Go("Servers"); vm.LatencyIndex = 0; vm.LatencyIndex = 1; vm.LatencyIndex = 0; vm.CancelPing.Execute(null); vm.Go("Home");
        await Task.Delay(400);
        if (vm.Scanning) throw new InvalidOperationException("Rapid mode changes and navigation must cancel pending scans.");
        vm.LatencyIndex = 1;
        foreach (var (node, ms) in vm.Data.Servers.Zip(new long?[] { 25, 125, 310, 68, 91, 165, 240, null, 80 })) { node.LatencyMs = ms; node.Latency = ms is null ? "—" : ms + " ms"; }
        report.Add("Bundled flags, exact default and rapid scan mode/navigation cancellation passed.");
        foreach (var page in new[] { "Home", "Servers", "Rules", "Subscriptions", "Settings" })
        {
            vm.Go(page); await vm.StopScansAsync(); await Task.Delay(300); window.UpdateLayout();
            foreach (var (node, ms) in vm.Data.Servers.Zip(new long?[] { 25, 125, 310, 68, 91, 165, 240, null, 80 })) { node.LatencyMs = ms; node.Latency = ms is null ? "—" : ms + " ms"; }
            window.UpdateLayout();
            CheckGeometry(window, page);
            if (page == "Servers" && Descendants(window).OfType<FlagView>().Count(flag => flag.IsVisible) != vm.Data.Servers.Count) throw new InvalidOperationException("Odd server lists must not render an empty card.");
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
            {
                var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(Root, page + "-" + (int)(scale * 100) + ".png")); encoder.Save(output);
            }
            report.Add(page + " rendered at 100/125/150/175%.");
        }
        vm.Go("Home"); window.UpdateLayout();
        var home = Descendants(window).OfType<HomeView>().Single();
        var homeServers = (ServersView)home.FindName("HomeServers");
        if (!homeServers.Embedded || Descendants(homeServers).OfType<ListBox>().Single().Items.Count != vm.ServerRows.Count)
            throw new InvalidOperationException("Home must contain all subscription server rows.");
        var scrollHome = Descendants(home).OfType<ScrollViewer>().First(); scrollHome.ScrollToBottom(); await Task.Delay(200); window.UpdateLayout();
        Capture(window, "Home-Servers");
        scrollHome.ScrollToTop(); window.UpdateLayout();
        ((Button)home.FindName("OpenServersCard")).Command.Execute("Servers");
        if (vm.PageName != "Servers") throw new InvalidOperationException("Home server card must navigate.");
        await vm.StopScansAsync(); vm.Go("Home"); window.UpdateLayout(); home = Descendants(window).OfType<HomeView>().Single();
        Dialogs.RenderObserver = dialog => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            var target = Descendants(dialog).OfType<Button>().Single(b => Equals(b.Tag, "EntirePC"));
            target.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        try { ((Button)home.FindName("ModeCard")).Command.Execute(null); }
        finally { Dialogs.RenderObserver = null; }
        if (!vm.IsWholePc) throw new InvalidOperationException("Home mode card must change routing.");
        vm.IsWholePc = false;
        vm.LatencyIndex = 0; ((Button)home.FindName("HomePing")).Command.Execute(null);
        using (var pingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
            while (vm.SelectedServer?.LatencyMs != -1) await Task.Delay(30, pingTimeout.Token);
        vm.LatencyIndex = 1;
        report.Add("Home: scrollable all-subscription server list and controls; server navigation; routing-mode dialog; clickable ping and red unavailable state.");
        await CheckHomeStatesAsync(window, vm, report);
        vm.Go("Settings"); await Task.Delay(200); window.UpdateLayout();
        var settings = Descendants(window).OfType<SettingsView>().Single();
        var settingsSearch = (TextBox)settings.FindName("SettingsSearch");
        settingsSearch.Text = "MTU"; window.UpdateLayout();
        if (Descendants(settings).OfType<SettingRow>().Count(r => r.Visibility == Visibility.Visible) != 1) throw new InvalidOperationException("Settings search must find MTU across categories.");
        settingsSearch.Text = "no-setting-has-this-name";
        if (Descendants(settings).OfType<SettingRow>().Any(r => r.Visibility == Visibility.Visible)) throw new InvalidOperationException("Settings empty search result is inconsistent.");
        settingsSearch.Clear();
        var settingsScroll = (ScrollViewer)settings.FindName("SettingsScroll");
        settingsScroll.ScrollToEnd(); window.UpdateLayout();
        if (settingsScroll.ScrollableHeight > 0 && settingsScroll.VerticalOffset <= 0) throw new InvalidOperationException("Settings scrolling must reach the lower rows.");
        settingsScroll.ScrollToTop(); window.UpdateLayout();
        foreach (var category in new[] { "Network", "Appearance", "Behavior", "Advanced", "Updates", "All" })
        {
            Descendants(settings).OfType<RadioButton>().Single(r => r.Tag as string == category).IsChecked = true;
            await Task.Delay(400); window.UpdateLayout(); Capture(window, "Settings-" + category);
            if (!Descendants(settings).OfType<CheckBox>().Any(Motion.GetSlidingSwitch)) throw new InvalidOperationException("Settings switch style must enable sliding motion.");
            foreach (var toggle in Descendants(settings).OfType<CheckBox>().Where(Motion.GetSlidingSwitch))
                if (toggle.Template.FindName("Thumb", toggle) is not FrameworkElement thumb || Canvas.GetLeft(thumb) != (toggle.IsChecked == true ? 23 : 3)) throw new InvalidOperationException("Initial switch thumb must reflect its checked state.");
        }
        var originalAccent = vm.AccentColor;
        Descendants(settings).OfType<RadioButton>().Single(r => r.Tag as string == "Updates").IsChecked = true;
        // Before publication the bundled GitHub URL still points to the previous release.
        // Verify the exact signed candidate built beside this workspace instead.
        var candidateFeed = Path.GetFullPath(Path.Combine(Root, "..", "..", "dist", "update.json"));
        if (File.Exists(candidateFeed))
        {
            var savedSource = vm.Settings.UpdateSource; vm.Settings.UpdateSource = candidateFeed;
            await vm.CheckUpdatesAsync(false);
            vm.Settings.UpdateSource = savedSource;
            if (!vm.UpdateStatus.Contains(vm.AppVersion) || vm.UpdateVisibility != Visibility.Collapsed) throw new InvalidOperationException("Published signed feed must report the current build as up to date.");
        }
        await CheckUpdateOverlayAsync(window, vm, report);
        foreach (var accent in new[] { "Blue", "Emerald", "Violet", "Cyan" }) { vm.AccentColor = accent; if (Application.Current.Resources["Accent"] is not SolidColorBrush) throw new InvalidOperationException("Accent must apply to live resources."); }
        vm.AccentColor = originalAccent;
        vm.PureBlack = true; if (((SolidColorBrush)Application.Current.Resources["Background"]).Color != Colors.Black) throw new InvalidOperationException("OLED background was not applied."); vm.PureBlack = false;
        vm.AnimationsEnabled = false; vm.IsProxyMode = true; window.UpdateLayout();
        foreach (var toggle in Descendants(settings).OfType<CheckBox>().Where(Motion.GetSlidingSwitch)) if (Motion.GetSwitchOffset(toggle) != (toggle.IsChecked == true ? 20 : 0) || toggle.HasAnimatedProperties) throw new InvalidOperationException("Reduced motion switches must move immediately with no animation.");
        vm.IsTunMode = true; vm.AnimationsEnabled = true;
        await Task.Delay(250);
        foreach (var toggle in Descendants(settings).OfType<CheckBox>().Where(Motion.GetSlidingSwitch))
        {
            if (toggle.Template.FindName("Thumb", toggle) is not FrameworkElement thumb || Canvas.GetLeft(thumb) != (toggle.IsChecked == true ? 23 : 3)) throw new InvalidOperationException("Switch thumb must reflect its actual checked state.");
        }
        report.Add("Live accent palettes, OLED and reduced-motion switch behavior passed.");
        vm.SidebarCollapsed = true; await Task.Delay(300);
        if (Math.Abs(((Border)window.FindName("SidebarHost")).ActualWidth - 76) > .1) throw new InvalidOperationException("Sidebar must settle at its collapsed width.");
        vm.SidebarCollapsed = false; await Task.Delay(300);
        if (Math.Abs(((Border)window.FindName("SidebarHost")).ActualWidth - 200) > .1) throw new InvalidOperationException("Sidebar must restore its expanded width.");
        if (vm.ImportQuickSubscription.CanExecute(null)) throw new InvalidOperationException("Empty quick subscription must be disabled.");
        var originalSubscriptions = vm.Data.Subscriptions.Count; var originalServers = vm.Data.Servers.Count;
        vm.QuickSource = $"vless://{Guid.NewGuid()}@127.0.0.1:9?security=none&type=tcp#Quick-import-fixture";
        vm.ImportQuickSubscription.Execute(null);
        var importTimeout = Stopwatch.StartNew(); while (vm.Importing && importTimeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        if (vm.Importing || vm.Data.Subscriptions.Count != originalSubscriptions + 1 || vm.Data.Servers.Count != originalServers + 1 || vm.QuickSource != "" || vm.PageName != "Servers") throw new InvalidOperationException("Quick import must add the subscription, clear the field and show servers.");
        vm.Go("Settings");
        report.Add("Animated sidebar final widths and inline subscription import passed.");
        vm.IsProxyMode = true; if (vm.IsTunMode || !vm.IsProxyMode) throw new InvalidOperationException("TUN and Proxy cannot be active together.");
        vm.IsTunMode = true; if (!vm.IsTunMode || vm.IsProxyMode) throw new InvalidOperationException("TUN must switch Proxy off.");
        report.Add("Settings categories, cross-category search, empty results and TUN/Proxy exclusivity passed.");
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
        foreach (var page in new[] { "Home", "Servers", "Rules", "Subscriptions", "Settings" }) { vm.Go(page); await vm.StopScansAsync(); await Task.Delay(150); window.UpdateLayout(); if (page == "Rules") CheckRulesViewport(window); Capture(window, page + "-Minimum"); }
        window.Width = 1000; window.Height = 740; window.UpdateLayout();
        vm.Go("Settings"); await Task.Delay(150); I18n.Set("English"); window.UpdateLayout(); Capture(window, "Settings-English"); I18n.Set("Русский");
        string dialogName = "Subscription"; Exception? renderFailure = null;
        Dialogs.RenderObserver = dialog => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try {
                dialog.UpdateLayout();
                if (dialogName == "Rule") CheckRuleEditor(dialog, vm);
                if (window.Content is not UIElement { Effect: System.Windows.Media.Effects.BlurEffect { Radius: 8 } }) throw new InvalidOperationException("Modal background must be blurred.");
                Capture(dialog, "Dialog-" + dialogName); Capture(window, "Backdrop-" + dialogName);
            }
            catch (Exception error) { renderFailure = error; }
            finally { dialog.Close(); }
        }));
        try
        {
            Dialogs.Subscription(null); dialogName = "Rule"; Dialogs.Rule(null, vm.Data.Servers);
            dialogName = "Preset"; Dialogs.Preset(vm.Presets);
            dialogName = "Application"; await Dialogs.ApplicationAsync();
            dialogName = "Confirm"; Dialogs.Confirm("Удалить правило?", "UI test fixture");
        }
        finally { Dialogs.RenderObserver = null; }
        if (renderFailure is not null) throw renderFailure;
        if (((UIElement)window.Content).Effect is not null || Dialogs.ModalOpen) throw new InvalidOperationException("Closing dialogs must restore the background.");
        report.Add("Rule editor: all six types, optional name, actions and server references; full button areas; modal blur/restoration passed.");
        report.Add("Five native dialogs rendered with the shared theme.");
        var originalProfile = vm.ActiveProfile;
        var adminProfile = new Profile { Name = "Правила админа · UI fixture" }; vm.Data.Profiles.Add(adminProfile); vm.ActiveProfile = adminProfile;
        Dialogs.RenderObserver = dialog => dialog.Dispatcher.BeginInvoke(new Action(() => Descendants(dialog).OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        try { vm.AddPreset.Execute(null); vm.AddPreset.Execute(null); }
        finally { Dialogs.RenderObserver = null; }
        if (adminProfile.Rules.Count != 42 || adminProfile.Rules.Count(r => !r.UseVpn) != 6) throw new InvalidOperationException("Admin preset command must add 42 rules exactly once.");
        vm.Go("Rules"); await Task.Delay(250); window.UpdateLayout(); Capture(window, "Rules-Admin");
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout(); Capture(window, "Rules-Admin-Minimum"); CheckRulesViewport(window);
        window.Width = 1000; window.Height = 740; window.UpdateLayout();
        var firstRule = adminProfile.Rules[0];
        Dialogs.RenderObserver = dialog => dialog.Dispatcher.BeginInvoke(new Action(() => {
            var editor = Descendants(dialog).OfType<RuleEditor>().Single(); editor.NameField.Text = "Edited fixture";
            Descendants(dialog).OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        try { vm.EditRule.Execute(firstRule); } finally { Dialogs.RenderObserver = null; }
        if (adminProfile.Rules[0].Id != firstRule.Id || adminProfile.Rules[0].Name != "Edited fixture" || adminProfile.Rules[0].UseVpn) throw new InvalidOperationException("Saving an edit must preserve the rule ID and Direct action.");
        vm.ActiveProfile = originalProfile; vm.Data.Profiles.Remove(adminProfile);
        report.Add("Admin preset Add command applied twice without duplicates (42 rules / 6 Direct); modal Save edited the same ID and kept its action.");
        vm.Go("Rules"); for (var i = 0; i < 500; i++) vm.ActiveProfile.Rules.Add(new() { Name = "Rule " + i, Values = [$"site{i}.example.com"] }); vm.RuleSearch = ""; vm.RuleSearch = "Rule"; await Task.Delay(200); window.UpdateLayout();
        vm.IsWholePc = true; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Routing mode selection is inconsistent.");
        vm.IsWholePc = false; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Selective mode selection is inconsistent.");
        report.Add("Mode switches: one selection in Entire PC and By rules.");
        var boxes = Descendants(window).OfType<ListBox>().ToArray(); var realized = boxes.Sum(b => Descendants(b).OfType<ListBoxItem>().Count()); report.Add($"503 rules, realized list containers: {realized} (virtualization).");
        vm.Go("Home"); await Task.Delay(1000); using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.TotalProcessorTime; await Task.Delay(3000); process.Refresh(); report.Add($"After rendering (not an idle benchmark): Working Set {process.WorkingSet64 / 1048576.0:F1} MB; private {process.PrivateMemorySize64 / 1048576.0:F1} MB; 3-second CPU {(process.TotalProcessorTime - before).TotalMilliseconds / 3000 / Environment.ProcessorCount * 100:F3}%.");
        File.WriteAllLines(Path.Combine(Root, "report.txt"), report);
    }
    internal static async Task MeasureIdleAsync(MainWindow window)
    {
        await Task.Delay(2000);
        var process = Process.GetCurrentProcess(); process.Refresh();
        var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew();
        await Task.Delay(5000); process.Refresh();
        var visibleCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        var workingSet = process.WorkingSet64 / 1048576d; var privateBytes = process.PrivateMemorySize64 / 1048576d;
        window.Hide(); await Task.Delay(1000); cpu = process.TotalProcessorTime; timer.Restart();
        await Task.Delay(5000); process.Refresh();
        var hiddenCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / timer.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        File.WriteAllText(Path.Combine(Root, "idle-report.txt"), $"Disconnected synthetic fixture, no screen captures or explicit GC. Visible CPU {visibleCpu:F3}%; hidden CPU {hiddenCpu:F3}%; visible Working Set {workingSet:F1} MB, private {privateBytes:F1} MB.\n");
    }
    private static void CheckTrayMenu(MainWindow window, MainViewModel vm, List<string> report)
    {
        var menu = window.BuildMenu();
        var serverItems = ((Forms.ToolStripMenuItem)menu.Items[2]).DropDownItems;
        serverItems[1].PerformClick();
        if (vm.SelectedServer != vm.Data.Servers[1]) throw new InvalidOperationException("Tray server selection did not update the view model.");
        for (var i = 0; i < 100; i++)
        {
            var previous = menu.Items.Cast<Forms.ToolStripItem>().SelectMany(item => item is Forms.ToolStripMenuItem submenu
                ? submenu.DropDownItems.Cast<Forms.ToolStripItem>().Append(item) : [item]).ToArray();
            menu = window.BuildMenu();
            if (previous.Any(item => !item.IsDisposed)) throw new InvalidOperationException("Rebuilding the tray menu did not dispose: " + string.Join(", ", previous.Where(item => !item.IsDisposed).Select(item => item.GetType().Name + ": " + item.Text)));
            if (menu.Items.Count != 7 || menu.Items[0].Text != vm.ConnectLabel) throw new InvalidOperationException("Repeated tray rebuilds changed the menu contents.");
            var servers = ((Forms.ToolStripMenuItem)menu.Items[2]).DropDownItems.Cast<Forms.ToolStripMenuItem>().ToArray();
            if (servers.Length != vm.Data.Servers.Count || servers.Count(item => item.Checked) != 1 || !servers[1].Checked)
                throw new InvalidOperationException("The tray menu must retain the current server selection.");
            var profiles = ((Forms.ToolStripMenuItem)menu.Items[3]).DropDownItems.Cast<Forms.ToolStripMenuItem>().ToArray();
            if (profiles.Length != vm.Data.Profiles.Count || profiles.Count(item => item.Checked) != 1)
                throw new InvalidOperationException("The tray menu must retain the current profile selection.");
        }
        vm.SelectedServer = vm.Data.Servers[0];
        report.Add("Tray menu rebuilt 100 times; old items/submenus disposed; server selection and profile checks passed.");
    }
    private static async Task CheckCaptionButtonsAsync(MainWindow window, MainViewModel vm, List<string> report)
    {
        var minimize = (Button)window.FindName("MinimizeButton");
        var maximize = (Button)window.FindName("MaximizeButton");
        var close = (Button)window.FindName("CloseButton");
        foreach (var button in new[] { minimize, maximize, close })
            if (button.ActualWidth < 46 || button.ActualHeight < 36) throw new InvalidOperationException("Caption buttons must have a full 46×36 hit area.");
        minimize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize button failed.");
        window.WindowState = WindowState.Normal;
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Maximized || ((IconView)maximize.Content).Icon != "Restore" || maximize.ToolTip as string != "Восстановить")
            throw new InvalidOperationException("Maximize button must switch to Restore.");
        Capture(window, "Window-Maximized");
        I18n.Set("English");
        if (maximize.ToolTip as string != "Restore" || close.ToolTip as string != "Close") throw new InvalidOperationException("Caption labels must follow the selected language.");
        I18n.Set("Русский");
        maximize.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.WindowState != WindowState.Normal || ((IconView)maximize.Content).Icon != "Maximize" || maximize.ToolTip as string != "Развернуть")
            throw new InvalidOperationException("Restore button failed.");
        var minimizeToTray = vm.Settings.MinimizeToTray;
        vm.Settings.MinimizeToTray = true;
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.IsVisible) throw new InvalidOperationException("Close must respect Minimize to tray.");
        window.Show();
        vm.Settings.MinimizeToTray = minimizeToTray;
        report.Add("Caption buttons: hit areas, minimize, maximize/restore icon and labels, language and close-to-tray behavior passed.");
    }
    private static async Task CheckUpdateOverlayAsync(MainWindow window, MainViewModel vm, List<string> report)
    {
        var version = Version.Parse(vm.AppVersion);
        var next = new Version(version.Major, version.Minor, version.Build + 1).ToString();
        var candidate = new AvailableUpdate(new(next, "fixture.exe", 90 * 1048576, new string('0', 64), "Более плавная работа приложения и улучшения подключения.\nНастройки, подписки и правила будут сохранены."), "fixture.exe");
        window.Hide(); vm.SetAvailableUpdate(candidate);
        if (vm.UpdateOverlayOpen) throw new InvalidOperationException("Updates must not open a window while the app is in the tray.");
        window.Show(); window.Activate();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.TryShowUpdate();
        await Task.Delay(350); window.UpdateLayout();
        var overlay = (UpdateOverlay)window.FindName("UpdatePopup");
        var surface = (Grid)window.FindName("AppSurface");
        if (!vm.UpdateOverlayOpen || !overlay.IsVisible || surface.IsEnabled || surface.Effect is not System.Windows.Media.Effects.BlurEffect)
            throw new InvalidOperationException($"The foreground update offer must be modal with a blurred, disabled background. Active={window.IsActive}, visible={window.IsVisible}, open={vm.UpdateOverlayOpen}, overlay={overlay.IsVisible}, enabled={surface.IsEnabled}, effect={surface.Effect?.GetType().Name ?? "none"}.");
        Capture(window, "Update-Available");
        var oldWidth = window.Width; var oldHeight = window.Height; window.Width = 860; window.Height = 660; window.UpdateLayout();
        foreach (var name in new[] { "LaterButton", "UpdateButton" })
        {
            var button = (Button)overlay.FindName(name);
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            if (button.ActualHeight < 46 || button.ActualWidth < 200 || bounds.Bottom > window.ActualHeight)
                throw new InvalidOperationException("Update popup actions must fit at minimum window size.");
        }
        Capture(window, "Update-Minimum");
        vm.DeferUpdate.Execute(null); window.UpdateLayout();
        if (vm.UpdateOverlayOpen || !surface.IsEnabled || surface.Effect is not null) throw new InvalidOperationException("Not now must restore the underlying application.");
        vm.SetAvailableUpdate(candidate); window.TryShowUpdate();
        if (vm.UpdateOverlayOpen) throw new InvalidOperationException("The same release must not prompt again in this session.");
        vm.InstallUpdate.Execute(null);
        if (!vm.UpdateOverlayOpen) throw new InvalidOperationException("Settings must be able to reopen a deferred update.");
        // Render-only fixtures set private presentation state; no download or installer is executed.
        static void Set(MainViewModel target, string name, object value) => typeof(MainViewModel).GetProperty(name)!.SetValue(target, value);
        Set(vm, nameof(vm.UpdateBusy), true); Set(vm, nameof(vm.UpdateStage), "Downloading"); Set(vm, nameof(vm.UpdatePercent), 57);
        Set(vm, nameof(vm.UpdateStatus), "Загружаем обновление…");
        await Task.Delay(350); window.UpdateLayout(); Capture(window, "Update-Progress");
        var progress = (ProgressBar)overlay.FindName("DownloadProgress");
        var rotation = (RotateTransform)overlay.FindName("SpinnerRotation");
        if (Math.Abs(progress.Value - 57) > .1 || (Motion.Enabled && !rotation.HasAnimatedProperties)) throw new InvalidOperationException("Download must show live progress and animated loading.");
        vm.AnimationsEnabled = false;
        if (rotation.HasAnimatedProperties || progress.HasAnimatedProperties) throw new InvalidOperationException("Update loading must respect reduced motion.");
        vm.AnimationsEnabled = true; window.Hide();
        if (rotation.HasAnimatedProperties) throw new InvalidOperationException("Hidden update loading must stop animating.");
        window.Show(); window.Activate();
        Set(vm, nameof(vm.UpdatePercent), 100); Set(vm, nameof(vm.UpdateStage), "Verifying");
        Set(vm, nameof(vm.UpdateStatus), "Проверяем размер и SHA-256…");
        await Task.Delay(300); window.UpdateLayout(); Capture(window, "Update-Verify");
        Set(vm, nameof(vm.UpdateStage), "Restarting"); Set(vm, nameof(vm.UpdateStatus), "Ожидаем подтверждения Windows…");
        window.UpdateLayout(); Capture(window, "Update-Restart");
        Set(vm, nameof(vm.UpdateStage), "Failed"); Set(vm, nameof(vm.UpdateBusy), false);
        Set(vm, nameof(vm.UpdateStatus), "Запрос Windows отменён. Можно повторить обновление.");
        window.UpdateLayout(); Capture(window, "Update-Cancelled");
        if (!vm.AcceptUpdate.CanExecute(null) || !vm.UpdateOverlayOpen) throw new InvalidOperationException("Failed updates must offer retry without closing the app.");
        vm.DeferUpdate.Execute(null); vm.SetAvailableUpdate(candidate with { Release = candidate.Release with { Version = "99.0.0" } }); window.TryShowUpdate();
        if (!vm.UpdateOverlayOpen) throw new InvalidOperationException("A different new release must be eligible for a prompt.");
        vm.DeferUpdate.Execute(null); vm.SetAvailableUpdate(null);
        window.Width = oldWidth; window.Height = oldHeight; window.UpdateLayout();
        report.Add("Updates: tray deferral, automatic foreground offer, Not now/session suppression, explicit reopen, new release, modal blur, 46px actions at minimum size, progress/stages, reduced motion, hidden animation stop and retry UI. No installer launched.");
    }
    private static async Task CheckHomeStatesAsync(MainWindow window, MainViewModel vm, List<string> report)
    {
        // Presentation fixtures use actual traffic snapshots without starting a tunnel.
        static void Set(MainViewModel target, string name, object value) => typeof(MainViewModel).GetProperty(name)!.SetValue(target, value);
        static void Traffic(MainViewModel target, TrafficSnapshot? value) => typeof(MainViewModel).GetMethod("SetTraffic", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(target, [value]);
        vm.Go("Home"); await vm.StopScansAsync(); window.UpdateLayout();
        foreach (var (node, ms) in vm.Data.Servers.Zip(new long?[] { 25, 125, 310, 68, 91, 165, 240, null, 80 })) { node.LatencyMs = ms; node.Latency = ms is null ? "—" : ms + " ms"; }
        var home = Descendants(window).OfType<HomeView>().Single();
        var orbit = (RotateTransform)home.FindName("Orbit"); var shift = (TranslateTransform)home.FindName("SpeedShift");
        Set(vm, nameof(vm.State), ConnectionState.Connecting);
        await Task.Delay(180); window.UpdateLayout(); Capture(window, "Home-Connecting");
        if (Motion.Enabled && !orbit.HasAnimatedProperties) throw new InvalidOperationException("Connecting must show a moving progress indicator.");
        Set(vm, nameof(vm.State), ConnectionState.Connected); Set(vm, nameof(vm.VpnIp), "203.0.113.42"); Set(vm, nameof(vm.Session), "00:16:42");
        TrafficSnapshot? sample = null;
        var start = DateTimeOffset.UtcNow.AddSeconds(-60);
        for (var i = 0; i < 60; i++)
        {
            sample = new((1.8 + Math.Cos(i * .45) * .6) * 125_000, (24 + Math.Sin(i * .35) * 8) * 125_000, 27_000_000, 410_000_000, start.AddSeconds(i));
            Traffic(vm, sample);
        }
        Traffic(vm, sample);
        if (vm.DownloadHistory.Count != 48 || vm.UploadHistory.Count != 48) throw new InvalidOperationException("Traffic histories must retain only 48 samples and ignore a repeated timestamp.");
        await Task.Delay(200); window.UpdateLayout();
        foreach (var chart in Descendants(home).OfType<TrafficChart>())
            if (chart.Samples?.Count != 48 || chart.ActualWidth <= 0 || chart.ActualHeight <= 0) throw new InvalidOperationException("Traffic charts must display their measured direction.");
        if (Motion.Enabled && (!shift.HasAnimatedProperties || orbit.HasAnimatedProperties)) throw new InvalidOperationException("Connected motion must replace the loading spinner.");
        Capture(window, "Home-Connected");
        var oldWidth = window.Width; var oldHeight = window.Height;
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout(); Capture(window, "Home-Connected-Minimum");
        var connect = (Button)home.FindName("ConnectPower");
        if (connect.ActualWidth < 218 || connect.ActualHeight < 54) throw new InvalidOperationException("The primary action must keep its full area at minimum size.");
        I18n.Set("English"); window.UpdateLayout(); Capture(window, "Home-English-Minimum"); I18n.Set("Русский");
        window.Width = oldWidth; window.Height = oldHeight;
        vm.AnimationsEnabled = false;
        if (shift.HasAnimatedProperties || orbit.HasAnimatedProperties) throw new InvalidOperationException("Home motion must respect reduced motion.");
        vm.AnimationsEnabled = true; window.Hide();
        if (shift.HasAnimatedProperties || orbit.HasAnimatedProperties) throw new InvalidOperationException("Home motion must stop while hidden.");
        window.Show(); window.Activate(); await Task.Delay(100);
        Set(vm, nameof(vm.State), ConnectionState.Error); Traffic(vm, null); Set(vm, nameof(vm.VpnIp), "—"); Set(vm, nameof(vm.Session), "—");
        window.UpdateLayout(); Capture(window, "Home-Error");
        Set(vm, nameof(vm.State), ConnectionState.Disconnected);
        if (vm.DownloadHistory.Count != 0 || vm.UploadHistory.Count != 0 || shift.HasAnimatedProperties || orbit.HasAnimatedProperties) throw new InvalidOperationException("Disconnected home must clear session traffic and stop motion.");
        report.Add("Night Track: one-time theme migration preserves later accent choices; connecting/connected/error and English/minimum layouts; real-direction charts capped at 48 unique samples; reduced motion and hidden animation stop.");
    }
    private static void Capture(Window window, string name)
    {
        foreach (var scale in new[] { 1.0, 1.75 })
        {
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Root, name + "-" + (int)(scale * 100) + ".png")); encoder.Save(output);
        }
    }
    private static void CheckGeometry(MainWindow window, string page)
    {
        var halo = (FrameworkElement)window.FindName("LogoHalo");
        if (!halo.IsVisible || halo.ActualWidth < 68 || halo.ActualHeight < 68) throw new InvalidOperationException("Logo glow is missing.");
        if (page == "Home")
        {
            var view = Descendants(window).OfType<HomeView>().Single(); var power = (FrameworkElement)view.FindName("PowerHalo"); var hero = (FrameworkElement)view.FindName("Hero");
            var bounds = power.TransformToAncestor(hero).TransformBounds(new Rect(0, 0, power.ActualWidth, power.ActualHeight));
            if (bounds.Top < 0 || bounds.Bottom > hero.ActualHeight) throw new InvalidOperationException("Home glow must fit inside its hero block.");
        }
        if (page == "Servers")
        {
            var values = Descendants(window).OfType<TextBlock>().Where(t => t.DataContext is Server && t.Text.EndsWith(" ms")).ToArray();
            if (values.Length != 8) throw new InvalidOperationException("Latency fixtures must actually be rendered.");
            foreach (var value in values)
            {
                var server = (Server)value.DataContext; var key = LatencyDisplay.Quality(server.LatencyMs) switch { LatencyQuality.Good => "PingGood", LatencyQuality.Moderate => "PingModerate", _ => "PingPoor" };
                if (value.Foreground != Application.Current.Resources[key]) throw new InvalidOperationException("Measured latency must use its quality color.");
                var parent = VisualTreeHelper.GetParent(value);
                while (parent is not null && parent is not Button) parent = VisualTreeHelper.GetParent(parent);
                if (parent is Button ping)
                {
                    ping.IsEnabled = false; ping.ApplyTemplate();
                    if (ping.Template.FindName("HitArea", ping) is not Border surface || surface.Opacity != 1 || ping.Opacity != 1 || value.Opacity != 1)
                        throw new InvalidOperationException("Busy ping buttons must preserve full color intensity.");
                    ping.ClearValue(UIElement.IsEnabledProperty);
                }
            }
            foreach (var button in Descendants(window).OfType<Button>().Where(b => b.IsVisible && b.Style == Application.Current.Resources["IconButton"]))
                if (Math.Abs(button.ActualWidth - button.ActualHeight) > .1 || button.ActualWidth < 46) throw new InvalidOperationException("Server toolbar icons must have square 46×46 areas.");
        }
    }
    private static void CheckRulesViewport(MainWindow window)
    {
        var view = Descendants(window).OfType<RulesView>().Single();
        var list = (ListBox)view.FindName("RulesList");
        if (list.ActualHeight < 100) throw new InvalidOperationException($"Routing controls must leave a usable rules viewport at minimum window size, including with a banner: {list.ActualHeight:F1}px.");
    }
    private static void CheckRuleEditor(Window dialog, MainViewModel vm)
    {
        var editor = Descendants(dialog).OfType<RuleEditor>().Single();
        var choices = Descendants(editor).OfType<RadioButton>().ToArray();
        if (choices.Length != 8 || choices.Any(c => c.ActualHeight < 44 || c.ActualWidth < 180)) throw new InvalidOperationException("Rule choices need full, equal-sized hit areas.");
        foreach (var (kind, value) in new[] { (RuleKind.Application, "Telegram.exe"), (RuleKind.GeoSite, "openai"), (RuleKind.Site, "figma.com"), (RuleKind.Contains, "claude"), (RuleKind.GeoIp, "telegram"), (RuleKind.Network, "192.168.1.0/24") })
        {
            editor.SelectKind(kind); editor.ValueField.Text = value; editor.NameField.Clear();
            var rule = editor.Build(); if (rule.Kind != kind || rule.Name.Length == 0 || rule.Values.Count != 1) throw new InvalidOperationException("Rule type or optional name was not saved correctly.");
        }
        editor.SelectKind(RuleKind.Application); editor.ValueField.Text = "Telegram.exe";
        editor.ServerField.SelectedIndex = 2; editor.SelectAction(true);
        if (editor.Build().ServerId != vm.Data.Servers[1].Id) throw new InvalidOperationException("Explicit server selection was lost.");
        editor.SelectAction(false);
        if (editor.ServerField.IsEnabled || editor.Build().UseVpn || editor.Build().ServerId is not null) throw new InvalidOperationException("Direct rules cannot use a VPN server.");
        editor.SelectAction(true); editor.ServerField.SelectedIndex = 0;
        dialog.UpdateLayout();
        foreach (var button in Descendants(dialog).OfType<Button>().Where(b => b.IsDefault || b.Content as string == "Отмена"))
            if (button.ActualHeight < 44 || button.ActualWidth < 200) throw new InvalidOperationException("Dialog action buttons are compressed.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var child = VisualTreeHelper.GetChild(d, i); yield return child; foreach (var x in Descendants(child)) yield return x; } }
}
