using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bebekon.Core;

/// <summary>Child cores are in a kill-on-close job: an app/service crash cannot orphan a TUN core.</summary>
public sealed class CoreProcess : IDisposable
{
    private readonly string executable;
    private readonly SafeLog log;
    private readonly bool xray;
    private Process? process;
    private readonly object stopGate = new();
    private IntPtr job;
    private bool disposed;
    private TaskCompletionSource ready = NewReady();
    public event Action? Exited;
    public bool Running { get { lock (stopGate) return process is { HasExited: false }; } }
    public int? Pid { get { lock (stopGate) return process is { HasExited: false } p ? p.Id : null; } }
    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CoreProcess(string executable, SafeLog log, bool xray = false) { this.executable = executable; this.log = log; this.xray = xray; }
    public async Task ValidateAsync(string config, CancellationToken ct)
    {
        if (!File.Exists(executable)) throw new UserError("Ядро VPN отсутствует. Переустановите приложение.");
        using var p = new Process { StartInfo = Info("check", config) };
        p.Start();
        var output = p.StandardOutput.ReadToEndAsync(ct); var error = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(timeout.Token); await output; await error; if (p.ExitCode != 0) throw new UserError("Ядро отклонило параметры подключения. Проверьте сервер и правила."); }
        finally { if (!p.HasExited) p.Kill(true); }
    }
    public async Task StartAsync(string config, CancellationToken ct)
    {
        if (Running) throw new UserError("Подключение уже запущено.");
        await ValidateAsync(config, ct); ready = NewReady();
        ct.ThrowIfCancellationRequested();
        lock (stopGate)
        {
        if (disposed) throw new UserError("Подключение остановлено.");
        job = Native.CreateJobObject(IntPtr.Zero, null);
        var limit = new Native.JobInfo(); limit.Basic.LimitFlags = 0x2000;
        if (job == IntPtr.Zero || !Native.SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<Native.JobInfo>())) { Dispose(); throw new UserError("Не удалось создать безопасный процесс ядра."); }
        process = new Process { StartInfo = Info("run", config), EnableRaisingEvents = true };
        process.Exited += (_, _) => { ready.TrySetException(new UserError("Ядро завершилось при подключении.")); Exited?.Invoke(); };
        process.OutputDataReceived += OnOutput; process.ErrorDataReceived += OnOutput;
        process.Start();
        if (!Native.AssignProcessToJobObject(job, process.Handle)) { Stop(); throw new UserError("Не удалось привязать процесс ядра."); }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await ready.Task.WaitAsync(timeout.Token); if (!Running) throw new UserError("Ядро завершилось."); }
        catch { Stop(); throw; }
    }
    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        if (e.Data.Contains("sing-box started", StringComparison.OrdinalIgnoreCase) || xray && e.Data.Contains("Xray ", StringComparison.OrdinalIgnoreCase) && e.Data.Contains(" started", StringComparison.OrdinalIgnoreCase)) { log.Write("Core started."); ready.TrySetResult(); }
        // Core output can contain destinations and private connection details. Do not persist raw lines.
        else if (e.Data.Contains("FATAL", StringComparison.OrdinalIgnoreCase)) { log.Write("Core fatal error. Connection parameters withheld."); ready.TrySetException(new UserError("Ядро не смогло запуститься. Возможен конфликт сетевых настроек или порта.")); }
        else if (e.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) log.Write("Core reported a network error (details withheld).");
    }
    private ProcessStartInfo Info(string command, string config)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.ArgumentList.Add(xray ? "run" : command); if (xray && command == "check") info.ArgumentList.Add("-test"); info.ArgumentList.Add(xray ? "-config" : "-c"); info.ArgumentList.Add(config); return info;
    }
    public void Stop()
    {
        Process? child; IntPtr ownedJob;
        lock (stopGate) { child = process; process = null; ownedJob = job; job = IntPtr.Zero; }
        if (ownedJob != IntPtr.Zero) Native.CloseHandle(ownedJob);
        if (child is not null) { try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); } } catch (InvalidOperationException) { } child.Dispose(); }
    }
    public void Dispose() { lock (stopGate) disposed = true; Stop(); }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct BasicInfo { public long PerProcessTime, PerJobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] public struct IoInfo { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] public struct JobInfo { public BasicInfo Basic; public IoInfo Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobInfo info, uint length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    }
}
