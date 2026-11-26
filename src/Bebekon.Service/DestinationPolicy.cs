using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bebekon.Core;

namespace Bebekon.Service;

internal sealed class DestinationPolicy(string root)
{
    private string FileName => Path.Combine(root, "destination-guard.json");
    private sealed record SavedPolicy(string[] Entries, bool Active);
    public DestinationGuard Saved { get; private set; } = DestinationGuard.Empty;
    public bool Active { get; private set; }
    public DestinationGuard Current => Active ? Saved : DestinationGuard.Empty;
    public string? Error { get; private set; }
    public Task ConfirmDnsAsync(CancellationToken ct) => Current.Domains.Length == 0 ? Task.CompletedTask : SetDnsAsync(Current.Domains, ct);

    public async Task RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(FileName)) return;
        try
        {
            var data = JsonSerializer.Deserialize<SavedPolicy>(await File.ReadAllBytesAsync(FileName, ct)) ?? throw new InvalidDataException();
            Saved = DestinationGuard.Parse(data.Entries);
            Active = data.Active && Saved.Enabled;
            await InstallAsync(Current, Current, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { Error = "Не удалось подтвердить ранее сохранённую защиту адресов."; }
    }

    public async Task SetAsync(DestinationGuard saved, bool active, bool tunnelReady, CancellationToken ct)
    {
        var previous = Current;
        var next = active ? saved : DestinationGuard.Empty;
        // Leave a recovery marker before the first system mutation. A restart replays the last committed policy.
        if (!File.Exists(FileName)) Save(Saved, Active);
        try
        {
            await InstallAsync(previous, next, ct);
            Save(saved, active); Saved = saved; Active = active && saved.Enabled; Error = null;
        }
        catch
        {
            Error = "Изменения защиты адресов не применены.";
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await InstallAsync(next, previous, recovery.Token);
                if (previous.Enabled && tunnelReady) KillSwitch.ApplyDestinationFilters(previous.FilterNetworks, true);
                Error = null;
            }
            catch { Error = "Требуется повторное применение защиты адресов. Проверьте правила DNS Windows."; }
            throw new UserError(Error ?? "Изменения не применены. Предыдущая политика восстановлена. Проверьте DNS-политики Windows.");
        }
    }

    private void Save(DestinationGuard guard, bool active)
    {
        var temporary = FileName + ".new";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new SavedPolicy(guard.Entries, active)));
        File.Move(temporary, FileName, true);
    }

    private static async Task InstallAsync(DestinationGuard previous, DestinationGuard next, CancellationToken ct)
    {
        // Block both scopes before changing DNS. No interface permission during a transition.
        KillSwitch.ApplyDestinationFilters(previous.FilterNetworks.Concat(next.FilterNetworks), false);
        await SetDnsAsync(next.Domains, ct);
        KillSwitch.ApplyDestinationFilters(next.FilterNetworks, false);
    }

    public static async Task RemoveAsync(string root, CancellationToken ct)
    {
        await ClearSystemPolicyAsync(ct);
        File.Delete(Path.Combine(root,"destination-guard.json"));
    }

    internal static async Task ClearSystemPolicyAsync(CancellationToken ct)
    {
        await SetDnsAsync([], ct);
        KillSwitch.ClearDestinationFilters();
    }

    private static async Task SetDnsAsync(string[] domains, CancellationToken ct)
    {
        using var stream = typeof(DestinationPolicy).Assembly.GetManifestResourceStream("Bebekon.DestinationDns")!;
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new UserError("Не удалось настроить DNS Windows.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await child.StandardInput.WriteAsync(JsonSerializer.Serialize(new { domains }));
            child.StandardInput.Close();
            await child.WaitForExitAsync(limit.Token);
            await Task.WhenAll(output, error);
            if (child.ExitCode != 0) throw new UserError("Windows не подтвердила DNS-политику. Проверьте конфликты с корпоративными правилами.");
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
}
