using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace Bebekon.Core;

/// <summary>
/// Windows Filtering Platform policy for outbound connections. A dynamic session owns the
/// legacy session-wide policy. Explicit durable policies are owned by the Bebekon provider.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class KillSwitch : IDisposable
{
    // Tunnel addresses from ConfigGenerator: apps that use the tunnel connect from these.
    private static readonly (uint Address, uint Mask) TunnelV4 = (0xAC1DFF00, 0xFFFFFFFC); // 172.29.255.0/30
    private static readonly (IPAddress Address, byte Prefix) TunnelV6 = (IPAddress.Parse("fd84:be:be::"), 126);
    private static readonly (uint, uint)[] LanV4 = [(0x0A000000, 0xFF000000), (0xAC100000, 0xFFF00000), (0xC0A80000, 0xFFFF0000), (0xA9FE0000, 0xFFFF0000), (0xE0000000, 0xF0000000), (0xFFFFFFFF, 0xFFFFFFFF)];
    private static readonly (IPAddress, byte)[] LanV6 = [(IPAddress.Parse("fe80::"), 10), (IPAddress.Parse("ff00::"), 8), (IPAddress.Parse("fc00::"), 7)];

    private static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid UserId = new("af043a0a-b34d-4f86-979c-c90371af6e66");
    private static readonly Guid LocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid LocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid RemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid RemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid LocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid IpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid Flags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    private const uint MatchEqual = 0, MatchFlagsAllSet = 6;
    private const uint TypeUInt8 = 1, TypeUInt16 = 2, TypeUInt32 = 3, TypeUInt64 = 4, TypeByteBlob = 12,
        TypeSecurityDescriptor = 14, TypeV4Mask = 0x100, TypeV6Mask = 0x101;
    private const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    private const uint FlagIsLoopback = 1, SessionDynamic = 1, AuthnWinNt = 10;

    private IntPtr engine;
    private readonly ulong[] blockIds;

    private KillSwitch(IntPtr engine, ulong[] blockIds)
    {
        this.engine = engine;
        this.blockIds = blockIds;
    }

    public static KillSwitch Enable(IEnumerable<string> trustedExecutables, NetBirdRouting? netBird = null,
        bool requireTunnelInterface = false, bool permitTunnel = true)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: true);
        try
        {
            var blocked = new List<ulong>();
            InTransaction(engine, () =>
            {
                var apps = ReadAppConditions(memory, trustedExecutables);
                var sublayer = AddSublayer(memory, engine, IntPtr.Zero);
                AddGlobalFilters(memory, engine, sublayer, IntPtr.Zero, apps, netBird, requireTunnelInterface, permitTunnel, blocked);
            });
            return new KillSwitch(engine, [.. blocked]);
        }
        catch { FwpmEngineClose0(engine); throw; }
    }

    public int InstalledBlockCount() => engine == IntPtr.Zero ? 0 : blockIds.Count(id => IsEnabledBlock(engine, id));
    public static bool TunnelInterfacePresent() => ConvertInterfaceAliasToLuid("Bebekon", out var luid) == 0 && luid != 0;

    private static IntPtr OpenEngine(NativeMemory memory, bool dynamic)
    {
        var session = memory.Alloc(72);
        WriteDisplay(memory, session + 16, "Bebekon VPN");
        if (dynamic) Marshal.WriteInt32(session + 32, (int)SessionDynamic);
        Marshal.WriteInt32(session + 36, 3000);
        Check(FwpmEngineOpen0(null, AuthnWinNt, IntPtr.Zero, session, out var engine), "FwpmEngineOpen0");
        return engine;
    }

    private static void InTransaction(IntPtr engine, Action update)
    {
        Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
        try
        {
            update();
            Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
        }
        catch { FwpmTransactionAbort0(engine); throw; }
    }

    private static Guid AddSublayer(NativeMemory memory, IntPtr engine, IntPtr provider)
    {
        var key = Guid.NewGuid();
        var layer = memory.Alloc(72);
        WriteGuid(layer, key);
        WriteDisplay(memory, layer + 16, "Bebekon VPN leak protection");
        if (provider != IntPtr.Zero)
        {
            Marshal.WriteInt32(layer + 32, 1); // FWPM_SUBLAYER_FLAG_PERSISTENT
            Marshal.WriteIntPtr(layer + 40, provider);
        }
        Marshal.WriteInt16(layer + 64, unchecked((short)0xFFFF));
        Check(FwpmSubLayerAdd0(engine, layer, IntPtr.Zero), "FwpmSubLayerAdd0");
        return key;
    }

    private static Condition[] ReadAppConditions(NativeMemory memory, IEnumerable<string> executables) =>
        executables.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Select(memory.App).ToArray();

    private static void AddTransportPermits(NativeMemory memory, IntPtr engine, Guid sublayer, Guid layer, IntPtr provider,
        Condition[] apps)
    {
        if (apps.Length == 0) return;
        // WFP evaluates the descriptor using FWP_ACTRL_MATCH_FILTER (0x1).
        // The same executable launched interactively cannot inherit the service exemption.
        var descriptor = new RawSecurityDescriptor("D:(A;;0x1;;;SY)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var buffer = memory.Alloc(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        var blob = memory.Alloc(16);
        Marshal.WriteInt32(blob, bytes.Length);
        Marshal.WriteIntPtr(blob + 8, buffer);
        var system = new Condition(UserId, MatchEqual, TypeSecurityDescriptor, blob.ToInt64());
        foreach (var app in apps)
            AddFilter(memory, engine, sublayer, layer, 15, ActionPermit, "VPN service transports", [app, system], provider);
    }

    private static void AddGlobalFilters(NativeMemory memory, IntPtr engine, Guid sublayer, IntPtr provider,
        Condition[] apps, NetBirdRouting? netBird, bool requireTunnelInterface, bool permitTunnel, List<ulong> blocked)
    {
        var bird = FindInterface(memory, netBird?.InterfaceName);
        var tunnel = FindInterface(memory, "Bebekon");
        foreach (var v4 in new[] { true, false })
        {
            var layer = v4 ? ConnectV4 : ConnectV6;
            AddTransportPermits(memory, engine, sublayer, layer, provider, apps);
            AddFilter(memory, engine, sublayer, layer, 14, ActionPermit, "Loopback", [new(Flags, MatchFlagsAllSet, TypeUInt32, FlagIsLoopback)], provider);
            if (permitTunnel && (!requireTunnelInterface || tunnel is not null))
            {
                var source = v4 ? V4(memory, LocalAddress, TunnelV4) : V6(memory, LocalAddress, TunnelV6);
                AddFilter(memory, engine, sublayer, layer, 13, ActionPermit, "Bebekon tunnel",
                    tunnel is { } adapter ? [source, adapter] : [source], provider);
            }
            if (bird is { } corporate) AddFilter(memory, engine, sublayer, layer, 13, ActionPermit, "NetBird interface", [corporate], provider);
            AddDhcp(memory, engine, sublayer, layer, v4, provider);
            blocked.Add(AddFilter(memory, engine, sublayer, layer, 12, ActionBlock, "DNS outside the tunnel",
                [new(RemotePort, MatchEqual, TypeUInt16, 53)], provider));
            if (provider == IntPtr.Zero)
            {
                var local = v4 ? LanV4.Select(r => V4(memory, RemoteAddress, r)) : LanV6.Select(r => V6(memory, RemoteAddress, r));
                foreach (var network in local) AddFilter(memory, engine, sublayer, layer, 11, ActionPermit, "Local network", [network], provider);
            }
            blocked.Add(AddFilter(memory, engine, sublayer, layer, 0, ActionBlock, "Outside permitted tunnels", [], provider));
        }
    }

    private static void AddDhcp(NativeMemory memory, IntPtr engine, Guid sublayer, Guid layer, bool v4, IntPtr provider) =>
        AddFilter(memory, engine, sublayer, layer, 14, ActionPermit, "DHCP",
            [new(IpProtocol, MatchEqual, TypeUInt8, 17), new(LocalPort, MatchEqual, TypeUInt16, v4 ? 68 : 546),
             new(RemotePort, MatchEqual, TypeUInt16, v4 ? 67 : 547)], provider);

    private static Condition? FindInterface(NativeMemory memory, string? name)
    {
        if (name is null || ConvertInterfaceAliasToLuid(name, out var luid) != 0 || luid == 0) return null;
        var pointer = memory.Alloc(8);
        Marshal.WriteInt64(pointer, unchecked((long)luid));
        return new(LocalInterface, MatchEqual, TypeUInt64, pointer.ToInt64());
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

    private static ulong AddFilter(NativeMemory memory, IntPtr engine, Guid sublayer, Guid layer, byte weight, uint action,
        string name, IReadOnlyList<Condition> conditions, IntPtr provider = default, byte[]? policyFingerprint = null)
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
        if (provider != IntPtr.Zero)
        {
            Marshal.WriteInt32(filter + 32, 1); // FWPM_FILTER_FLAG_PERSISTENT
            Marshal.WriteIntPtr(filter + 40, provider);
        }
        if (policyFingerprint is { Length: > 0 })
        {
            var data = memory.Alloc(policyFingerprint.Length);
            Marshal.Copy(policyFingerprint, 0, data, policyFingerprint.Length);
            Marshal.WriteInt32(filter + 48, policyFingerprint.Length);
            Marshal.WriteIntPtr(filter + 56, data);
        }
        WriteGuid(filter + 64, layer); WriteGuid(filter + 80, sublayer);
        Marshal.WriteInt32(filter + 96, (int)TypeUInt8); Marshal.WriteInt64(filter + 104, weight);
        Marshal.WriteInt32(filter + 112, conditions.Count); Marshal.WriteIntPtr(filter + 120, array);
        Marshal.WriteInt32(filter + 128, (int)action);
        Check(FwpmFilterAdd0(engine, filter, IntPtr.Zero, out var id), "FwpmFilterAdd0");
        return id;
    }

    private static bool IsEnabledBlock(IntPtr engine, ulong id)
    {
        var status = FwpmFilterGetById0(engine, id, out var filter);
        if (status == FilterNotFound) return false;
        Check(status, "FwpmFilterGetById0");
        try { return IsEnabledBlock(filter); }
        finally { FwpmFreeMemory0(ref filter); }
    }

    private static bool IsEnabledBlock(IntPtr filter) =>
        (Marshal.ReadInt32(filter + 32) & 0x20) == 0 && (uint)Marshal.ReadInt32(filter + 128) == ActionBlock;

    private static void WriteGuid(IntPtr p, Guid value) => Marshal.Copy(value.ToByteArray(), 0, p, 16);
    private static void WriteDisplay(NativeMemory memory, IntPtr p, string name) => Marshal.WriteIntPtr(p, memory.String(name));
    private static void Check(uint result, string call) { if (result != 0) throw new Win32Exception(unchecked((int)result), call + " failed: 0x" + result.ToString("X8")); }

    /// <summary>Zeroed unmanaged allocations that live until the WFP calls copied them.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> blocks = [];
        private readonly List<IntPtr> wfpBlocks = [];
        public IntPtr Alloc(int size) { var p = Marshal.AllocHGlobal(size); blocks.Add(p); for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0); return p; }
        public IntPtr String(string value) { var p = Marshal.StringToHGlobalUni(value); blocks.Add(p); return p; }
        public Condition App(string path)
        {
            Check(FwpmGetAppIdFromFileName0(path, out var blob), "FwpmGetAppIdFromFileName0");
            wfpBlocks.Add(blob);
            return new(AppId, MatchEqual, TypeByteBlob, blob.ToInt64());
        }
        public void Dispose()
        {
            foreach (var p in blocks) Marshal.FreeHGlobal(p);
            foreach (var block in wfpBlocks) { var pointer = block; FwpmFreeMemory0(ref pointer); }
        }
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, IntPtr subLayer, IntPtr securityDescriptor);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engineHandle, IntPtr filter, IntPtr securityDescriptor, out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetById0(IntPtr engineHandle, ulong id, out IntPtr filter);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr p);
    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ConvertInterfaceAliasToLuid(string interfaceAlias, out ulong luid);
}
