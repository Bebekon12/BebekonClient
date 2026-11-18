using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Bebekon.Core;

public sealed record NetBirdStatus(bool Installed, string State, string? PeerAddress, string? Version, string[] Networks)
{
    public bool Connected => PeerAddress is not null;
    public string[] ControlHosts { get; init; } = [];
}

/// <summary>Runs the registered NetBird CLI as the interactive user.</summary>
public static class NetBirdClient
{
    public static async Task<NetBirdStatus> GetStatusAsync(CancellationToken ct)
    {
        var executable = FindExecutable();
        if (executable is null) return new(false, "NotInstalled", null, null, []);
        try
        {
            var json = await WindowsNetworkCommand.ReadAsync(executable, ["status", "--json"], TimeSpan.FromSeconds(4), ct);
            return ParseStatus(json);
        }
        catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException ||
                                  e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(true, "Unavailable", null, null, []);
        }
    }

    public static async Task SetConnectedAsync(bool connected, CancellationToken ct)
    {
        var executable = FindExecutable() ?? throw new UserError("Исполняемый файл службы NetBird недоступен.");
        try
        {
            await WindowsNetworkCommand.ReadAsync(executable, [connected ? "up" : "down"], TimeSpan.FromMinutes(2), ct);
        }
        catch (Exception e) when (e is Win32Exception or IOException ||
                                  e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new UserError("NetBird не выполнил команду. Проверьте состояние в клиенте NetBird.");
        }
    }

    internal static NetBirdStatus ParseStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var state = root.TryGetProperty("daemonStatus", out var daemon) ? daemon.GetString() ?? "Unknown" : "Unknown";
        var address = root.TryGetProperty("netbirdIp", out var ip) ? ip.GetString()?.Split('/')[0] : null;
        var managementConnected = root.TryGetProperty("management", out var management) &&
            management.TryGetProperty("connected", out var connected) && connected.ValueKind == JsonValueKind.True;
        var peer = managementConnected && state == "Connected" && IPAddress.TryParse(address, out var parsed) ? parsed.ToString() : null;
        var version = root.TryGetProperty("daemonVersion", out var daemonVersion) ? daemonVersion.GetString() : null;
        var networks = root.TryGetProperty("networks", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Where(value => value.Length <= 253 && !value.Any(char.IsControl)).Take(512).ToArray()
            : [];
        return new(true, state, peer, version, networks)
        {
            ControlHosts = NormalizeControlHosts(new[] { "management", "signal" }.Select(service => ReadControlHost(root, service)))
        };
    }

    private static string? ReadControlHost(JsonElement root, string service)
    {
        if (!root.TryGetProperty(service, out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("url", out var endpoint) || endpoint.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.IdnHost;
    }

    internal static string[] NormalizeControlHosts(IEnumerable<string?>? hosts) =>
        (hosts ?? []).Take(2).OfType<string>().Select(host => host.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(host => host.Length is > 0 and <= 253 &&
                host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') &&
                Uri.CheckHostName(host) == UriHostNameType.Dns)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetBird");
            if (service?.GetValue("ImagePath") is not string image) return null;
            var command = image.Trim();
            var quoted = command.StartsWith('"');
            var end = quoted ? command.IndexOf('"', 1) : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
            if (end <= 1) return null;
            var path = command[(quoted ? 1 : 0)..end];
            return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                Path.GetFileName(path).Equals("netbird.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

internal static class WindowsNetworkCommand
{
    public static async Task<string> ReadAsync(string executable, string[] arguments, TimeSpan duration, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        timeout.Token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Could not start network diagnostics.");
        var drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            var output = new StringBuilder();
            var buffer = new char[4096];
            int length;
            while ((length = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + length > 1024 * 1024) throw new IOException("Network diagnostic output is too large.");
                output.Append(buffer, 0, length);
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException("Network diagnostic command failed.");
            return output.ToString();
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (InvalidOperationException) { }
            try { await drainErrors; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
}
