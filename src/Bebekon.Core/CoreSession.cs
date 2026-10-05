using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

public sealed record LoopbackBridge(int Port, string Password);
public sealed class XhttpRuntime
{
    public LoopbackBridge Direct { get; }
    public IReadOnlyDictionary<string, LoopbackBridge> Bridges { get; }
    private XhttpRuntime(LoopbackBridge direct, Dictionary<string, LoopbackBridge> bridges) { Direct = direct; Bridges = bridges; }
    internal static XhttpRuntime? Create(ConnectSpec spec, bool probeOnly)
    {
        RuleValidation.Validate(spec.Profile);
        var nodes = new List<Server> { spec.Server };
        if (!probeOnly)
        {
            foreach (var id in spec.Profile.Rules.Where(r => r.UseVpn && r.ServerId is not null).Select(r => r.ServerId!).Distinct().Where(id => id != spec.Server.Id))
            { var found = spec.RuleServers?.Where(s => s.Id == id).ToArray(); if (found?.Length != 1) throw new UserError("Сервер правила недоступен."); nodes.Add(found[0]); }
        }
        if (nodes.Count > 65) throw new UserError("Слишком много дополнительных серверов.");
        var xhttp = nodes.Where(s => s.Transport == "xhttp").ToArray(); if (xhttp.Length == 0) return null;
        var used = new HashSet<int> { spec.ProbePort, spec.OriginProbePort, 17890 };
        LoopbackBridge NewBridge() { int port; do { port = LatencyService.FreePort(); } while (!used.Add(port)); return new(port, Convert.ToHexString(RandomNumberGenerator.GetBytes(24))); }
        return new(NewBridge(), xhttp.ToDictionary(s => s.Id, _ => NewBridge()));
    }
    internal string Generate(ConnectSpec spec)
    {
        var nodes = new[] { spec.Server }.Concat(spec.RuleServers ?? []).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var inbound = new JsonArray(); var outbound = new JsonArray(); var rules = new JsonArray();
        foreach (var entry in Bridges)
        {
            var s = nodes[entry.Key]; ProtocolConfig.Validate(s); var tag = "xhttp-" + outbound.Count;
            inbound.Add(new JsonObject { ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = entry.Value.Port, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "password", ["accounts"] = new JsonArray { new JsonObject { ["user"] = "bebekon", ["pass"] = entry.Value.Password } }, ["udp"] = s.UdpEnabled, ["ip"] = "127.0.0.1" } });
            var stream = new JsonObject { ["network"] = "xhttp", ["security"] = s.Security, ["xhttpSettings"] = XhttpConfig.Options(s), ["sockopt"] = new JsonObject { ["dialerProxy"] = "direct-bridge" } };
            if (s.Security == "tls")
            { var tls = new JsonObject { ["serverName"] = s.Sni.Length > 0 ? s.Sni : s.Host, ["allowInsecure"] = false }; if (s.CertificatePin.Length > 0) tls["pinnedPeerCertSha256"] = s.CertificatePin; if (s.VerifyCertificateName.Length > 0) tls["verifyPeerCertByName"] = s.VerifyCertificateName; if (s.Fingerprint.Length > 0) tls["fingerprint"] = s.Fingerprint; if (s.Alpn.Count > 0) tls["alpn"] = new JsonArray(s.Alpn.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()); stream["tlsSettings"] = tls; }
            else if (s.Security == "reality") stream["realitySettings"] = new JsonObject { ["serverName"] = s.Sni, ["fingerprint"] = s.Fingerprint.Length > 0 ? s.Fingerprint : "chrome", ["password"] = s.PublicKey, ["shortId"] = s.ShortId };
            outbound.Add(new JsonObject { ["tag"] = tag, ["protocol"] = "vless", ["settings"] = new JsonObject { ["vnext"] = new JsonArray { new JsonObject { ["address"] = s.Host, ["port"] = s.Port, ["users"] = new JsonArray { new JsonObject { ["id"] = s.Uuid, ["encryption"] = "none" } } } } }, ["streamSettings"] = stream });
            rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(tag), ["outboundTag"] = tag });
        }
        // Remote XHTTP sockets go through sing-box's direct outbound. They never re-enter TUN routing.
        outbound.Add(new JsonObject { ["tag"] = "direct-bridge", ["protocol"] = "socks", ["settings"] = new JsonObject { ["servers"] = new JsonArray { new JsonObject { ["address"] = "127.0.0.1", ["port"] = Direct.Port, ["users"] = new JsonArray { new JsonObject { ["user"] = "bebekon", ["pass"] = Direct.Password } } } } } });
        return new JsonObject { ["log"] = new JsonObject { ["loglevel"] = "warning" }, ["inbounds"] = inbound, ["outbounds"] = outbound, ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules } }.ToJsonString(Json.Options);
    }
}

