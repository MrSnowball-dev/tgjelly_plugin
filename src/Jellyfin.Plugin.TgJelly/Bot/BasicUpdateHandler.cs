using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The update handler until plan 4's <c>UpdateRouter</c>: answers callback queries at once, and re-checks
/// destinations when the bot's membership in their chat changes (spec §9.4). Everything else is ignored.
/// </summary>
public sealed class BasicUpdateHandler : IUpdateHandler
{
    private readonly BotConnection _connection;
    private readonly HealthService _health;
    private readonly ILogger<BasicUpdateHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BasicUpdateHandler"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public BasicUpdateHandler(BotConnection connection, HealthService health, ILogger<BasicUpdateHandler> logger)
    {
        _connection = connection;
        _health = health;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.CallbackQuery is { } query && _connection.Sender is { } sender)
        {
            try
            {
                await sender.AnswerCallbackAsync(query.Id, ct).ConfigureAwait(false);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("TgJelly ignored an expired callback query");
            }
        }

        if (update.MyChatMember is { } member)
        {
            _health.RequestCheckForChat(member.Chat.Id);
        }
    }
}
