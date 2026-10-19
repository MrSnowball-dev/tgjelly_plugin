using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// An HTML message that asks the user something through the reply area.
/// </summary>
/// <param name="ChatId">The chat.</param>
/// <param name="Html">The HTML text.</param>
/// <param name="Markup">A <see cref="ReplyKeyboardMarkup"/>, <see cref="ReplyKeyboardRemove"/> or <see cref="ForceReplyMarkup"/>.</param>
public sealed record OutgoingPrompt(long ChatId, string Html, ReplyMarkup Markup);
