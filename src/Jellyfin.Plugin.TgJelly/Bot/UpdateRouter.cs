using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Sorts incoming updates for the poll loop (plan 4): button presses are answered at once, and everything that needs
/// screens or storage is queued for the <see cref="BotWorker"/>. Never throws.
/// </summary>
public sealed class UpdateRouter : IUpdateHandler
{
    private readonly BotWorkQueue _queue;
    private readonly IBotFlows _flows;
    private readonly BotConnection _connection;
    private readonly HealthService _health;
    private readonly DestinationMigration _migration;
    private readonly ILogger<UpdateRouter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateRouter"/> class.
    /// </summary>
    /// <param name="queue">The work queue.</param>
    /// <param name="flows">The bot flows the work is handed to.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="health">The health service.</param>
    /// <param name="migration">Moves destinations when a group becomes a supergroup.</param>
    /// <param name="logger">The logger.</param>
    public UpdateRouter(BotWorkQueue queue, IBotFlows flows, BotConnection connection, HealthService health, DestinationMigration migration, ILogger<UpdateRouter> logger)
    {
        _queue = queue;
        _flows = flows;
        _connection = connection;
        _health = health;
        _migration = migration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            if (update.CallbackQuery is { } query)
            {
                await RouteCallbackAsync(query, ct).ConfigureAwait(false);
            }
            else if (update.Message is { } message)
            {
                RouteMessage(message);
            }
            else if (update.MyChatMember is { } member)
            {
                _health.RequestCheckForChat(member.Chat.Id);
                _queue.Enqueue("chat-member", token => _flows.OnChatMemberAsync(member, token));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "TgJelly could not route update {UpdateId}", update.Id);
        }
    }

    private async Task RouteCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        if (_connection.Sender is { } sender)
        {
            try
            {
                await sender.AnswerCallbackAsync(query.Id, ct).ConfigureAwait(false);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("TgJelly ignored an expired callback query");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A failed answer must not drop the click: the work is queued below regardless.
                _logger.LogWarning(ex, "TgJelly could not answer a callback query");
            }
        }

        if (query.Message?.Chat.Type == ChatType.Private)
        {
            _queue.Enqueue("callback", token => _flows.OnCallbackAsync(query, token));
        }
    }

    private void RouteMessage(Message message)
    {
        switch (message.Chat.Type)
        {
            case ChatType.Private when message.From?.IsBot != true:
                _queue.Enqueue("private-message", token => _flows.OnPrivateMessageAsync(message, token));
                break;
            case ChatType.Group or ChatType.Supergroup when message.MigrateToChatId is { } newId:
                var oldId = message.Chat.Id;
                _queue.Enqueue("migrate", _ =>
                {
                    _migration.Migrate(oldId, newId);
                    _health.RequestCheckForChat(newId);
                    return Task.CompletedTask;
                });
                break;
        }
    }
}
