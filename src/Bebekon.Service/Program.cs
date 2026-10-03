using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Bebekon.Core;
using Microsoft.Win32;

namespace Bebekon.Service;

internal static class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--console"))
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            using var host = new VpnService(sid, true); host.RunConsole(); return;
        }
        var owner = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\BebekonVPN", "OwnerSid", null) as string;
        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("Authorized owner SID was not installed.");
        ServiceBase.Run(new VpnService(owner));
    }
}
internal sealed class VpnService : ServiceBase
{
    private readonly SecurityIdentifier owner;
    private readonly string root;
    private readonly string pipeName;
    private readonly SafeLog log;
    private readonly SafeLog coreLog;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private CoreProcess? core;
    private CoreTraffic? traffic;
    private ConnectSpec? last;
    private volatile ConnectionState state;
    private string? error;
    private DateTimeOffset? connectedAt;
    private DateTimeOffset lastCommand = DateTimeOffset.UtcNow;
    private readonly bool console;
    public VpnService(string ownerSid, bool console = false)
    {
        ServiceName = PipeProtocol.ServiceName; CanShutdown = true; AutoLog = false;
        owner = new(ownerSid); this.console = console;
        pipeName = console ? PipeProtocol.PipeName + ".test." + Environment.ProcessId : PipeProtocol.PipeName;
        root = console ? Path.Combine(Paths.UserRoot, "helper-test", Environment.ProcessId.ToString()) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BebekonVPN");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(root).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("Reparse points are forbidden in service runtime storage.");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        if (!console) security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }) security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (console) security.AddAccessRule(new(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var fileSecurity = new FileSecurity(); fileSecurity.SetAccessRuleProtection(true, false);
            if (!console) fileSecurity.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }) fileSecurity.AddAccessRule(new(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            if (console) fileSecurity.AddAccessRule(new(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(fileSecurity);
        }
        log = new(root, "service"); coreLog = new(root, "core");
    }
    protected override void OnStart(string[] args) { _ = ServeAsync(lifetime.Token); _ = IdleStopAsync(lifetime.Token); log.Write("Service started."); }
    public void RunConsole() { OnStart([]); Console.WriteLine("Bebekon helper; Ctrl+C to stop."); using var done = new ManualResetEventSlim(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); }; done.Wait(); OnStop(); }
    protected override void OnStop() { lifetime.Cancel(); gate.Wait(); try { StopCore(); } finally { gate.Release(); } log.Write("Service stopped."); }
    protected override void OnShutdown() => OnStop();
    private ServiceStatus Status()
    {
        // Exited notification can arrive after a status request. Check before snapshotting.
        if (state == ConnectionState.Connected && core?.Running != true) { state = ConnectionState.Error; error = "Ядро VPN завершилось. Подключитесь заново."; connectedAt = null; File.Delete(Path.Combine(root, "sing-box.json")); }
        return new(state, error, connectedAt, core?.Pid, state == ConnectionState.Connected ? traffic?.Snapshot : null);
    }
    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                acl.AddAccessRule(new(owner, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, acl);
                await pipe.WaitForConnectionAsync(ct);
                var authorized = false;
                pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); authorized = identity.User == owner; });
                if (!authorized) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await PipeProtocol.ReadAsync<ServiceRequest>(pipe, timeout.Token);
                await gate.WaitAsync(timeout.Token);
                ServiceResponse result;
                try
                {
                    lastCommand = DateTimeOffset.UtcNow;
                    switch (request.Operation)
                    {
                        case "GetStatus": break;
                        case "StopCore": StopCore(); break;
                        case "Shutdown": if (console) throw new UserError("Console helper must be stopped with Ctrl+C."); StopCore(); break;
                        case "StartCore": if (request.Spec is null) throw new UserError("Не указан сервер."); await StartCoreAsync(request.Spec, timeout.Token); break;
                        case "RestartCore": var spec = request.Spec ?? last ?? throw new UserError("Сначала выберите сервер."); StopCore(); await StartCoreAsync(spec, timeout.Token); break;
                        case "ValidateConfig": if (request.Spec is null) throw new UserError("Не указан сервер."); var checkPath = Path.Combine(root, "validate.json"); try { await File.WriteAllTextAsync(checkPath, ConfigGenerator.Generate(request.Spec), timeout.Token); using var checker = NewCore(); await checker.ValidateAsync(checkPath, timeout.Token); } finally { File.Delete(checkPath); } break;
                        case "GetLogs": var lines = Directory.GetFiles(root, "*.log").SelectMany(File.ReadLines).TakeLast(120); result = new(true, Status(), string.Join(Environment.NewLine, lines)); await PipeProtocol.WriteAsync(pipe, result, timeout.Token); continue;
                        default: throw new UserError("Неизвестная команда службы.");
                    }
                    result = new(true, Status());
                }
                catch (Exception e)
                {
                    var message = e is UserError ? e.Message : "Не удалось выполнить команду службы. Проверьте параметры подключения.";
                    if (request.Operation is "StartCore" or "RestartCore") { StopCore(); state = ConnectionState.Error; error = message; }
                    log.Write("Command failed: " + request.Operation + "; " + e.GetType().Name); result = new(false, Status(), message);
                }
                finally { gate.Release(); }
                await PipeProtocol.WriteAsync(pipe, result, timeout.Token);
                if (request.Operation == "Shutdown" && result.Ok) { _ = Task.Run(Stop); break; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.Write("IPC interrupted: " + e.GetType().Name); await Task.Delay(200, ct); }
        }
    }
    private CoreProcess NewCore() => new(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), coreLog);
    private async Task StartCoreAsync(ConnectSpec spec, CancellationToken ct)
    {
        if (core?.Running == true) throw new UserError("VPN уже подключён.");
        state = ConnectionState.Connecting; error = null; core?.Dispose();
        traffic?.Dispose(); traffic = new CoreTraffic();
        var config = traffic.AddToConfig(ConfigGenerator.Generate(spec)); var path = Path.Combine(root, "sing-box.json");
        await File.WriteAllTextAsync(path, config, ct); core = NewCore();
        var launchedCore = core;
        core.Exited += () => { if (ReferenceEquals(core, launchedCore) && state is ConnectionState.Connected or ConnectionState.Connecting) { state = ConnectionState.Error; error = "Ядро VPN завершилось. Подключитесь заново."; connectedAt = null; log.Write("Core exited unexpectedly."); File.Delete(path); } };
        await core.StartAsync(path, ct); traffic.Start(); last = spec; connectedAt = DateTimeOffset.UtcNow; state = ConnectionState.Connected;
    }
    private void StopCore()
    {
        state = ConnectionState.Disconnecting; traffic?.Dispose(); traffic = null; core?.Dispose(); core = null; state = ConnectionState.Disconnected; connectedAt = null; error = null;
        File.Delete(Path.Combine(root, "sing-box.json")); log.Write("Core stopped; runtime config removed.");
    }
    private async Task IdleStopAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(15), ct); if (!console && state != ConnectionState.Connected && state != ConnectionState.Connecting && DateTimeOffset.UtcNow - lastCommand > TimeSpan.FromSeconds(45)) { Stop(); return; } } }
        catch (OperationCanceledException) { }
    }
}
