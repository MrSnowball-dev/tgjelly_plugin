using Telegram.Bot;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Creates Telegram.Bot clients with TgJelly's HTTP and proxy settings.
/// </summary>
public interface ITelegramClientFactory
{
    /// <summary>
    /// Creates a client for <paramref name="token"/>.
    /// </summary>
    /// <param name="token">The bot token.</param>
    /// <returns>The client.</returns>
    /// <exception cref="System.ArgumentException">With <c>ParamName</c> <c>"token"</c> for a malformed token, or
    /// <c>"baseUrl"</c> for a malformed <c>TGJELLY_BOT_API_BASE_URL</c>.</exception>
    ITelegramBotClient Create(string token);
}
