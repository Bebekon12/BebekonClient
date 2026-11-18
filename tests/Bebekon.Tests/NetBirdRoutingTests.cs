using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class NetBirdRoutingTests
{
    [Fact]
    public void BoundDnsProbeRejectsAnUnrelatedOrTruncatedReply()
    {
        var request = NetBirdProbe.CreateQuery("git.corp.example", 1);
        Assert.Equal(1, request[^3]); // A, followed by class IN
        Assert.Equal(1, request[^1]);
        byte[] response = [.. request, 0xc0, 12, 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 100, 100, 0, 42];
        response[2] = 0x81; response[3] = 0x80; response[7] = 1;
        Assert.Equal(IPAddress.Parse("100.100.0.42"), Assert.Single(NetBirdProbe.ReadAnswer(response, request, 1)));
        Assert.Empty(NetBirdProbe.ReadAnswer(response.AsSpan(0, response.Length - 1), request, 1));
        response[13] ^= 1;
        Assert.Empty(NetBirdProbe.ReadAnswer(response, request, 1));
        response[13] ^= 1;
        response[0] ^= 1;
        Assert.Empty(NetBirdProbe.ReadAnswer(response, request, 1));
    }

    [Fact]
    public async Task DnsAndTcpProbesUseTheSameConfirmedInterface()
    {
        var address = IPAddress.Loopback;
        var index = WindowsRoutes.BestInterfaceIndex(address);
        Assert.NotNull(index);
        using var dns = new UdpClient(new IPEndPoint(address, 0));
        using var tcp = new TcpListener(address, 0);
        tcp.Start();
        var target = (IPEndPoint)tcp.LocalEndpoint;
        var resolver = (IPEndPoint)dns.Client.LocalEndPoint!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;

        async Task Respond()
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await dns.ReceiveAsync(ct);
                var isA = request.Buffer[^3] == 1;
                byte[] response = isA
                    ? [.. request.Buffer, 0xc0, 12, 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 127, 0, 0, 1]
                    : request.Buffer;
                response[2] = 0x81; response[3] = 0x80; response[7] = (byte)(isA ? 1 : 0);
                await dns.SendAsync(response, request.RemoteEndPoint, ct);
            }
        }

        var routing = new NetBirdRouting("Local test interface", index, null, null, [new("corp.example", true, address.ToString())]);
        var dnsWork = Respond();
        try
        {
            var report = await NetBirdProbe.VerifyAsync(routing, new("git.corp.example", Port: target.Port), resolver.Port, ct);
            await dnsWork;
            using var accepted = await tcp.AcceptTcpClientAsync(ct);
            Assert.Equal(address, report.Resolver);
            Assert.Equal(address, report.Reached);
            Assert.Equal(target.Port, report.Port);
        }
        finally
        {
            deadline.Cancel();
            try { await dnsWork; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData("Connected", true, "100.100.1.2")]
    [InlineData("Idle", false, null)]
    [InlineData("Connected", false, null)]
    public void StatusRequiresAConnectedDaemonAndManagement(string state, bool connected, string? expected)
    {
        var json = $$"""
            {"daemonStatus":"{{state}}","management":{"connected":{{(connected ? "true" : "false")}}},
            "netbirdIp":"100.100.1.2/16","daemonVersion":"0.83.1","networks":["10.25.0.0/16"]}
            """;
        var status = NetBirdClient.ParseStatus(json);
        Assert.Equal(expected, status.PeerAddress);
        Assert.Equal(expected is not null, status.Connected);
        Assert.Equal(new[] { "10.25.0.0/16" }, status.Networks);
    }

    [Fact]
    public async Task NetBirdControlPlaneCanBootstrapWhenCorporateDnsIsUnavailable()
    {
        var status = NetBirdClient.ParseStatus("""
            {
                "daemonStatus": "LoginFailed",
                "management": { "url": "https://Management.Corp.Example:443/api?source=test", "connected": false },
                "signal": { "url": "https://signal.corp.example:443", "connected": false }
            }
            """);
        Assert.False(status.Connected);
        Assert.Equal(new[] { "management.corp.example", "signal.corp.example" }, status.ControlHosts);

        const string executable = @"C:\Program Files\NetBird\NetBird.exe";
        var profile = new Profile
        {
            DefaultRoute = RouteTarget.Direct,
            Rules = [new() { Name = "Corporate", Values = ["corp.example"], Destination = RouteTarget.NetBird }]
        };
        var spec = CoreTests.Spec(profile) with { NetBirdExecutable = executable, NetBirdControlHosts = status.ControlHosts };
        var raw = ConfigGenerator.Generate(spec);
        var cfg = JsonNode.Parse(raw)!;
        Assert.Equal("hijack", (string?)cfg["inbounds"]![1]!["dns_mode"]);
        Assert.True((bool)cfg["inbounds"]![1]!["strict_route"]!);
        var dns = cfg["dns"]!["rules"]!.AsArray();
        Assert.Equal("management.corp.example", (string?)dns[0]!["domain"]![0]);
        Assert.Equal("signal.corp.example", (string?)dns[0]!["domain"]![1]);
        Assert.Equal("direct-dns", (string?)dns[0]!["server"]);
        Assert.Equal("REFUSED", (string?)dns[1]!["rcode"]);

        var routes = cfg["route"]!["rules"]!.AsArray();
        var daemon = routes[2]!;
        Assert.Equal("tun-in", (string?)daemon["inbound"]![0]);
        Assert.Equal("direct", (string?)daemon["outbound"]);
        var pattern = (string)daemon["process_path_regex"]![0]!;
        Assert.Matches(pattern, executable.ToLowerInvariant());
        Assert.DoesNotMatch(pattern, @"C:\Other\NetBird.exe");
        Assert.Equal("reject", (string?)routes.Last()!["action"]);

        var withoutRegisteredDaemon = JsonNode.Parse(ConfigGenerator.Generate(spec with { NetBirdExecutable = null }))!;
        Assert.Equal("hijack", (string?)withoutRegisteredDaemon["inbounds"]![1]!["dns_mode"]);
        Assert.Equal("REFUSED", (string?)withoutRegisteredDaemon["dns"]!["rules"]![0]!["rcode"]);
        var onlyProbe = JsonNode.Parse(ConfigGenerator.Generate(spec, probeOnly: true))!;
        Assert.Empty(onlyProbe["dns"]!["rules"]!.AsArray());
        Assert.Null(JsonSerializer.SerializeToNode(spec, Json.Options)!["netBirdExecutable"]);

        var coreExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(path, raw);
            using var core = new CoreProcess(coreExe, new(root, "core"));
            await core.ValidateAsync(path, default);

            // Exercise the real DNS rules through an isolated listener, with no system TUN.
            var port = LatencyService.FreePort();
            cfg["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "direct", ["tag"] = "test-dns", ["listen"] = "127.0.0.1", ["listen_port"] = port
            });
            cfg["route"]!["rules"]!.AsArray().Insert(0, new JsonObject
            {
                ["inbound"] = new JsonArray("test-dns"), ["action"] = "hijack-dns"
            });
            cfg["dns"]!["servers"]![0] = new JsonObject
            {
                ["type"] = "hosts", ["tag"] = "direct-dns",
                ["predefined"] = new JsonObject { ["management.corp.example"] = new JsonArray("192.0.2.40") }
            };
            await File.WriteAllTextAsync(path, cfg.ToJsonString());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await core.StartAsync(path, deadline.Token);
            using var client = new UdpClient();
            client.Connect(IPAddress.Loopback, port);

            var management = NetBirdProbe.CreateQuery("management.corp.example", 1);
            await client.SendAsync(management, deadline.Token);
            var reply = await client.ReceiveAsync(deadline.Token);
            Assert.Equal(IPAddress.Parse("192.0.2.40"), Assert.Single(NetBirdProbe.ReadAnswer(reply.Buffer, management, 1)));

            var privateHost = NetBirdProbe.CreateQuery("git.corp.example", 1);
            await client.SendAsync(privateHost, deadline.Token);
            var denied = await client.ReceiveAsync(deadline.Token);
            Assert.Equal(5, denied.Buffer[3] & 0xf); // REFUSED
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ConfirmedNetBirdAdapterKeepsNativeDnsEvenWithoutExplicitCorporateRules()
    {
        var spec = CoreTests.Spec() with
        {
            NetBird = new("NetBird", 27, null, null, []),
            NetBirdExecutable = @"C:\Program Files\NetBird\netbird.exe"
        };
        Assert.False(spec.Profile.UsesNetBird);
        var cfg = JsonNode.Parse(ConfigGenerator.Generate(spec))!;
        Assert.Equal("native", (string?)cfg["inbounds"]![1]!["dns_mode"]);
    }

    [Fact]
    public void DnsPolicyUsesOnlyResolversRoutedToNetBirdAndKeepsSpecificZonesFirst()
    {
        var zones = NetBirdRouting.ReadZones("""
            [
              {"Namespace":".corp.example","NameServers":"100.100.0.53"},
              {"Namespace":[".dev.corp.example"],"NameServers":["10.10.0.53","100.100.0.54"]},
              {"Namespace":".public.example","NameServers":"10.10.0.53"},
              {"Namespace":".secure.example","NameServers":"100.100.0.53","DnsSecValidationRequired":true}
            ]
            """, ip => ip.GetAddressBytes()[0] == 100);
        Assert.Collection(zones,
            zone => Assert.Equal(new("dev.corp.example", true, "100.100.0.54"), zone),
            zone => Assert.Equal(new("public.example", true, null), zone),
            zone => Assert.Equal(new("secure.example", true, null), zone),
            zone => Assert.Equal(new("corp.example", true, "100.100.0.53"), zone));
    }

    [Fact]
    public async Task SeparateInterfaceAndScopedDnsPassRealCoreSchemaValidation()
    {
        var profile = new Profile { Rules = [new() {
            Name = "Corporate", Values = ["git.corp.example", "git.dev.corp.example", "missing.example"],
            Destination = RouteTarget.NetBird
        }] };
        var spec = CoreTests.Spec(profile) with
        {
            NetBird = new("NetBird test", 12, null, null, [
                new("dev.corp.example", true, "100.100.0.54"),
                new("corp.example", true, "100.100.0.53")
            ])
        };
        var raw = ConfigGenerator.Generate(spec);
        var config = JsonNode.Parse(raw)!;
        Assert.Equal("native", (string?)config["inbounds"]![1]!["dns_mode"]);
        var egress = config["outbounds"]!.AsArray().Skip(2).ToArray();
        Assert.All(egress, outbound => Assert.Equal("NetBird test", (string?)outbound!["bind_interface"]));
        var dns = config["dns"]!["rules"]!.AsArray();
        Assert.Equal("netbird-1-dns", (string?)dns[0]!["server"]);
        Assert.Equal("netbird-0-dns", (string?)dns[1]!["server"]);
        Assert.Equal("REFUSED", (string?)dns[2]!["rcode"]);

        var coreExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(path, raw);
            using var core = new CoreProcess(coreExe, new(root, "core"));
            await core.ValidateAsync(path, default);

            var address = new RoutingRule { Name = "IP only", Kind = RuleKind.Network, Values = ["100.100.0.55"], Destination = RouteTarget.NetBird };
            var noDns = spec with { Profile = new() { Rules = [address] }, NetBird = new("NetBird test", 12, null, null, []) };
            var ipConfig = ConfigGenerator.Generate(noDns);
            Assert.Equal("netbird-ip", (string?)JsonNode.Parse(ipConfig)!["route"]!["rules"]![4]!["outbound"]);
            await File.WriteAllTextAsync(path, ipConfig);
            await core.ValidateAsync(path, default);
        }
        finally { Directory.Delete(root, true); }
    }
}
