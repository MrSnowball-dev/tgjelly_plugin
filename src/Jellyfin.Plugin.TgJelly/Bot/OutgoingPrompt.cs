using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// An HTML message whose markup acts on the reply area. The bot only uses it to clear a keyboard that a v0.1 client
/// still shows (stale input); every other screen is inline.
/// </summary>
/// <param name="ChatId">The chat.</param>
/// <param name="Html">The HTML text.</param>
/// <param name="Markup">A <see cref="ReplyKeyboardRemove"/>.</param>
public sealed record OutgoingPrompt(long ChatId, string Html, ReplyMarkup Markup);
