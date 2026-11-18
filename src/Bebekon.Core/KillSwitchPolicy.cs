using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bebekon.Core;

[SupportedOSPlatform("windows")]
public sealed partial class KillSwitch
{
    private static readonly Guid PolicyProvider = new("acf21de2-960b-480e-a43c-0b836110f1e4");
    private const uint FilterNotFound = 0x80320003;
    private const uint ProviderNotFound = 0x80320005;

    public static string[] TrustedTransports(string coreDirectory)
    {
        IEnumerable<string> paths = new[] { "sing-box.exe", "xray.exe", "trusttunnel_client.exe" }
            .Select(name => Path.Combine(coreDirectory, name));
        if (NetBirdClient.FindExecutable() is { } netBirdExecutable) paths = paths.Append(netBirdExecutable);
        return paths.ToArray();
    }

    public static int ApplyPersistent(ConnectSpec spec, IEnumerable<string> trustedTransports, bool tunnelReady)
        => ApplyPersistent(PolicyProvider, spec, trustedTransports, tunnelReady);

    internal static int ApplyPersistent(Guid providerKey, ConnectSpec spec, IEnumerable<string> trustedTransports, bool tunnelReady)
    {
        RuleValidation.Validate(spec.Profile);
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: false);
        var blocked = new List<ulong>();
        try
        {
            InTransaction(engine, () =>
            {
                var provider = memory.Alloc(16);
                WriteGuid(provider, providerKey);
                if (ProviderExists(engine, providerKey))
                    RemoveProviderObjects(memory, engine, provider);
                else
                    AddPolicyProvider(memory, engine, providerKey);

                var transports = ReadAppConditions(memory, trustedTransports);
                if (spec.Settings.KillSwitch)
                {
                    var global = AddSublayer(memory, engine, provider);
                    AddGlobalFilters(memory, engine, global, provider, transports, spec.NetBird, true, blocked);
                }
                var vpn = tunnelReady ? FindInterface(memory, "Bebekon") : null;
                var netBird = FindInterface(memory, spec.NetBird?.InterfaceName);
                foreach (var rule in spec.Profile.ActiveRules.Where(rule => rule.SystemProtection))
                {
                    var allowed = rule.Target == RouteTarget.Vpn ? vpn : rule.Target == RouteTarget.NetBird ? netBird : null;
                    var sublayer = AddSublayer(memory, engine, provider);
                    AddRuleFilters(memory, engine, sublayer, provider, rule, allowed, transports, blocked);
                }
            });
            var installed = blocked.Count(id => IsEnabledBlock(engine, id));
            if (installed != blocked.Count) throw new UserError("Windows не подтвердила установку всех фильтров защиты.");
            return installed;
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static int PersistentBlockCount() => PersistentBlockCount(PolicyProvider);

    internal static int PersistentBlockCount(Guid key)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: false);
        try
        {
            if (!ProviderExists(engine, key)) return 0;
            var provider = memory.Alloc(16);
            WriteGuid(provider, key);
            var count = 0;
            ReadProviderFilters(memory, engine, provider, filter => { if (IsEnabledBlock(filter)) count++; });
            return count;
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static void ClearPersistent() => ClearPersistent(PolicyProvider);

    internal static void ClearPersistent(Guid key)
    {
        using var memory = new NativeMemory();
        var engine = OpenEngine(memory, dynamic: false);
        try
        {
            InTransaction(engine, () =>
            {
                if (!ProviderExists(engine, key)) return;
                var provider = memory.Alloc(16);
                WriteGuid(provider, key);
                RemoveProviderObjects(memory, engine, provider);
                Check(FwpmProviderDeleteByKey0(engine, in key), "FwpmProviderDeleteByKey0");
            });
        }
        finally { FwpmEngineClose0(engine); }
    }

    private static void AddRuleFilters(NativeMemory memory, IntPtr engine, Guid sublayer, IntPtr provider, RoutingRule rule,
        Condition? allowedInterface, Condition[] trustedTransports, List<ulong> blocked)
    {
        foreach (var v4 in new[] { true, false })
        {
            var layer = v4 ? ConnectV4 : ConnectV6;
            foreach (var value in rule.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Condition match;
                if (rule.Kind == RuleKind.Application) match = memory.App(value);
                else
                {
                    var prefix = IPNetwork.Parse(value);
                    if (v4 != (prefix.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) continue;
                    match = NetworkCondition(memory, RemoteAddress, prefix);
                }
                var conditions = new List<Condition> { match };
                if (rule.Port is { } port) conditions.Add(new(RemotePort, MatchEqual, TypeUInt16, port));
                if (rule.Transport is { } protocol) conditions.Add(new(IpProtocol, MatchEqual, TypeUInt8, protocol == RuleTransport.Tcp ? 6 : 17));
                if (allowedInterface is { } adapter)
                {
                    if (rule.Target == RouteTarget.Vpn)
                    {
                        var source = v4 ? V4(memory, LocalAddress, TunnelV4) : V6(memory, LocalAddress, TunnelV6);
                        AddFilter(memory, engine, sublayer, layer, 14, ActionPermit, "Protected rule through Bebekon", [.. conditions, adapter, source], provider);
                    }
                    else AddFilter(memory, engine, sublayer, layer, 14, ActionPermit, "Protected rule through NetBird", [.. conditions, adapter], provider);
                }
                blocked.Add(AddFilter(memory, engine, sublayer, layer, 0, ActionBlock, "Protected routing rule", conditions, provider));
            }
            if (rule.Kind == RuleKind.Network)
            {
                foreach (var app in trustedTransports)
                    AddFilter(memory, engine, sublayer, layer, 15, ActionPermit, "VPN transports", [app], provider);
                AddDhcp(memory, engine, sublayer, layer, v4, provider);
            }
        }
    }

    private static Condition NetworkCondition(NativeMemory memory, Guid field, IPNetwork prefix)
    {
        if (prefix.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return V6(memory, field, (prefix.BaseAddress, (byte)prefix.PrefixLength));
        var address = BinaryPrimitives.ReadUInt32BigEndian(prefix.BaseAddress.GetAddressBytes());
        var mask = prefix.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - prefix.PrefixLength);
        return V4(memory, field, (address, mask));
    }

    private static bool ProviderExists(IntPtr engine, Guid key)
    {
        var result = FwpmProviderGetByKey0(engine, in key, out var data);
        if (result == ProviderNotFound) return false;
        Check(result, "FwpmProviderGetByKey0");
        FwpmFreeMemory0(ref data);
        return true;
    }

    private static void AddPolicyProvider(NativeMemory memory, IntPtr engine, Guid key)
    {
        var data = memory.Alloc(64);
        WriteGuid(data, key);
        WriteDisplay(memory, data + 16, "Bebekon VPN persistent routing protection");
        // No provider service name. BFE loads this policy even before the demand-start helper.
        Marshal.WriteInt32(data + 32, 1);
        Check(FwpmProviderAdd0(engine, data, IntPtr.Zero), "FwpmProviderAdd0");
    }

    private static void RemoveProviderObjects(NativeMemory memory, IntPtr engine, IntPtr provider)
    {
        var keys = new List<Guid>();
        ReadProviderFilters(memory, engine, provider, filter => keys.Add(Marshal.PtrToStructure<Guid>(filter)));
        foreach (var key in keys) Check(FwpmFilterDeleteByKey0(engine, in key), "FwpmFilterDeleteByKey0");

        var template = memory.Alloc(IntPtr.Size);
        Marshal.WriteIntPtr(template, provider);
        keys.Clear();
        ReadObjects(engine, template, FwpmSubLayerCreateEnumHandle0, FwpmSubLayerEnum0, FwpmSubLayerDestroyEnumHandle0,
            item => keys.Add(Marshal.PtrToStructure<Guid>(item)));
        foreach (var key in keys) Check(FwpmSubLayerDeleteByKey0(engine, in key), "FwpmSubLayerDeleteByKey0");
    }

    private static void ReadProviderFilters(NativeMemory memory, IntPtr engine, IntPtr provider, Action<IntPtr> visit)
    {
        foreach (var layer in new[] { ConnectV4, ConnectV6 })
        {
            var template = memory.Alloc(72);
            Marshal.WriteIntPtr(template, provider);
            WriteGuid(template + 8, layer);
            Marshal.WriteInt32(template + 28, 0x10); // FWP_FILTER_ENUM_FLAG_INCLUDE_DISABLED
            Marshal.WriteInt32(template + 56, -1);  // All action types
            ReadObjects(engine, template, FwpmFilterCreateEnumHandle0, FwpmFilterEnum0, FwpmFilterDestroyEnumHandle0, visit);
        }
    }

    private delegate uint OpenEnum(IntPtr engine, IntPtr template, out IntPtr enumerator);
    private delegate uint ReadEnum(IntPtr engine, IntPtr enumerator, uint limit, out IntPtr entries, out uint copied);
    private delegate uint CloseEnum(IntPtr engine, IntPtr enumerator);

    private static void ReadObjects(IntPtr engine, IntPtr template, OpenEnum open, ReadEnum read, CloseEnum close, Action<IntPtr> visit)
    {
        Check(open(engine, template, out var handle), "FwpmCreateEnumHandle");
        try
        {
            uint count;
            do
            {
                Check(read(engine, handle, 64, out var entries, out count), "FwpmEnum");
                try { for (var i = 0; i < count; i++) visit(Marshal.ReadIntPtr(entries, i * IntPtr.Size)); }
                finally { if (entries != IntPtr.Zero) FwpmFreeMemory0(ref entries); }
            } while (count == 64);
        }
        finally { close(engine, handle); }
    }

    [DllImport("fwpuclnt.dll")] private static extern uint FwpmProviderGetByKey0(IntPtr engine, in Guid key, out IntPtr provider);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmProviderAdd0(IntPtr engine, IntPtr provider, IntPtr securityDescriptor);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmProviderDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterEnum0(IntPtr engine, IntPtr enumerator, uint limit, out IntPtr entries, out uint copied);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engine, IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr enumerator);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerEnum0(IntPtr engine, IntPtr enumerator, uint limit, out IntPtr entries, out uint copied);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerDestroyEnumHandle0(IntPtr engine, IntPtr enumerator);
}
