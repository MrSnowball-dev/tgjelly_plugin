using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The "Advanced options" screen (spec §6.5): the manual fallbacks, reached only from the bot's failure messages.
/// </summary>
public sealed class AdvancedScreen
{
    private readonly BotConnection _connection;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdvancedScreen"/> class.
    /// </summary>
    /// <param name="connection">The bot connection, for the bot's username.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    public AdvancedScreen(BotConnection connection, StringTable strings, Formatter formatter)
    {
        _connection = connection;
        _strings = strings;
        _formatter = formatter;
    }

    /// <summary>
    /// Builds the options: add to a group or channel (links), use an existing topic, open the dashboard.
    /// </summary>
    /// <returns>The screen.</returns>
    public Screen Options()
    {
        var bot = _connection.BotUsername;
        var rows = new[]
        {
            new[] { InlineKeyboardButton.WithUrl(_strings.Get("advanced.group"), BotLinks.StartGroup(bot)) },
            new[] { InlineKeyboardButton.WithUrl(_strings.Get("advanced.channel"), BotLinks.StartChannel(bot)) },
            new[] { InlineKeyboardButton.WithCallbackData(_strings.Get("advanced.topic"), CallbackData.Format("ol")) },
            new[] { InlineKeyboardButton.WithCallbackData(_strings.Get("advanced.dashboard"), CallbackData.Format("od")) },
        };
        return new Screen(_formatter.Escape(_strings.Get("advanced.title")), new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// Builds the dashboard hint: where to add a chat by ID.
    /// </summary>
    /// <returns>The screen.</returns>
    public Screen Dashboard()
    {
        var back = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData(_strings.Get("menu.back"), CallbackData.Format("ox")) });
        return new Screen(_formatter.Escape(_strings.Get("advanced.dashboardPath")), back);
    }
}
