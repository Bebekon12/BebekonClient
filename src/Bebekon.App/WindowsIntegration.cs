using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Bebekon.App;
public static class AutoStart
{
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("BebekonVPN", "\"" + Environment.ProcessPath + "\" --tray"); else key.DeleteValue("BebekonVPN", false);
    }
}
public static class SystemProxy
{
    private sealed record Backup(int Enabled, string Server, string Override, int AutoDetect, string AutoConfig);
    private static string BackupFile => Path.Combine(Paths.UserRoot, "proxy-backup.dpapi");
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!File.Exists(BackupFile))
        {
            var backup = new Backup((int)(key.GetValue("ProxyEnable") ?? 0), (string)(key.GetValue("ProxyServer") ?? ""), (string)(key.GetValue("ProxyOverride") ?? ""), (int)(key.GetValue("AutoDetect") ?? 0), (string)(key.GetValue("AutoConfigURL") ?? ""));
            Directory.CreateDirectory(Paths.UserRoot); File.WriteAllBytes(BackupFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(backup), null, DataProtectionScope.CurrentUser));
        }
        key.SetValue("ProxyEnable", 1); key.SetValue("ProxyServer", "127.0.0.1:17890"); key.SetValue("ProxyOverride", "<local>"); key.SetValue("AutoDetect", 0); key.DeleteValue("AutoConfigURL", false); Refresh();
    }
    public static void Restore()
    {
        if (!File.Exists(BackupFile)) return;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(BackupFile), null, DataProtectionScope.CurrentUser); var b = JsonSerializer.Deserialize<Backup>(data)!;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath); key.SetValue("ProxyEnable", b.Enabled); key.SetValue("ProxyServer", b.Server); key.SetValue("ProxyOverride", b.Override); key.SetValue("AutoDetect", b.AutoDetect);
            if (b.AutoConfig.Length > 0) key.SetValue("AutoConfigURL", b.AutoConfig); else key.DeleteValue("AutoConfigURL", false); Refresh(); File.Delete(BackupFile);
        }
        catch { /* Keep encrypted backup for recovery; never overwrite it. */ }
    }
    private static void Refresh() { InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); }
    [DllImport("wininet.dll")] private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
}
public sealed record ApplicationEntry(string Name, string Path, string Group);
public static class ApplicationDiscovery
{
    public static List<ApplicationEntry> Find()
    {
        var apps = new Dictionary<string, ApplicationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) try { var path = p.MainModule?.FileName; if (path is not null && p.MainWindowHandle != IntPtr.Zero) apps[path] = new(p.MainWindowTitle.Length > 0 ? p.MainWindowTitle : p.ProcessName, path, "Запущенные"); } catch { }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var registryPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths" })
        {
            using var root = hive.OpenSubKey(registryPath); if (root is null) continue;
            foreach (var name in root.GetSubKeyNames()) { using var sub = root.OpenSubKey(name); var path = (sub?.GetValue(null) as string)?.Trim('"'); if (path is not null && File.Exists(path)) apps.TryAdd(path, new(System.IO.Path.GetFileNameWithoutExtension(path), path, "Установленные")); }
        }
        // Start Menu shortcuts cover per-user apps absent from App Paths, without scanning whole disks.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is not null)
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
                foreach (var link in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).Take(600))
                {
                    object? shortcut = null;
                    try { shortcut = shell.CreateShortcut(link); string target = ((dynamic)shortcut).TargetPath; if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) apps.TryAdd(target, new(System.IO.Path.GetFileNameWithoutExtension(link), target, "Установленные")); } catch { }
                    finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); }
                }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        return apps.Values.OrderBy(a => a.Group).ThenBy(a => a.Name).ToList();
    }
}
