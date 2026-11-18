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
        if (args is ["--clear-leak-protection"])
        {
            KillSwitch.ClearPersistent();
            return;
        }
        if (args is ["--test-wfp"])
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var checks = KillSwitch.TestLocalFilteringAsync(deadline.Token).GetAwaiter().GetResult();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(checks, Json.Options));
            Environment.ExitCode = checks.All(check => check.Outcome == WfpTestOutcome.Passed) ? 0 : 1;
            return;
        }
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
    private CoreSession? core;
    private KillSwitch? killSwitch;
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
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        if (!console) security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }) security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (console) security.AddAccessRule(new(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!console) ProtectedServiceStorage.Validate(root);
        // Apply the protected DACL atomically to a newly created directory.
        new DirectoryInfo(root).Create(security);
        if (!console) ProtectedServiceStorage.Validate(root);
        else if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(root).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("Reparse points are forbidden in service runtime storage.");
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
        return new(state, error, connectedAt, core?.Pid, state == ConnectionState.Connected ? traffic?.Snapshot : null,
            ReadProtection(), state == ConnectionState.Connected ? last?.NetBird?.InterfaceName : null);
    }
    private ProtectionStatus ReadProtection()
    {
        try
        {
            var persistent = console ? 0 : KillSwitch.PersistentBlockCount();
            return new(persistent + (killSwitch?.InstalledBlockCount() ?? 0), persistent);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(0, 0, "Не удалось прочитать состояние фильтров WFP.");
        }
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
                    WfpTestResult[]? test = null;
                    WfpBlockEvent[]? blockEvents = null;
                    switch (request.Operation)
                    {
                        case "GetStatus": break;
                        case "StopCore": StopCore(request.KeepBlocking); break;
                        case "ClearProtection": StopCore(); KillSwitch.ClearPersistent(); break;
                        case "TestProtection": test = await KillSwitch.TestLocalFilteringAsync(timeout.Token); break;
                        case "ProtectionEvents": blockEvents = KillSwitch.RecentPolicyBlocks(killSwitch); break;
                        case "ApplyStoredProtection":
                            await ApplyStoredProtectionAsync(request.Protection ?? throw new UserError("Не указана политика WFP."), timeout.Token);
                            break;
                        case "Shutdown": if (console) throw new UserError("Console helper must be stopped with Ctrl+C."); StopCore(); break;
                        case "StartCore": if (request.Spec is null) throw new UserError("Не указан сервер."); await StartCoreAsync(request.Spec, timeout.Token); break;
                        case "RestartCore": var spec = request.Spec ?? last ?? throw new UserError("Сначала выберите сервер."); await StartCoreAsync(spec, timeout.Token, restart: true); break;
                        case "ValidateConfig": if (request.Spec is null) throw new UserError("Не указан сервер."); await CoreSession.ValidateAsync(await ResolveNetBirdAsync(request.Spec, timeout.Token), CoreExe, root, timeout.Token); break;
                        case "GetLogs": var lines = Directory.GetFiles(root, "*.log").SelectMany(File.ReadLines).TakeLast(120); result = new(true, Status(), string.Join(Environment.NewLine, lines)); await PipeProtocol.WriteAsync(pipe, result, timeout.Token); continue;
                        default: throw new UserError("Неизвестная команда службы.");
                    }
                    result = new(true, Status(), WfpTest: test, BlockEvents: blockEvents);
                }
                catch (Exception e)
                {
                    var message = e is UserError ? e.Message : "Не удалось выполнить команду службы. Проверьте параметры подключения.";
                    if (request.Operation is ("StartCore" or "RestartCore") && (state != ConnectionState.Connected || core?.Running != true))
                    { StopCore(keepBlocking: true); state = ConnectionState.Error; error = message; }
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
    private string CoreExe => Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
    private static async Task<ConnectSpec> ResolveNetBirdAsync(ConnectSpec spec, CancellationToken ct)
    {
        RuleValidation.Validate(spec.Profile);
        if (spec.NetBirdPeer is null || !(spec.Profile.UsesNetBird || spec.Settings.KillSwitch || spec.Profile.HasProtectedRules)) return spec;
        return spec with { NetBird = await NetBirdRouting.DiscoverAsync(spec.NetBirdPeer, ct) };
    }
    private async Task StartCoreAsync(ConnectSpec spec, CancellationToken ct, bool restart = false)
    {
        if (!restart && core?.Running == true) throw new UserError("VPN уже подключён.");
        spec = await ResolveNetBirdAsync(spec, ct);
        var persistent = spec.Profile.HasProtectedRules || spec.Settings.KillSwitch && spec.Settings.PersistentKillSwitch;
        var global = spec.Settings.KillSwitch && spec.Settings.TunnelMode == TunnelMode.Tun;
        if (persistent && spec.Settings.TunnelMode != TunnelMode.Tun) throw new UserError("Для сохранённой защиты WFP требуется режим TUN.");
        if (persistent && console) throw new UserError("Для сохранённой защиты WFP требуется установленная служба.");
        if (persistent || global) ApplyProtection(spec, persistent, tunnelReady: false);
        if (restart) StopCore(keepBlocking: true);
        state = ConnectionState.Connecting; error = null; core?.Dispose();
        traffic?.Dispose(); traffic = new CoreTraffic();
        var path = Path.Combine(root, "sing-box.json"); core = new(CoreExe, coreLog);
        var launchedCore = core;
        core.Exited += () => { if (ReferenceEquals(core, launchedCore) && state is ConnectionState.Connected or ConnectionState.Connecting) { state = ConnectionState.Error; error = "Ядро VPN завершилось. Подключитесь заново."; connectedAt = null; log.Write("Core exited unexpectedly."); File.Delete(path); } };
        await core.StartAsync(spec, root, false, ct, traffic.AddToConfig);
        if (persistent || global) ApplyProtection(spec, persistent, tunnelReady: true);
        if (!persistent && !console && KillSwitch.PersistentBlockCount() > 0) KillSwitch.ClearPersistent();
        if (!global || persistent) ReleaseKillSwitch();
        traffic.Start(); last = spec; connectedAt = DateTimeOffset.UtcNow; state = ConnectionState.Connected;
    }
    private void ApplyProtection(ConnectSpec spec, bool persistent, bool tunnelReady)
    {
        var transports = KillSwitch.TrustedTransports(Path.Combine(AppContext.BaseDirectory, "core"));
        try
        {
            if (tunnelReady && !KillSwitch.TunnelInterfacePresent())
                throw new UserError("Windows не подтвердила интерфейс Bebekon для защиты WFP.");
            if (persistent)
            {
                if (KillSwitch.ApplyPersistent(spec, transports, tunnelReady) == 0)
                    throw new UserError("Windows не применила фильтры защиты.");
                return;
            }
            var updated = KillSwitch.Enable(transports, spec.NetBird, requireTunnelInterface: true, permitTunnel: tunnelReady);
            try
            {
                if (updated.InstalledBlockCount() != 4) throw new UserError("Windows не применила фильтры защиты.");
            }
            catch { updated.Dispose(); throw; }
            var old = killSwitch;
            killSwitch = updated;
            old?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            log.Write("WFP protection failed: " + e.GetType().Name);
            throw new UserError("Не удалось применить защиту WFP. Проверьте службу Base Filtering Engine и права службы VPN.");
        }
    }
    private async Task ApplyStoredProtectionAsync(StoredProtectionSpec spec, CancellationToken ct)
    {
        if (console || core?.Running == true) throw new UserError("Эта команда доступна установленной службе при отключённом Bebekon VPN.");
        RuleValidation.Validate(spec.Profile);
        if (!spec.Global && !spec.Profile.HasProtectedRules) throw new UserError("Сначала выберите правила защиты или сохраняемый Kill Switch.");
        var netBird = await NetBirdRouting.DiscoverAsync(spec.NetBirdPeer, ct);
        if (KillSwitch.ApplyPersistent(spec, netBird, KillSwitch.TrustedTransports(Path.Combine(AppContext.BaseDirectory, "core"))) == 0)
            throw new UserError("Windows не применила фильтры защиты.");
        ReleaseKillSwitch();
    }
    private void ReleaseKillSwitch() { if (killSwitch is null) return; killSwitch.Dispose(); killSwitch = null; log.Write("Leak protection disabled."); }
    private void StopCore(bool keepBlocking = false)
    {
        state = ConnectionState.Disconnecting; traffic?.Dispose(); traffic = null; core?.Dispose(); core = null; state = ConnectionState.Disconnected; connectedAt = null; error = null;
        if (!keepBlocking) ReleaseKillSwitch();
        File.Delete(Path.Combine(root, "sing-box.json")); File.Delete(Path.Combine(root, "xray.json")); log.Write("Core stopped; runtime config removed.");
    }
    private async Task IdleStopAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(15), ct); if (!console && killSwitch is null && state != ConnectionState.Connected && state != ConnectionState.Connecting && DateTimeOffset.UtcNow - lastCommand > TimeSpan.FromSeconds(45)) { Stop(); return; } } }
        catch (OperationCanceledException) { }
    }
}
