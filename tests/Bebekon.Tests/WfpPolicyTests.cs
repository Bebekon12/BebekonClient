using System.Net;
using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class WfpPolicyTests
{
    [Theory]
    [InlineData(RuleKind.Site, "example.org", RouteTarget.Vpn)]
    [InlineData(RuleKind.Application, "client.exe", RouteTarget.Vpn)]
    [InlineData(RuleKind.Network, "192.0.2.0/24", RouteTarget.Direct)]
    public void WfpRejectsUnsupportedProtectionClaims(RuleKind kind, string value, RouteTarget target) =>
        Assert.Throws<UserError>(() => RuleValidation.Validate(new RoutingRule
        {
            Name = "Protected", Kind = kind, Values = [value], Destination = target, SystemProtection = true
        }));

    [Fact]
    public void ProtectedScopeCannotBeShadowedByDirectWhenPassingThroughTun()
    {
        var profile = new Profile { Rules = [
            new() { Name = "Direct", Kind = RuleKind.Network, Values = ["192.0.2.0/24"], UseVpn = false },
            new() { Name = "Protected", Kind = RuleKind.Network, Values = ["192.0.2.10"], SystemProtection = true }
        ] };
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile)))!;
        Assert.Equal("vpn", (string?)config["route"]!["rules"]![4]!["outbound"]);
        var route = RoutingPolicy.Analyze(profile, new(Address: IPAddress.Parse("192.0.2.10")));
        Assert.Same(profile.Rules[1], route.Rule);
    }
}
