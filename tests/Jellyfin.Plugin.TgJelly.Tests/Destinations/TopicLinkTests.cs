using Jellyfin.Plugin.TgJelly.Destinations;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public class TopicLinkTests
{
    [Theory]
    [InlineData("https://t.me/c/1234567890/42", -1001234567890L, null, 42)]
    [InlineData("https://t.me/c/1234567890/42/100", -1001234567890L, null, 42)]
    [InlineData("t.me/c/1234567890/42", -1001234567890L, null, 42)]
    [InlineData("https://telegram.me/c/1234567890/7", -1001234567890L, null, 7)]
    [InlineData("https://t.me/jfgroup/42", null, "@jfgroup", 42)]
    [InlineData("https://t.me/jfgroup/42/9", null, "@jfgroup", 42)]
    public void TryParse_ValidLinks(string text, long? chatId, string? username, int thread)
    {
        Assert.True(TopicLink.TryParse(text, out var link));
        Assert.Equal(thread, link.ThreadId);
        Assert.Equal(chatId, link.Chat.Identifier);
        Assert.Equal(username, link.Chat.Username);
    }

    [Theory]
    [InlineData("https://t.me/c/1234567890")]
    [InlineData("https://t.me/c/1234567890/0")]
    [InlineData("https://t.me/c/abc/42")]
    [InlineData("https://example.com/c/1234567890/42")]
    [InlineData("https://t.me/jfgroup")]
    [InlineData("not a link")]
    [InlineData("")]
    public void TryParse_RejectsEverythingElse(string text) => Assert.False(TopicLink.TryParse(text, out _));
}
