using System.Net;
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

    [Fact]
    public void ProtocolAndPortApplyToTrafficAndPreviewInProfileOrder()
    {
        var profile = new Profile { Rules = [
            new() { Name = "Network", Kind = RuleKind.Network, Values = ["192.0.2.9/24"], Destination = RouteTarget.Block },
            new() { Name = "HTTPS", Values = ["example.org"], Transport = RuleTransport.Tcp, Port = 443 },
            new() { Name = "Other", Values = ["example.org"], UseVpn = false }
        ] };
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile)))!;
        Assert.Equal("192.0.2.0/24", profile.Rules[0].Values[0]);
        var route = config["route"]!["rules"]![5]!;
        Assert.Equal("tcp", (string?)route["network"]);
        Assert.Equal(443, (int?)route["port"]);
        Assert.Null(config["dns"]!["rules"]![0]!["port"]);

        var analysis = RoutingPolicy.Analyze(profile, new("sub.example.org", Transport: RuleTransport.Tcp, Port: 443));
        Assert.Same(profile.Rules[1], analysis.Rule);
        Assert.True(analysis.DependsOnConnectionDetails);
        Assert.Equal(RouteTarget.Block, RoutingPolicy.Analyze(profile, new("sub.example.org", IPAddress.Parse("192.0.2.7"))).Direction);
        Assert.Equal(RouteTarget.Direct, RoutingPolicy.Analyze(profile, new("sub.example.org", IPAddress.Parse("192.0.3.7"), Transport: RuleTransport.Udp, Port: 443)).Direction);
    }

    [Fact]
    public void VpnReservesNeverIncludeDirectAndSurviveAMissingPrimary()
    {
        var primary = CoreTests.Node();
        var reserve = CoreTests.Node("security=tls&type=ws");
        primary.Id = "primary";
        reserve.Id = "reserve";
        var rule = new RoutingRule { Name = "Privacy", Values = ["example.org"], ServerId = primary.Id,
            ForbiddenRoutes = [RouteTarget.Direct], FallbackServerIds = [reserve.Id] };
        var spec = CoreTests.Spec(new() { Rules = [rule] }) with { RuleServers = [primary, reserve] };
        var config = JsonNode.Parse(ConfigGenerator.Generate(spec))!;
        var group = config["outbounds"]!.AsArray().Last()!;
        Assert.Equal("urltest", (string?)group["type"]);
        Assert.Equal(new[] { "vpn-rule-1", "vpn-rule-2" }, group["outbounds"]!.AsArray().Select(node => (string)node!));
        var spare = JsonNode.Parse(ConfigGenerator.Generate(spec with { RuleServers = [reserve] }))!;
        Assert.Equal("vpn-rule-1", (string?)spare["route"]!["rules"]![4]!["outbound"]);
        var blocked = JsonNode.Parse(ConfigGenerator.Generate(spec with { RuleServers = [] }))!;
        Assert.Equal("reject", (string?)blocked["route"]!["rules"]![4]!["action"]);
    }

    [Fact]
    public void DirectFallbackMustBeExplicitAndAllowedAndWindowsBackupKeepsThePolicy()
    {
        var rule = new RoutingRule { Name = "Corp", Values = ["corp.example"], Destination = RouteTarget.NetBird,
            UnavailableRoute = RouteTarget.Direct, ForbiddenRoutes = [RouteTarget.Direct] };
        Assert.Throws<UserError>(() => RuleValidation.Validate(rule));
        rule.ForbiddenRoutes = null;
        var profile = new Profile { DefaultRoute = RouteTarget.Block, Rules = [rule] };
        var restored = BackupCodec.Import(BackupCodec.Export(new(), profile));
        Assert.Equal(RouteTarget.Block, restored.DefaultRoute);
        Assert.Equal(RouteTarget.Direct, Assert.Single(restored.Rules).UnavailableRoute);
        Assert.Equal(RouteTarget.NetBird, Assert.Single(restored.Rules).Target);
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile)))!;
        Assert.Equal("direct", (string?)config["route"]!["rules"]![4]!["outbound"]);
    }
}
