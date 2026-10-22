using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;
public class ImportLinkTests
{
    [Theory]
    [InlineData("bebekon://add/https://panel.example/sub/abc?x=1", "https://panel.example/sub/abc?x=1")]
    [InlineData("bebekon://add/https%3A%2F%2Fpanel.example%2Fsub%2Fabc", "https://panel.example/sub/abc")]
    [InlineData("bebekon://import?url=https%3A%2F%2Fpanel.example%2Fs%3Ft%3Da%2Bb&name=x", "https://panel.example/s?t=a+b")]
    [InlineData("  BEBEKON://install-config?url=vless%3A%2F%2Fid%40host%3A443%23Name ", "vless://id@host:443#Name")]
    public void DecodesProviderLinks(string text, string expected) => Assert.Equal(expected, ImportLink.Decode(text));

    [Theory]
    [InlineData("bebekon://add/javascript:alert(1)")]
    [InlineData("bebekon://add/file:///C:/Windows/win.ini")]
    [InlineData("bebekon://add/http://plain.example/sub")]
    [InlineData("bebekon://add/https://user:pass@panel.example/sub")]
    [InlineData("bebekon://connect?server=1")]
    [InlineData("bebekon://import?name=only")]
    [InlineData("bebekon://add/https%3A%2F%2Fpanel.example%2F%0D%0AX")]
    [InlineData("https://panel.example/sub")]
    public void RejectsEverythingElse(string text) => Assert.Null(ImportLink.Decode(text));
}
