using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class RoutingDirectionTests
{
    [Fact]
    public void ProfilesWithoutDestinationOrEnabledKeepTheirLegacyMeaning()
    {
        var profile = ProfileCodec.Import("""
            {"name":"Legacy","rules":[
              {"name":"Direct","values":["direct.example"],"useVpn":false},
              {"name":"VPN","values":["vpn.example"],"useVpn":true}
            ]}
            """);

        Assert.Equal(new[] { RouteTarget.Direct, RouteTarget.Vpn }, profile.ActiveRules.Select(rule => rule.Target));
        Assert.All(profile.Rules, rule => Assert.True(rule.Enabled));
    }

    [Fact]
    public void ExplicitDestinationTakesPrecedenceAndRoundTripsDisabledState()
    {
        var profile = ProfileCodec.Import("""
            {"name":"Current","rules":[
              {"name":"Corporate","values":["corp.example"],"destination":"netBird","useVpn":false,"enabled":false}
            ]}
            """);
        var rule = Assert.Single(ProfileCodec.Import(ProfileCodec.Export(profile)).Rules);
        Assert.Equal(RouteTarget.NetBird, rule.Target);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void DisabledPinnedRuleDoesNotRequireItsServerOrEnterDnsPolicy()
    {
        var profile = new Profile { Rules = [
            new() { Name = "Disabled", Values = ["disabled.example"], ServerId = "removed", Enabled = false },
            new() { Name = "Blocked", Values = ["blocked.example"], Destination = RouteTarget.Block }
        ] };
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile)))!;
        var userRoute = config["route"]!["rules"]![4]!;
        Assert.Equal("reject", (string?)userRoute["action"]);
        Assert.Equal("blocked.example", (string?)userRoute["domain_suffix"]![0]);
        var dnsRule = Assert.Single(config["dns"]!["rules"]!.AsArray())!;
        Assert.Equal("REFUSED", (string?)dnsRule["rcode"]);
    }

    [Theory]
    [InlineData(RouteTarget.Block)]
    [InlineData(RouteTarget.NetBird)]
    public void UnavailableDefaultRejectsTrafficAndDns(RouteTarget target)
    {
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(new() { DefaultRoute = target })))!;
        Assert.Equal("reject", (string?)config["route"]!["rules"]!.AsArray().Last()!["action"]);
        Assert.Equal("REFUSED", (string?)config["dns"]!["rules"]!.AsArray().Last()!["rcode"]);
    }
}
