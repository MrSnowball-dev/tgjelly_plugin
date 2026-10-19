using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The bot's conversations and screens (plan 4). The <see cref="BotWorker"/> calls these one at a time, in order,
/// so implementations never run concurrently with each other.
/// </summary>
public interface IBotFlows
{
    /// <summary>
    /// Handles a message sent to the bot in a private chat.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels handling.</param>
    /// <returns>A task.</returns>
    Task OnPrivateMessageAsync(Message message, CancellationToken ct);

    /// <summary>
    /// Handles a button press on a message in a private chat. The query was already answered.
    /// </summary>
    /// <param name="query">The callback query.</param>
    /// <param name="ct">Cancels handling.</param>
    /// <returns>A task.</returns>
    Task OnCallbackAsync(CallbackQuery query, CancellationToken ct);

    /// <summary>
    /// Handles a change of the bot's own membership in a chat.
    /// </summary>
    /// <param name="update">The change.</param>
    /// <param name="ct">Cancels handling.</param>
    /// <returns>A task.</returns>
    Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct);

    /// <summary>
    /// Called about every 30 seconds, for time-based work such as expiring flows.
    /// </summary>
    /// <param name="ct">Cancels the tick.</param>
    /// <returns>A task.</returns>
    Task TickAsync(CancellationToken ct);
}
