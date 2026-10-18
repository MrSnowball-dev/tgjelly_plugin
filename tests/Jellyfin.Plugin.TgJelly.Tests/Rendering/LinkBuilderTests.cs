using System;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public class LinkBuilderTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly FakeServerInfo _server = new();

    [Fact]
    public void NoDotHost_IsRejected() => Assert.Null(new LinkBuilder(_server).OpenInJellyfin("http://jellyfin:8096", Id));

    [Fact]
    public void Ipv6_IsAccepted()
        => Assert.Equal($"http://[2001:db8::1]:8096/web/#/details?id={Id:N}&serverId=a1b2c3", new LinkBuilder(_server).OpenInJellyfin("http://[2001:db8::1]:8096", Id));

    [Theory]
    [InlineData("https://x.example.com", "https://x.example.com/jf")]
    [InlineData("https://x.example.com/jf/", "https://x.example.com/jf")]
    [InlineData("https://x.example.com/JF", "https://x.example.com/JF")]
    public void BaseUrl_AppendedExactlyOnce(string publicUrl, string expectedRoot)
    {
        _server.BaseUrl = "/jf";
        Assert.Equal($"{expectedRoot}/web/#/details?id={Id:N}&serverId=a1b2c3", new LinkBuilder(_server).OpenInJellyfin(publicUrl, Id));
    }

    [Fact]
    public void BaseUrlWithoutLeadingSlash_IsNormalized()
    {
        _server.BaseUrl = "jf/";
        Assert.Equal($"https://x.example.com/jf/web/#/details?id={Id:N}&serverId=a1b2c3", new LinkBuilder(_server).OpenInJellyfin("https://x.example.com", Id));
    }

    [Fact]
    public void EmptyBaseUrl()
        => Assert.Equal($"https://x.example.com/web/#/details?id={Id:N}&serverId=a1b2c3", new LinkBuilder(_server).OpenInJellyfin("https://x.example.com/", Id));

    [Theory]
    [InlineData("http://192.168.1.5:8096/", "http://192.168.1.5:8096")]
    [InlineData("http://localhost:8096", null)]
    [InlineData(null, null)]
    public void Suggest_NormalizesAndValidates(string? candidate, string? expected)
    {
        _server.Suggestion = candidate;
        Assert.Equal(expected, new LinkBuilder(_server).Suggest());
    }

    [Fact]
    public void ProviderLinks()
    {
        Assert.Equal("https://www.imdb.com/title/tt2543164/", LinkBuilder.Imdb("tt2543164"));
        Assert.Equal("https://www.themoviedb.org/movie/329865", LinkBuilder.Tmdb(false, "329865"));
        Assert.Equal("https://www.themoviedb.org/tv/95396", LinkBuilder.Tmdb(true, "95396"));
    }
}
