using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The main menu.
/// </summary>
public sealed class MenuScreens
{
    /// <summary>The most code points on a button label.</summary>
    internal const int MaxLabelLength = 40;

    private readonly StringTable _strings;
    private readonly Formatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="MenuScreens"/> class.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    public MenuScreens(StringTable strings, Formatter formatter)
    {
        _strings = strings;
        _formatter = formatter;
    }

    /// <summary>
    /// Builds the main menu.
    /// </summary>
    /// <param name="destinations">The number of destinations.</param>
    /// <param name="detected">The number of chats added outside setup; shown only when above 0.</param>
    /// <returns>The screen.</returns>
    public Screen Main(int destinations, int detected) => Build(Icons.Heading(_strings, _formatter, "menu.title"), destinations, detected);

    /// <summary>
    /// Builds the main menu headed by how many destinations are set up, for people who open the bot again.
    /// </summary>
    /// <param name="destinations">The number of destinations.</param>
    /// <param name="detected">The number of chats added outside setup; shown only when above 0.</param>
    /// <returns>The screen.</returns>
    public Screen SetUp(int destinations, int detected) => Build(_formatter.Escape(_strings.Plural("menu.setUp", destinations)), destinations, detected);

    /// <summary>
    /// Cuts a title to a button label: at most 40 code points, ending in <c>…</c> when cut. Button text is plain, so
    /// nothing is escaped; use <see cref="Formatter.Escape"/> for message text.
    /// </summary>
    /// <param name="text">The title.</param>
    /// <returns>The label.</returns>
    internal static string Label(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count <= MaxLabelLength)
        {
            return text;
        }

        var cut = new StringBuilder();
        foreach (var rune in runes.Take(MaxLabelLength - 1))
        {
            cut.Append(rune.ToString());
        }

        return cut.Append('…').ToString();
    }

    private Screen Build(string headingHtml, int destinations, int detected)
    {
        var lines = new List<string>
        {
            ButtonGuide.Line(_formatter, _strings.Get("menu.destinations", ("count", destinations)), _strings.Get("menu.destinations.about")),
            ButtonGuide.Line(_strings, _formatter, "menu.settings", "menu.settings.about"),
            ButtonGuide.Line(_strings, _formatter, "menu.status", "menu.status.about"),
        };
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Buttons.Callback(_strings, "menu.destinations", CallbackData.Format("dl", "0"), ("count", destinations)) },
            new[] { Buttons.Callback(_strings, "menu.settings", CallbackData.Format("s")) },
            new[] { Buttons.Callback(_strings, "menu.status", CallbackData.Format("st")) },
        };
        if (detected > 0)
        {
            rows.Add([Buttons.Callback(_strings, "menu.detected", CallbackData.Format("xl", "0"), ("count", detected))]);
            lines.Add(ButtonGuide.Line(_formatter, _strings.Get("menu.detected", ("count", detected)), _strings.Get("menu.detected.about")));
        }

        return new Screen("<b>" + headingHtml + "</b>" + ButtonGuide.Block([.. lines]), new InlineKeyboardMarkup(rows));
    }
}
