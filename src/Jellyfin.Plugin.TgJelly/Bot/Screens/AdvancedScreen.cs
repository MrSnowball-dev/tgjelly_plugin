using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The "Advanced options" screen (spec §6.5): the manual fallbacks, reached only from the bot's failure messages.
/// </summary>
public sealed class AdvancedScreen
{
    private readonly StringTable _strings;
    private readonly Formatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdvancedScreen"/> class.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    public AdvancedScreen(StringTable strings, Formatter formatter)
    {
        _strings = strings;
        _formatter = formatter;
    }

    /// <summary>
    /// Builds the options: use an existing topic, open the dashboard. Adding to a group or channel is on the setup
    /// prompt itself, so it isn't repeated here.
    /// </summary>
    /// <returns>The screen.</returns>
    public Screen Options()
    {
        var rows = new[]
        {
            new[] { Buttons.Callback(_strings, "advanced.topic", CallbackData.Format("ol")) },
            new[] { Buttons.Callback(_strings, "advanced.dashboard", CallbackData.Format("od")) },
        };
        return new Screen(_formatter.Escape(_strings.Get("advanced.title")), new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// Builds the dashboard hint: where to add a chat by ID.
    /// </summary>
    /// <returns>The screen.</returns>
    public Screen Dashboard()
    {
        var back = new InlineKeyboardMarkup(new[] { Buttons.Callback(_strings, "menu.back", CallbackData.Format("ox")) });
        return new Screen(_formatter.Escape(_strings.Get("advanced.dashboardPath")), back);
    }
}
