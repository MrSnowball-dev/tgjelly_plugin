using System;
using System.Net;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public class TelegramClientFactoryTests
{
    private const string Token = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ";

    [Fact]
    public void Options_DefaultToTheRealApi_WithRetriesOff()
    {
        var options = TelegramClientFactory.CreateOptions(Token, new BotApiEnvironment(null));

        Assert.False(options.LocalBotServer);
        Assert.Equal(0, options.RetryThreshold);
        Assert.Equal(123456789, options.BotId);
    }

    [Fact]
    public void Options_UseTheEnvironmentBaseUrl()
        => Assert.True(TelegramClientFactory.CreateOptions(Token, new BotApiEnvironment("http://stub:8081")).LocalBotServer);

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("abc:def")]
    public void BadToken_ThrowsWithParamNameToken(string token)
        => Assert.Equal("token", Assert.Throws<ArgumentException>(() => TelegramClientFactory.CreateOptions(token, new BotApiEnvironment(null))).ParamName);

    [Fact]
    public void BadBaseUrl_ThrowsWithParamNameBaseUrl()
        => Assert.Equal("baseUrl", Assert.Throws<ArgumentException>(() => TelegramClientFactory.CreateOptions(Token, new BotApiEnvironment("stub"))).ParamName);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("http://stub:8081", "http://stub:8081")]
    public void Environment_TreatsBlankAsUnset(string? value, string? expected)
        => Assert.Equal(expected, BotApiEnvironment.FromValue(value).BaseUrl);

    [Theory]
    [InlineData(ProxyMode.None, "", false, null)]
    [InlineData(ProxyMode.System, "", true, null)]
    [InlineData(ProxyMode.Http, "proxy.lan:3128", true, "http://proxy.lan:3128/")]
    [InlineData(ProxyMode.Socks5, "socks5://10.0.0.2:1080", true, "socks5://10.0.0.2:1080/")]
    [InlineData(ProxyMode.Socks5, "10.0.0.2:1080", true, "socks5://10.0.0.2:1080/")]
    public void Handler_FollowsTheProxyMode(ProxyMode mode, string url, bool useProxy, string? proxyUri)
    {
        using var handler = TelegramClientFactory.CreateHandler(new ProxySettings { Mode = mode, Url = url }, BotSecrets.Empty);

        Assert.Equal(TimeSpan.FromMinutes(3), handler.PooledConnectionLifetime);
        Assert.Equal(useProxy, handler.UseProxy);
        Assert.Equal(proxyUri, (handler.Proxy as WebProxy)?.Address?.ToString());
    }

    [Fact]
    public void Handler_UsesProxyCredentials()
    {
        using var handler = TelegramClientFactory.CreateHandler(
            new ProxySettings { Mode = ProxyMode.Http, Url = "http://proxy.lan:3128" },
            BotSecrets.Empty with { ProxyUsername = "u", ProxyPassword = "p" });

        var credentials = (NetworkCredential)((WebProxy)handler.Proxy!).Credentials!;
        Assert.Equal(("u", "p"), (credentials.UserName, credentials.Password));
    }
}
