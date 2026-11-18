using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Bebekon.Core;

public enum WfpTestOutcome { Passed, Failed, Unverified }
public sealed record WfpTestResult(string AddressFamily, RuleTransport Transport, WfpTestOutcome Outcome, ulong? FilterId, bool Restored, string? Detail);
public sealed record WfpBlockEvent(DateTimeOffset Timestamp, string? Address, int? Port, byte? Protocol, ulong FilterId, string Reason);

public sealed partial class KillSwitch
{
    /// <summary>Tests the system filtering mechanism with fresh sockets and a loopback-only rule.</summary>
    public static Task<WfpTestResult[]> TestLocalFilteringAsync(CancellationToken ct)
        => TestLoopbackAsync(new RoutingRule
        {
            Name = "Bebekon local WFP fixture", Kind = RuleKind.Application,
            Values = [Environment.ProcessPath!], SystemProtection = true
        }, ct);

    public static Task<WfpTestResult[]> TestInstalledRuleAsync(RoutingRule rule, CancellationToken ct)
        => TestInstalledRuleAsync(PolicyProvider, rule, ct);

    internal static async Task<WfpTestResult[]> TestInstalledRuleAsync(Guid provider, RoutingRule rule, CancellationToken ct)
    {
        RuleValidation.Validate(rule);
        if (!rule.Enabled || !rule.SystemProtection) throw new UserError("У правила нет активной системной защиты.");
        var scope = rule.SystemScope();
        if (!ScopeIsInstalled(provider, scope)) throw new UserError("Фильтры выбранного правила не найдены в действующей политике WFP.");
        var result = await TestLoopbackAsync(scope, ct);
        if (!ScopeIsInstalled(provider, scope))
            return result.Select(check => check with { Outcome = WfpTestOutcome.Unverified, Detail = "Действующая политика изменилась во время теста." }).ToArray();
        return result;
    }

