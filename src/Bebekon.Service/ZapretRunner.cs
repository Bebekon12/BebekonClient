using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Bebekon.Core;

namespace Bebekon.Service;

/// <summary>
/// Runs zapret (winws + WinDivert) for the owner. Only a copy imported by an administrator into the
/// helper's protected storage is ever executed, after its hashes match the import manifest; user
/// folders are never run by the helper. The process lives in a kill-on-close job.
/// </summary>
internal sealed class ZapretRunner(string root, SafeLog log) : IDisposable
{
    private readonly string home = Path.Combine(root, "zapret");
    private const string Marker = "# bebekon-vpn: VPN servers (managed automatically)";
    private Process? process;
    private IntPtr job;
    private FileStream? activePackage;
    private string? running;
    private string? error;
    private string[] excluded = [];
    private readonly object gate = new();
    private string Bin => Path.Combine(home, "bin");
    private string Lists => Path.Combine(home, "lists");
    private string Profiles => Path.Combine(home, "profiles");
    private string Manifest => Path.Combine(home, "manifest.json");
    public bool Running { get { lock (gate) return process is { HasExited: false }; } }

    private sealed record ImportManifest(DateTimeOffset Imported, Dictionary<string, string> Hashes);

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    // Explicit ownership also works on machines where elevated files default to the individual user.
    private static void SetImportAcl(FileSystemSecurity acl, InheritanceFlags inherit)
    {
        acl.SetOwner(Administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
    }

    private static FileStream OpenProtectedFile(string path, FileMode mode, FileShare share)
    {
        var acl = new FileSecurity();
        SetImportAcl(acl, InheritanceFlags.None);
        return new FileInfo(path).Create(mode, FileSystemRights.Read | FileSystemRights.Write,
            share, 4096, FileOptions.None, acl);
    }

    internal static FileStream LockImport(string root) =>
        OpenProtectedFile(Path.Combine(root, "zapret-import.lock"), FileMode.OpenOrCreate, FileShare.Delete);

    private static void RestorePreviousImport(string home)
    {
        if (!Directory.Exists(home) && Directory.Exists(home + ".old"))
            Directory.Move(home + ".old", home);
    }

    internal static void RecoverImport(string root)
    {
        var home = Path.Combine(root, "zapret");
        if (Directory.Exists(home) || !Directory.Exists(home + ".old")) return;
        try
        {
            using var lease = LockImport(root);
            RestorePreviousImport(home);
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { /* An active import owns the swap. */ }
    }

    /// <summary>Elevated, one-time: copies a zapret folder into protected storage. Run as "--import-zapret &lt;folder&gt;".</summary>
    public static void Import(string root, string source)
    {
        if (!Path.IsPathFullyQualified(source) || source.StartsWith(@"\\")) throw new UserError("Выберите локальную папку zapret.");
        source = Path.GetFullPath(source);
        var bin = Path.Combine(source, "bin"); var lists = Path.Combine(source, "lists");
        foreach (var path in new[] { bin, lists })
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new UserError("Выберите папку zapret без перенаправлений.");
        foreach (var required in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
            if (!File.Exists(Path.Combine(bin, required))) throw new UserError("В папке нет bin\\" + required + ".");
        if (!Directory.Exists(lists)) throw new UserError("В папке нет lists.");
        using var lease = LockImport(root);
        var home = Path.Combine(root, "zapret");
        var staging = home + ".new";
        var previous = home + ".old";
        RestorePreviousImport(home);
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        try
        {
            BuildImport(source, bin, lists, staging);
            if (Directory.Exists(home)) Directory.Move(home, previous);
            try { Directory.Move(staging, home); }
            catch { RestorePreviousImport(home); throw; }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
    }

    private static void BuildImport(string source, string bin, string lists, string staging)
    {
        var acl = new DirectorySecurity();
        SetImportAcl(acl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit);
        new DirectoryInfo(staging).Create(acl);
        foreach (var part in new[] { "bin", "lists", "profiles" })
            new DirectoryInfo(Path.Combine(staging, part)).Create(acl);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Copy(string from, string to, long limit)
        {
            var info = new FileInfo(from);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > limit) throw new UserError("Файл не подходит: " + info.Name);
            using var input = File.OpenRead(from);
            using var output = OpenProtectedFile(to, FileMode.CreateNew, FileShare.None);
            input.CopyTo(output);
        }
        foreach (var file in Directory.GetFiles(bin))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".exe" or ".dll" or ".sys" or ".bin")) continue;
            var target = Path.Combine(staging, "bin", Path.GetFileName(file));
            Copy(file, target, 64L * 1024 * 1024);
            if (ext is ".exe" or ".dll" or ".sys") hashes[Path.GetFileName(file)] = Hash(target);
        }
        foreach (var file in Directory.GetFiles(lists, "*.txt")) Copy(file, Path.Combine(staging, "lists", Path.GetFileName(file)), 16L * 1024 * 1024);
        var profiles = 0;
        foreach (var file in Directory.GetFiles(source, "general*.bat").Where(f => Zapret.ProfileName().IsMatch(Path.GetFileName(f))))
        {
            var copy = Path.Combine(staging, "profiles", Path.GetFileName(file));
            Copy(file, copy, 512 * 1024);
            try { Zapret.Parse(Path.GetFileName(file), File.ReadAllText(copy), Path.Combine(staging, "bin"), Path.Combine(staging, "lists")); }
            catch (UserError) { File.Delete(copy); continue; }
            profiles++;
        }
        if (profiles == 0) throw new UserError("В папке нет поддерживаемых профилей general*.bat.");
        // Same defaults as service.bat: user lists must exist and never be empty.
        void WriteNew(string path, string content)
        {
            using var writer = new StreamWriter(OpenProtectedFile(path, FileMode.CreateNew, FileShare.None));
            writer.Write(content);
        }
        void Default(string name, string content) { var path = Path.Combine(staging, "lists", name); if (!File.Exists(path)) WriteNew(path, content); }
        Default("ipset-exclude-user.txt", "203.0.113.113/32\r\n"); Default("list-general-user.txt", "domain.example.abc\r\n"); Default("list-exclude-user.txt", "domain.example.abc\r\n");
        Default("ipset-all.txt", "");
        WriteNew(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new ImportManifest(DateTimeOffset.UtcNow, hashes), Json.Options));
    }

    public ZapretStatus Status()
    {
        string[] names = []; string[] summaries = [];
        DateTimeOffset? imported = null;
        try
        {
            if (!File.Exists(Manifest)) return new(false, false, null, null);
            imported = JsonSerializer.Deserialize<ImportManifest>(File.ReadAllText(Manifest), Json.Options)?.Imported;
            var parsed = Directory.GetFiles(Profiles, "*.bat").Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => { try { return Load(n); } catch (UserError) { return null; } }).OfType<ZapretProfile>().ToArray();
            names = [.. parsed.Select(p => p.Name)]; summaries = [.. parsed.Select(p => p.NeedsTcpTimestamps
                ? p.Summary + ". Профиль использует системную настройку TCP timestamps." : p.Summary)];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(true, Running, running, "Не удалось прочитать импорт zapret. Импортируйте папку заново.");
        }
        lock (gate) return new(File.Exists(Manifest), process is { HasExited: false }, running, error, names, summaries, imported);
    }

    private ZapretProfile Load(string name)
    {
        if (!Zapret.ProfileName().IsMatch(name)) throw new UserError("Неизвестный профиль zapret.");
        var path = Path.Combine(Profiles, name);
        if (!File.Exists(path)) throw new UserError("Профиль не найден. Импортируйте папку zapret заново.");
        return Zapret.Parse(name, File.ReadAllText(path), Bin, Lists);
    }

    public async Task StartAsync(string profile, IEnumerable<string> vpnHosts, CancellationToken ct)
    {
        Stop();
        activePackage = LockImport(root);
        try { await StartProcessAsync(profile, vpnHosts, ct); }
        catch { Stop(); throw; }
    }

    private async Task StartProcessAsync(string profile, IEnumerable<string> vpnHosts, CancellationToken ct)
    {
        if (!File.Exists(Manifest)) throw new UserError("Сначала импортируйте папку zapret.");
        var parsed = Load(profile);
        var manifest = JsonSerializer.Deserialize<ImportManifest>(File.ReadAllText(Manifest), Json.Options);
        if (manifest?.Hashes == null || !new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" }.All(
            required => manifest.Hashes.Keys.Contains(required, StringComparer.OrdinalIgnoreCase)))
            throw new UserError("Импорт zapret повреждён. Импортируйте папку заново.");
        foreach (var (file, hash) in manifest.Hashes)
            if (Path.GetFileName(file) != file || file.Contains(':') || !File.Exists(Path.Combine(Bin, file)) || Hash(Path.Combine(Bin, file)) != hash) throw new UserError("Файлы zapret изменены. Импортируйте папку заново.");
        var foreign = Process.GetProcessesByName("winws");
        if (foreign.Length > 0) { foreach (var p in foreign) p.Dispose(); throw new UserError("Уже работает другой zapret (general.bat или служба zapret). Остановите его, и Bebekon запустит свой."); }
        excluded = await ExcludeAsync(vpnHosts, ct);
        var output = new StringBuilder();
        lock (gate)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            var limit = new JobInfo(); limit.Basic.LimitFlags = 0x2000;
            if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<JobInfo>())) throw new UserError("Не удалось подготовить запуск zapret.");
            var info = new ProcessStartInfo(Path.Combine(Bin, "winws.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Bin, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in parsed.Arguments) info.ArgumentList.Add(arg);
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line && output.Length < 4000) lock (output) output.AppendLine(line); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line && output.Length < 4000) lock (output) output.AppendLine(line); };
            var mine = process;
            process.Exited += (_, _) => { lock (gate) if (ReferenceEquals(process, mine) && running is not null) { error = "zapret завершился. Проверьте профиль или запустите подбор."; log.Write("zapret exited."); } };
            process.Start();
            if (!AssignProcessToJobObject(job, process.Handle)) { Stop(); throw new UserError("Не удалось привязать процесс zapret."); }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            running = profile; error = null;
        }
        // winws opens WinDivert at start; a wrong driver or a conflict exits within a moment.
        await Task.Delay(1500, ct);
        if (!Running)
        {
            string detail; lock (output) detail = output.ToString();
            Stop();
            log.Write("zapret failed to start.");
            throw new UserError(detail.Contains("WinDivert", StringComparison.OrdinalIgnoreCase)
                ? "zapret не смог открыть драйвер WinDivert. Возможно, его блокирует антивирус или уже работает другой обход."
                : "zapret завершился сразу после запуска. Попробуйте другой профиль.");
        }
        log.Write("zapret started.");
    }

    /// <summary>Keeps the VPN servers out of zapret: their connections must never be split or faked.</summary>
    public async Task<bool> EnsureExcludedAsync(IEnumerable<string> vpnHosts, CancellationToken ct)
    {
        if (!Running || running is not { } profile) return false;
        var addresses = await ResolveAsync(vpnHosts, ct);
        if (addresses.All(excluded.Contains)) return false;
        await StartAsync(profile, vpnHosts, ct);
        return true;
    }

    private async Task<string[]> ExcludeAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var addresses = await ResolveAsync(hosts, ct);
        var path = Path.Combine(Lists, "ipset-exclude-user.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var start = lines.IndexOf(Marker);
        if (start >= 0) lines.RemoveRange(start, lines.Count - start);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) lines.Add("203.0.113.113/32");
        if (addresses.Length > 0) { lines.Add(Marker); lines.AddRange(addresses); }
        await File.WriteAllLinesAsync(path, lines, ct);
        return addresses;
    }
    private static async Task<string[]> ResolveAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(256))
        {
            try
            {
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                foreach (var address in addresses) result.Add(address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or TimeoutException or ArgumentException) { }
        }
        return [.. result];
    }

    public void SaveList(string name, IEnumerable<string> lines)
    {
        if (!File.Exists(Manifest)) throw new UserError("Сначала импортируйте папку zapret.");
        var valid = Zapret.ValidateList(name, lines);
        // Keep the managed VPN block when the user edits their exclusions.
        if (name == "ipset-exclude-user.txt" && excluded.Length > 0) valid = [.. valid.TakeWhile(l => l != Marker), Marker, .. excluded];
        File.WriteAllLines(Path.Combine(Lists, name), valid);
    }
    public string[] ReadList(string name)
    {
        if (!Zapret.UserLists.Contains(name)) throw new UserError("Этот список нельзя открыть.");
        var path = Path.Combine(Lists, name);
        return File.Exists(path) ? [.. File.ReadAllLines(path).TakeWhile(l => l != Marker)] : [];
    }

    public void Stop()
    {
        Process? child; IntPtr owned;
        lock (gate) { child = process; process = null; owned = job; job = IntPtr.Zero; running = null; }
        if (owned != IntPtr.Zero) CloseHandle(owned);
        try
        {
            if (child is not null) { try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(3000); } } catch (InvalidOperationException) { } finally { child.Dispose(); } log.Write("zapret stopped."); }
        }
        finally { activePackage?.Dispose(); activePackage = null; }
    }
    public void Dispose() => Stop();

    private static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }

    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long PerProcessTime, PerJobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoInfo { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobInfo { public BasicInfo Basic; public IoInfo Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobInfo info, uint length);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
