using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// <see cref="ITelegramSender"/> over a Telegram.Bot client, calling each method with named arguments.
/// </summary>
public sealed class TelegramSender : ITelegramSender
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private readonly ITelegramBotClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramSender"/> class.
    /// </summary>
    /// <param name="client">The client.</param>
    public TelegramSender(ITelegramBotClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public Task<User> GetMeAsync(CancellationToken ct) => _client.GetMe(cancellationToken: ct);

    /// <inheritdoc />
    public Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken ct) => _client.GetWebhookInfo(cancellationToken: ct);

    /// <inheritdoc />
    public Task DeleteWebhookAsync(CancellationToken ct) => _client.DeleteWebhook(dropPendingUpdates: false, cancellationToken: ct);

    /// <inheritdoc />
    public Task<ChatFullInfo> GetChatAsync(ChatId chat, CancellationToken ct) => _client.GetChat(chatId: chat, cancellationToken: ct);

    /// <inheritdoc />
    public Task<ChatMember> GetChatMemberAsync(ChatId chat, long userId, CancellationToken ct)
        => _client.GetChatMember(chatId: chat, userId: userId, cancellationToken: ct);

    /// <inheritdoc />
    public Task<Message> SendTextAsync(OutgoingText message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _client.SendMessage(
            chatId: message.ChatId,
            text: message.Text,
            parseMode: message.ParseMode,
            replyParameters: Reply(message.ReplyToMessageId),
            replyMarkup: message.Buttons,
            linkPreviewOptions: NoPreview,
            messageThreadId: Thread(message.ThreadId),
            disableNotification: message.Silent,
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task<Message> SendPhotoAsync(OutgoingPhoto message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _client.SendPhoto(
            chatId: message.ChatId,
            photo: message.Photo,
            caption: message.Caption,
            parseMode: message.ParseMode,
            replyParameters: Reply(message.ReplyToMessageId),
            replyMarkup: message.Buttons,
            messageThreadId: Thread(message.ThreadId),
            disableNotification: message.Silent,
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task EditTextAsync(MessageEdit edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return _client.EditMessageText(
            chatId: edit.ChatId,
            messageId: edit.MessageId,
            text: edit.Html,
            parseMode: edit.ParseMode,
            replyMarkup: edit.Buttons,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task EditCaptionAsync(MessageEdit edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return _client.EditMessageCaption(
            chatId: edit.ChatId,
            messageId: edit.MessageId,
            caption: edit.Html,
            parseMode: edit.ParseMode,
            replyMarkup: edit.Buttons,
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task SendTypingAsync(long chatId, int threadId, CancellationToken ct)
        => _client.SendChatAction(chatId: chatId, action: ChatAction.Typing, messageThreadId: Thread(threadId), cancellationToken: ct);

    /// <inheritdoc />
    public Task LeaveChatAsync(long chatId, CancellationToken ct) => _client.LeaveChat(chatId: chatId, cancellationToken: ct);

    /// <inheritdoc />
    public Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)
        => _client.AnswerCallbackQuery(callbackQueryId: callbackQueryId, cancellationToken: ct);

    /// <inheritdoc />
    public async Task SetCommandsAsync(IReadOnlyList<BotCommand> commands, CancellationToken ct)
    {
        await _client.SetMyCommands(commands: commands, scope: new BotCommandScopeAllPrivateChats(), cancellationToken: ct).ConfigureAwait(false);
        await _client.SetMyCommands(commands: [], scope: new BotCommandScopeDefault(), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetDescriptionsAsync(string description, string shortDescription, CancellationToken ct)
    {
        await _client.SetMyDescription(description: description, cancellationToken: ct).ConfigureAwait(false);
        await _client.SetMyShortDescription(shortDescription: shortDescription, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetChannelAdminRightsAsync(CancellationToken ct)
        => _client.SetMyDefaultAdministratorRights(
            rights: new ChatAdministratorRights { CanPostMessages = true, CanEditMessages = true },
            forChannels: true,
            cancellationToken: ct);

    private static int? Thread(int threadId) => threadId == 0 ? null : threadId;

    private static ReplyParameters? Reply(int? messageId)
        => messageId is { } id ? new ReplyParameters { MessageId = id, AllowSendingWithoutReply = true } : null;
}
