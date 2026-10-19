using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PublicUrlRulesTests
{
    [Theory]
    [InlineData("https://jf.example.com", true)]
    [InlineData("http://192.168.1.10:8096/jf", true)]
    [InlineData("http://10.0.0.5:8096", true)]
    [InlineData("http://jellyfin.lan:8096", true)]
    [InlineData("http://localhost:8096", false)]
    [InlineData("http://jellyfin:8096", false)]
    [InlineData("ftp://jf.example.com", false)]
    [InlineData("jf.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid(string? url, bool expected) => Assert.Equal(expected, PublicUrlRules.IsValid(url));

    [Theory]
    [InlineData("https://[2001:db8::1]:8096")]
    [InlineData("http://[fd00::1]")]
    public void Ipv6Literal_IsInvalid(string url) => Assert.False(PublicUrlRules.IsValid(url));

    [Fact]
    public void PrivateIpv4_IsValid() => Assert.True(PublicUrlRules.IsValid("http://192.168.1.5:8096"));
}
