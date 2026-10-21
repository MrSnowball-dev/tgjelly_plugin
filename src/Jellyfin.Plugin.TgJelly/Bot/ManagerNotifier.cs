using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Rendering;
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
    private const int MaxProblemChars = 300;

    private readonly BotMemory _memory;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;
    private readonly BotConnection _connection;
    private readonly StateStore _state;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly ILogger<ManagerNotifier> _logger;
    private readonly Dictionary<string, (HealthStatus Status, HashSet<long> Done)> _handled = new(StringComparer.Ordinal);

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
        _formatter = new Formatter(strings);
        _logger = logger;
    }

    /// <summary>
    /// Messages the recent managers about every destination whose current problem has not been reported yet. A
    /// problem is marked reported once every manager was messaged or given up on (blocked the bot, chat gone, other
    /// permanent 4xx), also when there is no manager to tell. A rate limit, server or transport error leaves that
    /// manager pending: the next call retries only the managers not yet messaged for this problem.
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
                _handled.Remove(destination.Id);
                continue;
            }

            if (_connection.Sender is not { } sender)
            {
                continue;
            }

            if (!_handled.TryGetValue(destination.Id, out var progress) || progress.Status != health.Status)
            {
                progress = (health.Status, []);
                _handled[destination.Id] = progress;
            }

            var pending = new List<long>();
            foreach (var chatId in _memory.RecentManagers())
            {
                if (!progress.Done.Contains(chatId))
                {
                    pending.Add(chatId);
                }
            }

            if (pending.Count > 0)
            {
                var text = _strings.Get("notify.problem", ("title", HtmlText.Escape(destination.Title)), ("problem", HtmlText.Escape(ProblemText(health))))
                    + ButtonGuide.Lines(_strings, _formatter, ("notify.btn.open", "notify.btn.open.about"), ("dest.btn.check", "dest.btn.check.about"));
                var buttons = new InlineKeyboardMarkup(new[]
                {
                    Buttons.Callback(_strings, "notify.btn.open", CallbackData.Format("d", destination.Id)),
                    Buttons.Callback(_strings, "dest.btn.check", CallbackData.Format("dc", destination.Id)),
                });
                foreach (var chatId in pending)
                {
                    if (await SendAsync(sender, chatId, text, buttons, destination, ct).ConfigureAwait(false))
                    {
                        progress.Done.Add(chatId);
                    }
                }
            }

            if (pending.TrueForAll(progress.Done.Contains))
            {
                MarkNotified(destination.Id, health.Status);
                _handled.Remove(destination.Id);
            }
        }
    }

    private string ProblemText(DestinationHealth health)
        => string.IsNullOrWhiteSpace(health.LastError) ? _strings.Get("dest.health." + health.Status) : Cap(health.LastError);

    /// <summary>Cuts a text to <see cref="MaxProblemChars"/> code points, adding an ellipsis.</summary>
    private static string Cap(string text)
    {
        var count = 0;
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (++count > MaxProblemChars)
            {
                return text[..end] + "…";
            }

            end += rune.Utf16SequenceLength;
        }

        return text;
    }

    /// <returns><c>true</c> when the manager is finished for this problem (messaged, or given up on); <c>false</c> to retry later.</returns>
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
        catch (ApiRequestException ex) when (ex.ErrorCode is >= 400 and < 500 and not 429)
        {
            _logger.LogWarning("TgJelly gave up telling manager {Chat} about destination {Destination}: {Error}", chatId, destination.Id, ex.Message);
            if (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("deactivated", StringComparison.OrdinalIgnoreCase))
            {
                _memory.RemoveManager(chatId);
            }

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
