using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// One screen of the bot: HTML text and its inline buttons.
/// </summary>
/// <param name="Html">The text, already escaped for Telegram HTML.</param>
/// <param name="Buttons">The inline buttons, if any.</param>
public sealed record Screen(string Html, InlineKeyboardMarkup? Buttons);
