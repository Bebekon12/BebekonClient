using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Input;

namespace Bebekon.App;

public sealed partial class MainViewModel
{
    private readonly UpdatePromptSession updatePrompts = new();
    private bool updateOverlayOpen;
    private string updateStage = "Offer";
    private int updatePercent;
    public bool UpdateOverlayOpen { get => updateOverlayOpen; private set => Set(ref updateOverlayOpen, value); }
    public string UpdateStage { get => updateStage; private set { Set(ref updateStage, value); NotifyUpdatePresentation(); } }
    public int UpdatePercent { get => updatePercent; private set { Set(ref updatePercent, value); Notify(nameof(UpdateTransfer)); } }
    public bool UpdateOffer => UpdateStage == "Offer";
    public bool UpdateFailed => UpdateStage == "Failed";
    public bool UpdateProgressVisible => !UpdateOffer;
    public bool UpdateIndeterminate => UpdateStage is "Verifying" or "Restarting";
    public string UpdateTargetVersion => "v" + availableUpdate?.Release.Version;
    public string UpdateVersionChange => "v" + AppVersion + "  →  " + UpdateTargetVersion;
    public string UpdateNotes => availableUpdate?.Release.Notes ?? "";
    public string UpdateTransfer => availableUpdate is { } update ? $"{update.Release.Size * UpdatePercent / 100.0 / 1048576:0.0} / {update.Release.Size / 1048576.0:0.0} MB" : "";
    public string UpdateHeading => UpdateStage switch { "Downloading" => T("Загружаем обновление", "Downloading update"), "Verifying" => T("Проверяем файл", "Verifying package"), "Restarting" => T("Готовим перезапуск", "Preparing to restart"), "Failed" => T("Обновление не завершено", "Update not completed"), _ => T("Доступно обновление", "An update is available") };
    public ICommand DeferUpdate { get; private set; } = null!;
    public ICommand AcceptUpdate { get; private set; } = null!;
    internal bool CanPromptForUpdate => availableUpdate is not null && !UpdateBusy && !UpdateOverlayOpen && updatePrompts.ShouldShow(availableUpdate.Release.Version);
    internal void PresentUpdate(bool explicitRequest = false)
    {
        if (UpdateBusy || availableUpdate is null || !updatePrompts.ShouldShow(availableUpdate.Release.Version, explicitRequest)) return;
        UpdateStage = "Offer"; UpdateOverlayOpen = true;
    }
    private void DismissUpdate()
    {
        if (UpdateBusy) { updateCancellation?.Cancel(); return; }
        if (availableUpdate is not null) updatePrompts.Defer(availableUpdate.Release.Version);
        UpdateOverlayOpen = false;
    }
    private void NotifyUpdatePresentation()
    {
        foreach (var name in new[] { nameof(UpdateOffer), nameof(UpdateFailed), nameof(UpdateProgressVisible), nameof(UpdateIndeterminate), nameof(UpdateHeading), nameof(UpdateTargetVersion), nameof(UpdateVersionChange), nameof(UpdateNotes), nameof(UpdateTransfer) }) Notify(name);
    }
    private AppUpdates? updater;
    private AvailableUpdate? availableUpdate;
    private CancellationTokenSource? updateCancellation;
    private bool updateBusy;
    private bool updateInstalling;
    private string updateStatus = "";
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version!.ToString(3);
    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public bool UpdateBusy { get => updateBusy; private set { Set(ref updateBusy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool AutoUpdates { get => Settings.CheckForUpdates; set { Settings.CheckForUpdates = value; Save(); Notify(); } }
    public string UpdateSource => string.IsNullOrWhiteSpace(Settings.UpdateSource) ? DefaultUpdateSource() : Settings.UpdateSource;
    public string UpdateSourceLabel => string.IsNullOrEmpty(UpdateSource) ? T("Канал ещё не подключён", "No channel configured") : UpdateSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? new Uri(UpdateSource).Host : T("Локальная папка сборок", "Local release folder");
    public Visibility UpdateVisibility => availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    public string UpdateNotice => T("Доступна версия ", "Version available: ") + availableUpdate?.Release.Version;
    public ICommand CheckUpdates { get; private set; } = null!;
    public ICommand InstallUpdate { get; private set; } = null!;
    public ICommand CancelUpdate { get; private set; } = null!;
    public ICommand ConfigureUpdates { get; private set; } = null!;
    public event Action? UpdateFound;
    internal void SetAvailableUpdate(AvailableUpdate? update)
    {
        var changed = availableUpdate?.Release.Version != update?.Release.Version;
        availableUpdate = update;
        NotifyUpdatePresentation();
        UpdateStatus = update is null ? T("Установлена последняя версия · ", "Up to date · ") + AppVersion : UpdateNotice;
        Notify(nameof(UpdateNotice)); Notify(nameof(UpdateVisibility)); CommandManager.InvalidateRequerySuggested();
        if (changed && update is not null) UpdateFound?.Invoke();
    }

    private static string DefaultUpdateSource()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "resources", "update-channel.json");
            if (!File.Exists(file)) return "";
            using var data = JsonDocument.Parse(File.ReadAllText(file));
            return AppUpdates.NormalizeSource(data.RootElement.GetProperty("source").GetString() ?? "");
        }
        catch { return ""; }
    }

