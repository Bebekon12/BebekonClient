using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class DirectTrafficPolicyTests
{
    private static JsonArray Routes(ConnectSpec spec) => JsonNode.Parse(ConfigGenerator.Generate(spec))!["route"]!["rules"]!.AsArray();
    private static async Task CheckWithSingBox(string config)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(file, config);
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../core/sing-box.exe"));
            using var core = new CoreProcess(exe, new(Path.GetTempPath(), "bebekon-direct-policy-check"));
            await core.ValidateAsync(file, default);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task NativeEngineAcceptsListsWithQuicFallbackAndPreservesTheVpnRoute()
    {
        var profile = new Profile { Rules = [
            new() { Name="VPN", Values=["vpn.example"] },
            new() { Name="Direct", Values=["www.example"], Destination=RouteTarget.Direct },
            new() { Name="Block", Values=["blocked.example"], Destination=RouteTarget.Block }
        ] };
        var spec = CoreTests.Spec(profile);
        spec.Settings.TlsFragment = true;
        spec.Settings.DirectPolicy = new() { Include=["example.org", "^exact.example", "192.0.2.8", "# comment"], Exclude=["a.example.org"], PreferTcp=true };
        var config = ConfigGenerator.Generate(spec);
        var json = JsonNode.Parse(config)!;
        var sets = json["route"]!["rule_set"]!.AsArray();
        Assert.Contains("192.0.2.8/32", sets.ToJsonString());
        Assert.Contains("\"domain\":[\"exact.example\"]", sets.ToJsonString());
        var rules = json["route"]!["rules"]!.AsArray();
        var vpn = rules.Single(r => (string?)r?["domain_suffix"]?[0] == "vpn.example");
        Assert.Equal("vpn", (string?)vpn!["outbound"]);
        Assert.Null(vpn["tls_fragment"]); Assert.Null(vpn["tls_record_fragment"]);
        // Every split or QUIC refusal honours the exclusions.
        foreach (var r in rules.Where(r => r?["tls_fragment"] != null || r?["tls_record_fragment"] != null || (string?)r?["action"] == "reject" && r.ToJsonString().Contains("\"quic\"")))
        {
            Assert.Contains("direct-exclude", r!.ToJsonString()); Assert.Contains("\"invert\":true", r.ToJsonString());
        }
        // The catch-all direct route is split only within the user's list and the bundled services.
        var catchAll = rules.Single(r => r?.ToJsonString().Contains("direct-include") == true && r.ToJsonString().Contains("\"tcp\""));
        Assert.Contains("geosite-youtube", catchAll!.ToJsonString());
        await CheckWithSingBox(config);
        spec.Settings.TlsFragment = false;
        Assert.DoesNotContain("direct-exclude", ConfigGenerator.Generate(spec));
    }

    [Fact]
    public async Task AutoSplitsThrottledServicesNotEveryDirectSite()
    {
        var spec = CoreTests.Spec(new Profile(), settings: new() { TlsFragment = true });
        var config = ConfigGenerator.Generate(spec);
        var rules = JsonNode.Parse(config)!["route"]!["rules"]!.AsArray();
        // The plain catch-all stays unsplit: npmjs.com, Microsoft and the like are left alone.
        var last = rules[^1]!.AsObject();
        Assert.Equal("direct", (string?)last["outbound"]); Assert.Null(last["tls_fragment"]); Assert.Null(last["tls_record_fragment"]);
        var split = rules.Single(r => r?["tls_record_fragment"] is not null);
        Assert.Contains("geosite-youtube", split!.ToJsonString()); Assert.Contains("\"tcp\"", split.ToJsonString());
        Assert.True((bool)split["tls_fragment"]!); // default method: both
        // QUIC to those services is refused so browsers fall back to TCP, where splitting works.
        var quic = rules.Single(r => (string?)r?["action"] == "reject" && r.ToJsonString().Contains("\"quic\""));
        Assert.Contains("geosite-discord", quic!.ToJsonString());
        await CheckWithSingBox(config);
    }

    [Theory]
    [InlineData(FragmentMethod.Records, false, true)]
    [InlineData(FragmentMethod.Split, true, false)]
    [InlineData(FragmentMethod.Both, true, true)]
    public void MethodSelectsSegmentsRecordsOrBoth(FragmentMethod method, bool segments, bool records)
    {
        var profile = new Profile { Rules = [new() { Name = "yt", Values = ["youtube.com"], UseVpn = false }] };
        var rules = Routes(CoreTests.Spec(profile, settings: new() { TlsFragment = true, DirectPolicy = new() { Method = method } }));
        var rule = rules.Single(r => (string?)r!["domain_suffix"]?[0] == "youtube.com")!;
        Assert.Equal(segments, rule["tls_fragment"] is not null);
        Assert.Equal(records, rule["tls_record_fragment"] is not null);
    }

    [Fact]
    public void ListOnlyWithoutAListSplitsOnlyTheUsersDirectRules()
    {
        var profile = new Profile { Rules = [new() { Name = "site", Values = ["slow.example"], UseVpn = false }] };
        var rules = Routes(CoreTests.Spec(profile, settings: new() { TlsFragment = true, DirectPolicy = new() { Scope = FragmentScope.ListOnly } }));
        Assert.Single(rules, r => r!["tls_record_fragment"] is not null);
        Assert.NotNull(rules.Single(r => (string?)r!["domain_suffix"]?[0] == "slow.example")!["tls_record_fragment"]);
        Assert.DoesNotContain(rules, r => r!.ToJsonString().Contains("geosite-youtube"));
    }

    [Fact]
    public void AllDirectKeepsThePreviousSplitEverythingBehaviour()
    {
        var rules = Routes(CoreTests.Spec(new Profile(), settings: new() { TlsFragment = true, DirectPolicy = new() { Scope = FragmentScope.AllDirect } }));
        var split = rules.Single(r => r!["tls_fragment"] is not null);
        Assert.DoesNotContain("rule_set", split!.ToJsonString());
    }

    [Fact]
    public void ProbeConfigBindsToTheUplinkAndMarksTheMethod()
    {
        var config = JsonNode.Parse(ThrottleProbe.Config(20500, System.Net.IPAddress.Parse("192.168.1.11"), FragmentMethod.Records))!;
        Assert.Equal("192.168.1.11", (string?)config["outbounds"]![0]!["inet4_bind_address"]);
        Assert.Equal("127.0.0.1", (string?)config["inbounds"]![0]!["listen"]);
        Assert.True((bool)config["route"]!["rules"]![1]!["tls_record_fragment"]!);
        Assert.Null(config["route"]!["rules"]![1]!["tls_fragment"]);
        var none = JsonNode.Parse(ThrottleProbe.Config(20500, null, null))!;
        Assert.Null(none["route"]!["rules"]![1]!["tls_record_fragment"]);
    }

    [Fact]
    public void RecommendationPrefersNoSplittingThenTheFastestWorkingMethod()
    {
        Assert.Null(ThrottleProbe.Recommend([new(null, 3, 3, 40), new(FragmentMethod.Records, 3, 3, 90)]));
        Assert.Equal(FragmentMethod.Records, ThrottleProbe.Recommend([new(null, 0, 3, null), new(FragmentMethod.Split, 3, 3, 600), new(FragmentMethod.Records, 3, 3, 110)]));
        Assert.Null(ThrottleProbe.Recommend([new(null, 0, 3, null), new(FragmentMethod.Split, 1, 3, 600)]));
    }
}