/// <summary>Owns all children and private runtime configs for one connection or probe.</summary>
public sealed class CoreSession(string executable, SafeLog log) : IDisposable
{
    private CoreProcess? primary;
    private readonly List<CoreProcess> companions = [];
    private readonly List<string> configs = [];
    private int stopping;
    private readonly object shutdownGate = new();
    internal int? CompanionPid { get { lock (shutdownGate) return companions.FirstOrDefault()?.Pid; } }
    internal int[] CompanionPids { get { lock (shutdownGate) return companions.Select(c => c.Pid).OfType<int>().ToArray(); } }
    public bool Running { get { lock (shutdownGate) return stopping == 0 && primary?.Running == true && companions.All(c => c.Running); } }
    public int? Pid { get { lock (shutdownGate) return primary?.Pid; } }
    public event Action? Exited;
    public async Task StartAsync(ConnectSpec spec, string directory, bool probeOnly, CancellationToken ct, Func<string, string>? decorate = null)
    {
        lock (shutdownGate) if (primary is not null || stopping != 0) throw new UserError("Подключение уже запущено или остановлено.");
        var starts = new List<Task>();
        try
        {
            ct.ThrowIfCancellationRequested();
            var runtime = XhttpRuntime.Create(spec, probeOnly);
            var trustTunnel = TrustTunnelRuntime.Create(spec, probeOnly, runtime);
            var config = ConfigGenerator.Generate(spec, probeOnly, runtime, trustTunnel);
            Directory.CreateDirectory(directory);
            var primaryPath = await WriteConfigAsync(directory, "sing-box.json", decorate?.Invoke(config) ?? config, ct);
            var main = Own(executable, false, false, true);
            if (runtime is not null)
            {
                var path = await WriteConfigAsync(directory, "xray.json", runtime.Generate(spec), ct);
                var xray = Own(Path.Combine(Path.GetDirectoryName(executable)!, "xray.exe"), true, false);
                await xray.StartAsync(path, ct);
            }
            // TrustTunnel's TLS/QUIC endpoint relays must exist before it can connect.
            await main.StartAsync(primaryPath, ct);
            if (trustTunnel is not null)
            {
                int index = 0;
                foreach (var node in trustTunnel.Nodes.Values)
                {
                    var path = await WriteConfigAsync(directory, "trusttunnel-" + index++ + ".toml",
                        TrustTunnelConfig.Generate(node.Server, node.Socks, node.Relays.Select(r => r.Port)), ct);
                    var client = Own(Path.Combine(Path.GetDirectoryName(executable)!, "trusttunnel_client.exe"), false, true);
                    starts.Add(client.StartAsync(path, ct));
                }
                // Await every start even after failure, so cancelled/failed startup cannot leave a late child.
                await Task.WhenAll(starts);
            }
            if (!Running) throw new UserError("Ядро завершилось при подключении.");
        }
        catch { Dispose(); try { await Task.WhenAll(starts); } catch { } Dispose(); throw; }
    }
    private CoreProcess Own(string path, bool xray, bool trustTunnel, bool main = false)
    {
        lock (shutdownGate)
        {
            if (stopping != 0) throw new UserError("Подключение остановлено.");
            var child = new CoreProcess(path, log, xray, trustTunnel); child.Exited += ChildExited;
            if (main) primary = child; else companions.Add(child);
            return child;
        }
    }
    private async Task<string> WriteConfigAsync(string directory, string name, string content, CancellationToken ct)
    {
        var path = Path.Combine(directory, name);
        lock (shutdownGate) { if (stopping != 0) throw new UserError("Подключение остановлено."); configs.Add(path); }
        await File.WriteAllTextAsync(path, content, ct);
        ct.ThrowIfCancellationRequested();
        lock (shutdownGate) if (stopping != 0) throw new UserError("Подключение остановлено.");
        return path;
    }
    public static async Task ValidateAsync(ConnectSpec spec, string executable, string directory, CancellationToken ct)
    {
        var runtime = XhttpRuntime.Create(spec, false);
        var trustTunnel = TrustTunnelRuntime.Create(spec, false, runtime);
        var config = ConfigGenerator.Generate(spec, false, runtime, trustTunnel);
        Directory.CreateDirectory(directory);
        var mainPath = Path.Combine(directory, "validate-sing-box.json"); var xrayPath = Path.Combine(directory, "validate-xray.json");
        try
        {
            await File.WriteAllTextAsync(mainPath, config, ct);
            using var checker = new CoreProcess(executable, new(directory, "validate")); await checker.ValidateAsync(mainPath, ct);
            if (runtime is not null)
            {
                await File.WriteAllTextAsync(xrayPath, runtime.Generate(spec), ct);
                using var xray = new CoreProcess(Path.Combine(Path.GetDirectoryName(executable)!, "xray.exe"), new(directory, "validate"), true);
                await xray.ValidateAsync(xrayPath, ct);
            }
            if (trustTunnel is not null)
            {
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "trusttunnel_client.exe"))) throw new UserError("Клиент TrustTunnel отсутствует. Обновите приложение.");
                foreach (var node in trustTunnel.Nodes.Values) TrustTunnelConfig.Generate(node.Server, node.Socks, node.Relays.Select(r => r.Port));
            }
        }
        finally { File.Delete(mainPath); File.Delete(xrayPath); }
    }
    private void ChildExited()
    {
        if (Volatile.Read(ref stopping) != 0) return;
        Dispose(); Exited?.Invoke();
    }
    public void Dispose()
    {
        lock (shutdownGate)
        {
            if (Interlocked.Exchange(ref stopping, 1) == 0)
            {
                // Unsubscribe every child before terminating any of them.
                if (primary is { } p) p.Exited -= ChildExited;
                foreach (var c in companions) c.Exited -= ChildExited;
                primary?.Dispose(); primary = null;
                foreach (var c in companions) c.Dispose();
                companions.Clear();
            }
            // Repeat cleanup after an in-flight config write observes shutdown.
            foreach (var path in configs)
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write("Runtime config cleanup failed: " + e.GetType().Name); }
        }
    }
}
