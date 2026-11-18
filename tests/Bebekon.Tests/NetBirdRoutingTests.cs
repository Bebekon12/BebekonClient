using System.Net;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class NetBirdRoutingTests
{
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
        }
        finally { Directory.Delete(root, true); }
    }
}
