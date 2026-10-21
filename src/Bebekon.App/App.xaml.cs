using System.Windows.Threading;

namespace Bebekon.App;
public partial class App : Application
{
    private Mutex? single;
    private EventWaitHandle? showRequest;
    private RegisteredWaitHandle? showWait;
    protected override async void OnStartup(StartupEventArgs e)
    {
        // A failed GPU surface can leave only the DWM frame visible after restore.
        // Use the WPF software pipeline for this small 2D dashboard and its dialogs.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke");
        var instance = @"Local\BebekonVPN." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + (smoke ? ".Smoke" : "");
        single = new(true, instance, out var first);
        if (!first)
        {
            // Opening the shortcut while the app already runs (e.g. started with Windows into the
            // tray) brings that window forward, as users expect, instead of reporting an error.
            if (!RequestShow(instance + ".Show")) MessageBox.Show("Bebekon VPN уже запущен. Откройте его через значок в трее.");
            Shutdown(); return;
        }
        showRequest = new(false, EventResetMode.AutoReset, instance + ".Show");
        showWait = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowWindow()), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) => { new SafeLog(Paths.Logs, "app").Write("UI exception: " + args.Exception.GetType().Name); if (MainWindow?.DataContext is MainViewModel vm) vm.Report(args.Exception); args.Handled = true; };
        try
        {
            var vm = smoke ? SmokeHarness.CreateViewModel() : new MainViewModel();
            // Started with Windows: stay in the tray from the first moment. Showing the window and
            // hiding it after a slow startup connection made it vanish while the user was using it.
            var window = new MainWindow(vm); MainWindow = window;
            if (smoke || !e.Args.Contains("--tray")) window.Show();
            if (smoke) { if (e.Args.Contains("--idle")) await SmokeHarness.MeasureIdleAsync(window); else await SmokeHarness.RunAsync(window, vm); await vm.StopScansAsync(); window.ForceExit = true; window.Close(); Shutdown(); return; }
            await vm.InitializeAsync(e.Args.Contains("--tray"));
        }
        catch (Exception ex)
        {
            new SafeLog(Paths.Logs, "app").Write("Startup failed: " + ex.GetType().Name);
            if (e.Args.Contains("--smoke")) File.WriteAllText(Path.Combine(Path.GetTempPath(), "Bebekon-smoke-error.txt"), ex.ToString());
            else MessageBox.Show(ex is UserError ? ex.Message : "Не удалось открыть Bebekon VPN. Подробности в папке логов.");
            Shutdown(1);
        }
    }
    private static bool RequestShow(string name)
    {
        // The first instance creates its signal right after the mutex; allow for that moment.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { using var signal = EventWaitHandle.OpenExisting(name); return signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }
    protected override void OnExit(ExitEventArgs e) { showWait?.Unregister(null); showRequest?.Dispose(); single?.Dispose(); base.OnExit(e); }
}
