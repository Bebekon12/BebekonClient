using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Bebekon.Core;

/// <summary>Stable app-specific HWID for subscription licensing; never sends the raw Windows identifier.</summary>
internal static class SubscriptionDeviceIdentity
{
    private static readonly Lazy<string> Header = new(ReadWindowsIdentity);
    public static string Get() => Header.Value;

    internal static string Derive(string machineGuid)
    {
        if (!Guid.TryParse(machineGuid, out var id) || id == Guid.Empty) throw Unavailable();
        // Keep this namespace stable across releases, reinstalls and changes of Windows account/elevation.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("BebekonVPN.subscription-device.v1:" + id.ToString("D")));
        return "win-" + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string ReadWindowsIdentity()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return Derive(key?.GetValue("MachineGuid") as string ?? "");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw Unavailable();
        }
    }

    private static UserError Unavailable() => new("Не удалось получить постоянный идентификатор устройства для подписки. Проверьте доступ к идентификатору Windows и повторите загрузку.");
}
