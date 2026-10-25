using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The only door to the Bot API besides the supervisor's GetUpdates loop (spec §5.4 rule 1): one thin method per
/// call. Exceptions are not caught here; callers classify them with <c>TelegramErrorClassifier</c>.
/// </summary>
public interface ITelegramSender
{
    /// <summary>Calls <c>getMe</c>.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The bot user.</returns>
    Task<User> GetMeAsync(CancellationToken ct);

    /// <summary>Calls <c>getWebhookInfo</c>.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The webhook info.</returns>
    Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken ct);

    /// <summary>Calls <c>deleteWebhook</c>, keeping pending updates.</summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task DeleteWebhookAsync(CancellationToken ct);

    /// <summary>Calls <c>getChat</c>.</summary>
    /// <param name="chat">The chat ID or <c>@username</c>.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The chat.</returns>
    Task<ChatFullInfo> GetChatAsync(ChatId chat, CancellationToken ct);

    /// <summary>Calls <c>getChatMember</c>.</summary>
    /// <param name="chat">The chat ID or <c>@username</c>.</param>
    /// <param name="userId">The user.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The member.</returns>
    Task<ChatMember> GetChatMemberAsync(ChatId chat, long userId, CancellationToken ct);

    /// <summary>Calls <c>sendMessage</c> with link previews off.</summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The sent message.</returns>
    Task<Message> SendTextAsync(OutgoingText message, CancellationToken ct);

    /// <summary>Calls <c>sendPhoto</c>.</summary>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The sent message.</returns>
    Task<Message> SendPhotoAsync(OutgoingPhoto message, CancellationToken ct);

    /// <summary>Calls <c>editMessageText</c> with link previews off.</summary>
    /// <param name="edit">The edit.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task EditTextAsync(MessageEdit edit, CancellationToken ct);

    /// <summary>Calls <c>editMessageCaption</c>.</summary>
    /// <param name="edit">The edit.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task EditCaptionAsync(MessageEdit edit, CancellationToken ct);

    /// <summary>Calls <c>sendChatAction</c> with <c>typing</c>; proves a forum topic exists without posting.</summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="threadId">The topic, or 0.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task SendTypingAsync(long chatId, int threadId, CancellationToken ct);

    /// <summary>Calls <c>leaveChat</c>.</summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task LeaveChatAsync(long chatId, CancellationToken ct);

    /// <summary>Calls <c>answerCallbackQuery</c> without text.</summary>
    /// <param name="callbackQueryId">The callback query.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>A task.</returns>
    Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct);

    /// <summary>Calls <c>sendMessage</c> in HTML with reply-area markup (only <c>ReplyKeyboardRemove</c>, for stale v0.1 keyboards).</summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The sent message.</returns>
    Task<Message> SendPromptAsync(OutgoingPrompt prompt, CancellationToken ct);

    /// <summary>Calls <c>deleteMessage</c>.</summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns><c>true</c> if deleted; <c>false</c> if the message is gone or cannot be deleted.</returns>
    Task<bool> DeleteMessageAsync(long chatId, int messageId, CancellationToken ct);

    /// <summary>Calls <c>createForumTopic</c>.</summary>
    /// <param name="chatId">The forum supergroup.</param>
    /// <param name="name">The topic name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The new topic's <c>message_thread_id</c>.</returns>
    Task<int> CreateForumTopicAsync(long chatId, string name, CancellationToken ct);
}
