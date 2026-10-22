using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class BackupTests
{
    [Fact]
    public void WindowsBackupRoundTripsSubscriptionsRulesAndSettings()
    {
        var state = new AppState { Settings = new() { Mtu = 1400, AutoConnect = true } };
        state.Subscriptions.Add(new() { Name = "Provider", Source = "https://p.example/sub", Hidden = true });
        var profile = new Profile { Rules = [new() { Name = "Figma", Values = ["figma.com"] }, new() { Name = "App", Kind = RuleKind.Application, Values = ["telegram.exe"], UseVpn = false }] };
        var data = BackupCodec.Import(BackupCodec.Export(state, profile));
        Assert.Equal(new BackupSubscription("Provider", "https://p.example/sub", true), Assert.Single(data.Subscriptions));
        Assert.Equal(["figma.com", "telegram.exe"], data.Rules.Select(r => r.Values[0]));
        Assert.False(data.Rules[1].UseVpn); Assert.Equal(1400, data.Settings!.Mtu); Assert.True(data.Settings.AutoConnect); Assert.Equal(0, data.Skipped);
    }
    [Fact]
    public void AndroidBackupKeepsPortableRulesAndSkipsAndroidOnlyParts()
    {
        var android = """
        {"format":"bebekon-backup","version":1,"platform":"android",
         "subscriptions":[{"name":"P","source":"https://p.example/s","hidden":false},{"name":"Manual","source":"","androidOutbound":{"type":"socks"}}],
         "rules":[{"name":"YT","kind":"geoSite","values":["youtube"],"vpn":true},{"name":"Bank","kind":"androidApp","values":["ru.bank"],"vpn":false},{"name":"Site","kind":"site","values":["https://www.example.org/x"],"vpn":true}],
         "androidPreferences":{"mtu":1400}}
        """;
        var data = BackupCodec.Import(android);
        Assert.Equal("https://p.example/s", Assert.Single(data.Subscriptions).Source);
        Assert.Equal(["youtube", "www.example.org"], data.Rules.Select(r => r.Values[0]));
        Assert.Null(data.Settings); Assert.Equal(2, data.Skipped);
    }
    [Theory] [InlineData("{}")] [InlineData("not json")] [InlineData("{\"format\":\"bebekon-backup\",\"version\":2}")]
    public void ForeignFilesAreRejected(string text) => Assert.Throws<UserError>(() => BackupCodec.Import(text));
}
