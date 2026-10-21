using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public class IconsTests
{
    private readonly StringTable _strings = StringTable.LoadEmbedded();

    [Fact]
    public void EveryIconKey_IsAString()
    {
        foreach (var key in Icons.ForLabel.Keys.Concat(Icons.ForHeading.Keys))
        {
            Assert.NotEqual(key, _strings.Get(key));
        }
    }

    [Fact]
    public void NoLabelOrHeadingWithAnIcon_StartsWithAnEmoji()
    {
        foreach (var key in Icons.ForLabel.Keys.Concat(Icons.ForHeading.Keys))
        {
            var first = _strings.Get(key, ("count", 1), ("title", "T")).EnumerateRunes().First().Value;
            var emoji = first is (>= 0x1F000 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or (>= 0x2B00 and <= 0x2BFF) or '‹';
            Assert.False(emoji, key + " starts with an emoji");
        }
    }

    [Fact]
    public void Heading_WrapsTheFallbackEmoji()
    {
        var html = Icons.Heading(_strings, new Formatter(_strings), "menu.title");

        Assert.Equal("<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu", html);
    }

    [Fact]
    public void Heading_WithoutAnIcon_IsJustTheEscapedText()
        => Assert.Equal("Send a test message to", Icons.Heading(_strings, new Formatter(_strings), "dest.picker.title"));

    [Fact]
    public void StripHtml_LeavesTheFallback()
        => Assert.Equal("<b>🏠 Menu</b>", Icons.StripHtml("<b><tg-emoji emoji-id=\"1\">🏠</tg-emoji> Menu</b>"));
}
