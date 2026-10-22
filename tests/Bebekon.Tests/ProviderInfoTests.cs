using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ProviderInfoTests
{
    [Fact]
    public void UsageHeaderParsesInAnyOrderAndIgnoresJunk()
    {
        var info = new ProviderInfo(); info.ParseUsage("download=2147483648; foo=bar; upload=1073741824; total=10737418240; expire=1893456000; total=x");
        Assert.Equal(1073741824, info.Upload); Assert.Equal(2147483648, info.Download); Assert.Equal(10737418240, info.Total);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), info.Expire);
        var empty = new ProviderInfo(); empty.ParseUsage("expire=0; total=-5"); Assert.Null(empty.Expire); Assert.Equal(0, empty.Total);
    }
    [Fact]
    public void LinksMustBeHttpsAndTextIsBounded()
    {
        Assert.Equal("https://t.me/support", ProviderInfo.SafeLink(" https://t.me/support "));
        Assert.Equal("", ProviderInfo.SafeLink("http://example.com")); Assert.Equal("", ProviderInfo.SafeLink("tg://resolve?domain=x")); Assert.Equal("", ProviderInfo.SafeLink("https://user:pw@example.com"));
        Assert.Equal("Новые серверы", ProviderInfo.Text("base64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Новые\nсерверы")), 300));
        Assert.Equal(11, ProviderInfo.Text(new string('a', 50), 10).Length);
        Assert.Equal(12, ProviderInfo.UpdateInterval("12")); Assert.Equal(168, ProviderInfo.UpdateInterval("999")); Assert.Equal(0, ProviderInfo.UpdateInterval("soon"));
    }
    [Fact]
    public void SubscriptionShowsRemainingTrafficAndWarnsEarly()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var sub = new Subscription { Total = 100L << 30, Download = 63L << 30, Expire = now.AddDays(30) };
        Assert.StartsWith("Осталось 37 ГБ из 100 ГБ · до ", sub.UsageText(now)); Assert.Equal("", sub.WarningText(now));
        sub.Download = 95L << 30; Assert.Equal("Осталось мало трафика", sub.WarningText(now));
        sub.Download = 0; sub.Expire = now.AddHours(30); Assert.Equal("Подписка закончится через 2 дн.", sub.WarningText(now));
        sub.Expire = now.AddMinutes(-1); Assert.Equal("Подписка истекла — продлите её у провайдера", sub.WarningText(now));
        Assert.Equal("", new Subscription().UsageText(now));
    }
    [Fact]
    public void RemoteSubscriptionsRefreshAtProviderIntervalOrTwelveHours()
    {
        var now = DateTimeOffset.Now;
        var sub = new Subscription { Source = "https://p.example/s", Updated = now.AddHours(-11) };
        Assert.False(sub.RefreshDue(now)); Assert.True(sub.RefreshDue(now.AddHours(1)));
        sub.UpdateIntervalHours = 2; Assert.True(sub.RefreshDue(now));
        Assert.True(new Subscription { Source = "https://p.example/s" }.RefreshDue(now));
        Assert.False(new Subscription { Source = "vless://pasted", Updated = now.AddDays(-9) }.RefreshDue(now));
        Assert.False(new Subscription { Source = "tt://not-a-link", Updated = now.AddDays(-9) }.RefreshDue(now));
    }
}
