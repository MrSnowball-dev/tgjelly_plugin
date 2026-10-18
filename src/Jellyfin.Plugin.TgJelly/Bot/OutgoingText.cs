using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// A text message to send.
/// </summary>
/// <param name="ChatId">The chat.</param>
/// <param name="ThreadId">The forum topic, or 0 for none / General.</param>
/// <param name="Text">The text.</param>
/// <param name="ParseMode">How Telegram parses <paramref name="Text"/>.</param>
/// <param name="Buttons">Inline URL buttons, if any.</param>
/// <param name="ReplyToMessageId">The message to reply to, if any.</param>
/// <param name="Silent">Whether to send without a notification sound.</param>
public sealed record OutgoingText(long ChatId, int ThreadId, string Text, ParseMode ParseMode, InlineKeyboardMarkup? Buttons, int? ReplyToMessageId, bool Silent);
