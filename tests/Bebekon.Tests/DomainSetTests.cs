using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public sealed class DomainSetTests
{
    private static DomainSet Hai() => DomainSets.Normalize("hai", ["https://www.Portal.hai.example/login, git.hai.example\nportal.hai.example"]);

    [Fact]
    public void NormalizeCanonicalizesAndDeduplicatesDomains()
    {
        var set = Hai();
        Assert.Equal(["git.hai.example", "portal.hai.example"], set.Domains);
        Assert.Throws<UserError>(() => DomainSets.Normalize("has space", ["a.example"]));
        Assert.Throws<UserError>(() => DomainSets.Normalize("empty", [" , "]));
        Assert.Throws<UserError>(() => DomainSets.Normalize("bad", ["not a domain/"]));
    }

    [Fact]
    public void ExpansionKeepsPriorityRouteAndOptions()
    {
        var set = Hai();
        var before = new RoutingRule { Name = "Before", Values = ["first.example"] };
        var mixed = new RoutingRule { Name = "Work", Kind = RuleKind.GeoSite, Values = ["openai", "my:hai"], UseVpn = true, Destination = RouteTarget.NetBird, Port = 443 };
        var after = new RoutingRule { Name = "After", Values = ["last.example"] };
        var profile = new Profile { Rules = [before, mixed, after] };

        var expanded = DomainSets.Expand(profile, [set]);

        Assert.Equal(["Before", "Work", "Work", "After"], expanded.Rules.Select(r => r.Name));
        Assert.Equal(RuleKind.GeoSite, expanded.Rules[1].Kind); Assert.Equal(["openai"], expanded.Rules[1].Values);
        Assert.Equal(RuleKind.Site, expanded.Rules[2].Kind); Assert.Equal(set.Domains, expanded.Rules[2].Values);
        Assert.All(expanded.Rules.Skip(1).Take(2), r => { Assert.Equal(RouteTarget.NetBird, r.Target); Assert.Equal(443, r.Port); });
        Assert.Equal(expanded.Rules.Count, expanded.Rules.Select(r => r.Id).Distinct().Count());
        // The stored profile keeps the reference; only the copy is expanded.
        Assert.Equal(["openai", "my:hai"], mixed.Values);
        RuleValidation.Validate(expanded);
    }

    [Fact]
    public void ExpansionOfMissingSetExplainsWhichRule()
    {
        var profile = new Profile { Rules = [new() { Name = "Work", Kind = RuleKind.GeoSite, Values = ["my:gone"] }] };
        var error = Assert.Throws<UserError>(() => DomainSets.Expand(profile, []));
        Assert.Contains("gone", error.Message); Assert.Contains("Work", error.Message);
    }

    [Fact]
    public void ExportImportRoundTripToleratesChatText()
    {
        var text = DomainSets.Export(Hai());
        var pasted = "Держи набор:\n```\n" + text + "\n```\nспасибо";
        var imported = DomainSets.Import(pasted);
        Assert.Equal("hai", imported.Name); Assert.Equal(Hai().Domains, imported.Domains);
        Assert.Throws<UserError>(() => DomainSets.Import("{\"name\":\"x\",\"domains\":[\"a.example\"]}"));
        Assert.Throws<UserError>(() => DomainSets.Import(text.Replace("\"version\": 1", "\"version\": 9")));
        Assert.Throws<UserError>(() => DomainSets.Import(text[..(text.Length / 2)]));
    }

    [Fact]
    public void OnlyTheDesktopAppAcceptsSetReferences()
    {
        var rule = new RoutingRule { Name = "Work", Kind = RuleKind.GeoSite, Values = ["my:hai"] };
        var previous = RuleValidation.CustomSetExists;
        try
        {
            // The privileged helper never installs a resolver, so references cannot reach it.
            RuleValidation.CustomSetExists = null;
            Assert.Throws<UserError>(() => RuleValidation.Validate(rule));
            RuleValidation.CustomSetExists = name => name == "hai";
            RuleValidation.Validate(rule);
            Assert.Throws<UserError>(() => RuleValidation.Validate(new RoutingRule { Name = "X", Kind = RuleKind.GeoSite, Values = ["my:other"] }));
        }
        finally { RuleValidation.CustomSetExists = previous; }
    }
}

public sealed class NetBirdLoginTests
{
    [Theory]
    [InlineData("Please do the SSO login in your browser. If your browser didn't open automatically, use this URL to log in:", null)]
    [InlineData("https://login.example.com/activate?user_code=ABCD-EFGH", "https://login.example.com/activate?user_code=ABCD-EFGH")]
    [InlineData("  open https://auth.example.com/device?code=1. ", "https://auth.example.com/device?code=1")]
    [InlineData("http://insecure.example.com/login", null)]
    [InlineData("https://user:pass@evil.example.com/", null)]
    public void OnlyHttpsLoginAddressesAreOpened(string line, string? expected)
        => Assert.Equal(expected, NetBirdClient.LoginUrl(line)?.AbsoluteUri);
}

public sealed class DomainGroupSharingTests
{
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public void LinkRoundTripKeepsDomainsColorAndIcon()
    {
        var set = DomainSets.Normalize("hai", ["portal.hai.example", "git.hai.example"], style: new() { Color = "Rose", Icon = "Briefcase" });
        var link = DomainSets.ToLink(set);
        Assert.StartsWith(DomainSets.LinkPrefix, link); Assert.DoesNotContain("portal", link);
        var back = DomainSets.Import("смотри: " + link + " — добавь");
        Assert.Equal(set.Domains, back.Domains); Assert.Equal("Rose", back.Color); Assert.Equal("Briefcase", back.Icon);
        Assert.Throws<UserError>(() => DomainSets.FromLink(DomainSets.LinkPrefix + "not-deflate"));
        // Unknown looks fall back to defaults instead of failing.
        Assert.Equal("Blue", DomainSets.Normalize("x", ["a.example"], style: new() { Color = "Neon" }).Color);
    }

