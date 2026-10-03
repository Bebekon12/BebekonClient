using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

public static class Paths
{
    public static string UserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BebekonVPN");
    public static string Logs => Path.Combine(UserRoot, "logs");
}
public sealed class StateStore(string? root = null)
{
    public string Root { get; } = root ?? Paths.UserRoot;
    public AppState Load()
    {
        var path = Path.Combine(Root, "state.dpapi"); if (!File.Exists(path)) return new();
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { var state = JsonSerializer.Deserialize<AppState>(data, Json.Options) ?? throw new JsonException(); if (state.SchemaVersion != 1 || state.Profiles.Count == 0) throw new JsonException(); foreach (var p in state.Profiles) RuleValidation.Validate(p); return state; }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or UserError) { throw new UserError("Не удалось прочитать защищённые настройки. Сохраните state.dpapi перед восстановлением."); }
    }
    public void Save(AppState state)
    {
        Directory.CreateDirectory(Root);
        var data = JsonSerializer.SerializeToUtf8Bytes(state, Json.Options);
        try { var protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser); var path = Path.Combine(Root, "state.dpapi"); File.WriteAllBytes(path + ".new", protectedData); File.Move(path + ".new", path, true); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    public void SaveRuntime(string config)
    {
        var dir = Path.Combine(Root, "runtime"); Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "sing-box.dpapi"), ProtectedData.Protect(Encoding.UTF8.GetBytes(config), null, DataProtectionScope.CurrentUser));
    }
}
public sealed class SafeLog(string directory, string name)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path = Path.GetFullPath(Path.Combine(directory, name + ".log"));
    public void Write(string message)
    {
        // Parallel probe cores use different SafeLog instances for the same file.
        lock (Gates.GetOrAdd(path, static _ => new()))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) { for (var i = 2; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true); File.Move(path, path + ".1", true); }
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + Redact(message) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging is best effort: an external lock/full disk must never
                // throw out of a process-output callback and terminate the app.
            }
        }
    }
    public static string Redact(string message)
    {
        message = Regex.Replace(message, @"(?i)(https?://|vless://)\S+", "[private-link]");
        message = Regex.Replace(message, @"(?i)\b[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\b", "[id]");
        message = Regex.Replace(message, @"(?i)(uuid|password|public_key|short_id|token)\s*[:=]\s*[^\s,]+", "$1=[secret]");
        return message.Length > 2000 ? message[..2000] : message;
    }
}
