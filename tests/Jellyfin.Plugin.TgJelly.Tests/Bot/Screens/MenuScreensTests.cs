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
        Assert.Equal(["Destinations (2)", "Settings", "Status", "Chats added outside setup (3)"], labels);
    }

    [Fact]
    public void MainMenu_ButtonsAndHeadingCarryTheirIcons()
    {
        var screen = _menus.Main(2, 0);

        Assert.Equal(["Destinations (2)", "Settings", "Status"], Labels(screen));
        Assert.Equal(
            [Icons.Chats, Icons.Settings, Icons.Status],
            screen.Buttons!.InlineKeyboard.SelectMany(r => r).Select(b => b.IconCustomEmojiId).ToArray());
        Assert.StartsWith("<b><tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu</b>", screen.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void MainMenu_ExplainsEachButton()
    {
        var plain = _menus.Main(2, 0).Html;
        Assert.Contains("<b>Destinations (2)</b> — see and change the chats I send to", plain, StringComparison.Ordinal);
        Assert.Contains("<b>Settings</b> — message style, update checks and the public URL", plain, StringComparison.Ordinal);
        Assert.Contains("<b>Status</b> — connection, destination health and recent errors", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Chats added outside setup", plain, StringComparison.Ordinal);

        var withDetected = _menus.SetUp(2, 1).Html;
        Assert.Contains("<b>Chats added outside setup (1)</b> — chats someone added me to outside setup; set them up or dismiss them", withDetected, StringComparison.Ordinal);
        Assert.Contains("<b>Status</b> — ", withDetected, StringComparison.Ordinal);
    }

    private static string[] Labels(Screen screen)
        => screen.Buttons!.InlineKeyboard.SelectMany(r => r).Select(b => b.Text).ToArray();
}
