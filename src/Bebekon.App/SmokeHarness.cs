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
        foreach (var name in new[] { "Estonia", "Poland", "Hungary", "Bulgaria", "Austria", "Romania", "Norway", "Albania", "USA · New York", "Russia, Saint Petersburg", "Russia, Novosibirsk", "Brazil", "France", "Germany", "Italy", "United Kingdom" }) data.Servers.Add(VlessParser.Parse("vless://" + Guid.NewGuid() + "@127.0.0.1:9?security=none&type=tcp#" + Uri.EscapeDataString(name)));
        data.Subscriptions.Add(new() { Name = "UI test fixture", Source = "https://example.invalid/private-token", ServerCount = data.Servers.Count });
        data.SelectedServerId = data.Servers[0].Id;
        data.Profiles[0].Rules.Add(new() { Name = "OpenAI / ChatGPT", Values = ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Claude / Anthropic", Values = ["claude.ai", "anthropic.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Telegram", Kind = RuleKind.Application, Values = ["Telegram.exe"] });
        data.Settings.TunnelMode = TunnelMode.Proxy;
        store.Save(data);
        var vm = new MainViewModel(store);
        if (vm.Settings.TunnelMode != TunnelMode.Tun) throw new InvalidOperationException("Startup must select TUN even after a prior proxy session.");
        return vm;
    }
    public static async Task RunAsync(MainWindow window, MainViewModel vm)
    {
        await Task.Delay(400); var report = new List<string> { "Startup selects TUN after a saved proxy session." };
        CheckTrayMenu(window, vm, report);
        await CheckCaptionButtonsAsync(window, vm, report);
        foreach (var page in new[] { "Home", "Servers", "Rules", "Subscriptions", "Settings" })
        {
            vm.Go(page); await Task.Delay(300); window.UpdateLayout();
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
            {
                var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(Root, page + "-" + (int)(scale * 100) + ".png")); encoder.Save(output);
            }
            report.Add(page + " rendered at 100/125/150/175%.");
        }
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
        foreach (var category in new[] { "Network", "Appearance", "Behavior", "Advanced", "All" })
        {
            Descendants(settings).OfType<RadioButton>().Single(r => r.Tag as string == category).IsChecked = true;
            window.UpdateLayout(); Capture(window, "Settings-" + category);
        }
        vm.IsProxyMode = true; if (vm.IsTunMode || !vm.IsProxyMode) throw new InvalidOperationException("TUN and Proxy cannot be active together.");
        vm.IsTunMode = true; if (!vm.IsTunMode || vm.IsProxyMode) throw new InvalidOperationException("TUN must switch Proxy off.");
        report.Add("Settings categories, cross-category search, empty results and TUN/Proxy exclusivity passed.");
        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
        foreach (var page in new[] { "Home", "Servers", "Rules", "Subscriptions", "Settings" }) { vm.Go(page); await Task.Delay(150); window.UpdateLayout(); Capture(window, page + "-Minimum"); }
        window.Width = 1000; window.Height = 740; window.UpdateLayout();
        vm.Go("Settings"); await Task.Delay(150); I18n.Set("English"); window.UpdateLayout(); Capture(window, "Settings-English"); I18n.Set("Русский");
        string dialogName = "Subscription"; Exception? renderFailure = null;
        Dialogs.RenderObserver = dialog => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { dialog.UpdateLayout(); Capture(dialog, "Dialog-" + dialogName); }
            catch (Exception error) { renderFailure = error; }
            finally { dialog.Close(); }
        }));
        try
        {
            Dialogs.Subscription(null); dialogName = "Rule"; Dialogs.Rule(null);
            dialogName = "Preset"; Dialogs.Preset(vm.Presets);
            dialogName = "Application"; await Dialogs.ApplicationAsync();
            dialogName = "Confirm"; Dialogs.Confirm("Удалить правило?", "UI test fixture");
        }
        finally { Dialogs.RenderObserver = null; }
        if (renderFailure is not null) throw renderFailure;
        report.Add("Five native dialogs rendered with the shared theme.");
        vm.Go("Rules"); for (var i = 0; i < 500; i++) vm.ActiveProfile.Rules.Add(new() { Name = "Rule " + i, Values = [$"site{i}.example.com"] }); vm.RuleSearch = ""; vm.RuleSearch = "Rule"; await Task.Delay(200); window.UpdateLayout();
        vm.IsWholePc = true; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Routing mode selection is inconsistent.");
        vm.IsWholePc = false; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Selective mode selection is inconsistent.");
        report.Add("Mode switches: one selection in Entire PC and By rules.");
        var boxes = Descendants(window).OfType<ListBox>().ToArray(); var realized = boxes.Sum(b => Descendants(b).OfType<ListBoxItem>().Count()); report.Add($"503 rules, realized list containers: {realized} (virtualization).");
        vm.Go("Home"); await Task.Delay(1000); using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.TotalProcessorTime; await Task.Delay(3000); process.Refresh(); report.Add($"After rendering (not an idle benchmark): Working Set {process.WorkingSet64 / 1048576.0:F1} MB; private {process.PrivateMemorySize64 / 1048576.0:F1} MB; 3-second CPU {(process.TotalProcessorTime - before).TotalMilliseconds / 3000 / Environment.ProcessorCount * 100:F3}%.");
        File.WriteAllLines(Path.Combine(Root, "report.txt"), report);
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
    private static void Capture(Window window, string name)
    {
        foreach (var scale in new[] { 1.0, 1.75 })
        {
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Root, name + "-" + (int)(scale * 100) + ".png")); encoder.Save(output);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var child = VisualTreeHelper.GetChild(d, i); yield return child; foreach (var x in Descendants(child)) yield return x; } }
}
