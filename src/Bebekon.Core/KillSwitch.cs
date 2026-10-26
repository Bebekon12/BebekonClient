using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bebekon.Core;

/// <summary>
/// Opt-in leak protection while the VPN reconnects or after its core stopped unexpectedly:
/// outbound traffic outside the tunnel is blocked except for the bundled cores, loopback and the
/// local network (DNS to it stays blocked). Same approach as WireGuard for Windows: Windows
/// Filtering Platform filters in a dynamic session, which Windows removes by itself when this
/// object is disposed or the service process ends, so a crash can never leave the PC offline.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KillSwitch : IDisposable
{
    // Tunnel addresses from ConfigGenerator: apps that use the tunnel connect from these.
    private static readonly (uint Address, uint Mask) TunnelV4 = (0xAC1DFF00, 0xFFFFFFFC); // 172.29.255.0/30
    private static readonly (IPAddress Address, byte Prefix) TunnelV6 = (IPAddress.Parse("fd84:be:be::"), 126);
    private static readonly (uint, uint)[] LanV4 = [(0x0A000000, 0xFF000000), (0xAC100000, 0xFFF00000), (0xC0A80000, 0xFFFF0000), (0xA9FE0000, 0xFFFF0000), (0xE0000000, 0xF0000000), (0xFFFFFFFF, 0xFFFFFFFF)];
    private static readonly (IPAddress, byte)[] LanV6 = [(IPAddress.Parse("fe80::"), 10), (IPAddress.Parse("ff00::"), 8), (IPAddress.Parse("fc00::"), 7)];

    private static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid LocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid Flags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    private const uint MatchEqual = 0, MatchFlagsAllSet = 6;
    private const uint TypeUInt8 = 1, TypeUInt16 = 2, TypeUInt32 = 3, TypeByteBlob = 12, TypeV4Mask = 0x100, TypeV6Mask = 0x101;
    private const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    private const uint FlagIsLoopback = 1, SessionDynamic = 1, AuthnWinNt = 10;

    private IntPtr engine;

    private KillSwitch(IntPtr engine) => this.engine = engine;

    /// <param name="trustedExecutables">Programs that reach the VPN server directly (sing-box, Xray, TrustTunnel).</param>
    public static KillSwitch Enable(IEnumerable<string> trustedExecutables)
    {
        using var memory = new NativeMemory();
        var session = memory.Alloc(72);
        WriteDisplay(memory, session + 16, "Bebekon VPN");
        Marshal.WriteInt32(session + 32, (int)SessionDynamic);
        Marshal.WriteInt32(session + 36, -1); // INFINITE transaction wait
        Check(FwpmEngineOpen0(null, AuthnWinNt, IntPtr.Zero, session, out var engine), "FwpmEngineOpen0");
        var blobs = new List<IntPtr>();
        try
        {
            Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
            try
            {
                var sublayer = Guid.NewGuid();
                var layer = memory.Alloc(72);
                WriteGuid(layer, sublayer); WriteDisplay(memory, layer + 16, "Bebekon VPN leak protection");
                Marshal.WriteInt16(layer + 64, unchecked((short)0xFFFF));
                Check(FwpmSubLayerAdd0(engine, layer, IntPtr.Zero), "FwpmSubLayerAdd0");

                var apps = new List<Condition>();
                foreach (var path in trustedExecutables.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Check(FwpmGetAppIdFromFileName0(path, out var blob), "FwpmGetAppIdFromFileName0");
                    blobs.Add(blob); apps.Add(new(AppId, MatchEqual, TypeByteBlob, blob.ToInt64()));
                }
                foreach (var v4 in new[] { true, false })
                {
                    var connect = v4 ? ConnectV4 : ConnectV6;
                    // Repeated fields are ORed by WFP; different fields are ANDed.
                    if (apps.Count > 0) AddFilter(memory, engine, sublayer, connect, 15, ActionPermit, "Bundled VPN cores", apps);
                    AddFilter(memory, engine, sublayer, connect, 14, ActionPermit, "Loopback", [new(Flags, MatchFlagsAllSet, TypeUInt32, FlagIsLoopback)]);
                    AddFilter(memory, engine, sublayer, connect, 13, ActionPermit, "Bebekon tunnel", [v4 ? V4(memory, LocalAddress, TunnelV4) : V6(memory, LocalAddress, TunnelV6)]);
                    AddFilter(memory, engine, sublayer, connect, 12, ActionBlock, "DNS outside the tunnel", [new(RemotePort, MatchEqual, TypeUInt16, 53)]);
                    AddFilter(memory, engine, sublayer, connect, 11, ActionPermit, "Local network", v4 ? LanV4.Select(r => V4(memory, RemoteAddress, r)).ToList() : LanV6.Select(r => V6(memory, RemoteAddress, r)).ToList());
                    AddFilter(memory, engine, sublayer, connect, 0, ActionBlock, "Everything else outside the tunnel", []);
                }
                Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
            }
            catch { FwpmTransactionAbort0(engine); throw; }
            return new KillSwitch(engine);
        }
        catch { FwpmEngineClose0(engine); throw; }
        finally { foreach (var blob in blobs) { var p = blob; FwpmFreeMemory0(ref p); } }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref engine, IntPtr.Zero);
        if (handle != IntPtr.Zero) FwpmEngineClose0(handle);
    }

    private readonly record struct Condition(Guid Field, uint Match, uint Type, long Value);

    private static Condition V4(NativeMemory memory, Guid field, (uint Address, uint Mask) range)
    {
        var p = memory.Alloc(8); Marshal.WriteInt32(p, unchecked((int)range.Address)); Marshal.WriteInt32(p + 4, unchecked((int)range.Mask));
        return new(field, MatchEqual, TypeV4Mask, p.ToInt64());
    }
    private static Condition V6(NativeMemory memory, Guid field, (IPAddress Address, byte Prefix) range)
    {
        var p = memory.Alloc(17); Marshal.Copy(range.Address.GetAddressBytes(), 0, p, 16); Marshal.WriteByte(p + 16, range.Prefix);
        return new(field, MatchEqual, TypeV6Mask, p.ToInt64());
    }

    private static void AddFilter(NativeMemory memory, IntPtr engine, Guid sublayer, Guid layer, byte weight, uint action, string name, IReadOnlyList<Condition> conditions)
    {
        // FWPM_FILTER_CONDITION0: fieldKey@0, matchType@16, conditionValue{type@24, value@32}; 40 bytes.
        var array = conditions.Count == 0 ? IntPtr.Zero : memory.Alloc(40 * conditions.Count);
        for (var i = 0; i < conditions.Count; i++)
        {
            var c = array + 40 * i;
            WriteGuid(c, conditions[i].Field); Marshal.WriteInt32(c + 16, (int)conditions[i].Match);
            Marshal.WriteInt32(c + 24, (int)conditions[i].Type); Marshal.WriteInt64(c + 32, conditions[i].Value);
        }
        // FWPM_FILTER0 (x64): displayData@16, layerKey@64, subLayerKey@80, weight{type@96,value@104},
        // numFilterConditions@112, filterCondition@120, action.type@128; 200 bytes.
        var filter = memory.Alloc(200);
        WriteDisplay(memory, filter + 16, name);
        WriteGuid(filter + 64, layer); WriteGuid(filter + 80, sublayer);
        Marshal.WriteInt32(filter + 96, (int)TypeUInt8); Marshal.WriteInt64(filter + 104, weight);
        Marshal.WriteInt32(filter + 112, conditions.Count); Marshal.WriteIntPtr(filter + 120, array);
        Marshal.WriteInt32(filter + 128, (int)action);
        Check(FwpmFilterAdd0(engine, filter, IntPtr.Zero, out _), "FwpmFilterAdd0");
    }

    private static void WriteGuid(IntPtr p, Guid value) => Marshal.Copy(value.ToByteArray(), 0, p, 16);
    private static void WriteDisplay(NativeMemory memory, IntPtr p, string name) => Marshal.WriteIntPtr(p, memory.String(name));
    private static void Check(uint result, string call) { if (result != 0) throw new Win32Exception(unchecked((int)result), call + " failed: 0x" + result.ToString("X8")); }

    /// <summary>Zeroed unmanaged allocations that live until the WFP calls copied them.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> blocks = [];
        public IntPtr Alloc(int size) { var p = Marshal.AllocHGlobal(size); blocks.Add(p); for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0); return p; }
        public IntPtr String(string value) { var p = Marshal.StringToHGlobalUni(value); blocks.Add(p); return p; }
        public void Dispose() { foreach (var p in blocks) Marshal.FreeHGlobal(p); }
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, IntPtr subLayer, IntPtr securityDescriptor);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engineHandle, IntPtr filter, IntPtr securityDescriptor, out ulong id);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr p);
}
