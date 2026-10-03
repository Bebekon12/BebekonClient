using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Bebekon.Core;

/// <summary>Registers the privileged helper once; the desktop UI keeps its ordinary user identity.</summary>
public static class ServiceInstaller
{
    private static readonly SemaphoreSlim installationGate = new(1);
    public static bool IsInstalled => Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BebekonVPN", "ImagePath", null) is string;
    private static bool CurrentHelperInstalled
    {
        get
        {
            var image = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BebekonVPN", "ImagePath", null) as string;
            if (string.IsNullOrWhiteSpace(image)) return false;
            try
            {
                var library = Path.ChangeExtension(image.Trim().Trim('"'), ".dll");
                Version.TryParse(FileVersionInfo.GetVersionInfo(library).FileVersion, out var installed);
                return !RequiresUpdate(typeof(ServiceInstaller).Assembly.GetName().Version!, installed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }
    public static bool RequiresUpdate(Version current, Version? installed) => installed is null || installed < current;

    public static async Task EnsureInstalledAsync(Action? installing = null, CancellationToken ct = default)
    {
        if (CurrentHelperInstalled) return;
        await installationGate.WaitAsync(ct);
        try
        {
            if (CurrentHelperInstalled) return;
            // Capture the UI owner's SID before UAC can switch to a different administrator account.
            var ownerSid = WindowsIdentity.GetCurrent().User!.Value;
            var start = CreateStartInfo(AppContext.BaseDirectory, ownerSid);
            installing?.Invoke();
            using var process = await Task.Run(() => Process.Start(start), ct)
                ?? throw new UserError("Не удалось запустить установку службы TUN.");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || !CurrentHelperInstalled ||
                !string.Equals(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\BebekonVPN", "OwnerSid", null) as string,
                    ownerSid, StringComparison.Ordinal))
                throw new UserError("Не удалось установить службу TUN. Запустите установщик Bebekon VPN.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new UserError("Установка службы TUN отменена. При подключении подтвердите запрос Windows на права администратора.");
        }
        finally { installationGate.Release(); }
    }

    public static ProcessStartInfo CreateStartInfo(string packageRoot, string ownerSid)
    {
        var sid = new SecurityIdentifier(ownerSid).Value;
        var root = Path.GetFullPath(packageRoot);
        var script = Path.Combine(root, "scripts", "install-service.ps1");
        if (!File.Exists(script) || !File.Exists(Path.Combine(root, "Bebekon.Service.exe")) ||
            !File.Exists(Path.Combine(root, "core", "sing-box.exe")))
            throw new UserError("Для TUN нужна полная папка приложения. Откройте Bebekon.App.exe из portable-сборки или установите Setup.");
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-OwnerSid", sid }
        };
    }
}