    private void InitializeUpdates()
    {
        using var key = typeof(MainViewModel).Assembly.GetManifestResourceStream("Bebekon.UpdateKey")!;
        using var reader = new StreamReader(key); updater = new(reader.ReadToEnd());
        UpdateStatus = T("Текущая версия ", "Current version ") + AppVersion;
        CheckUpdates = Async(_ => CheckUpdatesAsync(false), () => !UpdateBusy);
        InstallUpdate = new Command(_ => PresentUpdate(true), () => !UpdateBusy && availableUpdate is not null);
        AcceptUpdate = Async(_ => InstallUpdateAsync(), () => !UpdateBusy && availableUpdate is not null);
        DeferUpdate = new Command(_ => DismissUpdate());
        CancelUpdate = new Command(_ => updateCancellation?.Cancel(), () => UpdateBusy);
        ConfigureUpdates = new Command(_ =>
        {
            var source = Dialogs.Text(T("Источник обновлений", "Update source"), T("HTTPS-адрес update.json или путь к локальному файлу", "HTTPS update.json address or local file path"), UpdateSource);
            if (source is null) return;
            try
            {
                Settings.UpdateSource = AppUpdates.NormalizeSource(source); availableUpdate = null; Save();
                Notify(nameof(UpdateSource)); Notify(nameof(UpdateSourceLabel)); Notify(nameof(UpdateVisibility));
                UpdateStatus = T("Канал сохранён. Нажмите «Проверить».", "Channel saved. Click Check.");
            }
            catch (Exception e) { Report(e); }
        }, () => !UpdateBusy);
    }

    private async Task WatchUpdatesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                if (AutoUpdates && !UpdateBusy && UpdateSource.Length > 0) await CheckUpdatesAsync(true);
                await Task.Delay(TimeSpan.FromHours(6), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task CheckUpdatesAsync(bool background)
    {
        if (UpdateBusy || UpdateOverlayOpen) return;
        if (UpdateSource.Length == 0) { UpdateStatus = T("Укажите источник обновлений ниже.", "Configure an update source below."); return; }
        UpdateBusy = true; UpdateStatus = T("Проверяем обновления…", "Checking for updates…");
        updateCancellation?.Dispose(); updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            SetAvailableUpdate(await updater!.CheckAsync(UpdateSource, Assembly.GetExecutingAssembly().GetName().Version!, updateCancellation.Token));
        }
        catch (OperationCanceledException) { UpdateStatus = T("Проверка отменена или истекло время ожидания.", "Check cancelled or timed out."); }
        catch (Exception e)
        {
            UpdateStatus = e is UserError ? e.Message : T("Источник недоступен. Повторите проверку позже.", "Source unavailable. Try again later.");
            log.Write("Update check failed: " + e.GetType().Name);
        }
        finally { Notify(nameof(UpdateNotice)); Notify(nameof(UpdateVisibility)); UpdateBusy = false; }
        if (!background && availableUpdate is not null) PresentUpdate(true);
    }

