using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ClashSubscriptionTests
{
    private static string Node(string options = "") => $"name: '🇪🇪 Estonia · fixture'\ntype: vless\nserver: edge.example.com\nport: 443\nuuid: {CoreTests.Id}\nudp: true\n{options}";
    private static string Profile(string options = "") => "proxies:\n  - " + Node(options).TrimEnd().Replace("\n", "\n    ");
    private static Server Parse(string options = "") => Assert.Single(VlessParser.ParseSubscription(Profile(options)));

    [Fact]
    public void FullSnowStyleProfileExtractsFiveServersWithoutProviderRouting()
    {
        var yaml = "mixed-port: 12345\nmode: rule\ndns: {nameserver: [provider-dns.example]}\ntun: {enable: true}\nproxies:\n";
        for (var i = 0; i < 5; i++) yaml += "  - " + Node("network: tcp\npacket-encoding: xudp\nclient-fingerprint: chrome\n").Replace("edge.example.com", $"edge{i}.example.com").TrimEnd().Replace("\n", "\n    ") + "\n";
        yaml += "proxy-groups:\n  - {name: Provider, type: select, proxies: ['🇪🇪 Estonia · fixture']}\nrules: ['MATCH,Provider']\n";
        var servers = VlessParser.ParseSubscription(yaml);
        Assert.Equal(5, servers.Count);
        Assert.All(servers, server =>
        {
            Assert.True(server.Supported); Assert.Equal("none", server.Security); Assert.Equal("xudp", server.PacketEncoding);
            var profile = new Profile { Rules = [new() { Name = "My site", Values = ["my-site.example"] }] };
            var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile, server)))!;
            Assert.Equal("direct", (string?)config["route"]!["final"]);
            Assert.Contains("my-site.example", config.ToJsonString());
            Assert.DoesNotContain("provider-dns.example", config.ToJsonString()); Assert.DoesNotContain("12345", config.ToJsonString());
            Assert.Single(profile.Rules);
        });
    }

    [Fact]
    public void JsonFlowYamlAnchorsAndBase64HaveStableIdentity()
    {
        var node = Parse("packet-encoding: xudp");
        var json = new JsonObject { ["proxies"] = new JsonArray(new JsonObject
        {
            ["uuid"] = CoreTests.Id, ["port"] = 443, ["server"] = "edge.example.com", ["type"] = "vless",
            ["name"] = "🇪🇪 Estonia · fixture", ["udp"] = true, ["packet-encoding"] = "xudp"
        }) }.ToJsonString();
        Assert.Equal(node.Id, Assert.Single(VlessParser.ParseSubscription(json)).Id);
        var flow = $"{{proxies: [{{type: vless, name: '🇪🇪 Estonia · fixture', server: edge.example.com, port: 443, uuid: {CoreTests.Id}, udp: true, packet-encoding: xudp}}]}}";
        Assert.Equal(node.Id, Assert.Single(VlessParser.ParseSubscription(flow)).Id);
        var anchors = $"defaults: &base {{type: vless, server: wrong.example, port: 443, uuid: {CoreTests.Id}, udp: true}}\nproxies:\n  - <<: *base\n    server: edge.example.com\n    name: '🇪🇪 Estonia · fixture'\n    packet-encoding: xudp\n";
        Assert.Equal(node.Id, Assert.Single(VlessParser.ParseSubscription(anchors)).Id);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("\uFEFF\n---\n# fixture\n" + Profile("packet-encoding: xudp")));
        Assert.Equal(node.Id, Assert.Single(VlessParser.ParseSubscription(encoded)).Id);
    }

    [Fact]
    public void RealityAndTlsFingerprintAreMappedWithoutConfusingCertificatePin()
    {
        var server = Parse("tls: true\nflow: xtls-rprx-vision\nservername: tls.example.com\nclient-fingerprint: firefox\nreality-opts:\n  public-key: " + new string('A', 43) + "\n  short-id: 'abcd'\nalpn: [h2]");
        Assert.True(server.Supported); Assert.Equal("reality", server.Security); Assert.Equal("firefox", server.Fingerprint);
        Assert.Equal("tls.example.com", server.Sni); Assert.Equal("abcd", server.ShortId); Assert.Equal("xtls-rprx-vision", server.Flow);
        Assert.Equal(new[] { "h2" }, server.Alpn);
        Assert.False(Parse("tls: true\nfingerprint: certificate-pin").Supported);
    }

    [Theory]
    [InlineData("grpc", "grpc-opts:\n  grpc-service-name: edge/service", "grpc")]
    [InlineData("ws", "ws-opts:\n  path: /edge?a=b&c=d\n  headers: {Host: cdn.example.com}", "ws")]
    [InlineData("ws", "ws-opts:\n  path: /edge?a=b&c=d\n  headers: {Host: cdn.example.com}\n  v2ray-http-upgrade: true", "httpupgrade")]
    [InlineData("h2", "h2-opts:\n  path: /edge?a=b&c=d\n  host: [cdn.example.com]", "http")]
    public void MapsTlsTransports(string network, string options, string mapped)
    {
        var node = Parse("tls: true\nnetwork: " + network + "\n" + options);
        Assert.True(node.Supported); Assert.Equal(mapped, node.Transport);
        if (network == "grpc") Assert.Equal("edge/service", node.ServiceName);
        else { Assert.Equal("/edge?a=b&c=d", node.Path); Assert.Equal("cdn.example.com", node.TransportHost); }
    }

    [Fact]
    public void WebSocketHostSuppliesMissingTlsSniAndLegacyPacketFlagsArePreserved()
    {
        var ws = Parse("tls: true\nnetwork: ws\nws-opts: {headers: {hOsT: cdn.example.com}}");
        Assert.True(ws.Supported); Assert.Equal("cdn.example.com", ws.TransportHost); Assert.Equal("cdn.example.com", ws.Sni);
        var explicitSni = Parse("tls: true\nservername: tls.example.com\nnetwork: ws\nws-opts: {headers: {Host: cdn.example.com}}");
        Assert.Equal("tls.example.com", explicitSni.Sni);
        Assert.Equal("packetaddr", Parse("packet-addr: true").PacketEncoding);
        Assert.Equal("xudp", Parse("packet-addr: true\nxudp: true").PacketEncoding);
        var raw = CoreTests.Node("packetEncoding=none");
        var rawConfig = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(node: raw)))!;
        Assert.Equal("", (string?)rawConfig["outbounds"]![0]!["packet_encoding"]);
    }

    [Theory] [InlineData("none")] [InlineData("xudp")] [InlineData("packetaddr")]
    public void UdpEncodingAndDisableFlagReachCore(string encoding)
    {
        var node = Parse("packet-encoding: " + encoding);
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(node: node)))!;
        Assert.Equal(encoding == "none" ? "xudp" : encoding, (string?)config["outbounds"]![0]!["packet_encoding"]);
        var tcpOnly = Assert.Single(VlessParser.ParseSubscription(Profile().Replace("udp: true", "udp: false")));
        var tcpConfig = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(node: tcpOnly)))!;
        Assert.Equal("tcp", (string?)tcpConfig["outbounds"]![0]!["network"]);
        Assert.Equal("xudp", tcpOnly.PacketEncoding);
        Assert.NotEqual(ServerRefresh.ConnectionKey(node), ServerRefresh.ConnectionKey(tcpOnly));
    }

    [Theory]
    [InlineData("network: http")]
    [InlineData("tls: true\nskip-cert-verify: true")]
    [InlineData("network: ws\nws-opts: {max-early-data: 2048}")]
    [InlineData("network: grpc\ngrpc-opts: {grpc-user-agent: custom}")]
    [InlineData("dialer-proxy: Other")]
    [InlineData("packet-encoding: unknown")]
    public void UnsupportedWireOptionsStayVisibleAndCannotConnect(string options)
    {
        var node = Parse(options); Assert.False(node.Supported);
        Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(node: node)));
    }

    [Theory]
    [InlineData("proxies: []")]
    [InlineData("proxies: null")]
    [InlineData("proxies: [{type: trojan, password: private-secret}]")]
    [InlineData("proxies: []\nproxies: []")]
    [InlineData("proxies: &cycle [*cycle]")]
    [InlineData("proxies: []\n---\nproxies: []")]
    [InlineData("proxy-providers: {remote: {url: 'https://private.example'}}")]
    public void InvalidProfilesHaveSafeErrors(string content)
    {
        var error = Assert.Throws<UserError>(() => VlessParser.ParseSubscription(content));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public void NilUuidIsValidAcrossAllSubscriptionFormats()
    {
        var nil = Guid.Empty.ToString();
        var clash = Assert.Single(VlessParser.ParseSubscription(Profile().Replace(CoreTests.Id, nil)));
        var uri = Assert.Single(VlessParser.ParseSubscription($"vless://{nil}@edge.example.com:443#Fixture"));
        var json = SubscriptionTests.Xray("tcp", "tls");
        json["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!["id"] = nil;
        var xray = Assert.Single(VlessParser.ParseSubscription(json.ToJsonString()));
        Assert.All(new[] { clash, uri, xray }, node => { Assert.True(node.Supported); Assert.Equal(nil, node.Uuid); });
    }

    [Fact]
    public void InvalidCredentialsAndDepthAreRejectedWithoutLeaks()
    {
        var error = Assert.Throws<UserError>(() => VlessParser.ParseSubscription(Profile().Replace(CoreTests.Id, "private-secret")));
        Assert.DoesNotContain("private-secret", error.Message);
        Assert.Throws<UserError>(() => VlessParser.ParseSubscription("proxies: " + new string('[', 40) + new string(']', 40)));
        var duplicate = Profile().Replace("port: 443", "port: 443\n    port: 8443");
        Assert.Throws<UserError>(() => VlessParser.ParseSubscription(duplicate));
        var broken = Profile().Replace("name: '🇪🇪 Estonia · fixture'", "name: 'private-secret");
        Assert.DoesNotContain("private-secret", Assert.Throws<UserError>(() => VlessParser.ParseSubscription(broken)).Message);
    }

    [Fact]
    public async Task ProviderNegotiationAndPastedYamlUseSameImporter()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            var headers = new List<string>(); string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token)) is { Length: > 0 }) headers.Add(line);
            Assert.Contains("User-Agent: clash.meta", headers.Select(header => header.StartsWith("User-Agent: clash.meta") ? "User-Agent: clash.meta" : ""));
            Assert.Contains(true, headers.Select(header => header.StartsWith("x-hwid:") && System.Text.RegularExpressions.Regex.IsMatch(header[7..].Trim(), "^win-[a-f0-9]{32}$")));
            Assert.Contains("x-device-os: Windows", headers.Where(header => header.StartsWith("x-device-os:")));
            var body = Encoding.UTF8.GetBytes(Profile("packet-encoding: xudp"));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/yaml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        });
        var downloaded = Assert.Single(await SubscriptionLoader.LoadAsync($"http://127.0.0.1:{port}/subscription", timeout.Token));
        await serve;
        Assert.Equal(downloaded.Id, Assert.Single(await SubscriptionLoader.LoadAsync(Profile("packet-encoding: xudp"))).Id);
        Assert.Contains("веб-страницу", Assert.Throws<UserError>(() => VlessParser.ParseSubscription("<!DOCTYPE html><h1>private-secret</h1>")).Message);
    }

    [Fact]
    public void DeviceIdentityIsStableCanonicalAppSpecificAndDistinct()
    {
        var seed = CoreTests.Id;
        var first = SubscriptionDeviceIdentity.Derive(seed);
        Assert.Matches("^win-[a-f0-9]{32}$", first);
        Assert.Equal(first, SubscriptionDeviceIdentity.Derive(seed.ToUpperInvariant()));
        Assert.Equal(first, SubscriptionDeviceIdentity.Derive(Guid.Parse(seed).ToString("B")));
        Assert.NotEqual(first, SubscriptionDeviceIdentity.Derive("afdeca7e-73e2-4f5d-9e7b-0668cb7aa900"));
        Assert.DoesNotContain(seed, first);
        Assert.Throws<UserError>(() => SubscriptionDeviceIdentity.Derive(""));
        Assert.Throws<UserError>(() => SubscriptionDeviceIdentity.Derive(Guid.Empty.ToString()));
        Assert.True(SubscriptionDeviceIdentity.Get() == SubscriptionDeviceIdentity.Get());
    }

    [Theory]
    [InlineData("x-hwid-max-devices-reached", "лимит устройств")]
    [InlineData("x-hwid-limit", "лимит устройств")]
    [InlineData("x-hwid-not-supported", "HWID")]
    public async Task ProviderDeviceErrorsTakePrecedenceOverHttpStatusAndBody(string header, string message)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 404 Not Found\r\n{header.ToUpperInvariant()}: TRUE\r\nContent-Length: 14\r\nConnection: close\r\n\r\nprivate-secret"), timeout.Token);
        });
        var error = await Assert.ThrowsAsync<UserError>(() => SubscriptionLoader.LoadAsync($"http://127.0.0.1:{port}/subscription", timeout.Token));
        Assert.Contains(message, error.Message); Assert.DoesNotContain("private-secret", error.Message); await serve;
    }

    [Fact]
    public void ProviderPlaceholderAddressesCannotBeUsedAsServers()
    {
        foreach (var host in new[] { "0.0.0.0", "::" })
        {
            var node = Assert.Single(VlessParser.ParseSubscription(Profile().Replace("edge.example.com", "'" + host + "'").Replace(CoreTests.Id, Guid.Empty.ToString())));
            Assert.False(node.Supported); Assert.Contains("HWID", node.UnsupportedReason!);
            Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(node: node)));
        }
    }

    [Fact]
    public async Task BodyDownloadHonorsCallerCancellationAfterHeaders()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(cancel.Token);
            await using var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancel.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n"), cancel.Token);
            headersSent.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancel.Token); } catch (OperationCanceledException) { }
        });
        var loading = SubscriptionLoader.LoadAsync($"http://127.0.0.1:{port}/subscription", cancel.Token);
        await headersSent.Task.WaitAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading); await serve;
    }

    [Fact]
    public async Task OfficialCoreAcceptsClashTcpRealityAndTransports()
    {
        var root = Path.Combine(Path.GetTempPath(), "Bebekon-clash-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var options = new[] { "packet-encoding: xudp", "packet-encoding: none", "packet-encoding: packetaddr",
                "tls: true\nflow: xtls-rprx-vision\nservername: tls.example.com\nreality-opts: {public-key: '" + new string('A', 43) + "', short-id: 'abcd'}",
                "tls: true\nnetwork: grpc\ngrpc-opts: {grpc-service-name: edge/service}",
                "tls: true\nnetwork: ws\nws-opts: {path: /edge, headers: {Host: cdn.example.com}}",
                "tls: true\nnetwork: ws\nws-opts: {path: /edge, v2ray-http-upgrade: true}",
                "tls: true\nnetwork: h2\nh2-opts: {path: /edge, host: [cdn.example.com]}" };
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
            using var core = new CoreProcess(exe, new(root, "core"));
            foreach (var option in options)
            {
                var path = Path.Combine(root, "config.json");
                await File.WriteAllTextAsync(path, ConfigGenerator.Generate(CoreTests.Spec(node: Parse(option))));
                await core.ValidateAsync(path, default);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
