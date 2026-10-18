namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The result of saving a bot token.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Message">What to show.</param>
/// <param name="BotUsername">The bot's username, when known.</param>
/// <param name="WebhookDeleted">Whether another app's webhook was removed.</param>
public sealed record TokenSaveResult(TokenSaveOutcome Outcome, string Message, string? BotUsername = null, bool WebhookDeleted = false);
