using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public class MenuScreensTests
{
    private readonly MenuScreens _menus;

    public MenuScreensTests()
    {
        var strings = StringTable.LoadEmbedded();
        _menus = new MenuScreens(strings, new Formatter(strings));
    }

    [Fact]
    public void Main_ShowsDetectedOnlyWhenAny()
    {
        Assert.DoesNotContain(Labels(_menus.Main(2, 0)), l => l.StartsWith("Chats added outside setup", StringComparison.Ordinal));

        var labels = Labels(_menus.Main(2, 3));
        Assert.Equal("1|xl|0", _menus.Main(2, 3).Buttons!.InlineKeyboard.SelectMany(r => r).Last().CallbackData);
        Assert.Equal(["Destinations (2)", "Add destination", "Settings", "Status", "Chats added outside setup (3)"], labels);
    }

    [Fact]
    public void MainMenu_ButtonsAndHeadingCarryTheirIcons()
    {
        var screen = _menus.Main(2, 0);

        Assert.Equal(["Destinations (2)", "Add destination", "Settings", "Status"], Labels(screen));
        Assert.Equal(
            [Icons.Chats, Icons.Add, Icons.Settings, Icons.Status],
            screen.Buttons!.InlineKeyboard.SelectMany(r => r).Select(b => b.IconCustomEmojiId).ToArray());
        Assert.StartsWith("<b><tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu</b>", screen.Html, StringComparison.Ordinal);
    }

    private static string[] Labels(Screen screen)
        => screen.Buttons!.InlineKeyboard.SelectMany(r => r).Select(b => b.Text).ToArray();
}
