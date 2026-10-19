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
        Assert.Equal(["📬 Destinations (2)", "➕ Add destination", "⚙️ Settings", "🩺 Status", "Chats added outside setup (3)"], labels);
    }

    [Fact]
    public void HostileTitles_AreEscaped_AndCut()
    {
        var title = "<b>A&B</b>" + string.Concat(Enumerable.Repeat("😀", 60));
        var destination = new Destination { Id = "abcd1234", Title = title };

        var screen = _menus.Destinations([destination], 0);

        Assert.Contains("&lt;b&gt;A&amp;B&lt;/b&gt;", screen.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>A&B", screen.Html, StringComparison.Ordinal);
        var label = Labels(screen).First(l => l.StartsWith("<b>A&B", StringComparison.Ordinal));
        Assert.Equal(40, label.EnumerateRunes().Count());
        Assert.EndsWith("…", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Destinations_PageEightAtATime()
    {
        var all = Enumerable.Range(1, 10).Select(i => new Destination { Id = "id" + i, Title = "T" + i }).ToList();

        var second = Labels(_menus.Destinations(all, 1));

        Assert.Contains("T9", second);
        Assert.DoesNotContain("T1", second);
        Assert.Contains("‹ Prev", second);
        Assert.DoesNotContain("Next ›", second);
    }

    private static string[] Labels(Screen screen)
        => screen.Buttons!.InlineKeyboard.SelectMany(r => r).Select(b => b.Text).ToArray();
}
