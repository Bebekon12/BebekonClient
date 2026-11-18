using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Bebekon.Core;

public enum WfpTestOutcome { Passed, Failed, Unverified }
public sealed record WfpTestResult(string AddressFamily, RuleTransport Transport, WfpTestOutcome Outcome, ulong? FilterId, bool Restored, string? Detail);

public sealed partial class KillSwitch
{
    /// <summary>Tests the system filtering mechanism with fresh sockets and a loopback-only rule.</summary>
    public static async Task<WfpTestResult[]> TestLocalFilteringAsync(CancellationToken ct)
    {
        var results = new List<WfpTestResult>();
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            foreach (var transport in new[] { RuleTransport.Tcp, RuleTransport.Udp })
            {
                try { results.Add(await TestEndpointAsync(address, transport, ct)); }
                catch (SocketException)
                {
                    var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    results.Add(new(family, transport, WfpTestOutcome.Unverified, null, true, "Не удалось открыть локальное соединение для пробы."));
                }
            }
        return results.ToArray();
    }

    private static async Task<WfpTestResult> TestEndpointAsync(IPAddress address, RuleTransport transport, CancellationToken ct)
    {
        var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        var tcp = transport == RuleTransport.Tcp;
        var protocol = tcp ? ProtocolType.Tcp : ProtocolType.Udp;
        using var listener = new Socket(address.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram, protocol);
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
            using var filter = BlockProbe(source, target, protocol, out var id);
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

    private static KillSwitch BlockProbe(IPEndPoint source, IPEndPoint target, ProtocolType protocol, out ulong filterId)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: true);
        try
        {
            ulong id = 0;
            InTransaction(engine, () =>
            {
                var sublayer = AddSublayer(memory, engine, IntPtr.Zero);
                var v4 = target.AddressFamily == AddressFamily.InterNetwork;
                id = AddFilter(memory, engine, sublayer, v4 ? ConnectV4 : ConnectV6, 15, ActionBlock, "Bebekon local WFP diagnostic",
                    [memory.App(Environment.ProcessPath!), NetworkCondition(memory, RemoteAddress, new(target.Address, v4 ? 32 : 128)),
                     new(RemotePort, MatchEqual, TypeUInt16, target.Port), new(LocalPort, MatchEqual, TypeUInt16, source.Port),
                     new(IpProtocol, MatchEqual, TypeUInt8, (byte)protocol)]);
            });
            filterId = id;
            return new(engine, [id]);
        }
        catch { FwpmEngineClose0(engine); throw; }
    }

    private static bool ReadDrop(IntPtr engine, ulong id, long started, int sourcePort, int targetPort, byte protocol)
    {
        using var memory = new NativeMemory();
        var template = memory.Alloc(32);
        Marshal.WriteInt64(template, started);
        Marshal.WriteInt64(template + 8, DateTime.UtcNow.ToFileTimeUtc());
        var matched = false;
        ReadObjects(engine, template, FwpmNetEventCreateEnumHandle0, FwpmNetEventEnum0, FwpmNetEventDestroyEnumHandle0, item =>
        {
            const int tupleFlags = 0x01 | 0x08 | 0x10; // IP protocol and both ports
            var data = Marshal.PtrToStructure<ClassifyEvent>(item);
            if (data.Type == 3 && data.Detail != IntPtr.Zero && (data.Flags & tupleFlags) == tupleFlags &&
                data.TimeStamp >= started && data.Protocol == protocol && data.LocalPort == sourcePort &&
                data.RemotePort == targetPort && unchecked((ulong)Marshal.ReadInt64(data.Detail)) == id)
                matched = true;
        });
        return matched;
    }

    // FWPM_NET_EVENT0 on x64, with FWPM_NET_EVENT_HEADER0 at offset zero.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct ClassifyEvent
    {
        [FieldOffset(0)] public long TimeStamp;
        [FieldOffset(8)] public int Flags;
        [FieldOffset(16)] public byte Protocol;
        [FieldOffset(52)] public ushort LocalPort;
        [FieldOffset(54)] public ushort RemotePort;
        [FieldOffset(88)] public int Type;
        [FieldOffset(96)] public IntPtr Detail;
    }

    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventEnum0(IntPtr engine, IntPtr enumerator, uint limit, out IntPtr entries, out uint copied);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engine, IntPtr enumerator);
}
