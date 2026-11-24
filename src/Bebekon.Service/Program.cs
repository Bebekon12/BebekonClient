using System.IO.Pipes;
using System.Globalization;
using System.Net;
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BebekonVPN");
            ProtectedServiceStorage.Validate(path);
            if (File.Exists(Path.Combine(path, "destination-guard.json")) || KillSwitch.DestinationBlockCount() != 0)
                DestinationPolicy.RemoveAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            KillSwitch.ClearPersistent();
            return;
        }
        if (args is ["--import-zapret", var source])
        {
            // Elevated by the app through UAC: an administrator copies zapret into protected storage once.
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BebekonVPN");
            try
            {
                ProtectedServiceStorage.Validate(root);
                if (!Directory.Exists(root)) throw new UserError("Служба Bebekon ещё не запускалась.");
                ZapretRunner.Import(root, source);
            }
            catch (Exception e) when (e is UserError or IOException or UnauthorizedAccessException)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "bebekon-zapret-import.txt"), e is UserError ? e.Message : "Не удалось скопировать папку zapret.");
                Environment.ExitCode = 1;
            }
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
        if (args.Length > 0 && args[0] == "--test-netbird")
        {
            Environment.ExitCode = TestNetBirdAsync(args).GetAwaiter().GetResult();
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

    private static async Task<int> TestNetBirdAsync(string[] args)
    {
        var port = 443;
        if (args.Length is < 2 or > 3 || args.Length == 3 && (!int.TryParse(args[2], out port) || port is < 1 or > 65535))
        {
            Console.Error.WriteLine("Укажите --test-netbird <домен или IP> [TCP-порт].");
            return 2;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var status = await NetBirdClient.GetStatusAsync(deadline.Token);
            if (!status.Connected)
            {
                Console.Error.WriteLine("Для проверки нужен подключённый NetBird.");
                return 1;
            }
            var routing = await NetBirdRouting.DiscoverAsync(status.PeerAddress, deadline.Token);
            if (routing is null) throw new UserError("Windows не подтвердила интерфейс NetBird.");
            var domain = IPAddress.TryParse(args[1], out var address) ? null : new IdnMapping().GetAscii(RuleValidation.SiteHost(args[1]));
            if (domain is not null && Uri.CheckHostName(domain) != UriHostNameType.Dns)
                throw new UserError("Укажите корректное доменное имя или IP.");
            var report = await NetBirdProbe.VerifyAsync(routing, new(domain, address, Port: port), deadline.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                report.InterfaceName,
                Resolver = report.Resolver?.ToString(),
                Reached = report.Reached?.ToString(),
                report.Port
            }, Json.Options));
            return report.Reached is null ? 1 : 0;
        }
        catch (UserError error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (Exception error) when (error is ArgumentException or OperationCanceledException or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException)
        {
            Console.Error.WriteLine("Проба NetBird не завершилась.");
            return 1;
        }
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
    private readonly DestinationPolicy destinations;
    private readonly ZapretRunner zapret;
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
        destinations = new(root);
        log = new(root, "service"); coreLog = new(root, "core");
        zapret = new(root, log);
    }
    protected override void OnStart(string[] args) { _ = ServeAsync(lifetime.Token); _ = IdleStopAsync(lifetime.Token); log.Write("Service started."); }
    public void RunConsole() { OnStart([]); Console.WriteLine("Bebekon helper; Ctrl+C to stop."); using var done = new ManualResetEventSlim(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); }; done.Wait(); OnStop(); }
    protected override void OnStop() { lifetime.Cancel(); gate.Wait(); try { StopCore(); zapret.Stop(); } finally { gate.Release(); } log.Write("Service stopped."); }
    protected override void OnShutdown() => OnStop();
    private ServiceStatus Status()
    {
        // Exited notification can arrive after a status request. Check before snapshotting.
        if (state == ConnectionState.Connected && core?.Running != true) { state = ConnectionState.Error; error = "Ядро VPN завершилось. Подключитесь заново."; connectedAt = null; File.Delete(Path.Combine(root, "sing-box.json")); }
        return new(state, error, connectedAt, core?.Pid, state == ConnectionState.Connected ? traffic?.Snapshot : null,
            ReadProtection(), state == ConnectionState.Connected ? last?.NetBird?.InterfaceName : null,
            state == ConnectionState.Connected && traffic?.HasHealthyFallback == true);
    }
    private ProtectionStatus ReadProtection()
    {
        try
        {
            var persistent = console ? 0 : KillSwitch.PersistentBlockCount();
            var guarded = !console && (destinations.Active || destinations.Error != null) ? KillSwitch.DestinationBlockCount() : 0;
            var guardError = destinations.Error;
            if (destinations.Active && guarded != destinations.Current.FilterNetworks.Distinct().Count())
                guardError ??= "Windows не подтвердила полный набор фильтров для выбранных адресов.";
            return new(guarded + persistent + (killSwitch?.InstalledBlockCount() ?? 0), persistent + guarded,
                guardError, destinations.Saved.Entries, guarded, destinations.Active);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(0, 0, "Не удалось прочитать состояние фильтров WFP.", destinations.Saved.Entries, 0, destinations.Active);
        }
    }
    private async Task ServeAsync(CancellationToken ct)
    {
        if (!console)
        {
            await gate.WaitAsync(ct);
            try { await destinations.RestoreAsync(ct); }
            finally { gate.Release(); }
        }
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
                if (PipeProtocol.LongRunning(request.Operation)) timeout.CancelAfter(PipeProtocol.LongRunningDeadline);
                await gate.WaitAsync(timeout.Token);
                ServiceResponse result;
                try
                {
                    lastCommand = DateTimeOffset.UtcNow;
                    WfpTestResult[]? test = null;
                    WfpBlockEvent[]? blockEvents = null;
                    RecentDestination[]? recent = null;
                    ZapretStatus? zapretStatus = null;
                    switch (request.Operation)
                    {
                        case "GetStatus": break;
                        case "StopCore": StopCore(request.KeepBlocking); break;
                        case "SetDestinationGuard":
                            if (console || request.GuardTargets is null) throw new UserError("Для защиты адресов нужна установленная служба.");
                            var targets = DestinationGuard.Parse(request.GuardTargets);
                            if (destinations.Error == null && destinations.Active == (request.EnableGuard && targets.Enabled) &&
                                destinations.Saved.Entries.SequenceEqual(targets.Entries)) break;
                            if (request.EnableGuard && targets.Enabled && core?.Running == true && last?.Settings.TunnelMode != TunnelMode.Tun)
                                throw new UserError("Перед включением защиты выберите TUN.");
                            if (request.EnableGuard && core?.Running == true && last != null) targets.CheckEndpoints(last);
                            await destinations.SetAsync(targets, request.EnableGuard, core?.Running == true && last?.Settings.TunnelMode == TunnelMode.Tun, timeout.Token);
                            if (core?.Running == true && last != null)
                            {
                                try { await StartCoreAsync(last, timeout.Token, restart: true); }
                                catch { StopCore(keepBlocking: true); state = ConnectionState.Error; throw; }
                            }
                            break;
                        case "ClearProtection":
                            StopCore();
                            if (destinations.Active || destinations.Error != null) await destinations.SetAsync(destinations.Saved, false, false, timeout.Token);
                            KillSwitch.ClearPersistent();
                            break;
                        case "TestProtection": test = await KillSwitch.TestLocalFilteringAsync(timeout.Token); break;
                        case "TestDestinationGuard":
                            if (!destinations.Active) throw new UserError("Защита выбранных адресов выключена.");
                            test = await KillSwitch.TestDestinationFiltersAsync(destinations.Current, timeout.Token); break;
                        case "TestRuleProtection":
                            test = await KillSwitch.TestInstalledRuleAsync(request.TestRule ?? throw new UserError("Выберите защищённое правило."), timeout.Token);
                            break;
                        case "ProtectionEvents": blockEvents = KillSwitch.RecentPolicyBlocks(killSwitch); break;
                        case "ZapretStatus": zapretStatus = zapret.Status(); break;
                        case "ZapretStart":
                            if (console) throw new UserError("Для zapret нужна установленная служба.");
                            await zapret.StartAsync(request.Zapret ?? throw new UserError("Выберите профиль zapret."), (request.ZapretHosts ?? []).Concat(last is null ? [] : VpnHosts(last)), timeout.Token);
                            zapretStatus = zapret.Status(); break;
                        case "ZapretStop": zapret.Stop(); zapretStatus = zapret.Status(); break;
                        case "ZapretReadList":
                            result = new(true, Status(), string.Join("\n", zapret.ReadList(request.Zapret ?? ""))); await PipeProtocol.WriteAsync(pipe, result, timeout.Token); continue;
                        case "ZapretSaveList": zapret.SaveList(request.Zapret ?? "", request.ZapretList ?? []); zapretStatus = zapret.Status(); break;
                        case "RecentDestinations": recent = state == ConnectionState.Connected ? traffic?.Recent.Snapshot() ?? [] : []; break;
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
                    result = new(true, Status(), WfpTest: test, BlockEvents: blockEvents, Recent: recent, Zapret: zapretStatus);
                }
                catch (Exception e)
                {
                    var message = e is UserError ? e.Message : "Не удалось выполнить команду службы. Проверьте параметры подключения.";
                    if (request.Operation is ("StartCore" or "RestartCore") && (state != ConnectionState.Connected || core?.Running != true))
                    { StopCore(keepBlocking: true); state = ConnectionState.Error; error = message; }
                    log.Write("Command failed: " + request.Operation + "; " + e.GetType().Name);
                    result = new(false, Status(), message, Zapret: request.Operation.StartsWith("Zapret", StringComparison.Ordinal) ? zapret.Status() : null);
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
        if (!spec.UseNetBirdIntegration) return spec;
        var executable = NetBirdClient.FindExecutable();
        if (executable is null) return spec;
        return spec with
        {
            NetBirdExecutable = executable,
            NetBird = await NetBirdRouting.DiscoverAsync(spec.NetBirdPeer, ct)
        };
    }
    private async Task StartCoreAsync(ConnectSpec spec, CancellationToken ct, bool restart = false)
    {
        if (!restart && core?.Running == true) throw new UserError("VPN уже подключён.");
        if (destinations.Error != null) throw new UserError(destinations.Error);
        spec = (await ResolveNetBirdAsync(spec, ct)) with { Guard = destinations.Current, GuardCacheFile = Path.Combine(root,"destination-cache.db"), UplinkIPv6 = NetworkAddresses.HasIPv6Uplink() };
        if (!spec.UplinkIPv6) log.Write("Uplink has no IPv6; tunnel offers IPv4 only.");
        if (spec.Guard.Enabled)
        {
            spec.Guard.CheckEndpoints(spec);
            if (spec.Settings.TunnelMode != TunnelMode.Tun) throw new UserError("Для защиты выбранных адресов требуется TUN.");
            KillSwitch.ApplyDestinationFilters(spec.Guard.FilterNetworks, false);
            await destinations.ConfirmDnsAsync(ct);
        }
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
        core.Exited += () => _ = OnCoreExitAsync(launchedCore, path);
        await core.StartAsync(spec, root, false, ct, traffic.AddToConfig);
        if (persistent || global) ApplyProtection(spec, persistent, tunnelReady: true);
        if (!persistent && !console && KillSwitch.PersistentBlockCount() > 0) KillSwitch.ClearPersistent();
        if (!global || persistent) ReleaseKillSwitch();
        if (spec.Guard.Enabled)
        {
            if (!KillSwitch.TunnelInterfacePresent()) throw new UserError("Windows не подтвердила интерфейс TUN для защиты адресов.");
            KillSwitch.ApplyDestinationFilters(spec.Guard.FilterNetworks, true);
        }
        traffic.Start(); last = spec; connectedAt = DateTimeOffset.UtcNow; state = ConnectionState.Connected;
        // zapret must never touch the tunnel's own connections to the VPN servers.
        try { if (await zapret.EnsureExcludedAsync(VpnHosts(spec), ct)) log.Write("zapret exclusions updated for VPN servers."); }
        catch (UserError) { log.Write("zapret could not be restarted with VPN exclusions."); }
    }
    private async Task OnCoreExitAsync(CoreSession exited, string path)
    {
        try
        {
            await gate.WaitAsync(lifetime.Token);
            try
            {
                if (!ReferenceEquals(core, exited)) return;
                state = ConnectionState.Error; error = "Ядро VPN завершилось. Подключитесь заново."; connectedAt = null;
                traffic?.Dispose(); traffic = null;
                if (destinations.Active) KillSwitch.ApplyDestinationFilters(destinations.Current.FilterNetworks, false);
                File.Delete(path);
                log.Write("Core exited unexpectedly.");
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { log.Write("Unexpected exit cleanup failed: " + e.GetType().Name); }
    }
    /// <summary>Every address the tunnel itself dials: selected, rule, auto and TrustTunnel endpoints.</summary>
    private static IEnumerable<string> VpnHosts(ConnectSpec spec) =>
        new[] { spec.Server }.Concat(spec.RuleServers ?? []).Concat(spec.AutoServers ?? [])
            .SelectMany(s => new[] { s.Host }.Concat((s.TrustTunnel?.Addresses ?? []).Select(a => a.Contains("]:") ? a[1..a.IndexOf(']')] : a.Contains(':') && a.Count(c => c == ':') == 1 ? a[..a.IndexOf(':')] : a)));
    private void ApplyProtection(ConnectSpec spec, bool persistent, bool tunnelReady)
    {
        var transports = KillSwitch.TrustedTransports(Path.Combine(AppContext.BaseDirectory, "core"), spec.UseNetBirdIntegration);
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
        var netBird = spec.NetBirdEnabled ? await NetBirdRouting.DiscoverAsync(spec.NetBirdPeer, ct) : null;
        if (KillSwitch.ApplyPersistent(spec, netBird, KillSwitch.TrustedTransports(Path.Combine(AppContext.BaseDirectory, "core"), spec.NetBirdEnabled)) == 0)
            throw new UserError("Windows не применила фильтры защиты.");
        ReleaseKillSwitch();
    }
    private void ReleaseKillSwitch() { if (killSwitch is null) return; killSwitch.Dispose(); killSwitch = null; log.Write("Leak protection disabled."); }
    private void StopCore(bool keepBlocking = false)
    {
        try { if (core != null && destinations.Active) KillSwitch.ApplyDestinationFilters(destinations.Current.FilterNetworks, false); }
        finally
        {
            state = ConnectionState.Disconnecting; traffic?.Dispose(); traffic = null;
            var stopped = core; core = null; stopped?.Dispose();
            state = ConnectionState.Disconnected; connectedAt = null; error = null;
            if (!keepBlocking) ReleaseKillSwitch();
            File.Delete(Path.Combine(root, "sing-box.json")); File.Delete(Path.Combine(root, "xray.json")); log.Write("Core stopped; runtime config removed.");
        }
    }
    private async Task IdleStopAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(15), ct); if (!console && killSwitch is null && !zapret.Running && state != ConnectionState.Connected && state != ConnectionState.Connecting && DateTimeOffset.UtcNow - lastCommand > TimeSpan.FromSeconds(45)) { Stop(); return; } } }
        catch (OperationCanceledException) { }
    }
}
