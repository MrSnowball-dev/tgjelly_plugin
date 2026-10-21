using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// <see cref="ITelegramSender"/> over a Telegram.Bot client, calling each method with named arguments.
/// </summary>
public sealed class TelegramSender : ITelegramSender
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private readonly ITelegramBotClient _client;
    private readonly ILogger? _logger;
    private volatile bool _iconsBlocked;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramSender"/> class.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="logger">Logs the warning when Telegram rejects the custom icons; optional.</param>
    public TelegramSender(ITelegramBotClient client, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _logger = logger;
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
    public async Task<Message> SendTextAsync(OutgoingText message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_iconsBlocked)
        {
            message = Plain(message);
        }

        try
        {
            return await SendCoreAsync(message, ct).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (MayRejectIcons(ex) && HasIcons(message.Text, message.Buttons))
        {
            // A failed retry throws its own error and leaves the icons on: they weren't the problem.
            var sent = await SendCoreAsync(Plain(message), ct).ConfigureAwait(false);
            BlockIcons();
            return sent;
        }
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
    public async Task EditTextAsync(MessageEdit edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_iconsBlocked)
        {
            edit = Plain(edit);
        }

        try
        {
            await EditCoreAsync(edit, ct).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (MayRejectIcons(ex) && HasIcons(edit.Html, edit.Buttons))
        {
            await EditCoreAsync(Plain(edit), ct).ConfigureAwait(false);
            BlockIcons();
        }
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

    /// <inheritdoc />
    public Task<Message> SendPromptAsync(OutgoingPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return _client.SendMessage(
            chatId: prompt.ChatId,
            text: prompt.Html,
            parseMode: ParseMode.Html,
            replyMarkup: prompt.Markup,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteMessageAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _client.DeleteMessage(chatId: chatId, messageId: messageId, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400
            && (ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("message can't be deleted", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> CreateForumTopicAsync(long chatId, string name, CancellationToken ct)
    {
        var topic = await _client.CreateForumTopic(chatId: chatId, name: name, cancellationToken: ct).ConfigureAwait(false);
        return topic.MessageThreadId;
    }

    /// <summary>
    /// Whether a failed send or edit may have been rejected because of its custom emoji: Telegram's wording for that
    /// varies, so any 400 counts except "message is not modified" and a gone edit target, which say nothing about
    /// icons. The message is then retried once without them, and only a retry that goes through blocks them.
    /// </summary>
    private static bool MayRejectIcons(ApiRequestException ex)
        => ex.ErrorCode == 400
            && TelegramErrorClassifier.ClassifyDelivery(ex).Kind is not (DeliveryErrorKind.NotModified or DeliveryErrorKind.EditTargetGone);

    private static bool HasIcons(string html, InlineKeyboardMarkup? markup)
        => html.Contains("<tg-emoji", StringComparison.Ordinal)
            || (markup is not null && markup.InlineKeyboard.Any(row => row.Any(b => b.IconCustomEmojiId is not null)));

    private static InlineKeyboardMarkup? WithoutIcons(InlineKeyboardMarkup? markup)
    {
        if (markup is null)
        {
            return null;
        }

        return new InlineKeyboardMarkup(markup.InlineKeyboard.Select(row => row.Select(b => new InlineKeyboardButton(b.Text)
        {
            CallbackData = b.CallbackData,
            Url = b.Url,
            WebApp = b.WebApp,
            LoginUrl = b.LoginUrl,
            SwitchInlineQuery = b.SwitchInlineQuery,
            SwitchInlineQueryCurrentChat = b.SwitchInlineQueryCurrentChat,
            SwitchInlineQueryChosenChat = b.SwitchInlineQueryChosenChat,
            CopyText = b.CopyText,
            CallbackGame = b.CallbackGame,
            Pay = b.Pay,
            Style = b.Style,
        })));
    }

    private static OutgoingText Plain(OutgoingText m)
        => m with { Text = Icons.StripHtml(m.Text), Buttons = WithoutIcons(m.Buttons) };

    private static MessageEdit Plain(MessageEdit e)
        => e with { Html = Icons.StripHtml(e.Html), Buttons = WithoutIcons(e.Buttons) };

    private static int? Thread(int threadId) => threadId == 0 ? null : threadId;

    private Task<Message> SendCoreAsync(OutgoingText message, CancellationToken ct)
        => _client.SendMessage(
            chatId: message.ChatId,
            text: message.Text,
            parseMode: message.ParseMode,
            replyParameters: Reply(message.ReplyToMessageId),
            replyMarkup: message.Buttons,
            linkPreviewOptions: NoPreview,
            messageThreadId: Thread(message.ThreadId),
            disableNotification: message.Silent,
            cancellationToken: ct);

    private Task EditCoreAsync(MessageEdit edit, CancellationToken ct)
        => _client.EditMessageText(
            chatId: edit.ChatId,
            messageId: edit.MessageId,
            text: edit.Html,
            parseMode: edit.ParseMode,
            replyMarkup: edit.Buttons,
            linkPreviewOptions: NoPreview,
            cancellationToken: ct);

    private void BlockIcons()
    {
        if (!_iconsBlocked)
        {
            _iconsBlocked = true;
            _logger?.LogWarning("TgJelly's custom icons were rejected, so messages are sent without them. Custom emoji need Telegram Premium on the bot owner's account.");
        }
    }

    private static ReplyParameters? Reply(int? messageId)
        => messageId is { } id ? new ReplyParameters { MessageId = id, AllowSendingWithoutReply = true } : null;
}
