using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class AutoServerTests
{
    private static Server Node(string host, string parameters = "security=none&type=tcp") => VlessParser.Parse($"vless://{CoreTests.Id}@{host}:443?{parameters}#{host}");
    private static ConnectSpec Spec(Server selected, List<Server> pool, bool auto = true) => new(selected, new(), new() { AutoServer = auto }, 17999, new string('a', 48), AutoServers: pool);

    [Fact] public void BuildsUrltestWithSelectedServerFirst()
    {
        var a = Node("a.example.com"); var b = Node("b.example.com"); var c = Node("c.example.com");
        var config = JsonNode.Parse(ConfigGenerator.Generate(Spec(b, [a, b, c])))!;
        var outbounds = config["outbounds"]!.AsArray();
        var group = outbounds[0]!;
        Assert.Equal("urltest", (string?)group["type"]); Assert.Equal("vpn", (string?)group["tag"]);
        Assert.Equal(["auto-0", "auto-1", "auto-2"], group["outbounds"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal("b.example.com", (string?)outbounds.Single(o => (string?)o!["tag"] == "auto-0")!["server"]);
        Assert.Equal("a.example.com", (string?)outbounds.Single(o => (string?)o!["tag"] == "auto-1")!["server"]);
        // Probes, DNS and the default route keep using the "vpn" tag, now the group.
        Assert.Equal("vpn", (string?)config["route"]!["rules"]![0]!["outbound"]);
        Assert.Equal("vpn", (string?)config["dns"]!["servers"]![1]!["detour"]);
    }

    [Fact] public void OffOrSingleServerOrProbeKeepsPlainOutbound()
    {
        var a = Node("a.example.com"); var b = Node("b.example.com");
        Assert.Equal("vless", (string?)JsonNode.Parse(ConfigGenerator.Generate(Spec(a, [a, b], auto: false)))!["outbounds"]![0]!["type"]);
        Assert.Equal("vless", (string?)JsonNode.Parse(ConfigGenerator.Generate(Spec(a, [a])))!["outbounds"]![0]!["type"]);
        Assert.Equal("vless", (string?)JsonNode.Parse(ConfigGenerator.Generate(Spec(a, [a, b]), probeOnly: true))!["outbounds"]![0]!["type"]);
    }

    [Fact] public void SkipsCompanionAndUnsupportedServers()
    {
        var a = Node("a.example.com"); var b = Node("b.example.com"); var bad = Node("c.example.com", "type=kcp");
        var xhttp = Node("d.example.com", "type=xhttp&security=tls&sni=d.example.com&path=%2Fx");
        Assert.False(ConfigGenerator.AutoEligible(bad)); Assert.False(ConfigGenerator.AutoEligible(xhttp));
        Assert.Equal([a.Id, b.Id], ConfigGenerator.AutoServers(Spec(a, [bad, xhttp, a, b, a])).Select(s => s.Id).ToArray());
        Assert.Empty(ConfigGenerator.AutoServers(Spec(a, [a, b], auto: false)));
    }

    [Fact] public void LargePoolIsCappedInsteadOfFailing()
    {
        var pool = Enumerable.Range(0, 80).Select(i => Node($"s{i}.example.com")).ToList();
        var config = JsonNode.Parse(ConfigGenerator.Generate(Spec(pool[70], pool)))!;
        var members = config["outbounds"]![0]!["outbounds"]!.AsArray();
        Assert.Equal(64, members.Count);
        Assert.Equal("s70.example.com", (string?)config["outbounds"]!.AsArray().Single(o => (string?)o!["tag"] == "auto-0")!["server"]);
    }
}
public class TlsFragmentTests
{
    [Fact] public void OnlyDirectConnectionsAreFragmented()
    {
        var profile = new Profile { Rules = [new() { Name = "yt", Values = ["youtube.com"], UseVpn = false }, new() { Name = "ai", Values = ["openai.com"] }] };
        var spec = CoreTests.Spec(profile, settings: new() { TlsFragment = true });
        var rules = JsonNode.Parse(ConfigGenerator.Generate(spec))!["route"]!["rules"]!.AsArray();
        Assert.True((bool)rules.Single(r => (string?)r!["domain_suffix"]?[0] == "youtube.com")!["tls_fragment"]!);
        Assert.Null(rules.Single(r => (string?)r!["domain_suffix"]?[0] == "openai.com")!["tls_fragment"]);
        var last = rules[^1]!; Assert.Equal("direct", (string?)last["outbound"]); Assert.True((bool)last["tls_fragment"]!);
        Assert.Null(rules.FirstOrDefault(r => (string?)r!["outbound"] == "vpn" && r!["tls_fragment"] is not null));

        var whole = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(new() { DefaultRoute = RouteTarget.Vpn, Rules = profile.Rules }, settings: new() { TlsFragment = true })))!["route"]!["rules"]!.AsArray();
        Assert.Single(whole, r => r!["tls_fragment"] is not null);
        Assert.DoesNotContain("tls_fragment", ConfigGenerator.Generate(CoreTests.Spec(profile)));
    }
}
