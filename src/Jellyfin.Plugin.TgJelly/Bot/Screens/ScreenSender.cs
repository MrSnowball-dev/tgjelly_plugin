using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// Shows a <see cref="Screen"/> by editing the message it was last shown in, or by sending a new one.
/// </summary>
public sealed class ScreenSender
{
    private readonly BotConnection _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenSender"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    public ScreenSender(BotConnection connection) => _connection = connection;

    /// <summary>
    /// Shows a screen. With a message ID the message is edited; when Telegram says it is gone or can't be edited, a new
    /// one is sent instead. Telegram's "message is not modified" counts as success.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="screen">The screen.</param>
    /// <param name="editMessageId">The message to edit, or <c>null</c> to send a new one.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The ID of the message that now shows the screen.</returns>
    /// <exception cref="BotNotConnectedException">No bot is attached.</exception>
    public async Task<int> ShowAsync(long chatId, Screen screen, int? editMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var sender = _connection.RequireSender();
        if (editMessageId is { } id)
        {
            try
            {
                await sender.EditTextAsync(new MessageEdit(chatId, id, screen.Html, ParseMode.Html, screen.Buttons), ct).ConfigureAwait(false);
                return id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                switch (TelegramErrorClassifier.ClassifyDelivery(ex).Kind)
                {
                    case DeliveryErrorKind.NotModified:
                        return id;
                    case DeliveryErrorKind.EditTargetGone:
                        break;
                    default:
                        throw;
                }
            }
        }

        var sent = await sender.SendTextAsync(new OutgoingText(chatId, 0, screen.Html, ParseMode.Html, screen.Buttons, null, false), ct).ConfigureAwait(false);
        return sent.MessageId;
    }
}