    private async Task InstallUpdateAsync()
    {
        if (UpdateBusy || availableUpdate is not { } update) return;
        UpdateOverlayOpen = true; UpdatePercent = 0; UpdateStage = "Downloading";
        UpdateBusy = true; updateCancellation?.Dispose(); updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        string? package = null; var launched = false;
        try
        {
            UpdateStatus = T("Загружаем обновление…", "Downloading update…");
            package = await updater!.DownloadAsync(update, Path.Combine(store.Root, "updates"), new Progress<int>(percent =>
            {
                if (UpdateStage != "Downloading") return;
                UpdatePercent = percent;
                if (percent == 100) { UpdateStage = "Verifying"; UpdateStatus = T("Проверяем размер и SHA-256…", "Checking size and SHA-256…"); }
            }), updateCancellation.Token);
            updateCancellation.Token.ThrowIfCancellationRequested();
            updateInstalling = true; CommandManager.InvalidateRequerySuggested();
            await StopScansAsync();
            if (Connected || ConnectionBusy) await DisconnectAsync();
            Save();
            // Keep the verified package locked against writes until Windows opens it.
            using var verified = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
            UpdatePercent = 100; UpdateStage = "Verifying";
            await AppUpdates.VerifyInstallerAsync(verified, update.Release, updateCancellation.Token);
            UpdateStage = "Restarting";
            var start = new ProcessStartInfo(package) { UseShellExecute = true };
            foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/NOCLOSEAPPLICATIONS", "/UPDATE=1", "/HANDOFF=2", "/LOG",
                "/PARENTID=" + Environment.ProcessId, "/OWNERSID=" + WindowsIdentity.GetCurrent().User!.Value,
                "/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) }) start.ArgumentList.Add(argument);
            // Let Setup request its own elevation so runasoriginaluser keeps the original user's identity.
            using var process = Process.Start(start) ?? throw new UserError("Не удалось запустить обновление.");
            UpdateStatus = T("Ожидаем подтверждения Windows…", "Waiting for Windows approval…");
            using var handoff = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token);
            handoff.CancelAfter(TimeSpan.FromMinutes(3));
            while (!File.Exists(package + ".ready"))
            {
                if (process.HasExited) throw new UserError(T("Установщик закрыт. Текущая версия сохранена.", "Installer closed. Current version kept."));
                await Task.Delay(150, handoff.Token);
            }
            updateCancellation.Token.ThrowIfCancellationRequested();
            File.WriteAllText(package + ".proceed", "accepted");
            launched = true;
            if (Application.Current.MainWindow is MainWindow window) { window.ForceExit = true; window.Close(); }
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { UpdateStatus = T("Обновление отменено. Текущая версия сохранена.", "Update cancelled. Current version kept."); UpdateStage = "Failed"; }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { UpdateStatus = T("Запрос Windows отменён. Можно повторить обновление.", "Windows prompt cancelled. You can try again."); UpdateStage = "Failed"; }
        catch (Exception e) { UpdateStatus = e is UserError ? e.Message : T("Не удалось обновить приложение. Текущая версия сохранена.", "Update failed. Current version kept."); log.Write("Update failed: " + e.GetType().Name); UpdateStage = "Failed"; }
        finally { updateInstalling = false; UpdateBusy = false; if (package is not null) { try { File.Delete(package + ".ready"); if (!launched) { File.Delete(package + ".proceed"); File.Delete(package); } } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } } }
    }
}
