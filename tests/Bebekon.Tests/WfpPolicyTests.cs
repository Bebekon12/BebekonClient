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

    [Fact]
    public void DomainProtectionUsesTheEntireDedicatedExecutableAndDisablesOriginProbe()
    {
        const string app = @"C:\Applications\DedicatedBrowser.exe";
        var profile = new Profile
        {
            Rules =
            [
                new() { Name = "Other sites", Values = ["other.example"], Destination = RouteTarget.Direct },
                new() { Name = "Guarded domain", Values = ["guarded.example"], ProtectedExecutables = [app], SystemProtection = true, Port = 443 }
            ]
        };
        var config = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(profile) with { OriginProbePort = 17995 }))!;
        var rules = config["route"]!["rules"]!.AsArray();
        Assert.Equal(app, (string?)rules[4]!["process_path"]![0]);
        Assert.Null(rules[4]!["port"]);
        Assert.Equal("vpn", (string?)rules[4]!["outbound"]);
        Assert.DoesNotContain(config["inbounds"]!.AsArray(), inbound => (string?)inbound!["tag"] == "origin-probe");
        Assert.Equal(RouteTarget.Vpn, RoutingPolicy.Analyze(profile, new("other.example", Executable: app)).Direction);
        Assert.Equal(RouteTarget.Direct, RoutingPolicy.Analyze(profile, new("other.example", Executable: @"C:\Applications\Other.exe")).Direction);
        Assert.Equal(app, Assert.Single(BackupCodec.Import(BackupCodec.Export(new(), profile)).Rules.Last().ProtectedExecutables!));
    }

    [Fact]
    public void TwoDomainGuardsCannotSendTheSameExecutableToConflictingRoutes()
    {
        const string app = @"C:\Applications\DedicatedBrowser.exe";
        var profile = new Profile { Rules = [
            new() { Name = "VPN", Values = ["first.example"], ProtectedExecutables = [app], SystemProtection = true },
            new() { Name = "Blocked", Values = ["second.example"], ProtectedExecutables = [app], SystemProtection = true, Destination = RouteTarget.Block }
        ] };
        Assert.Throws<UserError>(() => RuleValidation.Validate(profile));
    }
}
