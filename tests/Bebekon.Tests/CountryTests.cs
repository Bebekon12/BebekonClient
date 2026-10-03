using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class CountryTests
{
    [Theory]
    [InlineData("🇱🇻 Латвия #2", "LV", "Латвия #2")]
    [InlineData("DE Германия", "DE", "Германия")]
    [InlineData("LT Литва — YouTube", "LT", "Литва — YouTube")]
    [InlineData("NL Нидерланды", "NL", "Нидерланды")]
    [InlineData("SE Швеция", "SE", "Швеция")]
    [InlineData("EU Hysteria", "EU", "Hysteria")]
    [InlineData("UK London", "GB", "London")]
    [InlineData("Russia, Saint Petersburg", "RU", "Russia, Saint Petersburg")]
    [InlineData("USA • New York", "US", "USA • New York")]
    [InlineData("Norway", "NO", "Norway")]
    [InlineData("Сингапур", "SG", "Сингапур")]
    public void ProviderLabelsResolve(string label, string code, string display)
    {
        Assert.Equal(code, CountryInfo.Resolve(label));
        Assert.Equal(display, CountryInfo.DisplayName(label));
    }
    [Theory]
    [InlineData("Private server")]
    [InlineData("RUle-based node")]
    [InlineData("MyUKservice")]
    [InlineData("")]
    public void UnknownLabelsDoNotInventCountry(string label) => Assert.Null(CountryInfo.Resolve(label));
    [Fact]
    public void OldSettingsKeepAppearanceDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"mtu\":1500}", Json.Options)!;
        Assert.True(settings.Animations); Assert.True(settings.GlowEffects);
        Assert.Equal("Cyan", settings.AccentColor); Assert.False(settings.PureBlack);
    }
}
