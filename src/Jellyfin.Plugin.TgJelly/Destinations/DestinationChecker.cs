using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Checks whether the bot can post in a chat: <c>getChat</c>, <c>getChatMember(bot)</c>, and for a forum topic a
/// typing action in that thread.
/// </summary>
public sealed class DestinationChecker
{
    private readonly BotConnection _connection;
    private readonly StringTable _strings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationChecker"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="strings">The string table.</param>
    public DestinationChecker(BotConnection connection, StringTable strings)
    {
        _connection = connection;
        _strings = strings;
    }

    /// <summary>
    /// Checks a chat. A health-type Telegram error (the bot is gone, lacks rights, the topic is missing) is a failed
    /// result; any other error (network, 5xx, 429, unknown) is thrown, so callers keep the previous health.
    /// </summary>
    /// <param name="chat">The chat ID or <c>@username</c>.</param>
    /// <param name="threadId">The forum topic, or 0.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns>The result.</returns>
    /// <exception cref="BotNotConnectedException">No bot is connected.</exception>
    public async Task<ChatCheckResult> CheckAsync(ChatId chat, int threadId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var sender = _connection.RequireSender();
        var unknownId = chat.Identifier ?? 0;

        ChatFullInfo info;
        try
        {
            info = await sender.GetChatAsync(chat, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && HealthProblem(ex) is { } problem)
        {
            return Failed(problem.Status, problem.Message, unknownId, string.Empty, ChatKind.Supergroup, false);
        }

        var kind = info.Type switch
        {
            ChatType.Private => ChatKind.Private,
            ChatType.Group => ChatKind.Group,
            ChatType.Channel => ChatKind.Channel,
            _ => ChatKind.Supergroup,
        };
        var title = info.Title ?? string.Join(' ', info.FirstName, info.LastName).Trim();
        if (title.Length == 0)
        {
            title = info.Id.ToString(CultureInfo.InvariantCulture);
        }

        if (kind == ChatKind.Private)
        {
            return new ChatCheckResult(true, HealthStatus.Ok, null, info.Id, title, kind, false, false);
        }

        ChatMember member;
        try
        {
            member = await sender.GetChatMemberAsync(info.Id, _connection.BotId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && HealthProblem(ex) is { } problem)
        {
            return Failed(problem.Status, problem.Message, info.Id, title, kind, info.IsForum);
        }

        var (passed, status, message) = Evaluate(kind, member);
        if (!passed)
        {
            return Failed(status, message, info.Id, title, kind, info.IsForum);
        }

        var photosBlocked = member switch
        {
            ChatMemberRestricted r => !r.CanSendPhotos,
            ChatMemberMember => info.Permissions is { CanSendPhotos: false },
            _ => false,
        };

        if (threadId > 0)
        {
            try
            {
                await sender.SendTypingAsync(info.Id, threadId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && HealthProblem(ex) is { } problem)
            {
                return Failed(problem.Status, problem.Message, info.Id, title, kind, info.IsForum);
            }
        }

        return new ChatCheckResult(true, HealthStatus.Ok, null, info.Id, title, kind, info.IsForum, photosBlocked);
    }

    private static ChatCheckResult Failed(HealthStatus status, string message, long chatId, string title, ChatKind kind, bool isForum)
        => new(false, status, message, chatId, title, kind, isForum, false);

    private (bool Passed, HealthStatus Status, string Message) Evaluate(ChatKind kind, ChatMember member)
    {
        if (member is ChatMemberLeft or ChatMemberBanned || member is ChatMemberRestricted { IsMember: false })
        {
            return (false, HealthStatus.Lost, _strings.Get("check.lost"));
        }

        if (kind == ChatKind.Channel)
        {
            return member is ChatMemberOwner || member is ChatMemberAdministrator { CanPostMessages: true }
                ? (true, HealthStatus.Ok, string.Empty)
                : (false, HealthStatus.NeedsPermissions, _strings.Get("check.channelNotAdmin"));
        }

        return member is ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember || member is ChatMemberRestricted { CanSendMessages: true }
            ? (true, HealthStatus.Ok, string.Empty)
            : (false, HealthStatus.NeedsPermissions, _strings.Get("check.groupCantSend"));
    }

    private (HealthStatus Status, string Message)? HealthProblem(Exception ex)
    {
        var error = TelegramErrorClassifier.ClassifyDelivery(ex);
        return error.Kind switch
        {
            DeliveryErrorKind.Lost when error.Description.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
                => (HealthStatus.Lost, _strings.Get("check.notFound")),
            DeliveryErrorKind.Lost => (HealthStatus.Lost, _strings.Get("check.lost")),
            DeliveryErrorKind.NeedsPermissions => (HealthStatus.NeedsPermissions, _strings.Get("check.groupCantSend")),
            DeliveryErrorKind.TopicMissing => (HealthStatus.TopicMissing, _strings.Get("check.topicMissing")),
            _ => null,
        };
    }
}
