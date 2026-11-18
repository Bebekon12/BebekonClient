using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class DirectTrafficPolicyTests
{
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
        Assert.Equal(2,sets.Count);
        Assert.Contains("192.0.2.8/32",sets.ToJsonString());
        Assert.Contains("\"domain\":[\"exact.example\"]",sets.ToJsonString());
        var rules = json["route"]!["rules"]!.AsArray();
        var vpn = rules.Single(r => (string?)r?["domain_suffix"]?[0] == "vpn.example");
        Assert.Equal("vpn",(string?)vpn!["outbound"]);
        Assert.Null(vpn["tls_fragment"]);
        foreach (var r in rules.Where(r => r?["tls_fragment"] != null || r?.ToJsonString().Contains("\"quic\"") == true && (string?)r?["action"] == "reject"))
        {
            Assert.Contains("direct-include",r!.ToJsonString()); Assert.Contains("direct-exclude",r.ToJsonString());
            Assert.Contains("\"invert\":true",r.ToJsonString());
        }
        var file = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            await File.WriteAllTextAsync(file,config);
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../core/sing-box.exe"));
            using var core = new CoreProcess(exe,new(Path.GetTempPath(),"bebekon-direct-policy-check"));
            await core.ValidateAsync(file,default);
        }
        finally { File.Delete(file); }
        spec.Settings.TlsFragment = false;
        Assert.DoesNotContain("direct-exclude",ConfigGenerator.Generate(spec));
    }
}
