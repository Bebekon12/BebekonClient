using System.Windows.Threading;

namespace Bebekon.App;
public partial class App : Application
{
    private Mutex? single;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke");
        single = new(true, @"Local\BebekonVPN." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + (smoke ? ".Smoke" : ""), out var first);
        if (!first) { MessageBox.Show("Bebekon VPN уже запущен. Откройте его через значок в трее."); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { new SafeLog(Paths.Logs, "app").Write("UI exception: " + args.Exception.GetType().Name); if (MainWindow?.DataContext is MainViewModel vm) vm.Report(args.Exception); args.Handled = true; };
        try
        {
            var vm = smoke ? SmokeHarness.CreateViewModel() : new MainViewModel();
            var window = new MainWindow(vm); MainWindow = window; window.Show();
            if (smoke) { await SmokeHarness.RunAsync(window, vm); window.ForceExit = true; window.Close(); Shutdown(); return; }
            await vm.InitializeAsync();
            if (e.Args.Contains("--tray")) window.Hide();
        }
        catch (Exception ex)
        {
            new SafeLog(Paths.Logs, "app").Write("Startup failed: " + ex.GetType().Name);
            if (e.Args.Contains("--smoke")) File.WriteAllText(Path.Combine(Path.GetTempPath(), "Bebekon-smoke-error.txt"), ex.ToString());
            else MessageBox.Show(ex is UserError ? ex.Message : "Не удалось открыть Bebekon VPN. Подробности в папке логов.");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { single?.Dispose(); base.OnExit(e); }
}