    private static async Task<WfpTestResult[]> TestLoopbackAsync(RoutingRule scope, CancellationToken ct)
    {
        var results = new List<WfpTestResult>();
        var addresses = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };
        if (scope is { Kind: RuleKind.Network })
        {
            var families = scope.Values.Select(value => IPNetwork.Parse(value).BaseAddress.AddressFamily).ToHashSet();
            addresses = addresses.Where(address => families.Contains(address.AddressFamily)).ToArray();
        }
        foreach (var address in addresses)
            foreach (var transport in scope.Transport is { } limited ? [limited] : new[] { RuleTransport.Tcp, RuleTransport.Udp })
            {
                try { results.Add(await TestEndpointAsync(address, transport, scope, ct)); }
                catch (SocketException)
                {
                    var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    results.Add(new(family, transport, WfpTestOutcome.Unverified, null, true, "Не удалось открыть локальное соединение для пробы."));
                }
            }
        return results.ToArray();
    }

    private static async Task<WfpTestResult> TestEndpointAsync(IPAddress address, RuleTransport transport, RoutingRule scope, CancellationToken ct)
    {
        var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        var tcp = transport == RuleTransport.Tcp;
        var protocol = tcp ? ProtocolType.Tcp : ProtocolType.Udp;
        using var listener = new Socket(address.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram, protocol) { ExclusiveAddressUse = true };
        listener.Bind(new IPEndPoint(address, 0));
        if (tcp) listener.Listen(4);
        var target = (IPEndPoint)listener.LocalEndPoint!;

        Socket ProbeSocket()
        {
            var probe = new Socket(address.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram, protocol);
            try { probe.Bind(new IPEndPoint(address, 0)); return probe; }
            catch { probe.Dispose(); throw; }
        }

        using var baseline = ProbeSocket();
        if (!await ReachesReceiverAsync(baseline, listener, target, tcp, ct))
            return new(family, transport, WfpTestOutcome.Unverified, null, true, "Контрольная проба не дошла до локального получателя.");

        ulong? matchedFilter = null;
        var reachedWhileBlocked = false;
        string? detail = null;
        using var probe = ProbeSocket();
        var source = (IPEndPoint)probe.LocalEndPoint!;
        try
        {
            using var filter = InstallProbePolicy(target, protocol, scope, out var id, out var permit);
            using var available = ProbeSocket();
            if (!await ReachesReceiverAsync(available, listener, target, tcp, ct))
                return new(family, transport, WfpTestOutcome.Unverified, null, true, "Контрольный маршрут изолированной пробы недоступен.");
            // Withdraw only the permit for this receiver. The scoped deny and every production
            // filter remain in effect while the client attempts its fallback connection.
            InTransaction(filter.engine, () => Check(FwpmFilterDeleteById0(filter.engine, permit), "FwpmFilterDeleteById0"));
            var started = DateTime.UtcNow.ToFileTimeUtc();
            reachedWhileBlocked = await ReachesReceiverAsync(probe, listener, target, tcp, ct);
            if (!reachedWhileBlocked)
            {
                var until = DateTime.UtcNow.AddMilliseconds(1200);
                do
                {
                    if (ReadDrop(filter.engine, id, started, source.Port, target.Port, (byte)protocol))
                    {
                        matchedFilter = id;
                        break;
                    }
                    await Task.Delay(120, ct);
                } while (DateTime.UtcNow < until);
            }
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            detail = e is Win32Exception windows
                ? $"Windows отклонила операцию WFP (0x{windows.NativeErrorCode:X8})."
                : "Windows не предоставила интерфейс WFP для диагностики.";
        }

        using var after = ProbeSocket();
        var restored = await ReachesReceiverAsync(after, listener, target, tcp, ct);
        if (!restored || reachedWhileBlocked)
            return new(family, transport, WfpTestOutcome.Failed, matchedFilter, restored,
                restored ? "Локальный получатель увидел запрещённую пробу." : "Контрольная проба после отката не дошла до получателя.");
        return matchedFilter is { } proof
            ? new(family, transport, WfpTestOutcome.Passed, proof, true, null)
            : new(family, transport, WfpTestOutcome.Unverified, null, true, detail ?? "Для пробы нет события блокировки нужным фильтром.");
    }

    private static async Task<bool> ReachesReceiverAsync(Socket probe, Socket listener, IPEndPoint target, bool tcp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
        try
        {
            if (tcp)
            {
                await probe.ConnectAsync(target, timeout.Token);
                using var accepted = await listener.AcceptAsync(timeout.Token);
                return Equals(accepted.RemoteEndPoint, probe.LocalEndPoint);
            }
            await probe.SendToAsync(new byte[] { 1 }, SocketFlags.None, target, timeout.Token);
            var received = await listener.ReceiveFromAsync(new byte[8], SocketFlags.None, target, timeout.Token);
            return received.ReceivedBytes == 1 && Equals(received.RemoteEndPoint, probe.LocalEndPoint);
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static KillSwitch InstallProbePolicy(IPEndPoint target, ProtocolType protocol, RoutingRule scope,
        out ulong filterId, out ulong permitId)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: true);
        try
        {
            ulong id = 0, allow = 0;
            InTransaction(engine, () =>
            {
                var sublayer = AddSublayer(memory, engine, IntPtr.Zero);
                var v4 = target.AddressFamily == AddressFamily.InterNetwork;
                var layer = v4 ? ConnectV4 : ConnectV6;
                var conditions = new List<Condition>
                {
                    memory.App(Environment.ProcessPath!),
                    NetworkCondition(memory, RemoteAddress, new(target.Address, v4 ? 32 : 128)),
                    new(RemotePort, MatchEqual, TypeUInt16, target.Port),
                    new(IpProtocol, MatchEqual, TypeUInt8, (byte)protocol)
                };
                id = AddScopeBlock(memory, engine, sublayer, layer, scope, conditions, IntPtr.Zero);
                allow = AddFilter(memory, engine, sublayer, layer, 14, ActionPermit, "Bebekon local allowed route",
                    [.. conditions, new(Flags, MatchFlagsAllSet, TypeUInt32, FlagIsLoopback)]);
            });
            filterId = id;
            permitId = allow;
            return new(engine, [id]);
        }
        catch { FwpmEngineClose0(engine); throw; }
    }

    private static bool ReadDrop(IntPtr engine, ulong id, long started, int sourcePort, int targetPort, byte protocol)
    {
        var matched = false;
        ReadBlockEvents(engine, started, (_, data, filterId) =>
        {
            const int tupleFlags = 0x01 | 0x08 | 0x10; // IP protocol and both ports
            if (filterId == id && (data.Flags & tupleFlags) == tupleFlags &&
                data.Protocol == protocol && data.LocalPort == sourcePort && data.RemotePort == targetPort)
                matched = true;
        });
        return matched;
    }

    internal static bool ObservePersistentDrop(Guid providerKey, long started, int sourcePort, int targetPort, byte protocol)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: false);
        try
        {
            var provider = memory.Alloc(16);
            WriteGuid(provider, providerKey);
            var ids = new List<ulong>();
            ReadProviderFilters(memory, engine, provider, filter =>
            {
                if (IsEnabledBlock(filter)) ids.Add(unchecked((ulong)Marshal.ReadInt64(filter + 176)));
            });
            return ids.Any(id => ReadDrop(engine, id, started, sourcePort, targetPort, protocol));
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static WfpBlockEvent[] RecentPolicyBlocks(KillSwitch? dynamicPolicy)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: false);
        try
        {
            var names = new Dictionary<ulong, string>();
            void RememberFilter(IntPtr filter)
            {
                if (IsEnabledBlock(filter))
                    names[unchecked((ulong)Marshal.ReadInt64(filter + 176))] =
                        Marshal.PtrToStringUni(Marshal.ReadIntPtr(filter + 16)) ?? "Bebekon WFP";
            }

            if (ProviderExists(engine, PolicyProvider))
            {
                var provider = memory.Alloc(16);
                WriteGuid(provider, PolicyProvider);
                ReadProviderFilters(memory, engine, provider, RememberFilter);
            }
            foreach (var id in dynamicPolicy?.blockIds ?? [])
            {
                var status = FwpmFilterGetById0(engine, id, out var filter);
                if (status == FilterNotFound) continue;
                Check(status, "FwpmFilterGetById0");
                try { RememberFilter(filter); }
                finally { FwpmFreeMemory0(ref filter); }
            }

            if (names.Count == 0) return [];
            var entries = new List<WfpBlockEvent>();
            ReadBlockEvents(engine, DateTime.UtcNow.AddMinutes(-10).ToFileTimeUtc(), (item, data, filterId) =>
            {
                if (!names.TryGetValue(filterId, out var reason)) return;
                entries.Add(new(DateTime.FromFileTimeUtc(data.TimeStamp), RemoteAddressText(item, data),
                    (data.Flags & 0x10) != 0 ? data.RemotePort : null,
                    (data.Flags & 0x01) != 0 ? data.Protocol : null, filterId, reason));
            });
            return entries.OrderByDescending(entry => entry.Timestamp).Take(24).ToArray();
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static string? RemoteAddressText(IntPtr item, ClassifyEvent data)
    {
        if ((data.Flags & 0x104) != 0x104) return null;
        if (data.Version == 0)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, unchecked((uint)Marshal.ReadInt32(item + 36)));
            return new IPAddress(bytes).ToString();
        }
        if (data.Version != 1) return null;
        var address = new byte[16];
        Marshal.Copy(item + 36, address, 0, address.Length);
        return new IPAddress(address).ToString();
    }

    private static void ReadBlockEvents(IntPtr engine, long started, Action<IntPtr, ClassifyEvent, ulong> visit)
    {
        using var memory = new NativeMemory();
        var template = memory.Alloc(32);
        Marshal.WriteInt64(template, started);
        Marshal.WriteInt64(template + 8, DateTime.UtcNow.ToFileTimeUtc());
        ReadObjects(engine, template, FwpmNetEventCreateEnumHandle0, FwpmNetEventEnum0, FwpmNetEventDestroyEnumHandle0, item =>
        {
            var data = Marshal.PtrToStructure<ClassifyEvent>(item);
            if (data.Type == 3 && data.Detail != IntPtr.Zero && data.TimeStamp >= started)
                visit(item, data, unchecked((ulong)Marshal.ReadInt64(data.Detail)));
        });
    }

    // FWPM_NET_EVENT0 on x64, with FWPM_NET_EVENT_HEADER0 at offset zero.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct ClassifyEvent
    {
        [FieldOffset(0)] public long TimeStamp;
        [FieldOffset(8)] public int Flags;
        [FieldOffset(12)] public int Version;
        [FieldOffset(16)] public byte Protocol;
        [FieldOffset(52)] public ushort LocalPort;
        [FieldOffset(54)] public ushort RemotePort;
        [FieldOffset(88)] public int Type;
        [FieldOffset(96)] public IntPtr Detail;
    }

    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventEnum0(IntPtr engine, IntPtr enumerator, uint limit, out IntPtr entries, out uint copied);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engine, IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
}
