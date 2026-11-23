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
