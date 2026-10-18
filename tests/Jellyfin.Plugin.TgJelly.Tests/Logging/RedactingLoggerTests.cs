using System;
using System.Net.Http;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Logging;

public class RedactingLoggerTests
{
    private const string Token = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ";

    [Fact]
    public void TokenInMessageAndException_IsRedacted()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        var logger = new RedactingLogger<RedactingLoggerTests>(factory);
        var inner = new HttpRequestException($"failed for /bot{Token}/getUpdates");

        logger.LogError(new InvalidOperationException($"outer {Token}", inner), "Request {Url} failed", $"/bot{Token}/getMe");

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.DoesNotContain(Token, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, entry.Exception, StringComparison.Ordinal);
        Assert.Contains("<token>", entry.Message, StringComparison.Ordinal);
        Assert.Contains("<token>", entry.Exception, StringComparison.Ordinal);
        Assert.Equal(typeof(RedactingLoggerTests).FullName, entry.Category);
    }

    [Fact]
    public void DisabledLevels_AreNotLogged()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Warning));
        var logger = new RedactingLogger<RedactingLoggerTests>(factory);

        logger.LogInformation("quiet");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.Empty(provider.Entries);
    }
}
