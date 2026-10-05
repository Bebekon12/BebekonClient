using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ProtocolTests
{
    private static string CoreExe => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
    private static string Enc(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Vmess => "vmess://" + Enc(JsonSerializer.Serialize(new { v = "2", ps = "Fixture", add = "edge.example.com", port = "443", id = CoreTests.Id, aid = "0", scy = "auto", net = "ws", type = "none", host = "cdn.example.com", path = "/ws", tls = "tls", sni = "edge.example.com" }));
    [Fact]
    public void MixedPlainAndBase64LinksIncludeEveryRequestedProtocol()
    {
        var links = new[] { $"vless://{CoreTests.Id}@edge.example.com:443?type=xhttp&mode=packet-up&path=%2Fapi", Vmess, "ss://" + Enc("aes-128-gcm:secret") + "@edge.example.com:443#SS", "trojan://p%40ss%3Aword@edge.example.com:443?sni=edge.example.com#Trojan", "hysteria://edge.example.com:443?auth=secret&upmbps=50&downmbps=100#HY", "hy2://user:secret@edge.example.com?sni=edge.example.com#HY2" };
        var text = string.Join('\n', links); var nodes = VlessParser.ParseSubscription(text);
        Assert.Equal(new[] { "vless", "vmess", "shadowsocks", "trojan", "hysteria", "hysteria2" }, nodes.Select(s => s.Type));
        Assert.All(nodes, s => Assert.True(s.Supported)); Assert.Equal(nodes.Select(s => s.Id), VlessParser.ParseSubscription(Enc(text)).Select(s => s.Id));
        Assert.Equal("p@ss:word", nodes[3].Password); Assert.Equal("user:secret", nodes[5].Password); Assert.Equal(443, nodes[5].Port);
        Assert.Equal("/ws", nodes[1].Path); Assert.Equal("cdn.example.com", nodes[1].TransportHost);
        Assert.Equal("Hysteria 2 · QUIC", nodes[5].Protocol);
    }
    [Theory]
    [InlineData("aes-128-gcm:p@ss", "aes-128-gcm", "p@ss")]
    [InlineData("chacha20-ietf-poly1305:p:ss", "chacha20-ietf-poly1305", "p:ss")]
    public void ShadowsocksSip002LegacyAndPlainCredentials(string credentials, string cipher, string password)
    {
        foreach (var link in new[] { "ss://" + Enc(credentials) + "@edge.example.com:8388#SS", "ss://" + Enc(credentials + "@edge.example.com:8388") + "#SS", "ss://" + Uri.EscapeDataString(credentials.Split(':')[0]) + ":" + Uri.EscapeDataString(credentials[(credentials.IndexOf(':') + 1)..]) + "@edge.example.com:8388#SS" })
        { var s = ProtocolParser.Parse(link); Assert.Equal(cipher, s.Cipher); Assert.Equal(password, s.Password); Assert.Equal(8388, s.Port); }
    }
    [Fact]
    public void PortHoppingAndBuiltInPluginsArePreserved()
    {
        var s = ProtocolParser.Parse("hy2://secret@edge.example.com:443,5000-5010/?obfs=salamander&obfs-password=obfs-secret");
        Assert.Equal(new[] { "443", "5000:5010" }, s.ServerPorts); Assert.False(s.TlsInsecure);
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(node: s)))!;
        Assert.Null(config["outbounds"]![0]!["server_port"]); Assert.Equal("salamander", (string?)config["outbounds"]![0]!["obfs"]!["type"]);
        var ss = ProtocolParser.Parse("ss://" + Enc("aes-128-gcm:secret") + "@edge.example.com:443?plugin=obfs-local%3Bobfs%3Dtls%3Bobfs-host%3Dexample.com");
        Assert.Equal("obfs-local", ss.Plugin); Assert.Equal("obfs=tls;obfs-host=example.com", ss.PluginOptions);
        Assert.Throws<UserError>(() => ProtocolParser.Parse("ss://" + Enc("aes-128-gcm:secret") + "@edge.example.com:443?plugin=cmd.exe"));
    }
    [Fact]
    public void MixedClashExtractsProtocolsWithoutImportingProviderRules()
    {
        var nodes = VlessParser.ParseSubscription($$$$$"""
            proxies:
              - {name: VMess, type: vmess, server: edge.example.com, port: 443, uuid: {{{{{CoreTests.Id}}}}}, alterId: 0, cipher: auto, tls: true, network: ws, ws-opts: {path: /ws}}
              - {name: SS, type: ss, server: edge.example.com, port: 8388, cipher: aes-128-gcm, password: secret}
              - {name: Trojan, type: trojan, server: edge.example.com, port: 443, password: secret, sni: edge.example.com}
              - {name: HY1, type: hysteria, server: edge.example.com, port: 443, auth-str: secret, up: 50 Mbps, down: 100 Mbps, obfs: obfs-secret}
              - {name: HY2, type: hysteria2, server: edge.example.com, port: 443, password: secret, obfs: salamander, obfs-password: obfs-secret}
              - {name: XHTTP, type: vless, server: edge.example.com, port: 443, uuid: {{{{{CoreTests.Id}}}}}, tls: true, network: xhttp, xhttp-opts: {path: /api, mode: packet-up, extra: {noGRPCHeader: true, xmux: {maxConcurrency: 4}}}}
            rules: [MATCH,SomeProviderSelector]
            """);
        Assert.Equal(6, nodes.Count); Assert.All(nodes, s => Assert.True(s.Supported));
        Assert.Equal(50, nodes[3].UpMbps); Assert.Equal("packet-up", nodes[5].XhttpMode); Assert.Contains("maxConcurrency", nodes[5].XhttpExtra);
    }
    [Fact]
    public void XrayJsonImportsVmessTrojanShadowsocksAndXhttp()
    {
        var nodes = VlessParser.ParseSubscription($$$$$"""
            {"outbounds":[
              {"tag":"VMess","protocol":"vmess","settings":{"vnext":[{"address":"edge.example.com","port":443,"users":[{"id":"{{{{{CoreTests.Id}}}}}","alterId":0,"security":"auto"}]}]}},
              {"tag":"SS","protocol":"shadowsocks","settings":{"servers":[{"address":"edge.example.com","port":8388,"method":"aes-128-gcm","password":"secret"}]}},
              {"tag":"Trojan","protocol":"trojan","settings":{"servers":[{"address":"edge.example.com","port":443,"password":"secret"}]},"streamSettings":{"security":"tls","tlsSettings":{"serverName":"edge.example.com"}}},
              {"tag":"XHTTP","protocol":"vless","settings":{"address":"edge.example.com","port":443,"id":"{{{{{CoreTests.Id}}}}}"},"streamSettings":{"network":"xhttp","security":"tls","tlsSettings":{"serverName":"edge.example.com"},"xhttpSettings":{"path":"/api","mode":"stream-up","extra":{"noGRPCHeader":true}}}}
            ]}
            """);
        Assert.Equal(new[] { "vmess", "shadowsocks", "trojan", "vless" }, nodes.Select(s => s.Type)); Assert.All(nodes, s => Assert.True(s.Supported));
        Assert.Equal("stream-up", nodes[3].XhttpMode);
    }
    [Fact]
    public void NewCredentialsAreRedactedAndPersistWithoutChangingOldVlessNodes()
    {
        var s = ProtocolParser.Parse("trojan://private-secret@edge.example.com:443");
        Assert.DoesNotContain("private-secret", new Subscription { Source = "trojan://private-secret@edge.example.com:443" }.SafeSource);
        foreach (var scheme in new[] { "vmess", "ss", "trojan", "hysteria", "hysteria2", "hy2" }) Assert.DoesNotContain("private-secret", SafeLog.Redact(scheme + "://private-secret@edge.example.com:443"));
        Assert.Equal("vless", JsonSerializer.Deserialize<Server>("{\"host\":\"edge.example.com\"}", Json.Options)!.Type);
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"));
        try { var store = new StateStore(root); store.Save(new() { Servers = [s], SelectedServerId = s.Id }); var restored = Assert.Single(store.Load().Servers); Assert.Equal(s.Password, restored.Password); Assert.Equal(s.Type, restored.Type); Assert.Equal(ServerRefresh.ConnectionKey(s), ServerRefresh.ConnectionKey(restored)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("{\"downloadSettings\":{\"sockopt\":{\"dialerProxy\":\"unsafe\"}}}")]
    [InlineData("{\"headers\":{\"X-Test\":\"line\\r\\nInjected: value\"}}")]
    [InlineData("{\"xmux\":{\"maxConcurrency\":100000000}}")] [InlineData("{\"noGRPCHeader\":true,\"noGRPCHeader\":false}")]
    public void XhttpDoesNotAcceptArbitraryConfigsOrUnboundedTuning(string extra) => Assert.Throws<UserError>(() => VlessParser.Parse($"vless://{CoreTests.Id}@edge.example.com:443?type=xhttp&extra=" + Uri.EscapeDataString(extra)));

    [Theory]
    [InlineData("vmess", "auto")] [InlineData("vmess", "aes-128-gcm")]
    [InlineData("shadowsocks", "aes-128-gcm")] [InlineData("shadowsocks", "2022-blake3-aes-128-gcm")]
    [InlineData("trojan", "")] [InlineData("hysteria", "")] [InlineData("hysteria2", "")]
    public async Task NativeProtocolsActuallyCarryTrafficAndStop(string type, string cipher)
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start(); var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var responder = Respond(target, lifetime.Token);
        try
        {
            var port = LatencyService.FreePort(); var password = cipher.StartsWith("2022-") ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) : "fixture-password";
            var s = new Server { Type = type, Host = "127.0.0.1", Port = port, Uuid = CoreTests.Id, Password = password, Cipher = cipher, Fingerprint = "", UpMbps = 100, DownMbps = 100 };
            var inbound = new JsonObject { ["type"] = type, ["listen"] = "127.0.0.1", ["listen_port"] = port }; string? trustedFixtureCertificate = null;
            if (type == "vmess") inbound["users"] = new JsonArray { new JsonObject { ["uuid"] = CoreTests.Id } };
            if (type == "shadowsocks") { inbound["method"] = cipher; inbound["password"] = password; }
            if (type == "trojan" || type == "hysteria2") inbound["users"] = new JsonArray { new JsonObject { ["password"] = password } };
            if (type == "hysteria") { inbound["users"] = new JsonArray { new JsonObject { ["auth_str"] = password } }; inbound["up_mbps"] = 100; inbound["down_mbps"] = 100; }
            if (type is "trojan" or "hysteria" or "hysteria2")
            {
                using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var certPath = Path.Combine(root, "cert.pem"); var keyPath = Path.Combine(root, "key.pem"); await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem()); await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
                inbound["tls"] = new JsonObject { ["enabled"] = true, ["certificate_path"] = certPath, ["key_path"] = keyPath }; s.Security = "tls"; s.Sni = "localhost"; trustedFixtureCertificate = certificate.ExportCertificatePem();
            }
            var config = new JsonObject { ["log"] = new JsonObject { ["level"] = "info" }, ["inbounds"] = new JsonArray(inbound), ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["inet4_bind_address"] = "127.0.0.2" } } };
            var path = Path.Combine(root, "fixture.json"); await File.WriteAllTextAsync(path, config.ToJsonString()); using var server = new CoreProcess(CoreExe, new(root, "server")); await server.StartAsync(path, lifetime.Token);
            var spec = new ConnectSpec(s, new(), new(), LatencyService.FreePort(), new string('b', 48));
            using var client = new CoreSession(CoreExe, new(root, "client")); await client.StartAsync(spec, Path.Combine(root, "client"), true, lifetime.Token, decorate: generated => {
                if (trustedFixtureCertificate is null) return generated;
                // Trust only this fixture certificate, without changing the user's root store or disabling verification.
                var document = JsonNode.Parse(generated)!; document["outbounds"]![0]!["tls"]!["certificate"] = new JsonArray(trustedFixtureCertificate); return document.ToJsonString();
            });
            using var http = LatencyService.ProbeClient(spec); Assert.Equal("127.0.0.2", await http.GetStringAsync($"http://127.0.0.1:{targetPort}/", lifetime.Token));
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var echo = EchoUdp(udp, lifetime.Token);
            Assert.Equal("udp-fixture", await SocksUdp(spec, ((IPEndPoint)udp.Client.LocalEndPoint!).Port, lifetime.Token)); await echo;
            Assert.True(client.Running); client.Dispose(); Assert.False(client.Running); Assert.False(File.Exists(Path.Combine(root, "client", "sing-box.json")));
        }
        finally { lifetime.Cancel(); target.Stop(); try { await responder; } catch (OperationCanceledException) { } Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("auto", false)] [InlineData("packet-up", false)] [InlineData("stream-up", false)] [InlineData("stream-one", false)] [InlineData("auto", true)] [InlineData("packet-up", true)]
    public async Task XhttpCompanionCarriesTrafficWithAuthenticatedDirectBridge(string mode, bool tls)
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start(); var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var responder = Respond(target, lifetime.Token);
        try
        {
            var port = LatencyService.FreePort();
            var fixture = $$$$$"""{"log":{"loglevel":"warning"},"inbounds":[{"listen":"127.0.0.1","port":{{{{{port}}}}},"protocol":"vless","settings":{"clients":[{"id":"{{{{{CoreTests.Id}}}}}"}],"decryption":"none"},"streamSettings":{"network":"xhttp","xhttpSettings":{"path":"/api","mode":"{{{{{mode}}}}}"}}}],"outbounds":[{"protocol":"freedom","sendThrough":"127.0.0.2"}]}""";
            string certificatePin = "";
            if (tls)
            {
                using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var certificatePath = Path.Combine(root, "cert.pem"); var keyPath = Path.Combine(root, "key.pem");
                certificatePin = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
                await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem()); await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
                var config = JsonNode.Parse(fixture)!; var transport = config["inbounds"]![0]!["streamSettings"]!; transport["security"] = "tls";
                transport["tlsSettings"] = new JsonObject { ["alpn"] = new JsonArray("h2", "http/1.1"), ["certificates"] = new JsonArray { new JsonObject { ["certificateFile"] = certificatePath, ["keyFile"] = keyPath } } }; fixture = config.ToJsonString();
            }
            var path = Path.Combine(root, "fixture.json"); await File.WriteAllTextAsync(path, fixture);
            using var server = new CoreProcess(Path.Combine(Path.GetDirectoryName(CoreExe)!, "xray.exe"), new(root, "server"), true); await server.StartAsync(path, lifetime.Token);
            var node = VlessParser.Parse($"vless://{CoreTests.Id}@127.0.0.1:{port}?type=xhttp&path=%2Fapi&mode={mode}");
            if (tls) { node.Security = "tls"; node.Sni = "localhost"; node.CertificatePin = certificatePin; }
            var spec = new ConnectSpec(node, new(), new(), LatencyService.FreePort(), new string('b', 48));
            using var client = new CoreSession(CoreExe, new(root, "client")); await client.StartAsync(spec, Path.Combine(root, "client"), true, lifetime.Token);
            using var http = LatencyService.ProbeClient(spec); Assert.Equal("127.0.0.2", await http.GetStringAsync($"http://127.0.0.1:{targetPort}/", lifetime.Token));
            var generated = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "client", "xray.json")))!;
            Assert.Equal("password", (string?)generated["inbounds"]![0]!["settings"]!["auth"]);
            Assert.Equal("direct-bridge", (string?)generated["outbounds"]![0]!["streamSettings"]!["sockopt"]!["dialerProxy"]);
            var singular = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "client", "sing-box.json")))!;
            Assert.Equal("xray-direct", (string?)singular["route"]!["rules"]![0]!["inbound"]![0]);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var echo = EchoUdp(udp, lifetime.Token);
            Assert.Equal("udp-fixture", await SocksUdp(spec, ((IPEndPoint)udp.Client.LocalEndPoint!).Port, lifetime.Token)); await echo;
            var primaryPid = client.Pid!.Value; var companionPid = client.CompanionPid!.Value;
            if (mode == "auto") { using var crashed = Process.GetProcessById(companionPid); crashed.Kill(); await crashed.WaitForExitAsync(lifetime.Token); while (client.Running) await Task.Delay(20, lifetime.Token); }
            else if (mode == "packet-up") { using var crashed = Process.GetProcessById(primaryPid); crashed.Kill(); await crashed.WaitForExitAsync(lifetime.Token); while (client.Running) await Task.Delay(20, lifetime.Token); }
            client.Dispose(); Assert.False(client.Running); Assert.False(File.Exists(Path.Combine(root, "client", "xray.json")));
            await Task.Delay(150, lifetime.Token); Assert.Throws<ArgumentException>(() => Process.GetProcessById(primaryPid)); Assert.Throws<ArgumentException>(() => Process.GetProcessById(companionPid));
        }
        finally { lifetime.Cancel(); target.Stop(); try { await responder; } catch (OperationCanceledException) { } Directory.Delete(root, true); }
    }
    [Fact]
    public void SingBoxJsonImportsAllNativeOutboundsAndRejectsProviderPaths()
    {
        var content = JsonSerializer.Serialize(new { inbounds = new[] { new { type = "tun", auto_route = true } }, outbounds = new object[] {
            new { type = "vmess", tag = "VMess", server = "edge.example.com", server_port = 443, uuid = CoreTests.Id, security = "auto" },
            new { type = "shadowsocks", tag = "SS", server = "edge.example.com", server_port = 8388, method = "aes-128-gcm", password = "secret" },
            new { type = "trojan", tag = "Trojan", server = "edge.example.com", server_port = 443, password = "secret", tls = new { enabled = true, server_name = "edge.example.com" } },
            new { type = "hysteria", tag = "HY1", server = "edge.example.com", server_port = 443, auth_str = "secret", up_mbps = 100, down_mbps = 100, tls = new { enabled = true } },
            new { type = "hysteria2", tag = "HY2", server = "edge.example.com", server_port = 443, password = "secret", tls = new { enabled = true } },
            new { type = "direct", tag = "direct" }, new { type = "selector", tag = "selector", outbounds = new[] { "VMess", "HY2" } }
        } });
        var nodes = VlessParser.ParseSubscription(content); Assert.Equal(5, nodes.Count); Assert.All(nodes, s => Assert.True(s.Supported));
        var raw = JsonNode.Parse(content)!; raw["outbounds"]![2]!["tls"]!["certificate_path"] = "C:/untrusted.pem";
        var unsupported = VlessParser.ParseSubscription(raw.ToJsonString()); Assert.False(unsupported[2].Supported); Assert.True(unsupported[0].Supported);
    }
    [Fact]
    public async Task XhttpRuleServerAndTlsRealitySchemasAreValidatedByBothCores()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var security in new[] { "none", "tls", "reality" })
            {
                var selected = ProtocolParser.Parse("trojan://fixture-secret@edge.example.com:443");
                var xhttp = VlessParser.Parse($"vless://{CoreTests.Id}@edge.example.com:443?type=xhttp&security={security}&sni=edge.example.com&pbk={new string('A', 43)}&sid=abcd&extra=" + Uri.EscapeDataString("{\"noGRPCHeader\":true,\"xPaddingBytes\":\"100-1000\",\"xmux\":{\"maxConcurrency\":4}}"));
                var spec = new ConnectSpec(selected, new() { Rules = [new() { Name = "Pinned", Values = ["example.com"], ServerId = xhttp.Id }] }, new(), LatencyService.FreePort(), new string('b', 48), [xhttp]);
                if (security == "none") xhttp.UdpEnabled = false;
                await CoreSession.ValidateAsync(spec, CoreExe, root, default);
                var runtime = XhttpRuntime.Create(spec, false); var config = JsonNode.Parse(ConfigGenerator.Generate(spec, false, runtime))!;
                Assert.Equal("trojan", (string?)config["outbounds"]![0]!["type"]); Assert.Equal("socks", (string?)config["outbounds"]![2]!["type"]);
                Assert.Contains("vpn-rule-1", config.ToJsonString());
                if (!xhttp.UdpEnabled) Assert.Equal("tcp", (string?)config["outbounds"]![2]!["network"]);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(20)] [InlineData(100)] [InlineData(250)] [InlineData(750)]
    public async Task CancelledXhttpStartupAndProbeRemoveChildrenAndPrivateFiles(int milliseconds)
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var silent = new TcpListener(IPAddress.Loopback, 0); silent.Start(); var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        var s = VlessParser.Parse($"vless://{CoreTests.Id}@127.0.0.1:{silentPort}?type=xhttp");
        var spec = new ConnectSpec(s, new(), new(), LatencyService.FreePort(), new string('b', 48));
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var client = new CoreSession(CoreExe, new(root, "client"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StartAsync(spec, root, true, cancellation.Token)); Assert.False(client.Running);
            Assert.False(File.Exists(Path.Combine(root, "sing-box.json"))); Assert.False(File.Exists(Path.Combine(root, "xray.json")));
            using var probeCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LatencyService(CoreExe).MeasureAsync(s, LatencyMode.HttpsGet, true, probeCancel.Token));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task EchoUdp(UdpClient udp, CancellationToken ct)
    { var packet = await udp.ReceiveAsync(ct); await udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, ct); }
    internal static async Task<string> SocksUdp(ConnectSpec spec, int port, CancellationToken ct, string target = "127.0.0.1")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5)); ct = timeout.Token;
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, spec.ProbePort, ct); await using var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, ct); var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, ct); Assert.Equal(new byte[] { 5, 2 }, greeting);
        var user = Encoding.UTF8.GetBytes("bebekon"); var password = Encoding.UTF8.GetBytes(spec.ProbePassword);
        await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Concat(new byte[] { (byte)password.Length }).Concat(password).ToArray(), ct);
        await stream.ReadExactlyAsync(greeting, ct); Assert.Equal(new byte[] { 1, 0 }, greeting);
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, ct); var header = new byte[4]; await stream.ReadExactlyAsync(header, ct); Assert.Equal(0, header[1]);
        var address = new byte[header[3] == 1 ? 4 : 16]; await stream.ReadExactlyAsync(address, ct); var portBytes = new byte[2]; await stream.ReadExactlyAsync(portBytes, ct); var relayPort = portBytes[0] * 256 + portBytes[1];
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var packet = new byte[] { 0, 0, 0, 1 }.Concat(IPAddress.Parse(target).GetAddressBytes()).Concat(new byte[] { (byte)(port >> 8), (byte)port }).Concat(Encoding.UTF8.GetBytes("udp-fixture")).ToArray();
        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, relayPort), ct); var reply = await udp.ReceiveAsync(ct); Assert.Equal(0, reply.Buffer[2]);
        var skip = reply.Buffer[3] == 1 ? 10 : reply.Buffer[3] == 4 ? 22 : 7 + reply.Buffer[4]; return Encoding.UTF8.GetString(reply.Buffer[skip..]);
    }
    private static async Task Respond(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var tcp = await listener.AcceptTcpClientAsync(ct); await using var stream = tcp.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
            var body = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString(); await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);
        }
    }
}
