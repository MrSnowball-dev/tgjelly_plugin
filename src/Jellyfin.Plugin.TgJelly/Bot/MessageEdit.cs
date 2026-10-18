using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// A new text or caption for a sent message.
/// </summary>
/// <param name="ChatId">The chat.</param>
/// <param name="MessageId">The message.</param>
/// <param name="Html">The new text or caption.</param>
/// <param name="ParseMode">How Telegram parses <paramref name="Html"/>.</param>
/// <param name="Buttons">Inline URL buttons, if any.</param>
public sealed record MessageEdit(long ChatId, int MessageId, string Html, ParseMode ParseMode, InlineKeyboardMarkup? Buttons);