    [Fact]
    public async Task FetchAcceptsPlainListsV2flyEntriesAndSharedGroups()
    {
        using var plain = new HttpClient(new Handler("# corp\nportal.hai.example\ndomain:git.hai.example\nfull:sso.hai.example\nregexp:.*bad\nkeyword:skip\n"));
        Assert.Equal(["portal.hai.example", "git.hai.example", "sso.hai.example"], await DomainSets.FetchAsync("https://lists.example/hai.txt", plain, default));
        using var shared = new HttpClient(new Handler(DomainSets.Export(DomainSets.Normalize("hai", ["a.hai.example"]))));
        Assert.Equal(["a.hai.example"], await DomainSets.FetchAsync("https://lists.example/hai.json", shared, default));
        await Assert.ThrowsAsync<UserError>(() => DomainSets.FetchAsync("http://lists.example/hai.txt", plain, default));
        using var empty = new HttpClient(new Handler("# nothing\n"));
        await Assert.ThrowsAsync<UserError>(() => DomainSets.FetchAsync("https://lists.example/empty.txt", empty, default));
    }

    [Fact]
    public void BundledSetCopiesExactAndSuffixDomains()
    {
        var (domains, skipped) = GeoCatalog.Domains("openai");
        Assert.Contains("openai.com", domains); Assert.True(skipped >= 0);
        Assert.All(domains, d => Assert.False(d.StartsWith('.')));
        DomainSets.Normalize("openai-my", domains);
    }
}

public sealed class RecentDestinationTests
{
    private static System.Text.Json.JsonElement Connections(params object[] items)
        => System.Text.Json.JsonSerializer.SerializeToElement(new { connections = items });
    private static object Connection(string id, string host, string chain, string type = "tun/tun-in", string process = "C:\\Apps\\chrome.exe", int port = 443)
        => new { id, metadata = new { host, destinationPort = port.ToString(), processPath = process.Replace("\\\\", "\\"), type }, chains = new[] { chain } };

    [Fact]
    public void KeepsOnlyUserDomainsWithFriendlyRoutes()
    {
        var log = new RecentDestinationLog(); var now = DateTimeOffset.UtcNow;
        log.Observe(Connections(
            Connection("1", "portal.hai.example", "netbird-corp"),
            Connection("2", "www.youtube.com", "vpn"),
            Connection("3", "example.org", "direct"),
            Connection("4", "93.184.216.34", "direct"),
            Connection("5", "www.gstatic.com", "vpn", type: "mixed/vpn-probe"),
            Connection("6", "ads.example", "block")), now);
        var seen = log.Snapshot().ToDictionary(e => e.Host, e => e.Route);
        Assert.Equal(new Dictionary<string, string> { ["portal.hai.example"] = "NetBird", ["www.youtube.com"] = "VPN", ["example.org"] = "Direct", ["ads.example"] = "Block" }, seen);
        Assert.Equal("chrome.exe", log.Snapshot()[0].Process);
    }

    [Fact]
    public void CountsNewConnectionsAndForgetsOldDomains()
    {
        var log = new RecentDestinationLog(); var start = DateTimeOffset.UtcNow;
        log.Observe(Connections(Connection("1", "a.example", "vpn")), start);
        log.Observe(Connections(Connection("1", "a.example", "vpn"), Connection("2", "a.example", "vpn")), start.AddSeconds(1));
        Assert.Equal(2, log.Snapshot().Single().Connections);
        log.Observe(Connections(Connection("9", "b.example", "vpn")), start + RecentDestinationLog.Window + TimeSpan.FromMinutes(1));
        Assert.Equal(["b.example"], log.Snapshot().Select(e => e.Host));
    }
}

public sealed class UplinkIPv6Tests
{
    [Theory]
    [InlineData("2a02:6b8::feed:0ff", true)]
    [InlineData("2606:4700::6811:8775", true)]
    [InlineData("2001:0:14c9:d804:409:fb98:d175:5203", false)] // Teredo
    [InlineData("2002:c000:204::1", false)]                   // 6to4
    [InlineData("fd84:be:be::1", false)]                      // ULA
    [InlineData("fe80::1", false)]
    [InlineData("192.168.1.11", false)]
    public void OnlyRealGlobalIPv6Counts(string address, bool global)
        => Assert.Equal(global, NetworkAddresses.IsGlobalIPv6(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void WithoutUplinkIPv6TheTunnelOffersIPv4OnlyAndDnsDropsAaaa()
    {
        var spec = new ConnectSpec(CoreTests.Node(), new Profile(), new Settings { TunnelMode = TunnelMode.Tun }, 20000, new string('a', 48));
        var dual = System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Generate(spec))!;
        var v4 = System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Generate(spec with { UplinkIPv6 = false }))!;
        string Addresses(System.Text.Json.Nodes.JsonNode config) => config["inbounds"]!.AsArray().First(i => (string?)i!["type"] == "tun")!["address"]!.ToJsonString();
        Assert.Contains("fd84:be:be::1/126", Addresses(dual)); Assert.Equal("prefer_ipv4", (string?)dual["dns"]!["strategy"]);
        Assert.DoesNotContain("fd84", Addresses(v4)); Assert.Equal("ipv4_only", (string?)v4["dns"]!["strategy"]);
    }
}
