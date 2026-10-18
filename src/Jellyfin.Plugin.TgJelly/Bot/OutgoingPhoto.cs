using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// A photo message to send.
/// </summary>
/// <param name="ChatId">The chat.</param>
/// <param name="ThreadId">The forum topic, or 0 for none / General.</param>
/// <param name="Photo">An upload or a cached file id.</param>
/// <param name="Caption">The caption.</param>
/// <param name="ParseMode">How Telegram parses <paramref name="Caption"/>.</param>
/// <param name="Buttons">Inline URL buttons, if any.</param>
/// <param name="ReplyToMessageId">The message to reply to, if any.</param>
/// <param name="Silent">Whether to send without a notification sound.</param>
public sealed record OutgoingPhoto(long ChatId, int ThreadId, InputFile Photo, string Caption, ParseMode ParseMode, InlineKeyboardMarkup? Buttons, int? ReplyToMessageId, bool Silent);
