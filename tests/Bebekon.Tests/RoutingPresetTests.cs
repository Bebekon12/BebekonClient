using System.Text.Json.Nodes;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public sealed class RoutingPresetTests
{
    [Fact] public void OlderHelperRequiresUpdateForNewRuleSchema()
    {
        var current = new Version(0, 1, 5, 0);
        Assert.True(ServiceInstaller.RequiresUpdate(current, new(0, 1, 4, 0)));
        Assert.True(ServiceInstaller.RequiresUpdate(current, null));
        Assert.False(ServiceInstaller.RequiresUpdate(current, new(0, 1, 5, 0)));
        Assert.False(ServiceInstaller.RequiresUpdate(current, new(0, 1, 6, 0)));
    }
    [Fact] public void AdminPresetPreservesScreenshotDirectionsAndTypes()
    {
        var preset = PresetCatalog.Load()[0]; var profile = new Profile();
        Assert.Equal("Правила админа", preset.Name); Assert.Equal(42, PresetCatalog.Apply(preset, profile));
        Assert.Equal(6, profile.Rules.Count(r => !r.UseVpn)); Assert.All(profile.Rules.Take(6), r => Assert.False(r.UseVpn));
        Assert.Contains(profile.Rules, r => r.Kind == RuleKind.GeoIp && r.Values.SequenceEqual(["telegram"]) && r.UseVpn);
        Assert.Contains(profile.Rules, r => r.Kind == RuleKind.GeoSite && r.Values.SequenceEqual(["youtube"]) && !r.UseVpn);
        Assert.Contains(profile.Rules, r => r.Kind == RuleKind.GeoSite && r.Values.SequenceEqual(["discord"]) && !r.UseVpn);
        Assert.Contains(profile.Rules, r => r.Kind == RuleKind.Site && r.Values.SequenceEqual(["store.supercell.com"]));
        Assert.Equal(2, profile.Rules.Count(r => r.Kind == RuleKind.Application && r.Values[0].Equals("telegram.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(42, profile.Rules.Select(r => r.Id).Distinct().Count()); RuleValidation.Validate(profile);
        Assert.Equal(0, PresetCatalog.Apply(preset, profile)); Assert.Equal(42, profile.Rules.Count);
    }
    [Fact] public void AdminExceptionsPrecedeExistingGoogleRule()
    {
        var profile = new Profile { Rules = [new() { Name = "Google", Kind = RuleKind.GeoSite, Values = ["google"] }] };
        PresetCatalog.Apply(PresetCatalog.Load()[0], profile);
        Assert.All(profile.Rules.Take(6), r => Assert.False(r.UseVpn)); Assert.Equal("Google", profile.Rules[6].Name);
    }
    [Fact] public void PresetInstancesAreIndependent()
    {
        var preset = PresetCatalog.Load()[0]; var a = new Profile(); var b = new Profile(); PresetCatalog.Apply(preset, a); PresetCatalog.Apply(preset, b);
        a.Rules[0].Values[0] = "changed"; a.Rules[0].UseVpn = true;
        Assert.NotEqual(a.Rules[0].Id, b.Rules[0].Id); Assert.NotEqual("changed", b.Rules[0].Values[0]); Assert.False(b.Rules[0].UseVpn);
    }
    [Theory] [InlineData(null, LatencyQuality.Unknown)] [InlineData(-1L, LatencyQuality.Poor)] [InlineData(0L, LatencyQuality.Good)] [InlineData(100L, LatencyQuality.Good)] [InlineData(101L, LatencyQuality.Moderate)] [InlineData(200L, LatencyQuality.Moderate)] [InlineData(201L, LatencyQuality.Poor)]
    public void LatencyColorBoundaries(long? ms, LatencyQuality quality) => Assert.Equal(quality, LatencyDisplay.Quality(ms));
    [Fact] public void LatencyUpdateNotifiesBinding()
    {
        var s = new Server(); var changes = new List<string?>(); s.PropertyChanged += (_, e) => changes.Add(e.PropertyName); s.LatencyMs = 75; s.LatencyMs = 201;
        Assert.Equal(new[] { "LatencyMs", "LatencyMs" }, changes);
    }
    [Theory] [InlineData(RuleKind.GeoSite, "../../data")] [InlineData(RuleKind.GeoIp, "unknown")]
    public void UnknownOrPathGeoValuesRejected(RuleKind kind, string value) => Assert.Throws<UserError>(() => RuleValidation.Validate(new RoutingRule { Name = "Bad", Kind = kind, Values = [value] }));
    [Fact] public void SetsAreEmbeddedDeduplicatedAndTelegramIncludesIpv6()
    {
        var p = new Profile { Rules = [new() { Name = "A", Kind = RuleKind.GeoSite, Values = ["openai"] }, new() { Name = "B", Kind = RuleKind.GeoSite, Values = ["openai"] }, new() { Name = "Telegram", Kind = RuleKind.GeoIp, Values = ["TELEGRAM"] }] };
        var c = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(p)))!;
        Assert.Equal(2, c["route"]!["rule_set"]!.AsArray().Count); Assert.Equal(2, c["dns"]!["rules"]!.AsArray().Count);
        Assert.Contains("2001:b28:f23d::/48", c.ToJsonString()); Assert.DoesNotContain("download_detour", c.ToJsonString());
        Assert.Equal("geosite-openai", (string?)c["route"]!["rules"]![4]!["rule_set"]![0]);
    }
    [Fact] public void PerRuleServerControlsOutboundAndDnsWhileProbeStaysOnSelectedServer()
    {
        var s = CoreTests.Node(); var second = CoreTests.Node(); second.Id = Guid.NewGuid().ToString("N"); second.Host = "second.example.com";
        var p = new Profile { Rules = [new() { Name = "Pinned", Values = ["openai.com"], ServerId = second.Id }] };
        var c = JsonNode.Parse(ConfigGenerator.Generate(new(s, p, new(), 17999, new string('a', 48), [second])))!;
        Assert.Equal("vpn-rule-1", (string?)c["route"]!["rules"]![4]!["outbound"]);
        Assert.Equal("vpn-rule-1-dns", (string?)c["dns"]!["rules"]![0]!["server"]);
        Assert.Equal("vpn-rule-1", (string?)c["dns"]!["servers"]![2]!["detour"]);
        Assert.Equal("second.example.com", (string?)c["outbounds"]![2]!["server"]);
        Assert.Equal("vpn", (string?)c["route"]!["rules"]![0]!["outbound"]);
    }
    [Fact] public void MissingOrInvalidPinnedServerFailsWithoutSilentFallback()
    {
        var s = CoreTests.Node(); var second = CoreTests.Node(); second.Id = Guid.NewGuid().ToString("N"); var p = new Profile { Rules = [new() { Name = "Pinned", Values = ["openai.com"], ServerId = second.Id }] };
        Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(p, s)));
        second.Uuid = "bad"; Assert.Throws<UserError>(() => ConfigGenerator.Generate(new(s, p, new(), 17999, new string('a', 48), [second])));
        var probe = JsonNode.Parse(ConfigGenerator.Generate(CoreTests.Spec(p, s), true))!; Assert.Equal(4, probe["route"]!["rules"]!.AsArray().Count);
        p.Rules[0].UseVpn = false; Assert.Contains("direct", ConfigGenerator.Generate(CoreTests.Spec(p, s)));
    }
    [Fact] public void ExtendedProfileExportImportPreservesKindsAndServerReference()
    {
        var p = new Profile { Rules = [new() { Name = "OpenAI", Kind = RuleKind.GeoSite, Values = ["openai"], ServerId = "fixture-server" }] };
        var imported = ProfileCodec.Import(ProfileCodec.Export(p)); Assert.Equal(RuleKind.GeoSite, imported.Rules[0].Kind); Assert.Equal("fixture-server", imported.Rules[0].ServerId);
    }
    [Fact] public async Task OfficialCoreAcceptsEveryBundledSetAndAdminPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var p = new Profile(); PresetCatalog.Apply(PresetCatalog.Load()[0], p);
            PresetCatalog.Apply(PresetCatalog.Load().Single(preset => preset.Name.StartsWith("Россия")), p);
            foreach (var tag in GeoCatalog.Available(RuleKind.GeoIp)) p.Rules.Add(new() { Name = tag, Kind = RuleKind.GeoIp, Values = [tag] });
            foreach (var tag in GeoCatalog.Available(RuleKind.GeoSite)) p.Rules.Add(new() { Name = tag, Kind = RuleKind.GeoSite, Values = [tag] });
            var second = CoreTests.Node(); second.Id = Guid.NewGuid().ToString("N"); p.Rules.Add(new() { Name = "Pinned", Values = ["example.net"], ServerId = second.Id });
            var path = Path.Combine(root, "config.json"); await File.WriteAllTextAsync(path, ConfigGenerator.Generate(CoreTests.Spec(p) with { RuleServers = [second] }));
            var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "sing-box.exe"));
            using var core = new CoreProcess(exe, new(root, "core")); await core.ValidateAsync(path, default);
        }
        finally { Directory.Delete(root, true); }
    }
}
