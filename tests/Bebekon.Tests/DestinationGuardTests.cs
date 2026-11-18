using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class DestinationGuardTests
{
    [Fact]
    public void NormalizesDnsAndBothIpFamiliesSeparately()
    {
        var guard = DestinationGuard.Parse(["*.EXAMPLE.org.", "example.org", "пример.рф", "192.0.2.9/24", "2001:db8::2"]);
        Assert.Equal(new[] { "example.org", "xn--e1afmkfd.xn--p1ai" }, guard.Domains);
        Assert.Equal(new[] { "192.0.2.0/24", "2001:db8::2/128" }, guard.Networks);
        Assert.Contains(DestinationGuard.Resolver + "/32", guard.FilterNetworks);
        Assert.DoesNotContain(DestinationGuard.FakeV4, DestinationGuard.Parse(["192.0.2.8"]).FilterNetworks);
        Assert.Empty(DestinationGuard.Parse([]).FilterNetworks);
        Assert.True(guard.Matches(new("child.example.org")));
        Assert.False(guard.Matches(new("anotherexample.org")));
        Assert.True(guard.Matches(new(Address: IPAddress.Parse("192.0.2.111"))));
        var profile = new Profile { Rules = [new() { Name="blocked", Values=["blocked.example.org"], Destination=RouteTarget.Block }] };
        Assert.Equal(RouteTarget.Block,RoutingPolicy.Analyze(profile,new("blocked.example.org"),guard).Direction);
        Assert.Equal(RouteTarget.Vpn,RoutingPolicy.Analyze(profile,new("child.example.org"),guard).Direction);
        Assert.Equal(RouteTarget.Direct,RoutingPolicy.Analyze(profile,new("other.example"),guard).Direction);
    }

    [Theory]
    [InlineData("https://example.org/test")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("*.com")]
    [InlineData("bad domain.example")]
    public void RejectsInvalidOrMachineWideEntries(string text)
        => Assert.Throws<UserError>(() => DestinationGuard.Parse([text]));

    [Fact]
    public async Task ProtectedSubdomainsReceiveOnlyGuardedAddressesFromTheRunningCore()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var relayPort = LatencyService.FreePort();
            var node = CoreTests.Node(); node.Host = "127.0.0.1"; node.Port = relayPort;
            var spec = CoreTests.Spec(node: node) with { Guard = DestinationGuard.Parse(["private.example"]), GuardCacheFile = Path.Combine(dir.FullName, "fakeip.db") };
            var config = JsonNode.Parse(ConfigGenerator.Generate(spec))!;
            // Exercise the production DNS policy over an isolated socket without creating a second machine-wide TUN.
            var port = LatencyService.FreePort();
            var proxy = LatencyService.FreePort();
            config["inbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "fixture", ["listen"] = "127.0.0.1", ["listen_port"] = port });
            config["inbounds"]!.AsArray().Add(new JsonObject { ["type"]="mixed", ["listen"]="127.0.0.1", ["listen_port"]=proxy });
            config["route"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["inbound"] = "fixture", ["action"] = "hijack-dns" });
            var file = Path.Combine(dir.FullName, "fixture.json");
            await File.WriteAllTextAsync(file, config.ToJsonString());
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../core/sing-box.exe"));
            using var core = new CoreProcess(exe, new(dir.FullName, "core"));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var relayConfig = Path.Combine(dir.FullName,"relay.json");
            await File.WriteAllTextAsync(relayConfig,$$$"""{"log":{"level":"info"},"dns":{"servers":[{"type":"hosts","tag":"hosts","predefined":{"child.private.example":["127.0.0.1"]}}]},"inbounds":[{"type":"vless","listen":"127.0.0.1","listen_port":{{{relayPort}}},"users":[{"uuid":"{{{CoreTests.Id}}}"}]}],"outbounds":[{"type":"direct","inet4_bind_address":"127.0.0.2","domain_resolver":"hosts"}]}""");
            using var relay = new CoreProcess(exe,new(dir.FullName,"relay"));
            await relay.StartAsync(relayConfig,limit.Token);
            await core.StartAsync(file, limit.Token);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Loopback, port);
            IPAddress? alias = null;
            foreach (var (qtype, range) in new[] { (1,DestinationGuard.FakeV4), (28,DestinationGuard.FakeV6), (65,"") })
            {
                var query = NetBirdProbe.CreateQuery("child.private.example", (ushort)qtype);
                await udp.SendAsync(query, limit.Token);
                var reply = (await udp.ReceiveAsync(limit.Token)).Buffer;
                Assert.Equal(0, reply[3] & 15);
                if (qtype == 65) { Assert.Equal(0, reply[6] + reply[7]); continue; }
                var addresses = NetBirdProbe.ReadAnswer(reply,query,(ushort)qtype);
                Assert.NotEmpty(addresses);
                Assert.All(addresses, ip => Assert.True(IPNetwork.Parse(range).Contains(ip)));
                if (qtype == 1) alias = addresses.First();
            }
            // The request contains only a synthetic IP. The remote VLESS relay resolves the original hostname.
            using var target = new TcpListener(IPAddress.Loopback,0); target.Start();
            using var http = new HttpClient(new SocketsHttpHandler { Proxy=new WebProxy($"http://127.0.0.1:{proxy}"), UseProxy=true });
            var request = http.GetStringAsync($"http://{alias}:{((IPEndPoint)target.LocalEndpoint).Port}/", limit.Token);
            using var accepted = await target.AcceptTcpClientAsync(limit.Token);
            Assert.Equal("127.0.0.2", ((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.ToString());
            using var reader = new StreamReader(accepted.GetStream(), leaveOpen:true);
            while ((await reader.ReadLineAsync(limit.Token)) is { Length: >0 }) { }
            await accepted.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8.ToArray(),limit.Token);
            Assert.Equal("OK",await request);
        }
        finally
        {
            for (var retry = 0; ; retry++)
            {
                try { dir.Delete(true); break; }
                catch (IOException) when (retry < 5) { await Task.Delay(50); }
            }
        }
    }

    [Fact]
    public async Task NativeCoreAcceptsGuardAndKeepsControlApiAndFakeAddressCache()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var profile = new Profile { Rules = [
                new() { Name = "Blocked", Values = ["blocked.example.org"], Destination = RouteTarget.Block },
                new() { Name = "Ordinary direct", Values = ["example.org"], Destination = RouteTarget.Direct }] };
            var spec = CoreTests.Spec(profile) with { Guard = DestinationGuard.Parse(["example.org", "192.0.2.7"]),
                GuardCacheFile = Path.Combine(dir.FullName, "cache.db") };
            using var traffic = new CoreTraffic();
            var json = traffic.AddToConfig(ConfigGenerator.Generate(spec));
            var config = JsonNode.Parse(json)!;
            Assert.NotNull(config["experimental"]!["clash_api"]);
            Assert.True((bool)config["experimental"]!["cache_file"]!["store_fakeip"]!);
            var dns = config["dns"]!["rules"]!.AsArray();
            Assert.Contains(dns, r => (string?)r?["server"] == "protected-addresses");
            Assert.Contains(dns, r => (string?)r?["rcode"] == "NOERROR");
            var route = config["route"]!["rules"]!.AsArray();
            Assert.True(route.ToList().FindIndex(r => (string?)r?["outbound"] == "vpn" && r?["domain_suffix"] != null)
                < route.ToList().FindIndex(r => (string?)r?["outbound"] == "direct" && r?["domain_suffix"] != null));
            var file = Path.Combine(dir.FullName, "guard.json");
            await File.WriteAllTextAsync(file, json);
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../core/sing-box.exe"));
            using var core = new CoreProcess(exe, new(dir.FullName,"test"));
            await core.ValidateAsync(file, default);
            Assert.DoesNotContain("protected-addresses", ConfigGenerator.Generate(spec, probeOnly:true));
        }
        finally { dir.Delete(true); }
    }
}
