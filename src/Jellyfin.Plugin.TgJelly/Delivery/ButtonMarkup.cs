using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// Turns <see cref="OutboxButton"/> rows into Telegram's inline keyboard, the one way for notifications and test sends:
/// a button with <see cref="OutboxButton.CallbackData"/> is a callback button, any other a URL button.
/// </summary>
public static class ButtonMarkup
{
    /// <summary>
    /// Builds the keyboard.
    /// </summary>
    /// <param name="rows">The button rows.</param>
    /// <returns>The keyboard, or <c>null</c> without buttons.</returns>
    public static InlineKeyboardMarkup? Of(IEnumerable<IEnumerable<OutboxButton>> rows)
    {
        var keyboard = rows.Select(row => row.Select(b => b.CallbackData is { } data
            ? InlineKeyboardButton.WithCallbackData(b.Text, data)
            : InlineKeyboardButton.WithUrl(b.Text, b.Url)).ToArray()).Where(row => row.Length > 0).ToArray();
        return keyboard.Length == 0 ? null : new InlineKeyboardMarkup(keyboard);
    }
}
