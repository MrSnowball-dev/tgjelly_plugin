using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public class ButtonGuideTests
{
    private readonly StringTable _strings = StringTable.LoadEmbedded();

    [Fact]
    public void Lines_ListsEachButtonInOrder()
    {
        var text = ButtonGuide.Lines(_strings, new Formatter(_strings), ("onboard.tryAgain", "onboard.tryAgain.about"), ("onboard.advanced", "onboard.advanced.about"));

        Assert.Equal(
            "\n\n<b>Try again</b> — check this chat again after you've fixed it\n<b>Advanced options</b> — other ways to add a chat: an existing topic's link, or the dashboard",
            text);
    }

    [Fact]
    public void Line_EscapesTheLabelAndTheAbout()
    {
        var line = ButtonGuide.Line(new Formatter(_strings), "Set up <Tom & Jerry>", "adds <it>");

        Assert.Equal("<b>Set up &lt;Tom &amp; Jerry&gt;</b> — adds &lt;it&gt;", line);
    }
}
