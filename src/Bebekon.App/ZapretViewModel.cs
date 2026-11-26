using System.Diagnostics;
using System.Windows.Input;

namespace Bebekon.App;

/// <summary>zapret (WinDivert) managed by the helper: one switch, an import, and an automatic profile search.</summary>
public sealed partial class MainViewModel
{
    private ZapretStatus? zapretStatus;
    public ZapretStatus? ZapretState { get => zapretStatus; private set { if (Set(ref zapretStatus, value)) { Notify(nameof(ZapretSummary)); Notify(nameof(ZapretEnabled)); } } }
    public string ZapretSummary => zapretStatus switch
    {
        null or { Installed: false } => T("Обход блокировок без VPN через WinDivert. Папка zapret ещё не импортирована.", "Bypass blocks without VPN through WinDivert. No zapret folder imported yet."),
        { Running: true, Profile: { } profile } => T("Работает · ", "Running · ") + profile,
        { Error: { } problem } => problem,
        _ => T("Выключен · профиль ", "Off · profile ") + Settings.ZapretProfile
    };
    public bool ZapretEnabled
    {
        get => Settings.ZapretEnabled;
        set
        {
            if (Settings.ZapretEnabled == value) return;
            Settings.ZapretEnabled = value; Save(); Notify();
            _ = ApplyZapretAsync();
        }
    }
    public ICommand ConfigureZapret => configureZapret ??= Async(async _ => { await RefreshZapretAsync(); Dialogs.ZapretSettings(this); });
    private ICommand? configureZapret;

    private IEnumerable<string> VpnServerHosts => Data.Servers.Select(s => s.Host).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    private async Task<ServiceResponse> ZapretCommandAsync(ServiceRequest request)
    {
        await EnsureServiceAsync();
        var reply = await service.SendAsync(request, true, lifetime.Token);
        if (reply.Zapret is { } status) ZapretState = status;
        if (!reply.Ok) throw new UserError(reply.Message ?? T("zapret не ответил.", "zapret did not respond."));
        return reply;
    }
    internal async Task RefreshZapretAsync()
    {
        if (testProbe is not null) return;
        try { await ZapretCommandAsync(new("ZapretStatus")); } catch (UserError) { }
    }
    /// <summary>Starts or stops zapret to match the switch. Bebekon's own TLS splitting is turned off while zapret works.</summary>
    internal async Task ApplyZapretAsync()
    {
        if (testProbe is not null) return;
        try
        {
            if (Settings.ZapretEnabled)
            {
                await ZapretCommandAsync(new("ZapretStart", Zapret: Settings.ZapretProfile, ZapretHosts: [.. VpnServerHosts]));
                if (TlsFragmentEnabled) { TlsFragmentEnabled = false; Banner = T("Zapret запущен. Обход замедления Bebekon выключен.", "Zapret started. Bebekon's own splitting is off."); }
            }
            else await ZapretCommandAsync(new("ZapretStop"));
        }
        catch (UserError e)
        {
            Banner = "zapret: " + e.Message;
        }
    }
    internal Task StartZapretAtLaunchAsync() => Settings.ZapretEnabled ? ApplyZapretAsync() : Task.CompletedTask;

    /// <summary>One UAC prompt: an administrator copies the folder into the helper's protected storage.</summary>
    internal async Task ImportZapretAsync(string folder)
    {
        await ZapretCommandAsync(new("ZapretStop"));
        try
        {
            await RunImportAsync(folder);
            Settings.ObhodFolder = folder; Save();
            await RefreshZapretAsync();
        }
        finally
        {
            if (Settings.ZapretEnabled && !lifetime.IsCancellationRequested) await ApplyZapretAsync();
        }
    }

    private async Task RunImportAsync(string folder)
    {
        var helper = ServiceInstaller.HelperExecutable ?? throw new UserError(T("Служба Bebekon не установлена.", "The Bebekon helper is not installed."));
        var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--import-zapret"); info.ArgumentList.Add(folder);
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { throw new UserError(T("Импорт отменён: нужны права администратора.", "Import cancelled: administrator rights are required.")); }
        if (process is null) throw new UserError(T("Не удалось запустить импорт.", "Could not start the import."));
        using (process)
        {
            await process.WaitForExitAsync(lifetime.Token);
            if (process.ExitCode == 2) throw new UserError(T("Проверьте папки bin, lists и профили general*.bat в выбранной сборке zapret.", "Check the bin, lists and general*.bat profiles in the selected zapret package."));
            if (process.ExitCode != 0) throw new UserError(T("Не удалось обновить защищённую копию zapret. Закройте подбор профиля и повторите импорт.", "Could not update the protected zapret copy. Close the profile search and retry."));
        }
    }

    internal sealed record ZapretTrial(string Profile, bool Works, long? MedianMs, string? Failed);
    /// <summary>
    /// Starts each imported profile in turn and opens the test sites directly through it. Picks the
    /// fastest profile that opens all of them, saves it, and leaves it running if zapret is on.
    /// </summary>
    internal async Task<IReadOnlyList<ZapretTrial>> FindZapretProfileAsync(Action<string, ZapretTrial?> progress, CancellationToken ct)
    {
        var status = (await ZapretCommandAsync(new("ZapretStatus"))).Zapret;
        var profiles = status?.Profiles ?? [];
        if (profiles.Length == 0) throw new UserError(T("Сначала импортируйте папку zapret.", "Import a zapret folder first."));
        var sites = Settings.ZapretSites.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sites.Length == 0) throw new UserError(T("Укажите сайты для проверки.", "Enter sites to check."));
        var uplink = System.Net.IPAddress.TryParse(NetworkAddresses.Read().Address, out var address) ? address : null;
        var singBox = Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
        var trials = new List<ZapretTrial>();
        try
        {
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                progress(profile, null);
                ZapretTrial trial;
                try
                {
                    await ZapretCommandAsync(new("ZapretStart", Zapret: profile, ZapretHosts: [.. VpnServerHosts]));
                    var (works, median, failed) = await Task.Run(() => ThrottleProbe.TestSitesAsync(singBox, sites, uplink, ct), ct);
                    trial = new(profile, works, median, failed);
                }
                catch (UserError e) { trial = new(profile, false, null, e.Message); }
                trials.Add(trial); progress(profile, trial);
            }
        }
        finally
        {
            var best = trials.Where(t => t.Works).OrderBy(t => t.MedianMs).FirstOrDefault();
            if (best is not null) { Settings.ZapretProfile = best.Profile; Save(); }
            try
            {
                if (Settings.ZapretEnabled) await ZapretCommandAsync(new("ZapretStart", Zapret: Settings.ZapretProfile, ZapretHosts: [.. VpnServerHosts]));
                else await ZapretCommandAsync(new("ZapretStop"));
            }
            catch (UserError e) { Banner = "zapret: " + e.Message; }
            Notify(nameof(ZapretSummary));
        }
        return trials;
    }
    internal async Task<string[]> ReadZapretListAsync(string name) => ((await ZapretCommandAsync(new("ZapretReadList", Zapret: name))).Message ?? "").Split('\n');
    internal async Task SaveZapretListAsync(string name, string[] lines)
    {
        await ZapretCommandAsync(new("ZapretSaveList", Zapret: name, ZapretList: lines));
        if (Settings.ZapretEnabled && ZapretState?.Running == true) await ApplyZapretAsync();
    }
}
