using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// Builds inline buttons whose label is a string key, with the label's icon from <see cref="Icons.ForLabel"/>.
/// </summary>
internal static class Buttons
{
    /// <summary>
    /// Builds a callback button.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="labelKey">The label's string key.</param>
    /// <param name="data">The callback data.</param>
    /// <param name="args">The label's arguments.</param>
    /// <returns>The button.</returns>
    public static InlineKeyboardButton Callback(StringTable strings, string labelKey, string data, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var button = InlineKeyboardButton.WithCallbackData(strings.Get(labelKey, args), data);
        button.IconCustomEmojiId = Icons.ForLabel.GetValueOrDefault(labelKey);
        return button;
    }

    /// <summary>
    /// Builds a URL button.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="labelKey">The label's string key.</param>
    /// <param name="url">The URL.</param>
    /// <returns>The button.</returns>
    public static InlineKeyboardButton Url(StringTable strings, string labelKey, string url)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var button = InlineKeyboardButton.WithUrl(strings.Get(labelKey), url);
        button.IconCustomEmojiId = Icons.ForLabel.GetValueOrDefault(labelKey);
        return button;
    }
}
