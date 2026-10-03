using System.Net;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public sealed class RegionalRulesTests
{
    [Fact] public void RussianSetIsOptInIdempotentAndPreservesExistingExceptions()
    {
        var preset = PresetCatalog.Load().Single(p => p.Name.StartsWith("Россия"));
        var exception = new RoutingRule { Name = "Custom direct", Values = ["discord.com"], UseVpn = false };
        var profile = new Profile { Rules = [exception] };
        Assert.Equal(18, PresetCatalog.Apply(preset, profile)); Assert.Same(exception, profile.Rules[0]);
        Assert.Equal(0, PresetCatalog.Apply(preset, profile));
        Assert.All(profile.Rules.Skip(1), r => { RuleValidation.Validate(r); Assert.True(r.UseVpn); Assert.NotNull(r.CreatedAt); });
        Assert.Contains(profile.Rules, r => r.Values.Contains("signal.me"));
        Assert.Contains(profile.Rules, r => r.Kind == RuleKind.GeoIp && r.Values.Contains("telegram"));
        Assert.Contains(profile.Rules, r => r.Values.Contains("RobloxPlayerBeta.exe"));
        var restored = ProfileCodec.Import(ProfileCodec.Export(profile));
        Assert.Equal(profile.Rules.Select(r => r.CreatedAt), restored.Rules.Select(r => r.CreatedAt));
        Assert.Null(restored.Rules[0].CreatedAt);
    }
    [Theory]
    [InlineData("10.1.2.3", true)] [InlineData("172.16.0.1", true)] [InlineData("172.31.255.255", true)] [InlineData("192.168.0.1", true)]
    [InlineData("172.15.0.1", false)] [InlineData("172.32.0.1", false)] [InlineData("8.8.8.8", false)] [InlineData("127.0.0.1", false)]
    [InlineData("fc00::1", true)] [InlineData("fdab::1", true)] [InlineData("fe80::1", false)] [InlineData("2001:4860::1", false)] [InlineData("::ffff:192.168.2.1", true)]
    public void AddressClassificationRespectsIpv4AndIpv6Boundaries(string address, bool expected) => Assert.Equal(expected, NetworkAddresses.IsPrivate(IPAddress.Parse(address)));
}
