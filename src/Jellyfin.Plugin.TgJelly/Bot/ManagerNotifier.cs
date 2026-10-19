using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Tells the recent managers, in private, when a destination turns Lost, NeedsPermissions or TopicMissing, once per
/// new problem (spec §9.4). Runs on the bot worker only, so calls never overlap.
/// </summary>
public sealed class ManagerNotifier
{
    private readonly BotMemory _memory;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;
    private readonly BotConnection _connection;
    private readonly StateStore _state;
    private readonly StringTable _strings;
    private readonly ILogger<ManagerNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagerNotifier"/> class.
    /// </summary>
    /// <param name="memory">The bot's persisted memory (the recent managers).</param>
    /// <param name="health">The destination health.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="state">The state store holding the health section.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="logger">The logger.</param>
    public ManagerNotifier(BotMemory memory, HealthService health, SettingsStore settings, BotConnection connection, StateStore state, StringTable strings, ILogger<ManagerNotifier> logger)
    {
        _memory = memory;
        _health = health;
        _settings = settings;
        _connection = connection;
        _state = state;
        _strings = strings;
        _logger = logger;
    }

    /// <summary>
    /// Messages the recent managers about every destination whose current problem has not been reported yet. A
    /// problem is marked reported even when there is no manager to tell; a manager who blocked the bot is forgotten;
    /// a transport or server error leaves the problem unreported so the next call retries.
    /// </summary>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>A task.</returns>
    public async Task NotifyAsync(CancellationToken ct)
    {
        foreach (var destination in _settings.Current.Destinations)
        {
            ct.ThrowIfCancellationRequested();
            var health = _health.Get(destination.Id);
            if (health.Notified || health.Status is not (HealthStatus.Lost or HealthStatus.NeedsPermissions or HealthStatus.TopicMissing))
            {
                continue;
            }

            if (_connection.Sender is not { } sender)
            {
                continue;
            }

            var managers = _memory.RecentManagers();
            var delivered = true;
            if (managers.Count > 0)
            {
                var text = _strings.Get("notify.problem", ("title", HtmlText.Escape(destination.Title)), ("problem", HtmlText.Escape(ProblemText(health))));
                var buttons = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData(_strings.Get("notify.btn.open"), CallbackData.Format("d", destination.Id)),
                    InlineKeyboardButton.WithCallbackData(_strings.Get("dest.btn.check"), CallbackData.Format("dc", destination.Id)),
                });
                foreach (var chatId in managers)
                {
                    delivered &= await SendAsync(sender, chatId, text, buttons, destination, ct).ConfigureAwait(false);
                }
            }

            if (delivered)
            {
                MarkNotified(destination.Id, health.Status);
            }
        }
    }

    private string ProblemText(DestinationHealth health)
        => string.IsNullOrWhiteSpace(health.LastError) ? _strings.Get("dest.health." + health.Status) : health.LastError;

    /// <returns><c>false</c> when the send should be retried later.</returns>
    private async Task<bool> SendAsync(ITelegramSender sender, long chatId, string text, InlineKeyboardMarkup buttons, Destination destination, CancellationToken ct)
    {
        try
        {
            await sender.SendTextAsync(new OutgoingText(chatId, 0, text, ParseMode.Html, buttons, null, false), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            _logger.LogInformation("TgJelly forgot manager {Chat}: they blocked the bot", chatId);
            _memory.RemoveManager(chatId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly could not tell manager {Chat} about destination {Destination}; will retry", chatId, destination.Id);
            return false;
        }
    }

    /// <summary>Marks the problem reported, unless the destination's status changed while the messages went out.</summary>
    private void MarkNotified(string destinationId, HealthStatus reported)
        => _state.Update(s =>
        {
            if (s.Health.TryGetValue(destinationId, out var h) && h.Status == reported)
            {
                h.Notified = true;
            }
        });
}
