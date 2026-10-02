using System.ComponentModel;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace Bebekon.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon icon;
    public bool ForceExit { get; set; }
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent(); this.vm = vm; DataContext = vm;
        icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "resources", "bebekon.ico"));
        tray = new() { Icon = icon, Text = "Bebekon VPN", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        tray.ContextMenuStrip = new(); tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        vm.StatusChanged += OnStatus; vm.PropertyChanged += OnChanged;
        Closing += OnClosing;
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Page) || !SystemParameters.ClientAreaAnimation) return;
        // No animation remains active after navigation.
        PageContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }
    private void OnStatus()
    {
        tray.Text = "Bebekon VPN · " + vm.FooterLabel;
        if (vm.Settings.Notifications && !IsVisible && vm.State is ConnectionState.Connected or ConnectionState.Error) tray.ShowBalloonTip(3000, "Bebekon VPN", vm.StatusLabel, vm.Connected ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }
    internal Forms.ContextMenuStrip BuildMenu()
    {
        var menu = tray.ContextMenuStrip!;
        // Disposing an owned ToolStripItem removes it from Items. Enumerate a
        // snapshot so reopening the tray menu cannot invalidate its enumerator.
        var previousItems = menu.Items.Cast<Forms.ToolStripItem>().ToArray();
        foreach (var item in previousItems) item.Dispose();
        menu.Items.Clear();
        var connect = menu.Items.Add(vm.ConnectLabel); connect.Enabled = !vm.ConnectionBusy; connect.Click += (_, _) => vm.ToggleConnect.Execute(null);
        menu.Items.Add(I18n.T("Текущий сервер: ", "Current server: ") + vm.ServerLabel).Enabled = false;
        var servers = new Forms.ToolStripMenuItem(I18n.T("Выбрать сервер", "Choose a server"));
        foreach (var s in vm.Data.Servers.Take(100)) { var item = new Forms.ToolStripMenuItem(s.Name) { Checked = s == vm.SelectedServer, Enabled = s.Supported }; item.Click += (_, _) => vm.SelectedServer = s; servers.DropDownItems.Add(item); } menu.Items.Add(servers);
        var profiles = new Forms.ToolStripMenuItem(I18n.T("Профиль: ", "Profile: ") + vm.ActiveProfile.Name);
        foreach (var p in vm.Data.Profiles) { var item = new Forms.ToolStripMenuItem(p.Name) { Checked = p == vm.ActiveProfile }; item.Click += (_, _) => vm.ActiveProfile = p; profiles.DropDownItems.Add(item); } menu.Items.Add(profiles);
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(I18n.T("Открыть", "Open")).Click += (_, _) => ShowWindow(); menu.Items.Add(I18n.T("Выход", "Exit")).Click += async (_, _) => await ExitAsync();
        return menu;
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ForceExit) { vm.StatusChanged -= OnStatus; vm.PropertyChanged -= OnChanged; vm.Dispose(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); icon.Dispose(); return; }
        e.Cancel = true; if (vm.Settings.MinimizeToTray) Hide(); else await ExitAsync();
    }
    private async Task ExitAsync()
    {
        try { if (vm.Connected || vm.ConnectionBusy) await vm.DisconnectAsync(); }
        catch (Exception e) { vm.Report(e); ShowWindow(); return; }
        ForceExit = true; Close(); Application.Current.Shutdown();
    }
}
