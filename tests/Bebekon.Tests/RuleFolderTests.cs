using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public sealed class RuleFolderTests
{
    private static (RuleFolder, List<RoutingRule>) Work()
    {
        var folder = new RuleFolder { Name = "Работа", Color = "Violet", Icon = "Briefcase" };
        var vpn = new RoutingRule { Name = "Portal", Values = ["portal.work.example"], UseVpn = true, FolderId = folder.Id, ServerId = "my-server", FallbackServerIds = ["other"] };
        var direct = new RoutingRule { Name = "Git", Values = ["git.work.example"], UseVpn = false, FolderId = folder.Id };
        var corp = new RoutingRule { Name = "Corp", Kind = RuleKind.Contains, Values = ["hai"], UseVpn = true, Destination = RouteTarget.NetBird, FolderId = folder.Id };
        return (folder, [vpn, direct, corp]);
    }

    [Fact]
    public void SharedFolderKeepsEachRulesOwnRouteButNotServerPins()
    {
        var (folder, rules) = Work();
        foreach (var text in new[] { RuleFolders.ToLink(folder, rules), "лови: " + RuleFolders.Export(folder, rules) + " 👍" })
        {
            var (imported, back) = RuleFolders.Import(text);
            Assert.Equal("Работа", imported.Name); Assert.Equal("Violet", imported.Color); Assert.Equal("Briefcase", imported.Icon);
            Assert.Equal([RouteTarget.Vpn, RouteTarget.Direct, RouteTarget.NetBird], back.Select(r => r.Target));
            Assert.All(back, r => { Assert.Equal(imported.Id, r.FolderId); Assert.Null(r.ServerId); Assert.Null(r.FallbackServerIds); });
            Assert.DoesNotContain(back, r => rules.Any(o => o.Id == r.Id));
        }
        Assert.DoesNotContain("my-server", RuleFolders.Export(folder, rules));
    }

    [Fact]
    public void BrokenOrForeignFolderTextIsRefused()
    {
        var (folder, rules) = Work();
        var text = RuleFolders.Export(folder, rules);
        Assert.Throws<UserError>(() => RuleFolders.Import(text[..(text.Length / 2)]));
        Assert.Throws<UserError>(() => RuleFolders.Import(text.Replace("bebekon-rule-folder", "something-else")));
        Assert.Throws<UserError>(() => RuleFolders.Import(text.Replace("\"version\": 1", "\"version\": 7")));
        Assert.Throws<UserError>(() => RuleFolders.Import(text.Replace("portal.work.example", "bad host/")));
        Assert.Throws<UserError>(() => RuleFolders.Import(RuleFolders.LinkPrefix + "not-deflate"));
        Assert.Throws<UserError>(() => RuleFolders.NormalizeName("   "));
    }

    [Fact]
    public void OldDomainGroupsBecomeFoldersWithoutChangingRoutes()
    {
        var state = new AppState();
        var set = DomainSets.Normalize("hai", ["portal.hai.example", "keyword:hai"], style: new() { Color = "Rose", Icon = "Star" });
        var unused = DomainSets.Normalize("spare", ["spare.example"]);
        state.DomainSets.AddRange([set, unused]);
        var before = new RoutingRule { Name = "Before", Values = ["first.example"] };
        var group = new RoutingRule { Name = "Work", Kind = RuleKind.GeoSite, Values = ["openai", "my:hai"], UseVpn = true, Destination = RouteTarget.NetBird };
        state.Profiles[0].Rules = [before, group];

        Assert.True(RuleFolders.MigrateDomainGroups(state));
        var profile = state.Profiles[0];
        Assert.Empty(state.DomainSets);
        Assert.Same(before, profile.Rules[0]);
        var work = profile.Folders.Single(f => f.Name == "Work");
        Assert.Equal("Rose", work.Color);
        var inside = profile.Rules.Where(r => r.FolderId == work.Id).ToList();
        Assert.Equal([RuleKind.GeoSite, RuleKind.Site, RuleKind.Contains], inside.Select(r => r.Kind));
        Assert.All(inside, r => Assert.Equal(RouteTarget.NetBird, r.Target));
        Assert.DoesNotContain(profile.Rules, r => r.Values.Any(DomainSets.IsReference));
        var spare = profile.Folders.Single(f => f.Name == "spare");
        Assert.All(profile.Rules.Where(r => r.FolderId == spare.Id), r => Assert.False(r.Enabled));
        RuleValidation.Validate(profile);
        Assert.False(RuleFolders.MigrateDomainGroups(state));
    }
}
