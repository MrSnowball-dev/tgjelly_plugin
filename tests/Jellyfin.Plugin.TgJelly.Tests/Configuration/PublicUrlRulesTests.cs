using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PublicUrlRulesTests
{
    [Theory]
    [InlineData("https://jf.example.com", true)]
    [InlineData("http://192.168.1.10:8096/jf", true)]
    [InlineData("http://[fd00::1]:8096", true)]
    [InlineData("http://localhost:8096", false)]
    [InlineData("http://jellyfin:8096", false)]
    [InlineData("ftp://jf.example.com", false)]
    [InlineData("jf.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid(string? url, bool expected) => Assert.Equal(expected, PublicUrlRules.IsValid(url));
}
